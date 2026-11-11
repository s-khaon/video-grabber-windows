using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Models;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

public partial class ConverterWindow : Window
{
    private readonly MediaConverter _converter;
    private readonly AppSettings _settings;
    private readonly IFilePicker _picker;
    private MediaConversionJob? _job;

    public ConverterWindow(MediaConverter converter, AppSettings settings, IFilePicker picker)
    {
        InitializeComponent();
        _converter = converter;
        _settings = settings;
        _picker = picker;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var draft = _converter.Draft;
        RemoteModeRadio.IsChecked = !draft.IsLocal;
        LocalModeRadio.IsChecked = draft.IsLocal;
        RemoteUrlBox.Text = draft.RemoteURL;
        LocalPathBox.Text = draft.LocalPath;
        FragmentDirectoryBox.Text = draft.FragmentDirectory;
        RefererBox.Text = draft.Referer;
        UserAgentBox.Text = draft.UserAgent;
        OutputDirectoryBox.Text = string.IsNullOrWhiteSpace(draft.OutputDirectory)
            ? _settings.DefaultSaveDirectory
            : draft.OutputDirectory;
        OutputNameBox.Text = draft.OutputName;
        UpdateSourcePanels();

        if (_converter.Current is { } job)
        {
            Attach(job);
        }
        else
        {
            ProgressCard.Visibility = Visibility.Collapsed;
        }
        UpdateButtons();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_job != null)
        {
            _job.PropertyChanged -= Job_PropertyChanged;
            _job.Logs.CollectionChanged -= Logs_CollectionChanged;
            _job = null;
        }
    }

    /// <summary>窗口已打开时从资源嗅探再次带入地址。</summary>
    public void ApplyPrefill()
    {
        if (_converter.Current?.IsRunning == true) return;
        var draft = _converter.Draft;
        RemoteModeRadio.IsChecked = !draft.IsLocal;
        LocalModeRadio.IsChecked = draft.IsLocal;
        RemoteUrlBox.Text = draft.RemoteURL;
        LocalPathBox.Text = draft.LocalPath;
        FragmentDirectoryBox.Text = draft.FragmentDirectory;
        RefererBox.Text = draft.Referer;
        UserAgentBox.Text = draft.UserAgent;
        if (!string.IsNullOrWhiteSpace(draft.OutputDirectory)) OutputDirectoryBox.Text = draft.OutputDirectory;
        OutputNameBox.Text = draft.OutputName;
        UpdateSourcePanels();
    }

    private void SourceMode_Changed(object sender, RoutedEventArgs e) => UpdateSourcePanels();

    private void UpdateSourcePanels()
    {
        if (RemotePanel == null || LocalPanel == null) return;
        var local = LocalModeRadio.IsChecked == true;
        RemotePanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var files = _picker.PickFiles("选择 m3u8 播放列表", "m3u8 播放列表|*.m3u8|所有文件|*.*");
        if (files.Count > 0) LocalPathBox.Text = files[0];
    }

    private void BrowseFragment_Click(object sender, RoutedEventArgs e)
    {
        var directory = _picker.PickFolder(FragmentDirectoryBox.Text, "选择分片目录");
        if (!string.IsNullOrEmpty(directory)) FragmentDirectoryBox.Text = directory;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var directory = _picker.PickFolder(OutputDirectoryBox.Text, "选择输出目录");
        if (!string.IsNullOrEmpty(directory)) OutputDirectoryBox.Text = directory;
    }

    private void CaptureDraft()
    {
        var draft = _converter.Draft;
        draft.IsLocal = LocalModeRadio.IsChecked == true;
        draft.RemoteURL = RemoteUrlBox.Text.Trim();
        draft.LocalPath = LocalPathBox.Text.Trim();
        draft.FragmentDirectory = FragmentDirectoryBox.Text.Trim();
        draft.Referer = RefererBox.Text.Trim();
        draft.UserAgent = UserAgentBox.Text.Trim();
        draft.OutputDirectory = OutputDirectoryBox.Text.Trim();
        draft.OutputName = OutputNameBox.Text.Trim();
    }

    private void StartCopy_Click(object sender, RoutedEventArgs e) => Start(ConversionMode.Copy);

    private void StartTranscode_Click(object sender, RoutedEventArgs e) => Start(ConversionMode.Transcode);

    private void Start(ConversionMode mode)
    {
        CaptureDraft();
        _converter.Start(mode);
        if (_converter.Current is { } job)
        {
            Attach(job);
        }
        UpdateButtons();
    }

    private void Attach(MediaConversionJob job)
    {
        if (_job != null)
        {
            _job.PropertyChanged -= Job_PropertyChanged;
            _job.Logs.CollectionChanged -= Logs_CollectionChanged;
        }
        _job = job;
        _job.PropertyChanged += Job_PropertyChanged;
        _job.Logs.CollectionChanged += Logs_CollectionChanged;
        ProgressCard.Visibility = Visibility.Visible;
        Render();
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Logs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        LogBox.Text = _job == null ? "" : string.Join("\n", _job.Logs);
        LogBox.ScrollToEnd();
    }

    private void Render()
    {
        if (_job is not { } job) return;
        StateText.Text = job.StateText;
        DetailText.Text = job.ProgressDetailText;
        ProgressBar.IsIndeterminate = job.IsRunning && !job.IsDeterminate;
        if (job.IsDeterminate)
        {
            ProgressBar.Value = job.Progress * 100;
        }
        OutputPathText.Text = string.IsNullOrEmpty(job.OutputPath) ? "" : "输出：" + job.OutputPath;
        CommandText.Text = job.CommandPreview;
        LogBox.Text = string.Join("\n", job.Logs);

        var failure = job.Failure;
        FailurePanel.Visibility = failure == null ? Visibility.Collapsed : Visibility.Visible;
        FailureText.Text = failure?.Message ?? "";
        FailureHint.Text = failure?.Hint ?? "";
        RetryTranscodeButton.Visibility = job.CanRetryWithTranscode ? Visibility.Visible : Visibility.Collapsed;
        ChooseOutputButton.Visibility = failure?.Kind == ConversionFailureKind.PermissionDenied
            ? Visibility.Visible
            : Visibility.Collapsed;
        RevealButton.Visibility = job.State == ConversionState.Completed ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var running = _converter.Current?.IsRunning == true;
        StartCopyButton.IsEnabled = !running;
        StartTranscodeButton.IsEnabled = !running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        InputGridEnabled(!running);
    }

    private void InputGridEnabled(bool enabled)
    {
        RemoteModeRadio.IsEnabled = enabled;
        LocalModeRadio.IsEnabled = enabled;
        RemoteUrlBox.IsEnabled = enabled;
        LocalPathBox.IsEnabled = enabled;
        FragmentDirectoryBox.IsEnabled = enabled;
        RefererBox.IsEnabled = enabled;
        UserAgentBox.IsEnabled = enabled;
        OutputDirectoryBox.IsEnabled = enabled;
        OutputNameBox.IsEnabled = enabled;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _converter.Cancel();
        Render();
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var directory = _picker.PickFolder(OutputDirectoryBox.Text, "选择输出目录");
        if (string.IsNullOrEmpty(directory)) return;
        OutputDirectoryBox.Text = directory;
        _converter.Draft.OutputDirectory = directory;
        FailurePanel.Visibility = Visibility.Collapsed;
    }

    private void RetryTranscode_Click(object sender, RoutedEventArgs e)
    {
        _converter.RetryWithTranscode();
        if (_converter.Current is { } job) Attach(job);
        UpdateButtons();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_job == null || string.IsNullOrEmpty(_job.OutputPath) || !System.IO.File.Exists(_job.OutputPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _job.OutputPath + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // 定位失败时不影响转换结果。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
