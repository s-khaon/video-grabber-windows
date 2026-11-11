namespace VideoGrabber.Core.Models;

/// <summary>任务状态。</summary>
public enum JobState
{
    Queued,
    Running,
    Completed,
    /// <summary>用户提前结束，已把下载到的部分封装成可播放视频；剩余数据保留，可继续下载。</summary>
    Partial,
    Failed,
    Cancelled
}

public static class JobStateText
{
    public static string DisplayName(this JobState state) => state switch
    {
        JobState.Queued => "排队中",
        JobState.Running => "下载中",
        JobState.Completed => "已完成",
        JobState.Partial => "已保存部分",
        JobState.Failed => "失败",
        JobState.Cancelled => "已取消",
        _ => "未知"
    };

    /// <summary>状态图标（Segoe MDL2 Assets 字形，WPF 端直接使用）。</summary>
    public static string Glyph(this JobState state) => state switch
    {
        JobState.Queued => "\uE823",
        JobState.Running => "\uE896",
        JobState.Completed => "\uE930",
        JobState.Partial => "\uE7BA",
        JobState.Failed => "\uE7BA",
        JobState.Cancelled => "\uE711",
        _ => "\uE946"
    };
}

/// <summary>失败类型：界面据此给出不同的下一步引导。</summary>
public enum FailureKind
{
    Other,
    ToolMissing,
    FfmpegMissing,
    LoginRequired,
    Forbidden,
    ExtractorUnsupported,
    Drm,
    Network,
    PermissionDenied,
    /// <summary>播放列表是广告 / 占位流（图片流、广告域名），重试也不会成功。</summary>
    PlaceholderStream,
    /// <summary>编码或容器不受支持、产物里没有可用视频流等确定性错误。</summary>
    UnsupportedMedia
}

/// <summary>失败信息，直接展示给用户。</summary>
public sealed class FailureInfo
{
    public string Message { get; init; } = "";
    public string? Hint { get; init; }
    public bool NeedsVerification { get; init; }
    public FailureKind Kind { get; init; } = FailureKind.Other;

    public bool OffersSniffing => Kind is FailureKind.ExtractorUnsupported or FailureKind.Forbidden;
}

/// <summary>下载引擎：yt-dlp 解析下载，或 ffmpeg 直接按地址抓流。</summary>
public enum DownloadEngine
{
    YtDlp,
    FfmpegDirect
}

public static class DownloadEngineText
{
    public static string DisplayName(this DownloadEngine engine) =>
        engine == DownloadEngine.YtDlp ? "yt-dlp" : "ffmpeg 直连";
}

/// <summary>清晰度选择。</summary>
public sealed record QualityChoice(string Id, string Label, int? Height, bool IsAudioOnly)
{
    public static readonly QualityChoice Best = new("best", "最佳画质", null, false);
    public static readonly QualityChoice AudioOnly = new("audio", "仅音频（MP3）", null, true);

    public static QualityChoice HeightChoice(int value) => new($"h{value}", $"{value}p", value, false);

    public string FormatSelector
    {
        get
        {
            if (IsAudioOnly) return "bestaudio/best";
            if (Height is int h) return $"bv*[height<={h}]+ba/b[height<={h}]";
            return "bv*+ba/b";
        }
    }
}

/// <summary>视频容器格式。</summary>
public enum VideoContainer
{
    Auto,
    Mp4,
    Mkv
}

public static class VideoContainerText
{
    public static string Label(this VideoContainer container) => container switch
    {
        VideoContainer.Mp4 => "MP4",
        VideoContainer.Mkv => "MKV",
        _ => "自动"
    };

    public static string? MergeOutputFormat(this VideoContainer container) => container switch
    {
        VideoContainer.Mp4 => "mp4",
        VideoContainer.Mkv => "mkv",
        _ => null
    };

    public static string ToKey(this VideoContainer container) => container switch
    {
        VideoContainer.Mp4 => "mp4",
        VideoContainer.Mkv => "mkv",
        _ => "auto"
    };

    public static VideoContainer FromKey(string? key) => key switch
    {
        "mp4" => VideoContainer.Mp4,
        "mkv" => VideoContainer.Mkv,
        _ => VideoContainer.Auto
    };
}
