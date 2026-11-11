using System.Windows;
using VideoGrabber.App.Platform;
using VideoGrabber.App.ViewModels;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(AppSettings settings, ToolEnvironment tools)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        var picker = ((App)Application.Current).FilePicker;
        _viewModel = new SettingsViewModel(settings, tools, picker);
        DataContext = _viewModel;
    }

    /// <summary>关闭后由 App 决定是否需要重新检测工具。</summary>
    public bool ToolPathsChanged => _viewModel.ToolPathsChanged;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Option_Changed(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OpenCookieManager_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ShowCookieManager();

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ShowBrowser();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Save();
        base.OnClosed(e);
    }
}
