using System.Text.Json;
using System.Text.Json.Serialization;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>任务快照里保存的清晰度。</summary>
public sealed class PersistedQuality
{
    public string Id { get; set; } = "best";
    public string Label { get; set; } = "最佳画质";
    public int? Height { get; set; }
    public bool IsAudioOnly { get; set; }

    public static PersistedQuality From(QualityChoice quality) => new()
    {
        Id = quality.Id,
        Label = quality.Label,
        Height = quality.Height,
        IsAudioOnly = quality.IsAudioOnly
    };

    public QualityChoice ToChoice() => new(Id, Label, Height, IsAudioOnly);
}

public sealed class PersistedFailure
{
    public string Message { get; set; } = "";
    public string? Hint { get; set; }
    public bool NeedsVerification { get; set; }
    public string Kind { get; set; } = "other";

    public static PersistedFailure From(FailureInfo failure) => new()
    {
        Message = failure.Message,
        Hint = failure.Hint,
        NeedsVerification = failure.NeedsVerification,
        Kind = FailureKindKey(failure.Kind)
    };

    public FailureInfo ToFailure() => new()
    {
        Message = Message,
        Hint = Hint,
        NeedsVerification = NeedsVerification,
        Kind = ParseKind(Kind)
    };

    private static string FailureKindKey(FailureKind kind) => kind switch
    {
        FailureKind.ToolMissing => "toolMissing",
        FailureKind.FfmpegMissing => "ffmpegMissing",
        FailureKind.LoginRequired => "loginRequired",
        FailureKind.Forbidden => "forbidden",
        FailureKind.ExtractorUnsupported => "extractorUnsupported",
        FailureKind.Drm => "drm",
        FailureKind.Network => "network",
        FailureKind.PermissionDenied => "permissionDenied",
        FailureKind.PlaceholderStream => "placeholderStream",
        FailureKind.UnsupportedMedia => "unsupportedMedia",
        _ => "other"
    };

    private static FailureKind ParseKind(string? key) => key switch
    {
        "toolMissing" => FailureKind.ToolMissing,
        "ffmpegMissing" => FailureKind.FfmpegMissing,
        "loginRequired" => FailureKind.LoginRequired,
        "forbidden" => FailureKind.Forbidden,
        "extractorUnsupported" => FailureKind.ExtractorUnsupported,
        "drm" => FailureKind.Drm,
        "network" => FailureKind.Network,
        "permissionDenied" => FailureKind.PermissionDenied,
        "placeholderStream" => FailureKind.PlaceholderStream,
        "unsupportedMedia" => FailureKind.UnsupportedMedia,
        _ => FailureKind.Other
    };
}

/// <summary>落盘的任务快照，用于重启后恢复队列与续传状态。</summary>
public sealed class PersistedJob
{
    public Guid Id { get; set; }
    public string UrlString { get; set; } = "";
    public string OriginalUrlString { get; set; } = "";
    public string Title { get; set; } = "";
    public bool TitleIsResolved { get; set; }
    public PersistedQuality Quality { get; set; } = new();
    public string Container { get; set; } = "auto";
    public string SaveDirectory { get; set; } = "";
    public string? CookiesPath { get; set; }
    public string ProxyDescription { get; set; } = "";
    public string Engine { get; set; } = "ytDlp";
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? PageURL { get; set; }
    public Guid? BatchID { get; set; }
    public string State { get; set; } = "queued";
    public double Progress { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public List<string> OutputFiles { get; set; } = new();
    public PersistedFailure? Failure { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSegmentedHLS { get; set; }
    public int AutoRetryCount { get; set; }
    public string? OutputBase { get; set; }
}

/// <summary>
/// 任务元数据仓储：每个任务一个目录，任务快照写在 `task.json`。
/// Windows 路径：%LOCALAPPDATA%\VideoGrabber\Tasks\&lt;uuid&gt;\。
/// </summary>
public sealed class TaskMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    private readonly object _gate = new();

    public TaskMetadataStore(IPathProvider paths)
    {
        _root = Path.Combine(paths.DataDirectory, "Tasks");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string TaskDirectory(Guid id) => Path.Combine(_root, id.ToString().ToUpperInvariant());

    /// <summary>yt-dlp 的 .part 专门放在任务目录下的 `ytdlp`，重启后能准确找回。</summary>
    public string YtDlpTempDirectory(Guid id) => Path.Combine(TaskDirectory(id), "ytdlp");

    public void Save(PersistedJob job)
    {
        var directory = TaskDirectory(job.Id);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(job, JsonOptions);
                var path = Path.Combine(directory, "task.json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // 快照写失败不影响正在进行的下载。
            }
        }
    }

    public List<PersistedJob> LoadAll()
    {
        var result = new List<PersistedJob>();
        if (!Directory.Exists(_root)) return result;

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var file = Path.Combine(directory, "task.json");
            if (!File.Exists(file)) continue;
            try
            {
                var job = JsonSerializer.Deserialize<PersistedJob>(File.ReadAllText(file), JsonOptions);
                if (job != null) result.Add(job);
            }
            catch (Exception)
            {
                // 单个任务文件损坏时跳过，不影响其它任务恢复。
            }
        }
        return result.OrderByDescending(job => job.CreatedAt).ToList();
    }

    /// <summary>删除任务记录；removeFiles 时连同临时数据一起删除。</summary>
    public void Delete(Guid id, bool removeFiles)
    {
        if (!removeFiles) return;
        try
        {
            var directory = TaskDirectory(id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 文件被占用时忽略，下次清理。
        }
    }
}
