using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using VideoGrabber.App.Platform;
using VideoGrabber.App.Views;
using VideoGrabber.Core;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DownloadManager _downloads;
    private readonly IFilePicker _filePicker;
    private DownloadJob? _selectedJob;

    public MainViewModel(DownloadManager downloads, AppSettings settings, ToolEnvironment tools, IFilePicker filePicker)
    {
        _downloads = downloads;
        _filePicker = filePicker;
        Settings = settings;
        Tools = tools;

        Jobs.CollectionChanged += OnJobsChanged;
        Tools.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ToolEnvironment.StatusSummary)
                or nameof(ToolEnvironment.IsReady)
                or nameof(ToolEnvironment.InvalidationNotice))
            {
                RaiseToolStatus();
            }
        };

        AddDownloadCommand = new RelayCommand(() => AddDownloadRequested?.Invoke());
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        ResumeCommand = new RelayCommand(() =>
        {
            if (SelectedJob != null) _downloads.Resume(SelectedJob);
        });
        RetryCommand = new RelayCommand(() =>
        {
            if (SelectedJob != null) _downloads.Retry(SelectedJob);
        });
        CancelCommand = new RelayCommand(() =>
        {
            if (SelectedJob != null) _downloads.Cancel(SelectedJob);
        });
        RemoveCommand = new RelayCommand(RemoveSelected);
        RevealCommand = new RelayCommand(RevealSelected);
        CancelAllCommand = new RelayCommand(() => _downloads.CancelAll());
        ClearFinishedCommand = new RelayCommand(() => _downloads.ClearFinished());
        RefreshToolsCommand = new RelayCommand(() => _ = RefreshToolsAsync());
    }

    public AppSettings Settings { get; }
    public ToolEnvironment Tools { get; }
    public ObservableCollection<DownloadJob> Jobs => _downloads.Jobs;

    public DownloadJob? SelectedJob
    {
        get => _selectedJob;
        set => SetProperty(ref _selectedJob, value);
    }

    public string QueueHeader => $"下载队列（{Jobs.Count}）";

    public bool IsQueueEmpty => Jobs.Count == 0;

    public string QueueEmptyHint => "还没有任务\n点「新增下载」粘贴短视频 / 页面链接";

    public string ToolStatusText => Tools.StatusSummary;

    public string ToolWarningText
    {
        get
        {
            if (Tools.InvalidationNotice is { } notice) return notice;
            if (Tools.YtDlp == null && Tools.Ffmpeg == null)
            {
                return "未检测到 yt-dlp 与 ffmpeg，下载会失败。点「设置」指定路径，或执行 winget install yt-dlp.yt-dlp 与 winget install Gyan.FFmpeg。";
            }
            if (Tools.YtDlp == null) return "未检测到 yt-dlp，无法使用主要下载引擎。";
            if (Tools.Ffmpeg == null) return "未检测到 ffmpeg，合并 / 转码 / 直连下载不可用。";
            return Tools.FfprobeMissing ? "未检测到 ffprobe，产物校验会退化。" : "";
        }
    }

    public bool HasToolWarning => !string.IsNullOrEmpty(ToolWarningText);

    public RelayCommand AddDownloadCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand RefreshToolsCommand { get; }

    public event Action? AddDownloadRequested;
    public event Action? SettingsRequested;

    public void OpenDefaultDownloadFolder()
    {
        var directory = string.IsNullOrWhiteSpace(Settings.DefaultSaveDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Settings.DefaultSaveDirectory;
        _filePicker.RevealInExplorer(Array.Empty<string>(), directory);
    }

    private void RemoveSelected()
    {
        if (SelectedJob is not { } job) return;
        var confirm = System.Windows.MessageBox.Show(
            $"删除任务「{job.Title}」？\n临时文件与记录会一并删除，已下载完成的视频文件保留。",
            "删除任务",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        _downloads.Remove(job);
        if (ReferenceEquals(SelectedJob, job)) SelectedJob = null;
    }

    private void RevealSelected()
    {
        if (SelectedJob is not { } job) return;
        _filePicker.RevealInExplorer(job.OutputFiles.ToList(), job.SaveDirectory);
    }

    private async Task RefreshToolsAsync()
    {
        await Tools.RefreshAsync();
        _downloads.Reschedule();
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(IsQueueEmpty));
    }

    private void RaiseToolStatus()
    {
        OnPropertyChanged(nameof(ToolStatusText));
        OnPropertyChanged(nameof(ToolWarningText));
        OnPropertyChanged(nameof(HasToolWarning));
    }
}
