using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 「提前结束」时把已下载的临时数据封装成可播放的部分视频，并让它通过同样的产物校验。
/// </summary>
public static class PartialMediaBuilder
{
    public enum OutcomeKind
    {
        Success,
        Failure
    }

    public readonly record struct Outcome(OutcomeKind Kind, string Path, string Message)
    {
        public static Outcome Success(string path) => new(OutcomeKind.Success, path, "");
        public static Outcome Failure(string message) => new(OutcomeKind.Failure, "", message);
    }

    private static readonly string[] PartialExtensions =
    {
        ".part", ".ytdl", ".mp4", ".mkv", ".webm", ".ts", ".m4v", ".mov", ".m4a", ".mp3"
    };

    /// <summary>在任务临时目录里找最适合做部分视频的源文件（优先 .part，其次最大的媒体文件）。</summary>
    public static string? FindPartialSource(string directory, bool allowMediaFallback = true)
    {
        if (!Directory.Exists(directory)) return null;

        string? bestPart = null;
        var bestPartSize = 0L;
        string? bestMedia = null;
        var bestMediaSize = 0L;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (!PartialExtensions.Contains(extension)) continue;
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                continue;
            }
            if (size <= 0) continue;

            if (extension is ".part" or ".ytdl")
            {
                if (size <= bestPartSize) continue;
                bestPartSize = size;
                bestPart = file;
            }
            else
            {
                if (size <= bestMediaSize) continue;
                bestMediaSize = size;
                bestMedia = file;
            }
        }

        if (bestPart != null) return bestPart;
        return allowMediaFallback ? bestMedia : null;
    }

    /// <summary>
    /// 把 <paramref name="source"/> 无损重新封装成 `… - 已下载部分.mp4`，必要时退回 MKV。
    /// </summary>
    public static async Task<Outcome> BuildAsync(
        string source,
        string baseName,
        string saveDirectory,
        bool isAudioOnly,
        AppSettings settings,
        ToolEnvironment tools,
        IProcessRunner runner,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source)) return Outcome.Failure("没有找到已下载的临时文件");

        if (tools.Ffmpeg is not { } ffmpeg) return Outcome.Failure("未找到 ffmpeg，无法封装已下载的部分");

        try
        {
            Directory.CreateDirectory(saveDirectory);
        }
        catch (Exception)
        {
            return Outcome.Failure("保存目录不可写");
        }

        var marker = FileNameRules.PartialFileNameMarker;
        var candidates = new List<(string Target, string[] Extra)>();
        if (isAudioOnly)
        {
            candidates.Add((FileNameRules.UniquePath(Path.Combine(saveDirectory, $"{baseName}{marker}.m4a")),
                new[] { "-vn", "-c:a", "copy" }));
            candidates.Add((FileNameRules.UniquePath(Path.Combine(saveDirectory, $"{baseName}{marker}.mp3")),
                new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "0" }));
        }
        else
        {
            candidates.Add((FileNameRules.UniquePath(Path.Combine(saveDirectory, $"{baseName}{marker}.mp4")),
                new[] { "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart" }));
            candidates.Add((FileNameRules.UniquePath(Path.Combine(saveDirectory, $"{baseName}{marker}.mkv")),
                new[] { "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy" }));
        }

        var verifier = new MediaVerifier(runner, settings, tools, log);
        var lastError = "未知错误";
        foreach (var candidate in candidates)
        {
            var arguments = new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-loglevel", "error",
                "-fflags", "+genpts", "-i", source
            };
            arguments.AddRange(candidate.Extra);
            arguments.Add(candidate.Target);

            ProcessOutput output;
            try
            {
                output = await runner.RunAsync(ffmpeg, arguments, ProxyConfig.Environment(settings), cancellationToken);
            }
            catch (Exception)
            {
                lastError = "无法启动 ffmpeg";
                continue;
            }
            if (output.ExitCode != 0)
            {
                lastError = OutputParser.LastErrorLine(output.Text) ?? $"ffmpeg 退出码 {output.ExitCode}";
                TryDelete(candidate.Target);
                log($"[提前结束] 封装失败：{lastError}");
                continue;
            }

            var verified = await verifier.EnsurePlayableAsync(candidate.Target, isAudioOnly, 0, cancellationToken);
            if (verified.Ok) return Outcome.Success(verified.Path);

            lastError = verified.Failure?.Message ?? "未知错误";
            log($"[提前结束] 部分视频校验失败：{lastError}");
            TryDelete(candidate.Target);
        }

        return Outcome.Failure(lastError);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
