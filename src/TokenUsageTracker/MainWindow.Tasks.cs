using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TokenUsageTracker;

public partial class MainWindow
{
    private readonly TaskHistory history = new();
    private readonly ClaudeTranscripts transcripts = new();
    private bool historyBusy;

    private async void OpenTasks(object sender, RoutedEventArgs e)
    {
        if (historyBusy) return;
        historyBusy = true;
        var button = (Button)sender;
        object label = button.Content;
        button.Content = "Loading…";
        string mode = settings.Provider, codexFolder = settings.DataFolder, claudeFolder = ClaudeUsage.ConfigFolder;
        bool weekly = button == WeeklyTasks;
        int fallback = weekly ? 10080 : 300;
        UsageWindow? Window(string tool) => latest.TryGetValue(tool, out var r) ? weekly ? r.Snapshot?.Secondary : r.Snapshot?.Primary : null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var groups = new List<(string Name, TaskRanking Ranking)>();
            if (settings.ShowsCodex)
            {
                var window = Window("Codex");
                groups.Add(("Codex", await Task.Run(() => history.Read(codexFolder, window, fallback, now))));
            }
            if (settings.ShowsClaude)
            {
                var window = Window("Claude");
                groups.Add(("Claude Code", await Task.Run(() => transcripts.ReadTasks(claudeFolder, window, fallback, now))));
            }
            if (settings.Provider != mode || settings.DataFolder != codexFolder || lifetime.IsCancellationRequested) return;
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 480, MaxWidth = 380 };
            foreach (var (name, ranking) in groups)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                string heading = (groups.Count > 1 ? name.ToUpperInvariant() + "\n" : "") + ranking.Note + "\nHighest token count first.\n" + TaskHistory.EstimateNote;
                menu.Items.Add(new MenuItem { Header = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, MaxWidth = 330, FontSize = 11 }, IsEnabled = false });
                menu.Items.Add(new Separator());
                if (ranking.Tasks.Count == 0) menu.Items.Add(new MenuItem { Header = "No locally recorded tasks in this window", IsEnabled = false });
                foreach (var task in ranking.Tasks)
                {
                    var row = new StackPanel { MaxWidth = 330 };
                    row.Children.Add(new TextBlock { Text = task.Task.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
                    row.Children.Add(new TextBlock { Text = task.Cost, FontSize = 11 });
                    var item = new MenuItem { Header = row, ToolTip = task.Hover };
                    item.Click += (_, _) => ShowTaskDetails(task);
                    menu.Items.Add(item);
                }
            }
            menu.IsOpen = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "Task history unavailable", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { historyBusy = false; button.Content = label; }
    }

    private void ShowTaskDetails(RankedTask task)
    {
        var details = new Window
        {
            Owner = this, Title = task.Task.Title, Width = Math.Min(720, SystemParameters.WorkArea.Width - 40), Height = Math.Min(650, SystemParameters.WorkArea.Height - 40),
            MinWidth = 280, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(23, 30, 38)), Foreground = Brushes.White,
            Content = new TextBox
            {
                Text = task.Details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(20), FontSize = 13
            }
        };
        details.Show();
    }
}
