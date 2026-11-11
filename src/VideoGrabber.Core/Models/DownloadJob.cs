using System.Collections.ObjectModel;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Models;

/// <summary>
/// 下载任务。可观察字段在队列所在线程（UI 线程）更新。
/// </summary>
public sealed class DownloadJob : ObservableObject
{
    private JobState _state = JobState.Queued;
    private double _progress;
    private string _speedText = "";
    private string _etaText = "";
    private long _downloadedBytes;
    private long _totalBytes;
    private FailureInfo? _failure;
    private string _commandPreview = "";
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private string _title = "";
    private bool _titleIsResolved;
    private bool _finishEarlyRequested;
    private string? _outputBase;
    private string? _directOutputPath;

    public DownloadJob(
        string urlString,
        string? title,
        QualityChoice quality,
        VideoContainer container,
        string saveDirectory,
        string? cookiesPath,
        string proxyDescription,
        DownloadEngine engine = DownloadEngine.YtDlp,
        string? referer = null,
        string? userAgent = null,
        string? pageURL = null,
        Guid? batchID = null,
        Guid? id = null,
        DateTime? createdAt = null)
    {
        Id = id ?? Guid.NewGuid();
        CreatedAt = createdAt ?? DateTime.Now;
        UrlString = urlString;
        OriginalUrlString = urlString;
        Title = title ?? UrlTools.HostOf(urlString) ?? urlString;
        TitleIsResolved = title != null;
        Quality = quality;
        Container = container;
        SaveDirectory = saveDirectory;
        CookiesPath = cookiesPath;
        ProxyDescription = proxyDescription;
        Engine = engine;
        Referer = referer;
        UserAgent = userAgent;
        PageURL = pageURL;
        BatchID = batchID;
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));
    }

    public Guid Id { get; }
    public DateTime CreatedAt { get; }
    public Guid? BatchID { get; }

    /// <summary>自动接管成功后会被替换成嗅探到的真实地址，原始输入保留在 OriginalUrlString。</summary>
    public string UrlString { get; set; }
    public string OriginalUrlString { get; }
    public string SaveDirectory { get; set; }
    public string? CookiesPath { get; set; }
    public string ProxyDescription { get; }
    public QualityChoice Quality { get; }
    public VideoContainer Container { get; }
    public bool AudioOnly => Quality.IsAudioOnly;

    /// <summary>下载方式与资源嗅探带入的请求头（自动接管会改写）。</summary>
    public DownloadEngine Engine { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? PageURL { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool TitleIsResolved
    {
        get => _titleIsResolved;
        set => SetProperty(ref _titleIsResolved, value);
    }

    public JobState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(ProgressIsDeterminate));
            OnPropertyChanged(nameof(ProgressDetailText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanFinishEarly));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (!SetProperty(ref _progress, value)) return;
            OnPropertyChanged(nameof(PercentText));
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

    public string EtaText
    {
        get => _etaText;
        set
        {
            if (!SetProperty(ref _etaText, value)) return;
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (!SetProperty(ref _downloadedBytes, value)) return;
            OnPropertyChanged(nameof(BytesText));
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (!SetProperty(ref _totalBytes, value)) return;
            OnPropertyChanged(nameof(BytesText));
        }
    }

    public FailureInfo? Failure
    {
        get => _failure;
        set
        {
            if (!SetProperty(ref _failure, value)) return;
            OnPropertyChanged(nameof(HasFailure));
            OnPropertyChanged(nameof(FailureMessage));
            OnPropertyChanged(nameof(FailureHint));
            OnPropertyChanged(nameof(FailureOffersSniffing));
            OnPropertyChanged(nameof(FailureNeedsVerification));
            OnPropertyChanged(nameof(IsDrmFailure));
        }
    }

    public bool HasFailure => _failure != null;
    public string FailureMessage => _failure?.Message ?? "";
    public string FailureHint => _failure?.Hint ?? "";
    public bool FailureOffersSniffing => _failure?.OffersSniffing == true;
    public bool FailureNeedsVerification => _failure?.NeedsVerification == true;
    public bool IsDrmFailure => _failure?.Kind == FailureKind.Drm;

    public ObservableCollection<string> OutputFiles { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>供只读文本框绑定的完整日志文本。</summary>
    public string LogText => string.Join(Environment.NewLine, Logs);

    public string CommandPreview
    {
        get => _commandPreview;
        set => SetProperty(ref _commandPreview, value);
    }

    public DateTime? StartedAt
    {
        get => _startedAt;
        set => SetProperty(ref _startedAt, value);
    }

    public DateTime? FinishedAt
    {
        get => _finishedAt;
        set => SetProperty(ref _finishedAt, value);
    }

    /// <summary>是否走「按分片下载」的 HLS 通道（可断点续传）。</summary>
    public bool IsSegmentedHls { get; set; }

    /// <summary>yt-dlp 失败后的自动重试次数（最多 3 次）。</summary>
    public int AutoRetryCount { get; set; }

    /// <summary>指数退避期间禁止调度。</summary>
    public DateTime? RetryAfter { get; set; }

    /// <summary>本次是从已下载的部分继续，启动时不要清零进度。</summary>
    public bool IsResuming { get; set; }

    /// <summary>输入 HLS 且播放列表没有 #EXT-X-ENDLIST（直播流），不能用 hlsnative。</summary>
    public bool? IsLiveHls { get; set; }

    /// <summary>运行中才发现是直播流，终止 hlsnative 后需要立即改用 ffmpeg 下载器重跑。</summary>
    public bool LiveRestartPending { get; set; }

    /// <summary>下载器已明确报告广告图片流；等待进程退出后保留这个确定性失败，不能误判为网络错误重试。</summary>
    public bool PlaceholderStopPending { get; set; }

    /// <summary>指定格式不可用时是否已经降级到「最佳可用格式」重试过。</summary>
    public bool DidFallbackFormat { get; set; }

    /// <summary>接管后用 yt-dlp 重下失败，是否已经自动回退到 ffmpeg。</summary>
    public bool DidFallbackToFfmpeg { get; set; }

    /// <summary>ffmpeg 直连的目标文件，结束后据此报告产物。</summary>
    public string? DirectOutputPath
    {
        get => _directOutputPath;
        set => SetProperty(ref _directOutputPath, value);
    }

    /// <summary>
    /// 本次任务固定的输出基名（标题 + 视频标识）。一旦生成就不再变化，
    /// 保证续传 / 重试不会因为目录里出现同名文件而改名或误判「已下载」。
    /// </summary>
    public string? OutputBase
    {
        get => _outputBase;
        set => SetProperty(ref _outputBase, value);
    }

    /// <summary>用户点了「提前结束并保存」；进程退出后要把已下载部分封装成视频。</summary>
    public bool FinishEarlyRequested
    {
        get => _finishEarlyRequested;
        set => SetProperty(ref _finishEarlyRequested, value);
    }

    public bool IsActive => State is JobState.Queued or JobState.Running;
    public bool CanResume => State is JobState.Failed or JobState.Cancelled or JobState.Partial;
    public bool CanRetry => State is JobState.Failed or JobState.Cancelled or JobState.Completed or JobState.Partial;
    public bool CanCancel => State is JobState.Running or JobState.Queued;
    public bool CanFinishEarly => State == JobState.Running;

    /// <summary>yt-dlp 与按分片下载的 HLS 都能报确定进度；普通 ffmpeg 直连用不确定进度条。</summary>
    public bool ProgressIsDeterminate => Engine == DownloadEngine.YtDlp || IsSegmentedHls;

    public string StatusText => State.DisplayName();
    public string StatusGlyph => State.Glyph();
    public string EngineText => Engine.DisplayName();
    public string ContainerText => Container.Label();
    public string PercentText => $"{Math.Round(Progress * 100)}%";

    /// <summary>进度行的副标题：百分比 · 速度 · 剩余时间。</summary>
    public string ProgressDetailText
    {
        get
        {
            if (State != JobState.Running) return State.DisplayName();
            if (!ProgressIsDeterminate)
            {
                return DownloadedBytes > 0 ? $"直连下载中 · {FileSize.Format(DownloadedBytes)}" : "直连下载中";
            }
            var parts = new List<string> { $"{Math.Round(Progress * 100)}%" };
            if (!string.IsNullOrEmpty(SpeedText)) parts.Add(SpeedText);
            if (!string.IsNullOrEmpty(EtaText)) parts.Add($"ETA {EtaText}");
            return string.Join(" · ", parts);
        }
    }

    public string BytesText
    {
        get
        {
            if (TotalBytes <= 0)
            {
                return DownloadedBytes > 0 ? FileSize.Format(DownloadedBytes) : "";
            }
            return $"{FileSize.Format(DownloadedBytes)} / {FileSize.Format(TotalBytes)}";
        }
    }

    /// <summary>追加一行日志，保留最近 3000 行。</summary>
    public void AppendLog(string line)
    {
        Logs.Add(line);
        while (Logs.Count > 3000) Logs.RemoveAt(0);
    }
}
