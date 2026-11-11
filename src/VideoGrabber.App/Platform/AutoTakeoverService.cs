using System.IO;
using System.Windows;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.Platform;

/// <summary>
/// 下载失败后的自动接管：隐藏 WebView2 打开页面 → 静音自动播放 / 滚动 → 嗅探 m3u8 / mp4 →
/// 选最优地址 → 带 Referer / UA / Cookie 重下。全程不弹窗、不抢焦点。
/// </summary>
public sealed class AutoTakeoverService
{
    private sealed class Session
    {
        public Session(Guid jobId) => JobID = jobId;
        public Guid JobID { get; }
        public HiddenBrowser? Browser { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private readonly DownloadManager _downloads;
    private readonly CookieStore _cookies;
    private readonly AppSettings _settings;
    private readonly string _userDataFolder;
    private readonly Dictionary<Guid, Session> _sessions = new();

    public AutoTakeoverService(
        DownloadManager downloads,
        CookieStore cookies,
        AppSettings settings,
        string userDataFolder)
    {
        _downloads = downloads;
        _cookies = cookies;
        _settings = settings;
        _userDataFolder = userDataFolder;
    }

    public bool IsRunning(Guid jobId) => _sessions.ContainsKey(jobId);

    /// <summary>启动接管；页面地址无效或同一任务已在接管时返回 false。</summary>
    public bool Start(DownloadJob job, string reason)
    {
        if (_sessions.ContainsKey(job.Id)) return false;
        if (DownloadManager.PageAddressFor(job) is not { } pageAddress) return false;

        var progress = job.Takeover ?? new TakeoverProgress();
        progress.Phase = TakeoverPhase.Preparing;
        progress.Add("创建后台浏览器（隐藏窗口，不抢焦点）", "running");
        job.Takeover = progress;
        job.DidAutoTakeover = true;

        var session = new Session(job.Id) { Cancellation = new CancellationTokenSource() };
        _sessions[job.Id] = session;
        _ = RunAsync(job, pageAddress, session);
        return true;
    }

    /// <summary>中止：停掉后台页面与等待。任务状态由 DownloadManager 统一收尾。</summary>
    public void Cancel(Guid jobId)
    {
        if (!_sessions.Remove(jobId, out var session)) return;
        session.Cancellation?.Cancel();
        _ = session.Browser?.DisposeAsync().AsTask();
    }

    public void CancelAll()
    {
        foreach (var key in _sessions.Keys.ToList()) Cancel(key);
    }

    private async Task RunAsync(DownloadJob job, string pageAddress, Session session)
    {
        var token = session.Cancellation!.Token;
        var progress = job.Takeover!;
        try
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => { });
            }

            var browser = new HiddenBrowser(_userDataFolder);
            session.Browser = browser;
            await browser.InitializeAsync();
            if (token.IsCancellationRequested) return;
            progress.FinishRunningSteps();
            progress.Add("后台浏览器已就绪", "done");

            // 2. 加载页面
            progress.Phase = TakeoverPhase.Loading;
            var loadStep = progress.Add("加载页面：" + pageAddress, "running");
            job.AppendLog("[接管] 加载页面：" + pageAddress);
            var loaded = await browser.LoadAsync(pageAddress, 25);
            if (token.IsCancellationRequested) return;
            await browser.RefreshUserAgentAsync();
            var probe = await browser.ProbeAsync();
            if (!loaded && string.IsNullOrEmpty(browser.CurrentUrl) && probe.IsEmpty)
            {
                Fail(job, progress, "加载页面", browser.LastError ?? "页面没有响应",
                    "检查网络或该地址是否可访问；也可以先用内置浏览器确认页面能正常打开。");
                return;
            }
            progress.Mark(loadStep, "done");
            progress.Add("页面已加载" + (string.IsNullOrEmpty(probe.Title) ? "" : "：" + probe.Title), "done");

            // 3. 只在「人机验证 / 登录」两处暂停
            if (probe.IsChallenge || probe.IsLogin)
            {
                var resolved = await PauseForUserAsync(job, browser, progress, pageAddress, probe.IsChallenge, token);
                if (token.IsCancellationRequested) return;
                if (!resolved)
                {
                    var name = probe.IsChallenge ? "人机验证" : "登录";
                    Fail(job, progress, name, "等待 3 分钟仍未通过",
                        "点「打开内置浏览器完成验证」完成验证 / 登录，然后重试该任务；本应用不会自动破解验证码。");
                    return;
                }
                probe = await browser.ProbeAsync();
            }

            // 4. 自动播放 + 嗅探
            progress.Phase = TakeoverPhase.Playing;
            var playStep = progress.Add("静音自动播放，必要时滚动到视频区域", "running");
            var deadline = DateTime.Now.AddSeconds(20);
            DateTime? firstCandidateAt = null;
            var challengeCheckedAt = DateTime.Now;
            while (DateTime.Now < deadline)
            {
                if (token.IsCancellationRequested) return;
                await browser.KickPlaybackAsync();
                await browser.ScrollPageAsync();
                var count = browser.Candidates().Count;
                progress.CandidateCount = count;

                if (count > 0)
                {
                    firstCandidateAt ??= DateTime.Now;
                    // 捕获到候选后再多等 4 秒，让主播放列表和更高码率变体出现。
                    if (DateTime.Now - firstCandidateAt >= TimeSpan.FromSeconds(4)) break;
                }
                else if (DateTime.Now - challengeCheckedAt >= TimeSpan.FromSeconds(4))
                {
                    challengeCheckedAt = DateTime.Now;
                    var current = await browser.ProbeAsync();
                    if (current.IsChallenge || current.IsLogin)
                    {
                        progress.Mark(playStep, "done");
                        var resolved = await PauseForUserAsync(
                            job, browser, progress, pageAddress, current.IsChallenge, token);
                        if (token.IsCancellationRequested) return;
                        if (!resolved)
                        {
                            var name = current.IsChallenge ? "人机验证" : "登录";
                            Fail(job, progress, name, "等待 3 分钟仍未通过",
                                "点「打开内置浏览器完成验证」完成验证 / 登录，然后重试该任务。");
                            return;
                        }
                        progress.Phase = TakeoverPhase.Playing;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1.5), token);
            }
            if (token.IsCancellationRequested) return;
            progress.Mark(playStep, "done");

            var candidates = browser.Candidates();
            progress.CandidateCount = candidates.Count;
            if (candidates.Count == 0)
            {
                var finalProbe = await browser.ProbeAsync();
                if (finalProbe.IsChallenge)
                {
                    Fail(job, progress, "人机验证", "页面仍停在 Cloudflare 验证页",
                        "打开内置浏览器完成验证后重试该任务。");
                    return;
                }
                if (finalProbe.IsLogin)
                {
                    Fail(job, progress, "登录", "页面要求登录后才能播放",
                        "打开内置浏览器登录后重试该任务。");
                    return;
                }
                Fail(job, progress, "收集媒体地址", "20 秒内没有捕获到 m3u8 / mp4",
                    "页面可能使用纯 blob 播放器、视频在独立播放器窗口，或需要登录。可以先用内置浏览器手动播放确认地址能出现。");
                return;
            }
            progress.Add($"已捕获 {candidates.Count} 个候选地址", "done");

            // 5. 挑最优
            progress.Phase = TakeoverPhase.Selecting;
            var selectStep = progress.Add("挑选最优地址（优先 m3u8 主播放列表、码率最高）", "running");
            var cookieURLs = candidates
                .Select(resource => string.IsNullOrEmpty(resource.RelatedSource) ? resource.UrlString : resource.RelatedSource!)
                .Append(pageAddress);
            var cookies = await browser.GetCookiesAsync(cookieURLs);
            if (token.IsCancellationRequested) return;

            var outcome = await TakeoverMediaSelector.SelectAsync(
                candidates,
                job.AudioOnly,
                pageAddress,
                browser.UserAgent,
                uri => _cookies.CookieHeaderFor(new[] { uri }),
                _settings,
                token);
            if (token.IsCancellationRequested) return;
            if (outcome.Selection == null)
            {
                if (outcome.OnlyAdCandidates)
                {
                    Fail(job, progress, "挑选地址", "当前页面只抓到了广告 / 占位流，没有真实视频",
                        "请在页面里播放正片后再抓一次：广告档会被自动跳过，抓到正片档（H.264 / HEVC / AV1）后会自动接管下载。");
                }
                else
                {
                    Fail(job, progress, "挑选地址", "候选地址都读不出可用的播放列表",
                        "可以在内置浏览器的资源嗅探里手动确认哪一条能播放后下载。");
                }
                return;
            }

            var selection = outcome.Selection;
            progress.Mark(selectStep, "done");
            progress.SelectedURL = selection.UrlString;
            progress.SelectedDetail = selection.Detail;
            progress.Add("已选择：" + selection.Detail, "done");

            // 6. 同步该站点 Cookie，供 yt-dlp / ffmpeg 复用
            string? cookiePath = null;
            if (Uri.TryCreate(pageAddress, UriKind.Absolute, out var pageUri))
            {
                try
                {
                    var written = _cookies.Write(cookies, pageUri);
                    cookiePath = written.FilePath;
                    job.CookiesPath = cookiePath;
                    progress.Add($"已同步 {written.Count} 条 Cookie（{Path.GetFileName(cookiePath)}）", "done");
                }
                catch (Exception ex)
                {
                    var existing = _cookies.CookieFileFor(pageUri);
                    cookiePath = existing;
                    if (existing != null)
                    {
                        progress.Add("沿用已保存的 Cookie：" + Path.GetFileName(existing));
                    }
                    else
                    {
                        progress.Add("Cookie 同步失败：" + ex.Message);
                    }
                }
            }

            // 7. 记忆站点策略
            if (_settings.RememberSniffSites
                && Uri.TryCreate(pageAddress, UriKind.Absolute, out var pageHost)
                && !string.IsNullOrEmpty(pageHost.Host))
            {
                _settings.RememberSniffHost(pageHost.Host);
                progress.Add("已记住站点策略：" + pageHost.Host + " 以后直接走嗅探，跳过 yt-dlp");
            }

            // 8. 交回下载队列重下
            if (token.IsCancellationRequested || job.State == JobState.Cancelled) return;
            progress.Phase = TakeoverPhase.Retrying;
            progress.Add("开始重新下载（已带页面 Referer / UA / Cookie）", "running");
            _downloads.BeginRetryWithTakeover(
                job,
                selection.UrlString,
                pageAddress,
                browser.UserAgent,
                pageAddress,
                cookiePath);
        }
        catch (OperationCanceledException)
        {
            progress.Phase = TakeoverPhase.Cancelled;
        }
        catch (Exception ex)
        {
            Fail(job, progress, "自动接管", ex.Message,
                "可以先用内置浏览器确认页面能正常播放，再手动把资源地址加入下载队列。");
        }
        finally
        {
            _sessions.Remove(job.Id);
            await DisposeBrowserAsync(session);
        }
    }

    private static async Task DisposeBrowserAsync(Session session)
    {
        if (session.Browser is { } browser)
        {
            await browser.DisposeAsync();
            session.Browser = null;
        }
    }

    /// <summary>暂停自动流程，等用户在内置浏览器里完成验证 / 登录；期间每 5 秒重载隐藏页面。</summary>
    private async Task<bool> PauseForUserAsync(
        DownloadJob job,
        HiddenBrowser browser,
        TakeoverProgress progress,
        string pageAddress,
        bool isChallenge,
        CancellationToken token)
    {
        progress.Phase = isChallenge ? TakeoverPhase.NeedsVerification : TakeoverPhase.NeedsLogin;
        progress.PausedNote = isChallenge
            ? "站点返回了 Cloudflare 人机验证。点「打开内置浏览器完成验证」，完成后这里会自动继续，不需要再次点击。"
            : "站点要求登录。点「打开内置浏览器」登录，完成后这里会自动继续。";
        var pauseStep = progress.Add(
            isChallenge ? "暂停：等待 Cloudflare 人机验证" : "暂停：等待登录", "running");
        job.AppendLog("[接管] " + (isChallenge ? "等待 Cloudflare 人机验证" : "等待登录"));

        var deadline = DateTime.Now.AddSeconds(180);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            if (token.IsCancellationRequested) return false;

            await browser.LoadAsync(pageAddress, 20);
            if (token.IsCancellationRequested) return false;
            var probe = await browser.ProbeAsync();
            if (browser.Candidates().Count > 0)
            {
                progress.Mark(pauseStep, "done");
                progress.PausedNote = null;
                progress.Add("已捕获媒体地址，自动继续", "done");
                return true;
            }
            if (!probe.IsChallenge && !probe.IsLogin)
            {
                progress.Mark(pauseStep, "done");
                progress.PausedNote = null;
                progress.Add("页面已恢复正常，自动继续", "done");
                return true;
            }
        }
        progress.Mark(pauseStep, "failed");
        return false;
    }

    private void Fail(DownloadJob job, TakeoverProgress progress, string step, string reason, string hint)
    {
        progress.FinishRunningSteps("failed");
        progress.Phase = TakeoverPhase.Failed;
        progress.Add($"卡在「{step}」：{reason}", "failed");
        job.AppendLog($"[自动接管] 失败：卡在「{step}」");
        job.State = JobState.Failed;
        job.FinishedAt = DateTime.Now;
        job.Progress = 0;
        job.SpeedText = "";
        job.EtaText = "";
        job.Failure = new FailureInfo
        {
            Message = $"自动接管失败：卡在「{step}」——{reason}",
            Hint = hint,
            Kind = FailureKind.Other
        };
        _downloads.NotifyTakeoverFinished();
    }
}
