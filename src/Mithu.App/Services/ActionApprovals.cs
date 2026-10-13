namespace Mithu.App.Services;

/// <summary>One "may I?" question waiting for the user's click.</summary>
public sealed class ApprovalRequest(string question, string? details = null)
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Question { get; } = question;

    /// <summary>Exactly what's affected ("IMG_0412.png, IMG_0413.png … (+10 more)"), or null.</summary>
    public string? Details { get; } = details;

    public Task<bool> Answer => _answer.Task;

    public bool IsAnswered => _answer.Task.IsCompleted;

    /// <summary>Only the user's click (or Stop, which says no) answers: the AI can never approve its own action.</summary>
    public void Decide(bool approved) => _answer.TrySetResult(approved);
}

/// <summary>
/// Asks the user before an action ("Ask me before every action", and later the Confirm tier). The chat shows each
/// question with Approve / Cancel; with no chat listening, the answer is no.
/// </summary>
public sealed class ActionApprovals
{
    public event Action<ApprovalRequest>? Requested;

    public Task<bool> RequestAsync(string question, string? details = null)
    {
        var request = new ApprovalRequest(question, details);
        if (Requested is null)
        {
            request.Decide(false);
        }
        else
        {
            Requested(request);
        }

        return request.Answer;
    }
}
