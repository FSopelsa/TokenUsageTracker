using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenUsageTracker;

public sealed class ClaudeResumeState
{
    public DateTimeOffset EnabledAt { get; set; }
    public List<string> Notified { get; set; } = new();
    public List<string> Dismissed { get; set; } = new();
}

/// <summary>
/// Decides when a Claude Code session stopped by a usage limit can continue, and remembers which
/// interruptions were already announced. It never runs Claude Code; the user resumes the session.
/// </summary>
public sealed class ClaudeResumeNotices
{
    public const double FullPercent = 99.5;
    private readonly string statePath;
    private ClaudeResumeState state;

    public IReadOnlyList<ClaudeInterruption> Ready { get; private set; } = Array.Empty<ClaudeInterruption>();
    public bool Enabled => state.EnabledAt != default;
    /// <summary>Only interruptions after this time are announced. A week-old limit has reset anyway.</summary>
    public DateTimeOffset ScanSince(DateTimeOffset now) => state.EnabledAt > now.AddDays(-8) ? state.EnabledAt : now.AddDays(-8);

    public ClaudeResumeNotices(string? path = null)
    {
        statePath = path ?? Path.Combine(AppData.Folder(), "claude-resume.json");
        try { state = JsonSerializer.Deserialize<ClaudeResumeState>(File.ReadAllText(statePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { state = new(); }
        state.Notified ??= new();
        state.Dismissed ??= new();
    }

    public void Configure(bool on, DateTimeOffset now)
    {
        if (on == Enabled) return;
        state = on ? new() { EnabledAt = now } : new();
        Ready = Array.Empty<ClaudeInterruption>();
        Save();
    }

    /// <summary>
    /// True when a snapshot received after the error shows room in every reported window, or when every
    /// window that was full has a known reset that has passed. A countdown alone never counts otherwise.
    /// </summary>
    public static bool CanContinue(ClaudeInterruption interruption, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return false;
        var windows = new[] { snapshot.Primary, snapshot.Secondary }.OfType<UsageWindow>().ToArray();
        if (windows.Length == 0) return false;
        if (snapshot.Timestamp > interruption.At && windows.All(w => !w.IsExpired(now) && w.UsedPercent < FullPercent)) return true;
        var blocking = windows.Where(w => w.UsedPercent >= FullPercent).ToArray();
        return blocking.Length > 0 && blocking.All(w => w.ResetsAt is { } reset && reset <= now);
    }

    /// <summary>Returns the interruptions to announce now. Each is announced once; a later prompt in that session cancels it.</summary>
    public IReadOnlyList<ClaudeInterruption> Tick(IEnumerable<ClaudeInterruption> found, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (!Enabled) return Array.Empty<ClaudeInterruption>();
        var current = found.Where(i => i.At >= state.EnabledAt).ToList();
        var ids = current.Select(i => i.ErrorId).ToHashSet();
        bool changed = state.Notified.RemoveAll(id => !ids.Contains(id)) + state.Dismissed.RemoveAll(id => !ids.Contains(id)) > 0;
        var due = current.Where(i => !state.Notified.Contains(i.ErrorId) && CanContinue(i, snapshot, now)).OrderBy(i => i.At).ToList();
        state.Notified.AddRange(due.Select(i => i.ErrorId));
        Ready = current.Where(i => state.Notified.Contains(i.ErrorId) && !state.Dismissed.Contains(i.ErrorId)).OrderBy(i => i.At).ToList();
        if (changed || due.Count > 0) Save();
        return due;
    }

    public void Dismiss(string errorId)
    {
        if (state.Dismissed.Contains(errorId)) return;
        state.Dismissed.Add(errorId);
        Ready = Ready.Where(i => i.ErrorId != errorId).ToList();
        Save();
    }

    /// <summary>A PowerShell command that reopens the session in its folder, or null for an unexpected session ID.</summary>
    public static string? Command(ClaudeInterruption interruption)
    {
        if (!Regex.IsMatch(interruption.SessionId, "^[A-Za-z0-9-]{1,100}$")) return null;
        string resume = "claude --resume " + interruption.SessionId;
        return interruption.Workspace.Length == 0 ? resume
            : $"Set-Location -LiteralPath '{interruption.Workspace.Replace("'", "''")}'; {resume}";
    }

    public static string Message(ClaudeInterruption interruption)
    {
        string path = interruption.Workspace.TrimEnd('\\', '/');
        string folder = path.Length == 0 ? "" : " in " + path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return $"‘{UsageTask.Short(interruption.Title, 60)}’{folder} can continue. Click to copy the resume command.";
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(statePath + ".tmp", statePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* An unsaved notice can repeat after a restart. */ }
    }
}
