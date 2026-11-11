using System.Globalization;

namespace VideoGrabber.Core.Services;

/// <summary>yt-dlp / ffmpeg 输出行解析。</summary>
public static class OutputParser
{
    public const string ProgressPrefix = "PROGRESS|";

    /// <summary>yt-dlp 的报错行通常在 stderr 上，这里统一提取最后一条 ERROR。</summary>
    public static string? LastErrorLine(string text)
    {
        var lines = (text ?? "")
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Trimmed())
            .Where(line => line.Length > 0)
            .ToList();
        return lines.LastOrDefault(line => line.Contains("ERROR:") || line.Contains("error"));
    }

    /// <summary>解析进度模板行：PROGRESS|百分比|速度|ETA|已下载|总量|估算总量。</summary>
    public static bool TryParseProgress(string line, out ProgressSample sample)
    {
        sample = default;
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return false;
        var parts = line[ProgressPrefix.Length..].Split('|');
        if (parts.Length < 2) return true;

        var percentText = parts[0].Replace("%", "").Trimmed();
        var sampleValue = new ProgressSample { Recognized = true };
        if (double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            sampleValue.Progress = Math.Clamp(percent / 100.0, 0, 1);
        }
        sampleValue.Speed = SanitizedStat(parts[1]);
        if (parts.Length > 2) sampleValue.Eta = SanitizedStat(parts[2]);
        if (parts.Length > 3 && long.TryParse(parts[3].Trimmed(), out var downloaded))
        {
            sampleValue.DownloadedBytes = downloaded;
        }
        if (parts.Length > 4 && long.TryParse(parts[4].Trimmed(), out var total) && total > 0)
        {
            sampleValue.TotalBytes = total;
        }
        else if (parts.Length > 5 && long.TryParse(parts[5].Trimmed(), out var estimate) && estimate > 0)
        {
            sampleValue.TotalBytes = estimate;
        }
        sample = sampleValue;
        return true;
    }

    /// <summary>`NA` / `unknown` 一律显示为空。</summary>
    private static string SanitizedStat(string value)
    {
        var trimmed = value.Trimmed();
        var lower = trimmed.ToLowerInvariant();
        if (trimmed.Length == 0 || lower == "na" || lower.StartsWith("unknown", StringComparison.Ordinal))
        {
            return "";
        }
        return trimmed;
    }

    /// <summary>ffmpeg -progress 输出：只取 total_size 作为已下载体积。</summary>
    public static bool TryParseFfmpegProgress(string line, out long totalSize)
    {
        totalSize = 0;
        string[] keys =
        {
            "total_size=", "out_time_us=", "out_time_ms=", "out_time=",
            "progress=", "speed=", "bitrate="
        };
        var key = keys.FirstOrDefault(line.StartsWith);
        if (key == null) return false;
        if (key == "total_size="
            && long.TryParse(line[key.Length..].Trimmed(), out var bytes)
            && bytes > 0)
        {
            totalSize = bytes;
        }
        return true;
    }

    /// <summary>从日志行解析产物路径。isMerge 表示这是合并后的最终文件。</summary>
    public static bool TryParseDestination(string line, out string path, out bool isMerge)
    {
        path = "";
        isMerge = false;
        string[] markers =
        {
            "[download] Destination: ",
            "[ExtractAudio] Destination: ",
            "[download] Writing video to ",
            "[download] Writing audio to "
        };
        foreach (var marker in markers)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            var value = line[(index + marker.Length)..].Trimmed();
            if (value.Length == 0) return false;
            path = value;
            return true;
        }

        const string downloadedSuffix = " has already been downloaded";
        if (line.StartsWith("[download] ", StringComparison.Ordinal)
            && line.EndsWith(downloadedSuffix, StringComparison.Ordinal))
        {
            var value = line["[download] ".Length..^downloadedSuffix.Length].Trimmed();
            if (value.Length == 0) return false;
            path = value;
            return true;
        }

        const string mergeMarker = "Merging formats into \"";
        var mergeIndex = line.IndexOf(mergeMarker, StringComparison.Ordinal);
        if (mergeIndex >= 0)
        {
            var value = line[(mergeIndex + mergeMarker.Length)..].Replace("\"", "").Trimmed();
            if (value.Length == 0) return false;
            path = value;
            isMerge = true;
            return true;
        }
        return false;
    }

    /// <summary>从文件名推导标题，去掉 yt-dlp 的 `.f137` 之类格式后缀。</summary>
    public static string CleanTitle(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var index = name.LastIndexOf(".f", StringComparison.Ordinal);
        if (index > 0 && int.TryParse(name[(index + 2)..], out _))
        {
            name = name[..index];
        }
        return name.Length == 0 ? path : name;
    }
}

public struct ProgressSample
{
    public bool Recognized;
    public double Progress;
    public string Speed;
    public string Eta;
    public long DownloadedBytes;
    public long TotalBytes;
}
