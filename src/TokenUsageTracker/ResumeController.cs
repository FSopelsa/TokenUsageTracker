using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TokenUsageTracker;

public sealed record InterruptedTask(string ChatId, string TurnId, string Path, string Target,
    DateTimeOffset FailedAt, string Workspace, string Model, string Provider, string Approval, string SandboxJson, string? Effort);

public sealed class ResumeEntry
{
    public InterruptedTask Task { get; set; } = null!;
    public string Stage { get; set; } = "Waiting";
    public string? QueueId { get; set; }
    public string? StartedTurn { get; set; }
}

public sealed class ResumeState
{
    public string Home { get; set; } = "";
    public string Target { get; set; } = "Desktop";
    public DateTimeOffset EnabledAt { get; set; }
    public string? Account { get; set; }
    public Dictionary<string, ResumeEntry> Entries { get; set; } = new();
}

public sealed class ResumeController : IDisposable
{
    public const string Prompt = "Continue the unfinished task from where it stopped. Preserve its scope and existing instructions. Stop when the task is complete.";
    private readonly string statePath;
    private readonly Func<string, bool, CancellationToken, Task<CodexClient>> connect;
    private ResumeState state;
    private readonly Dictionary<string, (long Length, DateTime Modified, InterruptedTask? Task)> cache = new();
    private readonly List<Process> running = new();
    private int revision;
    private bool enabled;
    private CancellationTokenSource pendingCancellation = new();
    public string Status { get; private set; } = "Auto-resume off";
    public int PendingCount => state.Entries.Values.Count(e => e.Stage == "Waiting");

    public ResumeController(string? path = null, Func<string, bool, CancellationToken, Task<CodexClient>>? connect = null)
    {
        this.connect = connect ?? CodexClient.ConnectAsync;
        statePath = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "resume-state.json");
        try
        {
            state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(statePath)) ?? new();
            if (state.Entries is null || state.Entries.Any(p => p.Value is null || p.Value.Task is null)) state = new();
            foreach (var entry in state.Entries.Values.Where(e => e.Stage == "Dispatching")) entry.Stage = "Manual";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { state = new(); }
    }

    public void Configure(bool on, string target, string home, DateTimeOffset now)
    {
        if (enabled == on && state.Target == target && state.Home == home) return;
        revision++;
        pendingCancellation.Cancel(); pendingCancellation.Dispose(); pendingCancellation = new();
        bool restore = on && !enabled && state.EnabledAt != default && state.Target == target && state.Home == home;
        enabled = on;
        if (!restore || !on) state = new() { Home = home, Target = target, EnabledAt = on ? now : default };
        lock (cache) cache.Clear();
        Save();
        Status = on ? $"Watching {target} for usage-limit interruptions" : "Auto-resume off";
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(statePath + ".tmp", statePath, overwrite: true);
    }

    public async Task TickAsync(LiveUsage? live, CancellationToken token)
    {
        if (!enabled) return;
        int version = revision;
        if (live?.Account is null) { Status = "Auto-resume waiting for live account usage"; return; }
        if (state.Account is { } old && old != live.Account)
        {
            state.Entries.Clear(); state.EnabledAt = DateTimeOffset.UtcNow;
            Status = "Account changed; previous pending tasks cleared";
        }
        state.Account = live.Account;
        string home = state.Home, target = state.Target;
        var tasks = await Task.Run(() => { lock (cache) return Scan(home); }, token);
        if (!enabled || version != revision) return;
        var byChat = tasks.ToDictionary(t => t.ChatId);
        foreach (var entry in state.Entries.Values)
        {
            if (!byChat.TryGetValue(entry.Task.ChatId, out var latest) || latest.TurnId != entry.Task.TurnId || latest != entry.Task)
            {
                if (entry.Stage == "Waiting") entry.Stage = "Cancelled";
            }
        }
        foreach (var task in tasks.Where(t => t.Target == target && t.FailedAt >= state.EnabledAt))
        {
            if (!state.Entries.TryGetValue(task.ChatId, out var previous) || previous.Task.TurnId != task.TurnId)
                state.Entries[task.ChatId] = new() { Task = task };
        }
        Save();
        var pending = state.Entries.Values.Where(e => e.Stage == "Waiting").OrderBy(e => e.Task.FailedAt).ToArray();
        if (pending.Length == 0)
        {
            Status = state.Entries.Values.Any(e => e.Stage == "Manual") ? "Recovery needs attention in Codex; no automatic retry"
                : state.Entries.Values.Any(e => e.Stage == "Started") ? "Continuation sent; monitoring for another usage limit"
                : $"Watching {target} for usage-limit interruptions";
            return;
        }
        if (!live.CanResume(DateTimeOffset.UtcNow))
        {
            var resets = new[] { live.Snapshot.Primary, live.Snapshot.Secondary }.OfType<UsageWindow>()
                .Where(w => w.UsedPercent >= 100 && w.ResetsAt > DateTimeOffset.UtcNow).Select(w => w.ResetsAt).ToArray();
            Status = $"{pending.Length} pending · " + (resets.Length > 0 ? $"waiting until {resets.Max()!.Value.ToLocalTime():g}"
                : "waiting for Codex to confirm included usage is available");
            return;
        }
        foreach (var entry in pending)
        {
            if (!enabled || version != revision || token.IsCancellationRequested) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, pendingCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Query again at dispatch, even when this poll already obtained a live reading.
                using var client = await connect(home, target == "Desktop", timeout.Token);
                var fresh = await client.ReadUsageAsync(timeout.Token);
                if (!enabled || version != revision) return;
                if (fresh.Account != state.Account) { Status = "Account changed; recovery paused"; return; }
                if (!fresh.CanResume(DateTimeOffset.UtcNow)) { Status = "Recovery waiting for included usage"; return; }
                var current = ReadInterrupted(entry.Task.Path);
                if (current != entry.Task || !Directory.Exists(entry.Task.Workspace))
                {
                    entry.Stage = "Cancelled"; Save(); continue;
                }
                if (target == "CLI")
                {
                    // Metadata read rejects deleted/archived or currently active chats before exec owns it.
                    var result = await client.RequestAsync("thread/read", new { threadId = current.ChatId, includeTurns = true }, timeout.Token);
                    if (!IsFailedTurn(result, current)) throw new IOException("CLI chat changed or is active. Continue it manually in Codex.");
                    ProcessStartInfo start;
                    try { start = CliStartInfo(current, home); }
                    catch (IOException) { entry.Stage = "Manual"; Save(); throw; }
                    if (!enabled || version != revision || ReadInterrupted(current.Path) != current)
                    { entry.Stage = "Cancelled"; Save(); continue; }
                    entry.Stage = "Dispatching"; Save();
                    var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                    process.ErrorDataReceived += (_, _) => { };
                    if (!process.Start()) throw new IOException("Could not start CLI continuation.");
                    running.Add(process);
                    process.BeginErrorReadLine();
                    entry.Stage = "Started"; Save();
                    _ = ObserveCliAsync(process, entry);
                }
                else
                {
                    entry.Stage = "Dispatching"; Save();
                    entry.StartedTurn = await client.ResumeDesktopAsync(current, queueId => { entry.QueueId = queueId; Save(); }, timeout.Token);
                    entry.Stage = "Started"; Save();
                }
                Status = $"Continued {target} chat {current.ChatId[..8]}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or OperationCanceledException or JsonException or InvalidOperationException)
            {
                if (!enabled || version != revision) return;
                if (entry.Stage == "Dispatching") entry.Stage = "Manual";
                Save();
                Status = e is OperationCanceledException ? "Recovery connection timed out; check Codex" : e.Message;
                return;
            }
        }
    }

    public static bool IsFailedTurn(JsonElement response, InterruptedTask task)
    {
        var thread = Json.Get(response, "thread"); var turns = Json.Get(thread, "turns");
        if (Json.Text(thread, "id") != task.ChatId || Json.Text(Json.Get(thread, "status"), "type") is not ("notLoaded" or "idle")
            || turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0) return false;
        var last = turns[turns.GetArrayLength() - 1];
        return Json.Text(last, "id") == task.TurnId && Json.Text(last, "status") == "failed"
            && Json.Text(Json.Get(last, "error"), "codexErrorInfo") == "usageLimitExceeded";
    }

    public static ProcessStartInfo CliStartInfo(InterruptedTask task, string home, string? executable = null)
    {
        using var doc = JsonDocument.Parse(task.SandboxJson);
        var sandbox = doc.RootElement;
        string? mode = Json.Text(sandbox, "type");
        if (mode is not ("read-only" or "workspace-write") || task.Approval is not ("on-request" or "never") || task.Provider != "openai")
            throw new IOException("Recorded CLI permissions/provider cannot be preserved. Continue this chat manually.");
        if (!Guid.TryParse(task.ChatId, out _) || string.IsNullOrWhiteSpace(task.Model) || !System.IO.Path.IsPathFullyQualified(task.Workspace))
            throw new IOException("Incomplete CLI session settings. Continue this chat manually.");
        var start = CodexClient.StartInfo(executable ?? CodexClient.FindExecutable(), home, "exec", "--json", "--sandbox", mode,
            "--config", "approval_policy=" + JsonSerializer.Serialize(task.Approval),
            "--config", "model_provider=" + JsonSerializer.Serialize(task.Provider),
            "--model", task.Model);
        if (mode == "workspace-write")
        {
            var roots = Json.Get(sandbox, "writable_roots");
            if (roots.ValueKind != JsonValueKind.Array || roots.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || !System.IO.Path.IsPathFullyQualified(r.GetString()!)))
                throw new IOException("Recorded writable roots are unavailable. Continue this chat manually.");
            void Config(string name, string value) { start.ArgumentList.Add("--config"); start.ArgumentList.Add("sandbox_workspace_write." + name + "=" + value); }
            Config("writable_roots", "[" + string.Join(",", roots.EnumerateArray().Select(r => JsonSerializer.Serialize(r.GetString()))) + "]");
            foreach (string key in new[] { "network_access", "exclude_tmpdir_env_var", "exclude_slash_tmp" })
            {
                var value = Json.Get(sandbox, key);
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new IOException("Incomplete CLI sandbox settings. Continue this chat manually.");
                Config(key, value.ValueKind == JsonValueKind.True ? "true" : "false");
            }
        }
        if (task.Effort is { Length: > 0 })
        {
            start.ArgumentList.Add("--config"); start.ArgumentList.Add("model_reasoning_effort=" + JsonSerializer.Serialize(task.Effort));
        }
        start.ArgumentList.Add("resume"); start.ArgumentList.Add(task.ChatId); start.ArgumentList.Add(Prompt);
        start.WorkingDirectory = task.Workspace;
        return start;
    }

    private async Task ObserveCliAsync(Process process, ResumeEntry entry)
    {
        bool completed = false, expectedThread = false, failed = false;
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    string? kind = Json.Text(root, "type");
                    if (kind == "thread.started")
                    {
                        expectedThread = Json.Text(root, "thread_id") == entry.Task.ChatId;
                        if (!expectedThread) { process.Kill(entireProcessTree: true); failed = true; }
                    }
                    if (kind == "turn.completed") completed = true;
                    if (kind is "turn.failed" or "error") failed = true;
                }
                catch (JsonException) { failed = true; }
            }
            await process.WaitForExitAsync();
            if (!expectedThread || process.ExitCode != 0 || failed || !completed) entry.Stage = "Manual";
            else entry.Stage = "Completed";
            Save();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            entry.Stage = "Manual";
            Status = "CLI continuation needs attention; check the chat before retrying";
        }
        finally { running.Remove(process); process.Dispose(); }
    }

    private List<InterruptedTask> Scan(string home)
    {
        string directory = System.IO.Path.Combine(home, "sessions");
        if (!Directory.Exists(directory)) return new();
        string[] paths = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories);
        var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in cache.Keys.Where(p => !present.Contains(p)).ToArray()) cache.Remove(path);
        foreach (string path in paths)
        {
            var file = new FileInfo(path);
            if (cache.TryGetValue(path, out var previous) && previous.Length == file.Length && previous.Modified == file.LastWriteTimeUtc) continue;
            cache[path] = (file.Length, file.LastWriteTimeUtc, ReadInterrupted(path));
        }
        return cache.Values.Select(v => v.Task).OfType<InterruptedTask>().GroupBy(t => t.ChatId).Select(g => g.MaxBy(t => t.FailedAt)!).ToList();
    }

    public static InterruptedTask? ReadInterrupted(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return null;
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') return null; // A partially written user action must block an older failure.
            stream.Seek(0, SeekOrigin.Begin);
            using var head = new StreamReader(stream, leaveOpen: true);
            string? first = head.ReadLine();
            if (first is null) return null;
            using var doc = JsonDocument.Parse(first);
            var meta = Json.Get(doc.RootElement, "payload");
            string? chat = Json.Text(meta, "id"), source = Json.Text(meta, "source"), originator = Json.Text(meta, "originator");
            if (Json.Text(doc.RootElement, "type") != "session_meta" || !Guid.TryParse(chat, out _) || source is null) return null;
            string? target = originator is "Codex Desktop" or "codex_work_desktop" ? "Desktop" : source is "cli" or "exec" ? "CLI" : null;
            if (target is null) return null;
            long size = 64 * 1024;
            while (true)
            {
                long start = Math.Max(0, stream.Length - size);
                stream.Seek(start, SeekOrigin.Begin);
                using var tail = new StreamReader(stream, leaveOpen: true);
                if (start > 0) tail.ReadLine();
                JsonElement context = default;
                InterruptedTask? candidate = null;
                bool sawContext = false;
                string? line;
                while ((line = tail.ReadLine()) is not null)
                {
                    try
                    {
                        using var record = JsonDocument.Parse(line);
                        var root = record.RootElement; var payload = Json.Get(root, "payload");
                        string? kind = Json.Text(root, "type"), type = Json.Text(payload, "type");
                        if (kind == "turn_context") { context = payload.Clone(); sawContext = true; candidate = null; }
                        if (kind == "response_item" && type == "message" && Json.Text(payload, "role") == "user") candidate = null;
                        if (kind != "event_msg") continue;
                        if (type is "task_started" or "turn_aborted" or "user_message") candidate = null;
                        if (type is not ("task_complete" or "turn_failed")) continue;
                        candidate = null;
                        var error = Json.Get(payload, "error");
                        if (Json.Text(error, "codex_error_info") != "usage_limit_exceeded"
                            || !DateTimeOffset.TryParse(Json.Text(root, "timestamp"), out var timestamp)) continue;
                        string? turn = Json.Text(payload, "turn_id") ?? Json.Text(context, "turn_id");
                        string? workspace = Json.Text(context, "cwd"), model = Json.Text(context, "model"), approval = Json.Text(context, "approval_policy");
                        var sandbox = Json.Get(context, "sandbox_policy");
                        if (turn is null || workspace is null || model is null || approval is null || sandbox.ValueKind != JsonValueKind.Object
                            || turn != Json.Text(context, "turn_id")) continue;
                        // Managed permission profiles need their owning runtime, not a guessed CLI translation.
                        if (target == "CLI" && Json.Get(context, "permissions").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) continue;
                        if (timestamp > DateTimeOffset.UtcNow.AddSeconds(5)) continue;
                        candidate = new(chat!, turn, path, target, timestamp, workspace, model, Json.Text(meta, "model_provider") ?? "",
                            approval, sandbox.GetRawText(), Json.Text(context, "effort"));
                    }
                    catch (JsonException) { candidate = null; }
                }
                if (sawContext || start == 0) return candidate;
                size = Math.Min(stream.Length, size * 2);
            }
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        enabled = false; revision++; pendingCancellation.Cancel(); pendingCancellation.Dispose();
        // Already-started CLI work may finish; closing the widget only stops future scheduling.
    }
}
