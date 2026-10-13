using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.App.Services;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;
using Mithu.Infrastructure.ClaudeCode;

namespace Mithu.App.ViewModels;

/// <summary>
/// The first-run setup (owner, 2026-10-13), a page of the panel in five steps: Welcome (what the companion does) →
/// Name (what to call it) → AI (opens AI settings for the key, or skip) → Permissions (each one shown, all switched
/// on to start with) → Done (three tips). Shown once on a brand-new install; ⋯ → "Run setup again" replays it.
/// Nothing is applied until Finish, except the name (so the later steps already use it).
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    public const int Welcome = 0, Name = 1, Ai = 2, Permissions = 3, Done = 4;

    private readonly ISettingsStore _settings;
    private readonly IStartupRegistration _startup;
    private readonly IDesktop _desktop;
    private readonly ClaudeCodeService _claude;
    private readonly AiSettingsViewModel _ai;
    private readonly ILogger<SetupViewModel> _logger;

    public SetupViewModel(ISettingsStore settings, IStartupRegistration startup, IDesktop desktop, ClaudeCodeService claude,
        AiSettingsViewModel ai, ILogger<SetupViewModel> logger)
    {
        _settings = settings;
        _startup = startup;
        _desktop = desktop;
        _claude = claude;
        _ai = ai;
        _logger = logger;
        _ai.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AiSettingsViewModel.IsOn) or nameof(AiSettingsViewModel.StateText))
            {
                OnPropertyChanged(nameof(IsAiOn));
                OnPropertyChanged(nameof(AiStateText));
            }
        };
    }

    /// <summary>"Set up AI" was pressed: the panel opens AI settings (Back returns here).</summary>
    public event Action? AiRequested;

    /// <summary>Setup finished (true) or skipped (false).</summary>
    public event Action<bool>? Closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsName), nameof(IsAi), nameof(IsPermissions), nameof(IsDone),
        nameof(CanGoBack), nameof(NextText), nameof(StepText))]
    private int _step;

    public bool IsWelcome => Step == Welcome;

    public bool IsName => Step == Name;

    public bool IsAi => Step == Ai;

    public bool IsPermissions => Step == Permissions;

    public bool IsDone => Step == Done;

    public bool CanGoBack => Step > Welcome && Step < Done;

    public string NextText => Step switch
    {
        Welcome => "Let's set up",
        Ai => IsAiOn ? "Next" : "Skip for now",
        Permissions => "Finish",
        Done => "Start",
        _ => "Next",
    };

    /// <summary>"Step 2 of 4" (Welcome and Done aren't counted).</summary>
    public string StepText => Step is > Welcome and < Done ? $"Step {Step} of 3" : string.Empty;

    // ===== Name ===============================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _nameDraft = CompanionNames.Default;

    public string Preview => $"Hi, I'm {CompanionNames.Clean(NameDraft)}!";

    public int MaxNameLength => CompanionNames.MaxLength;

    /// <summary>A few ideas to tap.</summary>
    public IReadOnlyList<string> NameIdeas { get; } = ["Mithu", "Pixel", "Buddy", "Nova", "Chotu"];

    [RelayCommand]
    private void PickName(string name) => NameDraft = name;

    // ===== AI =================================================================

    public bool IsAiOn => _ai.IsOn;

    public string AiStateText => _ai.StateText;

    [RelayCommand]
    private void SetUpAi() => AiRequested?.Invoke();

    // ===== Permissions (all on to start with) ==================================

    [ObservableProperty]
    private bool _startWithWindows = true;

    [ObservableProperty]
    private bool _screenshotShortcut = true;

    [ObservableProperty]
    private bool _readNotes = true;

    [ObservableProperty]
    private bool _lookAtScreen = true;

    [ObservableProperty]
    private bool _desktopControl = true;

    [ObservableProperty]
    private bool _readClipboard = true;

    [ObservableProperty]
    private bool _claudeCode = true;

    /// <summary>Claude Code is on this PC (its settings folder exists): only then is its switch shown.</summary>
    [ObservableProperty]
    private bool _hasClaudeCode;

    /// <summary>"Desktop, Documents, Downloads, Pictures, Music, Videos and the D: and E: drives".</summary>
    [ObservableProperty]
    private string _foldersText = string.Empty;

    /// <summary>Starts (or restarts) the setup from the first step, with the current name.</summary>
    public void Begin()
    {
        Step = Welcome;
        NameDraft = _settings.Current.CompanionName;
        HasClaudeCode = Directory.Exists(Path.GetDirectoryName(ClaudeCodeHooks.DefaultPath));
        var disks = SafeDisks();
        FoldersText = disks.Count == 0
            ? "Desktop, Documents, Downloads, Pictures, Music and Videos"
            : $"Desktop, Documents, Downloads, Pictures, Music, Videos and the {Join(disks.Select(d => d.Root.TrimEnd('\\')).ToList())} {(disks.Count == 1 ? "drive" : "drives")}";
        OnPropertyChanged(nameof(IsAiOn));
        OnPropertyChanged(nameof(AiStateText));
        OnPropertyChanged(nameof(NextText));
    }

    [RelayCommand]
    private void Next()
    {
        switch (Step)
        {
            case Name:
                // The name counts from here on: the next steps (and the header) already use it.
                var name = CompanionNames.Clean(NameDraft);
                NameDraft = name;
                _settings.Update(s => s with { CompanionName = name });
                break;
            case Permissions:
                Apply();
                break;
            case Done:
                Closed?.Invoke(true);
                return;
        }

        Step++;
    }

    /// <summary>← on the page (or Esc): the previous step; false on the first one.</summary>
    [RelayCommand]
    public void Back()
    {
        if (CanGoBack)
        {
            Step--;
        }
    }

    /// <summary>"Skip setup": nothing changes except that the setup won't show again.</summary>
    [RelayCommand]
    private void Skip()
    {
        _settings.Update(s => s with { SetupDone = true });
        Closed?.Invoke(false);
    }

    /// <summary>Back from AI settings: show what was set up.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsAiOn));
        OnPropertyChanged(nameof(AiStateText));
        OnPropertyChanged(nameof(NextText));
    }

    private void Apply()
    {
        var folders = DesktopControl
            ? _desktop.KnownFolders().Select(f => f.Path).Concat(SafeDisks().Select(d => d.Root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : null;
        _settings.Update(s => s with
        {
            SetupDone = true,
            Ai = s.Ai with { AllowNotes = ReadNotes, ScreenshotShortcut = ScreenshotShortcut, LookAtScreen = LookAtScreen },
            Desktop = s.Desktop with
            {
                Enabled = DesktopControl,
                AllowApps = true,
                AllowFiles = true,
                AllowSystem = true,
                AllowClipboard = ReadClipboard,
                AllowedFolders = folders ?? s.Desktop.AllowedFolders,
            },
        });

        try
        {
            _startup.SetEnabled(StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Setup couldn't change Start with Windows");
        }

        if (HasClaudeCode)
        {
            if (ClaudeCode && !_settings.Current.ClaudeCode.Enabled)
            {
                _claude.TryEnable(out _);
            }
            else if (!ClaudeCode && _settings.Current.ClaudeCode.Enabled)
            {
                _claude.Disable();
            }
        }

        _logger.LogInformation("Setup finished (desktop {Desktop}, Claude Code {Claude}, start with Windows {Startup})",
            DesktopControl, HasClaudeCode && ClaudeCode, StartWithWindows);
    }

    private IReadOnlyList<(string Root, string Name)> SafeDisks()
    {
        try
        {
            return _desktop.LocalDisks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
