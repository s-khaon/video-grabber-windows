using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using VideoGrabber.Core;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.ViewModels;

/// <summary>嗅探面板里的一行（可能是一个主播放列表组，也可能是单条媒体地址）。</summary>
public sealed class SniffRow : INotifyPropertyChanged
{
    private HlsVariantRow? _selectedVariant;

    public MediaResource? Resource { get; init; }
    public HlsPlaylistInfo? Info { get; init; }
    public IReadOnlyList<HlsVariantRow> Variants { get; init; } = Array.Empty<HlsVariantRow>();

    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public bool IsBlob => Resource?.IsBlob == true;
    public string? BlobSource => Resource?.RelatedSource;
    public bool CanDownload => Resource != null;
    public bool CanConvert => Resource?.Kind is MediaResourceKind.Hls or MediaResourceKind.Mp4;
    public Visibility VariantVisibility => Variants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BlobHintVisibility => IsBlob ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => HasDetail ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前选中的档位；默认取最高码率（列表已按码率降序，占位流排在最后）。</summary>
    public HlsVariantRow? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (ReferenceEquals(_selectedVariant, value)) return;
            _selectedVariant = value;
            AppViewModelHelpers.Notify(PropertyChanged, nameof(SelectedVariant));
            AppViewModelHelpers.Notify(PropertyChanged, nameof(ActiveUrl));
            AppViewModelHelpers.Notify(PropertyChanged, nameof(SelectionText));
            AppViewModelHelpers.Notify(PropertyChanged, nameof(SelectionVisibility));
        }
    }

    /// <summary>行内各操作（复制 / yt-dlp / ffmpeg / 转换）实际使用的地址。</summary>
    public string ActiveUrl => _selectedVariant?.UrlString ?? Resource?.UrlString ?? "";

    public string SelectionText => _selectedVariant == null ? "" : $"当前档位：{_selectedVariant.Label}";

    public Visibility SelectionVisibility => _selectedVariant == null ? Visibility.Collapsed : Visibility.Visible;

    public void InitializeVariantSelection()
    {
        if (Variants.Count == 0) return;
        SelectedVariant = Variants.FirstOrDefault(variant => !variant.IsPlaceholder) ?? Variants[0];
    }
}

public sealed class HlsVariantRow
{
    public required string UrlString { get; init; }
    public string Label { get; init; } = "";
    public string Technical { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Size { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool IsPlaceholder { get; init; }
    public bool HasBadge => Badge.Length > 0;
}

internal static class AppViewModelHelpers
{
    public static void Notify(PropertyChangedEventHandler? handler, string name) =>
        handler?.Invoke(null, new PropertyChangedEventArgs(name));
}

/// <summary>内置浏览器窗口的数据层：嗅探列表、过滤、状态文案。</summary>
public sealed class BrowserViewModel : INotifyPropertyChanged
{
    private readonly MediaSnifferStore _sniffer;
    private readonly CookieStore _cookies;
    private readonly HttpClient _http;
    private string _address = "";
    private string _statusText = "";
    private string _cookieStatus = "";
    private string _shieldText = "拦截 0";
    private string _shieldCountText = "0";
    private string _filter = "全部";
    private bool _sniffEnabled = true;

    public BrowserViewModel(MediaSnifferStore sniffer, CookieStore cookies, AppSettings settings)
    {
        _sniffer = sniffer;
        _cookies = cookies;
        Settings = settings;
        _http = HlsHttp.Create(settings);
        _sniffer.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<SniffRow> Rows { get; } = new();
    public string[] Filters { get; } = { "全部", "流媒体", "分片" };

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string CookieStatus
    {
        get => _cookieStatus;
        set => Set(ref _cookieStatus, value);
    }

    public string ShieldText
    {
        get => _shieldText;
        set => Set(ref _shieldText, value);
    }

    public string ShieldCountText
    {
        get => _shieldCountText;
        set => Set(ref _shieldCountText, value);
    }

    public bool IsEmpty => Rows.Count == 0;

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            Refresh();
        }
    }

    public bool SniffEnabled
    {
        get => _sniffEnabled;
        set
        {
            _sniffer.IsEnabled = value;
            Set(ref _sniffEnabled, value);
        }
    }

    public async Task InspectPendingAsync()
    {
        foreach (var resource in _sniffer.Resources)
        {
            if (resource.Kind is not (MediaResourceKind.Hls or MediaResourceKind.Mp4 or MediaResourceKind.Webm)) continue;
            if (!Uri.TryCreate(resource.UrlString, UriKind.Absolute, out var uri)) continue;
            if (!_sniffer.NeedsInspection(resource.UrlString)) continue;

            var context = new HlsRequestContext(
                string.IsNullOrEmpty(resource.UserAgent) ? MediaSnifferStore.DefaultUserAgent : resource.UserAgent,
                string.IsNullOrEmpty(resource.PageURL) ? _sniffer.PageURL : resource.PageURL,
                _cookies.CookieHeaderFor(uri));
            try
            {
                if (resource.Kind == MediaResourceKind.Hls)
                {
                    var info = await HlsPlaylistInspector.InspectAsync(resource.UrlString, context, _http);
                    _sniffer.FinishInspection(resource.UrlString, info);
                }
                else
                {
                    var size = await HlsPlaylistInspector.ContentLengthAsync(resource.UrlString, context, _http);
                    _sniffer.FinishInspection(resource.UrlString, null, size);
                }
            }
            catch (Exception)
            {
                _sniffer.FinishInspection(resource.UrlString);
            }
        }
        Refresh();
    }

    public async Task ReinspectAsync(MediaResource resource, bool force = false)
    {
        if (!Uri.TryCreate(resource.UrlString, UriKind.Absolute, out var uri)) return;
        if (force) _sniffer.BeginReinspection(resource.UrlString);
        var context = new HlsRequestContext(
            string.IsNullOrEmpty(resource.UserAgent) ? MediaSnifferStore.DefaultUserAgent : resource.UserAgent,
            string.IsNullOrEmpty(resource.PageURL) ? _sniffer.PageURL : resource.PageURL,
            _cookies.CookieHeaderFor(uri));
        try
        {
            if (resource.Kind == MediaResourceKind.Hls)
            {
                var info = await HlsPlaylistInspector.InspectAsync(resource.UrlString, context, _http);
                _sniffer.FinishInspection(resource.UrlString, info);
            }
            else if (resource.Kind is MediaResourceKind.Mp4 or MediaResourceKind.Webm)
            {
                var size = await HlsPlaylistInspector.ContentLengthAsync(resource.UrlString, context, _http);
                _sniffer.FinishInspection(resource.UrlString, null, size);
            }
        }
        catch (Exception)
        {
            _sniffer.FinishInspection(resource.UrlString);
        }
    }

    public void Refresh()
    {
        // 刷新时保留用户手动改选的档位，避免每秒重建列表就把选择重置回最高。
        var previousSelections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (row.Resource != null && row.SelectedVariant != null)
            {
                previousSelections[row.Resource.UrlString] = row.SelectedVariant.UrlString;
            }
        }

        var all = _sniffer.Resources;
        var masters = new List<(MediaResource Resource, HlsPlaylistInfo Info)>();
        foreach (var resource in all)
        {
            var info = _sniffer.PlaylistInfo(resource.UrlString);
            if (resource.Kind == MediaResourceKind.Hls && info?.Kind == HlsPlaylistInfo.InfoKind.Master)
            {
                masters.Add((resource, info));
            }
        }
        var childKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, info) in masters)
        {
            foreach (var variant in info.Variants) childKeys.Add(MediaResource.CanonicalKey(variant.UrlString));
        }

        Rows.Clear();
        foreach (var resource in all)
        {
            var key = MediaResource.CanonicalKey(resource.UrlString);
            var info = _sniffer.PlaylistInfo(resource.UrlString);
            var isMaster = info?.Kind == HlsPlaylistInfo.InfoKind.Master;

            if (!isMaster && resource.Kind == MediaResourceKind.Hls && childKeys.Contains(key)) continue;
            if (_filter == "分片" && resource.Kind != MediaResourceKind.Fragment) continue;
            if (_filter == "流媒体" && resource.Kind is MediaResourceKind.Fragment or MediaResourceKind.Other) continue;
            if (_filter == "全部" && resource.Kind == MediaResourceKind.Fragment) continue;

            var variants = new List<HlsVariantRow>();
            if (isMaster)
            {
                foreach (var variant in info!.Variants)
                {
                    variants.Add(new HlsVariantRow
                    {
                        UrlString = variant.UrlString,
                        Label = variant.ResolutionText ?? (variant.TechnicalText.Length > 0 ? variant.TechnicalText : "未知清晰度"),
                        Technical = variant.ResolutionText == null ? variant.TechnicalText : variant.TechnicalText,
                        Duration = variant.DurationText ?? "",
                        Size = variant.EstimatedSizeText ?? "",
                        Badge = variant.IsHighest ? "最高" : variant.IsRecommended ? "推荐" : "",
                        IsPlaceholder = variant.IsPlaceholder
                    });
                }
            }

            var title = resource.SuggestedTitle ?? (isMaster ? "HLS 主播放列表" : resource.FileName);
            var subtitleParts = new List<string> { resource.KindLabel, resource.Host };
            if (resource.IsPlaceholder) subtitleParts.Add("广告 / 占位流");
            if (info is { IsEncrypted: true }) subtitleParts.Add("AES 加密");
            if (info is { HasSeparateAudio: true }) subtitleParts.Add("音频单独分轨");

            var row = new SniffRow
            {
                Resource = resource,
                Info = info,
                Variants = variants,
                Title = title,
                Subtitle = string.Join(" · ", subtitleParts.Where(part => part.Length > 0)),
                Detail = DetailFor(resource, info),
                Badge = isMaster ? $"{variants.Count} 个档位" : resource.IsBlob ? "不可直接下载" : "",
            };
            row.InitializeVariantSelection();
            if (previousSelections.TryGetValue(resource.UrlString, out var selectedUrl))
            {
                row.SelectedVariant = row.Variants.FirstOrDefault(variant => variant.UrlString == selectedUrl)
                                      ?? row.SelectedVariant;
            }
            Rows.Add(row);
        }

        StatusText = Rows.Count == 0
            ? "还没有捕获到媒体地址。在左侧页面播放视频即可。"
            : $"共 {Rows.Count} 项 · 拦截数见右上角盾牌";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private string DetailFor(MediaResource resource, HlsPlaylistInfo? info)
    {
        var parts = new List<string>();
        if (info != null)
        {
            if (info is { Kind: HlsPlaylistInfo.InfoKind.Media })
            {
                if (info.DurationText is { } duration) parts.Add("时长 " + duration);
                if (info.SegmentCount > 0) parts.Add($"{info.SegmentCount} 个分片");
                if (info.TargetDuration > 0) parts.Add($"目标分片 {info.TargetDuration:0.#}s");
                if (info.EstimatedSizeText is { } size) parts.Add("约 " + size);
                if (info.IsEncrypted) parts.Add("已加密");
                if (info.IsPlaceholder) parts.Add("广告 / 占位流");
            }
            if (!string.IsNullOrEmpty(info.ErrorText))
            {
                parts.Add(info.ErrorText + (string.IsNullOrEmpty(info.ErrorHint) ? "" : " " + info.ErrorHint));
            }
        }
        else
        {
            var length = _sniffer.ContentLength(resource.UrlString);
            if (length is > 0) parts.Add("体积 " + FileSize.Format(length.Value));
        }
        return string.Join(" · ", parts);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
