using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithu.App.Services;
using Mithu.Core.Desktop;
using Mithu.Core.Interfaces;

namespace Mithu.App.ViewModels;

/// <summary>An allowed folder in the list.</summary>
public sealed record FolderRow(string Path, string Name);

/// <summary>
/// Desktop control settings: the master switch, what Mithu may do (apps, folders & files, volume & Settings
/// pages), the allowed folders, and "Ask me before every action". Privacy switches apply at once.
/// </summary>
public sealed partial class DesktopControlViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IDesktop _desktop;
    private readonly AiService _ai;
    private bool _loading;

    public DesktopControlViewModel(ISettingsStore settings, IDesktop desktop, AiService ai)
    {
        _settings = settings;
        _desktop = desktop;
        _ai = ai;
        Load();
    }

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    public ObservableCollection<FolderRow> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _enabled;

    [ObservableProperty]
    private bool _allowApps;

    [ObservableProperty]
    private bool _allowFiles;

    [ObservableProperty]
    private bool _allowSystem;

    [ObservableProperty]
    private bool _allowClipboard;

    [ObservableProperty]
    private bool _askBeforeEachAction;

    public string StateText => Enabled ? "On" : "Off";

    /// <summary>Desktop control works through the chat, so it needs AI.</summary>
    public bool NeedsAi => !_ai.IsReady;

    public bool HasNoFolders => Folders.Count == 0;

    private DesktopSettings Current => _settings.Current.Desktop;

    /// <summary>The page is shown: start from what is saved.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var s = Current;
            Enabled = s.Enabled;
            AllowApps = s.AllowApps;
            AllowFiles = s.AllowFiles;
            AllowSystem = s.AllowSystem;
            AllowClipboard = s.AllowClipboard;
            AskBeforeEachAction = s.AskBeforeEachAction;
            RefreshFolders();
            OnPropertyChanged(nameof(NeedsAi));
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnEnabledChanged(bool value) =>
        Save(s => s with { Enabled = value }, value
            ? "Desktop control is on. Ask me to open apps, folders or files."
            : "Desktop control is off. I won't touch your apps or files.");

    partial void OnAllowAppsChanged(bool value) => Save(s => s with { AllowApps = value }, value ? "I can open apps for you." : "I won't open apps.");

    partial void OnAllowFilesChanged(bool value) => Save(s => s with { AllowFiles = value }, value ? "I can open and find files in your allowed folders." : "I won't open or search files.");

    partial void OnAllowSystemChanged(bool value) => Save(s => s with { AllowSystem = value }, value ? "I can change the volume and open Settings pages." : "I won't change the volume or open Settings.");

    partial void OnAllowClipboardChanged(bool value) =>
        Save(s => s with { AllowClipboard = value }, value ? "I can read what you copy, when you ask." : "I won't read what you copy.");

    partial void OnAskBeforeEachActionChanged(bool value) =>
        Save(s => s with { AskBeforeEachAction = value }, value ? "I'll ask before every action." : "I'll act right away when you ask.");

    /// <summary>Adds a folder the user picked (not a whole drive, not Windows or program folders).</summary>
    public void AddFolder(string path)
    {
        if (PathGuard.WhyNotAllowedFolder(path, _desktop.SystemFolders(), _desktop.LocalDisks().Select(d => d.Root).ToList()) is { } problem)
        {
            Say(problem);
            return;
        }

        var folders = CurrentFolders();
        if (folders.Any(f => PathGuard.IsInside(path, f)))
        {
            Say("That folder is already allowed.");
            return;
        }

        // A folder that holds allowed ones replaces them.
        var clean = path.Length <= 3 ? path : path.TrimEnd('\\');
        var updated = folders.Where(f => !PathGuard.IsInside(f, clean)).Append(clean).ToList();
        Save(s => s with { AllowedFolders = updated }, $"Added {NameOf(clean)}");
        RefreshFolders();
    }

    /// <summary>Local disks (not the Windows disk) that aren't allowed yet, for "Add disk".</summary>
    public IReadOnlyList<(string Root, string Name)> AvailableDisks()
    {
        var folders = CurrentFolders();
        return _desktop.LocalDisks().Where(d => !folders.Any(f => PathGuard.IsInside(d.Root, f))).ToList();
    }

    [RelayCommand]
    private void RemoveFolder(FolderRow row)
    {
        var updated = CurrentFolders().Where(f => !string.Equals(f.TrimEnd('\\'), row.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
        Save(s => s with { AllowedFolders = updated }, $"{Companion.Name} can't reach {row.Name} any more");
        RefreshFolders();
    }

    [RelayCommand]
    private void ResetFolders()
    {
        Save(s => s with { AllowedFolders = null }, "Back to the usual folders");
        RefreshFolders();
    }

    private List<string> CurrentFolders() =>
        (Current.AllowedFolders ?? _desktop.KnownFolders().Select(f => f.Path).ToList()).ToList();

    private void RefreshFolders()
    {
        Folders.Clear();
        foreach (var path in CurrentFolders())
        {
            Folders.Add(new FolderRow(path, NameOf(path)));
        }

        OnPropertyChanged(nameof(HasNoFolders));
    }

    /// <summary>"Downloads", "Data (D:)", or the folder's own name.</summary>
    private string NameOf(string path)
    {
        var trimmed = path.Length <= 3 ? path : path.TrimEnd('\\');
        var known = _desktop.KnownFolders().FirstOrDefault(k => string.Equals(k.Path.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase)).Name;
        var disk = _desktop.LocalDisks().FirstOrDefault(d => string.Equals(d.Root, trimmed, StringComparison.OrdinalIgnoreCase)).Name;
        var name = known ?? disk ?? System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private void Save(Func<DesktopSettings, DesktopSettings> change, string message)
    {
        if (_loading)
        {
            return;
        }

        _settings.Update(s => s with { Desktop = change(s.Desktop) });
        Say(message);
    }

    private void Say(string text) => StatusChanged?.Invoke(text);
}
