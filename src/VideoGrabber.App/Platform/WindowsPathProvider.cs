using System.IO;
using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.App.Platform;

/// <summary>
/// Windows 路径：配置在 %APPDATA%\VideoGrabber，任务与 Cookies 在 %LOCALAPPDATA%\VideoGrabber。
/// </summary>
public sealed class WindowsPathProvider : IPathProvider
{
    public string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoGrabber");

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoGrabber");

    public string DownloadsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public WindowsPathProvider()
    {
        ToolSearchDirectories = BuildSearchDirectories();
    }

    /// <summary>常见包管理器与手工安装位置，用于在 PATH 未更新时也能找到工具。</summary>
    public IReadOnlyList<string> ToolSearchDirectories { get; }

    public string ExecutableSuffix => ".exe";

    private List<string> BuildSearchDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
            Path.Combine(profile, "scoop", "shims"),
            Path.Combine(programData, "chocolatey", "bin"),
            @"C:\ffmpeg\bin",
            Path.Combine(DataDirectory, "Tools"),
            Path.Combine(local, "Programs", "yt-dlp"),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "tools")
        };
        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
