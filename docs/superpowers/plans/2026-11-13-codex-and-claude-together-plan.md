# Implementation plan: Codex and Claude Code in one view

Date: 2026-11-13
Design: `docs/superpowers/specs/2026-11-13-codex-and-claude-together-design.md` (approved 2026-11-13)
Branch: `prel/exciting-bohr-mdt58o`, which already contains `main` (merge commit `7c73149`). Task 19 merges into `main`.

Every code block below is the exact content of a draft that was compiled and checked before this plan was written: the usage checks passed (172) and the app and window checks compiled with `-p:EnableWindowsTargeting=true`. The window checks themselves need Windows; they run in CI in Task 18.

## Plan: Codex and Claude Code in one view

### Context
- Framework: WPF with WinForms tray icons, `net10.0-windows`, published as one portable exe.
- Language: C# on the .NET 10 SDK.
- Testing: `tests/UsageChecks` (console, `net10.0`, runs on Linux, links the pure source files) and `tests/WindowChecks` (WPF, Windows only, runs in the GitHub Actions workflow).
- Integration points: `MainWindow` (UI, refresh, menus), `WidgetSettings` (saved settings), `TaskbarReading` and `TrayBadge` (tray and taskbar), `ClaudeUsage` (status-line snapshot, unchanged), `TaskHistory` (Codex task list), `.github/workflows/windows.yml` (unchanged; runs `scripts/Build.ps1`).
- Rules from AGENTS.md: never open `auth.json` or Claude credentials; tests use fakes and temp folders and never start Codex or Claude; window checks must not call `WindowsShell` setters.

### Departures from the design, and calls made while drafting
1. **No `UsageSection` user control.** The window checks find elements by name, and `FindName` cannot see inside a user control's name scope, so the Claude cells are a second named block in `MainWindow.xaml`.
2. **Minimum sizes:** full Both mode 380 × 370 and compact Both mode 300 × 150 (the design suggested 380 × 340 for full). The height was estimated from the layout; Task 18's CI run is the real test.
3. **Compact Both mode hides reset lines.** It keeps only percentages and bars, with the tool named in each label, like the agreed sketch.
4. **Blocking window:** any Claude window in the current snapshot at 99.5% or more used. This applies the design's rule without tracking reading history.
5. **Notices scan transcripts changed in the last 8 days** (or since notices were enabled, if later). A week-old limit has reset anyway, and this keeps the scan small.

### How to execute
- Run every command from the repository root.
- "Replace the whole file" means write the code block as the file's full content. New files are marked as new.
- Stop at any failed verify step, fix it, and rerun that step before continuing.

### Tasks

#### Task 1: Settings: a Both provider with its own sizes

**Goal:** Add the "Both" provider, separate Both-mode window sizes, the Claude notice setting and a Normalize step that repairs unknown saved values.

**File:** src/TokenUsageTracker/WidgetSettings.cs

Replace the whole file with the code below. Load() now calls Normalize(), so MainWindow no longer needs its own provider check (removed in Task 14).

**Code:**
```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenUsageTracker;

public sealed class WidgetSettings
{
    private static readonly string FilePath = Path.Combine(AppData.Folder(), "settings.json");
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = true;
    public bool CompactMode { get; set; }
    /// <summary>"Codex", "Claude" or "Both".</summary>
    public string Provider { get; set; } = "Codex";
    public double? FullWidth { get; set; }
    public double? FullHeight { get; set; }
    public double? CompactWidth { get; set; }
    public double? CompactHeight { get; set; }
    public double? BothFullWidth { get; set; }
    public double? BothFullHeight { get; set; }
    public double? BothCompactWidth { get; set; }
    public double? BothCompactHeight { get; set; }
    public bool AutoResume { get; set; }
    public string ResumeTarget { get; set; } = "Desktop";
    public string? CodexFolder { get; set; }
    public bool ClaudeResumeNotice { get; set; } = true;

    [JsonIgnore] public bool Both => Provider == "Both";
    [JsonIgnore] public bool ShowsCodex => Provider is "Codex" or "Both";
    [JsonIgnore] public bool ShowsClaude => Provider is "Claude" or "Both";

    /// <summary>Repairs values a hand-edited or older settings file may contain.</summary>
    public void Normalize()
    {
        if (Provider is not ("Codex" or "Claude" or "Both")) Provider = "Codex";
        if (ResumeTarget is not ("Desktop" or "CLI")) ResumeTarget = "Desktop";
    }

    public (double Width, double Height) WindowSize(bool compact) => WindowSize(compact, Both);

    public (double Width, double Height) WindowSize(bool compact, bool both)
    {
        static double Valid(double? value, double fallback) => value is > 0 && double.IsFinite(value.Value) ? value.Value : fallback;
        if (both)
            return compact ? (Valid(BothCompactWidth, 432), Valid(BothCompactHeight, 170))
                : (Valid(BothFullWidth, 440), Valid(BothFullHeight, 400));
        return compact ? (Valid(CompactWidth, 432), Valid(CompactHeight, 162))
            : (Valid(FullWidth, 316), Valid(FullHeight, 384));
    }

    public void RememberSize(bool compact, double width, double height)
    {
        if (Both)
        {
            if (compact) { BothCompactWidth = width; BothCompactHeight = height; }
            else { BothFullWidth = width; BothFullHeight = height; }
        }
        else if (compact) { CompactWidth = width; CompactHeight = height; }
        else { FullWidth = width; FullHeight = height; }
    }

    [JsonIgnore]
    public string DataFolder => !string.IsNullOrWhiteSpace(CodexFolder) ? CodexFolder
        : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static WidgetSettings Load()
    {
        AppData.MigrateLegacyFolder();
        WidgetSettings settings;
        try { settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { settings = new(); }
        settings.Normalize();
        return settings;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds. `WindowSize(bool)` still exists, so existing callers compile unchanged.

#### Task 2: Colour palettes as pure data

**Goal:** Hold the warning palette (percentage text) and the Claude palette (bars and badges in Both mode) as RGB values the usage checks can test.

**File:** src/TokenUsageTracker/UsageColors.cs

New file: create it with the code below.

**Code:**
```csharp
using System;

namespace TokenUsageTracker;

/// <summary>
/// Colours for percentages, bars and tray badges. The percentage text always uses the warning palette.
/// In Both mode, Claude's bars and badges use their own shades so the two tools are told apart at a glance.
/// </summary>
public static class UsageColors
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public double Luminance
        {
            get
            {
                static double Channel(byte value)
                {
                    double c = value / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
            }
        }
    }

    public static readonly Rgb Card = new(23, 30, 38);
    public static readonly Rgb Green = new(190, 240, 120);
    public static readonly Rgb Amber = new(249, 198, 110);
    public static readonly Rgb Red = new(255, 132, 132);
    public static readonly Rgb Muted = new(155, 169, 183);
    public static readonly Rgb ClaudeGreen = new(111, 216, 192);
    public static readonly Rgb ClaudeAmber = new(245, 158, 91);
    public static readonly Rgb ClaudeRose = new(240, 98, 146);

    /// <summary>Percentage text: the warning palette for every tool. <see cref="UsageLevel.None"/> means expired, stale or missing.</summary>
    public static Rgb Text(UsageLevel level) => level switch
    {
        UsageLevel.Critical => Red,
        UsageLevel.Low => Amber,
        UsageLevel.Normal => Green,
        _ => Muted
    };

    /// <summary>Bars and tray badges: Claude's own shades only when both tools are shown.</summary>
    public static Rgb Bar(UsageLevel level, string provider, bool both) => !both || provider != "Claude" ? Text(level) : level switch
    {
        UsageLevel.Critical => ClaudeRose,
        UsageLevel.Low => ClaudeAmber,
        UsageLevel.Normal => ClaudeGreen,
        _ => Muted
    };

    public static double Contrast(Rgb a, Rgb b)
    {
        double light = Math.Max(a.Luminance, b.Luminance), dark = Math.Min(a.Luminance, b.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds; the new file is not used yet.

#### Task 3: Taskbar follows the lower tool

**Goal:** Add `TaskbarReading.Lowest`, which picks the tool with the least room left for the single taskbar button (ties go to Codex; no usable window gives an empty reading).

**File:** src/TokenUsageTracker/TaskbarReading.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace TokenUsageTracker;

public enum UsageLevel { None, Normal, Low, Critical }

/// <summary>
/// What the taskbar button and the notification-area icon show: the tighter of the reported
/// windows whose reset has not passed. A missing or expired window never counts as a full allowance.
/// </summary>
public sealed record TaskbarReading(UsageLevel Level, double Fraction, string Badge)
{
    public const int TrayTextLimit = 127;
    public static readonly TaskbarReading Empty = new(UsageLevel.None, 0, "");

    public static UsageLevel LevelOf(double remaining) => remaining <= 10 ? UsageLevel.Critical
        : remaining <= 25 ? UsageLevel.Low : UsageLevel.Normal;

    public static TaskbarReading From(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        UsageWindow? tightest = null;
        foreach (var window in Usable(snapshot, now))
            if (tightest is null || window.Remaining < tightest.Remaining) tightest = window;
        if (tightest is null) return Empty;
        double remaining = tightest.Remaining;
        // Round down: the badge must never show more than is left.
        return new(LevelOf(remaining), remaining / 100, ((int)Math.Floor(remaining)).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The tool with the least room left, for the single taskbar button. Ties go to the earlier tool in
    /// <paramref name="tools"/> (Codex is listed first). No usable window anywhere gives an empty provider.
    /// </summary>
    public static (string Provider, TaskbarReading Reading) Lowest(IEnumerable<(string Provider, UsageSnapshot? Snapshot)> tools, DateTimeOffset now)
    {
        (string Provider, TaskbarReading Reading) lowest = ("", Empty);
        foreach (var (provider, snapshot) in tools)
        {
            var reading = From(snapshot, now);
            if (reading.Level != UsageLevel.None && (lowest.Reading.Level == UsageLevel.None || reading.Fraction < lowest.Reading.Fraction))
                lowest = (provider, reading);
        }
        return lowest;
    }

    public static string TrayText(string provider, UsageSnapshot? snapshot, DateTimeOffset now, string age)
    {
        var parts = new List<string>();
        foreach (var window in Usable(snapshot, now))
            parts.Add($"{window.Label.Replace(" window", "", StringComparison.Ordinal)} {window.Remaining.ToString("0.#", CultureInfo.CurrentCulture)}%");
        string text = parts.Count == 0 ? $"{provider} Usage — open widget for status"
            : $"{provider}: {string.Join(" · ", parts)} left — {age} ago";
        return text.Length <= TrayTextLimit ? text : text[..(TrayTextLimit - 1)] + "…";
    }

    private static IEnumerable<UsageWindow> Usable(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot?.Primary is { } primary && !primary.IsExpired(now)) yield return primary;
        if (snapshot?.Secondary is { } secondary && !secondary.IsExpired(now)) yield return secondary;
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds; `From`, `LevelOf` and `TrayText` are unchanged.

#### Task 4: Share the window-start rule and token parsing

**Goal:** Extract `TaskHistory.WindowStart` (current window or rolling lookback) and make `Number` internal, so the Claude task list uses the same rules as Codex.

**File:** src/TokenUsageTracker/TaskHistory.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TokenUsageTracker;

public sealed record TaskTokens(DateTimeOffset At, long Input, long Cached, long Output, string? Response, bool Modern)
{
    public long Total => Input + Output; // Cached input is already included in input.
}

public sealed class UsageTask
{
    public string Session { get; init; } = "";
    public string Turn { get; init; } = "";
    public string? RootTurn { get; set; }
    public string? Parent { get; init; }
    public bool Agent { get; init; }
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "Unknown";
    public string Workspace { get; set; } = "";
    public string Status { get; set; } = "In progress / no completion recorded";
    public string Result { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    public List<string> Steps { get; } = new();
    public List<TaskTokens> Tokens { get; } = new();
    public string Title
    {
        get
        {
            string text = Prompt;
            int request = text.IndexOf("## My request:", StringComparison.OrdinalIgnoreCase);
            if (request >= 0) text = text[(request + 14)..];
            return Short(text.Length > 0 ? text.TrimStart('#', ' ', '\r', '\n') : Agent ? "Agent subtask" : "Prompt not recorded", 85);
        }
    }
    public static string Short(string text, int length) { string value = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return value.Length <= length ? value : value[..length] + "…"; }
}

public sealed record RankedTask(UsageTask Task, long Input, long Cached, long Output, double? EstimatedPoints, DateTimeOffset First, DateTimeOffset Last, string Subtasks)
{
    public long Total => Input + Output;
    public string Cost => (Total > 0 ? $"{Total:N0} tokens" : "Tokens not recorded") + " · " + (EstimatedPoints is { } p ? $"≈{p:0.##} pp estimated" : "quota cost unavailable");
    public string Hover => $"{Task.Title}\n{Cost}\n{Task.Model} · {Task.Status}\n{First.ToLocalTime():g} – {Last.ToLocalTime():g}\nClick for prompt and activity.";
    public string Details => $"{Task.Title}\n\n{Cost}\nInput: {Input:N0} (cached: {Cached:N0}, already included)\nOutput: {Output:N0} (includes reasoning)\nModel: {Task.Model}\nStatus: {Task.Status}\nActivity in this window: {First.ToLocalTime():f} – {Last.ToLocalTime():f}\nWorkspace: {Task.Workspace}\nChat: {Task.Session}\nTurn: {Task.Turn}\n\n{TaskHistory.EstimateNote}\nTokens are recorded in-window responses, including linked agent work. They are not an API price. A response crossing a boundary is attributed at its recorded timestamp.\n\nPROMPT\n{Task.Prompt}\n\nRECORDED RESULT\n{Task.Result}\n\nTOOL ACTIVITY\n{string.Join("\n", Task.Steps.GroupBy(s => s).Select(g => $"{g.Key} × {g.Count()}"))}\n\nLINKED SUBTASKS\n{Subtasks}";
}

public sealed record TaskRanking(IReadOnlyList<RankedTask> Tasks, string Note);

public sealed class TaskHistory
{
    public const string EstimateNote = "Estimated pp = task share of locally recorded tokens × reported window usage. Models and cached tokens consume quota differently. Missing chats, devices or accounts make this incomplete; exact per-task quota cost is not reported.";
    private readonly Dictionary<string, (long Length, DateTime Modified, List<UsageTask> Tasks)> cache = new();

    /// <summary>The start of the reported window when it is current, otherwise a rolling lookback.</summary>
    public static (DateTimeOffset Start, bool Exact) WindowStart(UsageWindow? window, int fallbackMinutes, DateTimeOffset now)
    {
        bool exact = window?.Minutes is > 0 && window.ResetsAt is { } reset && reset > now && reset.AddMinutes(-window.Minutes.Value) <= now;
        return (exact ? window!.ResetsAt!.Value.AddMinutes(-window.Minutes!.Value) : now.AddMinutes(-fallbackMinutes), exact);
    }

    public TaskRanking Read(string home, UsageWindow? window, int fallbackMinutes, DateTimeOffset now)
    {
        var (start, exactWindow) = WindowStart(window, fallbackMinutes, now);
        var tasks = new List<UsageTask>();
        bool incomplete = false;
        try
        {
            string folder = Path.Combine(home, "sessions");
            if (!Directory.Exists(folder)) return new(Array.Empty<RankedTask>(), "No local Codex session files found.");
            string[] paths = Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories);
            var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string old in cache.Keys.Where(p => !present.Contains(p)).ToArray()) cache.Remove(old);
            foreach (string path in paths)
            {
                try
                {
                    var file = new FileInfo(path);
                    if (file.LastWriteTimeUtc < start.UtcDateTime) continue;
                    if (!cache.TryGetValue(path, out var saved) || saved.Length != file.Length || saved.Modified != file.LastWriteTimeUtc)
                    {
                        saved = (file.Length, file.LastWriteTimeUtc, Parse(path));
                        cache[path] = saved;
                    }
                    tasks.AddRange(saved.Tasks);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { incomplete = true; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new(Array.Empty<RankedTask>(), "Cannot read local task history. Check the Codex data folder and permissions."); }
        return Rank(tasks, start, now, exactWindow ? window!.UsedPercent : null,
            $"{(exactWindow ? "Current quota window" : "Reset unavailable; rolling lookback")}: {start.ToLocalTime():g} – {now.ToLocalTime():g}. Local sessions only." + (incomplete ? " Some files could not be read." : ""));
    }

    public static TaskRanking Rank(IEnumerable<UsageTask> source, DateTimeOffset start, DateTimeOffset end, double? used, string note = "")
    {
        var tasks = source.GroupBy(t => (t.Session, t.Turn)).Select(g => g.First()).ToArray();
        var groups = new Dictionary<UsageTask, List<(UsageTask Task, TaskTokens Tokens)>>();
        var seen = new HashSet<string>();
        foreach (var task in tasks)
        {
            var owner = task;
            var visited = new HashSet<UsageTask> { task };
            while (owner.Parent is { } parent)
            {
                var next = owner.RootTurn is null ? null : tasks.FirstOrDefault(t => t.Session == parent && (t.Turn == owner.RootTurn || t.RootTurn == owner.RootTurn));
                if (next is null || !visited.Add(next)) break;
                owner = next;
            }
            bool modern = task.Tokens.Any(t => t.Modern);
            if (task.Started >= start && task.Started <= end && task.Prompt.Length > 0 && !groups.ContainsKey(owner)) groups[owner] = new();
            foreach (var tokens in task.Tokens.Where(t => t.Modern == modern && t.At >= start && t.At <= end))
            {
                if (tokens.Response is { } id && !seen.Add(id)) continue;
                if (!groups.TryGetValue(owner, out var samples)) groups[owner] = samples = new();
                samples.Add((task, tokens));
            }
        }
        double total = groups.Values.Sum(samples => samples.Sum(s => (double)s.Tokens.Total));
        var ranked = groups.Select(g =>
        {
            long input = g.Value.Sum(s => s.Tokens.Input), cached = g.Value.Sum(s => s.Tokens.Cached), output = g.Value.Sum(s => s.Tokens.Output);
            string agents = string.Join("\n\n", g.Value.Select(s => s.Task).Where(t => t != g.Key).Distinct().Select(t => $"{t.Title}\n{t.Model} · {t.Status}\nPrompt: {t.Prompt}\nResult: {t.Result}"));
            return new RankedTask(g.Key, input, cached, output, used is >= 0 and <= 100 && total > 0 && input + output > 0 ? (input + output) / total * used : null,
                g.Value.Count > 0 ? g.Value.Min(s => s.Tokens.At) : g.Key.Started, g.Value.Count > 0 ? g.Value.Max(s => s.Tokens.At) : g.Key.Started, agents.Length > 0 ? agents : "No linked agent subtasks recorded.");
        }).OrderByDescending(t => t.Total).ThenByDescending(t => t.Last).ToArray();
        return new(ranked, note);
    }

    public static List<UsageTask> Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tasks = new Dictionary<string, UsageTask>();
        UsageTask? current = null;
        string session = path, workspace = "", model = "Unknown";
        string? parent = null;
        bool agent = false;
        long previousInput = 0, previousOutput = 0, previousCached = 0;
        UsageTask Get(string turn)
        {
            if (!tasks.TryGetValue(turn, out var task)) tasks[turn] = task = new() { Session = session, Turn = turn, Parent = parent, Agent = agent, Workspace = workspace, Model = model };
            return task;
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement; var p = Json.Get(root, "payload");
                string? kind = Json.Text(root, "type"), type = Json.Text(p, "type");
                if (kind == "session_meta")
                {
                    session = Json.Text(p, "id") ?? path; parent = Json.Text(p, "parent_thread_id");
                    agent = Json.Get(p, "source").ValueKind == JsonValueKind.Object; workspace = Json.Text(p, "cwd") ?? "";
                }
                if (kind == "turn_context" || kind == "event_msg" && type == "task_started")
                {
                    string? turn = Json.Text(p, "turn_id");
                    if (turn is not null) { current = Get(turn); current.RootTurn = Json.Text(p, "root_turn_id") ?? current.RootTurn; if (current.Started == default && DateTimeOffset.TryParse(Json.Text(root, "timestamp"), out var started)) current.Started = started; }
                    model = Json.Text(p, "model") ?? model;
                    if (current is not null) { current.Model = model; current.Workspace = Json.Text(p, "cwd") ?? workspace; }
                }
                if (kind == "token_usage_record" && DateTimeOffset.TryParse(Json.Text(root, "timestamp"), out var at))
                {
                    if (Json.Text(p, "thread_id") is { } thread && thread != session) continue;
                    string? turn = Json.Text(p, "turn_id");
                    if (turn is null) continue;
                    var task = Get(turn); task.RootTurn = Json.Text(p, "root_turn_id") ?? task.RootTurn;
                    var u = Json.Get(p, "usage");
                    task.Tokens.Add(new(at, Number(u, "input_tokens"), Number(u, "cached_input_tokens"), Number(u, "output_tokens"), Json.Text(p, "response_id"), true));
                }
                if (kind == "event_msg" && type == "token_count")
                {
                    var u = Json.Get(Json.Get(p, "info"), "total_token_usage");
                    if (u.ValueKind != JsonValueKind.Object) continue;
                    long input = Number(u, "input_tokens"), output = Number(u, "output_tokens"), cached = Number(u, "cached_input_tokens");
                    if (current is not null && DateTimeOffset.TryParse(Json.Text(root, "timestamp"), out var time))
                        current.Tokens.Add(new(time, input < previousInput ? input : input - previousInput, cached < previousCached ? cached : cached - previousCached, output < previousOutput ? output : output - previousOutput, null, false));
                    previousInput = input; previousOutput = output; previousCached = cached;
                }
                if (current is null) continue;
                if (kind == "event_msg" && type == "user_message" && Json.Text(p, "message") is { } message) current.Prompt = message;
                if (kind == "response_item" && type == "message" && Json.Text(p, "role") == "user" && current.Prompt.Length == 0)
                {
                    var content = Json.Get(p, "content");
                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        string text = string.Join("\n", content.EnumerateArray().Select(c => Json.Text(c, "text")).Where(t => t is not null));
                        if (!text.StartsWith("<environment_context>") && !text.StartsWith("<external_codex") && !text.StartsWith("# AGENTS.md instructions") && !text.StartsWith("<subagent_notification")) current.Prompt = text;
                    }
                }
                if (kind == "event_msg" && type is "task_complete" or "turn_failed" or "turn_aborted")
                {
                    var task = Json.Text(p, "turn_id") is { } turn ? Get(turn) : current;
                    task.Status = type == "task_complete" ? "Completed" : type == "turn_failed" ? "Failed" : "Stopped";
                    task.Result = Json.Text(p, "last_agent_message") ?? task.Result;
                }
                if (kind == "response_item" && type is "function_call" or "custom_tool_call")
                {
                    string name = Json.Text(p, "name") ?? "Tool";
                    current.Steps.Add(name);
                    if (name.EndsWith("spawn_agent", StringComparison.Ordinal) && Json.Text(p, "arguments") is { } arguments)
                    {
                        try { using var args = JsonDocument.Parse(arguments); if (Json.Text(args.RootElement, "message") is { } subtask) current.Steps.Add("Delegated task: " + subtask); }
                        catch (JsonException) { }
                    }
                }
            }
            catch (JsonException) { /* A live session can end with a partial JSON record. */ }
        }
        return tasks.Values.ToList();
    }

    internal static long Number(JsonElement value, string key) { var n = Json.Get(value, key); return n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out long count) && count >= 0 && count <= long.MaxValue / 1000 ? count : 0; }
}
```

**Verify:** `dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release`

**Checkpoint:** Output ends with `All 132 checks passed.` (no behaviour change for Codex).

#### Task 5: Read Claude Code transcripts

**Goal:** Parse `<Claude config>/projects/**/*.jsonl` into tasks (one per real prompt, replies deduplicated by message id, subagents linked to the parent prompt) and detect sessions whose last reply was a usage-limit error.

**File:** src/TokenUsageTracker/ClaudeTranscripts.cs

New file: create it with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TokenUsageTracker;

/// <summary>A Claude Code session whose last reply was a usage-limit error with no later prompt.</summary>
public sealed record ClaudeInterruption(string SessionId, string ErrorId, DateTimeOffset At, string Workspace, string Title);

public sealed record ClaudeTranscript(IReadOnlyList<UsageTask> Tasks, ClaudeInterruption? Interruption);

/// <summary>
/// Reads Claude Code transcripts under <c>&lt;config&gt;/projects/**/*.jsonl</c> for the task lists and for
/// usage-limit interruptions. It never opens credentials and keeps only what the widget displays.
/// </summary>
public sealed class ClaudeTranscripts
{
    public const string EstimateNote = "Claude Code transcripts record input, cache-write, cache-read and output tokens per reply. Cache reads may count less toward Claude limits than other tokens, so the estimate can overstate long-context tasks.";
    private readonly Dictionary<string, (long Length, DateTime Modified, ClaudeTranscript Transcript)> cache = new();

    public TaskRanking ReadTasks(string claudeFolder, UsageWindow? window, int fallbackMinutes, DateTimeOffset now)
    {
        var (start, exact) = TaskHistory.WindowStart(window, fallbackMinutes, now);
        var transcripts = Read(claudeFolder, start, out bool incomplete, out string? problem);
        if (problem is not null) return new(Array.Empty<RankedTask>(), problem);
        var tasks = transcripts.SelectMany(t => t.Tasks).ToList();
        LinkAgents(tasks);
        return TaskHistory.Rank(tasks, start, now, exact ? window!.UsedPercent : null,
            $"{(exact ? "Current quota window" : "Reset unavailable; rolling lookback")}: {start.ToLocalTime():g} – {now.ToLocalTime():g}. Local Claude Code transcripts only. {EstimateNote}"
            + (incomplete ? " Some files could not be read." : ""));
    }

    public IReadOnlyList<ClaudeInterruption> Interruptions(string claudeFolder, DateTimeOffset since) =>
        Read(claudeFolder, since, out _, out _).Select(t => t.Interruption).OfType<ClaudeInterruption>()
            .Where(i => i.At >= since).GroupBy(i => i.SessionId).Select(g => g.OrderBy(i => i.At).Last()).ToList();

    private List<ClaudeTranscript> Read(string claudeFolder, DateTimeOffset since, out bool incomplete, out string? problem)
    {
        incomplete = false; problem = null;
        var result = new List<ClaudeTranscript>();
        string folder = Path.Combine(claudeFolder, "projects");
        try
        {
            if (!Directory.Exists(folder)) { problem = "No local Claude Code transcripts found."; return result; }
            string[] paths = Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories);
            lock (cache)
            {
                var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string old in cache.Keys.Where(p => !present.Contains(p)).ToArray()) cache.Remove(old);
                foreach (string path in paths)
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (file.LastWriteTimeUtc < since.UtcDateTime) continue;
                        if (!cache.TryGetValue(path, out var saved) || saved.Length != file.Length || saved.Modified != file.LastWriteTimeUtc)
                            cache[path] = saved = (file.Length, file.LastWriteTimeUtc, Parse(path));
                        result.Add(saved.Transcript);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { incomplete = true; }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { problem = "Cannot read Claude Code transcripts. Check the Claude config folder and permissions."; }
        return result;
    }

    /// <summary>Attributes each subagent task to the parent session's prompt that was running when the agent started.</summary>
    private static void LinkAgents(List<UsageTask> tasks)
    {
        var prompts = tasks.Where(t => !t.Agent).GroupBy(t => t.Session).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Started).ToList());
        foreach (var agent in tasks.Where(t => t.Agent))
            if (agent.Parent is { } parent && prompts.TryGetValue(parent, out var list) && list.LastOrDefault(t => t.Started <= agent.Started) is { } owner)
                agent.RootTurn = owner.Turn;
    }

    public static ClaudeTranscript Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tasks = new List<UsageTask>();
        var replies = new Dictionary<string, (UsageTask Task, int Index)>();
        UsageTask? main = null, agent = null;
        (string Id, DateTimeOffset At, string Session, string Workspace)? limit = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string? type = Json.Text(root, "type");
                if (type is not ("user" or "assistant")) continue;
                if (!DateTimeOffset.TryParse(Json.Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                bool sidechain = Json.Get(root, "isSidechain").ValueKind == JsonValueKind.True;
                string session = Json.Text(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
                string workspace = Json.Text(root, "cwd") ?? "";
                string id = Json.Text(root, "uuid") ?? $"{session}:{at.UtcTicks}";
                var message = Json.Get(root, "message");
                if (type == "user")
                {
                    if (PromptText(root) is not { } prompt) continue;
                    // Agent tasks get their own session key so ranking links them to the parent prompt instead of to each other.
                    var task = new UsageTask
                    {
                        Session = sidechain ? session + "/agent" : session, Turn = id, Parent = sidechain ? session : null, Agent = sidechain,
                        Prompt = prompt, Workspace = workspace, Started = at, Status = "No reply recorded"
                    };
                    tasks.Add(task);
                    if (sidechain) agent = task;
                    else { main = task; limit = null; }
                    continue;
                }
                var current = sidechain ? agent : main;
                if (IsUsageLimit(root))
                {
                    if (current is not null) current.Status = "Interrupted by usage limit";
                    if (!sidechain) limit = (id, at, session, workspace);
                    continue;
                }
                if (Json.Get(root, "isApiErrorMessage").ValueKind == JsonValueKind.True)
                {
                    if (current is not null) current.Status = "API error";
                    continue;
                }
                if (!sidechain) limit = null;
                if (current is null) continue;
                if (Json.Text(message, "model") is { } model && model != "<synthetic>") current.Model = model;
                var content = Json.Get(message, "content");
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var item in content.EnumerateArray())
                    {
                        string? kind = Json.Text(item, "type");
                        if (kind == "tool_use") current.Steps.Add(Json.Text(item, "name") ?? "Tool");
                        if (kind == "text" && Json.Text(item, "text") is { Length: > 0 } text) { current.Result = text; current.Status = "Replied"; }
                    }
                var usage = Json.Get(message, "usage");
                if (usage.ValueKind != JsonValueKind.Object) continue;
                long cacheRead = TaskHistory.Number(usage, "cache_read_input_tokens");
                var tokens = new TaskTokens(at,
                    TaskHistory.Number(usage, "input_tokens") + TaskHistory.Number(usage, "cache_creation_input_tokens") + cacheRead,
                    cacheRead, TaskHistory.Number(usage, "output_tokens"), Json.Text(message, "id"), true);
                // One reply is written as several lines with the same message id; keep its latest usage once.
                if (tokens.Response is { } reply && replies.TryGetValue(reply, out var earlier))
                    earlier.Task.Tokens[earlier.Index] = tokens with { At = earlier.Task.Tokens[earlier.Index].At };
                else
                {
                    if (tokens.Response is { } key) replies[key] = (current, current.Tokens.Count);
                    current.Tokens.Add(tokens);
                }
            }
            catch (JsonException) { /* A live session can end with a partial JSON record. */ }
        }
        var interruption = limit is { } l ? new ClaudeInterruption(l.Session, l.Id, l.At, l.Workspace, main?.Title ?? "Claude Code session") : null;
        return new(tasks, interruption);
    }

    /// <summary>
    /// shortcut: the exact wording of Claude Code's usage-limit error has not been confirmed from a real transcript,
    /// so this matches leniently. Pin it down with a usage check once a real example is available.
    /// </summary>
    public static bool IsUsageLimit(JsonElement root)
    {
        if (Json.Get(root, "isApiErrorMessage").ValueKind != JsonValueKind.True) return false;
        if (Json.Text(root, "error") is { } error && error.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)) return true;
        string text = MessageText(Json.Get(Json.Get(root, "message"), "content"));
        return text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || text.Contains("limit reached", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PromptText(JsonElement root)
    {
        if (Json.Get(root, "isMeta").ValueKind == JsonValueKind.True) return null;
        var content = Json.Get(Json.Get(root, "message"), "content");
        if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(c => Json.Text(c, "type") == "tool_result")) return null;
        string text = MessageText(content).Trim();
        if (text.Length == 0 || text.StartsWith("<local-command-", StringComparison.Ordinal)
            || text.StartsWith("[Request interrupted", StringComparison.Ordinal) || text.StartsWith("<system-reminder>", StringComparison.Ordinal))
            return null;
        return text;
    }

    private static string MessageText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Where(c => Json.Text(c, "type") == "text").Select(c => Json.Text(c, "text")).OfType<string>()),
        _ => ""
    };
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds. The parser only reads transcripts; it never opens credentials.

#### Task 6: Decide when a Claude session can continue

**Goal:** Persist which interruptions were announced, announce each once when every full window has reset or a newer reading shows room, and build the PowerShell resume command and notice text.

**File:** src/TokenUsageTracker/ClaudeResumeNotices.cs

New file: create it with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenUsageTracker;

public sealed class ClaudeResumeState
{
    public DateTimeOffset EnabledAt { get; set; }
    public List<string> Notified { get; set; } = new();
    public List<string> Dismissed { get; set; } = new();
}

/// <summary>
/// Decides when a Claude Code session stopped by a usage limit can continue, and remembers which
/// interruptions were already announced. It never runs Claude Code; the user resumes the session.
/// </summary>
public sealed class ClaudeResumeNotices
{
    public const double FullPercent = 99.5;
    private readonly string statePath;
    private ClaudeResumeState state;

    public IReadOnlyList<ClaudeInterruption> Ready { get; private set; } = Array.Empty<ClaudeInterruption>();
    public bool Enabled => state.EnabledAt != default;
    /// <summary>Only interruptions after this time are announced. A week-old limit has reset anyway.</summary>
    public DateTimeOffset ScanSince(DateTimeOffset now) => state.EnabledAt > now.AddDays(-8) ? state.EnabledAt : now.AddDays(-8);

    public ClaudeResumeNotices(string? path = null)
    {
        statePath = path ?? Path.Combine(AppData.Folder(), "claude-resume.json");
        try { state = JsonSerializer.Deserialize<ClaudeResumeState>(File.ReadAllText(statePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { state = new(); }
        state.Notified ??= new();
        state.Dismissed ??= new();
    }

    public void Configure(bool on, DateTimeOffset now)
    {
        if (on == Enabled) return;
        state = on ? new() { EnabledAt = now } : new();
        Ready = Array.Empty<ClaudeInterruption>();
        Save();
    }

    /// <summary>
    /// True when a snapshot received after the error shows room in every reported window, or when every
    /// window that was full has a known reset that has passed. A countdown alone never counts otherwise.
    /// </summary>
    public static bool CanContinue(ClaudeInterruption interruption, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return false;
        var windows = new[] { snapshot.Primary, snapshot.Secondary }.OfType<UsageWindow>().ToArray();
        if (windows.Length == 0) return false;
        if (snapshot.Timestamp > interruption.At && windows.All(w => !w.IsExpired(now) && w.UsedPercent < FullPercent)) return true;
        var blocking = windows.Where(w => w.UsedPercent >= FullPercent).ToArray();
        return blocking.Length > 0 && blocking.All(w => w.ResetsAt is { } reset && reset <= now);
    }

    /// <summary>Returns the interruptions to announce now. Each is announced once; a later prompt in that session cancels it.</summary>
    public IReadOnlyList<ClaudeInterruption> Tick(IEnumerable<ClaudeInterruption> found, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (!Enabled) return Array.Empty<ClaudeInterruption>();
        var current = found.Where(i => i.At >= state.EnabledAt).ToList();
        var ids = current.Select(i => i.ErrorId).ToHashSet();
        bool changed = state.Notified.RemoveAll(id => !ids.Contains(id)) + state.Dismissed.RemoveAll(id => !ids.Contains(id)) > 0;
        var due = current.Where(i => !state.Notified.Contains(i.ErrorId) && CanContinue(i, snapshot, now)).OrderBy(i => i.At).ToList();
        state.Notified.AddRange(due.Select(i => i.ErrorId));
        Ready = current.Where(i => state.Notified.Contains(i.ErrorId) && !state.Dismissed.Contains(i.ErrorId)).OrderBy(i => i.At).ToList();
        if (changed || due.Count > 0) Save();
        return due;
    }

    public void Dismiss(string errorId)
    {
        if (state.Dismissed.Contains(errorId)) return;
        state.Dismissed.Add(errorId);
        Ready = Ready.Where(i => i.ErrorId != errorId).ToList();
        Save();
    }

    /// <summary>A PowerShell command that reopens the session in its folder, or null for an unexpected session ID.</summary>
    public static string? Command(ClaudeInterruption interruption)
    {
        if (!Regex.IsMatch(interruption.SessionId, "^[A-Za-z0-9-]{1,100}$")) return null;
        string resume = "claude --resume " + interruption.SessionId;
        return interruption.Workspace.Length == 0 ? resume
            : $"Set-Location -LiteralPath '{interruption.Workspace.Replace("'", "''")}'; {resume}";
    }

    public static string Message(ClaudeInterruption interruption)
    {
        string path = interruption.Workspace.TrimEnd('\\', '/');
        string folder = path.Length == 0 ? "" : " in " + path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return $"‘{UsageTask.Short(interruption.Title, 60)}’{folder} can continue. Click to copy the resume command.";
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(statePath + ".tmp", statePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* An unsaved notice can repeat after a restart. */ }
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds. Nothing in this class starts Claude Code.

#### Task 7: Compile the new pure files into the usage checks

**Goal:** Link `UsageColors.cs`, `ClaudeTranscripts.cs` and `ClaudeResumeNotices.cs` into the Linux-runnable usage checks.

**File:** tests/UsageChecks/UsageChecks.csproj

Replace the whole file with the code below.

**Code:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup><Compile Include="../../src/TokenUsageTracker/UsageReader.cs" Link="UsageReader.cs" /></ItemGroup>
  <ItemGroup><Compile Include="../../src/TokenUsageTracker/WidgetSettings.cs" Link="WidgetSettings.cs" /></ItemGroup>
  <ItemGroup>
    <Compile Include="../../src/TokenUsageTracker/AppData.cs" Link="AppData.cs" />
    <Compile Include="../../src/TokenUsageTracker/CodexClient.cs" Link="CodexClient.cs" />
    <Compile Include="../../src/TokenUsageTracker/ResumeController.cs" Link="ResumeController.cs" />
    <Compile Include="../../src/TokenUsageTracker/ClaudeUsage.cs" Link="ClaudeUsage.cs" />
    <Compile Include="../../src/TokenUsageTracker/TaskHistory.cs" Link="TaskHistory.cs" />
    <Compile Include="../../src/TokenUsageTracker/TaskbarReading.cs" Link="TaskbarReading.cs" />
    <Compile Include="../../src/TokenUsageTracker/UsageColors.cs" Link="UsageColors.cs" />
    <Compile Include="../../src/TokenUsageTracker/ClaudeTranscripts.cs" Link="ClaudeTranscripts.cs" />
    <Compile Include="../../src/TokenUsageTracker/ClaudeResumeNotices.cs" Link="ClaudeResumeNotices.cs" />
  </ItemGroup>
</Project>
```

**Verify:** `dotnet build tests/UsageChecks/UsageChecks.csproj --configuration Release`

**Checkpoint:** Build succeeds.

#### Task 8: Usage checks: settings, colours, taskbar

**Goal:** Check the Both setting round trip and fallbacks, palette rules and contrast against the card, and `TaskbarReading.Lowest`.

**File:** tests/UsageChecks/BothChecks.cs

New file: create it with the code below.

**Code:**
```csharp
using System.Text.Json;
using TokenUsageTracker;

internal static class BothChecks
{
    public static void Run(Action<bool, string> check)
    {
        var both = JsonSerializer.Deserialize<WidgetSettings>("{\"Provider\":\"Both\"}")!;
        both.Normalize();
        check(both.Both && both.ShowsCodex && both.ShowsClaude, "Both mode shows Codex and Claude Code");
        var unknown = JsonSerializer.Deserialize<WidgetSettings>("{\"Provider\":\"Gemini\",\"ResumeTarget\":\"Phone\"}")!;
        unknown.Normalize();
        check(unknown.Provider == "Codex" && unknown.ResumeTarget == "Desktop", "Unknown saved provider and resume target fall back to defaults");
        check(both.WindowSize(true) == (432, 170) && both.WindowSize(false) == (440, 400)
            && both.WindowSize(true, both: false) == (432, 162) && both.WindowSize(false, both: false) == (316, 384),
            "Both mode has its own default sizes and leaves the single-tool defaults alone");
        both.RememberSize(compact: true, 500, 200);
        both.RememberSize(compact: false, 520, 420);
        check(both.BothCompactWidth == 500 && both.BothFullHeight == 420 && both.CompactWidth is null && both.FullWidth is null,
            "Resizing in Both mode does not change the single-tool sizes");
        var saved = JsonSerializer.Deserialize<WidgetSettings>(JsonSerializer.Serialize(both))!;
        check(saved.Provider == "Both" && saved.WindowSize(true) == (500, 200) && saved.ClaudeResumeNotice,
            "Both mode, its sizes and the Claude notice setting survive a settings round trip");

        foreach (var level in new[] { UsageLevel.Normal, UsageLevel.Low, UsageLevel.Critical })
        {
            check(UsageColors.Bar(level, "Codex", both: true) == UsageColors.Text(level)
                && UsageColors.Bar(level, "Claude", both: false) == UsageColors.Text(level),
                $"{level}: Codex bars and single-tool Claude bars keep the warning colour");
            check(UsageColors.Bar(level, "Claude", both: true) != UsageColors.Bar(level, "Codex", both: true),
                $"{level}: Claude bars differ from Codex bars when both are shown");
            check(UsageColors.Contrast(UsageColors.Bar(level, "Claude", both: true), UsageColors.Card) >= 3
                && UsageColors.Contrast(UsageColors.Text(level), UsageColors.Card) >= 4.5,
                $"{level}: bar colours stand out from the card and percentage text stays readable");
        }
        check(UsageColors.Bar(UsageLevel.None, "Claude", both: true) == UsageColors.Muted && UsageColors.Text(UsageLevel.None) == UsageColors.Muted,
            "Expired, stale or missing readings are muted for both tools");

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        UsageSnapshot Snapshot(double fiveUsed, double weekUsed) =>
            new(now, null, new(fiveUsed, 300, now.AddHours(2)), new(weekUsed, 10080, now.AddDays(3)));
        var lowest = TaskbarReading.Lowest(new (string, UsageSnapshot?)[] { ("Codex", Snapshot(10, 20)), ("Claude", Snapshot(23.5, 41.2)) }, now);
        check(lowest.Provider == "Claude" && Math.Abs(lowest.Reading.Fraction - 0.588) < 1e-9, "The taskbar follows the tool with less room left");
        var tie = TaskbarReading.Lowest(new (string, UsageSnapshot?)[] { ("Codex", Snapshot(50, 0)), ("Claude", Snapshot(50, 0)) }, now);
        check(tie.Provider == "Codex", "A tie on the taskbar goes to Codex");
        var oneMissing = TaskbarReading.Lowest(new (string, UsageSnapshot?)[] { ("Codex", null), ("Claude", Snapshot(23.5, 41.2)) }, now);
        check(oneMissing.Provider == "Claude", "A tool without a reading never wins the taskbar as if it were empty or full");
        var none = TaskbarReading.Lowest(new (string, UsageSnapshot?)[] { ("Codex", null), ("Claude", null) }, now);
        check(none.Provider == "" && none.Reading == TaskbarReading.Empty, "No usable window for either tool clears the taskbar");
    }
}
```

**Verify:** `dotnet build tests/UsageChecks/UsageChecks.csproj --configuration Release`

**Checkpoint:** Build succeeds; the checks run once Task 10 wires them in.

#### Task 9: Usage checks: Claude tasks and resume notices

**Goal:** Check transcript grouping, deduplication, subagent linking, window boundaries, partial lines, deleted files, interruption detection, notice timing, once-only announcement, restart persistence, cancellation, and safe command quoting. All fixtures live in temp folders.

**File:** tests/UsageChecks/ClaudeTranscriptChecks.cs

New file: create it with the code below.

**Code:**
```csharp
using System.Text.Json;
using TokenUsageTracker;

internal static class ClaudeTranscriptChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ClaudeTranscriptChecks-" + Guid.NewGuid());
        string project = Path.Combine(folder, "projects", "C--work-app");
        Directory.CreateDirectory(Path.Combine(project, "s1", "subagents"));
        var now = DateTimeOffset.UtcNow;
        string Stamp(double hours) => now.AddHours(hours).ToString("O");
        object Usage(long input, long write, long read, long output) => new { input_tokens = input, cache_creation_input_tokens = write, cache_read_input_tokens = read, output_tokens = output };
        object Prompt(string session, string id, string text, double hours, bool agent = false) => new
        { type = "user", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isSidechain = agent, message = new { role = "user", content = text } };
        object Reply(string session, string id, string message, double hours, object usage, string text = "Done.", bool agent = false) => new
        {
            type = "assistant", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isSidechain = agent,
            message = new { id = message, model = "claude-test", content = new object[] { new { type = "tool_use", name = "Edit" }, new { type = "text", text } }, usage }
        };
        object LimitError(string session, string id, double hours) => new
        {
            type = "assistant", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isApiErrorMessage = true, error = "rate_limit",
            message = new { id = "synthetic-" + id, model = "<synthetic>", content = new[] { new { type = "text", text = "Claude usage limit reached. Your limit will reset at 3pm." } }, usage = Usage(0, 0, 0, 0) }
        };
        void Write(string path, params object[] lines) => File.WriteAllText(path, string.Join("\n", lines.Select(l => JsonSerializer.Serialize(l))) + "\n");
        try
        {
            string s1 = Path.Combine(project, "s1.jsonl");
            Write(s1,
                new { type = "user", sessionId = "s1", uuid = "meta", timestamp = Stamp(-1), isMeta = true, message = new { role = "user", content = "Caveat: local command output" } },
                Prompt("s1", "p1", "Fix the login bug", -1),
                Reply("s1", "r1a", "msg_1", -0.99, Usage(100, 50, 1000, 20)),
                Reply("s1", "r1b", "msg_1", -0.98, Usage(100, 50, 1000, 40)),
                new { type = "user", sessionId = "s1", uuid = "tool", timestamp = Stamp(-0.97), message = new { role = "user", content = new[] { new { type = "tool_result", content = "ok" } } } },
                Prompt("s1", "old", "An old prompt", -7),
                Reply("s1", "rold", "msg_old", -7, Usage(5000, 0, 0, 5000)));
            File.AppendAllText(s1, "{\"type\":\"assistant\",\"partial");
            Write(Path.Combine(project, "s1", "subagents", "agent-1.jsonl"),
                Prompt("s1", "ap1", "Check the fix", -0.95, agent: true),
                Reply("s1", "ar1", "msg_2", -0.94, Usage(10, 0, 0, 5), agent: true));
            var transcripts = new ClaudeTranscripts();
            var five = transcripts.ReadTasks(folder, new UsageWindow(40, 300, now.AddHours(2)), 300, now);
            var task = five.Tasks.SingleOrDefault();
            check(task?.Task.Title == "Fix the login bug" && task.Input == 1160 && task.Cached == 1000 && task.Output == 45,
                "Claude tasks group by prompt, count a streamed reply once and include linked subagent tokens");
            check(task?.EstimatedPoints == 40 && task.Task.Steps.Contains("Edit") && task.Task.Result == "Done." && task.Task.Status == "Replied",
                "Claude tasks carry the estimate, tool activity and recorded result");
            check(five.Note.Contains("Cache reads may count less"), "The Claude task note explains the cache-read caveat");
            check(transcripts.ReadTasks(folder, null, 10080, now).Tasks.Any(t => t.Task.Title == "An old prompt"),
                "Older prompts appear in the weekly lookback but not in the five-hour window");
            check(new ClaudeTranscripts().ReadTasks(Path.Combine(folder, "missing"), null, 300, now).Note.Contains("No local Claude Code transcripts"),
                "A machine without Claude transcripts gets an actionable empty state");

            string s2 = Path.Combine(project, "s2.jsonl"), s3 = Path.Combine(project, "s3.jsonl"), s4 = Path.Combine(project, "s4.jsonl");
            Write(s2, Prompt("s2", "p2", "Refactor the parser", -0.5), Reply("s2", "r2", "msg_3", -0.49, Usage(10, 0, 0, 1)), LimitError("s2", "e2", -0.48));
            Write(s3, Prompt("s3", "p3", "Earlier task", -0.5), LimitError("s3", "e3", -0.48), Prompt("s3", "p3b", "Try again later", -0.2));
            Write(s4, Prompt("s4", "p4", "Other failure", -0.5),
                new { type = "assistant", sessionId = "s4", uuid = "e4", timestamp = Stamp(-0.48), isApiErrorMessage = true, message = new { content = new[] { new { type = "text", text = "API Error: 500 overloaded" } } } });
            var found = transcripts.Interruptions(folder, now.AddDays(-1));
            check(found.Count == 1 && found[0] is { SessionId: "s2", ErrorId: "e2", Workspace: @"C:\work\app", Title: "Refactor the parser" },
                "A usage-limit error at the end of a session is an interruption; a later prompt or another API error is not");
            check(transcripts.ReadTasks(folder, null, 300, now).Tasks.Any(t => t.Task.Title == "Refactor the parser" && t.Task.Status == "Interrupted by usage limit"),
                "The task list marks a task stopped by a usage limit");
            File.Delete(s2);
            check(transcripts.Interruptions(folder, now.AddDays(-1)).Count == 0, "Deleted transcripts leave no cached interruption");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }

        string statePath = Path.Combine(Path.GetTempPath(), "ClaudeResumeChecks-" + Guid.NewGuid() + ".json");
        try
        {
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            var interruption = new ClaudeInterruption("0b7d3e1c-1111-2222-3333-444455556666", "e1", t0.AddMinutes(1), @"C:\work\it's", "Fix the login bug");
            var full = new UsageSnapshot(t0, "Claude", new(100, 300, t0.AddHours(2)), new(60, 10080, t0.AddDays(3)));
            var notices = new ClaudeResumeNotices(statePath);
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(3)).Count == 0, "Claude notices stay off until enabled");
            notices.Configure(true, t0);
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(1)).Count == 0, "No Claude notice before the full window resets");
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(3)).Count == 1 && notices.Ready.Count == 1,
                "A Claude notice is due once the full window's reset has passed");
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(4)).Count == 0, "Each interruption is announced only once");
            check(new ClaudeResumeNotices(statePath).Tick(new[] { interruption }, full, t0.AddHours(4)).Count == 0,
                "Announced interruptions survive a widget restart");
            notices.Dismiss("e1");
            check(notices.Ready.Count == 0, "Dismissing removes a session from the resume list");
            check(notices.Tick(Array.Empty<ClaudeInterruption>(), full, t0.AddHours(5)).Count == 0 && notices.Ready.Count == 0,
                "A later prompt in the session cancels its notice");
            var stale = new UsageSnapshot(t0, "Claude", new(97, 300, t0.AddHours(2)), new(60, 10080, t0.AddDays(3)));
            check(!ClaudeResumeNotices.CanContinue(interruption, stale, t0.AddHours(3)),
                "With no full window known, a countdown alone never announces a session");
            var fresh = stale with { Timestamp = t0.AddMinutes(30), Primary = new(30, 300, t0.AddHours(6)) };
            check(ClaudeResumeNotices.CanContinue(interruption, fresh, t0.AddMinutes(31)),
                "A reading received after the error that shows room announces the session");
            var older = interruption with { ErrorId = "old", At = t0.AddDays(-1) };
            check(notices.Tick(new[] { older }, fresh, t0.AddHours(6)).Count == 0, "Interruptions from before enabling are ignored");
            check(ClaudeResumeNotices.Command(interruption) == @"Set-Location -LiteralPath 'C:\work\it''s'; claude --resume 0b7d3e1c-1111-2222-3333-444455556666"
                && ClaudeResumeNotices.Command(interruption with { SessionId = "x; Remove-Item" }) is null,
                "The copied resume command quotes the folder and rejects unexpected session IDs");
            check(ClaudeResumeNotices.Message(interruption).Contains("in it's") && ClaudeResumeNotices.Message(interruption).Contains("Fix the login bug"),
                "The notice names the task and its folder");
            notices.Configure(false, t0);
            check(!new ClaudeResumeNotices(statePath).Enabled, "Turning notices off clears their state");
        }
        finally { if (File.Exists(statePath)) File.Delete(statePath); }
    }
}
```

**Verify:** `dotnet build tests/UsageChecks/UsageChecks.csproj --configuration Release`

**Checkpoint:** Build succeeds.

#### Task 10: Run the new usage checks

**Goal:** Call `BothChecks.Run` and `ClaudeTranscriptChecks.Run` after the taskbar checks.

**File:** tests/UsageChecks/Program.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System.Text.Json;
using TokenUsageTracker;

if (args is ["--fake-codex", var scenario]) { ResumeChecks.Fake(scenario); return; }
if (args is ["--claude-statusline"])
{
    Console.InputEncoding = Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    Environment.ExitCode = ClaudeUsage.RunStatusLine(Console.In, Console.Out, Console.Error, ClaudeUsage.ConfigFolder);
    return;
}

try
{
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

var resizedSettings = JsonSerializer.Deserialize<WidgetSettings>("""
    {"FullWidth":350,"FullHeight":400,"CompactWidth":240,"CompactHeight":120,"CompactMode":true}
    """)!;
var savedSettings = JsonSerializer.SerializeToElement(resizedSettings);
Check(savedSettings.TryGetProperty("CompactWidth", out var compactWidth) && compactWidth.GetDouble() == 240
    && savedSettings.TryGetProperty("CompactHeight", out var compactHeight) && compactHeight.GetDouble() == 120
    && savedSettings.TryGetProperty("FullWidth", out var fullWidth) && fullWidth.GetDouble() == 350
    && savedSettings.TryGetProperty("FullHeight", out var fullHeight) && fullHeight.GetDouble() == 400,
    "Resized dimensions survive a settings round trip independently for both layouts");
Check(resizedSettings.WindowSize(true) == (240, 120) && resizedSettings.WindowSize(false) == (350, 400),
    "Switching layouts restores each layout's saved dimensions");
var oldSettings = JsonSerializer.Deserialize<WidgetSettings>("{\"CompactMode\":true}")!;
Check(oldSettings.WindowSize(true) == (432, 162) && oldSettings.WindowSize(false) == (316, 384),
    "Older settings without dimensions retain the original default sizes");
var invalidSize = new WidgetSettings { FullWidth = double.NaN, FullHeight = -1, CompactWidth = 0, CompactHeight = double.PositiveInfinity };
Check(invalidSize.WindowSize(true) == (432, 162) && invalidSize.WindowSize(false) == (316, 384),
    "Invalid saved dimensions fall back to usable window sizes");

string Event(string timestamp, double percent = 6, string? id = "codex", bool primary = true) => JsonSerializer.Serialize(new
{
    timestamp, type = "event_msg", payload = new
    {
        type = "token_count", rate_limits = new
        {
            limit_id = id, plan_type = "plus",
            primary = primary ? new { used_percent = percent, window_minutes = 300, resets_at = 1792503113L } : null,
            secondary = new { used_percent = 1.0, window_minutes = 10080, resets_at = 1793089913L }
        }
    }
});

var snapshot = UsageReader.ParseLine(Event("2026-10-20T08:54:09Z"))!;
Check(snapshot.Primary?.Remaining == 94 && snapshot.Secondary?.Remaining == 99, "Plan remaining uses the reported percentage, independent of token totals");
Check(snapshot.Primary?.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792503113), "Reset timestamps use Unix seconds");
Check(snapshot.Primary?.Label == "5-hour window" && snapshot.Secondary?.Label == "Weekly window", "Window lengths label themselves");
Check(snapshot.Primary!.IsExpired(DateTimeOffset.FromUnixTimeSeconds(1792503113)), "A past reset is unknown rather than assumed to be 100% available");
Check(UsageReader.ParseLine(Event("2026-10-20T08:54:09Z", primary: false))?.Secondary?.Remaining == 99, "Plans with only a weekly window work");
Check(UsageReader.ParseLine(Event("2026-10-20T08:54:09Z", id: null)) is not null, "Older records without a limit ID work");
Check(UsageReader.ParseLine(Event("2026-10-20T08:54:09Z", id: "codex_other_model")) is null, "Model-specific allowances cannot replace the general plan allowance");
Check(UsageReader.ParseLine(Event("invalid")) is null, "Records without a valid timestamp are ignored");
Check(UsageReader.ParseLine(Event("2026-10-20T08:54:09Z", -1))?.Primary is null
    && UsageReader.ParseLine(Event("2026-10-20T08:54:09Z", 101))?.Primary is null, "Out-of-range percentages are not shown as a real balance");
foreach (string line in new[] { "{\"rate_limits\":", "[]", "null", "{\"type\":\"event_msg\",\"payload\":[]}", Event("2026-10-20T08:54:09Z").Replace("\"used_percent\":6", "\"used_percent\":\"six\"") })
    _ = UsageReader.ParseLine(line);
Check(true, "Partial writes, unrelated messages, and invalid field types do not crash the reader");

var paceAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
UsageWindow Pace(double used, int? minutes = 300, double resetInMinutes = 150) => new(used, minutes, paceAt.AddMinutes(resetInMinutes));
Check(Pace(60).RunsOutAt(paceAt) == paceAt.AddMinutes(100), "Average pace since the window opened forecasts when it runs out before reset");
Check(Pace(30).RunsOutAt(paceAt) is null, "No forecast when the average pace lasts until reset");
Check(Pace(double.Epsilon).RunsOutAt(paceAt) is null, "Tiny valid usage percentages cannot overflow the forecast");
Check(Pace(5, resetInMinutes: 290).RunsOutAt(paceAt) is null, "No forecast this early in a window");
Check(Pace(0).RunsOutAt(paceAt) is null && Pace(100).RunsOutAt(paceAt) is null, "Unused and exhausted windows get no forecast");
Check(Pace(60, minutes: null).RunsOutAt(paceAt) is null && Pace(60, resetInMinutes: -1).RunsOutAt(paceAt) is null
    && new UsageWindow(60, 300, null).RunsOutAt(paceAt) is null, "Missing window lengths, missing resets and passed resets get no forecast");
Check(Pace(60, resetInMinutes: 400).RunsOutAt(paceAt) is null, "A reset further away than the window length is not extrapolated");

string root = Path.Combine(Path.GetTempPath(), "CodexUsageChecks-" + Guid.NewGuid());
try
{
    var reader = new UsageReader();
    Check(reader.Read(root).Snapshot is null && reader.Read(root).Error is not null, "A machine without Codex sessions gets an actionable empty state");
    string dir = Path.Combine(root, "sessions", "2026", "10", "20");
    Directory.CreateDirectory(dir);
    string older = Path.Combine(dir, "older.jsonl"), newer = Path.Combine(dir, "newer.jsonl");
    File.WriteAllText(older, Event("2026-10-20T08:00:00Z", 80) + "\n");
    File.WriteAllText(newer, Event("2026-10-20T09:00:00Z", 25) + "\n");
    File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(1));
    Check(reader.Read(root).Snapshot?.Primary?.Remaining == 75, "Newest event wins across sessions even when an older file has a newer modification time");
    File.AppendAllText(newer, Event("2026-10-20T09:01:00Z", 30) + "\n{\"rate_limits\":");
    using (var writer = new FileStream(newer, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        Check(reader.Read(root).Snapshot?.Primary?.Remaining == 70, "Appends update the cache while Codex holds the file open; incomplete last lines are ignored");
    File.WriteAllText(newer, Event("2026-10-20T09:02:00Z", 40) + "\n");
    Check(reader.Read(root).Snapshot?.Primary?.Remaining == 60, "Truncated or replaced logs invalidate the cached reading");
    File.AppendAllText(newer, new string('x', 200_000) + "\n");
    Check(new UsageReader().Read(root).Snapshot?.Primary?.Remaining == 60, "Tail scanning reaches a usage record behind a large tool-output line");
    File.Delete(newer);
    Check(reader.Read(root).Snapshot?.Primary?.Remaining == 20, "Deleted sessions leave no cached phantom balance");
    Check(reader.Read(Path.Combine(root, "other-profile")).Snapshot is null, "Changing the data folder does not show the previous profile's data");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

await ResumeChecks.RunAsync(Check);
await ClaudeChecks.RunAsync(Check);
TaskHistoryChecks.Run(Check);
TaskbarChecks.Run(Check);
BothChecks.Run(Check);
ClaudeTranscriptChecks.Run(Check);

if (args.Contains("--live-rpc"))
{
    string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var client = await CodexClient.ConnectAsync(home, false, timeout.Token);
    var live = await client.ReadUsageAsync(timeout.Token);
    Check(live.Snapshot.Primary is not null, "Real read-only Codex account RPC returns plan usage");
    Console.WriteLine($"Live RPC: primary={live.Snapshot.Primary?.Remaining}%, weekly={live.Snapshot.Secondary?.Remaining}%, included-usage-allowed={live.OrdinaryUsageAllowed}");
}

if (args.Contains("--live-history"))
{
    string home = WidgetSettings.Load().DataFolder;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var history = new TaskHistory();
    var ranking = history.Read(home, null, 10080, DateTimeOffset.UtcNow);
    Console.WriteLine($"History diagnostics: tasks={ranking.Tasks.Count}, with tokens={ranking.Tasks.Count(t => t.Total > 0)}, with prompts={ranking.Tasks.Count(t => t.Task.Prompt.Length > 0)}; {ranking.Note}");
    Check(ranking.Tasks.Count > 0 && ranking.Tasks.Any(t => t.Total > 0 && t.Task.Prompt.Length > 0), "Real local history contains named tasks with token records");
    Console.WriteLine($"Local history: {ranking.Tasks.Count} tasks; scan={watch.ElapsedMilliseconds}ms");
    watch.Restart(); history.Read(home, null, 300, DateTimeOffset.UtcNow);
    Console.WriteLine($"Cached five-hour scan={watch.ElapsedMilliseconds}ms");
}

if (args.Contains("--live"))
{
    string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var liveReader = new UsageReader();
    var live = liveReader.Read(home);
    Check(live.Snapshot is not null, "Live local Codex data contains a readable plan snapshot");
    Console.WriteLine($"Live: plan={live.Snapshot!.Plan}, primary={live.Snapshot.Primary?.Remaining}%, weekly={live.Snapshot.Secondary?.Remaining}%, recorded={live.Snapshot.Timestamp:O}, scan={clock.ElapsedMilliseconds}ms");
    clock.Restart(); liveReader.Read(home);
    Console.WriteLine($"Cached scan: {clock.ElapsedMilliseconds}ms");
}
Console.WriteLine($"All {checks} checks passed.");
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    Environment.ExitCode = 1;
}
```

**Verify:** `dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release`

**Checkpoint:** Output ends with `All 172 checks passed.` Stop and fix before continuing if any check fails.

#### Task 11: Tray badges per tool

**Goal:** Fill badges from `UsageColors.Bar` and draw Claude's badge as a circle in Both mode, so the two tray icons are told apart by shape.

**File:** src/TokenUsageTracker/TrayBadge.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TokenUsageTracker;

/// <summary>Draws the remaining percentage as a small icon for the notification area and the taskbar overlay.</summary>
internal sealed class TrayBadge : IDisposable
{
    private readonly IntPtr handle;
    public Icon Icon { get; }
    public ImageSource Image { get; }

    private TrayBadge(IntPtr handle)
    {
        this.handle = handle;
        Icon = Icon.FromHandle(handle);
        var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        Image = image;
    }

    /// <summary>
    /// Codex, and either tool on its own, uses a rounded square in the warning colours. In Both mode Claude
    /// uses a circle in its own shades, because Windows does not let apps choose the order of tray icons.
    /// </summary>
    public static TrayBadge Render(string text, UsageLevel level, int size, string provider = "Codex", bool both = false)
    {
        size = Math.Max(16, size);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(System.Drawing.Color.Transparent);
            // Same colours as the widget's bars.
            var rgb = UsageColors.Bar(level, provider, both);
            var fill = System.Drawing.Color.FromArgb(rgb.R, rgb.G, rgb.B);
            using (var path = RoundedSquare(size, both && provider == "Claude" ? size / 2f : size / 4f))
            using (var brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap
            };
            // Shrink the font until the number fits; "100" needs a smaller size than two digits.
            float em = size * 0.8f;
            var font = new Font("Segoe UI", em, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            while (em > 5 && graphics.MeasureString(text, font, PointF.Empty, format).Width > size - 1)
            {
                font.Dispose();
                em -= 0.5f;
                font = new Font("Segoe UI", em, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using (font)
            using (var ink = new SolidBrush(System.Drawing.Color.FromArgb(23, 30, 38)))
                graphics.DrawString(text, font, ink, new RectangleF(0, 0, size, size), format);
        }
        return new TrayBadge(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedSquare(float size, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Icon.FromHandle does not own the handle, so release it here after the icon is no longer shown.</summary>
    public void Dispose()
    {
        Icon.Dispose();
        WindowsShell.DestroyIcon(handle);
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds. Existing callers keep working through the optional parameters.

#### Task 12: Window layout: a Claude block

**Goal:** Name the plan label, add the `ClaudeBlock` (heading row and two side-by-side cells) at row 3, and move the task buttons and footer down one row.

**File:** src/TokenUsageTracker/MainWindow.xaml

Replace the whole file with the code below. Departure from the design: a second named block replaces the proposed `UsageSection` user control, because the window checks find elements by name and `FindName` cannot see inside a user control's name scope.

**Code:**
```xml
<Window x:Class="TokenUsageTracker.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Codex Usage" Width="316" Height="384" WindowStyle="None"
        ResizeMode="CanResize" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" FontFamily="Segoe UI" Foreground="#EDF2F6"
        UseLayoutRounding="True" PreviewKeyDown="OnKeyDown">
  <Window.Resources>
    <Style TargetType="Button">
      <Setter Property="Background" Value="Transparent" />
      <Setter Property="Foreground" Value="#A9B4C0" />
      <Setter Property="BorderThickness" Value="0" />
      <Setter Property="Width" Value="28" /><Setter Property="Height" Value="28" />
      <Setter Property="Cursor" Value="Hand" /><Setter Property="FontSize" Value="16" />
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="6">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#29323C" /></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="Background" Value="#364452" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style TargetType="ProgressBar">
      <Setter Property="Height" Value="5" /><Setter Property="Maximum" Value="100" />
      <Setter Property="Background" Value="#2A333D" /><Setter Property="Foreground" Value="#BEF078" />
      <Setter Property="Template"><Setter.Value>
        <ControlTemplate TargetType="ProgressBar">
          <Grid><Border Background="{TemplateBinding Background}" CornerRadius="2.5" />
            <Border x:Name="PART_Track">
              <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}" CornerRadius="2.5" HorizontalAlignment="Left" />
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
  </Window.Resources>
  <Grid>
  <Border x:Name="Card" Margin="9" Padding="19,14,19,16" CornerRadius="18" Background="#171E26" BorderBrush="#36414C" BorderThickness="1">
    <Border.Effect><DropShadowEffect BlurRadius="14" ShadowDepth="3" Opacity="0.3" /></Border.Effect>
      <Grid x:Name="LayoutGrid">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="*" /><RowDefinition Height="Auto" />
      </Grid.RowDefinitions>
      <Grid x:Name="Header" MouseLeftButtonDown="DragWidget" Background="Transparent" Cursor="SizeAll">
        <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
          <Border Width="25" Height="25" Background="#BEF078" CornerRadius="7" Margin="0,0,9,0">
            <TextBlock Text="&gt;_" Foreground="#171E26" FontFamily="Consolas" FontWeight="Bold" FontSize="12" VerticalAlignment="Center" HorizontalAlignment="Center" />
          </Border>
          <TextBlock x:Name="ProviderText" Text="Codex" FontWeight="SemiBold" FontSize="15" VerticalAlignment="Center" />
        </StackPanel>
        <CheckBox x:Name="AutoResumeToggle" Grid.Column="1" Content="Auto-resume" VerticalAlignment="Center" HorizontalAlignment="Right"
                  Margin="4,0,4,0" FontSize="9" Foreground="#EDF2F6" Cursor="Hand"
                  Checked="AutoResumeChanged" Unchecked="AutoResumeChanged"
                  ToolTip="Continue tasks interrupted by a usage limit when included usage returns. Select Desktop or CLI in Options."
                  AutomationProperties.Name="Automatically resume tasks after a Codex usage limit" />
        <StackPanel Grid.Column="2" Orientation="Horizontal" HorizontalAlignment="Right">
          <Button x:Name="ModeButton" Content="↕" Click="ToggleCompact" ToolTip="Switch to compact mode" AutomationProperties.Name="Switch to compact mode" />
          <Button x:Name="PinButton" Content="⌖" Click="TogglePin" ToolTip="Always on top" AutomationProperties.Name="Toggle always on top" />
          <Button Content="⋯" Click="OpenMenu" ToolTip="Options" AutomationProperties.Name="Widget options" />
        </StackPanel>
      </Grid>
      <Grid x:Name="PlanRow" Grid.Row="1" Margin="0,19,0,0">
        <TextBlock x:Name="PlanLabel" Text="PLAN REMAINING" Foreground="#9BA9B7" FontSize="10" FontWeight="SemiBold" />
        <TextBlock x:Name="PlanText" Text="LOCAL" Foreground="#BEF078" FontSize="10" HorizontalAlignment="Right" />
      </Grid>
      <Grid x:Name="UsageGrid" Grid.Row="2">
        <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="Auto" /></Grid.RowDefinitions>
        <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="0" /></Grid.ColumnDefinitions>
      <StackPanel x:Name="PrimaryPanel" Grid.Row="0" Margin="0,9,0,0">
        <Grid x:Name="PrimaryHeading">
          <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="0" /></Grid.RowDefinitions>
          <TextBlock x:Name="PrimaryLabel" Text="5-hour window" FontSize="12" Foreground="#ACB8C5" VerticalAlignment="Center" />
          <TextBlock x:Name="PrimaryValue" Text="—" FontSize="35" FontWeight="SemiBold" HorizontalAlignment="Right" />
        </Grid>
        <ProgressBar x:Name="PrimaryBar" Margin="0,5,0,0" AutomationProperties.Name="Primary window remaining percent" />
        <TextBlock x:Name="PrimaryReset" Text="Waiting for Codex" FontSize="11" Foreground="#8F9FAF" Margin="0,7,0,0" TextTrimming="CharacterEllipsis" />
      </StackPanel>
      <StackPanel x:Name="SecondaryPanel" Grid.Row="1" Margin="0,16,0,0">
        <Grid x:Name="SecondaryHeading">
          <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="0" /></Grid.RowDefinitions>
          <TextBlock x:Name="SecondaryLabel" Text="Weekly window" FontSize="12" Foreground="#ACB8C5" VerticalAlignment="Center" />
          <TextBlock x:Name="SecondaryValue" Text="—" FontSize="25" FontWeight="SemiBold" HorizontalAlignment="Right" />
        </Grid>
        <ProgressBar x:Name="SecondaryBar" Margin="0,7,0,0" AutomationProperties.Name="Secondary window remaining percent" />
        <TextBlock x:Name="SecondaryReset" Text="Waiting for Codex" FontSize="11" Foreground="#8F9FAF" Margin="0,7,0,0" TextTrimming="CharacterEllipsis" />
      </StackPanel>
      </Grid>
      <StackPanel x:Name="ClaudeBlock" Grid.Row="3" Visibility="Collapsed">
        <Grid x:Name="ClaudePlanRow" Margin="0,14,0,0">
          <TextBlock Text="CLAUDE" Foreground="#9BA9B7" FontSize="10" FontWeight="SemiBold" />
          <TextBlock x:Name="ClaudePlanText" Text="CLAUDE" Foreground="#6FD8C0" FontSize="10" HorizontalAlignment="Right" />
        </Grid>
        <Grid x:Name="ClaudeGrid">
          <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
          <StackPanel x:Name="ClaudePrimaryPanel" Margin="0,3,8,0">
            <Grid x:Name="ClaudePrimaryHeading">
              <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="0" /></Grid.RowDefinitions>
              <TextBlock x:Name="ClaudePrimaryLabel" Text="5-hour window" FontSize="12" Foreground="#ACB8C5" VerticalAlignment="Center" />
              <TextBlock x:Name="ClaudePrimaryValue" Text="—" FontSize="22" FontWeight="SemiBold" HorizontalAlignment="Right" />
            </Grid>
            <ProgressBar x:Name="ClaudePrimaryBar" Margin="0,5,0,0" AutomationProperties.Name="Claude five-hour window remaining percent" />
            <TextBlock x:Name="ClaudePrimaryReset" Text="Waiting for Claude" FontSize="11" Foreground="#8F9FAF" Margin="0,7,0,0" TextTrimming="CharacterEllipsis" />
          </StackPanel>
          <StackPanel x:Name="ClaudeSecondaryPanel" Grid.Column="1" Margin="8,3,0,0">
            <Grid x:Name="ClaudeSecondaryHeading">
              <Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="0" /></Grid.RowDefinitions>
              <TextBlock x:Name="ClaudeSecondaryLabel" Text="Weekly window" FontSize="12" Foreground="#ACB8C5" VerticalAlignment="Center" />
              <TextBlock x:Name="ClaudeSecondaryValue" Text="—" FontSize="22" FontWeight="SemiBold" HorizontalAlignment="Right" />
            </Grid>
            <ProgressBar x:Name="ClaudeSecondaryBar" Margin="0,5,0,0" AutomationProperties.Name="Claude weekly window remaining percent" />
            <TextBlock x:Name="ClaudeSecondaryReset" Text="Waiting for Claude" FontSize="11" Foreground="#8F9FAF" Margin="0,7,0,0" TextTrimming="CharacterEllipsis" />
          </StackPanel>
        </Grid>
      </StackPanel>
      <Grid x:Name="TaskLists" Grid.Row="4" Margin="0,9,0,0">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
        <Button x:Name="FiveHourTasks" Content="5h tasks ▾" Width="Auto" Height="26" FontSize="11" HorizontalAlignment="Stretch" Click="OpenTasks" AutomationProperties.Name="Show tasks in the five-hour window" ToolTip="Local Codex and Claude Code tasks ranked by tokens. Hover for a summary; click for details."/>
        <Button x:Name="WeeklyTasks" Grid.Column="1" Content="Weekly tasks ▾" Width="Auto" Height="26" FontSize="11" HorizontalAlignment="Stretch" Click="OpenTasks" AutomationProperties.Name="Show tasks in the weekly window" ToolTip="Local tasks ranked by tokens. Percentage points are estimates."/>
      </Grid>
      <Border x:Name="Footer" Grid.Row="6" BorderBrush="#303A45" BorderThickness="0,1,0,0" Padding="0,11,0,0" Margin="0,14,0,0">
        <Grid>
          <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="28" /></Grid.ColumnDefinitions>
          <StackPanel VerticalAlignment="Center">
            <TextBlock x:Name="StatusText" Text="Looking for usage…" FontSize="10" Foreground="#9BA9B7" TextTrimming="CharacterEllipsis" />
            <TextBlock x:Name="SourceText" Text="from local Codex activity" FontSize="10" Foreground="#718292" Margin="0,3,0,0" />
          </StackPanel>
          <Button x:Name="RefreshButton" Grid.Column="1" Content="↻" Click="RefreshClicked" ToolTip="Refresh live usage (F5)" AutomationProperties.Name="Refresh Codex usage" />
        </Grid>
      </Border>
    </Grid>
  </Border>
  <Thumb x:Name="ResizeHandle" Width="20" Height="20" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,8,8"
         Cursor="SizeNWSE" Focusable="True" IsTabStop="True" DragDelta="ResizeDragged" DragCompleted="ResizeCompleted"
         KeyDown="ResizeKeyDown" ToolTip="Drag to resize. Focus here and use arrow keys to resize."
         AutomationProperties.Name="Resize widget">
    <Thumb.Template>
      <ControlTemplate TargetType="Thumb">
        <Border x:Name="Grip" Background="Transparent" CornerRadius="3">
          <Path Data="M 5,15 L 15,5 M 10,15 L 15,10" Stroke="#9BA9B7" StrokeThickness="1.3" />
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Grip" Property="Background" Value="#364452" /></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Thumb.Template>
  </Thumb>
  </Grid>
</Window>
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds; the new elements are collapsed until Task 14 uses them.

#### Task 13: Task lists for both tools

**Goal:** Open one menu with a Codex group and/or a Claude Code group, each estimated against its own tool's current window.

**File:** src/TokenUsageTracker/MainWindow.Tasks.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TokenUsageTracker;

public partial class MainWindow
{
    private readonly TaskHistory history = new();
    private readonly ClaudeTranscripts transcripts = new();
    private bool historyBusy;

    private async void OpenTasks(object sender, RoutedEventArgs e)
    {
        if (historyBusy) return;
        historyBusy = true;
        var button = (Button)sender;
        object label = button.Content;
        button.Content = "Loading…";
        string mode = settings.Provider, codexFolder = settings.DataFolder, claudeFolder = ClaudeUsage.ConfigFolder;
        bool weekly = button == WeeklyTasks;
        int fallback = weekly ? 10080 : 300;
        UsageWindow? Window(string tool) => latest.TryGetValue(tool, out var r) ? weekly ? r.Snapshot?.Secondary : r.Snapshot?.Primary : null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var groups = new List<(string Name, TaskRanking Ranking)>();
            if (settings.ShowsCodex)
            {
                var window = Window("Codex");
                groups.Add(("Codex", await Task.Run(() => history.Read(codexFolder, window, fallback, now))));
            }
            if (settings.ShowsClaude)
            {
                var window = Window("Claude");
                groups.Add(("Claude Code", await Task.Run(() => transcripts.ReadTasks(claudeFolder, window, fallback, now))));
            }
            if (settings.Provider != mode || settings.DataFolder != codexFolder || lifetime.IsCancellationRequested) return;
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 480, MaxWidth = 380 };
            foreach (var (name, ranking) in groups)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                string heading = (groups.Count > 1 ? name.ToUpperInvariant() + "\n" : "") + ranking.Note + "\nHighest token count first.\n" + TaskHistory.EstimateNote;
                menu.Items.Add(new MenuItem { Header = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, MaxWidth = 330, FontSize = 11 }, IsEnabled = false });
                menu.Items.Add(new Separator());
                if (ranking.Tasks.Count == 0) menu.Items.Add(new MenuItem { Header = "No locally recorded tasks in this window", IsEnabled = false });
                foreach (var task in ranking.Tasks)
                {
                    var row = new StackPanel { MaxWidth = 330 };
                    row.Children.Add(new TextBlock { Text = task.Task.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
                    row.Children.Add(new TextBlock { Text = task.Cost, FontSize = 11 });
                    var item = new MenuItem { Header = row, ToolTip = task.Hover };
                    item.Click += (_, _) => ShowTaskDetails(task);
                    menu.Items.Add(item);
                }
            }
            menu.IsOpen = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "Task history unavailable", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { historyBusy = false; button.Content = label; }
    }

    private void ShowTaskDetails(RankedTask task)
    {
        var details = new Window
        {
            Owner = this, Title = task.Task.Title, Width = Math.Min(720, SystemParameters.WorkArea.Width - 40), Height = Math.Min(650, SystemParameters.WorkArea.Height - 40),
            MinWidth = 280, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(23, 30, 38)), Foreground = Brushes.White,
            Content = new TextBox
            {
                Text = task.Details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(20), FontSize = 13
            }
        };
        details.Show();
    }
}
```

**Verify:** `grep -c "ClaudeTranscripts transcripts" src/TokenUsageTracker/MainWindow.Tasks.cs`

**Checkpoint:** Prints `1`. The app build only succeeds after Task 14, because this file uses the `latest` readings that Task 14 adds.

#### Task 14: Window: read, render and notify per tool

**Goal:** Read Codex and Claude independently and render each as soon as it arrives; lay out Both mode; colour text by level and bars by tool; show one tray number per tool and the lower tool on the taskbar; extend the menu; announce Claude sessions that can continue. `readLive`, `notices` and `notifyResume` are seams for the window checks.

**File:** src/TokenUsageTracker/MainWindow.xaml.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TokenUsageTracker;

public partial class MainWindow : Window
{
    private const string CodexDashboard = "https://chatgpt.com/codex/settings/usage";
    private const string ClaudeDashboard = "https://claude.ai/settings/usage";

    /// <summary>One tool's reading, ready to render. Status is the single-tool footer line; Short is its part of the Both-mode footer.</summary>
    private sealed record ProviderReading(string Provider, UsageSnapshot? Snapshot, string Plan, string Status, string Short, bool Warn,
        string Extra, bool ExtraWarn, bool ExtraNotable, string ExtraTip, string Details, string? CompactStatus);

    private sealed record Cell(Grid Heading, TextBlock Label, TextBlock Value, ProgressBar Bar, TextBlock Reset, string DefaultLabel);

    private readonly WidgetSettings settings = WidgetSettings.Load();
    private readonly UsageReader reader = new();
    private readonly ResumeController resume = new();
    private readonly ClaudeResumeNotices notices = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ProviderReading> latest = new();
    private readonly Cell[] mainCells, claudeCells;
    private LiveUsage? lastLive;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu = new();
    private readonly System.Drawing.Icon appIcon;
    private TrayBadge? badge;
    private TaskbarReading shownReading = TaskbarReading.Empty;
    private Forms.NotifyIcon? claudeTray;
    private TrayBadge? claudeBadge;
    private TaskbarReading shownClaudeReading = TaskbarReading.Empty;
    private string? balloonCommand;
    private bool layoutBoth;
    private bool reading;
    // Seams for the window checks: they must never start Codex or show real notifications.
    private Func<string, CancellationToken, Task<LiveUsage>> readLive = ReadLiveAsync;
    private Action<ClaudeInterruption> notifyResume;
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(190, 240, 120));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(249, 198, 110));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 132, 132));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(155, 169, 183));
    private static readonly Brush SourceDim = new SolidColorBrush(Color.FromRgb(113, 130, 146));
    private static readonly Dictionary<UsageColors.Rgb, Brush> LevelBrushes = new();

    public MainWindow()
    {
        InitializeComponent();
        mainCells = new[]
        {
            new Cell(PrimaryHeading, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, "5-hour window"),
            new Cell(SecondaryHeading, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, "Weekly window")
        };
        claudeCells = new[]
        {
            new Cell(ClaudePrimaryHeading, ClaudePrimaryLabel, ClaudePrimaryValue, ClaudePrimaryBar, ClaudePrimaryReset, "5-hour window"),
            new Cell(ClaudeSecondaryHeading, ClaudeSecondaryLabel, ClaudeSecondaryValue, ClaudeSecondaryBar, ClaudeSecondaryReset, "Weekly window")
        };
        notifyResume = ShowResumeBalloon;
        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information;
        trayMenu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(RestoreWidget));
        trayMenu.Items.Add("Refresh usage", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync()));
        trayMenu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));
        tray = NewTray("Codex Usage — double-click to show");
        TaskbarItemInfo = new TaskbarItemInfo();
        ShowInTaskbar = settings.ShowInTaskbar;
        AutoResumeToggle.IsChecked = settings.AutoResume;
        Topmost = settings.AlwaysOnTop;
        SetCompact(settings.CompactMode, save: false);
        UpdateProvider();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdatePin();
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            RestorePosition();
            ConfigureResume();
            timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            timer.Stop(); lifetime.Cancel(); resume.Dispose();
            tray.Dispose(); claudeTray?.Dispose(); badge?.Dispose(); claudeBadge?.Dispose(); appIcon.Dispose();
        };
        Closing += (_, _) => SaveSettings();
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    private Forms.NotifyIcon NewTray(string text)
    {
        var icon = new Forms.NotifyIcon { Text = text, Icon = appIcon, Visible = true, ContextMenuStrip = trayMenu };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWidget);
        icon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(CopyBalloonCommand);
        return icon;
    }

    private string[] ShownTools() => settings.Provider switch
    {
        "Both" => new[] { "Codex", "Claude" },
        "Claude" => new[] { "Claude" },
        _ => new[] { "Codex" }
    };

    private static async Task<LiveUsage> ReadLiveAsync(string folder, CancellationToken token)
    {
        using var client = await CodexClient.ConnectAsync(folder, desktop: false, token);
        return await client.ReadUsageAsync(token);
    }

    private async Task RefreshAsync()
    {
        if (reading) return;
        reading = true;
        string mode = settings.Provider;
        try
        {
            // Each tool renders as soon as its own read finishes, so a slow Codex read never holds back Claude.
            var reads = new List<Task>();
            if (settings.ShowsCodex) reads.Add(ShowWhenReadAsync(ReadCodexAsync(settings.DataFolder)));
            if (settings.ShowsClaude) reads.Add(ShowWhenReadAsync(ReadClaudeAsync(ClaudeUsage.ConfigFolder)));
            await Task.WhenAll(reads);
        }
        finally
        {
            reading = false;
            if (mode != settings.Provider && !lifetime.IsCancellationRequested) _ = RefreshAsync();
        }
    }

    private async Task ShowWhenReadAsync(Task<ProviderReading?> read)
    {
        if (await read is { } result && !lifetime.IsCancellationRequested) ShowReading(result);
    }

    private static bool IsStale(UsageSnapshot? snapshot, DateTimeOffset now) => snapshot is not null && now - snapshot.Timestamp > TimeSpan.FromMinutes(15);
    private static string PlanOf(UsageSnapshot? snapshot, string fallback) => snapshot?.Plan?.Replace('_', ' ').ToUpperInvariant() ?? fallback;

    private async Task<ProviderReading?> ReadCodexAsync(string folder)
    {
        bool Current() => settings.ShowsCodex && settings.DataFolder == folder && !lifetime.IsCancellationRequested;
        LiveUsage? live = null;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            live = await readLive(folder, timeout.Token);
            if (Current()) lastLive = live;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
        {
            error = e is OperationCanceledException ? "Live usage timed out. Check Codex sign-in and connection." : e.Message;
        }
        if (!Current()) return null;
        var result = live is not null ? new UsageResult(live.Snapshot, null)
            : lastLive is not null ? new UsageResult(lastLive.Snapshot, error)
            : await Task.Run(() => reader.Read(folder));
        if (!Current()) return null;
        var now = DateTimeOffset.Now;
        var snapshot = result.Snapshot;
        string source = live is not null ? "Live account usage" : lastLive is not null ? "Last live reading" : "Local snapshot";
        string shortSource = live is not null ? "live" : lastLive is not null ? "last live" : "local";
        string status = snapshot is null ? "Waiting for plan usage"
            : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
            : $"{source} · {Age(now - snapshot.Timestamp)} ago";
        string recovery;
        try
        {
            await resume.TickAsync(live, lifetime.Token);
            recovery = resume.Status;
            if (settings.AutoResume && settings.ResumeTarget == "Desktop" && !File.Exists(CodexClient.DesktopSocket(folder)))
                recovery = "Desktop connection unavailable: no shared control socket. CLI mode is available in Options.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { recovery = "Recovery paused: " + e.Message; }
        if (!Current()) return null;
        string? compact = settings.AutoResume
            ? recovery.StartsWith("Desktop connection unavailable", StringComparison.Ordinal) ? "Desktop connection unavailable" : $"{source} · {resume.PendingCount} pending"
            : null;
        return new("Codex", snapshot, PlanOf(snapshot, "LOCAL"), status,
            snapshot is null ? "Codex waiting" : $"Codex {shortSource} {Age(now - snapshot.Timestamp)} ago",
            error is not null || result.Error is not null || IsStale(snapshot, now),
            settings.AutoResume ? recovery : "Auto-resume off · refreshes every 15s", false, false, recovery,
            $"{source}\n{error ?? result.Error}\n{recovery}\n{folder}", compact);
    }

    private async Task<ProviderReading?> ReadClaudeAsync(string folder)
    {
        bool Current() => settings.ShowsClaude && ClaudeUsage.ConfigFolder == folder && !lifetime.IsCancellationRequested;
        var result = await Task.Run(() => ClaudeUsage.Read(folder));
        if (!Current()) return null;
        var now = DateTimeOffset.Now;
        var snapshot = result.Snapshot;
        const string source = "Claude Code snapshot";
        string status = snapshot is null ? "Waiting for Claude Code · see Options"
            : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
            : $"{source} · {Age(now - snapshot.Timestamp)} ago";
        bool needsSetup = await Task.Run(() => ClaudeUsage.GetStatusLineState(folder, Environment.ProcessPath!)) == ClaudeUsage.StatusLineState.OtherWidgetPath;
        if (!Current()) return null;
        if (notices.Enabled)
        {
            var since = notices.ScanSince(DateTimeOffset.UtcNow);
            var found = await Task.Run(() => transcripts.Interruptions(folder, since));
            if (!Current()) return null;
            foreach (var interruption in notices.Tick(found, snapshot, DateTimeOffset.UtcNow)) notifyResume(interruption);
        }
        string recovery = needsSetup ? "Claude status line points to an older widget location · Options → Set up Claude usage"
            : !notices.Enabled ? "Claude usage display only"
            : notices.Ready.Count > 0 ? $"{notices.Ready.Count} Claude session(s) can continue · Options → Claude sessions to resume"
            : "Notifies when a Claude session stopped by a usage limit can continue";
        var cache = snapshot?.Cache;
        string cacheNote = cache is null ? "" : $"\n{cache.Describe(now)}\nPrompt cache of the most recently updated Claude Code session.";
        return new("Claude", snapshot, PlanOf(snapshot, "CLAUDE"), status,
            snapshot is null ? "Claude waiting" : $"Claude snapshot {Age(now - snapshot.Timestamp)} ago",
            result.Error is not null || IsStale(snapshot, now),
            needsSetup ? "Claude status line needs setup · see Options" : cache?.Describe(now) ?? "From Claude Code · refreshes every 15s",
            needsSetup || cache?.ExpiresAt is { } cold && cold > now && cold - now <= TimeSpan.FromMinutes(2),
            needsSetup || cache is not null, recovery + cacheNote,
            $"{source}\n{result.Error}\n{recovery}{cacheNote}\n{folder}", null);
    }

    private void ShowReading(ProviderReading r)
    {
        if (!(r.Provider == "Claude" ? settings.ShowsClaude : settings.ShowsCodex)) return;
        latest[r.Provider] = r;
        var now = DateTimeOffset.Now;
        bool claudeBlock = r.Provider == "Claude" && settings.Both;
        var cells = claudeBlock ? claudeCells : mainCells;
        bool stale = IsStale(r.Snapshot, now);
        var readAt = r.Snapshot?.Timestamp ?? now;
        ShowWindow(r.Snapshot?.Primary, cells[0], r.Provider, now, readAt, stale);
        ShowWindow(r.Snapshot?.Secondary, cells[1], r.Provider, now, readAt, stale);
        (claudeBlock ? ClaudePlanText : PlanText).Text = r.Plan;
        ShowFooter();
        ShowOnTaskbar(now);
    }

    private void ShowFooter()
    {
        var shown = ShownTools().Select(t => latest.GetValueOrDefault(t)).OfType<ProviderReading>().ToList();
        if (shown.Count == 0) return;
        if (!settings.Both)
        {
            var r = shown[0];
            StatusText.Text = settings.CompactMode && r.CompactStatus is { } pending ? $"{r.Plan} · {pending}"
                : settings.CompactMode && r.Snapshot is not null ? $"{r.Plan} · {r.Status}" : r.Status;
            StatusText.Foreground = r.Warn ? Amber : Muted;
            SourceText.Text = r.Extra;
            SourceText.Foreground = r.ExtraWarn ? Amber : SourceDim;
            SourceText.ToolTip = r.ExtraTip;
            Card.ToolTip = StatusText.ToolTip = AutoResumeToggle.ToolTip = r.Details;
        }
        else
        {
            StatusText.Text = string.Join(" · ", shown.Select(r => r.Short));
            StatusText.Foreground = shown.Any(r => r.Warn) ? Amber : Muted;
            // A Claude setup warning or cache countdown is more urgent than the Codex auto-resume line.
            var extra = shown.FirstOrDefault(r => r.ExtraNotable) ?? shown[0];
            SourceText.Text = extra.Extra;
            SourceText.Foreground = extra.ExtraWarn ? Amber : SourceDim;
            SourceText.ToolTip = string.Join("\n\n", shown.Select(r => r.ExtraTip));
            Card.ToolTip = StatusText.ToolTip = AutoResumeToggle.ToolTip = string.Join("\n\n", shown.Select(r => $"{r.Provider}\n{r.Details}"));
        }
        SourceText.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip = null;
    }

    private static Brush BrushOf(UsageColors.Rgb color)
    {
        if (!LevelBrushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            LevelBrushes[color] = brush;
        }
        return brush;
    }

    private void ShowWindow(UsageWindow? w, Cell cell, string provider, DateTimeOffset now, DateTimeOffset readAt, bool stale = false)
    {
        // Compact Both mode has no section headings, so each label names its tool.
        string prefix = settings.Both && settings.CompactMode ? (provider == "Claude" ? "Claude " : "Codex ") : "";
        string Label(string text) => prefix.Length == 0 ? text : prefix + text.Replace(" window", "", StringComparison.Ordinal);
        if (w is null)
        {
            cell.Label.Text = Label(cell.DefaultLabel);
            cell.Value.Text = "—"; cell.Value.Foreground = Muted; cell.Bar.Value = 0;
            cell.Reset.Text = "No window reported"; cell.Reset.ToolTip = null;
            return;
        }
        cell.Label.Text = Label(w.Label);
        bool expired = w.IsExpired(now);
        var level = expired || stale ? UsageLevel.None : TaskbarReading.LevelOf(w.Remaining);
        cell.Value.Foreground = BrushOf(UsageColors.Text(level));
        cell.Bar.Foreground = BrushOf(UsageColors.Bar(level, provider, settings.Both));
        cell.Value.Text = expired ? "—" : w.Remaining.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        cell.Bar.Value = expired ? 0 : w.Remaining;
        var empty = expired ? null : w.RunsOutAt(readAt);
        if (empty <= now) empty = null; // A stale reading's projection that already passed says nothing useful.
        string forecast = empty is { } at ? " · empty ~" + (at - now < TimeSpan.FromDays(1) ? "" : at.ToLocalTime().ToString("ddd ", CultureInfo.CurrentCulture))
            + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "";
        cell.Reset.Text = expired ? "Reset passed · waiting for " + provider
            : w.ResetsAt is { } time ? "Resets in " + Countdown(time - now) + forecast : "Reset time unavailable";
        cell.Reset.ToolTip = w.ResetsAt?.ToLocalTime().ToString("f")
            + (empty is { } e ? $"\nAt the average pace since this window opened, it runs out around {e.ToLocalTime():f}." : "");
    }

    /// <summary>One tray number per shown tool, and taskbar progress and overlay for whichever tool has less room.</summary>
    private void ShowOnTaskbar(DateTimeOffset now)
    {
        var tools = ShownTools();
        UsageSnapshot? Snapshot(string tool) => latest.TryGetValue(tool, out var r) ? r.Snapshot : null;
        string Text(string tool) => TaskbarReading.TrayText(tool, Snapshot(tool), now, Snapshot(tool) is { } s ? Age(now - s.Timestamp) : "");
        UpdateTray(tray, ref badge, ref shownReading, tools[0], Snapshot(tools[0]), now);
        tray.Text = Text(tools[0]);
        if (claudeTray is not null)
        {
            UpdateTray(claudeTray, ref claudeBadge, ref shownClaudeReading, "Claude", Snapshot("Claude"), now);
            claudeTray.Text = Text("Claude");
        }
        var (provider, reading) = TaskbarReading.Lowest(tools.Select(t => (t, Snapshot(t))), now);
        TaskbarItemInfo.Description = string.Join("\n", tools.Select(Text));
        TaskbarItemInfo.ProgressState = reading.Level switch
        {
            UsageLevel.Normal => TaskbarItemProgressState.Normal,
            UsageLevel.Low => TaskbarItemProgressState.Paused,
            UsageLevel.Critical => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None
        };
        TaskbarItemInfo.ProgressValue = reading.Fraction;
        TaskbarItemInfo.Overlay = provider.Length == 0 ? null : provider == tools[0] ? badge?.Image : claudeBadge?.Image;
    }

    private void UpdateTray(Forms.NotifyIcon icon, ref TrayBadge? current, ref TaskbarReading shown, string tool, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var reading = TaskbarReading.From(snapshot, now);
        if (reading.Badge == shown.Badge && reading.Level == shown.Level) return;
        var previous = current;
        current = null;
        try
        {
            if (reading.Level != UsageLevel.None)
                current = TrayBadge.Render(reading.Badge, reading.Level, Forms.SystemInformation.SmallIconSize.Width, tool, settings.Both);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException) { current = null; }
        icon.Icon = current?.Icon ?? appIcon;
        shown = current is null ? TaskbarReading.Empty : reading;
        previous?.Dispose();
    }

    /// <summary>Badges change shape and colour with the mode, so a mode change starts the trays afresh.</summary>
    private void ResetTrays()
    {
        tray.Icon = appIcon;
        TaskbarItemInfo.Overlay = null;
        badge?.Dispose(); badge = null;
        shownReading = TaskbarReading.Empty;
        if (claudeTray is not null) claudeTray.Icon = appIcon;
        claudeBadge?.Dispose(); claudeBadge = null;
        shownClaudeReading = TaskbarReading.Empty;
        if (!settings.Both && claudeTray is not null) { claudeTray.Visible = false; claudeTray.Dispose(); claudeTray = null; }
        if (settings.Both && claudeTray is null) claudeTray = NewTray("Claude Usage — double-click to show");
    }

    private void ShowResumeBalloon(ClaudeInterruption interruption)
    {
        balloonCommand = ClaudeResumeNotices.Command(interruption);
        (claudeTray ?? tray).ShowBalloonTip(10_000, "Claude usage has reset", ClaudeResumeNotices.Message(interruption), Forms.ToolTipIcon.Info);
    }

    private void CopyBalloonCommand()
    {
        if (balloonCommand is { } command) CopyResumeCommand(command);
        RestoreWidget();
    }

    private void CopyResumeCommand(string command)
    {
        try { Clipboard.SetText(command); }
        catch (ExternalException e)
        { MessageBox.Show(this, $"Could not copy to the clipboard: {e.Message}\n\n{command}", "Resume Claude session", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string Countdown(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    private static string Age(TimeSpan span) => span.TotalSeconds < 60 ? "a moment"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : span.TotalDays < 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";

    private void DragWidget(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is ButtonBase) return;
        if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); SaveSettings(); }
    }

    private void RestorePosition()
    {
        Left = settings.Left ?? SystemParameters.WorkArea.Right - Width - 24;
        Top = settings.Top ?? SystemParameters.WorkArea.Bottom - Height - 24;
        // Screen working areas use physical pixels; convert the rendered window before checking overlap.
        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = new Rect(PointToScreen(new Point(0, 0)), new Size(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY));
        bool reachable = double.IsFinite(Left) && double.IsFinite(Top) && Array.Exists(Forms.Screen.AllScreens, s =>
        {
            var r = s.WorkingArea;
            var overlap = Rect.Intersect(bounds, new Rect(r.X, r.Y, r.Width, r.Height));
            return !overlap.IsEmpty && overlap.Width >= bounds.Width - 20 && overlap.Height >= bounds.Height - 20;
        });
        if (!reachable)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Bottom - Height - 24;
        }
    }

    private void SaveSettings()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        settings.Left = Left; settings.Top = Top; settings.AlwaysOnTop = Topmost;
        settings.RememberSize(settings.CompactMode, Width, Height);
        if (!settings.Save()) { StatusText.Text = "Could not save preferences"; StatusText.ToolTip = "Check LocalAppData write permissions."; }
    }

    internal void RestoreWidget() { Show(); WindowState = WindowState.Normal; RestorePosition(); Activate(); }
    private void UpdatePin() { PinButton.Foreground = Topmost ? Green : Muted; PinButton.ToolTip = Topmost ? "Always on top: on" : "Always on top: off"; }
    private void ToggleCompact(object sender, RoutedEventArgs e) => SetCompact(!settings.CompactMode, save: true);

    private void SetCompact(bool compact, bool save)
    {
        if (save) SaveSettings();
        settings.CompactMode = compact;
        bool both = layoutBoth = settings.Both;
        bool sideBySide = compact || both;
        MinWidth = both ? compact ? 300 : 380 : compact ? 240 : 280;
        MinHeight = both ? compact ? 150 : 370 : compact ? 120 : 384;
        MaxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        MaxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        var size = settings.WindowSize(compact);
        Width = Math.Clamp(size.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(size.Height, MinHeight, MaxHeight);
        Card.Margin = new Thickness(compact ? 6 : 9);
        Card.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(19, 14, 19, 16);
        PlanRow.Visibility = SourceText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ClaudeBlock.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        ClaudePlanRow.Visibility = both && !compact ? Visibility.Visible : Visibility.Collapsed;
        ClaudeBlock.Margin = new Thickness(0, both && compact ? 4 : 0, 0, 0);
        PrimaryPanel.Margin = new Thickness(0, sideBySide ? 3 : 9, sideBySide ? 8 : 0, 0);
        SecondaryPanel.Margin = new Thickness(sideBySide ? 8 : 0, sideBySide ? 3 : 16, 0, 0);
        ClaudePrimaryPanel.Margin = new Thickness(0, compact ? 3 : 6, 8, 0);
        ClaudeSecondaryPanel.Margin = new Thickness(8, compact ? 3 : 6, 0, 0);
        UsageGrid.RowDefinitions[1].Height = sideBySide ? new GridLength(0) : GridLength.Auto;
        UsageGrid.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(SecondaryPanel, sideBySide ? 0 : 1);
        Grid.SetColumn(SecondaryPanel, sideBySide ? 1 : 0);
        foreach (var cell in mainCells.Concat(claudeCells))
        {
            cell.Label.FontSize = compact ? 10 : both ? 11 : 12;
            cell.Reset.FontSize = compact ? 9 : both ? 10 : 11;
            cell.Reset.Margin = new Thickness(0, compact ? 3 : both ? 5 : 7, 0, 0);
            // Compact Both mode stays concise: reset lines move to full-size mode.
            cell.Reset.Visibility = both && compact ? Visibility.Collapsed : Visibility.Visible;
            cell.Bar.Height = compact ? 4 : 5;
        }
        PrimaryBar.Margin = both ? new Thickness(0, compact ? 3 : 5, 0, 0) : compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 5, 0, 0);
        SecondaryBar.Margin = both ? new Thickness(0, compact ? 3 : 5, 0, 0) : compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 7, 0, 0);
        ClaudePrimaryBar.Margin = ClaudeSecondaryBar.Margin = new Thickness(0, compact ? 3 : 5, 0, 0);
        Footer.Margin = new Thickness(0, compact ? 8 : 14, 0, 0);
        Footer.Padding = new Thickness(0, compact ? 6 : 11, 0, 0);
        ModeButton.ToolTip = compact ? "Switch to full-size mode" : "Switch to compact mode";
        AutomationProperties.SetName(ModeButton, ModeButton.ToolTip.ToString());
        ModeButton.Width = PinButton.Width = RefreshButton.Width = compact ? 26 : 28;
        ModeButton.Height = PinButton.Height = RefreshButton.Height = compact ? 24 : 28;
        UpdateResponsiveLayout();
        // Labels carry the tool name only in compact Both mode; redraw what is already known.
        foreach (var known in latest.Values.ToList()) ShowReading(known);
        if (save) SaveSettings();
    }

    private void UpdateResponsiveLayout()
    {
        bool compact = settings.CompactMode, both = settings.Both;
        TaskLists.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FiveHourTasks.IsEnabled = WeeklyTasks.IsEnabled = true;
        bool narrow = Width < (both ? compact ? 380 : 420 : compact ? 360 : 310);
        bool stacked = (compact || both) && narrow;
        AutoResumeToggle.Visibility = narrow || settings.Provider == "Claude" ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = compact && Height < (both ? 165 : 160) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var cell in mainCells.Concat(claudeCells))
        {
            cell.Heading.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
            Grid.SetRow(cell.Value, stacked ? 1 : 0);
            cell.Value.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        PrimaryValue.FontSize = both || stacked ? 22 : compact ? 26 : 35;
        SecondaryValue.FontSize = both || compact ? 22 : 25;
        ClaudePrimaryValue.FontSize = ClaudeSecondaryValue.FontSize = 22;
        Card.Padding = compact ? new Thickness(narrow ? 8 : 12, narrow ? 4 : 6, narrow ? 8 : 12, narrow ? 4 : 6) : new Thickness(19, 14, 19, 16);
    }

    private void ResizeWindow(double horizontal, double vertical)
    {
        Width = Math.Clamp(Width + horizontal, MinWidth, MaxWidth);
        Height = Math.Clamp(Height + vertical, MinHeight, MaxHeight);
    }

    private void ResizeDragged(object sender, DragDeltaEventArgs e) => ResizeWindow(e.HorizontalChange, e.VerticalChange);
    private void ResizeCompleted(object sender, DragCompletedEventArgs e) => SaveSettings();
    private void ResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        ResizeWindow(e.Key == Key.Left ? -10 : e.Key == Key.Right ? 10 : 0,
            e.Key == Key.Up ? -10 : e.Key == Key.Down ? 10 : 0);
        SaveSettings();
        e.Handled = true;
    }

    private void TogglePin(object sender, RoutedEventArgs e) { Topmost = !Topmost; UpdatePin(); SaveSettings(); }
    private void AutoResumeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        settings.AutoResume = AutoResumeToggle.IsChecked == true;
        ConfigureResume();
    }

    private void ConfigureResume()
    {
        try { resume.Configure(settings.ShowsCodex && settings.AutoResume, settings.ResumeTarget, settings.DataFolder, DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings.AutoResume = false; AutoResumeToggle.IsChecked = false;
            StatusText.Text = "Could not save recovery state"; StatusText.ToolTip = e.Message;
        }
        notices.Configure(settings.ShowsClaude && settings.ClaudeResumeNotice, DateTimeOffset.UtcNow);
        SaveSettings();
        _ = RefreshAsync();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenMenu(object sender, RoutedEventArgs e) => ShowMenu();

    private void UpdateProvider()
    {
        latest.Clear();
        bool both = settings.Both;
        Title = both ? "Token Usage" : settings.Provider + " Usage";
        ProviderText.Text = both ? "Codex + Claude" : settings.Provider;
        RefreshButton.ToolTip = both ? "Refresh Codex and reread Claude Code (F5)"
            : settings.Provider == "Claude" ? "Reread Claude Code snapshot (F5)" : "Refresh live usage (F5)";
        AutomationProperties.SetName(RefreshButton, RefreshButton.ToolTip.ToString());
        var now = DateTimeOffset.Now;
        foreach (var cell in mainCells) ShowWindow(null, cell, both ? "Codex" : settings.Provider, now, now);
        foreach (var cell in claudeCells) ShowWindow(null, cell, "Claude", now, now);
        PlanLabel.Text = both ? "CODEX" : "PLAN REMAINING";
        PlanText.Text = both ? "CODEX" : settings.Provider.ToUpperInvariant();
        ClaudePlanText.Text = "CLAUDE";
        ResetTrays();
        ShowOnTaskbar(now);
        StatusText.Text = "Waiting for " + (both ? "Codex and Claude Code" : settings.Provider) + " usage";
        StatusText.Foreground = Muted;
        SourceText.Text = "Refreshes every 15s"; SourceText.Foreground = SourceDim;
        Card.ToolTip = null;
        // Both mode has its own layout and sizes; a change between one tool and two re-applies them.
        if (layoutBoth != both) SetCompact(settings.CompactMode, save: false);
        else UpdateResponsiveLayout();
    }

    private void SetProvider(string provider)
    {
        if (settings.Provider == provider) return;
        SaveSettings(); // Remember the size of the layout being left.
        settings.Provider = provider;
        lastLive = null;
        UpdateProvider();
        ConfigureResume();
    }

    private void SetupClaude()
    {
        try
        {
            bool configured = ClaudeUsage.ConfigureStatusLine(ClaudeUsage.ConfigFolder, Environment.ProcessPath!);
            MessageBox.Show(this, configured
                ? "Claude Code usage display is connected. Use Claude Code and wait for a response, then refresh the widget. Project or managed status-line settings may override this setup."
                : "Your custom Claude status line was left unchanged. Forward its original JSON input to TokenUsageTracker.exe --claude-statusline and discard the bridge output. See the Claude setup section in README.md.",
                "Claude usage setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, e.Message, "Claude setup failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); return item; }
        void Add(string text, Action action) => menu.Items.Add(Item(text, action));
        var providers = new MenuItem { Header = "Usage provider" };
        foreach (var (provider, label) in new[] { ("Codex", "Codex"), ("Claude", "Claude Code"), ("Both", "Codex and Claude Code") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.Provider == provider };
            item.Click += (_, _) => SetProvider(provider);
            providers.Items.Add(item);
        }
        menu.Items.Add(providers);
        Add("Refresh usage  (F5)", async () => await RefreshAsync());
        if (settings.ShowsCodex)
        {
            var autoResume = new MenuItem { Header = "Auto-resume", IsCheckable = true, IsChecked = settings.AutoResume };
            autoResume.Click += (_, _) => AutoResumeToggle.IsChecked = autoResume.IsChecked;
            menu.Items.Add(autoResume);
            var targets = new MenuItem { Header = "Resume target" };
            foreach (string target in new[] { "Desktop", "CLI" })
            {
                var radio = new RadioButton { Content = target == "Desktop" ? "Codex Desktop" : "Terminal / CLI", GroupName = "ResumeTarget", IsChecked = settings.ResumeTarget == target, Margin = new Thickness(6) };
                radio.Checked += (_, _) => { settings.ResumeTarget = target; ConfigureResume(); menu.IsOpen = false; };
                targets.Items.Add(new MenuItem { Header = radio, StaysOpenOnClick = true });
            }
            menu.Items.Add(targets);
        }
        if (settings.ShowsClaude)
        {
            Add("Set up Claude usage…", SetupClaude);
            var notice = new MenuItem { Header = "Notify when Claude can continue", IsCheckable = true, IsChecked = settings.ClaudeResumeNotice };
            notice.Click += (_, _) => { settings.ClaudeResumeNotice = notice.IsChecked; ConfigureResume(); };
            menu.Items.Add(notice);
            if (notices.Ready.Count > 0)
            {
                var sessions = new MenuItem { Header = "Claude sessions to resume" };
                foreach (var interruption in notices.Ready)
                {
                    // A TextBlock header keeps underscores in task titles from becoming access keys.
                    var entry = new MenuItem { Header = new TextBlock { Text = UsageTask.Short(interruption.Title, 50) }, ToolTip = ClaudeResumeNotices.Message(interruption) };
                    if (ClaudeResumeNotices.Command(interruption) is { } command) entry.Items.Add(Item("Copy resume command", () => CopyResumeCommand(command)));
                    entry.Items.Add(Item("Dismiss", () => notices.Dismiss(interruption.ErrorId)));
                    sessions.Items.Add(entry);
                }
                menu.Items.Add(sessions);
            }
        }
        Add(Topmost ? "Turn off always on top" : "Turn on always on top", () => TogglePin(this, new RoutedEventArgs()));
        if (settings.Both)
        {
            var dashboards = new MenuItem { Header = "Open usage dashboard" };
            dashboards.Items.Add(Item("Codex", () => OpenUrl(CodexDashboard)));
            dashboards.Items.Add(Item("Claude Code", () => OpenUrl(ClaudeDashboard)));
            menu.Items.Add(dashboards);
        }
        else Add("Open usage dashboard", () => OpenUrl(settings.Provider == "Claude" ? ClaudeDashboard : CodexDashboard));
        if (settings.ShowsCodex)
        {
            Add("Choose Codex data folder…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Codex folder containing sessions (usually .codex)" };
                if (dialog.ShowDialog(this) == true) { settings.CodexFolder = dialog.FolderName; lastLive = null; ConfigureResume(); }
            });
            Add("Use default Codex folder", () => { settings.CodexFolder = null; lastLive = null; ConfigureResume(); });
        }
        menu.Items.Add(WindowsMenu());
        menu.Items.Add(new Separator());
        Add("Hide to tray  (Esc)", Hide);
        Add("Quit", Close);
        menu.PlacementTarget = this; menu.IsOpen = true;
    }

    private MenuItem WindowsMenu()
    {
        string exe = Environment.ProcessPath!;
        var windows = new MenuItem { Header = "Taskbar and Start" };
        void Toggle(string header, bool isChecked, Action<bool> apply)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) =>
            {
                try { apply(item.IsChecked); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException)
                { MessageBox.Show(this, e.Message, "Taskbar and Start", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            windows.Items.Add(item);
        }
        Toggle("Show on taskbar", ShowInTaskbar, on => { settings.ShowInTaskbar = ShowInTaskbar = on; SaveSettings(); });
        bool startup = false, shortcut = false;
        try { startup = WindowsShell.StartsWithWindows(exe); shortcut = WindowsShell.HasStartMenuShortcut(exe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException) { }
        Toggle("Start with Windows", startup, on => WindowsShell.SetStartsWithWindows(exe, on));
        Toggle("Start menu shortcut", shortcut, on =>
        {
            WindowsShell.SetStartMenuShortcut(exe, on);
            if (on) MessageBox.Show(this, "Token Usage Tracker is now in the Start menu. To pin it, open Start, find Token Usage Tracker under All, right-click it, and choose Pin to Start or Pin to taskbar.",
                "Taskbar and Start", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        return windows;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { await RefreshAsync(); e.Handled = true; }
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
```

**Verify:** `dotnet build src/TokenUsageTracker/TokenUsageTracker.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** `Build succeeded.` with no warnings. Single-tool behaviour is unchanged: the same status text, source line, tooltips and sizes.

#### Task 15: Window checks for Both mode

**Goal:** Isolate Claude notice state and notifications, include the Claude block in the layout checks, render Both mode at default and minimum sizes in full and compact layouts, and check bar colours, two tray icons, the taskbar choice and failure isolation with a fake Codex reader.

**File:** tests/WindowChecks/Program.cs

Replace the whole file with the code below.

**Code:**
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using TokenUsageTracker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Never show the window: Loaded starts account polling and optional task recovery.
        var window = new MainWindow();
        int checks = 0;
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        // Claude notices keep state in LocalAppData; point them at a temporary file so the checks never touch the real one.
        string noticeState = Path.Combine(Path.GetTempPath(), "WindowChecks-claude-resume-" + Guid.NewGuid() + ".json");
        SetField("notices", new ClaudeResumeNotices(noticeState));
        SetField("notifyResume", new Action<ClaudeInterruption>(_ => throw new Exception("Window checks must not show Claude notices")));
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        try
        {
            var settings = (WidgetSettings)Field("settings");
            // MainWindow loads the user's saved preferences; start from Codex so a saved Claude choice cannot hide Codex-only controls.
            settings.Provider = "Codex";
            settings.FullWidth = 350; settings.FullHeight = 400;
            settings.CompactWidth = 300; settings.CompactHeight = 150;
            Call("SetCompact", true, false);
            Check(window.Width == 300 && window.Height == 150, "Compact mode restores its custom dimensions");
            Call("SetCompact", false, false);
            Check(window.Width == 350 && window.Height == 400, "Full mode restores its independent custom dimensions");
            settings.CompactWidth = double.MaxValue; settings.CompactHeight = double.MaxValue;
            Call("SetCompact", true, false);
            Check(window.Width <= SystemParameters.VirtualScreenWidth && window.Height <= SystemParameters.VirtualScreenHeight,
                "Oversized saved dimensions cannot exceed the desktop");
            settings.CompactWidth = 300; settings.CompactHeight = 150;
            Call("SetCompact", true, false);
            var grip = (Thumb)window.FindName("ResizeHandle");
            grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 240 && window.Height == 120, "Dragging clamps compact mode to its usable minimum");
            grip.RaiseEvent(new DragDeltaEventArgs(20, 10) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 260 && window.Height == 130, "Dragging can enlarge the widget again");
            grip.RaiseEvent(new DragDeltaEventArgs(-20, -10) { RoutedEvent = Thumb.DragDeltaEvent });

            var root = (FrameworkElement)window.Content;
            Rect Bounds(string name)
            {
                var control = (FrameworkElement)window.FindName(name);
                return control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            }
            void Render(string file)
            {
                Call("UpdateResponsiveLayout");
                root.Measure(new Size(window.Width, window.Height));
                root.Arrange(new Rect(0, 0, window.Width, window.Height));
                root.UpdateLayout();
                Rect content = Bounds("LayoutGrid");
                var card = (System.Windows.Controls.Border)window.FindName("Card");
                double visibleBottom = window.Height - card.Margin.Bottom - card.BorderThickness.Bottom - card.Padding.Bottom;
                bool Visible(string name) => ((FrameworkElement)window.FindName(name)).Visibility == Visibility.Visible;
                var names = new List<string> { "Header", "PrimaryLabel", "PrimaryValue", "PrimaryBar", "SecondaryLabel", "SecondaryValue", "SecondaryBar" };
                if (Visible("PrimaryReset")) names.AddRange(new[] { "PrimaryReset", "SecondaryReset" });
                if (Visible("ClaudeBlock"))
                {
                    names.AddRange(new[] { "ClaudePrimaryLabel", "ClaudePrimaryValue", "ClaudePrimaryBar", "ClaudeSecondaryLabel", "ClaudeSecondaryValue", "ClaudeSecondaryBar" });
                    if (Visible("ClaudePrimaryReset")) names.AddRange(new[] { "ClaudePrimaryReset", "ClaudeSecondaryReset" });
                    if (Visible("ClaudePlanRow")) names.Add("ClaudePlanRow");
                }
                if (((FrameworkElement)window.FindName("TaskLists")).Visibility == Visibility.Visible)
                    names.AddRange(new[] { "TaskLists", "FiveHourTasks", "WeeklyTasks" });
                if (((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Visible)
                {
                    names.Add("Footer"); names.Add("StatusText");
                    if (((FrameworkElement)window.FindName("SourceText")).Visibility == Visibility.Visible) names.Add("SourceText");
                }
                foreach (string name in names)
                {
                    Rect bounds = Bounds(name);
                    Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= content.Left && bounds.Top >= content.Top
                        && bounds.Right <= Math.Min(content.Right + 1, window.Width - 6)
                        && bounds.Bottom <= Math.Min(content.Bottom + 1, visibleBottom),
                        name + " fits inside the card at " + window.Width + " × " + window.Height);
                    if (window.FindName(name) is System.Windows.Controls.TextBlock text)
                        Check(bounds.Height >= text.FontSize, name + " text is not vertically clipped (height " + bounds.Height + ")");
                }
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(".build");
                using var output = File.Create(Path.Combine(".build", file));
                encoder.Save(output);
            }
            // Use long representative readings to expose narrow-layout overlap.
            ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text = "99.9%";
            ((System.Windows.Controls.TextBlock)window.FindName("SecondaryValue")).Text = "100%";
            ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).Text = "Resets in 4h 59m";
            ((System.Windows.Controls.TextBlock)window.FindName("SecondaryReset")).Text = "Resets in 6d 23h";
            Render("window-compact-minimum.png");
            Check(Bounds("PrimaryValue").Top >= Bounds("PrimaryLabel").Bottom
                && Bounds("SecondaryValue").Top >= Bounds("SecondaryLabel").Bottom, "Narrow labels do not overlap percentages");
            Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)window.FindName("PinButton")).Visibility == Visibility.Collapsed,
                "Secondary header controls hide at narrow widths");
            Check(((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Collapsed, "Short compact windows hide the footer");
            window.Width = 432; window.Height = 162;
            Render("window-compact-default.png");
            Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Visible
                && ((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Visible,
                "Growing the compact window restores header controls and status");
            window.Width = 240; window.Height = 160;
            Render("window-compact-narrow-footer.png");
            window.Width = 432; window.Height = 120;
            Render("window-compact-wide-short.png");
            Call("SetCompact", false, false);
            grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 280 && window.Height == 384, "Full layout has its own usable minimum");
            Render("window-full-minimum.png");
            string claudeFolder = Path.Combine(Path.GetTempPath(), "ClaudeWindowChecks-" + Guid.NewGuid());
            string? previousFolder = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var previousContext = System.Threading.SynchronizationContext.Current;
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeFolder);
                System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                settings.Provider = "Claude";
                Call("SetCompact", true, false);
                window.Width = 240; window.Height = 120;
                Call("UpdateProvider");
                Check(window.Title == "Claude Usage" && ((System.Windows.Controls.TextBlock)window.FindName("ProviderText")).Text == "Claude",
                    "Claude selection updates the window title and visible provider");
                string claudeInput = System.Text.Json.JsonSerializer.Serialize(new
                {
                    prompt_cache = new { expires_at = DateTimeOffset.UtcNow.AddSeconds(100).ToUnixTimeSeconds(), recache_tokens_if_cold = 183402 },
                    rate_limits = new
                    {
                        five_hour = new { used_percentage = 23.5, resets_at = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds() },
                        seven_day = new { used_percentage = 41.2, resets_at = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds() }
                    }
                });
                ClaudeUsage.Capture(claudeInput, claudeFolder, DateTimeOffset.UtcNow);
                void Refresh()
                {
                    var task = (Task)typeof(MainWindow).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                    task.GetAwaiter().GetResult();
                }
                Refresh();
                Check(((System.Windows.Controls.ProgressBar)window.FindName("PrimaryBar")).Value == 76.5
                    && ((System.Windows.Controls.ProgressBar)window.FindName("SecondaryBar")).Value == 58.8,
                    "Claude refresh displays both allowances from the status-line snapshot");
                var taskbar = window.TaskbarItemInfo;
                string TrayText() => (string)Field("tray").GetType().GetProperty("Text")!.GetValue(Field("tray"))!;
                Check(taskbar.ProgressState == TaskbarItemProgressState.Normal && Math.Abs(taskbar.ProgressValue - 0.588) < 1e-9
                    && taskbar.Overlay is BitmapSource && typeof(MainWindow).GetField("badge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is not null,
                    "Taskbar button shows the tighter Claude window as progress with a number badge");
                Check(TrayText().StartsWith("Claude: 5-hour", StringComparison.Ordinal) && TrayText().Contains("Weekly"),
                    "Tray tooltip lists both Claude windows");
                Check(((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text.Contains("Claude Code snapshot"),
                    "Claude readings identify themselves as snapshots");
                Check(((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).Text.Contains("empty ~")
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).ToolTip.ToString()!.Contains("average pace"),
                    "Claude window shows its pace forecast and explanation");
                Check(((System.Windows.Controls.TextBlock)window.FindName("SourceText")).Text.Contains("re-cache ~183k tokens")
                    && ((System.Windows.Controls.Border)window.FindName("Card")).ToolTip.ToString()!.Contains("most recently updated"),
                    "Claude cache countdown appears in the footer and compact tooltip");
                Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Collapsed,
                    "Claude display hides the unsupported automatic recovery control");
                Render("window-claude-minimum.png");
                Call("SetCompact", false, false);
                Render("window-claude-full.png");
                ClaudeUsage.ConfigureStatusLine(claudeFolder, "old-widget.exe");
                Refresh();
                Check(((System.Windows.Controls.TextBlock)window.FindName("SourceText")).Text.Contains("needs setup"),
                    "A moved widget's setup warning remains visible when cache data exists");
                File.Delete(Path.Combine(claudeFolder, "usage-widget", "usage.json"));
                Refresh();
                Check(((System.Windows.Controls.ProgressBar)window.FindName("PrimaryBar")).Value == 0
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—"
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).ToolTip is null,
                    "Missing Claude data clears the old balance instead of falling back to Codex");
                Check(window.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.None && window.TaskbarItemInfo.Overlay is null
                    && typeof(MainWindow).GetField("badge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
                    "Missing Claude data clears the taskbar progress and tray number");
                settings.Provider = "Codex";
                Call("UpdateProvider");
                Check(window.Title == "Codex Usage" && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—",
                    "Switching back to Codex clears the Claude balance immediately");

                // Both tools. A fake Codex reader keeps the checks from starting Codex; an empty folder stands in for local sessions.
                settings.CodexFolder = Path.Combine(claudeFolder, "codex-home");
                LiveUsage FakeCodex() => new(new UsageSnapshot(DateTimeOffset.UtcNow, "plus",
                    new UsageWindow(10, 300, DateTimeOffset.UtcNow.AddHours(3)), new UsageWindow(20, 10080, DateTimeOffset.UtcNow.AddDays(4))), null, true);
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromResult(FakeCodex())));
                ClaudeUsage.Capture(claudeInput, claudeFolder, DateTimeOffset.UtcNow);
                T Find<T>(string name) => (T)window.FindName(name);
                Color BrushColor(string name) => ((SolidColorBrush)(window.FindName(name) is System.Windows.Controls.ProgressBar bar ? bar.Foreground : Find<System.Windows.Controls.TextBlock>(name).Foreground)).Color;
                settings.Provider = "Both";
                settings.BothFullWidth = 440; settings.BothFullHeight = 400; settings.BothCompactWidth = 432; settings.BothCompactHeight = 170;
                Call("SetCompact", false, false);
                Call("UpdateProvider");
                Check(window.Title == "Token Usage" && Find<System.Windows.Controls.TextBlock>("ProviderText").Text == "Codex + Claude"
                    && Find<FrameworkElement>("ClaudeBlock").Visibility == Visibility.Visible,
                    "Both mode shows Codex and Claude Code together");
                Refresh();
                Check(Find<System.Windows.Controls.ProgressBar>("PrimaryBar").Value == 90 && Find<System.Windows.Controls.ProgressBar>("SecondaryBar").Value == 80
                    && Find<System.Windows.Controls.ProgressBar>("ClaudePrimaryBar").Value == 76.5 && Find<System.Windows.Controls.ProgressBar>("ClaudeSecondaryBar").Value == 58.8,
                    "Both mode fills all four windows");
                Check(BrushColor("PrimaryBar") != BrushColor("ClaudePrimaryBar") && BrushColor("PrimaryValue") == BrushColor("ClaudePrimaryValue"),
                    "Codex and Claude bars differ while their percentage text shares the warning colour");
                string IconText(string field) => (string)Field(field).GetType().GetProperty("Text")!.GetValue(Field(field))!;
                Check(Field("claudeTray") is not null && Field("claudeBadge") is not null
                    && IconText("tray").StartsWith("Codex:", StringComparison.Ordinal) && IconText("claudeTray").StartsWith("Claude:", StringComparison.Ordinal),
                    "Both mode shows one tray number per tool");
                Check(Math.Abs(window.TaskbarItemInfo.ProgressValue - 0.588) < 1e-9 && window.TaskbarItemInfo.Overlay is BitmapSource,
                    "The taskbar follows the tool with less room left");
                Render("window-both-full-default.png");
                grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
                Check(window.Width == 380 && window.Height == 370, "Full Both mode has its own usable minimum");
                Render("window-both-full-minimum.png");
                Call("SetCompact", true, false);
                Check(window.Width == 432 && window.Height == 170, "Compact Both mode restores its own size");
                Check(Find<System.Windows.Controls.TextBlock>("ClaudePrimaryLabel").Text.StartsWith("Claude", StringComparison.Ordinal)
                    && Find<System.Windows.Controls.TextBlock>("PrimaryLabel").Text.StartsWith("Codex", StringComparison.Ordinal),
                    "Compact Both labels name their tool");
                Render("window-both-compact-default.png");
                Check(Find<FrameworkElement>("Footer").Visibility == Visibility.Visible, "Compact Both mode shows the footer at its default size");
                grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
                Check(window.Width == 300 && window.Height == 150, "Compact Both mode has its own usable minimum");
                Render("window-both-compact-minimum.png");
                SetField("lastLive", null);
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromException<LiveUsage>(new IOException("offline"))));
                Refresh();
                Check(Find<System.Windows.Controls.TextBlock>("PrimaryValue").Text == "—" && Find<System.Windows.Controls.ProgressBar>("ClaudePrimaryBar").Value == 76.5,
                    "A failed Codex read leaves the Claude numbers in place");
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromResult(FakeCodex())));
                File.Delete(Path.Combine(claudeFolder, "usage-widget", "usage.json"));
                Refresh();
                Check(Find<System.Windows.Controls.TextBlock>("ClaudePrimaryValue").Text == "—" && Find<System.Windows.Controls.ProgressBar>("PrimaryBar").Value == 90,
                    "Missing Claude data leaves the Codex numbers in place");
                settings.Provider = "Codex";
                Call("UpdateProvider");
                Check(Field("claudeTray") is null && Find<FrameworkElement>("ClaudeBlock").Visibility == Visibility.Collapsed,
                    "Leaving Both mode removes the Claude tray icon and section");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousFolder);
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(claudeFolder)) Directory.Delete(claudeFolder, recursive: true);
            }
            var badgeType = typeof(MainWindow).Assembly.GetType("TokenUsageTracker.TrayBadge")!;
            foreach (var (text, provider) in new[] { ("0", "Codex"), ("58", "Claude"), ("100", "Codex") })
            {
                var rendered = (IDisposable)badgeType.GetMethod("Render")!.Invoke(null, new object[] { text, UsageLevel.Critical, 16, provider, true })!;
                var image = (BitmapSource)badgeType.GetProperty("Image")!.GetValue(rendered)!;
                Check(image.PixelWidth == 16 && image.PixelHeight == 16, $"Tray badge \"{text}\" renders at the small icon size");
                if (text == "100")
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(".build", "tray-badge-100.png"));
                    encoder.Save(output);
                }
                rendered.Dispose();
            }
            Check(!window.IsLoaded, "Window checks never start account polling or task recovery");
            Console.WriteLine($"All {checks} window checks passed.");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.ExitCode = 1;
        }
        finally
        {
            ((IDisposable)Field("tray")).Dispose();
            (typeof(MainWindow).GetField("claudeTray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as IDisposable)?.Dispose();
            ((IDisposable)Field("resume")).Dispose();
            if (File.Exists(noticeState)) File.Delete(noticeState);
        }
    }
}
```

**Verify:** `dotnet build tests/WindowChecks/WindowChecks.csproj -p:EnableWindowsTargeting=true`

**Checkpoint:** Build succeeds. These checks run only on Windows: locally with `dotnet run --project tests/WindowChecks/WindowChecks.csproj --configuration Release`, otherwise in CI (Task 18).

#### Task 16: README: document Both mode, Claude task lists and resume notices

**Goal:** Update the feature list, add "Codex and Claude Code together" and "Continuing a Claude Code session after a usage limit", extend the tray, taskbar and task-history text, and state what is read and stored about Claude Code. The script also adds the AGENTS.md lines in Task 17.

**File:** README.md

**Code:**
```python
import sys

root = sys.argv[1]


def edit(path, pairs):
    with open(path, encoding="utf-8") as f:
        s = f.read()
    for a, b in pairs:
        assert s.count(a) == 1, (path, a)
        s = s.replace(a, b)
    with open(path, "w", encoding="utf-8") as f:
        f.write(s)


edit(root + "/README.md", [
    ("A small desktop widget that shows the remaining usage in your Codex or Claude plan, including reset times for the current and weekly windows.",
     "A small desktop widget that shows the remaining usage in your Codex plan, your Claude plan, or both side by side, including reset times for the current and weekly windows."),
    ("- Select **Codex** or **Claude Code** in Options; the selected provider is remembered.",
     "- Select **Codex**, **Claude Code**, or **Codex and Claude Code** in Options; the selected provider is remembered."),
    ("- Optional automatic continuation of Codex tasks interrupted by a usage limit, with saved **Codex Desktop** or **CLI** target settings.",
     "- Optional automatic continuation of Codex tasks interrupted by a usage limit, with saved **Codex Desktop** or **CLI** target settings.\n"
     "- A notification when a Claude Code session stopped by a usage limit can continue, with its resume command ready to paste."),
    ("- Press **F5** to refresh usage or **Esc** to hide. Double-click the notification-area icon to show the widget again.\n",
     "- Press **F5** to refresh usage or **Esc** to hide. Double-click the notification-area icon to show the widget again.\n"
     "\n"
     "### Codex and Claude Code together\n"
     "\n"
     "Select **⋯ → Usage provider → Codex and Claude Code** to see both tools in one widget. Each tool gets a row with its 5-hour and weekly windows next to each other, Codex on top. A tool is blocked by whichever of its own windows runs out first, so its two numbers sit side by side.\n"
     "\n"
     "- The percentage text always shows the warning level: green above 25%, amber at 25% or less, red at 10% or less.\n"
     "- The bars tell the tools apart. Codex bars keep the warning colours; Claude bars use their own shades of the same levels (mint, orange, rose).\n"
     "- Full-size mode shows reset times and pace forecasts. Compact mode shows only the percentages and bars, with the tool named in each label.\n"
     "- The footer shows how recent each tool's reading is. If one tool cannot be read, the other keeps its numbers.\n"
     "- This mode remembers its own window size, separate from the single-tool layouts. Full-size mode can shrink to 380 × 370 and compact mode to 300 × 150.\n"),
    ("With no usable reading, it shows the app icon.",
     "With no usable reading, it shows the app icon. With both tools shown there are two icons: Codex as a rounded square and Claude as a circle in its own shades. Windows decides the order of tray icons, so the shape tells them apart."),
    ("with the number as an overlay badge.",
     "with the number as an overlay badge. With both tools shown, it follows whichever tool has less room left."),
    ("Open **5h tasks ▾** or **Weekly tasks ▾** for local Codex prompts ranked by recorded tokens.",
     "Open **5h tasks ▾** or **Weekly tasks ▾** for local Codex or Claude Code prompts ranked by recorded tokens. With both tools shown, the list has a Codex group and a Claude Code group, each estimated against its own tool's window."),
    ("These are not API prices. Claude status-line snapshots do not contain task history, so these lists are available for Codex only.",
     "These are not API prices.\n"
     "\n"
     "Claude Code tasks come from the transcripts under `%USERPROFILE%\\.claude\\projects` (or `CLAUDE_CONFIG_DIR`). A task is one prompt and the replies that follow it, including the subagents it started. Input counts fresh input, cache writes and cache reads; the details window shows cache reads separately. Cache reads may count less toward Claude limits than other tokens, so estimates for long-context tasks can be too high."),
    ("Claude support is display-only. Selecting Claude pauses Codex recovery and clears its pending queue; returning to Codex reapplies your saved auto-resume preference and watches for new interruptions.",
     "Claude support does not start work on its own. Selecting Claude alone pauses Codex recovery and clears its pending queue; returning to Codex or to both tools reapplies your saved auto-resume preference and watches for new interruptions.\n"
     "\n"
     "### Continuing a Claude Code session after a usage limit\n"
     "\n"
     "When a usage limit stops a Claude Code session, the widget notices the error at the end of that session's transcript. Once the limit has reset, it shows a Windows notification that names the task and its folder. Click the notification to copy a PowerShell command that opens the folder and runs `claude --resume <session>`, then paste it into a terminal.\n"
     "\n"
     "- The notification waits until every window that was full has reset, or until Claude Code reports room in a newer reading. A countdown alone never triggers it.\n"
     "- Each interruption is announced once. A new prompt in that session cancels the notice.\n"
     "- **⋯ → Claude sessions to resume** lists the sessions that can continue, with **Copy resume command** and **Dismiss**.\n"
     "- Turn notices off with **⋯ → Notify when Claude can continue**. Interruptions from before you turned them on are ignored.\n"
     "\n"
     "The widget never runs Claude Code itself. Recognising the usage-limit error relies on its wording in the transcript, which has not been confirmed against a real example yet."),
    ("it does not copy chat transcripts or credentials.",
     "it does not copy chat transcripts or credentials. Claude notices store only session and error identifiers in `claude-resume.json` in the same folder. Claude Code transcripts are read locally for task lists and notices and are never copied or sent anywhere; the widget does not open Claude credentials."),
])

edit(root + "/AGENTS.md", [
    ("and sets a `statusLine` in the user's Claude `settings.json`.\n",
     "and sets a `statusLine` in the user's Claude `settings.json`.\n"
     "- Claude Code transcripts: `ClaudeTranscripts.cs` reads `projects/**/*.jsonl` for the Claude task list and usage-limit interruptions; `ClaudeResumeNotices.cs` decides when an interrupted session can continue (notify only, never runs `claude`).\n"
     "- Both tools: `WidgetSettings.Provider` is `Codex`, `Claude` or `Both`; `UsageColors.cs` holds the warning and Claude palettes. Window checks replace the `readLive`, `notices` and `notifyResume` fields so they never start Codex, touch real state or show notifications.\n"),
])

```

**Verify:** `python3 -I /tmp/readme_edit.py . && grep -c "Codex and Claude Code together" README.md`

**Checkpoint:** Save the code as `/tmp/readme_edit.py`, run it from the repository root, and confirm it prints `1`. Each replacement asserts its anchor text appears exactly once, so a changed README stops the script instead of editing the wrong place.

#### Task 17: AGENTS.md: point agents at the new files

**Goal:** Record where the Claude transcript reader, the notice logic, the provider modes and the window-check seams live.

**File:** AGENTS.md

**Code:**
```markdown
- Claude Code transcripts: `ClaudeTranscripts.cs` reads `projects/**/*.jsonl` for the Claude task list and usage-limit interruptions; `ClaudeResumeNotices.cs` decides when an interrupted session can continue (notify only, never runs `claude`).
- Both tools: `WidgetSettings.Provider` is `Codex`, `Claude` or `Both`; `UsageColors.cs` holds the warning and Claude palettes. Window checks replace the `readLive`, `notices` and `notifyResume` fields so they never start Codex, touch real state or show notifications.
```

**Verify:** `grep -c "ClaudeResumeNotices.cs" AGENTS.md`

**Checkpoint:** Prints `1`. Task 16's script already inserts these two lines after the "Claude Code:" bullet; if you skipped that script, insert them there by hand.

#### Task 18: Commit, push and run the Windows checks in CI

**Goal:** Commit the implementation on the feature branch and let the Windows workflow run the usage checks, window checks and publish.

**File:** (git) prel/exciting-bohr-mdt58o

**Code:**
```bash
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release
git add -A src tests README.md AGENTS.md
git commit -F - <<'MSG'
Show Codex and Claude Code together, with Claude task lists and resume notices

Adds a Both provider that shows each tool's 5-hour and weekly windows side by
side, colours percentage text by warning level and Claude's bars in their own
shades, shows one tray number per tool and puts the lower tool on the taskbar.
Claude Code transcripts now feed the task lists and a notification when a
session stopped by a usage limit can continue. The widget never runs Claude.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_011yeZjNUxYifWffR1ydAAaB
MSG
git push -u origin prel/exciting-bohr-mdt58o
```

**Verify:** The "Windows widget" GitHub Actions run for the pushed commit finishes green (window checks print `All N window checks passed.`).

**Checkpoint:** If a window check fails with "fits inside the card" for a Both-mode screenshot, raise that layout's minimum in `SetCompact` (MinWidth/MinHeight for `both`) and the matching numbers in README and the minimum-size checks, then push again. Download the `.build` screenshots locally with `Build.cmd` to look at the layouts. Do not continue to Task 19 until CI is green.

#### Task 19: Merge everything into main

**Goal:** Bring the taskbar work, the main merge and the Both-mode work into main, as the user asked.

**File:** (git) main

**Code:**
```bash
git fetch origin main
git checkout -B main origin/main
git merge --no-ff prel/exciting-bohr-mdt58o -m "Merge taskbar and Start integration, and Codex and Claude Code together"
dotnet run --project tests/UsageChecks/UsageChecks.csproj --configuration Release
git push origin main
git checkout prel/exciting-bohr-mdt58o
```

**Verify:** `git log origin/main --oneline -1` shows the merge commit, and the GitHub Actions run on main is green.

**Checkpoint:** If origin/main moved and the merge conflicts, resolve, rerun the usage checks and the app build, and push. Never force-push main.

## Execution notes

- Task 18, first CI run: the full Both minimum (380 × 370) clipped the footer. Below 420 px wide the cells stacked each percentage under its label, which adds about 15 px per tool. Full Both cells now keep the label and percentage on one line (`stacked = compact && narrow`). The same arithmetic showed compact Both would clip by a pixel or two, so its default height is now 180 (was 170), its minimum 300 × 160 (was 150) and its footer hides below 180.
