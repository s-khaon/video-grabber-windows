using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.Platform;

/// <summary>后台页面的状态摘要：用于判断 Cloudflare 验证 / 登录页。</summary>
public sealed class PageProbe
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Text { get; init; } = "";
    public bool PasswordField { get; init; }
    public int VideoCount { get; init; }
    public bool ChallengeFlag { get; init; }

    public bool IsEmpty => Title.Length == 0 && Text.Length == 0 && Url.Length == 0;

    public bool IsChallenge
    {
        get
        {
            if (ChallengeFlag) return true;
            var lower = Text.ToLowerInvariant();
            var markers = new[]
            {
                "just a moment", "checking your browser", "verify you are human",
                "attention required", "enable javascript and cookies",
                "人机验证", "安全验证", "请稍候", "正在验证"
            };
            return markers.Any(lower.Contains);
        }
    }

    public bool IsLogin
    {
        get
        {
            if (PasswordField && VideoCount == 0) return true;
            var lowerUrl = Url.ToLowerInvariant();
            var loginPaths = new[] { "/login", "/signin", "/sign-in", "/account/login", "/passport/" };
            if (loginPaths.Any(lowerUrl.Contains)) return true;
            var lower = Text.ToLowerInvariant();
            var markers = new[] { "sign in to continue", "请先登录", "登录后继续", "log in to continue" };
            return VideoCount == 0 && markers.Any(lower.Contains);
        }
    }
}

/// <summary>
/// 隐藏的 WebView2：屏幕外窗口、不抢焦点，用注册表外的独立数据目录与可见浏览器共享 Cookie。
/// 自动接管与站点数据清理都复用它。
/// </summary>
public sealed class HiddenBrowser : IAsyncDisposable
{
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required";

    private readonly string _userDataFolder;
    private Window? _window;
    private WebView2? _webView;
    private CoreWebView2? _core;
    private TaskCompletionSource<bool>? _loadCompletion;
    private bool _disposed;

    public HiddenBrowser(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        Sniffer = new MediaSnifferStore();
    }

    public MediaSnifferStore Sniffer { get; }
    public string UserAgent { get; private set; } = "";
    public string? LastError { get; private set; }
    public string? CurrentUrl => _core?.Source;

    private static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>必须在 UI 线程调用；创建屏幕外窗口并注入嗅探脚本。</summary>
    public async Task InitializeAsync()
    {
        if (_core != null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
                Width = 1280,
                Height = 800,
                Left = -32000,
                Top = -32000
            };
            _webView = new WebView2();
            _window.Content = _webView;
            _window.Show();
        });

        Directory.CreateDirectory(_userDataFolder);
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
        var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
        await _webView!.EnsureCoreWebView2Async(environment);
        _core = _webView.CoreWebView2;
        _core.Settings.AreDefaultScriptDialogsEnabled = false;
        _core.Settings.AreDevToolsEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        UserAgent = _core.Settings.UserAgent;
        Sniffer.UserAgent = UserAgent;

        await _core.AddScriptToExecuteOnDocumentCreatedAsync(SnifferScript.Source);
        _core.WebMessageReceived += OnWebMessageReceived;
        _core.NavigationStarting += (_, args) => Sniffer.NotePage(args.Uri);
        _core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                LastError = "页面加载失败：" + args.WebErrorStatus;
            }
            _loadCompletion?.TrySetResult(args.IsSuccess || !string.IsNullOrEmpty(_core?.Source));
            _loadCompletion = null;
        };
        _core.NewWindowRequested += (_, args) => args.Handled = true;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "media") return;
            if (!root.TryGetProperty("url", out var urlElement)) return;
            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(url)) return;
            var via = root.TryGetProperty("via", out var viaElement) ? viaElement.GetString() ?? "unknown" : "unknown";
            var page = root.TryGetProperty("page", out var pageElement) ? pageElement.GetString() : null;
            Sniffer.Record(url!, via, page);
        }
        catch (JsonException)
        {
            // 忽略页面自己的 postMessage。
        }
    }

    /// <summary>加载页面；超时或失败返回 false。</summary>
    public async Task<bool> LoadAsync(string address, int timeoutSeconds)
    {
        if (_core == null) await InitializeAsync();
        if (_core == null || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            LastError = "地址无效";
            return false;
        }

        _loadCompletion?.TrySetResult(false);
        LastError = null;
        Sniffer.NotePage(address);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadCompletion = completion;
        try
        {
            _core.Navigate(address);
            var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completed != completion.Task)
            {
                LastError = "加载超时";
                return false;
            }
            return completion.Task.Result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task RefreshUserAgentAsync()
    {
        var value = await EvaluateStringAsync("navigator.userAgent");
        if (!string.IsNullOrEmpty(value))
        {
            UserAgent = value!;
            Sniffer.UserAgent = UserAgent;
        }
    }

    public async Task KickPlaybackAsync()
    {
        await EvaluateAsync("window.__vgNudgePlayback ? (window.__vgNudgePlayback(), 'ok') : 'missing'");
    }

    public async Task ScrollPageAsync()
    {
        await EvaluateAsync("(function(){try{window.scrollBy(0, Math.round((window.innerHeight||700)*0.7));}catch(e){}})()");
    }

    public IReadOnlyList<MediaResource> Candidates()
    {
        return Sniffer.Resources
            .Where(resource => resource.Kind is MediaResourceKind.Hls or MediaResourceKind.Mp4
                or MediaResourceKind.Webm or MediaResourceKind.Audio
                || (resource.IsBlob && resource.RelatedSource != null))
            .ToList();
    }

    public async Task<PageProbe> ProbeAsync()
    {
        const string script = """
            (function () {
              var text = '';
              try { text = (document.body ? document.body.innerText : '').slice(0, 4000); } catch (e) { }
              var password = false;
              try {
                var fields = document.querySelectorAll('input[type=password]');
                for (var i = 0; i < fields.length; i++) {
                  var rect = fields[i].getBoundingClientRect();
                  if (rect.width > 4 && rect.height > 4) { password = true; break; }
                }
              } catch (e) { }
              var challenge = false;
              try {
                challenge = !!document.querySelector('#challenge-form, #challenge-running, #cf-challenge-running, iframe[src*="challenges.cloudflare.com"], iframe[src*="turnstile"]');
              } catch (e) { }
              var videos = 0;
              try { videos = document.querySelectorAll('video').length; } catch (e) { }
              return {
                title: document.title || '',
                url: location.href || '',
                text: text,
                password: password,
                videos: videos,
                challenge: challenge
              };
            })();
            """;
        var json = await EvaluateStringAsync(script);
        if (string.IsNullOrEmpty(json)) return new PageProbe();
        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            return new PageProbe
            {
                Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                Url = root.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                PasswordField = root.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True,
                VideoCount = root.TryGetProperty("videos", out var videos) && videos.TryGetInt32(out var count) ? count : 0,
                ChallengeFlag = root.TryGetProperty("challenge", out var challenge) && challenge.ValueKind == JsonValueKind.True
            };
        }
        catch (JsonException)
        {
            return new PageProbe();
        }
    }

    /// <summary>按地址读出 WebView2 共享 Cookie（同一数据目录的页面与媒体域名）。</summary>
    public async Task<List<BrowserCookie>> GetCookiesAsync(IEnumerable<string> urls)
    {
        var result = new List<BrowserCookie>();
        if (_core == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var urlString in urls.Append(CurrentUrl ?? ""))
        {
            if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(uri.GetLeftPart(UriPartial.Authority))) continue;
            try
            {
                var raw = await _core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
                result.AddRange(CookieMapper.ToBrowserCookies(raw, uri));
            }
            catch (Exception)
            {
                // 单个域名失败不影响其它域名。
            }
        }
        return result;
    }

    /// <summary>删除某个站点的 Cookie，并清空 localStorage / sessionStorage / IndexedDB。</summary>
    public async Task ClearSiteDataAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        if (_core == null) await InitializeAsync();
        if (_core == null) return;

        var root = "https://" + host.Trim() + "/";
        try
        {
            var cookies = await _core.CookieManager.GetCookiesAsync(root);
            foreach (var cookie in cookies) _core.CookieManager.DeleteCookie(cookie);
        }
        catch (Exception)
        {
            // Cookie 删除失败也继续清本地存储。
        }

        await LoadAsync(root, 12);
        await ClearStorageAsync();
    }

    public async Task ClearStorageAsync()
    {
        await EvaluateAsync("""
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
    }

    /// <summary>清空本机 WebView2 的站点数据（Cookie / 本地存储 / 缓存），与「清空全部 Cookie」配套。</summary>
    public async Task ClearAllSiteDataAsync()
    {
        if (_core == null) await InitializeAsync();
        if (_core == null) return;
        try
        {
            await _core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.DiskCache);
        }
        catch (Exception)
        {
            // 旧版 Runtime 不支持时，至少删除 Cookie。
            _core.CookieManager.DeleteAllCookies();
        }
    }

    private async Task EvaluateAsync(string script)
    {
        if (_core == null) return;
        try
        {
            await _core.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // 页面未就绪时忽略。
        }
    }

    /// <summary>执行脚本并返回 JSON 文本（对象 / 字符串都会被序列化为 JSON）。</summary>
    private async Task<string?> EvaluateStringAsync(string script)
    {
        if (_core == null) return null;
        try
        {
            var raw = await _core.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            // ExecuteScriptAsync 对字符串结果返回带引号的 JSON，这里统一还原。
            if (raw.StartsWith('"'))
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            return raw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCompletion?.TrySetResult(false);
        _loadCompletion = null;
        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _webView?.Dispose();
                _window?.Close();
            }
            catch (Exception)
            {
                // 关闭失败不影响任务收尾。
            }
        });
        _core = null;
        _webView = null;
        _window = null;
        Sniffer.Clear();
    }
}
