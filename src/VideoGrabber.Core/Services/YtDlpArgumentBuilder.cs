using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>
/// yt-dlp 参数拼装。顺序与原版严格一致（-P temp 在前、-P 保存目录在后，
/// -o 的 `%` 需要转义），任何顺序变化都会影响续传与产出位置。
/// </summary>
public static class YtDlpArgumentBuilder
{
    public static List<string> Build(
        DownloadJob job,
        AppSettings settings,
        ToolEnvironment tools,
        string tempDirectory)
    {
        // --continue 复用 .part 文件，重试只补没下完的部分。
        var arguments = new List<string>
        {
            "--newline", "--no-color", "--no-playlist", "--ignore-config", "--continue",
            "-P", $"temp:{tempDirectory}",
            "-P", job.SaveDirectory
        };

        // 使用「标题 + 视频标识」的固定基名，续传时也不会改名。
        var outputBase = FileNameRules.ResolvedOutputBase(job).Replace("%", "%%");
        arguments.AddRange(new[] { "-o", $"{outputBase}.%(ext)s" });

        // 始终告诉 yt-dlp 去哪里找 ffmpeg / ffprobe：收尾修复与元数据提取都依赖它们。
        var ffmpeg = tools.Executable(ToolEnvironment.Tool.Ffmpeg);
        if (ffmpeg != null)
        {
            arguments.AddRange(new[] { "--ffmpeg-location", Path.GetDirectoryName(ffmpeg) ?? "" });
        }

        // 直播（无 #EXT-X-ENDLIST）不能用 hlsnative：改用 ffmpeg 下载器边录边封装成 MPEG-TS。
        if (job.IsLiveHls == true)
        {
            arguments.AddRange(new[] { "--downloader", "ffmpeg", "--hls-use-mpegts" });
            var segmentArguments = string.Join(" ", tools.HlsSegmentExtensionArguments);
            if (!string.IsNullOrEmpty(segmentArguments))
            {
                arguments.AddRange(new[] { "--downloader-args", $"ffmpeg_i:{segmentArguments}" });
            }
        }

        if (job.AudioOnly)
        {
            arguments.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        }
        else
        {
            // 指定的清晰度不存在时（例如直播只有单一格式）退回最佳可用格式。
            arguments.AddRange(new[] { "-f", job.DidFallbackFormat ? "b" : job.Quality.FormatSelector });
            var mergeFormat = job.Container.MergeOutputFormat();
            if (mergeFormat != null) arguments.AddRange(new[] { "--merge-output-format", mergeFormat });
        }

        var rate = settings.RateLimit.Trimmed();
        if (rate.Length > 0) arguments.AddRange(new[] { "-r", rate });

        arguments.AddRange(ProxyConfig.Resolve(settings).YtDlpArguments());

        if (!string.IsNullOrEmpty(job.CookiesPath)) arguments.AddRange(new[] { "--cookies", job.CookiesPath! });

        // 嗅探到的地址通常要求与页面一致的 Referer / User-Agent，否则容易 403。
        var referer = job.Referer?.Trimmed();
        if (!string.IsNullOrEmpty(referer))
        {
            arguments.AddRange(new[] { "--referer", referer!.RefererValue() });
            var origin = referer.OriginValue();
            if (!string.IsNullOrEmpty(origin)) arguments.AddRange(new[] { "--add-header", $"Origin: {origin}" });
        }
        var userAgent = job.UserAgent?.Trimmed();
        if (!string.IsNullOrEmpty(userAgent)) arguments.AddRange(new[] { "--user-agent", userAgent! });

        arguments.AddRange(ShellWords.Split(settings.ExtraArguments));

        arguments.AddRange(new[]
        {
            "--progress-template",
            "download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s"
        });
        arguments.Add(job.UrlString);
        return arguments;
    }

    /// <summary>把参数拼成可直接复制到终端的命令预览。</summary>
    public static string Preview(string executable, IEnumerable<string> arguments)
    {
        static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
        return string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    }
}
