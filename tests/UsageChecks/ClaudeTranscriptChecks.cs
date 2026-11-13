using System.Text.Json;
using TokenUsageTracker;

internal static class ClaudeTranscriptChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ClaudeTranscriptChecks-" + Guid.NewGuid());
        string project = Path.Combine(folder, "projects", "C--work-app");
        Directory.CreateDirectory(Path.Combine(project, "s1", "subagents"));
        var now = DateTimeOffset.UtcNow;
        string Stamp(double hours) => now.AddHours(hours).ToString("O");
        object Usage(long input, long write, long read, long output) => new { input_tokens = input, cache_creation_input_tokens = write, cache_read_input_tokens = read, output_tokens = output };
        object Prompt(string session, string id, string text, double hours, bool agent = false) => new
        { type = "user", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isSidechain = agent, message = new { role = "user", content = text } };
        object Reply(string session, string id, string message, double hours, object usage, string text = "Done.", bool agent = false) => new
        {
            type = "assistant", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isSidechain = agent,
            message = new { id = message, model = "claude-test", content = new object[] { new { type = "tool_use", name = "Edit" }, new { type = "text", text } }, usage }
        };
        object LimitError(string session, string id, double hours) => new
        {
            type = "assistant", sessionId = session, uuid = id, timestamp = Stamp(hours), cwd = @"C:\work\app", isApiErrorMessage = true, error = "rate_limit",
            message = new { id = "synthetic-" + id, model = "<synthetic>", content = new[] { new { type = "text", text = "Claude usage limit reached. Your limit will reset at 3pm." } }, usage = Usage(0, 0, 0, 0) }
        };
        void Write(string path, params object[] lines) => File.WriteAllText(path, string.Join("\n", lines.Select(l => JsonSerializer.Serialize(l))) + "\n");
        try
        {
            string s1 = Path.Combine(project, "s1.jsonl");
            Write(s1,
                new { type = "user", sessionId = "s1", uuid = "meta", timestamp = Stamp(-1), isMeta = true, message = new { role = "user", content = "Caveat: local command output" } },
                Prompt("s1", "p1", "Fix the login bug", -1),
                Reply("s1", "r1a", "msg_1", -0.99, Usage(100, 50, 1000, 20)),
                Reply("s1", "r1b", "msg_1", -0.98, Usage(100, 50, 1000, 40)),
                new { type = "user", sessionId = "s1", uuid = "tool", timestamp = Stamp(-0.97), message = new { role = "user", content = new[] { new { type = "tool_result", content = "ok" } } } },
                Prompt("s1", "old", "An old prompt", -7),
                Reply("s1", "rold", "msg_old", -7, Usage(5000, 0, 0, 5000)));
            File.AppendAllText(s1, "{\"type\":\"assistant\",\"partial");
            Write(Path.Combine(project, "s1", "subagents", "agent-1.jsonl"),
                Prompt("s1", "ap1", "Check the fix", -0.95, agent: true),
                Reply("s1", "ar1", "msg_2", -0.94, Usage(10, 0, 0, 5), agent: true));
            var transcripts = new ClaudeTranscripts();
            var five = transcripts.ReadTasks(folder, new UsageWindow(40, 300, now.AddHours(2)), 300, now);
            var task = five.Tasks.SingleOrDefault();
            check(task?.Task.Title == "Fix the login bug" && task.Input == 1160 && task.Cached == 1000 && task.Output == 45,
                "Claude tasks group by prompt, count a streamed reply once and include linked subagent tokens");
            check(task?.EstimatedPoints == 40 && task.Task.Steps.Contains("Edit") && task.Task.Result == "Done." && task.Task.Status == "Replied",
                "Claude tasks carry the estimate, tool activity and recorded result");
            check(five.Note.Contains("Cache reads may count less"), "The Claude task note explains the cache-read caveat");
            check(transcripts.ReadTasks(folder, null, 10080, now).Tasks.Any(t => t.Task.Title == "An old prompt"),
                "Older prompts appear in the weekly lookback but not in the five-hour window");
            check(new ClaudeTranscripts().ReadTasks(Path.Combine(folder, "missing"), null, 300, now).Note.Contains("No local Claude Code transcripts"),
                "A machine without Claude transcripts gets an actionable empty state");

            string s2 = Path.Combine(project, "s2.jsonl"), s3 = Path.Combine(project, "s3.jsonl"), s4 = Path.Combine(project, "s4.jsonl");
            Write(s2, Prompt("s2", "p2", "Refactor the parser", -0.5), Reply("s2", "r2", "msg_3", -0.49, Usage(10, 0, 0, 1)), LimitError("s2", "e2", -0.48));
            Write(s3, Prompt("s3", "p3", "Earlier task", -0.5), LimitError("s3", "e3", -0.48), Prompt("s3", "p3b", "Try again later", -0.2));
            Write(s4, Prompt("s4", "p4", "Other failure", -0.5),
                new { type = "assistant", sessionId = "s4", uuid = "e4", timestamp = Stamp(-0.48), isApiErrorMessage = true, message = new { content = new[] { new { type = "text", text = "API Error: 500 overloaded" } } } });
            var found = transcripts.Interruptions(folder, now.AddDays(-1));
            check(found.Count == 1 && found[0] is { SessionId: "s2", ErrorId: "e2", Workspace: @"C:\work\app", Title: "Refactor the parser" },
                "A usage-limit error at the end of a session is an interruption; a later prompt or another API error is not");
            check(transcripts.ReadTasks(folder, null, 300, now).Tasks.Any(t => t.Task.Title == "Refactor the parser" && t.Task.Status == "Interrupted by usage limit"),
                "The task list marks a task stopped by a usage limit");
            File.Delete(s2);
            check(transcripts.Interruptions(folder, now.AddDays(-1)).Count == 0, "Deleted transcripts leave no cached interruption");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }

        string statePath = Path.Combine(Path.GetTempPath(), "ClaudeResumeChecks-" + Guid.NewGuid() + ".json");
        try
        {
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            var interruption = new ClaudeInterruption("0b7d3e1c-1111-2222-3333-444455556666", "e1", t0.AddMinutes(1), @"C:\work\it's", "Fix the login bug");
            var full = new UsageSnapshot(t0, "Claude", new(100, 300, t0.AddHours(2)), new(60, 10080, t0.AddDays(3)));
            var notices = new ClaudeResumeNotices(statePath);
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(3)).Count == 0, "Claude notices stay off until enabled");
            notices.Configure(true, t0);
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(1)).Count == 0, "No Claude notice before the full window resets");
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(3)).Count == 1 && notices.Ready.Count == 1,
                "A Claude notice is due once the full window's reset has passed");
            check(notices.Tick(new[] { interruption }, full, t0.AddHours(4)).Count == 0, "Each interruption is announced only once");
            check(new ClaudeResumeNotices(statePath).Tick(new[] { interruption }, full, t0.AddHours(4)).Count == 0,
                "Announced interruptions survive a widget restart");
            notices.Dismiss("e1");
            check(notices.Ready.Count == 0, "Dismissing removes a session from the resume list");
            check(notices.Tick(Array.Empty<ClaudeInterruption>(), full, t0.AddHours(5)).Count == 0 && notices.Ready.Count == 0,
                "A later prompt in the session cancels its notice");
            var stale = new UsageSnapshot(t0, "Claude", new(97, 300, t0.AddHours(2)), new(60, 10080, t0.AddDays(3)));
            check(!ClaudeResumeNotices.CanContinue(interruption, stale, t0.AddHours(3)),
                "With no full window known, a countdown alone never announces a session");
            var fresh = stale with { Timestamp = t0.AddMinutes(30), Primary = new(30, 300, t0.AddHours(6)) };
            check(ClaudeResumeNotices.CanContinue(interruption, fresh, t0.AddMinutes(31)),
                "A reading received after the error that shows room announces the session");
            var older = interruption with { ErrorId = "old", At = t0.AddDays(-1) };
            check(notices.Tick(new[] { older }, fresh, t0.AddHours(6)).Count == 0, "Interruptions from before enabling are ignored");
            check(ClaudeResumeNotices.Command(interruption) == @"Set-Location -LiteralPath 'C:\work\it''s'; claude --resume 0b7d3e1c-1111-2222-3333-444455556666"
                && ClaudeResumeNotices.Command(interruption with { SessionId = "x; Remove-Item" }) is null,
                "The copied resume command quotes the folder and rejects unexpected session IDs");
            check(ClaudeResumeNotices.Message(interruption).Contains("in it's") && ClaudeResumeNotices.Message(interruption).Contains("Fix the login bug"),
                "The notice names the task and its folder");
            notices.Configure(false, t0);
            check(!new ClaudeResumeNotices(statePath).Enabled, "Turning notices off clears their state");
        }
        finally { if (File.Exists(statePath)) File.Delete(statePath); }
    }
}
