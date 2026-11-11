using System.Text.Json;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>下载产物的容器嗅探结果：用来识别「TS 内容被写成 .mp4」这种播不了的文件。</summary>
public enum MediaContainerKind
{
    MpegTs,
    Mp4,
    Matroska,
    Other
}

public readonly record struct MediaVerifyOutcome(bool Ok, string Path, FailureInfo? Failure)
{
    public static MediaVerifyOutcome Success(string path) => new(true, path, null);
    public static MediaVerifyOutcome Failed(FailureInfo failure) => new(false, "", failure);
}

/// <summary>
/// 下载完成前的产物校验：流信息、时长、占位图、可解码性；
/// 校验不通过时自动 remux / 转码，修不好返回失败原因，绝不直接标「已完成」。
/// </summary>
public sealed class MediaVerifier
{
    /// <summary>小于这个体积基本不可能是完整视频。</summary>
    public const long MinimumBytes = 64 * 1024;

    private readonly IProcessRunner _runner;
    private readonly AppSettings _settings;
    private readonly ToolEnvironment _tools;
    private readonly Action<string> _log;

    public MediaVerifier(IProcessRunner runner, AppSettings settings, ToolEnvironment tools, Action<string> log)
    {
        _runner = runner;
        _settings = settings;
        _tools = tools;
        _log = log;
    }

    public async Task<MediaVerifyOutcome> EnsurePlayableAsync(
        string path,
        bool isAudioOnly,
        long expectedBytes,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return MediaVerifyOutcome.Failed(Failure(
                "下载进程已结束，但没有找到输出文件。",
                "确认保存目录可写后点「重试」。"));
        }

        var size = FileLength(path);
        if (size < MinimumBytes)
        {
            return MediaVerifyOutcome.Failed(Failure(
                $"输出文件只有 {size} 字节，明显不完整。",
                "点「继续下载」补齐剩余数据后再试。"));
        }
        if (expectedBytes > MinimumBytes && size < expectedBytes * 0.5)
        {
            return MediaVerifyOutcome.Failed(Failure(
                $"输出文件 {FileSize.Format(size)}，明显小于预期的 {FileSize.Format(expectedBytes)}。",
                "文件可能在结束前被截断，点「继续下载」补齐剩余部分。"));
        }

        var reasons = new List<string>();
        string? skippedReason = null;
        if (Sniff(path) == MediaContainerKind.MpegTs && IsMp4Container(path))
        {
            reasons.Add("MPEG-TS 内容被封装进了 .mp4");
        }
        var inspect = await InspectAsync(path, isAudioOnly, cancellationToken);
        if (inspect.Broken != null) reasons.Add(inspect.Broken);
        if (inspect.Skipped != null)
        {
            skippedReason = inspect.Skipped;
            _log($"[校验] {inspect.Skipped}");
        }

        if (reasons.Count == 0)
        {
            _log(skippedReason != null ? $"[校验] 未做解码校验：{skippedReason}" : $"[校验] 产物校验通过（{FileSize.Format(size)}）");
            return MediaVerifyOutcome.Success(path);
        }

        _log($"[校验] {string.Join("；", reasons)}");

        var ffmpeg = _tools.Ffmpeg;
        if (ffmpeg == null)
        {
            if (_tools.Ffprobe == null)
            {
                _log("[校验] 未找到 ffmpeg / ffprobe，无法校验或修复产物");
                return MediaVerifyOutcome.Success(path);
            }
            return MediaVerifyOutcome.Failed(Failure(
                $"产物校验未通过：{string.Join("；", reasons)}。缺少 ffmpeg，无法自动修复。",
                "安装完整版 ffmpeg（自带 ffprobe）后点「继续下载」，或使用菜单「文件 → 修复本地视频文件…」。",
                FailureKind.FfmpegMissing));
        }

        // 1) 先尝试无损重新封装（最常见：TS-in-MP4、moov 在尾部、时间戳异常）。
        _log("[校验] 正在尝试无损重新封装…");
        if (await RemuxAsync(path, ffmpeg, cancellationToken) is { } remuxed)
        {
            var remuxCheck = await InspectAsync(remuxed, isAudioOnly, cancellationToken);
            if (remuxCheck.Broken == null)
            {
                _log("[校验] 重新封装后校验通过");
                return MediaVerifyOutcome.Success(Replace(path, remuxed));
            }
            TryDelete(remuxed);
        }

        // 2) 再尝试转码（结构损坏但数据可读时）。
        _log("[校验] 无损重新封装未通过，尝试转码修复（耗时较长）…");
        if (await TranscodeAsync(path, ffmpeg, cancellationToken) is { } transcoded)
        {
            var transcodeCheck = await InspectAsync(transcoded, isAudioOnly, cancellationToken);
            if (transcodeCheck.Broken == null)
            {
                _log("[校验] 转码后校验通过");
                return MediaVerifyOutcome.Success(Replace(path, transcoded));
            }
            TryDelete(transcoded);
        }

        return MediaVerifyOutcome.Failed(Failure(
            $"下载已结束，但产物无法播放：{string.Join("；", reasons)}，自动修复也失败了。",
            "文件结构已损坏，需要重新下载。也可以试试菜单「文件 → 修复本地视频文件…」，手动选用别的容器再试。"));
    }

    // MARK: - 校验

    private readonly record struct InspectResult(string? Broken, string? Skipped);

    private async Task<InspectResult> InspectAsync(string path, bool isAudioOnly, CancellationToken cancellationToken)
    {
        if (_tools.Ffprobe is { } ffprobe)
        {
            var probe = await InspectWithFfprobeAsync(path, isAudioOnly, ffprobe, cancellationToken);
            if (probe.Broken != null) return probe;
        }
        else if (_tools.Ffmpeg is { } ffmpeg)
        {
            var probe = await InspectWithFfmpegAsync(path, isAudioOnly, ffmpeg, cancellationToken);
            if (probe.Broken != null || probe.Skipped != null) return probe;
        }
        else
        {
            return new InspectResult(null, "缺少 ffmpeg / ffprobe，跳过产物校验");
        }

        if (_tools.Ffmpeg is not { } decoder) return new InspectResult(null, null);
        if (await DecodeCheckAsync(path, decoder, cancellationToken) is { } error)
        {
            return new InspectResult($"解码测试失败：{error}", null);
        }
        return new InspectResult(null, null);
    }

    private sealed class FfprobeOutput
    {
        public sealed class Stream
        {
            public string? codec_type { get; set; }
            public string? codec_name { get; set; }
            public int? width { get; set; }
            public int? height { get; set; }
            public string? duration { get; set; }
        }

        public sealed class FormatInfo
        {
            public string? duration { get; set; }
            public string? format_name { get; set; }
        }

        public List<Stream>? streams { get; set; }
        public FormatInfo? format { get; set; }
    }

    private async Task<InspectResult> InspectWithFfprobeAsync(
        string path,
        bool isAudioOnly,
        string ffprobe,
        CancellationToken cancellationToken)
    {
        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(
                ffprobe,
                new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path },
                null,
                cancellationToken);
        }
        catch (Exception)
        {
            return new InspectResult(null, null);
        }
        if (output.ExitCode != 0)
        {
            var detail = OutputParser.LastErrorLine(output.Text) ?? $"ffprobe 退出码 {output.ExitCode}";
            return new InspectResult($"ffprobe 无法读取文件：{detail}", null);
        }

        FfprobeOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FfprobeOutput>(output.Text);
        }
        catch (Exception)
        {
            parsed = null;
        }
        if (parsed == null)
        {
            return new InspectResult("ffprobe 输出无法解析，文件结构可能已损坏", null);
        }

        var streams = parsed.streams ?? new List<FfprobeOutput.Stream>();
        var video = streams.Where(stream => stream.codec_type == "video").ToList();
        var audio = streams.Where(stream => stream.codec_type == "audio").ToList();
        if (video.Count == 0 && audio.Count == 0)
        {
            return new InspectResult("没有可识别的音视频流", null);
        }
        if (isAudioOnly)
        {
            if (audio.Count == 0) return new InspectResult("没有音频流", null);
        }
        else
        {
            var videoStream = video.FirstOrDefault();
            if (videoStream == null) return new InspectResult("没有视频流", null);
            var codec = (videoStream.codec_name ?? "").ToLowerInvariant();
            var width = videoStream.width ?? 0;
            var height = videoStream.height ?? 0;
            if (codec is "png" or "gif" or "bmp" or "tiff" or "webp" or "mjpeg")
            {
                return new InspectResult($"视频流是静态图占位（{codec}），不是真实画面", null);
            }
            if (width > 0 && height > 0 && width <= 32 && height <= 32)
            {
                return new InspectResult($"视频分辨率只有 {width}×{height}，不是有效画面", null);
            }
        }

        var durations = new[]
        {
            parsed.format?.duration,
            video.FirstOrDefault()?.duration,
            audio.FirstOrDefault()?.duration
        }
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .Where(value => double.IsFinite(value) && value > 0)
            .ToList();
        var duration = durations.Count > 0 ? durations.Max() : 0;
        if (duration < 1)
        {
            return new InspectResult($"无法读取有效时长（{DurationText(duration)}），索引可能已损坏", null);
        }
        return new InspectResult(null, null);
    }

    /// <summary>没有 ffprobe 时用 ffmpeg 的自述信息兜底解析。</summary>
    private async Task<InspectResult> InspectWithFfmpegAsync(
        string path,
        bool isAudioOnly,
        string ffmpeg,
        CancellationToken cancellationToken)
    {
        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(
                ffmpeg,
                new[] { "-hide_banner", "-nostdin", "-i", path, "-map", "0:v:0?", "-map", "0:a:0?", "-t", "15", "-f", "null", "-" },
                null,
                cancellationToken);
        }
        catch (Exception)
        {
            return new InspectResult(null, "无法启动 ffmpeg，跳过校验");
        }

        var text = output.Text;
        var hasVideo = text.Contains("Video:", StringComparison.Ordinal);
        var hasAudio = text.Contains("Audio:", StringComparison.Ordinal);
        if (!hasVideo && !hasAudio) return new InspectResult("没有可识别的音视频流", null);
        if (isAudioOnly && !hasAudio) return new InspectResult("没有音频流", null);
        if (!isAudioOnly && !hasVideo) return new InspectResult("没有视频流", null);
        if (FirstCodec(text) is { } codec && codec is "png" or "gif" or "bmp" or "tiff" or "webp")
        {
            return new InspectResult($"视频流是静态图占位（{codec}），不是真实画面", null);
        }
        if (DurationIn(text) is not { } duration || duration < 1)
        {
            return new InspectResult("无法读取有效时长，索引可能已损坏", null);
        }
        if (DecodeErrorIn(text) is { } error)
        {
            return new InspectResult($"解码测试失败：{error}", null);
        }
        return new InspectResult(null, null);
    }

    /// <summary>前 15 秒 + 结尾 5 秒各解一次，覆盖「能开头播、结尾坏」的情况。</summary>
    private async Task<string?> DecodeCheckAsync(string path, string ffmpeg, CancellationToken cancellationToken)
    {
        if (await DecodeAsync(path, ffmpeg, new[] { "-t", "15" }, cancellationToken) is { } error) return error;

        string text;
        try
        {
            var output = await _runner.RunAsync(
                ffmpeg,
                new[] { "-hide_banner", "-nostdin", "-i", path, "-f", "null", "-t", "1", "-" },
                null,
                cancellationToken);
            text = output.Text;
        }
        catch (Exception)
        {
            return null;
        }
        if (DurationIn(text) is not { } duration || duration <= 25) return null;
        var start = Math.Max(0, duration - 5);
        return await DecodeAsync(
            path,
            ffmpeg,
            new[] { "-ss", start.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "-t", "5" },
            cancellationToken);
    }

    private async Task<string?> DecodeAsync(string path, string ffmpeg, string[] extra, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        arguments.AddRange(extra);
        arguments.AddRange(new[] { "-i", path, "-map", "0:v:0?", "-map", "0:a:0?", "-f", "null", "-" });

        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(ffmpeg, arguments, null, cancellationToken);
        }
        catch (Exception)
        {
            return "无法启动 ffmpeg";
        }
        if (DecodeErrorIn(output.Text) is { } detail) return detail;
        if (output.ExitCode != 0 && OutputParser.LastErrorLine(output.Text) is { } last) return last;
        return null;
    }

    private static string? DecodeErrorIn(string text)
    {
        string[] markers =
        {
            "Invalid data found when processing input",
            "Error while decoding",
            "moov atom not found",
            "could not find codec parameters",
            "Invalid NAL unit size",
            "Truncated"
        };
        foreach (var marker in markers)
        {
            if (!text.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var line = text.Split('\n')
                .FirstOrDefault(item => item.Contains(marker, StringComparison.OrdinalIgnoreCase))
                ?.Trimmed();
            return line ?? marker;
        }
        return null;
    }

    private static string? FirstCodec(string text)
    {
        const string marker = "Video: ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = text[(index + marker.Length)..];
        var token = new string(rest.TakeWhile(ch => !char.IsWhiteSpace(ch) && ch != ',' && ch != '(').ToArray());
        return token.Length == 0 ? null : token.ToLowerInvariant();
    }

    private static double? DurationIn(string text)
    {
        const string marker = "Duration: ";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = text[(index + marker.Length)..];
        var token = new string(rest.TakeWhile(ch => ch != ',' && !char.IsWhiteSpace(ch)).ToArray());
        if (!token.Contains(':')) return null;
        var parts = token.Split(':')
            .Select(part => double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .ToList();
        if (parts.Count != 3 || parts.Any(double.IsNaN)) return null;
        return parts[0] * 3600 + parts[1] * 60 + parts[2];
    }

    private static string DurationText(double value) =>
        double.IsFinite(value) && value > 0
            ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : "0s";

    // MARK: - 修复

    /// <summary>无损重新封装到同目录临时文件；失败返回 null。</summary>
    public async Task<string?> RemuxAsync(string path, string ffmpeg, CancellationToken cancellationToken = default)
    {
        var extension = IsMp4Container(path) || Path.GetExtension(path).Length == 0 ? "mp4" : Path.GetExtension(path).TrimStart('.');
        var destination = RepairPath(path, "remux", extension);
        TryDelete(destination);

        var arguments = new List<string> { "-y", "-nostdin", "-hide_banner", "-v", "error", "-i", path };
        arguments.AddRange(new[] { "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy" });
        if (extension == "mp4") arguments.AddRange(new[] { "-movflags", "+faststart" });
        arguments.Add(destination);

        var result = await RunFfmpegAsync(ffmpeg, arguments, cancellationToken);
        if (result != 0 || FileLength(destination) <= 0)
        {
            TryDelete(destination);
            return null;
        }
        return destination;
    }

    /// <summary>结构损坏时的兜底转码。</summary>
    public async Task<string?> TranscodeAsync(string path, string ffmpeg, CancellationToken cancellationToken = default)
    {
        var destination = RepairPath(path, "transcode", "mp4");
        TryDelete(destination);
        var arguments = new List<string>
        {
            "-y", "-nostdin", "-hide_banner", "-v", "error",
            "-i", path,
            "-map", "0:v:0?", "-map", "0:a:0?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            destination
        };
        var result = await RunFfmpegAsync(ffmpeg, arguments, cancellationToken);
        if (result != 0 || FileLength(destination) <= 0)
        {
            TryDelete(destination);
            return null;
        }
        return destination;
    }

    private async Task<int> RunFfmpegAsync(string ffmpeg, List<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var output = await _runner.RunAsync(ffmpeg, arguments, ProxyConfig.Environment(_settings), cancellationToken);
            return output.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>修复成功后原地替换，保持任务里的路径不变。</summary>
    private static string Replace(string original, string replacement)
    {
        try
        {
            File.Replace(replacement, original, null, ignoreMetadataErrors: true);
            return original;
        }
        catch (Exception)
        {
            TryDelete(original);
            try
            {
                File.Move(replacement, original);
                return original;
            }
            catch (Exception)
            {
                return replacement;
            }
        }
    }

    private static string RepairPath(string path, string tag, string extension)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, $"{baseName}.vg{tag}.{extension}");
    }

    // MARK: - 工具

    public static MediaContainerKind Sniff(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            var data = buffer.AsSpan(0, read);
            if (data.Length < 4) return MediaContainerKind.Other;
            if (data.Length >= 189 && data[0] == 0x47 && data[188] == 0x47) return MediaContainerKind.MpegTs;
            if (data.Length >= 12
                && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            {
                return MediaContainerKind.Mp4;
            }
            if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            {
                return MediaContainerKind.Matroska;
            }
            return MediaContainerKind.Other;
        }
        catch (Exception)
        {
            return MediaContainerKind.Other;
        }
    }

    private static bool IsMp4Container(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "mp4" or "m4v" or "mov";
    }

    public static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略：下次修复会覆盖。
        }
    }

    private static FailureInfo Failure(string message, string? hint, FailureKind kind = FailureKind.Other) => new()
    {
        Message = message,
        Hint = hint,
        Kind = kind
    };
}
