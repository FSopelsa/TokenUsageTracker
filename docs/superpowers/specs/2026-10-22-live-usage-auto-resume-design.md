# Live usage and automatic task resuming

Date: 2026-10-22

Status: implementation requested directly by the user; further brainstorming and planning gates waived.

## Purpose

Show current Codex plan usage and optionally continue the exact task interrupted by a usage limit once the account can work again. Keep the existing full and compact widget layouts and portable Windows build.

The user requested a saved setting with two target options, **Codex Desktop** and **CLI**, so the widget can be used on computers with different Codex setups.

## Evidence and compatibility

- The widget currently selects the newest `token_count` snapshot in local session logs. Those snapshots can lag behind account usage or repeat a previously cached value.
- A read-only `account/rateLimits/read` call through the installed Codex app-server succeeded during investigation. This provides a live source without handling credentials in the widget or making a model request.
- The installed CLI exposes exact-session resume and queue commands. The reference project resumes with `codex exec resume <SESSION_ID>`.
- A working connection to the existing Desktop runtime has **not** been established on this installation. The local daemon status probe failed. A Desktop chat must not be silently moved into a separate CLI runtime.

Desktop mode therefore has a compatibility gate: automatic continuation is enabled only when the widget can reach the supported shared app-server that owns the Desktop chat, inspect that exact chat, and submit work to it. Otherwise the UI shows **Desktop connection unavailable** and the reason. The setting remains available for compatible installations. Queuing a message alone is not proof that the Desktop runtime will start it.

## Controls

- Add an **Auto-resume** checkbox in the existing header, off by default. Preserve the current window dimensions; use the existing free header space.
- Add mutually exclusive **Codex Desktop** and **CLI** radio options under **Resume target** in the options menu. Default to Desktop. Save both settings locally.
- Show pending count and the next eligible reset in a tooltip/status message. Keep the normal usage and reset display.
- Disabling auto-resume clears pending recovery. Changing target mode clears the previous mode's pending recovery. Neither action interrupts work that has already resumed.
- Checkbox interaction must be excluded from the header's drag handler, just like the existing buttons. Keyboard and accessibility labels must identify the toggle and selected target.

## Live usage

Use the installed Codex executable and its documented app-server protocol. Reuse the existing 15-second polling loop, with one refresh in flight at a time and a ten-second request timeout. Perform an immediate refresh on F5 and immediately before any recovery attempt.

The widget does not open `auth.json`, collect tokens, or add an API-key setup flow. Codex manages its existing sign-in. Missing Codex, missing ChatGPT sign-in, unsupported protocol, network errors, and timeouts produce an actionable status rather than a fabricated balance.

Use the `codex` bucket from `rateLimitsByLimitId` when present; accept the compatible single-bucket response only when it represents the same general allowance. Parse reported window durations and Unix reset timestamps. A successful backend response receives a fresh retrieval timestamp.

If a live refresh fails, keep the last reading with an explicit **Last live reading** or **Local snapshot** label. Existing JSONL reads remain a display fallback. Cached readings, missing fields, and elapsed reset timestamps alone never authorize automatic recovery.

## Eligible tasks

Monitor local root-chat session records for the selected target mode while auto-resume is enabled. Do not schedule spawned agents or historical failed tasks from before the option was enabled.

Require a structured terminal turn failure whose `codex_error_info` identifies `usage_limit_exceeded`. Do not infer task failure from a percentage, assistant prose, a network error, a permission refusal, or an application crash. Unknown record formats remain unscheduled.

Remember the exact chat UUID and failed turn identity, its workspace, recorded execution settings, and the relevant account identity. Distinguish Desktop and CLI origin from session metadata; unknown origins remain unscheduled.

Before recovery, reread the task state. A later completion, cancellation, user message, active turn, archive, missing workspace, changed account, or incompatible execution settings invalidates the pending attempt. A fresh rate limit during a widget-initiated continuation may create a new pending recovery for that new failed turn.

## Recovery flow

1. Record an eligible interrupted task once, keyed by chat UUID and failed turn.
2. Wait until its reported reset time, while continuing normal usage refreshes. On wake from sleep or widget restart, evaluate overdue work immediately.
3. Fetch current account usage. Continue waiting if a five-hour, weekly, or other applicable allowance still blocks the task. Failures or ambiguous availability do not start work. Do not spend credits or redeem a reset to make the task run.
4. Revalidate the exact task, account, workspace, and execution settings.
5. Persist the attempt as in flight, then send one continuation: **Continue the unfinished task from where it stopped. Preserve its scope and existing instructions. Stop when the task is complete.**
6. Observe the result. Completion removes the pending entry. A new quota failure waits for its next reset. Other failures stop recovery and show the reason.

### Codex Desktop

Use the supported connection to the existing Desktop app-server, verify that the chat is idle, and submit a continuation to that same chat. Preserve its runtime and approval handling. A successful request must be followed by a verified started turn; a queued message is not displayed as resumed work.

Connection discovery and the exact continuation method must be proven against the installed Desktop version before enabling this mode. An unavailable connection, unsupported method, or uncertain delivery leaves the task pending for manual attention, with no CLI fallback, private database writes, or simulated keystrokes.

### CLI

Resume the recorded UUID through the installed CLI in the recorded workspace. Preserve the recorded model/provider, sandbox, and approval policy; do not use `--last`, create a fork, or enable approval/sandbox bypass flags. If settings cannot be preserved or approval is required in a noninteractive run, stop recovery and report that manual continuation is needed.

Use process arguments rather than composing an executable shell command from transcript content. Capture only the small amount of structured output needed to classify the result.

## Persistence and lifetime

Store settings and a small, atomically written pending-state JSON file under `%LOCALAPPDATA%\CodexUsageWidget`, outside the repository. Persist identifiers and recovery metadata, not credentials, prompts, or chat transcripts.

Reuse the existing single-instance widget guard. Save the in-flight marker before dispatch. After a crash or ambiguous acknowledgement, reconcile the chat's later turns; if delivery cannot be established, require manual attention rather than risk a duplicate continuation.

The widget performs recovery while running, including when hidden to the tray. Quitting stops its scheduler. Pending entries are revalidated when it starts again. Do not install a Windows service, scheduled task, or startup entry for this feature.

## Smallest code changes

- `CodexClient.cs`: executable discovery, app-server connection, live usage reads, and supported continuation requests.
- `ResumeController.cs`: eligible-task detection, pending state, revalidation, and recovery decisions. Keep transport work in `CodexClient`.
- `UsageReader.cs`: retain the current display fallback and reuse its usage records where useful. Do not duplicate the usage display model.
- `MainWindow.xaml` / `MainWindow.xaml.cs`: header toggle, target radio controls, status/source labels, and calls from the existing timer.
- `WidgetSettings.cs`: saved enable flag and target mode, with backward-compatible defaults.
- `tests/UsageChecks`: reuse the current dependency-free check program for parsing and recovery-policy cases; use a fake Codex process for transport behavior.
- `README.md`: public setup, live/fallback sources, auto-resume behavior, Desktop compatibility, and widget-lifetime requirements. Remove the duplicate compact-mode feature bullet while touching this section.

No new third-party runtime dependency, background installer, database, or general job framework.

## Verification and completion

Cover live-response parsing, unsupported/missing fields, timeout, and cached-data fallback. Recovery checks must include an exhausted weekly window, a reset time passing before quota refreshes, cancellation, task completion, a newer user message, account/mode changes, duplicate discovery, restart after dispatch, and approval refusal.

Use fake time and a fake Codex process to exercise the lifecycle without exhausting account usage or starting work in a real project. Verify the real usage read separately with a read-only query.

Compile and publish the Windows application. Inspect both window modes for clipping, toggle accessibility, and drag behavior. Existing parsing checks must keep passing.

Desktop mode is not considered implemented until a continuation is demonstrated in the same Desktop chat through a supported connection. If that cannot be established, report the compatibility limitation before treating the requested feature as complete. Distinguish simulated reset tests from an actual five-hour recovery cycle; do not claim the latter without observing it.

## References

- [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
- [Codex CLI reference](https://learn.chatgpt.com/docs/cli/reference)
- [codex-auto-resume reference project](https://github.com/lutzkind/codex-auto-resume)

Existing working-tree edits are preserved. This design is left uncommitted for review; GitHub publishing remains user-controlled.
