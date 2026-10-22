using System;
using System.Threading;
using System.Windows;

namespace TokenUsageTracker;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--claude-statusline"])
        {
            Console.InputEncoding = Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Shutdown(ClaudeUsage.RunStatusLine(Console.In, Console.Out, Console.Error, ClaudeUsage.ConfigFolder));
            return;
        }
        instance = new Mutex(true, @"Local\CodexUsageWidget", out bool first);
        if (!first) { Shutdown(); return; }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
