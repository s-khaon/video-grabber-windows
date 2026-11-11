namespace VideoGrabber.Core.Abstractions;

/// <summary>
/// 一次性执行的子进程输出（用于探测、版本检查）。
/// </summary>
public readonly record struct ProcessOutput(int ExitCode, string Text);

/// <summary>
/// 流式进程句柄：持续输出日志行，并提供退出等待。
/// </summary>
public interface IProcessHandle : IDisposable
{
    /// <summary>每读到一行输出（stdout / stderr 合并）触发一次。</summary>
    event Action<string>? OutputLine;

    /// <summary>等待进程退出并返回退出码。</summary>
    Task<int> WaitForExitAsync();

    /// <summary>强制结束进程。</summary>
    void Kill();

    /// <summary>
    /// 请求尽快停止。Windows 没有 SIGINT，默认实现为直接结束进程；
    /// yt-dlp 自身会保留 .part 文件，因此续传行为不受影响。
    /// </summary>
    void RequestStop();
}

/// <summary>
/// 进程执行器。Core 只依赖这个接口，具体实现由宿主平台提供。
/// </summary>
public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken = default);

    IProcessHandle Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment);
}

/// <summary>保存目录 / 文件选择。</summary>
public interface IFilePicker
{
    /// <summary>选择一个目录；取消时返回 null。</summary>
    string? PickFolder(string? initialDirectory, string title = "选择文件夹");

    /// <summary>选择多个文件；取消时返回空集合。</summary>
    IReadOnlyList<string> PickFiles(string title = "选择文件", string filter = "视频文件|*.mp4;*.mkv;*.webm;*.mov;*.ts;*.m4a;*.mp3|所有文件|*.*");

    /// <summary>在系统文件管理器中定位文件；路径不存在时打开所在目录。</summary>
    void RevealInExplorer(IReadOnlyList<string> paths, string fallbackDirectory);
}

/// <summary>剪贴板。</summary>
public interface IClipboardService
{
    string? GetText();
    void SetText(string text);
}

/// <summary>桌面通知。</summary>
public interface INotifier
{
    void Notify(string title, string message);
}

/// <summary>
/// 平台路径与工具搜索位置。
/// </summary>
public interface IPathProvider
{
    /// <summary>配置目录（Windows: %APPDATA%\VideoGrabber）。</summary>
    string SettingsDirectory { get; }

    /// <summary>数据目录（Windows: %LOCALAPPDATA%\VideoGrabber），存放任务、Cookies、日志。</summary>
    string DataDirectory { get; }

    /// <summary>默认下载目录。</summary>
    string DownloadsDirectory { get; }

    /// <summary>除 PATH 之外额外搜索可执行文件的目录。</summary>
    IReadOnlyList<string> ToolSearchDirectories { get; }

    /// <summary>可执行文件后缀（Windows: ".exe"）。</summary>
    string ExecutableSuffix { get; }
}
