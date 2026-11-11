using System.Collections.ObjectModel;
using VideoGrabber.App.Views;
using VideoGrabber.Core;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.ViewModels;

/// <summary>设置窗口：工具路径、下载默认值、代理。所有改动直接写入 AppSettings 并落盘。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IFilePicker _filePicker;
    private readonly string _originalYtDlpPath;
    private readonly string _originalFfmpegPath;
    private readonly string _originalFfprobePath;

    private QualityChoice _selectedDefaultQuality;
    private string _proxyTestResult = "";
    private bool _isTestingProxy;

    public SettingsViewModel(AppSettings settings, ToolEnvironment tools, IFilePicker filePicker)
    {
        Settings = settings;
        Tools = tools;
        _filePicker = filePicker;
        _originalYtDlpPath = settings.YtDlpPath;
        _originalFfmpegPath = settings.FfmpegPath;
        _originalFfprobePath = settings.FfprobePath;

        ConcurrencyOptions = new[] { 1, 2, 3, 4, 5, 6 };
        DefaultQualityOptions = QualityCatalog.Build(null);
        _selectedDefaultQuality = QualityCatalog.Select(DefaultQualityOptions, settings.DefaultQualityId);

        BrowseDownloadDirectoryCommand = new RelayCommand(BrowseDownloadDirectory);
        BrowseYtDlpCommand = new RelayCommand(() => PickExecutable(ToolEnvironment.Tool.YtDlp));
        BrowseFfmpegCommand = new RelayCommand(() => PickExecutable(ToolEnvironment.Tool.Ffmpeg));
        BrowseFfprobeCommand = new RelayCommand(() => PickExecutable(ToolEnvironment.Tool.Ffprobe));
        TestProxyCommand = new RelayCommand(() => _ = TestProxyAsync());
        RefreshToolsCommand = new RelayCommand(() => _ = RefreshToolsAsync());

        Whitelist = new ObservableCollection<string>(settings.AdBlockWhitelist.OrderBy(item => item));
        SniffHosts = new ObservableCollection<string>(settings.SniffOnlyHosts.OrderBy(item => item));
        AddWhitelistCommand = new RelayCommand(AddWhitelist);
        RemoveWhitelistCommand = new RelayCommand<string>(RemoveWhitelist);
        ClearWhitelistCommand = new RelayCommand(ClearWhitelist);
        RemoveSniffHostCommand = new RelayCommand<string>(RemoveSniffHost);
        ClearSniffHostsCommand = new RelayCommand(ClearSniffHosts);
    }

    public ObservableCollection<string> Whitelist { get; }
    public ObservableCollection<string> SniffHosts { get; }

    private string _whitelistInput = "";
    public string WhitelistInput
    {
        get => _whitelistInput;
        set => SetProperty(ref _whitelistInput, value);
    }

    public RelayCommand AddWhitelistCommand { get; }
    public RelayCommand<string> RemoveWhitelistCommand { get; }
    public RelayCommand ClearWhitelistCommand { get; }
    public RelayCommand<string> RemoveSniffHostCommand { get; }
    public RelayCommand ClearSniffHostsCommand { get; }

    private void AddWhitelist()
    {
        var value = WhitelistInput.Trim().ToLowerInvariant();
        if (value.Length == 0) return;
        if (value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal))
        {
            value = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;
        }
        if (Whitelist.Contains(value)) return;
        Whitelist.Add(value);
        Settings.AdBlockWhitelist = Whitelist.OrderBy(item => item).ToList();
        Settings.Save();
        WhitelistInput = "";
    }

    private void RemoveWhitelist(string? host)
    {
        if (string.IsNullOrEmpty(host)) return;
        Whitelist.Remove(host);
        Settings.AdBlockWhitelist = Whitelist.ToList();
        Settings.Save();
    }

    private void ClearWhitelist()
    {
        Whitelist.Clear();
        Settings.AdBlockWhitelist.Clear();
        Settings.Save();
    }

    private void RemoveSniffHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return;
        SniffHosts.Remove(host);
        Settings.SniffOnlyHosts = SniffHosts.ToList();
        Settings.Save();
    }

    private void ClearSniffHosts()
    {
        SniffHosts.Clear();
        Settings.SniffOnlyHosts.Clear();
        Settings.Save();
    }

    public AppSettings Settings { get; }
    public ToolEnvironment Tools { get; }

    public IReadOnlyList<int> ConcurrencyOptions { get; }
    public IReadOnlyList<QualityChoice> DefaultQualityOptions { get; }

    public QualityChoice SelectedDefaultQuality
    {
        get => _selectedDefaultQuality;
        set
        {
            if (!SetProperty(ref _selectedDefaultQuality, value)) return;
            Settings.DefaultQualityId = value.Id;
            Settings.Save();
        }
    }

    public string ProxyTestResult
    {
        get => _proxyTestResult;
        private set => SetProperty(ref _proxyTestResult, value);
    }

    public bool IsTestingProxy
    {
        get => _isTestingProxy;
        private set
        {
            if (!SetProperty(ref _isTestingProxy, value)) return;
            OnPropertyChanged(nameof(IsNotTestingProxy));
        }
    }

    public bool IsNotTestingProxy => !IsTestingProxy;

    /// <summary>窗口关闭后据此决定是否重新检测工具。</summary>
    public bool ToolPathsChanged =>
        Settings.YtDlpPath != _originalYtDlpPath
        || Settings.FfmpegPath != _originalFfmpegPath
        || Settings.FfprobePath != _originalFfprobePath;

    public RelayCommand BrowseDownloadDirectoryCommand { get; }
    public RelayCommand BrowseYtDlpCommand { get; }
    public RelayCommand BrowseFfmpegCommand { get; }
    public RelayCommand BrowseFfprobeCommand { get; }
    public RelayCommand TestProxyCommand { get; }
    public RelayCommand RefreshToolsCommand { get; }

    public void Save()
    {
        Settings.Save();
    }

    private void BrowseDownloadDirectory()
    {
        var picked = _filePicker.PickFolder(Settings.DefaultSaveDirectory, "选择默认保存目录");
        if (picked == null) return;
        Settings.DefaultSaveDirectory = picked;
        Settings.Save();
        OnPropertyChanged(nameof(Settings));
    }

    private void PickExecutable(ToolEnvironment.Tool tool)
    {
        var files = _filePicker.PickFiles(
            $"选择 {ToolEnvironment.DisplayName(tool)}",
            "可执行文件|*.exe|所有文件|*.*");
        if (files.Count == 0) return;
        var path = files[0];
        switch (tool)
        {
            case ToolEnvironment.Tool.YtDlp:
                Settings.YtDlpPath = path;
                break;
            case ToolEnvironment.Tool.Ffmpeg:
                Settings.FfmpegPath = path;
                break;
            default:
                Settings.FfprobePath = path;
                break;
        }
        Settings.Save();
        OnPropertyChanged(nameof(Settings));
    }

    private async Task TestProxyAsync()
    {
        IsTestingProxy = true;
        ProxyTestResult = "正在测试…";
        try
        {
            var result = await ProxyChecker.TestAsync(Settings);
            ProxyTestResult = result.Message;
        }
        finally
        {
            IsTestingProxy = false;
        }
    }

    private async Task RefreshToolsAsync()
    {
        await Tools.RefreshAsync();
    }
}
