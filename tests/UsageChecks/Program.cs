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
