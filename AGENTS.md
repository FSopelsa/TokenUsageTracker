# Agent notes: TokenUsageTracker

A Windows WPF widget (`src/TokenUsageTracker`, `net10.0-windows`) that shows remaining plan usage for Codex or Claude Code. `README.md` describes the user-facing behaviour.

## Build and test

- Full Windows build: `Build.cmd`. It runs `scripts/Build.ps1`: usage checks, window checks, then a portable publish to `dist/`. Quit a running widget first, or the build stops.
- Usage checks (no GUI, runs on any .NET 10 SDK): `dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release`. They use temp fixtures and a fake Codex process. The `--live`, `--live-rpc` and `--live-history` flags read the real local Codex data, so run them only when the user asks.
- Window checks (Windows only, WPF and WinForms): `dotnet run --project tests/WindowChecks/WindowChecks.csproj --configuration Release`. Screenshots go to `.build/`.
- Compile check without a Windows machine: `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`. This type-checks the WPF code but does not run it.

## Where things live

- Codex: `CodexClient.cs` (app-server JSON-RPC: `account/read`, `account/rateLimits/read`, and the Desktop proxy socket), `UsageReader.cs` (session logs), `ResumeController.cs` (auto-resume), `TaskHistory.cs` (local task ranking).
- Claude Code: `ClaudeUsage.cs` is the status-line bridge. It writes `usage-widget/usage.json` under `CLAUDE_CONFIG_DIR` (default `~/.claude`) and sets a `statusLine` in the user's Claude `settings.json`.
- UI: `MainWindow.xaml`, `MainWindow*.cs`.
- Design notes: `docs/superpowers/specs/`.
- `.projectmem/` is generated memory for AI sessions and is partly stale. The code and `README.md` are authoritative.

## Rules

- Never open `auth.json` or Claude credentials. The widget does not read them either.
- Do not commit usage logs or local settings. `.gitignore` covers them.
- Tests must not start model tasks or spend credits. The checks use fakes.

## Claude status-line contract

Claude Code includes `rate_limits` only for subscribers, and only after the first reply. A window is present only until its reset time. A status-line update with no usable window must not clear the last reading. An explicit `rate_limits_available: false` (API key, Bedrock, Vertex) does clear it. The logic is in `ClaudeUsage.Capture`.
