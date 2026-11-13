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
        check(both.WindowSize(true) == (432, 180) && both.WindowSize(false) == (440, 400)
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
