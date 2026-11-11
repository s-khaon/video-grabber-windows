﻿using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

public enum HlsResultKind
{
    Completed,
    Partial,
    Failed,
    Cancelled,
    /// <summary>播放列表没有 #EXT-X-ENDLIST（直播流），需要改用 ffmpeg 录制。</summary>
    Live
}

public sealed record HlsRunResult(
    HlsResultKind Kind,
    string? Path = null,
    string? PartialPath = null,
    int Completed = 0,
    int Total = 0,
    FailureInfo? Failure = null);

/// <summary>
/// 按分片下载 HLS：最多 4 并发、每片最多 3 次重试、completed.json 记录进度，
/// 全部完成后生成本地播放列表交给 ffmpeg 无损封装。支持提前结束生成部分视频。
/// </summary>
public sealed class HlsDownloadRunner
{
    private const int MaxConcurrency = 4;
    private const int MaxAttempts = 3;
    private const string PartialFileName = "local-partial.m3u8";

    private readonly DownloadJob _job;
    private readonly AppSettings _settings;
    private readonly ToolEnvironment _tools;
    private readonly TaskMetadataStore _store;
    private readonly IProcessRunner _runner;
    private readonly Func<IReadOnlyList<Uri>, string?>? _cookieProvider;
    private readonly Action<Action> _dispatch;
    private readonly HttpClient _client;

    private HlsResumeState? _state;
    private volatile bool _finishEarly;
    private string? _firstFailure;

    public HlsDownloadRunner(
        DownloadJob job,
        AppSettings settings,
        ToolEnvironment tools,
        TaskMetadataStore store,
        IProcessRunner runner,
        Func<IReadOnlyList<Uri>, string?>? cookieProvider,
        Action<Action> dispatch)
    {
        _job = job;
        _settings = settings;
        _tools = tools;
        _store = store;
        _runner = runner;
        _cookieProvider = cookieProvider;
        _dispatch = dispatch;
        _client = HlsHttp.Create(settings);
    }

    /// <summary>用户提前结束：不再派发新分片，把手上的分片封装成部分视频。</summary>
    public void RequestFinishEarly() => _finishEarly = true;

    private HlsRequestContext RequestContext
    {
        get
        {
            var urls = new List<Uri>();
            AddUrl(urls, _job.PageURL);
            AddUrl(urls, _job.UrlString);
            AddUrl(urls, _job.HlsResume?.PlaylistUrl);
            string? cookie = null;
            if (urls.Count > 0 && _cookieProvider != null)
            {
                try
                {
                    cookie = _cookieProvider(urls);
                }
                catch (Exception)
                {
                    cookie = null;
                }
            }
            return new HlsRequestContext(_job.UserAgent, _job.Referer, cookie);
        }
    }

    private static void AddUrl(List<Uri> urls, string? value)
    {
        if (Uri.TryCreate(value ?? "", UriKind.Absolute, out var uri)) urls.Add(uri);
    }

    // MARK: - 主流程

    public async Task<HlsRunResult> RunAsync(CancellationToken cancellationToken)
    {
        Log("[分片下载] 开始解析播放列表");

        HlsResumeState state;
        try
        {
            state = await PrepareResumeStateAsync(cancellationToken);
        }
        catch (HlsFetchException ex)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: ex.ToFailure());
        }
        if (cancellationToken.IsCancellationRequested) return new HlsRunResult(HlsResultKind.Cancelled);
        if (_job.IsLiveHls == true) return new HlsRunResult(HlsResultKind.Live);

        _state = state;
        _dispatch(() =>
        {
            _job.HlsResume = state;
            _job.IsSegmentedHls = true;
            _job.DirectOutputPath = state.TargetPath;
        });

        var segmentsDirectory = Path.Combine(state.TempDirectory, "segments");
        try
        {
            Directory.CreateDirectory(segmentsDirectory);
        }
        catch (Exception ex)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = $"无法创建临时目录：{ex.Message}",
                Hint = "检查磁盘空间或应用数据目录权限后重试。",
                Kind = FailureKind.PermissionDenied
            });
        }

        // 磁盘上的完成记录与实际文件取交集，避免重复请求，也避免文件被删后误跳过。
        var completed = _store.LoadCompleted(_job.Id);
        foreach (var index in completed.ToList())
        {
            if (index < 0 || index >= state.Segments.Count)
            {
                completed.Remove(index);
                continue;
            }
            var file = SegmentFile(segmentsDirectory, index, state.Segments[index]);
            if (!File.Exists(file) || new FileInfo(file).Length <= 0) completed.Remove(index);
        }

        var total = state.Segments.Count;
        var completedBytes = completed.Sum(index => FileLength(SegmentFile(segmentsDirectory, index, state.Segments[index])));

        if (completed.Count > 0)
        {
            Log($"[分片下载] 已存在 {completed.Count}/{total} 个分片，从断点继续");
        }
        UpdateProgress(state, completed.Count, completedBytes);
        _store.SaveCompleted(_job.Id, completed);

        // init 段（fMP4 的 moov）先单独取，缺它无法封装。
        if (state.InitSegment is { } init)
        {
            var initFile = Path.Combine(segmentsDirectory, InitFileName(init.Url));
            if (!File.Exists(initFile) || new FileInfo(initFile).Length <= 0)
            {
                var outcome = await DownloadSegmentAsync(init, initFile, RequestContext, cancellationToken);
                if (outcome.Cancelled) return new HlsRunResult(HlsResultKind.Cancelled);
                if (outcome.Error != null)
                {
                    return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
                    {
                        Message = $"初始化分片下载失败：{outcome.Error}",
                        Hint = "点「继续下载」只会重试缺失部分；持续失败时检查该地址是否已过期。",
                        Kind = FailureKind.Network
                    });
                }
                completedBytes += outcome.Bytes;
            }
        }

        var result = await DownloadMissingAsync(state, segmentsDirectory, completed, completedBytes, cancellationToken);
        _store.SaveCompleted(_job.Id, completed);
        _dispatch(() => _job.HlsResume = state);

        if (result == BatchResult.Cancelled) return new HlsRunResult(HlsResultKind.Cancelled);

        if (result == BatchResult.StoppedEarly)
        {
            return await MuxAsync(state, segmentsDirectory, completed.OrderBy(index => index).ToList(), partial: true, cancellationToken);
        }
        if (_firstFailure != null)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = _firstFailure,
                Hint = $"已完成的 {completed.Count}/{total} 个分片会保留，点「继续下载」只补剩下的分片。",
                Kind = FailureKind.Network
            });
        }

        // 所有分片都下完后才点提前结束：按完整视频收尾即可。
        if (_finishEarly && completed.Count < total)
        {
            return await MuxAsync(state, segmentsDirectory, completed.OrderBy(index => index).ToList(), partial: true, cancellationToken);
        }
        return await MuxAsync(state, segmentsDirectory, completed.OrderBy(index => index).ToList(), partial: false, cancellationToken);
    }

    // MARK: - 续传元数据

    private async Task<HlsResumeState> PrepareResumeStateAsync(CancellationToken cancellationToken)
    {
        if (_job.HlsResume is { Segments.Count: > 0 } existing)
        {
            if (!Directory.Exists(existing.TempDirectory))
            {
                try
                {
                    Directory.CreateDirectory(existing.TempDirectory);
                }
                catch (Exception)
                {
                    // 后面创建 segments 目录时会再报一次。
                }
            }
            existing.TargetPath = _job.DirectOutputPath ?? existing.TargetPath;
            return existing;
        }

        var context = RequestContext;
        var fetched = await FetchTextAsync(_job.UrlString, context, cancellationToken);
        var mediaText = fetched.Text;
        var mediaUrl = fetched.FinalUrl;

        var variants = HlsPlaylistParser.ParseMaster(fetched.Text, fetched.FinalUrl);
        if (variants != null)
        {
            var playable = variants.Where(variant => !variant.IsPlaceholder).ToList();
            if (playable.Count == 0) throw HlsFetchException.Placeholder();
            var chosen = ChooseVariant(playable, _job.Quality.Height)
                         ?? throw HlsFetchException.EmptyMaster();
            var media = await FetchTextAsync(chosen.UrlString, context, cancellationToken);
            mediaText = media.Text;
            mediaUrl = media.FinalUrl;
        }

        var spec = HlsPlaylistParser.ParseMedia(mediaText, mediaUrl);
        if (spec.Segments.Count == 0) throw HlsFetchException.NotPlaylist();

        var adSegments = spec.Segments.Count(segment => AdStreamDetector.IsAdUrl(segment.Url));
        if (adSegments > 0 && adSegments * 2 >= spec.Segments.Count)
        {
            Log($"[广告/占位流] {adSegments}/{spec.Segments.Count} 个分片来自广告域名");
            throw HlsFetchException.Placeholder();
        }
        if (spec.Segments.Count > 0
            && await AdStreamDetectorProbe.ProbeImageSegmentAsync(spec.Segments[0].Url, context, _client, cancellationToken))
        {
            Log("[广告/占位流] 首个分片返回的是图片，不是视频数据");
            throw HlsFetchException.Placeholder();
        }

        // 没有 #EXT-X-ENDLIST 就是直播：分片通道拿不到完整内容，改用 ffmpeg 录制。
        _job.IsLiveHls = !mediaText.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
        if (_job.IsLiveHls == true)
        {
            Log("[直播] 播放列表没有 #EXT-X-ENDLIST，改用 ffmpeg 录制直播流");
            return new HlsResumeState
            {
                PlaylistUrl = mediaUrl.AbsoluteUri,
                PlaylistText = mediaText,
                Segments = spec.Segments,
                InitSegment = spec.InitSegment,
                TempDirectory = _store.HlsTempDirectory(_job.Id),
                TargetPath = _job.DirectOutputPath ?? "",
                TotalDuration = spec.TotalDuration
            };
        }

        var tempDirectory = _store.HlsTempDirectory(_job.Id);
        var target = _job.DirectOutputPath ?? FileNameRules.DirectOutputPath(_job);
        _job.DirectOutputPath = target;
        Log($"[分片下载] 共 {spec.Segments.Count} 个分片，目标：{target}");

        return new HlsResumeState
        {
            PlaylistUrl = mediaUrl.AbsoluteUri,
            PlaylistText = mediaText,
            Segments = spec.Segments,
            InitSegment = spec.InitSegment,
            TempDirectory = tempDirectory,
            TargetPath = target,
            TotalDuration = spec.TotalDuration
        };
    }

    private static HlsVariant? ChooseVariant(List<HlsVariant> variants, int? height)
    {
        if (variants.Count == 0) return null;
        if (height is > 0)
        {
            var eligible = variants.Where(variant => (variant.Height ?? 0) > 0 && (variant.Height ?? 0) <= height).ToList();
            if (eligible.Count > 0) return eligible.OrderByDescending(variant => variant.EffectiveBandwidth).First();
        }
        return variants.OrderByDescending(variant => variant.EffectiveBandwidth).First();
    }

    // MARK: - 并发下载

    private enum BatchResult
    {
        Finished,
        StoppedEarly,
        Cancelled
    }

    private async Task<BatchResult> DownloadMissingAsync(
        HlsResumeState state,
        string segmentsDirectory,
        HashSet<int> completed,
        long completedBytes,
        CancellationToken cancellationToken)
    {
        var missing = new ConcurrentQueue<int>(
            Enumerable.Range(0, state.Segments.Count).Where(index => !completed.Contains(index)));
        if (missing.IsEmpty) return BatchResult.Finished;

        var bytesLock = new object();
        var stoppedEarly = false;
        var failures = new ConcurrentQueue<string>();

        async Task WorkerAsync()
        {
            while (missing.TryDequeue(out var index))
            {
                if (_finishEarly || cancellationToken.IsCancellationRequested) return;
                var record = state.Segments[index];
                var file = SegmentFile(segmentsDirectory, index, record);
                var outcome = await DownloadSegmentAsync(record, file, RequestContext, cancellationToken);
                if (outcome.Cancelled) return;
                if (outcome.Error != null)
                {
                    failures.Enqueue($"分片 {index + 1} 下载失败：{outcome.Error}");
                    continue;
                }

                int count;
                long bytesSnapshot;
                int[] snapshot;
                lock (bytesLock)
                {
                    completed.Add(index);
                    completedBytes += outcome.Bytes;
                    count = completed.Count;
                    bytesSnapshot = completedBytes;
                    snapshot = completed.ToArray();
                }
                _store.SaveCompleted(_job.Id, snapshot);
                UpdateProgress(state, count, bytesSnapshot);
                if (count == state.Segments.Count)
                {
                    Log("[分片下载] 全部分片已下载完成，开始封装");
                }
                // 提前结束时不再派发新分片，当前这批完成后立刻封装。
                if (_finishEarly) return;
            }
        }

        var workers = Enumerable.Range(0, Math.Min(MaxConcurrency, missing.Count))
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
        await Task.WhenAll(workers);

        if (_finishEarly) stoppedEarly = true;
        if (failures.TryDequeue(out var first)) _firstFailure = first;
        if (cancellationToken.IsCancellationRequested) return BatchResult.Cancelled;
        if (stoppedEarly) return BatchResult.StoppedEarly;
        return BatchResult.Finished;
    }

    private readonly record struct SegmentOutcome(long Bytes, string? Error, bool Cancelled)
    {
        public static SegmentOutcome CancelledResult => new(0, null, true);
    }

    private async Task<SegmentOutcome> DownloadSegmentAsync(
        HlsSegmentRecord segment,
        string file,
        HlsRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(segment.Url, UriKind.Absolute, out var uri))
        {
            return new SegmentOutcome(0, "分片地址无效", false);
        }

        var lastError = "未知错误";
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return SegmentOutcome.CancelledResult;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                context.Apply(request);

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "分片需要登录或已过期（401/403）"
                        : $"HTTP {(int)response.StatusCode}";
                    if (!ShouldRetry(response.StatusCode))
                    {
                        return new SegmentOutcome(0, lastError, false);
                    }
                }
                else
                {
                    var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
                    if (data.Length == 0)
                    {
                        lastError = "服务器返回了空分片";
                    }
                    else
                    {
                        // 有的站点在 TS 分片前塞一段图片头伪装成图片：下载时直接剥掉。
                        var cleaned = AdStreamDetector.StrippingFakeImageHeader(data);
                        var temp = file + ".tmp";
                        await File.WriteAllBytesAsync(temp, cleaned, cts.Token);
                        File.Move(temp, file, overwrite: true);
                        return new SegmentOutcome(cleaned.Length, null, false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return SegmentOutcome.CancelledResult;
            }
            catch (OperationCanceledException)
            {
                lastError = "请求超时";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < MaxAttempts - 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return SegmentOutcome.CancelledResult;
                }
            }
        }
        return new SegmentOutcome(0, lastError, false);
    }

    private static bool ShouldRetry(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    // MARK: - 封装

    private async Task<HlsRunResult> MuxAsync(
        HlsResumeState state,
        string segmentsDirectory,
        List<int> indices,
        bool partial,
        CancellationToken cancellationToken)
    {
        _dispatch(() =>
        {
            _job.SpeedText = "";
            _job.EtaText = "";
        });

        if (partial && indices.Count == 0)
        {
            Log("[提前结束] 还没有下载到任何完整分片，无法生成部分视频；已下载的数据仍保留");
            return new HlsRunResult(HlsResultKind.Cancelled);
        }
        Log(partial
            ? $"[提前结束] 用已下载的 {indices.Count}/{state.Segments.Count} 个分片封装部分视频"
            : "[分片下载] 正在用 ffmpeg 拼接封装…");

        var ffmpeg = _tools.Ffmpeg;
        if (ffmpeg == null)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = "未找到 ffmpeg，无法拼接分片。",
                Hint = "执行 winget install Gyan.FFmpeg，或在「设置 → 工具」中指定 ffmpeg 路径后点「继续下载」。",
                Kind = FailureKind.FfmpegMissing
            });
        }

        // 续传任务里的分片可能还是旧版本写下的，封装前统一剥掉伪装图片头。
        StripFakeImageHeaders(state, segmentsDirectory);

        if (partial)
        {
            // 丢弃旧的部分播放列表，避免 ffmpeg 读到过期内容。
            TryDelete(Path.Combine(state.TempDirectory, PartialFileName));
        }

        // 鉴权站点上的 AES 密钥先由应用取回，播放列表里改指本地文件。
        var keyFiles = await DownloadKeysAsync(state, state.TempDirectory, cancellationToken);
        var playlistText = LocalPlaylist(state, keyFiles, partial ? indices : null, partial);
        var playlistFile = Path.Combine(state.TempDirectory, partial ? PartialFileName : "local.m3u8");
        try
        {
            Directory.CreateDirectory(state.TempDirectory);
            await File.WriteAllTextAsync(playlistFile, playlistText, new System.Text.UTF8Encoding(false), cancellationToken);
        }
        catch (Exception ex)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = $"无法生成本地播放列表：{ex.Message}",
                Hint = "检查临时目录权限后点「继续下载」。",
                Kind = FailureKind.PermissionDenied
            });
        }

        var target = partial ? FileNameRules.PartialOutputPath(state.TargetPath) : state.TargetPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
        }
        catch (Exception)
        {
            // 交给 ffmpeg 报错。
        }

        var arguments = new List<string>
        {
            "-y", "-nostdin", "-hide_banner", "-loglevel", "error",
            // 本地分片 + 远程 AES 密钥：HLS 默认白名单只放行 file/crypto。
            "-protocol_whitelist", "file,http,https,tcp,tls,crypto,httpproxy",
            "-allowed_extensions", "ALL"
        };
        arguments.AddRange(_tools.HlsSegmentExtensionArguments);

        var needsRemoteHeaders = playlistText.Contains("http://", StringComparison.Ordinal)
                                 || playlistText.Contains("https://", StringComparison.Ordinal);
        if (needsRemoteHeaders)
        {
            var headers = new HlsRequestContext(_job.UserAgent, _job.Referer, RequestContext.Cookie).HeaderValue();
            if (!string.IsNullOrEmpty(headers)) arguments.AddRange(new[] { "-headers", headers! });
        }

        arguments.AddRange(new[] { "-i", playlistFile });
        if (_job.AudioOnly)
        {
            arguments.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "0" });
        }
        else
        {
            arguments.AddRange(new[] { "-c", "copy" });
            if (target.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || target.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(new[] { "-movflags", "+faststart" });
            }
        }
        arguments.Add(target);

        ProcessOutput output;
        try
        {
            output = await _runner.RunAsync(ffmpeg, arguments, ProxyConfig.Environment(_settings), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HlsRunResult(HlsResultKind.Cancelled);
        }
        catch (Exception ex)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = $"无法启动 ffmpeg：{ex.Message}",
                Hint = "检查 ffmpeg 路径是否有效后点「继续下载」。",
                Kind = FailureKind.FfmpegMissing
            });
        }

        if (output.ExitCode != 0)
        {
            var detail = OutputParser.LastErrorLine(output.Text) ?? $"ffmpeg 退出码 {output.ExitCode}";
            var lower = output.Text.ToLowerInvariant();
            if (lower.Contains("does not contain any stream"))
            {
                return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
                {
                    Message = "分片里没有真实视频流（只有广告 / 占位图片）。",
                    Hint = "这条播放列表是广告 / 占位流，不要重试；回到页面里播放正片后再抓一次。",
                    Kind = FailureKind.PlaceholderStream
                });
            }
            if (lower.Contains("codec not currently supported in container")
                || lower.Contains("could not find codec parameters")
                || lower.Contains("unsupported codec")
                || lower.Contains("unknown decoder")
                || lower.Contains("decoder not found"))
            {
                return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
                {
                    Message = $"分片已下载完，但编码与目标容器不兼容：{detail}",
                    Hint = "把容器改为 MKV（或「自动」）后点「继续下载」；分片仍保留在临时目录，不会重新下载。",
                    Kind = FailureKind.UnsupportedMedia
                });
            }
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = $"分片已下载完，但封装失败：{detail}",
                Hint = "分片仍保留在临时目录，点「继续下载」可只重新封装；持续失败可换 MP4 / MKV 容器。",
                Kind = FailureKind.Other
            });
        }

        if (!File.Exists(target) || new FileInfo(target).Length <= 0)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: new FailureInfo
            {
                Message = "封装完成但没有找到输出文件。",
                Hint = "确认保存目录可写后点「继续下载」。",
                Kind = FailureKind.PermissionDenied
            });
        }

        // 封装完也要校验：TS-in-MP4、索引损坏、占位图都会在这里被发现并修复。
        var verifier = new MediaVerifier(_runner, _settings, _tools, message => Log(message));
        var verified = await verifier.EnsurePlayableAsync(target, _job.AudioOnly, 0, cancellationToken);
        if (!verified.Ok)
        {
            return new HlsRunResult(HlsResultKind.Failed, Failure: verified.Failure);
        }

        if (partial)
        {
            Log($"[提前结束] 部分视频已保存：{verified.Path}");
            return new HlsRunResult(HlsResultKind.Partial, PartialPath: verified.Path, Completed: indices.Count, Total: state.Segments.Count);
        }
        if (verified.Path != target)
        {
            state.TargetPath = verified.Path;
            _dispatch(() => _job.DirectOutputPath = verified.Path);
        }
        Log("==> 下载完成");
        return new HlsRunResult(HlsResultKind.Completed, Path: verified.Path, Completed: indices.Count, Total: state.Segments.Count);
    }

    /// <summary>清理已下载分片里的伪装图片头。</summary>
    private void StripFakeImageHeaders(HlsResumeState state, string segmentsDirectory)
    {
        var files = new List<string>();
        if (state.InitSegment is { } init) files.Add(Path.Combine(segmentsDirectory, InitFileName(init.Url)));
        for (var index = 0; index < state.Segments.Count; index++)
        {
            files.Add(SegmentFile(segmentsDirectory, index, state.Segments[index]));
        }

        var cleaned = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            try
            {
                var prefix = new byte[64 * 1024];
                int read;
                using (var stream = File.OpenRead(file))
                {
                    read = stream.Read(prefix, 0, prefix.Length);
                }
                var span = prefix.AsSpan(0, read);
                if (!AdStreamDetector.IsImageData(span)) continue;
                var offset = AdStreamDetector.TransportStreamOffset(span);
                if (offset is not > 0) continue;
                var data = File.ReadAllBytes(file);
                if (offset >= data.Length) continue;
                File.WriteAllBytes(file, data[offset.Value..]);
                cleaned++;
            }
            catch (Exception)
            {
                // 单个文件处理失败不影响其余分片。
            }
        }
        if (cleaned > 0) Log($"[分片下载] 已剥掉 {cleaned} 个分片的伪装图片头");
    }

    /// <summary>用本地分片路径生成播放列表；密钥地址尽量换成本地文件。</summary>
    private string LocalPlaylist(HlsResumeState state, Dictionary<string, string> keyFiles, List<int>? indices, bool partial)
    {
        var selected = indices ?? Enumerable.Range(0, state.Segments.Count).ToList();
        var lines = new List<string> { "#EXTM3U", "#EXT-X-VERSION:3" };
        var target = selected.Count > 0
            ? selected.Max(index => state.Segments[index].Duration)
            : state.TotalDuration;
        if (target <= 0) target = state.TotalDuration;
        if (target > 0) lines.Add($"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(target)}");
        lines.Add("#EXT-X-MEDIA-SEQUENCE:0");
        lines.Add("#EXT-X-PLAYLIST-TYPE:VOD");

        string? currentKey = null;
        if (state.InitSegment is { } init)
        {
            if (init.KeyLine != null && init.KeyLine != currentKey)
            {
                lines.Add(LocalizedKeyLine(init.KeyLine, keyFiles));
                currentKey = init.KeyLine;
            }
            lines.Add($"#EXT-X-MAP:URI=\"{InitFileName(init.Url)}\"");
        }

        int? previous = null;
        foreach (var index in selected)
        {
            if (index < 0 || index >= state.Segments.Count) continue;
            var segment = state.Segments[index];
            if (segment.KeyLine != null && segment.KeyLine != currentKey)
            {
                lines.Add(LocalizedKeyLine(segment.KeyLine, keyFiles));
                currentKey = segment.KeyLine;
            }
            // 提前结束时已下载分片可能不连续，插入 discontinuity 让 ffmpeg 正确处理时间戳跳变。
            if (segment.Discontinuity || (previous is { } prev && index != prev + 1))
            {
                lines.Add("#EXT-X-DISCONTINUITY");
            }
            lines.Add($"#EXTINF:{segment.Duration.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},");
            lines.Add(SegmentFileName(index, segment));
            previous = index;
        }
        lines.Add("#EXT-X-ENDLIST");
        return string.Join("\n", lines) + "\n";
    }

    private static string LocalizedKeyLine(string line, Dictionary<string, string> keyFiles)
    {
        var uri = HlsPlaylistParser.UriValue(line);
        if (uri == null || !keyFiles.TryGetValue(uri, out var local)) return line;
        return line.Replace($"URI=\"{uri}\"", $"URI=\"{local}\"");
    }

    /// <summary>
    /// 用页面请求头预先抓取 AES 密钥：ffmpeg 的 HLS demuxer 打开密钥时不会继承
    /// `-headers`，鉴权站点会直接 403，所以密钥必须由应用自己取回本地。
    /// </summary>
    private async Task<Dictionary<string, string>> DownloadKeysAsync(
        HlsResumeState state,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        var records = new List<HlsSegmentRecord>();
        if (state.InitSegment is { } init) records.Add(init);
        records.AddRange(state.Segments);

        foreach (var record in records)
        {
            var uri = HlsPlaylistParser.UriValue(record.KeyLine ?? "");
            if (uri == null || urls.Contains(uri)) continue;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) continue;
            if (parsed.Scheme is not ("http" or "https")) continue;
            urls.Add(uri);
        }
        if (urls.Count == 0) return new Dictionary<string, string>();

        var keysDirectory = Path.Combine(tempDirectory, "keys");
        try
        {
            Directory.CreateDirectory(keysDirectory);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }

        var mapping = new Dictionary<string, string>();
        var context = RequestContext;
        for (var index = 0; index < urls.Count; index++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, urls[index]);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                context.Apply(request);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode) continue;
                var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
                if (data.Length == 0) continue;
                var file = Path.Combine(keysDirectory, $"key{index}.bin");
                await File.WriteAllBytesAsync(file, data, cts.Token);
                mapping[urls[index]] = file;
            }
            catch (Exception)
            {
                Log($"[分片下载] 密钥下载失败，保留原地址交给 ffmpeg：{urls[index]}");
            }
        }
        if (mapping.Count > 0) Log($"[分片下载] 已用页面请求头下载 {mapping.Count} 个 AES 密钥");
        return mapping;
    }

    // MARK: - 工具

    private async Task<(string Text, Uri FinalUrl)> FetchTextAsync(string address, HlsRequestContext context, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw HlsFetchException.InvalidUrl();
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            context.Apply(request);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw HlsFetchException.Http((int)response.StatusCode);
            }
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (data.Length > 4 * 1024 * 1024) data = data[..(4 * 1024 * 1024)];
            var text = System.Text.Encoding.UTF8.GetString(data);
            return (text, response.RequestMessage?.RequestUri ?? uri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw HlsFetchException.TimedOut();
        }
        catch (HlsFetchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw HlsFetchException.Network(ex.Message);
        }
    }

    private void UpdateProgress(HlsResumeState state, int completedCount, long bytes)
    {
        var total = Math.Max(1, state.Segments.Count);
        _dispatch(() =>
        {
            _job.Progress = Math.Min(1, (double)completedCount / total);
            _job.DownloadedBytes = bytes;
            _job.SpeedText = $"{completedCount}/{state.Segments.Count} 分片";
            _lastPersist ??= DateTime.Now;
            if ((DateTime.Now - _lastPersist.Value).TotalSeconds >= 2)
            {
                _lastPersist = DateTime.Now;
                Persist?.Invoke();
            }
        });
    }

    private DateTime? _lastPersist;

    /// <summary>由 DownloadManager 注入的「落盘一次任务快照」回调。</summary>
    public Action? Persist { get; set; }

    /// <summary>日志统一回到 UI 线程再写入，避免后台线程改动 ObservableCollection。</summary>
    private void Log(string message) => _dispatch(() => _job.AppendLog(message));

    private void TryDelete(string path)
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

    private static string SegmentFileName(int index, HlsSegmentRecord segment) =>
        $"seg_{index:D5}.{FileExtension(segment.Url)}";

    private static string SegmentFile(string directory, int index, HlsSegmentRecord segment) =>
        Path.Combine(directory, SegmentFileName(index, segment));

    private static string InitFileName(string urlString) => $"init.{FileExtension(urlString)}";

    private static string FileExtension(string urlString)
    {
        var path = Uri.TryCreate(urlString, UriKind.Absolute, out var uri) ? uri.AbsolutePath : urlString;
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string[] allowed = { "ts", "m4s", "mp4", "m4v", "aac", "mp3", "m4a", "vtt", "webvtt", "key", "bin" };
        return allowed.Contains(extension) ? extension : "ts";
    }
}

/// <summary>播放列表拉取失败的分类，用来生成中文失败原因。</summary>
public sealed class HlsFetchException : Exception
{
    private HlsFetchException(string message, string hint, FailureKind kind) : base(message)
    {
        Hint = hint;
        Kind = kind;
    }

    public string Hint { get; }
    public FailureKind Kind { get; }

    public FailureInfo ToFailure() => new() { Message = Message, Hint = Hint, Kind = Kind };

    public static HlsFetchException InvalidUrl() =>
        new("地址不是有效的 http(s) 播放列表。", "确认链接以 http:// 或 https:// 开头，且指向 .m3u8。", FailureKind.ExtractorUnsupported);

    public static HlsFetchException NotPlaylist() =>
        new("地址返回的内容不是 HLS 播放列表。", "确认这是 .m3u8 地址；有时需要带 Referer / Cookie，可在内置浏览器里重新嗅探。", FailureKind.ExtractorUnsupported);

    public static HlsFetchException EmptyMaster() =>
        new("主播放列表里没有可用的档位。", "回到页面里播放正片后再嗅探一次。", FailureKind.ExtractorUnsupported);

    public static HlsFetchException Placeholder() =>
        new("这条播放列表是广告 / 占位流，不是视频。",
            "回到页面里播放正片，等资源面板出现非广告地址后再下载；如果页面上只抓到广告档，请播放正片后重新抓一次。",
            FailureKind.PlaceholderStream);

    public static HlsFetchException TimedOut() =>
        new("请求播放列表超时。", "检查网络或代理设置后点「继续下载」。", FailureKind.Network);

    public static HlsFetchException Network(string message) =>
        new($"请求播放列表失败：{message}", "检查网络或代理设置后点「继续下载」。", FailureKind.Network);

    public static HlsFetchException Http(int status) =>
        status is 401 or 403
            ? new($"播放列表需要登录或已过期（HTTP {status}）。", "用内置浏览器完成登录 / 验证后重新抓取地址。", FailureKind.Forbidden)
            : new($"请求播放列表失败（HTTP {status}）。", "检查地址是否已过期，或稍后重试。", FailureKind.Network);
}
