using System.Diagnostics;
using System.Windows;
using VideoGrabber.App.Platform;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

public partial class CookieManagerWindow : Window
{
    private readonly CookieStore _cookies;
    private readonly BrowserDataService _browserData;

    public CookieManagerWindow(CookieStore cookies, BrowserDataService browserData)
    {
        InitializeComponent();
        _cookies = cookies;
        _browserData = browserData;
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        _cookies.Reload();
        EntryList.ItemsSource = null;
        EntryList.ItemsSource = _cookies.Entries;
        EntryList.Visibility = _cookies.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = _cookies.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"共 {_cookies.Entries.Count} 个站点 · {_cookies.Directory}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _cookies.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开文件夹失败：" + ex.Message;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not CookieEntry entry)
        {
            StatusText.Text = "先在列表里选中要删除的站点。";
            return;
        }
        var answer = MessageBox.Show(
            this,
            $"删除 {entry.Host} 的 Cookie？\n\n同时会清理内置浏览器里该站点的 Cookie、localStorage 与 IndexedDB，已登录状态会被退出。",
            "删除 Cookie",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        StatusText.Text = $"正在删除 {entry.Host}…";
        var fileRemoved = _cookies.Delete(entry);
        try
        {
            await _browserData.ClearSiteAsync(entry.Host);
        }
        catch (Exception ex)
        {
            StatusText.Text = (fileRemoved ? "Cookie 文件已删除，" : "Cookie 文件删除失败，")
                              + "浏览器站点数据清理出错：" + ex.Message;
            RefreshList();
            return;
        }
        StatusText.Text = fileRemoved
            ? $"已删除 {entry.Host} 的 Cookie 与浏览器站点数据。"
            : $"已清理 {entry.Host} 的浏览器站点数据，但 cookie 文件删除失败。";
        RefreshList();
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_cookies.Entries.Count == 0)
        {
            StatusText.Text = "没有可清空的 Cookie。";
            return;
        }
        var answer = MessageBox.Show(
            this,
            "清空全部 Cookie？\n\n会删除所有站点的 cookie 文件，并清理内置浏览器的 Cookie、localStorage 与 IndexedDB。此操作不可撤销。",
            "清空全部 Cookie",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        StatusText.Text = "正在清空…";
        var result = _cookies.DeleteAll();
        try
        {
            // WebView2 清理全部站点数据偶尔会卡住，15 秒兜底避免窗口一直停在「正在清空」。
            var cleanup = _browserData.ClearAllAsync();
            var finished = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(15)));
            if (finished != cleanup)
            {
                StatusText.Text = $"已删除 {result.Removed} 个 cookie 文件，浏览器站点数据清理超时（15 秒），可稍后重试。";
                RefreshList();
                return;
            }
            await cleanup;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"已删除 {result.Removed} 个 cookie 文件，浏览器数据清理出错：{ex.Message}";
            RefreshList();
            return;
        }
        StatusText.Text = result.Failures.Count == 0
            ? $"已清空 {result.Removed} 个站点的 Cookie 与浏览器站点数据。"
            : $"已清空 {result.Removed} 个站点，{result.Failures.Count} 个文件删除失败。";
        RefreshList();
    }
}
