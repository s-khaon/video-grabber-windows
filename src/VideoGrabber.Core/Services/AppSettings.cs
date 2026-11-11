using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 任务完成后的工具缓存（路径 / 版本 / 检测时间），与设置一起持久化。
/// </summary>
public sealed class ToolStatusCache
{
    public string? YtDlp { get; set; }
    public string? YtDlpVersion { get; set; }
    public string? Ffmpeg { get; set; }
    public string? FfmpegVersion { get; set; }
    public string? Ffprobe { get; set; }
    public string? FfprobeVersion { get; set; }
    public DateTime? CheckedAt { get; set; }
}

/// <summary>
/// 用户偏好。持久化到 %APPDATA%\VideoGrabber\settings.json（键名沿用 macOS 版）。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    [JsonIgnore] public string FilePath => _filePath;

    public string YtDlpPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    /// <summary>留空时按 ffmpeg 所在目录自动查找（通常与 ffmpeg 同目录）。</summary>
    public string FfprobePath { get; set; } = "";

    private int _maxConcurrentDownloads = 3;
    /// <summary>并发下载数，1–6。</summary>
    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set { _maxConcurrentDownloads = Math.Clamp(value, 1, 6); }
    }

    public string RateLimit { get; set; } = "";
    public bool ProxyEnabled { get; set; } = true;
    /// <summary>默认使用本机 Clash 的 HTTP 端口；all_proxy 会自动推导为 socks5 同端口。</summary>
    public string ProxyUrl { get; set; } = ProxyConfig.DefaultUrl;
    public string ExtraArguments { get; set; } = "";
    public string DefaultSaveDirectory { get; set; } = "";
    /// <summary>`best` / `h1440`… / `audio`。</summary>
    public string DefaultQualityId { get; set; } = "best";
    public bool UseCookiesByDefault { get; set; } = true;
    public bool AdBlockEnabled { get; set; } = true;
    public List<string> AdBlockWhitelist { get; set; } = new();
    public bool AutoTakeoverEnabled { get; set; } = true;
    public bool RememberSniffSites { get; set; } = true;
    public List<string> SniffOnlyHosts { get; set; } = new();
    public bool KeepIncompleteTempFiles { get; set; } = true;
    public ToolStatusCache? ToolStatusCache { get; set; }

    private AppSettings(string filePath) => _filePath = filePath;

    public static AppSettings Load(string settingsDirectory, string fallbackDownloadDirectory)
    {
        Directory.CreateDirectory(settingsDirectory);
        var path = Path.Combine(settingsDirectory, "settings.json");
        AppSettings settings;
        try
        {
            var json = File.Exists(path) ? File.ReadAllText(path) : null;
            settings = json == null
                ? new AppSettings(path)
                : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings(path);
        }
        catch (Exception)
        {
            // 配置损坏时退回默认值，不让应用无法启动。
            settings = new AppSettings(path);
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultSaveDirectory))
        {
            settings.DefaultSaveDirectory = fallbackDownloadDirectory;
        }
        settings.Save();
        return settings;
    }

    /// <summary>原子写入：先写临时文件再替换，避免异常退出留下半个 JSON。</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(this, JsonOptions);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception)
            {
                // 设置保存失败不应影响下载流程。
            }
        }
    }

    // MARK: - 站点嗅探策略

    /// <summary>该地址所在站点是否已被标记为「必须嗅探」。</summary>
    public bool RemembersSniffing(string urlString)
    {
        var host = UrlTools.HostOf(urlString)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return false;
        return SniffOnlyHosts.Any(entry =>
        {
            var value = entry.ToLowerInvariant();
            return host == value || host.EndsWith("." + value, StringComparison.Ordinal);
        });
    }

    public void RememberSniffHost(string host)
    {
        var value = host.ToLowerInvariant();
        if (value.StartsWith("www.")) value = value[4..];
        if (value.Length == 0 || SniffOnlyHosts.Contains(value)) return;
        SniffOnlyHosts.Add(value);
        Save();
    }

    public void ForgetSniffHost(string host)
    {
        SniffOnlyHosts.RemoveAll(item => item == host);
        Save();
    }

    public void ClearSniffHosts()
    {
        SniffOnlyHosts.Clear();
        Save();
    }
}
