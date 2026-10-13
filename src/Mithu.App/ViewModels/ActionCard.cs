using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithu.App.Services;
using Mithu.Core.Desktop;

namespace Mithu.App.ViewModels;

public enum StepState
{
    Running,
    Done,
    Failed,

    /// <summary>Waiting for the user's Approve / Cancel.</summary>
    Waiting,
    Cancelled,
}

/// <summary>One step on an execution card ("◌ Moving 12 files", "✓ Opened Spotify", "? Move 12 files → Pictures").</summary>
public sealed partial class CardStep(string text, StepState state) : ObservableObject
{
    [ObservableProperty]
    private string _text = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsWaiting), nameof(IsCancelled))]
    private StepState _state = state;

    /// <summary>What's affected (file names) or why it failed; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    /// <summary>The question this step waits on (Confirm tier, or "Ask me before every action").</summary>
    public ApprovalRequest? Request { get; set; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsRunning => State == StepState.Running;

    public bool IsDone => State == StepState.Done;

    public bool IsFailed => State == StepState.Failed;

    public bool IsWaiting => State == StepState.Waiting;

    public bool IsCancelled => State == StepState.Cancelled;

    /// <summary>The state's icon: ◌ running, ✓ done, ⓘ failed, ? waiting, – cancelled.</summary>
    public string Glyph => State switch
    {
        StepState.Done => "",
        StepState.Failed => "",
        StepState.Waiting => "",
        StepState.Cancelled => "",
        _ => "",
    };
}

/// <summary>
/// The execution card: what Mithu does on the PC for one request, step by step. Open while working (with Stop and
/// any Approve / Cancel), then folded to one line ("✓ Moved 12 files → Pictures · Undo"); tap to open it again.
/// </summary>
public sealed partial class ActionCard(string title) : ObservableObject
{
    public string Title { get; } = title;

    public ObservableCollection<CardStep> Steps { get; } = [];

    /// <summary>Logged actions this card can undo, newest last.</summary>
    public List<(long Id, IReadOnlyList<FileOp> Ops)> Undoable { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo), nameof(ShowSteps), nameof(Summary), nameof(SummaryGlyph))]
    private bool _isWorking = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSteps))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo), nameof(Summary))]
    private bool _isUndone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private bool _isUndoing;

    /// <summary>The steps show while working, or when the folded card is opened.</summary>
    public bool ShowSteps => IsWorking || IsExpanded;

    public bool CanUndo => !IsWorking && !IsUndone && !IsUndoing && Undoable.Count > 0;

    public bool CanExpand => Steps.Count > 1 || Steps.Any(s => s.HasDetail);

    /// <summary>"✓ Opened Spotify" for one step; "Organize screenshots · 4 steps · 1 failed" for more.</summary>
    public string Summary
    {
        get
        {
            if (Steps.Count == 1 && !IsUndone)
            {
                return Steps[0].Text;
            }

            // Lead with what was actually done (the last change), not the wording of the request.
            var failed = Steps.Count(s => s.IsFailed);
            var main = Steps.LastOrDefault(s => s.IsDone && !s.Text.StartsWith("Searched", StringComparison.Ordinal))?.Text ?? Title;
            var text = $"{main} · {Steps.Count} step{(Steps.Count == 1 ? string.Empty : "s")}";
            text += failed > 0 ? $" · {failed} didn't work" : string.Empty;
            return IsUndone ? text + " · undone" : text;
        }
    }

    /// <summary>✓ all done, ⓘ something failed, – stopped or cancelled.</summary>
    public string SummaryGlyph =>
        Steps.Any(s => s.IsFailed) ? ""
        : Steps.Count > 0 && Steps.All(s => s.IsCancelled) ? ""
        : IsUndone ? ""
        : "";

    public bool SummaryIsWarning => Steps.Any(s => s.IsFailed);

    /// <summary>"2 done" while working.</summary>
    public string Progress => $"{Steps.Count(s => s.IsDone)} done";

    public CardStep Add(string text)
    {
        var step = new CardStep(text, StepState.Running);
        Steps.Add(step);
        Changed();
        return step;
    }

    /// <summary>Folds the card when the work is over (a step still running was stopped).</summary>
    public void Finish()
    {
        foreach (var step in Steps.Where(s => s.IsRunning || s.IsWaiting))
        {
            step.State = StepState.Cancelled;
            step.Detail = "Stopped";
        }

        IsWorking = false;
        IsExpanded = false;
        Changed();
    }

    public void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SummaryGlyph));
        OnPropertyChanged(nameof(SummaryIsWarning));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(CanUndo));
    }
}
