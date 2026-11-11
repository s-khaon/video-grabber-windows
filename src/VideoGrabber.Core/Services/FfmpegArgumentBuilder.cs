using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>ffmpeg 直连下载 / 封装参数。</summary>
public static class FfmpegArgumentBuilder
{
    /// <summary>ffmpeg 直接按地址抓流（-c copy）。</summary>
    public static List<string> BuildDirect(
        DownloadJob job,
        ToolEnvironment tools,
        string outputPath,
        string? cookieHeader = null)
    {
        var arguments = new List<string>
        {
            "-y", "-nostdin", "-hide_banner", "-loglevel", "info", "-progress", "pipe:1", "-nostats"
        };

        // 加密 m3u8 的密钥文件（常见 key.bin）扩展名不在 HLS 默认白名单里，需要显式放行。
        if (job.UrlString.ToLowerInvariant().Contains(".m3u8"))
        {
            arguments.AddRange(new[] { "-allowed_extensions", "ALL" });
            arguments.AddRange(tools.HlsSegmentExtensionArguments);
        }

        var headers = new FfmpegRequestHeaders(job.Referer, job.UserAgent, cookieHeader);
        var headerValue = headers.Value();
        if (headerValue != null) arguments.AddRange(new[] { "-headers", headerValue });

        if (UrlTools.IsHttp(job.UrlString))
        {
            arguments.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5" });
        }
        arguments.AddRange(new[] { "-rw_timeout", "20000000" });

        arguments.AddRange(new[] { "-i", job.UrlString, "-c", "copy", outputPath });
        return arguments;
    }
}
