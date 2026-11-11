using VideoGrabber.Core.Models;

namespace VideoGrabber.Core.Services;

/// <summary>把下载失败信息翻译成可执行的中文提示（优先级与原版一致）。</summary>
public static class ErrorExplainer
{
    public static FailureInfo Explain(string output, string url)
    {
        var lower = (output ?? "").ToLowerInvariant();

        // 1. DRM：Widevine / FairPlay / PlayReady 属于无法下载的加密。
        if (lower.Contains("widevine") || lower.Contains("fairplay") || lower.Contains("playready")
            || lower.Contains("drm protected") || lower.Contains("drm-protected")
            || lower.Contains("encrypted media") || lower.Contains("this video is drm")
            || lower.Contains("skd://"))
        {
            return new FailureInfo
            {
                Message = "该内容使用 DRM 加密（Widevine / FairPlay），无法下载。",
                Hint = "yt-dlp 和 ffmpeg 只能处理不加密或 AES-128 加密的流；DRM 加密的媒体需要授权解密，本应用不会尝试绕过。",
                Kind = FailureKind.Drm
            };
        }

        // 1.1 广告 / 占位流：分片是 PNG/JPEG 图片，产物里没有任何视频流。
        if (lower.Contains("output file does not contain any stream")
            || lower.Contains("file does not contain any stream")
            || lower.Contains("video: png")
            || lower.Contains("detected format png_pipe")
            || lower.Contains("detected format jpeg_pipe")
            || lower.Contains("mismatches allowed extensions"))
        {
            return new FailureInfo
            {
                Message = "这条播放列表是广告 / 占位流，不是视频（分片里只有图片，没有视频流）。",
                Hint = "回到页面里播放正片，等资源面板出现非广告地址后再下载；如果页面上只抓到广告档，请播放正片后重新抓一次。",
                Kind = FailureKind.PlaceholderStream
            };
        }

        // 1.2 编码 / 容器不支持：确定性失败，不自动重试。
        if (lower.Contains("codec not currently supported in container")
            || lower.Contains("could not find codec parameters")
            || lower.Contains("unsupported codec")
            || lower.Contains("unknown decoder")
            || lower.Contains("decoder not found")
            || lower.Contains("requested formats are incompatible for merge"))
        {
            return new FailureInfo
            {
                Message = "媒体编码与目标容器不兼容，无法直接合并或封装。",
                Hint = "在任务详情里把容器改为 MKV（或「自动」）后重试；如果地址本身是广告 / 占位流，请回到页面播放正片重新抓取。",
                Kind = FailureKind.UnsupportedMedia
            };
        }

        if (lower.Contains("sign in to confirm you're not a bot")
            || lower.Contains("sign in to confirm you’re not a bot")
            || lower.Contains("confirm your age")
            || lower.Contains("please sign in")
            || lower.Contains("login required")
            || lower.Contains("cookies are no longer valid"))
        {
            return new FailureInfo
            {
                Message = "目标站点要求登录或人机验证。",
                Hint = "打开「站点登录 / Cloudflare 验证」窗口，在真实浏览器里完成验证，保存 Cookies 后重试该任务。",
                NeedsVerification = true,
                Kind = FailureKind.LoginRequired
            };
        }

        if (lower.Contains("cloudflare") || lower.Contains("just a moment") || lower.Contains("challenge"))
        {
            return new FailureInfo
            {
                Message = "站点返回了人机验证页面，yt-dlp 无法直接通过。",
                Hint = "用内置浏览器打开该网址，手动完成验证，然后保存 Cookies 并在任务中重试。",
                NeedsVerification = true,
                Kind = FailureKind.LoginRequired
            };
        }

        if (lower.Contains("http error 403") || lower.Contains("403: forbidden") || lower.Contains(" 403 "))
        {
            return new FailureInfo
            {
                Message = "站点返回 403：地址需要页面鉴权（Referer / Cookie / 短时 token）。",
                Hint = "切到内置浏览器的资源嗅探，播放后从捕获到的 m3u8 / mp4 里下载，应用会自动带上该页面的 Referer、User-Agent 与已保存的 Cookies。",
                Kind = FailureKind.Forbidden
            };
        }

        if (lower.Contains("operation not permitted")
            || lower.Contains("permission denied")
            || lower.Contains("read-only file system")
            || lower.Contains("unable to open for writing")
            || lower.Contains("error opening output file")
            || lower.Contains("failed to open output")
            || lower.Contains("access is denied")
            || lower.Contains("拒绝访问"))
        {
            return new FailureInfo
            {
                Message = "没有权限写入保存目录。",
                Hint = "点任务详情里的「重新选择保存目录」换一个可写位置（推荐「下载」或「视频」）；也可以在「设置 → 下载」改默认目录后重试。",
                Kind = FailureKind.PermissionDenied
            };
        }

        if (lower.Contains("ffmpeg not found") || lower.Contains("ffprobe and ffmpeg not found")
            || lower.Contains("postprocessing: ffmpeg"))
        {
            return new FailureInfo
            {
                Message = "缺少 ffmpeg，无法合并音视频或转码。",
                Hint = "执行 winget install Gyan.FFmpeg，或在设置里指定 ffmpeg 路径。",
                Kind = FailureKind.FfmpegMissing
            };
        }

        // 2. 解析不到真实地址：典型是网站在 JS 里动态加载 m3u8 / 分片。
        if (lower.Contains("unsupported url")
            || lower.Contains("is not a valid url")
            || lower.Contains("no video formats found")
            || lower.Contains("unable to extract")
            || lower.Contains("no suitable info found")
            || lower.Contains("requested format is not available")
            || lower.Contains("failed to extract")
            || lower.Contains("no formats found"))
        {
            return new FailureInfo
            {
                Message = "yt-dlp 解析不到可直接下载的真实地址。",
                Hint = "这类站点用 JS 动态加载媒体。切到内置浏览器的「资源嗅探」：播放视频，从捕获到的 m3u8 / mp4 里点「用 yt-dlp 下载」或「用 ffmpeg 直连下载」。",
                Kind = FailureKind.ExtractorUnsupported
            };
        }

        if (lower.Contains("video unavailable") || lower.Contains("private video")
            || lower.Contains("members-only") || lower.Contains("this video is unavailable"))
        {
            return new FailureInfo
            {
                Message = "视频不可访问（可能已删除、私有或仅限会员）。",
                Hint = "在浏览器中确认该视频可以正常播放后再试。",
                Kind = FailureKind.Other
            };
        }

        if (lower.Contains("unable to download webpage") || lower.Contains("timed out")
            || lower.Contains("connection refused") || lower.Contains("network is unreachable")
            || lower.Contains("connection reset") || lower.Contains("temporary failure"))
        {
            return new FailureInfo
            {
                Message = "网络请求失败。",
                Hint = "检查网络连通性；如果站点需要代理，请在设置里填写代理地址。",
                Kind = FailureKind.Network
            };
        }

        var line = OutputParser.LastErrorLine(output ?? "") ?? "未知错误";
        return new FailureInfo
        {
            Message = line.Replace("ERROR: ", ""),
            Hint = "查看下方日志了解完整输出；如果浏览器里能正常播放，可以切到资源嗅探手动抓地址。",
            Kind = FailureKind.Other
        };
    }
}
