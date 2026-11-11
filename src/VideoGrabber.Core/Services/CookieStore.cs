using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.Core.Services;

/// <summary>从浏览器导出的一条 cookie。</summary>
public sealed record BrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    DateTime? ExpiresAt);

/// <summary>Cookies 目录里的一个站点导出文件。</summary>
public sealed record CookieEntry(
    string Host,
    string FilePath,
    int CookieCount,
    long FileSize,
    DateTime ModifiedAt);

/// <summary>
/// 内置浏览器导出的 Netscape cookie 文件（`&lt;主域&gt;.txt`）读写与匹配。
/// 与 macOS 版保持同一格式，yt-dlp / ffmpeg 可直接使用。
/// </summary>
public sealed class CookieStore
{
    /// <summary>两级公共后缀：主域要再往前取一段（如 `bbc.co.uk`）。</summary>
    private static readonly HashSet<string> MultpartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "com.cn", "com.au", "co.jp", "com.hk", "com.tw",
        "co.kr", "com.sg", "co.nz", "co.in", "com.br", "com.mx", "net.cn", "org.cn"
    };

    private readonly object _gate = new();

    public CookieStore(IPathProvider paths)
    {
        Directory = Path.Combine(paths.DataDirectory, "Cookies");
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception)
        {
            // 目录创建失败时列表为空，保存时再报错。
        }
        Reload();
    }

    public string Directory { get; }

    public IReadOnlyList<CookieEntry> Entries { get; private set; } = Array.Empty<CookieEntry>();

    public void Reload()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                Entries = Array.Empty<CookieEntry>();
                return;
            }
            var entries = new List<CookieEntry>();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.txt"))
            {
                try
                {
                    var info = new FileInfo(file);
                    var content = File.ReadAllText(file);
                    var count = content
                        .Split('\n')
                        .Select(line => line.TrimEnd('\r'))
                        .Count(line => line.Length > 0
                                       && !line.StartsWith("# ", StringComparison.Ordinal)
                                       && (!line.StartsWith('#') || line.StartsWith("#HttpOnly_", StringComparison.Ordinal)));
                    entries.Add(new CookieEntry(
                        Path.GetFileNameWithoutExtension(file),
                        file,
                        count,
                        info.Length,
                        info.LastWriteTime));
                }
                catch (Exception)
                {
                    // 单个文件读失败时跳过。
                }
            }
            Entries = entries.OrderBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            Entries = Array.Empty<CookieEntry>();
        }
    }

    public CookieEntry? EntryFor(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        if (host.Length == 0) return null;
        return Entries.FirstOrDefault(entry =>
            host == entry.Host || host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase));
    }

    public string? CookieFileFor(Uri url) => EntryFor(url)?.FilePath;

    public string? CookieHeaderFor(Uri url) => CookieHeaderFor(new[] { url });

    /// <summary>合并多个地址的 cookie（页面主域 + CDN），同名 cookie 后面的地址优先。</summary>
    public string? CookieHeaderFor(IEnumerable<Uri> urls)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var url in urls)
        {
            var host = url.Host.ToLowerInvariant();
            if (host.Length == 0) continue;
            foreach (var entry in Entries.Where(entry =>
                         host == entry.Host || host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase)))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(entry.FilePath);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    var fields = line.Split('\t');
                    if (fields.Length < 7) continue;

                    var domain = fields[0];
                    if (domain.StartsWith("#HttpOnly_", StringComparison.Ordinal))
                    {
                        domain = domain["#HttpOnly_".Length..];
                    }
                    else if (domain.StartsWith('#'))
                    {
                        continue;
                    }
                    var normalized = domain.TrimStart('.').ToLowerInvariant();
                    if (normalized.Length == 0) continue;
                    // cookie 域覆盖当前主机，或当前主机是 cookie 的子域时才发送。
                    if (!(host == normalized
                          || host.EndsWith("." + normalized, StringComparison.Ordinal)
                          || normalized.EndsWith("." + host, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var name = fields[5];
                    var value = fields[6];
                    if (name.Length == 0) continue;
                    if (!values.ContainsKey(name)) order.Add(name);
                    values[name] = value;
                }
            }
        }

        if (order.Count == 0) return null;
        return string.Join("; ", order.Select(name => $"{name}={values[name]}"));
    }

    /// <summary>主域：`www.youtube.com` → `youtube.com`，`bbc.co.uk` → `bbc.co.uk`。</summary>
    public static string BaseDomain(string host)
    {
        var parts = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return string.Join(".", parts);
        var lastTwo = string.Join(".", parts[^2..]);
        if (MultpartSuffixes.Contains(lastTwo) && parts.Length >= 3)
        {
            return string.Join(".", parts[^3..]);
        }
        return lastTwo;
    }

    /// <summary>导出 Netscape 格式 cookie 文件；只保留与页面主域相关的 cookie。</summary>
    public (string FilePath, int Count) Write(IEnumerable<BrowserCookie> cookies, Uri pageUrl)
    {
        var host = pageUrl.Host.ToLowerInvariant();
        if (host.Length == 0) throw new CookieWriteException("当前页面地址无效，无法判断站点。");

        var baseDomain = BaseDomain(host);
        var related = cookies
            .Where(cookie =>
            {
                var domain = cookie.Domain.TrimStart('.').ToLowerInvariant();
                if (domain.Length == 0) return false;
                return domain == baseDomain
                       || domain.EndsWith("." + baseDomain, StringComparison.Ordinal)
                       || baseDomain.EndsWith("." + domain, StringComparison.Ordinal);
            })
            .OrderBy(cookie => cookie.Name, StringComparer.Ordinal)
            .ToList();

        if (related.Count == 0)
        {
            throw new CookieWriteException($"没有拿到 {baseDomain} 的 cookie。请先在页面里完成验证或登录，等页面正常显示后再保存。");
        }

        var lines = new List<string>
        {
            "# Netscape HTTP Cookie File",
            $"# 由 Video Grabber 从内置浏览器导出，站点: {baseDomain}",
            $"# 导出时间: {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}",
            ""
        };
        foreach (var cookie in related)
        {
            var domainField = cookie.HttpOnly ? "#HttpOnly_" + cookie.Domain : cookie.Domain;
            var includeSubdomains = cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            var secure = cookie.Secure ? "TRUE" : "FALSE";
            var expiry = cookie.ExpiresAt is { } expires
                ? new DateTimeOffset(expires.ToUniversalTime()).ToUnixTimeSeconds().ToString()
                : "0";
            var value = cookie.Value
                .Replace('\t', ' ')
                .Replace('\n', ' ')
                .Replace('\r', ' ');
            lines.Add(string.Join("\t", domainField, includeSubdomains, path, secure, expiry, cookie.Name, value));
        }

        System.IO.Directory.CreateDirectory(Directory);
        var filePath = Path.Combine(Directory, $"{baseDomain}.txt");
        File.WriteAllText(filePath, string.Join("\n", lines) + "\n");
        Reload();
        return (filePath, related.Count);
    }

    public bool Delete(CookieEntry entry)
    {
        try
        {
            if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
            Reload();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public (int Removed, List<string> Failures) DeleteAll()
    {
        var removed = 0;
        var failures = new List<string>();
        foreach (var entry in Entries.ToList())
        {
            try
            {
                File.Delete(entry.FilePath);
                removed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Host}: {ex.Message}");
            }
        }
        Reload();
        return (removed, failures);
    }

    /// <summary>手动写入一个站点文件（测试 / 迁移用）。</summary>
    public void WriteRaw(string host, string content)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, $"{BaseDomain(host)}.txt"), content);
        }
        Reload();
    }
}

public sealed class CookieWriteException : Exception
{
    public CookieWriteException(string message) : base(message)
    {
    }
}
