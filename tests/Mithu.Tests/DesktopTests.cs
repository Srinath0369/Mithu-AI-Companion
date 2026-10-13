using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mithu.App.Services;
using Mithu.Core.Ai;
using Mithu.Core.Desktop;
using Mithu.Core.Interfaces;

namespace Mithu.Tests;

public class PathGuardTests
{
    private static readonly string[] Roots = [@"C:\Users\me\Documents", @"C:\Users\me\Downloads"];

    [Theory]
    [InlineData(@"C:\Users\me\Documents\report.pdf")]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"c:\users\ME\downloads\sub\file.txt")]
    [InlineData(@"C:/Users/me/Downloads/a.png")]
    public void Paths_inside_allowed_folders_pass(string path) => Assert.True(PathGuard.Check(path, Roots).Allowed);

    [Theory]
    [InlineData(@"C:\Users\me\Documents2\x.txt")]                 // a different folder with a similar name
    [InlineData(@"C:\Users\me\AppData\Local\Mithu\secrets.dat")]
    [InlineData(@"C:\Users\me\Documents\..\AppData\x")]            // climbing out
    [InlineData(@"C:\Users\me\Documents\.\x")]
    [InlineData(@"\\server\share\Documents\x.txt")]                // network
    [InlineData(@"\\?\C:\Users\me\Documents\x.txt")]               // device path
    [InlineData(@"Documents\x.txt")]                               // relative
    [InlineData(@"C:\Users\me\Documents\x.txt:hidden")]            // alternate data stream
    [InlineData("")]
    public void Escapes_are_refused(string path)
    {
        var check = PathGuard.Check(path, Roots);
        Assert.False(check.Allowed);
        Assert.False(string.IsNullOrEmpty(check.Reason));
    }

    [Fact]
    public void A_link_inside_an_allowed_folder_that_leads_outside_is_refused()
    {
        static string? Real(string p) => p.StartsWith(@"C:\Users\me\Documents\link", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Windows\System32" + p[@"C:\Users\me\Documents\link".Length..]
            : p;
        Assert.False(PathGuard.Check(@"C:\Users\me\Documents\link\drivers\etc\hosts", Roots, Real).Allowed);
        Assert.True(PathGuard.Check(@"C:\Users\me\Documents\real\notes.txt", Roots, Real).Allowed);
    }

    [Theory]
    [InlineData(@"C:\", false)]             // the Windows disk
    [InlineData(@"D:\", true)]              // a disk inside the PC
    [InlineData(@"E:\", false)]             // e.g. a USB stick (not in the local disks list)
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Users\me", false)]            // holds AppData
    [InlineData(@"\\server\share", false)]
    [InlineData(@"D:\Projects", true)]
    [InlineData(@"C:\Users\me\Work", true)]
    public void Only_sensible_folders_can_be_added(string path, bool ok)
    {
        string[] system = [@"C:\Windows", @"C:\Program Files", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local"];
        Assert.Equal(ok, PathGuard.WhyNotAllowedFolder(path, system, [@"D:\"]) is null);
    }
}

public class AppMatcherTests
{
    private static readonly AppEntry[] Apps =
    [
        new("Visual Studio Code", "code"), new("Visual Studio 2022", "vs"), new("Google Chrome", "chrome"),
        new("Spotify", "spotify"), new("Word", "word"), new("WordPad", "wordpad"), new("Calculator", "calc"),
        new("Microsoft Edge", "edge"), new("Notepad", "notepad"), new("Notepad++", "npp"),
    ];

    [Theory]
    [InlineData("VS Code", "Visual Studio Code")]
    [InlineData("vscode", "Visual Studio Code")]
    [InlineData("visual studio code", "Visual Studio Code")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("Spotify", "Spotify")]
    [InlineData("word", "Word")]
    [InlineData("calc", "Calculator")]
    [InlineData("edge", "Microsoft Edge")]
    [InlineData("notepad", "Notepad")]
    [InlineData("notepad++", "Notepad++")]
    [InlineData("studio code", "Visual Studio Code")]
    public void Finds_the_app_meant(string query, string expected) => Assert.Equal(expected, AppMatcher.Best(query, Apps)?.Name);

    [Theory]
    [InlineData("photoshop")]
    [InlineData("x")]
    public void Unknown_apps_are_not_guessed(string query) => Assert.Null(AppMatcher.Best(query, Apps));

    [Fact]
    public void Open_windows_match_by_product_or_process_name()
    {
        RunningApp[] running = [new("Spotify", "Spotify", "Song - Artist", 1), new("Visual Studio Code", "Code", "x.cs - Companion", 2)];
        Assert.Equal(2, AppMatcher.BestRunning("vs code", running)?.Handle);
        Assert.Equal(1, AppMatcher.BestRunning("spotify", running)?.Handle);
        Assert.Null(AppMatcher.BestRunning("chrome", running));
    }

    [Theory]
    [InlineData("Uninstall Spotify", false)]
    [InlineData("Python 3.12 Documentation", false)]
    [InlineData("Spotify", true)]
    public void Start_menu_extras_are_not_apps(string name, bool launchable) => Assert.Equal(launchable, AppMatcher.IsLaunchable(name));
}

public class FileSearchTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void Matches_words_types_and_dates()
    {
        var q = FileSearchQuery.From("tax report", "pdf", "2026-10-11", "2026-10-11", Zone);
        var day = new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
        Assert.True(q.Matches("Tax Report final.pdf", day));
        Assert.False(q.Matches("Tax Report final.docx", day));
        Assert.False(q.Matches("report.pdf", day));                      // "tax" missing
        Assert.False(q.Matches("Tax Report.pdf", day.AddDays(1)));       // the next day
        Assert.False(q.Matches("Tax Report.pdf", day.AddDays(-1)));
    }

    [Theory]
    [InlineData("image", "png")]
    [InlineData("Screenshots", "jpg")]
    [InlineData(".pdf", "pdf")]
    [InlineData("spreadsheet", "csv")]
    public void Kinds_become_extensions(string type, string expected) => Assert.Contains(expected, FileSearchQuery.Kinds(type));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("run.PS1", true)]
    [InlineData("Spotify.lnk", true)]
    [InlineData("macro.xlsm", true)]
    [InlineData("report.pdf", false)]
    [InlineData("photo.jpg", false)]
    public void Programs_and_scripts_are_recognised(string name, bool runnable) => Assert.Equal(runnable, RunnableFiles.IsRunnable(name));

    [Theory]
    [InlineData("wifi", "ms-settings:network-wifi")]
    [InlineData("Wi-Fi", "ms-settings:network-wifi")]
    [InlineData("windows update", "ms-settings:windowsupdate")]
    [InlineData("Night light", "ms-settings:nightlight")]
    public void Settings_pages_are_found_by_name(string key, string uri) => Assert.Equal(uri, SettingsPages.Find(key)?.Uri);

    [Fact]
    public void Unknown_settings_pages_are_refused() => Assert.Null(SettingsPages.Find("regedit"));
}

public sealed class DesktopToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly FakeDesktop _desktop = new();
    private readonly ActionApprovals _approvals = new();

    public DesktopToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Downloads"));
        File.WriteAllText(Path.Combine(_folder, "Downloads", "invoice.pdf"), "x");
        File.WriteAllText(Path.Combine(_folder, "Downloads", "setup.exe"), "x");
        _desktop.Known = [("Downloads", Path.Combine(_folder, "Downloads"))];
        _settings.Update(s => s with { Desktop = new DesktopSettings { Enabled = true } });
    }

    private readonly FakeClipboard _clipboard = new();

    private DesktopTools Tools() => new(_desktop, _settings, _approvals, _clipboard, new FakeTimeProvider(), NullLogger<DesktopTools>.Instance);

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public string? ReadText() => Text;

        public bool WriteText(string text)
        {
            Text = text;
            return true;
        }
    }

    private Task<ToolOutcome> Run(string name, string args) =>
        new Toolbox(null!, Tools()).RunAsync(new AiToolCall("t1", name, args));

    [Fact]
    public void Nothing_is_offered_until_turned_on_and_each_area_has_its_own_switch()
    {
        _settings.Update(s => s with { Desktop = new DesktopSettings() });
        Assert.Empty(Tools().Definitions());

        _settings.Update(s => s with { Desktop = new DesktopSettings { Enabled = true, AllowFiles = false } });
        var names = Tools().Definitions().Select(d => d.Name).ToList();
        Assert.Contains("open_app", names);
        Assert.Contains("set_volume", names);
        Assert.DoesNotContain("open_file", names);
        Assert.DoesNotContain("search_files", names);
        Assert.All(Tools().Definitions(), d => JsonDocument.Parse(d.ParametersSchemaJson).Dispose()); // valid JSON schemas
    }

    [Fact]
    public async Task A_tool_that_is_switched_off_is_refused_even_if_called()
    {
        _settings.Update(s => s with { Desktop = new DesktopSettings { Enabled = true, AllowApps = false } });
        var outcome = await Run("open_app", """{"name":"Spotify"}""");
        Assert.True(outcome.Result.IsError);
        Assert.Empty(_desktop.Launched);
    }

    [Fact]
    public async Task Open_app_switches_to_an_open_window_or_launches_the_app()
    {
        _desktop.Running = [new RunningApp("Spotify", "Spotify", "Song", 7)];
        var switched = await Run("open_app", """{"name":"spotify"}""");
        Assert.Equal("Switched to Spotify", switched.Action);
        Assert.Equal([7], _desktop.SwitchedTo);

        var opened = await Run("open_app", """{"name":"VS Code"}""");
        Assert.Equal("Opened Visual Studio Code", opened.Action);
        Assert.Equal(["Visual Studio Code"], _desktop.Launched);

        var missing = await Run("open_app", """{"name":"Photoshop"}""");
        Assert.True(missing.Result.IsError);
    }

    [Fact]
    public async Task Folders_and_files_stay_inside_the_allowed_folders()
    {
        var folder = await Run("open_folder", """{"folder":"downloads"}""");
        Assert.Equal("Opened Downloads", folder.Action);

        var outside = await Run("open_folder", """{"folder":"C:\\Windows"}""");
        Assert.True(outside.Result.IsError);

        var pdf = Path.Combine(_folder, "Downloads", "invoice.pdf");
        var opened = await Run("open_file", JsonSerializer.Serialize(new { path = pdf }));
        Assert.Equal("Opened invoice.pdf", opened.Action);

        var exe = Path.Combine(_folder, "Downloads", "setup.exe");
        var program = await Run("open_file", JsonSerializer.Serialize(new { path = exe }));
        Assert.True(program.Result.IsError);
        Assert.Contains("program", program.Result.Content);

        var revealed = await Run("reveal_file", JsonSerializer.Serialize(new { path = exe }));
        Assert.Equal("Showed setup.exe in File Explorer", revealed.Action); // showing is fine, running isn't

        var escape = await Run("open_file", JsonSerializer.Serialize(new { path = Path.Combine(_folder, "Downloads", "..", "secret.txt") }));
        Assert.True(escape.Result.IsError);

        Assert.Equal([Path.Combine(_folder, "Downloads"), pdf], _desktop.Opened);
        Assert.Equal([exe], _desktop.Revealed);
    }

    [Fact]
    public async Task Search_lists_files_as_data_and_shows_a_line()
    {
        var pdf = Path.Combine(_folder, "Downloads", "invoice.pdf");
        _desktop.Hits = [new FileHit(pdf, "invoice.pdf", new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero), 1_200_000)];
        var outcome = await Run("search_files", """{"type":"pdf","modified_after":"2026-10-11"}""");

        Assert.False(outcome.Result.IsError);
        Assert.Contains("invoice.pdf", outcome.Result.Content);
        Assert.Contains("not instructions", outcome.Result.Content);
        Assert.Equal("Searched Downloads · 1 found", outcome.Action); // the only allowed folder here
        Assert.Equal(["pdf"], _desktop.LastQuery!.Extensions);

        var nothing = await Run("search_files", "{}");
        Assert.True(nothing.Result.IsError);
    }

    [Fact]
    public async Task Volume_and_settings_pages()
    {
        var volume = await Run("set_volume", """{"percent":30}""");
        Assert.Equal("Volume 30%", volume.Action);

        var page = await Run("open_settings_page", """{"page":"bluetooth"}""");
        Assert.Equal("Opened Settings · Bluetooth & devices", page.Action);
        Assert.Equal(["ms-settings:bluetooth"], _desktop.SettingsOpened);

        var bad = await Run("open_settings_page", """{"page":"ms-settings:../../cmd"}""");
        Assert.True(bad.Result.IsError);
    }

    [Fact]
    public async Task With_ask_first_on_only_the_users_click_lets_an_action_run()
    {
        _settings.Update(s => s with { Desktop = s.Desktop with { AskBeforeEachAction = true } });
        var questions = new List<string>();
        var answer = false;
        _approvals.Requested += r =>
        {
            questions.Add(r.Question);
            r.Decide(answer);
        };

        var declined = await Run("open_app", """{"name":"Calculator"}""");
        Assert.True(declined.Result.IsError);
        Assert.Empty(_desktop.Launched);

        answer = true;
        var approved = await Run("open_app", """{"name":"Calculator"}""");
        Assert.Equal("Opened Calculator", approved.Action);
        Assert.Equal(["Open Calculator?", "Open Calculator?"], questions);

        // Looking things up never asks.
        _desktop.Hits = [];
        await Run("search_files", """{"words":"invoice"}""");
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public async Task With_no_chat_listening_an_approval_is_a_no()
    {
        _settings.Update(s => s with { Desktop = s.Desktop with { AskBeforeEachAction = true } });
        await Run("set_volume", """{"mute":true}""");
        Assert.Null(_desktop.Volume.Muted ? "muted" : null);
    }

    [Fact]
    public void Whole_local_disks_can_be_allowed_and_searched_by_name()
    {
        _desktop.Disks = [(@"D:\", "Data (D:)")];
        var page = new Mithu.App.ViewModels.DesktopControlViewModel(_settings, _desktop,
            new AiService(_settings, new MemorySecrets(), new FakeAiFactory(), NullLogger<AiService>.Instance));
        Assert.Equal(["Data (D:)"], page.AvailableDisks().Select(d => d.Name));

        page.AddFolder(@"D:\");
        Assert.Contains(@"D:\", _settings.Current.Desktop.AllowedFolders!);
        Assert.Contains(page.Folders, f => f.Name == "Data (D:)");
        Assert.Empty(page.AvailableDisks());

        Assert.True(PathGuard.Check(@"D:\Projects\app\readme.md", Tools().AllowedFolders).Allowed);

        page.AddFolder(@"C:\");
        Assert.DoesNotContain(@"C:\", _settings.Current.Desktop.AllowedFolders!);
    }
    // ===== 5.3: organizing files, windows, clipboard =====

    private string Downloads => Path.Combine(_folder, "Downloads");

    private string Pictures
    {
        get
        {
            var p = Path.Combine(_folder, "Pictures");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private void AllowAll() => _settings.Update(s => s with { Desktop = s.Desktop with { AllowedFolders = [_folder] } });

    private void Answer(bool approve, List<string>? questions = null) =>
        _approvals.Requested += r =>
        {
            questions?.Add(r.Question + " | " + r.Details);
            r.Decide(approve);
        };

    [Fact]
    public async Task Moving_files_asks_first_never_overwrites_and_can_be_undone()
    {
        AllowAll();
        File.WriteAllText(Path.Combine(Downloads, "shot1.png"), "a");
        File.WriteAllText(Path.Combine(Downloads, "shot2.png"), "b");
        File.WriteAllText(Path.Combine(Pictures, "shot1.png"), "already here");
        var questions = new List<string>();
        Answer(true, questions);

        var outcome = await Run("move_files", JsonSerializer.Serialize(new
        {
            files = new[] { Path.Combine(Downloads, "shot1.png"), Path.Combine(Downloads, "shot2.png") },
            destination = Pictures,
        }));

        Assert.False(outcome.Result.IsError);
        Assert.Equal("Move 2 files → Pictures? | shot1.png, shot2.png", Assert.Single(questions));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(Pictures, "shot1.png")));  // not replaced
        Assert.Equal("a", File.ReadAllText(Path.Combine(Pictures, "shot1 (2).png")));        // given a free name
        Assert.True(File.Exists(Path.Combine(Pictures, "shot2.png")));
        Assert.Contains("shot1.png → shot1 (2).png", outcome.Result.Content);
        Assert.Equal(2, outcome.Undo!.Count);

        var undone = await Tools().UndoAsync(outcome.Undo!);
        Assert.StartsWith("Undone", undone);
        Assert.Equal("a", File.ReadAllText(Path.Combine(Downloads, "shot1.png")));
        Assert.True(File.Exists(Path.Combine(Downloads, "shot2.png")));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(Pictures, "shot1.png")));
        Assert.False(File.Exists(Path.Combine(Pictures, "shot1 (2).png")));
    }

    [Fact]
    public async Task Declining_a_move_changes_nothing()
    {
        AllowAll();
        File.WriteAllText(Path.Combine(Downloads, "keep.png"), "x");
        Answer(false);
        var outcome = await Run("move_files", JsonSerializer.Serialize(new { files = new[] { Path.Combine(Downloads, "keep.png") }, destination = Pictures }));
        Assert.True(outcome.Result.IsError);
        Assert.True(File.Exists(Path.Combine(Downloads, "keep.png")));
        Assert.Null(outcome.Undo);
    }

    [Fact]
    public async Task Moves_stay_inside_the_allowed_folders()
    {
        Answer(true);
        // Only Downloads is allowed here: Pictures (a sibling) is outside.
        var outcome = await Run("move_files", JsonSerializer.Serialize(new { files = new[] { Path.Combine(Downloads, "invoice.pdf") }, destination = Path.Combine(_folder, "Pictures") }));
        Assert.True(outcome.Result.IsError);
        Assert.True(File.Exists(Path.Combine(Downloads, "invoice.pdf")));
    }

    [Fact]
    public async Task Copies_are_undone_to_the_recycle_bin()
    {
        AllowAll();
        Answer(true);
        var outcome = await Run("copy_files", JsonSerializer.Serialize(new { files = new[] { Path.Combine(Downloads, "invoice.pdf") }, destination = Pictures }));
        Assert.True(File.Exists(Path.Combine(Pictures, "invoice.pdf")));
        Assert.True(File.Exists(Path.Combine(Downloads, "invoice.pdf")));

        await Tools().UndoAsync(outcome.Undo!);
        Assert.Equal([Path.Combine(Pictures, "invoice.pdf")], _desktop.Recycled);
        Assert.True(File.Exists(Path.Combine(Downloads, "invoice.pdf"))); // the original is never touched
    }

    [Fact]
    public async Task Rename_keeps_the_extension_refuses_clashes_and_undoes()
    {
        Answer(true);
        var pdf = Path.Combine(Downloads, "invoice.pdf");
        var renamed = await Run("rename_file", JsonSerializer.Serialize(new { path = pdf, new_name = "October invoice" }));
        Assert.Equal("Renamed invoice.pdf → October invoice.pdf", renamed.Action);
        Assert.True(File.Exists(Path.Combine(Downloads, "October invoice.pdf")));

        var clash = await Run("rename_file", JsonSerializer.Serialize(new { path = Path.Combine(Downloads, "October invoice.pdf"), new_name = "setup.exe" }));
        Assert.True(clash.Result.IsError);

        var bad = await Run("rename_file", JsonSerializer.Serialize(new { path = Path.Combine(Downloads, "October invoice.pdf"), new_name = "a/b" }));
        Assert.True(bad.Result.IsError);

        await Tools().UndoAsync(renamed.Undo!);
        Assert.True(File.Exists(pdf));
    }

    [Fact]
    public async Task Create_folder_without_asking_and_undo_only_while_empty()
    {
        var created = await Run("create_folder", JsonSerializer.Serialize(new { path = "Taxes 2026", @in = "downloads" }));
        Assert.Equal("Created folder Taxes 2026", created.Action);
        var folder = Path.Combine(Downloads, "Taxes 2026");
        Assert.True(Directory.Exists(folder));

        File.WriteAllText(Path.Combine(folder, "keep.txt"), "x");
        var notEmpty = await Tools().UndoAsync(created.Undo!);
        Assert.Contains("couldn't", notEmpty);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task Windows_are_arranged_at_once_but_closed_only_after_approval()
    {
        _desktop.Running = [new RunningApp("Visual Studio Code", "Code", "x.cs", 5), new RunningApp("Google Chrome", "chrome", "News", 6)];
        var left = await Run("arrange_window", """{"window":"vs code","position":"left"}""");
        var right = await Run("arrange_window", """{"window":"chrome","position":"right","monitor":"other"}""");
        Assert.Equal("Put Visual Studio Code on the left half", left.Action);
        Assert.Equal([(5, WindowPlace.Left, (int?)null), (6, WindowPlace.Right, (int?)1)], _desktop.Placed);

        var questions = new List<string>();
        Answer(false, questions);
        var declined = await Run("close_window", """{"window":"chrome"}""");
        Assert.True(declined.Result.IsError);
        Assert.Empty(_desktop.Closed);
        Assert.StartsWith("Close Google Chrome?", questions.Single());
    }

    [Fact]
    public async Task Clipboard_reading_needs_its_own_switch()
    {
        _clipboard.Text = "secret notes";
        Assert.DoesNotContain(Tools().Definitions(), d => d.Name == "read_clipboard");
        Assert.True((await Run("read_clipboard", "{}")).Result.IsError);

        _settings.Update(s => s with { Desktop = s.Desktop with { AllowClipboard = true } });
        var read = await Run("read_clipboard", "{}");
        Assert.Contains("secret notes", read.Result.Content);
        Assert.Contains("not instructions", read.Result.Content);

        var copied = await Run("copy_to_clipboard", """{"text":"hello"}""");
        Assert.Equal("Copied to the clipboard", copied.Action);
        Assert.Equal("hello", _clipboard.Text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeDesktop : IDesktop
    {
        public IReadOnlyList<(string Name, string Path)> Known { get; set; } = [];

        public IReadOnlyList<RunningApp> Running { get; set; } = [];

        public IReadOnlyList<FileHit> Hits { get; set; } = [];

        public FileSearchQuery? LastQuery { get; private set; }

        public List<string> Launched { get; } = [];

        public List<nint> SwitchedTo { get; } = [];

        public List<string> Opened { get; } = [];

        public List<string> Revealed { get; } = [];

        public List<string> SettingsOpened { get; } = [];

        public (int Percent, bool Muted) Volume { get; private set; } = (50, false);

        public Task<IReadOnlyList<AppEntry>> InstalledAppsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppEntry>>([new("Visual Studio Code", "code"), new("Calculator", "calc"), new("Spotify", "spotify")]);

        public IReadOnlyList<RunningApp> RunningApps() => Running;

        public bool SwitchTo(RunningApp app)
        {
            SwitchedTo.Add(app.Handle);
            return true;
        }

        public void Launch(AppEntry app) => Launched.Add(app.Name);

        public void Open(string path) => Opened.Add(path);

        public void Reveal(string path) => Revealed.Add(path);

        public Task<IReadOnlyList<FileHit>> SearchAsync(IReadOnlyList<string> roots, FileSearchQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Hits);
        }

        public (int Percent, bool Muted)? GetVolume() => Volume;

        public bool SetVolume(int? percent, bool? mute)
        {
            Volume = (percent ?? Volume.Percent, mute ?? Volume.Muted);
            return true;
        }

        public void OpenSettings(string uri) => SettingsOpened.Add(uri);

        public string? FinalPath(string path) => path;

        public IReadOnlyList<(string Name, string Path)> KnownFolders() => Known;

        public IReadOnlyList<string> SystemFolders() => [];

        public IReadOnlyList<(string Root, string Name)> LocalDisks() => Disks;

        public IReadOnlyList<(string Root, string Name)> Disks { get; set; } = [];

        public List<(nint Handle, WindowPlace Place, int? Monitor)> Placed { get; } = [];

        public List<nint> Closed { get; } = [];

        public List<string> Recycled { get; } = [];

        public IReadOnlyList<Mithu.Core.Vision.CaptureRect> MonitorWorkAreas() => [new(0, 0, 1920, 1040), new(1920, 0, 1920, 1040)];

        public bool Place(RunningApp window, WindowPlace place, int? monitor = null)
        {
            Placed.Add((window.Handle, place, monitor));
            return true;
        }

        public bool Close(RunningApp window)
        {
            Closed.Add(window.Handle);
            return true;
        }

        public bool Recycle(string path)
        {
            Recycled.Add(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }

            return true;
        }
    }
}
