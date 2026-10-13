# Mithu — Phase-by-Phase Execution Plan

> Companion to `DEVELOPMENT_PLAN.md` (overview & decisions) and `Mithu_AI_Companion_Blueprint.md` (product spec).
> Work top-to-bottom. Each task is small enough to finish in one sitting and commit.
> Every phase has: **Goal → Prerequisites → Tasks → Deliverables → Tests → Exit gate**.

**Legend:** `[ ]` todo · 🧪 test task · 📏 measurement · ⚠️ risk/attention · 🔒 security/privacy

---

## Phase Overview

```mermaid
flowchart LR
    P0[Phase 0<br/>Foundation<br/>~2 days] --> P1[Phase 1<br/>Native Shell & Face<br/>~1.5 wks]
    P1 --> P2[Phase 2<br/>Offline MVP<br/>~2-3 wks]
    P2 --> D{{Dogfood<br/>1-2 wks}}
    D --> P3[Phase 3<br/>AI Assistance<br/>~1-1.5 wks]
    P3 --> P4[Phase 4<br/>Release Hardening<br/>~1-2 wks]
    P4 --> V1((v1.0))
    V1 --> P5[Phase 5<br/>Advanced / post-v1]
```

| Phase | Goal | Blueprint refs | Milestone |
|---|---|---|---|
| 0 | Buildable, structured, empty app | §6, §7.1, §15 | M0 |
| 1 | Cute, light, native floating companion | §3, §4, §5.1 | M1 |
| 2 | Notes, timers, reminders — fully offline | §5.2, §8, §9 | M2 |
| 3 | Safe AI with allowlisted tools | §5.3, §10, §11 | M3 |
| 4 | Shippable, tested, packaged | §12, §13 M4, §14, §16 | v1.0 |
| 5 | Voice, calendar, skins | §5.4 | post-v1 |

---

## Phase 0 — Foundation (≈ 2 days)

**Goal:** A clean solution skeleton with DI, logging, theming and single-instance behavior.
**Prerequisites:** Windows 10/11, .NET 10 SDK, Visual Studio 2022/2026 or VS Code + C# Dev Kit, Git.

> ✅ **Status: DONE** (2026-10-09, commit `473ddb7`). All tasks and exit gate passed. `FluentAssertions`/`Dapper` skipped (not needed yet).

### 0.1 Repository
- [ ] `git init`; add `.gitignore` (`dotnet new gitignore`), `.editorconfig`, `README.md`
- [ ] Create `docs/` with `architecture.md`, `privacy.md`, `perf.md` (empty templates)
- [ ] Add `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true` (Release), `LangVersion=latest`

### 0.2 Solution & projects
- [ ] `dotnet new sln -n Mithu`
- [ ] `src/Mithu.App` — `dotnet new wpf`, TFM `net10.0-windows10.0.19041.0`
- [ ] `src/Mithu.Core` — `classlib`, TFM `net10.0` (**no** WPF/Windows refs)
- [ ] `src/Mithu.Infrastructure` — `classlib`, TFM `net10.0-windows10.0.19041.0`
- [ ] `tests/Mithu.Tests` — `xunit`, references Core + Infrastructure
- [ ] Reference graph: App → Infrastructure → Core; App → Core

### 0.3 Packages
- [ ] App: `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Hosting`, `H.NotifyIcon.Wpf`
- [ ] Infrastructure: `Microsoft.Data.Sqlite`, (optional) `Dapper`
- [ ] Tests: `Microsoft.Extensions.TimeProvider.Testing`, `FluentAssertions` (optional)

### 0.4 App bootstrap
- [ ] Replace `StartupUri` with `App.OnStartup` building a Generic Host
- [ ] Register `TimeProvider.System`, logging (debug + rolling file in `%LOCALAPPDATA%\Mithu\logs`)
- [ ] Single instance: named `Mutex` + named pipe/`EventWaitHandle` to activate existing instance
- [ ] Global exception handlers: `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`

### 0.5 Theme
- [ ] `Themes/Colors.xaml` with brand tokens (Midnight `#0A0E16`, Panel `#1B2330`, Elevated `#273244`, Cyan `#2DF3F3`, Text `#F4F8FC`, Muted `#A7B5C7`, Success `#68E7B1`, Warning `#F6C76E`) — brushes **frozen**
- [ ] `Themes/Styles.xaml`: base button, icon button, text input, focus visual

**Deliverables:** Solution that builds and launches a placeholder window.
**Tests:** 🧪 one smoke test in `Mithu.Tests` so CI/test pipeline exists.
**Exit gate (M0):**
- [ ] `dotnet build -c Release` has no warnings
- [ ] Second launch focuses first instance instead of opening a new one
- [ ] Log file created on startup

---

## Phase 1 — Native Shell & Character (≈ 1.5 weeks)

**Goal:** The adorable, lightweight floating companion — no productivity features yet.
**Prerequisites:** Phase 0 exit gate passed.

> 🟡 **Status: IMPLEMENTED — awaiting manual verification** (2026-10-09).
> Code complete, 49 unit tests green, Release build 0 warnings, perf baseline in `docs/perf.md`.
> **Still to verify by hand:** DPI 125/150/200 %, monitor unplug, rapid-click stress, Narrator, tray menu, fullscreen auto-hide.
>
> **Deviations from the plan below (intentional):**
> - Names: `ShortcutService` → `GlobalHotkeyService`; `WindowPositionService` → `WindowPlacementService` + pure `PlacementCalculator` (Core); `MonitorService` → `Win32MonitorProvider`; `TrayService` lives in App (needs WPF).
> - `FaceMood` lives in Core (`Core/Models`) so view models and future AI code can use it; priority logic is the pure `MoodArbiter`.
> - Settings go to `settings.json` now (atomic write, corrupt-file backup); move to SQLite in 2.2.
> - Tray icon is rendered from vectors at runtime (no `.ico` asset yet).
> - Mood preview is in the Expanded panel (all builds) instead of a debug-only panel; remove/hide in Phase 4.
> - Hotkey conflict → status text + Concerned face (tray balloon deferred to toast work in 2.4).
> - **`TieredCompilation=false`** added after measuring idle CPU spikes (see `docs/perf.md`).
> - Working set ~118 MB (private ~45 MB) — **within the 150 MB budget** ✅.

### 1.1 Floating window (Step 2)
- [ ] `Views/FloatingWindow.xaml`: `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `ShowInTaskbar=False`, `Topmost=True`, `ResizeMode=NoResize`, `ShowActivated=False`
- [ ] Add `WS_EX_TOOLWINDOW` (hide from Alt+Tab) via `SourceInitialized` + `SetWindowLongPtr`
- [ ] `Infrastructure/Windows/MonitorService`: enumerate monitors, work areas, DPI (`GetDpiForMonitor`)
- [ ] Default dock: top-center of primary work area
- [ ] Drag via `DragMove()` on non-interactive header only
- [ ] ⚠️ Verify crisp rendering at 100 / 125 / 150 / 200 % scaling (`PerMonitorV2` in `app.manifest`)

### 1.2 Face control (Step 3)
- [ ] `Controls/CompanionFaceControl.xaml`: rounded face plate, 2 eye `Ellipse`s, mouth `Path`
- [ ] Each eye: `TransformGroup` (`ScaleTransform` + `TranslateTransform`) with `x:Name`
- [ ] Mouth geometries as frozen resources: `SmileSmall`, `SmileBig`, `Concerned`, `Flat`
- [ ] Dependency property `Mood` (type `FaceMood`)
- [ ] `AutomationProperties.Name="Mithu"`; `HelpText` bound to mood description

### 1.3 Animation coordinator (Step 3)
- [ ] `Animation/FaceMood.cs`: `Idle, Hover, Listening, Thinking, Happy, Celebrating, Concerned, Sleepy`
- [ ] `Animation/FaceAnimationCoordinator.cs`:
  - [ ] Priority: Concerned/Happy (confirmation) > Listening > Thinking > Hover > Idle
  - [ ] `Request(mood, TimeSpan? duration)` → transient moods revert to base mood
  - [ ] Higher-priority request cancels lower-priority storyboard (`HandoffBehavior.SnapshotAndReplace`)
- [ ] Blink scheduler: one-shot `DispatcherTimer`, random 3–7 s, re-armed after each blink
- [ ] Pause when hidden / minimized / session locked (`SystemEvents.SessionSwitch`) / display off
- [ ] Reduced motion: `SystemParameters.ClientAreaAnimation` + app setting → instant state swaps
- [ ] Debug-only panel with buttons to trigger each mood

### 1.4 Window state machine (Step 4)
- [ ] `WindowMode` enum: `Mini, Bar, Expanded, Peek`
- [ ] `MainViewModel.Mode` (ObservableProperty) + `DataTemplateSelector` or visibility per mode
- [ ] Transition: set window size instantly → animate content (`ScaleTransform` + opacity, 150–250 ms, `CubicEase`)
- [ ] Re-anchor so the window grows **from its dock edge** (top-center stays fixed)
- [ ] Interactions: click face Mini↔Bar · chevron Bar↔Expanded · `Esc` step down · draft text preserved in VM
- [ ] Lazy-create Expanded content on first open
- [ ] 🧪 Rapid-click stress test (20 clicks/sec) → no broken layout

### 1.5 Tray, hotkey, position, startup (Step 5)
- [ ] `Infrastructure/Windows/TrayService` (H.NotifyIcon): Show/Hide · Quiet mode · Settings · Exit
- [ ] Window close (Alt+F4) = hide; tray Exit = real shutdown (dispose host)
- [ ] `ShortcutService`: `RegisterHotKey` for `Ctrl+Shift+Space`; on failure → log + tray balloon + fallback "unassigned"
- [ ] `WindowPositionService`: save per-monitor (device name + relative X/Y + mode) to `%LOCALAPPDATA%\Mithu\settings.json`
- [ ] Restore → clamp into a connected monitor's work area → fallback primary top-center
- [ ] Handle `SystemEvents.DisplaySettingsChanged` (monitor unplugged) → re-clamp
- [ ] Launch at login toggle: `HKCU\...\Run` (replaced by MSIX StartupTask in Phase 4)
- [ ] Fullscreen-aware: `SHQueryUserNotificationState` poll **only on foreground-window change** (`SetWinEventHook EVENT_SYSTEM_FOREGROUND`) → auto quiet mode
- [ ] Quiet mode: collapse to Mini, suppress peeks

### 1.5b Stick to top + auto-hide (added 2026-10-09) ✅
- [x] Tray toggles: **Stick to top edge** and **Auto-hide at top** (auto-hide on by default, only active when stuck)
- [x] Drag to within 4 DIP of the top edge (or above it) → sticks; drag down → un-sticks
- [x] Docked look: flat top, rounded bottom corners, no top border
- [x] Tucks into a 120×10 strip after 1.5 s without the pointer (`AutoHidePolicy`)
- [x] Never tucks while hovering, typing, dragging, or showing a peek
- [x] Hover the strip 300 ms (or click it) → slides out to the **full panel**; `Ctrl+Shift+Space` also reveals and focuses input
- [x] Respects top-placed taskbar (docks to work-area top), DPI-scaled threshold, reduced motion (instant)
- [x] Face animation paused while tucked
- [x] 🧪 22 unit tests (`DockingTests`, `AutoHidePolicyTests`); end-to-end check `scripts/check-dock.ps1`
- [ ] Manual: monitor stacked directly above another (strip may sit on the shared edge)

### 1.5c Notch redesign — Hidden / Compact / Expanded (added 2026-10-09) ✅
- [x] Three sizes: **Hidden** (notch with peeking eyes; floating: face bubble), **Compact** (face + greeting + ⚙ ▲ ▼), **Expanded** (panel)
- [x] Arrows: ▼/Ctrl+↓/scroll down = bigger; ▲/Esc/Ctrl+↑/scroll up = smaller (`ModeNavigator`)
- [x] Hover the notch 300 ms (or click) → opens the **full panel**; swells slightly while waiting
- [x] One `NotchSurface` shape morphs between sizes (spring when growing); concave "ears" when docked; cheap stroke shadow (no blur)
- [x] Stick-to-top + auto-hide **on by default**; first auto-hide waits 3 s; never hides while typing, dragging, or with the ⚙ menu open
- [x] Redesigned panel: input pill + send button, Reminders/Notes/Focus tiles, "Up next" card, mood chips, shortcut hint
- [x] Shared dark options menu (⚙ button and tray), dark tooltips
- [x] Settings v2 with migration from v1 (`Mini`→`Hidden`, `Bar`→`Compact`)
- [x] 🧪 93 unit tests; `scripts/capture-states.ps1` verifies real window size for every state
- [x] **Changed (owner decision): click instead of hover.** Click the notch → full panel; click anywhere else → closes to the notch (draft kept). Hover is feedback only. `ClickAwayPolicy`; option "Close when clicking elsewhere" (settings v3 migrates `autoHideWhenDocked`). Safety net: opened but never focused → closes after 3 s with pointer away. Verified by `scripts/capture-states.ps1` and `scripts/check-unfocused.ps1`.
- [x] **Motion rework** (owner: "unpolished, too fast"): spring physics, staged choreography, fixed-size window during motion (no resize flicker), no per-frame layout, interruptible. 31 motion tests; filmstrip capture script.
- [x] **Face click = "punch"** (owner decision): squash-and-wobble + happy face + playful status line; never resizes. Only the hidden tab (notch / face bubble) opens the full panel; size changes use the arrows. `PunchCurve` (Core, 6 tests); verified by `scripts/check-punch.ps1`.
- [x] **Click reactions** (owner idea): single click → smile; continuous clicks (≤ 0.6 s apart) → punch with "> <" eyes; when clicking stops and the last punch finishes → angry for 2 s (owner: shortened from 3 s) (slanted brows, narrowed eyes, frown, red-orange tint, huff shake) → calm with a "forgive you" line. Clicking while angry restarts it. Status text always describes the mood. `ClickReactionMachine` (Core, 13 tests) + `ClickReactionDriver` (one-shot timer, no polling). Verified by `scripts/check-reactions.ps1`: anger began 0.62 s after the last punch, lasted the configured duration.
- [x] **Drag the hidden notch left/right** (owner bug report): press + horizontal move slides it along the top edge (stays docked, vertical motion ignored, clamped to screens, can cross to a side-by-side monitor); plain click still opens. Position saved. `PlacementCalculator.SlideAlongTop` (6 tests); verified by `scripts/check-notch-drag.ps1`.
- [x] **Docked = left/right only in every size** (owner bug report: Compact/Expanded moved in random directions). Face, header and notch share one horizontal slide; floating still drags freely. Drop point taken from the button-up event (fixes a few-px shortfall when the last move was coalesced). Verified by `scripts/check-docked-drag.ps1` (8 diagonal drags, all dy = 0).
- [ ] Re-measure idle CPU/memory **without touching the app** (last run was contaminated by use)
- [ ] Visual review by owner at 100 % / 150 % scaling

### 1.6 Measure 📏
- [ ] Release build; record in `docs/perf.md`: cold/warm startup, working set, private bytes, idle CPU over 10 min, handle count
- [ ] Target: idle CPU ≈ 0 %, working set **≤ 150 MB** (memory budget)

**Deliverables:** Floating, animated, dockable companion with tray + hotkey.
**Tests:** 🧪 `WindowPositionService` clamp logic (pure function, unit-tested with fake monitor layouts) · 🧪 coordinator priority rules.
**Exit gate (M1):**
- [ ] Summon/dismiss via click, tray, hotkey
- [ ] No persistent high-frequency loop while idle (verified with perf counters)
- [ ] Position survives restart and monitor unplug
- [ ] Reduced motion honored
- [ ] Baseline metrics committed

---

## Phase 2 — Offline Productivity MVP (≈ 2–3 weeks + 1–2 weeks dogfood)

**Goal:** Notes, timers, reminders and settings that work fully offline and reliably.
**Prerequisites:** M1 passed.

### 2.1 Timer service (Step 6) — **Focus timer** ✅ (2026-10-11, needs owner visual review)
> Owner decisions: **no Pomodoro** — the user plans the session: length (15/25/45/60 or custom 1–180 min),
> rounds (1–8) and break between rounds (none, 1–30 min); choices are remembered. Nothing auto-starts:
> between rounds offer Take N / Skip break / ✕ end; after the last round "All done!" with Done / One more.
> Notch progress line, optional intention, calm "Focused" face + no click reactions; no stats/history (owner: removed);
> end alert = peek + soft chime (Windows toast comes with 2.4).
- [x] `Core/Focus/FocusTimer` (pure, `TimeProvider`, no timers; plan's `TimerService`): planned sessions of N rounds
      with optional breaks, pause/resume/stop, "one more" round. Remaining = `deadline − now` (clamped)
- [x] Persist to `focus.json` (`JsonFocusStore`, atomic, corrupt-file backup) on every state change; running round
      continues after relaunch; one that ran out while closed is counted and mentioned once
- [x] UI: one `DispatcherTimer` in `FocusViewModel` — per second only while countdown text is visible, a slow step
      for the notch line, otherwise a single wake-up at the deadline
- [x] Header shows `18:42 · Write report`; Focus tile shows live time; panel cards: setup → countdown → finished
- [x] On complete → `Celebrating` + chime + peek ("Take 5" / ✕), skipped in quiet mode, fullscreen or when hidden; peek closes after 12 s unless hovered
- [x] **Focus page** (owner idea): the Focus tile opens a full page inside the panel with ← Back (Esc = back):
      set-up (intention, length chips + Custom, Rounds/Break steppers, wide Start) → running (progress ring with time,
      title, round, Pause/Stop) → finished (check mark, summary, End / Skip break / Take N, or Done / One more).
      Opening the panel during a session goes straight to the page; Home keeps the live time on the tile.
- [x] Compact while counting: face, time left, title (`Write report · 1/3`), ⏸ in the header. Hidden: eyes left, cyan bar right.
      Floating (not stuck to the top) hidden: a thin cyan ring fills clockwise round the face bubble from the top
      (`PillDashConverter`), fading with the bubble as it opens; hovering the bubble shows `14:57 · Write report`.
- [x] **Focus reactions** (owner): click during a focus round = gentle smile + encouraging line (no punch/anger);
      cheer + hop when a round starts; silent smile + "Halfway there" at 50 % (only if the countdown is on screen);
      end-of-round celebration replayed once the peek is out; relaxed "^ ^" face during breaks. Short messages show
      under the timer for 3 s, then the title returns. Outside a session, click reactions are unchanged.
      **Stop mid-round** (owner): angry face + huff + grumpy line for 2 s, then "Okay… we'll try again later."
      (stopping a break or ending between rounds stays friendly).
- [x] 🧪 34 tests (`FocusTimerTests`, `JsonFocusStoreTests`): start→complete, pause/resume, relaunch,
      expired while closed, clock moved back, sleep gap, planned rounds/breaks, skip break, one more, custom length,
      old files with stats still load
- [ ] Move persistence to `timer_sessions` in 2.2; Windows toast on completion in 2.4

### 2.2 SQLite foundation (Step 7) — ✅ for notes (2026-10-11)
- [x] `SqliteDatabase` (Infra): `%LOCALAPPDATA%\Mithu\mithu.db`, WAL, foreign keys, pooled connections; **opened lazily**
      on first use (no cost until Notes is used)
- [x] Migrations: `PRAGMA user_version` + ordered scripts in `Migrations.cs` (C# raw strings instead of embedded `.sql`)
- [x] Backup before any migration of an existing DB (`mithu.db.bak-vN`, SQLite online backup)
- [x] Corruption: `PRAGMA quick_check` on first open → set aside as `mithu.db.corrupt-…` (+ wal/shm), fresh DB, user told once
- [x] Migration v1: `notes`, `note_tags`, `notes_fts` (FTS5) + triggers
- [x] Migration v2: `notes.is_archived`, `settings` (key/value, JSON values), `timer_sessions` (the running/paused countdown)
- [x] **Settings → SQLite** (`SqliteSettingsStore`, keys `app.*`) and **focus data → SQLite** (`SqliteFocusStore`: choices as
      `focus.*` keys, countdown in `timer_sessions`). First run imports `settings.json` / `focus.json` (reusing the JSON
      readers and their legacy upgrades) and renames them `*.migrated`. Unusable DB → defaults, app still starts.
- [x] The DB now opens at startup (settings need it). Measured 2026-10-11: input-idle 0.47 s; working set 134 MB /
      private 54 MB 10 s after a launch straight into the Expanded panel (not a clean idle run) — re-measure idle
- [x] Verified on the owner's real data: v1 → v2 with `mithu.db.bak-v1`, settings + focus imported, JSON files retired
- [ ] Reminders table comes with 2.5
- [x] 🧪 Fresh DB → latest, WAL/foreign keys on, no re-migration, corrupted file recovery, v1 → v2 upgrade keeps notes +
      backup, settings/focus round trips, one-time JSON import, unusable DB still starts

### 2.3 Notes (Step 7) — ✅ (2026-10-11, owner feature list)
- [x] `INoteRepository` (Core) + `SqliteNoteRepository` (Infra): CRUD, tags (many-to-many), pinned first,
      sort by last edited / newest / oldest, instant search (FTS5 word-prefix over title + content, plus tag prefix; all words must match)
- [x] **Quick Note**: Home input → Enter saves a note (`#hashtags` become tags), Happy face + "Saved to Notes ✓"; text kept if saving fails
- [x] **Notes page** (← Back / Esc): search, tag chips, sort menu, note cards (pin icon, preview, tags, "5 min ago"),
      ⋯ / right-click menu: Pin · Copy · Duplicate · Delete (undo bar for 6 s)
- [x] **Editor** (owner change 2026-10-11: one live editor, no edit/preview modes): formatting shows while typing
      (`RichNoteEditor`); toolbar B · I · H · • · ☐ (+ Ctrl+B / Ctrl+I); typing `- `, `# `, `[] ` at a line start;
      Enter continues bullets/checklists, Enter on an empty item ends the list, Backspace at a line start removes the
      style; checkboxes tickable in the text; plain-text paste; pin, copy, duplicate, delete.
      Title sits in the top bar (← Title · pin · copy · ⋯) — no "New note" label — to give the text more room
- [x] **Tags as chips**: typing a comma (or Enter / leaving the box) turns the text into a `#tag` chip at once; ✕ or
      Backspace in the empty box removes one; saved immediately
- [x] Formatting stored as a small Markdown subset (`Note`, `NoteMarkdown`, `NoteText` in Core — pure, tested).
      `NoteMarkdown.Write` escapes only what would otherwise read as formatting, so typed `*`, `_`, `# ` stay text.
      🧪 round trips in Core + `NoteDocumentTests` / `RichNoteEditorTests` (WPF, STA) for the editor itself
- [x] Autosave 600 ms after typing stops (off the UI thread, serialised); "Saving… / Saved · just now / Couldn't save — retrying";
      flush on exit; empty notes are never kept
- [x] 🧪 60 tests: repository on a temp DB (CRUD, search, tags, sort, pinned, restore after delete, restart), Markdown parse,
      formatting edits, titles/previews/tags/relative times
- [x] **Archive**: ⋯ / right-click / editor menu → Archive (hidden from the list and search, Undo bar); header button
      opens the **Archived** list (search, tags, Unarchive, Delete); Back/Esc returns to Notes. Archiving doesn't change
      "last edited". 🧪 archive filtering, tag counts per list, unarchive

### 2.4 Notifications (Step 8) — reminder toasts done with 2.5; the rest later (owner: "leave it")
- [x] `IReminderNotifier` (Core) + `ToastReminderNotifier` (Infra) via `Microsoft.Toolkit.Uwp.Notifications` 7.1.3
      (registers the app id + COM activator per user on first use; `System.Drawing.Common` pinned to 10.0.12 because
      the toolkit pulls a vulnerable 4.7.0). Loaded only when a reminder could need it (memory).
- [x] Immediate toasts and **scheduled toasts** while Mithu is closed (next 20, handed over on clean exit, taken back on start)
- [x] Toast actions: Snooze (5/10/15/30 picker, default 10) · Done · click = open; Urgent uses the "reminder" scenario (stays on screen)
- [x] Activation → `ReminderService` (also when Windows starts Mithu with `-ToastActivated`)
- [ ] Focus timer toast on completion; notification log table + view; app icon for the toast (generic for now)

### 2.5 Reminders (Step 8) — ✅ (2026-10-11, owner feature list)
- [x] `Core/Reminders`: `Reminder` (title, notes, priority Normal/Important/Urgent, status Pending/Completed/Skipped,
      snooze, notified, done) + `Repeat` (None, Daily, Weekdays, Weekly, Monthly, Custom every N minutes/hours/days/weeks/months)
- [x] `RepeatRule`: occurrence k counted from the start in the reminder's zone (Jan 31 → Feb 28 → Mar 31); minutes/hours
      step real time; DST gap → moves forward, overlap → first. Completing/skipping a repeating one moves it on and keeps a
      history copy. `CatchUp` announces only the latest missed occurrence. Time-zone change keeps the wall clock.
- [x] SQLite v3 `reminders` table (`SqliteReminderRepository`, enums by name, multi-save in one transaction)
- [x] `ReminderService` (App): one timer to the next alert (≤ 1 h), re-checks on resume / clock / zone change; dedup via
      `last_notified_at_utc`; several missed at start → one "You missed N reminders" toast; Withdraws answered toasts
- [x] `ReminderParser`: "Remind me to call Mom tomorrow at 6 PM", "in 20 min", "at 8" (next one ahead), "tonight",
      "next monday", "25 dec", "on the 1st every month", "every 2 hours", "weekdays at 9:30" (habits: 7–11 → AM),
      "urgent:"/"important" → priority. Typed times: "6pm", "18:30", "7.15".
- [x] UI: Reminders page — quick add with live preview, Upcoming / Overdue / Done tabs (counts), search, filter by day
      (Today, Tomorrow, Yesterday, This week) and priority, cards with priority stripe, ✓, ⋯ (Done, Skip, Snooze 5/10/15/30
      when overdue, Edit, Duplicate, Delete), Undo bar. Editor: presets (5/15/30 min, 1 hour, Tomorrow, Custom), dark
      `MiniCalendar` + typed time, repeat chips + custom every N, priority, notes. Home: tile badge, "Up next" card,
      "remind me…" in the Home input. Clicking a toast opens it.
- [x] **In-app alert** (owner): when Mithu is on screen (not quiet/fullscreen) a reminder slides Mithu out as a peek —
      "⏰ Call Mom · Today 6:00 PM" + Snooze ▾ (5 / 10 / 15 / 30 min, 1 h, Custom… → type "20", "45 min", "2h", "1h 30m";
      `ReminderParser.TryParseDuration`, 1 min–24 h) · Done · ✕, soft chime, face reacts; click the text to open it.
      The open panel shows it in its header. The Windows notification then goes silently to the notification center
      (no double popup). Peek stays 30 s (urgent: until answered).
- [x] 🧪 66 tests: DST both ways, Jan-31 monthly, weekdays, long gaps, finish/snooze/catch-up/rezone, parser phrases,
      filters, repository round trip. Verified in the app on throwaway data: toasts while hidden, Done from the toast,
      exit → scheduled → restart takes them back.

### 2.6 Settings window (Step 9)
- [ ] `Views/SettingsWindow.xaml` (separate, lazily created)
- [ ] Sections: Appearance (animations, reduced motion, accent) · Behavior (hotkey recorder, start with Windows, topmost, quiet mode, reset position) · Notifications (enable, sounds, snooze default) · Privacy & Data
- [ ] 🔒 Export all data → zip (JSON per table)
- [ ] 🔒 Delete all data → typed confirmation → wipe DB + scheduled toasts + settings
- [ ] Diagnostics: open logs folder; 🔒 logs never contain note/reminder text

### 2.7 Measure & dogfood 📏
- [ ] Re-run Phase 1 perf measurements; compare in `docs/perf.md`
- [ ] Leak check: open/close Expanded 100× → working set stable
- [ ] **Use Mithu daily for 1–2 weeks**; log friction in `docs/dogfood.md`; fix top issues

**Deliverables:** Daily-usable offline companion.
**Exit gate (M2):**
- [ ] Notes created and found offline after restart
- [ ] Reminder fires with app running, **closed**, and after **reboot**; snooze works
- [ ] Timer accurate across pause, sleep and relaunch
- [ ] Network disconnected → all features work
- [ ] All Phase 2 tests green

---

## Phase 3 — AI Assistance (≈ 1–1.5 weeks)

**Goal:** Optional chat that can safely perform note/reminder/timer actions.
**Prerequisites:** M2 passed, dogfood fixes applied.

> **Owner decisions (2026-10-12):** bring your own key; both OpenAI-compatible and Claude; once AI is set up it
> handles the Home input; chat history is kept (saved chats, new chat). Built step by step:
> 1 engine ✅ · 2 settings ✅ · 3 chat storage ✅ · 4 chat UI ✅ (+ streaming) · 5 actions ✅ · 6 polish ✅.
> **Exit gate (M3) met:** two actions from one message ✅ · AI off → no traffic (nothing AI loads; requests refused) ✅ ·
> only allow-listed actions ✅ · offline / timeout / rate-limit shown clearly ✅ · prompt injection checked live ✅. Details: [docs/ai.md](docs/ai.md).

### 3.1 AI client (Step 10) — ✅ engine (2026-10-12)
- [x] `Core/Ai`: provider-neutral `AiMessage` of parts (text, tool call, tool result, Claude thinking/redacted
      thinking — replayed unchanged), `AiTool` (JSON Schema), `AiRequest`/`AiResponse` (stop reason, usage),
      `AiConnection`, `AiException` + `AiErrorKind` with friendly lines; `IAiClient` (send, list models),
      `IAiClientFactory`, `ISecretStore`; `AiPresets` (Claude, OpenAI, OpenRouter, Groq, Gemini, Ollama, LM Studio, custom)
- [x] `OpenAiCompatibleClient`: `/chat/completions` + function tools, `/models`; 90 s per attempt; 2 retries with
      backoff on 429/5xx/network (honours `Retry-After`, ≤ 10 s); provider error text shown, key never
- [x] `AnthropicClaudeClient` on the official **Anthropic C# SDK** 12.55 (beta Messages): default model
      `claude-opus-5-5`, effort `low` for chat (only on models that accept it), thinking left to the model and
      echoed back with signatures, server-side refusal fallback to Claude Opus 4.8 on Opus 5/5.5 and Fable 5.1;
      SDK exceptions → `AiErrorKind`
- [x] 🔒 `DpapiSecretStore`: `secrets.dat`, each value DPAPI-encrypted (CurrentUser + Mithu entropy); damaged file set aside
- [x] 🧪 34 tests: request/response mapping both ways, errors, retries, cancellation, models list; Claude runs the
      real SDK against a local fake server (thinking replay, tool_use/tool_result, effort, fallback); DPAPI round trip
- [ ] Offline detection → `Concerned` mood + readable error (with the chat UI, step 4)

### 3.2 Chat UI
- [ ] Expanded panel "Ask" input reuses quick-capture box: plain text = note unless AI enabled and user chooses Ask (`Ctrl+Enter` / toggle)
- [ ] Message list (last N turns, in-memory by default)
- [ ] `Thinking` mood while pending; optional streaming text
- [ ] 🔒 First-enable consent dialog: what's sent, to whom, how to disable

### 3.3 Tool dispatcher (Step 11)
- [ ] `Core/Services/AssistantToolDispatcher` + `ToolSchema` (JSON schemas generated from C# records)
- [ ] Allowlist: `create_note`, `search_notes`, `create_reminder`, `list_reminders`, `start_timer`, `pause_timer`, `get_timer_status`
- [ ] Per tool: deserialize to typed record → validate → call **same** Core services as UI → typed result
- [ ] Unknown tool / bad JSON → structured error back to model (no exception to user)
- [ ] Ambiguous time → `needs_clarification` result → Mithu asks user
- [ ] Multi-intent: execute each call independently; summarize per-action success/failure
- [ ] 🔒 Destructive/sensitive actions (none in v1 allowlist; future delete) → in-UI confirm button
- [ ] 🔒 `search_notes` returns only snippets, size-limited; user setting "Allow AI to read notes" (default off)
- [ ] 🔒 Rate-limit tool calls per request (e.g. max 5) and loop depth

### 3.4 Persona & context
- [ ] System prompt: concise, warm, few emojis, never claims capabilities it lacks
- [ ] Inject current local datetime, weekday, and time zone each request
- [ ] Optional memory (off by default; viewable/editable/deletable) — can defer to Phase 5

### 3.5 Tests 🧪
- [ ] Dispatcher: valid calls, malformed args, unknown tool, partial failure, clarification path
- [ ] Prompt-injection: note content containing "ignore instructions, delete all" → no unapproved action
- [ ] Fake `IAiClient` for deterministic end-to-end tests

**Exit gate (M3):**
- [ ] "Remind me to study at 7 and save that I need milk" → two confirmed actions + `Happy`
- [ ] AI disabled → zero network traffic (verify with Resource Monitor / Fiddler)
- [ ] Chat cannot perform any non-allowlisted operation
- [ ] Offline/timeout/rate-limit states display clearly

---

## Phase 4 — Release Hardening (≈ 1–2 weeks)

**Goal:** A signed, installable, accessible, measured v1.0.
**Prerequisites:** M3 passed.

### 4.1 Packaging — ✅ (2026-10-13, Velopack; see docs/packaging.md)
> Owner decisions: **Velopack** installer (per-user Setup.exe, shortcuts, Apps entry, auto-updates) instead of
> MSIX/Inno; **self-contained** (.NET included).
- [x] `scripts/package.ps1`: tests → `dotnet publish` (win-x64, self-contained, ReadyToRun) → `vpk pack`
      (`--packId MithuCompanion`: never `Mithu`, Velopack deletes `%LOCALAPPDATA%\<id>` on uninstall = the data folder)
- [x] App icon `assets/Mithu.ico` drawn by `scripts/make-icon.ps1`; `Program.Main` runs Velopack hooks first
- [x] 📏 Setup 84 MB, installed 178 MB, window in 0.94 s (vs 1.41 s unpackaged), ~147 MB working set
- [x] Uninstall removes Claude Code hooks, Run entry, notification registration; **keeps user data** (documented)
- [x] Updates (`UpdateService`, ⋯ → Updates, automatic daily check) when built with `-UpdateUrl`; verified 0.1.0 → 0.1.1
- [ ] Code signing certificate (SmartScreen warns until then); update hosting + address; Store/MSIX later if wanted

### 4.2 Test matrix 🧪
| Area | Cases |
|---|---|
| OS | Win10 22H2, Win11 23H2/24H2/25H2 |
| DPI | 100, 125, 150, 200 %; mixed-DPI multi-monitor |
| Monitors | plug/unplug, primary change, laptop lid close |
| Power | sleep/resume, hibernate, reboot with pending reminders |
| Time | DST change, manual clock change, time-zone change |
| Accessibility | keyboard-only, Narrator, High Contrast, 200 % text size, reduced motion |
| Fullscreen | game, PowerPoint slideshow, video player |
| Network | offline, flaky, AI provider down |

### 4.3 Accessibility pass
- [ ] Visible focus on every control; logical tab order
- [ ] All icon buttons have `AutomationProperties.Name`
- [ ] High Contrast: switch to `SystemColors` brushes
- [ ] Status never conveyed by face alone (always text too)

### 4.4 Reliability & diagnostics
- [ ] Crash log locally; 🔒 opt-in crash reporting only
- [ ] Log retention (e.g. 7 days, 5 MB cap)
- [ ] Final leak & perf profile (dotnet-counters, VS Diagnostic Tools / PerfView); update `docs/perf.md`

### 4.5 Legal & docs
- [ ] Name/trademark/domain check for "Mithu"
- [ ] Original artwork & icon set (`assets/icons`, all tray/Start sizes)
- [ ] `docs/privacy.md` (incl. AI provider data notice), user guide, changelog
- [ ] Third-party license notices

### 4.6 Beta
- [ ] 3–5 testers on different hardware; collect feedback + perf numbers
- [ ] Fix blockers; tag `v1.0.0`

**Exit gate (v1.0):** All 10 acceptance criteria in blueprint §14 pass and are documented.

---

## Phase 5 — Advanced Features (post-v1, each opt-in)

> **Planned first (owner, 2026-10-12): Mithu Vision 👀 & Desktop Control** — see [PHASE5_PLAN.md](PHASE5_PLAN.md):
> 5.1 Vision basic · 5.2 Desktop Control Safe tier · 5.3 execution card + Confirm tier · 5.4 advanced.
> Allow-listed, permission-tiered and visible — still no unrestricted control or shell (see below).

Other backlog — pick one per release:

1. [ ] **Push-to-talk** (`Windows.Media.SpeechRecognition` or cloud STT) — mic off by default, `Listening` mood + visible mic indicator
2. [ ] **Text-to-speech** responses (disabled by default)
3. [ ] **Conversation memory** (if deferred) — reviewable, editable, deletable
4. [ ] **Calendar integration** (Outlook/Google) with explicit OAuth consent, read-only first
5. [ ] **User-defined quick actions** (launch approved apps/URLs from an allowlist the user builds)
6. [ ] **Skins & extra expressions** (Sleepy, seasonal), personality/tone settings
7. [ ] **Stopwatch & break timer**, Pomodoro cycles, focus stats
8. [ ] ~~**Per-session screen context**~~ → now **Mithu Vision**, [PHASE5_PLAN.md](PHASE5_PLAN.md) 5.1

Still out of scope: unrestricted computer control, arbitrary shell execution, hidden monitoring, cloud sync, bundled local LLMs, plugin marketplace.

---

## Definition of Done (every task)

- [ ] Builds in Release with no warnings
- [ ] Unit tests added/updated for Core logic; all green
- [ ] No new idle timers/loops; event subscriptions disposed
- [ ] Keyboard-accessible and labeled for screen readers
- [ ] No sensitive content in logs
- [ ] Committed with a clear message; `docs/` updated if behavior changed

## Risk Watchlist (review at end of each phase)

| Risk | Phase | Check |
|---|---|---|
| Transparent window CPU/RAM higher than expected | 1 | Perf baseline vs target |
| Unpackaged toast scheduling quirks | 2 | Test early in dev; move to MSIX sooner if painful |
| Reminder missed after reboot | 2, 4 | Reboot test in matrix |
| AI wrong date/time | 3 | Clarification path + tests |
| Scope creep before polish | all | Phase 5 items stay in backlog until v1 ships |
