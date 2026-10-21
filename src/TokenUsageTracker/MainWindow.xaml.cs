using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TokenUsageTracker;

public partial class MainWindow : Window
{
    private readonly WidgetSettings settings = WidgetSettings.Load();
    private readonly UsageReader reader = new();
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
        Topmost = settings.AlwaysOnTop;
        UpdatePin();
        tray = new Forms.NotifyIcon
        {
            Text = "Codex Usage — double-click to show",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information,
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(RestoreWidget));
        menu.Items.Add("Refresh local usage", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync()));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWidget);
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            RestorePosition();
            timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) => { timer.Stop(); tray.Dispose(); };
        Closing += (_, _) => SaveSettings();
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    private async Task RefreshAsync()
    {
        if (reading) return;
        reading = true;
        string folder = settings.DataFolder;
        try
        {
            var result = await Task.Run(() => reader.Read(folder));
            var now = DateTimeOffset.Now;
            var snapshot = result.Snapshot;
            ShowWindow(snapshot?.Primary, PrimaryLabel, PrimaryValue, PrimaryBar, PrimaryReset, now);
            ShowWindow(snapshot?.Secondary, SecondaryLabel, SecondaryValue, SecondaryBar, SecondaryReset, now);
            PlanText.Text = snapshot?.Plan?.Replace('_', ' ').ToUpperInvariant() ?? "LOCAL";
            bool stale = snapshot is not null && now - snapshot.Timestamp > TimeSpan.FromMinutes(15);
            StatusText.Text = snapshot is null ? "Waiting for plan usage"
                : snapshot.Timestamp > now.AddMinutes(5) ? "Check your computer clock"
                : $"{(stale ? "Last seen" : "Updated")} {Age(now - snapshot.Timestamp)} ago";
            StatusText.Foreground = result.Error is not null || stale ? Amber : Muted;
            StatusText.ToolTip = result.Error ?? (snapshot is null ? folder
                : $"Recorded {snapshot.Timestamp.ToLocalTime():g}\n{folder}\nUpdates after Codex records activity; refresh only rereads local files.");
            ToolTip = result.Error;
            tray.Text = snapshot?.Primary is { } w && !w.IsExpired(now)
                ? $"Codex: {w.Remaining:0.#}% left — {Age(now - snapshot.Timestamp)} ago"
                : "Codex Usage — open widget for status";
        }
        finally { reading = false; }
    }

    private static void ShowWindow(UsageWindow? w, TextBlock label, TextBlock value, ProgressBar bar, TextBlock reset, DateTimeOffset now)
    {
        if (w is null)
        {
            value.Text = "—"; value.Foreground = Muted; bar.Value = 0;
            reset.Text = "No window reported";
            return;
        }
        label.Text = w.Label;
        bool expired = w.IsExpired(now);
        Brush color = expired ? Muted : w.Remaining <= 10 ? Red : w.Remaining <= 25 ? Amber : Green;
        value.Text = expired ? "—" : w.Remaining.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        value.Foreground = bar.Foreground = color;
        bar.Value = expired ? 0 : w.Remaining;
        reset.Text = expired ? "Reset passed · waiting for Codex"
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
                if (node is Button) return;
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
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Bottom - Height - 24;
        }
    }

    private void SaveSettings()
    {
        settings.Left = Left; settings.Top = Top; settings.AlwaysOnTop = Topmost;
        if (!settings.Save()) { StatusText.Text = "Could not save preferences"; StatusText.ToolTip = "Check LocalAppData write permissions."; }
    }

    private void RestoreWidget() { Show(); WindowState = WindowState.Normal; RestorePosition(); Activate(); }
    private void UpdatePin() { PinButton.Foreground = Topmost ? Green : Muted; PinButton.ToolTip = Topmost ? "Always on top: on" : "Always on top: off"; }
    private void TogglePin(object sender, RoutedEventArgs e) { Topmost = !Topmost; UpdatePin(); SaveSettings(); }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenMenu(object sender, RoutedEventArgs e) => ShowMenu();

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Refresh local usage  (F5)", async () => await RefreshAsync());
        Add(Topmost ? "Turn off always on top" : "Turn on always on top", () => TogglePin(this, new RoutedEventArgs()));
        Add("Open usage dashboard", () => Process.Start(new ProcessStartInfo("https://chatgpt.com/codex/settings/usage") { UseShellExecute = true }));
        Add("Choose Codex data folder…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Codex folder containing sessions (usually .codex)" };
            if (dialog.ShowDialog(this) == true) { settings.CodexFolder = dialog.FolderName; SaveSettings(); _ = RefreshAsync(); }
        });
        Add("Use default Codex folder", () => { settings.CodexFolder = null; SaveSettings(); _ = RefreshAsync(); });
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
