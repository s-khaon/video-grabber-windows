using System.Text;
using System.Text.RegularExpressions;

namespace VideoGrabber.Core;

public static class StringTools
{
    private static readonly Regex AnsiRegex = new("\u001B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static string Trimmed(this string? value) => (value ?? "").Trim();

    /// <summary>
    /// 浏览器发 Referer 时会去掉 URL 片段；这里保持一致，
    /// 否则 `https://host/page#fragment` 会让严格校验 Referer 的站点返回 403。
    /// </summary>
    public static string RefererValue(this string value)
    {
        var trimmed = value.Trimmed();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// 从页面地址构造浏览器同源请求会带的 Origin 头。仅接受 http(s)。
    /// </summary>
    public static string? OriginValue(this string value)
    {
        var trimmed = value.Trimmed();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        var builder = new UriBuilder { Scheme = scheme, Host = uri.Host, Port = uri.IsDefaultPort ? -1 : uri.Port };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>去掉 yt-dlp 可能输出的 ANSI 颜色控制符。</summary>
    public static string StrippingAnsi(this string value) => AnsiRegex.Replace(value, "");
}

public static class FileSize
{
    /// <summary>与 macOS 版 ByteCountFormatter(.file) 相近的可读体积文案。</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        if (unit == 0) return $"{bytes} bytes";
        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>把用户输入的额外参数切成 argv，支持单双引号。</summary>
public static class ShellWords
{
    public static List<string> Split(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(input)) return result;

        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        foreach (var ch in input)
        {
            switch (ch)
            {
                case '"' when !inSingle:
                    inDouble = !inDouble;
                    hasToken = true;
                    break;
                case '\'' when !inDouble:
                    inSingle = !inSingle;
                    hasToken = true;
                    break;
                case ' ' or '\t' or '\n' when !inSingle && !inDouble:
                    if (hasToken)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                    break;
                default:
                    current.Append(ch);
                    hasToken = true;
                    break;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}

public static class UrlTools
{
    /// <summary>
    /// 链接规范化：去空白；http(s) 且 host 含 `.` 直接用；无 scheme 时补 https://；其余拒绝。
    /// </summary>
    public static string? Normalize(string? input)
    {
        var trimmed = input.Trimmed();
        if (trimmed.Length == 0) return null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Contains('.'))
        {
            return trimmed;
        }
        if (trimmed.Contains("://")) return null;

        var withScheme = "https://" + trimmed;
        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var added) && added.Host.Contains('.'))
        {
            return withScheme;
        }
        return null;
    }

    public static string? HostOf(string? urlString) =>
        Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    /// <summary>URL 最后一段（含扩展名）；无法解析时返回空串。</summary>
    public static string LastPathComponent(string? urlString)
    {
        if (!Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri)) return "";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "" : Uri.UnescapeDataString(segments[^1]);
    }

    public static bool LooksLikeHls(string? urlString)
    {
        var lower = (urlString ?? "").ToLowerInvariant();
        if (lower.Contains(".m3u8")) return true;
        if (!Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHttp(string? urlString) =>
        Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// ffmpeg 的 HTTP 请求头。统一用 `-headers` 一次传多行（CRLF 分隔），
/// 避免 `-referer` / `-user_agent` 与 `-headers` 里的同名头叠加导致严格校验的 CDN 返回 403。
/// 直连下载、HLS 封装、转换三条路径共用这一份构造。
/// </summary>
public sealed record FfmpegRequestHeaders(string? Referer, string? UserAgent, string? Cookie)
{
    public List<string> Lines()
    {
        var items = new List<string>();
        if (!string.IsNullOrEmpty(Referer?.Trimmed()))
        {
            items.Add($"Referer: {Referer.Trimmed().RefererValue()}");
        }
        if (!string.IsNullOrEmpty(UserAgent?.Trimmed()))
        {
            items.Add($"User-Agent: {UserAgent.Trimmed()}");
        }
        if (!string.IsNullOrEmpty(Cookie?.Trimmed()))
        {
            items.Add($"Cookie: {Cookie.Trimmed()}");
        }
        // 部分 CDN 会校验 Origin，缺失时同样按跨域请求拒绝。
        var origin = Referer?.Trimmed().OriginValue();
        if (!string.IsNullOrEmpty(origin)) items.Add($"Origin: {origin}");
        return items;
    }

    /// <summary>`-headers` 的参数值：每个头一行、以 CRLF 结尾；没有任何头时返回 null。</summary>
    public string? Value()
    {
        var lines = Lines();
        return lines.Count == 0 ? null : string.Concat(lines.Select(line => line + "\r\n"));
    }
}
