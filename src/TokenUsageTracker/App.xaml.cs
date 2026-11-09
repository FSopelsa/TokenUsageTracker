using System;
using System.Threading;
using System.Windows;

namespace TokenUsageTracker;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? showListener;

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
        // A second launch (Start menu, pinned taskbar button, sign-in entry) brings the running widget forward.
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexUsageWidget.Show");
        if (!first)
        {
            WindowsShell.AllowSetForegroundWindow(WindowsShell.ASFW_ANY);
            showRequest.Set();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        showListener = ThreadPool.RegisterWaitForSingleObject(showRequest,
            (_, _) => Dispatcher.BeginInvoke(window.RestoreWidget), null, Timeout.Infinite, executeOnlyOnce: false);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showListener?.Unregister(null);
        showRequest?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
