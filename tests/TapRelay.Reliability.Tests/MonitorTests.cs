using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TapRelay;

internal static partial class Program
{
    private const string TraeWorkPath = @"C:\taprelay-fixtures\TraeWork CN\logs\run\window1\renderer.log";
    private const string TraeCodePath = @"C:\taprelay-fixtures\Trae CN\logs\run\window1\renderer.log";

    private static async Task TestLogsAndMonitor()
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "task-log-cases.json")));
        foreach (var sample in cases.RootElement.EnumerateArray())
        {
            var taskLog = sample.GetProperty("taskLog").GetString()!;
            await Test("PC wire preserves " + sample.GetProperty("name").GetString(), async () =>
            {
                using var server = CreateServer();
                string? wire = null;
                server.OutboundBroadcast += json => wire = json;
                await server.BroadcastFromMonitorAsync("result", "codex", "completed", "probe", "", 0, taskLog: taskLog);
                using var payload = JsonDocument.Parse(wire!);
                Require(payload.RootElement.GetProperty("taskLog").GetString() == taskLog, "Task log changed on wire");
            });
        }
        await Test("PC bounds task logs without splitting Unicode pairs", async () =>
        {
            using var server = CreateServer();
            string? wire = null;
            server.OutboundBroadcast += json => wire = json;
            await server.BroadcastFromMonitorAsync("result", "codex", "completed", "probe", "", 0,
                taskLog: new string('x', 3999) + "😀suffix");
            using var payload = JsonDocument.Parse(wire!);
            Require(payload.RootElement.GetProperty("taskLog").GetString() == new string('x', 3999), "Split Unicode pair");
        });
        await Test("TraeWork monitor sends terminal states without Toast and deduplicates", () =>
        {
            using var server = CreateServer();
            var messages = CaptureMessages(server);
            using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true, (_, time) => null);
            Feed(monitor, TraeWorkPath, Done("completed", "one"));
            Feed(monitor, TraeWorkPath, Done("completed", "one"));
            Feed(monitor, TraeWorkPath, Done("failed", "two"));
            Feed(monitor, TraeWorkPath, Done("canceled", "three"));
            Require(messages.Count == 2, "Lost terminal event or duplicated/cancelled event leaked");
            Require(messages[0].GetProperty("source").GetString() == "traework", "Wrong source route");
            Require(messages[0].GetProperty("taskLog").GetString() == "", "Invented a task log");
            Require(messages[1].GetProperty("status").GetString() == "failed", "Failure was relabelled");
            Require(PendingTrae(monitor).Count == 0, "TraeWork entered TraeCode usage wait");
            return Task.CompletedTask;
        });
        await Test("TraeCode monitor waits for usage and publishes only real Toast", () =>
        {
            using var server = CreateServer();
            var messages = CaptureMessages(server);
            using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true,
                (_, time) => ("项目结果", "已生成报告\n已完成校验"));
            Feed(monitor, TraeCodePath, Done("completed", "one"));
            Require(messages.Count == 0 && PendingTrae(monitor).Count == 1, "Completion skipped usage window");
            Feed(monitor, TraeCodePath,
                "event: cn_session_usage_tail ; params: {\"action\":\"show\",\"agent_message_id\":\"one\",\"usage_summary\":\"12.50\"}");
            Require(messages.Count == 1, "Usage-matched event not sent");
            Require(messages[0].GetProperty("source").GetString() == "trae" &&
                messages[0].GetProperty("credits").GetString() == "12.50" &&
                messages[0].GetProperty("taskLog").GetString() == "已生成报告\n已完成校验", "Lost source, usage or log");
            return Task.CompletedTask;
        });
        await Test("TraeCode expired completion rejects missing or template logs", () =>
        {
            foreach (var toast in new (string Title, string Body)?[] { null, ("项目结果", "任务已完成") })
            {
                using var server = CreateServer();
                var messages = CaptureMessages(server);
                using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true, (_, time) => toast);
                Feed(monitor, TraeCodePath, Done("completed", "one"));
                ExpireTrae(monitor);
                Require(messages.Count == 0 && PendingTrae(monitor).Count == 0, "Missing/template task log was published");
            }
            return Task.CompletedTask;
        });
        await Test("TraeCode usage timeout preserves real log and emits once", () =>
        {
            using var server = CreateServer();
            var messages = CaptureMessages(server);
            using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true,
                (_, time) => ("项目结果", "已修复两处缺陷并完成验证"));
            Feed(monitor, TraeCodePath, Done("completed", "one"));
            ExpireTrae(monitor);
            ExpireTrae(monitor);
            Require(messages.Count == 1 && messages[0].GetProperty("credits").GetString() == "", "Bad timeout/dedup behavior");
            return Task.CompletedTask;
        });
        await Test("TraeWork waiting reaches actionable card with session identity", () =>
        {
            using var server = CreateServer();
            var messages = CaptureMessages(server);
            using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true,
                (_, time) => ("等待处理", "请选择要保留的冲突文件版本"));
            Feed(monitor, TraeWorkPath,
                "[NotificationPort] Sending waiting notification: {\"sessionId\":\"probe-session\",\"agentMessageId\":\"one\",\"planItemId\":\"question\"}");
            Require(messages.Count == 1 && messages[0].GetProperty("status").GetString() == "waiting" &&
                messages[0].GetProperty("requiresAction").GetBoolean() &&
                messages[0].GetProperty("sessionId").GetString() == "probe-session", "Waiting metadata was lost");
            return Task.CompletedTask;
        });
        await Test("WorkBuddy stop hooks are not task completions", () =>
        {
            const string workBuddyPath =
                @"C:\Users\Admin\.workbuddy\logs\2026-09-27\世界杯球赛分析项目__fixture.log";
            const string sessionId = "workbuddy-compaction-session";
            using var server = CreateServer();
            var messages = CaptureMessages(server);
            using var monitor = new AgentCompletionMonitor(server, _ => { }, _ => true);

            Feed(monitor, workBuddyPath,
                "[SessionManager] entry opts: sessionId=" + sessionId + " serve=true");
            Feed(monitor, workBuddyPath,
                "[SessionManager][Gate] willRetry=true reason=compacting session=" + sessionId);
            Feed(monitor, workBuddyPath,
                "[SessionHookManager] executeStopHooks sessionId=" + sessionId +
                " stopHookActive=false stopReason=completed");
            Require(messages.Count == 0, "Non-authoritative stop hook was published as completion");

            Feed(monitor, workBuddyPath,
                "[SessionRunStateMachine] transition | sessionId=" + sessionId +
                " | event=COMPACT_ENDED | from=compacting | to=idle");
            Feed(monitor, workBuddyPath,
                "[SessionHookManager] executeStopHooks sessionId=" + sessionId +
                " stopHookActive=false stopReason=completed");
            Require(messages.Count == 0, "Stop hook was published after compaction ended");
            Feed(monitor, workBuddyPath,
                "[AcpView] Prompt completed for session " + sessionId +
                ", stopReason: end_turn, finishReason: stop");
            Require(messages.Count == 1 &&
                    messages[0].GetProperty("status").GetString() == "completed" &&
                    messages[0].GetProperty("sessionId").GetString() == sessionId,
                "Real completion after compaction was lost");
            return Task.CompletedTask;
        });
    }

    private static List<JsonElement> CaptureMessages(NotifyServer server)
    {
        var messages = new List<JsonElement>();
        server.OutboundBroadcast += json => { using var doc = JsonDocument.Parse(json); messages.Add(doc.RootElement.Clone()); };
        return messages;
    }

    private static string Done(string status, string messageId) =>
        "[DoneHandler] Stream done event received " + JsonSerializer.Serialize(new
        { sessionId = "probe-session", status, agentMessageId = messageId });

    private static void Feed(AgentCompletionMonitor monitor, string path, string line) =>
        typeof(AgentCompletionMonitor).GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, new object[] { path, line });

    private static IList PendingTrae(AgentCompletionMonitor monitor) =>
        (IList)typeof(AgentCompletionMonitor).GetField("_pendingTraeCompletions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(monitor)!;

    private static void ExpireTrae(AgentCompletionMonitor monitor)
    {
        foreach (var entry in PendingTrae(monitor))
            entry!.GetType().GetProperty("DeadlineUtc")!.SetValue(entry, DateTime.MinValue);
        typeof(AgentCompletionMonitor).GetMethod("FlushExpiredTraeCompletions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, Array.Empty<object>());
    }
}
