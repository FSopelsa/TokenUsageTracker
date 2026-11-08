using System;
using System.IO;

namespace TokenUsageTracker;

/// <summary>Per-user storage for settings and auto-resume state, with a one-time move from the pre-rename folder.</summary>
public static class AppData
{
    public const string FolderName = "TokenUsageTracker";
    public const string LegacyFolderName = "CodexUsageWidget";

    public static string DefaultBase => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Folder(string? baseFolder = null) => Path.Combine(baseFolder ?? DefaultBase, FolderName);

    /// <summary>
    /// Moves the old settings folder to the new name when only the old one exists.
    /// If the new folder already exists it wins and the old one is left untouched.
    /// Returns true when a move happened; never throws.
    /// </summary>
    public static bool MigrateLegacyFolder(string? baseFolder = null)
    {
        string root = baseFolder ?? DefaultBase;
        string legacy = Path.Combine(root, LegacyFolderName);
        string current = Path.Combine(root, FolderName);
        try
        {
            if (!Directory.Exists(legacy) || Directory.Exists(current)) return false;
            Directory.Move(legacy, current);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
