# Codex and Claude Code in one view

Date: 2026-11-13

Status: draft for approval. The layout, colours, resume scope and branch handling were agreed in conversation; items marked **(my call)** are proposals the user can still change.

## Why

The user switches between Codex and Claude Code during the day. Before starting a task they want to know which tool has room left, in one concise view, without switching the widget's provider.

## Goals

1. A third provider choice, **Both**, that shows Codex and Claude Code together, grouped by tool: each tool's 5-hour and weekly windows side by side.
2. Each tool's bars have their own colours in Both mode. The warning level (green, amber, red) is always readable from the percentage text.
3. In Both mode, two tray numbers (one per tool) and a taskbar button that warns about whichever tool is lower.
4. Features that worked for only one tool are extended where the local data allows:
   - Task lists (5h and weekly) also cover Claude Code, from its local transcripts.
   - When a Claude usage limit interrupted a session and the limit has reset, a notification names the session and offers its resume command.
5. Everything ends up merged into `main`.

## Non-goals

- **Automatic Claude resume.** The user wants it eventually. This release only notifies. The detection and state below are designed so a later dispatcher can reuse them (see "Future: automatic Claude resume").
- **A Codex prompt-cache countdown.** Codex logs cached token counts but not when the cache expires. That line stays Claude-only.
- Separate refresh intervals per tool, or more than two tools.

## Anti-goals (a failure even if it works)

- One tool's slow or failed read blanks or delays the other tool's numbers. A Codex RPC timeout (10 s) must not hold back the Claude display.
- A missing, expired or unread window is shown as available. This rule already applies to each tool and must survive the combined view.
- The widget starts Claude work by itself, or reads Claude credentials. It reads only `projects/**/*.jsonl` transcripts and its own `usage-widget/usage.json` under the Claude config folder.
- Both mode makes the single-tool modes worse. Codex-only and Claude-only users see no change except the new menu choice, and the Claude task list in Claude mode.

## Constraints

- WPF on `net10.0-windows`. No new NuGet packages.
- Notifications use `NotifyIcon.ShowBalloonTip`, which Windows 10 and 11 show as a normal notification.
- Pure logic goes in files the usage checks compile (`tests/UsageChecks`, `net10.0`, runs on Linux). WPF and WinForms code is covered by the window checks, which run on Windows CI.
- Window checks must not call `WindowsShell` setters, show real notifications, or read the real Claude or Codex folders.
- Claude transcripts can be large. Parse incrementally with the same file cache pattern as `TaskHistory` (length and last-write time) and skip files last written before the window start.

## Approaches considered

**A. Grouped by tool (chosen by the user).** One section per tool, with its 5-hour and weekly windows next to each other. A tool is blocked by whichever of its own windows runs out first, so those two numbers belong together when deciding which tool to use.

**B. Grouped by window.** A 5-hour section and a weekly section, each comparing Codex with Claude. It compares like with like, but you have to read across both sections to judge one tool. Rejected.

**C. A second widget window per tool.** No layout work, but two windows to place and two "always on top" stacks. It is not the concise single view the user asked for. Rejected.

## Design

### Provider choice and settings

- `WidgetSettings.Provider` accepts `"Codex"`, `"Claude"` or `"Both"`. Any other saved value falls back to `"Codex"`, as today.
- Options → Usage provider gets a third item, **Codex and Claude Code**.
- Both mode remembers its own window sizes (`BothFullWidth`, `BothFullHeight`, `BothCompactWidth`, `BothCompactHeight`), so switching back to one tool does not leave an oversized widget **(my call)**. `WindowSize(compact)` becomes `WindowSize(compact, both)`. Defaults: full 440 × 380, compact 432 × 150.
- `ClaudeResumeNotice` (default `true`): notify when an interrupted Claude session can continue. It applies in Claude and Both modes **(my call)**.

### Layout

Extract the per-tool block (heading with plan, 5-hour cell, weekly cell) into a reusable `UsageSection` user control **(my call)**. Single-tool modes show one section with today's arrangement. Both mode shows two sections, Codex on top.

Full mode, Both:

```
 >_  Codex + Claude               Auto-resume  ↕ ⌖ ⋯
 CODEX · PLUS
 5-hour            82%    Weekly            64%
 ████████░░                ██████░░░░
 Resets in 2h 10m          Resets in 4d 3h
 CLAUDE · MAX
 5-hour            31%    Weekly            58%
 ███░░░░░░░                █████░░░░░
 Resets in 1h 05m          Resets in 2d 7h · empty ~Fri 14:00
 [   5h tasks ▾   ]        [  Weekly tasks ▾  ]
 ─────────────────────────────────────────────────
 Codex live · 1m ago  ·  Claude snapshot · 4m ago   ↻
 Cache cold in 3m · re-cache ~183k tokens
```

Compact mode, Both:

```
 >_  Codex + Claude                   ↕ ⌖ ⋯
 Codex    5h  82% ████░    Week 64% ███░
 Claude   5h  31% ██░░░    Week 58% ███░
 Codex 1m ago · Claude 4m ago              ↻
```

- The heading reads "Codex + Claude" and the window title "Token Usage".
- Footer, first line: one short status per tool. Second line: the Claude cache line when one exists, otherwise the Codex auto-resume status. Compact mode hides the second line, as today; its text moves to the card tooltip.
- The auto-resume toggle stays visible in Both mode, because Codex auto-resume works there.
- Minimum sizes must keep every visible element inside the card; the window checks enforce this. Suggested minimums are 300 × 140 compact and 380 × 340 full **(builder may adjust)**. At narrow compact widths, apply the same rules that already hide secondary header controls.
- Single-tool modes keep today's layout and default sizes.
- **Options menu in Both mode** shows the items of both tools: auto-resume and resume target, the Codex data folder items, **Set up Claude usage…**, and **Open usage dashboard ▸** with one entry per tool. **F5** and ↻ reread both tools.

### Colours

The percentage text always uses the warning palette (today's colours): green above 25%, amber at 25% or below, red at 10% or below, and muted when expired or stale.

Bars:

| Mode | Codex bars | Claude bars |
|---|---|---|
| Codex only or Claude only | warning palette, as today | warning palette, as today |
| Both | warning palette: lime `#BEF078`, amber `#F9C66E`, red `#FF8484` | Claude palette: mint `#6FD8C0`, orange `#F59E5B`, rose `#F06292` |

Both palettes change shade at the same levels, so the bars still warn. The Claude hex values are **my call** and are checked for contrast against the card background `#171E26`. Put the choice in a pure `UsageColors` helper returning RGB values, so the usage checks can test it.

### Reading both tools

- Split `RefreshAsync` into `ReadCodexAsync` and `ReadClaudeAsync`. Each returns a `ProviderReading`: the snapshot, a source label, an error, a stale flag, and that tool's extra line (Codex recovery status, or the Claude cache line and setup warning).
- In Both mode, both reads start together. Each section renders as soon as its own read finishes. The footer and taskbar update after each render, from the latest reading of each tool.
- `Current()` checks are per tool, so changing the Codex data folder discards only a stale Codex result.
- Codex auto-resume runs whenever Codex is shown (Codex or Both mode): `resume.Configure` uses `Provider is "Codex" or "Both"`.

### Tray and taskbar

- **Single-tool modes:** unchanged, one tray icon.
- **Both mode:** a second `NotifyIcon` for Claude. Each icon shows its own tool's tighter-window number, with the tooltip naming the tool, for example "Claude: 5-hour 31% · Weekly 58% left — 4m ago". Windows does not let apps choose the order of tray icons, so the badge shape tells them apart: Codex keeps the rounded square, Claude uses a circle. The badge fill uses the same palettes as the bars **(my call)**. Both icons share the menu and the double-click action. Leaving Both mode disposes the second icon.
- **Taskbar button:** progress and overlay come from whichever tool has the lowest remaining usable window. `TaskbarReading` gains a provider-aware overload that returns which tool won, so the overlay uses that tool's badge. The thumbnail description lists both tools.

### Claude task lists

- New `ClaudeTaskHistory`. It reads `<Claude config folder>/projects/**/*.jsonl` and returns the existing `TaskRanking`/`RankedTask` types, so the menu and details window are reused.
- A task is one real user prompt plus the assistant replies that follow it, up to the next real prompt. A real prompt is a `user` entry that is not a tool result and not a meta or system message.
- Tokens: deduplicate assistant entries by `message.id`, because one reply is written as several lines carrying the same usage. Input = `input_tokens + cache_creation_input_tokens + cache_read_input_tokens`, Cached = `cache_read_input_tokens`, Output = `output_tokens`, attributed at the entry's `timestamp`.
- Subagent transcripts (`isSidechain` entries, or files in a session's subagent folder) count toward the parent session's task when they can be linked, otherwise as their own "Agent subtask".
- Model from `message.model`; workspace from `cwd`; status "Interrupted by usage limit" when the detector below matches.
- Estimated percentage points use the same rule and note as Codex: token share × reported window usage. Ranking includes cached tokens, as for Codex **(my call)**. The details window shows cached tokens separately. The note says cache reads may count less than other tokens toward Claude limits.
- Both mode: the two task buttons open one menu with a "Codex" group and a "Claude Code" group. Each group is estimated against its own tool's window.
- In Claude mode the task buttons are enabled. The README sentence saying Claude has no task history is replaced.

### Claude resume notification

Detection (a pure `ClaudeInterruptions` class, shared with the transcript parser):

- A session counts as interrupted by a usage limit when its last assistant entry is an API error message (`isApiErrorMessage: true`) that names a usage or rate limit, and no real user prompt follows it.
- Match the error leniently: an `error` field of `rate_limit`, or message text containing "usage limit" or "limit reached" (case-insensitive). The exact current wording is not confirmed from a real sample (see "Open questions").
- Record the session ID, the error entry's `uuid`, the time, the `cwd`, and the task title.

When to notify:

- A blocking window is any Claude window whose stored reading at or after the error time shows at least 99.5% used **(my call)**. Notify once every blocking window's reset time has passed.
- Notify sooner if a snapshot received after the error reports both windows below 99.5% used.
- If no blocking window is known (for example, the snapshot was stale), wait for a snapshot received after the error that reports both windows below 99.5% used. A countdown alone never triggers the notice.
- It notifies once per error `uuid`. State is stored in `%LOCALAPPDATA%\TokenUsageTracker\claude-resume.json`, the same folder as Codex recovery state, holding only IDs, times, `cwd` and the title.
- Errors from before the feature was first enabled are ignored, as in Codex recovery.
- A newer user prompt in that session cancels a pending notice.

The notification:

- Text: "Claude usage has reset. ‘<task title>’ in <folder name> can continue. Click to copy the resume command."
- Clicking copies `Set-Location -LiteralPath '<cwd>'; claude --resume <session id>` (PowerShell, the default Windows terminal) and shows the widget **(my call)**.
- Options → **Claude sessions to resume ▸** lists pending sessions. Each item copies its command; "Dismiss" clears it.
- Options → **Notify when Claude can continue** (checkable) turns `ClaudeResumeNotice` on or off.
- Notifications go through a small seam (an interface or delegate), so the window checks can record them instead of showing them.

The notification never runs `claude` itself.

### Future: automatic Claude resume (not in this release)

The `ClaudeInterruptions` state holds everything a dispatcher needs: session ID, `cwd` and the error ID. A later version could run `claude --resume <id> -p "<continue prompt>"` in the background, like the Codex CLI target. Two problems must be solved first:

1. A trustworthy "allowance is back" signal. A passed countdown alone is not enough under the existing rule.
2. Permission mode. A headless run cannot ask for approvals.

These are not in scope now.

## Testing

Usage checks (Linux):

- Settings: `"Both"` round trip; an unknown provider falls back; Both sizes are kept separately from the single-tool sizes.
- `UsageColors`: the right palette for each mode, tool and level; text colour is independent of tool.
- `TaskbarReading` across two tools: the lower tool wins; ties go to Codex; no usable window anywhere gives the empty reading; tray tooltips per tool.
- `ClaudeTaskHistory` from fixture transcripts: grouping by prompt, `message.id` dedupe, cached tokens, subagent linking, window boundaries, a partial last line, a deleted file.
- `ClaudeInterruptions`: detects the limit error; ignores other API errors; a later prompt cancels; notifies once per error; waits for the reset; ignores errors from before enabling; state round trip.

Window checks (Windows CI):

- Both mode at minimum and default sizes, full and compact: all four cells fit in the card, with screenshots.
- In Both mode the Codex and Claude bar colours differ at the same level, and the text colour matches the level.
- A Claude read failure leaves the Codex cells filled, and the reverse.
- Two tray icons in Both mode, one after switching back. The taskbar follows the lower tool.

## Branches

The taskbar branch `prel/exciting-bohr-mdt58o` already has `main` merged in (commit `7c73149`). This work continues on that branch. When the window checks pass on Windows CI, everything merges into `main`.

## Open questions

1. **Exact Claude usage-limit error text.** The detector matches leniently. One real example would let a usage check pin it down: the last two lines of a Claude transcript that ended at a usage limit, with prompt text removed. It is not blocking.

## Left to the builder

- Control and file names, apart from those named above.
- Exact minimum sizes, within the window-check rule.
- How subagent transcripts are linked to a parent session, as long as linked tokens are not counted twice.
