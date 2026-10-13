using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Mithu.Core.Desktop;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Desktop;

/// <summary>
/// Desktop control on Windows. Apps come from the Start menu's app list (<c>shell:AppsFolder</c>: desktop and Store
/// apps alike) and start the way Start starts them; open windows from <c>EnumWindows</c>; files through a bounded
/// walk that never follows junctions; volume through Core Audio. Logs what happened, never file names or paths.
/// </summary>
public sealed class WindowsDesktop(ILogger<WindowsDesktop> logger) : IDesktop
{
    private static readonly TimeSpan AppsCacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SearchTimeLimit = TimeSpan.FromSeconds(5);

    private IReadOnlyList<AppEntry>? _apps;
    private DateTime _appsAt;

    // ===== Apps ====================================================================

    public async Task<IReadOnlyList<AppEntry>> InstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        if (_apps is not null && DateTime.UtcNow - _appsAt < AppsCacheFor)
        {
            return _apps;
        }

        // The Shell's COM objects want a single-threaded apartment: read the list on a short-lived STA thread.
        var done = new TaskCompletionSource<IReadOnlyList<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(ReadAppsFolder());
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Couldn't read the Start menu app list");
                done.TrySetResult([]);
            }
        }) { IsBackground = true, Name = "Mithu apps list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var apps = await done.Task.WaitAsync(cancellationToken);
        if (apps.Count > 0)
        {
            _apps = apps;
            _appsAt = DateTime.UtcNow;
        }

        logger.LogInformation("Start menu lists {Count} apps", apps.Count);
        return apps;
    }

    private static List<AppEntry> ReadAppsFolder()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("No Shell.Application");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (AppMatcher.IsLaunchable(name) && !string.IsNullOrWhiteSpace(id) && seen.Add(name))
                {
                    list.Add(new AppEntry(name, id));
                }
            }

            return list;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Launch(AppEntry app)
    {
        // The same way the Start menu starts it (works for desktop and Store apps).
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { @"shell:AppsFolder\" + app.LaunchId }, UseShellExecute = false });
    }

    public IReadOnlyList<RunningApp> RunningApps()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<RunningApp>();
        var names = new Dictionary<uint, (string Name, string Process)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero
                || (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0
                || (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            {
                return true;
            }

            var title = Title(hWnd);
            GetWindowThreadProcessId(hWnd, out var pid);
            if (title.Length == 0 || pid == own)
            {
                return true;
            }

            if (!names.TryGetValue(pid, out var info))
            {
                info = ProcessNames(pid);
                names[pid] = info;
            }

            // Store apps live inside "ApplicationFrameHost": their title is the app's name.
            var name = info.Process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? title : info.Name;
            list.Add(new RunningApp(name, info.Process, title, hWnd));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static (string Name, string Process) ProcessNames(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string description;
            try
            {
                description = process.MainModule?.FileVersionInfo.FileDescription ?? string.Empty;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                description = string.Empty; // another user's or an elevated process
            }

            // Some programs describe themselves by file name ("Notepad.exe"): show "Notepad".
            if (description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                description = description[..^4];
            }

            return (description.Length > 0 ? description : process.ProcessName, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (string.Empty, string.Empty);
        }
    }

    public bool SwitchTo(RunningApp app)
    {
        if (IsIconic(app.Handle))
        {
            ShowWindow(app.Handle, SW_RESTORE);
        }

        return SetForegroundWindow(app.Handle);
    }

    // ===== Windows =================================================================

    public IReadOnlyList<Core.Vision.CaptureRect> MonitorWorkAreas()
    {
        var list = new List<(Core.Vision.CaptureRect Work, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                list.Add((new Core.Vision.CaptureRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), (info.Flags & 1) != 0));
            }

            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Work.X).Select(m => m.Work).ToList();
    }

    public bool Place(RunningApp window, WindowPlace place, int? monitor = null)
    {
        var hWnd = window.Handle;
        switch (place)
        {
            case WindowPlace.Minimize:
                return ShowWindow(hWnd, SW_MINIMIZE);
            case WindowPlace.Restore:
                ShowWindow(hWnd, SW_RESTORE);
                return SetForegroundWindow(hWnd);
        }

        var areas = MonitorWorkAreas();
        Core.Vision.CaptureRect work;
        if (monitor is { } index && index >= 0 && index < areas.Count)
        {
            work = areas[index];
        }
        else
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(hWnd, 2 /* nearest */), ref info);
            work = new Core.Vision.CaptureRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        }

        if (IsZoomed(hWnd) || IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE); // a maximized window can't be resized
        }

        if (place == WindowPlace.Maximize)
        {
            // On another monitor: move there first, then maximize.
            SetWindowPos(hWnd, IntPtr.Zero, work.X + 40, work.Y + 40, work.Width / 2, work.Height / 2, SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(hWnd, SW_MAXIMIZE);
            return SetForegroundWindow(hWnd);
        }

        if (WindowLayout.Target(work, place) is not { } visible)
        {
            return false;
        }

        GetWindowRect(hWnd, out var rect);
        var windowRect = new Core.Vision.CaptureRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var frameRect = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0
            ? new Core.Vision.CaptureRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top)
            : windowRect;
        var target = WindowLayout.WithBorders(visible, windowRect, frameRect);
        var moved = SetWindowPos(hWnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        SetForegroundWindow(hWnd);
        return moved;
    }

    public bool Close(RunningApp window) => PostMessage(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public bool Recycle(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            Func = FO_DELETE,
            From = path + "\0\0", // the API takes a double-null-terminated list
            Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted;
    }

    // ===== Files ===================================================================

    public void Open(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Reveal(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
    }

    public Task<IReadOnlyList<FileHit>> SearchAsync(IReadOnlyList<string> roots, FileSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<FileHit>>(() =>
        {
            var started = Stopwatch.StartNew();
            var scanned = 0;
            var hits = new List<FileHit>();
            var distinct = roots.Where(Directory.Exists)
                .Where(r => !roots.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) && PathGuard.IsInside(r, o)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in distinct)
            {
                var files = new FileSystemEnumerable<FileHit?>(root, (ref FileSystemEntry e) =>
                    query.Matches(e.FileName.ToString(), e.LastWriteTimeUtc)
                        ? new FileHit(e.ToFullPath(), e.FileName.ToString(), e.LastWriteTimeUtc, e.Length)
                        : null,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                        MaxRecursionDepth = 10,
                    })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                    // Never into junctions or linked folders (they could lead outside the allowed folders).
                    ShouldRecursePredicate = (ref FileSystemEntry e) =>
                        (e.Attributes & FileAttributes.ReparsePoint) == 0 && !e.FileName.Equals("node_modules", StringComparison.OrdinalIgnoreCase),
                };

                foreach (var hit in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (hit is not null)
                    {
                        hits.Add(hit);
                    }

                    if (++scanned >= FileSearchQuery.MaxScanned || started.Elapsed > SearchTimeLimit)
                    {
                        logger.LogInformation("File search stopped early after {Count} files", scanned);
                        goto done;
                    }
                }
            }

            done:
            logger.LogInformation("File search looked at {Count} files in {Ms} ms", scanned, started.ElapsedMilliseconds);
            return hits.OrderByDescending(h => h.ModifiedUtc).Take(FileSearchQuery.MaxResults).ToList();
        }, cancellationToken);

    public string? FinalPath(string path)
    {
        // The deepest part that exists, resolved; the rest (not created yet) is added back.
        var existing = path;
        var rest = string.Empty;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return null;
            }

            rest = Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }

        using var handle = CreateFile(existing, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        var real = buffer.ToString();
        return rest.Length == 0 ? real : Path.Combine(real, rest.TrimEnd('\\'));
    }

    public IReadOnlyList<(string Name, string Path)> KnownFolders()
    {
        var list = new List<(string, string)>
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        };
        if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var downloads) == 0)
        {
            list.Add(("Downloads", Marshal.PtrToStringUni(downloads) ?? string.Empty));
            Marshal.FreeCoTaskMem(downloads);
        }

        list.Add(("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        list.Add(("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
        list.Add(("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
        return list.Where(f => f.Item2.Length > 0).ToList();
    }

    public IReadOnlyList<(string Root, string Name)> LocalDisks()
    {
        var windows = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var list = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady
                    || drive.RootDirectory.FullName.Equals(windows, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                list.Add((drive.RootDirectory.FullName, $"{label} ({letter})"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A disk that went away or can't be read: not offered.
            }
        }

        return list;
    }

    public IReadOnlyList<string> SystemFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ];

    // ===== System ==================================================================

    public (int Percent, bool Muted)? GetVolume()
    {
        try
        {
            var volume = Speakers();
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            return ((int)Math.Round(level * 100), muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            logger.LogWarning(ex, "No audio device to read the volume from");
            return null;
        }
    }

    public bool SetVolume(int? percent, bool? mute)
    {
        try
        {
            var volume = Speakers();
            var context = Guid.Empty;
            if (percent is { } p)
            {
                Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(p, 0, 100) / 100f, ref context));
                if (mute is null && p > 0)
                {
                    Marshal.ThrowExceptionForHR(volume.SetMute(false, ref context)); // "set volume to 30" also unmutes
                }
            }

            if (mute is { } m)
            {
                Marshal.ThrowExceptionForHR(volume.SetMute(m, ref context));
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            logger.LogWarning(ex, "Couldn't change the volume");
            return false;
        }
    }

    public void OpenSettings(string uri)
    {
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only ms-settings: pages can be opened.", nameof(uri));
        }

        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private static IAudioEndpointVolume Speakers()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint));
        return (IAudioEndpointVolume)endpoint;
    }

    private static string Title(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ===== Win32 / COM ============================================================

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint FILE_SHARE_ALL = 7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
