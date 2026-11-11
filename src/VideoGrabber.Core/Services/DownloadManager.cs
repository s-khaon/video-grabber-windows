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
    private readonly SynchronizationContext? _context;

    private readonly Dictionary<Guid, IProcessHandle> _processes = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _retryWakeups = new();
    private readonly Dictionary<Guid, DateTime> _lastProgressPersist = new();

    public DownloadManager(
        AppSettings settings,
        ToolEnvironment tools,
        TaskMetadataStore store,
        IProcessRunner runner)
    {
        _settings = settings;
        _tools = tools;
        _store = store;
        _runner = runner;
        _probe = new YtDlpProbe(runner, tools, settings);
        _context = SynchronizationContext.Current;
        Restore();
    }

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    public AppSettings Settings => _settings;
    public ToolEnvironment Tools => _tools;

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

        // Cookies 复用由 P3 的 CookieStore 接入；此处先不附带。
        string? cookiePath = null;

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
                job.State = JobState.Cancelled;
                job.FinishedAt = DateTime.Now;
                job.SpeedText = "";
                job.EtaText = "";
                if (_processes.TryGetValue(job.Id, out var handle))
                {
                    handle.RequestStop();
                }
                if (!_settings.KeepIncompleteTempFiles) DiscardResumeData(job);
                break;
            case JobState.Queued:
                job.State = JobState.Cancelled;
                job.FinishedAt = DateTime.Now;
                if (!_settings.KeepIncompleteTempFiles) DiscardResumeData(job);
                break;
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
        OnUi(() => Finish(jobId, exitCode));
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

    private void Finish(Guid jobId, int exitCode)
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

            job.State = JobState.Completed;
            job.Progress = 1;
            job.AppendLog("==> 下载完成");
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
            job.State = JobState.Completed;
            job.FinishedAt = DateTime.Now;
            job.Progress = 1;
            job.Failure = null;
            job.AutoRetryCount = 0;
            job.RetryAfter = null;
            job.OutputFiles.Clear();
            job.OutputFiles.Add(recovered);
            var size = FileLength(recovered);
            job.DownloadedBytes = size;
            job.TotalBytes = Math.Max(job.TotalBytes, size);
            job.AppendLog("==> 下载器报告了分片错误，但产物可正常播放，按完成处理");
            RemoveYtDlpTemp(job);
            PersistNow(job);
            Pump();
            return;
        }

        var combined = string.Join("\n", job.Logs);

        // 只有日志明确说格式不可用时，才降级到最佳可用格式重试一次。
        if (job.Engine == DownloadEngine.YtDlp
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
        if (job.Engine == DownloadEngine.YtDlp && job.AutoRetryCount < 3 && ShouldAutoRetry(failure))
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
