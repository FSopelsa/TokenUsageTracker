using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace TokenUsageTracker;

/// <summary>
/// Start menu shortcut and start-at-sign-in for the portable widget. Windows has no API that lets an
/// app pin itself to Start or the taskbar; the user pins the shortcut this creates.
/// </summary>
internal static class WindowsShell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CodexUsageWidget";

    public static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex Usage Widget.lnk");

    public static bool HasStartMenuShortcut(string exe) =>
        File.Exists(StartMenuShortcut) && SamePath(ShortcutTarget(StartMenuShortcut), exe);

    public static void SetStartMenuShortcut(string exe, bool enabled)
    {
        if (!enabled) { File.Delete(StartMenuShortcut); return; }
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription("Remaining Codex or Claude Code plan usage");
            link.SetIconLocation(exe, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuShortcut)!);
            ((IPersistFile)link).Save(StartMenuShortcut, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    /// <summary>Checked only when the sign-in entry starts this copy, so a moved executable can be registered again.</summary>
    public static bool StartsWithWindows(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string command && string.Equals(command, RunCommand(exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartsWithWindows(string exe, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, RunCommand(exe));
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static string RunCommand(string exe) => "\"" + exe + "\"";

    private static bool SamePath(string? a, string b) => !string.IsNullOrEmpty(a)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string? ShortcutTarget(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            return target.ToString();
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    private const uint SLGP_RAWPATH = 0x4;
    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
