using TokenUsageTracker;

internal static class TaskbarChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        UsageWindow Window(double used, int minutes, double hoursToReset = 2) => new(used, minutes, now.AddHours(hoursToReset));
        UsageSnapshot Snapshot(UsageWindow? primary, UsageWindow? secondary) => new(now.AddMinutes(-2), "plus", primary, secondary);

        var both = TaskbarReading.From(Snapshot(Window(10, 300), Window(97, 10080)), now);
        check(both.Level == UsageLevel.Critical && Math.Abs(both.Fraction - 0.03) < 1e-9 && both.Badge == "3",
            "Taskbar and tray show the tighter of the two windows");
        var expired = TaskbarReading.From(Snapshot(Window(99, 300, hoursToReset: -1), Window(40, 10080)), now);
        check(expired.Badge == "60" && expired.Level == UsageLevel.Normal,
            "A window whose reset has passed is skipped, not read as empty or full");
        check(TaskbarReading.From(null, now) == TaskbarReading.Empty
            && TaskbarReading.From(Snapshot(null, null), now) == TaskbarReading.Empty
            && TaskbarReading.From(Snapshot(Window(0, 300, hoursToReset: -1), null), now) == TaskbarReading.Empty,
            "No usable window shows no taskbar progress or tray number");
        check(TaskbarReading.Empty.Level == UsageLevel.None && TaskbarReading.Empty.Badge == "",
            "The empty reading carries no level or number");
        check(TaskbarReading.From(Snapshot(Window(0.1, 300), null), now).Badge == "99"
            && TaskbarReading.From(Snapshot(Window(99.6, 300), null), now).Badge == "0"
            && TaskbarReading.From(Snapshot(Window(0, 300), null), now).Badge == "100",
            "The tray number rounds down so it never overstates what is left");
        check(TaskbarReading.LevelOf(25.1) == UsageLevel.Normal && TaskbarReading.LevelOf(25) == UsageLevel.Low
            && TaskbarReading.LevelOf(10.1) == UsageLevel.Low && TaskbarReading.LevelOf(10) == UsageLevel.Critical,
            "Taskbar colours change at the widget's 25% and 10% thresholds");

        string text = TaskbarReading.TrayText("Codex", Snapshot(Window(10, 300), Window(97, 10080)), now, "2m");
        check(text.StartsWith("Codex: ", StringComparison.Ordinal) && text.Contains("5-hour") && text.Contains("Weekly")
            && text.EndsWith("2m ago", StringComparison.Ordinal),
            "Tray tooltip names both windows and the reading's age");
        check(!TaskbarReading.TrayText("Claude", Snapshot(Window(99, 300, hoursToReset: -1), Window(40, 10080)), now, "1h").Contains("5-hour"),
            "Tray tooltip leaves out a window whose reset has passed");
        check(TaskbarReading.TrayText("Claude", null, now, "1h") == "Claude Usage — open widget for status",
            "Tray tooltip without a reading points to the widget");
        string longName = TaskbarReading.TrayText(new string('x', 200), Snapshot(Window(10, 300), Window(97, 10080)), now, "2m");
        check(longName.Length <= TaskbarReading.TrayTextLimit, "Tray tooltip fits the Windows notification-icon limit");
    }
}
