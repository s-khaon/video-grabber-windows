using System.Windows;
using VideoGrabber.App.Platform;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.Views;

/// <summary>「修复本地视频文件…」：逐个校验 / 修复，边跑边显示明细。</summary>
public partial class MediaRepairWindow : Window
{
    private readonly MediaRepairService _service;
    private readonly IReadOnlyList<string> _files;
    private CancellationTokenSource? _cancellation;

    public MediaRepairWindow(MediaRepairService service, IReadOnlyList<string> files)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _service = service;
        _files = files;
        Loaded += (_, _) => _ = RunAsync();
        Closing += (_, _) => _cancellation?.Cancel();
    }

    private async Task RunAsync()
    {
        _cancellation = new CancellationTokenSource();
        CloseButton.IsEnabled = false;
        Progress.Maximum = Math.Max(1, _files.Count);
        Progress.Value = 0;

        var done = 0;
        var lines = new List<string>();
        try
        {
            foreach (var file in _files)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                Append($"[{done + 1}/{_files.Count}] {System.IO.Path.GetFileName(file)}");
                var entry = await _service.RepairAsync(file, _cancellation.Token);
                lines.Add(entry.Ok ? $"✓ {entry.Message}" : $"✗ {entry.Message}");
                Append(entry.Ok ? $"  ✓ {entry.Message}" : $"  ✗ {entry.Message}");
                foreach (var line in entry.Log) Append("    " + line);
                Append("");
                done++;
                Progress.Value = done;
            }

            var succeeded = lines.Count(line => line.StartsWith('✓'));
            Append($"完成：成功 {succeeded} 个，失败 {lines.Count - succeeded} 个。");
        }
        catch (OperationCanceledException)
        {
            Append("已取消。");
        }
        catch (Exception ex)
        {
            Append("修复失败：" + ex.Message);
        }
        finally
        {
            CloseButton.IsEnabled = true;
        }
    }

    private void Append(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
