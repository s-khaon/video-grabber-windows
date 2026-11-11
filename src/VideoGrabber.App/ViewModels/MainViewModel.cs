using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly Dictionary<DownloadJob, TakeoverProgress> _trackedTakeovers = new();
    private DownloadJob? _selectedJob;

    public MainViewModel(DownloadManager downloads, AppSettings settings, ToolEnvironment tools, IFilePicker filePicker)
    {
        _downloads = downloads;
        _filePicker = filePicker;
        Settings = settings;
        Tools = tools;

        Jobs.CollectionChanged += OnJobsChanged;
        foreach (var job in Jobs) AttachJob(job);
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
        FinishEarlyCommand = new RelayCommand(() =>
        {
            if (SelectedJob != null) _downloads.FinishEarly(SelectedJob);
        });
        RemoveCommand = new RelayCommand(RemoveSelected);
        RevealCommand = new RelayCommand(RevealSelected);
        CancelAllCommand = new RelayCommand(() => _downloads.CancelAll());
        CancelAllTakeoversCommand = new RelayCommand(() => _downloads.CancelAllTakeovers());
        ClearFinishedCommand = new RelayCommand(() => _downloads.ClearFinished());
        RefreshToolsCommand = new RelayCommand(() => _ = RefreshToolsAsync());
        RepairLocalCommand = new RelayCommand(() => RepairRequested?.Invoke());
        BrowserCommand = new RelayCommand(() => BrowserRequested?.Invoke());
        CookieManagerCommand = new RelayCommand(() => CookieManagerRequested?.Invoke());
        ConverterCommand = new RelayCommand(() => ConverterRequested?.Invoke());
        OpenBrowserForJobCommand = new RelayCommand<DownloadJob>(job =>
        {
            var url = job == null ? null : DownloadManager.PageAddressFor(job);
            if (!string.IsNullOrEmpty(url)) BrowserForJobRequested?.Invoke(url);
        });
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

    public int ActiveTakeoverCount => Jobs.Count(job => job.Takeover is { IsActive: true });

    public bool HasActiveTakeover => ActiveTakeoverCount > 0;

    public string TakeoverSummary => HasActiveTakeover
        ? $"自动接管中：{ActiveTakeoverCount} 个任务"
        : "";

    public Visibility TakeoverBannerVisibility => HasActiveTakeover ? Visibility.Visible : Visibility.Collapsed;

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
    public RelayCommand FinishEarlyCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand CancelAllTakeoversCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand RefreshToolsCommand { get; }
    public RelayCommand RepairLocalCommand { get; }
    public RelayCommand BrowserCommand { get; }
    public RelayCommand CookieManagerCommand { get; }
    public RelayCommand ConverterCommand { get; }
    public RelayCommand<DownloadJob> OpenBrowserForJobCommand { get; }

    public event Action? AddDownloadRequested;
    public event Action? SettingsRequested;
    public event Action? RepairRequested;
    public event Action? BrowserRequested;
    public event Action? CookieManagerRequested;
    public event Action? ConverterRequested;
    public event Action<string>? BrowserForJobRequested;

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
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var job in _trackedTakeovers.Keys.ToList()) DetachJob(job);
            foreach (var job in Jobs) AttachJob(job);
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (DownloadJob job in e.OldItems) DetachJob(job);
            }
            if (e.NewItems != null)
            {
                foreach (DownloadJob job in e.NewItems) AttachJob(job);
            }
        }

        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(IsQueueEmpty));
        RaiseTakeoverStats();
    }

    private void AttachJob(DownloadJob job)
    {
        job.PropertyChanged -= OnJobPropertyChanged;
        job.PropertyChanged += OnJobPropertyChanged;
        SyncTakeoverSubscription(job);
    }

    private void DetachJob(DownloadJob job)
    {
        job.PropertyChanged -= OnJobPropertyChanged;
        if (_trackedTakeovers.Remove(job, out var takeover)) takeover.PropertyChanged -= OnTakeoverChanged;
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadJob.Takeover) && sender is DownloadJob job)
        {
            SyncTakeoverSubscription(job);
        }
    }

    /// <summary>接管对象会随重试被替换，这里保持订阅指向当前实例。</summary>
    private void SyncTakeoverSubscription(DownloadJob job)
    {
        _trackedTakeovers.TryGetValue(job, out var tracked);
        if (ReferenceEquals(tracked, job.Takeover)) return;

        if (tracked != null) tracked.PropertyChanged -= OnTakeoverChanged;
        if (job.Takeover is { } takeover)
        {
            _trackedTakeovers[job] = takeover;
            takeover.PropertyChanged += OnTakeoverChanged;
        }
        else
        {
            _trackedTakeovers.Remove(job);
        }
        RaiseTakeoverStats();
    }

    private void OnTakeoverChanged(object? sender, PropertyChangedEventArgs e) => RaiseTakeoverStats();

    private void RaiseTakeoverStats()
    {
        OnPropertyChanged(nameof(ActiveTakeoverCount));
        OnPropertyChanged(nameof(HasActiveTakeover));
        OnPropertyChanged(nameof(TakeoverSummary));
        OnPropertyChanged(nameof(TakeoverBannerVisibility));
    }

    private void RaiseToolStatus()
    {
        OnPropertyChanged(nameof(ToolStatusText));
        OnPropertyChanged(nameof(ToolWarningText));
        OnPropertyChanged(nameof(HasToolWarning));
    }
}
