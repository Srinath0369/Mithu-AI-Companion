# Mithu (मिठू) — Your Little Desktop Dost

> **Product and technical blueprint**  
> Platform: Windows 10/11 | Application: Native C# + WPF | Working status: Design specification and phased implementation plan  
> Working name: **Mithu** (subject to trademark, domain, and app-store name checks before release)

---

## 1. Product vision

**Mithu** is a lightweight, friendly, animated AI companion that lives near the top edge of the Windows desktop. It is always easy to reach but never demands attention. A small cyan-eyed face expands into a top bar or a compact productivity panel for notes, reminders, focus timers, questions, and later voice commands and approved desktop actions.

**Positioning:** A cute desktop *dost* (friend) that helps users remember, capture ideas, and focus, without being another large chat window.

**North star:** Users can complete common tasks in one short interaction, while Mithu stays quiet and uses minimal resources when idle.

### Principles

1. **Lightweight by default:** No Electron, Chromium, or WebView2 in the main application. Native WPF vector rendering.
2. **Useful without AI:** Notes, timers, and reminders continue to work offline.
3. **Adorable but not distracting:** Expression and animation enhance feedback, not productivity interruptions.
4. **Privacy and control:** Clear consent for microphone, screen or file access; easy data deletion and a pause switch.
5. **Safe automation:** The model may request approved actions but never execute arbitrary code or OS commands.
6. **Fast interactions:** Quick capture, keyboard-first controls, responsive panel transitions.
7. **Accessible:** High contrast, reduced motion, keyboard navigation, screen-reader labels, and DPI scaling.

## 2. Name and character identity

**Recommended working name:** Mithu (मिठू). Friendly, short, memorable, suitable for speech and UI labels. Hindi-inspired affectionate nickname; do not assume exclusive commercial rights.

**Tagline:** *Your Little Desktop Dost.*

Other naming options:

| Name | Character feel | Possible tagline |
|---|---|---|
| **Mithu** | Sweet, affectionate, friendly | Your Little Desktop Dost |
| **Chiku** | Curious, playful, energetic | A Tiny Helper, Always Nearby |
| **Golu** | Round, warm, cuddly | Little Face, Big Help |
| **Piku** | Tiny, bright, cheerful | Your Pocket-Sized Desktop Pal |
| **Jugnu** | Glowing, magical, gentle | A Little Spark on Your Desktop |
| **Laddoo** | Adorable and comforting | Sweet Help, All Day |

**Character visual:** Dark charcoal face plate, softly rounded capsule silhouette, cyan glowing oval eyes, curved smile, optional light outer rim. The initial face should be drawn with WPF vector shapes (`Ellipse`, `Path`, `Border`), rather than a video, web canvas, or continuously playing GIF.

**Brand colors (starting palette):**

| Token | Hex | Use |
|---|---|---|
| Midnight | `#0A0E16` | Face and dark surfaces |
| Panel | `#1B2330` | Main bar |
| Elevated | `#273244` | Cards and hover backgrounds |
| Cyan | `#2DF3F3` | Eyes, smile, active state |
| Text | `#F4F8FC` | Primary text |
| Muted | `#A7B5C7` | Secondary text |
| Success | `#68E7B1` | Confirmed action |
| Warning | `#F6C76E` | Noncritical attention |

The original reference is visual inspiration only. Produce independent character artwork and branding for release.

## 3. Core UI / UX

### 3.1 Window states

| State | Approximate size (DIP) | What the user sees | Trigger |
|---|---|---|---|
| **Mini face** | 80–96 × 65–85 | Just Mithu's animated face | Manual collapse, quiet mode |
| **Top bar** | 300–440 × 76–100 | Face, status, quick actions | Click face or hotkey |
| **Expanded panel** | 380–460 × 300–560 | Chat input, notes, reminders, timer | Expand button / task interaction |
| **Notification peek** | 300–420 × 72–110 | One timely message and action | Reminder or timer completion |
| **Settings** | Independent small window | Behavior, appearance, privacy | Tray or settings button |

Sizes are **design targets**, not fixed pixels. Respect Windows DPI scaling and available monitor work areas. Keep the default dock at top center; allow dragging, repositioning, and remembering per-monitor placement.

### 3.2 Layout sketches

**Mini**

```text
         ╭──────────╮
         │  ●   ●   │
         │    ᴗ     │
         ╰──────────╯
```

**Bar**

```text
╭───────────────────────────────────────────╮
│ [● ᴗ ●]  Hi! Ready when you are  🎙  ⌄ │
╰───────────────────────────────────────────╯
```

**Expanded panel**

```text
╭───────────────────────────────────────────╮
│ [Mithu]  Good morning!               ⚙  ×│
│───────────────────────────────────────────│
│ Ask Mithu, or write a quick note...    ↵  │
│                                           │
│  🔔 Reminders  📝 Notes  ⏱ Focus       │
│                                           │
│  Coming up: Project review · 5:00 PM      │
│  Focus timer: 24:59             Pause     │
╰───────────────────────────────────────────╯
```

### 3.3 Interaction rules

- **Left-click face:** Expand/collapse between mini and bar.
- **Expand chevron:** Open/close the full panel.
- **Drag handle or noninteractive header:** Move the window without interfering with buttons.
- **Global shortcut:** Configurable, suggested `Ctrl+Shift+Space` if not in use.
- **Esc:** Close the current panel; never silently discard unsaved user text.
- **Tray icon:** Show/hide, pause notifications, launch settings, exit.
- **Keyboard-first input:** Enter submits; Shift+Enter creates a new line.
- **Fullscreen-aware:** Suspend overlays or enter quiet mode for games/presentations, with a user override.
- **No surprise focus stealing:** Reminders use Windows notifications; only raise a panel in response to a user action.
- **Multiple monitors:** Store a monitor-safe position; recover gracefully when a display is disconnected.

### 3.4 Accessibility

- Keyboard access and visible focus indicators.
- Accessible names for nontext icons and the animated face.
- Prefer native controls; expose notifications as readable text, not animation alone.
- Follow Windows text scaling and high contrast where feasible.
- Honor the OS reduced-motion setting and an app-level animation toggle.
- Never rely on facial expression alone to convey success, errors, or missed reminders.

## 4. Face animation specification

Build a simple, deterministic **expression state machine** with temporary overlays. WPF storyboards animate existing transforms and paths; avoid continually redrawing a full scene.

| State | Visual | Trigger | Typical motion |
|---|---|---|---|
| Idle | Bright eyes, small smile | No active interaction | Randomized blink every ~3–7 s |
| Hover | Slightly brighter eyes | Pointer enters face | 150–200 ms easing |
| Listening | Open/attentive eyes | Microphone on, later phase | Subtle pulse; show mic indicator |
| Thinking | Eye glance / short blink | AI operation pending | Low-motion loop, paused when hidden |
| Happy | Curved eyes, larger smile | Successful action | 250–400 ms smile |
| Celebrating | Cheerful eyes, small bounce | Focus finished | One brief 400–700 ms sequence |
| Concerned | Mildly curved brows | Error or ambiguous request | 250–400 ms, also show text |
| Sleepy | Half-closed eyes | Opt-in idle personality | Rare expression; do not imply monitoring |

**Technical implementation:** Define `FaceMood` enum and a `CompanionFaceControl` UserControl; store eye shapes and mouth geometry independently. Use `ScaleTransform` for blink (Y axis), transform groups for glance/bounce, and `DoubleAnimation`/`Storyboard` with `CubicEase` where appropriate. Cancel and replace animation when a more important event arrives.

**Priority:** Error/confirmation > actively listening > thinking > hover > idle. **Reduced motion:** Replace movement with instantaneous or subtle color/shape changes. **Efficiency:** No continuous frame loop while idle; schedule infrequent blink events, pause decorative animation when hidden/minimized, unsubscribe events when closed.

## 5. Features and scope

### 5.1 Phase 1 — Native shell and character

- [ ] Boot a borderless, transparent WPF window without a browser component.
- [ ] Draw original face in WPF vectors; idle blink and hover reactions.
- [ ] Expand/collapse bar with smooth transitions.
- [ ] Drag, dock, reposition, and persist position across launches.
- [ ] Add tray icon, hide/show, exit, optional launch at login.
- [ ] Add global shortcut with collision detection.
- [ ] Support light/dark accent options and reduced motion.
- [ ] Profile idle RAM/CPU and ensure background animation is quiet.

### 5.2 Phase 2 — Offline productivity MVP

**Notes**
- Instant quick capture; create/edit/delete/archive.
- Local full-text or indexed search; tags optional.
- Autosave with failure feedback; clear deletion behavior.

**Reminders**
- Natural-language entry *may initially use a basic form*: title, local due date/time, optional recurrence.
- Local durable storage; pending/due/completed/snoozed states.
- Windows notification or other app notification pathway, with a visible notification log.
- Correct time zone, DST, restarts, sleep/wake, and overdue handling.

**Timers**
- 25-minute focus default; custom duration, pause/resume/reset.
- Stopwatch and optional break timer later.
- Persist current session / deadline and resume sensibly after app relaunch.
- Countdown derived from an absolute deadline, **not** counting DispatcherTimer ticks.

**Settings**
- Animation and notification preferences, start-with-Windows, position, shortcut, export/delete data.

### 5.3 Phase 3 — AI assistance

- Local typed chat UI that calls a configurable AI service.
- Explain and answer questions, but avoid making fabricated claims of system access.
- Structured, permissioned tool calls: `create_note`, `search_notes`, `create_reminder`, `list_reminders`, `start_timer`, `pause_timer`, `get_timer_status`.
- Parse multi-intent requests; return per-action success or failure.
- Clarify ambiguous date/time references using the device time zone.
- Optional conversation memory, explicitly enabled and editable.
- Error states: offline, timeout, rate limit, partial success, invalid tool args.

### 5.4 Phase 4 — Optional advanced features

- Push-to-talk speech recognition; clear active microphone indicator.
- Text-to-speech voice output, disabled by default.
- Calendar integration with explicit account authorization.
- Approved app launching / user-defined quick actions.
- Optional read-only on-screen context capture, explicitly consented per session.
- Character skins, more expressions, user-customizable personality and tone.

**Out of scope initially:** Unrestricted computer control, arbitrary shell/PowerShell execution, hidden screen or microphone monitoring, cloud sync, large local language models, marketplace/plugins.

## 6. Technology stack (native and lightweight)

| Component | Recommendation | Reason |
|---|---|---|
| OS target | Windows 10 and Windows 11 | Desktop-focused |
| Language | C# | Good productivity, familiar Windows ecosystem |
| UI | **WPF** on supported modern .NET | Native graphics, XAML vector controls, no Chromium |
| Pattern | MVVM | Separates UI, data, and logic |
| UI animations | WPF transforms / Storyboards | Built-in rendering; no browser |
| Local database | SQLite (`Microsoft.Data.Sqlite`) | Lightweight, offline persistent storage |
| Logging | `Microsoft.Extensions.Logging`, optional file sink | Diagnostics with controlled retention |
| Timers/scheduling | App services with persisted UTC deadlines | Robust over restarts and sleep |
| Notifications | Supported Windows desktop notification integration | User-visible system reminders |
| AI transport | `HttpClient` to configured service / secure backend | Avoid local LLM RAM cost |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` | Organized, testable components |
| Secrets | Windows-backed secure credential storage or a backend proxy | Avoid plaintext API secrets |
| Packaging | MSIX or a suitable Windows installer | Install, updates, notifications integration |
| Tests | xUnit/NUnit + small Windows UI smoke tests | Verify core behavior |

**Important distinction:** WPF is a .NET-native Windows desktop UI technology, not a Chromium renderer. It does carry .NET and WPF memory overhead; no exact RAM figure is guaranteed.

### 6.1 Why not Electron, Tauri, or WebView2?

They can be valid for other products, but this project explicitly prioritizes a self-contained native Windows UI without an embedded browser renderer. Tauri typically uses Windows WebView2; WPF avoids that. A local or cloud AI client can still communicate over HTTP without embedding a browser.

### 6.2 Target metrics, not promises

| Metric | Aspirational target | How to check |
|---|---|---|
| Idle RAM | **Working set ≤ 150 MB** after warm-up (owner-approved budget, 2026-10-09); private bytes tracked separately | Task Manager + Process Explorer; measure private bytes and working set |
| Idle CPU | Near 0% average | Performance Monitor over 10+ minutes |
| Animation | Smooth on typical 60 Hz monitors | WPF profiling, frame observation |
| Startup | Under ~2 seconds on a representative SSD device | Repeated cold/warm launches |
| Background network | None until needed / enabled | Network trace |
| Offline features | Notes, reminders, timers functional | Disconnect network and test |

Measure Release builds under comparable Windows versions and machines. Profile managed allocations, event subscriptions, memory leaks, rendering invalidations, and app startup cost. Never trade reliable reminder delivery for an arbitrary RAM threshold.

## 7. High-level architecture

```text
User (clicks, keyboard, future voice)
                 │
                 ▼
┌──────────────────────────────────────────────┐
│  WPF Presentation                            │
│  FloatingWindow · FaceControl · Panels       │
│  ViewModels · AnimationCoordinator           │
└─────────────────────┬────────────────────────┘
                      │ commands/events
                      ▼
┌──────────────────────────────────────────────┐
│  Application Services                        │
│  Notes · Reminders · Timers · Settings        │
│  Notifications · Tool Dispatcher · AI Client │
└───────────────┬──────────────────┬───────────┘
                │                  │
                ▼                  ▼
       SQLite / local files  AI provider/backend
                │                  │
                └──── result ──────┘
                       │
                       ▼
       UI confirmation + face expression
```

**Separation rule:** The LLM chooses from explicit operations, while trusted application code validates and performs them. The AI never directly touches SQLite, arbitrary files, process launches, or OS APIs.

### 7.1 Suggested solution layout

```text
Mithu.sln
src/
  Mithu.App/                     # WPF application and entry point
    App.xaml
    Views/
      FloatingWindow.xaml
      ExpandedPanel.xaml
      SettingsWindow.xaml
    Controls/
      CompanionFaceControl.xaml
    ViewModels/
      MainViewModel.cs
      NotesViewModel.cs
      TimerViewModel.cs
    Themes/
      Colors.xaml
      Styles.xaml
    Animation/
      FaceMood.cs
      FaceAnimationCoordinator.cs
  Mithu.Core/                    # UI-independent domain logic
    Models/
      Note.cs
      Reminder.cs
      TimerSession.cs
    Interfaces/
      INoteRepository.cs
      IReminderRepository.cs
      IAiClient.cs
    Services/
      TimerService.cs
      ReminderScheduler.cs
      AssistantToolDispatcher.cs
  Mithu.Infrastructure/          # OS and persistence integration
    Persistence/
      SqliteConnectionFactory.cs
      NoteRepository.cs
      ReminderRepository.cs
    Notifications/
    Windows/
      ShortcutService.cs
      TrayService.cs
      WindowPositionService.cs
    AI/
      HttpAiClient.cs
      ToolSchema.cs
  Mithu.Tests/
    TimerServiceTests.cs
    ReminderSchedulerTests.cs
    ToolDispatcherTests.cs
assets/
  icons/
docs/
  architecture.md
  privacy.md
```

Start with one WPF project if that is fastest. Split into projects when testability and complexity justify it; don't add unnecessary layers before building the first functioning bar.

## 8. Data model

All entries have stable IDs and creation/update timestamps. Store UTC instants for deadlines and preserve the intended time zone for recurring reminders.

### `notes`

| Column | Type | Meaning |
|---|---|---|
| `id` | TEXT primary key | UUID |
| `title` | TEXT | Optional title |
| `content` | TEXT | Plain text or clearly defined format |
| `created_at_utc` | TEXT | ISO 8601 UTC |
| `updated_at_utc` | TEXT | ISO 8601 UTC |
| `is_archived` | INTEGER | 0/1 |

### `reminders`

| Column | Type | Meaning |
|---|---|---|
| `id` | TEXT primary key | UUID |
| `title` | TEXT | Short display text |
| `due_at_utc` | TEXT | UTC due instant |
| `time_zone_id` | TEXT | Time-zone identity for recurrence semantics |
| `recurrence_rule` | TEXT nullable | Optional validated recurrence representation |
| `status` | TEXT | pending/done/snoozed/cancelled |
| `last_notified_at_utc` | TEXT nullable | Deduplication / audit |
| `created_at_utc` | TEXT | UTC creation instant |

### `timer_sessions`

| Column | Type | Meaning |
|---|---|---|
| `id` | TEXT primary key | Session UUID |
| `duration_seconds` | INTEGER | Chosen duration |
| `remaining_seconds` | INTEGER | Last paused remainder |
| `deadline_utc` | TEXT nullable | Deadline when running |
| `state` | TEXT | idle/running/paused/completed |

### `settings`

Key/value representation is adequate for the initial release. Include `notifications_enabled`, `animations_enabled`, `launch_on_startup`, `dock_position`, `global_hotkey`, `quiet_mode`, `ai_enabled`, `microphone_enabled`, `reduced_motion`, and `privacy_preferences`.

**Migration policy:** Version the schema and test upgrade paths, backups, and corrupted-database behavior.

## 9. Timer and reminder correctness

**Timer algorithm:** On start, save `deadlineUtc = UtcNow + remaining`. On each UI refresh, compute `remaining = max(0, deadlineUtc - UtcNow)`. On pause, save remaining time and clear the deadline. On resume, create a new deadline. UI tick frequency does not determine elapsed time.

**Reminder engine:** Persist reminders, then use one scheduler that always calculates the next due item. On app startup and system resume, recheck any overdue reminders and decide whether to show, aggregate, or mark them delivered according to user preferences. Avoid duplicates using stable reminder IDs and notification state.

**Important limitation:** An ordinary desktop process does not guarantee notifications while it is not running. If reminders must work after app exit or reboot, design and test an appropriate Windows scheduling/background activation strategy; do not claim reliability until verified on the selected installation model.

**Date examples to test:** DST transitions, time-zone changes, ambiguous “tomorrow at 8,” local midnight, suspended laptop, reboot, overdue reminders, and recurring reminders across months.

## 10. AI behavior and tool execution

### 10.1 Assistant persona

Mithu is concise, cheerful, warm, and optional. It does **not** overuse emojis, interrupt the user, feign human feelings, or imply capabilities it lacks. Confirmation example: “Saved your note and set a reminder for 7 PM.”

### 10.2 Allowed tool schema (illustrative)

```json
{
  "name": "create_reminder",
  "arguments": {
    "title": "Call Amma",
    "local_datetime": "2026-10-09T19:00:00",
    "time_zone_id": "Asia/Kolkata"
  }
}
```

The example is a proposed structured tool-call format, not an API implementation contract. Backend logic validates dates, handles ambiguous clock times, checks permissions, converts to UTC, saves to SQLite, and returns a typed result.

### 10.3 Multi-action flow

```text
“Remind me to study at 7 and save that I need milk.”
  → Identify requested reminder and note
  → Resolve the date and time or ask clarification
  → Validate both operations
  → Persist operations independently
  → Confirm each success or report partial failure
  → Animate a brief happy expression
```

### 10.4 Security boundary

- Tool allowlist; strongly typed inputs; validation on every operation.
- No arbitrary PowerShell, CMD, filesystem traversal, or process execution from freeform AI output.
- Require explicit approval for destructive actions, external messaging, payments, or sensitive data access.
- Avoid sending local notes or files to a model unless necessary and authorized.
- Mask secrets from logs, redact telemetry, limit request size, and rate-limit AI actions.
- For commercial distribution, don't ship a shared provider API key in the client executable. Use a backend with authentication and quotas, or securely managed user-supplied keys.

## 11. Privacy and offline guarantees

**Offline features:** Notes, timers, reminder creation, and local search should function without a network connection.

**User-facing controls:**

- AI toggle; clear explanation when data is sent to an external service.
- Microphone off by default; opt-in push-to-talk in a later release.
- No hidden screen capture or always-listening behavior.
- Local export and delete for notes, reminders, and settings.
- Optional memory that can be reviewed, corrected, and disabled.
- Logs disabled or minimized by default and stripped of sensitive note content.
- App exit must mean exit; explain how any optional background reminder component operates.

If cloud chat is enabled, provide a privacy notice for the selected provider and retention policy before release.

## 12. Performance engineering

**Rendering:** Use cached brushes/geometries where practical; prefer transforms and opacity over rebuilding a whole XAML tree. Animate only elements that are currently visible. Avoid excessive shadows, blur, acrylic-like effects, and per-frame polling.

**Windowing:** Use one persistent lightweight overlay and lazily create heavier panels. Unload unused views and cancel event subscriptions correctly. Prefer event-driven updates over repeated timers.

**Background work:** Use asynchronous I/O for database/network work. Batch changes sensibly. Keep UI-thread operations short and marshal only necessary updates onto the Dispatcher.

**AI:** Call a remote service asynchronously; do not bundle local model weights in the MVP. Stream optional text responses only when it improves perceived latency.

**Measure:** Record private bytes, working set, managed heap allocations, handles, CPU average, startup time, and UI responsiveness before and after each major feature.

## 13. Development milestones

### Milestone 0 — Validate existing starter (1–2 days)

- [ ] Build and run the supplied WPF starter on Windows.
- [ ] Confirm floating borderless draggable window and correct expand/collapse behavior.
- [ ] Confirm randomized blink and timer start/pause/reset.
- [ ] Record initial startup time, idle memory, and CPU.
- [ ] Fix any platform-specific rendering, display scale, or compile issues.

### Milestone 1 — Polished native shell (about 1 week)

- [ ] Rename project and namespace to Mithu.
- [ ] Replace prototype face with reusable `CompanionFaceControl`.
- [ ] Implement mini/bar/expanded state machine and animations.
- [ ] Add tray and hotkey; improve drag interaction.
- [ ] Save preferences and window location.
- [ ] Implement reduced motion and quiet mode.

### Milestone 2 — Offline productivity (about 1–2 weeks)

- [ ] SQLite setup/migrations and notes CRUD/search.
- [ ] Reliable timer service with saved state.
- [ ] Reminder CRUD, snooze, notifications and wake/restart checks.
- [ ] Settings, offline error handling, tests and diagnostics.

### Milestone 3 — AI tool calling (about 1–2 weeks)

- [ ] Typed chat input and conversation responses.
- [ ] AI service configuration and secure key approach.
- [ ] Allowlisted note, reminder, and timer tools.
- [ ] Structured validation, confirmation, partial failure reporting.
- [ ] Privacy controls and network error handling.

### Milestone 4 — Release hardening (time based on test results)

- [ ] Multi-monitor, DPI, sleep/resume and accessibility testing.
- [ ] Windows installer, launch-at-login preferences, uninstall and data handling.
- [ ] Crash reporting policy, logging and final resource profiling.
- [ ] Test with several Windows users on different hardware.
- [ ] Verify name availability, artwork rights, and documentation.

Schedules are rough estimates, not delivery commitments.

## 14. Acceptance criteria for first usable release

1. User can summon and dismiss the companion via tray, click, and shortcut.
2. Face animates smoothly without a persistent high-frequency idle loop.
3. User can create and find notes offline after a restart.
4. User can create a reminder, receive a notification, and snooze it under supported Windows lifecycle conditions.
5. Timer accurately tracks real elapsed time through pauses and normal app runtime.
6. Chat cannot execute unapproved operations.
7. User can disable AI, animation, sounds, and notifications separately.
8. Position survives restarts and monitor changes safely.
9. UI remains accessible with Windows scaling and keyboard navigation.
10. Performance results are documented from a release build and checked for regressions.

## 15. Development environment / quick start

**Prerequisites:** Windows 10/11; Visual Studio 2022 with `.NET desktop development`, or an appropriate supported .NET SDK and editor. The *existing prototype* targets **`net8.0-windows`** with `UseWPF=true`.

To run the existing starter project:

```powershell
cd path\to\MochiWpfStarter
dotnet restore
dotnet run
```

To create a fresh WPF app (example):

```powershell
dotnet new wpf -n Mithu.App -f net8.0
dotnet run --project Mithu.App
```

For a new production app, select a currently supported .NET version and test the chosen runtime/install strategy. The above commands align with the existing starter's .NET 8 target; they are not a promise of future support duration.

**Existing prototype scope:** Borderless transparent draggable topmost window; vector face with blinking; expandable panel; 25-minute focus timer with pause/resume/reset. **Not implemented in starter:** Notes, SQLite, reminders, tray, shortcut, AI integration, installer, multi-monitor support. The starter was not compiled in the original generation environment; verify on Windows.

## 16. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Borderless transparency consumes more than expected | Profile, minimize effects and invalidation, consider simpler borders |
| Notification missed while app closed/asleep | Explicit scheduling strategy, lifecycle testing, overdue recovery |
| AI creates wrong event date | User-local timezone, validation, clarification for ambiguity |
| Excessive idle animations | Sparse scheduled blinks, hidden-state pause, reduced motion |
| Unsafe AI actions | Allowlist, typed validation, explicit permission/confirmation |
| Tool/API cost | Quotas, opt-in AI, sensible request sizes, clear error states |
| Multi-monitor off-screen window | Clamp restored position to connected display work area |
| WPF UI growing too complex | MVVM separation, reusable controls, profiling and UI tests |

## 17. First build task for a coding assistant

> **Implement Mithu Phase 1 on Windows in native C# WPF.** Begin from the existing `MochiWpfStarter` project (targeting .NET 8 WPF), verify it builds, and keep the app Chromium-free. Refactor the face into a reusable vector `CompanionFaceControl`; define `FaceMood` and an animation coordinator with randomized idle blinking, hover, happy and thinking expressions; implement mini, bar, and expanded layouts with smooth, interruption-safe size transitions. Add a system tray icon, Show/Hide/Quit actions, window position persistence, optional topmost, and reduced-motion support. Do not yet add a database, external AI APIs, browser controls, or microphone features. Include exact changed files, build instructions, and manual test steps. Prioritize low idle resource use and maintainable code.

---

## 18. Project decisions to confirm later

- Name: **Mithu** (working recommendation; verify availability).
- Platform: **Windows desktop only** for v1.
- Stack: **C# + WPF + .NET**, no Electron/Chromium/WebView2.
- Design: Cyan-eyed character with compact top bar.
- Persistence: **SQLite** for offline productivity data.
- AI: Optional remote model through a constrained tool layer; provider selected later.
- Default personality: Friendly and concise, with user-configurable quiet mode.
- Release model: Local-first, with privacy and accessible controls.

**One-sentence product pitch:** *Mithu is a tiny, cheerful Windows desktop dost that helps you remember, focus, and get things done—without getting in your way.*
