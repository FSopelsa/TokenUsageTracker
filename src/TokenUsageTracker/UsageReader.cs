using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TokenUsageTracker;

public sealed record UsageWindow(double UsedPercent, int? Minutes, DateTimeOffset? ResetsAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
    public bool IsExpired(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
    public string Label => Minutes switch
    {
        300 => "5-hour window", 10080 => "Weekly window",
        >= 1440 => $"{Minutes / 1440}-day window",
        >= 60 => $"{Minutes / 60.0:0.#}-hour window",
        > 0 => $"{Minutes}-minute window", _ => "Usage window"
    };
}

public sealed record UsageSnapshot(DateTimeOffset Timestamp, string? Plan,
    UsageWindow? Primary, UsageWindow? Secondary);

public sealed record UsageResult(UsageSnapshot? Snapshot, string? Error);

public sealed class UsageReader
{
    private readonly Dictionary<string, (long Length, DateTime Modified, UsageSnapshot? Snapshot)> cache = new();

    public UsageResult Read(string codexHome)
    {
        try
        {
            string sessions = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(sessions))
                return new(null, "No sessions folder. Open Codex or choose its data folder in the menu.");
            string[] paths = Directory.GetFiles(sessions, "*.jsonl", SearchOption.AllDirectories);
            var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string old in cache.Keys.Where(p => !present.Contains(p)).ToArray()) cache.Remove(old);
            bool unreadable = false;
            foreach (string path in paths)
            {
                try
                {
                    var file = new FileInfo(path);
                    if (cache.TryGetValue(path, out var previous) && previous.Length == file.Length && previous.Modified == file.LastWriteTimeUtc)
                        continue;
                    cache[path] = (file.Length, file.LastWriteTimeUtc, ReadLatest(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Retry locked or rotating files next poll, preserving the last known reading.
                    unreadable = true;
                }
            }
            var latest = cache.Values.Select(v => v.Snapshot).OfType<UsageSnapshot>()
                .MaxBy(s => s.Timestamp);
            return new(latest, unreadable ? "Some session files could not be read; the reading may be out of date."
                : latest is null ? "No plan usage recorded yet. Send a message in Codex, then refresh." : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(null, "Cannot read the Codex folder. Check its location and permissions.");
        }
    }

    private static UsageSnapshot? ReadLatest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        long size = 64 * 1024;
        while (true)
        {
            long start = Math.Max(0, length - size);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, leaveOpen: true);
            if (start > 0) reader.ReadLine(); // The first line may begin in the middle of a JSON record.
            UsageSnapshot? latest = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var candidate = ParseLine(line);
                if (candidate is not null && (latest is null || candidate.Timestamp >= latest.Timestamp)) latest = candidate;
            }
            if (latest is not null || start == 0) return latest;
            size = Math.Min(length, size * 2);
        }
    }

    public static UsageSnapshot? ParseLine(string line)
    {
        if (!line.Contains("rate_limits", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || Text(payload, "type") != "token_count"
                || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object
                || !DateTimeOffset.TryParse(Text(root, "timestamp"), out var timestamp)) return null;
            string? id = Text(limits, "limit_id");
            if (id is not null && id != "codex") return null;
            var primary = ParseWindow(limits, "primary");
            var secondary = ParseWindow(limits, "secondary");
            return primary is null && secondary is null ? null : new(timestamp, Text(limits, "plan_type"), primary, secondary);
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { return null; }
    }

    private static UsageWindow? ParseWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object
            || !w.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out double percent) || !double.IsFinite(percent) || percent < 0 || percent > 100)
            return null;
        int? minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number
            && m.TryGetInt32(out int n) && n > 0 ? n : null;
        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long seconds)
            && seconds >= 0 && seconds <= 253402300799)
            resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new(percent, minutes, resets);
    }

    private static string? Text(JsonElement obj, string key) => obj.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
