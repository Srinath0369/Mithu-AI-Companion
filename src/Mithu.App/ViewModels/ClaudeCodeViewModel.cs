using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithu.Core.ClaudeCode;
using Mithu.Infrastructure.ClaudeCode;

namespace Mithu.App.ViewModels;

/// <summary>A Claude Code session on Home: "Companion · Editing App.xaml.cs · 3 min".</summary>
public sealed partial class ClaudeSessionRow(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string _project = string.Empty;

    [ObservableProperty]
    private string _activity = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsNeedsYou), nameof(IsDone))]
    private ClaudeState _state;

    [ObservableProperty]
    private string _elapsed = string.Empty;

    public bool IsBusy => State is ClaudeState.Working or ClaudeState.Thinking;

    public bool IsNeedsYou => State == ClaudeState.NeedsYou;

    public bool IsDone => State == ClaudeState.Done;
}

/// <summary>
/// What Claude Code is doing, live: the sessions on Home (working first), and one line for Mithu's header
/// ("Claude Code · Companion · Running dotnet test"). Fed by <see cref="Services.ClaudeCodeService"/>. Kept in
/// memory only; nothing from Claude Code is saved or logged.
/// </summary>
public sealed partial class ClaudeCodeViewModel : ObservableObject
{
    private readonly ClaudeTracker _tracker = new();
    private readonly TimeProvider _time;
    private readonly TranscriptTail? _transcripts;
    private readonly DispatcherTimer _clock;

    public ClaudeCodeViewModel(TimeProvider time, TranscriptTail? transcripts = null)
    {
        _time = time;
        _transcripts = transcripts;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) =>
        {
            ReadTranscripts();
            Refresh();
        };
    }

    /// <summary>A request finished (the session, as it is now).</summary>
    public event Action<ClaudeSession>? Finished;

    /// <summary>Claude Code is waiting for the user's permission.</summary>
    public event Action<ClaudeSession>? NeedsYou;

    public ObservableCollection<ClaudeSessionRow> Sessions { get; } = [];

    /// <summary>Some session is working or waiting: the header shows <see cref="HeaderTitle"/> and <see cref="HeaderDetail"/>.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>"Claude Code · Companion": the header's title while it works.</summary>
    [ObservableProperty]
    private string _headerTitle = string.Empty;

    /// <summary>"Running the tests · 2 min": the header's line under the title while it works.</summary>
    [ObservableProperty]
    private string _headerDetail = string.Empty;

    /// <summary>What the busiest session is doing (null when none is working).</summary>
    [ObservableProperty]
    private ClaudeState? _busyState;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>One hook event (JSON), on the UI thread.</summary>
    public void OnEvent(string json)
    {
        if (ClaudeCodeEvents.Parse(json) is not { } e)
        {
            return;
        }

        // Tokens written so far count first (before "finished" adds them up, or a new request starts from zero).
        if (_tracker.Find(e.SessionId) is { } known)
        {
            ReadTranscript(known);
        }
        else
        {
            _transcripts?.ReadNew(e.TranscriptPath); // first sight: start following from here
        }

        var (session, newState) = _tracker.Apply(e, _time.GetUtcNow());
        Refresh();
        if (session is null)
        {
            return;
        }

        if (newState == ClaudeState.Done)
        {
            Finished?.Invoke(session);
        }
        else if (newState == ClaudeState.NeedsYou)
        {
            NeedsYou?.Invoke(session);
        }
    }

    /// <summary>Stopped listening: nothing to show.</summary>
    public void Clear()
    {
        Sessions.Clear();
        IsBusy = false;
        HeaderTitle = string.Empty;
        HeaderDetail = string.Empty;
        BusyState = null;
        _clock.Stop();
        OnPropertyChanged(nameof(HasSessions));
    }

    /// <summary>New token counts for the sessions at work.</summary>
    private void ReadTranscripts()
    {
        foreach (var s in _tracker.Visible(_time.GetUtcNow()).Where(s => s.State != ClaudeState.Done))
        {
            ReadTranscript(s);
        }
    }

    private void ReadTranscript(ClaudeSession s)
    {
        if (_transcripts is not null && s.TranscriptPath is { } path)
        {
            ClaudeTracker.AddTranscript(s, _transcripts.ReadNew(path));
        }
    }

    private void Refresh()
    {
        var now = _time.GetUtcNow();
        var visible = _tracker.Visible(now).Take(3).ToList();

        // Update rows in place (no flicker), add new ones, drop gone ones.
        foreach (var gone in Sessions.Where(r => visible.All(s => s.Id != r.Id)).ToList())
        {
            Sessions.Remove(gone);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var s = visible[i];
            var row = Sessions.FirstOrDefault(r => r.Id == s.Id);
            if (row is null)
            {
                row = new ClaudeSessionRow(s.Id);
                Sessions.Insert(Math.Min(i, Sessions.Count), row);
            }
            else if (Sessions.IndexOf(row) != i)
            {
                Sessions.Move(Sessions.IndexOf(row), i);
            }

            row.Project = s.Project;
            row.Activity = s.Activity;
            row.State = s.State;
            row.Elapsed = s.State == ClaudeState.Done && s.DoneUtc is { } done
                ? ClaudeTracker.Elapsed(now - done) + " ago"
                : ClaudeTracker.Progress(s, now);
        }

        var busy = visible.FirstOrDefault(s => s.State is ClaudeState.NeedsYou or ClaudeState.Working or ClaudeState.Thinking);
        IsBusy = busy is not null;
        BusyState = busy?.State;
        HeaderTitle = busy is null ? string.Empty : $"Claude Code · {busy.Project}";
        HeaderDetail = busy switch
        {
            null => string.Empty,
            { State: ClaudeState.NeedsYou } => busy.Activity.Replace("Needs your OK: ", string.Empty, StringComparison.Ordinal), // the title says it needs you
            _ => $"{ClaudeTracker.Progress(busy, now)} · {busy.Activity}",
        };
        OnPropertyChanged(nameof(HasSessions));

        // A running clock while it works; a slow one for "5 min ago".
        _clock.Interval = TimeSpan.FromSeconds(busy is null ? 15 : 1);
        if (Sessions.Count > 0)
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }
}
