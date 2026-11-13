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
