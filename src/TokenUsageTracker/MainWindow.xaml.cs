using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TokenUsageTracker;

public partial class MainWindow : Window
{
    private readonly WidgetSettings settings = WidgetSettings.Load();
    private readonly UsageReader reader = new();
    private readonly ResumeController resume = new();
    private readonly CancellationTokenSource lifetime = new();
    private LiveUsage? lastLive;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray;
    private bool reading;
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(190, 240, 120));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(249, 198, 110));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 132, 132));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(155, 169, 183));

    public MainWindow()
    {
        InitializeComponent();
        if (settings.Provider is not ("Codex" or "Claude")) settings.Provider = "Codex";
        if (settings.ResumeTarget is not ("Desktop" or "CLI")) settings.ResumeTarget = "Desktop";
        AutoResumeToggle.IsChecked = settings.AutoResume;
        Topmost = settings.AlwaysOnTop;
        SetCompact(settings.CompactMode, save: false);
        UpdateProvider();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdatePin();
        tray = new Forms.NotifyIcon
        {
            Text = "Codex Usage — double-click to show",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information,
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(RestoreWidget));
        menu.Items.Add("Refresh usage", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync()));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWidget);
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            RestorePosition();
            ConfigureResume();
            timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); resume.Dispose(); tray.Dispose(); };
        Closing += (_, _) => SaveSettings();
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    private async Task RefreshAsync()
    {
        if (reading) return;
        reading = true;
        string provider = settings.Provider;
        bool claude = provider == "Claude";
        string folder = claude ? ClaudeUsage.ConfigFolder : settings.DataFolder;
        bool Current() => provider == settings.Provider && folder == (claude ? ClaudeUsage.ConfigFolder : settings.DataFolder);
        try
        {
            LiveUsage? live = null;
            string? error = null;
            try
            {
                if (!claude)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var client = await CodexClient.ConnectAsync(folder, desktop: false, timeout.Token);
                    live = await client.ReadUsageAsync(timeout.Token);
                    if (Current()) lastLive = live;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException
                or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
            {
                error = e is OperationCanceledException ? "Live usage timed out. Check Codex sign-in and connection." : e.Message;
            }
            if (lifetime.IsCancellationRequested || !Current()) return;
            var result = claude ? await Task.Run(() => ClaudeUsage.Read(folder))
                : live is not null ? new UsageResult(live.Snapshot, null)
                : lastLive is not null ? new UsageResult(lastLive.Snapshot, error)
                : await Task.Run(() => reader.Read(folder));
            if (!Current() || lifetime.IsCancellationRequested) return;
            var now = DateTimeOffset.Now;
            var snapshot = result.Snapshot;
            historySnapshot = snapshot;
            bool stale = snapshot is not null && now - snapshot.Timestamp > TimeSpan.FromMinutes(15);
            ShowWindow(snapshot?.Primary, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, now, stale);
            ShowWindow(snapshot?.Secondary, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, now, stale);
            PlanText.Text = snapshot?.Plan?.Replace('_', ' ').ToUpperInvariant() ?? (claude ? "CLAUDE" : "LOCAL");
            string source = claude ? "Claude Code snapshot" : live is not null ? "Live account usage" : lastLive is not null ? "Last live reading" : "Local snapshot";
            string status = snapshot is null ? claude ? "Waiting for Claude Code · see Options" : "Waiting for plan usage"
                : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
                : $"{source} · {Age(now - snapshot.Timestamp)} ago";
            StatusText.Text = settings.CompactMode && snapshot is not null
                ? $"{PlanText.Text} · {status}" : status;
            StatusText.Foreground = error is not null || result.Error is not null || stale ? Amber : Muted;
            string recovery;
            bool claudeNeedsSetup = false;
            try
            {
                if (claude)
                {
                    claudeNeedsSetup = await Task.Run(() => ClaudeUsage.GetStatusLineState(folder, Environment.ProcessPath!)) == ClaudeUsage.StatusLineState.OtherWidgetPath;
                    recovery = claudeNeedsSetup ? "Claude status line points to an older widget location · Options → Set up Claude usage"
                        : "Claude usage display only · auto-resume unavailable";
                }
                else
                {
                    await resume.TickAsync(live, lifetime.Token);
                    recovery = resume.Status;
                    if (settings.AutoResume && settings.ResumeTarget == "Desktop" && !File.Exists(CodexClient.DesktopSocket(folder)))
                        recovery = "Desktop connection unavailable: no shared control socket. CLI mode is available in Options.";
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            { recovery = "Recovery paused: " + e.Message; }
            if (lifetime.IsCancellationRequested || !Current()) return;
            SourceText.Text = claude ? (claudeNeedsSetup ? "Claude status line needs setup · see Options" : "From Claude Code · refreshes every 15s")
                : settings.AutoResume ? recovery : "Auto-resume off · refreshes every 15s";
            SourceText.TextTrimming = TextTrimming.CharacterEllipsis;
            SourceText.ToolTip = recovery;
            string details = $"{source}\n{error ?? result.Error}\n{recovery}\n{folder}";
            Card.ToolTip = details;
            StatusText.ToolTip = AutoResumeToggle.ToolTip = details;
            if (!claude && settings.AutoResume && settings.CompactMode)
                StatusText.Text = $"{PlanText.Text} · " + (recovery.StartsWith("Desktop connection unavailable", StringComparison.Ordinal)
                    ? "Desktop connection unavailable" : $"{source} · {resume.PendingCount} pending");
            ToolTip = null;
            tray.Text = snapshot?.Primary is { } w && !w.IsExpired(now)
                ? $"{provider}: {w.Remaining:0.#}% left — {Age(now - snapshot.Timestamp)} ago"
                : $"{provider} Usage — open widget for status";
        }
        finally
        {
            reading = false;
            if (provider != settings.Provider && !lifetime.IsCancellationRequested) _ = RefreshAsync();
        }
    }

    private void ShowWindow(UsageWindow? w, TextBlock label, TextBlock value, ProgressBar bar, TextBlock reset, DateTimeOffset now, bool stale = false)
    {
        if (w is null)
        {
            value.Text = "—"; value.Foreground = Muted; bar.Value = 0;
            reset.Text = "No window reported";
            return;
        }
        label.Text = w.Label;
        bool expired = w.IsExpired(now);
        Brush color = expired || stale ? Muted : w.Remaining <= 10 ? Red : w.Remaining <= 25 ? Amber : Green;
        value.Text = expired ? "—" : w.Remaining.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        value.Foreground = bar.Foreground = color;
        bar.Value = expired ? 0 : w.Remaining;
        reset.Text = expired ? "Reset passed · waiting for " + settings.Provider
            : w.ResetsAt is { } time ? "Resets in " + Countdown(time - now) : "Reset time unavailable";
        reset.ToolTip = w.ResetsAt?.ToLocalTime().ToString("f");
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
        if (settings.CompactMode) { settings.CompactWidth = Width; settings.CompactHeight = Height; }
        else { settings.FullWidth = Width; settings.FullHeight = Height; }
        if (!settings.Save()) { StatusText.Text = "Could not save preferences"; StatusText.ToolTip = "Check LocalAppData write permissions."; }
    }

    private void RestoreWidget() { Show(); WindowState = WindowState.Normal; RestorePosition(); Activate(); }
    private void UpdatePin() { PinButton.Foreground = Topmost ? Green : Muted; PinButton.ToolTip = Topmost ? "Always on top: on" : "Always on top: off"; }
    private void ToggleCompact(object sender, RoutedEventArgs e) => SetCompact(!settings.CompactMode, save: true);

    private void SetCompact(bool compact, bool save)
    {
        if (save) SaveSettings();
        settings.CompactMode = compact;
        MinWidth = compact ? 240 : 280;
        MinHeight = compact ? 120 : 384;
        MaxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        MaxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        var size = settings.WindowSize(compact);
        Width = Math.Clamp(size.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(size.Height, MinHeight, MaxHeight);
        Card.Margin = new Thickness(compact ? 6 : 9);
        Card.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(19, 14, 19, 16);
        PlanRow.Visibility = SourceText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PrimaryPanel.Margin = new Thickness(0, compact ? 3 : 9, compact ? 8 : 0, 0);
        SecondaryPanel.Margin = new Thickness(compact ? 8 : 0, compact ? 3 : 16, 0, 0);
        UsageGrid.RowDefinitions[1].Height = compact ? new GridLength(0) : GridLength.Auto;
        UsageGrid.ColumnDefinitions[1].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(SecondaryPanel, compact ? 0 : 1);
        Grid.SetColumn(SecondaryPanel, compact ? 1 : 0);
        PrimaryLabel.FontSize = SecondaryLabel.FontSize = compact ? 10 : 12;
        PrimaryValue.FontSize = compact ? 26 : 35;
        SecondaryValue.FontSize = compact ? 22 : 25;
        PrimaryReset.FontSize = SecondaryReset.FontSize = compact ? 9 : 11;
        PrimaryReset.Margin = SecondaryReset.Margin = new Thickness(0, compact ? 3 : 7, 0, 0);
        PrimaryBar.Height = SecondaryBar.Height = compact ? 4 : 5;
        PrimaryBar.Margin = compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 5, 0, 0);
        SecondaryBar.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 7, 0, 0);
        Footer.Margin = new Thickness(0, compact ? 8 : 14, 0, 0);
        Footer.Padding = new Thickness(0, compact ? 6 : 11, 0, 0);
        ModeButton.ToolTip = compact ? "Switch to full-size mode" : "Switch to compact mode";
        AutomationProperties.SetName(ModeButton, ModeButton.ToolTip.ToString());
        ModeButton.Width = PinButton.Width = RefreshButton.Width = compact ? 26 : 28;
        ModeButton.Height = PinButton.Height = RefreshButton.Height = compact ? 24 : 28;
        UpdateResponsiveLayout();
        if (save) SaveSettings();
    }

    private void UpdateResponsiveLayout()
    {
        bool compact = settings.CompactMode;
        TaskLists.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FiveHourTasks.IsEnabled = WeeklyTasks.IsEnabled = settings.Provider == "Codex";
        TaskLists.ToolTip = settings.Provider == "Claude" ? "Claude status-line snapshots do not contain task history or token costs." : null;
        bool narrow = Width < (compact ? 360 : 310);
        bool stacked = compact && narrow;
        AutoResumeToggle.Visibility = narrow || settings.Provider == "Claude" ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = compact && Height < 160 ? Visibility.Collapsed : Visibility.Visible;
        PrimaryHeading.RowDefinitions[1].Height = SecondaryHeading.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(PrimaryValue, stacked ? 1 : 0);
        Grid.SetRow(SecondaryValue, stacked ? 1 : 0);
        PrimaryValue.HorizontalAlignment = SecondaryValue.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PrimaryValue.FontSize = stacked ? 22 : compact ? 26 : 35;
        SecondaryValue.FontSize = compact ? 22 : 25;
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
        try { resume.Configure(settings.Provider == "Codex" && settings.AutoResume, settings.ResumeTarget, settings.DataFolder, DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings.AutoResume = false; AutoResumeToggle.IsChecked = false;
            StatusText.Text = "Could not save recovery state"; StatusText.ToolTip = e.Message;
        }
        SaveSettings();
        _ = RefreshAsync();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenMenu(object sender, RoutedEventArgs e) => ShowMenu();

    private void UpdateProvider()
    {
        historySnapshot = null;
        Title = settings.Provider + " Usage";
        ProviderText.Text = settings.Provider;
        RefreshButton.ToolTip = settings.Provider == "Claude" ? "Reread Claude Code snapshot (F5)" : "Refresh live usage (F5)";
        AutomationProperties.SetName(RefreshButton, RefreshButton.ToolTip.ToString());
        PrimaryLabel.Text = "5-hour window"; SecondaryLabel.Text = "Weekly window";
        ShowWindow(null, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, DateTimeOffset.Now);
        ShowWindow(null, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, DateTimeOffset.Now);
        PlanText.Text = settings.Provider.ToUpperInvariant();
        StatusText.Text = "Waiting for " + settings.Provider + " usage";
        SourceText.Text = "Refreshes every 15s";
        Card.ToolTip = null;
        UpdateResponsiveLayout();
    }

    private void SetProvider(string provider)
    {
        if (settings.Provider == provider) return;
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
                : "Your custom Claude status line was left unchanged. Forward its original JSON input to CodexUsageWidget.exe --claude-statusline and discard the bridge output. See the Claude setup section in README.md.",
                "Claude usage setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, e.Message, "Claude setup failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        var providers = new MenuItem { Header = "Usage provider" };
        foreach (string provider in new[] { "Codex", "Claude" })
        {
            var item = new MenuItem { Header = provider == "Claude" ? "Claude Code" : "Codex", IsCheckable = true, IsChecked = settings.Provider == provider };
            item.Click += (_, _) => SetProvider(provider);
            providers.Items.Add(item);
        }
        menu.Items.Add(providers);
        Add("Refresh usage  (F5)", async () => await RefreshAsync());
        if (settings.Provider == "Codex")
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
        else Add("Set up Claude usage…", SetupClaude);
        Add(Topmost ? "Turn off always on top" : "Turn on always on top", () => TogglePin(this, new RoutedEventArgs()));
        Add("Open usage dashboard", () => Process.Start(new ProcessStartInfo(settings.Provider == "Claude"
            ? "https://claude.ai/settings/usage" : "https://chatgpt.com/codex/settings/usage") { UseShellExecute = true }));
        if (settings.Provider == "Codex")
        {
            Add("Choose Codex data folder…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Codex folder containing sessions (usually .codex)" };
                if (dialog.ShowDialog(this) == true) { settings.CodexFolder = dialog.FolderName; lastLive = null; ConfigureResume(); }
            });
            Add("Use default Codex folder", () => { settings.CodexFolder = null; lastLive = null; ConfigureResume(); });
        }
        menu.Items.Add(new Separator());
        Add("Hide to tray  (Esc)", Hide);
        Add("Quit", Close);
        menu.PlacementTarget = this; menu.IsOpen = true;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { await RefreshAsync(); e.Handled = true; }
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
