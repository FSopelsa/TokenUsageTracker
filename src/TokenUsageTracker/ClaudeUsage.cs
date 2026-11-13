using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenUsageTracker;

public static class ClaudeUsage
{
    public static string ConfigFolder => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } path
        ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string CachePath(string folder) => Path.Combine(folder, "usage-widget", "usage.json");

    public static UsageSnapshot Parse(JsonElement data, DateTimeOffset receivedAt)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new JsonException("Expected Claude Code status-line JSON.");
        var limits = Json.Get(data, "rate_limits");
        UsageWindow? Window(string key, int minutes)
        {
            var window = Json.Get(limits, key);
            var used = Json.Get(window, "used_percentage");
            if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out double percent)
                || !double.IsFinite(percent) || percent < 0 || percent > 100) return null;
            return new(percent, minutes, UnixTime(Json.Get(window, "resets_at")));
        }
        var cache = Json.Get(data, "prompt_cache");
        var recache = Json.Get(cache, "recache_tokens_if_cold");
        var prompt = new PromptCache(UnixTime(Json.Get(cache, "expires_at")),
            recache.ValueKind == JsonValueKind.Number && recache.TryGetInt64(out long tokens) && tokens >= 0 ? tokens : null);
        return new(receivedAt, "Claude", Window("five_hour", 300), Window("seven_day", 10080),
            prompt.ExpiresAt is null && prompt.RecacheTokens is null ? null : prompt);
    }

    private static DateTimeOffset? UnixTime(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds)
        && seconds >= 0 && seconds <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    // Within one window usage only rises, so a lower value for the same reset comes from an idle session's older response.
    public static UsageWindow? Merge(UsageWindow? next, UsageWindow? last, DateTimeOffset now) =>
        next is null ? last is not null && !last.IsExpired(now) ? last : null
        : last is not null && next.ResetsAt is not null && last.ResetsAt == next.ResetsAt && last.UsedPercent > next.UsedPercent ? last : next;

    public static UsageSnapshot Capture(string input, string folder, DateTimeOffset receivedAt)
    {
        if (input.Length > 1_048_576) throw new IOException("Claude status-line data is too large.");
        using var doc = JsonDocument.Parse(input);
        var next = Parse(doc.RootElement, receivedAt);
        UsageSnapshot? last = null;
        try { last = Load(CachePath(folder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* A corrupt or unreadable cache is replaced. */ }
        bool unavailable = Json.Get(doc.RootElement, "rate_limits_available").ValueKind == JsonValueKind.False;
        bool reported = next.Primary is not null || next.Secondary is not null;
        if (!unavailable && !reported && next.Cache is null) return last ?? next;
        // A windowless session keeps the last reading; a partial report replaces missing windows.
        var five = unavailable ? null : reported ? next.Primary is null ? null : Merge(next.Primary, last?.Primary, receivedAt) : Merge(null, last?.Primary, receivedAt);
        var week = unavailable ? null : reported ? next.Secondary is null ? null : Merge(next.Secondary, last?.Secondary, receivedAt) : Merge(null, last?.Secondary, receivedAt);
        bool stale = next.Primary is not null && five != next.Primary || next.Secondary is not null && week != next.Secondary;
        var snapshot = new UsageSnapshot(last is null || unavailable || reported && !stale ? receivedAt : last.Timestamp, "Claude", five, week, next.Cache ?? last?.Cache);
        object? Window(UsageWindow? w) => w is null ? null : new { used_percentage = w.UsedPercent, resets_at = w.ResetsAt?.ToUnixTimeSeconds() };
        // Store only allowances, cache expiry/size and receipt time; status-line input also contains private session metadata.
        AtomicWrite(CachePath(folder), JsonSerializer.Serialize(new
        {
            received_at = snapshot.Timestamp,
            rate_limits = new { five_hour = Window(five), seven_day = Window(week) },
            prompt_cache = snapshot.Cache is { } c ? new { expires_at = c.ExpiresAt?.ToUnixTimeSeconds(), recache_tokens_if_cold = c.RecacheTokens } : null
        }));
        return snapshot;
    }

    private static UsageSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 1_048_576) throw new IOException("Claude usage snapshot is too large.");
        using var doc = JsonDocument.Parse(stream);
        if (!DateTimeOffset.TryParse(Json.Text(doc.RootElement, "received_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var time) || time > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new JsonException("Invalid Claude snapshot timestamp.");
        return Parse(doc.RootElement, time);
    }

    public static UsageResult Read(string folder)
    {
        try
        {
            var snapshot = Load(CachePath(folder));
            if (snapshot is null) return new(null, "Choose Set up Claude usage in Options, then use Claude Code. Requires reported subscription rate limits.");
            return snapshot.Primary is null && snapshot.Secondary is null
                ? new(null, "Claude Code reported no subscription windows. Use an eligible subscription and wait for a new response; API usage and expired windows are not shown.")
                : new(snapshot, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new(null, "Cannot read Claude usage: " + e.Message); }
    }

    public static int RunStatusLine(TextReader input, TextWriter output, TextWriter error, string folder)
    {
        try
        {
            var snapshot = Capture(input.ReadToEnd(), folder, DateTimeOffset.UtcNow);
            string Display(UsageWindow? w) => w is null || w.IsExpired(DateTimeOffset.UtcNow) ? "—" : w.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            output.WriteLine($"Claude remaining · 5h {Display(snapshot.Primary)} · weekly {Display(snapshot.Secondary)}");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { error.WriteLine("Claude usage widget: " + e.Message); return 1; }
    }

    public static string BridgeCommand(string executable)
    {
        string script = "$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding; "
            + "$data = [Console]::In.ReadToEnd(); $data | & '" + executable.Replace("'", "''") + "' --claude-statusline; exit $LASTEXITCODE";
        return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public static bool ConfigureStatusLine(string folder, string executable)
    {
        string path = Path.Combine(folder, "settings.json");
        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        var settings = original is null ? new JsonObject() : JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new JsonException("Claude settings must be a JSON object.");
        string command = BridgeCommand(executable);
        string marker = Path.Combine(folder, "usage-widget", "statusline-command.txt");
        string? previous = File.Exists(marker) ? File.ReadAllText(marker) : null;
        var status = settings["statusLine"];
        if (status is not null)
        {
            if (status is not JsonObject obj || obj["command"] is not JsonValue value || !value.TryGetValue<string>(out var existing)) return false;
            if (existing == command && obj["refreshInterval"] is not null) return true;
            if (!string.IsNullOrWhiteSpace(existing) && existing != previous) return false;
        }
        var line = status as JsonObject ?? new JsonObject();
        line["type"] = "command"; line["command"] = command;
        // Event-driven updates go quiet while a session waits on subagents; a timer keeps the snapshot current. Keeps a user-set interval.
        line["refreshInterval"] ??= 60;
        if (status is null) settings["statusLine"] = line;
        Directory.CreateDirectory(folder);
        if (original is not null && !File.Exists(path + ".usage-widget.bak")) File.Copy(path, path + ".usage-widget.bak");
        if ((File.Exists(path) ? File.ReadAllText(path) : null) != original)
            throw new IOException("Claude settings changed during setup. Try again.");
        AtomicWrite(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AtomicWrite(marker, command);
        return true;
    }

    public enum StatusLineState { NotConfigured, Connected, OtherWidgetPath, Custom, Unreadable }

    // Reports whether Claude settings point at this widget copy, at a widget copy that was moved, or at something else.
    public static StatusLineState GetStatusLineState(string folder, string executable)
    {
        try
        {
            string path = Path.Combine(folder, "settings.json");
            if (!File.Exists(path)) return StatusLineState.NotConfigured;
            var settings = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new JsonException("Claude settings must be a JSON object.");
            var status = settings["statusLine"];
            if (status is null) return StatusLineState.NotConfigured;
            if (status is not JsonObject obj || obj["command"] is not JsonValue value || !value.TryGetValue<string>(out var command))
                return StatusLineState.Custom;
            if (command == BridgeCommand(executable)) return StatusLineState.Connected;
            string marker = Path.Combine(folder, "usage-widget", "statusline-command.txt");
            return File.Exists(marker) && command == File.ReadAllText(marker) ? StatusLineState.OtherWidgetPath : StatusLineState.Custom;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return StatusLineState.Unreadable; }
    }

    private static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
