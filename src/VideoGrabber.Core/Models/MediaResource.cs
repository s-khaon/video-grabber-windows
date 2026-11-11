using VideoGrabber.Core.Services;

namespace VideoGrabber.Core.Models;

public enum MediaResourceKind
{
    Hls,
    Mp4,
    Fragment,
    Audio,
    Webm,
    Blob,
    Other
}

/// <summary>内置浏览器里捕获到的一条媒体地址。</summary>
public sealed class MediaResource
{
    public required string UrlString { get; init; }
    public MediaResourceKind Kind { get; init; }
    public string Via { get; init; } = "unknown";
    public string PageURL { get; init; } = "";
    public string UserAgent { get; init; } = "";
    public DateTime DiscoveredAt { get; init; } = DateTime.Now;
    /// <summary>blob: 地址关联的最近一个 m3u8（播放器把分片拼进内存之前加载的播放列表）。</summary>
    public string? RelatedSource { get; set; }
    /// <summary>面板里带入的展示名（例如清晰度档位），优先于文件名。</summary>
    public string? TitleHint { get; set; }

    public bool IsBlob => Kind == MediaResourceKind.Blob;

    /// <summary>广告域名 / 占位资源：面板里标注并排到末尾，自动接管会跳过。</summary>
    public bool IsPlaceholder => AdStreamDetector.IsAdUrl(UrlString);

    public string Host =>
        Uri.TryCreate(UrlString, UriKind.Absolute, out var uri) ? uri.Host : "";

    public string FileName
    {
        get
        {
            if (!Uri.TryCreate(UrlString, UriKind.Absolute, out var uri)) return UrlString;
            var last = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
            return last.Length == 0 ? UrlString : last;
        }
    }

    /// <summary>用于下载任务标题：优先档位展示名，其次去掉扩展名的文件名。</summary>
    public string? SuggestedTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TitleHint)) return TitleHint.Trimmed();
            if (IsBlob) return null;
            var baseName = Path.GetFileNameWithoutExtension(FileName);
            return baseName.Length == 0 ? null : baseName;
        }
    }

    public string KindLabel => Kind switch
    {
        MediaResourceKind.Hls => "HLS 播放列表",
        MediaResourceKind.Mp4 => "MP4 视频",
        MediaResourceKind.Fragment => "分片（m4s/ts）",
        MediaResourceKind.Audio => "音频",
        MediaResourceKind.Webm => "WebM",
        MediaResourceKind.Blob => "blob 内存流",
        _ => "其他媒体"
    };

    public MediaResource ReplacingUrl(string newUrl, MediaResourceKind? kind = null, string? titleHint = null) => new()
    {
        UrlString = newUrl,
        Kind = kind ?? Classify(newUrl),
        Via = "related",
        PageURL = PageURL,
        UserAgent = UserAgent,
        DiscoveredAt = DiscoveredAt,
        TitleHint = titleHint ?? TitleHint
    };

    /// <summary>归并指纹：忽略 query / fragment 与主机大小写，短时 token 不同也能归到同一组。</summary>
    public static string CanonicalKey(string urlString)
    {
        if (urlString.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return urlString.ToLowerInvariant();
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return urlString;
        var builder = new UriBuilder(uri)
        {
            Query = "",
            Fragment = "",
            Host = uri.Host.ToLowerInvariant()
        };
        return builder.Uri.AbsoluteUri;
    }

    public static MediaResourceKind Classify(string urlString)
    {
        var lower = urlString.ToLowerInvariant();
        if (lower.StartsWith("blob:")) return MediaResourceKind.Blob;
        var extension = "";
        if (Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        }
        return extension switch
        {
            "m3u8" or "m3u" => MediaResourceKind.Hls,
            "mp4" or "m4v" or "mov" => MediaResourceKind.Mp4,
            "m4s" or "ts" => MediaResourceKind.Fragment,
            "mp3" or "m4a" or "aac" or "flac" or "wav" => MediaResourceKind.Audio,
            "webm" => MediaResourceKind.Webm,
            _ => MediaResourceKind.Other
        };
    }

    /// <summary>去掉 query / fragment 后按扩展名判断是否媒体地址（兼容签名地址）。</summary>
    public static bool LooksLikeMedia(string urlString)
    {
        var lower = urlString.ToLowerInvariant();
        var cut = lower.IndexOfAny(new[] { '?', '#' });
        var path = cut >= 0 ? lower[..cut] : lower;
        string[] extensions = { ".m3u8", ".m4u8", ".mp4", ".m4s", ".ts", ".mp3", ".webm", ".m4a", ".mov" };
        return extensions.Any(path.EndsWith);
    }
}
