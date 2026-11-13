using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenUsageTracker;

public sealed class WidgetSettings
{
    private static readonly string FilePath = Path.Combine(AppData.Folder(), "settings.json");
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = true;
    public bool CompactMode { get; set; }
    /// <summary>"Codex", "Claude" or "Both".</summary>
    public string Provider { get; set; } = "Codex";
    public double? FullWidth { get; set; }
    public double? FullHeight { get; set; }
    public double? CompactWidth { get; set; }
    public double? CompactHeight { get; set; }
    public double? BothFullWidth { get; set; }
    public double? BothFullHeight { get; set; }
    public double? BothCompactWidth { get; set; }
    public double? BothCompactHeight { get; set; }
    public bool AutoResume { get; set; }
    public string ResumeTarget { get; set; } = "Desktop";
    public string? CodexFolder { get; set; }
    public bool ClaudeResumeNotice { get; set; } = true;

    [JsonIgnore] public bool Both => Provider == "Both";
    [JsonIgnore] public bool ShowsCodex => Provider is "Codex" or "Both";
    [JsonIgnore] public bool ShowsClaude => Provider is "Claude" or "Both";

    /// <summary>Repairs values a hand-edited or older settings file may contain.</summary>
    public void Normalize()
    {
        if (Provider is not ("Codex" or "Claude" or "Both")) Provider = "Codex";
        if (ResumeTarget is not ("Desktop" or "CLI")) ResumeTarget = "Desktop";
    }

    public (double Width, double Height) WindowSize(bool compact) => WindowSize(compact, Both);

    public (double Width, double Height) WindowSize(bool compact, bool both)
    {
        static double Valid(double? value, double fallback) => value is > 0 && double.IsFinite(value.Value) ? value.Value : fallback;
        if (both)
            return compact ? (Valid(BothCompactWidth, 432), Valid(BothCompactHeight, 170))
                : (Valid(BothFullWidth, 440), Valid(BothFullHeight, 400));
        return compact ? (Valid(CompactWidth, 432), Valid(CompactHeight, 162))
            : (Valid(FullWidth, 316), Valid(FullHeight, 384));
    }

    public void RememberSize(bool compact, double width, double height)
    {
        if (Both)
        {
            if (compact) { BothCompactWidth = width; BothCompactHeight = height; }
            else { BothFullWidth = width; BothFullHeight = height; }
        }
        else if (compact) { CompactWidth = width; CompactHeight = height; }
        else { FullWidth = width; FullHeight = height; }
    }

    [JsonIgnore]
    public string DataFolder => !string.IsNullOrWhiteSpace(CodexFolder) ? CodexFolder
        : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static WidgetSettings Load()
    {
        AppData.MigrateLegacyFolder();
        WidgetSettings settings;
        try { settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { settings = new(); }
        settings.Normalize();
        return settings;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
