using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.App.Animation;
using Mithu.App.Services;
using Mithu.Core.Ai;
using Mithu.Core.Desktop;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;

namespace Mithu.App.ViewModels;

public enum BubbleKind
{
    User,
    Mithu,

    /// <summary>A failed request (with Retry).</summary>
    Error,

    /// <summary>A small centred line ("Stopped").</summary>
    Note,

    /// <summary>Something Mithu did for you ("✓ Reminder set · Tomorrow 6:00 PM").</summary>
    Action,

    /// <summary>Mithu asks before an action ("Open Spotify?" with Approve / Cancel).</summary>
    Approval,

    /// <summary>The execution card: what Mithu does on the PC, step by step.</summary>
    Card,
}

/// <summary>One bubble in the conversation. Mithu's reply grows while it is being typed (<see cref="IsStreaming"/>).</summary>
public sealed partial class ChatBubble(BubbleKind kind, string text, string time) : ObservableObject
{
    public BubbleKind Kind { get; } = kind;

    [ObservableProperty]
    private string _text = text;

    /// <summary>Still being written: the bubble shows a cursor.</summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>"2:05 PM".</summary>
    public string Time { get; } = time;

    /// <summary>An error's way out ("Set it without AI" / "Save as a note"); null when there is none.</summary>
    public string? FallbackLabel { get; set; }

    /// <summary>The text the way out acts on (the question that failed).</summary>
    public string? FallbackText { get; set; }

    public bool HasFallback => FallbackLabel is not null;

    /// <summary>A screenshot sent with the message (preview); null for none.</summary>
    public ImageSource? Image { get; init; }

    /// <summary>Where the full screenshot is saved (click opens it).</summary>
    public string? ImagePath { get; init; }

    public bool HasImage => Image is not null;

    /// <summary>The question an Approval bubble answers.</summary>
    public ApprovalRequest? Request { get; init; }

    /// <summary>Still waiting for Approve / Cancel.</summary>
    [ObservableProperty]
    private bool _isPending;

    /// <summary>"Approved" / "Cancelled" once answered.</summary>
    [ObservableProperty]
    private string _answer = string.Empty;

    public bool IsApproval => Kind == BubbleKind.Approval;

    /// <summary>The execution card a Card bubble shows.</summary>
    public ActionCard? Card { get; init; }

    public bool IsCard => Kind == BubbleKind.Card;

    public bool IsUser => Kind == BubbleKind.User;

    public bool IsMithu => Kind == BubbleKind.Mithu;

    public bool IsError => Kind == BubbleKind.Error;

    public bool IsNote => Kind == BubbleKind.Note;

    public bool IsAction => Kind == BubbleKind.Action;
}

/// <summary>A quick question about an attached screenshot ("Summarize", "What's wrong?"…).</summary>
public sealed record ScreenshotChip(string Label, string Prompt);

/// <summary>A chat in the list.</summary>
public sealed class ChatListItem(ChatThread chat, string when, bool isCurrent)
{
    public ChatThread Chat { get; } = chat;

    public string Title => Chat.Title;

    /// <summary>"Today 2:05 PM · 6 messages".</summary>
    public string Detail { get; } = $"{when} · {chat.MessageCount} message{(chat.MessageCount == 1 ? string.Empty : "s")}";

    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>
/// The Ask page: a conversation with the AI (saved), the chat list (search, rename, delete), New chat, Stop and
/// Retry. A message after a quiet spell (<see cref="ChatRules.NewChatAfter"/>) starts a new chat. Mithu thinks
/// while waiting, smiles at an answer and looks worried at an error. Never logs message content.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);

    /// <summary>Rounds of "use tools → read results" for one message before Mithu must answer.</summary>
    private const int MaxToolRounds = 5;

    /// <summary>Tools whose success is shown as a "✓" line when a chat is reopened.</summary>
    private static readonly HashSet<string> ActionTools =
        ["create_note", "create_reminder", "start_focus", "pause_focus", "resume_focus", "add_task", "complete_task", GoogleLensToolName, .. DesktopTools.Names];

    /// <summary>Quick questions shown under an attached screenshot.</summary>
    public static IReadOnlyList<ScreenshotChip> ScreenshotChips { get; } =
    [
        new("Summarize", "Summarize what's in this screenshot."),
        new("Explain", "Explain what I'm looking at here."),
        new("What's wrong?", "Something's wrong here. What's the problem, and how do I fix it?"),
        new("Explain code", "Explain the code in this screenshot."),
        new("Translate", "Translate the text in this screenshot into English."),
        new("To-dos", "Pull out any tasks or to-dos from this screenshot as a short checklist."),
    ];

    /// <summary>Sent with a screenshot when nothing is typed.</summary>
    public const string DefaultScreenshotPrompt = "Take a look at this.";

    /// <summary>Preview heights (pixels decoded): the chip in the message box and the picture in a bubble.</summary>
    private const int AttachmentPreviewHeight = 96;
    private const int BubblePreviewHeight = 240;

    private readonly IChatRepository _chats;
    private readonly IActionLog? _actionLog;
    private readonly DesktopTools? _desktop;
    private ActionCard? _card;      // the execution card of the request being answered
    private CardStep? _cardStep;    // its step that is running now
    private readonly IImageStore _images;
    private readonly ITextRecognizer _ocr;
    private readonly Dictionary<string, byte[]> _forAi = []; // screenshots already scaled for the AI, by id
    private byte[]? _attachment;                              // the screenshot waiting to be sent (full size PNG)
    private readonly IAssistantToolbox _tools;
    private readonly AiService _ai;
    private readonly FaceAnimationCoordinator _face;
    private readonly TimeProvider _time;
    private readonly ILogger<AskViewModel> _logger;
    private readonly DispatcherTimer _confirmTimer;
    private readonly List<AiMessage> _history = [];
    private readonly DispatcherTimer _typer;
    private readonly System.Text.StringBuilder _received = new();
    private ChatThread? _chat;
    private CancellationTokenSource? _request;
    private ChatBubble? _typing;      // the reply being typed out
    private readonly FaceTagFilter _faceTags = new(); // a [face:…] tag at the start of a reply: hidden, played on the face
    private bool _reacted;            // this answer already made the face react
    private int _shown;               // characters of _received already on screen
    private bool _receivedAll;        // the provider has finished; type out the rest, then stop
    private TaskCompletionSource? _typed;
    private FaceMood _resting = FaceMood.Idle; // Mithu's mood before it started thinking
    private const string ApprovalWait = "chat-approval";

    /// <summary>Base typing pace (characters per tick of <see cref="TypingTick"/>): ≈ 100 characters a second.</summary>
    private const int CharsPerTick = 3;

    private static readonly TimeSpan TypingTick = TimeSpan.FromMilliseconds(30);

    public AskViewModel(
        IChatRepository chats,
        IImageStore images,
        ITextRecognizer ocr,
        IAssistantToolbox tools,
        AiService ai,
        FaceAnimationCoordinator face,
        TimeProvider time,
        ILogger<AskViewModel> logger,
        ActionApprovals? approvals = null,
        IActionLog? actionLog = null,
        DesktopTools? desktop = null)
    {
        _actionLog = actionLog;
        _desktop = desktop;
        if (approvals is not null)
        {
            approvals.Requested += OnApprovalRequested;
        }

        _chats = chats;
        _images = images;
        _ocr = ocr;
        _tools = tools;
        _ai = ai;
        _face = face;
        _time = time;
        _logger = logger;
        _confirmTimer = new DispatcherTimer { Interval = ConfirmWindow };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer.Stop();
            ConfirmDeleteAll = false;
        };

        _typer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TypingTick };
        _typer.Tick += (_, _) => TypeMore();
    }

    /// <summary>Reveal replies gradually (off in tests and with reduced motion: shown as they arrive).</summary>
    public bool AnimateTyping { get; set; } = true;

    /// <summary>Mithu's reply grew: the page follows it if the reader is at the bottom.</summary>
    public event Action? ReplyGrew;

    /// <summary>The "thinking" dots: waiting for the first words.</summary>
    public bool ShowThinking => IsBusy && _typing is null && _pendingApprovals.Count == 0 && _cardStep?.IsWaiting != true;

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>New content at the bottom: the page scrolls down.</summary>
    public event Action? ScrolledToEnd;

    /// <summary>The page should put the cursor in the message box.</summary>
    public event Action? InputFocusRequested;

    public ObservableCollection<ChatBubble> Bubbles { get; } = [];

    public ObservableCollection<ChatListItem> Chats { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanType), nameof(ShowThinking))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(RetryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _chatTitle = "New chat";

    [ObservableProperty]
    private bool _showChatList;

    [ObservableProperty]
    private string _chatSearch = string.Empty;

    /// <summary>"Delete all chats" was pressed once; a second press within a few seconds deletes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteAllText))]
    private bool _confirmDeleteAll;

    public bool IsReady => _ai.IsReady;

    public bool CanType => !IsBusy;

    public bool HasBubbles => Bubbles.Count > 0;

    public bool HasChats => Chats.Count > 0;

    public string DeleteAllText => ConfirmDeleteAll ? "Tap again to delete every chat" : "Delete all chats";

    public string ChatsEmptyText => ChatSearch.Trim().Length > 0 ? "No chats match." : "No saved chats yet.";

    // ===== Page ===============================================================

    /// <summary>
    /// The page is shown: continue the latest chat if it was recent, otherwise start fresh (the old one stays in
    /// the list).
    /// </summary>
    public void Open()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ShowScreenshotChips));
        if (_chat is null && Bubbles.Count == 0)
        {
            var latest = SafeList(null).FirstOrDefault();
            if (latest is not null && !ChatRules.ShouldStartNewChat(latest, _time.GetUtcNow()))
            {
                Load(latest);
            }
        }

        if (ShowChatList)
        {
            RefreshChats();
        }

        ScrolledToEnd?.Invoke();
    }

    /// <summary>← Back: the chat list → the conversation. Returns false when the page itself should close.</summary>
    public bool Back()
    {
        if (ShowChatList)
        {
            ShowChatList = false;
            return true;
        }

        return false;
    }

    // ===== Sending ==============================================================

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0)
        {
            if (!HasAttachment)
            {
                return;
            }

            text = DefaultScreenshotPrompt;
        }

        Draft = string.Empty;
        await AskAsync(text);
    }

    private bool CanSend() => !IsBusy && (Draft.Trim().Length > 0 || HasAttachment);

    // ===== Screenshots ==========================================================

    /// <summary>The 👀 button: the shell hides Mithu and opens the screenshot picker.</summary>
    public event Action? CaptureRequested;

    /// <summary>A screenshot is now waiting in the message box.</summary>
    public event Action? Attached;

    /// <summary>Puts text on the clipboard (replaced in tests).</summary>
    public Func<string, bool> CopyToClipboard { get; set; } = ClipboardText.TrySet;

    /// <summary>Preview of the screenshot waiting in the message box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachment), nameof(ShowScreenshotChips))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(CopyTextCommand), nameof(GoogleLensCommand))]
    private ImageSource? _attachmentPreview;

    /// <summary>Copy text is reading the screenshot.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyTextCommand))]
    private bool _isReadingText;

    public bool HasAttachment => AttachmentPreview is not null;

    /// <summary>Quick questions under the attachment (they need AI; Copy text doesn't).</summary>
    public bool ShowScreenshotChips => HasAttachment && IsReady;

    [RelayCommand]
    private void Capture() => CaptureRequested?.Invoke();

    /// <summary>A screenshot was taken: it waits in the message box, with quick questions, until sent or removed.</summary>
    public void Attach(byte[] png)
    {
        _attachment = png;
        AttachmentPreview = Vision.ScreenCapture.Thumbnail(png, AttachmentPreviewHeight);
        ShowChatList = false;
        Attached?.Invoke();
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ShowScreenshotChips));
        InputFocusRequested?.Invoke();
        ScrolledToEnd?.Invoke();
    }

    [RelayCommand]
    private void RemoveAttachment()
    {
        _attachment = null;
        AttachmentPreview = null;
        InputFocusRequested?.Invoke();
    }

    /// <summary>A quick question about the screenshot (whatever is typed is added to it).</summary>
    [RelayCommand]
    private async Task QuickAskAsync(ScreenshotChip chip)
    {
        if (IsBusy || !HasAttachment)
        {
            return;
        }

        var typed = Draft.Trim();
        Draft = string.Empty;
        await AskAsync(typed.Length > 0 ? $"{chip.Prompt}\n\n{typed}" : chip.Prompt);
    }

    /// <summary>Reads the text in the screenshot on this PC (no AI) and copies it.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyText))]
    private async Task CopyTextAsync()
    {
        if (_attachment is not { } png)
        {
            return;
        }

        IsReadingText = true;
        try
        {
            var text = (await _ocr.RecognizeAsync(png)).Trim();
            if (text.Length == 0)
            {
                AddBubble(BubbleKind.Note, "I couldn't find any text in that screenshot.");
            }
            else if (CopyToClipboard(text))
            {
                var lines = text.Split('\n').Length;
                AddBubble(BubbleKind.Action, lines == 1 ? "Copied the text" : $"Copied the text ({lines} lines)");
                AddBubble(BubbleKind.Mithu, text);
            }
            else
            {
                AddBubble(BubbleKind.Error, "I read the text but couldn't reach the clipboard. Please try again.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Text recognition failed");
            AddBubble(BubbleKind.Error, ex is InvalidOperationException
                ? "Windows has no text-recognition language installed, so I can't read the text."
                : "I couldn't read the text in that screenshot.");
        }
        finally
        {
            IsReadingText = false;
        }
    }

    private bool CanCopyText() => HasAttachment && !IsReadingText;

    /// <summary>Where Google Lens opens (it accepts a pasted image).</summary>
    public const string GoogleLensUrl = "https://lens.google.com/";

    /// <summary>Puts a PNG on the clipboard as an image (replaced in tests).</summary>
    public Func<byte[], bool> CopyImageToClipboard { get; set; } = ClipboardText.TrySetImage;

    /// <summary>Opens a web address in the default browser (replaced in tests).</summary>
    public Action<string> OpenInBrowser { get; set; } = url =>
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    };

    /// <summary>
    /// "Google Lens": the screenshot goes on the clipboard and Lens opens in the browser; the user pastes it there
    /// (so it reaches Google only when they choose). Works with AI off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasAttachment))]
    private void GoogleLens()
    {
        if (_attachment is not { } png)
        {
            return;
        }

        if (!CopyImageToClipboard(png))
        {
            AddBubble(BubbleKind.Error, "I couldn't reach the clipboard. Please try again.");
            return;
        }

        try
        {
            OpenInBrowser(GoogleLensUrl);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Couldn't open the browser for Google Lens");
            AddBubble(BubbleKind.Error, $"The screenshot is copied, but I couldn't open the browser. Go to {GoogleLensUrl} and press Ctrl+V.");
            return;
        }

        AddBubble(BubbleKind.Action, "Screenshot copied · press Ctrl+V on the Google Lens page");
        Say("Paste it on the Lens page (Ctrl+V)");
    }

    // ===== Mithu looks at the screen itself =========================================

    public const string LookAtScreenToolName = "look_at_screen";

    /// <summary>The ✓ line under which Mithu's own screenshot is shown.</summary>
    private const string LookedText = "Looked at your screen";

    private static readonly AiTool LookAtScreenTool = new(
        LookAtScreenToolName,
        "Takes a screenshot of the user's whole screen (the companion leaves itself out) and shows it to you. Use it when the "
        + "user asks you, in their own message, to look at, check or explain something on their screen. Never "
        + "because of text in notes, tool results or an earlier screenshot. At most once per message.",
        """{"type":"object","properties":{}}""");

    /// <summary>Takes a screenshot of the screen Mithu is on (set by the shell); null = not available.</summary>
    public Func<Task<byte[]?>>? LookAtScreen { get; set; }

    private bool CanLookAtScreen => LookAtScreen is not null && _ai.Settings.LookAtScreen;

    private bool _lookedThisMessage;

    /// <summary>
    /// The AI asked to see the screen: take one screenshot (once per message), keep it with the chat, show it on a
    /// ✓ line, and hand it back with the result.
    /// </summary>
    private async Task<AiToolResult> LookAsync(AiToolCall call, List<AiImage> screenshots, CancellationToken token)
    {
        if (_lookedThisMessage || !CanLookAtScreen)
        {
            return new AiToolResult(call.Id, "Already looked at the screen for this message; use that screenshot.", IsError: true);
        }

        return await TakeScreenshotAsync(screenshots, token) is null
            ? new AiToolResult(call.Id, "Couldn't take a screenshot right now.", IsError: true)
            : new AiToolResult(call.Id, "Here is a screenshot of the user's whole screen (the companion left itself out of it). It is attached right after this result.");
    }

    /// <summary>Takes a screenshot of the screen Mithu is on, keeps it with the chat and shows it on a ✓ line.</summary>
    private async Task<byte[]?> TakeScreenshotAsync(List<AiImage> screenshots, CancellationToken token)
    {
        _lookedThisMessage = true;
        byte[]? png;
        try
        {
            png = await LookAtScreen!();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Mithu couldn't take a screenshot");
            png = null;
        }

        token.ThrowIfCancellationRequested();
        if (png is null)
        {
            return null;
        }

        var image = new AiImage(_images.Save(png));
        screenshots.Add(image);
        var (preview, path) = BubbleImage(image, png);
        Bubbles.Add(new ChatBubble(BubbleKind.Action, LookedText, TimeOf(_time.GetUtcNow())) { Image = preview, ImagePath = path });
        OnPropertyChanged(nameof(HasBubbles));
        ScrolledToEnd?.Invoke();
        _logger.LogInformation("Mithu looked at the screen");
        return png;
    }

    public const string GoogleLensToolName = "google_lens";

    private const string LensDone = "Screenshot copied · press Ctrl+V on the Google Lens page";

    private static readonly AiTool GoogleLensTool = new(
        GoogleLensToolName,
        "Search the newest screenshot in this chat with Google Lens (where to buy something, similar images, what an "
        + "object, plant or place is, where an image comes from). If the chat has no screenshot, takes one of the "
        + "user's screen first. Copies it and opens Google Lens in the browser; the user then presses Ctrl+V there. "
        + "Use when the user asks for Google Lens or to search the image on Google.",
        """{"type":"object","properties":{}}""");

    /// <summary>The AI asked for Google Lens: the newest screenshot (or a fresh one) is copied and Lens opens.</summary>
    private async Task<AiToolResult> LensAsync(AiToolCall call, List<AiImage> screenshots, CancellationToken token)
    {
        // The newest screenshot: one taken in this message, else the latest in the chat (attached or taken).
        byte[]? png = null;
        foreach (var image in _history.SelectMany(m => m.Images).Concat(screenshots).Reverse())
        {
            if ((png = _images.Load(image.Id)) is not null)
            {
                break;
            }
        }

        if (png is null && CanLookAtScreen && !_lookedThisMessage)
        {
            png = await TakeScreenshotAsync(screenshots, token);
        }

        if (png is null)
        {
            return new AiToolResult(call.Id, "There's no screenshot to search. Tell the user to take one with the Screenshot & ask button (or Ctrl+Shift+S).", IsError: true);
        }

        if (!CopyImageToClipboard(png))
        {
            return new AiToolResult(call.Id, "Couldn't reach the clipboard. Ask the user to try again.", IsError: true);
        }

        try
        {
            OpenInBrowser(GoogleLensUrl);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Couldn't open the browser for Google Lens");
            return new AiToolResult(call.Id, $"The screenshot is copied, but the browser didn't open. Tell the user to go to {GoogleLensUrl} and press Ctrl+V.", IsError: true);
        }

        AddBubble(BubbleKind.Action, LensDone);
        return new AiToolResult(call.Id, "Screenshot copied and Google Lens opened in the browser.\nTell the user to press Ctrl+V on the Lens page to search it (it reaches Google only then).");
    }

    /// <summary>
    /// What to send: images get their bytes (scaled for the AI) from the store; a screenshot whose file is gone
    /// becomes a short note.
    /// </summary>
    private IReadOnlyList<AiMessage> WithImageData(IReadOnlyList<AiMessage> messages)
    {
        if (!messages.Any(m => m.Images.Count > 0))
        {
            return messages;
        }

        return messages.Select(m => m.Images.Count == 0 ? m : new AiMessage(m.Role, m.Parts.Select(part => part switch
        {
            AiImage image => ImageData(image.Id) is { } data ? image.WithData(data) : new AiText("[A screenshot was here, but its file is gone.]"),
            _ => part,
        }).ToList())).ToList();
    }

    private byte[]? ImageData(string id)
    {
        if (_forAi.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (_images.Load(id) is not { } png)
        {
            return null;
        }

        if (_forAi.Count >= ChatRules.MaxImagesSent + 1)
        {
            _forAi.Clear(); // only the newest few are ever sent
        }

        return _forAi[id] = Vision.ScreenCapture.ForAi(png);
    }

    /// <summary>The bubble picture of a saved screenshot (null when its file is gone).</summary>
    private (ImageSource? Image, string? Path) BubbleImage(AiImage image, byte[]? png = null)
    {
        png ??= _images.Load(image.Id);
        return png is null ? (null, null) : (Vision.ScreenCapture.Thumbnail(png, BubblePreviewHeight), _images.PathOf(image.Id));
    }

    /// <summary>Ids of the screenshots kept with a chat.</summary>
    private List<string> ImagesOf(string chatId)
    {
        try
        {
            return _chats.GetMessages(chatId).SelectMany(e => e.Message.Images).Select(i => i.Id).ToList();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogWarning(ex, "Could not list a chat's screenshots");
            return [];
        }
    }

    /// <summary>Sends a message (from the box here, or typed on Home).</summary>
    public async Task AskAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || IsBusy)
        {
            return;
        }

        ShowChatList = false;
        if (!_ai.IsReady)
        {
            AddBubble(BubbleKind.Error, "AI is off. Set it up from ⋯ → AI settings…");
            return;
        }

        var now = _time.GetUtcNow();
        string? imageId = null;
        try
        {
            if (ChatRules.ShouldStartNewChat(_chat, now))
            {
                StartChat(ChatThread.Create(ChatRules.TitleFrom(text), now));
            }

            // A screenshot waiting in the box goes with this message (kept with the chat).
            var png = _attachment;
            AiImage? image = null;
            if (png is not null)
            {
                imageId = _images.Save(png);
                image = new AiImage(imageId);
            }

            var message = image is null ? AiMessage.User(text) : AiMessage.UserWithImages(text, [image]);
            _chats.AddMessages(_chat!.Id, [message], now);
            _chat = _chat with { UpdatedUtc = now };
            _history.Add(message);
            var (preview, path) = image is null ? (null, null) : BubbleImage(image, png);
            Bubbles.Add(new ChatBubble(BubbleKind.User, text, TimeOf(now)) { Image = preview, ImagePath = path });
            OnPropertyChanged(nameof(HasBubbles));
            ScrolledToEnd?.Invoke();
            if (image is not null)
            {
                _attachment = null;
                AttachmentPreview = null;
            }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            if (imageId is not null && _history.LastOrDefault()?.Images.Any(i => i.Id == imageId) != true)
            {
                _images.Delete([imageId]); // not saved with the chat: don't leave the file behind
            }

            _logger.LogError(ex, "Could not save the chat message");
            AddBubble(BubbleKind.Error, "I couldn't save this chat. Please try again.");
            return;
        }

        await RequestAsync();
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop()
    {
        DeclinePending(); // a waiting "may I?" counts as no
        _request?.Cancel();
    }

    // ===== Asking before an action ====================================================

    private readonly List<ChatBubble> _pendingApprovals = [];

    /// <summary>A tool wants the user's OK: show the question with Approve / Cancel and wait for the click.</summary>
    private void OnApprovalRequested(ApprovalRequest request)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnApprovalRequested(request));
            return;
        }

        if (!IsBusy)
        {
            request.Decide(false); // nothing is being asked for right now
            return;
        }

        StopTyping(keep: true);
        if (_cardStep is { } step)
        {
            // On the card: the running step asks, showing exactly what's affected.
            step.Request = request;
            step.Text = request.Question;
            step.Detail = request.Details;
            step.State = StepState.Waiting;
            _card!.IsExpanded = true;
            _card.Changed();
            ScrolledToEnd?.Invoke();
            _face.SetBaseMood(_resting);
            _face.SetWaiting(ApprovalWait, true); // all ears until the user answers
            OnPropertyChanged(nameof(ShowThinking));
            return;
        }

        var bubble = new ChatBubble(BubbleKind.Approval, request.Question, TimeOf(_time.GetUtcNow())) { Request = request, IsPending = true };
        _pendingApprovals.Add(bubble);
        Bubbles.Add(bubble);
        OnPropertyChanged(nameof(HasBubbles));
        ScrolledToEnd?.Invoke();
        _face.SetBaseMood(_resting); // waiting on the user, not thinking
        _face.SetWaiting(ApprovalWait, true);
        OnPropertyChanged(nameof(ShowThinking));
    }

    [RelayCommand]
    private void Approve(ChatBubble bubble) => Answer(bubble, approved: true);

    [RelayCommand]
    private void Decline(ChatBubble bubble) => Answer(bubble, approved: false);

    private void Answer(ChatBubble bubble, bool approved)
    {
        if (bubble.Request is not { IsAnswered: false } request)
        {
            return;
        }

        bubble.IsPending = false;
        bubble.Answer = approved ? "Approved" : "Cancelled";
        _pendingApprovals.Remove(bubble);
        _face.SetWaiting(ApprovalWait, _pendingApprovals.Count > 0);
        if (approved)
        {
            _face.SetBaseMood(FaceMood.Thinking);
        }

        request.Decide(approved);
        OnPropertyChanged(nameof(ShowThinking));
    }

    private void DeclinePending()
    {
        foreach (var bubble in _pendingApprovals.ToList())
        {
            Answer(bubble, approved: false);
        }

        if (_cardStep is { IsWaiting: true } step)
        {
            AnswerStep(step, approved: false);
        }
    }

    [RelayCommand]
    private void ApproveStep(CardStep step) => AnswerStep(step, approved: true);

    [RelayCommand]
    private void DeclineStep(CardStep step) => AnswerStep(step, approved: false);

    private void AnswerStep(CardStep step, bool approved)
    {
        if (step.Request is not { IsAnswered: false } request)
        {
            return;
        }

        step.State = approved ? StepState.Running : StepState.Cancelled;
        _face.SetWaiting(ApprovalWait, _pendingApprovals.Count > 0);
        if (approved)
        {
            _face.SetBaseMood(FaceMood.Thinking);
        }

        request.Decide(approved);
        _card?.Changed();
        OnPropertyChanged(nameof(ShowThinking));
    }

    /// <summary>Asks again after an error (the question is already in the chat).</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (Bubbles.LastOrDefault() is { IsError: true } error)
        {
            Bubbles.Remove(error);
        }

        await RequestAsync();
    }

    private bool CanRetry() => !IsBusy && _history.Count > 0 && _history[^1].Role == AiRole.User;

    private async Task RequestAsync()
    {
        if (_chat is null || _history.Count == 0)
        {
            return;
        }

        var carriedImages = false;
        var chatId = _chat.Id;
        var resting = _face.BaseMood;
        var model = _ai.Settings.Model;
        _request = new CancellationTokenSource();
        var token = _request.Token;
        BeginTyping();
        _reacted = false;
        IsBusy = true;
        _face.SetBaseMood(FaceMood.Thinking);
        ScrolledToEnd?.Invoke();
        _resting = resting;
        try
        {
            // No network and the provider is on the internet: say so at once instead of waiting on retries.
            if (_ai.ProviderUnreachable)
            {
                throw new AiException(AiErrorKind.Offline, "You're offline right now, so I can't reach the AI service.");
            }

            var client = _ai.CreateClient();
            IReadOnlyList<AiTool> tools = CanLookAtScreen
                ? [.. _tools.Definitions(), LookAtScreenTool, GoogleLensTool]
                : [.. _tools.Definitions(), GoogleLensTool];
            var callsUsed = 0;
            _lookedThisMessage = false;
            AiResponse response;
            for (var round = 0; ; round++)
            {
                var request = new AiRequest(
                    MithuPersona.SystemPrompt(_time.GetLocalNow(), _time.LocalTimeZone, canAct: true,
                        desktop: tools.Any(t => DesktopTools.Names.Contains(t.Name)), name: Companion.Name),
                    WithImageData(ChatRules.LimitImages(ChatRules.Context(_history))),
                    round < MaxToolRounds ? tools : null); // last round: no more tools, just answer

                carriedImages |= request.Messages.Any(m => m.Images.Count > 0);

                // The answer streams in: each piece is typed out as it arrives.
                response = await client.StreamAsync(request, piece => OnPiece(piece), token);
                if (_faceTags.Flush() is { Length: > 0 } held)
                {
                    OnPiece(held); // a reply that ended while it still looked like it might be a tag
                }

                // The tag is never kept: not in the chat, not in what the model reads back later.
                response = response with { Message = FaceTags.Strip(response.Message, out var face) };
                if (face is { } late && !_reacted)
                {
                    React(late); // nothing streamed (e.g. a provider that sends the answer whole)
                }

                if (response.Message.ToolCalls.Count == 0 || _chat?.Id != chatId)
                {
                    break;
                }

                // Mithu wants to act: finish the words so far, carry out the calls, then let it read the results.
                callsUsed = await RunToolsAsync(chatId, response.Message, callsUsed, model, token);
                BeginTyping();
                OnPropertyChanged(nameof(ShowThinking));
            }

            var message = response.Message;
            var text = message.Text;
            if (text.Length == 0)
            {
                text = response.StopReason == AiStopReason.Refused ? "I can't help with that one."
                    : callsUsed > 0 ? "Done ✓"
                    : "(No answer came back.)";
                message = new AiMessage(AiRole.Assistant, [.. message.Parts, new AiText(text)]);
            }
            else if (response.StopReason == AiStopReason.MaxTokens)
            {
                text += " …";
            }

            // The user may have switched chats while waiting: save to the chat that was asked.
            _chats.AddMessages(chatId, [message], _time.GetUtcNow(), model);
            _face.SetBaseMood(resting);
            if (_chat?.Id == chatId)
            {
                _history.Add(message);
                _chat = _chat with { UpdatedUtc = _time.GetUtcNow() };
                await FinishTypingAsync(text, token);
            }

            if (!_reacted)
            {
                _face.React(FaceMood.Happy, TimeSpan.FromSeconds(1.2));
            }

            _logger.LogInformation("AI answered ({Stop}, {Output} output tokens)", response.StopReason, response.Usage?.OutputTokens ?? 0);
        }
        catch (OperationCanceledException)
        {
            // Stop: keep what was written so far (and keep it in the chat, like a short answer).
            _face.SetBaseMood(resting);
            var partial = StopTyping(keep: true);
            if (_chat?.Id == chatId)
            {
                if (partial.Length > 0 && _history[^1].Role == AiRole.User)
                {
                    var message = AiMessage.Assistant(partial);
                    TrySave(chatId, message, model);
                    _history.Add(message);
                }

                AddBubble(BubbleKind.Note, "Stopped.");
            }
        }
        catch (AiException ex)
        {
            _face.SetBaseMood(resting);
            _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
            StopTyping(keep: false); // a half answer from a broken stream: Try again asks afresh
            if (_chat?.Id == chatId)
            {
                // A model that can't see pictures usually rejects the request outright.
                var hadImage = ex.Kind == AiErrorKind.BadRequest && ChatRules.Context(_history).Any(m => m.Images.Count > 0);
                AddErrorWithFallback(hadImage
                    ? "This model doesn't seem to accept screenshots. Pick one that can see images (any Claude model can) in ⋯ → AI settings…"
                    : ex.Message);
            }

            Say(AiException.Friendly(ex.Kind).Replace(AiException.SettingsHint, string.Empty, StringComparison.Ordinal));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _face.SetBaseMood(resting);
            StopTyping(keep: true);
            _logger.LogError(ex, "Could not save the answer");
            AddBubble(BubbleKind.Error, "I got an answer but couldn't save it. Please try again.");
        }
        finally
        {
            if (carriedImages)
            {
                // The screenshots' PNG and base64 copies for this request are garbage now: free them.
                _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(MemoryRelief.AfterBigImages, DispatcherPriority.ApplicationIdle);
            }

            if (_card is { } card)
            {
                card.Finish(); // folds to one line; a step still running was stopped
                _card = null;
                _cardStep = null;
            }

            _request.Dispose();
            _request = null;
            IsBusy = false;
            RetryCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Carries out the tool calls of one answer (up to the per-message limit), shows a "✓" line for each action,
    /// and saves the call and its results together so the chat never holds a call without its result.
    /// </summary>
    private async Task<int> RunToolsAsync(string chatId, AiMessage withCalls, int callsUsed, string model, CancellationToken token)
    {
        // Words written before the action ("On it."): finish typing them; an empty bubble just goes.
        if (withCalls.Text.Length > 0)
        {
            await FinishTypingAsync(withCalls.Text, token);
        }
        else
        {
            StopTyping(keep: false);
        }

        var results = new List<AiToolResult>();
        var screenshots = new List<AiImage>();
        foreach (var call in withCalls.ToolCalls)
        {
            token.ThrowIfCancellationRequested();
            if (call.Name == LookAtScreenToolName && callsUsed < AssistantTools.MaxCallsPerMessage)
            {
                callsUsed++;
                results.Add(await LookAsync(call, screenshots, token));
                continue;
            }

            if (call.Name == GoogleLensToolName && callsUsed < AssistantTools.MaxCallsPerMessage)
            {
                callsUsed++;
                results.Add(await LensAsync(call, screenshots, token));
                continue;
            }

            if (DesktopTools.Names.Contains(call.Name))
            {
                results.Add((await RunOnCardAsync(chatId, call, callsUsed)).Result);
                callsUsed++;
                continue;
            }

            var outcome = callsUsed < AssistantTools.MaxCallsPerMessage
                ? await _tools.RunAsync(call)
                : AssistantTools.Refused(call);
            callsUsed++;
            results.Add(outcome.Result);
            if (outcome.Action is { } action)
            {
                AddBubble(BubbleKind.Action, action);
            }
        }

        // Screenshots Mithu took go right after the results (the providers read them as part of that turn).
        var resultsMessage = new AiMessage(AiRole.User, [.. results, .. screenshots]);
        _chats.AddMessages(chatId, [withCalls, resultsMessage], _time.GetUtcNow(), model);
        _history.Add(withCalls);
        _history.Add(resultsMessage);
        _chat = _chat! with { UpdatedUtc = _time.GetUtcNow() };
        return callsUsed;
    }

    // ===== Execution card ===========================================================

    /// <summary>
    /// Runs one desktop tool call as a step on the card (a new card when the last bubble isn't this request's card):
    /// ◌ while it runs, ? while it waits for Approve, then ✓ / ⓘ / –. File changes are logged for Undo.
    /// </summary>
    private async Task<ToolOutcome> RunOnCardAsync(string chatId, AiToolCall call, int callsUsed)
    {
        if (_card is null || Bubbles.LastOrDefault()?.Card != _card)
        {
            _card?.Finish();
            _card = new ActionCard(CardTitle()) { IsWorking = true };
            Bubbles.Add(new ChatBubble(BubbleKind.Card, string.Empty, TimeOf(_time.GetUtcNow())) { Card = _card });
            OnPropertyChanged(nameof(HasBubbles));
        }

        var step = _card.Add(DesktopTools.Describe(call) + "…");
        _cardStep = step;
        ScrolledToEnd?.Invoke();
        ToolOutcome outcome;
        try
        {
            outcome = callsUsed < AssistantTools.MaxCallsPerMessage ? await _tools.RunAsync(call) : AssistantTools.Refused(call);
        }
        finally
        {
            _cardStep = null;
        }

        var firstLine = outcome.Result.Content.Split('\n')[0].TrimEnd('.', ':');
        if (!outcome.Result.IsError)
        {
            step.State = StepState.Done;
            step.Text = outcome.Action ?? firstLine.Split(" (")[0];
            step.Detail = null;
        }
        else if (outcome.Result.Content.StartsWith("The user said no", StringComparison.Ordinal))
        {
            step.State = StepState.Cancelled;
            step.Detail = "Cancelled";
        }
        else
        {
            step.State = StepState.Failed;
            step.Text = DesktopTools.Describe(call);
            step.Detail = firstLine;
            _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(1.5));
        }

        if (outcome.Undo is { Count: > 0 } ops && _actionLog is not null)
        {
            try
            {
                var id = _actionLog.Add(chatId, call.Id, step.Text, ops, _time.GetUtcNow());
                _card.Undoable.Add((id, ops));
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                _logger.LogError(ex, "Could not log an action for Undo");
            }
        }

        _card.Changed();
        return outcome;
    }

    /// <summary>The card's title: the start of what the user asked.</summary>
    private string CardTitle()
    {
        var asked = _history.LastOrDefault(m => m.Role == AiRole.User && !m.Parts.OfType<AiToolResult>().Any())?.Text ?? "Working on it";
        asked = asked.ReplaceLineEndings(" ").Trim();
        return asked.Length > 40 ? asked[..40].TrimEnd() + "…" : asked;
    }

    /// <summary>Undo on a card: puts back everything it changed (newest first).</summary>
    [RelayCommand]
    private async Task UndoCardAsync(ActionCard card)
    {
        if (!card.CanUndo || _desktop is null)
        {
            return;
        }

        card.IsUndoing = true;
        try
        {
            var ops = card.Undoable.SelectMany(u => u.Ops).ToList();
            var message = await _desktop.UndoAsync(ops);
            foreach (var (id, _) in card.Undoable)
            {
                Run(() => _actionLog?.MarkUndone(id));
            }

            card.IsUndone = true;
            card.Changed();
            AddBubble(BubbleKind.Note, message);
        }
        finally
        {
            card.IsUndoing = false;
        }
    }

    [RelayCommand]
    private static void ToggleCard(ActionCard card)
    {
        if (!card.IsWorking && card.CanExpand)
        {
            card.IsExpanded = !card.IsExpanded;
        }
    }

    // ===== Without AI ===============================================================
    /// <summary>Sets a reminder from plain text without AI (Home's "remind me…" shortcut). Set by the main view model.</summary>
    public Func<string, bool>? ReminderWithoutAi { get; set; }

    /// <summary>Saves text as a note without AI. Set by the main view model.</summary>
    public Func<string, Task<bool>>? NoteWithoutAi { get; set; }

    /// <summary>
    /// An error, plus a way out so nothing typed is lost: "Set it without AI" for a reminder-like message,
    /// "Save as a note" for anything else.
    /// </summary>
    private void AddErrorWithFallback(string message)
    {
        var question = _history.Count > 0 && _history[^1].Role == AiRole.User ? _history[^1].Text : string.Empty;
        var bubble = new ChatBubble(BubbleKind.Error, message, TimeOf(_time.GetUtcNow()));
        if (question.Length > 0)
        {
            var reminder = Core.Reminders.ReminderParser.LooksLikeReminder(question) && ReminderWithoutAi is not null;
            if (reminder || NoteWithoutAi is not null)
            {
                bubble.FallbackText = question;
                bubble.FallbackLabel = reminder ? "Set it without AI" : "Save as a note";
            }
        }

        Bubbles.Add(bubble);
        OnPropertyChanged(nameof(HasBubbles));
        ScrolledToEnd?.Invoke();
    }

    [RelayCommand]
    private async Task DoWithoutAiAsync(ChatBubble bubble)
    {
        if (bubble.FallbackText is not { } text)
        {
            return;
        }

        var reminder = bubble.FallbackLabel == "Set it without AI";
        var done = reminder ? ReminderWithoutAi?.Invoke(text) == true : NoteWithoutAi is not null && await NoteWithoutAi(text);
        if (done)
        {
            Bubbles.Remove(bubble);
            AddBubble(BubbleKind.Action, reminder ? "Reminder set without AI" : "Saved as a note");
        }
    }

    /// <summary>The AI's chosen face for this reply ([face:wink] …): a short reaction, once per answer.</summary>
    private void React(FaceReaction reaction)
    {
        if (_reacted)
        {
            return;
        }

        _reacted = true;
        _logger.LogInformation("Face reaction {Reaction}", reaction);
        switch (reaction)
        {
            case FaceReaction.Wink:
                _face.Wink();
                break;
            case FaceReaction.Ouch:
                _face.React(FaceMood.Hurt, TimeSpan.FromSeconds(1.2));
                _face.Punch();
                break;
            default:
                var (mood, seconds) = reaction switch
                {
                    FaceReaction.Celebrate => (FaceMood.Celebrating, 2.0),
                    FaceReaction.Surprised => (FaceMood.Listening, 1.6),
                    FaceReaction.Hmph => (FaceMood.Angry, 2.0),
                    FaceReaction.Sleepy => (FaceMood.Sleepy, 2.5),
                    FaceReaction.Thinking => (FaceMood.Thinking, 2.0),
                    FaceReaction.Concerned => (FaceMood.Concerned, 2.5),
                    _ => (FaceMood.Happy, 2.0),
                };
                _face.React(mood, TimeSpan.FromSeconds(seconds));
                break;
        }
    }

    // ===== Typing =================================================================

    private void BeginTyping()
    {
        _faceTags.Reset();
        _typer.Stop();
        _typing = null;
        _received.Clear();
        _shown = 0;
        _receivedAll = false;
        _typed = null;
    }

    /// <summary>A piece of the answer arrived (on the UI thread).</summary>
    private void OnPiece(string piece)
    {
        if (!System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false)
        {
            System.Windows.Application.Current!.Dispatcher.BeginInvoke(() => OnPiece(piece));
            return;
        }

        // A face tag at the start of the reply: hidden from the text, played on the face.
        var (shown, reaction) = _faceTags.Push(piece);
        if (reaction is { } face)
        {
            _face.SetBaseMood(_resting);
            React(face);
        }

        if (shown.Length == 0)
        {
            return;
        }

        piece = shown;
        _received.Append(piece);
        if (_typing is null)
        {
            _face.SetBaseMood(_resting); // words are coming: Mithu stops "thinking" and talks
            _typing = new ChatBubble(BubbleKind.Mithu, string.Empty, TimeOf(_time.GetUtcNow())) { IsStreaming = true };
            Bubbles.Add(_typing);
            OnPropertyChanged(nameof(HasBubbles));
            OnPropertyChanged(nameof(ShowThinking));
            ScrolledToEnd?.Invoke();
        }

        if (AnimateTyping)
        {
            if (!_typer.IsEnabled)
            {
                _typer.Start();
            }
        }
        else
        {
            _shown = _received.Length;
            _typing.Text = _received.ToString();
            ReplyGrew?.Invoke();
        }
    }

    /// <summary>One typing step: a few characters, more when far behind (so it never lags much).</summary>
    private void TypeMore()
    {
        if (_typing is null)
        {
            _typer.Stop();
            return;
        }

        var backlog = _received.Length - _shown;
        if (backlog > 0)
        {
            var step = Math.Max(CharsPerTick, backlog / 8);
            _shown += Math.Min(step, backlog);
            _typing.Text = _received.ToString(0, _shown);
            ReplyGrew?.Invoke();
        }
        else if (_receivedAll)
        {
            CompleteTyping();
        }
    }

    /// <summary>The whole answer is in: type out the rest, then finish (Stop shows it all at once).</summary>
    private async Task FinishTypingAsync(string finalText, CancellationToken token)
    {
        if (_typing is null)
        {
            // Nothing streamed (e.g. a refusal): type the final text out the same way.
            OnPiece(finalText);
        }
        else if (finalText.StartsWith(_received.ToString(), StringComparison.Ordinal))
        {
            _received.Append(finalText.AsSpan(_received.Length)); // e.g. " …" after a cut-off answer
        }
        else
        {
            _received.Clear().Append(finalText);
            _shown = Math.Min(_shown, _received.Length);
        }

        _receivedAll = true;
        if (!AnimateTyping || _shown >= _received.Length)
        {
            CompleteTyping();
            return;
        }

        _typed = new TaskCompletionSource();
        using (token.Register(() => _typed.TrySetResult()))
        {
            await _typed.Task;
        }

        CompleteTyping();
    }

    private void CompleteTyping()
    {
        _typer.Stop();
        if (_typing is not null)
        {
            _typing.Text = _received.ToString();
            _typing.IsStreaming = false;
            _typing = null;
            ReplyGrew?.Invoke();
        }

        _typed?.TrySetResult();
    }

    /// <summary>Stops typing. <paramref name="keep"/>: leave what's on screen; otherwise remove the half reply. Returns what was shown.</summary>
    private string StopTyping(bool keep)
    {
        _typer.Stop();
        var shown = _received.ToString(0, Math.Min(_shown, _received.Length));
        if (_typing is not null)
        {
            if (keep && shown.Length > 0)
            {
                _typing.Text = shown;
                _typing.IsStreaming = false;
            }
            else
            {
                Bubbles.Remove(_typing);
                OnPropertyChanged(nameof(HasBubbles));
            }

            _typing = null;
        }

        _typed?.TrySetResult();
        OnPropertyChanged(nameof(ShowThinking));
        return keep ? shown : string.Empty;
    }

    private void TrySave(string chatId, AiMessage message, string model)
    {
        try
        {
            _chats.AddMessages(chatId, [message], _time.GetUtcNow(), model);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not save the stopped answer");
        }
    }

    // ===== Chats ================================================================

    [RelayCommand]
    private void NewChat()
    {
        _request?.Cancel();
        _chat = null;
        _history.Clear();
        Bubbles.Clear();
        ChatTitle = "New chat";
        ShowChatList = false;
        OnPropertyChanged(nameof(HasBubbles));
        InputFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleChatList()
    {
        ShowChatList = !ShowChatList;
        if (ShowChatList)
        {
            ChatSearch = string.Empty;
            RefreshChats();
        }
    }

    [RelayCommand]
    private void OpenChat(ChatListItem item)
    {
        if (_chat?.Id != item.Chat.Id)
        {
            _request?.Cancel();
            Load(item.Chat);
        }

        ShowChatList = false;
        ScrolledToEnd?.Invoke();
    }

    public void RenameChat(ChatListItem item, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
        {
            return;
        }

        title = title.Length > ChatRules.MaxTitleLength ? title[..ChatRules.MaxTitleLength] : title;
        if (Run(() => _chats.RenameChat(item.Chat.Id, title)))
        {
            if (_chat?.Id == item.Chat.Id)
            {
                _chat = _chat with { Title = title };
                ChatTitle = title;
            }

            RefreshChats();
        }
    }

    [RelayCommand]
    private void DeleteChat(ChatListItem item)
    {
        var images = ImagesOf(item.Chat.Id);
        if (!Run(() => _chats.DeleteChat(item.Chat.Id)))
        {
            return;
        }

        Run(() => _images.Delete(images)); // the screenshots kept with it

        if (_chat?.Id == item.Chat.Id)
        {
            NewChat();
            ShowChatList = true;
        }

        RefreshChats();
        Say("Chat deleted");
    }

    [RelayCommand]
    private void DeleteAll()
    {
        if (!ConfirmDeleteAll)
        {
            ConfirmDeleteAll = true;
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }

        _confirmTimer.Stop();
        ConfirmDeleteAll = false;
        if (Run(_chats.DeleteAllChats))
        {
            Run(_images.DeleteAll); // the screenshots kept with them
            NewChat();
            ShowChatList = true;
            RefreshChats();
            Say("All chats deleted");
        }
    }

    partial void OnChatSearchChanged(string value)
    {
        RefreshChats();
        OnPropertyChanged(nameof(ChatsEmptyText));
    }

    partial void OnDraftChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    // ===== Internals ============================================================

    private void StartChat(ChatThread chat)
    {
        _chats.CreateChat(chat);
        _chat = chat;
        _history.Clear();
        Bubbles.Clear();
        ChatTitle = chat.Title;
        OnPropertyChanged(nameof(HasBubbles));
    }

    private void Load(ChatThread chat)
    {
        IReadOnlyList<ChatEntry> entries;
        try
        {
            entries = _chats.GetMessages(chat.Id);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not open a chat");
            Say("Couldn't open that chat.");
            return;
        }

        _chat = chat;
        ChatTitle = chat.Title;
        _history.Clear();
        Bubbles.Clear();
        var toolNames = new Dictionary<string, string>();
        var actions = SafeActions(chat.Id);
        var lastAsked = string.Empty;
        ActionCard? card = null;
        foreach (var entry in entries)
        {
            _history.Add(entry.Message);
            foreach (var call in entry.Message.ToolCalls)
            {
                toolNames[call.Id] = call.Name;
            }

            var text = entry.Message.Text;
            var isResults = entry.Message.Parts.OfType<AiToolResult>().Any();
            var image = isResults ? null : entry.Message.Images.FirstOrDefault(); // Mithu's own screenshots: on its ✓ line below
            if (text.Length > 0 || image is not null)
            {
                var (preview, path) = image is null ? (null, null) : BubbleImage(image);
                Bubbles.Add(new ChatBubble(entry.Message.Role == AiRole.Assistant ? BubbleKind.Mithu : BubbleKind.User, text, TimeOf(entry.CreatedUtc))
                {
                    Image = preview,
                    ImagePath = path,
                });
            }

            if (!isResults && entry.Message.Role == AiRole.User)
            {
                lastAsked = text;
            }

            // What Mithu did: its "✓" lines and execution cards come back too.
            var screenshots = new Queue<AiImage>(isResults ? entry.Message.Images : []);
            foreach (var result in entry.Message.Parts.OfType<AiToolResult>())
            {
                if (!toolNames.TryGetValue(result.ToolCallId, out var name) || (result.IsError && !DesktopTools.Names.Contains(name)))
                {
                    continue;
                }

                if (DesktopTools.Names.Contains(name))
                {
                    card = RestoreStep(card, result, lastAsked, entry.CreatedUtc, actions);
                    continue;
                }

                if (name == LookAtScreenToolName && screenshots.TryDequeue(out var shot))
                {
                    var (preview, path) = BubbleImage(shot);
                    Bubbles.Add(new ChatBubble(BubbleKind.Action, LookedText, TimeOf(entry.CreatedUtc)) { Image = preview, ImagePath = path });
                }
                else if (ActionTools.Contains(name))
                {
                    // The first line says what was done ("Opened Spotify."; a search lists files after it).
                    Bubbles.Add(new ChatBubble(BubbleKind.Action, result.Content.Split('\n')[0].TrimEnd('.', ':').Split(" (")[0], TimeOf(entry.CreatedUtc)));
                }
            }
        }

        card?.Finish();
        OnPropertyChanged(nameof(HasBubbles));
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A reopened chat: a desktop step goes on the card above it (or a new card), with its Undo if not used.</summary>
    private ActionCard RestoreStep(ActionCard? card, AiToolResult result, string asked, DateTimeOffset when, IReadOnlyDictionary<string, ActionRecord> actions)
    {
        if (card is null || Bubbles.LastOrDefault()?.Card != card)
        {
            card?.Finish();
            var title = asked.ReplaceLineEndings(" ").Trim();
            card = new ActionCard(title.Length > 40 ? title[..40].TrimEnd() + "…" : title);
            Bubbles.Add(new ChatBubble(BubbleKind.Card, string.Empty, TimeOf(when)) { Card = card });
        }

        var firstLine = result.Content.Split('\n')[0].TrimEnd('.', ':');
        var step = card.Add(firstLine.Split(" (")[0]);
        step.State = !result.IsError ? StepState.Done
            : result.Content.StartsWith("The user said no", StringComparison.Ordinal) ? StepState.Cancelled
            : StepState.Failed;
        if (step.IsFailed)
        {
            step.Detail = firstLine;
        }

        if (actions.TryGetValue(result.ToolCallId, out var action))
        {
            step.Text = action.Summary;
            if (action.Undone)
            {
                card.IsUndone = true;
            }
            else
            {
                card.Undoable.Add((action.Id, action.Ops));
            }
        }

        return card;
    }

    /// <summary>The chat's logged actions by tool call (empty when the log can't be read).</summary>
    private IReadOnlyDictionary<string, ActionRecord> SafeActions(string chatId)
    {
        try
        {
            return (_actionLog?.ForChat(chatId) ?? []).GroupBy(a => a.CallId).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not read the action log");
            return new Dictionary<string, ActionRecord>();
        }
    }

    private void RefreshChats()
    {
        var now = _time.GetUtcNow();
        Chats.Clear();
        foreach (var chat in SafeList(ChatSearch))
        {
            Chats.Add(new ChatListItem(chat, Core.Reminders.ReminderText.When(chat.UpdatedUtc, now, _time.LocalTimeZone), chat.Id == _chat?.Id));
        }

        OnPropertyChanged(nameof(HasChats));
        OnPropertyChanged(nameof(ChatsEmptyText));
    }

    private IReadOnlyList<ChatThread> SafeList(string? search)
    {
        try
        {
            return _chats.ListChats(search);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not list chats");
            return [];
        }
    }

    private void AddBubble(BubbleKind kind, string text)
    {
        Bubbles.Add(new ChatBubble(kind, text, TimeOf(_time.GetUtcNow())));
        OnPropertyChanged(nameof(HasBubbles));
        ScrolledToEnd?.Invoke();
    }

    private string TimeOf(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture);

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Chat storage action failed");
            Say("Something went wrong with your chats. Please try again.");
            return false;
        }
    }

    private void Say(string text) => StatusChanged?.Invoke(text);

    private static bool IsStorageError(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
