using Forms = System.Windows.Forms;

namespace VideoGrabber.App.Platform;

/// <summary>
/// 托盘图标：关闭主窗口后应用仍在后台下载，需要托盘入口重新打开或退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIcon(Action showWindow, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => showWindow();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Video Grabber",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    /// <summary>
    /// 托盘用单独的 16/20/24/32 多尺寸图标，让系统按 DPI 选择，而不是缩放 256 大图。
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/tray.ico"));
        if (resource == null) return System.Drawing.SystemIcons.Application;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream);
    }

    public void Notify(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // 托盘气泡失败不影响下载。
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
