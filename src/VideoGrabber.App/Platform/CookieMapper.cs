using Microsoft.Web.WebView2.Core;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.Platform;

/// <summary>WebView2 Cookie → CookieStore 的中立结构。</summary>
internal static class CookieMapper
{
    public static IEnumerable<BrowserCookie> ToBrowserCookies(
        IEnumerable<CoreWebView2Cookie> cookies,
        Uri page)
    {
        foreach (var cookie in cookies)
        {
            DateTime? expires = cookie.Expires == default(DateTime) ? null : cookie.Expires;
            yield return new BrowserCookie(
                cookie.Name ?? "",
                cookie.Value ?? "",
                string.IsNullOrEmpty(cookie.Domain) ? page.Host : cookie.Domain,
                cookie.Path ?? "/",
                cookie.IsSecure,
                cookie.IsHttpOnly,
                expires);
        }
    }
}
