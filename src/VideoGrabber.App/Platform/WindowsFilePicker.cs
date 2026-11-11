using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.App.Platform;

public sealed class WindowsFilePicker : IFilePicker
{
    public string? PickFolder(string? initialDirectory, string title = "选择文件夹")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickFiles(string title = "选择文件", string filter = "视频文件|*.mp4;*.mkv;*.webm;*.mov;*.ts;*.m4a;*.mp3|所有文件|*.*")
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    public void RevealInExplorer(IReadOnlyList<string> paths, string fallbackDirectory)
    {
        var first = paths.FirstOrDefault(File.Exists);
        try
        {
            if (first != null)
            {
                // /select 需要参数单独传，路径含空格时才不会解析错。
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
                return;
            }
            var directory = Directory.Exists(fallbackDirectory)
                ? fallbackDirectory
                : Path.GetDirectoryName(fallbackDirectory);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // 资源管理器启动失败时静默忽略，不影响下载。
        }
    }
}

public sealed class WindowsClipboardService : IClipboardService
{
    public string? GetText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SetText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // 剪贴板被其它进程占用时忽略。
        }
    }
}
