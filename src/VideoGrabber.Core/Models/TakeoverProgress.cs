using System.Collections.ObjectModel;

namespace VideoGrabber.Core.Models;

/// <summary>自动接管的一次步骤，详情页按时间线展示。</summary>
public sealed class TakeoverStep : ObservableObject
{
    private string _status = "info";

    public TakeoverStep(string text, string status = "info")
    {
        Text = text;
        CreatedAt = DateTime.Now;
        _status = status;
    }

    public string Text { get; }
    public DateTime CreatedAt { get; }

    /// <summary>info / running / done / failed。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsDone));
        }
    }

    public string Glyph => _status switch
    {
        "running" => "\uE823",
        "done" => "\uE930",
        "failed" => "\uE7BA",
        _ => "\uE946"
    };

    public bool IsRunning => _status == "running";
    public bool IsFailed => _status == "failed";
    public bool IsDone => _status == "done";
}

/// <summary>
/// 自动接管进度：隐藏浏览器 → 加载页面 → 自动播放嗅探 → 挑选地址 → 重新下载。
/// 挂在任务上，供主窗口详情区展示。
/// </summary>
public sealed class TakeoverProgress : ObservableObject
{
    private TakeoverPhase _phase = TakeoverPhase.Preparing;
    private int _candidateCount;
    private string? _selectedURL;
    private string? _selectedDetail;
    private string? _pausedNote;

    public ObservableCollection<TakeoverStep> Steps { get; } = new();
    public DateTime StartedAt { get; } = DateTime.Now;

    public TakeoverPhase Phase
    {
        get => _phase;
        set
        {
            if (!SetProperty(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(NeedsUserAction));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(IsFinished));
        }
    }

    public int CandidateCount
    {
        get => _candidateCount;
        set
        {
            if (!SetProperty(ref _candidateCount, value)) return;
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public string? SelectedURL
    {
        get => _selectedURL;
        set => SetProperty(ref _selectedURL, value);
    }

    public string? SelectedDetail
    {
        get => _selectedDetail;
        set => SetProperty(ref _selectedDetail, value);
    }

    /// <summary>等待人工验证 / 登录时展示的说明。</summary>
    public string? PausedNote
    {
        get => _pausedNote;
        set
        {
            if (!SetProperty(ref _pausedNote, value)) return;
            OnPropertyChanged(nameof(HasPausedNote));
        }
    }

    public bool HasPausedNote => !string.IsNullOrEmpty(_pausedNote);

    public bool IsActive => _phase.IsActive();
    public bool IsFinished => !IsActive;
    public bool NeedsUserAction => _phase is TakeoverPhase.NeedsVerification or TakeoverPhase.NeedsLogin;

    public string Title => _phase.Title();

    public string Glyph => _phase switch
    {
        TakeoverPhase.Succeeded => "\uE930",
        TakeoverPhase.Failed => "\uE7BA",
        TakeoverPhase.Cancelled => "\uE711",
        TakeoverPhase.NeedsVerification or TakeoverPhase.NeedsLogin => "\uE72E",
        TakeoverPhase.Retrying => "\uE896",
        _ => "\uE823"
    };

    public string SummaryText => _phase switch
    {
        TakeoverPhase.Preparing => "准备后台浏览器…",
        TakeoverPhase.Loading => "加载页面…",
        TakeoverPhase.Playing => $"自动播放并嗅探（已捕获 {CandidateCount} 个）",
        TakeoverPhase.Selecting => $"从 {CandidateCount} 个候选中挑选最优地址…",
        TakeoverPhase.Retrying => "已选中地址，正在重新下载…",
        TakeoverPhase.NeedsVerification => "等待完成 Cloudflare 验证（完成后自动继续）",
        TakeoverPhase.NeedsLogin => "等待登录（完成后自动继续）",
        TakeoverPhase.Succeeded => "已接管并下载完成",
        TakeoverPhase.Failed => "自动接管失败",
        TakeoverPhase.Cancelled => "已中止",
        _ => ""
    };

    public TakeoverStep Add(string text, string status = "info")
    {
        var step = new TakeoverStep(text, status);
        Steps.Add(step);
        return step;
    }

    public void Mark(TakeoverStep step, string status) => step.Status = status;

    public void FinishRunningSteps(string status = "done")
    {
        foreach (var step in Steps.Where(item => item.IsRunning)) step.Status = status;
    }
}

public enum TakeoverPhase
{
    Preparing,
    Loading,
    Playing,
    Selecting,
    Retrying,
    NeedsVerification,
    NeedsLogin,
    Succeeded,
    Failed,
    Cancelled
}

public static class TakeoverPhaseText
{
    /// <summary>仍在自动接管流程中（包括等待人工验证）。</summary>
    public static bool IsActive(this TakeoverPhase phase) => phase is
        TakeoverPhase.Preparing or TakeoverPhase.Loading or TakeoverPhase.Playing or
        TakeoverPhase.Selecting or TakeoverPhase.Retrying or TakeoverPhase.NeedsVerification or
        TakeoverPhase.NeedsLogin;

    public static string Title(this TakeoverPhase phase) => phase switch
    {
        TakeoverPhase.Preparing => "准备后台浏览器",
        TakeoverPhase.Loading => "加载页面",
        TakeoverPhase.Playing => "自动播放并嗅探",
        TakeoverPhase.Selecting => "挑选最优地址",
        TakeoverPhase.Retrying => "自动重新下载",
        TakeoverPhase.NeedsVerification => "等待人机验证",
        TakeoverPhase.NeedsLogin => "等待登录",
        TakeoverPhase.Succeeded => "接管成功",
        TakeoverPhase.Failed => "接管失败",
        TakeoverPhase.Cancelled => "已中止",
        _ => ""
    };
}
