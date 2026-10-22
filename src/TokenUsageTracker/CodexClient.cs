using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TokenUsageTracker;

public sealed record LiveUsage(UsageSnapshot Snapshot, string? Account, bool OrdinaryUsageAllowed)
{
    public bool CanResume(DateTimeOffset now) => OrdinaryUsageAllowed && Account is not null
        && now >= Snapshot.Timestamp && now - Snapshot.Timestamp < TimeSpan.FromSeconds(30)
        && Snapshot.Primary is { UsedPercent: < 100, ResetsAt: { } p } && p > now
        && (Snapshot.Secondary is null || Snapshot.Secondary is { UsedPercent: < 100, ResetsAt: { } s } && s > now);
}

// Codex owns authentication; this client only speaks its local JSON-RPC protocol.
public sealed class CodexClient : IDisposable
{
    private readonly Process process;
    private int nextId;
    private CodexClient(Process process) => this.process = process;

    public static string FindExecutable()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string path = Path.Combine(dir.Trim('"'), "codex.exe");
            if (File.Exists(path)) return path;
        }
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktop))
        {
            string? path = Directory.EnumerateFiles(desktop, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (path is not null) return path;
        }
        // npm's .cmd shim cannot be executed directly with redirected standard input.
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string vendor = Path.Combine(dir.Trim('"'), "node_modules", "@openai");
            if (!Directory.Exists(vendor)) continue;
            string? path = Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (path is not null) return path;
        }
        throw new IOException("Codex executable not found. Install Codex Desktop or the Codex CLI, then restart the widget.");
    }

    public static ProcessStartInfo StartInfo(string executable, string home, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["CODEX_HOME"] = home;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    public static string DesktopSocket(string home) => Path.Combine(home, "app-server-control", "app-server-control.sock");

    public static async Task<CodexClient> ConnectAsync(string home, bool desktop, CancellationToken token)
    {
        if (desktop && !File.Exists(DesktopSocket(home)))
            throw new IOException("Desktop connection unavailable: this Codex installation has no shared control socket.");
        string[] args = desktop ? new[] { "app-server", "proxy", "--sock", DesktopSocket(home) } : new[] { "app-server" };
        return await ConnectProcessAsync(StartInfo(FindExecutable(), home, args), token);
    }

    public static async Task<CodexClient> ConnectProcessAsync(ProcessStartInfo start, CancellationToken token)
    {
        var process = Process.Start(start) ?? throw new IOException("Could not start Codex.");
        process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics without retaining private session output.
        process.BeginErrorReadLine();
        var client = new CodexClient(process);
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_usage_widget", title = "Codex Usage Widget", version = "1.0" },
                capabilities = new { experimentalApi = true }
            }, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        int id = ++nextId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("Codex connection closed. Check that Codex is running and signed in.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var serverId))
            {
                // Never approve work on behalf of a person, including unexpected protocol requests.
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = serverId.Clone(), error = new { code = -32601, message = "Use Codex to respond to this request." }
                }).AsMemory(), token);
                continue;
            }
            if (!root.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || !replyId.TryGetInt32(out int value) || value != id) continue;
            if (root.TryGetProperty("error", out _))
                throw new IOException($"Codex rejected {method}. Check sign-in and update Codex if this method is unsupported.");
            if (!root.TryGetProperty("result", out var result)) throw new IOException("Codex returned an invalid response.");
            return result.Clone();
        }
    }

    public async Task<LiveUsage> ReadUsageAsync(CancellationToken token)
    {
        var account = await RequestAsync("account/read", new { refreshToken = false }, token);
        if (Json.Text(Json.Get(account, "account"), "type") != "chatgpt")
            throw new IOException("Sign in to Codex with ChatGPT to read plan usage.");
        var limits = await RequestAsync("account/rateLimits/read", null, token);
        string? identity = Json.Text(limits, "accountId") ?? Json.Text(Json.Get(account, "workspaceRouting"), "chatgptAccountId")
            ?? Json.Text(Json.Get(account, "account"), "email");
        string? fingerprint = string.IsNullOrEmpty(identity) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return ParseUsage(limits, DateTimeOffset.UtcNow, fingerprint);
    }

    public static LiveUsage ParseUsage(JsonElement result, DateTimeOffset fetched, string? account)
    {
        var buckets = Json.Get(result, "rateLimitsByLimitId");
        var limits = buckets.ValueKind == JsonValueKind.Object ? Json.Get(buckets, "codex") : Json.Get(result, "rateLimits");
        if (limits.ValueKind != JsonValueKind.Object || Json.Text(limits, "limitId") is { } id && id != "codex")
            throw new IOException("Codex did not report the general plan allowance.");
        UsageWindow? Window(string key)
        {
            var w = Json.Get(limits, key);
            var used = Json.Get(w, "usedPercent");
            if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out double percent)
                || !double.IsFinite(percent) || percent < 0 || percent > 100) return null;
            var duration = Json.Get(w, "windowDurationMins");
            int? minutes = duration.TryGetInt32Safe(out int n) && n > 0 ? n : null;
            var reset = Json.Get(w, "resetsAt");
            DateTimeOffset? time = reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out long seconds)
                && seconds >= 0 && seconds <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            return new(percent, minutes, time);
        }
        var primary = Window("primary"); var secondary = Window("secondary");
        if (primary is null && secondary is null) throw new IOException("Codex has not reported any plan windows.");
        bool allowed = Json.Get(result, "ordinaryUsageAllowed").ValueKind == JsonValueKind.True;
        if (buckets.ValueKind == JsonValueKind.Object)
            foreach (var bucket in buckets.EnumerateObject())
                if (Blocked(bucket.Value)) allowed = false;
        allowed &= !Blocked(limits);
        // A malformed reported window is unavailable, even if another window can be displayed.
        allowed &= (Json.Get(limits, "primary").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || primary is not null)
            && (Json.Get(limits, "secondary").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || secondary is not null);
        return new(new(fetched, Json.Text(limits, "planType"), primary, secondary), account, allowed);
    }

    private static bool Blocked(JsonElement bucket) => Json.Get(bucket, "spendControlReached").ValueKind == JsonValueKind.True
        || Json.Get(bucket, "rateLimitReachedType").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        || new[] { "primary", "secondary" }.Any(key =>
        {
            var used = Json.Get(Json.Get(bucket, key), "usedPercent");
            return used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out double n) && n >= 100;
        });

    public async Task<string> ResumeDesktopAsync(InterruptedTask task, Action<string> queued, CancellationToken token)
    {
        var response = await RequestAsync("thread/read", new { threadId = task.ChatId, includeTurns = true }, token);
        var thread = Json.Get(response, "thread");
        var turns = Json.Get(thread, "turns");
        if (Json.Text(thread, "id") != task.ChatId || Json.Text(Json.Get(thread, "status"), "type") != "idle"
            || turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
            throw new IOException("Desktop chat is not idle in the shared runtime. Continue it manually in Codex.");
        var last = turns[turns.GetArrayLength() - 1];
        if (Json.Text(last, "id") != task.TurnId || Json.Text(last, "status") != "failed"
            || Json.Text(Json.Get(last, "error"), "codexErrorInfo") != "usageLimitExceeded")
            throw new IOException("Desktop chat changed since the usage limit. Continue it manually in Codex.");
        var list = await RequestAsync("thread/queue/list", new { threadId = task.ChatId }, token);
        if (Json.Get(list, "data").ValueKind != JsonValueKind.Array || Json.Get(list, "data").GetArrayLength() != 0)
            throw new IOException("Desktop chat already has queued work. Continue it manually in Codex.");
        var added = await RequestAsync("thread/queue/add", new
        {
            threadId = task.ChatId, clientUserMessageId = Guid.NewGuid().ToString(),
            input = new[] { new { type = "text", text = ResumeController.Prompt } }
        }, token);
        string queueId = Json.Text(Json.Get(added, "queuedSubmission"), "id") ?? throw new IOException("Desktop did not confirm the queued message.");
        bool confirmed = false;
        try
        {
            queued(queueId);
            var started = await RequestAsync("thread/queue/start", new { threadId = task.ChatId, queuedSubmissionId = queueId }, token);
            var turn = Json.Get(started, "turn");
            if (Json.Text(turn, "status") != "inProgress" || Json.Text(turn, "id") is not { Length: > 0 } turnId)
                throw new IOException("Desktop did not confirm a started turn. Check the chat before retrying.");
            confirmed = true;
            return turnId;
        }
        finally
        {
            if (!confirmed)
            {
                // Remove a known queued message if start fails or the user disables recovery.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await RequestAsync("thread/queue/delete", new { threadId = task.ChatId, queuedSubmissionId = queueId }, cleanup.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            }
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}

internal static class Json
{
    public static JsonElement Get(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value) ? value : default;
    public static string? Text(JsonElement obj, string name) => Get(obj, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static bool TryGetInt32Safe(this JsonElement value, out int n)
    {
        n = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out n);
    }
}
