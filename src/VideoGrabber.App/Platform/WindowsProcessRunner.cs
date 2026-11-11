using System.Diagnostics;
using System.Text;
using VideoGrabber.Core.Abstractions;

namespace VideoGrabber.App.Platform;

/// <summary>
/// Windows 进程执行器：合并 stdout / stderr，逐行回调给 Core。
/// </summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, environment) };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {executable}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        var text = stdoutTask.Result + stderrTask.Result;
        return new ProcessOutput(process.ExitCode, text);
    }

    public IProcessHandle Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(executable, arguments, environment),
            EnableRaisingEvents = true
        };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {executable}");
        return new WindowsProcessHandle(process);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment != null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value == null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }
        return startInfo;
    }
}

public sealed class WindowsProcessHandle : IProcessHandle
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public WindowsProcessHandle(Process process)
    {
        _process = process;
        _process.OutputDataReceived += OnData;
        _process.ErrorDataReceived += OnData;
        _process.Exited += OnExited;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        if (_process.HasExited) _exit.TrySetResult(_process.ExitCode);
    }

    public event Action<string>? OutputLine;

    public Task<int> WaitForExitAsync() => _exit.Task;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程已经退出时忽略。
        }
    }

    /// <summary>Windows 没有 SIGINT，直接结束进程；yt-dlp 会保留 .part 文件，续传不受影响。</summary>
    public void RequestStop() => Kill();

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        OutputLine?.Invoke(e.Data);
    }

    private void OnExited(object? sender, EventArgs e)
    {
        try
        {
            _process.WaitForExit();
        }
        catch (Exception)
        {
            // 忽略退出时的竞态。
        }
        _exit.TrySetResult(_process.ExitCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OutputLine = null;
        try
        {
            _process.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
