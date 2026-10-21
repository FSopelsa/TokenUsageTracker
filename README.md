# Codex Usage Widget for Windows

A small desktop widget that shows the remaining usage in your Codex plan, including reset times for the current and weekly windows.

<img src="docs/preview.png" alt="Preview of the Codex Usage Widget" width="316" />

The percentages shown in this preview are illustrative. The widget reads usage recorded by Codex on the device where it runs.

## Features

- Draggable window with an optional always-on-top setting.
- Remaining usage and reset countdowns for both reported plan windows.
- Notification-area icon, remembered position, and refresh every 15 seconds.
- No API key or additional sign-in required.

## Installation

### Download a build

The GitHub Actions workflow builds a portable `win-x64` executable on each push, pull request, or manual workflow run. After a successful run, download `CodexUsageWidget-win-x64` from the workflow's **Artifacts** section and extract it. Run `CodexUsageWidget.exe` on Windows 10 or 11 x64. An installed .NET runtime is not required.

Actions artifacts are build outputs; they are not GitHub Releases.

### Build from source

Clone the repository and run `Launch.cmd`. On first launch, the script runs the usage checks and builds the widget. Building requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and an internet connection to download Microsoft's runtime packs. The resulting portable executable is written to `dist/CodexUsageWidget.exe`.

After pulling source changes, quit the running widget, run `Build.cmd`, then run `Launch.cmd`.

The build script also supports Windows ARM64:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1 -Runtime win-arm64
```

## Using the widget

- Drag the header to move the widget. Its position is remembered on that device.
- Use the crosshair button to toggle **Always on top**.
- Select **⋯** or right-click the widget to refresh, open the Codex usage dashboard, choose a data folder, hide to the notification area, or quit.
- Press **F5** to reread usage or **Esc** to hide. Double-click the notification-area icon to show the widget again.

## Usage data and privacy

By default, the widget reads Codex session records from `%USERPROFILE%\.codex\sessions`. It honors `CODEX_HOME`; **Choose Codex data folder** can select another Codex data directory. Settings stay in `%LOCALAPPDATA%\CodexUsageWidget\settings.json` on the local device.

The widget reads local files without changing them. It does not read `auth.json` or make network requests. The dashboard opens in a browser only when selected.

Usage values come from the latest plan-usage snapshot recorded in local Codex session files. The widget checks for changes every 15 seconds; it does not query the account live. Usage from other devices or cloud sessions appears after Codex records a newer snapshot locally. Older readings are marked **Last seen**. After a window's reset time passes, the widget waits for a new snapshot instead of assuming the allowance has reset.

The [Codex usage dashboard](https://chatgpt.com/codex/settings/usage) shows current account usage and limits. The data format is an internal Codex detail and may change; see the upstream [token-count protocol](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs).

## Development

Run the local usage checks:

```powershell
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release
```

Build and publish the portable executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1
```

The widget shows remaining plan usage; it does not estimate API dollar costs or display token totals. This independent project is not affiliated with OpenAI.
