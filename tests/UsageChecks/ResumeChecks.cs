using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenUsageTracker;

internal static class ResumeChecks
{
    private const string Chat = "11111111-1111-4111-8111-111111111111";
    private const string Turn = "22222222-2222-4222-8222-222222222222";
    private static readonly string Account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fake-account")));
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static object Limits(bool? allowed = true, double weekly = 12) => new
    {
        accountId = "fake-account", ordinaryUsageAllowed = allowed,
        rateLimits = new
        {
            limitId = "codex", planType = "plus",
            primary = new { usedPercent = 25.5, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() },
            secondary = new { usedPercent = weekly, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() }
        }
    };

    public static void Fake(string scenario)
    {
        string? line;
        bool initialized = false;
        while ((line = Console.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line); var request = doc.RootElement;
            string method = request.GetProperty("method").GetString()!;
            if (method == "initialized") { initialized = true; continue; }
            if (!request.TryGetProperty("id", out var id)) continue;
            if (scenario == "timeout") { Thread.Sleep(5000); continue; }
            if (method != "initialize" && !initialized) throw new Exception("Handshake notification missing");
            var parameters = request.GetProperty("params");
            if (method.StartsWith("thread/", StringComparison.Ordinal) && parameters.GetProperty("threadId").GetString() != Chat)
                throw new Exception("Wrong chat addressed");
            object result = method switch
            {
                "initialize" => new { userAgent = "fake-codex" },
                "account/read" => new { account = new { type = "chatgpt", email = "fake@example.invalid" } },
                "account/rateLimits/read" => Limits(),
                "thread/read" => new
                {
                    thread = new { id = Chat, status = new { type = scenario == "active" ? "active" : "idle" },
                        turns = new[] { new { id = Turn, status = "failed", error = new { codexErrorInfo = "usageLimitExceeded" } } } }
                },
                "thread/queue/list" => new { data = Array.Empty<object>() },
                "thread/queue/add" => new { queuedSubmission = new { id = "queued-test-message" } },
                "thread/queue/start" => new { turn = new { id = "started-test-turn", status = scenario == "queue-only" ? "failed" : "inProgress" } },
                "thread/queue/delete" => new { deleted = true },
                _ => throw new Exception("Unexpected RPC: " + method)
            };
            Console.WriteLine(JsonSerializer.Serialize(new { method = "account/rateLimits/updated", @params = new { } }));
            Console.WriteLine(JsonSerializer.Serialize(new { id = id.Clone(), result }));
        }
    }

    private static Task<CodexClient> FakeClient(string scenario, CancellationToken token)
    {
        var start = CodexClient.StartInfo(Environment.ProcessPath!, Path.GetTempPath(), "--fake-codex", scenario);
        return CodexClient.ConnectProcessAsync(start, token);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var usage = CodexClient.ParseUsage(Element(Limits()), now, Account);
        check(usage.Snapshot.Primary?.Remaining == 74.5 && usage.CanResume(now), "Fresh included usage allows recovery with the reported percentage");
        check(!usage.CanResume(now.AddSeconds(31)), "A stale live response cannot authorize recovery");
        check(!CodexClient.ParseUsage(Element(Limits(null)), now, Account).CanResume(now), "Missing ordinaryUsageAllowed never implies recovery");
        check(!CodexClient.ParseUsage(Element(Limits(false)), now, Account).CanResume(now), "Backend denial blocks recovery despite available percentages");
        check(!CodexClient.ParseUsage(Element(Limits(weekly: 100)), now, Account).CanResume(now), "An exhausted weekly window prevents five-hour recovery");
        check(!CodexClient.ParseUsage(Element(Limits()), now, null).CanResume(now), "An unidentified account cannot authorize recovery");
        var general = Element(Limits()).GetProperty("rateLimits");
        var multi = Element(new { ordinaryUsageAllowed = true, rateLimitsByLimitId = new { codex = general, other = new { spendControlReached = true } } });
        check(!CodexClient.ParseUsage(multi, now, Account).CanResume(now), "Another reported spend-control block prevents recovery");
        var expired = usage with { Snapshot = usage.Snapshot with { Primary = new(0, 300, now) } };
        check(!expired.CanResume(now), "Passing a reset time does not restore the allowance");
        bool rejected = false;
        try { CodexClient.ParseUsage(Element(new { rateLimitsByLimitId = new { other = new { } }, rateLimits = Limits() }), now, Account); }
        catch (IOException) { rejected = true; }
        check(rejected, "A model-specific bucket cannot substitute for the general allowance");
        foreach (string json in new[] { "null", "[]", "{}", "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":\"bad\"}}}" })
        {
            using var doc = JsonDocument.Parse(json);
            try { CodexClient.ParseUsage(doc.RootElement, now, Account); }
            catch (IOException) { }
        }
        check(true, "Malformed live responses fail without inventing usage");

        string home = Path.Combine(Path.GetTempPath(), "CodexResumeChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        string path = Path.Combine(home, "sessions", "fixture.jsonl");
        string statePath = Path.Combine(home, "state.json");
        string Record(string kind, object payload) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-1), type = kind, payload }) + "\n";
        string Meta(object source, string originator = "Codex Desktop") => Record("session_meta", new { id = Chat, source, originator, model_provider = "openai" });
        string context = Record("turn_context", new { turn_id = Turn, cwd = home, model = "gpt-test", approval_policy = "on-request", sandbox_policy = new { type = "read-only" } });
        string End(string kind = "task_complete", string? error = "usage_limit_exceeded") => Record("event_msg", new { type = kind, turn_id = Turn, error = error is null ? null : new { codex_error_info = error } });
        string fixture = Meta("vscode") + context + End();
        try
        {
            File.WriteAllText(path, fixture);
            var task = ResumeController.ReadInterrupted(path)!;
            check(task.ChatId == Chat && task.TurnId == Turn && task.Target == "Desktop", "Structured quota failure captures the exact root chat and turn");
            File.WriteAllText(path, Meta(new { subagent = "worker" }) + context + End());
            check(ResumeController.ReadInterrupted(path) is null, "Spawned agents are excluded");
            File.WriteAllText(path, Meta("vscode") + context + End(error: "unauthorized"));
            check(ResumeController.ReadInterrupted(path) is null, "Authentication and non-quota errors are never scheduled");
            File.WriteAllText(path, Meta("vscode") + context + End("turn_failed"));
            check(ResumeController.ReadInterrupted(path) is not null, "Structured turn_failed records are supported");
            foreach (string ending in new[] { End(error: null), End("turn_aborted"), Record("event_msg", new { type = "user_message" }), Record("event_msg", new { type = "task_started" }) })
            {
                File.WriteAllText(path, fixture + ending);
                check(ResumeController.ReadInterrupted(path) is null, "Later completion, cancellation, user activity or active turn invalidates recovery");
            }
            File.WriteAllText(path, fixture + Record("response_item", new { type = "function_call_output", output = new string('x', 200_000) }));
            check(ResumeController.ReadInterrupted(path) is not null, "Session tail scanning reaches the failed turn behind a large output");
            File.AppendAllText(path, "{\"type\":\"event_msg\"");
            check(ResumeController.ReadInterrupted(path) is null, "A partially written action blocks an older quota failure");
            File.WriteAllText(path, fixture);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var fake = await FakeClient("normal", timeout.Token))
            {
                var actual = await fake.ReadUsageAsync(timeout.Token);
                check(actual.Account == Account && actual.CanResume(DateTimeOffset.UtcNow), "Fake process verifies initialization, notifications, account identity and live RPC transport");
                string? queued = null;
                string turn = await fake.ResumeDesktopAsync(task, id => queued = id, timeout.Token);
                check(queued == "queued-test-message" && turn == "started-test-turn", "Desktop recovery queues and verifies a started turn in the exact chat");
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var fake = await FakeClient("queue-only", timeout.Token))
            {
                bool blocked = false;
                try { await fake.ResumeDesktopAsync(task, _ => { }, timeout.Token); } catch (IOException) { blocked = true; }
                check(blocked, "A queue acknowledgement without a started turn is not reported as resumed");
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                bool timedOut = false;
                try { using var fake = await FakeClient("timeout", timeout.Token); } catch (OperationCanceledException) { timedOut = true; }
                check(timedOut, "An unresponsive Codex process times out and is disposed");
            }

            int connections = 0;
            Task<CodexClient> Connect(string _, bool desktop, CancellationToken token) { connections++; return FakeClient("normal", token); }
            using (var controller = new ResumeController(statePath, Connect))
            {
                controller.Configure(true, "Desktop", home, now);
                await controller.TickAsync(usage, CancellationToken.None);
                check(connections == 0, "Enabling recovery does not resume historical failed tasks");
                controller.Configure(false, "Desktop", home, now);
                controller.Configure(true, "Desktop", home, now.AddMinutes(-2));
                await controller.TickAsync(CodexClient.ParseUsage(Element(Limits(weekly: 100)), DateTimeOffset.UtcNow, Account), CancellationToken.None);
                check(controller.PendingCount == 1 && connections == 0, "Pending tasks wait through an exhausted weekly window");
                await controller.TickAsync(usage, CancellationToken.None);
                check(connections == 1 && controller.PendingCount == 0, "Eligible recovery dispatches once after fresh quota and task checks");
                await controller.TickAsync(usage, CancellationToken.None);
                check(connections == 1, "Repeated polls cannot dispatch the same failed turn twice");
            }
            using (var controller = new ResumeController(statePath, Connect))
            {
                controller.Configure(true, "Desktop", home, now);
                await controller.TickAsync(usage, CancellationToken.None);
                check(connections == 1, "Restart preserves already-dispatched state without duplicating work");
                controller.Configure(true, "CLI", home, now);
                check(controller.PendingCount == 0, "Changing target mode clears pending tasks");
                controller.Configure(false, "CLI", home, now);
                check(controller.PendingCount == 0, "Disabling recovery clears pending tasks");
            }
            var state = new ResumeState { Home = home, Target = "Desktop", EnabledAt = now.AddMinutes(-2), Account = Account,
                Entries = new() { [Chat] = new() { Task = task, Stage = "Dispatching" } } };
            File.WriteAllText(statePath, JsonSerializer.Serialize(state));
            using (var controller = new ResumeController(statePath, Connect))
            {
                controller.Configure(true, "Desktop", home, now);
                await controller.TickAsync(usage, CancellationToken.None);
                check(connections == 1 && controller.Status.Contains("attention"), "A crash during dispatch requires attention instead of risking a duplicate");
            }
            File.Delete(statePath);
            using (var controller = new ResumeController(statePath, Connect))
            {
                controller.Configure(true, "Desktop", home, now.AddMinutes(-2));
                await controller.TickAsync(CodexClient.ParseUsage(Element(Limits(false)), DateTimeOffset.UtcNow, Account), CancellationToken.None);
                File.AppendAllText(path, Record("event_msg", new { type = "user_message" }));
                await controller.TickAsync(usage, CancellationToken.None);
                check(controller.PendingCount == 0 && connections == 1, "A new user message cancels a pending attempt before dispatch");
            }
            File.Delete(statePath); File.WriteAllText(path, fixture);
            using (var controller = new ResumeController(statePath, Connect))
            {
                controller.Configure(true, "Desktop", home, now.AddMinutes(-2));
                await controller.TickAsync(CodexClient.ParseUsage(Element(Limits(false)), DateTimeOffset.UtcNow, Account), CancellationToken.None);
                await controller.TickAsync(usage with { Account = "different-account" }, CancellationToken.None);
                check(controller.PendingCount == 0 && connections == 1, "An account change discards previous recovery tasks");
            }
            File.Delete(statePath);
            ResumeController? disabling = null;
            async Task<CodexClient> DisableDuringConnect(string _, bool desktop, CancellationToken token)
            {
                var client = await FakeClient("normal", token);
                disabling!.Configure(false, "Desktop", home, DateTimeOffset.UtcNow);
                return client;
            }
            using (disabling = new ResumeController(statePath, DisableDuringConnect))
            {
                disabling.Configure(true, "Desktop", home, now.AddMinutes(-2));
                await disabling.TickAsync(usage, CancellationToken.None);
                check(disabling.PendingCount == 0 && JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(statePath))!.EnabledAt == default,
                    "Disabling during a connection prevents dispatch and clears pending recovery");
            }

            var cli = task with { Target = "CLI" };
            var start = ResumeController.CliStartInfo(cli, home, "codex.exe");
            check(start.ArgumentList.Contains(Chat) && !start.ArgumentList.Contains("--last") && start.WorkingDirectory == home
                && start.ArgumentList.Contains("approval_policy=\"on-request\""), "CLI resumes the exact UUID in its workspace with recorded approval policy");
            bool unsafePolicy = false;
            try { ResumeController.CliStartInfo(cli with { SandboxJson = "{\"type\":\"danger-full-access\"}" }, home, "codex.exe"); } catch (IOException) { unsafePolicy = true; }
            check(unsafePolicy, "Untranslatable CLI permissions require manual continuation");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
