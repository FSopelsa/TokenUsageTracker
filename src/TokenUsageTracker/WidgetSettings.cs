using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenUsageTracker;

public sealed class WidgetSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "settings.json");
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool CompactMode { get; set; }
    public string Provider { get; set; } = "Codex";
    public double? FullWidth { get; set; }
    public double? FullHeight { get; set; }
    public double? CompactWidth { get; set; }
    public double? CompactHeight { get; set; }
    public bool AutoResume { get; set; }
    public string ResumeTarget { get; set; } = "Desktop";
    public string? CodexFolder { get; set; }

    public (double Width, double Height) WindowSize(bool compact)
    {
        static double Valid(double? value, double fallback) => value is > 0 && double.IsFinite(value.Value) ? value.Value : fallback;
        return compact ? (Valid(CompactWidth, 432), Valid(CompactHeight, 162))
            : (Valid(FullWidth, 316), Valid(FullHeight, 348));
    }

    [JsonIgnore]
    public string DataFolder => !string.IsNullOrWhiteSpace(CodexFolder) ? CodexFolder
        : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static WidgetSettings Load()
    {
        try { return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
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
