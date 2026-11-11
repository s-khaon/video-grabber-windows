# Video Grabber Windows 移植方案

> 参考实现：`d:\project\personal\video-grabber`（macOS / SwiftUI，只读）
> 目标仓库：`d:\project\personal\video-grabber-windows`
> 本文档基于 README 与全部源码逐文件核对，凡与 README 不一致处均以源码为准，并单独标注。

---

## 0. 结论摘要

- 应用本质：**一个任务队列 + 三个下载引擎（yt-dlp / ffmpeg 直连 / HLS 分片）+ 一个内嵌浏览器（验证、Cookie、嗅探）+ 自动接管编排 + 产物校验修复**。
- 没有 macOS 独有算法或系统框架能力（无 AVFoundation、无 Keychain、无系统级代理注入）。专有依赖集中在 **UI 框架（SwiftUI/AppKit）与 WebView（WKWebView）** 两处，均有对等替代（WPF + WebView2）。
- 因此移植风险不在“能不能做”，而在 **行为细节的对齐**：yt-dlp 参数顺序、日志解析正则、失败分类优先级、命名与续传规则、嗅探分组规则。本文第 3 节逐条固化这些规则。
- 界面技术栈仍未由用户确认，本文第 6 节给出建议与取舍，第 8 节列为待定决策。

---

## 1. 参考项目结构

```
Package.swift                 SwiftPM，macOS 14+，单一可执行 target
Info.plist                    com.videograbber.app / LSMinimumSystemVersion 14.0 / ATS 宽松
Sources/VideoGrabber/
  App/VideoGrabberApp.swift   窗口与 Scene、菜单命令、AppDelegate 生命周期
  Models/DownloadJob.swift    任务状态机、清晰度/容器模型
  Services/                   25 个服务（下载、嗅探、Cookie、代理、工具、转换、校验…）
  Views/                      11 个界面（主窗口、详情、设置、浏览器、嗅探面板…）
  Support/                    字符串/参数/错误分类工具
Resources/ContentBlocker/     EasyList 离线转换产物（9.7MB 网络规则 + 510KB 外观选择器）
Filters/                      easylist.txt / easylistchina.txt 快照
script/                       构建、打包、图标、adblock 规则生成、样本验证
```

源码总量约 5.0 MB；其中 `DownloadManager.swift` 85KB、`BrowserWindowView.swift` 50KB、`HLSDownloadRunner.swift` 47KB、`AutoTakeover.swift` 45KB、`MediaConverter.swift` 35KB 是移植重点。

> **README 未记录但代码中存在的功能**（必须在 Windows 版保留）：
> 1. 下载完成后 **产物校验 + 自动 remux/转码修复**（`MediaVerifier`），校验不过不标“已完成”；
> 2. 菜单「修复本地视频文件…」（⇧⌘R，`MediaRepairPanel`）；
> 3. **HLS 分片直下通道**（`HLSDownloadRunner`，最多 4 并发、逐分片重试），支持真正断点续传；
> 4. **队列与任务状态持久化**（`TaskMetadataStore`，重启后恢复）；
> 5. **「已保存部分」状态**：提前结束 → 用已下载数据封装成可播放的部分视频，仍可续传；
> 6. **自动重试**：瞬时错误最多 3 次指数退避（2s/4s/8s）；格式不可用降级重试一次；
> 7. **直播流识别**：无 `#EXT-X-ENDLIST` 时切换 `--downloader ffmpeg --hls-use-mpegts` 重新录制；
> 8. **ffprobe 单独检测**（缺失只降级，不阻断下载）。

---

## 2. 功能清单（用户可见行为 / 参数 / 默认值 / 边界）

### 2.1 任务与队列

| 项 | 行为 |
|---|---|
| 入队 | URL 规范化：去空白；`http(s)` 且 host 含 `.` 直接用；无 scheme 时补 `https://`；其余拒绝（返回 nil）。 |
| 并发 | `maxConcurrentDownloads`，默认 **3**，范围 1–6；`pump()` 倒序遍历（最新在列表顶部，先入队先跑）。 |
| 批量 | 一次粘贴多行 → 同 `batchID`，**串行**执行（前一个 running 时后续跳过）。 |
| 状态 | `queued / running / completed / partial / failed / cancelled`，中文：排队中、下载中、已完成、已保存部分、失败、已取消。 |
| 取消 | 运行中：终止进程 / HLS runner；`keepIncompleteTempFiles=false` 时删除临时数据。 |
| 提前结束 | `finishEarly`：HLS 立刻收尾；yt-dlp 先 SIGINT，8 秒后强杀；用已下载数据生成「已保存部分」；临时数据保留。 |
| 继续下载 | failed/cancelled/partial → 重新入队，`isResuming=true`，不清进度，保留 `.part` 与已完成分片。 |
| 重试 | 清空续传数据、输出基名、日志、接管字段，回到用户最初粘贴的 URL，进度归零。 |
| 删除 / 清理已完成 | 移除任务并删除任务目录（含临时文件）。 |
| 全部取消 / 全部中止接管 | 侧栏与设置页入口。 |
| 持久化 | 每任务目录 `<数据目录>/VideoGrabber/Tasks/<uuid>/{task.json, completed.json, ytdlp/}`；进度每 2 秒落盘一次；启动时恢复队列。 |

### 2.2 新增下载

- 单行或多行链接输入；「解析」调用 `yt-dlp --dump-single-json --no-warnings --no-playlist --skip-download --ignore-config` + 代理 + 该站点 cookies。
- 解析结果：标题、时长、上传者、缩略图、可用高度集合（仅 `vcodec != none` 且 `height > 0`）。
- 清晰度下拉：`最佳画质` + 实际可用高度；解析不到时回退 `[2160,1440,1080,720,480]`；另有 `仅音频（MP3）`。
- 容器：自动 / MP4 / MKV；仅音频时禁用容器并提示「音频会转成 MP3（--audio-quality 0）」。
- 保存目录：默认取设置；选择后记住。
- Cookie 复用开关：默认取设置；显示命中的 `<域名>.txt` 条数与更新时间，未命中给出去浏览器验证的提示。
- 失败时按 `FailureKind` 给不同按钮：`forbidden / extractorUnsupported` → 「切到资源嗅探」；`loginRequired` → 「打开内置浏览器」；`drm` → 明确不可下载。
- 批量入队时 `title=nil`，由下载完成后的文件名回填标题。

### 2.3 引擎 A：yt-dlp

参数构造（顺序即代码顺序，`makeArguments`）：

```
--newline --no-color --no-playlist --ignore-config --continue
-P temp:<任务临时目录>  -P <保存目录>
-o "<输出基名>%(ext)s"                 // 基名中的 % 转义为 %%
--ffmpeg-location <ffmpeg 所在目录>
[直播] --downloader ffmpeg --hls-use-mpegts
[直播 & ffmpeg>=7.1] --downloader-args "ffmpeg_i:-allowed_segment_extensions ALL -extension_picky 0"
仅音频: -f bestaudio/best -x --audio-format mp3 --audio-quality 0
否则:  -f bv*[height<=H]+ba/b[height<=H] / bv*+ba/b / b（降级重试时）
       [容器非自动] --merge-output-format mp4|mkv
[限速] -r <rate>
[代理] --proxy http://host:port
[cookie] --cookies <文件>
[referer] --referer <去 fragment 的页面地址>  [+ --add-header "Origin: scheme://host[:port]"]
[UA] --user-agent <UA>
<额外参数按 ShellWords 拆分后原样插入>
--progress-template "download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s"
<URL>
```

- 子进程环境：继承系统环境 + `http_proxy/https_proxy/all_proxy/no_proxy`（+ `SSL_CERT_FILE` 等，macOS 专用）+ PATH 前置 ffprobe/ffmpeg 目录。
- 进度：`PROGRESS|百分比|速度|ETA|已下载|总量|估算总量`；`NA/unknown` 一律显示为空；总量优先 `total_bytes`，其次 `total_bytes_estimate`。
- 日志：保留最近 3000 行；解析产物路径（`[download] Destination:`、`[ExtractAudio] Destination:`、`Writing video/audio to`、`already been downloaded`、`Merging formats into "..."`；合并产物替换此前列表）。
- 直播识别：仅当 `isLiveHLS != false` 时命中 `Live HLS streams are not supported by the native downloader` 才切换（避免把 VOD 误判）。
- 占位流识别：HLS 任务中出现 `opening '<广告域名>'` 或 `video: png/jpeg/mjpeg/gif/webp`、`png_pipe`、`jpeg_pipe`、`mismatches allowed extensions` → 立即终止，标记确定性失败，不重试。
- 失败后处理顺序（`finish`，严格按此优先级）：
  1. 占位流 → 失败，删除半成品，不重试；
  2. 提前结束且非 0 退出 → 生成部分视频；
  3. 退出码 0 → 找产物 → `MediaVerifier` 校验 → completed；
  4. 运行中判定直播 → 清半成品，`--hls-use-mpegts` 重录；
  5. 非 0 但有可播放产物（`recoveredOutputAfterFailure`）→ 按完成处理；
  6. 接管中用 yt-dlp 重下仍失败 → 自动改 ffmpeg 直连一次；
  7. `Requested format is not available` / `No video formats found` 且未降级过 → 用 `-f b` 重试；
  8. 瞬时错误（`shouldAutoRetry`）且 <3 次 → 2/4/8 秒退避重试；
  9. 失败 → 若满足条件转自动接管。
- 错误分类（`ErrorExplainer`，优先级从高到低，全部中文文案见源码 `Helpers.swift`）：DRM → 占位流 → 编码/容器不支持 → 登录/人机验证 → Cloudflare → 403 → 权限 → ffmpeg 缺失 → 解析失败 → 视频不可用 → 网络 → 兜底（取最后一非空行）。

### 2.4 引擎 B：ffmpeg 直连

```
-y -nostdin -hide_banner -loglevel info -progress pipe:1 -nostats
-i <地址>
-c copy  [或 -vn/-c:a 用于仅音频]
-headers "Referer: ...\r\nUser-Agent: ...\r\nCookie: ...\r\nOrigin: ...\r\n"
```

- 进度只有 `total_size=`（不确定进度条），显示「直连下载中 · 体积」。
- 失败归类同上；产物路径 = `<输出基名>.<扩展名>`（扩展名取 URL 末段，白名单 `mp4/mkv/webm/mp3/m4a/aac/flac/wav/mov`，否则 mp4），重名追加 ` (2)`…最多 999。

### 2.5 引擎 C：HLS 分片通道（可续传）

- 触发：URL 含 `.m3u8`，或引擎为 ffmpegDirect 且已标记分片模式。
- 流程：拉播放列表 → 解析分片（含 `EXT-X-KEY` 绝对化、`EXT-X-DISCONTINUITY`、init 段）→ 任务目录建临时目录 → 合并 `completed.json` → 下载缺失分片（**最多 4 并发**，每片 **最多 3 次**，退避 1s/2s）→ 每片完成即写 `completed.json` → 生成本地 m3u8 → ffmpeg 封装。
- 封装：`-c copy`，视频加 `+faststart`；仅音频 `-vn -c:a libmp3lame`；本地播放列表需 `-allowed_extensions ALL` 与（ffmpeg≥7.1）`-allowed_segment_extensions ALL -extension_picky 0`。
- 提前结束：用已完成分片生成 `local-partial.m3u8` 封装成 `<基名> - 已下载部分.mp4`，保留分片可续传。
- 直播（无 `ENDLIST`）：不用分片通道，转 ffmpeg 下载器。
- 分片失败 → 失败原因分类；AES-128 可解，DRM 不可解。

### 2.6 产物校验与修复

- 体积 < 64KB → 失败；明显小于 `totalBytes` 的 50% → 失败。
- 魔数嗅探：MPEG-TS（0x47 @0/@188）、MP4（`ftyp`）、Matroska；**TS 内容写在 .mp4 里** 视为异常。
- `ffprobe -print_format json` 校验流信息/时长/可解码性；ffmpeg 缺失且 ffprobe 也缺失 → 记为「未做解码校验」并通过。
- 异常时：先无损 remux，再转码（libx264 veryfast crf20 + aac 192k）；修复成功原地替换；失败返回可读原因。
- 菜单「修复本地视频文件…」：多选文件，逐个跑同一流程，汇总 ✓/✗ 与明细。

### 2.7 输出命名

- 基名 = `sanitize(标题) [稳定视频标识]`，标题截断 120 字符；标识取自页面 URL / 原始 URL / 当前 URL 的最后一个有效路径段（跳过 `index/master/playlist/segment/index-*`、10 位以上纯数字时间戳），否则用 URL 的 SHA-256 前 4 字节 hex。
- 基名消毒：`/ : \ "` → `_`，`×`→`x`，换行→空格，删除 NUL。
- `outputBase` 首次生成后固定，续传/重试不改名；`-o` 中 `%` 转义。
- 部分视频：`<完整名> - 已下载部分.<ext>`；重名追加 ` (n)`。

### 2.8 Cookie

- 目录：`~/Library/Application Support/VideoGrabber/Cookies/<主域名>.txt`（Windows：`%LOCALAPPDATA%\VideoGrabber\Cookies\`）。
- 格式：Netscape，`# Netscape HTTP Cookie File` 头；HttpOnly 加 `#HttpOnly_` 前缀；`domain / includeSubdomains / path / secure / expiry / name / value` tab 分隔。
- 匹配：按主域做后缀匹配，含子域；对 `co.uk` 等多级后缀做基础处理；嗅探地址优先用来源页面域名的 cookie。
- 列表：条数、大小、更新时间；单删 / 清空全部均二次确认；删除同时清浏览器站点数据（cookie/localStorage/IndexedDB）。

### 2.9 内置浏览器

- 地址栏输入 + 回车；真实浏览器 UA；可完成 Cloudflare 验证与登录。
- 「保存 Cookies」：导出当前站点（主域）cookie 到文件，底部结果条显示域名/条数/路径或失败原因；另有「清理会话」。
- 广告拦截：网络层（规则） + 外观层（注入 CSS 隐藏） + 弹窗拦截；工具栏盾牌显示本次会话拦截数（分项），支持总开关、按站点临时关闭、白名单管理。
- 放行：`challenges.cloudflare.com`、`turnstile.cloudflare.com`、`cloudflareclient.com`、`/cdn-cgi/challenge-platform/`，以及 `m3u8/mp4/m4s/ts/mpd/webm` 等媒体资源。
- 规则离线随包，运行时零网络请求；规则更新 = 替换 `Filters/*.txt` 后重新生成 JSON。

### 2.10 资源嗅探

- 注入脚本钩住：`window.fetch`、`XMLHttpRequest.open`、`HTMLMediaElement.src` setter、`srcObject` setter、`URL.createObjectURL`、`MutationObserver`（`video/audio/source` 的 `src` 与新增节点）。
- 命中后缀：`m3u8 / m3u8 / mp4 / m4s / ts / mp3 / webm / m4a / mov` 与 `blob:`；上报 `url + via + 页面地址 + UA`。
- 去重按 URL；上限 400 条；筛选「全部 / 流媒体 / 分片」，分片默认不进「全部」。
- HLS 主列表展开为档位：分辨率、平均/峰值码率、帧率、编码、时长、估算体积；标「最高」「推荐」，默认选最高码率。
- 媒体列表（子清单）显示总时长、分片数、目标分片时长、加密方式、估算体积；mp4/webm 有 `Content-Length` 时显示真实体积。
- 分组：同一主列表的多档、短时 token 不同的重复地址、已被展开的子清单归为一组。
- 行操作：复制；yt-dlp 下载（带 Referer/UA/Cookie）；ffmpeg 直连带 `-headers`；`.m3u8` 行可「转换为 mp4…」（自动带上 Referer/UA/Cookie）。
- `blob:` 标注「不可直接下载」，关联最近捕获的来源 m3u8，提供「用该 m3u8 下载」。

### 2.11 自动接管

- 触发：yt-dlp 失败且类型属于 403 / 需鉴权 / 解析不到（`shouldAutoTakeover`），且总开关开启、已记住的站点则直接进入。
- 流程：隐藏浏览器加载页面（25s 超时）→ 每帧注入静音自动播放与滚动 → 收集候选 → HLS 优先主列表最高码率，否则最大 mp4/webm → 同步 Cookie → yt-dlp 重下 → 仍失败换 ffmpeg 直连 → 成功后记忆站点。
- 暂停条件：Cloudflare 验证 / 需登录。每 5s 用共享 Cookie 重载一次，180s 超时判失败；界面提供「打开内置浏览器完成验证 / 登录」。
- 阶段（`Phase`）：`loading / playing / selecting / retrying / succeeded / failed / cancelled`，每阶段有标题与图标；详情页展示步骤时间线，可中止。
- 设置：总开关、站点记忆开关、已记住站点列表（逐个删除 / 全部清除）。

### 2.12 m3u8 转 mp4

- 入口：菜单 ⇧⌘M、工具栏「转换」、嗅探行「转换为 mp4…」。
- 输入：网络地址或本地 `.m3u8`（可选分片目录；相对 URI 改写成绝对路径后写临时播放列表，原文件不动）。
- 参数：

```
-y -nostdin -hide_banner -loglevel info -progress pipe:1 -nostats
-protocol_whitelist file,http,https,tcp,tls,crypto,httpproxy
[.m3u8] -allowed_extensions ALL (+ ffmpeg>=7.1 的分片扩展名参数)
-headers "Referer/User-Agent/Cookie/Origin"
[远端] -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -rw_timeout 20000000
-i <输入>
-c copy                          // 默认
或 -c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 192k   // 转码重试
<输出>
```

- 进度：解析 `total_size / out_time_us / out_time_ms / out_time / progress / speed / bitrate`；能取到总时长显示百分比，否则不确定进度条。
- 输出名默认按输入（远端取主机名，本地取文件名），目录默认跟随设置。
- 取消 → 删除半成品；失败按地址失效/403/AES 密钥/DRM/网络/ffmpeg 缺失/目录不可写归类，不可写时提供「重新选择目录…」。

### 2.13 设置项全表

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ytDlpPath` / `ffmpegPath` / `ffprobePath` | string | "" | 留空自动检测；ffprobe 缺省按 ffmpeg 同目录找 |
| `maxConcurrentDownloads` | int | 3 | 1–6，改动立即重新调度 |
| `rateLimit` | string | "" | 如 `5M`、`800K`，空=不限速 |
| `proxyEnabled` | bool | true | 关闭=完全直连 |
| `proxyURL` | string | `http://127.0.0.1:7897` | all_proxy 自动推导为同端口 socks5 |
| `extraArguments` | string | "" | 追加到 yt-dlp 参数尾部，支持引号 |
| `defaultSaveDirectory` | string | `~/Downloads` | |
| `defaultQualityID` | string | `best` | `best` / `h1440`… / `audio` |
| `useCookiesByDefault` | bool | true | 新建任务默认复用 cookie |
| `adBlockEnabled` | bool | true | |
| `adBlockWhitelist` | [string] | [] | 按 host，子域生效 |
| `autoTakeoverEnabled` | bool | true | |
| `rememberSniffSites` | bool | true | |
| `sniffOnlyHosts` | [string] | [] | 已确认需嗅探的站点 |
| `keepIncompleteTempFiles` | bool | true | 关闭后失败/取消会删 `.part` 与分片 |
| `toolStatusCache` | JSON | — | 路径 / 版本 / 检测时间 |

### 2.14 窗口、菜单、快捷键

| 快捷键 | 动作 |
|---|---|
| ⌘N | 新增下载 |
| ⌘0 | 打开主窗口 |
| ⇧⌘M | m3u8 转 mp4… |
| ⇧⌘R | 修复本地视频文件… |
| ⇧⌘B | 站点登录 / 验证… |
| ⇧⌘K | Cookie 管理… |
| ⌘, | 设置（系统默认） |

- 窗口：主窗口（最小 900×540，默认 1120×720）、转换器（最小 620×560）、浏览器（最小 720×520，默认 1120×820）、Cookie 管理（640×520）、设置（620×500）。
- 主窗口：左队列侧栏（空状态、任务进度、菜单「清除已结束」「取消全部」），右详情（标题/URL/操作、进度卡、接管卡、失败卡、详情信息、命令预览、日志）。
- 关闭主窗口不退出应用（下载继续）；再次激活时唤回主窗口。
- Windows 建议：菜单改为窗口内菜单栏（文件/编辑/视图/帮助）+ 托盘图标提供「显示主窗口 / 全部取消 / 退出」。

### 2.15 代理

- 生效范围：yt-dlp 子进程（解析与下载）、ffmpeg 直连、转换；**内置浏览器不读应用内代理**（macOS 版走系统代理）。
- 注入：`http_proxy`、`https_proxy`、`all_proxy`（socks5 同端口）、`no_proxy`（本机与内网段）+ `--proxy`。
- 「测试连通性」：请求 `https://www.gstatic.com/generate_204`，超时 12s/15s，成功显示毫秒延迟，失败给出端口未监听/超时等原因。

### 2.16 工具检测

- 搜索目录（macOS）：`/opt/homebrew/bin`、`/usr/local/bin`、`/opt/local/bin`、`/usr/bin`、`/bin`、`~/bin`、`~/.local/bin`、`~/.pyenv/shims`、Python Framework + `PATH`。
- 版本：`yt-dlp --version`；`ffmpeg -version` / `ffprobe -version`（取首行并去掉前缀）。
- 结果缓存并持久化；启动只校验文件是否存在，失效才重新定位并提示「路径已失效」。
- ffmpeg ≥ 7.1 才追加 `-allowed_segment_extensions / -extension_picky`。

---

## 3. macOS 专有能力 → Windows 映射

| macOS 能力 | 用途 | Windows 方案 |
|---|---|---|
| SwiftUI / AppKit | 全部界面 | **WPF (.NET 8)** 或 WinUI 3 / Electron（见第 6 节） |
| WKWebView | 内置浏览器、隐藏接管浏览器 | **WebView2**（本机已装 Evergreen Runtime 157） |
| `WKContentRuleList` | 网络层广告拦截 | WebView2 `AddWebResourceRequestedFilter` + `WebResourceRequested` 取消请求（规则改由 C# 预编译匹配） |
| `WKUserScript`（documentStart） | 嗅探、外观过滤、自动播放 | `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` |
| `WKScriptMessageHandler` | JS→原生上报 | `window.chrome.webview.postMessage` + `WebMessageReceived` |
| `WKHTTPCookieStore` | 导出/清理 cookie | `CoreWebView2.CookieManager.GetCookiesAsync` / `DeleteCookies` |
| `WKWebsiteDataStore` | 清理 localStorage/IndexedDB/cache | `Profile.ClearBrowsingDataAsync` + JS 清理脚本 |
| `NSWorkspace.activateFileViewerSelecting` | 在 Finder 中显示 | `explorer.exe /select,"<path>"` |
| `NSOpenPanel` / `NSSavePanel` | 选文件/目录 | `OpenFileDialog` / `OpenFolderDialog`（.NET 8 WPF 内置） |
| `UserDefaults` | 设置与检测缓存 | `%APPDATA%\VideoGrabber\settings.json`（System.Text.Json，原子写） |
| `~/Library/Application Support` | Cookies、任务目录 | `%LOCALAPPDATA%\VideoGrabber\` |
| security-scoped bookmark | 保存目录持久授权 | Windows 无沙盒等价物：存普通路径 + 写探针校验可写 |
| `Process.interrupt()`（SIGINT） | 提前结束、保留 `.part` | Windows 无 SIGINT：默认 `taskkill` 结束进程（yt-dlp 本身会保留 `.part`）；HLS 通道内部实现原子停止，行为不受影响 |
| `NSApplication` 关闭窗口不退出 | 后台继续下载 | `ShutdownMode=OnExplicitShutdown` + 托盘退出 |
| Menu bar 全局菜单 | 文件菜单命令 | 窗口内 Menu + 托盘菜单 |
| ⌘ / ⇧⌘ | 快捷键 | Ctrl / Ctrl+Shift |
| `SSL_CERT_FILE` 注入 | 修静态 ffmpeg 证书 | 不需要；如遇证书问题改为指定 `cacert.pem` |
| `AppIcon.icns` / `.app` / DMG | 打包 | `.ico` + 自包含发布（zip / MSI，可选） |

---

## 4. 外部依赖与分发

| 依赖 | 版本要求 | 分发方式 |
|---|---|---|
| `yt-dlp.exe` | 最新稳定版 | **不内置**（与 macOS 版一致），启动检测 + 安装指引；支持手动指定路径 |
| `ffmpeg.exe` / `ffprobe.exe` | ≥ 7.1 才启用新分片参数；更低版本可用但降级 | 同上；建议 gyan.dev 完整版（自带 ffprobe） |
| WebView2 Runtime | Evergreen | Win11 内置；安装包检测并引导安装 |
| .NET 运行时 | .NET 8 | 自包含发布可免安装 |

安装指引文案（Windows）：`winget install yt-dlp.yt-dlp`、`winget install Gyan.FFmpeg`，或 `scoop install yt-dlp ffmpeg`。
检测目录：`%LOCALAPPDATA%\Microsoft\WinGet\Links`、`%USERPROFILE%\scoop\shims`、`C:\ProgramData\chocolatey\bin`、`C:\ffmpeg\bin`、`%LOCALAPPDATA%\Programs\...` 以及 `PATH`。

> 可选增强（需用户决定）：首次启动时一键下载 yt-dlp/ffmpeg 到 `%LOCALAPPDATA%\VideoGrabber\bin`。macOS 版没有此功能，为保证“功能一致”先不做。

---

## 5. 关键行为对齐清单（移植验收基线）

1. yt-dlp 参数**顺序与内容**逐字一致（尤其是 `-P temp:` / `-P save` 双路径与 `-o` 的 `%` 转义）。
2. `PROGRESS|` 模板字段顺序一致；`NA/unknown` 归空。
3. 失败分类优先级一致（DRM > 占位流 > 编码 > 登录 > Cloudflare > 403 > 权限 > ffmpeg > 解析 > 不可用 > 网络 > 兜底）。
4. 自动重试 3 次（2/4/8s）、格式降级一次、直播重录、接管后 ffmpeg 回退一次——顺序不可调换。
5. 命名规则与 `outputBase` 固定策略一致；部分视频后缀 ` - 已下载部分`。
6. Cookie 文件格式、域名匹配、删除联动一致。
7. 嗅探去重/上限 400/过滤/分组/blob 关联一致。
8. 接管阶段文案与 5s 重载、180s 超时一致。
9. 转换器参数、`-protocol_whitelist`、AES 放行、copy→转码重试一致。
10. 设置项键语义与默认值一致（Windows 用 JSON 键名可沿用原 key）。

---

## 6. 技术栈建议

### 方案 A（推荐）：.NET 8 + WPF + WebView2

- **理由**
  - 与 SwiftUI 的 `@Observable` 模型一一对应（`INotifyPropertyChanged` + MVVM），任务状态机、队列、命令可近乎直译。
  - `Process`、`HttpClient`、`System.Text.Json`、文件系统能力完备，yt-dlp/ffmpeg 编排是强项。
  - WebView2 提供与 WKWebView 对等的四件套（documentStart 脚本、postMessage、CookieManager、请求拦截），嗅探/去广告/接管全部可实现。
  - 自包含单文件发布，安装包小、内存占用低、启动快，最接近原版桌面体验。
- **代价**：本机当前**没有 .NET SDK**（只有 runtime 残留），需要 `winget install Microsoft.DotNet.SDK.8`；XAML 开发效率低于 HTML。

### 方案 B：Electron + TypeScript

- **理由**：本机已有 Node 20 / npm 10，可立即开工；`session.webRequest` 天然适合去广告与嗅探；嗅探注入 JS 可从 Swift 版几乎原样搬运；跨平台可反过来复用到 macOS。
- **代价**：安装包 100MB+、内存占用高；与“原生 Windows 应用”的观感有差距；进程管理需注意 packaged 路径与子进程清理。

### 方案 C：WinUI 3

- 观感最现代，但未打包部署（unpackaged）与 WebView2 组合的坑较多，学习成本高于 WPF，收益不明显 → 不推荐。

### 方案 D：Tauri 2

- 体积最小，但本机无 Rust 工具链，且 5 万行级别的业务编排需要用 Rust 重写，成本最高 → 不推荐。

---

## 7. 分阶段移植计划

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P0 骨架** | 解决方案/项目结构、设置存储（JSON + 原子写）、数据目录、工具检测与缓存、主窗口壳 + 侧栏/详情布局、日志组件 | 启动即显示依赖状态；设置可持久化；关窗不退出 |
| **P1 下载主链** | 新增下载（URL 解析/清晰度/容器/目录/Cookie 开关）、队列与并发、yt-dlp 参数与进度/日志解析、失败分类、自动重试、命名规则、任务持久化 | 粘贴 YouTube/B 站链接可完成下载；重启后队列恢复；失败文案与原版一致 |
| **P2 直连与续传** | ffmpeg 直连、HLS 分片通道（4 并发/3 重试/续传）、提前结束部分视频、产物校验与修复、修复本地文件菜单 | 断网/中断后可续传；TS-in-MP4 自动修复；「已保存部分」可播放 |
| **P3 浏览器与 Cookie** | WebView2 浏览器窗口、地址栏、Cookie 导出/清理、Cookie 管理窗口、清理超时兜底 | 可完成 Cloudflare 验证并导出 `cf_clearance`；下载自动带 cookie |
| **P4 资源嗅探** | 注入脚本、消息桥、资源面板（筛选/分组/档位/blobl 关联/复制/清空）、HLS 播放列表解析与体积估算 | 在 hls.js demo 抓到 m3u8 与分片；档位信息正确；可用 ffmpeg 直连下载 |
| **P5 自动接管** | 隐藏浏览器、自动播放、选流、Cookie 同步、失败重下、站点记忆、暂停/超时、步骤时间线 | 对 403 站点全自动成功；验证型站点暂停并可人工完成后继续 |
| **P6 广告拦截** | 规则转换脚本（Python→Windows 规则）、请求拦截、外观注入、计数、白名单、设置页 | 拦截计数正确；Cloudflare 与媒体资源不被误拦 |
| **P7 转换器** | m3u8→mp4 窗口、本地播放列表改写、进度/取消/转码重试、错误归类 | 远端与本地播放列表均能产出可播放 mp4 |
| **P8 打包收尾** | 图标、自包含发布、安装包/zip、README、样本回归脚本 | 干净机器可运行；第 5 节 10 条基线逐条通过 |

依赖关系：P1 是主干；P2 可与 P3 并行；P4 依赖 P3；P5 依赖 P4；P6 独立但依赖 P3；P7 依赖 P2 的 ffmpeg 封装逻辑。

---

## 8. 待定决策

1. **界面技术栈（阻塞 P0）**：A. .NET 8 + WPF（推荐）/ B. Electron / C. WinUI 3 / D. Tauri。
2. 是否内置 yt-dlp / ffmpeg（默认不内置，与 macOS 版一致）。
3. 是否需要托盘常驻与开机自启（macOS 版为“关窗不退出”，无托盘）。
4. 是否需要安装包（MSI）还是仅提供 zip 便携包。
