# How to release a new version of Mithu

A step-by-step guide: change the version number, build the installer, check it, and hand it out.
For background (why Velopack, what the installer does, size and speed) see [docs/packaging.md](docs/packaging.md).

---

## 1. One-time setup (per PC)

You need the .NET 10 SDK (already used to build Mithu) and Velopack's packer, `vpk`:

```powershell
dotnet tool install -g vpk
```

Check it works with `vpk -h`. If PowerShell says `vpk` is not recognised, close and reopen the terminal (the
tools folder `%USERPROFILE%\.dotnet\tools` is added to PATH for new terminals).

> Keep `vpk` and the `Velopack` package in `src/Mithu.App/Mithu.App.csproj` on the same version (currently
> **1.2.161**). To upgrade both: `dotnet tool update -g vpk` and change `<PackageReference Include="Velopack" Version="…" />`
> to the version `vpk` reports.

---

## 2. Change the version number

The version lives in **one place**: `Directory.Build.props` in the repository root.

```xml
<Version>0.1.0</Version>
```

Change it, for example to `0.2.0`, and save. That number is used for the app (`Mithu.exe` file version, the
"Mithu x.y.z started" log line), the installer, and *Settings → Apps*.

### Which number to pick

Versions are `MAJOR.MINOR.PATCH` ([SemVer](https://semver.org)):

| Change | Example | When |
|---|---|---|
| **PATCH** +1 | 0.2.0 → 0.2.1 | Bug fixes only |
| **MINOR** +1 | 0.2.1 → 0.3.0 | New features (reset PATCH to 0) |
| **MAJOR** +1 | 0.9.0 → 1.0.0 | Big milestone or breaking change (reset the others to 0) |
| Pre-release | 1.0.0-beta.1 | Test builds before the real 1.0.0 |

Rules:
- **Every release must be higher than the last one.** Updates only go up: installed copies ignore a version that
  isn't newer, and the packer refuses to build a version that's already in `artifacts\releases`.
- Never reuse or change a version that was already handed out. Made a mistake? Release the next PATCH.

> You can also skip editing the file and pass the version on the command line (`-Version 0.2.0`, see below). Editing
> `Directory.Build.props` is better for real releases: the number is then saved in git with the code.

---

## 3. Build the installer

From the repository root:

```powershell
powershell -File scripts\package.ps1
```

This runs, in order:
1. **All tests** (stops if any fail: no installer is built from broken code),
2. **Publish**: a Release build for Windows x64 with .NET included and precompiled for a fast start,
3. **Pack**: Velopack makes the installer and update files.

It takes a few minutes. At the end it lists the files and sizes.

### Options

| Command | What it does |
|---|---|
| `powershell -File scripts\package.ps1` | Version from `Directory.Build.props`, tests first, **no** auto-updates. |
| `... -Version 0.2.0` | Use this version instead of the one in `Directory.Build.props`. |
| `... -UpdateUrl https://example.com/mithu` | Installed copies check this address for updates (see step 5). Without it, the update menu is hidden. |
| `... -SkipTests` | Don't run the tests (quick local experiments only, never for a real release). |
| `... -Force` | Rebuild a version that was already built: deletes `artifacts\releases` first. Local testing only (it also removes older releases that updates need). |

Options can be combined, for example:

```powershell
powershell -File scripts\package.ps1 -Version 0.2.0 -UpdateUrl https://example.com/mithu
```

---

## 4. What you get

Everything is in `artifacts\releases` (not committed to git):

| File | Use |
|---|---|
| **`MithuCompanion-win-Setup.exe`** | **The installer. This is what people download and run.** |
| `MithuCompanion-win-Portable.zip` | Runs without installing (no shortcuts, no updates). |
| `MithuCompanion-<version>-full.nupkg` | The full update package. |
| `MithuCompanion-<version>-delta.nupkg` | A small update from the previous version (from the second release on). |
| `releases.win.json`, `RELEASES`, `assets.win.json` | The list of releases that update checks read. |

Keep this folder between releases: the packer uses the previous release to make the small delta update.

---

## 5. Updates (optional)

Installed copies can update themselves (⋯ menu → **Updates**), but only if the installer was built with
`-UpdateUrl`. The address is where you'll upload the files from `artifacts\releases`, for example a folder on your
website or a GitHub repository's Releases.

For every release:
1. Build with the **same** `-UpdateUrl` as before.
2. Upload **all** files from `artifacts\releases` to that address (overwrite the release lists, keep older
   `.nupkg` files).

Installed copies then find the new version (automatically once a day, or with *Check for updates now*), download it,
and install it when Mithu restarts (*Restart to update to x.y.z*, or the next time Mithu closes).

> Copies installed from a build **without** `-UpdateUrl` never check for updates. Those users need to download and
> run the new `Setup.exe` once; it updates the existing installation and keeps their data.

---

## 6. Check it before handing it out

1. **Install it** on your PC or a test PC: run `MithuCompanion-win-Setup.exe`.
   - Windows may show *"Windows protected your PC"* because the installer isn't code-signed yet: click
     **More info → Run anyway**.
2. Mithu should start. Check *Settings → Apps → Installed apps*: **Mithu** with the new version number.
3. Try the main things: open the panel, add a note and a reminder, the AI chat if you use it.
4. If you built with `-UpdateUrl`: install the *previous* version, then use ⋯ → **Check for updates now** and
   **Restart to update**; the new version should start.

> Testing on the PC where you develop: the installed Mithu and the one you run from Visual Studio / `dotnet run` share
> the same data folder (`%LOCALAPPDATA%\Mithu`) and can't run at the same time. Close one before starting the other.

---

## 7. Hand it out

- Share **`MithuCompanion-win-Setup.exe`** (download link, GitHub release, USB…).
- Installing again over an older version updates it; notes, reminders, chats and settings are kept.
- **Uninstalling** (*Settings → Apps → Mithu → Uninstall*) removes the program, shortcuts and Mithu's Claude Code
  hooks, but **keeps the user's data** in `%LOCALAPPDATA%\Mithu`. To remove that too, delete the folder afterwards.

---

## 8. Save the release in git

```powershell
git add -A
git commit -m "Release 0.2.0"
git tag v0.2.0
```

The tag marks exactly which code each installer was built from.

---

## Quick checklist

- [ ] Version raised in `Directory.Build.props` (higher than the last release)
- [ ] `powershell -File scripts\package.ps1` (with `-UpdateUrl …` if you use updates) finished with all tests passed
- [ ] Installed and tried `MithuCompanion-win-Setup.exe`
- [ ] Uploaded `artifacts\releases` (if you use updates) / shared `Setup.exe`
- [ ] Committed and tagged `vX.Y.Z`

## Troubleshooting

| Problem | Fix |
|---|---|
| `vpk not found` | `dotnet tool install -g vpk`, then open a new terminal. |
| `There is a release in channel win which is equal or greater to the current version` | Raise the version (step 2). For a local rebuild of the same number only: add `-Force`. |
| `Tests failed: no package built.` | Fix the failing tests (`dotnet test tests\Mithu.Tests -c Release` shows which). |
| `Version 'x' isn't SemVer` | Use three numbers like `1.2.3` (optionally `-beta.1`). |
| Build says a file is in use / locked | Close Mithu (tray icon → Exit) and run the script again. |
| *"Windows protected your PC"* when installing | Expected until the installer is code-signed: **More info → Run anyway**. |
| The update menu doesn't appear | The installer was built without `-UpdateUrl`, or Mithu is running from a build folder instead of the installed copy. |
