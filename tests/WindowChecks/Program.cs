using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using TokenUsageTracker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Never show the window: Loaded starts account polling and optional task recovery.
        var window = new MainWindow();
        int checks = 0;
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        // Claude notices keep state in LocalAppData; point them at a temporary file so the checks never touch the real one.
        string noticeState = Path.Combine(Path.GetTempPath(), "WindowChecks-claude-resume-" + Guid.NewGuid() + ".json");
        SetField("notices", new ClaudeResumeNotices(noticeState));
        SetField("notifyResume", new Action<ClaudeInterruption>(_ => throw new Exception("Window checks must not show Claude notices")));
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        try
        {
            var settings = (WidgetSettings)Field("settings");
            // MainWindow loads the user's saved preferences; start from Codex so a saved Claude choice cannot hide Codex-only controls.
            settings.Provider = "Codex";
            settings.FullWidth = 350; settings.FullHeight = 400;
            settings.CompactWidth = 300; settings.CompactHeight = 150;
            Call("SetCompact", true, false);
            Check(window.Width == 300 && window.Height == 150, "Compact mode restores its custom dimensions");
            Call("SetCompact", false, false);
            Check(window.Width == 350 && window.Height == 400, "Full mode restores its independent custom dimensions");
            settings.CompactWidth = double.MaxValue; settings.CompactHeight = double.MaxValue;
            Call("SetCompact", true, false);
            Check(window.Width <= SystemParameters.VirtualScreenWidth && window.Height <= SystemParameters.VirtualScreenHeight,
                "Oversized saved dimensions cannot exceed the desktop");
            settings.CompactWidth = 300; settings.CompactHeight = 150;
            Call("SetCompact", true, false);
            var grip = (Thumb)window.FindName("ResizeHandle");
            grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 240 && window.Height == 120, "Dragging clamps compact mode to its usable minimum");
            grip.RaiseEvent(new DragDeltaEventArgs(20, 10) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 260 && window.Height == 130, "Dragging can enlarge the widget again");
            grip.RaiseEvent(new DragDeltaEventArgs(-20, -10) { RoutedEvent = Thumb.DragDeltaEvent });

            var root = (FrameworkElement)window.Content;
            Rect Bounds(string name)
            {
                var control = (FrameworkElement)window.FindName(name);
                return control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            }
            void Render(string file)
            {
                Call("UpdateResponsiveLayout");
                root.Measure(new Size(window.Width, window.Height));
                root.Arrange(new Rect(0, 0, window.Width, window.Height));
                root.UpdateLayout();
                Rect content = Bounds("LayoutGrid");
                var card = (System.Windows.Controls.Border)window.FindName("Card");
                double visibleBottom = window.Height - card.Margin.Bottom - card.BorderThickness.Bottom - card.Padding.Bottom;
                bool Visible(string name) => ((FrameworkElement)window.FindName(name)).Visibility == Visibility.Visible;
                var names = new List<string> { "Header", "PrimaryLabel", "PrimaryValue", "PrimaryBar", "SecondaryLabel", "SecondaryValue", "SecondaryBar" };
                if (Visible("PrimaryReset")) names.AddRange(new[] { "PrimaryReset", "SecondaryReset" });
                if (Visible("ClaudeBlock"))
                {
                    names.AddRange(new[] { "ClaudePrimaryLabel", "ClaudePrimaryValue", "ClaudePrimaryBar", "ClaudeSecondaryLabel", "ClaudeSecondaryValue", "ClaudeSecondaryBar" });
                    if (Visible("ClaudePrimaryReset")) names.AddRange(new[] { "ClaudePrimaryReset", "ClaudeSecondaryReset" });
                    if (Visible("ClaudePlanRow")) names.Add("ClaudePlanRow");
                }
                if (((FrameworkElement)window.FindName("TaskLists")).Visibility == Visibility.Visible)
                    names.AddRange(new[] { "TaskLists", "FiveHourTasks", "WeeklyTasks" });
                if (((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Visible)
                {
                    names.Add("Footer"); names.Add("StatusText");
                    if (((FrameworkElement)window.FindName("SourceText")).Visibility == Visibility.Visible) names.Add("SourceText");
                }
                foreach (string name in names)
                {
                    Rect bounds = Bounds(name);
                    Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= content.Left && bounds.Top >= content.Top
                        && bounds.Right <= Math.Min(content.Right + 1, window.Width - 6)
                        && bounds.Bottom <= Math.Min(content.Bottom + 1, visibleBottom),
                        name + " fits inside the card at " + window.Width + " × " + window.Height);
                    if (window.FindName(name) is System.Windows.Controls.TextBlock text)
                        Check(bounds.Height >= text.FontSize, name + " text is not vertically clipped (height " + bounds.Height + ")");
                }
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(".build");
                using var output = File.Create(Path.Combine(".build", file));
                encoder.Save(output);
            }
            // Use long representative readings to expose narrow-layout overlap.
            ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text = "99.9%";
            ((System.Windows.Controls.TextBlock)window.FindName("SecondaryValue")).Text = "100%";
            ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).Text = "Resets in 4h 59m";
            ((System.Windows.Controls.TextBlock)window.FindName("SecondaryReset")).Text = "Resets in 6d 23h";
            Render("window-compact-minimum.png");
            Check(Bounds("PrimaryValue").Top >= Bounds("PrimaryLabel").Bottom
                && Bounds("SecondaryValue").Top >= Bounds("SecondaryLabel").Bottom, "Narrow labels do not overlap percentages");
            Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)window.FindName("PinButton")).Visibility == Visibility.Collapsed,
                "Secondary header controls hide at narrow widths");
            Check(((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Collapsed, "Short compact windows hide the footer");
            window.Width = 432; window.Height = 162;
            Render("window-compact-default.png");
            Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Visible
                && ((FrameworkElement)window.FindName("Footer")).Visibility == Visibility.Visible,
                "Growing the compact window restores header controls and status");
            window.Width = 240; window.Height = 160;
            Render("window-compact-narrow-footer.png");
            window.Width = 432; window.Height = 120;
            Render("window-compact-wide-short.png");
            Call("SetCompact", false, false);
            grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(window.Width == 280 && window.Height == 384, "Full layout has its own usable minimum");
            Render("window-full-minimum.png");
            string claudeFolder = Path.Combine(Path.GetTempPath(), "ClaudeWindowChecks-" + Guid.NewGuid());
            string? previousFolder = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var previousContext = System.Threading.SynchronizationContext.Current;
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeFolder);
                System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                settings.Provider = "Claude";
                Call("SetCompact", true, false);
                window.Width = 240; window.Height = 120;
                Call("UpdateProvider");
                Check(window.Title == "Claude Usage" && ((System.Windows.Controls.TextBlock)window.FindName("ProviderText")).Text == "Claude",
                    "Claude selection updates the window title and visible provider");
                string claudeInput = System.Text.Json.JsonSerializer.Serialize(new
                {
                    prompt_cache = new { expires_at = DateTimeOffset.UtcNow.AddSeconds(100).ToUnixTimeSeconds(), recache_tokens_if_cold = 183402 },
                    rate_limits = new
                    {
                        five_hour = new { used_percentage = 23.5, resets_at = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds() },
                        seven_day = new { used_percentage = 41.2, resets_at = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds() }
                    }
                });
                ClaudeUsage.Capture(claudeInput, claudeFolder, DateTimeOffset.UtcNow);
                void Refresh()
                {
                    var task = (Task)typeof(MainWindow).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                    task.GetAwaiter().GetResult();
                }
                Refresh();
                Check(((System.Windows.Controls.ProgressBar)window.FindName("PrimaryBar")).Value == 76.5
                    && ((System.Windows.Controls.ProgressBar)window.FindName("SecondaryBar")).Value == 58.8,
                    "Claude refresh displays both allowances from the status-line snapshot");
                var taskbar = window.TaskbarItemInfo;
                string TrayText() => (string)Field("tray").GetType().GetProperty("Text")!.GetValue(Field("tray"))!;
                Check(taskbar.ProgressState == TaskbarItemProgressState.Normal && Math.Abs(taskbar.ProgressValue - 0.588) < 1e-9
                    && taskbar.Overlay is BitmapSource && typeof(MainWindow).GetField("badge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is not null,
                    "Taskbar button shows the tighter Claude window as progress with a number badge");
                Check(TrayText().StartsWith("Claude: 5-hour", StringComparison.Ordinal) && TrayText().Contains("Weekly"),
                    "Tray tooltip lists both Claude windows");
                Check(((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text.Contains("Claude Code snapshot"),
                    "Claude readings identify themselves as snapshots");
                Check(((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).Text.Contains("empty ~")
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).ToolTip.ToString()!.Contains("average pace"),
                    "Claude window shows its pace forecast and explanation");
                Check(((System.Windows.Controls.TextBlock)window.FindName("SourceText")).Text.Contains("re-cache ~183k tokens")
                    && ((System.Windows.Controls.Border)window.FindName("Card")).ToolTip.ToString()!.Contains("most recently updated"),
                    "Claude cache countdown appears in the footer and compact tooltip");
                Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Collapsed,
                    "Claude display hides the unsupported automatic recovery control");
                Render("window-claude-minimum.png");
                Call("SetCompact", false, false);
                Render("window-claude-full.png");
                ClaudeUsage.ConfigureStatusLine(claudeFolder, "old-widget.exe");
                Refresh();
                Check(((System.Windows.Controls.TextBlock)window.FindName("SourceText")).Text.Contains("needs setup"),
                    "A moved widget's setup warning remains visible when cache data exists");
                File.Delete(Path.Combine(claudeFolder, "usage-widget", "usage.json"));
                Refresh();
                Check(((System.Windows.Controls.ProgressBar)window.FindName("PrimaryBar")).Value == 0
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—"
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryReset")).ToolTip is null,
                    "Missing Claude data clears the old balance instead of falling back to Codex");
                Check(window.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.None && window.TaskbarItemInfo.Overlay is null
                    && typeof(MainWindow).GetField("badge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
                    "Missing Claude data clears the taskbar progress and tray number");
                settings.Provider = "Codex";
                Call("UpdateProvider");
                Check(window.Title == "Codex Usage" && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—",
                    "Switching back to Codex clears the Claude balance immediately");

                // Both tools. A fake Codex reader keeps the checks from starting Codex; an empty folder stands in for local sessions.
                settings.CodexFolder = Path.Combine(claudeFolder, "codex-home");
                LiveUsage FakeCodex() => new(new UsageSnapshot(DateTimeOffset.UtcNow, "plus",
                    new UsageWindow(10, 300, DateTimeOffset.UtcNow.AddHours(3)), new UsageWindow(20, 10080, DateTimeOffset.UtcNow.AddDays(4))), null, true);
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromResult(FakeCodex())));
                ClaudeUsage.Capture(claudeInput, claudeFolder, DateTimeOffset.UtcNow);
                T Find<T>(string name) => (T)window.FindName(name);
                Color BrushColor(string name) => ((SolidColorBrush)(window.FindName(name) is System.Windows.Controls.ProgressBar bar ? bar.Foreground : Find<System.Windows.Controls.TextBlock>(name).Foreground)).Color;
                settings.Provider = "Both";
                settings.BothFullWidth = 440; settings.BothFullHeight = 400; settings.BothCompactWidth = 432; settings.BothCompactHeight = 170;
                Call("SetCompact", false, false);
                Call("UpdateProvider");
                Check(window.Title == "Token Usage" && Find<System.Windows.Controls.TextBlock>("ProviderText").Text == "Codex + Claude"
                    && Find<FrameworkElement>("ClaudeBlock").Visibility == Visibility.Visible,
                    "Both mode shows Codex and Claude Code together");
                Refresh();
                Check(Find<System.Windows.Controls.ProgressBar>("PrimaryBar").Value == 90 && Find<System.Windows.Controls.ProgressBar>("SecondaryBar").Value == 80
                    && Find<System.Windows.Controls.ProgressBar>("ClaudePrimaryBar").Value == 76.5 && Find<System.Windows.Controls.ProgressBar>("ClaudeSecondaryBar").Value == 58.8,
                    "Both mode fills all four windows");
                Check(BrushColor("PrimaryBar") != BrushColor("ClaudePrimaryBar") && BrushColor("PrimaryValue") == BrushColor("ClaudePrimaryValue"),
                    "Codex and Claude bars differ while their percentage text shares the warning colour");
                string IconText(string field) => (string)Field(field).GetType().GetProperty("Text")!.GetValue(Field(field))!;
                Check(Field("claudeTray") is not null && Field("claudeBadge") is not null
                    && IconText("tray").StartsWith("Codex:", StringComparison.Ordinal) && IconText("claudeTray").StartsWith("Claude:", StringComparison.Ordinal),
                    "Both mode shows one tray number per tool");
                Check(Math.Abs(window.TaskbarItemInfo.ProgressValue - 0.588) < 1e-9 && window.TaskbarItemInfo.Overlay is BitmapSource,
                    "The taskbar follows the tool with less room left");
                Render("window-both-full-default.png");
                grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
                Check(window.Width == 380 && window.Height == 370, "Full Both mode has its own usable minimum");
                Render("window-both-full-minimum.png");
                Call("SetCompact", true, false);
                Check(window.Width == 432 && window.Height == 170, "Compact Both mode restores its own size");
                Check(Find<System.Windows.Controls.TextBlock>("ClaudePrimaryLabel").Text.StartsWith("Claude", StringComparison.Ordinal)
                    && Find<System.Windows.Controls.TextBlock>("PrimaryLabel").Text.StartsWith("Codex", StringComparison.Ordinal),
                    "Compact Both labels name their tool");
                Render("window-both-compact-default.png");
                Check(Find<FrameworkElement>("Footer").Visibility == Visibility.Visible, "Compact Both mode shows the footer at its default size");
                grip.RaiseEvent(new DragDeltaEventArgs(-10000, -10000) { RoutedEvent = Thumb.DragDeltaEvent });
                Check(window.Width == 300 && window.Height == 150, "Compact Both mode has its own usable minimum");
                Render("window-both-compact-minimum.png");
                SetField("lastLive", null);
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromException<LiveUsage>(new IOException("offline"))));
                Refresh();
                Check(Find<System.Windows.Controls.TextBlock>("PrimaryValue").Text == "—" && Find<System.Windows.Controls.ProgressBar>("ClaudePrimaryBar").Value == 76.5,
                    "A failed Codex read leaves the Claude numbers in place");
                SetField("readLive", new Func<string, CancellationToken, Task<LiveUsage>>((_, _) => Task.FromResult(FakeCodex())));
                File.Delete(Path.Combine(claudeFolder, "usage-widget", "usage.json"));
                Refresh();
                Check(Find<System.Windows.Controls.TextBlock>("ClaudePrimaryValue").Text == "—" && Find<System.Windows.Controls.ProgressBar>("PrimaryBar").Value == 90,
                    "Missing Claude data leaves the Codex numbers in place");
                settings.Provider = "Codex";
                Call("UpdateProvider");
                Check(Field("claudeTray") is null && Find<FrameworkElement>("ClaudeBlock").Visibility == Visibility.Collapsed,
                    "Leaving Both mode removes the Claude tray icon and section");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousFolder);
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(claudeFolder)) Directory.Delete(claudeFolder, recursive: true);
            }
            var badgeType = typeof(MainWindow).Assembly.GetType("TokenUsageTracker.TrayBadge")!;
            foreach (var (text, provider) in new[] { ("0", "Codex"), ("58", "Claude"), ("100", "Codex") })
            {
                var rendered = (IDisposable)badgeType.GetMethod("Render")!.Invoke(null, new object[] { text, UsageLevel.Critical, 16, provider, true })!;
                var image = (BitmapSource)badgeType.GetProperty("Image")!.GetValue(rendered)!;
                Check(image.PixelWidth == 16 && image.PixelHeight == 16, $"Tray badge \"{text}\" renders at the small icon size");
                if (text == "100")
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(".build", "tray-badge-100.png"));
                    encoder.Save(output);
                }
                rendered.Dispose();
            }
            Check(!window.IsLoaded, "Window checks never start account polling or task recovery");
            Console.WriteLine($"All {checks} window checks passed.");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.ExitCode = 1;
        }
        finally
        {
            ((IDisposable)Field("tray")).Dispose();
            (typeof(MainWindow).GetField("claudeTray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as IDisposable)?.Dispose();
            ((IDisposable)Field("resume")).Dispose();
            if (File.Exists(noticeState)) File.Delete(noticeState);
        }
    }
}
