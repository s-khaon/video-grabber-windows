using VideoGrabber.Core.Services;

namespace VideoGrabber.App.Platform;

/// <summary>
/// 浏览器数据的后台入口：站点数据清理 / 全局清理共用同一个隐藏 WebView2，
/// 这样与内置浏览器使用同一 cookie 库，不会出现「文件删了浏览器还带着登录态」。
/// </summary>
public sealed class BrowserDataService : IAsyncDisposable
{
    private readonly string _userDataFolder;
    private HiddenBrowser? _browser;

    public BrowserDataService(string userDataFolder) => _userDataFolder = userDataFolder;

    private async Task<HiddenBrowser> GetBrowserAsync()
    {
        if (_browser == null)
        {
            _browser = new HiddenBrowser(_userDataFolder);
            await _browser.InitializeAsync();
        }
        return _browser;
    }

    public async Task ClearSiteAsync(string host)
    {
        var browser = await GetBrowserAsync();
        await browser.ClearSiteDataAsync(host);
    }

    public async Task ClearAllAsync()
    {
        var browser = await GetBrowserAsync();
        await browser.ClearAllSiteDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }
    }
}
