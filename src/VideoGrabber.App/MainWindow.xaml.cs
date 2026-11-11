using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VideoGrabber.App.ViewModels;

namespace VideoGrabber.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>日志实时滚动到末尾。</summary>
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        // TextChanged 在布局更新前触发，延后一帧再滚动才能拿到新高度。
        box.Dispatcher.BeginInvoke(() => box.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenDefaultDownloadFolder();

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Video Grabber 调用本机的 yt-dlp 与 ffmpeg 下载网页视频。\n\n" +
            "首次使用前请确认已安装：\n" +
            "  winget install yt-dlp.yt-dlp\n" +
            "  winget install Gyan.FFmpeg\n\n" +
            "下载设置保存在 %APPDATA%\\VideoGrabber，任务与 Cookies 保存在 %LOCALAPPDATA%\\VideoGrabber。",
            "关于 Video Grabber",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
