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
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Video Grabber",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
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
