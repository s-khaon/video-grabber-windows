using System.Diagnostics;
using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.Core.Services;

/// <summary>定位本机的 yt-dlp / ffmpeg / ffprobe 可执行文件。</summary>
public sealed class ToolLocator
{
    private readonly IPathProvider _paths;

    public ToolLocator(IPathProvider paths) => _paths = paths;

    public string? Locate(string name, string customPath)
    {
        var trimmed = customPath.Trimmed();
        if (trimmed.Length > 0)
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (File.Exists(expanded)) return expanded;
        }

        var directories = new List<string>(_paths.ToolSearchDirectories);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        directories.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var directory in directories)
        {
            var expanded = Environment.ExpandEnvironmentVariables(directory.Trimmed());
            if (expanded.Length == 0) continue;
            var candidate = Path.Combine(expanded, name + _paths.ExecutableSuffix);
            if (File.Exists(candidate)) return candidate;
            var bare = Path.Combine(expanded, name);
            if (File.Exists(bare)) return bare;
        }
        return null;
    }

    /// <summary>ffprobe 通常和 ffmpeg 同目录，找不到时按 ffmpeg 所在目录再试一次。</summary>
    public string? LocateFfprobe(string customFfmpegPath, string customFfprobePath)
    {
        var direct = Locate("ffprobe", customFfprobePath);
        if (direct != null) return direct;
        var ffmpeg = Locate("ffmpeg", customFfmpegPath);
        if (ffmpeg == null) return null;
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe" + _paths.ExecutableSuffix);
        return File.Exists(sibling) ? sibling : null;
    }

    /// <summary>读取可执行文件的版本首行，失败返回 null。</summary>
    public async Task<string?> VersionAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process == null) return null;
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = stdout.Length > 0 ? stdout : stderr;
            var firstLine = text.Split('\n').FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(firstLine) ? null : firstLine;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 工具状态的共享缓存，供界面展示。
/// 探测结果（路径、版本、检测时间）持久化在设置里：启动时直接读缓存，
/// 只有缓存不存在时才执行完整探测。
/// </summary>
public sealed class ToolEnvironment : ObservableObject
{
    public enum Tool
    {
        YtDlp,
        Ffmpeg,
        Ffprobe
    }

    private readonly IPathProvider _paths;
    private readonly ToolLocator _locator;
    private readonly AppSettings _settings;

    private string? _ytDlp;
    private string? _ytDlpVersion;
    private string? _ffmpeg;
    private string? _ffmpegVersion;
    private string? _ffprobe;
    private string? _ffprobeVersion;
    private DateTime? _checkedAt;
    private bool _isRefreshing;
    private string? _invalidationNotice;

    public ToolEnvironment(IPathProvider paths, AppSettings settings)
    {
        _paths = paths;
        _settings = settings;
        _locator = new ToolLocator(paths);
        RestoreFromCache();
    }

    public string? YtDlp => _ytDlp;
    public string? YtDlpVersion => _ytDlpVersion;
    public string? Ffmpeg => _ffmpeg;
    public string? FfmpegVersion => _ffmpegVersion;
    public string? Ffprobe => _ffprobe;
    public string? FfprobeVersion => _ffprobeVersion;
    public DateTime? CheckedAt => _checkedAt;

    public bool HasCache => _checkedAt != null;
    public bool IsReady => _ytDlp != null && _ffmpeg != null;

    /// <summary>有 ffmpeg 但缺 ffprobe：yt-dlp 的元数据提取与收尾会退化。</summary>
    public bool FfprobeMissing => _ffmpeg != null && _ffprobe == null;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>缓存路径失效时的提示；非 null 表示需要重新检测。</summary>
    public string? InvalidationNotice
    {
        get => _invalidationNotice;
        private set
        {
            if (SetProperty(ref _invalidationNotice, value)) OnPropertyChanged(nameof(HasInvalidationNotice));
        }
    }

    public bool HasInvalidationNotice => !string.IsNullOrEmpty(_invalidationNotice);

    public string StatusSummary
    {
        get
        {
            var ytDlp = _ytDlp == null ? "yt-dlp 缺失" : $"yt-dlp {_ytDlpVersion ?? "已就绪"}";
            var ffmpeg = _ffmpeg == null ? "ffmpeg 缺失" : $"ffmpeg {_ffmpegVersion ?? "已就绪"}";
            var ffprobe = _ffprobe == null ? "ffprobe 缺失" : "ffprobe 已就绪";
            return $"{ytDlp} · {ffmpeg} · {ffprobe}";
        }
    }

    /// <summary>
    /// ffmpeg 7.1 起把 HLS 分片扩展名校验拆成 `-allowed_segment_extensions`，
    /// 并新增 `-extension_picky`。旧版本不认识这些选项，传入会直接报错退出。
    /// </summary>
    public IReadOnlyList<string> HlsSegmentExtensionArguments
    {
        get
        {
            var raw = _ffmpegVersion;
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            var numbers = raw.Split('.')
                .Take(2)
                .Select(part => new string(part.TakeWhile(char.IsDigit).ToArray()))
                .Where(part => part.Length > 0)
                .Select(int.Parse)
                .ToList();
            if (numbers.Count < 2) return Array.Empty<string>();
            var supported = numbers[0] > 7 || (numbers[0] == 7 && numbers[1] >= 1);
            return supported
                ? new[] { "-allowed_segment_extensions", "ALL", "-extension_picky", "0" }
                : Array.Empty<string>();
        }
    }

    /// <summary>启动时调用：命中缓存立即使用；没有缓存才做完整检测。</summary>
    public async Task PrepareAsync()
    {
        if (!HasCache)
        {
            await RefreshAsync();
            return;
        }
        ValidateCachedPaths();
        if (_ffprobe == null && _ffmpeg != null)
        {
            await ProbeAsync(Tool.Ffprobe);
        }
    }

    /// <summary>完整检测并写入缓存（设置里的「重新检测」按钮）。</summary>
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var ytDlp = _locator.Locate("yt-dlp", _settings.YtDlpPath);
            var ffmpeg = _locator.Locate("ffmpeg", _settings.FfmpegPath);
            var ffprobe = _locator.LocateFfprobe(_settings.FfmpegPath, _settings.FfprobePath);

            var ytDlpVersion = ytDlp == null ? null : await _locator.VersionAsync(ytDlp, new[] { "--version" });
            var ffmpegRaw = ffmpeg == null ? null : await _locator.VersionAsync(ffmpeg, new[] { "-version" });
            var ffprobeRaw = ffprobe == null ? null : await _locator.VersionAsync(ffprobe, new[] { "-version" });

            _ytDlp = ytDlp;
            _ytDlpVersion = ytDlpVersion;
            _ffmpeg = ffmpeg;
            _ffmpegVersion = ffmpegRaw?.Replace("ffmpeg version ", "");
            _ffprobe = ffprobe;
            _ffprobeVersion = ffprobeRaw?.Replace("ffprobe version ", "");
            _checkedAt = DateTime.Now;
            InvalidationNotice = null;
            RaiseAll();
            Persist();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>设置里手填或选择路径后调用，只重新解析这一个工具并同步缓存。</summary>
    public async Task CustomPathChangedAsync(Tool tool)
    {
        await Task.Delay(400);
        await ProbeAsync(tool);
    }

    /// <summary>下载流程取可执行文件：缓存路径失效时自动尝试重新定位。</summary>
    public string? Executable(Tool tool)
    {
        var cached = PathOf(tool);
        if (cached != null && File.Exists(cached)) return cached;

        var relocated = tool == Tool.Ffprobe
            ? _locator.LocateFfprobe(_settings.FfmpegPath, _settings.FfprobePath)
            : _locator.Locate(NameOf(tool), CustomPathOf(tool));

        if (relocated != null)
        {
            Apply(tool, relocated, null);
            _checkedAt = DateTime.Now;
            InvalidationNotice = null;
            Persist();
            return relocated;
        }

        if (cached != null)
        {
            Invalidate(tool, "文件不存在或无法访问");
        }
        return null;
    }

    /// <summary>执行失败等场景下标记某个工具失效，界面据此提示重新检测。</summary>
    public void Invalidate(Tool tool, string reason)
    {
        Apply(tool, null, null);
        _checkedAt = DateTime.Now;
        InvalidationNotice = $"{DisplayName(tool)} 的路径已失效（{reason}），请在「设置 → 工具」里重新检测或重新指定路径。";
        Persist();
    }

    public static string DisplayName(Tool tool) => tool switch
    {
        Tool.YtDlp => "yt-dlp",
        Tool.Ffmpeg => "ffmpeg",
        _ => "ffprobe"
    };

    // MARK: - 内部

    private async Task ProbeAsync(Tool tool)
    {
        var path = tool == Tool.Ffprobe
            ? _locator.LocateFfprobe(_settings.FfmpegPath, _settings.FfprobePath)
            : _locator.Locate(NameOf(tool), CustomPathOf(tool));

        IsRefreshing = true;
        try
        {
            string? version = null;
            if (path != null)
            {
                var raw = await _locator.VersionAsync(path, tool == Tool.YtDlp ? new[] { "--version" } : new[] { "-version" });
                version = tool switch
                {
                    Tool.Ffmpeg => raw?.Replace("ffmpeg version ", ""),
                    Tool.Ffprobe => raw?.Replace("ffprobe version ", ""),
                    _ => raw
                };
            }
            Apply(tool, path, version);
            _checkedAt = DateTime.Now;
            InvalidationNotice = null;
            Persist();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>只做文件存在性检查，不启动任何子进程。</summary>
    private void ValidateCachedPaths()
    {
        if (_ytDlp != null && !File.Exists(_ytDlp)) Invalidate(Tool.YtDlp, "文件不存在或无法访问");
        if (_ffmpeg != null && !File.Exists(_ffmpeg)) Invalidate(Tool.Ffmpeg, "文件不存在或无法访问");
        if (_ffprobe != null && !File.Exists(_ffprobe)) Invalidate(Tool.Ffprobe, "文件不存在或无法访问");
    }

    private void Apply(Tool tool, string? path, string? version)
    {
        switch (tool)
        {
            case Tool.YtDlp:
                if (_ytDlp != path) _ytDlpVersion = version;
                _ytDlp = path;
                break;
            case Tool.Ffmpeg:
                if (_ffmpeg != path) _ffmpegVersion = version;
                _ffmpeg = path;
                break;
            case Tool.Ffprobe:
                if (_ffprobe != path) _ffprobeVersion = version;
                _ffprobe = path;
                break;
        }
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(YtDlp));
        OnPropertyChanged(nameof(YtDlpVersion));
        OnPropertyChanged(nameof(Ffmpeg));
        OnPropertyChanged(nameof(FfmpegVersion));
        OnPropertyChanged(nameof(Ffprobe));
        OnPropertyChanged(nameof(FfprobeVersion));
        OnPropertyChanged(nameof(CheckedAt));
        OnPropertyChanged(nameof(HasCache));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(FfprobeMissing));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HlsSegmentExtensionArguments));
    }

    private string? PathOf(Tool tool) => tool switch
    {
        Tool.YtDlp => _ytDlp,
        Tool.Ffmpeg => _ffmpeg,
        _ => _ffprobe
    };

    private string NameOf(Tool tool) => DisplayName(tool);

    private string CustomPathOf(Tool tool) => tool switch
    {
        Tool.YtDlp => _settings.YtDlpPath,
        Tool.Ffmpeg => _settings.FfmpegPath,
        _ => _settings.FfprobePath
    };

    private void RestoreFromCache()
    {
        var cache = _settings.ToolStatusCache;
        if (cache == null) return;
        _ytDlp = cache.YtDlp;
        _ytDlpVersion = cache.YtDlpVersion;
        _ffmpeg = cache.Ffmpeg;
        _ffmpegVersion = cache.FfmpegVersion;
        _ffprobe = cache.Ffprobe;
        _ffprobeVersion = cache.FfprobeVersion;
        _checkedAt = cache.CheckedAt;
    }

    private void Persist()
    {
        _settings.ToolStatusCache = new ToolStatusCache
        {
            YtDlp = _ytDlp,
            YtDlpVersion = _ytDlpVersion,
            Ffmpeg = _ffmpeg,
            FfmpegVersion = _ffmpegVersion,
            Ffprobe = _ffprobe,
            FfprobeVersion = _ffprobeVersion,
            CheckedAt = _checkedAt
        };
        _settings.Save();
    }
}
