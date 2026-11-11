using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>输出命名规则：标题 + 稳定视频标识，续传 / 重试不改名。</summary>
public static class FileNameRules
{
    /// <summary>「提前结束」生成的临时视频标记，自动收尾时不会把它当成完整产物。</summary>
    public const string PartialFileNameMarker = " - 已下载部分";

    public static readonly HashSet<string> MediaOutputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "webm", "mov", "m4v", "avi", "flv", "ts",
        "m4a", "mp3", "aac", "ogg", "opus", "wav"
    };

    private static readonly string[] UniqueExtensions =
    {
        "mp4", "mkv", "webm", "mov", "m4v", "mp3", "m4a", "aac",
        "flac", "wav", "ts", "part", "ytdl"
    };

    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "index", "master", "playlist", "chunklist", "main", "prog_index",
        "video", "videos", "stream", "streams", "media", "hls", "vod",
        "e", "v", "embed", "player", "watch", "file", "files", "download",
        "downloads", "api", "mp4", "m3u8", "ts"
    };

    private static readonly Regex NumericTimestamp = new("^[0-9]{10,}$", RegexOptions.Compiled);
    private static readonly Regex UnsafeIdentifierChars = new("[^a-z0-9._-]", RegexOptions.Compiled);

    /// <summary>
    /// 文件名里不能出现路径分隔符；`:` 在资源管理器里会显示成 `/`，也一并替换。
    /// Windows 额外禁止 `&lt; &gt; | ? *`，末尾不能是点或空格，这里一并处理。
    /// </summary>
    public static string SanitizedFileBase(string? raw)
    {
        var value = (raw ?? "").Trimmed()
            .Replace("/", "_")
            .Replace(":", "_")
            .Replace("\\", "_")
            .Replace("\"", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace("|", "_")
            .Replace("?", "_")
            .Replace("*", "_")
            .Replace("×", "x")
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\0", "");
        return value.TrimEnd(' ', '.');
    }

    /// <summary>把标题和稳定的视频标识拼成基名：`javclan.com 1920x1080 [nm420mnqg669]`。</summary>
    public static string MakeOutputBase(DownloadJob job)
    {
        var title = SanitizedFileBase(job.Title);
        if (title.Length == 0) title = SanitizedFileBase(UrlTools.HostOf(job.UrlString) ?? "video");
        if (title.Length == 0) title = "video";
        if (title.Length > 120) title = title[..120];

        var identifier = StableVideoIdentifier(job);
        return identifier.Length == 0 ? title : $"{title} [{identifier}]";
    }

    private static string StableVideoIdentifier(DownloadJob job)
    {
        var fromPage = string.IsNullOrEmpty(job.PageURL) ? null : Identifier(job.PageURL!);
        if (!string.IsNullOrEmpty(fromPage)) return fromPage!;
        var fromOriginal = Identifier(job.OriginalUrlString);
        if (!string.IsNullOrEmpty(fromOriginal)) return fromOriginal!;
        var fromCurrent = Identifier(job.UrlString);
        if (!string.IsNullOrEmpty(fromCurrent)) return fromCurrent!;
        // 实在提取不到可读标识时，用用户最初粘贴的地址（保留查询参数）做短哈希。
        return ShortHash((job.PageURL ?? job.OriginalUrlString).ToLowerInvariant());
    }

    /// <summary>
    /// 从 URL 里挑一个能区分视频的路径段：
    /// 页面地址优先取最后一个有效段；媒体地址跳过 `index-f3-v1-a1`、`index`、`master` 等通用段；
    /// 10 位以上的纯数字通常是过期时间戳，不拿来当标识。
    /// </summary>
    public static string? Identifier(string urlString)
    {
        if (!Uri.TryCreate(urlString ?? "", UriKind.Absolute, out var uri)) return null;
        var components = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (components.Count == 0) return null;

        var last = components[^1];
        components.RemoveAt(components.Count - 1);
        var extension = Path.GetExtension(last);
        components.Add(extension.Length > 0 ? Path.GetFileNameWithoutExtension(last) : last);

        for (var index = components.Count - 1; index >= 0; index--)
        {
            var lower = components[index].ToLowerInvariant();
            if (GenericNames.Contains(lower)) continue;
            if (lower.StartsWith("index-", StringComparison.Ordinal)
                || lower.StartsWith("seg-", StringComparison.Ordinal)
                || lower.StartsWith("init-", StringComparison.Ordinal)) continue;
            if (NumericTimestamp.IsMatch(lower)) continue;

            var cleaned = UnsafeIdentifierChars.Replace(lower, "-").Trim('-', '.', '_');
            if (cleaned.Length < 3) continue;
            return cleaned.Length > 32 ? cleaned[..32] : cleaned;
        }
        return null;
    }

    private static string ShortHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>文件名是否属于这个固定基名：`base` 本身，或同名冲突时的 `base (2)`。</summary>
    public static bool NameMatchesBase(string name, string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return true;
        var candidate = name.ToLowerInvariant();
        var target = baseName.ToLowerInvariant();
        return candidate == target || candidate.StartsWith(target + " (", StringComparison.Ordinal);
    }

    /// <summary>
    /// 输出基名：优先使用任务里已固定的基名；否则在保存目录里挑一个不冲突的名字并固定下来。
    /// </summary>
    public static string ResolvedOutputBase(DownloadJob job)
    {
        var existing = job.OutputBase?.Trimmed();
        if (!string.IsNullOrEmpty(existing)) return existing!;

        var inherited = InheritedOutputBase(job);
        if (!string.IsNullOrEmpty(inherited))
        {
            job.OutputBase = inherited;
            return inherited!;
        }

        var unique = UniqueOutputBase(job);
        job.OutputBase = unique;
        return unique;
    }

    private static string? InheritedOutputBase(DownloadJob job)
    {
        var target = job.DirectOutputPath;
        if (!string.IsNullOrEmpty(target))
        {
            var name = Path.GetFileNameWithoutExtension(target);
            if (name.Length > 0 && !name.Contains(PartialFileNameMarker)) return name;
        }

        var output = job.OutputFiles.FirstOrDefault(file =>
            !file.Contains(Path.DirectorySeparatorChar + "Tasks" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !file.Contains(PartialFileNameMarker, StringComparison.Ordinal));
        if (output != null)
        {
            var name = Path.GetFileNameWithoutExtension(output);
            if (name.Length > 0) return name;
        }
        return null;
    }

    /// <summary>在保存目录里挑一个不与现有文件冲突的基名（追加 ` (2)`、` (3)`…）。</summary>
    public static string UniqueOutputBase(DownloadJob job)
    {
        var baseName = MakeOutputBase(job);
        var directory = job.SaveDirectory;
        var candidate = baseName;
        var index = 2;
        while (UniqueExtensions.Any(ext => File.Exists(Path.Combine(directory, $"{candidate}.{ext}"))))
        {
            candidate = $"{baseName} ({index})";
            index++;
            if (index > 999) break;
        }
        return candidate;
    }

    /// <summary>ffmpeg / 分片下载的输出路径：固定基名 + 按任务 URL 推断的扩展名。</summary>
    public static string DirectOutputPath(DownloadJob job)
    {
        var containerExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mp4", "mkv", "webm", "mp3", "m4a", "aac", "flac", "wav", "mov"
        };
        var rawName = UrlTools.LastPathComponent(job.UrlString);
        var ext = Path.GetExtension(rawName).TrimStart('.').ToLowerInvariant();
        var outputExtension = containerExtensions.Contains(ext) ? ext : "mp4";

        var baseName = ResolvedOutputBase(job);
        var candidate = Path.Combine(job.SaveDirectory, $"{baseName}.{outputExtension}");
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(job.SaveDirectory, $"{baseName} ({index}).{outputExtension}");
            index++;
            if (index > 999) break;
        }
        return candidate;
    }

    /// <summary>提前结束时的部分视频路径：`&lt;完整视频名&gt; - 已下载部分.mp4`，不会覆盖完整产物。</summary>
    public static string PartialOutputPath(string target)
    {
        var extension = Path.GetExtension(target).TrimStart('.');
        if (extension.Length == 0) extension = "mp4";
        var baseName = Path.GetFileNameWithoutExtension(target) + PartialFileNameMarker;
        var directory = Path.GetDirectoryName(target) ?? "";
        var candidate = Path.Combine(directory, $"{baseName}.{extension}");
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({index}).{extension}");
            index++;
            if (index > 999) break;
        }
        return candidate;
    }

    /// <summary>文件已存在时追加 ` (2)`、` (3)`…，绝不覆盖用户已有文件。</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = path;
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({index}){extension}");
            index++;
            if (index > 999) break;
        }
        return candidate;
    }
}
