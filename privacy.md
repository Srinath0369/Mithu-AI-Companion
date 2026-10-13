# Privacy

Mithu is a local-first app. It has no accounts, no analytics and no telemetry. Your data stays on your computer
unless you turn on a feature that needs the internet, and each of those is described below.

_Last updated: 13 October 2026 · applies to Mithu 1.0.0_

## What is stored on your computer

Everything lives in `%LOCALAPPDATA%\Mithu`:

| Data | Where | Notes |
|---|---|---|
| Notes, reminders, to-dos, chats, settings | `mithu.db` (SQLite) | Kept until you delete them. Backups named `mithu.db.bak-vN` are made before database upgrades. |
| Screenshots used in chats | `images\` | Deleted together with their chat. |
| AI keys | `secrets.dat` | Encrypted with Windows DPAPI, so only your Windows account on this PC can read them. |
| Desktop actions (moves, copies, renames) | `mithu.db` | A local record of what was changed, used for **Undo**. |
| Logs | `logs\` | Diagnostics only (no notes, reminders, messages, file names or keys). Kept for 7 days. |

Uninstalling Mithu keeps this folder so you can reinstall without losing anything. Delete the folder to remove all
data.

## When Mithu uses the internet

With the default settings, Mithu makes **no network requests at all**.

### AI assistant (off by default)

AI turns on only after you add a provider, a model and (for cloud providers) your own API key, and confirm a consent
screen. While it is on, each message you send goes **directly from your PC to the provider you chose** (for example
Anthropic, OpenAI, Google, OpenRouter or Groq). Mithu has no server in between. The request contains:

- your message and the recent conversation (up to about 40 messages);
- the current date, time and time zone, so phrases like "tomorrow at 6" work;
- the name you gave your companion;
- results of actions the AI took for you (for example "reminder saved", a list of your tasks, or file names it found);
- **only if you allowed it:** short matching snippets from your notes, a screenshot you attached or asked it to take,
  and text you copied when you ask about it.

What the provider does with this data is governed by **their** privacy policy and your account with them. With a
local model (Ollama, LM Studio) nothing leaves your computer.

Turn AI off in **⋯ → AI settings…**; from then on nothing is sent. **Remove** deletes the saved key from this PC.

### Google Lens (only when you click it)

The **Google Lens** button copies the screenshot to your clipboard and opens Google Lens in your browser. Mithu does
not upload the image itself; it is shared only if you paste it there.

### Updates

Version 1.0.0 does not check for updates. Future builds published with an update address check it daily (you can
turn this off in **⋯ → Updates**). A check downloads the list of releases and, if a newer one exists, the update
package. Nothing about you, your data or your use is sent.

## Permissions

Each of these is a separate switch, shown during the first-run setup and changeable at any time:

| Permission | What it allows |
|---|---|
| Let AI read my notes | The AI can search your notes; only short matching snippets are sent. |
| Look at my screen when I ask | The AI can take a screenshot when your message asks for it. Every screenshot is shown in the chat. Mithu leaves its own window out. |
| Screenshot shortcut (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>) | You pick an area to ask about. Nothing is sent until you send the message. |
| Read what I copy | The AI can read your clipboard, only when you ask about what you copied. |
| Use apps & files on this PC | The AI can open apps, arrange windows, and find, open and organise files **only inside the folders you allow**. Moving, copying, renaming files and closing windows always ask for your approval. Mithu never deletes files, runs programs or scripts, or changes system settings. |
| Show what Claude Code is doing | See below. |
| Start with Windows | Adds Mithu to your sign-in programs. |

## Claude Code status

When enabled, Mithu adds small hooks to Claude Code's settings file (`~/.claude/settings.json`; a backup is kept and
your own hooks are untouched). Claude Code then sends status events to Mithu **on your own computer only**
(`127.0.0.1`, protected by a random secret). Mithu shows the current step, time and token count, reading only the
token numbers from Claude Code's local transcript. Nothing from Claude Code is saved, logged or sent anywhere.
Turning the feature off, or uninstalling Mithu, removes the hooks.

## Notifications

Reminders use Windows notifications. Windows may keep them in the notification center until you dismiss them.
Uninstalling removes Mithu's scheduled notifications.

## Your control

- Delete any note, reminder, task or chat inside the app (deleting a chat also deletes its screenshots).
- Turn AI and each permission off at any time.
- Remove everything by deleting `%LOCALAPPDATA%\Mithu` after uninstalling.

## Questions

Mithu is open source under the MIT License; you can check exactly what it does in the source code. For questions,
open an issue on the project's GitHub page.
