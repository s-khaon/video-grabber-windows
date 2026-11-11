using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>调用 `yt-dlp --dump-single-json` 解析标题、时长与可用清晰度。</summary>
public sealed class YtDlpProbe
{
    public sealed record Outcome(
        string Title,
        double? Duration,
        string? Uploader,
        string? Thumbnail,
        IReadOnlyList<int> Heights);

    public sealed class ProbeException : Exception
    {
        public ProbeException(string message, bool needsVerification = false, FailureKind kind = FailureKind.Other)
            : base(message)
        {
            NeedsVerification = needsVerification;
            Kind = kind;
        }

        public bool NeedsVerification { get; }
        public FailureKind Kind { get; }
    }

    private readonly IProcessRunner _runner;
    private readonly ToolEnvironment _tools;
    private readonly AppSettings _settings;

    public YtDlpProbe(IProcessRunner runner, ToolEnvironment tools, AppSettings settings)
    {
        _runner = runner;
        _tools = tools;
        _settings = settings;
    }

    public async Task<Outcome> ProbeAsync(string urlString, string? cookiePath, CancellationToken cancellationToken = default)
    {
        var ytDlp = _tools.Executable(ToolEnvironment.Tool.YtDlp);
        if (ytDlp == null)
        {
            throw new ProbeException(
                _tools.InvalidationNotice ?? "未找到 yt-dlp。请执行 winget install yt-dlp.yt-dlp，或在设置中指定路径。",
                kind: FailureKind.ToolMissing);
        }

        var normalized = UrlTools.Normalize(urlString);
        if (normalized == null) throw new ProbeException("链接格式不正确。");

        var arguments = new List<string>
        {
            "--dump-single-json", "--no-warnings", "--no-playlist", "--skip-download", "--ignore-config"
        };
        arguments.AddRange(ProxyConfig.Resolve(_settings).YtDlpArguments());
        if (!string.IsNullOrEmpty(cookiePath)) arguments.AddRange(new[] { "--cookies", cookiePath });
        arguments.Add(normalized);

        var environment = BuildEnvironment();
        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(ytDlp, arguments, environment, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ProbeException($"无法启动 yt-dlp：{ex.Message}", kind: FailureKind.ToolMissing);
        }

        if (output.ExitCode != 0)
        {
            var info = ErrorExplainer.Explain(output.Text, normalized);
            throw new ProbeException(info.Message, info.NeedsVerification, info.Kind);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output.Text);
            var root = document.RootElement;
            var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var format in formats.EnumerateArray())
                {
                    var vcodec = format.TryGetProperty("vcodec", out var codec) ? codec.GetString() : "none";
                    if (string.IsNullOrEmpty(vcodec) || vcodec == "none") continue;
                    if (format.TryGetProperty("height", out var heightElement)
                        && heightElement.TryGetInt32(out var height) && height > 0)
                    {
                        heights.Add(height);
                    }
                }
            }

            string title = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? titleElement.GetString() ?? normalized
                : normalized;
            double? duration = root.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var seconds)
                ? seconds
                : null;
            string? uploader = root.TryGetProperty("uploader", out var uploaderElement) && uploaderElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? uploaderElement.GetString()
                : null;
            string? thumbnail = root.TryGetProperty("thumbnail", out var thumbnailElement) && thumbnailElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? thumbnailElement.GetString()
                : null;

            return new Outcome(title, duration, uploader, thumbnail, heights.ToList());
        }
        catch (Exception)
        {
            throw new ProbeException("解析 yt-dlp 输出失败。");
        }
    }

    /// <summary>子进程环境：代理 + 把 ffmpeg/ffprobe 目录放进 PATH，yt-dlp 兜底时也能找到。</summary>
    public Dictionary<string, string?> BuildEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONUNBUFFERED"] = "1"
        };
        foreach (var pair in ProxyConfig.Environment(_settings)) environment[pair.Key] = pair.Value;

        var prefixes = new List<string>();
        var ffmpeg = _tools.Executable(ToolEnvironment.Tool.Ffmpeg);
        if (ffmpeg != null) prefixes.Add(Path.GetDirectoryName(ffmpeg) ?? "");
        var ffprobe = _tools.Executable(ToolEnvironment.Tool.Ffprobe);
        if (ffprobe != null)
        {
            var directory = Path.GetDirectoryName(ffprobe) ?? "";
            if (!prefixes.Contains(directory)) prefixes.Add(directory);
        }
        if (prefixes.Count > 0)
        {
            var separator = Path.PathSeparator;
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            environment["PATH"] = string.Join(separator, prefixes) + separator + current;
        }
        return environment;
    }

    public static string? DurationText(double? seconds)
    {
        if (seconds is not { } value || value <= 0) return null;
        var total = (int)Math.Round(value);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
