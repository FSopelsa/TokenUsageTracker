using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                var names = new List<string> { "Header", "PrimaryLabel", "PrimaryValue", "PrimaryBar", "PrimaryReset", "SecondaryLabel", "SecondaryValue", "SecondaryBar", "SecondaryReset" };
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
            Check(window.Width == 280 && window.Height == 348, "Full layout has its own usable minimum");
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
                Check(((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text.Contains("Claude Code snapshot"),
                    "Claude readings identify themselves as snapshots");
                Check(((FrameworkElement)window.FindName("AutoResumeToggle")).Visibility == Visibility.Collapsed,
                    "Claude display hides the unsupported automatic recovery control");
                Render("window-claude-minimum.png");
                Call("SetCompact", false, false);
                Render("window-claude-full.png");
                File.Delete(Path.Combine(claudeFolder, "usage-widget", "usage.json"));
                Refresh();
                Check(((System.Windows.Controls.ProgressBar)window.FindName("PrimaryBar")).Value == 0
                    && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—",
                    "Missing Claude data clears the old balance instead of falling back to Codex");
                settings.Provider = "Codex";
                Call("UpdateProvider");
                Check(window.Title == "Codex Usage" && ((System.Windows.Controls.TextBlock)window.FindName("PrimaryValue")).Text == "—",
                    "Switching back to Codex clears the Claude balance immediately");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousFolder);
                System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(claudeFolder)) Directory.Delete(claudeFolder, recursive: true);
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
            ((IDisposable)Field("resume")).Dispose();
        }
    }
}
