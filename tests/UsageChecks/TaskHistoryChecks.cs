using System.Text.Json;
using TokenUsageTracker;

internal static class TaskHistoryChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "TaskHistoryChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(folder, "sessions"));
        var now = DateTimeOffset.UtcNow;
        string path = Path.Combine(folder, "sessions", "root.jsonl");
        void Emit(string file, string kind, object payload, double hours = -2) => File.AppendAllText(file, JsonSerializer.Serialize(new { timestamp = now.AddHours(hours), type = kind, payload }) + "\n");
        void Start(string file, string turn, string prompt, double hours = -2, string? root = null)
        {
            Emit(file, "event_msg", new { type = "task_started", turn_id = turn, root_turn_id = root ?? turn }, hours);
            Emit(file, "turn_context", new { turn_id = turn, root_turn_id = root ?? turn, model = "test-model", cwd = "workspace" }, hours);
            Emit(file, "response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = prompt } } }, hours);
        }
        void Tokens(string file, string session, string turn, string response, long input, long cached, long output, double hours = -2) => Emit(file, "token_usage_record", new { thread_id = session, turn_id = turn, root_turn_id = turn, response_id = response, usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output } }, hours);
        try
        {
            Emit(path, "session_meta", new { id = "root", source = "cli", cwd = "workspace" });
            Start(path, "old", "Older task", -6);
            Tokens(path, "root", "old", "old-response", 100, 0, 10, -6);
            Start(path, "big", "## My request:\nFix the expensive task");
            Tokens(path, "root", "big", "big-response", 1000, 800, 100);
            Tokens(path, "root", "big", "big-response", 1000, 800, 100);
            Emit(path, "event_msg", new { type = "token_count", info = new { total_token_usage = new { input_tokens = 1100, cached_input_tokens = 800, output_tokens = 110 } } });
            Emit(path, "event_msg", new { type = "task_complete", turn_id = "big", last_agent_message = "Fixed it." });
            Start(path, "small", "Smaller task", -0.5);
            Tokens(path, "root", "small", "small-response", 200, 0, 10, -0.5);
            Start(path, "pending", "Pending task", -0.1);
            File.AppendAllText(path, "{partial\n");
            string agent = Path.Combine(folder, "sessions", "agent.jsonl");
            Emit(agent, "session_meta", new { id = "agent", parent_thread_id = "root", source = new { subagent = "thread_spawn" } });
            Start(agent, "agent-turn", "Check the fix", -1, "big");
            // An agent's root turn belongs to its parent; its own turn remains distinct.
            Emit(agent, "token_usage_record", new { thread_id = "agent", turn_id = "agent-turn", root_turn_id = "big", response_id = "agent-response", usage = new { input_tokens = 300, cached_input_tokens = 100, output_tokens = 100 } }, -1);
            var history = new TaskHistory();
            var five = history.Read(folder, new(34.2, 300, now.AddHours(1)), 300, now);
            check(five.Tasks.Count == 3 && five.Tasks[0].Task.Turn == "big" && five.Tasks[1].Task.Turn == "small", "Task ranking uses active reset boundaries and includes prompts without recorded tokens");
            var big = five.Tasks[0];
            check(big.Total == 1500 && big.Cached == 900 && Math.Abs(big.EstimatedPoints!.Value - 30) < 0.001, "Modern response IDs deduplicate usage, exclude duplicate legacy counts and include agent tokens without adding cached input twice");
            check(big.Task.Title == "Fix the expensive task" && big.Details.Contains("Fixed it.") && big.Subtasks.Contains("Check the fix"), "Task details preserve the prompt, completion and linked subtask");
            var weekly = history.Read(folder, new(50, 10080, now.AddDays(1)), 10080, now);
            check(weekly.Tasks.Count == 4 && weekly.Tasks.Any(t => t.Task.Turn == "old"), "Weekly ranking includes tasks outside the five-hour window");
            var unknown = history.Read(folder, new(34, 300, now.AddMinutes(-1)), 300, now);
            check(unknown.Tasks.All(t => t.EstimatedPoints is null) && unknown.Note.Contains("rolling"), "Expired windows use an explicit rolling lookback without inventing quota costs");
            Start(path, "new", "New appended task", -0.05);
            Tokens(path, "root", "new", "new-response", 10, 0, 1, -0.05);
            check(history.Read(folder, null, 300, now).Tasks.Count == 4, "Appending a live session invalidates the task cache and tolerates partial JSON");
            File.Delete(agent);
            check(history.Read(folder, null, 300, now).Tasks.First().Total == 1100, "Deleted agent sessions no longer contribute cached tokens");
            string legacy = Path.Combine(folder, "legacy.jsonl");
            Emit(legacy, "session_meta", new { id = "legacy", source = "cli" });
            Start(legacy, "one", "First legacy task");
            var count = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = 10 } } };
            Emit(legacy, "event_msg", count); Emit(legacy, "event_msg", count);
            Start(legacy, "two", "Second legacy task");
            Emit(legacy, "event_msg", new { type = "token_count", info = new { total_token_usage = new { input_tokens = 180, cached_input_tokens = 90, output_tokens = 30 } } });
            var old = TaskHistory.Rank(TaskHistory.Parse(legacy), now.AddHours(-5), now, 20);
            check(old.Tasks.Count == 2 && old.Tasks.Sum(t => t.Total) == 210, "Legacy session counters are differenced across turns and repeated counters add no cost");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
