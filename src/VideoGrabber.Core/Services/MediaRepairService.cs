using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.Core.Services;

public sealed record MediaRepairEntry(string Path, bool Ok, string Message, IReadOnlyList<string> Log);

/// <summary>菜单「修复本地视频文件…」：逐个走同一套校验 / 修复流程。</summary>
public sealed class MediaRepairService
{
    private readonly AppSettings _settings;
    private readonly ToolEnvironment _tools;
    private readonly IProcessRunner _runner;

    public MediaRepairService(AppSettings settings, ToolEnvironment tools, IProcessRunner runner)
    {
        _settings = settings;
        _tools = tools;
        _runner = runner;
    }

    public async Task<MediaRepairEntry> RepairAsync(string path, CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        if (_tools.Ffmpeg == null)
        {
            return new MediaRepairEntry(path, false, "未找到 ffmpeg，无法修复。", log);
        }

        var verifier = new MediaVerifier(_runner, _settings, _tools, log.Add);
        var outcome = await verifier.EnsurePlayableAsync(
            path,
            isAudioOnly: IsAudioOnly(path),
            expectedBytes: 0,
            cancellationToken);

        if (!outcome.Ok)
        {
            return new MediaRepairEntry(path, false, outcome.Failure?.Message ?? "未知错误", log);
        }

        var fileName = System.IO.Path.GetFileName(path);
        if (outcome.Path == path)
        {
            return new MediaRepairEntry(path, true, "已可正常播放", log);
        }
        return new MediaRepairEntry(outcome.Path, true, $"已修复为 {System.IO.Path.GetFileName(outcome.Path)}（原文件保留）", log);
    }

    /// <summary>批量修复；单个失败不影响其他文件。</summary>
    public async Task<List<MediaRepairEntry>> RepairManyAsync(
        IEnumerable<string> paths,
        Action<MediaRepairEntry>? onEach = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MediaRepairEntry>();
        foreach (var path in paths)
        {
            var entry = await RepairAsync(path, cancellationToken);
            results.Add(entry);
            onEach?.Invoke(entry);
        }
        return results;
    }

    private static bool IsAudioOnly(string path)
    {
        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "mp3" or "m4a" or "aac" or "flac" or "wav" or "ogg" or "opus";
    }
}
