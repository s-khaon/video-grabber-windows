using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.Core.Tests;

/// <summary>
/// 真实下载冒烟测试：需要本机 yt-dlp / ffmpeg 与网络。
/// 默认跳过，设置 VIDEOGRABBER_E2E=1 后运行：
///   $env:VIDEOGRABBER_E2E="1"; dotnet test --filter DownloadPipelineE2E
/// </summary>
public class DownloadPipelineE2E
{
    private sealed class TestPathProvider : IPathProvider
    {
        public TestPathProvider(string root)
        {
            SettingsDirectory = Path.Combine(root, "settings");
            DataDirectory = Path.Combine(root, "data");
            DownloadsDirectory = Path.Combine(root, "downloads");
            Directory.CreateDirectory(SettingsDirectory);
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(DownloadsDirectory);
        }

        public string SettingsDirectory { get; }
        public string DataDirectory { get; }
        public string DownloadsDirectory { get; }
        public IReadOnlyList<string> ToolSearchDirectories { get; } = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoGrabber", "Tools")
        };
        public string ExecutableSuffix => ".exe";
    }

    [Fact]
    public async Task Download_DirectMp4_CompletesWithPlayableFile()
    {
        if (Environment.GetEnvironmentVariable("VIDEOGRABBER_E2E") != "1") return;

        var root = Path.Combine(Path.GetTempPath(), "vg-e2e-" + Guid.NewGuid().ToString("N"));
        var paths = new TestPathProvider(root);
        var settings = AppSettings.Load(paths.SettingsDirectory, paths.DownloadsDirectory);
        settings.ProxyEnabled = Environment.GetEnvironmentVariable("VG_E2E_PROXY") == "1";
        settings.KeepIncompleteTempFiles = true;
        settings.Save();

        var tools = new ToolEnvironment(paths, settings);
        await tools.RefreshAsync();
        Assert.NotNull(tools.YtDlp);

        var manager = new DownloadManager(settings, tools, new TaskMetadataStore(paths), new ProcessRunner());
        var job = manager.Enqueue(
            "https://www.w3schools.com/html/mov_bbb.mp4",
            "E2E 冒烟测试",
            QualityChoice.Best,
            VideoContainer.Auto,
            paths.DownloadsDirectory,
            useCookies: false);
        Assert.NotNull(job);

        var deadline = DateTime.Now.AddMinutes(3);
        while (DateTime.Now < deadline && job!.State is JobState.Queued or JobState.Running)
        {
            await Task.Delay(500);
        }

        Assert.Equal(JobState.Completed, job!.State);
        var output = Assert.Single(job.OutputFiles);
        Assert.True(File.Exists(output), output);
        Assert.True(new FileInfo(output).Length > 1024);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    /// <summary>测试用的真实进程执行器（与 Windows 端实现等价）。</summary>
    private sealed class ProcessRunner : IProcessRunner
    {
        public async Task<ProcessOutput> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken = default)
        {
            using var process = Create(executable, arguments, environment);
            if (!process.Start()) throw new InvalidOperationException("start failed");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessOutput(process.ExitCode, stdout.Result + stderr.Result);
        }

        public IProcessHandle Start(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment)
        {
            var process = Create(executable, arguments, environment);
            process.EnableRaisingEvents = true;
            if (!process.Start()) throw new InvalidOperationException("start failed");
            return new Handle(process);
        }

        private static System.Diagnostics.Process Create(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    if (pair.Value != null) startInfo.Environment[pair.Key] = pair.Value;
                }
            }
            return new System.Diagnostics.Process { StartInfo = startInfo };
        }

        private sealed class Handle : IProcessHandle
        {
            private readonly System.Diagnostics.Process _process;
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Handle(System.Diagnostics.Process process)
            {
                _process = process;
                _process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) OutputLine?.Invoke(e.Data);
                };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) OutputLine?.Invoke(e.Data);
                };
                _process.Exited += (_, _) => _exit.TrySetResult(_process.ExitCode);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            public event Action<string>? OutputLine;

            public Task<int> WaitForExitAsync() => _exit.Task;

            public void Kill()
            {
                try
                {
                    if (!_process.HasExited) _process.Kill(true);
                }
                catch (Exception)
                {
                }
            }

            public void RequestStop() => Kill();

            public void Dispose() => _process.Dispose();
        }
    }
}
