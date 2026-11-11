namespace VideoGrabber.Core.Models;

/// <summary>HLS 播放列表里的一个分片（含 init 段）。</summary>
public sealed class HlsSegmentRecord
{
    public string Url { get; set; } = "";
    public double Duration { get; set; }
    /// <summary>原始播放列表里的 EXT-X-KEY 行（URI 已解析成绝对地址），切换密钥时逐段记录。</summary>
    public string? KeyLine { get; set; }
    /// <summary>该分片之前是否有 EXT-X-DISCONTINUITY。</summary>
    public bool Discontinuity { get; set; }
    public bool IsInit { get; set; }
}

/// <summary>媒体播放列表的解析结果。</summary>
public sealed class HlsMediaSpec
{
    public List<HlsSegmentRecord> Segments { get; } = new();
    public HlsSegmentRecord? InitSegment { get; set; }
    public double TargetDuration { get; set; }
    public double TotalDuration { get; set; }
}

/// <summary>主播放列表里的一个档位。</summary>
public sealed class HlsVariant
{
    public string UrlString { get; init; } = "";
    public string? Resolution { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long Bandwidth { get; init; }
    public long AverageBandwidth { get; init; }
    public double? FrameRate { get; init; }
    public string? Codecs { get; init; }
    /// <summary>广告域名 / 图片编码：不能选它下载。</summary>
    public bool IsPlaceholder { get; init; }
    public double? Duration { get; set; }

    /// <summary>码率：优先 AVERAGE-BANDWIDTH，缺失时退回 BANDWIDTH。</summary>
    public long EffectiveBandwidth => AverageBandwidth > 0 ? AverageBandwidth : Bandwidth;
}

/// <summary>
/// HLS 任务的续传元数据：播放列表内容、分片清单、目标路径。
/// 已完成分片单独写在 `completed.json`，避免频繁重写播放列表。
/// </summary>
public sealed class HlsResumeState
{
    public string PlaylistUrl { get; set; } = "";
    public string PlaylistText { get; set; } = "";
    public List<HlsSegmentRecord> Segments { get; set; } = new();
    public HlsSegmentRecord? InitSegment { get; set; }
    public string TargetPath { get; set; } = "";
    public string TempDirectory { get; set; } = "";
    public double TotalDuration { get; set; }
    public long EstimatedBytes { get; set; }
}
