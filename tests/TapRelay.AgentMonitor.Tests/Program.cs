using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;
using TapRelay;

internal static class Program
{
    private static int _passed;
    private static int _skipped;
    private static bool _liveFreebuff;
    private static readonly List<string> Failures = new List<string>();

    private static int Main(string[] args)
    {
        _liveFreebuff = args.Contains("--live-freebuff", StringComparer.Ordinal);
        Test("ZCode CLI Windows path is supported", () =>
            AgentLogPaths.IsSupportedFile(@"C:\Users\alice\.zcode\cli\log\zcode-2026-07-31.jsonl"));
        Test("ZCode CLI Unix path is supported", () =>
            AgentLogPaths.IsSupportedFile("/home/alice/.zcode/cli/log/zcode-2026-07-31.jsonl"));
        Test("ZCode desktop macOS path is supported", () =>
            AgentLogPaths.IsSupportedFile("/Users/alice/.zcode/v2/logs/2026-07-31.log"));
        Test("ChatGPT Unix session is supported", () =>
            AgentLogPaths.IsChatGPTSession("/home/alice/.codex/sessions/2026/07/31/session.jsonl"));
        Test("Claude Unix transcript is supported", () =>
            AgentLogPaths.IsClaudeTranscript("/home/alice/.claude/projects/demo/session.jsonl"));
        Test("Claude subagent transcript is excluded", () =>
            !AgentLogPaths.IsClaudeTranscript("/home/alice/.claude/projects/demo/subagents/child.jsonl"));
        Test("Trae macOS renderer path is supported", () =>
            AgentLogPaths.IsTraeLog("/Users/alice/Library/Application Support/Trae CN/logs/main/renderer.log"));
        Test("TRAE SOLO legacy renderer path is recognized", () =>
            AgentLogPaths.IsTraeWorkRendererLog("/home/alice/.config/TRAE SOLO CN/logs/main/renderer.log"));
        Test("TraeWork CN renderer path is recognized", () =>
            AgentLogPaths.IsTraeWorkRendererLog("/home/alice/.config/TraeWork CN/logs/main/renderer.log"));
        Test("Unrelated renderer log is rejected", () =>
            !AgentLogPaths.IsSupportedFile("/tmp/random/renderer.log") ||
            !AgentLogPaths.IsTraeLog("/tmp/random/renderer.log"));
        Test("WorkBuddy ACP log path is supported", () =>
            AgentLogPaths.IsSupportedFile(@"C:\Users\alice\.workbuddy\logs\2026-08-04\workspace__hash.log") &&
            AgentLogPaths.IsWorkBuddyLog(@"C:\Users\alice\.workbuddy\logs\2026-08-04\workspace__hash.log"));
        Test("WorkBuddy AI ACP log path is supported independently", () =>
            AgentLogPaths.IsSupportedFile(@"C:\Users\alice\.workbuddy-ai\logs\2026-08-04\workspace__hash.log") &&
            AgentLogPaths.IsWorkBuddyAiLog(@"C:\Users\alice\.workbuddy-ai\logs\2026-08-04\workspace__hash.log") &&
            !AgentLogPaths.IsWorkBuddyLog(@"C:\Users\alice\.workbuddy-ai\logs\2026-08-04\workspace__hash.log"));
        Test("Unrelated WorkBuddy-looking log path is rejected", () =>
            !AgentLogPaths.IsWorkBuddyLog(@"C:\Users\alice\.workbuddy\log-archive\workspace.log"));
        Test("WorkBuddy ACP prefers the live endpoint for the requested session", () =>
        {
            var registered = new List<(string SessionId, string Endpoint)>
            {
                ("other-session", "http://127.0.0.1:63949"),
                ("target-session", "http://127.0.0.1:58479"),
                ("target-session", "http://127.0.0.1:39482")
            };
            var ordered = WorkBuddyAcpAgentProxy.OrderEndpointCandidates(
                "target-session",
                registered,
                new[] { "http://127.0.0.1:39482" });
            return ordered.Count >= 3 &&
                   ordered[0] == "http://127.0.0.1:39482" &&
                   ordered[1] == "http://127.0.0.1:58479" &&
                   ordered[2] == "http://127.0.0.1:63949";
        });
        Test("WorkBuddy ACP sandbox config uses string values", () =>
            WorkBuddyAcpAgentProxy.ToAcpSandboxValue(true) == "true" &&
            WorkBuddyAcpAgentProxy.ToAcpSandboxValue(false) == "false");
        Test("WorkBuddy ACP latest continuation cancels queued prompts", () =>
        {
            using var document = JsonDocument.Parse(
                WorkBuddyAcpAgentProxy.BuildSessionCancelRequest("workbuddy-session"));
            var root = document.RootElement;
            return root.GetProperty("method").GetString() == "session/cancel" &&
                   root.GetProperty("params").GetProperty("sessionId").GetString() == "workbuddy-session" &&
                   !root.TryGetProperty("id", out _);
        });
        Test("WorkBuddy ACP queue requests target the session and item", () =>
        {
            using var getDocument = JsonDocument.Parse(
                WorkBuddyAcpAgentProxy.BuildSessionGetMessageQueueRequest("workbuddy-session"));
            using var removeDocument = JsonDocument.Parse(
                WorkBuddyAcpAgentProxy.BuildSessionRemoveQueueItemRequest(
                    "workbuddy-session",
                    "queued-item-1"));
            var getRoot = getDocument.RootElement;
            var removeRoot = removeDocument.RootElement;
            return getRoot.GetProperty("method").GetString() == "session/get_message_queue" &&
                   getRoot.GetProperty("params").GetProperty("sessionId").GetString() ==
                       "workbuddy-session" &&
                   removeRoot.GetProperty("method").GetString() == "session/remove_queue_item" &&
                   removeRoot.GetProperty("params").GetProperty("sessionId").GetString() ==
                       "workbuddy-session" &&
                   removeRoot.GetProperty("params").GetProperty("itemId").GetString() ==
                       "queued-item-1" &&
                   removeRoot.TryGetProperty("id", out _);
        });
        Test("WorkBuddy ACP queue response extracts unique IDs from SSE", () =>
        {
            const string response = "event: response\n" +
                                    "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{" +
                                    "\"items\":[{" +
                                    "\"id\":\"queued-item-1\"},{" +
                                    "\"id\":\"queued-item-2\"},{" +
                                    "\"id\":\"queued-item-1\"},{\"text\":\"no-id\"}]}}\n";
            return WorkBuddyAcpAgentProxy.TryReadQueueItemIds(
                       response,
                       out var itemIds) &&
                   itemIds.SequenceEqual(new[] { "queued-item-1", "queued-item-2" });
        });
        Test("WorkBuddy ACP empty queue response is successful", () =>
        {
            return WorkBuddyAcpAgentProxy.TryReadQueueItemIds(
                       "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"items\":[]}}",
                       out var itemIds) &&
                   itemIds.Count == 0 &&
                   WorkBuddyAcpAgentProxy.TryReadQueueItemIds(
                       "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
                       out var missingItems) &&
                   missingItems.Count == 0;
        });
        Test("WorkBuddy ACP queue error response is rejected", () =>
        {
            return !WorkBuddyAcpAgentProxy.TryReadQueueItemIds(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601}}",
                out var itemIds) &&
                   itemIds.Count == 0;
        });
        Test("WorkBuddy ACP full access handles permission requests", () =>
        {
            const string request = "{\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"session/request_permission\",\"params\":{" +
                                   "\"sessionId\":\"workbuddy-session\",\"options\":[" +
                                   "{\"optionId\":\"allow_always\"},{\"optionId\":\"allow\"},{\"optionId\":\"reject\"}]}}";
            return WorkBuddyAcpAgentProxy.TryBuildPermissionResponse(
                       request,
                       true,
                       out var response,
                       out var sessionId,
                       out var decision) &&
                   sessionId == "workbuddy-session" &&
                   decision == "selected:allow" &&
                   JsonDocument.Parse(response).RootElement
                       .GetProperty("result").GetProperty("outcome").GetProperty("outcome")
                       .GetString() == "selected" &&
                   JsonDocument.Parse(response).RootElement
                       .GetProperty("result").GetProperty("outcome").GetProperty("optionId")
                       .GetString() == "allow";
        });
        Test("WorkBuddy ACP default permission requests do not hang", () =>
        {
            const string request = "{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"session/request_permission\",\"params\":{" +
                                   "\"sessionId\":\"workbuddy-session\",\"options\":[{\"optionId\":\"allow\"}]}}";
            return WorkBuddyAcpAgentProxy.TryBuildPermissionResponse(
                       request,
                       false,
                       out var response,
                       out _,
                       out var decision) &&
                   decision == "cancelled" &&
                   JsonDocument.Parse(response).RootElement
                       .GetProperty("result").GetProperty("outcome").GetProperty("outcome")
                       .GetString() == "cancelled";
        });
        Test("OpenCode continuation uses the supported run session command", () =>
        {
            var args = ShellAgentConfig.OpenCode.BuildCommand("ses_test", "继续检查当前任务", @"W:\TapRelay");
            return args.StartsWith("run --session \"ses_test\" ", StringComparison.Ordinal) &&
                   args.EndsWith("\"继续检查当前任务\"", StringComparison.Ordinal) &&
                   args.IndexOf("session \"ses_test\" --send", StringComparison.Ordinal) < 0;
        });

        Test("Qwen is explicitly blacklisted", () =>
            UnsupportedAppRegistry.IsUnsupported(UnsupportedAppRegistry.QwenKey) &&
            UnsupportedAppRegistry.IsUnsupported("qianwen") &&
            UnsupportedAppRegistry.LooksLikeUnsupportedPath(@"C:\Program Files\QianwenApp") &&
            UnsupportedAppRegistry.TryGetMessage("\u5343\u95ee", out var message) &&
            message.IndexOf("\u6682\u4e0d\u652f\u6301", StringComparison.Ordinal) >= 0);

        Test("ChatGPT native duration_ms is preserved", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-03T04:47:54.011Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"codex-duration\",\"duration_ms\":594070}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.DurationMs == 594070L;
        });

        Test("Trae native duration is preserved as milliseconds", () =>
        {
            const string line = "2026-08-03T09:22:29.587+08:00 event: code_comp_complete_shown ; params: {\"session_id\":\"trae-duration\",\"message_id\":\"message-1\",\"duration\":93124,\"is_interrupted\":0}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.DurationMs == 93124L;
        });

        Test("TRAE SOLO duration pairs live turn start with DoneHandler", () =>
        {
            const string start = "2026-08-02T19:37:37.285+08:00 [info] reason=live_turn_started_during_fetch {\"sessionId\":\"solo-duration\"}";
            const string end = "2026-08-02T19:38:03.870+08:00 [info] [DoneHandler] Stream done event received {\"sessionId\":\"solo-duration\",\"status\":\"completed\",\"agentMessageId\":\"message-1\"}";
            var timing = new AgentTaskTiming();
            timing.ObserveLine("traework", start);
            return AgentCompletionEventParser.TryParseTraeWorkLine(end, out var parsed) &&
                   timing.Resolve(parsed, end) == 26585L;
        });

        Test("ZCode duration pairs turn.started with terminal turn", () =>
        {
            const string start = "{\"timestamp\":\"2026-08-02T03:15:50.436Z\",\"event\":\"turn.started\",\"sessionId\":\"session-1\",\"turnId\":\"turn-1\",\"status\":\"started\"}";
            const string end = "{\"timestamp\":\"2026-08-02T03:15:52.929Z\",\"event\":\"turn.failed\",\"sessionId\":\"session-1\",\"turnId\":\"turn-1\",\"status\":\"failed\"}";
            var timing = new AgentTaskTiming();
            timing.ObserveLine("zcode", start);
            return AgentCompletionEventParser.TryParseZCodeCliLine(end, out var parsed) &&
                   timing.Resolve(parsed, end) == 2493L;
        });

        Test("Trae failed task duration falls back to sendChatMessage timestamp", () =>
        {
            const string start = "2026-08-03T09:20:42.132+08:00 [info] [ai-chat][sendChatMessage] sessionId=trae-failed-duration, agentRunInfo=undefined";
            const string end = "2026-08-03T09:20:43.909+08:00 event: code_comp_fail ; params: {\"session_id\":\"trae-failed-duration\",\"message_id\":\"message-1\",\"error_message\":\"quota exceeded\"}";
            var timing = new AgentTaskTiming();
            timing.ObserveLine("trae", start);
            return AgentCompletionEventParser.TryParseTraeLine(end, "trae", out var parsed) &&
                   timing.Resolve(parsed, end) == 1777L;
        });

        Test("Doubao request duration uses the full chat request timing", () =>
        {
            const string line = "[rlog] req_id=doubao-duration ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"text/event-stream\"},\"timing\":{\"request\":{\"duration\":23984}}}";
            return AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out var parsed, readRenderedContent: false) &&
                   parsed.DurationMs == 23984L;
        });

        Test("Doubao new IM chain/single submit acknowledgement is ignored", () =>
        {
            // 8-23 起新版豆包先走 /im/chain/single 提交请求，普通 JSON 200
            // 只表示请求已接收，不能代表模型已完成。
            const string line = "[rlog] req_id=doubao-im-1 ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/im/chain/single\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"application/json; charset=utf-8\"}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Doubao non-streaming chat acknowledgement is ignored", () =>
        {
            const string line = "[rlog] req_id=doubao-chat-ack ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"application/json; charset=utf-8\"}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Doubao streaming completion is parsed as completed", () =>
        {
            const string line = "[rlog] req_id=doubao-im-stream ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"text/event-stream\"}}";
            return AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out var parsed, readRenderedContent: false) &&
                   parsed.Status == "completed" &&
                   parsed.TaskId == "doubao-im-stream";
        });

        Test("Doubao new IM chain/single non-200 response parses as failed", () =>
        {
            const string line = "[rlog] req_id=doubao-im-fail ret=Ok(500) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/im/chain/single\",\"status\":\"SUCCESS\"},\"response\":{\"code\":500}}";
            return AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out var parsed, readRenderedContent: false) &&
                   parsed.Status == "failed" &&
                   parsed.TaskId == "doubao-im-fail";
        });

        Test("Codex Hume subagent session metadata is recognized", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-03T03:52:53.447Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"id\":\"019fc5c0-c5e9-7c72-bad8-892fb0ef07fd\",\"forked_from_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"parent_thread_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"depth\":1,\"agent_path\":\"/root/reference_assets\",\"agent_nickname\":\"Hume\",\"agent_role\":null}}},\"thread_source\":\"subagent\",\"agent_nickname\":\"Hume\",\"agent_path\":\"/root/reference_assets\"}}";
            return CodexSessionMetadata.ClassifyLine(line) == CodexSessionKind.Subagent;
        });

        Test("Codex Kant existing session file is recognized as subagent", () =>
        {
            const string metadata = "{\"timestamp\":\"2026-08-03T04:24:36.080Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"id\":\"019fc5dd-cdec-7f33-be6c-71222ade9fd4\",\"forked_from_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"parent_thread_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"depth\":1,\"agent_path\":\"/root/motion_review\",\"agent_nickname\":\"Kant\",\"agent_role\":null}}},\"thread_source\":\"subagent\",\"agent_nickname\":\"Kant\",\"agent_path\":\"/root/motion_review\"}}";
            const string completion = "{\"timestamp\":\"2026-08-03T04:32:46.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"019fc5dd-d008-71d2-8728-43718ad229e0\"}}";
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-codex-session-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "rollout.jsonl");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(path, metadata + "\n" + completion + "\n", new UTF8Encoding(false));
                return CodexSessionMetadata.ReadFromFile(path) == CodexSessionKind.Subagent;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Codex primary user session metadata remains eligible", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-03T03:46:28.915Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"source\":\"vscode\",\"thread_source\":\"user\",\"originator\":\"Codex Desktop\"}}";
            return CodexSessionMetadata.ClassifyLine(line) == CodexSessionKind.Primary;
        });

        Test("Codex session metadata exposes its resumable session id", () =>
        {
            const string line = "{\"type\":\"session_meta\",\"payload\":{\"sessionId\":\"resume-session-id\",\"id\":\"fallback-id\"}}";
            return CodexSessionMetadata.TryReadSessionId(line) == "resume-session-id";
        });

        Test("Codex session id can be recovered from an existing session file", () =>
        {
            const string metadata = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"resume-file-id\"}}";
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-codex-resume-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "rollout.jsonl");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(path, "{\"type\":\"event_msg\"}\n" + metadata + "\n", new UTF8Encoding(false));
                return CodexSessionMetadata.ReadSessionIdFromFile(path) == "resume-file-id";
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Codex working directory can be recovered from an existing session file", () =>
        {
            const string metadata = "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"W:\\\\TapRelay\"}}";
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-codex-cwd-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "rollout.jsonl");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(path, "{\"type\":\"event_msg\"}\n" + metadata + "\n", new UTF8Encoding(false));
                return CodexSessionMetadata.ReadWorkingDirectoryFromFile(path) == "W:\\TapRelay";
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Codex nested subagent source is sufficient without thread_source", () =>
        {
            const string line = "{\"type\":\"session_meta\",\"payload\":{\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"parent\"}}}}}";
            return CodexSessionMetadata.ClassifyLine(line) == CodexSessionKind.Subagent;
        });

        Test("Codex replayed parent metadata cannot downgrade a subagent session", () =>
        {
            const string childMetadata = "{\"timestamp\":\"2026-08-03T05:38:53.472Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"id\":\"019fc621-d14f-78f0-83f0-c8bf8eed2b98\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\"}}},\"thread_source\":\"subagent\"}}";
            const string replayedParentMetadata = "{\"timestamp\":\"2026-08-03T05:38:53.472Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"id\":\"019fc5ba-9bbc-73c2-845e-cca210909f0a\",\"source\":\"vscode\",\"thread_source\":\"user\"}}";
            var kind = CodexSessionKind.Unknown;
            kind = CodexSessionMetadata.Merge(kind, CodexSessionMetadata.ClassifyLine(childMetadata));
            kind = CodexSessionMetadata.Merge(kind, CodexSessionMetadata.ClassifyLine(replayedParentMetadata));
            return kind == CodexSessionKind.Subagent;
        });

        Test("Explicit false and primary markers remain eligible", () =>
        {
            const string line = "{\"isSidechain\":false,\"is_subagent\":0,\"session\":{\"subagentType\":\"undefined\"},\"agent_type\":\"solo_agent_lite\",\"thread_source\":\"user\"}";
            using var json = System.Text.Json.JsonDocument.Parse(line);
            return !AgentRecordClassifier.IsSubagent(json.RootElement);
        });

        Test("Claude sidechain terminal record is classified as subagent", () =>
        {
            const string line = "{\"type\":\"system\",\"subtype\":\"turn_duration\",\"isSidechain\":true,\"session_id\":\"child\"}";
            using var json = System.Text.Json.JsonDocument.Parse(line);
            return AgentRecordClassifier.IsSubagent(json.RootElement);
        });

        Test("ChatGPT completed event parses from payload", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-01T09:00:00.000Z\",\"payload\":{\"type\":\"task_complete\",\"session_id\":\"codex-session\",\"task_id\":\"codex-completed\",\"cwd\":\"W:\\\\TapRelay\"}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Source == "codex" && parsed.Status == "completed" &&
                   parsed.TaskId == "codex-session:codex-completed" &&
                   parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal) &&
                   parsed.Message.Contains("ChatGPT", StringComparison.Ordinal) &&
                   !parsed.Message.Contains("WorkBuddy", StringComparison.Ordinal);
        });

        Test("ChatGPT project name only accepts explicit metadata", () =>
        {
            const string userLine = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":\"Implement the notification grouping\"}}";
            const string namedLine = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"projectName\":\"TapRelay Mobile\",\"content\":\"Implement the notification grouping\"}}";
            const string completionLine = "{\"payload\":{\"type\":\"task_complete\",\"session_id\":\"session-project\",\"turn_id\":\"turn-project\",\"cwd\":\"W:\\\\TapRelay\"}}";
            return !AgentCompletionEventParser.TryExtractChatGPTProjectName(userLine, out _) &&
                   AgentCompletionEventParser.TryExtractChatGPTProjectName(namedLine, out var projectName) &&
                   projectName == "TapRelay Mobile" &&
                   AgentCompletionEventParser.TryParseChatGPTLine(completionLine, out var parsed) &&
                   parsed.ProjectName == "TapRelay";
        });

        Test("ChatGPT session metadata exposes the selected working folder", () =>
        {
            const string sessionMeta = "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"W:\\\\TapRelay\",\"session_id\":\"session-project\"}}";
            return AgentCompletionEventParser.TryExtractChatGPTWorkingDirectory(sessionMeta) == "W:\\TapRelay";
        });

        Test("ChatGPT turn context exposes the selected working folder", () =>
        {
            const string turnContext = "{\"type\":\"turn_context\",\"payload\":{\"cwd\":\"W:\\\\TapRelay\",\"workspace_roots\":[\"W:\\\\TapRelay\"]}}";
            return AgentCompletionEventParser.TryExtractChatGPTWorkingDirectory(turnContext) == "W:\\TapRelay";
        });

        Test("ChatGPT project resolver uses the configured project name", () =>
        {
            const string state = "{\"local-projects\":{\"project-id\":{\"name\":\"任务通知\",\"rootPaths\":[\"W:\\\\TapRelay\"]}}}";
            return CodexProjectResolver.ResolveFromStateJson(state, "W:\\TapRelay") == "任务通知" &&
                   CodexProjectResolver.ResolveFromStateJson(state, "W:\\TapRelay\\android-app") == "任务通知";
        });

        Test("Agent project folder takes precedence over task title", () =>
            ProjectNameResolver.ResolveFolderFirst(
                "Implement the notification grouping",
                @"W:\\workspace\\TapRelay",
                "agent-task",
                "task details") == "TapRelay");

        Test("Claude last-prompt cd selects the project directory", () =>
            ClaudeProjectResolver.ResolvePromptDirectory("cd W:\\TapRelay") ==
            Path.GetFullPath(@"W:\TapRelay"));

        Test("Claude last-prompt supports a directory with non-ASCII name", () =>
            ClaudeProjectResolver.ResolvePromptDirectory("cd W:\\中转站推荐网页") ==
            Path.GetFullPath(@"W:\中转站推荐网页"));

        Test("Claude PowerShell location command selects the project directory", () =>
            ClaudeProjectResolver.ResolvePromptDirectory(
                "Set-Location -LiteralPath 'W:\\公益网站'") ==
            Path.GetFullPath(@"W:\公益网站"));

        Test("Claude command prefix and trailing task text are ignored", () =>
            ClaudeProjectResolver.ResolvePromptDirectory(
                "! cd W:\\TapRelay 刚刚检查任务状态") ==
            Path.GetFullPath(@"W:\TapRelay"));

        Test("Claude assistant tool command selects the project directory", () =>
        {
            const string line = "{\"type\":\"assistant\",\"cwd\":\"C:\\\\Users\\\\Admin\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"input\":{\"command\":\"cd W:\\\\TapRelay && Get-Location\"}}]}}";
            using var json = System.Text.Json.JsonDocument.Parse(line);
            return ClaudeProjectResolver.ResolveToolUseDirectory(json.RootElement) ==
                   Path.GetFullPath(@"W:\TapRelay");
        });

        Test("Claude user profile cwd does not overwrite a remembered project", () =>
            ClaudeProjectResolver.PreferWorkingDirectory(
                @"W:\TapRelay",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ==
            Path.GetFullPath(@"W:\TapRelay"));

        Test("Claude desktop cwd is accepted as the project directory", () =>
            ClaudeProjectResolver.PreferWorkingDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"W:\PixGo",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ==
            Path.GetFullPath(@"W:\PixGo"));

        Test("Netdisk project name uses the containing folder", () =>
            ProjectNameResolver.ResolveNetdiskFolder(
                @"D:\\Cloud\\FolderA\\archive.zip",
                "archive.zip") == "FolderA");

        Test("Suyan project name is fixed to image generation", () =>
            ProjectNameResolver.NormalizeForSource("suyan", "任意任务标题") == "生图任务");

        Test("ChatGPT failure event stays failed with error detail", () =>
        {
            const string line = "{\"type\":\"turn.failed\",\"sessionId\":\"codex-session\",\"turnId\":\"codex-failed\",\"status\":\"failed\",\"error\":\"quota exceeded\"}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Status == "failed" && parsed.TaskId == "codex-session:codex-failed" &&
                   parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal) &&
                   parsed.Message.Contains("quota exceeded", StringComparison.Ordinal) &&
                   !parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("ChatGPT approval event asks for user action", () =>
        {
            const string line = "{\"payload\":{\"type\":\"exec_approval_request\",\"session_id\":\"codex-session\",\"turn_id\":\"codex-approval\",\"prompt\":\"Allow terminal command?\"}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Status == "waiting" && parsed.TaskId == "codex-session:codex-approval" &&
                   parsed.Message.Contains("\u7b49\u5f85", StringComparison.Ordinal) &&
                   parsed.Message.Contains("Allow terminal command?", StringComparison.Ordinal);
        });

        Test("ChatGPT input-required status asks for user action", () =>
        {
            const string line = "{\"event\":\"turn.status\",\"session_id\":\"codex-session\",\"turn_id\":\"codex-input\",\"status\":\"requires_action\",\"question\":\"Choose a target\"}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Status == "waiting" && parsed.TaskId == "codex-session:codex-input";
        });

        Test("Codex request_user_input function call is waiting with the real question log", () =>
        {
            const string line = "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"id\":\"fc-question\",\"call_id\":\"call-question\",\"name\":\"request_user_input\",\"arguments\":\"{\\\"questions\\\":[{\\\"header\\\":\\\"发布范围\\\",\\\"question\\\":\\\"是否发布？\\\",\\\"options\\\":[{\\\"label\\\":\\\"仅验证\\\",\\\"description\\\":\\\"不覆盖发布包\\\"},{\\\"label\\\":\\\"允许生成\\\",\\\"description\\\":\\\"生成最新发布包\\\"}]}]}\",\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"turn-question\"}}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Source == "codex" && parsed.Status == "waiting" &&
                   parsed.TaskId == "call-question" &&
                   parsed.TurnId == "turn-question" &&
                   parsed.TaskLog.Contains("是否发布？", StringComparison.Ordinal) &&
                   parsed.TaskLog.Contains("允许生成", StringComparison.Ordinal) &&
                   parsed.Message.Contains("是否发布？", StringComparison.Ordinal) &&
                   parsed.CodexInputCallId == "call-question" &&
                   string.IsNullOrWhiteSpace(parsed.RequestId);
        });

        Test("Codex function_call_output exposes the original call id", () =>
        {
            const string line = "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"call-question\",\"output\":\"request_user_input is unavailable in Default mode\"}}";
            return AgentCompletionEventParser.TryReadCodexFunctionCallOutput(line, out var callId) &&
                   callId == "call-question";
        });

        Test("Codex non-function output is not treated as a request result", () =>
        {
            const string line = "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"call_id\":\"call-question\",\"output\":\"Script completed\"}}";
            return !AgentCompletionEventParser.TryReadCodexFunctionCallOutput(line, out _);
        });

        Test("ChatGPT cancellation is ignored", () =>
        {
            const string line = "{\"type\":\"turn.failed\",\"session_id\":\"codex-session\",\"turn_id\":\"codex-cancelled\",\"status\":\"cancelled\"}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("Codex task_started marks a new turn boundary", () =>
        {
            const string start = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"t1\"}}";
            const string complete = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"t1\"}}";
            return AgentCompletionEventParser.IsCodexTurnStartEvent(start) &&
                   !AgentCompletionEventParser.IsCodexTurnStartEvent(complete);
        });

        Test("Codex token_count event sums the last step usage", () =>
        {
            const string line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":99999},\"last_token_usage\":{\"input_tokens\":18345,\"cached_input_tokens\":12672,\"output_tokens\":576,\"reasoning_output_tokens\":352,\"total_tokens\":18921}}}}";
            if (!AgentCompletionEventParser.TryReadCodexTokenCountEvent(line, out var step)) return false;
            return step == 18345L + 12672L + 576L + 352L;
        });

        Test("Codex token_count with null info is still consumed", () =>
        {
            const string line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}";
            return AgentCompletionEventParser.TryReadCodexTokenCountEvent(line, out var step) && step == 0L;
        });

        Test("Codex non-token events are not treated as token steps", () =>
        {
            const string line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"t1\"}}";
            return !AgentCompletionEventParser.TryReadCodexTokenCountEvent(line, out _);
        });

        Test("Codex end-to-end: session file turn token is attached to completion", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-codex-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sessionPath = Path.Combine(directory, "session.jsonl");
            try
            {
                // 真实 codex session 顺序：task_started → token_count(多次增量) → task_complete。
                // 两轮之间 task_started 会清零，避免把上一轮的 token 串到下一轮。
                File.WriteAllLines(sessionPath, new[]
                {
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-1\"}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":500,\"output_tokens\":100,\"reasoning_output_tokens\":50}}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":300,\"output_tokens\":200}}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-1\",\"duration_ms\":9000}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-2\"}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":77,\"output_tokens\":23}}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-2\",\"duration_ms\":1200}}"
                });

                var tokens = new CodexTurnTokenAccumulator();
                var completions = new List<AgentCompletionEventData>();
                foreach (var line in File.ReadLines(sessionPath))
                {
                    if (tokens.TryObserve(line)) continue;
                    if (!AgentCompletionEventParser.TryParseChatGPTLine(line, out var completion)) continue;
                    completion.TokenCount = completion.Status == "waiting" ? 0L : tokens.Take();
                    completions.Add(completion);
                }

                var expectedTurn1 = 1000L + 500L + 100L + 50L + 300L + 200L;
                var expectedTurn2 = 77L + 23L;
                return completions.Count == 2 &&
                       completions[0].Status == "completed" &&
                       completions[0].TaskId == "turn-1" &&
                       completions[0].TokenCount == expectedTurn1 &&
                       completions[0].DurationMs == 9000 &&
                       completions[1].Status == "completed" &&
                       completions[1].TaskId == "turn-2" &&
                       completions[1].TokenCount == expectedTurn2 &&
                       completions[1].DurationMs == 1200;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("WorkBuddy ACP session_end end_turn parses as completed", () =>
        {
            const string line = "[2026/8/4 12:00:00.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"session_end\",\"stopReason\":\"end_turn\",\"_meta\":{\"codebuddy.ai/requestId\":\"workbuddy-completed\"}}}}";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Source == "workbuddy" && parsed.Status == "completed" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-completed" &&
                   parsed.Message.Contains("WorkBuddy", StringComparison.Ordinal);
        });

        Test("WorkBuddy refusal session_end parses as failed", () =>
        {
            const string line = "[2026/8/4 12:00:01.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"session_end\",\"stopReason\":\"refusal\",\"_meta\":{\"codebuddy.ai/requestId\":\"workbuddy-failed\"}}}}";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Status == "failed" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-failed" &&
                   parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal);
        });

        Test("WorkBuddy Prompt completed timestamp separates turns but matches across logs", () =>
        {
            const string turn1 = "[2026/10/12 22:18:33.836] [Info] [AcpView]  Prompt completed for session s-1, stopReason: end_turn, finishReason: stop";
            const string turn2 = "[2026/10/12 22:33:19.418] [Info] [AcpView]  Prompt completed for session s-1, stopReason: end_turn, finishReason: stop";
            const string turn2OtherLog = "[2026/10/12 22:33:19.418] [Info] [AcpView]  Prompt completed for session s-1, stopReason: end_turn, finishReason: stop";
            var ts1 = AgentCompletionEventParser.ReadWorkBuddyLogTimestamp(turn1);
            var ts2 = AgentCompletionEventParser.ReadWorkBuddyLogTimestamp(turn2);
            var ts2Other = AgentCompletionEventParser.ReadWorkBuddyLogTimestamp(turn2OtherLog);
            return ts1 != ts2 && ts2 == ts2Other && ts2 == "2026/10/12 22:33:19.418";
        });

        Test("WorkBuddy cancellation session_end is ignored", () =>
        {
            const string line = "[2026/8/4 12:00:02.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"session_end\",\"stopReason\":\"cancelled\"}}}";
            return !AgentCompletionEventParser.TryParseWorkBuddyLine(line, out _);
        });

        Test("WorkBuddy ACP hasError marker is recognized", () =>
        {
            const string line = "[2026/8/4 12:00:02.500] [Info] [ACP Agent] RESULT: isStream=true, isFinal=false, hasInterruptions=false, hasError=true";
            return AgentCompletionEventParser.IsWorkBuddyFailureMarker(line);
        });

        Test("WorkBuddy current ACP result parses as completed with tracked session", () =>
        {
            const string sessionLine = "[2026/8/4 12:00:02.000] [Info] [CliDispatcher] entry opts: sessionId=workbuddy-session serve=true";
            const string resultLine = "[2026/8/4 12:00:03.040] [Info] [ACP Agent] RESULT: completed successfully, stopReason=end_turn, finishReason=stop";
            var sessionId = AgentCompletionEventParser.ReadWorkBuddyLogSessionId(sessionLine);
            return sessionId == "workbuddy-session" &&
                   AgentCompletionEventParser.TryParseWorkBuddyLine(resultLine, sessionId, out var parsed) &&
                   parsed.Status == "completed" &&
                   parsed.TaskId == "workbuddy-session";
        });

        Test("WorkBuddy current ACP final error parses as failed", () =>
        {
            const string line = "[2026/8/4 12:00:04.000] [Info] [ACP Agent] RESULT: isStream=true, isFinal=true, hasInterruptions=false, hasError=true";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, "workbuddy-session", out var parsed) &&
                   parsed.Status == "failed" &&
                   parsed.TaskId == "workbuddy-session";
        });

        Test("WorkBuddy permission request parses as waiting", () =>
        {
            const string line = "[2026/8/4 12:00:03.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"workbuddy-session\",\"toolCall\":{\"toolCallId\":\"workbuddy-permission\",\"title\":\"Allow command?\"},\"options\":[]}}";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-permission" &&
                   parsed.Message.Contains("Allow command?", StringComparison.Ordinal);
        });

        Test("WorkBuddy question request parses as waiting", () =>
        {
            const string line = "[2026/8/4 12:00:04.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"id\":\"workbuddy-question\",\"method\":\"_codebuddy.ai/question\",\"params\":{\"sessionId\":\"workbuddy-session\",\"questions\":[{\"question\":\"Choose a target\"}]}}";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-question" &&
                   parsed.Message.Contains("Choose a target", StringComparison.Ordinal);
        });

        Test("WorkBuddy ordinary tool_call is not a waiting event", () =>
        {
            const string line = "[2026/8/4 12:00:05.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"Read:36\",\"name\":\"Read\",\"status\":\"pending\"}}}";
            return !AgentCompletionEventParser.TryParseWorkBuddyLine(line, out _);
        });

        Test("WorkBuddy AskUserQuestion tool title parses as waiting", () =>
        {
            const string line = "[2026/8/4 12:00:06.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"workbuddy-ask\",\"title\":\"AskUserQuestion\",\"status\":\"in_progress\"}}}";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-ask";
        });

        Test("WorkBuddy truncated AskUserQuestion tool title parses as waiting", () =>
        {
            const string line = "[2026/8/4 12:00:07.000] [Info] [ACP StreamManager] outgoingWritable.write called, message={\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"workbuddy-session\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"workbuddy-truncated-ask\",\"title\":\"AskU";
            return AgentCompletionEventParser.TryParseWorkBuddyLine(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "workbuddy-session:workbuddy-truncated-ask";
        });

        Test("WorkBuddy awaiting_input transition parses as waiting", () =>
        {
            const string line = "[2026/8/23 10:47:03.737] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-1\",\"signal\":\"awaiting_input\",\"from\":\"working\",\"to\":\"pending\",\"turnSeq\":6}";
            return AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(line, out var parsed) &&
                   parsed.Source == "workbuddy" && parsed.Status == "waiting" &&
                   parsed.SessionId == "wb-session-1" &&
                   parsed.TaskId == "wb-session-1:awaiting:6" &&
                   parsed.Message.Contains("WorkBuddy", StringComparison.Ordinal);
        });

        Test("WorkBuddy awaiting_input without turnSeq still parses as waiting", () =>
        {
            const string line = "[2026/8/23 10:47:03.737] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-2\",\"signal\":\"awaiting_input\",\"from\":\"working\",\"to\":\"pending\"}";
            return AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "wb-session-2:awaiting";
        });

        Test("WorkBuddy new pending-input polling line parses as waiting", () =>
        {
            // 8-23 起新版：deriveExportedPendingInputKind 轮询行（约 30 秒/次）。
            const string line = "[2026/8/23 20:25:29.991] [Info] [SessionManager] deriveExportedPendingInputKind: returning 'permission' {\"sessionId\":\"wb-session-3\",\"fromRequests\":\"permission\",\"fromTaskState\":\"permission\",\"pendingPermissionCount\":1,\"pendingQuestionCount\":0,\"runState\":\"awaiting_input\",\"isProcessing\":true}";
            return AgentCompletionEventParser.TryParseWorkBuddyPendingInput(line, out var parsed) &&
                   parsed.Source == "workbuddy" && parsed.Status == "waiting" &&
                   parsed.SessionId == "wb-session-3" &&
                   parsed.TaskId == "wb-session-3:awaiting:permission" &&
                   parsed.Message.Contains("\u786e\u8ba4", StringComparison.Ordinal);
        });

        Test("WorkBuddy new pending-input question kind parses as waiting", () =>
        {
            const string line = "[2026/8/23 20:26:00.005] [Info] [SessionManager] deriveExportedPendingInputKind: returning 'question' {\"sessionId\":\"wb-session-4\",\"fromRequests\":\"question\",\"fromTaskState\":\"question\",\"pendingPermissionCount\":0,\"pendingQuestionCount\":1,\"runState\":\"awaiting_input\",\"isProcessing\":true}";
            return AgentCompletionEventParser.TryParseWorkBuddyPendingInput(line, out var parsed) &&
                   parsed.Status == "waiting" &&
                   parsed.TaskId == "wb-session-4:awaiting:question";
        });

        Test("WorkBuddy new pending-input non-awaiting line is ignored", () =>
        {
            // runState 不是 awaiting_input（如任务已在工作中）不应误报 waiting。
            const string line = "[2026/8/23 20:30:20.345] [Info] [SessionManager] deriveExportedPendingInputKind: returning 'none' {\"sessionId\":\"wb-session-5\",\"fromRequests\":\"\",\"fromTaskState\":\"\",\"pendingPermissionCount\":0,\"pendingQuestionCount\":0,\"runState\":\"working\",\"isProcessing\":true}";
            return !AgentCompletionEventParser.TryParseWorkBuddyPendingInput(line, out _);
        });

        Test("WorkBuddy new pending-input working_started transition clears dedup target", () =>
        {
            // 恢复信号：task state transition 到 working 应能提取 sessionId（供清除去重标记）。
            const string line = "[2026/8/23 20:29:07.506] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-3\",\"signal\":\"working_started\",\"from\":\"planning\",\"to\":\"working\",\"turnSeq\":3}";
            return AgentCompletionEventParser.ReadWorkBuddyLogSessionId(line) == "wb-session-3";
        });

        Test("WorkBuddy planning/working transitions are not waiting events", () =>
        {
            const string planning = "[2026/8/23 09:08:27.524] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-3\",\"signal\":\"planning_started\",\"from\":\"working\",\"to\":\"planning\",\"turnSeq\":4}";
            const string working = "[2026/8/23 09:08:27.524] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-3\",\"signal\":\"working_started\",\"from\":\"planning\",\"to\":\"working\",\"turnSeq\":4}";
            const string resolved = "[2026/8/23 11:36:25.297] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-3\",\"signal\":\"input_resolved\",\"from\":\"pending\",\"to\":\"working\",\"turnSeq\":6}";
            return !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(planning, out _) &&
                   !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(working, out _) &&
                   !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(resolved, out _);
        });

        Test("WorkBuddy awaiting_input with mismatched from/to is rejected", () =>
        {
            const string line = "[2026/8/23 10:47:03.737] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-4\",\"signal\":\"awaiting_input\",\"from\":\"planning\",\"to\":\"pending\",\"turnSeq\":6}";
            const string noSignal = "[2026/8/23 10:47:03.737] [Info] [SessionManager] task state transition {\"sessionId\":\"wb-session-4\",\"signal\":\"working_started\",\"from\":\"working\",\"to\":\"working\",\"turnSeq\":6}";
            return !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(line, out _) &&
                   !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(noSignal, out _);
        });

        Test("WorkBuddy awaiting_input line without JSON object is rejected", () =>
        {
            const string line = "[2026/8/23 10:47:03.737] [Info] [SessionManager] task state transition (no payload) awaiting_input";
            return !AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(line, out _);
        });

        Test("ChatGPT patch_apply_end step is not a task completion", () =>
        {
            // event_msg sub-operation: the finished patch step carries
            // status:"completed" but the turn is still running. Must not notify.
            const string line = "{\"timestamp\":\"2026-08-01T14:22:15.621Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"patch_apply_end\",\"turn_id\":\"019fbd3a-6421-7450-b6c5-b01e29f49903\",\"status\":\"completed\"}}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("ChatGPT task_started lifecycle event is not a task completion", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-25T03:49:54.450Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-started\"}}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("ChatGPT item_completed lifecycle event is not a task completion", () =>
        {
            const string line = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-item\",\"status\":\"completed\"}}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("ChatGPT turn-ended hook marker is not a task completion", () =>
        {
            const string line = "{\"type\":\"turn-ended\",\"turn_id\":\"turn-hook\"}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("ChatGPT custom_tool_call step is not a task completion", () =>
        {
            // response_item record for a finished exec tool call.
            const string line = "{\"timestamp\":\"2026-08-01T12:10:14.487Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"status\":\"completed\",\"call_id\":\"call_x\",\"name\":\"exec\"}}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("ChatGPT task_complete with error is reported as failed", () =>
        {
            // Codex reuses task_complete for failures, distinguished by `error`.
            const string line = "{\"timestamp\":\"2026-08-01T14:39:19.302Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"019fbd3a-6421-7450-b6c5-b01e29f49903\",\"last_agent_message\":null,\"error\":{\"message\":\"stream disconnected before completion: Upstream service temporarily unavailable\",\"codex_error_info\":\"other\"}}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Source == "codex" && parsed.Status == "failed" &&
                    parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal) &&
                   parsed.Message.Contains("stream disconnected", StringComparison.Ordinal) &&
                    !parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("ChatGPT task_complete without error stays completed", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-01T11:26:48.988Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"019fbce7-5c82-7981-99c3-841b54b8cf0a\",\"last_agent_message\":\"task completed\"}}";
            return AgentCompletionEventParser.TryParseChatGPTLine(line, out var parsed) &&
                   parsed.Status == "completed" &&
                    parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal) &&
                    parsed.Message.Contains("task completed", StringComparison.Ordinal);
        });

        Test("Codex session reader uses the current turn final assistant message", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-codex-message-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sessionPath = Path.Combine(directory, "session.jsonl");
            try
            {
                File.WriteAllLines(sessionPath, new[]
                {
                    "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"用户提示不能作为日志\"}],\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"turn-1\"}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-1\",\"item\":{\"type\":\"CommandExecution\",\"phase\":\"commentary\",\"content\":[{\"type\":\"Text\",\"text\":\"工具输出不能作为日志\"}]}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-1\",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"commentary\",\"content\":[{\"type\":\"Text\",\"text\":\"中间进度\"}]}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"turn_id\":\"turn-1\",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"Text\",\"text\":\"真实 Codex 完成日志\"}]}}}",
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-1\",\"last_agent_message\":null}}"
                });
                return CodexSessionReader.TryReadLastAssistantMessage(sessionPath, "turn-1", out var message) &&
                       message == "真实 Codex 完成日志";
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Codex response_item assistant output is read without user or tool text", () =>
        {
            const string line = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"assistant log\"}],\"internal_chat_message_metadata_passthrough\":{\"turn_id\":\"turn-response\"}}}";
            return CodexSessionReader.TryReadAssistantMessageLine(line, "turn-response", out var message, out var isFinal) &&
                   message == "assistant log" && !isFinal;
        });

        Test("ChatGPT terminal with explicit sidechain marker is ignored", () =>
        {
            const string line = "{\"type\":\"event_msg\",\"is_sidechain\":true,\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"codex-child\"}}";
            return !AgentCompletionEventParser.TryParseChatGPTLine(line, out _);
        });

        Test("Trae completed event parses as completed", () =>
        {
            const string line = "event: code_comp_complete_shown ; params: {\"session_id\":\"trae-session\",\"turn_id\":\"trae-turn\",\"agent_name\":\"builder\",\"is_interrupted\":0}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.Source == "trae" && parsed.Status == "completed" &&
                   parsed.TaskId == "trae-session:trae-turn" &&
                   parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("Trae failure event is failed and never completed", () =>
        {
            const string line = "event: code_comp_fail ; params: {\"session_id\":\"trae-session\",\"message_id\":\"trae-failure\",\"agent_name\":\"builder\",\"error_message\":\"quota exceeded\"}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.Status == "failed" && parsed.TaskId == "trae-session:trae-failure" &&
                   parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal) &&
                   !parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("Trae waiting event asks for user action", () =>
        {
            const string line = "event: code_comp_selection_required ; params: {\"session_id\":\"trae-session\",\"message_id\":\"trae-selection\"}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.Status == "waiting" && parsed.TaskId == "trae-session:trae-selection" &&
                   parsed.Message.Contains("\u5f85\u5904\u7406", StringComparison.Ordinal);
        });

        Test("Trae interrupted completion is ignored", () =>
        {
            const string line = "event: code_comp_complete_shown ; params: {\"session_id\":\"trae-session\",\"turn_id\":\"trae-interrupted\",\"is_interrupted\":1}";
            return !AgentCompletionEventParser.TryParseTraeLine(line, "trae", out _);
        });

        Test("Trae terminal with explicit subagent type is ignored", () =>
        {
            const string line = "event: code_comp_complete_shown ; params: {\"session_id\":\"trae-child\",\"turn_id\":\"child-turn\",\"subagentType\":\"planner\"}";
            return !AgentCompletionEventParser.TryParseTraeLine(line, "trae", out _);
        });

        Test("Trae sendChatMessage metadata distinguishes primary and subagent sessions", () =>
        {
            const string primary = "2026-08-03T09:20:42.132+08:00 [info] [ai-chat][sendChatMessage] sessionId=trae-main, agentRunInfo=undefined, session.subagentType=undefined";
            const string child = "2026-08-03T09:20:43.132+08:00 [info] [ai-chat][sendChatMessage] sessionId=trae-child, agentRunInfo={runId:1}, session.subagentType=planner";
            return AgentRecordClassifier.TryParseTraeSessionLine(primary, out var primaryId, out var primaryIsSubagent) &&
                   primaryId == "trae-main" && !primaryIsSubagent &&
                   AgentRecordClassifier.TryParseTraeSessionLine(child, out var childId, out var childIsSubagent) &&
                   childId == "trae-child" && childIsSubagent;
        });

        Test("Trae run_script_show confirmation popup notifies as waiting", () =>
        {
            const string line = "2026-08-02T21:21:25.089+08:00 [info] [ai-chat][ai-chat][x] event:  run_script_show ; params:  {\"session_id\":\"trae-session\",\"message_id\":\"trae-message\",\"block_id\":\"trae-block\",\"tool_id\":\"trae-tool\",\"auto_run\":0,\"auto_run_mode\":\"alwaysRun\",\"hit_red_list\":\"Set-Content -Path 'w:\\\\demo.json'\",\"block_level\":\"redlist\"}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.Source == "trae" && parsed.Status == "waiting" &&
                   parsed.TaskId == "trae-session:trae-tool" &&
                   parsed.Message.Contains("\u786e\u8ba4", StringComparison.Ordinal) &&
                   parsed.Message.Contains("Set-Content", StringComparison.Ordinal);
        });

        Test("Trae run_script_show with auto_run=1 is ignored", () =>
        {
            const string line = "event: run_script_show ; params: {\"session_id\":\"trae-session\",\"tool_id\":\"trae-tool\",\"auto_run\":1,\"hit_red_list\":\"\"}";
            return !AgentCompletionEventParser.TryParseTraeLine(line, "trae", out _);
        });

        Test("Trae run_script_show without command still notifies", () =>
        {
            const string line = "event: run_script_show ; params: {\"session_id\":\"trae-session\",\"tool_id\":\"trae-tool2\",\"auto_run\":0,\"hit_red_list\":\"\",\"running_command_list\":\"\"}";
            return AgentCompletionEventParser.TryParseTraeLine(line, "trae", out var parsed) &&
                   parsed.Status == "waiting" && parsed.TaskId == "trae-session:trae-tool2" &&
                   parsed.Message.Contains("\u786e\u8ba4", StringComparison.Ordinal);
        });

        Test("Trae run_script_click is not a waiting trigger", () =>
        {
            const string line = "event: run_script_click ; params: {\"session_id\":\"trae-session\",\"tool_id\":\"trae-tool\",\"auto_run\":0}";
            return !AgentCompletionEventParser.TryParseTraeLine(line, "trae", out _);
        });

        Test("TraeWork failed event is failed", () =>
        {
            const string line = "[DoneHandler] Stream done event received {\"sessionId\":\"solo-session\",\"status\":\"failed\",\"agentMessageId\":\"solo-failure\"}";
            return AgentCompletionEventParser.TryParseTraeWorkLine(line, out var parsed) &&
                   parsed.Source == "traework" && parsed.Status == "failed" &&
                   parsed.TaskId == "solo-session:solo-failure" &&
                   parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal) &&
                   !parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("TraeWork waiting event asks for confirmation", () =>
        {
            const string line = "[NotificationPort] Sending waiting notification: {\"sessionId\":\"solo-session\",\"agentMessageId\":\"solo-agent\",\"planItemId\":\"solo-plan\"}";
            return AgentCompletionEventParser.TryParseTraeWorkLine(line, out var parsed) &&
                   parsed.Status == "waiting" && parsed.TaskId == "solo-session:solo-plan" &&
                   parsed.Message.Contains("\u5f85\u5904\u7406", StringComparison.Ordinal);
        });

        Test("TraeWork cancelled event is ignored", () =>
        {
            const string line = "[DoneHandler] Stream done event received {\"sessionId\":\"solo-session\",\"status\":\"canceled\",\"agentMessageId\":\"solo-cancelled\"}";
            return !AgentCompletionEventParser.TryParseTraeWorkLine(line, out _);
        });

        Test("TraeWork aborted/stopped status is treated as user stop (ignored)", () =>
        {
            const string a = "[DoneHandler] Stream done event received {\"sessionId\":\"s\",\"status\":\"aborted\",\"agentMessageId\":\"m1\"}";
            const string b = "[DoneHandler] Stream done event received {\"sessionId\":\"s\",\"status\":\"user_stopped\",\"agentMessageId\":\"m2\"}";
            return !AgentCompletionEventParser.TryParseTraeWorkLine(a, out _) &&
                   !AgentCompletionEventParser.TryParseTraeWorkLine(b, out _);
        });

        Test("TraeWork unknown terminal status is reported as failed (no silent drop)", () =>
        {
            const string line = "[DoneHandler] Stream done event received {\"sessionId\":\"solo-session\",\"status\":\"timeout\",\"agentMessageId\":\"solo-timeout\"}";
            return AgentCompletionEventParser.TryParseTraeWorkLine(line, out var parsed) &&
                   parsed.Status == "failed" && parsed.TaskId == "solo-session:solo-timeout";
        });

        Test("TraeWork missing status is skipped", () =>
        {
            const string line = "[DoneHandler] Stream done event received {\"sessionId\":\"solo-session\",\"agentMessageId\":\"solo-nostatus\"}";
            return !AgentCompletionEventParser.TryParseTraeWorkLine(line, out _);
        });

        Test("TraeWork explicit subagent terminal is ignored", () =>
        {
            const string line = "[DoneHandler] Stream done event received {\"sessionId\":\"solo-child\",\"status\":\"completed\",\"agentMessageId\":\"child-message\",\"isSubagent\":true}";
            return !AgentCompletionEventParser.TryParseTraeWorkLine(line, out _);
        });

        // P078：TraeCode CN（原 Trae CN）3.3.91 真实 renderer.log 只产生 DoneHandler/NotificationPort
        // 新格式（老 code_comp_* 已绝迹），且必须以 source="trae" 而非 "traework" 归档到对应渠道。
        Test("TraeCode real DoneHandler line parses as trae source", () =>
        {
            const string line = "2026-08-21T07:48:43.347+08:00 [info] [ai-chat/v2] [DoneHandler] Stream done event received {\"sessionId\":\"6a878450c4ba1064c7040f36\",\"status\":\"completed\",\"agentMessageId\":\"6a878da0927a4c98a770635d\"}";
            return AgentCompletionEventParser.TryParseTraeWorkLine(line, "trae", out var parsed) &&
                   parsed.Source == "trae" && parsed.Status == "completed" &&
                   parsed.TaskId == "6a878450c4ba1064c7040f36:6a878da0927a4c98a770635d" &&
                   parsed.SessionId == "6a878450c4ba1064c7040f36" &&
                   parsed.EventKey.StartsWith("trae:completed:", StringComparison.Ordinal) &&
                   parsed.Message.Contains("\u5b8c\u6210", StringComparison.Ordinal);
        });

        Test("TraeCode failed DoneHandler line keeps trae source and failed status", () =>
        {
            const string line = "2026-08-21T07:20:00.000+08:00 [info] [ai-chat/v2] [DoneHandler] Stream done event received {\"sessionId\":\"traecode-fail\",\"status\":\"failed\",\"agentMessageId\":\"traecode-fail-msg\"}";
            return AgentCompletionEventParser.TryParseTraeWorkLine(line, "trae", out var parsed) &&
                   parsed.Source == "trae" && parsed.Status == "failed" &&
                   parsed.EventKey.StartsWith("trae:failed:", StringComparison.Ordinal);
        });

        Test("TraeCode duration pairs Stream started with DoneHandler", () =>
        {
            const string start = "2026-08-21T07:28:32.364+08:00 [info] [ai-chat/v2] [StreamDomainService] Stream started {\"sessionId\":\"traecode-duration\",\"userMessageId\":\"temp-user-x\"}";
            const string end = "2026-08-21T07:28:42.364+08:00 [info] [ai-chat/v2] [DoneHandler] Stream done event received {\"sessionId\":\"traecode-duration\",\"status\":\"completed\",\"agentMessageId\":\"msg-1\"}";
            var timing = new AgentTaskTiming();
            timing.ObserveLine("trae", start);
            return AgentCompletionEventParser.TryParseTraeWorkLine(end, "trae", out var parsed) &&
                   timing.Resolve(parsed, end) == 10000L;
        });

        // P080：TraeCode CN 积分明细只认 cn_session_usage_tail 的 action=show；
        // request_success 预取回执不带数值，必须忽略。
        Test("TraeCode usage tail show event parses credits", () =>
        {
            const string line = "2026-08-21T08:00:00.000+08:00 [info] [ai-chat][ai-chat][☕] event:  cn_session_usage_tail ; params:  {\"action\":\"show\",\"agent_message_id\":\"msg-credits-1\",\"usage_summary\":\"172.69\"}";
            return AgentCompletionEventParser.TryParseTraeUsageLine(line, out var agentMessageId, out var usageSummary) &&
                   agentMessageId == "msg-credits-1" && usageSummary == "172.69";
        });

        Test("TraeCode usage tail request_success is ignored", () =>
        {
            const string line = "2026-08-21T08:00:00.000+08:00 [info] [ai-chat][ai-chat][☕] event:  cn_session_usage_tail ; params:  {\"action\":\"request_success\",\"agent_message_id\":\"msg-credits-2\"}";
            return !AgentCompletionEventParser.TryParseTraeUsageLine(line, out _, out _);
        });

        Test("TraeCode usage tail unrelated line is ignored", () =>
        {
            const string line = "2026-08-21T08:00:00.000+08:00 [info] [ai-chat][ai-chat][☕] event:  other_event ; params:  {\"action\":\"show\",\"agent_message_id\":\"msg-x\",\"usage_summary\":\"1\"}";
            return !AgentCompletionEventParser.TryParseTraeUsageLine(line, out _, out _);
        });

        // P082：TraeCode CN 完成/失败/待处理通知正文统一为固定通用文案，引导用户
        // 到电脑端查看详情（Agent 回复正文存云端本地不可得，工具轨迹语义不足以当
        // "总结日志"，用户明确要求不使用）。metadata/plan item/temp-id 解析器与执行摘要
        // 缓存全部移除，不再依赖 parsed_query/工具轨迹拼装正文。

        Test("Doubao Windows saman log is supported", () =>
            AgentLogPaths.IsDoubaoLog(@"C:\Users\alice\AppData\Local\Doubao\User Data\sdk_storage\log\saman_2026.0731.0.log"));
        Test("Doubao Unix-style saman log is supported", () =>
            AgentLogPaths.IsDoubaoLog("/home/alice/.local/share/Doubao/User Data/sdk_storage/log/saman_2026.0731.0.log"));
        Test("Doubao unrelated log path is rejected", () =>
            !AgentLogPaths.IsDoubaoLog(@"C:\Users\alice\AppData\Local\Doubao\logs\saman_2026.0731.0.log"));

        Test("ZCode completed event parses with unique turnId", () =>
        {
            const string line = "{\"timestamp\":\"2026-07-31T09:00:00.000Z\",\"event\":\"turn.completed\",\"sessionId\":\"sess_1\",\"turnId\":\"turn_1\",\"status\":\"completed\",\"context\":{\"workspacePath\":\"/work/demo\"}}";
            return AgentCompletionEventParser.TryParseZCodeCliLine(line, out var parsed) &&
                   parsed.Status == "completed" && parsed.TaskId == "turn_1" &&
                   parsed.Cwd == "/work/demo" && parsed.Source == "zcode";
        });

        Test("ZCode failed event parses", () =>
        {
            const string line = "{\"timestamp\":\"2026-07-31T09:00:01.000Z\",\"event\":\"turn.failed\",\"sessionId\":\"sess_1\",\"turnId\":\"turn_2\",\"status\":\"failed\",\"context\":{\"cwd\":\"/work/demo\"}}";
            return AgentCompletionEventParser.TryParseZCodeCliLine(line, out var parsed) &&
                   parsed.Status == "failed" && parsed.TaskId == "turn_2" &&
                    parsed.Message.Contains("\u5931\u8d25", StringComparison.Ordinal);
        });

        Test("ZCode cancelled event does not masquerade as failure", () =>
        {
            const string line = "{\"timestamp\":\"2026-07-31T09:00:02.000Z\",\"event\":\"turn.failed\",\"sessionId\":\"sess_1\",\"turnId\":\"turn_3\",\"status\":\"cancelled\"}";
            return !AgentCompletionEventParser.TryParseZCodeCliLine(line, out _);
        });

        Test("ZCode fallback taskId includes timestamp", () =>
        {
            const string first = "{\"timestamp\":\"2026-07-31T09:00:03.000Z\",\"event\":\"turn.completed\",\"sessionId\":\"sess_same\",\"status\":\"completed\"}";
            const string second = "{\"timestamp\":\"2026-07-31T09:00:04.000Z\",\"event\":\"turn.completed\",\"sessionId\":\"sess_same\",\"status\":\"completed\"}";
            return AgentCompletionEventParser.TryParseZCodeCliLine(first, out var a) &&
                   AgentCompletionEventParser.TryParseZCodeCliLine(second, out var b) &&
                   a.TaskId != b.TaskId;
        });

        Test("ZCode repoWiki task_complete false positive is rejected", () =>
        {
            const string line = "[host] [repoWiki] Wiki task_complete refresh skipped {\"workspaceKey\":\"W:\\\\demo\",\"taskId\":\"sess_fake\"}";
            return !AgentCompletionEventParser.TryParseZCodeDesktopLine(line, out _);
        });

        Test("ZCode turn.finished synonym parses as completed", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-01T09:00:00.000Z\",\"event\":\"turn.finished\",\"sessionId\":\"sess_x\",\"turnId\":\"turn_fin\",\"status\":\"ok\"}";
            return AgentCompletionEventParser.TryParseZCodeCliLine(line, out var parsed) &&
                   parsed.Status == "completed" && parsed.TaskId == "turn_fin" && parsed.Source == "zcode";
        });

        Test("ZCode sub-operation completed event is ignored", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-01T09:00:00.000Z\",\"event\":\"model.request.completed\",\"sessionId\":\"sess_x\",\"turnId\":\"turn_sub\"}";
            return !AgentCompletionEventParser.TryParseZCodeCliLine(line, out _);
        });

        Test("ZCode turn.started is not a completion", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-01T09:00:00.000Z\",\"event\":\"turn.started\",\"sessionId\":\"sess_x\",\"turnId\":\"turn_st\",\"status\":\"started\"}";
            return !AgentCompletionEventParser.TryParseZCodeCliLine(line, out _);
        });

        Test("ZCode desktop structured completion parses", () =>
        {
            const string line = "[zcode-agent] task_complete workspace=W:\\\\demo taskId=turn_desktop_1";
            return AgentCompletionEventParser.TryParseZCodeDesktopLine(line, out var parsed) &&
                   parsed.Status == "completed" && parsed.TaskId == "turn_desktop_1";
        });

        Test("ZCode CLI explicit subagent turn is ignored", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-03T09:00:00.000Z\",\"event\":\"turn.completed\",\"sessionId\":\"child\",\"turnId\":\"child-turn\",\"context\":{\"agent_type\":\"sub_agent\"}}";
            return !AgentCompletionEventParser.TryParseZCodeCliLine(line, out _);
        });

        Test("ZCode desktop explicit sidechain terminal is ignored", () =>
        {
            const string line = "[zcode-agent] task_complete taskId=child-turn {\"taskId\":\"child-turn\",\"message_type\":\"sidechain\"}";
            return !AgentCompletionEventParser.TryParseZCodeDesktopLine(line, out _);
        });

        Test("ZCode AskUserQuestion started registers pending wait", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-21T04:27:48.330Z\",\"event\":\"tool.call.started\",\"sessionId\":\"sess_w\",\"turnId\":\"turn_w\",\"toolCallId\":\"call_w1\",\"context\":{\"toolName\":\"AskUserQuestion\"}}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(line, out var toolCallId, out var sessionId, out var turnId) ==
                   AgentCompletionEventParser.ZCodeWaitingAction.Register &&
                   toolCallId == "call_w1" && sessionId == "sess_w" && turnId == "turn_w";
        });

        Test("ZCode AskUserQuestion cancelled completes on completed event", () =>
        {
            const string started = "{\"timestamp\":\"2026-08-21T04:27:48.330Z\",\"event\":\"tool.call.started\",\"sessionId\":\"sess_w\",\"turnId\":\"turn_w\",\"toolCallId\":\"call_w2\",\"context\":{\"toolName\":\"AskUserQuestion\"}}";
            const string completed = "{\"timestamp\":\"2026-08-21T04:27:48.340Z\",\"event\":\"tool.call.completed\",\"sessionId\":\"sess_w\",\"turnId\":\"turn_w\",\"toolCallId\":\"call_w2\",\"context\":{\"toolName\":\"AskUserQuestion\"}}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(started, out _, out _, out _) ==
                       AgentCompletionEventParser.ZCodeWaitingAction.Register &&
                   AgentCompletionEventParser.ClassifyZCodeWaiting(completed, out var doneId, out _, out _) ==
                       AgentCompletionEventParser.ZCodeWaitingAction.Cancel &&
                   doneId == "call_w2";
        });

        Test("ZCode yolo AskUserQuestion is ignored (auto-answered)", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-21T04:27:48.328Z\",\"event\":\"tool.permission.resolved\",\"sessionId\":\"sess_y\",\"turnId\":\"turn_y\",\"toolCallId\":\"call_y1\",\"context\":{\"toolName\":\"AskUserQuestion\",\"mode\":\"yolo\",\"decision\":\"modify\"}}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(line, out _, out _, out _) ==
                   AgentCompletionEventParser.ZCodeWaitingAction.Ignore;
        });

        Test("ZCode auto-answered modify decision is ignored", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-21T04:27:48.328Z\",\"event\":\"tool.permission.resolved\",\"sessionId\":\"sess_m\",\"turnId\":\"turn_m\",\"toolCallId\":\"call_m1\",\"context\":{\"toolName\":\"AskUserQuestion\",\"decision\":\"modify\"}}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(line, out _, out _, out _) ==
                   AgentCompletionEventParser.ZCodeWaitingAction.Ignore;
        });

        Test("ZCode non-AskUserQuestion tool call is not a waiting event", () =>
        {
            const string line = "{\"timestamp\":\"2026-08-21T04:27:48.330Z\",\"event\":\"tool.call.started\",\"sessionId\":\"sess_n\",\"turnId\":\"turn_n\",\"toolCallId\":\"call_n1\",\"context\":{\"toolName\":\"Bash\"}}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(line, out _, out _, out _) ==
                   AgentCompletionEventParser.ZCodeWaitingAction.None;
        });

        Test("ZCode non-waiting event is None", () =>
        {
            // tool.call.started for a non-interactive tool (Bash) is not a waiting signal,
            // and a random event name should not be misclassified as Register.
            const string bashStarted = "{\"timestamp\":\"2026-08-21T04:27:48.330Z\",\"event\":\"tool.call.started\",\"sessionId\":\"sess_o\",\"turnId\":\"turn_o\",\"toolCallId\":\"call_o1\",\"context\":{\"toolName\":\"Bash\"}}";
            const string unrelated = "{\"timestamp\":\"2026-08-21T04:27:48.330Z\",\"event\":\"model.request.completed\",\"sessionId\":\"sess_o\",\"turnId\":\"turn_o\"}";
            return AgentCompletionEventParser.ClassifyZCodeWaiting(bashStarted, out _, out _, out _) ==
                       AgentCompletionEventParser.ZCodeWaitingAction.None &&
                   AgentCompletionEventParser.ClassifyZCodeWaiting(unrelated, out _, out _, out _) ==
                       AgentCompletionEventParser.ZCodeWaitingAction.None;
        });

        Test("Doubao completed rlog without rendered assistant text is ignored", () =>
        {
            var oldLocalAppData = Environment.GetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable);
            var emptyRoot = Path.Combine(Path.GetTempPath(), "taprelay-doubao-isolate-" + Guid.NewGuid().ToString("N"));
            var oldOverride = DoubaoTaskContentSource.SessionTitleOverride;
            var oldAssistantOverride = DoubaoTaskContentSource.AssistantMessageOverride;
            try
            {
                // 隔离：不读真实豆包日志/窗口标题，验证无内容时回落固定文案。
                DoubaoTaskContentSource.SessionTitleOverride = string.Empty;
                DoubaoTaskContentSource.AssistantMessageOverride = string.Empty;
                Directory.CreateDirectory(Path.Combine(emptyRoot, "Doubao", "User Data", "sdk_storage", "log"));
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, emptyRoot);
                const string line = "[37852:20872:0731/193657.767:INFO:x] [AhaNetSDK LOG] [rlog] req_id=bdba402d-72ef-4610-8049-88a42acac98b ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"text/event-stream\"}}";
                return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
            }
            finally
            {
                DoubaoTaskContentSource.SessionTitleOverride = oldOverride;
                DoubaoTaskContentSource.AssistantMessageOverride = oldAssistantOverride;
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, oldLocalAppData);
                Directory.Delete(emptyRoot, true);
            }
        });

        Test("Doubao non-cancelled error parses as failed", () =>
        {
            const string line = "[AhaNetSDK LOG] [rlog] req_id=doubao-failed-1 ret=Err(Msg(\"server error\")) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"FAILED\"},\"response\":{\"code\":500}}";
            return AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out var parsed) &&
                   parsed.Source == "doubao" && parsed.Status == "failed" &&
                   parsed.TaskId == "doubao-failed-1" && parsed.Message == "\u8c46\u5305\u4efb\u52a1\u5931\u8d25";
        });

        Test("Doubao cancelled request does not generate failure", () =>
        {
            const string line = "[AhaNetSDK LOG] [rlog] req_id=doubao-cancelled-1 ret=Err(Msg(\"request canceled\")) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"IO_PENDING\"},\"response\":{\"code\":200}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Doubao non-completion network request is ignored", () =>
        {
            const string line = "[AhaNetSDK LOG] [rlog] req_id=doubao-list-1 ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://mcs.doubao.com/list\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Doubao rlog without request id is ignored", () =>
        {
            const string line = "[AhaNetSDK LOG] [rlog] ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Doubao explicit subagent completion is ignored", () =>
        {
            const string line = "[AhaNetSDK LOG] [rlog] req_id=doubao-child ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200},\"metadata\":{\"agent_type\":\"subagent\"}}";
            return !AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out _);
        });

        Test("Claude AskUserQuestion prompt yields tool_use id and question text", () =>
        {
            // Real Haha transcript shape: assistant row, stop_reason=tool_use, AskUserQuestion block.
            const string line = "{\"type\":\"assistant\",\"entrypoint\":\"claude-desktop\",\"version\":\"999.0.0-local\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"AskUserQuestion-1784203639871436273-304427\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"header\":\"API \u91cd\u547d\u540d\",\"question\":\"\u8981\u6539\u540d\u5417\uff1f\",\"multiSelect\":false,\"options\":[]}]}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetAskUserQuestionPrompt(document.RootElement, out var toolUseId, out var question) &&
                   toolUseId == "AskUserQuestion-1784203639871436273-304427" &&
                   question == "\u8981\u6539\u540d\u5417\uff1f";
        });

        Test("Claude AskUserQuestion falls back to header when question is absent", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_14bd9a849a524740a8c90c7b\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"header\":\"\u80fd\u529b\u5224\u65ad\u65b9\u5f0f\",\"options\":[]}]}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetAskUserQuestionPrompt(document.RootElement, out var toolUseId, out var question) &&
                   toolUseId == "call_14bd9a849a524740a8c90c7b" &&
                   question == "\u80fd\u529b\u5224\u65ad\u65b9\u5f0f";
        });

        Test("Claude AskUserQuestion joins multiple questions", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_multi\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"question\":\"A?\"},{\"question\":\"B?\"}]}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetAskUserQuestionPrompt(document.RootElement, out _, out var question) &&
                   question == "A?; B?";
        });

        Test("Other tool_use rows are not treated as AskUserQuestion prompts", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_bash\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.TryGetAskUserQuestionPrompt(document.RootElement, out _, out _);
        });

        Test("Answered AskUserQuestion tool_result is not a new prompt", () =>
        {
            // The reply arrives as a user row carrying tool_result for the same id; it must not
            // re-fire a waiting notification.
            const string line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call_14bd9a849a524740a8c90c7b\",\"content\":\"Your questions have been answered\"}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.TryGetAskUserQuestionPrompt(document.RootElement, out _, out _);
        });

        Test("Haha pending Bash tool_use is a permission waiting prompt", () =>
        {
            // Real shape from 输入法-gpt (2026-08-11): pending Bash gradle command, stop_reason=tool_use.
            const string line = "{\"type\":\"assistant\",\"entrypoint\":\"claude-desktop\",\"version\":\"999.0.0-local\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_00_USPMxpRA28cY7BpjwNGF8789\",\"name\":\"Bash\",\"input\":{\"command\":\"cd \\\"W:/输入法-gpt\\\" && ./gradlew.bat :app:assembleDebug\"}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                       document.RootElement,
                       out var toolUseId,
                       out var toolName,
                       out var command) &&
                   toolUseId == "call_00_USPMxpRA28cY7BpjwNGF8789" &&
                   toolName == "Bash" &&
                   command.Contains("./gradlew.bat");
        });

        Test("Haha Write tool_use is a permission waiting prompt", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_write_1\",\"name\":\"Write\",\"input\":{\"file_path\":\"C:\\\\x\\\\a.cs\"}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                       document.RootElement,
                       out var toolUseId,
                       out var toolName,
                       out var command) &&
                   toolUseId == "call_write_1" &&
                   toolName == "Write" &&
                   command.Contains("a.cs");
        });

        Test("Read-only tool rows are not permission waiting prompts", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_read_1\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.TryGetPendingPermissionPrompt(document.RootElement, out _, out _, out _);
        });

        Test("AskUserQuestion is not routed through permission waiting classifier", () =>
        {
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_ask_1\",\"name\":\"AskUserQuestion\",\"input\":{}}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.TryGetPendingPermissionPrompt(document.RootElement, out _, out _, out _);
        });

        Test("Answered tool results are collected from user rows", () =>
        {
            const string userLine = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call_b\",\"content\":\"ok\"}]}}";
            using var userDoc = JsonDocument.Parse(userLine);
            var answered = AgentCompletionEventParser.CollectToolResultIds(userDoc.RootElement);
            return answered.Contains("call_b");
        });

        Test("bypassPermissions is treated as an auto-allow permission mode", () =>
            AgentCompletionEventParser.IsPermissionAutoAllowMode("bypassPermissions") &&
            AgentCompletionEventParser.IsPermissionAutoAllowMode("dont-ask") &&
            AgentCompletionEventParser.IsPermissionAutoAllowMode("yolo") &&
            !AgentCompletionEventParser.IsPermissionAutoAllowMode("default") &&
            !AgentCompletionEventParser.IsPermissionAutoAllowMode("plan"));

        Test("session-meta permissionMode is read from Haha transcript rows", () =>
        {
            const string line = "{\"type\":\"session-meta\",\"isMeta\":true,\"workDir\":\"W:\\\\输入法-gpt\",\"permissionMode\":\"bypassPermissions\"}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryReadPermissionMode(document.RootElement, out var mode) &&
                   mode == "bypassPermissions";
        });

        Test("user row permissionMode is read from Haha transcript rows", () =>
        {
            const string line = "{\"type\":\"user\",\"permissionMode\":\"bypassPermissions\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"继续\"}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryReadPermissionMode(document.RootElement, out var mode) &&
                   mode == "bypassPermissions";
        });

        Test("Haha Gradle Bash timeout is read as 600000ms", () =>
        {
            // Real false-positive from 输入法-gpt 2026-08-15 22:30: compileDebugKotlin
            // ran 3m11s; the 90s permission heuristic fired while Gradle was still running.
            const string line = "{\"type\":\"assistant\",\"message\":{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_b8815305196541caaee7674c\",\"name\":\"Bash\",\"input\":{\"command\":\"cd C:/imegpt-workspace && ./gradlew.bat --stop >/dev/null 2>&1; ./gradlew.bat --no-daemon :keyboard-ui:compileDebugKotlin\",\"timeout\":600000}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.ReadToolUseTimeoutMs(document.RootElement) == 600000L &&
                   AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                       document.RootElement,
                       out var toolUseId,
                       out var toolName,
                       out var command) &&
                   toolUseId == "call_b8815305196541caaee7674c" &&
                   toolName == "Bash" &&
                   command.Contains("compileDebugKotlin");
        });

        Test("Haha Background command summary is a background subtask", () =>
        {
            // Real false-positive from 2026-08-10 14:47: Background production build finished
            // while the parent interactive turn was still waiting on audit agents.
            return AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Background command \"Baseline production build (renderer + electron)\" completed (exit code 0)") &&
                   AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Background command \"Wait for device reconnection\" failed with exit code 1");
        });

        Test("Haha Agent subagent summary is a background subtask", () =>
        {
            return AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Agent \"Audit LibraryView UI/UX\" completed") &&
                   AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Agent \"Audit settings dialogs UI/UX\" completed");
        });

        Test("Haha interactive completion summary is not a background subtask", () =>
        {
            return !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Full baseline green (build exit 0). Now waiting on the 3 audit agents.") &&
                   !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "已修复并装到真机验证。") &&
                   !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(string.Empty) &&
                   !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary("Agent without quotes is interactive text");
        });

        Test("Haha plain-description subtask summaries are not matched by prefix classifier", () =>
        {
            // Real false-positives from 2026-08-10: subagent summaries with no Background/Agent
            // prefix (no outputFile in the event) slipped through the old prefix-only filter as
            // "task completed". Production now discards ALL cc-haha-task-notification events
            // (main task uses end_turn), so these shapes are no longer broadcast. This documents
            // that the prefix classifier alone cannot recognize them — the discard is the fix.
            return !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "探索 PC 端 UI 布局细节") &&
                   !AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Map backend AI services & IPC") &&
                   AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary(
                       "Background command \"Trace stdout raw data\" completed (exit code 0)");
        });

        Test("Claude human user prompt is recognized for turn timing", () =>
        {
            const string line = "{\"type\":\"user\",\"timestamp\":\"2026-08-10T07:14:14.006Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"fix duration\"},{\"type\":\"image\"}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement) &&
                   AgentCompletionEventParser.TryReadEventTimestamp(document.RootElement, out var startedAt) &&
                   startedAt.UtcDateTime.Year == 2026;
        });

        Test("Claude tool_result user row is not a human prompt", () =>
        {
            const string line = "{\"type\":\"user\",\"timestamp\":\"2026-08-10T07:14:48.684Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call_x\",\"content\":\"ok\"}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement);
        });

        // P092: ExitPlanMode 是「请审批这份方案」，阻塞在用户身上；实测真实等待
        // 3.7s / 4.0s / 20.2s。此前它不在权限敏感集合里，完全没有 waiting 资格，
        // 用户离开电脑时永远收不到通知。EnterPlanMode 实测恒为 0.0s，不得纳入。
        Test("ExitPlanMode is treated as a blocking approval tool", () =>
            AgentCompletionEventParser.IsPermissionSensitiveToolName("ExitPlanMode"));

        Test("EnterPlanMode is not treated as blocking (auto-confirmed, 0s)", () =>
            !AgentCompletionEventParser.IsPermissionSensitiveToolName("EnterPlanMode"));

        Test("ExitPlanMode pending approval is extracted from a Haha assistant row", () =>
        {
            const string line = "{\"type\":\"assistant\",\"entrypoint\":\"claude-desktop\",\"timestamp\":\"2026-08-02T08:13:26.000Z\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"79f2b25f-plan\",\"name\":\"ExitPlanMode\",\"input\":{\"plan\":\"迁移鉴权中间件\"}}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                       document.RootElement,
                       out var toolUseId,
                       out var toolName,
                       out _) &&
                   toolUseId == "79f2b25f-plan" &&
                   toolName == "ExitPlanMode";
        });

        Test("EnterPlanMode row yields no pending approval", () =>
        {
            const string line = "{\"type\":\"assistant\",\"entrypoint\":\"claude-desktop\",\"timestamp\":\"2026-08-02T08:11:06.000Z\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"c3900c1b-enter\",\"name\":\"EnterPlanMode\",\"input\":{}}]}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                document.RootElement, out _, out _, out _);
        });

        // P091: Haha 把后台 Bash/子代理完成报告以 type=user 行注入同一转录，正文是
        // <task-notification> XML。这类行既不能当轮次起点，其后的 end_turn 答复也不是
        // 用户主任务终态——实测一次真人提问被推成 5 条完成通知。
        Test("Haha task-notification injection is not a human prompt", () =>
        {
            const string line = "{\"type\":\"user\",\"userType\":\"external\",\"timestamp\":\"2026-09-01T04:47:15.000Z\",\"permissionMode\":\"auto\",\"message\":{\"role\":\"user\",\"content\":\"<task-notification>\\n<task-id>bwyuv67da</task-id>\\n<status>completed</status>\\n<summary>Background command \\\"Launch Electron app\\\" completed (exit code 0)</summary>\\n</task-notification>\"}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.IsHahaBackgroundTaskNotificationPrompt(document.RootElement) &&
                   !AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement);
        });

        Test("Haha task-notification injection is detected in array content form", () =>
        {
            const string line = "{\"type\":\"user\",\"timestamp\":\"2026-09-01T04:48:16.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<task-notification>\\n<task-id>b8tjnutep</task-id>\\n<status>completed</status>\\n</task-notification>\"}]}}";
            using var document = JsonDocument.Parse(line);
            return AgentCompletionEventParser.IsHahaBackgroundTaskNotificationPrompt(document.RootElement) &&
                   !AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement);
        });

        Test("Real human prompt is still a human prompt, not an injection", () =>
        {
            const string line = "{\"type\":\"user\",\"timestamp\":\"2026-09-01T04:51:34.000Z\",\"message\":{\"role\":\"user\",\"content\":\"重新打包\"}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.IsHahaBackgroundTaskNotificationPrompt(document.RootElement) &&
                   AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement);
        });

        Test("A prompt merely mentioning task-notification in prose is still human", () =>
        {
            // 只有真正的注入格式才被排除；用户讨论这个标签名不应被误判。
            const string line = "{\"type\":\"user\",\"timestamp\":\"2026-09-01T05:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"请解释 task-notification 注入是怎么回事\"}}";
            using var document = JsonDocument.Parse(line);
            return !AgentCompletionEventParser.IsHahaBackgroundTaskNotificationPrompt(document.RootElement) &&
                   AgentCompletionEventParser.IsClaudeHumanUserPrompt(document.RootElement);
        });

        // P093：手机端「允许 / 拒绝」要能回写到 Haha sidecar，靠的是通知里带的
        // requestId 能唯一定位「哪个会话的哪个权限请求」。这个编解码是手机与
        // PC 之间的线上契约，格式错了按钮会静默失效，因此单独回归。
        Test("P093 haha approval requestId round-trips session and request id", () =>
        {
            const string sessionId = "ef8c2d32-1f4b-4a90-9c1e-77d0b3a5e612";
            const string permissionRequestId = "perm_01JQ9F";
            var requestId = ClaudeHahaApprovalRequestId.Build(sessionId, permissionRequestId);
            return ClaudeHahaApprovalRequestId.Owns(requestId) &&
                   ClaudeHahaApprovalRequestId.TryParse(requestId, out var parsedSession, out var parsedRequest) &&
                   parsedSession == sessionId &&
                   parsedRequest == permissionRequestId;
        });

        Test("P093 haha approval requestId keeps colons inside the sidecar request id", () =>
        {
            // sessionId 是 UUID（无冒号），但 sidecar 的 requestId 允许带冒号，
            // 所以只能在第一个冒号处切分，不能用 Split。
            const string sessionId = "2a1eb64e-b21e-429d-95fd-c9cc4d2b52bf";
            const string permissionRequestId = "tool:Bash:42";
            var requestId = ClaudeHahaApprovalRequestId.Build(sessionId, permissionRequestId);
            return ClaudeHahaApprovalRequestId.TryParse(requestId, out var parsedSession, out var parsedRequest) &&
                   parsedSession == sessionId &&
                   parsedRequest == permissionRequestId;
        });

        Test("P093 haha approval bridge ignores foreign and malformed requestIds", () =>
        {
            // workbuddy / HTTP /approval 的 requestId 必须继续走各自分支，
            // 缺少任一段的畸形值不能被当成可路由请求。
            var foreign = new[]
            {
                "workbuddy:2a1eb64e-b21e-429d-95fd-c9cc4d2b52bf",
                "b3f1c0d2e4a5",
                string.Empty,
                "claude-haha:",
                "claude-haha:only-session",
                "claude-haha::missing-session"
            };

            foreach (var requestId in foreign)
            {
                if (ClaudeHahaApprovalRequestId.TryParse(requestId, out _, out _))
                {
                    return false;
                }
            }

            return !ClaudeHahaApprovalRequestId.Owns("workbuddy:abc") &&
                   !ClaudeHahaApprovalRequestId.Owns(null);
        });

        Test("Haha interactive duration is elapsed from human prompt to end_turn", () =>
        {
            // Real 15:28 sample: user "重新构建" 07:27:58.422Z → end_turn 07:28:24.096Z ≈ 26s.
            var startedAt = DateTimeOffset.Parse("2026-08-10T07:27:58.422Z", System.Globalization.CultureInfo.InvariantCulture);
            var endedAt = DateTimeOffset.Parse("2026-08-10T07:28:24.096Z", System.Globalization.CultureInfo.InvariantCulture);
            var elapsed = AgentCompletionEventParser.ComputeElapsedDurationMs(startedAt, endedAt);
            return elapsed == 25_674L &&
                   AgentCompletionEventParser.ComputeElapsedDurationMs(endedAt, startedAt) == 0L &&
                   // same start, later intermediate end_turn still reports growing elapsed (must not zero out)
                   AgentCompletionEventParser.ComputeElapsedDurationMs(
                       startedAt,
                       DateTimeOffset.Parse("2026-08-10T07:30:52.068Z", System.Globalization.CultureInfo.InvariantCulture)) == 173_646L;
        });

        Test("Environment overrides isolate per-user monitor roots", () =>
        {
            var oldHome = Environment.GetEnvironmentVariable(AgentLogPaths.UserHomeOverrideVariable);
            var oldAppData = Environment.GetEnvironmentVariable(AgentLogPaths.AppDataOverrideVariable);
            var oldLocalAppData = Environment.GetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable);
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "taprelay-monitor-test");
                Environment.SetEnvironmentVariable(AgentLogPaths.UserHomeOverrideVariable, Path.Combine(temp, "user"));
                Environment.SetEnvironmentVariable(AgentLogPaths.AppDataOverrideVariable, Path.Combine(temp, "appdata"));
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, Path.Combine(temp, "localappdata"));
                var roots = AgentLogPaths.GetWatchDirectories();
                return roots.Count == 11 &&
                       roots[3].StartsWith(Path.Combine(temp, "user"), StringComparison.OrdinalIgnoreCase) &&
                       roots[0].StartsWith(Path.Combine(temp, "appdata"), StringComparison.OrdinalIgnoreCase) &&
                       roots[2].EndsWith(Path.Combine("appdata", "TraeWork CN", "logs"), StringComparison.OrdinalIgnoreCase) &&
                       roots[8].StartsWith(Path.Combine(temp, "localappdata"), StringComparison.OrdinalIgnoreCase) &&
                       roots[9].EndsWith(Path.Combine("user", ".workbuddy", "logs"), StringComparison.OrdinalIgnoreCase) &&
                       roots[10].EndsWith(Path.Combine("user", ".workbuddy-ai", "logs"), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentLogPaths.UserHomeOverrideVariable, oldHome);
                Environment.SetEnvironmentVariable(AgentLogPaths.AppDataOverrideVariable, oldAppData);
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, oldLocalAppData);
            }
        });

        Test("Doubao task content reads last omnibox input from newest saman log", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-doubao-x-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "saman_2026.0810.0.log");
                var lines = new[]
                {
                    "[saman_omnibox] QueryAutocomplete input: \u6444\u5f71\u62cd\u767d\u5e95\u56fe\u8981\u600e\u4e48\u7528",
                    "[saman_omnibox] QueryAutocomplete input: \u6444\u5f71\u62cd\u767d\u5e95\u56fe\u8981\u600e\u4e48\u7528\u4e09\u8272\u5361",
                    "[saman_omnibox] QueryAutocomplete input: \u6444\u5f71\u62cd\u767d\u5e95\u56fe\u8981\u600e\u4e48\u7528\u4e09\u8272\u5361,"
                };
                // third line ends with ',' which our regex treats as terminator → falls back to second input.
                File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                return DoubaoTaskContentSource.TryReadLatestInput(directory) == "\u6444\u5f71\u62cd\u767d\u5e95\u56fe\u8981\u600e\u4e48\u7528\u4e09\u8272\u5361";
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Doubao task content ignores non-saman files", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-doubao-y-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "netmain-0-2026-8-10.alaudalog"), "QueryAutocomplete input: hello", new UTF8Encoding(false));
                return DoubaoTaskContentSource.TryReadLatestInput(directory) == string.Empty;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Doubao completed message carries the rendered assistant summary", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-doubao-z-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "Doubao", "User Data", "sdk_storage", "log"));
            var oldLocalAppData = Environment.GetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable);
            var oldSessionTitle = DoubaoTaskContentSource.SessionTitleOverride;
            var oldAssistantMessage = DoubaoTaskContentSource.AssistantMessageOverride;
            var oldActiveConversationId = DoubaoTaskContentSource.ActiveConversationIdOverride;
            try
            {
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, directory);
                DoubaoTaskContentSource.SessionTitleOverride = "箱庭都市攻略";
                DoubaoTaskContentSource.AssistantMessageOverride = "箱庭都市物语（箱庭都市）完整攻略：开罗模拟经营核心机制与前期开局建议";
                DoubaoTaskContentSource.ActiveConversationIdOverride = "conversation_doubao_test";
                var logFile = Path.Combine(directory, "Doubao", "User Data", "sdk_storage", "log", "saman_2026.0810.0.log");
                File.WriteAllText(
                    logFile,
                    "[saman_omnibox] QueryAutocomplete input: \u6444\u5f71\u9ed1\u5361\u662f\u6307\u4ec0\u4e48\n",
                    new UTF8Encoding(false));

                const string line = "[AhaNetSDK LOG] [rlog] req_id=doubao-content-1 ret=Ok(200) request_log={\"base\":{\"origin_url\":\"https://www.doubao.com/chat/completion\",\"status\":\"SUCCESS\"},\"response\":{\"code\":200,\"response_content_type\":\"text/event-stream\"}}";
                return AgentCompletionEventParser.TryParseDoubaoRlogLine(line, out var parsed) &&
                       parsed.Source == "doubao" && parsed.Status == "completed" &&
                       parsed.Message.StartsWith("箱庭都市物语（箱庭都市）完整攻略", StringComparison.Ordinal) &&
                       parsed.Message.IndexOf("摄影黑卡", StringComparison.Ordinal) < 0 &&
                       parsed.ProjectName == "箱庭都市攻略" &&
                       parsed.TaskLog.StartsWith("箱庭都市物语（箱庭都市）完整攻略", StringComparison.Ordinal) &&
                       parsed.SessionId == "conversation_doubao_test";
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentLogPaths.LocalAppDataOverrideVariable, oldLocalAppData);
                DoubaoTaskContentSource.SessionTitleOverride = oldSessionTitle;
                DoubaoTaskContentSource.AssistantMessageOverride = oldAssistantMessage;
                DoubaoTaskContentSource.ActiveConversationIdOverride = oldActiveConversationId;
                Directory.Delete(directory, true);
            }
        });

        Test("Doubao suggestion list subtree is excluded from assistant rows", () =>
        {
            return DoubaoTaskContentSource.IsSuggestionSubtree("suggest-list-item item-active") &&
                   DoubaoTaskContentSource.IsSuggestionSubtree("SUGGEST-LIST-ITEM") &&
                   !DoubaoTaskContentSource.IsSuggestionSubtree("v_list_row assistant");
        });

        Test("Doubao IndexedDB assistant text is correlated and bounded", () =>
        {
            var values = new[]
            {
                BuildDoubaoStructuredCloneMessage(
                    userType: 2,
                    text: "当前用户问题：豆包测试输入",
                    isFinished: true)
                    .Concat(BuildDoubaoStructuredCloneMessage(
                        userType: 4,
                        text: new string('真', 5000),
                        isFinished: true))
                    .ToArray()
            };
            var taskLog = DoubaoIndexedDbMessageReader.TryReadAssistantMessageFromRawValues(
                values,
                "豆包测试输入");
            return taskLog.Length <= 4000 &&
                   taskLog.StartsWith("真真真", StringComparison.Ordinal) &&
                   taskLog.IndexOf("豆包测试输入", StringComparison.Ordinal) < 0;
        });

        Test("Doubao completed notification summary stays within protocol limit", () =>
        {
            var summary = DoubaoTaskContentSource.BuildCompletedMessage(new string('结', 4000));
            return summary.Length <= 500 && summary.EndsWith("...", StringComparison.Ordinal);
        });

        Test("OpenCode ReadTerminalSince filters out subagent sessions", () =>
        {
            // 回归 2026-08-16 误报：子代理会话（parent_id 非空）的 stop 消息被
            // 当成主任务完成广播（10:57:44、10:59:51）。修复后的 SQL 必须排除
            // 子会话消息，且子会话的 time_updated 不得推进基线。
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-opencode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "opencode.db");
            try
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE session (
                                id TEXT PRIMARY KEY,
                                parent_id TEXT,
                                directory TEXT NOT NULL,
                                title TEXT NOT NULL,
                                time_created INTEGER NOT NULL,
                                time_updated INTEGER NOT NULL);
                            CREATE TABLE message (
                                id TEXT PRIMARY KEY,
                                session_id TEXT NOT NULL,
                                time_created INTEGER NOT NULL,
                                time_updated INTEGER NOT NULL,
                                data TEXT NOT NULL);
                            CREATE TABLE part (
                                id TEXT PRIMARY KEY,
                                message_id TEXT NOT NULL,
                                time_updated INTEGER NOT NULL,
                                data TEXT NOT NULL);";
                        command.ExecuteNonQuery();
                    }

                    // 主会话（parent_id 为空）stop 消息 + 子会话（parent_id 非空）stop 消息。
                    // 子会话消息的 time_updated(8000) 大于主会话(5000)，若被过滤不彻底，
                    // observedMax 会被推进到 8000，吞掉后续真实完成事件。
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO session (id, parent_id, directory, title, time_created, time_updated) VALUES ('main', NULL, 'W:/demo', 'Main task', 1000, 6000);" +
                            "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-main', 'main', 2000, 5000, '{\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":2000,\"completed\":5000}}');" +
                            "INSERT INTO part (id, message_id, time_updated, data) VALUES ('part-main', 'msg-main', 4999, '{\"type\":\"text\",\"text\":\"主会话真实回复\"}');" +
                            "INSERT INTO session (id, parent_id, directory, title, time_created, time_updated) VALUES ('child', 'main', 'W:/demo', 'Child task (@general subagent)', 3000, 9000);" +
                            "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-child', 'child', 4000, 8000, '{\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":4000,\"completed\":8000}}');" +
                            "INSERT INTO part (id, message_id, time_updated, data) VALUES ('part-child', 'msg-child', 7999, '{\"type\":\"text\",\"text\":\"子会话回复\"}');";
                        command.ExecuteNonQuery();
                    }
                }

                var (events, observedMax) = OpenCodeDatabaseReader.ReadTerminalSince(dbPath, 0);
                return events.Count == 1 &&
                       events[0].MessageId == "msg-main" &&
                       events[0].Status == "completed" &&
                       events[0].Message == "主会话真实回复" &&
                       events[0].ProjectName.Length == 0 &&
                       observedMax == 5000;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("OpenCode ReadTerminalSince honors the after watermark", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-opencode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "opencode.db");
            try
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE session (
                                id TEXT PRIMARY KEY,
                                parent_id TEXT,
                                directory TEXT NOT NULL,
                                title TEXT NOT NULL,
                                time_created INTEGER NOT NULL,
                                time_updated INTEGER NOT NULL);
                            CREATE TABLE message (
                                id TEXT PRIMARY KEY,
                                session_id TEXT NOT NULL,
                                time_created INTEGER NOT NULL,
                                time_updated INTEGER NOT NULL,
                                data TEXT NOT NULL);
                            CREATE TABLE part (
                                id TEXT PRIMARY KEY,
                                message_id TEXT NOT NULL,
                                time_updated INTEGER NOT NULL,
                                data TEXT NOT NULL);";
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO session (id, parent_id, directory, title, time_created, time_updated) VALUES ('main', NULL, 'W:/demo', 'Main task', 1000, 6000);" +
                            "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-a', 'main', 2000, 3000, '{\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":2000,\"completed\":3000}}');" +
                            "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-b', 'main', 3500, 4500, '{\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":3500,\"completed\":4500}}');" +
                            "INSERT INTO part (id, message_id, time_updated, data) VALUES ('part-a', 'msg-a', 2999, '{\"type\":\"text\",\"text\":\"旧回复\"}');" +
                            "INSERT INTO part (id, message_id, time_updated, data) VALUES ('part-b', 'msg-b', 4499, '{\"type\":\"text\",\"text\":\"新回复\"}');";
                        command.ExecuteNonQuery();
                    }
                }

                // after=3000：只应返回 msg-b；基线推进到 4500。
                var (events, observedMax) = OpenCodeDatabaseReader.ReadTerminalSince(dbPath, 3000);
                return events.Count == 1 &&
                       events[0].MessageId == "msg-b" &&
                       events[0].Status == "completed" &&
                       observedMax == 4500 &&
                       OpenCodeDatabaseReader.ReadTerminalSince(dbPath, 4500).Events.Count == 0;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Freebuff reader parses thread rows and max updated watermark", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-freebuff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "desktop-v2.db");
            try
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE threads (
                                id TEXT PRIMARY KEY,
                                project_id TEXT, project_path TEXT, title TEXT, status TEXT,
                                turn_state TEXT NOT NULL DEFAULT 'idle',
                                last_turn_outcome TEXT, attention_reason TEXT, attention_revision INTEGER NOT NULL DEFAULT 0,
                                attention_acknowledged_revision INTEGER NOT NULL DEFAULT 0,
                                last_prompt_at INTEGER, last_turn_finished_at INTEGER, updated_at INTEGER,
                                harness_state TEXT);";
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO threads (id, project_id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state) " +
                            "VALUES ('t1', 'p1', 'W:/TapRelay', '测试任务', 'open', 'idle', 'completed', 'finished', 1, 1000, 9000, 9000, NULL);" +
                            "INSERT INTO threads (id, project_id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state) " +
                            "VALUES ('t2', 'p1', 'W:/TapRelay', 'New thread', 'open', 'idle', 'error', 'failed', 1, 2000, 7000, 7000, '{\"output\":{\"type\":\"error\",\"message\":\"API 欠费\"}}');";
                        command.ExecuteNonQuery();
                    }
                }

                var (rows, observedMax) = FreebuffDatabaseReader.ReadThreadsWithMax(dbPath);
                return rows.Count == 2 &&
                       rows[0].Id == "t1" &&
                       rows[0].LastTurnOutcome == "completed" &&
                       rows[1].LastTurnOutcome == "error" &&
                       observedMax == 9000;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Freebuff reader extracts last-turn token usage from messages", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-freebuff-tokens-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "desktop-v2.db");
            try
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE threads (
                                id TEXT PRIMARY KEY,
                                project_id TEXT, project_path TEXT, title TEXT, status TEXT,
                                turn_state TEXT NOT NULL DEFAULT 'idle',
                                last_turn_outcome TEXT, attention_reason TEXT, attention_revision INTEGER NOT NULL DEFAULT 0,
                                attention_acknowledged_revision INTEGER NOT NULL DEFAULT 0,
                                last_prompt_at INTEGER, last_turn_finished_at INTEGER, updated_at INTEGER,
                                harness_state TEXT);
                            CREATE TABLE messages (
                                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                                thread_id TEXT NOT NULL,
                                request_id TEXT, input_id TEXT, role TEXT,
                                parts_json TEXT NOT NULL DEFAULT '[]',
                                attachments_json TEXT NOT NULL DEFAULT '[]',
                                metrics_json TEXT NOT NULL DEFAULT '{}',
                                ts INTEGER);";
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO threads (id, project_id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state) " +
                            "VALUES ('t1', 'p1', 'W:/TapRelay', '任务', 'open', 'idle', 'completed', 'finished', 1, 1000, 9000, 9000, NULL);" +
                            "INSERT INTO threads (id, project_id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state) " +
                            "VALUES ('t2', 'p1', 'W:/TapRelay', '无消息', 'open', 'idle', 'completed', 'finished', 1, 1000, 9000, 9000, NULL);" +
                            "INSERT INTO messages (thread_id, role, metrics_json, ts) VALUES ('t1', 'user', '{}', 1000);" +
                            "INSERT INTO messages (thread_id, role, metrics_json, ts) VALUES ('t1', 'assistant', '{\"usage\":{\"inputTokens\":500,\"outputTokens\":120,\"totalTokens\":620}}', 9000);" +
                            "INSERT INTO messages (thread_id, role, metrics_json, ts) VALUES ('t1', 'assistant', '{\"usage\":{\"inputTokens\":700,\"outputTokens\":300,\"totalTokens\":1000}}', 9500);" +
                            "UPDATE messages SET parts_json='[{\"kind\":\"text\",\"text\":\"数据库执行总结\"}]' WHERE thread_id='t1' AND role='assistant' AND ts=9500;";
                        command.ExecuteNonQuery();
                    }
                }

                var rows = FreebuffDatabaseReader.ReadThreads(dbPath);
                var t1 = rows.FirstOrDefault(r => r.Id == "t1");
                var t2 = rows.FirstOrDefault(r => r.Id == "t2");
                // 取最后一个 assistant 消息的 totalTokens，而非更早的 620；无消息线程退化为 0。
                return t1 != null && t1.TokenCount == 1000 &&
                       t1.AssistantPartsJson.Contains("数据库执行总结", StringComparison.Ordinal) &&
                       t2 != null && t2.TokenCount == 0;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Freebuff reader tolerates missing messages table", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "taprelay-freebuff-nomsg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "desktop-v2.db");
            try
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE threads (
                                id TEXT PRIMARY KEY,
                                project_id TEXT, project_path TEXT, title TEXT, status TEXT,
                                turn_state TEXT NOT NULL DEFAULT 'idle',
                                last_turn_outcome TEXT, attention_reason TEXT, attention_revision INTEGER NOT NULL DEFAULT 0,
                                attention_acknowledged_revision INTEGER NOT NULL DEFAULT 0,
                                last_prompt_at INTEGER, last_turn_finished_at INTEGER, updated_at INTEGER,
                                harness_state TEXT);";
                        command.ExecuteNonQuery();
                    }
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "INSERT INTO threads (id, project_id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state) " +
                            "VALUES ('t1', 'p1', 'W:/TapRelay', '任务', 'open', 'idle', 'completed', 'finished', 1, 1000, 9000, 9000, NULL);";
                        command.ExecuteNonQuery();
                    }
                }

                var rows = FreebuffDatabaseReader.ReadThreads(dbPath);
                return rows.Count == 1 && rows[0].TokenCount == 0;
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

        Test("Freebuff terminal mapping: running to idle with outcome", () =>
        {
            // completed：运行中 -> 完成；token 统计随行携带。
            var completed = FreebuffMonitor.TryBuildTerminalEvent(new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                Title = "重构适配器",
                TurnState = "idle",
                LastTurnOutcome = "completed",
                UpdatedAt = 2000,
                TokenCount = 850,
                AssistantPartsJson = "[{\"kind\":\"reasoning\",\"text\":\"不要展示这段\"},{\"kind\":\"text\",\"text\":\"执行总结：已完成适配器重构并通过测试\"}]"
            }, new FreebuffMonitor.ThreadSnapshot { LastTurnOutcome = null, UpdatedAt = 1000 });
            if (completed == null || completed.Status != "completed") return false;
            if (completed.Message != "执行总结：已完成适配器重构并通过测试") return false;
            if (completed.TaskLog != completed.Message) return false;
            if (completed.Message.IndexOf("适配器重构", StringComparison.Ordinal) < 0) return false;
            if (completed.Message.IndexOf("Freebuff", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (completed.TokenCount != 850) return false;

            // error -> failed，正文取 harness_state.output.message
            var failed = FreebuffMonitor.TryBuildTerminalEvent(new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                Title = "新任务",
                TurnState = "idle",
                LastTurnOutcome = "error",
                HarnessState = "{\"output\":{\"type\":\"error\",\"message\":\"The API returned a 401.\"}}",
                UpdatedAt = 3000
            }, new FreebuffMonitor.ThreadSnapshot { LastTurnOutcome = null, UpdatedAt = 2000 });
            if (failed == null || failed.Status != "failed") return false;
            if (failed.Message.IndexOf("401", StringComparison.Ordinal) < 0) return false;

            // stopped / closed -> stopped（用户停止，不误报失败）
            var stopped = FreebuffMonitor.TryBuildTerminalEvent(new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                Title = "旧任务",
                TurnState = "idle",
                LastTurnOutcome = "stopped",
                UpdatedAt = 4000
            }, new FreebuffMonitor.ThreadSnapshot { LastTurnOutcome = null, UpdatedAt = 3000 });
            var closed = FreebuffMonitor.TryBuildTerminalEvent(new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                Title = "旧任务",
                TurnState = "idle",
                LastTurnOutcome = "closed",
                UpdatedAt = 5000
            }, new FreebuffMonitor.ThreadSnapshot { LastTurnOutcome = null, UpdatedAt = 4000 });
            return stopped != null && stopped.Status == "stopped" &&
                   closed != null && closed.Status == "stopped";
        });

        Test("Freebuff assistant task log ignores reasoning and tool parts", () =>
            FreebuffDatabaseReader.ReadAssistantTaskLog(
                "[{\"kind\":\"reasoning\",\"text\":\"internal\"},{\"kind\":\"tool\",\"text\":\"command\"},{\"kind\":\"text\",\"text\":\"最终总结内容\"},{\"kind\":\"text\",\"text\":\"补充结果\"}]") ==
            "最终总结内容\n\n补充结果");

        Test("Freebuff terminal mapping: unchanged state produces no event", () =>
        {
            // 同一终态再次扫描（updated_at 未变）不应重复通知。
            var row = new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                TurnState = "idle",
                LastTurnOutcome = "completed",
                UpdatedAt = 2000
            };
            return FreebuffMonitor.TryBuildTerminalEvent(row, new FreebuffMonitor.ThreadSnapshot
            {
                LastTurnOutcome = "completed",
                UpdatedAt = 2000
            }) == null;
        });

        Test("Freebuff terminal mapping: merge-conflict attention is waiting", () =>
        {
            var waiting = FreebuffMonitor.TryBuildTerminalEvent(new FreebuffThreadRow
            {
                Id = "t",
                ProjectPath = "W:/TapRelay",
                TurnState = "running",
                AttentionReason = "merge-conflict",
                AttentionRevision = 3,
                UpdatedAt = 2000
            }, new FreebuffMonitor.ThreadSnapshot { AttentionReason = null, UpdatedAt = 1000 });
            return waiting != null && waiting.Status == "waiting" &&
                   waiting.Message.IndexOf("合并冲突", StringComparison.Ordinal) >= 0;
        });

        Test("Freebuff error extraction reads harness_state output.message", () =>
        {
            var error = FreebuffDatabaseReader.ReadErrorFromHarnessState(
                "{\"output\":{\"type\":\"error\",\"message\":\"The session ended before this response completed.\"}}");
            return error.IndexOf("session ended", StringComparison.Ordinal) >= 0;
        });

        Test("Freebuff elevation SSE event maps to waiting status", () =>
        {
            const string payload =
                "{\"type\":\"elevation_request\",\"request\":{\"id\":\"req-1\",\"threadId\":\"thr-1\",\"command\":\"powershell -Command Start-Process\",\"cwd\":\"W:\\\\TapRelay\",\"reason\":\"需要管理员权限\",\"createdAt\":1787000000000}}";
            if (!FreebuffOrchestratorEventParser.TryParseElevationRequest(payload, out var completion)) return false;
            return completion.Status == "waiting" &&
                   completion.TaskId == "freebuff-elev-req-1" &&
                   completion.Cwd.IndexOf("TapRelay", StringComparison.Ordinal) >= 0 &&
                   completion.Message.IndexOf("需要你确认", StringComparison.Ordinal) >= 0 &&
                   completion.Message.IndexOf("管理员权限", StringComparison.Ordinal) >= 0 &&
                   completion.Message.IndexOf("Start-Process", StringComparison.Ordinal) >= 0;
        });

        Test("Freebuff elevation parser ignores non-elevation events", () =>
        {
            const string payload = "{\"type\":\"thread\",\"threadId\":\"thr-1\"}";
            return !FreebuffOrchestratorEventParser.TryParseElevationRequest(payload, out _);
        });

        Test("Freebuff orchestrator port parses ready and listening lines", () =>
        {
            var fromReady = FreebuffDataLocator.ReadOrchestratorPortFromLine(
                "[orchestrator-ready] {\"launchId\":\"abc\",\"pid\":1234,\"port\":64562}");
            var fromListening = FreebuffDataLocator.ReadOrchestratorPortFromLine(
                "freebuff-desktop orchestrator listening on http://127.0.0.1:5173");
            var fromGarbage = FreebuffDataLocator.ReadOrchestratorPortFromLine("Starting Freebuff orchestrator");
            return fromReady == 64562 && fromListening == 5173 && fromGarbage == null;
        });

        TestLiveFreebuff("Freebuff live environment: locator finds real databases and port", () =>
        {
            // Explicit integration check: missing prerequisites are failures when requested.
            var found = FreebuffDataLocator.FindDatabases().ToArray();
            if (found.Length == 0)
            {
                throw new InvalidOperationException("Freebuff 实机验证缺少已运行会话的数据库");
            }

            foreach (var database in found)
            {
                if (!File.Exists(database)) return false;
                if (!database.EndsWith("desktop-v2.db", StringComparison.OrdinalIgnoreCase)) return false;
                var (rows, observedMax) = FreebuffDatabaseReader.ReadThreadsWithMax(database);
                if (rows.Count == 0) throw new InvalidOperationException("Freebuff 实机验证的数据库没有可校验会话，请先在应用中完成一个任务");
                if (observedMax < 0) return false;
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Id)) return false;
                }
            }

            var port = FreebuffDataLocator.ReadOrchestratorPort();
            if (port == null) throw new InvalidOperationException("Freebuff 实机验证缺少运行中的 orchestrator 端口");
            return port > 0 && port <= 65535;
        });

        Test("IsPathUnderRoot accepts nested executable under root", () =>
            AdaptedProcessGate.IsPathUnderRoot(@"C:\Program Files\ZCode\zcode.exe", @"C:\Program Files") &&
            AdaptedProcessGate.IsPathUnderRoot(@"C:\Program Files\ZCode\bin\zcode.exe", @"C:\Program Files\"));
        Test("IsPathUnderRoot rejects partial directory prefix", () =>
            !AdaptedProcessGate.IsPathUnderRoot(@"C:\Tools2\zcode.exe", @"C:\Tools") &&
            !AdaptedProcessGate.IsPathUnderRoot(@"C:\Tools-extra\zcode.exe", @"C:\Tools"));
        Test("IsPathUnderRoot is case-insensitive and tolerates trailing separators", () =>
            AdaptedProcessGate.IsPathUnderRoot(@"c:\program files\zcode\zcode.exe", @"C:\Program Files\"));
        Test("IsPathUnderRoot rejects unrelated path", () =>
            !AdaptedProcessGate.IsPathUnderRoot(@"D:\Temp\zcode.exe", @"C:\Program Files"));
        Test("IsPathUnderRoot rejects empty or whitespace input", () =>
            !AdaptedProcessGate.IsPathUnderRoot(string.Empty, @"C:\Program Files") &&
            !AdaptedProcessGate.IsPathUnderRoot(@"C:\Program Files\zcode.exe", " ") &&
            !AdaptedProcessGate.IsPathUnderRoot(@"C:\Program Files\zcode.exe", null!));

        Test("Renamed stub process outside trusted roots is not install evidence", () =>
        {
            // 回归测试：临时目录里改名复制的同名 exe（E2E 曾因此误报“已安装”）
            // 只有所在目录被列为可信根时才算证据；其它根、空根都不算。
            var rootA = Path.Combine(Path.GetTempPath(), "taprelay-gate-rootA-" + Guid.NewGuid().ToString("N"));
            var rootB = Path.Combine(Path.GetTempPath(), "taprelay-gate-rootB-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootA);
            var source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(source))
            {
                return true; // 环境缺少 powershell 时跳过。
            }

            var executable = Path.Combine(rootA, "zcode.exe");
            File.Copy(source, executable, true);
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 60\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                // 进程刚启动时 MainModule 可能尚未就绪，轮询等待路径可读。
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (AdaptedProcessGate.IsRunningFromTrustedRoot("zcode", new List<string?> { rootA }))
                    {
                        break;
                    }
                    Thread.Sleep(100);
                }

                return AdaptedProcessGate.IsRunningFromTrustedRoot("zcode", new List<string?> { rootA }) &&
                       !AdaptedProcessGate.IsRunningFromTrustedRoot("zcode", new List<string?> { rootB }) &&
                       !AdaptedProcessGate.IsRunningFromTrustedRoot("zcode", new List<string?>()) &&
                       !AdaptedProcessGate.AreProcessesRunningFromRoots(
                           new[] { "zcode" }, new string?[] { null, " " });
            }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
                try { process?.Dispose(); } catch { }
                try { Directory.Delete(rootA, true); } catch { }
                try { Directory.Delete(rootB, true); } catch { }
            }
        });

        Test("TraeUserPromptResolver extracts prompt bytes after key", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "taprelay-prompt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "state.vscdb");
                var key = "chat-suggest:cache:abc123";
                var oldJson = "{\"version\":1,\"response\":{\"success\":true,\"code\":0,\"result\":[{\"id\":\"x-1\",\"text\":\"旧指令\"}]}," +
                              "\"messagesLength\":10,\"savedAt\":100}";
                var newJson = "{\"version\":1,\"response\":{\"success\":true,\"code\":0,\"result\":[{\"id\":\"y-1\",\"text\":\"超时时间从12秒变为4秒\"}]}," +
                              "\"messagesLength\":11,\"savedAt\":200}";
                // 模拟真实库布局：内部页索引区 + 叶子页（键名后跟 JSON）+ 旧 cell 残留。
                var payload = Encoding.UTF8.GetBytes(
                    "header" + key + "nextkey" + key + "\x11\x22" + newJson + "gap" + key + "\x33\x44" + oldJson);
                File.WriteAllBytes(file, payload);

                var values = TraeUserPromptResolver.ExtractPromptValues(file, key).ToList();
                return values.Count == 2 &&
                       values.Any(v => v.Text == "超时时间从12秒变为4秒" && v.SavedAt == 200) &&
                       values.Any(v => v.Text == "旧指令" && v.SavedAt == 100);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Test("TraeUserPromptResolver skips index-only key occurrences and truncated JSON", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "taprelay-prompt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "state.vscdb");
                var key = "ai-chat.chatQueryCompletion.v2.s1";
                // 内部页（键名后无 '{'）+ 截断 JSON（括号不闭合）+ 正常值（含中文 UTF-8 字节）。
                var payload = Encoding.UTF8.GetBytes(
                    key + "other" +
                    key + "ab{\"version\":1,\"response\":{\"result\":[{\"text\":\"截断" +
                    key + "cd{\"version\":1,\"response\":{\"result\":[{\"text\":\"OK中文\"}]},\"savedAt\":5}");
                File.WriteAllBytes(file, payload);

                var values = TraeUserPromptResolver.ExtractPromptValues(file, key).ToList();
                return values.Count == 1 && values[0].Text == "OK中文" && values[0].SavedAt == 5;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Test("TraeUserPromptResolver.Summarize collapses whitespace and truncates", () =>
        {
            var collapsed = TraeUserPromptResolver.Summarize("  多行\n\n文本\t压缩  ");
            var longText = new string('字', 300);
            var truncated = TraeUserPromptResolver.Summarize(longText);
            return collapsed == "多行 文本 压缩" &&
                   truncated.Length == TraeUserPromptResolver.MaxPromptLength + 3 &&
                   truncated.EndsWith("...");
        });

        Test("TraeWorkWorkspaceResolver extracts logs directory name", () =>
        {
            return TraeWorkWorkspaceResolver.ExtractLogsDirectoryName(
                       @"C:\Users\Admin\AppData\Roaming\TRAE SOLO CN\logs\20260821T133204\window1\renderer.log") == "20260821T133204" &&
                   TraeWorkWorkspaceResolver.ExtractLogsDirectoryName(
                       @"C:\Users\x/logs\20260616T074159/window1/renderer.log") == "20260616T074159" &&
                   TraeWorkWorkspaceResolver.ExtractLogsDirectoryName(@"C:\no\here.txt") == string.Empty &&
                   TraeWorkWorkspaceResolver.ExtractLogsDirectoryName("") == string.Empty;
        });

        Test("TraeWorkWorkspaceResolver reads folder and nested workspace meta", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "taprelay-wsws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 单文件夹形态（TraeCode 式）：folder URI。
                var folderMeta = Path.Combine(dir, "folder-workspace.json");
                File.WriteAllText(folderMeta, "{\"folder\":\"file:///c%3A/Users/Admin/Projects/Demo\"}");

                // 多文件夹形态（TraeWork 式）：指向二级 workspace.json。
                var nestedRoot = Path.Combine(dir, "Workspaces", "1778059807377");
                Directory.CreateDirectory(nestedRoot);
                var nestedMeta = Path.Combine(nestedRoot, "workspace.json");
                File.WriteAllText(nestedMeta, "{\"folders\":[{\"path\":\"W:/TapRelay\"},{\"path\":\"missing/dir\"}]}");
                var workspaceMeta = Path.Combine(dir, "workspace-workspace.json");
                File.WriteAllText(workspaceMeta,
                    "{\"workspace\":\"file:///" + Uri.EscapeDataString(nestedMeta.Replace('\\', '/')) + "\"}");

                var demoDir = Path.Combine(Path.GetTempPath(), "taprelay-wsws-demo");
                Directory.CreateDirectory(demoDir);
                var singleMeta = Path.Combine(dir, "single.json");
                File.WriteAllText(singleMeta,
                    "{\"folder\":\"file:///" + Uri.EscapeDataString(demoDir.Replace('\\', '/')) + "\"}");

                var invalid = Path.Combine(dir, "invalid.json");
                File.WriteAllText(invalid, "{\"nothing\":true}");

                return TraeWorkWorkspaceResolver.TryReadWorkspaceFolder(workspaceMeta, out var nested) &&
                       string.Equals(nested, "W:\\TapRelay", StringComparison.OrdinalIgnoreCase) &&
                       TraeWorkWorkspaceResolver.TryReadWorkspaceFolder(singleMeta, out var single) &&
                       string.Equals(single, demoDir, StringComparison.OrdinalIgnoreCase) &&
                       !TraeWorkWorkspaceResolver.TryReadWorkspaceFolder(invalid, out _) &&
                       !TraeWorkWorkspaceResolver.TryReadWorkspaceFolder(nestedMeta, out _) &&
                       !TraeWorkWorkspaceResolver.TryReadWorkspaceFolder(folderMeta, out _);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                try { Directory.Delete(Path.Combine(Path.GetTempPath(), "taprelay-wsws-demo"), true); } catch { }
            }
        });

        Test("TraeUserPromptResolver live environment resolves real prompts", () =>
        {
            // 真实环境验证：从本机 state.vscdb 里挑一个真实会话缓存键，
            // 校验字节提取链路对真实 SQLite 布局有效。未安装/无缓存时跳过。
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var traeworkDb = new[]
            {
                Path.Combine(appData, "TRAE SOLO CN", "User", "globalStorage", "state.vscdb"),
                Path.Combine(appData, "TraeWork CN", "User", "globalStorage", "state.vscdb")
            }.FirstOrDefault(File.Exists);
            if (traeworkDb != null)
            {
                var text = ReadAllTextShared(traeworkDb);
                var match = Regex.Match(text, @"chat-suggest:cache:([0-9a-f]{16,32})");
                if (match.Success)
                {
                    if (!TraeUserPromptResolver.TryResolvePrompt("traework", match.Groups[1].Value, out var prompt) ||
                        string.IsNullOrWhiteSpace(prompt))
                    {
                        return false;
                    }
                }
            }

            var traeStorage = new[]
            {
                Path.Combine(appData, "Trae CN", "User", "workspaceStorage"),
                Path.Combine(appData, "TraeCode CN", "User", "workspaceStorage")
            }.FirstOrDefault(Directory.Exists);
            if (traeStorage != null)
            {
                foreach (var entry in Directory.EnumerateDirectories(traeStorage))
                {
                    var database = Path.Combine(entry, "state.vscdb");
                    if (!File.Exists(database))
                    {
                        continue;
                    }

                    var text = ReadAllTextShared(database);
                    var match = Regex.Match(text, @"ai-chat\.chatQueryCompletion\.v2\.([0-9a-f]{16,32})");
                    if (!match.Success)
                    {
                        continue;
                    }

                    return TraeUserPromptResolver.TryResolvePrompt("trae", match.Groups[1].Value, out var prompt) &&
                           !string.IsNullOrWhiteSpace(prompt);
                }
            }

            return true;
        });

        Test("ToastNotificationReader parses ToastText02 and ToastGeneric payloads", () =>
        {
            var completed =
                "<toast><visual><binding template=\"ToastText02\">" +
                "<text id=\"1\">任务完成 · 适配 TraeCode CN</text>" +
                "<text id=\"2\">通知正文改固定文案的改动已经全部完成并部署验证通过。</text>" +
                "</binding></visual><audio silent=\"true\"/></toast>";
            // ZCode 桌面端模板：text 无 id，按位置取标题/正文。
            var zcodeGeneric =
                "<toast><visual><binding template=\"ToastGeneric\">" +
                "<text>任务已完成</text>" +
                "<text>任务：修复打包软件logo与名称问题</text>" +
                "</binding></visual><audio silent=\"true\"/></toast>";
            // OpenCode 桌面端较新模板：ToastGeneric + 图片节点（text 仍按位置）。
            var opencodeGeneric =
                "<toast><visual><binding template=\"ToastGeneric\">" +
                "<text>回复已就绪</text>" +
                "<text>灵感库AI大模型配置自动匹配</text>" +
                "<image id=\"1\" placement=\"appLogoOverride\" src=\"C:\\img.png\"/>" +
                "</binding></visual></toast>";
            var waiting =
                "<toast><visual><binding template=\"ToastText02\">" +
                "<text id=\"1\">Waiting for your response</text>" +
                "<text id=\"2\">Your task is awaiting your response. Please return to TRAE.</text>" +
                "</binding></visual></toast>";
            var malformed = "<toast><visual>not closed";

            return ToastNotificationReader.TryParseToastPayload(completed, out var t1, out var b1) &&
                   t1 == "任务完成 · 适配 TraeCode CN" &&
                   b1 == "通知正文改固定文案的改动已经全部完成并部署验证通过。" &&
                   ToastNotificationReader.TryParseToastPayload(zcodeGeneric, out var t3, out var b3) &&
                   t3 == "任务已完成" && b3 == "任务：修复打包软件logo与名称问题" &&
                   ToastNotificationReader.TryParseToastPayload(opencodeGeneric, out var t4, out var b4) &&
                   t4 == "回复已就绪" && b4 == "灵感库AI大模型配置自动匹配" &&
                   ToastNotificationReader.TryParseToastPayload(waiting, out var t2, out var b2) &&
                   t2 == "Waiting for your response" &&
                   b2.Contains("awaiting your response") &&
                   !ToastNotificationReader.TryParseToastPayload(malformed, out _, out _) &&
                   !ToastNotificationReader.TryParseToastPayload("", out _, out _) &&
                   !ToastNotificationReader.TryParseToastPayload(null!, out _, out _) &&
                   ToastNotificationReader.BuildMessage("标题", "正文") == "标题\n\n正文" &&
                   ToastNotificationReader.BuildMessage("标题", "") == "标题" &&
                   ToastNotificationReader.BuildMessage("", "正文").Length == 0;
        });

        Test("Trae toast task log rejects user prompts and generic status text", () =>
        {
            var realBody = "已完成 BuildTraeMessage 的回归验证，真实结果已写入通知卡片。";
            return ToastNotificationReader.IsRealTaskLog("任务完成 · TraeCode CN", realBody) &&
                   ToastNotificationReader.TryBuildRealTaskMessage(
                       "任务完成 · TraeCode CN", realBody, out var message, out var taskLog) &&
                   message.Contains(realBody) && taskLog == realBody &&
                   !ToastNotificationReader.IsRealTaskLog(
                       "任务完成 · TraeCode CN", "指令：请检查 BuildTraeMessage") &&
                   !ToastNotificationReader.IsRealTaskLog(
                       "任务完成 · TraeCode CN", "任务已完成，请到电脑端查看详细日志") &&
                   !ToastNotificationReader.TryBuildRealTaskMessage(
                       "任务完成 · TraeCode CN", "任务已完成", out _, out _);
        });

        Test("Trae toast reader chooses the nearest real toast for the event time", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "taprelay-toast-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var database = Path.Combine(tempDir, "wpndatabase.db");
            var eventTime = DateTime.UtcNow;
            try
            {
                using (var connection = new SqliteConnection("Data Source=" + database))
                {
                    connection.Open();
                    using var schema = connection.CreateCommand();
                    schema.CommandText =
                        "CREATE TABLE NotificationHandler (RecordId INTEGER PRIMARY KEY, PrimaryId TEXT);" +
                        "CREATE TABLE Notification (Payload TEXT, ArrivalTime INTEGER, HandlerId INTEGER);" +
                        "INSERT INTO NotificationHandler (RecordId, PrimaryId) VALUES (1, 'ByteDance.TraeCN');";
                    schema.ExecuteNonQuery();

                    void InsertToast(string body, DateTime arrival)
                    {
                        using var insert = connection.CreateCommand();
                        insert.CommandText =
                            "INSERT INTO Notification (Payload, ArrivalTime, HandlerId) " +
                            "VALUES (@payload, @arrival, 1)";
                        var payload = insert.CreateParameter();
                        payload.ParameterName = "@payload";
                        payload.Value =
                            "<toast><visual><binding template=\"ToastText02\">" +
                            "<text id=\"1\">任务完成 · TraeCode CN</text>" +
                            "<text id=\"2\">" + body + "</text>" +
                            "</binding></visual></toast>";
                        insert.Parameters.Add(payload);
                        var arrivalParameter = insert.CreateParameter();
                        arrivalParameter.ParameterName = "@arrival";
                        arrivalParameter.Value = arrival.ToFileTimeUtc();
                        insert.Parameters.Add(arrivalParameter);
                        insert.ExecuteNonQuery();
                    }

                    InsertToast("较早但是真实的旧结果", eventTime.AddSeconds(-8));
                    InsertToast("任务已完成", eventTime.AddSeconds(5));
                    InsertToast("当前回合的真实 Agent 总结", eventTime.AddSeconds(1));
                }

                ToastNotificationReader.DatabaseOverride = database;
                var codeMatched = ToastNotificationReader.TryReadLatestRealTaskLog(
                           "trae", eventTime, out var title, out var body, TimeSpan.FromSeconds(10)) &&
                       title == "任务完成 · TraeCode CN" && body == "当前回合的真实 Agent 总结";
                using (var connection = new SqliteConnection("Data Source=" + database))
                {
                    connection.Open();
                    using var rename = connection.CreateCommand();
                    rename.CommandText = "UPDATE NotificationHandler SET PrimaryId = 'ByteDance.TraeSoloCN'";
                    rename.ExecuteNonQuery();
                }
                return codeMatched && ToastNotificationReader.TryReadLatestRealTaskLog(
                           "traework", eventTime, out _, out var workBody) &&
                       workBody == "当前回合的真实 Agent 总结" &&
                       !ToastNotificationReader.TryReadLatestRealTaskLog(
                           "traework", eventTime.AddMinutes(1), out _, out _);
            }
            finally
            {
                ToastNotificationReader.DatabaseOverride = null;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

        Test("ToastNotificationReader live environment reads latest toasts", () =>
        {
            // 真实环境验证：本机 wpndatabase.db 存在各应用通知时读取最新一条
            //（放宽时间窗到 30 天——只验证读取与解析链路）。
            var database = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");
            if (!File.Exists(database))
            {
                return true; // 系统未启用通知持久化时跳过。
            }

            foreach (var source in new[] { "trae", "traework", "zcode", "opencode" })
            {
                if (ToastNotificationReader.TryReadLatest(
                        source,
                        DateTime.UtcNow - TimeSpan.FromMinutes(5),
                        out var title,
                        out var body,
                        TimeSpan.FromDays(30)))
                {
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return false;
                    }
                    Console.WriteLine("  toast[" + source + "]: " + title);
                }
            }

            return true; // 读不到（未安装/无通知）不视为失败；读到即校验通过。
        });

        Test("ZCodeSessionReader resolves real title, turn usage and task log from a temp database", () =>
        {
            // 构造一个最小化的 ZCode db.sqlite 骨架，验证标题/用量/日志三条读取链路，
            // 不依赖开发机上的真实会话库，也不要求 ZCode 已安装。
            var tempDir = Path.Combine(Path.GetTempPath(), "taprelay-zcode-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var database = Path.Combine(tempDir, "db.sqlite");
            try
            {
                using (var connection = new SqliteConnection("Data Source=" + database))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                        CREATE TABLE session (
                            id TEXT, project_id TEXT, workspace_id TEXT, parent_id TEXT, slug TEXT,
                            directory TEXT, path TEXT, title TEXT, version TEXT, share_url TEXT,
                            summary_additions INTEGER, summary_deletions INTEGER, summary_files INTEGER,
                            summary_diffs TEXT, revert TEXT, permission TEXT,
                            time_created INTEGER, time_updated INTEGER, time_compacting INTEGER,
                            time_archived INTEGER, task_type TEXT, title_source TEXT,
                            title_message_id TEXT, time_title_updated INTEGER, trace_id TEXT
                        );
                        CREATE TABLE turn_usage (
                            session_id TEXT, turn_id TEXT, trace_id TEXT, user_message_id TEXT,
                            status TEXT, started_at INTEGER, first_model_start_at INTEGER,
                            first_token_at INTEGER, completed_at INTEGER, duration_ms INTEGER,
                            time_to_first_token_ms INTEGER, model_request_count INTEGER,
                            model_retry_count INTEGER, tool_call_count INTEGER, tool_error_count INTEGER,
                            input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
                            cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER,
                            computed_total_tokens INTEGER, retryable INTEGER, cancelled_by_user INTEGER,
                            context_exceeded INTEGER, error_type TEXT, error_code TEXT
                        );
                        CREATE TABLE message (id TEXT, session_id TEXT, time_created INTEGER,
                            time_updated INTEGER, data TEXT, sequence INTEGER);
                        CREATE TABLE part (id TEXT, message_id TEXT, session_id TEXT,
                            time_created INTEGER, time_updated INTEGER, data TEXT, sequence INTEGER);
                        INSERT INTO session (id, directory, path, title) VALUES
                            ('sess_abc123', 'W:\\project', 'W:\\project', '修复登录问题');
                        INSERT INTO turn_usage (session_id, turn_id, status, duration_ms,
                            computed_total_tokens, completed_at) VALUES
                            ('sess_abc123', 'turn_1', 'completed', 12345, 67890, 1000);
                        INSERT INTO message (id, session_id, data, sequence) VALUES
                            ('msg_user', 'sess_abc123', '{""role"": ""user"", ""anchor"": {""turnId"": ""turnA""}}', 1),
                            ('msg_asst_a', 'sess_abc123', '{""role"": ""assistant"", ""anchor"": {""turnId"": ""turnA""}}', 2),
                            ('msg_asst_b', 'sess_abc123', '{""role"": ""assistant"", ""anchor"": {""turnId"": ""turnB""}}', 3);
                        INSERT INTO part (id, message_id, session_id, data, sequence) VALUES
                            ('p1', 'msg_user', 'sess_abc123', '{""type"":""text"",""text"":""请修复登录""}', 1),
                            -- ZCode resets part.sequence for every message. Both assistant
                            -- messages therefore use the same sequence value in real data.
                            ('p2', 'msg_asst_a', 'sess_abc123', '{""type"":""text"",""text"":""已修复登录页跳转。""}', 3),
                            ('p3', 'msg_asst_b', 'sess_abc123', '{""type"":""text"",""text"":""补充了退出按钮样式。""}', 3);
                    ";
                    command.ExecuteNonQuery();
                }

                ZCodeSessionReader.DatabaseOverride = database;

                var titleOk = ZCodeSessionReader.TryResolveTitle("sess_abc123", out var title) &&
                              title == "修复登录问题";
                var usageOk = ZCodeSessionReader.TryResolveTurnUsage(
                                  "sess_abc123", "turn_1", out var durationMs, out var tokenCount) &&
                              durationMs == 12345L && tokenCount == 67890L;
                // 无 turnId 时取整个会话最近一条 assistant 正文（紧凑 JSON，冒号后无空格）。
                var logOk = ZCodeSessionReader.TryResolveTaskLog("sess_abc123", out var taskLogGlobal) &&
                            taskLogGlobal.Contains("退出按钮样式") &&
                            !taskLogGlobal.Contains("请修复登录");
                // 按 turnId 过滤：turnA 只取该轮正文，不得混入 turnB 的内容。
                var logTurnOk = ZCodeSessionReader.TryResolveTaskLog(
                                    "sess_abc123", "turnA", out var taskLogTurnA) &&
                                taskLogTurnA.Contains("登录页跳转") &&
                                !taskLogTurnA.Contains("退出按钮") &&
                                !taskLogTurnA.Contains("请修复登录");
                // 当轮提问：turnA 应取该轮第一条用户提问「请修复登录」，且不带 turnB 的
                // 提问；无 turnId 时回退整个会话最近一条用户提问。
                var queryTurnOk = ZCodeSessionReader.TryResolveTurnQuery(
                                      "sess_abc123", "turnA", out var queryTurnA) &&
                                  queryTurnA.Contains("请修复登录") &&
                                  !queryTurnA.Contains("低字面量提醒");
                var queryGlobalOk = ZCodeSessionReader.TryResolveTurnQuery(
                                        "sess_abc123", string.Empty, out var queryGlobal) &&
                                    !string.IsNullOrWhiteSpace(queryGlobal);

                return titleOk && usageOk && logOk && logTurnOk && queryTurnOk && queryGlobalOk;
            }
            finally
            {
                ZCodeSessionReader.DatabaseOverride = null;
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        Test("WorkBuddySessionReader resolves the real task title from a temp database", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "taprelay-workbuddy-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var database = Path.Combine(tempDir, "workbuddy.db");
            try
            {
                using (var connection = new SqliteConnection("Data Source=" + database))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                        CREATE TABLE sessions (id TEXT, cwd TEXT, title TEXT, is_playground INTEGER,
                                               permission_mode TEXT, use_sandbox_cli INTEGER);
                        INSERT INTO sessions (id, cwd, title, is_playground, permission_mode, use_sandbox_cli) VALUES
                            ('workbuddy-session', 'C:\\Users\\Admin\\WorkBuddy\\2026-08-26-10-51-35', '查询容桂天气变化', 1, 'bypassPermissions', 1),
                            ('workbuddy-project', 'W:\\TapRelay', '优化快速连接弹窗按钮布局与文本', 0, 'fullAccess', 1);
                    ";
                    command.ExecuteNonQuery();
                }

                WorkBuddySessionReader.DatabaseOverride = database;
                var titleOk = WorkBuddySessionReader.TryResolveTitle(
                                  "workbuddy-session", out var title) &&
                              title == "查询容桂天气变化";
                var taskNameOk = WorkBuddySessionReader.TryResolveProjectOrTaskName(
                                     "workbuddy-session", out var taskName, out var taskIsProject) &&
                                 taskName == "查询容桂天气变化" && !taskIsProject;
                var projectNameOk = WorkBuddySessionReader.TryResolveProjectOrTaskName(
                                         "workbuddy-project", out var projectName, out var projectIsProject) &&
                                     projectName == "TapRelay" && projectIsProject;
                var permissionModeOk = WorkBuddySessionReader.TryResolvePermissionMode(
                                            "workbuddy-project", out var permissionMode) &&
                                        permissionMode == "fullAccess" &&
                                        WorkBuddyAcpAgentProxy.TryNormalizePermissionMode(
                                            permissionMode, out var normalizedMode) &&
                                        normalizedMode == "fullAccess";
                var sandboxOk = WorkBuddySessionReader.TryResolveSandboxEnabled(
                                    "workbuddy-project", out var sandboxEnabled) && sandboxEnabled;
                var safetyOk = !WorkBuddyAcpAgentProxy.TryNormalizePermissionMode(
                                   "allowEverything", out _);
                return titleOk && taskNameOk && projectNameOk && permissionModeOk &&
                       sandboxOk && safetyOk;
            }
            finally
            {
                WorkBuddySessionReader.DatabaseOverride = null;
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        Test("Cloudflared tunnel detector parses hostname with matching port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cfd-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "tunnel: 7a8442ca-f76f-4ad6-9b83-1b78fc0ef9da\n" +
                "credentials-file: C:\\Users\\test\\.cloudflared\\7a8442ca-f76f-4ad6-9b83-1b78fc0ef9da.json\n" +
                "\n" +
                "ingress:\n" +
                "  - hostname: win.example.org\n" +
                "    service: http://localhost:1122\n" +
                "  - service: http_status:404\n");
            try
            {
                return CloudflaredTunnelDetector.TryParseTunnelHost(file, 1122) == "win.example.org";
            }
            finally { File.Delete(file); }
        });

        Test("Cloudflared tunnel detector rejects mismatched port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cfd-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "ingress:\n" +
                "  - hostname: win.example.org\n" +
                "    service: http://localhost:80\n");
            try
            {
                return CloudflaredTunnelDetector.TryParseTunnelHost(file, 1122) == string.Empty;
            }
            finally { File.Delete(file); }
        });

        Test("Cloudflared tunnel detector rejects IP hostname and missing file", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cfd-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "ingress:\n" +
                "  - hostname: 192.168.5.6\n" +
                "    service: http://localhost:1122\n");
            try
            {
                return CloudflaredTunnelDetector.TryParseTunnelHost(file, 1122) == string.Empty &&
                       CloudflaredTunnelDetector.TryParseTunnelHost(file + ".missing", 1122) == string.Empty;
            }
            finally { File.Delete(file); }
        });

        Test("frp ini detector parses http tunnel with matching local_port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "frp-test-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(file,
                "[common]\n" +
                "server_addr = 1.2.3.4\n" +
                "server_port = 7000\n" +
                "\n" +
                "[web]\n" +
                "type = http\n" +
                "local_port = 1122\n" +
                "custom_domains = win.example.org\n" +
                "\n" +
                "[ssh]\n" +
                "type = tcp\n" +
                "local_port = 22\n" +
                "remote_port = 7001\n");
            try
            {
                var endpoints = FrpTunnelDetector.TryParseConfig(file, 1122);
                return endpoints.Count == 1 && endpoints[0] == "ws://win.example.org";
            }
            finally { File.Delete(file); }
        });

        Test("frp ini detector parses tcp tunnel to server:remote_port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "frp-test-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(file,
                "[common]\n" +
                "server_addr = frp.example.com\n" +
                "server_port = 7000\n" +
                "\n" +
                "[ssh]\n" +
                "type = tcp\n" +
                "local_port = 1122\n" +
                "remote_port = 7001\n");
            try
            {
                var endpoints = FrpTunnelDetector.TryParseConfig(file, 1122);
                return endpoints.Count == 1 && endpoints[0] == "ws://frp.example.com:7001";
            }
            finally { File.Delete(file); }
        });

        Test("frp ini detector rejects mismatched local_port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "frp-test-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(file,
                "[web]\n" +
                "type = http\n" +
                "local_port = 80\n" +
                "custom_domains = win.example.org\n");
            try
            {
                return FrpTunnelDetector.TryParseConfig(file, 1122).Count == 0;
            }
            finally { File.Delete(file); }
        });

        Test("frp toml detector parses customDomains array", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "frp-test-" + Guid.NewGuid().ToString("N") + ".toml");
            File.WriteAllText(file,
                "serverAddr = \"1.2.3.4\"\n" +
                "serverPort = 7000\n" +
                "\n" +
                "[[proxies]]\n" +
                "name = \"web\"\n" +
                "type = \"http\"\n" +
                "localPort = 1122\n" +
                "customDomains = [\"a.example.com\", \"b.example.com\"]\n" +
                "\n" +
                "[[proxies]]\n" +
                "name = \"other\"\n" +
                "type = \"tcp\"\n" +
                "localPort = 80\n" +
                "remotePort = 8000\n");
            try
            {
                var endpoints = FrpTunnelDetector.TryParseConfig(file, 1122);
                return endpoints.Count == 2 &&
                       endpoints.Contains("ws://a.example.com") &&
                       endpoints.Contains("ws://b.example.com");
            }
            finally { File.Delete(file); }
        });

        Test("ngrok yml detector parses hostname tunnel", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "ngrok-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "version: \"2\"\n" +
                "authtoken: xxx\n" +
                "tunnels:\n" +
                "  web:\n" +
                "    proto: http\n" +
                "    addr: 1122\n" +
                "    hostname: example.ngrok.app\n");
            try
            {
                var endpoints = NgrokTunnelDetector.TryParseConfig(file, 1122);
                return endpoints.Count == 1 && endpoints[0] == "ws://example.ngrok.app";
            }
            finally { File.Delete(file); }
        });

        Test("ngrok yml detector parses tcp remote_addr", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "ngrok-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "tunnels:\n" +
                "  ssh:\n" +
                "    proto: tcp\n" +
                "    addr: 1122\n" +
                "    remote_addr: 0.tcp.ngrok.io:12345\n");
            try
            {
                var endpoints = NgrokTunnelDetector.TryParseConfig(file, 1122);
                return endpoints.Count == 1 && endpoints[0] == "ws://0.tcp.ngrok.io:12345";
            }
            finally { File.Delete(file); }
        });

        Test("cpolar yml without hostname yields no endpoints", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cpolar-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "tunnels:\n" +
                "  web:\n" +
                "    proto: http\n" +
                "    addr: 1122\n");
            try
            {
                return NgrokTunnelDetector.TryParseConfig(file, 1122).Count == 0;
            }
            finally { File.Delete(file); }
        });

        Test("ngrok yml detector rejects mismatched addr port", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "ngrok-test-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(file,
                "tunnels:\n" +
                "  web:\n" +
                "    proto: http\n" +
                "    addr: 8080\n" +
                "    hostname: example.ngrok.app\n");
            try
            {
                return NgrokTunnelDetector.TryParseConfig(file, 1122).Count == 0;
            }
            finally { File.Delete(file); }
        });

        Test("tunnel detector registry registers all known detectors", () =>
        {
            var detectors = TunnelDetectorRegistry.Detectors;
            return detectors.Count == 4 &&
                   detectors.Any(d => d.Name == "cloudflared") &&
                   detectors.Any(d => d.Name == "frp") &&
                   detectors.Any(d => d.Name == "ngrok/cpolar") &&
                   detectors.Any(d => d.Name == "tailscale/zerotier");
        });

        Console.WriteLine($"Agent monitor tests: {_passed} passed, {Failures.Count} failed, {_skipped} skipped.");
        foreach (var failure in Failures)
        {
            Console.Error.WriteLine("FAIL: " + failure);
        }

        return Failures.Count == 0 ? 0 : 1;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    private static void TestLiveFreebuff(string name, Func<bool> assertion)
    {
        if (!_liveFreebuff)
        {
            _skipped++;
            Console.WriteLine("SKIP: " + name + " (enable with --live-freebuff)");
            return;
        }
        Test(name, assertion);
    }

    private static void Test(string name, Func<bool> assertion)
    {
        try
        {
            if (assertion())
            {
                _passed++;
                Console.WriteLine("PASS: " + name);
            }
            else
            {
                Failures.Add(name);
            }
        }
        catch (Exception error)
        {
            Failures.Add(name + " (" + error.GetType().Name + ": " + error.Message + ")");
        }
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in roots)
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory != null && depth < 10; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static byte[] BuildDoubaoStructuredCloneMessage(int userType, string text, bool isFinished)
    {
        var bytes = new List<byte>();
        AddDoubaoField(bytes, "user_type");
        bytes.Add((byte)'I');
        bytes.Add((byte)userType);
        AddDoubaoField(bytes, "content_blocks_v2");
        bytes.Add((byte)'A');
        bytes.Add(1);
        AddDoubaoField(bytes, "text_block");
        bytes.Add((byte)'o');
        AddDoubaoField(bytes, "text");
        bytes.Add((byte)'c');
        bytes.AddRange(BitConverter.GetBytes((ushort)text.Length));
        bytes.AddRange(Encoding.Unicode.GetBytes(text));
        AddDoubaoField(bytes, "is_finish");
        bytes.Add((byte)'"');
        bytes.Add(1);
        bytes.Add((byte)(isFinished ? '1' : '0'));
        return bytes.ToArray();
    }

    private static void AddDoubaoField(List<byte> bytes, string name)
    {
        bytes.Add((byte)'"');
        bytes.Add((byte)name.Length);
        bytes.AddRange(Encoding.ASCII.GetBytes(name));
    }
}
