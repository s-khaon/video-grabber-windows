using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VideoGrabber.App.Platform;
using VideoGrabber.App.ViewModels;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

/// <summary>
/// 内置浏览器：完成 Cloudflare 验证 / 登录、导出 Cookie，并做资源嗅探。
/// WebView2 提供与 WKWebView 对等的四件套：文档开始脚本、postMessage、Cookie 管理、请求拦截。
/// </summary>
public partial class BrowserWindow : Window
{
    private readonly MediaSnifferStore _sniffer;
    private readonly CookieStore _cookies;
    private readonly AppSettings _settings;
    private readonly DownloadManager _downloads;
    private readonly AdBlockEngine? _adblock;
    private readonly string _userDataFolder;
    private readonly BrowserViewModel _viewModel;

    private CoreWebView2? _core;
    private bool _initialized;
    private int _networkBlocked;
    private int _popupBlocked;
    private int _cosmeticApplied;
    private readonly DispatcherTimer _inspectionDebounce;
    private bool _cosmeticAppliedThisPage;

    /// <summary>documentStart 时向原生要对外观过滤 CSS，比导航完成后再注入更早隐藏广告位。</summary>
    private const string CosmeticRequestScript = """
        (function () {
          try {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'cosmetic', host: location.hostname }));
          } catch (e) { }
        })();
        """;

    public BrowserWindow(
        MediaSnifferStore sniffer,
        CookieStore cookies,
        AppSettings settings,
        string userDataFolder,
        DownloadManager downloads,
        AdBlockEngine? adblock)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _sniffer = sniffer;
        _cookies = cookies;
        _settings = settings;
        _downloads = downloads;
        _adblock = adblock;
        _userDataFolder = userDataFolder;
        _viewModel = new BrowserViewModel(sniffer, cookies, settings);
        DataContext = _viewModel;

        _inspectionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _inspectionDebounce.Tick += async (_, _) =>
        {
            _inspectionDebounce.Stop();
            await _viewModel.InspectPendingAsync();
        };

        Loaded += OnLoaded;
    }

    /// <summary>打开时直接加载的地址（菜单入口 / 接管失败后的「去验证」）。</summary>
    public string? InitialURL { get; set; }

    /// <summary>窗口已存在时从菜单 / 接管卡片跳转到新地址。</summary>
    public void NavigateTo(string address)
    {
        if (!_initialized)
        {
            InitialURL = address;
            return;
        }
        Navigate(address);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            _core = Browser.CoreWebView2;

            _core.Settings.AreDefaultContextMenusEnabled = true;
            _core.Settings.AreDevToolsEnabled = true;
            _core.Settings.IsStatusBarEnabled = false;
            _core.Settings.IsSwipeNavigationEnabled = true;

            // 真实浏览器 UA 直接沿用 WebView2 默认值，Cloudflare 验证通过率最高。
            _sniffer.UserAgent = _core.Settings.UserAgent;

            await _core.AddScriptToExecuteOnDocumentCreatedAsync(SnifferScript.Source);
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticRequestScript);
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.SourceChanged += OnSourceChanged;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnWebResourceRequested;

            if (!string.IsNullOrWhiteSpace(InitialURL))
            {
                Navigate(InitialURL!);
            }
            else
            {
                AddressBox.Focus();
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "无法启动内置浏览器：" + ex.Message;
            MessageBox.Show(
                this,
                "无法启动内置浏览器（WebView2 Runtime）。\n\n请先安装 Microsoft Edge WebView2 Runtime 后重试。\n\n" + ex.Message,
                "内置浏览器不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // MARK: - 导航

    private void Navigate(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return;
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.Host.Contains('.'))
        {
            _viewModel.StatusText = "地址无效：" + input.Trim();
            return;
        }
        _viewModel.Address = trimmed;
        _core?.Navigate(trimmed);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_core == null) return;
        _sniffer.NotePage(e.Uri);
        _cosmeticAppliedThisPage = false;
        PageProgress.Visibility = Visibility.Visible;
        UpdateNavigationButtons();
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (_core == null) return;
        _viewModel.Address = _core.Source;
        UpdateNavigationButtons();
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_core == null) return;
        PageProgress.Visibility = Visibility.Collapsed;
        _viewModel.Address = _core.Source;
        UpdateNavigationButtons();
        ShieldButton.IsChecked = AdBlockActiveForCurrentPage();

        if (!e.IsSuccess)
        {
            _viewModel.StatusText = "页面加载失败：" + e.WebErrorStatus;
            return;
        }

        _viewModel.StatusText = "";
        await ApplyCosmeticFilterAsync();
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _core?.CanGoBack == true;
        ForwardButton.IsEnabled = _core?.CanGoForward == true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 播放器 / 广告的弹窗不创建新窗口；广告计一次弹窗拦截。
        e.Handled = true;
        if (AdBlockActiveForCurrentPage())
        {
            _popupBlocked++;
            UpdateShieldText();
        }
        if (!string.IsNullOrEmpty(e.Uri) && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            _core?.Navigate(e.Uri);
        }
    }

    // MARK: - 嗅探

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            var messageType = typeElement.GetString();

            if (messageType == "cosmetic")
            {
                var host = root.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null;
                if (!string.IsNullOrEmpty(host)) await ApplyCosmeticForHostAsync(host!);
                return;
            }

            if (messageType != "media") return;
            if (!root.TryGetProperty("url", out var urlElement)) return;
            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(url)) return;
            var via = root.TryGetProperty("via", out var viaElement) ? viaElement.GetString() ?? "unknown" : "unknown";
            var page = root.TryGetProperty("page", out var pageElement) ? pageElement.GetString() : null;
            var ua = root.TryGetProperty("ua", out var uaElement) ? uaElement.GetString() : null;
            if (!string.IsNullOrEmpty(ua)) _sniffer.UserAgent = ua!;

            if (!_sniffer.Record(url!, via, page))
            {
                // 地址重复时也要重新解析：同一地址可能是主列表刚出现时解析失败过。
                if (_sniffer.Find(url!) == null) return;
            }
            _inspectionDebounce.Stop();
            _inspectionDebounce.Start();
        }
        catch (JsonException)
        {
            // 非本应用的 postMessage 忽略。
        }
    }

    // MARK: - Cookie

    private async void SaveCookies_Click(object sender, RoutedEventArgs e)
    {
        if (_core == null) return;
        var page = _core.Source;
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _viewModel.CookieStatus = "保存失败：页面还没有加载完成，先输入网址并回车。";
            return;
        }

        _viewModel.CookieStatus = $"正在导出 {uri.Host} 的 Cookie…";
        try
        {
            var raw = await _core.CookieManager.GetCookiesAsync(page);
            var result = _cookies.Write(CookieMapper.ToBrowserCookies(raw, uri), uri);
            _viewModel.CookieStatus = $"保存成功：{Path.GetFileNameWithoutExtension(result.FilePath)} 共 {result.Count} 条 Cookie · {result.FilePath}";
        }
        catch (CookieWriteException ex)
        {
            _viewModel.CookieStatus = "保存失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            _viewModel.CookieStatus = "保存失败：" + ex.Message;
        }
    }

    private async void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        if (_core == null) return;
        var page = _core.Source;
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _viewModel.CookieStatus = "没有可清理的页面，先输入网址并回车。";
            return;
        }

        _viewModel.CookieStatus = $"正在清理 {uri.Host} 的 Cookie 与本地存储…";
        try
        {
            var raw = await _core.CookieManager.GetCookiesAsync(page);
            foreach (var cookie in raw) _core.CookieManager.DeleteCookie(cookie);

            await _core.ExecuteScriptAsync("""
                (function () {
                  try { localStorage.clear(); } catch (e) { }
                  try { sessionStorage.clear(); } catch (e) { }
                  try {
                    if (indexedDB && indexedDB.databases) {
                      indexedDB.databases().then(function (list) {
                        (list || []).forEach(function (item) {
                          if (item && item.name) { try { indexedDB.deleteDatabase(item.name); } catch (e) { } }
                        });
                      }).catch(function () { });
                    }
                  } catch (e) { }
                })();
                """);

            var entry = _cookies.EntryFor(uri);
            var fileRemoved = entry == null || _cookies.Delete(entry);
            _viewModel.CookieStatus = fileRemoved
                ? $"已清理 {uri.Host} 的会话（浏览器 Cookie 与本地存储，导出文件已删除）。"
                : $"已清理 {uri.Host} 的浏览器数据，但导出的 cookie 文件删除失败，可在 Cookie 管理里重试。";
            _core.Reload();
        }
        catch (Exception ex)
        {
            _viewModel.CookieStatus = "清理失败：" + ex.Message;
        }
    }

    private static IEnumerable<BrowserCookie> ToBrowserCookies(
        IEnumerable<CoreWebView2Cookie> cookies,
        Uri page) => CookieMapper.ToBrowserCookies(cookies, page);

    /// <summary>下载前把页面与媒体域名的 Cookie 都落盘，避免用户忘记保存导致 403。</summary>
    private async Task SyncCookiesAsync(string? pageUrl, string? mediaUrl)
    {
        if (_core == null) return;
        var page = string.IsNullOrEmpty(pageUrl) ? _core.Source : pageUrl!;
        if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || pageUri.Scheme is not ("http" or "https")) return;
        try
        {
            var raw = await _core.CookieManager.GetCookiesAsync(page);
            _cookies.Write(ToBrowserCookies(raw, pageUri), pageUri);

            if (!string.IsNullOrEmpty(mediaUrl)
                && Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri)
                && mediaUri.Scheme is "http" or "https"
                && !string.Equals(mediaUri.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                var mediaCookies = await _core.CookieManager.GetCookiesAsync(mediaUrl);
                if (mediaCookies.Count > 0)
                {
                    _cookies.Write(ToBrowserCookies(mediaCookies, mediaUri), mediaUri);
                }
            }
        }
        catch (Exception)
        {
            // Cookie 导出失败不阻断下载；任务仍会带 Referer / UA。
        }
    }

    // MARK: - 广告拦截

    private bool AdBlockActiveForCurrentPage()
    {
        if (_adblock == null || !_settings.AdBlockEnabled) return false;
        if (_core == null) return false;
        var host = HostOf(_core.Source);
        return string.IsNullOrEmpty(host) || !IsWhitelisted(host);
    }

    private bool AdBlockActiveForHost(string host)
    {
        if (_adblock == null || !_settings.AdBlockEnabled) return false;
        return host.Length == 0 || !IsWhitelisted(host);
    }

    private bool IsWhitelisted(string host)
    {
        var normalized = host.ToLowerInvariant();
        return _settings.AdBlockWhitelist.Any(entry =>
        {
            var value = entry.ToLowerInvariant();
            if (value.Length == 0) return false;
            return normalized == value
                   || normalized.EndsWith("." + value, StringComparison.Ordinal)
                   || value.EndsWith("." + normalized, StringComparison.Ordinal);
        });
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) ? uri.Host : "";

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core == null || _adblock == null || !AdBlockActiveForCurrentPage()) return;
        var requestUrl = e.Request.Uri;
        if (ShouldAlwaysAllow(requestUrl)) return;

        var documentUri = Uri.TryCreate(_core.Source, UriKind.Absolute, out var document)
            ? document
            : new Uri("https://" + HostOf(requestUrl));
        var thirdParty = Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)
                         && AdBlockEngine.IsThirdParty(documentUri, request);

        if (!_adblock.ShouldBlock(documentUri, requestUrl, ResourceTypeName(e.ResourceContext), thirdParty)) return;

        try
        {
            e.Response = _core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            _networkBlocked++;
            UpdateShieldText();
        }
        catch (Exception)
        {
            // 拦截失败时放行请求，不影响页面。
        }
    }

    /// <summary>Cloudflare 验证与媒体资源永远放行，避免误拦导致验证过不去或视频下不了。</summary>
    private static bool ShouldAlwaysAllow(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("challenges.cloudflare.com")
            || lower.Contains("turnstile.cloudflare.com")
            || lower.Contains("cloudflareclient.com")
            || lower.Contains("/cdn-cgi/challenge-platform/"))
        {
            return true;
        }
        var path = lower.Split('?')[0].Split('#')[0];
        var extensions = new[] { ".m3u8", ".mpd", ".mp4", ".m4s", ".ts", ".webm", ".m4a", ".mp3" };
        return extensions.Any(path.EndsWith);
    }

    private static string ResourceTypeName(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => "document",
        CoreWebView2WebResourceContext.Stylesheet => "style-sheet",
        CoreWebView2WebResourceContext.Image => "image",
        CoreWebView2WebResourceContext.Media => "media",
        CoreWebView2WebResourceContext.Font => "font",
        CoreWebView2WebResourceContext.Script => "script",
        CoreWebView2WebResourceContext.XmlHttpRequest => "raw",
        CoreWebView2WebResourceContext.Fetch => "raw",
        _ => "other"
    };

    private Task ApplyCosmeticFilterAsync() =>
        _core == null ? Task.CompletedTask : ApplyCosmeticForHostAsync(HostOf(_core.Source));

    private async Task ApplyCosmeticForHostAsync(string host)
    {
        if (_core == null || _adblock == null || !AdBlockActiveForHost(host)) return;
        var css = _adblock.CssFor(host);
        if (css.Length == 0) return;
        var json = JsonSerializer.Serialize(css);
        try
        {
            await _core.ExecuteScriptAsync($$"""
                (function () {
                  try {
                    var id = '__vg_adblock_style';
                    var style = document.getElementById(id);
                    if (!style) {
                      style = document.createElement('style');
                      style.id = id;
                      (document.head || document.documentElement).appendChild(style);
                    }
                    style.textContent = {{json}};
                  } catch (e) { }
                })();
                """);
            if (_cosmeticAppliedThisPage) return;
            _cosmeticAppliedThisPage = true;
            _cosmeticApplied++;
            UpdateShieldText();
        }
        catch (Exception)
        {
            // 页面未就绪时忽略；documentStart 与导航完成各注入一次，后者兜底。
        }
    }

    private void UpdateShieldText()
    {
        var total = _networkBlocked + _popupBlocked + _cosmeticApplied;
        _viewModel.ShieldText = $"拦截 {total}";
        _viewModel.ShieldCountText = total.ToString();
        _viewModel.StatusText = total > 0
            ? $"本次会话已拦截 {total} 项（请求 {_networkBlocked} · 弹窗 {_popupBlocked} · 外观 {_cosmeticApplied}）"
            : _viewModel.StatusText;
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        var host = _core == null ? "" : HostOf(_core.Source);
        if (host.Length == 0)
        {
            ShieldButton.IsChecked = false;
            return;
        }

        if (IsWhitelisted(host))
        {
            _settings.AdBlockWhitelist.RemoveAll(entry =>
                string.Equals(entry, host, StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase));
            _viewModel.StatusText = $"已对 {host} 重新启用广告拦截。";
        }
        else
        {
            _settings.AdBlockWhitelist.Add(host);
            _viewModel.StatusText = $"已对 {host} 关闭广告拦截，页面会按原样加载。";
        }
        _settings.Save();
        _core?.Reload();
    }

    private void ToggleSniffer_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = SnifferColumn.Width.Value < 1;
        SnifferColumn.Width = collapsed ? new GridLength(392) : new GridLength(0);
        SnifferPanel.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SnifferToolbarButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: - 列表行操作

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var url = row.ActiveUrl;
        if (url.Length == 0) return;
        try
        {
            Clipboard.SetText(url);
            _viewModel.StatusText = row.SelectedVariant == null
                ? "地址已复制到剪贴板。"
                : $"已复制当前档位（{row.SelectedVariant.Label}）地址。";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "复制失败：" + ex.Message;
        }
    }

    private async void DownloadYtDlp_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Resource: { } resource } row) return;
        await EnqueueAsync(resource, DownloadEngine.YtDlp, row.ActiveUrl);
    }

    private async void DownloadDirect_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Resource: { } resource } row) return;
        await EnqueueAsync(resource, DownloadEngine.FfmpegDirect, row.ActiveUrl);
    }

    private async void BlobDownload_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Resource: { } resource }) return;
        if (string.IsNullOrEmpty(resource.RelatedSource))
        {
            _viewModel.StatusText = "这条 blob 还没有关联到 m3u8，先在页面里播放视频再试。";
            return;
        }
        await EnqueueAsync(resource, DownloadEngine.FfmpegDirect, resource.RelatedSource);
    }

    private async void VariantDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HlsVariantRow variant) return;
        var page = _core?.Source;
        await SyncCookiesAsync(page, variant.UrlString);
        var job = _downloads.Enqueue(
            variant.UrlString,
            variant.Label,
            QualityChoice.Best,
            VideoContainer.Auto,
            _settings.DefaultSaveDirectory,
            useCookies: true,
            engine: DownloadEngine.FfmpegDirect,
            referer: page,
            userAgent: _sniffer.UserAgent,
            pageURL: page);
        ReportEnqueue(job, "HLS 档位");
        e.Handled = true;
    }

    private async Task EnqueueAsync(MediaResource resource, DownloadEngine engine, string? overrideUrl = null)
    {
        var url = string.IsNullOrEmpty(overrideUrl) ? resource.UrlString : overrideUrl!;
        var page = string.IsNullOrEmpty(resource.PageURL) ? _core?.Source : resource.PageURL;
        await SyncCookiesAsync(page, url);
        var job = _downloads.Enqueue(
            url,
            resource.SuggestedTitle,
            QualityChoice.Best,
            VideoContainer.Auto,
            _settings.DefaultSaveDirectory,
            useCookies: true,
            engine: engine,
            referer: page,
            userAgent: string.IsNullOrEmpty(resource.UserAgent) ? _sniffer.UserAgent : resource.UserAgent,
            pageURL: page);
        ReportEnqueue(job, engine == DownloadEngine.YtDlp ? "yt-dlp" : "ffmpeg 直连");
    }

    private void ReportEnqueue(DownloadJob? job, string engine)
    {
        if (job == null)
        {
            _viewModel.StatusText = "无法加入下载队列：地址格式不正确。";
            return;
        }
        _viewModel.StatusText = $"已加入下载队列（{engine}），进度可在主窗口查看。";
    }

    private async void Reinspect_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Resource: { } resource }) return;
        await _viewModel.ReinspectAsync(resource, force: true);
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Resource: { } resource } row) return;
        if (!row.CanConvert)
        {
            _viewModel.StatusText = "只有 .m3u8 / mp4 地址可以转换为 mp4。";
            return;
        }
        var page = string.IsNullOrEmpty(resource.PageURL) ? _core?.Source : resource.PageURL;
        await SyncCookiesAsync(page, row.ActiveUrl);
        App.Current.ShowConverter(row.ActiveUrl, page, _sniffer.UserAgent);
    }

    private void ClearSniff_Click(object sender, RoutedEventArgs e)
    {
        _sniffer.Clear();
        _viewModel.StatusText = "已清空本次嗅探记录。";
    }

    private static SniffRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SniffRow;

    // MARK: - 工具栏

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_core?.CanGoBack == true) _core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_core?.CanGoForward == true) _core.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _core?.Reload();

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(_viewModel.Address);

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigate(AddressBox.Text);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _inspectionDebounce.Stop();
        base.OnClosed(e);
    }
}
