using System.Net;
using System.Net.Http;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>主播放列表里的一档清晰度（含汇总信息）。</summary>
public sealed class HlsVariantInfo
{
    public string UrlString { get; init; } = "";
    public long Bandwidth { get; init; }
    public long AverageBandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string? Codecs { get; init; }
    public double Duration { get; set; }
    public int SegmentCount { get; set; }
    public bool IsHighest { get; set; }
    public bool IsRecommended { get; set; }

    public bool IsPlaceholder =>
        AdStreamDetector.IsAdUrl(UrlString) || AdStreamDetector.IsImageCodec(Codecs);

    public bool IsRealVideo => !IsPlaceholder && AdStreamDetector.IsVideoCodec(Codecs);

    public long EffectiveBandwidth => AverageBandwidth > 0 ? AverageBandwidth : Bandwidth;

    public string? ResolutionText => Width is > 0 && Height is > 0 ? $"{Width}×{Height}" : Height is > 0 ? $"{Height}p" : null;

    public string? FrameRateText => FrameRate is > 0
        ? (Math.Abs(Math.Round(FrameRate.Value) - FrameRate.Value) < 0.005
            ? FrameRate.Value.ToString("0")
            : FrameRate.Value.ToString("0.##"))
        : null;

    public string? CodecText
    {
        get
        {
            if (string.IsNullOrEmpty(Codecs)) return null;
            var lower = Codecs.ToLowerInvariant();
            if (lower.Contains("avc1") || lower.Contains("avc3")) return "avc1";
            if (lower.Contains("hev1") || lower.Contains("hvc1")) return "hevc";
            if (lower.Contains("av01")) return "av01";
            if (lower.Contains("vp09") || lower.Contains("vp9")) return "vp09";
            return Codecs.Split(',')[0].Trimmed();
        }
    }

    public string TechnicalText
    {
        get
        {
            var parts = new List<string>();
            if (AverageBandwidth > 0 && Math.Abs(AverageBandwidth - Bandwidth) > Math.Max(1000, Bandwidth / 50))
            {
                parts.Add("平均 " + BitrateText(AverageBandwidth));
                parts.Add("峰值 " + BitrateText(Bandwidth));
            }
            else if (EffectiveBandwidth > 0)
            {
                parts.Add("码率 " + BitrateText(EffectiveBandwidth));
            }
            if (FrameRateText is { } fps) parts.Add($"{fps} fps");
            if (CodecText is { } codec) parts.Add(codec);
            return string.Join(" · ", parts);
        }
    }

    public string? DurationText => Duration > 0 ? ClockText(Duration) : null;

    /// <summary>码率 × 时长 / 8；只是估算，实际体积受分片与音轨影响。</summary>
    public long EstimatedBytes => Duration > 0 && EffectiveBandwidth > 0
        ? (long)Math.Round(EffectiveBandwidth * Duration / 8)
        : 0;

    public string? EstimatedSizeText => EstimatedBytes > 0 ? FileSize.Format(EstimatedBytes) : null;

    public static string BitrateText(long bitsPerSecond)
    {
        var mbps = bitsPerSecond / 1_000_000.0;
        return mbps >= 1 ? mbps.ToString("0.0") + " Mbps" : Math.Round(bitsPerSecond / 1000.0) + " kbps";
    }

    public static string ClockText(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "—";
        var total = (int)Math.Round(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes:D2}:{secs:D2}";
    }
}

/// <summary>单个 HLS 播放列表的解析结果。</summary>
public sealed class HlsPlaylistInfo
{
    public enum InfoKind
    {
        Master,
        Media,
        Unknown
    }

    public InfoKind Kind { get; init; }
    public string UrlString { get; init; } = "";
    public List<HlsVariantInfo> Variants { get; init; } = new();
    public double Duration { get; init; }
    public int SegmentCount { get; init; }
    public long EstimatedBytes { get; init; }
    public double TargetDuration { get; init; }
    public bool IsEncrypted { get; init; }
    /// <summary>主列表把音频单独分轨时提示：直接下某一档可能没有声音。</summary>
    public bool HasSeparateAudio { get; init; }
    public bool IsPlaceholder { get; init; }
    public string? ErrorText { get; init; }
    public string? ErrorHint { get; init; }

    public string? DurationText => Duration > 0 ? HlsVariantInfo.ClockText(Duration) : null;
    public string? EstimatedSizeText => EstimatedBytes > 0 ? FileSize.Format(EstimatedBytes) : null;
}

/// <summary>用页面同款请求头抓播放列表，展开主列表的清晰度档位，并估算体积。</summary>
public static class HlsPlaylistInspector
{
    private const int MaxEnrichedVariants = 8;

    public static async Task<HlsPlaylistInfo> InspectAsync(
        string urlString,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var fetched = await FetchDetailedAsync(urlString, context, client, cancellationToken);
        if (fetched.Failure != null)
        {
            return new HlsPlaylistInfo
            {
                Kind = HlsPlaylistInfo.InfoKind.Unknown,
                UrlString = urlString,
                ErrorText = fetched.Failure.Message,
                ErrorHint = fetched.Failure.Suggestion
            };
        }

        var text = fetched.Text!;
        var finalUrl = fetched.FinalUrl!;
        var master = HlsPlaylistParser.ParseMaster(text, finalUrl);
        if (master != null)
        {
            if (master.Count == 0)
            {
                return new HlsPlaylistInfo
                {
                    Kind = HlsPlaylistInfo.InfoKind.Unknown,
                    UrlString = urlString,
                    ErrorText = "主播放列表里没有任何清晰度档位。",
                    ErrorHint = "刷新页面重新播放后再试；也可以直接用主列表地址交给 yt-dlp 处理。"
                };
            }
            var enriched = await EnrichAsync(master, context, client, cancellationToken);
            var ranked = Ranked(enriched);
            return new HlsPlaylistInfo
            {
                Kind = HlsPlaylistInfo.InfoKind.Master,
                UrlString = urlString,
                Variants = ranked,
                Duration = ranked.Count == 0 ? 0 : ranked.Max(variant => variant.Duration),
                SegmentCount = ranked.Count == 0 ? 0 : ranked.Max(variant => variant.SegmentCount),
                HasSeparateAudio = text.Contains("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal),
                IsPlaceholder = ranked.Count > 0 && ranked.All(variant => variant.IsPlaceholder)
            };
        }

        if (text.Contains("#EXTINF", StringComparison.Ordinal)
            || text.Contains("#EXT-X-TARGETDURATION", StringComparison.Ordinal))
        {
            var spec = HlsPlaylistParser.ParseMedia(text, finalUrl);
            var estimatedBytes = 0L;
            if (spec.Segments.Count > 1)
            {
                var size = await ContentLengthAsync(spec.Segments[0].Url, context, client, cancellationToken);
                if (size > 0) estimatedBytes = size * spec.Segments.Count;
            }
            var adSegments = spec.Segments.Count(segment => AdStreamDetector.IsAdUrl(segment.Url));
            return new HlsPlaylistInfo
            {
                Kind = HlsPlaylistInfo.InfoKind.Media,
                UrlString = urlString,
                Duration = spec.TotalDuration,
                SegmentCount = spec.Segments.Count,
                EstimatedBytes = estimatedBytes,
                TargetDuration = spec.TargetDuration,
                IsEncrypted = spec.InitSegment?.KeyLine != null
                              || spec.Segments.Any(segment => segment.KeyLine != null),
                IsPlaceholder = adSegments > 0 && adSegments * 2 >= spec.Segments.Count
            };
        }

        return new HlsPlaylistInfo
        {
            Kind = HlsPlaylistInfo.InfoKind.Unknown,
            UrlString = urlString,
            ErrorText = "返回的内容不是有效的 HLS 播放列表（可能是错误页或 JSON）。",
            ErrorHint = "重新播放视频并选择扩展名为 .m3u8 的条目；也可能是请求被重定向到了错误页。"
        };
    }

    private static async Task<List<HlsVariantInfo>> EnrichAsync(
        List<HlsVariant> variants,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var targets = variants.Take(MaxEnrichedVariants).ToList();
        var details = new Dictionary<string, (double Duration, int Segments)>();
        var tasks = targets.Select(async variant =>
        {
            var fetched = await FetchDetailedAsync(variant.UrlString, context, client, cancellationToken);
            if (fetched.Failure != null) return (variant.UrlString, 0.0, 0);
            var spec = HlsPlaylistParser.ParseMedia(fetched.Text!, fetched.FinalUrl!);
            return (variant.UrlString, spec.TotalDuration, spec.Segments.Count);
        });
        foreach (var (url, duration, segments) in await Task.WhenAll(tasks))
        {
            details[url] = (duration, segments);
        }

        var fallbackDuration = details.Count == 0 ? 0 : details.Values.Max(value => value.Duration);
        var fallbackSegments = details.Count == 0 ? 0 : details.Values.Max(value => value.Segments);

        return variants.Select(variant =>
        {
            details.TryGetValue(variant.UrlString, out var detail);
            return new HlsVariantInfo
            {
                UrlString = variant.UrlString,
                Bandwidth = variant.Bandwidth,
                AverageBandwidth = variant.AverageBandwidth,
                Width = variant.Width,
                Height = variant.Height,
                FrameRate = variant.FrameRate,
                Codecs = variant.Codecs,
                Duration = detail.Duration > 0 ? detail.Duration : fallbackDuration,
                SegmentCount = detail.Segments > 0 ? detail.Segments : fallbackSegments
            };
        }).ToList();
    }

    /// <summary>最高码率与推荐档：同分辨率里码率最低的那档最划算；广告档不参与并沉底。</summary>
    private static List<HlsVariantInfo> Ranked(List<HlsVariantInfo> variants)
    {
        if (variants.Count == 0) return variants;
        var playable = variants.Where(variant => !variant.IsPlaceholder).ToList();
        var basis = playable.Count > 0 ? playable : variants;
        var sorted = basis.OrderByDescending(variant => variant.EffectiveBandwidth).ToList();
        var highest = sorted[0].UrlString;

        string? recommended = null;
        var videoVariants = basis.Where(variant => (variant.Height ?? 0) > 0).ToList();
        if (videoVariants.Count > 0)
        {
            var maxHeight = videoVariants.Max(variant => variant.Height ?? 0);
            var sameSize = videoVariants.Where(variant => (variant.Height ?? 0) == maxHeight).ToList();
            if (sameSize.Count > 1)
            {
                recommended = sameSize.OrderBy(variant => variant.EffectiveBandwidth).First().UrlString;
            }
        }
        if (recommended == null && sorted.Count > 1) recommended = sorted[1].UrlString;
        if (recommended == highest) recommended = null;

        foreach (var variant in variants)
        {
            variant.IsHighest = variant.UrlString == highest;
            variant.IsRecommended = variant.UrlString == recommended;
        }
        return variants
            .OrderBy(variant => variant.IsPlaceholder)
            .ThenByDescending(variant => variant.EffectiveBandwidth)
            .ToList();
    }

    // MARK: - 网络

    private sealed record FetchedText(string? Text, Uri? FinalUrl, FetchFailure? Failure);

    private sealed record FetchFailure(string Message, string Suggestion, int? StatusCode, bool Transient);

    private static async Task<FetchedText> FetchDetailedAsync(
        string address,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new FetchedText(null, null, new FetchFailure(
                "地址格式不完整，无法发起请求。",
                "重新在页面里播放视频，用面板中新捕获的地址再试。",
                null,
                false));
        }

        FetchFailure last = new("网络错误：请求未完成", "确认页面能正常播放后再试。", null, true);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(600 * attempt, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new FetchedText(null, null, last);
                }
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                context.Apply(request);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = FromStatus((int)response.StatusCode);
                    last = failure;
                    if (!failure.Transient || attempt == 2) return new FetchedText(null, null, failure);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[2 * 1024 * 1024];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                    if (read <= 0) break;
                    total += read;
                }
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                return new FetchedText(text, response.RequestMessage?.RequestUri ?? uri, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new FetchedText(null, null, last);
            }
            catch (OperationCanceledException)
            {
                return new FetchedText(null, null, new FetchFailure(
                    "请求超时：服务器在规定时间内没有响应。",
                    "检查网络或页面是否需要人机验证，然后重新播放视频并点「重新解析」。",
                    null,
                    false));
            }
            catch (Exception ex)
            {
                var failure = new FetchFailure(
                    "网络错误：" + ex.Message,
                    "确认页面能正常播放、网络与证书没有问题，再重新播放视频并点「重新解析」。",
                    null,
                    true);
                last = failure;
                if (attempt == 2) return new FetchedText(null, null, failure);
            }
        }
        return new FetchedText(null, null, last);
    }

    private static FetchFailure FromStatus(int code)
    {
        var (message, suggestion) = code switch
        {
            401 or 403 => ("服务器返回 " + code + "：拒绝访问，播放列表需要有效的 Referer / Cookie 或签名。",
                "先在左侧页面重新播放一次视频，让播放器刷新 Cookie / token，再点「重新解析」；仍然 403 时刷新页面重新抓一次地址。"),
            404 or 410 => ("服务器返回 " + code + "：播放列表地址已失效（签名过期或文件已被清理）。",
                "地址已过期：刷新页面并重新播放视频，等面板里出现新地址后再下载。"),
            429 => ("服务器返回 429：请求过于频繁，被站点限流。",
                "稍等几十秒再点「重新解析」，避免连续触发限流。"),
            >= 500 and <= 599 => ("服务器返回 " + code + "：站点临时故障。",
                "站点临时故障，稍后点「重新解析」；持续失败可刷新页面重试。"),
            _ => ("服务器返回 HTTP " + code + "。", "确认页面仍能正常播放，刷新后重新抓取地址再试。")
        };
        var transient = code == 403 || code == 408 || code == 429 || code is >= 500 and <= 599;
        return new FetchFailure(message, suggestion, code, transient);
    }

    /// <summary>HEAD 拿 Content-Length；服务器拒绝 HEAD 时用单字节 Range 读 Content-Range。</summary>
    public static async Task<long> ContentLengthAsync(
        string address,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return 0;

        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            context.Apply(head);
            using var headResponse = await client.SendAsync(head, headCts.Token);
            if (headResponse.IsSuccessStatusCode
                && headResponse.Content.Headers.ContentLength is long length
                && length > 0)
            {
                return length;
            }
        }
        catch (Exception)
        {
            // HEAD 被拒绝时继续用 Range 兜底。
        }

        try
        {
            using var rangeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rangeCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var ranged = new HttpRequestMessage(HttpMethod.Get, uri);
            ranged.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            context.Apply(ranged);
            using var response = await client.SendAsync(ranged, HttpCompletionOption.ResponseHeadersRead, rangeCts.Token);
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange?.Length is long total && total > 0) return total;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentLength is long length
                && length > 0)
            {
                return length;
            }
        }
        catch (Exception)
        {
            // 拿不到体积就返回 0，面板不显示体积。
        }
        return 0;
    }
}
