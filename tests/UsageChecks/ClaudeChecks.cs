using System.Diagnostics;
using System.Text.Json;
using TokenUsageTracker;

internal static class ClaudeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string input = """
            {"session_id":"private-session","cwd":"private-workspace","context_window":{"used_percentage":99},
             "rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1893456000},
                            "seven_day":{"used_percentage":41.2,"resets_at":1894060800}}}
            """;
        var now = DateTimeOffset.UtcNow;
        using var doc = JsonDocument.Parse(input);
        var snapshot = ClaudeUsage.Parse(doc.RootElement, now);
        check(snapshot.Primary?.Remaining == 76.5 && snapshot.Secondary?.Remaining == 58.8,
            "Claude uses reported subscription allowances rather than context-window tokens");
        check(snapshot.Primary?.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1893456000)
            && snapshot.Primary.Label == "5-hour window" && snapshot.Secondary?.Label == "Weekly window",
            "Claude reset times use Unix seconds and both subscription windows are labelled");
        using var weekly = JsonDocument.Parse("""{"rate_limits":{"seven_day":{"used_percentage":100}}}""");
        check(ClaudeUsage.Parse(weekly.RootElement, now) is { Primary: null, Secondary.Remaining: 0, Secondary.ResetsAt: null },
            "Claude weekly-only limits work without inventing a reset time");
        foreach (string used in new[] { "-1", "101", "\"40\"", "null", "true", "1e400" })
        {
            using var invalid = JsonDocument.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + used + "}}}");
            check(ClaudeUsage.Parse(invalid.RootElement, now).Primary is null, "Claude rejects invalid usage percentage " + used);
        }
        string root = Path.Combine(Path.GetTempPath(), "ClaudeUsageChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            check(ClaudeUsage.Read(root) is { Snapshot: null, Error: not null }, "Missing Claude setup gives an actionable empty state");
            ClaudeUsage.Capture(input, root, now);
            check(ClaudeUsage.Read(root).Snapshot?.Primary?.Remaining == 76.5, "Claude status-line updates reach the cached usage reader");
            string cache = Path.Combine(root, "usage-widget", "usage.json");
            string contents = File.ReadAllText(cache);
            check(!contents.Contains("private-session") && !contents.Contains("private-workspace") && !contents.Contains("context_window"),
                "Claude bridge saves no session, workspace, transcript or context metadata");
            string before = File.ReadAllText(cache);
            ClaudeUsage.Capture("{}", root, now.AddMinutes(1));
            check(File.ReadAllText(cache) == before && ClaudeUsage.Read(root).Snapshot?.Primary?.Remaining == 76.5,
                "A status line without rate limits, such as before the first reply, leaves the saved reading unchanged");
            ClaudeUsage.Capture("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":\"x\"}}}", root, now.AddMinutes(2));
            check(File.ReadAllText(cache) == before, "An invalid windowless update keeps the last good reading");
            ClaudeUsage.Capture("""{"rate_limits":{"seven_day":{"used_percentage":41.2,"resets_at":1894060800}}}""", root, now.AddMinutes(3));
            check(ClaudeUsage.Read(root).Snapshot is { Primary: null, Secondary.Remaining: 58.8 },
                "A fresh update without a five-hour window does not revive the older five-hour allowance");
            ClaudeUsage.Capture("""{"rate_limits_available":false}""", root, now.AddMinutes(4));
            check(ClaudeUsage.Read(root).Snapshot is null, "Unavailable subscription limits clear a cached reading");
            check(!Directory.EnumerateFiles(Path.Combine(root, "usage-widget"), "*.tmp").Any(), "Snapshot writes leave no temporary files behind");
            ClaudeUsage.Capture(input, root, now);
            try { ClaudeUsage.Capture("{", root, now); } catch (JsonException) { }
            check(ClaudeUsage.Read(root).Snapshot?.Primary?.Remaining == 76.5, "Malformed status-line input cannot corrupt a complete cached reading");
            ClaudeUsage.Capture(input, root, now.AddDays(1));
            check(ClaudeUsage.Read(root).Snapshot is null, "Claude snapshots with future timestamps are not displayed as real usage");
            File.WriteAllText(cache, "[]");
            check(ClaudeUsage.Read(root).Snapshot is null, "Corrupt Claude cache files produce an empty state without crashing");

            string config = Path.Combine(root, "settings.json");
            const string original = "{\"permissions\":{\"allow\":[\"Read\"]},\"theme\":\"dark\"}";
            File.WriteAllText(config, original);
            string executable = Environment.ProcessPath!;
            check(ClaudeUsage.ConfigureStatusLine(root, executable), "Claude setup can connect a new status line");
            using (var settings = JsonDocument.Parse(File.ReadAllText(config)))
            {
                check(settings.RootElement.GetProperty("theme").GetString() == "dark"
                    && settings.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString() == "Read",
                    "Claude setup preserves unrelated settings and permissions");
            }
            check(File.ReadAllText(config + ".usage-widget.bak") == original, "Claude setup preserves an exact backup of existing settings");
            string configured = File.ReadAllText(config);
            check(ClaudeUsage.ConfigureStatusLine(root, executable) && File.ReadAllText(config) == configured, "Repeating Claude setup is idempotent");
            check(ClaudeUsage.ConfigureStatusLine(root, executable + ".moved"), "Claude setup can reconnect a relocated widget-owned status line");
            check(ClaudeUsage.GetStatusLineState(root, executable + ".moved") == ClaudeUsage.StatusLineState.Connected
                && ClaudeUsage.GetStatusLineState(root, executable) == ClaudeUsage.StatusLineState.OtherWidgetPath,
                "A moved widget reports the older status-line location instead of silently keeping it");
            const string custom = "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-custom-script\",\"padding\":2}}";
            File.WriteAllText(config, custom);
            check(!ClaudeUsage.ConfigureStatusLine(root, executable) && File.ReadAllText(config) == custom,
                "Claude setup never overwrites a custom status line");
            check(ClaudeUsage.GetStatusLineState(root, executable) == ClaudeUsage.StatusLineState.Custom,
                "A custom status line is reported as custom, not as a widget path");
            File.WriteAllText(config, "{");
            try { ClaudeUsage.ConfigureStatusLine(root, executable); } catch (JsonException) { }
            check(File.ReadAllText(config) == "{", "Claude setup never overwrites malformed settings");
            check(new WidgetSettings().Provider == "Codex", "Existing widget settings continue to select Codex by default");

            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command"); start.ArgumentList.Add(ClaudeUsage.BridgeCommand(executable));
                start.Environment["CLAUDE_CONFIG_DIR"] = root;
                using var process = Process.Start(start)!;
                await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                string output = await stdout, errors = await stderr;
                if (process.ExitCode != 0 || errors.Length != 0 || !output.Contains("76.5%"))
                    Console.Error.WriteLine($"Bridge diagnostic: exit={process.ExitCode}, stdout={output}, stderr={errors}");
                check(process.ExitCode == 0 && errors.Length == 0 && output.Contains("76.5%")
                    && ClaudeUsage.Read(root).Snapshot?.Secondary?.Remaining == 58.8,
                    "Generated Windows status-line command passes stdin to the bridge and returns usable terminal output");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
