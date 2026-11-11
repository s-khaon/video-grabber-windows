using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>
/// 资源嗅探的数据层：去重、400 上限、页面切换清空、blob 关联最近 m3u8、
/// 播放列表解析结果与直链体积缓存。UI 只读这里的数据。
/// </summary>
public sealed class MediaSnifferStore
{
    public const int Limit = 400;

    public static string DefaultUserAgent { get; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/124.0.0.0 Safari/537.36";

    private readonly object _gate = new();
    private readonly List<MediaResource> _resources = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HlsPlaylistInfo> _playlistInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _contentLengths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inspecting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inspected = new(StringComparer.Ordinal);
    private string _pageHost = "";

    /// <summary>数据变化（新增、清空、解析完成）时触发，UI 负责回到自己的线程刷新。</summary>
    public event Action? Changed;

    public string PageURL { get; private set; } = "";
    public string UserAgent { get; set; } = DefaultUserAgent;
    /// <summary>关闭后不再记录，用于在验证页面上临时静音。</summary>
    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<MediaResource> Resources
    {
        get
        {
            lock (_gate) return _resources.ToList();
        }
    }

    public IReadOnlyList<string> InspectingURLs
    {
        get
        {
            lock (_gate) return _inspecting.ToList();
        }
    }

    public HlsPlaylistInfo? PlaylistInfo(string urlString)
    {
        lock (_gate)
        {
            if (_playlistInfos.TryGetValue(urlString, out var exact)) return exact;
            var key = MediaResource.CanonicalKey(urlString);
            foreach (var (url, info) in _playlistInfos)
            {
                if (MediaResource.CanonicalKey(url) == key) return info;
            }
            return null;
        }
    }

    public long? ContentLength(string urlString)
    {
        lock (_gate)
        {
            if (_contentLengths.TryGetValue(urlString, out var exact)) return exact;
            var key = MediaResource.CanonicalKey(urlString);
            foreach (var (url, size) in _contentLengths)
            {
                if (MediaResource.CanonicalKey(url) == key) return size;
            }
            return null;
        }
    }

    /// <summary>页面主域变化时清空，避免不同站点的地址混在一起。</summary>
    public void NotePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var host = uri.Host.ToLowerInvariant();
        var clear = false;
        lock (_gate)
        {
            PageURL = url;
            if (_pageHost.Length > 0 && host.Length > 0 && !string.Equals(host, _pageHost, StringComparison.OrdinalIgnoreCase))
            {
                clear = true;
            }
            _pageHost = host;
        }
        if (clear) Clear();
    }

    /// <summary>记录一条资源；重复地址、非媒体地址会被忽略，超过 400 条时丢弃最旧的。</summary>
    public bool Record(string urlString, string via, string? pageURL = null)
    {
        if (!IsEnabled) return false;
        var trimmed = urlString.Trimmed();
        if (trimmed.Length == 0) return false;
        if (!trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            && !MediaResource.LooksLikeMedia(trimmed))
        {
            return false;
        }

        MediaResource resource;
        lock (_gate)
        {
            if (_seen.Contains(trimmed)) return false;
            _seen.Add(trimmed);

            var kind = MediaResource.Classify(trimmed);
            resource = new MediaResource
            {
                UrlString = trimmed,
                Kind = kind,
                Via = via,
                PageURL = string.IsNullOrEmpty(pageURL) ? PageURL : pageURL,
                UserAgent = UserAgent,
                DiscoveredAt = DateTime.Now
            };
            if (kind == MediaResourceKind.Blob)
            {
                // blob 没有可复用的地址，关联最近的 m3u8 作为可下载来源。
                resource.RelatedSource = _resources.FirstOrDefault(item => item.Kind == MediaResourceKind.Hls && !item.IsBlob)?.UrlString;
            }

            _resources.Insert(0, resource);
            if (_resources.Count > Limit)
            {
                for (var index = Limit; index < _resources.Count; index++)
                {
                    _seen.Remove(_resources[index].UrlString);
                }
                _resources.RemoveRange(Limit, _resources.Count - Limit);
            }
        }
        Changed?.Invoke();
        return true;
    }

    public MediaResource? Find(string urlString)
    {
        lock (_gate) return _resources.FirstOrDefault(item => item.UrlString == urlString);
    }

    public bool NeedsInspection(string urlString, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _inspected.Contains(urlString)) return false;
            if (!force && _inspecting.Contains(urlString)) return false;
            if (!force && IsKnownChildPlaylistLocked(urlString)) return false;
            _inspecting.Add(urlString);
            return true;
        }
    }

    public void BeginReinspection(string urlString)
    {
        if (!urlString.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate) _inspecting.Add(urlString);
        Changed?.Invoke();
    }

    public void FinishInspection(string urlString, HlsPlaylistInfo? info = null, long? contentLength = null)
    {
        lock (_gate)
        {
            _inspecting.Remove(urlString);
            _inspected.Add(urlString);
            if (info != null) _playlistInfos[urlString] = info;
            if (contentLength is > 0) _contentLengths[urlString] = contentLength.Value;
        }
        Changed?.Invoke();
    }

    /// <summary>已经被某个主列表展开过的子列表不再单独解析。</summary>
    private bool IsKnownChildPlaylistLocked(string address)
    {
        var key = MediaResource.CanonicalKey(address);
        foreach (var info in _playlistInfos.Values)
        {
            if (info.Kind != HlsPlaylistInfo.InfoKind.Master) continue;
            if (info.Variants.Any(variant => MediaResource.CanonicalKey(variant.UrlString) == key)) return true;
        }
        return false;
    }

    /// <summary>某个地址所属的组键：被主列表展开的子列表归到主列表。</summary>
    public string GroupKeyFor(MediaResource resource)
    {
        lock (_gate)
        {
            var key = MediaResource.CanonicalKey(resource.UrlString);
            foreach (var (masterUrl, info) in _playlistInfos)
            {
                if (info.Kind != HlsPlaylistInfo.InfoKind.Master) continue;
                if (MediaResource.CanonicalKey(masterUrl) == key) return key;
                if (info.Variants.Any(variant => MediaResource.CanonicalKey(variant.UrlString) == key)) return key;
            }
            return key;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _resources.Clear();
            _seen.Clear();
            _playlistInfos.Clear();
            _contentLengths.Clear();
            _inspecting.Clear();
            _inspected.Clear();
        }
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}
