using System;

namespace TokenUsageTracker;

/// <summary>
/// Colours for percentages, bars and tray badges. The percentage text always uses the warning palette.
/// In Both mode, Claude's bars and badges use their own shades so the two tools are told apart at a glance.
/// </summary>
public static class UsageColors
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public double Luminance
        {
            get
            {
                static double Channel(byte value)
                {
                    double c = value / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
            }
        }
    }

    public static readonly Rgb Card = new(23, 30, 38);
    public static readonly Rgb Green = new(190, 240, 120);
    public static readonly Rgb Amber = new(249, 198, 110);
    public static readonly Rgb Red = new(255, 132, 132);
    public static readonly Rgb Muted = new(155, 169, 183);
    public static readonly Rgb ClaudeGreen = new(111, 216, 192);
    public static readonly Rgb ClaudeAmber = new(245, 158, 91);
    public static readonly Rgb ClaudeRose = new(240, 98, 146);

    /// <summary>Percentage text: the warning palette for every tool. <see cref="UsageLevel.None"/> means expired, stale or missing.</summary>
    public static Rgb Text(UsageLevel level) => level switch
    {
        UsageLevel.Critical => Red,
        UsageLevel.Low => Amber,
        UsageLevel.Normal => Green,
        _ => Muted
    };

    /// <summary>Bars and tray badges: Claude's own shades only when both tools are shown.</summary>
    public static Rgb Bar(UsageLevel level, string provider, bool both) => !both || provider != "Claude" ? Text(level) : level switch
    {
        UsageLevel.Critical => ClaudeRose,
        UsageLevel.Low => ClaudeAmber,
        UsageLevel.Normal => ClaudeGreen,
        _ => Muted
    };

    public static double Contrast(Rgb a, Rgb b)
    {
        double light = Math.Max(a.Luminance, b.Luminance), dark = Math.Min(a.Luminance, b.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }
}
