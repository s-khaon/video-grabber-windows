namespace VideoGrabber.Core.Services;

/// <summary>
/// 识别广告 / 占位流：这类播放列表的分片来自广告域名，或直接返回 PNG / JPEG
/// 图片（播放器把图片当视频流），用 ffmpeg 硬录只会得到「没有视频流」的结果。
/// </summary>
public static class AdStreamDetector
{
    /// <summary>
    /// 可以作为「确定性」依据的广告服务主机。不能把 URL 路径中的 `ad`、
    /// `placeholder` 等字样当成广告：不少 CDN 的随机路径、签名参数或视频 ID
    /// 都会恰好含有这些片段。误判会比漏掉广告更糟，因为会阻止用户下载正片。
    /// </summary>
    private static readonly string[] KnownAdHosts =
    {
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "adservice.google.com"
    };

    private static readonly string[] ImageCodecs = { "png", "jpeg", "jpg", "mjpeg", "gif", "webp", "bmp", "tiff" };
    private static readonly string[] VideoCodecs = { "avc1", "avc3", "hev1", "hvc1", "av01", "vp09", "vp9" };

    /// <summary>分片 / 播放列表地址是否位于已知广告服务域名（只检查主机名，不检查路径词元）。</summary>
    public static bool IsAdUrl(string? urlString)
    {
        if (!Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return KnownAdHosts.Any(entry => host == entry || host.EndsWith("." + entry, StringComparison.Ordinal));
    }

    /// <summary>播放列表 CODECS 是否声明为图片编码。</summary>
    public static bool IsImageCodec(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs)) return false;
        var lower = codecs.ToLowerInvariant();
        return ImageCodecs.Any(lower.Contains);
    }

    /// <summary>是否正常的视频编码（avc1 / hevc / av01 / vp9）。</summary>
    public static bool IsVideoCodec(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs)) return false;
        var lower = codecs.ToLowerInvariant();
        return VideoCodecs.Any(lower.Contains);
    }

    /// <summary>字节头是否是常见图片格式（PNG / JPEG / GIF / WebP / BMP）。</summary>
    public static bool IsImageData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return true;
        }
        if (data[0] == 0xFF && data[1] == 0xD8) return true;
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true;
        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return true;
        }
        if (data[0] == 0x42 && data[1] == 0x4D) return true;
        return false;
    }

    /// <summary>
    /// 在数据里定位第一段 MPEG-TS 同步链：0x47 每 188 字节出现一次。
    /// 连续命中 <paramref name="requiredSyncs"/> 个同步字才认定是 TS，避免随机字节误报。
    /// </summary>
    public static int? TransportStreamOffset(ReadOnlySpan<byte> data, int requiredSyncs = 5)
    {
        const int packetSize = 188;
        if (requiredSyncs <= 1) return null;
        var window = packetSize * requiredSyncs;
        if (data.Length < window) return null;

        for (var start = 0; start <= data.Length - window; start++)
        {
            if (data[start] != 0x47) continue;
            var matched = true;
            for (var step = 1; step < requiredSyncs; step++)
            {
                if (data[start + packetSize * step] == 0x47) continue;
                matched = false;
                break;
            }
            if (matched) return start;
        }
        return null;
    }

    /// <summary>
    /// 如果分片是「图片头 + MPEG-TS」，去掉图片头只留下视频数据；
    /// 其他情况原样返回，避免破坏正常的 TS / fMP4 分片。
    /// </summary>
    public static byte[] StrippingFakeImageHeader(byte[] data)
    {
        if (!IsImageData(data)) return data;
        var offset = TransportStreamOffset(data);
        if (offset is not > 0 || offset >= data.Length) return data;
        return data[offset.Value..];
    }
}
