using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

public enum ConversionMode
{
    /// <summary>直接封装，不重新编码。</summary>
    Copy,
    /// <summary>转码成 H.264 / AAC，兼容任何播放器。</summary>
    Transcode
}

public static class ConversionModeText
{
    public static string DisplayName(this ConversionMode mode) =>
        mode == ConversionMode.Copy ? "直接封装" : "转码（H.264 / AAC）";
}

public enum ConversionState
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}

public static class ConversionStateText
{
    public static string DisplayName(this ConversionState state) => state switch
    {
        ConversionState.Idle => "待开始",
        ConversionState.Running => "转换中",
        ConversionState.Completed => "已完成",
        ConversionState.Failed => "失败",
        ConversionState.Cancelled => "已取消",
        _ => ""
    };
}

public enum ConversionFailureKind
{
    FfmpegMissing,
    InvalidInput,
    FileNotFound,
    Forbidden,
    NotFound,
    AesKey,
    Drm,
    IncompatibleContainer,
    Network,
    PermissionDenied,
    Other
}

public sealed class ConversionFailure
{
    public string Message { get; init; } = "";
    public string? Hint { get; init; }
    public ConversionFailureKind Kind { get; init; }

    /// <summary>只有「编码与 mp4 不兼容」才值得提示用户改用转码。</summary>
    public bool OffersTranscode => Kind == ConversionFailureKind.IncompatibleContainer;
}

/// <summary>转换窗口的表单内容；从资源嗅探跳转时会被预填。</summary>
public sealed class ConversionDraft
{
    public bool IsLocal { get; set; }
    public string RemoteURL { get; set; } = "";
    public string LocalPath { get; set; } = "";
    /// <summary>本地分片所在目录（留空表示与 m3u8 同目录）。</summary>
    public string FragmentDirectory { get; set; } = "";
    public string Referer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string? PageURL { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string OutputName { get; set; } = "";
}

/// <summary>一次 m3u8 → mp4 转换任务。</summary>
public sealed class MediaConversionJob : ObservableObject
{
    private ConversionState _state = ConversionState.Idle;
    private double _progress;
    private bool _isDeterminate;
    private double _durationSeconds;
    private double _processedSeconds;
    private long _sizeBytes;
    private string _speedText = "";
    private ConversionFailure? _failure;
    private string _commandPreview = "";

    public MediaConversionJob(
        string input,
        string displayName,
        string outputPath,
        ConversionMode mode,
        string? referer,
        string? userAgent,
        string? pageURL,
        bool isLocalInput,
        string? temporaryDirectory)
    {
        Input = input;
        DisplayName = displayName;
        OutputPath = outputPath;
        Mode = mode;
        Referer = referer;
        UserAgent = userAgent;
        PageURL = pageURL;
        IsLocalInput = isLocalInput;
        TemporaryDirectory = temporaryDirectory;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Input { get; }
    public string DisplayName { get; }
    public string OutputPath { get; }
    public ConversionMode Mode { get; }
    public string? Referer { get; }
    public string? UserAgent { get; }
    public string? PageURL { get; }
    public bool IsLocalInput { get; }
    public string? TemporaryDirectory { get; }
    public ObservableCollection<string> Logs { get; } = new();

    public ConversionState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(ProgressDetailText));
            OnPropertyChanged(nameof(CanRetryWithTranscode));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (!SetProperty(ref _progress, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public bool IsDeterminate
    {
        get => _isDeterminate;
        set
        {
            if (!SetProperty(ref _isDeterminate, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (!SetProperty(ref _durationSeconds, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public double ProcessedSeconds
    {
        get => _processedSeconds;
        set
        {
            if (!SetProperty(ref _processedSeconds, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (!SetProperty(ref _sizeBytes, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public string SpeedText
    {
        get => _speedText;
        set
        {
            if (!SetProperty(ref _speedText, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public ConversionFailure? Failure
    {
        get => _failure;
        set
        {
            if (!SetProperty(ref _failure, value)) return;
            OnPropertyChanged(nameof(CanRetryWithTranscode));
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public string CommandPreview
    {
        get => _commandPreview;
        set => SetProperty(ref _commandPreview, value);
    }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public bool IsRunning => _state == ConversionState.Running;
    public string StateText => _state.DisplayName();
    public bool CanRetryWithTranscode =>
        _state == ConversionState.Failed
        && _failure?.OffersTranscode == true
        && Mode == ConversionMode.Copy;

    public string ProgressDetailText => _state switch
    {
        ConversionState.Running => BuildRunningText(),
        ConversionState.Completed => _sizeBytes > 0 ? "已完成 · " + FileSize.Format(_sizeBytes) : "已完成",
        ConversionState.Failed => "未完成的输出已清理",
        ConversionState.Cancelled => "已取消，未完成的文件已删除",
        _ => "待开始"
    };

    private string BuildRunningText()
    {
        var parts = new List<string>();
        if (_isDeterminate) parts.Add($"{(int)Math.Round(_progress * 100)}%");
        if (_durationSeconds > 0)
        {
            parts.Add($"{TimeText(_processedSeconds)} / {TimeText(_durationSeconds)}");
        }
        if (_sizeBytes > 0) parts.Add(FileSize.Format(_sizeBytes));
        if (!string.IsNullOrEmpty(_speedText)) parts.Add(_speedText);
        return parts.Count == 0 ? "转换中…" : string.Join(" · ", parts);
    }

    public static string TimeText(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "00:00";
        var total = (int)Math.Round(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, secs)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes, secs);
    }
}

/// <summary>
/// m3u8 → mp4 转换器。同一时间只跑一个任务，重复开始会替换上一个已结束的任务。
/// 与下载引擎共用 ffmpeg 参数与代理环境，保证同一地址手动转换也能成功。
/// </summary>
public sealed class MediaConverter
{
    private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"out_time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly IProcessRunner _runner;
    private readonly AppSettings _settings;
    private readonly ToolEnvironment _tools;
    private readonly CookieStore _cookies;
    private readonly Action<Action> _dispatch;

    private IProcessHandle? _process;
    private CancellationTokenSource? _cancellation;

    public MediaConverter(
        IProcessRunner runner,
        AppSettings settings,
        ToolEnvironment tools,
        CookieStore cookies,
        Action<Action> dispatch)
    {
        _runner = runner;
        _settings = settings;
        _tools = tools;
        _cookies = cookies;
        _dispatch = dispatch;
    }

    /// <summary>当前 / 最近一次任务；界面绑定它。</summary>
    public MediaConversionJob? Current { get; private set; }

    public ConversionDraft Draft { get; private set; } = new();

    public void Prefill(string remoteURL, string? referer, string? userAgent, string? pageURL)
    {
        Draft = new ConversionDraft
        {
            IsLocal = false,
            RemoteURL = remoteURL,
            Referer = referer ?? "",
            UserAgent = userAgent ?? "",
            PageURL = pageURL,
            OutputDirectory = string.IsNullOrWhiteSpace(Draft.OutputDirectory)
                ? _settings.DefaultSaveDirectory
                : Draft.OutputDirectory
        };
    }

    public void Start(ConversionMode mode)
    {
        if (Current?.IsRunning == true) return;
        var draft = Draft;

        if (string.IsNullOrWhiteSpace(_tools.Ffmpeg))
        {
            Current = FailedJob(draft, mode,
                "未找到 ffmpeg，无法转换。",
                "到「设置 → 工具」指定 ffmpeg 路径后重新检测；Windows 上可把 ffmpeg.exe 放到程序目录。",
                ConversionFailureKind.FfmpegMissing);
            return;
        }

        PreparedInput prepared;
        try
        {
            prepared = PrepareInput(draft);
        }
        catch (ConversionInputException ex)
        {
            Current = FailedJob(draft, mode, ex.Failure.Message, ex.Failure.Hint, ex.Failure.Kind);
            return;
        }

        var directory = string.IsNullOrWhiteSpace(draft.OutputDirectory)
            ? _settings.DefaultSaveDirectory
            : draft.OutputDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            Current = FailedJob(draft, mode,
                "没有权限写入输出目录：" + directory,
                "在「设置 → 下载」里换一个可写目录（推荐「下载」），再重新开始。",
                ConversionFailureKind.PermissionDenied);
            return;
        }

        var outputPath = MakeOutputPath(prepared, draft, directory);
        var needsNetworkHeaders = !draft.IsLocal || prepared.NetworkBacked;
        var job = new MediaConversionJob(
            prepared.FfmpegInput,
            prepared.DisplayName,
            outputPath,
            mode,
            needsNetworkHeaders ? NullIfEmpty(draft.Referer.Trim()) : null,
            needsNetworkHeaders ? NullIfEmpty(draft.UserAgent.Trim()) : null,
            draft.PageURL,
            draft.IsLocal,
            prepared.TemporaryDirectory)
        {
            State = ConversionState.Running,
            StartedAt = DateTime.Now,
            Progress = 0,
            ProcessedSeconds = 0,
            SizeBytes = 0
        };
        Current = job;

        var arguments = MakeArguments(job);
        job.CommandPreview = Quote(_tools.Ffmpeg!) + " " + string.Join(" ", arguments.Select(Quote));
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _ = RunAsync(job, arguments, cancellation);
    }

    /// <summary>编码与 mp4 不兼容后，用户选择转码重试。</summary>
    public void RetryWithTranscode()
    {
        if (Current is { State: ConversionState.Failed } failed && failed.Failure?.OffersTranscode == true)
        {
            Draft.OutputName = "";
            Start(ConversionMode.Transcode);
        }
    }

    public void Cancel()
    {
        if (Current is not { IsRunning: true } job) return;
        _cancellation?.Cancel();
        try
        {
            _process?.Kill();
        }
        catch (Exception)
        {
            // 进程可能已退出。
        }
        _dispatch(() =>
        {
            job.State = ConversionState.Cancelled;
            job.FinishedAt = DateTime.Now;
            TryDelete(job.OutputPath);
            CleanupTemporary(job);
        });
    }

    public void RevealOutput() => _ = Task.CompletedTask;

    public void Reset()
    {
        if (Current?.IsRunning == true) return;
        Current = null;
    }

    // MARK: - 运行

    private async Task RunAsync(
        MediaConversionJob job,
        IReadOnlyList<string> arguments,
        CancellationTokenSource cancellation)
    {
        IProcessHandle? handle = null;
        try
        {
            handle = _runner.Start(_tools.Ffmpeg!, arguments, ProxyConfig.Environment(_settings));
            _process = handle;
            handle.OutputLine += line => Consume(job, line);
            var exitCode = await handle.WaitForExitAsync();
            if (cancellation.IsCancellationRequested) return;

            var exited = Interlocked.Exchange(ref _process, null);
            exited?.Dispose();
            Finish(job, exitCode);
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                job.State = ConversionState.Failed;
                job.FinishedAt = DateTime.Now;
                job.Failure = new ConversionFailure
                {
                    Message = "无法启动 ffmpeg：" + ex.Message,
                    Hint = "缓存的 ffmpeg 路径可能已失效，请到「设置 → 工具」中重新检测。",
                    Kind = ConversionFailureKind.FfmpegMissing
                };
                TryDelete(job.OutputPath);
                CleanupTemporary(job);
            });
        }
        finally
        {
            if (handle != null)
            {
                try { handle.Dispose(); } catch (Exception) { }
            }
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void Finish(MediaConversionJob job, int exitCode)
    {
        _dispatch(() =>
        {
            job.FinishedAt = DateTime.Now;
            if (exitCode == 0 && File.Exists(job.OutputPath) && new FileInfo(job.OutputPath).Length > 0)
            {
                job.Progress = 1;
                job.IsDeterminate = true;
                job.SizeBytes = new FileInfo(job.OutputPath).Length;
                job.State = ConversionState.Completed;
                CleanupTemporary(job);
                return;
            }

            job.State = ConversionState.Failed;
            job.Failure = ClassifyFailure(job, exitCode);
            TryDelete(job.OutputPath);
            CleanupTemporary(job);
        });
    }

    private void Consume(MediaConversionJob job, string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return;
        _dispatch(() =>
        {
            if (ApplyProgress(job, line)) return;
            var durationMatch = DurationRegex.Match(line);
            if (durationMatch.Success && job.DurationSeconds <= 0)
            {
                job.DurationSeconds = ParseHours(durationMatch);
            }
            job.Logs.Add(line);
            while (job.Logs.Count > 3000) job.Logs.RemoveAt(0);
        });
    }

    private static bool ApplyProgress(MediaConversionJob job, string line)
    {
        if (line.StartsWith("out_time=", StringComparison.Ordinal))
        {
            var match = TimeRegex.Match(line);
            if (match.Success)
            {
                job.ProcessedSeconds = ParseHours(match);
                if (job.DurationSeconds > 0)
                {
                    job.IsDeterminate = true;
                    job.Progress = Math.Clamp(job.ProcessedSeconds / job.DurationSeconds, 0, 1);
                }
            }
            return true;
        }
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            var value = line[(line.IndexOf('=') + 1)..];
            if (long.TryParse(value, out var microseconds) && microseconds >= 0)
            {
                job.ProcessedSeconds = microseconds / 1_000_000.0;
                if (job.DurationSeconds > 0)
                {
                    job.IsDeterminate = true;
                    job.Progress = Math.Clamp(job.ProcessedSeconds / job.DurationSeconds, 0, 1);
                }
            }
            return true;
        }
        if (line.StartsWith("total_size=", StringComparison.Ordinal))
        {
            if (long.TryParse(line[(line.IndexOf('=') + 1)..], out var size) && size >= 0)
            {
                job.SizeBytes = size;
            }
            return true;
        }
        if (line.StartsWith("speed=", StringComparison.Ordinal))
        {
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            job.SpeedText = value is "N/A" or "" ? "" : value;
            return true;
        }
        if (line.StartsWith("progress=", StringComparison.Ordinal)
            || line.StartsWith("bitrate=", StringComparison.Ordinal)
            || line.StartsWith("frame=", StringComparison.Ordinal)
            || line.StartsWith("fps=", StringComparison.Ordinal)
            || line.StartsWith("stream_", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static double ParseHours(Match match)
    {
        var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    // MARK: - 参数

    private IReadOnlyList<string> MakeArguments(MediaConversionJob job)
    {
        var arguments = new List<string>
        {
            "-y", "-nostdin", "-hide_banner", "-loglevel", "info", "-progress", "pipe:1", "-nostats",
            "-protocol_whitelist", "file,http,https,tcp,tls,crypto,httpproxy"
        };

        if (job.Input.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(new[] { "-allowed_extensions", "ALL" });
            arguments.AddRange(_tools.HlsSegmentExtensionArguments);
        }

        var cookieHeader = CookieHeaderFor(job);
        var context = new HlsRequestContext(job.UserAgent, job.Referer, cookieHeader);
        if (context.HeaderValue() is { } headers)
        {
            arguments.AddRange(new[] { "-headers", headers });
        }

        if (job.Input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || job.Input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(new[]
            {
                "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
                "-rw_timeout", "20000000"
            });
        }

        arguments.AddRange(new[] { "-i", job.Input });

        if (job.Mode == ConversionMode.Copy)
        {
            arguments.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            arguments.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-c:a", "aac", "-b:a", "192k"
            });
        }

        arguments.Add(job.OutputPath);
        return arguments;
    }

    private string? CookieHeaderFor(MediaConversionJob job)
    {
        var urls = new List<Uri>();
        if (!string.IsNullOrWhiteSpace(job.PageURL) && Uri.TryCreate(job.PageURL, UriKind.Absolute, out var pageUri))
        {
            urls.Add(pageUri);
        }
        if (Uri.TryCreate(job.Input, UriKind.Absolute, out var mediaUri) && mediaUri.Scheme is "http" or "https")
        {
            urls.Add(mediaUri);
        }
        if (urls.Count == 0) return null;
        return _cookies.CookieHeaderFor(urls);
    }

    // MARK: - 输入准备

    private sealed class PreparedInput
    {
        public string FfmpegInput { get; init; } = "";
        public string DisplayName { get; init; } = "video";
        public string? TemporaryDirectory { get; init; }
        public bool NetworkBacked { get; init; }
    }

    private sealed class ConversionInputException : Exception
    {
        public ConversionInputException(ConversionFailure failure) => Failure = failure;
        public ConversionFailure Failure { get; }
    }

    private PreparedInput PrepareInput(ConversionDraft draft)
    {
        if (draft.IsLocal)
        {
            var path = Environment.ExpandEnvironmentVariables(draft.LocalPath.Trim());
            if (path.Length == 0)
            {
                throw new ConversionInputException(new ConversionFailure
                {
                    Message = "还没有选择本地 m3u8 文件。",
                    Hint = "点「选择 m3u8 文件…」挑一个播放列表；如果分片在别的目录，再指定分片目录。",
                    Kind = ConversionFailureKind.InvalidInput
                });
            }
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new ConversionInputException(new ConversionFailure
                {
                    Message = "找不到文件：" + path,
                    Hint = "确认路径没有写错，或重新选择一次。",
                    Kind = ConversionFailureKind.FileNotFound
                });
            }
            if (Directory.Exists(path))
            {
                var found = Directory.EnumerateFiles(path, "*.m3u8")
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (found == null)
                {
                    throw new ConversionInputException(new ConversionFailure
                    {
                        Message = "这个目录里没有 .m3u8 播放列表。",
                        Hint = "选择包含播放列表（.m3u8）的目录，或直接选文件。",
                        Kind = ConversionFailureKind.InvalidInput
                    });
                }
                path = found;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var fragmentDirectory = Environment.ExpandEnvironmentVariables(draft.FragmentDirectory.Trim());
            var playlistDirectory = Path.GetDirectoryName(path) ?? "";
            var networkBacked = HasRemoteURI(path);
            if (fragmentDirectory.Length == 0 || PathsEqual(fragmentDirectory, playlistDirectory))
            {
                return new PreparedInput
                {
                    FfmpegInput = path,
                    DisplayName = name.Length == 0 ? "video" : name,
                    NetworkBacked = networkBacked
                };
            }

            var rewritten = RewritePlaylist(path, fragmentDirectory);
            return new PreparedInput
            {
                FfmpegInput = rewritten.PlaylistPath,
                DisplayName = name.Length == 0 ? "video" : name,
                TemporaryDirectory = rewritten.Directory,
                NetworkBacked = networkBacked
            };
        }

        var input = draft.RemoteURL.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new ConversionInputException(new ConversionFailure
            {
                Message = "地址不是有效的 http(s) 链接。",
                Hint = "填入以 http:// 或 https:// 开头的 m3u8 地址；本地文件请切到「本地文件」。",
                Kind = ConversionFailureKind.InvalidInput
            });
        }
        var baseName = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
        if (baseName.Length == 0
            || new[] { "index", "master", "playlist", "chunklist", "prog_index", "main" }
                .Contains(baseName.ToLowerInvariant()))
        {
            baseName = uri.Host;
        }
        return new PreparedInput { FfmpegInput = input, DisplayName = baseName };
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasRemoteURI(string path)
    {
        try
        {
            var text = File.ReadAllText(path).ToLowerInvariant();
            return text.Contains("http://") || text.Contains("https://");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (string PlaylistPath, string Directory) RewritePlaylist(string path, string fragmentDirectory)
    {
        var text = File.ReadAllText(path);
        var rewritten = RewriteURIs(text, fragmentDirectory);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "VideoGrabber", "convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var output = Path.Combine(tempDirectory, Path.GetFileName(path));
        File.WriteAllText(output, rewritten);
        return (output, tempDirectory);
    }

    private static string RewriteURIs(string text, string baseDirectory)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                lines.Add(raw);
            }
            else if (line.StartsWith('#'))
            {
                lines.Add(RewriteUriAttribute(raw, baseDirectory));
            }
            else
            {
                lines.Add(ResolveURI(line, baseDirectory));
            }
        }
        return string.Join("\n", lines);
    }

    private static string ResolveURI(string uri, string baseDirectory)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out _)) return uri;
        var expanded = Environment.ExpandEnvironmentVariables(uri);
        if (Path.IsPathRooted(expanded)) return expanded;
        return Path.Combine(baseDirectory, expanded);
    }

    private static string RewriteUriAttribute(string line, string baseDirectory)
    {
        const string marker = "URI=\"";
        if (!line.Contains(marker, StringComparison.Ordinal)) return line;
        var output = new System.Text.StringBuilder();
        var rest = line;
        while (true)
        {
            var index = rest.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                output.Append(rest);
                break;
            }
            output.Append(rest[..(index + marker.Length)]);
            rest = rest[(index + marker.Length)..];
            var end = rest.IndexOf('"');
            if (end < 0)
            {
                output.Append(rest);
                break;
            }
            var value = rest[..end];
            output.Append(ResolveURI(value, baseDirectory));
            output.Append('"');
            rest = rest[(end + 1)..];
        }
        return output.ToString();
    }

    // MARK: - 输出与失败

    private static string MakeOutputPath(PreparedInput prepared, ConversionDraft draft, string directory)
    {
        var baseName = draft.OutputName.Trim();
        if (baseName.Length == 0) baseName = prepared.DisplayName;
        foreach (var invalid in new[] { "/", ":", "\"", "\\", "*", "?", "<", ">", "|" })
        {
            baseName = baseName.Replace(invalid, "_");
        }
        if (baseName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }
        if (baseName.Trim().Length == 0) baseName = "video";
        return FileNameRules.UniquePath(Path.Combine(directory, baseName + ".mp4"));
    }

    private static MediaConversionJob FailedJob(
        ConversionDraft draft,
        ConversionMode mode,
        string message,
        string? hint,
        ConversionFailureKind kind)
    {
        var job = new MediaConversionJob(
            draft.IsLocal ? draft.LocalPath : draft.RemoteURL,
            "—",
            "",
            mode,
            null,
            null,
            null,
            draft.IsLocal,
            null)
        {
            State = ConversionState.Failed,
            FinishedAt = DateTime.Now
        };
        job.Failure = new ConversionFailure { Message = message, Hint = hint, Kind = kind };
        return job;
    }

    private static ConversionFailure ClassifyFailure(MediaConversionJob job, int exitCode)
    {
        var text = string.Join("\n", job.Logs).ToLowerInvariant();
        ConversionFailure Make(string message, string hint, ConversionFailureKind kind) =>
            new() { Message = message, Hint = hint, Kind = kind };

        if (text.Contains("no such file or directory"))
        {
            return Make("找不到输入文件或分片。",
                "本地转换请确认 m3u8 与分片目录都存在；如果分片在别处，在「分片目录」里指定。",
                ConversionFailureKind.FileNotFound);
        }
        if (text.Contains("permission denied"))
        {
            return Make("没有权限读取输入或写入输出。",
                "换一个可写目录（推荐「下载」），或检查文件是否被其它程序占用。",
                ConversionFailureKind.PermissionDenied);
        }
        if (text.Contains("401") || text.Contains("403") || text.Contains("forbidden"))
        {
            return Make("服务器拒绝访问（401 / 403）。",
                "页面可能需要登录或 Referer / Cookie。先用内置浏览器打开页面并保存 Cookie，再从资源嗅探里转换。",
                ConversionFailureKind.Forbidden);
        }
        if (text.Contains("404") || text.Contains("not found"))
        {
            return Make("服务器返回 404：地址或分片已失效。",
                "重新在页面里播放一次，确认资源嗅探里的地址还在；必要时换一个档位。",
                ConversionFailureKind.NotFound);
        }
        if (text.Contains("unable to open key file") || text.Contains("invalid data found when processing input"))
        {
            return Make("HLS 的 AES 密钥打不开。",
                "播放列表里的 key URI 需要 Referer / Cookie 才能取到。先从资源嗅探里转换（会带上页面请求头），或把 key 文件放到分片目录。",
                ConversionFailureKind.AesKey);
        }
        if (text.Contains("drm") || text.Contains("sample-aes"))
        {
            return Make("这条流受 DRM 保护，无法转换。",
                "ffmpeg 只能处理不加密或 AES-128 加密的 HLS；DRM 需要授权解密，本应用不会尝试绕过。",
                ConversionFailureKind.Drm);
        }
        if (text.Contains("could not find tag for codec")
            || text.Contains("not supported")
            || text.Contains("could not find codec parameters")
            || text.Contains("unsupported codec")
            || text.Contains("codec not currently supported"))
        {
            return Make("这条流的编码与 MP4 容器不兼容，直接封装失败。",
                "改用「转码（H.264 / AAC）」重新转换一次；画质会略降，但兼容所有播放器。",
                ConversionFailureKind.IncompatibleContainer);
        }
        if (text.Contains("connection timed out")
            || text.Contains("failed to resolve")
            || text.Contains("could not resolve host")
            || text.Contains("network is unreachable")
            || text.Contains("connection refused")
            || text.Contains("server returned 5"))
        {
            return Make("网络错误，ffmpeg 取不到分片。",
                "检查网络或代理设置；如果浏览器里能播放，可以改用内置浏览器的资源嗅探重新抓取地址。",
                ConversionFailureKind.Network);
        }
        return Make($"ffmpeg 退出（代码 {exitCode}）。",
            "展开下方日志查看完整 ffmpeg 输出；如果地址在浏览器里能播放，可尝试从资源嗅探重新抓取。",
            ConversionFailureKind.Other);
    }

    private void CleanupTemporary(MediaConversionJob job)
    {
        if (string.IsNullOrEmpty(job.TemporaryDirectory)) return;
        try
        {
            if (Directory.Exists(job.TemporaryDirectory)) Directory.Delete(job.TemporaryDirectory, true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响结果。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 输出可能被占用。
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Quote(string value) => value.Contains(' ') ? '"' + value + '"' : value;
}
