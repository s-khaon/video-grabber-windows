using System.Windows;
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
    public DownloadManager Downloads { get; private set; } = null!;
    public MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>真正的退出（菜单 / 托盘）；关窗只是隐藏。</summary>
    public bool IsExiting { get; private set; }

    private TrayIcon? _tray;
    private MainWindow? _window;

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
        Downloads = new DownloadManager(Settings, Tools, TaskStore, Runner);

        ViewModel = new MainViewModel(Downloads, Settings, Tools, FilePicker);
        ViewModel.AddDownloadRequested += ShowAddDownload;
        ViewModel.SettingsRequested += ShowSettings;

        _window = new MainWindow { DataContext = ViewModel };
        _window.Closing += OnMainWindowClosing;
        _window.Show();

        _tray = new TrayIcon(ShowMainWindow, ExitApplication);

        // 启动时后台检测工具，不阻塞界面。
        _ = PrepareToolsAsync();
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
        var window = new AddDownloadWindow(Downloads, Settings, FilePicker, ClipboardService) { Owner = _window };
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

    public void ExitApplication()
    {
        IsExiting = true;
        _tray?.Dispose();
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

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsExiting) return;
        e.Cancel = true;
        _window?.Hide();
    }
}
