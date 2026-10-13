<p align="center">
  <img src="docs/screenshots/logo.png" width="96" alt="Mithu logo" />
</p>

<h1 align="center">Mithu</h1>

<p align="center">
  A lightweight desktop companion for Windows — notes, reminders, focus and AI, one click away at the top of your screen.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.0.0-2df3f3?style=flat-square" alt="Version 1.0.0" />
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0d1117?style=flat-square" alt="Windows 10 and 11" />
  <img src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square" alt=".NET 10" />
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="MIT license" />
</p>

<p align="center">
  <img src="docs/screenshots/home.png" width="560" alt="Mithu home panel" />
</p>

## Overview

Mithu sits in a small notch at the top of the screen. Click it or press <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Space</kbd> to open the panel; click elsewhere and it tucks away again. It is a native C# / WPF application — no Electron or embedded browser — using around 150 MB of memory and close to zero CPU when idle.

All data is stored locally. AI features are optional and use your own API key.

## Features

| Area | What it does |
|---|---|
| **Notes** | Quick notes with `#tags`, a live formatting editor (headings, lists, checklists), pinning, archive and search. |
| **Reminders** | Natural-language input ("tomorrow at 6 pm", "every weekday at 9:30"), repeats, priorities, snooze and Windows notifications. |
| **To-do** | Simple task lists without dates, with stars and undo. |
| **Focus timer** | Planned rounds and breaks, shown in the header and on the notch. |
| **AI assistant** | Claude, OpenAI, Gemini, OpenRouter, Groq, or local models via Ollama and LM Studio. Creates notes, reminders and tasks for you. |
| **Screen help** | Snip any area with <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd> to ask about it, copy its text or search it in Google Lens. Requires a vision-capable model. |
| **Desktop control** | Opens apps, arranges windows and finds or organises files in folders you allow. File moves, renames and closing windows always ask first. |
| **Claude Code status** | Live progress of Claude Code sessions — current step, elapsed time and tokens — with a notification when it finishes or needs input. |
| **Personalisation** | Rename your companion, choose the panel size, border lines, docking and motion settings. A first-run setup covers the essentials. |

## Screenshots

<table>
  <tr>
    <td align="center"><img src="docs/screenshots/chat.png" width="280" alt="AI chat" /><br /><sub>AI chat</sub></td>
    <td align="center"><img src="docs/screenshots/reminders.png" width="280" alt="Reminders" /><br /><sub>Reminders</sub></td>
    <td align="center"><img src="docs/screenshots/notes.png" width="280" alt="Notes" /><br /><sub>Notes</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/screenshots/todo.png" width="280" alt="To-do" /><br /><sub>To-do</sub></td>
    <td align="center"><img src="docs/screenshots/focus.png" width="280" alt="Focus timer" /><br /><sub>Focus timer</sub></td>
    <td align="center"><img src="docs/screenshots/claude-code.png" width="280" alt="Claude Code status" /><br /><sub>Claude Code status</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="docs/screenshots/setup.png" width="280" alt="First-run setup" /><br /><sub>First-run setup</sub></td>
    <td align="center"><img src="docs/screenshots/menu.png" width="280" alt="Settings menu" /><br /><sub>Settings menu</sub></td>
    <td align="center"><img src="docs/screenshots/compact.png" width="280" alt="Compact view" /><br /><img src="docs/screenshots/notch.png" width="180" alt="Notch" /><br /><sub>Compact view and notch</sub></td>
  </tr>
</table>

## Installation

1. Download `MithuCompanion-win-Setup.exe` from the [latest release](../../releases/latest).
2. Run the installer. No administrator rights are required and the .NET runtime is included.
3. Follow the first-run setup.

> [!NOTE]
> The installer is not code-signed yet. If Windows SmartScreen appears, select **More info → Run anyway**.

To uninstall, use **Settings → Apps → Mithu**. Personal data in `%LOCALAPPDATA%\Mithu` is kept; delete that folder to remove it.

## Building from source

**Requirements:** Windows 10 (2004+) or Windows 11, [.NET 10 SDK](https://dotnet.microsoft.com/download)

```powershell
dotnet run --project src/Mithu.App    # run the app
dotnet test Mithu.slnx                # run the tests
```

To build the installer, install the Velopack CLI once (`dotnet tool install -g vpk`) and run:

```powershell
powershell -File scripts\package.ps1
```

## Project structure

```
src/
  Mithu.App              WPF application: windows, face animation, pages, view models
  Mithu.Core             Platform-independent logic: reminders, focus, AI messages, rules
  Mithu.Infrastructure   Windows integration, SQLite storage, notifications, AI clients
tests/
  Mithu.Tests            xUnit tests
scripts/                 Packaging and icon scripts
```

## Privacy

- Notes, reminders, tasks and chats are stored only on your computer.
- AI is disabled until you add a key and confirm what will be sent. Keys are encrypted with Windows DPAPI.
- Note access, screenshots and clipboard reading are individual permissions, and every screenshot is shown in the chat.
- Logs contain diagnostics only, never your content.

Details: [privacy.md](privacy.md)

## License

Released under the [MIT License](LICENSE). © 2026 Srinath.
