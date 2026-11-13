# Claude prompt-cache countdown, safer snapshot merging, and pace forecast

Date: 2026-11-03

Status: the user asked for the three research ideas to be evaluated, planned and implemented in one pass; separate approval gates waived. Written under the ponytail rules (smallest complete change).

## Evaluation of the three ideas

### 1. Prompt-cache countdown (keep the core, skip the diagnostics)

Claude Code v2.1.251+ sends `prompt_cache` in the status-line JSON. `expires_at` is an absolute Unix time, so the widget can count down on its own 15-second timer even while Claude Code is idle. That covers the case that matters: you step away and want to know whether replying now is cheaper than replying later. `recache_tokens_if_cold` says how much the next prompt re-caches once the cache goes cold.

- **Keep:** `expires_at` and `recache_tokens_if_cold`. A single footer line shows "Cache cold in 3m · re-cache ~183k tokens", and later "Cache cold · next prompt re-caches ~183k tokens". The line turns amber in the last two minutes.
- **Skip:** `misses`, `last_miss_cause`, `hit_ratio`, `ttl`, `warm`. These are diagnostics that Claude Code's `/usage` already shows. `warm` can be derived from `expires_at`.
- **Limit:** the cache belongs to whichever session wrote last. The tooltip says so. Keeping per-session caches would mean storing session IDs, which the bridge deliberately avoids.

### 2. Monotonic merge (keep, and it fixes a real bug)

Claude Code runs the status line once when a session starts, before any response. At that point `rate_limits` is absent. `Capture` currently writes that absence, so **opening any new Claude session blanks the widget** until that session gets its first reply. An idle session that re-renders can also replace a newer reading with an older, lower one.

- An update without any usable windows keeps the previous reading. A partial limits update clears the missing window; an explicit `rate_limits_available: false` clears both.
- If a window arrives with the **same** `resets_at` as the stored one, the widget keeps the higher `used_percentage`, because usage within a fixed window only increases. A different `resets_at` still replaces the stored window, as today. This means a second account or a new window is never blocked by an old value.
- `received_at` advances only when the incoming data supplied at least one displayed window. A stale session therefore cannot make an old value look fresh.
- **Accepted risk:** if Anthropic reduces usage mid-window without changing `resets_at`, the widget shows less remaining than the true value until the reset. That errs on the cautious side.

### 3. Usage log and burn-rate forecast (keep the forecast, drop the log)

A forecast does not need a stored history. The average pace since the window opened (`resets_at − window length`) can be computed from a single reading. It works for both Codex and Claude, with no new file, retention policy or migration.

- When that pace runs out **before** the reset, the reset line reads "Resets in 2h 10m · empty ~15:40". The tooltip explains that this assumes the average pace since the window opened. The widget shows nothing when the pace lasts until the reset, when a reset or window length is missing, or when less than 1/20 of the window has passed (too early to extrapolate).
- The pace is measured at the reading's own timestamp, not at the current time, so a stale reading does not understate the pace. A projected time that has already passed is not shown.
- **Skipped:** a persisted reading log and a "recent pace" estimate. They would only matter if bursty use made the average misleading. That is marked as a `shortcut:` in the code.

## Design

| Unit | Change |
|---|---|
| `UsageReader.cs` | `PromptCache(ExpiresAt, RecacheTokens)` record with `Describe(now)`; optional `Cache` on `UsageSnapshot`; `UsageWindow.RunsOutAt(readAt)`. |
| `ClaudeUsage.cs` | `Parse` reads `prompt_cache`; `Capture` merges with the stored snapshot and persists the two cache fields; `Read` shares a `Load` helper. |
| `MainWindow.xaml.cs` | The reset line and tooltip show the forecast; the Claude footer shows the cache line, also in the card tooltip. |
| `README.md` | Documents the stored fields, the new merge rule and the forecast. |

Validation at the trust boundary matches the existing reset handling: finite, in-range Unix seconds and a non-negative integer token count. Invalid values are dropped, never displayed. Compact mode shows the cache line only in the card tooltip, because its footer line is hidden.

## Testing

Add the following to `tests/UsageChecks`:

- forecast cases: runs out before the reset, lasts until the reset, too early, zero usage, expired, missing window length;
- cache text when warm and when cold, with and without a re-cache count;
- invalid cache values are rejected;
- a window-less input keeps the stored allowance and its timestamp;
- the same reset keeps the higher usage without making the reading look fresh;
- a new reset replaces the stored window;
- cache fields survive the merge;
- the stored file still contains no session or workspace data.

Update the existing check that expected a window-less input to clear the allowance. The current `WindowChecks` still hold: a deleted snapshot file still blanks the display.
