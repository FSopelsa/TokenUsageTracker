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

    public TaskRanking Read(string home, UsageWindow? window, int fallbackMinutes, DateTimeOffset now)
    {
        bool exactWindow = window?.Minutes is > 0 && window.ResetsAt is { } reset && reset > now && reset.AddMinutes(-window.Minutes.Value) <= now;
        DateTimeOffset start = exactWindow ? window!.ResetsAt!.Value.AddMinutes(-window.Minutes!.Value) : now.AddMinutes(-fallbackMinutes);
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

    private static long Number(JsonElement value, string key) { var n = Json.Get(value, key); return n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out long count) && count >= 0 && count <= long.MaxValue / 1000 ? count : 0; }
}
