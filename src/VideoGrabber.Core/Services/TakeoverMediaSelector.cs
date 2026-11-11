using System.Net.Http;
using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>接管选中的下载地址。</summary>
public sealed class TakeoverSelection
{
    public string UrlString { get; init; } = "";
    public string Detail { get; init; } = "";
    public MediaResourceKind Kind { get; init; }
}

public sealed class TakeoverSelectionOutcome
{
    public TakeoverSelection? Selection { get; init; }
    /// <summary>候选里只有广告 / 占位档，没有真实视频。</summary>
    public bool OnlyAdCandidates { get; init; }
}

/// <summary>
/// 自动接管的候选挑选：优先 m3u8 主播放列表、码率最高；其次媒体播放列表；
/// 再按体积挑 MP4 / WebM，最后才退到音频流。广告档一律排除。
/// </summary>
public static class TakeoverMediaSelector
{
    public static async Task<TakeoverSelectionOutcome> SelectAsync(
        IEnumerable<MediaResource> resources,
        bool audioOnly,
        string pageURL,
        string? userAgent,
        Func<Uri, string?> cookieProvider,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var all = Dedupe(resources);
        var candidates = all.Where(resource => !resource.IsPlaceholder).ToList();
        if (candidates.Count == 0)
        {
            return new TakeoverSelectionOutcome { OnlyAdCandidates = all.Count > 0 };
        }

        using var client = HlsHttp.Create(settings);
        var sawAdPlaylist = false;

        var hls = candidates.Where(resource => resource.Kind == MediaResourceKind.Hls).ToList();
        var progressive = candidates
            .Where(resource => resource.Kind is MediaResourceKind.Mp4 or MediaResourceKind.Webm)
            .ToList();
        var audio = candidates.Where(resource => resource.Kind == MediaResourceKind.Audio).ToList();

        if (audioOnly)
        {
            if (audio.FirstOrDefault() is { } audioItem)
            {
                return new TakeoverSelectionOutcome
                {
                    Selection = new TakeoverSelection
                    {
                        UrlString = audioItem.UrlString,
                        Detail = "音频流 · " + audioItem.Host,
                        Kind = MediaResourceKind.Audio
                    }
                };
            }
            if (hls.FirstOrDefault() is { } hlsAudio)
            {
                return new TakeoverSelectionOutcome
                {
                    Selection = new TakeoverSelection
                    {
                        UrlString = hlsAudio.UrlString,
                        Detail = "HLS 音频播放列表 · " + hlsAudio.Host,
                        Kind = MediaResourceKind.Hls
                    }
                };
            }
        }

        if (hls.Count > 0)
        {
            MediaResource? bestMaster = null;
            var bestBandwidth = 0L;
            var bestVariants = 0;
            MediaResource? mediaPlaylist = null;

            foreach (var resource in hls.Take(4))
            {
                var info = await InspectAsync(resource.UrlString, pageURL, userAgent, cookieProvider, client, cancellationToken);
                if (info == null) continue;
                if (info.Kind == HlsPlaylistInfo.InfoKind.Master)
                {
                    var playable = info.Variants.Where(variant => !variant.IsPlaceholder).ToList();
                    if (playable.Count == 0)
                    {
                        sawAdPlaylist = true;
                        continue;
                    }
                    var bandwidth = playable.Max(variant => variant.EffectiveBandwidth);
                    if (bestMaster == null || bandwidth > bestBandwidth)
                    {
                        bestMaster = resource;
                        bestBandwidth = bandwidth;
                        bestVariants = playable.Count;
                    }
                }
                else if (info.Kind == HlsPlaylistInfo.InfoKind.Media)
                {
                    if (info.IsPlaceholder)
                    {
                        sawAdPlaylist = true;
                    }
                    else
                    {
                        mediaPlaylist ??= resource;
                    }
                }
            }

            if (bestMaster != null)
            {
                var detail = "HLS 主播放列表";
                if (bestBandwidth > 0)
                {
                    detail += " · 最高码率 " + HlsVariantInfo.BitrateText(bestBandwidth);
                }
                detail += $"（{bestVariants} 个清晰度）";
                return new TakeoverSelectionOutcome
                {
                    Selection = new TakeoverSelection
                    {
                        UrlString = bestMaster.UrlString,
                        Detail = detail,
                        Kind = MediaResourceKind.Hls
                    },
                    OnlyAdCandidates = sawAdPlaylist
                };
            }
            if (mediaPlaylist != null)
            {
                return new TakeoverSelectionOutcome
                {
                    Selection = new TakeoverSelection
                    {
                        UrlString = mediaPlaylist.UrlString,
                        Detail = "HLS 播放列表 · " + mediaPlaylist.Host,
                        Kind = MediaResourceKind.Hls
                    },
                    OnlyAdCandidates = sawAdPlaylist
                };
            }
            var first = hls[0];
            return new TakeoverSelectionOutcome
            {
                Selection = new TakeoverSelection
                {
                    UrlString = first.UrlString,
                    Detail = "HLS 播放列表 · " + first.Host,
                    Kind = MediaResourceKind.Hls
                },
                OnlyAdCandidates = sawAdPlaylist
            };
        }

        if (progressive.Count > 0)
        {
            MediaResource? best = null;
            var bestSize = 0L;
            foreach (var resource in progressive.Take(4))
            {
                var size = await ContentLengthAsync(resource.UrlString, pageURL, userAgent, cookieProvider, client, cancellationToken);
                if (best == null || size > bestSize)
                {
                    best = resource;
                    bestSize = size;
                }
            }
            if (best != null)
            {
                var label = best.Kind == MediaResourceKind.Webm ? "WebM 视频" : "MP4 视频";
                var detail = label;
                if (bestSize > 0) detail += " · " + FileSize.Format(bestSize);
                detail += " · " + best.Host;
                return new TakeoverSelectionOutcome
                {
                    Selection = new TakeoverSelection
                    {
                        UrlString = best.UrlString,
                        Detail = detail,
                        Kind = best.Kind
                    },
                    OnlyAdCandidates = sawAdPlaylist
                };
            }
        }

        if (audio.FirstOrDefault() is { } fallbackAudio)
        {
            return new TakeoverSelectionOutcome
            {
                Selection = new TakeoverSelection
                {
                    UrlString = fallbackAudio.UrlString,
                    Detail = "音频流 · " + fallbackAudio.Host,
                    Kind = MediaResourceKind.Audio
                },
                OnlyAdCandidates = sawAdPlaylist
            };
        }

        return new TakeoverSelectionOutcome { OnlyAdCandidates = sawAdPlaylist };
    }

    private static async Task<HlsPlaylistInfo?> InspectAsync(
        string urlString,
        string pageURL,
        string? userAgent,
        Func<Uri, string?> cookieProvider,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return null;
        var context = new HlsRequestContext(userAgent, pageURL, SafeCookieHeader(cookieProvider, uri));
        try
        {
            return await HlsPlaylistInspector.InspectAsync(urlString, context, client, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<long> ContentLengthAsync(
        string urlString,
        string pageURL,
        string? userAgent,
        Func<Uri, string?> cookieProvider,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            var context = new HlsRequestContext(userAgent, pageURL, SafeCookieHeader(cookieProvider, uri));
            context.Apply(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return 0;
            return response.Content.Headers.ContentLength ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? SafeCookieHeader(Func<Uri, string?> provider, Uri uri)
    {
        try
        {
            return provider(uri);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>blob 换成关联的来源 m3u8；按 URL 去重。</summary>
    private static List<MediaResource> Dedupe(IEnumerable<MediaResource> resources)
    {
        var result = new List<MediaResource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var url = string.IsNullOrEmpty(resource.RelatedSource) ? resource.UrlString : resource.RelatedSource!;
            var kind = string.IsNullOrEmpty(resource.RelatedSource) ? resource.Kind : MediaResourceKind.Hls;
            if (kind is not (MediaResourceKind.Hls or MediaResourceKind.Mp4 or MediaResourceKind.Webm
                or MediaResourceKind.Audio or MediaResourceKind.Fragment))
            {
                continue;
            }
            if (kind == MediaResourceKind.Fragment) continue;
            if (!seen.Add(url)) continue;
            result.Add(new MediaResource
            {
                UrlString = url,
                Kind = kind,
                Via = resource.Via,
                PageURL = resource.PageURL,
                UserAgent = resource.UserAgent,
                DiscoveredAt = resource.DiscoveredAt,
                RelatedSource = resource.RelatedSource,
                TitleHint = resource.TitleHint
            });
        }
        return result;
    }
}
