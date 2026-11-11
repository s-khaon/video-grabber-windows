# Video Grabber (Windows)

Windows 版网页视频下载器，功能对齐 macOS 版（`d:\project\personal\video-grabber`）：
调用本机 `yt-dlp` + `ffmpeg` 下载网页视频，内置浏览器完成 Cloudflare 人机验证与登录，
导出 `cookies.txt` 复用站点会话，并提供资源嗅探、失败自动接管、m3u8 转 mp4、产物校验修复。

## 现状

- [x] 功能清单与技术方案：[docs/porting-analysis.md](docs/porting-analysis.md)
- [x] P0 骨架（.NET 8 + WPF + WebView2，Core 与 UI 分层）
- [x] P1 下载主链（yt-dlp 参数 / 队列 / 进度 / 失败分类 / 自动重试 / 命名 / 持久化）
- [x] P2 直连 / HLS 分片续传 / 提前结束部分视频 / 产物校验与修复
- [x] P3 内置浏览器与 Cookie（WebView2、导出 / 清理、Cookie 管理窗口）
- [x] P4 资源嗅探（注入脚本、档位选择、分组、blob 关联、行操作）
- [x] P5 自动接管（隐藏浏览器、自动播放、选流、Cookie 同步、站点记忆、暂停 / 超时）
- [x] P6 广告拦截（请求拦截 + 外观注入、计数、白名单、设置页）
- [x] P7 转换器（远端 / 本地 m3u8、进度、取消、copy→转码重试、错误归类）
- [x] P8 打包收尾（自包含发布、WiX MSI、覆盖安装验证）

## 依赖

- `yt-dlp`、`ffmpeg`（含 `ffprobe`）需自行安装并被应用检测到：

```powershell
winget install yt-dlp.yt-dlp
winget install Gyan.FFmpeg
```

## 与 macOS 版的差异

仅限平台等价替换（WPF/WebView2、`explorer /select`、`%LOCALAPPDATA%` 等），
不改变下载行为、参数、失败分类、命名与续传规则。详见方案文档第 3 节。
