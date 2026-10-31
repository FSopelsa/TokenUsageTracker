# Claude cloud environment

Settings for a Claude Code cloud environment that works on this repository. The
cloud session builds and runs the cross-platform checks; Windows-only behaviour
(WPF rendering, the status-line bridge, live Codex/Claude accounts) is verified by
the `Windows widget` GitHub Actions workflow and on your own PCs.

Verified in a cloud session (Ubuntu 24.04, .NET SDK 10.0.114): the setup script
installs from a clean state in about 15 seconds, all 90 usage checks pass, and both
the widget and `WindowChecks` compile with Windows targeting (0 warnings).

## Setup script

Paste the contents of [`scripts/cloud-setup.sh`](../scripts/cloud-setup.sh) into
the environment's **Setup script** field. The environment does not read the file
from the repository; it is kept here so changes are reviewed in git.

The script installs only `dotnet-sdk-10.0` from Ubuntu's signed archive, sets the
.NET telemetry opt-outs for its own run (the script cannot see environment
variables), and skips the install when the SDK is already present.

Do not use a generic Node/Java template here: this repository has no
`package.json`, so `npm ci` fails and the session starts without .NET.

## Network access

Use **Trusted** (the default package-manager allowlist), not **Full**. The
project only needs the Ubuntu archive (setup) and `api.nuget.org` (restore,
including the Windows targeting pack). [`NuGet.Config`](../NuGet.Config) already
clears all other package sources.

## Network secrets / API credentials

None. Builds and checks need no keys, and GitHub access goes through Claude's
GitHub integration without a token in the container. Never add `auth.json`,
Claude/Codex sign-in files, `ANTHROPIC_API_KEY`, `GH_TOKEN`, `GITHUB_TOKEN` or a
personal access token. Environment variables are visible to anyone who can use
the environment.

## Environment variables

| Name | Value | Purpose |
| --- | --- | --- |
| `DOTNET_CLI_TELEMETRY_OPTOUT` | `1` | No .NET CLI telemetry |
| `DOTNET_NOLOGO` | `1` | No first-run banner |
| `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE` | `1` | No background workload update checks |

Leave `CODEX_HOME` and `CLAUDE_CONFIG_DIR` unset: they point at Windows paths that
do not exist in the container.

## Commands for cloud sessions

```bash
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release
```

```bash
dotnet build tests/WindowChecks/WindowChecks.csproj --configuration Release -p:EnableWindowsTargeting=true
```

The second command compiles the widget and its window checks but cannot run them.
Do not pass `--live-rpc` in the cloud; it needs an installed, signed-in Codex.
Pushing a branch runs `scripts/Build.ps1` on `windows-latest`, which runs both
check suites and publishes the portable executable.
