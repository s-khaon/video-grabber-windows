using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 广告拦截（网络层）：加载随包附带的 EasyList 规则 JSON，按 URL 匹配拦截。
/// 规则离线随包，运行时零网络请求；与 macOS 版使用同一份生成产物。
/// </summary>
public sealed class AdBlockEngine
{
    private sealed class Rule
    {
        public Regex? Pattern { get; init; }
        public string[] Literals { get; init; } = Array.Empty<string>();
        public bool ThirdPartyOnly { get; init; }
        public HashSet<string>? ResourceTypes { get; init; }
        public string[] IfDomains { get; init; } = Array.Empty<string>();
        public string[] UnlessDomains { get; init; } = Array.Empty<string>();
    }

    private readonly List<Rule> _rules = new();
    private readonly List<Rule> _exceptions = new();
    private readonly Dictionary<string, List<Rule>> _index = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(60);

    public int RuleCount => _rules.Count;
    public int ExceptionCount => _exceptions.Count;
    public string GeneratedAt { get; private set; } = "";
    public IReadOnlyList<string> Sources { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> CosmeticSelectors { get; private set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CosmeticDomainSelectors { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public static async Task<AdBlockEngine> LoadAsync(
        string rulesPath,
        string cosmeticPath,
        string metaPath,
        CancellationToken cancellationToken = default)
    {
        var engine = new AdBlockEngine();
        if (!File.Exists(rulesPath)) return engine;

        var json = await File.ReadAllTextAsync(rulesPath, cancellationToken);
        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            engine.AddRule(element);
        }
        engine.BuildIndex();

        if (File.Exists(cosmeticPath))
        {
            try
            {
                using var cosmetic = JsonDocument.Parse(await File.ReadAllTextAsync(cosmeticPath, cancellationToken));
                var root = cosmetic.RootElement;
                if (root.TryGetProperty("selectors", out var selectors))
                {
                    engine.CosmeticSelectors = selectors.EnumerateArray()
                        .Select(item => item.GetString() ?? "")
                        .Where(item => item.Length > 0)
                        .ToList();
                }
                if (root.TryGetProperty("domains", out var domains))
                {
                    var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in domains.EnumerateObject())
                    {
                        map[property.Name] = property.Value.EnumerateArray()
                            .Select(item => item.GetString() ?? "")
                            .Where(item => item.Length > 0)
                            .ToList();
                    }
                    engine.CosmeticDomainSelectors = map;
                }
            }
            catch (Exception)
            {
                // 外观规则读不到时只缺少元素隐藏，不影响网络层拦截。
            }
        }

        if (File.Exists(metaPath))
        {
            try
            {
                using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken));
                if (meta.RootElement.TryGetProperty("generatedAt", out var generated)) engine.GeneratedAt = generated.GetString() ?? "";
                if (meta.RootElement.TryGetProperty("sources", out var sources))
                {
                    engine.Sources = sources.EnumerateArray().Select(item => item.GetString() ?? "").ToList();
                }
            }
            catch (Exception)
            {
                // 元信息缺失不影响拦截。
            }
        }
        return engine;
    }

    private void AddRule(JsonElement element)
    {
        if (!element.TryGetProperty("trigger", out var trigger)) return;
        if (!element.TryGetProperty("action", out var action)) return;
        var actionType = action.TryGetProperty("type", out var type) ? type.GetString() : null;
        if (actionType is not ("block" or "ignore-previous-rules")) return;

        var filter = trigger.TryGetProperty("url-filter", out var filterElement) ? filterElement.GetString() : null;
        if (string.IsNullOrEmpty(filter)) return;

        var caseSensitive = trigger.TryGetProperty("url-filter-is-case-sensitive", out var cs)
                            && cs.ValueKind == JsonValueKind.True;
        Regex? pattern = null;
        try
        {
            pattern = new Regex(
                filter,
                RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                RegexTimeout);
        }
        catch (ArgumentException)
        {
            // 少数规则包含 .NET 不支持的语法，直接跳过（与 WebKit 行为差异可忽略）。
            return;
        }

        var literals = ExtractLiterals(filter);
        var thirdParty = trigger.TryGetProperty("load-type", out var loadTypes)
                         && loadTypes.EnumerateArray().Any(item => item.GetString() == "third-party");

        HashSet<string>? resourceTypes = null;
        if (trigger.TryGetProperty("resource-type", out var types))
        {
            resourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in types.EnumerateArray())
            {
                if (item.GetString() is { } value) resourceTypes.Add(value);
            }
        }

        var rule = new Rule
        {
            Pattern = pattern,
            Literals = literals,
            ThirdPartyOnly = thirdParty,
            ResourceTypes = resourceTypes,
            IfDomains = ReadDomains(trigger, "if-domain"),
            UnlessDomains = ReadDomains(trigger, "unless-domain")
        };

        if (actionType == "ignore-previous-rules") _exceptions.Add(rule);
        else _rules.Add(rule);
    }

    private static string[] ReadDomains(JsonElement trigger, string name)
    {
        if (!trigger.TryGetProperty(name, out var element)) return Array.Empty<string>();
        return element.EnumerateArray()
            .Select(item => (item.GetString() ?? "").TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private void BuildIndex()
    {
        foreach (var rule in _rules)
        {
            var key = IndexKey(rule);
            if (!_index.TryGetValue(key, out var list))
            {
                list = new List<Rule>();
                _index[key] = list;
            }
            list.Add(rule);
        }
    }

    private static string IndexKey(Rule rule)
    {
        var longest = rule.Literals.OrderByDescending(literal => literal.Length).FirstOrDefault();
        return longest is { Length: >= 3 } ? longest[..3] : "";
    }

    /// <summary>
    /// 从 WebKit 的 url-filter 正则里提取长度 ≥3 的字面量，用于快速预筛，
    /// 避免每条请求都跑 6 万条正则。
    /// </summary>
    private static string[] ExtractLiterals(string filter)
    {
        var literals = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var index = 0; index < filter.Length; index++)
        {
            var ch = filter[index];
            if (ch == '\\' && index + 1 < filter.Length)
            {
                var next = filter[index + 1];
                if (!char.IsLetterOrDigit(next)) current.Append(next);
                index++;
                continue;
            }
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '/' or ':' or '=')
            {
                current.Append(ch);
            }
            else
            {
                if (current.Length >= 3) literals.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= 3) literals.Add(current.ToString());
        return literals.ToArray();
    }

    /// <summary>
    /// 判断是否拦截。documentUri 是页面地址（用于第三方 / 域名条件），
    /// resourceType 取 WebView2 的资源类型名。
    /// </summary>
    public bool ShouldBlock(Uri documentUri, string requestUrl, string resourceType, bool isThirdParty)
    {
        if (_rules.Count == 0) return false;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri)) return false;

        var url = requestUrl.ToLowerInvariant();
        var documentHost = documentUri.Host.ToLowerInvariant();
        var candidates = Candidates(url);

        foreach (var rule in candidates)
        {
            if (!Matches(rule, url, requestUri, documentHost, resourceType, isThirdParty)) continue;
            if (IsExcepted(url, requestUri, documentHost, resourceType, isThirdParty)) continue;
            return true;
        }
        return false;
    }

    private IEnumerable<Rule> Candidates(string lowerUrl)
    {
        var seen = new HashSet<Rule>();
        if (_index.TryGetValue("", out var unindexed))
        {
            foreach (var rule in unindexed) seen.Add(rule);
        }
        // 用 URL 的 3-gram 反查候选规则；先去重再逐个验证。
        for (var index = 0; index + 3 <= lowerUrl.Length; index++)
        {
            if (!_index.TryGetValue(lowerUrl.Substring(index, 3), out var list)) continue;
            foreach (var rule in list) seen.Add(rule);
        }
        return seen;
    }

    private bool IsExcepted(string url, Uri requestUri, string documentHost, string resourceType, bool thirdParty)
    {
        foreach (var rule in _exceptions)
        {
            if (Matches(rule, url, requestUri, documentHost, resourceType, thirdParty)) return true;
        }
        return false;
    }

    private static bool Matches(Rule rule, string lowerUrl, Uri requestUri, string documentHost, string resourceType, bool thirdParty)
    {
        foreach (var literal in rule.Literals)
        {
            if (!lowerUrl.Contains(literal, StringComparison.Ordinal)) return false;
        }
        if (rule.ThirdPartyOnly && !thirdParty) return false;
        if (rule.ResourceTypes != null && !rule.ResourceTypes.Contains(resourceType)) return false;

        var host = requestUri.Host.ToLowerInvariant();
        if (rule.IfDomains.Length > 0 && !rule.IfDomains.Any(domain => HostMatches(documentHost, domain))) return false;
        if (rule.UnlessDomains.Length > 0 && rule.UnlessDomains.Any(domain => HostMatches(documentHost, domain))) return false;

        try
        {
            return rule.Pattern?.IsMatch(lowerUrl) == true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>是否为跨站请求（按主域比较）。</summary>
    public static bool IsThirdParty(Uri document, Uri request) =>
        !string.Equals(
            CookieStore.BaseDomain(document.Host),
            CookieStore.BaseDomain(request.Host),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>当前页面的外观隐藏规则（全局 + 站点专属）。</summary>
    public string CssFor(string host)
    {
        var selectors = new List<string>(CosmeticSelectors);
        var normalized = host.ToLowerInvariant();
        foreach (var (domain, list) in CosmeticDomainSelectors)
        {
            if (!HostMatches(normalized, domain)) continue;
            selectors.AddRange(list);
        }
        if (selectors.Count == 0) return "";
        return string.Join(",", selectors) + "{display:none !important;}";
    }
}
