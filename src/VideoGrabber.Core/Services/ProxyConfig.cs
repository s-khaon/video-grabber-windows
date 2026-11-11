using System.Net;
using System.Net.Http;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 代理配置：默认走本机 Clash（127.0.0.1:7897）。
/// </summary>
public sealed class ProxyConfiguration
{
    public bool Enabled { get; init; }
    public Uri? HttpUrl { get; init; }
    public Uri? SocksUrl { get; init; }
    /// <summary>配置了代理但地址写错时用于提示。</summary>
    public string? InvalidReason { get; init; }

    public bool IsActive => Enabled && HttpUrl != null;

    public string Summary
    {
        get
        {
            if (!Enabled) return "直连";
            return HttpUrl == null ? "代理地址无效" : HttpUrl.AbsoluteUri;
        }
    }

    /// <summary>注入子进程的环境变量（大小写各一份，覆盖 curl / requests / urllib）。</summary>
    public Dictionary<string, string> Environment()
    {
        if (!IsActive || HttpUrl == null || SocksUrl == null) return new Dictionary<string, string>();
        var http = HttpUrl.AbsoluteUri;
        var socks = SocksUrl.AbsoluteUri;
        // ffmpeg/curl 不读系统代理的例外列表，本机与局域网地址需要显式绕过。
        const string bypass = "localhost,127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,*.local";
        return new Dictionary<string, string>
        {
            ["http_proxy"] = http,
            ["https_proxy"] = http,
            ["all_proxy"] = socks,
            ["no_proxy"] = bypass,
            ["NO_PROXY"] = bypass
        };
    }

    /// <summary>传给 yt-dlp 的参数。</summary>
    public List<string> YtDlpArguments() =>
        IsActive && HttpUrl != null
            ? new List<string> { "--proxy", HttpUrl.AbsoluteUri }
            : new List<string>();
}

public static class ProxyConfig
{
    public const string DefaultUrl = "http://127.0.0.1:7897";
    public const string DefaultSocksUrl = "socks5://127.0.0.1:7897";

    public static ProxyConfiguration Resolve(AppSettings settings)
    {
        if (!settings.ProxyEnabled)
        {
            return new ProxyConfiguration { Enabled = false };
        }

        var raw = string.IsNullOrWhiteSpace(settings.ProxyUrl) ? DefaultUrl : settings.ProxyUrl.Trimmed();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return new ProxyConfiguration { Enabled = true, InvalidReason = "代理地址格式不正确" };
        }
        if (uri.IsDefaultPort)
        {
            return new ProxyConfiguration { Enabled = true, InvalidReason = "代理地址缺少端口" };
        }

        var scheme = string.IsNullOrEmpty(uri.Scheme) ? "http" : uri.Scheme.ToLowerInvariant();
        Uri socks = scheme.StartsWith("socks", StringComparison.Ordinal)
            ? uri
            : new UriBuilder("socks5", uri.Host, uri.Port).Uri;

        return new ProxyConfiguration { Enabled = true, HttpUrl = uri, SocksUrl = socks };
    }

    /// <summary>子进程统一环境：代理变量。</summary>
    public static Dictionary<string, string> Environment(AppSettings settings) =>
        Resolve(settings).Environment();
}

/// <summary>检测代理是否能真正访问外网。</summary>
public static class ProxyChecker
{
    public readonly record struct Result(bool Ok, string Message);

    public static async Task<Result> TestAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var configuration = ProxyConfig.Resolve(settings);
        if (!configuration.Enabled) return new Result(true, "当前为直连模式，未启用代理。");
        if (configuration.HttpUrl == null)
        {
            return new Result(false, configuration.InvalidReason ?? "代理地址无效。");
        }

        var host = configuration.HttpUrl.Host;
        var port = configuration.HttpUrl.Port;
        var isSocks = configuration.HttpUrl.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"{(isSocks ? "socks5" : "http")}://{host}:{port}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var started = DateTime.Now;
        try
        {
            using var response = await client.GetAsync("https://www.gstatic.com/generate_204", cancellationToken);
            var elapsed = (int)(DateTime.Now - started).TotalMilliseconds;
            var code = (int)response.StatusCode;
            return code is >= 200 and <= 399
                ? new Result(true, $"代理连通，延迟约 {elapsed} ms（{host}:{port}）")
                : new Result(false, "代理返回异常状态码，请检查 Clash 规则。");
        }
        catch (TaskCanceledException)
        {
            return new Result(false, "连接超时，请检查代理端口与网络。");
        }
        catch (HttpRequestException)
        {
            return new Result(false, $"无法连接 {host}:{port}，请确认 Clash 已启动并开启 HTTP 代理端口。");
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }
}
