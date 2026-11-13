using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TokenUsageTracker;

public partial class MainWindow : Window
{
    private const string CodexDashboard = "https://chatgpt.com/codex/settings/usage";
    private const string ClaudeDashboard = "https://claude.ai/settings/usage";

    /// <summary>One tool's reading, ready to render. Status is the single-tool footer line; Short is its part of the Both-mode footer.</summary>
    private sealed record ProviderReading(string Provider, UsageSnapshot? Snapshot, string Plan, string Status, string Short, bool Warn,
        string Extra, bool ExtraWarn, bool ExtraNotable, string ExtraTip, string Details, string? CompactStatus);

    private sealed record Cell(Grid Heading, TextBlock Label, TextBlock Value, ProgressBar Bar, TextBlock Reset, string DefaultLabel);

    private readonly WidgetSettings settings = WidgetSettings.Load();
    private readonly UsageReader reader = new();
    private readonly ResumeController resume = new();
    private readonly ClaudeResumeNotices notices = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ProviderReading> latest = new();
    private readonly Cell[] mainCells, claudeCells;
    private LiveUsage? lastLive;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu = new();
    private readonly System.Drawing.Icon appIcon;
    private TrayBadge? badge;
    private TaskbarReading shownReading = TaskbarReading.Empty;
    private Forms.NotifyIcon? claudeTray;
    private TrayBadge? claudeBadge;
    private TaskbarReading shownClaudeReading = TaskbarReading.Empty;
    private string? balloonCommand;
    private bool layoutBoth;
    private bool reading;
    // Seams for the window checks: they must never start Codex or show real notifications.
    private Func<string, CancellationToken, Task<LiveUsage>> readLive = ReadLiveAsync;
    private Action<ClaudeInterruption> notifyResume;
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(190, 240, 120));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(249, 198, 110));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 132, 132));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(155, 169, 183));
    private static readonly Brush SourceDim = new SolidColorBrush(Color.FromRgb(113, 130, 146));
    private static readonly Dictionary<UsageColors.Rgb, Brush> LevelBrushes = new();

    public MainWindow()
    {
        InitializeComponent();
        mainCells = new[]
        {
            new Cell(PrimaryHeading, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, "5-hour window"),
            new Cell(SecondaryHeading, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, "Weekly window")
        };
        claudeCells = new[]
        {
            new Cell(ClaudePrimaryHeading, ClaudePrimaryLabel, ClaudePrimaryValue, ClaudePrimaryBar, ClaudePrimaryReset, "5-hour window"),
            new Cell(ClaudeSecondaryHeading, ClaudeSecondaryLabel, ClaudeSecondaryValue, ClaudeSecondaryBar, ClaudeSecondaryReset, "Weekly window")
        };
        notifyResume = ShowResumeBalloon;
        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information;
        trayMenu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(RestoreWidget));
        trayMenu.Items.Add("Refresh usage", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync()));
        trayMenu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));
        tray = NewTray("Codex Usage — double-click to show");
        TaskbarItemInfo = new TaskbarItemInfo();
        ShowInTaskbar = settings.ShowInTaskbar;
        AutoResumeToggle.IsChecked = settings.AutoResume;
        Topmost = settings.AlwaysOnTop;
        SetCompact(settings.CompactMode, save: false);
        UpdateProvider();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdatePin();
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            RestorePosition();
            ConfigureResume();
            timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            timer.Stop(); lifetime.Cancel(); resume.Dispose();
            tray.Dispose(); claudeTray?.Dispose(); badge?.Dispose(); claudeBadge?.Dispose(); appIcon.Dispose();
        };
        Closing += (_, _) => SaveSettings();
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    private Forms.NotifyIcon NewTray(string text)
    {
        var icon = new Forms.NotifyIcon { Text = text, Icon = appIcon, Visible = true, ContextMenuStrip = trayMenu };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWidget);
        icon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(CopyBalloonCommand);
        return icon;
    }

    private string[] ShownTools() => settings.Provider switch
    {
        "Both" => new[] { "Codex", "Claude" },
        "Claude" => new[] { "Claude" },
        _ => new[] { "Codex" }
    };

    private static async Task<LiveUsage> ReadLiveAsync(string folder, CancellationToken token)
    {
        using var client = await CodexClient.ConnectAsync(folder, desktop: false, token);
        return await client.ReadUsageAsync(token);
    }

    private async Task RefreshAsync()
    {
        if (reading) return;
        reading = true;
        string mode = settings.Provider;
        try
        {
            // Each tool renders as soon as its own read finishes, so a slow Codex read never holds back Claude.
            var reads = new List<Task>();
            if (settings.ShowsCodex) reads.Add(ShowWhenReadAsync(ReadCodexAsync(settings.DataFolder)));
            if (settings.ShowsClaude) reads.Add(ShowWhenReadAsync(ReadClaudeAsync(ClaudeUsage.ConfigFolder)));
            await Task.WhenAll(reads);
        }
        finally
        {
            reading = false;
            if (mode != settings.Provider && !lifetime.IsCancellationRequested) _ = RefreshAsync();
        }
    }

    private async Task ShowWhenReadAsync(Task<ProviderReading?> read)
    {
        if (await read is { } result && !lifetime.IsCancellationRequested) ShowReading(result);
    }

    private static bool IsStale(UsageSnapshot? snapshot, DateTimeOffset now) => snapshot is not null && now - snapshot.Timestamp > TimeSpan.FromMinutes(15);
    private static string PlanOf(UsageSnapshot? snapshot, string fallback) => snapshot?.Plan?.Replace('_', ' ').ToUpperInvariant() ?? fallback;

    private async Task<ProviderReading?> ReadCodexAsync(string folder)
    {
        bool Current() => settings.ShowsCodex && settings.DataFolder == folder && !lifetime.IsCancellationRequested;
        LiveUsage? live = null;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            live = await readLive(folder, timeout.Token);
            if (Current()) lastLive = live;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
        {
            error = e is OperationCanceledException ? "Live usage timed out. Check Codex sign-in and connection." : e.Message;
        }
        if (!Current()) return null;
        var result = live is not null ? new UsageResult(live.Snapshot, null)
            : lastLive is not null ? new UsageResult(lastLive.Snapshot, error)
            : await Task.Run(() => reader.Read(folder));
        if (!Current()) return null;
        var now = DateTimeOffset.Now;
        var snapshot = result.Snapshot;
        string source = live is not null ? "Live account usage" : lastLive is not null ? "Last live reading" : "Local snapshot";
        string shortSource = live is not null ? "live" : lastLive is not null ? "last live" : "local";
        string status = snapshot is null ? "Waiting for plan usage"
            : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
            : $"{source} · {Age(now - snapshot.Timestamp)} ago";
        string recovery;
        try
        {
            await resume.TickAsync(live, lifetime.Token);
            recovery = resume.Status;
            if (settings.AutoResume && settings.ResumeTarget == "Desktop" && !File.Exists(CodexClient.DesktopSocket(folder)))
                recovery = "Desktop connection unavailable: no shared control socket. CLI mode is available in Options.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { recovery = "Recovery paused: " + e.Message; }
        if (!Current()) return null;
        string? compact = settings.AutoResume
            ? recovery.StartsWith("Desktop connection unavailable", StringComparison.Ordinal) ? "Desktop connection unavailable" : $"{source} · {resume.PendingCount} pending"
            : null;
        return new("Codex", snapshot, PlanOf(snapshot, "LOCAL"), status,
            snapshot is null ? "Codex waiting" : $"Codex {shortSource} {Age(now - snapshot.Timestamp)} ago",
            error is not null || result.Error is not null || IsStale(snapshot, now),
            settings.AutoResume ? recovery : "Auto-resume off · refreshes every 15s", false, false, recovery,
            $"{source}\n{error ?? result.Error}\n{recovery}\n{folder}", compact);
    }

    private async Task<ProviderReading?> ReadClaudeAsync(string folder)
    {
        bool Current() => settings.ShowsClaude && ClaudeUsage.ConfigFolder == folder && !lifetime.IsCancellationRequested;
        var result = await Task.Run(() => ClaudeUsage.Read(folder));
        if (!Current()) return null;
        var now = DateTimeOffset.Now;
        var snapshot = result.Snapshot;
        const string source = "Claude Code snapshot";
        string status = snapshot is null ? "Waiting for Claude Code · see Options"
            : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
            : $"{source} · {Age(now - snapshot.Timestamp)} ago";
        bool needsSetup = await Task.Run(() => ClaudeUsage.GetStatusLineState(folder, Environment.ProcessPath!)) == ClaudeUsage.StatusLineState.OtherWidgetPath;
        if (!Current()) return null;
        if (notices.Enabled)
        {
            var since = notices.ScanSince(DateTimeOffset.UtcNow);
            var found = await Task.Run(() => transcripts.Interruptions(folder, since));
            if (!Current()) return null;
            foreach (var interruption in notices.Tick(found, snapshot, DateTimeOffset.UtcNow)) notifyResume(interruption);
        }
        string recovery = needsSetup ? "Claude status line points to an older widget location · Options → Set up Claude usage"
            : !notices.Enabled ? "Claude usage display only"
            : notices.Ready.Count > 0 ? $"{notices.Ready.Count} Claude session(s) can continue · Options → Claude sessions to resume"
            : "Notifies when a Claude session stopped by a usage limit can continue";
        var cache = snapshot?.Cache;
        string cacheNote = cache is null ? "" : $"\n{cache.Describe(now)}\nPrompt cache of the most recently updated Claude Code session.";
        return new("Claude", snapshot, PlanOf(snapshot, "CLAUDE"), status,
            snapshot is null ? "Claude waiting" : $"Claude snapshot {Age(now - snapshot.Timestamp)} ago",
            result.Error is not null || IsStale(snapshot, now),
            needsSetup ? "Claude status line needs setup · see Options" : cache?.Describe(now) ?? "From Claude Code · refreshes every 15s",
            needsSetup || cache?.ExpiresAt is { } cold && cold > now && cold - now <= TimeSpan.FromMinutes(2),
            needsSetup || cache is not null, recovery + cacheNote,
            $"{source}\n{result.Error}\n{recovery}{cacheNote}\n{folder}", null);
    }

    private void ShowReading(ProviderReading r)
    {
        if (!(r.Provider == "Claude" ? settings.ShowsClaude : settings.ShowsCodex)) return;
        latest[r.Provider] = r;
        var now = DateTimeOffset.Now;
        bool claudeBlock = r.Provider == "Claude" && settings.Both;
        var cells = claudeBlock ? claudeCells : mainCells;
        bool stale = IsStale(r.Snapshot, now);
        var readAt = r.Snapshot?.Timestamp ?? now;
        ShowWindow(r.Snapshot?.Primary, cells[0], r.Provider, now, readAt, stale);
        ShowWindow(r.Snapshot?.Secondary, cells[1], r.Provider, now, readAt, stale);
        (claudeBlock ? ClaudePlanText : PlanText).Text = r.Plan;
        ShowFooter();
        ShowOnTaskbar(now);
    }

    private void ShowFooter()
    {
        var shown = ShownTools().Select(t => latest.GetValueOrDefault(t)).OfType<ProviderReading>().ToList();
        if (shown.Count == 0) return;
        if (!settings.Both)
        {
            var r = shown[0];
            StatusText.Text = settings.CompactMode && r.CompactStatus is { } pending ? $"{r.Plan} · {pending}"
                : settings.CompactMode && r.Snapshot is not null ? $"{r.Plan} · {r.Status}" : r.Status;
            StatusText.Foreground = r.Warn ? Amber : Muted;
            SourceText.Text = r.Extra;
            SourceText.Foreground = r.ExtraWarn ? Amber : SourceDim;
            SourceText.ToolTip = r.ExtraTip;
            Card.ToolTip = StatusText.ToolTip = AutoResumeToggle.ToolTip = r.Details;
        }
        else
        {
            StatusText.Text = string.Join(" · ", shown.Select(r => r.Short));
            StatusText.Foreground = shown.Any(r => r.Warn) ? Amber : Muted;
            // A Claude setup warning or cache countdown is more urgent than the Codex auto-resume line.
            var extra = shown.FirstOrDefault(r => r.ExtraNotable) ?? shown[0];
            SourceText.Text = extra.Extra;
            SourceText.Foreground = extra.ExtraWarn ? Amber : SourceDim;
            SourceText.ToolTip = string.Join("\n\n", shown.Select(r => r.ExtraTip));
            Card.ToolTip = StatusText.ToolTip = AutoResumeToggle.ToolTip = string.Join("\n\n", shown.Select(r => $"{r.Provider}\n{r.Details}"));
        }
        SourceText.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip = null;
    }

    private static Brush BrushOf(UsageColors.Rgb color)
    {
        if (!LevelBrushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            LevelBrushes[color] = brush;
        }
        return brush;
    }

    private void ShowWindow(UsageWindow? w, Cell cell, string provider, DateTimeOffset now, DateTimeOffset readAt, bool stale = false)
    {
        // Compact Both mode has no section headings, so each label names its tool.
        string prefix = settings.Both && settings.CompactMode ? (provider == "Claude" ? "Claude " : "Codex ") : "";
        string Label(string text) => prefix.Length == 0 ? text : prefix + text.Replace(" window", "", StringComparison.Ordinal);
        if (w is null)
        {
            cell.Label.Text = Label(cell.DefaultLabel);
            cell.Value.Text = "—"; cell.Value.Foreground = Muted; cell.Bar.Value = 0;
            cell.Reset.Text = "No window reported"; cell.Reset.ToolTip = null;
            return;
        }
        cell.Label.Text = Label(w.Label);
        bool expired = w.IsExpired(now);
        var level = expired || stale ? UsageLevel.None : TaskbarReading.LevelOf(w.Remaining);
        cell.Value.Foreground = BrushOf(UsageColors.Text(level));
        cell.Bar.Foreground = BrushOf(UsageColors.Bar(level, provider, settings.Both));
        cell.Value.Text = expired ? "—" : w.Remaining.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        cell.Bar.Value = expired ? 0 : w.Remaining;
        var empty = expired ? null : w.RunsOutAt(readAt);
        if (empty <= now) empty = null; // A stale reading's projection that already passed says nothing useful.
        string forecast = empty is { } at ? " · empty ~" + (at - now < TimeSpan.FromDays(1) ? "" : at.ToLocalTime().ToString("ddd ", CultureInfo.CurrentCulture))
            + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "";
        cell.Reset.Text = expired ? "Reset passed · waiting for " + provider
            : w.ResetsAt is { } time ? "Resets in " + Countdown(time - now) + forecast : "Reset time unavailable";
        cell.Reset.ToolTip = w.ResetsAt?.ToLocalTime().ToString("f")
            + (empty is { } e ? $"\nAt the average pace since this window opened, it runs out around {e.ToLocalTime():f}." : "");
    }

    /// <summary>One tray number per shown tool, and taskbar progress and overlay for whichever tool has less room.</summary>
    private void ShowOnTaskbar(DateTimeOffset now)
    {
        var tools = ShownTools();
        UsageSnapshot? Snapshot(string tool) => latest.TryGetValue(tool, out var r) ? r.Snapshot : null;
        string Text(string tool) => TaskbarReading.TrayText(tool, Snapshot(tool), now, Snapshot(tool) is { } s ? Age(now - s.Timestamp) : "");
        UpdateTray(tray, ref badge, ref shownReading, tools[0], Snapshot(tools[0]), now);
        tray.Text = Text(tools[0]);
        if (claudeTray is not null)
        {
            UpdateTray(claudeTray, ref claudeBadge, ref shownClaudeReading, "Claude", Snapshot("Claude"), now);
            claudeTray.Text = Text("Claude");
        }
        var (provider, reading) = TaskbarReading.Lowest(tools.Select(t => (t, Snapshot(t))), now);
        TaskbarItemInfo.Description = string.Join("\n", tools.Select(Text));
        TaskbarItemInfo.ProgressState = reading.Level switch
        {
            UsageLevel.Normal => TaskbarItemProgressState.Normal,
            UsageLevel.Low => TaskbarItemProgressState.Paused,
            UsageLevel.Critical => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None
        };
        TaskbarItemInfo.ProgressValue = reading.Fraction;
        TaskbarItemInfo.Overlay = provider.Length == 0 ? null : provider == tools[0] ? badge?.Image : claudeBadge?.Image;
    }

    private void UpdateTray(Forms.NotifyIcon icon, ref TrayBadge? current, ref TaskbarReading shown, string tool, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var reading = TaskbarReading.From(snapshot, now);
        if (reading.Badge == shown.Badge && reading.Level == shown.Level) return;
        var previous = current;
        current = null;
        try
        {
            if (reading.Level != UsageLevel.None)
                current = TrayBadge.Render(reading.Badge, reading.Level, Forms.SystemInformation.SmallIconSize.Width, tool, settings.Both);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException) { current = null; }
        icon.Icon = current?.Icon ?? appIcon;
        shown = current is null ? TaskbarReading.Empty : reading;
        previous?.Dispose();
    }

    /// <summary>Badges change shape and colour with the mode, so a mode change starts the trays afresh.</summary>
    private void ResetTrays()
    {
        tray.Icon = appIcon;
        TaskbarItemInfo.Overlay = null;
        badge?.Dispose(); badge = null;
        shownReading = TaskbarReading.Empty;
        if (claudeTray is not null) claudeTray.Icon = appIcon;
        claudeBadge?.Dispose(); claudeBadge = null;
        shownClaudeReading = TaskbarReading.Empty;
        if (!settings.Both && claudeTray is not null) { claudeTray.Visible = false; claudeTray.Dispose(); claudeTray = null; }
        if (settings.Both && claudeTray is null) claudeTray = NewTray("Claude Usage — double-click to show");
    }

    private void ShowResumeBalloon(ClaudeInterruption interruption)
    {
        balloonCommand = ClaudeResumeNotices.Command(interruption);
        (claudeTray ?? tray).ShowBalloonTip(10_000, "Claude usage has reset", ClaudeResumeNotices.Message(interruption), Forms.ToolTipIcon.Info);
    }

    private void CopyBalloonCommand()
    {
        if (balloonCommand is { } command) CopyResumeCommand(command);
        RestoreWidget();
    }

    private void CopyResumeCommand(string command)
    {
        try { Clipboard.SetText(command); }
        catch (ExternalException e)
        { MessageBox.Show(this, $"Could not copy to the clipboard: {e.Message}\n\n{command}", "Resume Claude session", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string Countdown(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    private static string Age(TimeSpan span) => span.TotalSeconds < 60 ? "a moment"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : span.TotalDays < 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";

    private void DragWidget(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is ButtonBase) return;
        if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); SaveSettings(); }
    }

    private void RestorePosition()
    {
        Left = settings.Left ?? SystemParameters.WorkArea.Right - Width - 24;
        Top = settings.Top ?? SystemParameters.WorkArea.Bottom - Height - 24;
        // Screen working areas use physical pixels; convert the rendered window before checking overlap.
        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = new Rect(PointToScreen(new Point(0, 0)), new Size(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY));
        bool reachable = double.IsFinite(Left) && double.IsFinite(Top) && Array.Exists(Forms.Screen.AllScreens, s =>
        {
            var r = s.WorkingArea;
            var overlap = Rect.Intersect(bounds, new Rect(r.X, r.Y, r.Width, r.Height));
            return !overlap.IsEmpty && overlap.Width >= bounds.Width - 20 && overlap.Height >= bounds.Height - 20;
        });
        if (!reachable)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Bottom - Height - 24;
        }
    }

    private void SaveSettings()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        settings.Left = Left; settings.Top = Top; settings.AlwaysOnTop = Topmost;
        settings.RememberSize(settings.CompactMode, Width, Height);
        if (!settings.Save()) { StatusText.Text = "Could not save preferences"; StatusText.ToolTip = "Check LocalAppData write permissions."; }
    }

    internal void RestoreWidget() { Show(); WindowState = WindowState.Normal; RestorePosition(); Activate(); }
    private void UpdatePin() { PinButton.Foreground = Topmost ? Green : Muted; PinButton.ToolTip = Topmost ? "Always on top: on" : "Always on top: off"; }
    private void ToggleCompact(object sender, RoutedEventArgs e) => SetCompact(!settings.CompactMode, save: true);

    private void SetCompact(bool compact, bool save)
    {
        if (save) SaveSettings();
        settings.CompactMode = compact;
        bool both = layoutBoth = settings.Both;
        bool sideBySide = compact || both;
        MinWidth = both ? compact ? 300 : 380 : compact ? 240 : 280;
        MinHeight = both ? compact ? 160 : 370 : compact ? 120 : 384;
        MaxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        MaxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        var size = settings.WindowSize(compact);
        Width = Math.Clamp(size.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(size.Height, MinHeight, MaxHeight);
        Card.Margin = new Thickness(compact ? 6 : 9);
        Card.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(19, 14, 19, 16);
        PlanRow.Visibility = SourceText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ClaudeBlock.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        ClaudePlanRow.Visibility = both && !compact ? Visibility.Visible : Visibility.Collapsed;
        ClaudeBlock.Margin = new Thickness(0, both && compact ? 4 : 0, 0, 0);
        PrimaryPanel.Margin = new Thickness(0, sideBySide ? 3 : 9, sideBySide ? 8 : 0, 0);
        SecondaryPanel.Margin = new Thickness(sideBySide ? 8 : 0, sideBySide ? 3 : 16, 0, 0);
        ClaudePrimaryPanel.Margin = new Thickness(0, compact ? 3 : 6, 8, 0);
        ClaudeSecondaryPanel.Margin = new Thickness(8, compact ? 3 : 6, 0, 0);
        UsageGrid.RowDefinitions[1].Height = sideBySide ? new GridLength(0) : GridLength.Auto;
        UsageGrid.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(SecondaryPanel, sideBySide ? 0 : 1);
        Grid.SetColumn(SecondaryPanel, sideBySide ? 1 : 0);
        foreach (var cell in mainCells.Concat(claudeCells))
        {
            cell.Label.FontSize = compact ? 10 : both ? 11 : 12;
            cell.Reset.FontSize = compact ? 9 : both ? 10 : 11;
            cell.Reset.Margin = new Thickness(0, compact ? 3 : both ? 5 : 7, 0, 0);
            // Compact Both mode stays concise: reset lines move to full-size mode.
            cell.Reset.Visibility = both && compact ? Visibility.Collapsed : Visibility.Visible;
            cell.Bar.Height = compact ? 4 : 5;
        }
        PrimaryBar.Margin = both ? new Thickness(0, compact ? 3 : 5, 0, 0) : compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 5, 0, 0);
        SecondaryBar.Margin = both ? new Thickness(0, compact ? 3 : 5, 0, 0) : compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 7, 0, 0);
        ClaudePrimaryBar.Margin = ClaudeSecondaryBar.Margin = new Thickness(0, compact ? 3 : 5, 0, 0);
        Footer.Margin = new Thickness(0, compact ? 8 : 14, 0, 0);
        Footer.Padding = new Thickness(0, compact ? 6 : 11, 0, 0);
        ModeButton.ToolTip = compact ? "Switch to full-size mode" : "Switch to compact mode";
        AutomationProperties.SetName(ModeButton, ModeButton.ToolTip.ToString());
        ModeButton.Width = PinButton.Width = RefreshButton.Width = compact ? 26 : 28;
        ModeButton.Height = PinButton.Height = RefreshButton.Height = compact ? 24 : 28;
        UpdateResponsiveLayout();
        // Labels carry the tool name only in compact Both mode; redraw what is already known.
        foreach (var known in latest.Values.ToList()) ShowReading(known);
        if (save) SaveSettings();
    }

    private void UpdateResponsiveLayout()
    {
        bool compact = settings.CompactMode, both = settings.Both;
        TaskLists.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FiveHourTasks.IsEnabled = WeeklyTasks.IsEnabled = true;
        bool narrow = Width < (both ? compact ? 380 : 420 : compact ? 360 : 310);
        // Only compact cells are narrow enough to need the percentage under its label; full Both cells keep them on one line.
        bool stacked = compact && narrow;
        AutoResumeToggle.Visibility = narrow || settings.Provider == "Claude" ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = compact && Height < (both ? 180 : 160) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var cell in mainCells.Concat(claudeCells))
        {
            cell.Heading.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
            Grid.SetRow(cell.Value, stacked ? 1 : 0);
            cell.Value.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        PrimaryValue.FontSize = both || stacked ? 22 : compact ? 26 : 35;
        SecondaryValue.FontSize = both || compact ? 22 : 25;
        ClaudePrimaryValue.FontSize = ClaudeSecondaryValue.FontSize = 22;
        Card.Padding = compact ? new Thickness(narrow ? 8 : 12, narrow ? 4 : 6, narrow ? 8 : 12, narrow ? 4 : 6) : new Thickness(19, 14, 19, 16);
    }

    private void ResizeWindow(double horizontal, double vertical)
    {
        Width = Math.Clamp(Width + horizontal, MinWidth, MaxWidth);
        Height = Math.Clamp(Height + vertical, MinHeight, MaxHeight);
    }

    private void ResizeDragged(object sender, DragDeltaEventArgs e) => ResizeWindow(e.HorizontalChange, e.VerticalChange);
    private void ResizeCompleted(object sender, DragCompletedEventArgs e) => SaveSettings();
    private void ResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        ResizeWindow(e.Key == Key.Left ? -10 : e.Key == Key.Right ? 10 : 0,
            e.Key == Key.Up ? -10 : e.Key == Key.Down ? 10 : 0);
        SaveSettings();
        e.Handled = true;
    }

    private void TogglePin(object sender, RoutedEventArgs e) { Topmost = !Topmost; UpdatePin(); SaveSettings(); }
    private void AutoResumeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        settings.AutoResume = AutoResumeToggle.IsChecked == true;
        ConfigureResume();
    }

    private void ConfigureResume()
    {
        try { resume.Configure(settings.ShowsCodex && settings.AutoResume, settings.ResumeTarget, settings.DataFolder, DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings.AutoResume = false; AutoResumeToggle.IsChecked = false;
            StatusText.Text = "Could not save recovery state"; StatusText.ToolTip = e.Message;
        }
        notices.Configure(settings.ShowsClaude && settings.ClaudeResumeNotice, DateTimeOffset.UtcNow);
        SaveSettings();
        _ = RefreshAsync();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenMenu(object sender, RoutedEventArgs e) => ShowMenu();

    private void UpdateProvider()
    {
        latest.Clear();
        bool both = settings.Both;
        Title = both ? "Token Usage" : settings.Provider + " Usage";
        ProviderText.Text = both ? "Codex + Claude" : settings.Provider;
        RefreshButton.ToolTip = both ? "Refresh Codex and reread Claude Code (F5)"
            : settings.Provider == "Claude" ? "Reread Claude Code snapshot (F5)" : "Refresh live usage (F5)";
        AutomationProperties.SetName(RefreshButton, RefreshButton.ToolTip.ToString());
        var now = DateTimeOffset.Now;
        foreach (var cell in mainCells) ShowWindow(null, cell, both ? "Codex" : settings.Provider, now, now);
        foreach (var cell in claudeCells) ShowWindow(null, cell, "Claude", now, now);
        PlanLabel.Text = both ? "CODEX" : "PLAN REMAINING";
        PlanText.Text = both ? "CODEX" : settings.Provider.ToUpperInvariant();
        ClaudePlanText.Text = "CLAUDE";
        ResetTrays();
        ShowOnTaskbar(now);
        StatusText.Text = "Waiting for " + (both ? "Codex and Claude Code" : settings.Provider) + " usage";
        StatusText.Foreground = Muted;
        SourceText.Text = "Refreshes every 15s"; SourceText.Foreground = SourceDim;
        Card.ToolTip = null;
        // Both mode has its own layout and sizes; a change between one tool and two re-applies them.
        if (layoutBoth != both) SetCompact(settings.CompactMode, save: false);
        else UpdateResponsiveLayout();
    }

    private void SetProvider(string provider)
    {
        if (settings.Provider == provider) return;
        SaveSettings(); // Remember the size of the layout being left.
        settings.Provider = provider;
        lastLive = null;
        UpdateProvider();
        ConfigureResume();
    }

    private void SetupClaude()
    {
        try
        {
            bool configured = ClaudeUsage.ConfigureStatusLine(ClaudeUsage.ConfigFolder, Environment.ProcessPath!);
            MessageBox.Show(this, configured
                ? "Claude Code usage display is connected. Use Claude Code and wait for a response, then refresh the widget. Project or managed status-line settings may override this setup."
                : "Your custom Claude status line was left unchanged. Forward its original JSON input to TokenUsageTracker.exe --claude-statusline and discard the bridge output. See the Claude setup section in README.md.",
                "Claude usage setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, e.Message, "Claude setup failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); return item; }
        void Add(string text, Action action) => menu.Items.Add(Item(text, action));
        var providers = new MenuItem { Header = "Usage provider" };
        foreach (var (provider, label) in new[] { ("Codex", "Codex"), ("Claude", "Claude Code"), ("Both", "Codex and Claude Code") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.Provider == provider };
            item.Click += (_, _) => SetProvider(provider);
            providers.Items.Add(item);
        }
        menu.Items.Add(providers);
        Add("Refresh usage  (F5)", async () => await RefreshAsync());
        if (settings.ShowsCodex)
        {
            var autoResume = new MenuItem { Header = "Auto-resume", IsCheckable = true, IsChecked = settings.AutoResume };
            autoResume.Click += (_, _) => AutoResumeToggle.IsChecked = autoResume.IsChecked;
            menu.Items.Add(autoResume);
            var targets = new MenuItem { Header = "Resume target" };
            foreach (string target in new[] { "Desktop", "CLI" })
            {
                var radio = new RadioButton { Content = target == "Desktop" ? "Codex Desktop" : "Terminal / CLI", GroupName = "ResumeTarget", IsChecked = settings.ResumeTarget == target, Margin = new Thickness(6) };
                radio.Checked += (_, _) => { settings.ResumeTarget = target; ConfigureResume(); menu.IsOpen = false; };
                targets.Items.Add(new MenuItem { Header = radio, StaysOpenOnClick = true });
            }
            menu.Items.Add(targets);
        }
        if (settings.ShowsClaude)
        {
            Add("Set up Claude usage…", SetupClaude);
            var notice = new MenuItem { Header = "Notify when Claude can continue", IsCheckable = true, IsChecked = settings.ClaudeResumeNotice };
            notice.Click += (_, _) => { settings.ClaudeResumeNotice = notice.IsChecked; ConfigureResume(); };
            menu.Items.Add(notice);
            if (notices.Ready.Count > 0)
            {
                var sessions = new MenuItem { Header = "Claude sessions to resume" };
                foreach (var interruption in notices.Ready)
                {
                    // A TextBlock header keeps underscores in task titles from becoming access keys.
                    var entry = new MenuItem { Header = new TextBlock { Text = UsageTask.Short(interruption.Title, 50) }, ToolTip = ClaudeResumeNotices.Message(interruption) };
                    if (ClaudeResumeNotices.Command(interruption) is { } command) entry.Items.Add(Item("Copy resume command", () => CopyResumeCommand(command)));
                    entry.Items.Add(Item("Dismiss", () => notices.Dismiss(interruption.ErrorId)));
                    sessions.Items.Add(entry);
                }
                menu.Items.Add(sessions);
            }
        }
        Add(Topmost ? "Turn off always on top" : "Turn on always on top", () => TogglePin(this, new RoutedEventArgs()));
        if (settings.Both)
        {
            var dashboards = new MenuItem { Header = "Open usage dashboard" };
            dashboards.Items.Add(Item("Codex", () => OpenUrl(CodexDashboard)));
            dashboards.Items.Add(Item("Claude Code", () => OpenUrl(ClaudeDashboard)));
            menu.Items.Add(dashboards);
        }
        else Add("Open usage dashboard", () => OpenUrl(settings.Provider == "Claude" ? ClaudeDashboard : CodexDashboard));
        if (settings.ShowsCodex)
        {
            Add("Choose Codex data folder…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Codex folder containing sessions (usually .codex)" };
                if (dialog.ShowDialog(this) == true) { settings.CodexFolder = dialog.FolderName; lastLive = null; ConfigureResume(); }
            });
            Add("Use default Codex folder", () => { settings.CodexFolder = null; lastLive = null; ConfigureResume(); });
        }
        menu.Items.Add(WindowsMenu());
        menu.Items.Add(new Separator());
        Add("Hide to tray  (Esc)", Hide);
        Add("Quit", Close);
        menu.PlacementTarget = this; menu.IsOpen = true;
    }

    private MenuItem WindowsMenu()
    {
        string exe = Environment.ProcessPath!;
        var windows = new MenuItem { Header = "Taskbar and Start" };
        void Toggle(string header, bool isChecked, Action<bool> apply)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) =>
            {
                try { apply(item.IsChecked); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException)
                { MessageBox.Show(this, e.Message, "Taskbar and Start", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            windows.Items.Add(item);
        }
        Toggle("Show on taskbar", ShowInTaskbar, on => { settings.ShowInTaskbar = ShowInTaskbar = on; SaveSettings(); });
        bool startup = false, shortcut = false;
        try { startup = WindowsShell.StartsWithWindows(exe); shortcut = WindowsShell.HasStartMenuShortcut(exe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException) { }
        Toggle("Start with Windows", startup, on => WindowsShell.SetStartsWithWindows(exe, on));
        Toggle("Start menu shortcut", shortcut, on =>
        {
            WindowsShell.SetStartMenuShortcut(exe, on);
            if (on) MessageBox.Show(this, "Token Usage Tracker is now in the Start menu. To pin it, open Start, find Token Usage Tracker under All, right-click it, and choose Pin to Start or Pin to taskbar.",
                "Taskbar and Start", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        return windows;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { await RefreshAsync(); e.Handled = true; }
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
