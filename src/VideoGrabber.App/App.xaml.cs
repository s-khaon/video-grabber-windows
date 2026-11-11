using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoGrabber.App.Platform;
using VideoGrabber.App.ViewModels;
using VideoGrabber.App.Views;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

/// <summary>
/// 应用组合根：只在这里装配平台实现与 Core 服务，Core 不反向依赖 WPF。
/// </summary>
public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public WindowsPathProvider Paths { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public ToolEnvironment Tools { get; private set; } = null!;
    public TaskMetadataStore TaskStore { get; private set; } = null!;
    public WindowsProcessRunner Runner { get; private set; } = null!;
    public WindowsFilePicker FilePicker { get; private set; } = null!;
    public WindowsClipboardService ClipboardService { get; private set; } = null!;
    public CookieStore Cookies { get; private set; } = null!;
    public MediaSnifferStore Sniffer { get; private set; } = null!;
    public DownloadManager Downloads { get; private set; } = null!;
    public MediaConverter Converter { get; private set; } = null!;
    public MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>后台 WebView2 与内置浏览器共用同一个数据目录，Cookie / 登录态互通。</summary>
    public string BrowserDataFolder { get; private set; } = "";

    public AdBlockEngine? AdBlock { get; private set; }

    /// <summary>真正的退出（菜单 / 托盘）；关窗只是隐藏。</summary>
    public bool IsExiting { get; private set; }

    private TrayIcon? _tray;
    private MainWindow? _window;
    private BrowserWindow? _browserWindow;
    private CookieManagerWindow? _cookieWindow;
    private ConverterWindow? _converterWindow;
    private BrowserDataService? _browserData;
    private AutoTakeoverService? _autoTakeover;
    private Task<AdBlockEngine?>? _adBlockLoad;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Paths = new WindowsPathProvider();
        Settings = AppSettings.Load(Paths.SettingsDirectory, Paths.DownloadsDirectory);
        Tools = new ToolEnvironment(Paths, Settings);
        TaskStore = new TaskMetadataStore(Paths);
        Runner = new WindowsProcessRunner();
        FilePicker = new WindowsFilePicker();
        ClipboardService = new WindowsClipboardService();
        Cookies = new CookieStore(Paths);
        Sniffer = new MediaSnifferStore { IsEnabled = true };
        BrowserDataFolder = Path.Combine(Paths.DataDirectory, "BrowserData");
        Downloads = new DownloadManager(Settings, Tools, TaskStore, Runner, Cookies);
        _browserData = new BrowserDataService(BrowserDataFolder);
        _autoTakeover = new AutoTakeoverService(Downloads, Cookies, Settings, BrowserDataFolder);
        Converter = new MediaConverter(Runner, Settings, Tools, Cookies,
            action => Dispatcher.BeginInvoke(action, DispatcherPriority.Normal));

        Downloads.AutoTakeoverRequested += (job, reason) => _autoTakeover?.Start(job, reason);
        Downloads.TakeoverCancelRequested += job => _autoTakeover?.Cancel(job.Id);

        ViewModel = new MainViewModel(Downloads, Settings, Tools, FilePicker);
        ViewModel.AddDownloadRequested += ShowAddDownload;
        ViewModel.SettingsRequested += ShowSettings;
        ViewModel.RepairRequested += ShowRepair;
        ViewModel.BrowserRequested += () => ShowBrowser();
        ViewModel.CookieManagerRequested += ShowCookieManager;
        ViewModel.ConverterRequested += () => ShowConverter(null, null, null);
        ViewModel.BrowserForJobRequested += ShowBrowser;

        _window = new MainWindow { DataContext = ViewModel };
        _window.Closing += OnMainWindowClosing;
        _window.Show();

        _tray = new TrayIcon(ShowMainWindow, () => Downloads.CancelAll(), ExitApplication);

        // 启动时后台检测工具与广告规则，不阻塞界面。
        _ = PrepareToolsAsync();
        _ = EnsureAdBlockAsync();
    }

    public void ShowMainWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowAddDownload()
    {
        var window = new AddDownloadWindow(Downloads, Settings, FilePicker, ClipboardService, Cookies) { Owner = _window };
        window.ShowDialog();
    }

    public async void ShowSettings()
    {
        var window = new SettingsWindow(Settings, Tools) { Owner = _window };
        window.ShowDialog();
        Downloads.Reschedule();
        // 手动改了工具路径时立即重新检测，状态栏与下载参数随后生效。
        if (window.ToolPathsChanged)
        {
            await Tools.RefreshAsync();
            Downloads.Reschedule();
        }
    }

    public void ShowRepair()
    {
        var files = FilePicker.PickFiles(
            "修复本地视频文件",
            "视频文件|*.mp4;*.mkv;*.webm;*.mov;*.ts;*.m4v;*.m4a;*.mp3|所有文件|*.*");
        if (files.Count == 0) return;

        var service = new MediaRepairService(Settings, Tools, Runner);
        var window = new MediaRepairWindow(service, files) { Owner = _window };
        window.ShowDialog();
    }

    /// <summary>打开内置浏览器；传地址时直接导航（如接管失败后的「去验证」）。</summary>
    public async void ShowBrowser(string? initialUrl = null)
    {
        await EnsureAdBlockAsync();
        if (_browserWindow != null)
        {
            if (!string.IsNullOrWhiteSpace(initialUrl)) _browserWindow.NavigateTo(initialUrl!);
            if (_browserWindow.WindowState == WindowState.Minimized) _browserWindow.WindowState = WindowState.Normal;
            _browserWindow.Activate();
            return;
        }

        _browserWindow = new BrowserWindow(Sniffer, Cookies, Settings, BrowserDataFolder, Downloads, AdBlock)
        {
            Owner = _window,
            InitialURL = initialUrl
        };
        _browserWindow.Closed += (_, _) => _browserWindow = null;
        _browserWindow.Show();
    }

    public void ShowCookieManager()
    {
        if (_cookieWindow != null)
        {
            if (_cookieWindow.WindowState == WindowState.Minimized) _cookieWindow.WindowState = WindowState.Normal;
            _cookieWindow.Activate();
            return;
        }
        _cookieWindow = new CookieManagerWindow(Cookies, _browserData!) { Owner = _window };
        _cookieWindow.Closed += (_, _) => _cookieWindow = null;
        _cookieWindow.Show();
    }

    /// <summary>从资源嗅探打开转换器；已打开时把地址预填进去。</summary>
    public void ShowConverter(string? url, string? referer, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            Converter.Prefill(url!, referer, userAgent, referer);
        }

        if (_converterWindow != null)
        {
            if (!string.IsNullOrWhiteSpace(url)) _converterWindow.ApplyPrefill();
            if (_converterWindow.WindowState == WindowState.Minimized) _converterWindow.WindowState = WindowState.Normal;
            _converterWindow.Activate();
            return;
        }

        _converterWindow = new ConverterWindow(Converter, Settings, FilePicker) { Owner = _window };
        _converterWindow.Closed += (_, _) => _converterWindow = null;
        _converterWindow.Show();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        _tray?.Dispose();
        _autoTakeover?.CancelAll();
        if (_window != null) _window.Close();
        Shutdown();
    }

    private async Task PrepareToolsAsync()
    {
        try
        {
            await Tools.PrepareAsync();
        }
        finally
        {
            Downloads.Reschedule();
        }
    }

    private Task<AdBlockEngine?> EnsureAdBlockAsync()
    {
        if (AdBlock != null) return Task.FromResult<AdBlockEngine?>(AdBlock);
        return _adBlockLoad ??= LoadAdBlockAsync();
    }

    private async Task<AdBlockEngine?> LoadAdBlockAsync()
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "AdBlock");
            AdBlock = await AdBlockEngine.LoadAsync(
                Path.Combine(directory, "adblock-rules.json"),
                Path.Combine(directory, "cosmetic.json"),
                Path.Combine(directory, "meta.json"));
            return AdBlock;
        }
        catch (Exception)
        {
            // 规则缺失时浏览器仍然可用，只是不拦截广告。
            return null;
        }
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsExiting) return;
        e.Cancel = true;
        _window?.Hide();
    }
}
