using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithu.App.Services;
using Mithu.Core.Ai;

namespace Mithu.App.ViewModels;

/// <summary>A row in the model dropdown.</summary>
public sealed record ModelOption(string Id, bool IsSelected);

/// <summary>
/// AI settings page: provider, address, key (masked; saved encrypted), model, Test connection, "Let AI read my
/// notes", and turning AI on (after a one-time notice about what is sent) or off. Edits are drafts until
/// Turn on / Save; nothing is contacted except by Test connection.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    /// <summary>Claude models offered as quick picks (the provider's full list appears after Test connection).</summary>
    public static readonly (string Id, string Label, string Hint)[] ClaudeModels =
    [
        ("claude-opus-5-5", "Opus 5.5", "Most capable"),
        ("claude-sonnet-5-5", "Sonnet 5.5", "Balanced"),
        ("claude-haiku-5-5", "Haiku 5.5", "Fastest, lowest cost"),
    ];

    private readonly AiService _ai;
    private CancellationTokenSource? _testCancel;

    public AiSettingsViewModel(AiService ai)
    {
        _ai = ai;
        Providers = AiPresets.All.Select(p => new ReminderChip(p.Id, p.Name, chip => SelectPreset(chip.Key))).ToList();
        ModelPicks = ClaudeModels.Select(m => new ReminderChip(m.Id, m.Label, chip => Model = chip.Key)).ToList();
        Load();
    }

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The key box should be emptied (provider changed, or the key was saved).</summary>
    public event Action? KeyDraftCleared;

    public IReadOnlyList<ReminderChip> Providers { get; }

    /// <summary>Quick model choices (Claude only).</summary>
    public IReadOnlyList<ReminderChip> ModelPicks { get; }

    /// <summary>Models the provider listed during Test connection.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    // ===== Draft ================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(NeedsKey), nameof(AcceptsKey), nameof(KeyHint), nameof(HasSavedKey), nameof(IsClaude),
        nameof(CanTurnOn), nameof(AddressEditable))]
    private string _presetId = "claude";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn))]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn))]
    private string _model = string.Empty;

    /// <summary>What is typed in the key box (never shown back; saved encrypted on Turn on / Save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn))]
    private string _keyDraft = string.Empty;

    [ObservableProperty]
    private bool _allowNotes;

    /// <summary>Ctrl+Shift+S takes a screenshot to ask about (applies at once).</summary>
    [ObservableProperty]
    private bool _screenshotShortcut;

    /// <summary>Mithu may take a screenshot itself when asked in chat (applies at once).</summary>
    [ObservableProperty]
    private bool _lookAtScreen;

    // ===== Test =================================================================

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestMessage))]
    private string _testMessage = string.Empty;

    /// <summary>null = not tested; true = connected; false = failed.</summary>
    [ObservableProperty]
    private bool? _testOk;

    public bool HasTestMessage => TestMessage.Length > 0;

    public bool HasAvailableModels => AvailableModels.Count > 0;

    private const int MaxSuggestions = 40;

    /// <summary>Listed models matching what is typed in the model box (up to 40), for picking.</summary>
    public IReadOnlyList<string> ModelSuggestions => Matches().Take(MaxSuggestions).ToList();

    /// <summary>The same, as rows for the dropdown (the chosen one is ticked).</summary>
    public IReadOnlyList<ModelOption> ModelOptions =>
        ModelSuggestions.Select(m => new ModelOption(m, m.Equals(Model.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>"Showing 40 of 312 — type to narrow", or "No models match" (empty when everything fits).</summary>
    public string ModelListNote
    {
        get
        {
            var count = Matches().Count();
            return count == 0 ? "No listed model matches — you can still use what you typed."
                : count > MaxSuggestions ? $"Showing {MaxSuggestions} of {count} — type to narrow"
                : string.Empty;
        }
    }

    public bool HasModelListNote => ModelListNote.Length > 0;

    /// <summary>The model dropdown is open (▾ button, typing, or right after Test connection).</summary>
    [ObservableProperty]
    private bool _isModelListOpen;

    [RelayCommand]
    private void ToggleModelList() => IsModelListOpen = !IsModelListOpen && HasAvailableModels;

    private bool _choosingModel;

    private IEnumerable<string> Matches()
    {
        var typed = Model.Trim();
        return AvailableModels.Any(m => m.Equals(typed, StringComparison.OrdinalIgnoreCase))
            ? AvailableModels // an exact pick: show everything again
            : AvailableModels.Where(m => m.Contains(typed, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyModelList()
    {
        OnPropertyChanged(nameof(ModelSuggestions));
        OnPropertyChanged(nameof(ModelOptions));
        OnPropertyChanged(nameof(ModelListNote));
        OnPropertyChanged(nameof(HasModelListNote));
    }

    // ===== State ================================================================

    [ObservableProperty]
    private bool _showConsent;

    public AiPreset Preset => AiPresets.Find(PresetId) ?? AiPresets.All[0];

    public bool IsClaude => Preset.Kind == AiProviderKind.Anthropic;

    public bool NeedsKey => Preset.NeedsKey;

    /// <summary>Show the key field (required for cloud providers, optional for Other).</summary>
    public bool AcceptsKey => Preset.AcceptsKey;

    /// <summary>Local servers and "Other" have an address the user may need to change.</summary>
    public bool AddressEditable => !IsClaude;

    public bool HasSavedKey => _ai.HasKey(PresetId);

    /// <summary>"Saved key ••••1a2b" or where to get one.</summary>
    public string KeyHint => _ai.KeyHint(PresetId) is { } hint ? $"Saved key {hint} · paste a new one to replace it" : Preset.KeyHint;

    public bool IsOn => _ai.IsReady;

    /// <summary>"AI is on · Claude (Anthropic) · claude-opus-5-5" or "AI is off".</summary>
    public string StateText => IsOn
        ? $"On · {AiPresets.Find(_ai.Settings.PresetId)?.Name ?? "Custom"} · {_ai.Settings.Model}"
        : "Off";

    public bool CanTurnOn => BaseUrl.Trim().Length > 0 && Model.Trim().Length > 0
        && (!NeedsKey || HasSavedKey || KeyDraft.Trim().Length > 0);

    // ===== Commands =============================================================

    /// <summary>The page is being shown: start from what is saved.</summary>
    public void Load()
    {
        var s = _ai.Settings;
        PresetId = s.PresetId;
        BaseUrl = s.BaseUrl;
        Model = s.Model;
        AllowNotes = s.AllowNotes;
        ScreenshotShortcut = s.ScreenshotShortcut;
        LookAtScreen = s.LookAtScreen;
        KeyDraft = string.Empty;
        ShowConsent = false;
        ClearTest();
        SelectChips();
        KeyDraftCleared?.Invoke();
        RefreshState();
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        _testCancel?.Cancel();
        _testCancel = new CancellationTokenSource();
        IsTesting = true;
        TestOk = null;
        TestMessage = "Testing…";
        try
        {
            var result = await _ai.TestAsync(Draft(enabled: false), CurrentKey(), _testCancel.Token);
            TestOk = result.Ok;
            TestMessage = result.Message;
            AvailableModels.Clear();
            foreach (var model in result.Models)
            {
                AvailableModels.Add(model);
            }

            // Nothing chosen yet: take the first model the server offers.
            if (result.Ok && Model.Trim().Length == 0 && result.Models.Count > 0)
            {
                Model = result.Models[0];
            }

            OnPropertyChanged(nameof(HasAvailableModels));
            NotifyModelList();
            IsModelListOpen = result.Ok && result.Models.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting;

    /// <summary>Turn on AI: the first time, show what is sent and where; then save.</summary>
    [RelayCommand]
    private void TurnOn()
    {
        if (!CanTurnOn)
        {
            return;
        }

        if (!_ai.Settings.ConsentGiven)
        {
            ShowConsent = true;
            return;
        }

        Apply(enabled: true);
        Say("AI is on ✓");
    }

    [RelayCommand]
    private void AcceptConsent()
    {
        ShowConsent = false;
        _ai.Update(a => a with { ConsentGiven = true });
        Apply(enabled: true);
        Say("AI is on ✓");
    }

    [RelayCommand]
    private void CancelConsent() => ShowConsent = false;

    /// <summary>AI is on and the user changed something: keep it on with the new choices.</summary>
    [RelayCommand]
    private void Save()
    {
        if (!CanTurnOn)
        {
            return;
        }

        Apply(enabled: true);
        Say("AI settings saved");
    }

    [RelayCommand]
    private void TurnOff()
    {
        _ai.Update(a => a with { Enabled = false });
        RefreshState();
        Say("AI is off. Nothing is sent anywhere.");
    }

    /// <summary>Removes the saved key for the selected provider.</summary>
    [RelayCommand]
    private void RemoveKey()
    {
        _ai.SetKey(PresetId, null);
        if (_ai.Settings.PresetId == PresetId && NeedsKey)
        {
            _ai.Update(a => a with { Enabled = false });
        }

        RefreshState();
        Say("Key removed from this PC");
    }

    [RelayCommand]
    private void ChooseModel(string model)
    {
        _choosingModel = true;
        Model = model;
        _choosingModel = false;
        IsModelListOpen = false;
    }

    partial void OnAllowNotesChanged(bool value)
    {
        // A privacy switch: applies at once.
        if (_ai.Settings.AllowNotes != value)
        {
            _ai.Update(a => a with { AllowNotes = value });
            Say(value ? "AI may now search your notes when it helps." : "AI won't read your notes.");
        }
    }

    partial void OnScreenshotShortcutChanged(bool value)
    {
        if (_ai.Settings.ScreenshotShortcut != value)
        {
            _ai.Update(a => a with { ScreenshotShortcut = value });
            Say(value ? $"{AiSettings.ScreenshotHotkey} shows me your screen" : $"{AiSettings.ScreenshotHotkey} is free for other apps");
        }
    }

    partial void OnLookAtScreenChanged(bool value)
    {
        if (_ai.Settings.LookAtScreen != value)
        {
            _ai.Update(a => a with { LookAtScreen = value });
            Say(value ? "Ask me to look at your screen any time." : "I won't take screenshots myself.");
        }
    }

    partial void OnModelChanged(string value)
    {
        foreach (var chip in ModelPicks)
        {
            chip.IsSelected = chip.Key == value.Trim();
        }

        NotifyModelList();

        // Typing a model name opens the list to narrow it; picking one closes it.
        if (!_choosingModel && HasAvailableModels)
        {
            IsModelListOpen = true;
        }
    }

    // ===== Internals ============================================================

    private void SelectPreset(string id)
    {
        if (id == PresetId)
        {
            return;
        }

        var preset = AiPresets.Find(id)!;
        var saved = _ai.Settings;
        PresetId = id;

        // Back to the saved provider: its saved address and model; otherwise the preset's.
        BaseUrl = saved.PresetId == id ? saved.BaseUrl : preset.BaseUrl;
        Model = saved.PresetId == id ? saved.Model : preset.SuggestedModel;
        KeyDraft = string.Empty;
        KeyDraftCleared?.Invoke();
        ClearTest();
        SelectChips();
    }

    private void SelectChips()
    {
        foreach (var chip in Providers)
        {
            chip.IsSelected = chip.Key == PresetId;
        }

        OnModelChanged(Model);
    }

    private void Apply(bool enabled)
    {
        if (KeyDraft.Trim().Length > 0)
        {
            _ai.SetKey(PresetId, KeyDraft);
            KeyDraft = string.Empty;
            KeyDraftCleared?.Invoke();
        }

        var draft = Draft(enabled);
        _ai.Update(a => draft with { ConsentGiven = a.ConsentGiven || draft.ConsentGiven });
        RefreshState();
    }

    private AiSettings Draft(bool enabled) => _ai.Settings with
    {
        PresetId = PresetId,
        Kind = Preset.Kind,
        BaseUrl = BaseUrl.Trim(),
        Model = Model.Trim(),
        AllowNotes = AllowNotes,
        Enabled = enabled,
    };

    /// <summary>The typed key, or null to use the one saved for this provider.</summary>
    private string? CurrentKey() => KeyDraft.Trim().Length > 0 ? KeyDraft.Trim() : null;

    private void ClearTest()
    {
        TestMessage = string.Empty;
        TestOk = null;
        AvailableModels.Clear();
        IsModelListOpen = false;
        OnPropertyChanged(nameof(HasAvailableModels));
        NotifyModelList();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasSavedKey));
        OnPropertyChanged(nameof(KeyHint));
        OnPropertyChanged(nameof(CanTurnOn));
    }

    private void Say(string text) => StatusChanged?.Invoke(text);
}
