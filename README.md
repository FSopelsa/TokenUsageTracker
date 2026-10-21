# Codex Usage Widget for Windows

A small, draggable Windows widget showing **remaining Codex plan usage** and reset countdowns. Native WPF / .NET 10; no browser runtime, API key, or additional sign-in.

<img src="docs/preview.png" alt="Widget preview with illustrative usage values" width="316" />

Preview values are illustrative; the running widget reads your local Codex records.

## Run on this PC

Double-click **Launch.cmd**. On the first launch it runs checks and builds the portable executable. Building requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and an internet connection to download Microsoft's runtime packs.

Once built, `dist/CodexUsageWidget.exe` runs by itself on Windows 10/11 x64 without an installed .NET runtime. You can copy that file to another PC or create a normal Windows shortcut to it. Windows may show SmartScreen for this unsigned app.

## Use on your PC with GitHub

1. On the target PC, install Git and the .NET 10 SDK, clone this repository, then double-click **Launch.cmd**.
2. For updates: quit the widget from its menu, run `git pull`, then double-click **Build.cmd** followed by **Launch.cmd**.

The included GitHub Actions workflow also builds and uploads `CodexUsageWidget-win-x64` on every push. Download and extract that artifact from the repository's **Actions** tab to run the executable without installing the SDK. The workflow must have run successfully first; this is an Actions artifact, not a GitHub Release.

Each PC reads its own Codex installation. Sign into the same Codex account there and send a message to get a recent plan reading. Git does **not** transfer your login, session history, or preferences.

## Controls

- Drag the header to move the widget. Its position is remembered on that PC.
- The crosshair toggles **always on top** (on by default).
- **⋯** or right-click opens the menu: refresh, usage dashboard, data folder, hide, quit.
- **F5** rereads local files; **Esc** hides to the notification tray. Double-click the tray icon to show it again. Windows may put that icon in its tray overflow.
- Quit from the widget menu or tray menu before rebuilding. Only one instance runs per Windows session.

## Where the numbers come from

The widget reads `event_msg` / `token_count` / `rate_limits` records in `%USERPROFILE%\.codex\sessions\**\*.jsonl`. It honors `CODEX_HOME`, and **Choose Codex data folder** lets you select another `.codex` directory. The app reads these files without changing them, does not read `auth.json`, and makes no network requests. **Open usage dashboard** opens your browser only when you select it.

Remaining usage is `100 − used_percent`. The primary and secondary windows use Codex's reported durations and reset times; the UI does not hardcode plan capacities. A window that is not reported displays a dash. Other model-specific limit IDs are excluded from the general Codex allowance.

It checks local files every 15 seconds, caches unchanged files, and scans from the end of changed logs. **The values are the latest local snapshot, not a live account query.** Activity on another PC or in the cloud appears after this PC receives a newer Codex snapshot. Refresh does not contact OpenAI or force Codex to refresh its own data. Readings older than 15 minutes are marked **Last seen**; expired windows show **waiting for Codex**, rather than inventing a restored balance. If you switch Codex accounts, send a message before relying on the widget.

The displayed age is the event's local recording time. Codex can re-emit a snapshot it already holds, so a recent event does not guarantee a new server measurement. The [official usage dashboard](https://chatgpt.com/codex/settings/usage) is the place to verify your account's current limits. OpenAI also documents the dashboard and CLI `/status` in its [usage and pricing guide](https://learn.chatgpt.com/docs/pricing#where-can-i-see-my-current-usage-limits).

Settings are saved outside the repository in `%LOCALAPPDATA%\CodexUsageWidget\settings.json`. Delete that file while the widget is closed to reset its position and data folder.

This version focuses on **remaining plan allowance**, as requested. It does not turn subscription usage into a dollar bill or add token totals. Log formats are an internal Codex detail and may change; see the upstream [token-count protocol](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs). This is an independent utility, not an OpenAI product.

## Build and check

```powershell
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release
# Also check this PC's actual local logs (prints percentages, not chat content):
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release -- --live
# Checks + self-contained single-file build:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1
# Native Windows ARM64 executable:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1 -Runtime win-arm64
```

Source: `src/TokenUsageTracker`. Checks use only the .NET standard library and synthetic in-memory/temp-directory records. The optional live check is read-only. There are no third-party runtime or test packages.
