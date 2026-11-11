using System.Collections.ObjectModel;
using System.Windows;
using VideoGrabber.App.Views;
using VideoGrabber.Core;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.ViewModels;

/// <summary>新增下载窗口：粘贴链接 → 解析 → 选清晰度 / 容器 → 入队。</summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    public sealed record ContainerOption(VideoContainer Value, string Label);

    private readonly DownloadManager _downloads;
    private readonly AppSettings _settings;
    private readonly CookieStore? _cookies;
    private readonly IFilePicker _filePicker;
    private readonly IClipboardService _clipboard;

    private string _links = "";
    private QualityChoice _selectedQuality = QualityChoice.Best;
    private ContainerOption _selectedContainer;
    private string _saveDirectory = "";
    private string _statusMessage = "";
    private bool _isProbing;
    private bool _hasProbe;
    private string _probeTitle = "";
    private string _probeMeta = "";
    private string? _thumbnailUrl;
    private bool _useCookies;

    public AddDownloadViewModel(
        DownloadManager downloads,
        AppSettings settings,
        IFilePicker filePicker,
        IClipboardService clipboard,
        CookieStore? cookies = null)
    {
        _downloads = downloads;
        _settings = settings;
        _cookies = cookies;
        _filePicker = filePicker;
        _clipboard = clipboard;
        _useCookies = settings.UseCookiesByDefault;

        SaveDirectory = string.IsNullOrWhiteSpace(settings.DefaultSaveDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.DefaultSaveDirectory;
        QualityOptions = new ObservableCollection<QualityChoice>(QualityCatalog.Build(null));
        _selectedQuality = QualityCatalog.Select(QualityOptions, settings.DefaultQualityId);
        ContainerOptions = new List<ContainerOption>
        {
            new(VideoContainer.Auto, "自动"),
            new(VideoContainer.Mp4, "MP4"),
            new(VideoContainer.Mkv, "MKV")
        };
        _selectedContainer = ContainerOptions[0];

        PasteCommand = new RelayCommand(PasteFromClipboard);
        ProbeCommand = new RelayCommand(() => _ = ProbeAsync());
        BrowseCommand = new RelayCommand(Browse);
        EnqueueCommand = new RelayCommand(Enqueue);
    }

    public string Links
    {
        get => _links;
        set
        {
            if (!SetProperty(ref _links, value)) return;
            OnPropertyChanged(nameof(CookieHint));
        }
    }

    /// <summary>是否复用内置浏览器导出的 cookie。</summary>
    public bool UseCookies
    {
        get => _useCookies;
        set
        {
            if (!SetProperty(ref _useCookies, value)) return;
            OnPropertyChanged(nameof(CookieHint));
        }
    }

    /// <summary>命中 / 缺失 cookie 的提示，与 macOS 版新增下载窗口一致。</summary>
    public string CookieHint
    {
        get
        {
            var first = FirstLink();
            if (first == null) return "粘贴链接后会显示命中的 Cookies 文件。";
            if (_cookies == null || !Uri.TryCreate(first, UriKind.Absolute, out var uri))
            {
                return "链接无效，无法匹配 Cookies。";
            }
            _cookies.Reload();
            var entry = _cookies.EntryFor(uri);
            if (entry == null)
            {
                return "没有找到该站点的 Cookies。遇到登录 / 验证时，可在「工具 → 打开内置浏览器」里完成验证并保存 Cookies。";
            }
            return $"命中 {entry.Host}.txt · {entry.CookieCount} 条 · 更新于 {entry.ModifiedAt:MM-dd HH:mm}";
        }
    }

    public ObservableCollection<QualityChoice> QualityOptions { get; }

    public QualityChoice SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!SetProperty(ref _selectedQuality, value)) return;
            OnPropertyChanged(nameof(IsAudioOnly));
            OnPropertyChanged(nameof(ContainerHint));
        }
    }

    public IReadOnlyList<ContainerOption> ContainerOptions { get; }

    public ContainerOption SelectedContainer
    {
        get => _selectedContainer;
        set => SetProperty(ref _selectedContainer, value);
    }

    public string SaveDirectory
    {
        get => _saveDirectory;
        set => SetProperty(ref _saveDirectory, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        set
        {
            if (!SetProperty(ref _isProbing, value)) return;
            OnPropertyChanged(nameof(IsNotProbing));
        }
    }

    public bool IsNotProbing => !IsProbing;

    public bool HasProbe
    {
        get => _hasProbe;
        set => SetProperty(ref _hasProbe, value);
    }

    public string ProbeTitle
    {
        get => _probeTitle;
        set => SetProperty(ref _probeTitle, value);
    }

    public string ProbeMeta
    {
        get => _probeMeta;
        set => SetProperty(ref _probeMeta, value);
    }

    public string? ThumbnailUrl
    {
        get => _thumbnailUrl;
        set => SetProperty(ref _thumbnailUrl, value);
    }

    public bool IsAudioOnly => SelectedQuality.IsAudioOnly;

    public string ContainerHint => IsAudioOnly
        ? "仅音频会转成 MP3（--audio-quality 0），容器格式不生效。"
        : "「自动」由 yt-dlp 决定容器；MP4 兼容性最好，MKV 能容纳更多编码。";

    public bool Enqueued { get; private set; }

    public RelayCommand PasteCommand { get; }
    public RelayCommand ProbeCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand EnqueueCommand { get; }

    private void PasteFromClipboard()
    {
        var text = _clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "剪贴板里没有文本。";
            return;
        }
        Links = text.Trimmed();
        StatusMessage = "";
    }

    private void Browse()
    {
        var picked = _filePicker.PickFolder(SaveDirectory, "选择保存目录");
        if (picked != null) SaveDirectory = picked;
    }

    private async Task ProbeAsync()
    {
        var first = FirstLink();
        if (first == null)
        {
            StatusMessage = "请先粘贴至少一条 http(s) 链接。";
            return;
        }

        IsProbing = true;
        StatusMessage = "正在解析…";
        try
        {
            var outcome = await _downloads.ProbeAsync(first);
            ProbeTitle = outcome.Title;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(outcome.Uploader)) parts.Add(outcome.Uploader!);
            if (YtDlpProbe.DurationText(outcome.Duration) is { } duration) parts.Add(duration);
            if (outcome.Heights.Count > 0) parts.Add($"最高 {outcome.Heights.Max()}p");
            ProbeMeta = string.Join(" · ", parts);
            ThumbnailUrl = outcome.Thumbnail;
            HasProbe = true;

            var preferred = SelectedQuality.Id;
            var options = QualityCatalog.Build(outcome.Heights);
            QualityOptions.Clear();
            foreach (var option in options) QualityOptions.Add(option);
            SelectedQuality = QualityCatalog.Select(options, preferred);
            StatusMessage = "解析成功，可以加入队列。";
        }
        catch (YtDlpProbe.ProbeException ex)
        {
            HasProbe = false;
            StatusMessage = ex.Message;
            if (ex.NeedsVerification)
            {
                StatusMessage += "\n该站点需要登录或人机验证：可在「工具 → 打开内置浏览器」完成验证并保存 Cookies。";
            }
        }
        catch (Exception ex)
        {
            HasProbe = false;
            StatusMessage = $"解析失败：{ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>入队；全部链接都不合法时返回 false，窗口保持打开。</summary>
    public bool TryEnqueue()
    {
        var lines = Links.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            StatusMessage = "请先粘贴至少一条链接。";
            return false;
        }

        var directory = SaveDirectory.Trimmed();
        if (directory.Length == 0)
        {
            StatusMessage = "请选择保存目录。";
            return false;
        }

        Guid? batch = lines.Count > 1 ? Guid.NewGuid() : null;
        var added = 0;
        foreach (var line in lines)
        {
            var job = _downloads.Enqueue(
                line,
                lines.Count == 1 && HasProbe ? ProbeTitle : null,
                SelectedQuality,
                SelectedContainer.Value,
                directory,
                UseCookies,
                batchID: batch);
            if (job != null) added++;
        }

        if (added == 0)
        {
            StatusMessage = "没有可用的链接（只接受 http / https 地址）。";
            return false;
        }

        _settings.DefaultSaveDirectory = directory;
        _settings.Save();
        Enqueued = true;
        return true;
    }

    private void Enqueue()
    {
        if (TryEnqueue()) CloseRequested?.Invoke();
    }

    public event Action? CloseRequested;

    private string? FirstLink() =>
        Links.Replace("\r", "").Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
}
