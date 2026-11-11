using System.Net;
using System.Net.Http;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>发往播放列表 / 分片 / 密钥的请求头。</summary>
public sealed record HlsRequestContext(string? UserAgent, string? Referer, string? Cookie)
{
    public void Apply(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Trimmed());
        }
        if (!string.IsNullOrWhiteSpace(Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", Referer.Trimmed().RefererValue());
            var origin = Referer.Trimmed().OriginValue();
            if (!string.IsNullOrEmpty(origin)) request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        if (!string.IsNullOrWhiteSpace(Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", Cookie.Trimmed());
        }
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
    }

    /// <summary>ffmpeg `-headers` 需要的一整段请求头（`\r\n` 分隔）。</summary>
    public string? HeaderValue()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(UserAgent)) lines.Add("User-Agent: " + UserAgent.Trimmed());
        if (!string.IsNullOrWhiteSpace(Referer))
        {
            lines.Add("Referer: " + Referer.Trimmed().RefererValue());
            var origin = Referer.Trimmed().OriginValue();
            if (!string.IsNullOrEmpty(origin)) lines.Add("Origin: " + origin);
        }
        if (!string.IsNullOrWhiteSpace(Cookie)) lines.Add("Cookie: " + Cookie.Trimmed());
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }
}

/// <summary>HLS 通道共用的 HttpClient：默认无超时，单次请求用 CTS 控制。</summary>
public static class HlsHttp
{
    public static HttpClient Create(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var proxy = ProxyConfig.Resolve(settings);
        if (proxy.IsActive && proxy.HttpUrl != null)
        {
            handler.Proxy = new WebProxy(proxy.HttpUrl);
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static Uri? Resolve(string uri, Uri baseUri)
    {
        var trimmed = uri.Trimmed();
        if (trimmed.Length == 0) return null;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Scheme))
        {
            return absolute;
        }
        return Uri.TryCreate(baseUri, trimmed, out var relative) ? relative : null;
    }
}

/// <summary>HLS 播放列表解析：媒体列表（分片 / 密钥 / init）与主列表（档位）。</summary>
public static class HlsPlaylistParser
{
    private const string TargetDurationPrefix = "#EXT-X-TARGETDURATION:";
    private const string ExtInfPrefix = "#EXTINF:";
    private const string KeyPrefix = "#EXT-X-KEY:";
    private const string MapPrefix = "#EXT-X-MAP:";

    /// <summary>解析媒体播放列表：保留密钥轮换、init 段与不连续标记，URI 全部解析成绝对地址。</summary>
    public static HlsMediaSpec ParseMedia(string text, Uri baseUri)
    {
        var spec = new HlsMediaSpec();
        var pendingDuration = 0.0;
        string? pendingKey = null;
        var pendingDiscontinuity = false;
        var keyChanged = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trimmed();
            if (line.Length == 0) continue;

            if (line.StartsWith(TargetDurationPrefix, StringComparison.Ordinal))
            {
                if (double.TryParse(line[TargetDurationPrefix.Length..].Trimmed(), out var target)) spec.TargetDuration = target;
                continue;
            }
            if (line.StartsWith(ExtInfPrefix, StringComparison.Ordinal))
            {
                var value = line[ExtInfPrefix.Length..];
                var comma = value.IndexOf(',');
                if (comma >= 0) value = value[..comma];
                if (double.TryParse(value.Trim(), out var duration)) pendingDuration = duration;
                continue;
            }
            if (line.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                pendingKey = line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : RewriteUri(line, baseUri);
                keyChanged = true;
                continue;
            }
            if (line.StartsWith(MapPrefix, StringComparison.Ordinal))
            {
                var rewritten = RewriteUri(line, baseUri);
                var uri = UriValue(rewritten);
                if (!string.IsNullOrEmpty(uri))
                {
                    spec.InitSegment = new HlsSegmentRecord
                    {
                        Url = uri!,
                        Duration = 0,
                        KeyLine = pendingKey,
                        IsInit = true
                    };
                }
                continue;
            }
            if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
            {
                pendingDiscontinuity = true;
                continue;
            }
            if (line.StartsWith('#')) continue;

            var resolved = HlsHttp.Resolve(line, baseUri);
            if (resolved == null) continue;
            // 密钥行只在轮换后绑定到后续分片，避免每段都重复记录。
            var key = keyChanged ? pendingKey : spec.Segments.LastOrDefault()?.KeyLine;
            keyChanged = false;
            spec.Segments.Add(new HlsSegmentRecord
            {
                Url = resolved.AbsoluteUri,
                Duration = pendingDuration > 0 ? pendingDuration : spec.TargetDuration,
                KeyLine = key,
                Discontinuity = pendingDiscontinuity
            });
            pendingDuration = 0;
            pendingDiscontinuity = false;
        }

        spec.TotalDuration = spec.Segments.Sum(segment => segment.Duration);
        return spec;
    }

    /// <summary>解析主播放列表；不是主列表（没有 EXT-X-STREAM-INF）时返回 null。</summary>
    public static List<HlsVariant>? ParseMaster(string text, Uri baseUri)
    {
        if (!text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal)) return null;

        var variants = new List<HlsVariant>();
        HlsVariant? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trimmed();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = ParseVariantAttributes(line["#EXT-X-STREAM-INF:".Length..], baseUri);
                continue;
            }
            if (line.StartsWith('#')) continue;
            if (pending == null) continue;
            var resolved = HlsHttp.Resolve(line, baseUri);
            if (resolved != null)
            {
                variants.Add(new HlsVariant
                {
                    UrlString = resolved.AbsoluteUri,
                    Resolution = pending.Resolution,
                    Width = pending.Width,
                    Height = pending.Height,
                    Bandwidth = pending.Bandwidth,
                    AverageBandwidth = pending.AverageBandwidth,
                    FrameRate = pending.FrameRate,
                    Codecs = pending.Codecs,
                    IsPlaceholder = pending.IsPlaceholder
                });
            }
            pending = null;
        }
        return variants;
    }

    private static HlsVariant ParseVariantAttributes(string attributes, Uri baseUri)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var parts = new List<string>();
        foreach (var ch in attributes)
        {
            if (ch == '"') inQuotes = !inQuotes;
            if (ch == ',' && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());

        foreach (var part in parts)
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            var key = part[..index].Trimmed().ToUpperInvariant();
            var value = part[(index + 1)..].Trimmed().Trim('"');
            map[key] = value;
        }

        long bandwidth = 0;
        long average = 0;
        if (map.TryGetValue("BANDWIDTH", out var bandwidthText)) long.TryParse(bandwidthText, out bandwidth);
        if (map.TryGetValue("AVERAGE-BANDWIDTH", out var averageText)) long.TryParse(averageText, out average);

        int? width = null;
        int? height = null;
        string? resolution = null;
        if (map.TryGetValue("RESOLUTION", out var resolutionText))
        {
            resolution = resolutionText;
            var split = resolutionText.Split('x');
            if (split.Length == 2)
            {
                if (int.TryParse(split[0], out var w)) width = w;
                if (int.TryParse(split[1], out var h)) height = h;
            }
        }

        double? frameRate = null;
        if (map.TryGetValue("FRAME-RATE", out var frameText)
            && double.TryParse(frameText, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            frameRate = parsed;
        }

        map.TryGetValue("CODECS", out var codecs);
        var isPlaceholder = AdStreamDetector.IsAdUrl(baseUri.AbsoluteUri) || AdStreamDetector.IsImageCodec(codecs);
        // 声明了编码但完全没有视频编码（只有 mjpeg / png 之类）的档位同样不可用。
        if (!string.IsNullOrEmpty(codecs) && AdStreamDetector.IsImageCodec(codecs) && !AdStreamDetector.IsVideoCodec(codecs))
        {
            isPlaceholder = true;
        }

        return new HlsVariant
        {
            Resolution = resolution,
            Width = width,
            Height = height,
            Bandwidth = bandwidth,
            AverageBandwidth = average,
            FrameRate = frameRate,
            Codecs = codecs,
            IsPlaceholder = isPlaceholder
        };
    }

    /// <summary>把 KEY / MAP 行里的 URI 改成绝对地址，便于在本地播放列表里继续使用。</summary>
    private static string RewriteUri(string line, Uri baseUri)
    {
        var uri = UriValue(line);
        if (string.IsNullOrEmpty(uri)) return line;
        var resolved = HlsHttp.Resolve(uri!, baseUri);
        if (resolved == null) return line;
        return line.Replace($"URI=\"{uri}\"", $"URI=\"{resolved.AbsoluteUri}\"");
    }

    public static string? UriValue(string line)
    {
        const string marker = "URI=\"";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = line[(index + marker.Length)..];
        var end = rest.IndexOf('"');
        return end < 0 ? null : rest[..end];
    }
}

/// <summary>快速判断播放列表是不是直播（没有 #EXT-X-ENDLIST）、广告占位流。</summary>
public static class HlsPlaylistProbe
{
    public enum LiveState
    {
        Live,
        Vod,
        Unknown,
        /// <summary>广告 / 占位流：分片来自广告域名或返回图片，不能下载。</summary>
        Placeholder
    }

    public static async Task<LiveState> DetectAsync(
        string urlString,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var address = urlString;
        for (var round = 0; round < 2; round++)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return LiveState.Unknown;
            if (uri.Scheme != "http" && uri.Scheme != "https") return LiveState.Unknown;

            string text;
            Uri finalUri;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                context.Apply(request);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode) return LiveState.Unknown;
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[2 * 1024 * 1024];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                    if (read <= 0) break;
                    total += read;
                }
                text = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                finalUri = response.RequestMessage?.RequestUri ?? uri;
            }
            catch (Exception)
            {
                return LiveState.Unknown;
            }

            // 主播放列表先取一档媒体列表再看是否结束。
            if (text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var variants = HlsPlaylistParser.ParseMaster(text, finalUri);
                if (variants == null || variants.Count == 0) return LiveState.Unknown;
                var playable = variants.Where(variant => !variant.IsPlaceholder).ToList();
                var chosen = playable
                    .OrderByDescending(variant => variant.EffectiveBandwidth)
                    .FirstOrDefault();
                if (chosen == null) return LiveState.Placeholder;
                address = chosen.UrlString;
                continue;
            }
            if (text.Contains("#EXTINF", StringComparison.Ordinal))
            {
                var spec = HlsPlaylistParser.ParseMedia(text, finalUri);
                var adCount = spec.Segments.Count(segment => AdStreamDetector.IsAdUrl(segment.Url));
                if (adCount > 0 && adCount * 2 >= spec.Segments.Count) return LiveState.Placeholder;
                if (spec.Segments.Count > 0
                    && await AdStreamDetectorProbe.ProbeImageSegmentAsync(spec.Segments[0].Url, context, client, cancellationToken))
                {
                    return LiveState.Placeholder;
                }
                return text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal) ? LiveState.Vod : LiveState.Live;
            }
            return LiveState.Unknown;
        }
        return LiveState.Unknown;
    }
}

/// <summary>请求首个分片前 64KB，判断它是不是图片占位流（同时兼容「图片头 + TS」伪装）。</summary>
public static class AdStreamDetectorProbe
{
    public static async Task<bool> ProbeImageSegmentAsync(
        string urlString,
        HlsRequestContext context,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // 读足够多的内容：图片头之后还要能看到几段 188 字节的 TS 包。
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-65535");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            context.Apply(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return false;
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (data.Length == 0) return false;
            // 只要能看到连续的 MPEG-TS 同步字，就是伪装成图片的视频分片，不是占位流。
            if (AdStreamDetector.TransportStreamOffset(data) != null) return false;
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.Ordinal))
            {
                return true;
            }
            return AdStreamDetector.IsImageData(data);
        }
        catch (Exception)
        {
            // 请求失败时按「不是图片」处理，避免误伤正常流。
            return false;
        }
    }
}
