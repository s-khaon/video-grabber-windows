using System.Collections.ObjectModel;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 下载队列：管理并发、启动 yt-dlp、解析进度与日志。
/// 所有对外的状态变更都会回到构造时所在的同步上下文（WPF 里即 UI 线程），
/// 因此 Core 不需要引用任何 UI 框架。
/// </summary>
public sealed class DownloadManager
{
    private readonly AppSettings _settings;
    private readonly ToolEnvironment _tools;
    private readonly TaskMetadataStore _store;
    private readonly IProcessRunner _runner;
    private readonly YtDlpProbe _probe;
    private readonly CookieStore? _cookies;
    private readonly SynchronizationContext? _context;

    private readonly Dictionary<Guid, IProcessHandle> _processes = new();
    private readonly Dictionary<Guid, HlsDownloadRunner> _hlsRunners = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _hlsCancellations = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _retryWakeups = new();
    private readonly Dictionary<Guid, DateTime> _lastProgressPersist = new();

    public DownloadManager(
        AppSettings settings,
        ToolEnvironment tools,
        TaskMetadataStore store,
        IProcessRunner runner,
        CookieStore? cookies = null)
    {
        _settings = settings;
        _tools = tools;
        _store = store;
        _runner = runner;
        _cookies = cookies;
        _probe = new YtDlpProbe(runner, tools, settings);
        _context = SynchronizationContext.Current;
        Restore();
    }

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    public AppSettings Settings => _settings;
    public ToolEnvironment Tools => _tools;

    /// <summary>任务落败且符合条件时请求平台层启动自动接管（参数：任务、原因）。</summary>
    public event Action<DownloadJob, string>? AutoTakeoverRequested;

    /// <summary>任务被取消 / 移除时，通知平台层中止正在进行的接管。</summary>
    public event Action<DownloadJob>? TakeoverCancelRequested;

    // MARK: - 队列操作

    /// <summary>把链接加入队列；链接不合法时返回 null。</summary>
    public DownloadJob? Enqueue(
        string urlString,
        string? title,
        QualityChoice quality,
        VideoContainer container,
        string saveDirectory,
        bool useCookies,
        DownloadEngine? engine = null,
        string? referer = null,
        string? userAgent = null,
        string? pageURL = null,
        Guid? batchID = null)
    {
        var normalized = UrlTools.Normalize(urlString);
        if (normalized == null) return null;

        // Cookie 复用：按地址所属主域找导出的 cookie 文件。
        string? cookiePath = null;
        if (useCookies && _cookies != null && Uri.TryCreate(normalized, UriKind.Absolute, out var normalizedUri))
        {
            cookiePath = _cookies.CookieFileFor(normalizedUri);
        }

        // 直接粘贴的 m3u8 默认走分片下载，才能断点续传。
        var resolvedEngine = engine ?? (UrlTools.LooksLikeHls(normalized) ? DownloadEngine.FfmpegDirect : DownloadEngine.YtDlp);
        var job = new DownloadJob(
            normalized,
            title,
            quality,
            container,
            saveDirectory,
            cookiePath,
            ProxyConfig.Resolve(_settings).Summary,
            resolvedEngine,
            referer,
            userAgent,
            pageURL,
            batchID);

        Jobs.Insert(0, job);
        PersistNow(job);
        Pump();
        return job;
    }

    public void Cancel(DownloadJob job)
    {
        if (_retryWakeups.Remove(job.Id, out var wakeup))
        {
            wakeup.Cancel();
        }
        job.RetryAfter = null;
        job.FinishEarlyRequested = false;

        switch (job.State)
        {
            case JobState.Running:
                if (job.TakeoverIsActive)
                {
                    TakeoverCancelRequested?.Invoke(job);
                    job.Takeover?.FinishRunningSteps("failed");
                    if (job.Takeover != null)
                    {
                        job.Takeover.Phase = TakeoverPhase.Cancelled;
                        job.Takeover.PausedNote = null;
                    }
                }
                job.State = JobState.Cancelled;
                job.FinishedAt = DateTime.Now;
                job.SpeedText = "";
                job.EtaText = "";
                if (_processes.TryGetValue(job.Id, out var handle))
                {
                    handle.RequestStop();
                }
                if (_hlsCancellations.TryGetValue(job.Id, out var hlsCancellation)) hlsCancellation.Cancel();
                if (!_settings.KeepIncompleteTempFiles) DiscardResumeData(job);
                break;
            case JobState.Queued:
                job.State = JobState.Cancelled;
                job.FinishedAt = DateTime.Now;
                if (!_settings.KeepIncompleteTempFiles) DiscardResumeData(job);
                break;
        }
        if (job.Takeover?.Phase == TakeoverPhase.Retrying)
        {
            TakeoverCancelRequested?.Invoke(job);
            job.Takeover.Phase = TakeoverPhase.Cancelled;
        }
        PersistNow(job);
    }

    /// <summary>
    /// 提前结束：停止下载，用已下载的数据封装成可播放的部分视频；临时数据保留，
    /// 之后点「继续下载」仍可补齐完整版。分片通道与 yt-dlp / ffmpeg 直连都支持。
    /// </summary>
    public void FinishEarly(DownloadJob job)
    {
        if (job.State != JobState.Running) return;
        job.FinishEarlyRequested = true;

        if (_hlsRunners.TryGetValue(job.Id, out var runner))
        {
            runner.RequestFinishEarly();
            job.AppendLog("[提前结束] 停止派发新分片，用已下载的部分生成视频");
            PersistNow(job);
            return;
        }

        if (_processes.TryGetValue(job.Id, out var handle))
        {
            job.AppendLog("[提前结束] 正在停止下载，并用已下载的数据生成部分视频");
            handle.RequestStop();
        }
        PersistNow(job);
    }

    /// <summary>失败 / 取消 / 已保存部分后继续未完成的部分。</summary>
    public void Resume(DownloadJob job)
    {
        if (job.State is not (JobState.Failed or JobState.Cancelled or JobState.Partial)) return;
        if (_retryWakeups.Remove(job.Id, out var wakeup)) wakeup.Cancel();
        job.RetryAfter = null;
        job.IsResuming = true;
        job.State = JobState.Queued;
        job.Failure = null;
        job.PlaceholderStopPending = false;
        job.SpeedText = "";
        job.EtaText = "";
        job.FinishedAt = null;
        job.AppendLog("[续传] 从已完成的进度继续");
        PersistNow(job);
        Pump();
    }

    public void Retry(DownloadJob job)
    {
        if (job.State is not (JobState.Failed or JobState.Cancelled or JobState.Completed or JobState.Partial)) return;
        if (_retryWakeups.Remove(job.Id, out var wakeup)) wakeup.Cancel();
        job.RetryAfter = null;
        job.AutoRetryCount = 0;
        job.IsResuming = false;
        job.IsLiveHls = null;
        job.LiveRestartPending = false;
        job.PlaceholderStopPending = false;
        job.DidFallbackFormat = false;
        job.DidFallbackToFfmpeg = false;
        job.DidAutoTakeover = false;
        job.Takeover = null;
        // 「重试」从头开始：清掉续传数据，并允许重新生成唯一文件名。
        DiscardResumeData(job);
        job.DirectOutputPath = null;
        job.OutputBase = null;
        job.FinishEarlyRequested = false;
        job.State = JobState.Queued;
        job.Progress = 0;
        job.DownloadedBytes = 0;
        job.TotalBytes = 0;
        job.SpeedText = "";
        job.EtaText = "";
        job.Failure = null;
        job.OutputFiles.Clear();
        job.Logs.Clear();
        job.StartedAt = null;
        job.FinishedAt = null;
        PersistNow(job);
        Pump();
    }

    public void Remove(DownloadJob job)
    {
        if (job.State == JobState.Running) Cancel(job);
        if (_retryWakeups.Remove(job.Id, out var wakeup)) wakeup.Cancel();
        _processes.Remove(job.Id);
        Jobs.Remove(job);
        _store.Delete(job.Id, removeFiles: true);
    }

    public void ClearFinished()
    {
        var removed = Jobs.Where(job => job.State is not (JobState.Running or JobState.Queued)).ToList();
        foreach (var job in removed)
        {
            Jobs.Remove(job);
            _store.Delete(job.Id, removeFiles: true);
        }
    }

    /// <summary>并发数或工具路径变化后重新调度。</summary>
    public void Reschedule() => Pump();

    /// <summary>解析链接元数据（新增下载窗口的「解析」）。</summary>
    public Task<YtDlpProbe.Outcome> ProbeAsync(string urlString, CancellationToken cancellationToken = default) =>
        _probe.ProbeAsync(urlString, null, cancellationToken);

    public void CancelAll()
    {
        foreach (var job in Jobs.ToList())
        {
            if (job.State is JobState.Running or JobState.Queued) Cancel(job);
        }
    }

    /// <summary>只中止自动接管（隐藏浏览器 / 已选地址重下阶段），不取消普通下载任务。</summary>
    public int CancelAllTakeovers()
    {
        var count = 0;
        foreach (var job in Jobs.ToList())
        {
            if (job.Takeover is not { } takeover) continue;
            if (!takeover.IsActive && takeover.Phase != TakeoverPhase.Retrying) continue;
            Cancel(job);
            count++;
        }
        return count;
    }

    // MARK: - 调度

    private void Pump()
    {
        var runningCount = Jobs.Count(job => job.State == JobState.Running);
        var limit = Math.Max(1, _settings.MaxConcurrentDownloads);
        var slots = limit - runningCount;
        if (slots <= 0) return;

        // Jobs 是最新在前，倒序遍历保证先来先下载。
        foreach (var job in Jobs.Reverse().ToList())
        {
            if (slots <= 0) break;
            if (job.State != JobState.Queued) continue;
            if (job.RetryAfter is { } retryAfter && retryAfter > DateTime.Now) continue;
            // 同一批粘贴进来的链接串行执行。
            if (job.BatchID is { } batch && Jobs.Any(other => other.BatchID == batch && other.State == JobState.Running))
            {
                continue;
            }

            Start(job);
            if (job.State == JobState.Running) slots--;
        }
    }

    private void Start(DownloadJob job)
    {
        // 站点策略：已确认必须走嗅探的站点，直接跳过必然失败的 yt-dlp 解析。
        if (job.Engine == DownloadEngine.YtDlp
            && job.Takeover == null
            && AutoTakeoverRequested != null
            && _settings.AutoTakeoverEnabled
            && _settings.RemembersSniffing(job.UrlString)
            && PageAddressFor(job) != null)
        {
            StartTakeover(job, "已记住该站点需要嗅探，跳过 yt-dlp");
            return;
        }

        // 分片通道：已解析过播放列表，或用户直接粘贴 .m3u8（直播流改走 ffmpeg 录制）。
        if (job.IsLiveHls != true
            && (job.HlsResume != null
                || (job.Engine == DownloadEngine.FfmpegDirect && UrlTools.LooksLikeHls(job.UrlString))))
        {
            StartHls(job);
            return;
        }
        switch (job.Engine)
        {
            case DownloadEngine.YtDlp:
                StartYtDlp(job);
                break;
            default:
                StartFfmpegDirect(job);
                break;
        }
    }

    // MARK: - HLS 分片通道

    private void StartHls(DownloadJob job)
    {
        if (_tools.Executable(ToolEnvironment.Tool.Ffmpeg) == null)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = new FailureInfo
            {
                Message = "未找到 ffmpeg，无法拼接 HLS 分片。",
                Hint = "执行 winget install Gyan.FFmpeg，或在设置中指定 ffmpeg 路径；也可以改用 yt-dlp 下载。",
                Kind = FailureKind.FfmpegMissing
            };
            PersistNow(job);
            return;
        }

        if (RequestDirectoryAccess(job) is { } permissionFailure)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = permissionFailure;
            PersistNow(job);
            return;
        }

        job.State = JobState.Running;
        job.StartedAt = DateTime.Now;
        job.FinishedAt = null;
        job.Failure = null;
        job.SpeedText = "正在检测播放列表…";
        job.EtaText = "";
        if (!job.IsResuming)
        {
            job.Progress = 0;
            job.DownloadedBytes = 0;
            job.TotalBytes = 0;
        }
        job.IsResuming = false;
        PersistNow(job);

        if (_hlsCancellations.Remove(job.Id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var cts = new CancellationTokenSource();
        _hlsCancellations[job.Id] = cts;

        var runner = new HlsDownloadRunner(
            job,
            _settings,
            _tools,
            _store,
            _runner,
            _cookies == null ? null : urls => _cookies.CookieHeaderFor(urls),
            OnUi)
        {
            Persist = () => PersistNow(job)
        };
        if (job.FinishEarlyRequested) runner.RequestFinishEarly();
        _hlsRunners[job.Id] = runner;
        _ = RunHlsAsync(job, runner, cts);
    }

    private async Task RunHlsAsync(DownloadJob job, HlsDownloadRunner runner, CancellationTokenSource cts)
    {
        HlsRunResult result;
        try
        {
            result = await runner.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new HlsRunResult(HlsResultKind.Cancelled);
        }
        catch (Exception ex)
        {
            result = new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = $"分片下载失败：{ex.Message}",
                Hint = "点「继续下载」重试；已完成的分片会保留。",
                Kind = FailureKind.Other
            });
        }
        finally
        {
            _hlsCancellations.Remove(job.Id, out var stored);
            if (ReferenceEquals(stored, cts)) cts.Dispose();
        }
        OnUi(() => FinishHls(job, result));
    }

    private void FinishHls(DownloadJob job, HlsRunResult result)
    {
        _hlsRunners.Remove(job.Id);
        job.SpeedText = "";
        job.EtaText = "";

        var wasCancelled = job.State == JobState.Cancelled;
        var wasEarlyFinish = job.FinishEarlyRequested;
        job.FinishEarlyRequested = false;

        switch (result.Kind)
        {
            case HlsResultKind.Completed:
                job.State = JobState.Completed;
                job.Progress = 1;
                job.AutoRetryCount = 0;
                job.FinishedAt = DateTime.Now;
                if (result.Path is { } path && File.Exists(path))
                {
                    job.OutputFiles.Clear();
                    job.OutputFiles.Add(path);
                    var size = FileLength(path);
                    job.DownloadedBytes = size;
                    job.TotalBytes = size;
                }
                if (!job.TitleIsResolved && job.OutputFiles.LastOrDefault() is { } last)
                {
                    job.Title = OutputParser.CleanTitle(last);
                    job.TitleIsResolved = true;
                }
                job.AppendLog("==> 下载完成");
                if (job.Takeover is { IsActive: true } doneTakeover)
                {
                    doneTakeover.Phase = TakeoverPhase.Succeeded;
                    doneTakeover.FinishRunningSteps();
                    doneTakeover.Add("下载完成", "done");
                }
                CleanupHlsResume(job);
                break;

            case HlsResultKind.Partial:
                job.State = JobState.Partial;
                job.FinishedAt = DateTime.Now;
                job.AutoRetryCount = 0;
                job.Progress = (double)result.Completed / Math.Max(1, result.Total);
                job.Failure = null;
                if (result.PartialPath is { } partialPath)
                {
                    job.OutputFiles.Clear();
                    job.OutputFiles.Add(partialPath);
                    job.DownloadedBytes = FileLength(partialPath);
                }
                job.AppendLog($"==> 已提前结束：已下载 {result.Completed}/{result.Total} 个分片，已保存为可播放的部分视频");
                job.AppendLog("==> 未下载的分片仍保留，点「继续下载」可补齐完整版");
                // 分片必须保留，才能继续下载。
                break;

            case HlsResultKind.Live:
                job.IsLiveHls = true;
                job.HlsResume = null;
                job.IsSegmentedHls = false;
                if (_tools.YtDlp != null)
                {
                    job.Engine = DownloadEngine.YtDlp;
                    job.State = JobState.Queued;
                    job.AppendLog("[直播] 改用 yt-dlp + ffmpeg 录制（不使用 hlsnative）");
                    PersistNow(job);
                    Pump();
                    return;
                }
                job.AppendLog("[直播] 未找到 yt-dlp，直接用 ffmpeg 录制");
                job.DirectOutputPath = FileNameRules.DirectOutputPath(job);
                StartFfmpegDirect(job);
                return;

            case HlsResultKind.Cancelled:
                job.State = JobState.Cancelled;
                job.FinishedAt = DateTime.Now;
                if (!_settings.KeepIncompleteTempFiles && !wasEarlyFinish) DiscardResumeData(job);
                break;

            default:
                job.FinishedAt = DateTime.Now;
                if (wasCancelled)
                {
                    job.State = JobState.Cancelled;
                }
                else
                {
                    job.State = JobState.Failed;
                    job.Failure = result.Failure;
                    job.AppendLog($"下载失败：{result.Failure?.Message}");
                    if (job.Takeover is { Phase: TakeoverPhase.Retrying } hlsTakeover)
                    {
                        hlsTakeover.FinishRunningSteps("failed");
                        hlsTakeover.Phase = TakeoverPhase.Failed;
                        hlsTakeover.Add("下载失败：" + (result.Failure?.Message ?? "未知错误"), "failed");
                    }
                }
                if (!_settings.KeepIncompleteTempFiles && !wasEarlyFinish) DiscardResumeData(job);
                break;
        }

        PersistNow(job);
        Pump();
    }

    /// <summary>成功后清掉临时分片，只保留任务记录。</summary>
    private void CleanupHlsResume(DownloadJob job)
    {
        var directory = job.HlsResume?.TempDirectory;
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 文件被占用时忽略，下次清理。
            }
        }
        job.HlsResume = null;
        job.IsSegmentedHls = false;
    }

    // MARK: - yt-dlp

    private string YtDlpTempDirectory(Guid jobId) => _store.YtDlpTempDirectory(jobId);

    private static long PartialBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        return Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly)
            .Sum(file =>
            {
                try
                {
                    return new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    return 0L;
                }
            });
    }

    private FailureInfo? RequestDirectoryAccess(DownloadJob job)
    {
        try
        {
            Directory.CreateDirectory(job.SaveDirectory);
            var probe = Path.Combine(job.SaveDirectory, $".videograbber-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception)
        {
            return new FailureInfo
            {
                Message = "没有权限写入保存目录。",
                Hint = "点任务详情里的「重新选择保存目录」换一个可写位置；也可以在「设置 → 下载」改默认目录后重试。",
                Kind = FailureKind.PermissionDenied
            };
        }
    }

    private void StartYtDlp(DownloadJob job)
    {
        var ytDlp = _tools.Executable(ToolEnvironment.Tool.YtDlp);
        if (ytDlp == null)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            var notice = _tools.InvalidationNotice;
            job.Failure = notice != null
                ? new FailureInfo
                {
                    Message = notice,
                    Hint = "打开「设置 → 工具」点击「重新检测」，或重新指定 yt-dlp 路径。",
                    Kind = FailureKind.ToolMissing
                }
                : new FailureInfo
                {
                    Message = "未找到 yt-dlp。",
                    Hint = "执行 winget install yt-dlp.yt-dlp，或在设置中指定 yt-dlp 路径。",
                    Kind = FailureKind.ToolMissing
                };
            PersistNow(job);
            return;
        }

        if (RequestDirectoryAccess(job) is { } permissionFailure)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = permissionFailure;
            PersistNow(job);
            return;
        }

        job.State = JobState.Running;
        job.StartedAt = DateTime.Now;
        job.FinishedAt = null;
        // 续传时保留已下载进度，进度条不从 0 开始。
        if (!job.IsResuming)
        {
            job.Progress = 0;
            job.DownloadedBytes = 0;
            job.TotalBytes = 0;
        }
        job.Failure = null;
        job.SpeedText = "";
        job.EtaText = "";
        job.IsResuming = false;

        // 把 .part 放在任务专属临时目录：重启后能准确找回，继续时只补剩余字节。
        var tempDirectory = YtDlpTempDirectory(job.Id);
        try
        {
            Directory.CreateDirectory(tempDirectory);
        }
        catch (Exception)
        {
            // 创建失败时 yt-dlp 会直接报错，交给失败分类处理。
        }
        var partialBytes = PartialBytes(tempDirectory);
        if (partialBytes > 0)
        {
            job.DownloadedBytes = Math.Max(job.DownloadedBytes, partialBytes);
            if (job.TotalBytes > 0)
            {
                job.Progress = Math.Min(0.99, (double)job.DownloadedBytes / job.TotalBytes);
            }
            job.AppendLog($"[续传] 发现未完成文件 {FileSize.Format(partialBytes)}，从断点继续");
        }
        PersistNow(job);

        if (_tools.Ffprobe == null)
        {
            job.AppendLog("[环境] 未检测到 ffprobe：yt-dlp 的元数据提取 / FixupM3u8 收尾不可用，下载完成后将由 ffmpeg 校验并重新封装");
        }

        var arguments = YtDlpArgumentBuilder.Build(job, _settings, _tools, tempDirectory);
        job.CommandPreview = YtDlpArgumentBuilder.Preview(ytDlp, arguments);

        try
        {
            var handle = _runner.Start(ytDlp, arguments, _probe.BuildEnvironment());
            _processes[job.Id] = handle;
            handle.OutputLine += line => OnUi(() => ConsumeLine(job, line));
            _ = HandleExitAsync(job.Id, handle);
        }
        catch (Exception ex)
        {
            // 缓存的可执行文件可能已被移动/删除，标记为失效，提示用户重新检测。
            _tools.Invalidate(ToolEnvironment.Tool.YtDlp, "无法执行该文件");
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = new FailureInfo
            {
                Message = $"无法启动 yt-dlp：{ex.Message}",
                Hint = "缓存的 yt-dlp 路径可能已失效，请在「设置 → 工具」中重新检测。",
                Kind = FailureKind.ToolMissing
            };
            PersistNow(job);
            Pump();
        }
    }

    private async Task HandleExitAsync(Guid jobId, IProcessHandle handle)
    {
        int exitCode;
        try
        {
            exitCode = await handle.WaitForExitAsync();
        }
        catch (Exception)
        {
            exitCode = -1;
        }
        handle.Dispose();
        OnUi(() => _ = FinishAsync(jobId, exitCode));
    }

    // MARK: - ffmpeg 直连

    private void StartFfmpegDirect(DownloadJob job)
    {
        var ffmpeg = _tools.Executable(ToolEnvironment.Tool.Ffmpeg);
        if (ffmpeg == null)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = new FailureInfo
            {
                Message = "未找到 ffmpeg，无法直连下载。",
                Hint = "执行 winget install Gyan.FFmpeg，或在设置中指定 ffmpeg 路径；也可以改用 yt-dlp 下载。",
                Kind = FailureKind.FfmpegMissing
            };
            PersistNow(job);
            return;
        }

        if (RequestDirectoryAccess(job) is { } permissionFailure)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = permissionFailure;
            PersistNow(job);
            return;
        }

        // 续传时沿用上一次的目标路径，不能因为文件已存在就改成 `(2)` 从头下载。
        var outputPath = job.DirectOutputPath ?? FileNameRules.DirectOutputPath(job);
        job.DirectOutputPath = outputPath;

        var arguments = FfmpegArgumentBuilder.BuildDirect(job, _tools, outputPath);
        job.CommandPreview = YtDlpArgumentBuilder.Preview(ffmpeg, arguments);

        job.State = JobState.Running;
        job.StartedAt = DateTime.Now;
        job.FinishedAt = null;
        if (!job.IsResuming)
        {
            job.Progress = 0;
            job.DownloadedBytes = 0;
            job.TotalBytes = 0;
        }
        job.Failure = null;
        job.SpeedText = "";
        job.EtaText = "";
        job.IsResuming = false;

        try
        {
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ProxyConfig.Environment(_settings)) env[pair.Key] = pair.Value;
            var handle = _runner.Start(ffmpeg, arguments, env);
            _processes[job.Id] = handle;
            handle.OutputLine += line => OnUi(() => ConsumeLine(job, line));
            _ = HandleExitAsync(job.Id, handle);
        }
        catch (Exception ex)
        {
            _tools.Invalidate(ToolEnvironment.Tool.Ffmpeg, "无法执行该文件");
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = new FailureInfo
            {
                Message = $"无法启动 ffmpeg：{ex.Message}",
                Hint = "缓存的 ffmpeg 路径可能已失效，请在「设置 → 工具」中重新检测。",
                Kind = FailureKind.FfmpegMissing
            };
            PersistNow(job);
            Pump();
        }
    }

    // MARK: - 输出解析

    private void ConsumeLine(DownloadJob job, string rawLine)
    {
        var line = rawLine.StrippingAnsi().Trimmed();
        if (line.Length == 0) return;

        if (OutputParser.TryParseProgress(line, out var sample))
        {
            ApplyProgress(job, sample);
            return;
        }

        if (job.Engine == DownloadEngine.FfmpegDirect
            && OutputParser.TryParseFfmpegProgress(line, out var totalSize))
        {
            if (totalSize > 0) job.DownloadedBytes = totalSize;
            return;
        }

        job.AppendLog(line);

        // yt-dlp 在「直播状态未知」时也会打印这条警告，只有探测结果不是明确的 VOD 时才切换下载器。
        if (job.Engine == DownloadEngine.YtDlp
            && job.IsLiveHls != false
            && !job.LiveRestartPending
            && line.Contains("Live HLS streams are not supported by the native downloader"))
        {
            job.IsLiveHls = true;
            job.LiveRestartPending = true;
            job.AppendLog("[直播] yt-dlp 判定为直播流：停止 hlsnative，改用 ffmpeg 下载器重新录制");
            if (_processes.TryGetValue(job.Id, out var handle)) handle.RequestStop();
            return;
        }

        if (OutputParser.TryParseDestination(line, out var path, out var isMerge))
        {
            if (isMerge)
            {
                // 合并后的文件才是最终产物，替换掉中间的音视频分片。
                job.OutputFiles.Clear();
                job.OutputFiles.Add(path);
            }
            else if (!job.OutputFiles.Contains(path))
            {
                job.OutputFiles.Add(path);
            }
        }
    }

    private void ApplyProgress(DownloadJob job, ProgressSample sample)
    {
        if (!sample.Recognized) return;

        job.Progress = sample.Progress;
        job.SpeedText = sample.Speed;
        job.EtaText = sample.Eta;
        if (sample.DownloadedBytes > 0) job.DownloadedBytes = sample.DownloadedBytes;
        if (sample.TotalBytes > 0) job.TotalBytes = sample.TotalBytes;

        // 每 2 秒落一次进度，进程被强杀后重启仍能从上次位置显示。
        var now = DateTime.Now;
        if (!_lastProgressPersist.TryGetValue(job.Id, out var last) || (now - last).TotalSeconds >= 2)
        {
            _lastProgressPersist[job.Id] = now;
            PersistNow(job);
        }
    }

    // MARK: - 收尾

    private async Task FinishAsync(Guid jobId, int exitCode)
    {
        _processes.Remove(jobId);
        var job = Jobs.FirstOrDefault(item => item.Id == jobId);
        if (job == null) return;

        job.SpeedText = "";
        job.EtaText = "";

        // 下载器输出已确认这是广告 / 图片流：保留确定性失败原因，不进入自动重试。
        if (job.PlaceholderStopPending)
        {
            job.PlaceholderStopPending = false;
            if (job.Engine == DownloadEngine.FfmpegDirect && job.DirectOutputPath is { } directPath)
            {
                TryDeleteFile(directPath);
            }
            RemoveYtDlpTemp(job);
            FailPlaceholderStream(job);
            return;
        }

        if (job.State == JobState.Cancelled)
        {
            job.FinishedAt = DateTime.Now;
            job.Takeover?.FinishRunningSteps("failed");
            if (job.Takeover?.Phase == TakeoverPhase.Retrying)
            {
                job.Takeover.Phase = TakeoverPhase.Cancelled;
            }
            if (job.Engine == DownloadEngine.FfmpegDirect
                && job.DirectOutputPath is { } path
                && !_settings.KeepIncompleteTempFiles)
            {
                TryDeleteFile(path);
            }
            PersistNow(job);
            Pump();
            return;
        }

        // 用户提前结束：把已下载的数据封装成部分视频，点「继续下载」仍可补齐。
        if (job.FinishEarlyRequested && exitCode != 0)
        {
            job.FinishEarlyRequested = false;
            await BuildPartialAsync(job);
            return;
        }

        if (exitCode == 0)
        {
            job.AutoRetryCount = 0;
            job.RetryAfter = null;
            job.FinishedAt = DateTime.Now;
            RemoveYtDlpTemp(job);

            if (job.Engine == DownloadEngine.FfmpegDirect
                && job.DirectOutputPath is { } directOutput
                && File.Exists(directOutput))
            {
                job.OutputFiles.Clear();
                job.OutputFiles.Add(directOutput);
                if (job.TotalBytes == 0)
                {
                    job.DownloadedBytes = FileLength(directOutput);
                }
            }
            if (job.TotalBytes > 0) job.DownloadedBytes = job.TotalBytes;

            // 临时目录里的路径会在收尾时被移除，以保存目录里的最终文件为准。
            var finalFiles = ResolvedOutputFiles(job);
            if (finalFiles.Count > 0)
            {
                job.OutputFiles.Clear();
                foreach (var file in finalFiles) job.OutputFiles.Add(file);
            }
            if (!job.TitleIsResolved)
            {
                if (job.OutputFiles.LastOrDefault() is { } lastOutput)
                {
                    job.Title = OutputParser.CleanTitle(lastOutput);
                }
                job.TitleIsResolved = true;
            }

            if (job.OutputFiles.Count == 0)
            {
                job.State = JobState.Failed;
                job.Failure = new FailureInfo
                {
                    Message = "下载进程已结束，但没有找到输出文件。",
                    Hint = "确认保存目录可写后点「重试」。",
                    Kind = FailureKind.Other
                };
                job.AppendLog("==> 未找到输出文件，任务失败");
                PersistNow(job);
                Pump();
                return;
            }

            // 校验产物：体积、魔数、ffprobe 解码；不通过时自动 remux / 转码。
            var primary = job.OutputFiles[job.OutputFiles.Count - 1];
            var verifier = new MediaVerifier(_runner, _settings, _tools, job.AppendLog);
            var verified = await verifier.EnsurePlayableAsync(
                primary,
                job.AudioOnly,
                job.TotalBytes,
                CancellationToken.None);
            if (!verified.Ok)
            {
                job.State = JobState.Failed;
                job.Failure = verified.Failure;
                job.AppendLog($"==> 产物校验失败：{verified.Failure?.Message}");
                PersistNow(job);
                Pump();
                return;
            }
            if (verified.Path != primary)
            {
                job.OutputFiles[job.OutputFiles.Count - 1] = verified.Path;
            }

            job.State = JobState.Completed;
            job.Progress = 1;
            job.AppendLog("==> 下载完成");
            if (job.Takeover is { IsActive: true } takeover)
            {
                takeover.Phase = TakeoverPhase.Succeeded;
                takeover.FinishRunningSteps();
                takeover.Add("下载完成", "done");
                job.AppendLog("[自动接管] 下载完成");
            }
            PersistNow(job);
            Pump();
            return;
        }

        // 运行中发现是直播：清掉 hlsnative 的半成品，立刻用 ffmpeg 下载器重新录制。
        if (job.LiveRestartPending)
        {
            job.LiveRestartPending = false;
            job.State = JobState.Queued;
            job.IsResuming = false;
            job.Progress = 0;
            job.DownloadedBytes = 0;
            job.TotalBytes = 0;
            job.Failure = null;
            job.AutoRetryCount = 0;
            job.RetryAfter = null;
            RemoveYtDlpTemp(job);
            job.AppendLog("[直播] 改用 ffmpeg 下载器（--hls-use-mpegts）重新录制");
            PersistNow(job);
            Pump();
            return;
        }

        // yt-dlp 的 hlsnative 在个别分片失败时会跳过该分片继续合并，最终仍会生成可播放文件。
        if (job.Engine == DownloadEngine.YtDlp && RecoveredOutputAfterFailure(job) is { } recovered)
        {
            var recoveredVerifier = new MediaVerifier(_runner, _settings, _tools, job.AppendLog);
            var recoveredCheck = await recoveredVerifier.EnsurePlayableAsync(
                recovered,
                job.AudioOnly,
                job.TotalBytes,
                CancellationToken.None);
            if (recoveredCheck.Ok)
            {
                job.State = JobState.Completed;
                job.FinishedAt = DateTime.Now;
                job.Progress = 1;
                job.Failure = null;
                job.AutoRetryCount = 0;
                job.RetryAfter = null;
                job.OutputFiles.Clear();
                job.OutputFiles.Add(recoveredCheck.Path);
                var size = FileLength(recoveredCheck.Path);
                job.DownloadedBytes = size;
                job.TotalBytes = Math.Max(job.TotalBytes, size);
                job.AppendLog("==> 下载器报告了分片错误，但产物可正常播放，按完成处理");
                if (job.Takeover is { IsActive: true } takeover)
                {
                    takeover.Phase = TakeoverPhase.Succeeded;
                    takeover.FinishRunningSteps();
                    takeover.Add("下载完成（部分分片被跳过）", "done");
                }
                RemoveYtDlpTemp(job);
                PersistNow(job);
                Pump();
                return;
            }
            job.AppendLog("[校验] 失败后找到的产物无法播放，继续按失败处理");
        }

        // 接管后用 yt-dlp 重下仍失败：同一地址改用 ffmpeg 直连（Referer / UA / Cookie 不变）。
        if (job.DidAutoTakeover && job.Engine == DownloadEngine.YtDlp && !job.DidFallbackToFfmpeg)
        {
            job.DidFallbackToFfmpeg = true;
            job.Engine = DownloadEngine.FfmpegDirect;
            job.State = JobState.Queued;
            job.Progress = 0;
            job.DownloadedBytes = 0;
            job.TotalBytes = 0;
            job.Failure = null;
            job.AutoRetryCount = 0;
            job.RetryAfter = null;
            job.IsResuming = false;
            job.DirectOutputPath = null;
            DiscardResumeData(job);
            job.AppendLog("[自动接管] yt-dlp 重下仍失败，改用 ffmpeg 直连（相同 Referer / UA / Cookie）");
            job.Takeover?.FinishRunningSteps("failed");
            job.Takeover?.Add("yt-dlp 重下仍失败，自动改用 ffmpeg 直连重试");
            PersistNow(job);
            Pump();
            return;
        }

        var combined = string.Join("\n", job.Logs);

        // 只有日志明确说格式不可用时，才降级到最佳可用格式重试一次。
        if (job.Takeover == null
            && job.Engine == DownloadEngine.YtDlp
            && !job.DidFallbackFormat
            && (combined.Contains("Requested format is not available") || combined.Contains("No video formats found")))
        {
            job.DidFallbackFormat = true;
            job.State = JobState.Queued;
            job.IsResuming = true;
            job.Failure = null;
            job.AutoRetryCount = 0;
            job.RetryAfter = null;
            job.AppendLog("[格式] 指定格式不可用，改用最佳可用格式重试");
            PersistNow(job);
            Pump();
            return;
        }

        var failure = ErrorExplainer.Explain(combined, job.UrlString);
        job.Failure = failure;

        // 瞬时错误先自动重试 3 次（2s / 4s / 8s 退避），保留 .part 不重复下载。
        if (job.Takeover == null
            && job.Engine == DownloadEngine.YtDlp
            && job.AutoRetryCount < 3
            && ShouldAutoRetry(failure))
        {
            job.AutoRetryCount += 1;
            var delay = Math.Pow(2, job.AutoRetryCount);
            job.State = JobState.Queued;
            job.IsResuming = true;
            job.RetryAfter = DateTime.Now.AddSeconds(delay);
            job.AppendLog($"[自动重试] 第 {job.AutoRetryCount}/3 次，{delay:0} 秒后继续（保留已下载部分）");
            ScheduleRetryWakeup(job);
            PersistNow(job);
            Pump();
            return;
        }

        job.State = JobState.Failed;
        job.FinishedAt = DateTime.Now;
        if (!_settings.KeepIncompleteTempFiles)
        {
            if (job.DirectOutputPath is { } path) TryDeleteFile(path);
            DiscardResumeData(job);
        }

        if (job.Takeover is { } progress)
        {
            if (progress.Phase == TakeoverPhase.Retrying)
            {
                progress.FinishRunningSteps("failed");
                progress.Phase = TakeoverPhase.Failed;
                progress.Add("下载失败：" + (job.Failure?.Message ?? "未知错误"), "failed");
            }
        }
        else if (ShouldAutoTakeover(job, failure))
        {
            StartTakeover(job, $"yt-dlp 失败（{FailureLabel(failure?.Kind)}），自动接管");
            return;
        }

        PersistNow(job);
        Pump();
    }

    // MARK: - 自动接管

    private bool ShouldAutoTakeover(DownloadJob job, FailureInfo? failure)
    {
        if (!_settings.AutoTakeoverEnabled || failure == null) return false;
        if (failure.Kind is not (FailureKind.Forbidden or FailureKind.LoginRequired or FailureKind.ExtractorUnsupported))
        {
            return false;
        }
        if (job.Engine != DownloadEngine.YtDlp || job.DidAutoTakeover || job.Takeover != null || job.PageURL != null)
        {
            return false;
        }
        return PageAddressFor(job) != null;
    }

    private void StartTakeover(DownloadJob job, string reason)
    {
        var progress = new TakeoverProgress();
        progress.Add("触发原因：" + reason);
        job.Takeover = progress;
        job.DidAutoTakeover = true;
        job.State = JobState.Running;
        job.StartedAt = DateTime.Now;
        job.FinishedAt = null;
        job.Failure = null;
        job.Progress = 0;
        job.DownloadedBytes = 0;
        job.TotalBytes = 0;
        job.SpeedText = "";
        job.EtaText = "";
        job.Logs.Insert(0, "[自动接管] " + reason);
        PersistNow(job);
        AutoTakeoverRequested?.Invoke(job, reason);
    }

    /// <summary>接管找到地址后，把同一任务改写成直连下载，保留完整接管步骤。</summary>
    public void BeginRetryWithTakeover(
        DownloadJob job,
        string urlString,
        string? referer,
        string? userAgent,
        string? pageURL,
        string? cookiePath)
    {
        job.UrlString = urlString;
        job.Engine = DownloadEngine.YtDlp;
        job.Referer = referer;
        job.UserAgent = userAgent;
        job.PageURL = pageURL;
        if (!string.IsNullOrEmpty(cookiePath)) job.CookiesPath = cookiePath;
        job.DirectOutputPath = null;
        job.DidFallbackToFfmpeg = false;
        job.State = JobState.Queued;
        job.Failure = null;
        job.Progress = 0;
        job.DownloadedBytes = 0;
        job.TotalBytes = 0;
        job.SpeedText = "";
        job.EtaText = "";
        job.StartedAt = null;
        job.FinishedAt = null;
        job.AutoRetryCount = 0;
        job.RetryAfter = null;
        job.IsResuming = false;
        if (job.Takeover != null) job.Takeover.Phase = TakeoverPhase.Retrying;
        DiscardResumeData(job);
        job.AppendLog("[自动接管] 改用嗅探地址：" + urlString);
        PersistNow(job);
        Pump();
    }

    /// <summary>接管在进入下载前就结束时，让队列重新分配并发名额。</summary>
    public void NotifyTakeoverFinished() => Pump();

    /// <summary>接管只针对网页地址；媒体直链没有页面可加载。</summary>
    public static string? PageAddressFor(DownloadJob job)
    {
        var page = job.PageURL?.Trim();
        if (!string.IsNullOrEmpty(page) && !IsDirectMedia(page)) return page;

        var address = job.UrlString.Trim();
        if (string.IsNullOrEmpty(address) || IsDirectMedia(address)) return null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        return uri.Host.Contains('.') ? address : null;
    }

    public static bool IsDirectMedia(string address)
    {
        var path = address.Split('?')[0].Split('#')[0];
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var media = new[]
        {
            "m3u8", "m3u", "mpd", "mp4", "m4v", "mov", "webm", "m4s", "ts", "mp3", "m4a", "aac", "flac", "wav"
        };
        return media.Contains(extension);
    }

    private static string FailureLabel(FailureKind? kind) => kind switch
    {
        FailureKind.Forbidden => "403 / 需要鉴权",
        FailureKind.LoginRequired => "需要登录",
        FailureKind.ExtractorUnsupported => "解析不到可下载地址",
        FailureKind.Drm => "DRM 加密",
        FailureKind.Network => "网络错误",
        FailureKind.ToolMissing => "缺少工具",
        FailureKind.FfmpegMissing => "缺少 ffmpeg",
        FailureKind.PermissionDenied => "保存目录不可写",
        FailureKind.PlaceholderStream => "广告 / 占位流",
        FailureKind.UnsupportedMedia => "编码或容器不支持",
        _ => "未知错误"
    };

    /// <summary>提前结束：把已下载的临时数据封装成部分视频，状态标为「已保存部分」。</summary>
    private async Task BuildPartialAsync(DownloadJob job)
    {
        var source = job.Engine == DownloadEngine.YtDlp
            ? PartialMediaBuilder.FindPartialSource(YtDlpTempDirectory(job.Id))
            : job.DirectOutputPath is { } direct && File.Exists(direct) ? direct : null;

        if (source == null || FileLength(source) < MediaVerifier.MinimumBytes)
        {
            job.State = JobState.Failed;
            job.FinishedAt = DateTime.Now;
            job.Failure = new FailureInfo
            {
                Message = "提前结束时还没有下载到足够的数据，无法生成可播放的视频。",
                Hint = "点「继续下载」再多下一点后重试；也可以点「重试」从头开始。",
                Kind = FailureKind.Other
            };
            job.AppendLog("==> 已提前结束，但没有可用的部分数据");
            PersistNow(job);
            Pump();
            return;
        }

        job.AppendLog("[提前结束] 正在把已下载的数据封装成部分视频…");
        var baseName = FileNameRules.MakeOutputBase(job);
        var result = await PartialMediaBuilder.BuildAsync(
            source,
            baseName,
            job.SaveDirectory,
            job.AudioOnly,
            _settings,
            _tools,
            _runner,
            job.AppendLog,
            CancellationToken.None);

        job.FinishedAt = DateTime.Now;
        job.SpeedText = "";
        job.EtaText = "";
        if (result.Kind == PartialMediaBuilder.OutcomeKind.Success)
        {
            job.State = JobState.Partial;
            job.Failure = null;
            job.OutputFiles.Clear();
            job.OutputFiles.Add(result.Path);
            job.DownloadedBytes = FileLength(result.Path);
            job.AppendLog($"==> 已提前结束，已保存部分视频：{result.Path}");
            job.AppendLog("==> 临时数据已保留，点「继续下载」可补齐完整版");
        }
        else
        {
            job.State = JobState.Failed;
            job.Failure = new FailureInfo
            {
                Message = $"提前结束时无法生成部分视频：{result.Message}",
                Hint = "点「继续下载」补齐更多数据后再提前结束；也可以点「重试」从头开始。",
                Kind = FailureKind.Other
            };
            job.AppendLog($"==> 提前结束失败：{result.Message}");
        }
        PersistNow(job);
        Pump();
    }

    /// <summary>只有网络类错误才值得自动重试；确定性失败重试只会浪费时间。</summary>
    private static bool ShouldAutoRetry(FailureInfo? failure) => failure == null || failure.Kind == FailureKind.Network;

    private void ScheduleRetryWakeup(DownloadJob job)
    {
        if (_retryWakeups.Remove(job.Id, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _retryWakeups[job.Id] = cts;
        var delay = job.RetryAfter is { } after ? after - DateTime.Now : TimeSpan.Zero;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            OnUi(() =>
            {
                _retryWakeups.Remove(job.Id);
                job.RetryAfter = null;
                if (job.State is JobState.Queued or JobState.Failed)
                {
                    job.State = JobState.Queued;
                    Pump();
                }
            });
        });
    }

    /// <summary>播放列表被判定为广告 / 占位流：立刻失败，不重试。</summary>
    private void FailPlaceholderStream(DownloadJob job)
    {
        job.State = JobState.Failed;
        job.FinishedAt = DateTime.Now;
        job.Progress = 0;
        job.SpeedText = "";
        job.EtaText = "";
        job.Failure = new FailureInfo
        {
            Message = "这条播放列表是广告 / 占位流，不是视频。",
            Hint = "回到页面里播放正片，等资源面板出现非广告地址后再下载；如果页面上只抓到广告档，请播放正片后重新抓一次。",
            Kind = FailureKind.PlaceholderStream
        };
        job.AppendLog("[广告/占位流] 分片来自广告域名或返回图片流，没有真实视频，已停止，不重试");
        PersistNow(job);
        Pump();
    }

    /// <summary>过滤掉临时目录路径，必要时按开始时间在保存目录里找最终产物。</summary>
    private List<string> ResolvedOutputFiles(DownloadJob job)
    {
        var existing = job.OutputFiles
            .Where(file => File.Exists(file)
                           && !file.Contains(Path.DirectorySeparatorChar + "Tasks" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !file.Contains(FileNameRules.PartialFileNameMarker, StringComparison.Ordinal))
            .ToList();
        if (existing.Count > 0) return existing;
        if (job.StartedAt is not { } startedAt) return new List<string>();

        var result = new List<string>();
        if (!Directory.Exists(job.SaveDirectory)) return result;
        foreach (var file in Directory.EnumerateFiles(job.SaveDirectory))
        {
            if (Path.GetFileName(file).Contains(FileNameRules.PartialFileNameMarker, StringComparison.Ordinal)) continue;
            DateTime modified;
            try
            {
                modified = File.GetLastWriteTime(file);
            }
            catch (Exception)
            {
                continue;
            }
            if (modified < startedAt.AddSeconds(-2)) continue;
            if (!string.IsNullOrEmpty(job.OutputBase)
                && !FileNameRules.NameMatchesBase(Path.GetFileNameWithoutExtension(file), job.OutputBase!))
            {
                continue;
            }
            result.Add(file);
        }
        return result;
    }

    /// <summary>进程非零退出后，确认保存目录里是否已经有本次任务生成的文件。</summary>
    private string? RecoveredOutputAfterFailure(DownloadJob job)
    {
        var baseName = job.OutputBase ?? FileNameRules.MakeOutputBase(job);
        var candidates = ResolvedOutputFiles(job);
        if (Directory.Exists(job.SaveDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(job.SaveDirectory))
            {
                var extension = Path.GetExtension(file).TrimStart('.');
                if (!FileNameRules.MediaOutputExtensions.Contains(extension)) continue;
                if (Path.GetFileName(file).Contains(FileNameRules.PartialFileNameMarker, StringComparison.Ordinal)) continue;
                DateTime modified;
                try
                {
                    modified = File.GetLastWriteTime(file);
                }
                catch (Exception)
                {
                    continue;
                }
                if (modified < job.CreatedAt) continue;
                if (FileNameRules.NameMatchesBase(Path.GetFileNameWithoutExtension(file), baseName))
                {
                    candidates.Add(file);
                }
            }
        }

        return candidates
            .Where(file => FileNameRules.MediaOutputExtensions.Contains(Path.GetExtension(file).TrimStart('.')))
            .Distinct()
            .OrderByDescending(file => File.GetLastWriteTime(file))
            .FirstOrDefault();
    }

    // MARK: - 清理

    private void DiscardResumeData(DownloadJob job)
    {
        RemoveYtDlpTemp(job);
        var hlsDirectory = job.HlsResume?.TempDirectory ?? _store.HlsTempDirectory(job.Id);
        try
        {
            if (Directory.Exists(hlsDirectory)) Directory.Delete(hlsDirectory, recursive: true);
        }
        catch (Exception)
        {
            // 文件被占用时忽略，下次清理。
        }
        job.HlsResume = null;
        job.IsSegmentedHls = false;
    }

    private void RemoveYtDlpTemp(DownloadJob job)
    {
        var directory = YtDlpTempDirectory(job.Id);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 文件被占用时忽略，下次清理。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略：下次重试会覆盖。
        }
    }

    private static long FileLength(string path)
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

    // MARK: - 持久化

    public void PersistNow(DownloadJob job)
    {
        _store.Save(Snapshot(job));
    }

    private static PersistedJob Snapshot(DownloadJob job) => new()
    {
        Id = job.Id,
        UrlString = job.UrlString,
        OriginalUrlString = job.OriginalUrlString,
        Title = job.Title,
        TitleIsResolved = job.TitleIsResolved,
        Quality = PersistedQuality.From(job.Quality),
        Container = job.Container.ToKey(),
        SaveDirectory = job.SaveDirectory,
        CookiesPath = job.CookiesPath,
        ProxyDescription = job.ProxyDescription,
        Engine = job.Engine == DownloadEngine.YtDlp ? "ytDlp" : "ffmpegDirect",
        Referer = job.Referer,
        UserAgent = job.UserAgent,
        PageURL = job.PageURL,
        BatchID = job.BatchID,
        State = job.State switch
        {
            JobState.Queued => "queued",
            JobState.Running => "running",
            JobState.Completed => "completed",
            JobState.Partial => "partial",
            JobState.Cancelled => "cancelled",
            _ => "failed"
        },
        Progress = job.Progress,
        DownloadedBytes = job.DownloadedBytes,
        TotalBytes = job.TotalBytes,
        OutputFiles = job.OutputFiles.ToList(),
        Failure = job.Failure == null ? null : PersistedFailure.From(job.Failure),
        CreatedAt = job.CreatedAt,
        IsSegmentedHLS = job.IsSegmentedHls,
        HlsResume = job.HlsResume,
        DirectOutputPath = job.DirectOutputPath,
        IsLiveHLS = job.IsLiveHls,
        AutoRetryCount = job.AutoRetryCount,
        OutputBase = job.OutputBase
    };

    /// <summary>应用启动时恢复队列；上次没跑完的任务保留续传信息，标记为可继续。</summary>
    private void Restore()
    {
        var tasks = _store.LoadAll();
        foreach (var task in tasks)
        {
            var job = new DownloadJob(
                task.UrlString,
                task.Title,
                task.Quality.ToChoice(),
                VideoContainerText.FromKey(task.Container),
                task.SaveDirectory,
                task.CookiesPath,
                task.ProxyDescription,
                task.Engine == "ffmpegDirect" ? DownloadEngine.FfmpegDirect : DownloadEngine.YtDlp,
                task.Referer,
                task.UserAgent,
                task.PageURL,
                task.BatchID,
                task.Id,
                task.CreatedAt);
            job.TitleIsResolved = task.TitleIsResolved;
            job.IsSegmentedHls = task.IsSegmentedHLS;
            job.HlsResume = task.HlsResume;
            job.DirectOutputPath = task.DirectOutputPath;
            job.IsLiveHls = task.IsLiveHLS;
            job.AutoRetryCount = task.AutoRetryCount;
            job.OutputBase = task.OutputBase;
            job.Progress = task.Progress;
            job.DownloadedBytes = task.DownloadedBytes;
            job.TotalBytes = task.TotalBytes;
            foreach (var file in task.OutputFiles) job.OutputFiles.Add(file);

            switch (task.State)
            {
                case "completed":
                    job.State = JobState.Completed;
                    break;
                case "partial":
                    job.State = JobState.Partial;
                    break;
                case "cancelled":
                    job.State = JobState.Cancelled;
                    break;
                default:
                    // running / queued 说明上次被强制退出，保留进度等用户点「继续下载」。
                    job.State = JobState.Failed;
                    job.Failure = new FailureInfo
                    {
                        Message = "上次退出应用时该任务未完成。",
                        Hint = "点「继续下载」从已完成的进度接着下，不会重新下载已完成的分片。",
                        Kind = FailureKind.Network
                    };
                    job.AppendLog("[恢复] 上次退出应用时未完成，可继续下载");
                    break;
            }
            if (task.Failure != null) job.Failure = task.Failure.ToFailure();
            Jobs.Add(job);
        }
    }

    // MARK: - 线程调度

    /// <summary>把回调切回 UI 线程；没有同步上下文时直接执行。</summary>
    private void OnUi(Action action)
    {
        if (_context == null || SynchronizationContext.Current == _context)
        {
            action();
            return;
        }
        _context.Post(_ => action(), null);
    }
}
