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
