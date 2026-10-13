using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TapRelay
{
    internal sealed class AgentCompletionEventData
    {
        internal string Message { get; set; } = string.Empty;
        /// <summary>原生终态事件携带的回合 ID，用于从 Codex JSONL 精确反查本轮正文。</summary>
        internal string TurnId { get; set; } = string.Empty;
        /// <summary>从来源自身会话记录读取的真实任务日志，空串表示没有可靠正文。</summary>
        internal string TaskLog { get; set; } = string.Empty;
        /// <summary>内部关联用的用户输入，不参与广播；用于豆包延迟读取对应助手回合。</summary>
        internal string CorrelationText { get; set; } = string.Empty;
        internal string Source { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal string TaskId { get; set; } = string.Empty;
        internal string SessionId { get; set; } = string.Empty;
        internal string Cwd { get; set; } = string.Empty;
        internal string ProjectName { get; set; } = string.Empty;
        /// <summary>来源日志行的事件时间；TraeCode Toast 必须按它关联当前回合。</summary>
        internal DateTimeOffset? EventTimeUtc { get; set; }
        internal long DurationMs { get; set; }
        /// <summary>本轮累计 token；0 表示来源未提供可靠用量，手机端不展示。</summary>
        internal long TokenCount { get; set; }
        /// <summary>
        /// TraeCode CN 的本轮消耗积分（如 "172.69"）；空串表示积分明细尚未到达或来源不支持，
        /// 手机端不展示。DONE 事件本身不带该字段，由 cn_session_usage_tail 的 show 事件补齐。
        /// </summary>
        internal string Credits { get; set; } = string.Empty;
        /// <summary>终态事件携带的 agentMessageId；TraeCode 积分明细按它匹配挂起的完成事件。</summary>
        internal string AgentMessageId { get; set; } = string.Empty;
        /// <summary>通知卡片标题（任务名称）；空串表示由手机端按来源+状态生成默认标题。</summary>
        internal string NotificationTitle { get; set; } = string.Empty;
        /// <summary>
        /// 等待确认请求 ID（如 workbuddy:&lt;sessionId&gt;）；空串表示该等待不可远程处理。
        /// 随广播下发手机端，用于「允许 / 拒绝」按钮回传 approval.resolve。
        /// </summary>
        internal string RequestId { get; set; } = string.Empty;
        /// <summary>
        /// Codex request_user_input 的原生 function call ID。该调用可能随后收到
        /// function_call_output（例如 Default mode 下的 unavailable），只有在没有
        /// 输出回传并经过宽限期后才允许广播 waiting。
        /// </summary>
        internal string CodexInputCallId { get; set; } = string.Empty;
        internal string EventKey { get; set; } = string.Empty;
    }

    internal static class AgentCompletionEventParser
    {
        internal static bool TryParseChatGPTLine(
            string line,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var payload = root.TryGetProperty("payload", out var nestedPayload) &&
                              nestedPayload.ValueKind == JsonValueKind.Object
                    ? nestedPayload
                    : root;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }
                var rootType = ReadString(root, "type");

                // Codex rollout files interleave the task-lifecycle event stream
                // (root type "event_msg") with per-item records (root type
                // "response_item": tool calls, reasoning, assistant messages).
                // Item records carry their own per-step status (e.g. a finished
                // exec tool call has status:"completed") and are NOT task status.
                // Treating them as task completion fired a premature "浠诲姟宸插畬鎴?.
                // The one exception is Codex's host-side request_user_input
                // function call: it is the durable record for the UI question
                // shown to the user while the turn is blocked.
                if (string.Equals(rootType, "response_item", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseCodexUserInputRequest(root, payload, out result))
                    {
                        return true;
                    }

                    return false;
                }

                var eventName = FirstNonEmpty(
                    ReadString(payload, "type"),
                    ReadString(payload, "event"),
                    ReadString(payload, "event_name"),
                    ReadString(root, "event"),
                    string.Equals(rootType, "event_msg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rootType, "response", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : rootType);
                if (IsChatGPTPerItemEvent(eventName))
                {
                    // Codex emits item_completed/item_started for individual
                    // tool or response items before the task_complete record.
                    // Those lifecycle events must never close the whole turn.
                    return false;
                }
                var rawStatus = FirstNonEmpty(
                    ReadString(payload, "status"),
                    ReadString(payload, "state"),
                    ReadString(payload, "result"),
                    ReadString(root, "status"),
                    ReadString(root, "state"));
                var eventStatus = ClassifyChatGPTSignal(eventName);

                // Only trust the item-level status/state/result field when the
                // event itself is a task/turn/response-level status event. Codex
                // emits many sub-operation events (patch_apply_end,
                // exec_command_end, custom_tool_call, ...) that carry
                // status:"completed" for that single step while the task keeps
                // running. Deriving task status from those fields reported the
                // task as finished on the first completed patch/tool step.
                var statusStatus = IsChatGPTStatusCarrier(eventName)
                    ? ClassifyChatGPTSignal(rawStatus)
                    : string.Empty;
                if (eventStatus == "cancelled" || statusStatus == "cancelled")
                {
                    // User cancellation/interrupt is not a failure and should not alert as one.
                    return false;
                }

                var status = eventStatus == "failed" || statusStatus == "failed"
                    ? "failed"
                    : eventStatus == "waiting" || statusStatus == "waiting"
                        ? "waiting"
                        : eventStatus == "completed" || statusStatus == "completed"
                            ? "completed"
                            : string.Empty;
                if (string.IsNullOrWhiteSpace(status))
                {
                    return false;
                }

                // Codex emits `task_complete` for BOTH successful and failed
                // turns. A failed turn carries a populated `error` object (e.g.
                // "stream disconnected before completion" / "model is at
                // capacity") and a null `last_agent_message`. Without inspecting
                // the error, every failed turn was announced as "浠诲姟宸插畬鎴?, and
                // because the completed/failed dedupe keys differ by status the
                // real failure that followed produced no notification at all.
                if (status == "completed" && HasCodexTaskError(payload))
                {
                    status = "failed";
                }

                var sessionId = FirstNonEmpty(
                    ReadDeepString(payload, "session_id"),
                    ReadDeepString(payload, "sessionId"),
                    ReadDeepString(root, "session_id"),
                    ReadDeepString(root, "sessionId"));
                var taskToken = FirstNonEmpty(
                    ReadDeepString(payload, "turn_id"),
                    ReadDeepString(payload, "turn-id"),
                    ReadDeepString(payload, "turnId"),
                    ReadDeepString(payload, "task_id"),
                    ReadDeepString(payload, "task-id"),
                    ReadDeepString(payload, "taskId"),
                    ReadDeepString(payload, "response_id"),
                    ReadDeepString(payload, "responseId"),
                    ReadDeepString(payload, "request_id"),
                    ReadDeepString(payload, "requestId"),
                    ReadDeepString(root, "turn_id"),
                    ReadDeepString(root, "turn-id"),
                    ReadDeepString(root, "turnId"),
                    ReadDeepString(root, "task_id"),
                    ReadDeepString(root, "task-id"),
                    ReadDeepString(root, "taskId"),
                    ReadDeepString(root, "response_id"),
                    ReadDeepString(root, "responseId"),
                    ReadDeepString(root, "request_id"),
                    ReadDeepString(root, "requestId"));
                var timestamp = FirstNonEmpty(
                    ReadDeepString(payload, "timestamp"),
                    ReadDeepString(root, "timestamp"));
                var token = FirstNonEmpty(
                    taskToken,
                    timestamp,
                    HashText(line));
                var taskId = string.IsNullOrWhiteSpace(sessionId)
                    ? token
                    : sessionId + ":" + token;
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return false;
                }

                var cwd = FirstNonEmpty(
                    ReadDeepString(payload, "cwd"),
                    ReadDeepString(payload, "working_directory"),
                    ReadDeepString(payload, "workspacePath"),
                    ReadDeepString(payload, "workspace_path"),
                    ReadDeepString(root, "cwd"),
                    ReadDeepString(root, "working_directory"),
                    ReadDeepString(root, "workspacePath"),
                    ReadDeepString(root, "workspace_path"));
                result = new AgentCompletionEventData
                {
                    Message = BuildChatGPTMessage(status, payload, root),
                    TurnId = taskToken,
                    Source = "codex",
                    Status = status,
                    TaskId = taskId,
                    SessionId = sessionId,
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        FirstNonEmpty(
                            ReadDeepString(payload, "project_name"),
                            ReadDeepString(payload, "projectName"),
                            ReadDeepString(payload, "task_name"),
                            ReadDeepString(payload, "taskName"),
                            ReadDeepString(payload, "title")),
                        cwd,
                        taskId,
                        string.Empty),
                    DurationMs = ReadDeepPositiveLong(payload, "duration_ms", "durationMs"),
                    EventKey = "codex:" + status + ":" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseCodexUserInputRequest(
            JsonElement root,
            JsonElement payload,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (payload.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(payload, "type"), "function_call", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadString(payload, "name"), "request_user_input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!payload.TryGetProperty("arguments", out var rawArguments))
            {
                return false;
            }

            JsonDocument? argumentsDocument = null;
            try
            {
                JsonElement arguments;
                if (rawArguments.ValueKind == JsonValueKind.Object)
                {
                    arguments = rawArguments;
                }
                else if (rawArguments.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(rawArguments.GetString()))
                {
                    argumentsDocument = JsonDocument.Parse(rawArguments.GetString()!);
                    arguments = argumentsDocument.RootElement;
                }
                else
                {
                    return false;
                }

                var taskLog = BuildCodexUserInputTaskLog(arguments);
                if (string.IsNullOrWhiteSpace(taskLog))
                {
                    return false;
                }

                var sessionId = FirstNonEmpty(
                    ReadDeepString(payload, "session_id"),
                    ReadDeepString(payload, "sessionId"),
                    ReadDeepString(root, "session_id"),
                    ReadDeepString(root, "sessionId"));
                var turnId = FirstNonEmpty(
                    ReadDeepString(payload, "turn_id"),
                    ReadDeepString(payload, "turnId"),
                    ReadDeepString(root, "turn_id"),
                    ReadDeepString(root, "turnId"));
                // A turn may ask more than one question. The function-call id is
                // therefore the task identity; turn_id is retained only as the
                // surrounding turn metadata.
                var requestToken = FirstNonEmpty(
                    ReadString(payload, "call_id"),
                    ReadString(payload, "id"),
                    turnId,
                    HashText(taskLog));
                if (string.IsNullOrWhiteSpace(requestToken))
                {
                    return false;
                }

                var taskId = string.IsNullOrWhiteSpace(sessionId)
                    ? requestToken
                    : sessionId + ":" + requestToken;
                var cwd = FirstNonEmpty(
                    ReadDeepString(payload, "cwd"),
                    ReadDeepString(payload, "working_directory"),
                    ReadDeepString(payload, "workingDirectory"),
                    ReadDeepString(payload, "workspacePath"),
                    ReadDeepString(payload, "workspace_path"),
                    ReadDeepString(root, "cwd"),
                    ReadDeepString(root, "working_directory"),
                    ReadDeepString(root, "workingDirectory"),
                    ReadDeepString(root, "workspacePath"),
                    ReadDeepString(root, "workspace_path"));
                var projectName = FirstNonEmpty(
                    ReadDeepString(payload, "project_name"),
                    ReadDeepString(payload, "projectName"),
                    ReadDeepString(payload, "task_name"),
                    ReadDeepString(payload, "taskName"),
                    ReadDeepString(payload, "title"),
                    ReadDeepString(root, "project_name"),
                    ReadDeepString(root, "projectName"),
                    ReadDeepString(root, "task_name"),
                    ReadDeepString(root, "taskName"),
                    ReadDeepString(root, "title"));

                result = new AgentCompletionEventData
                {
                    Message = "ChatGPT 等待你的操作：" + SummarizeChatGPTDetail(taskLog),
                    TurnId = turnId,
                    TaskLog = taskLog,
                    Source = "codex",
                    Status = "waiting",
                    TaskId = taskId,
                    SessionId = sessionId,
                    CodexInputCallId = ReadString(payload, "call_id"),
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        projectName,
                        cwd,
                        taskId,
                        taskLog),
                    EventKey = "codex:waiting:" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            finally
            {
                argumentsDocument?.Dispose();
            }
        }

        /// <summary>
        /// 读取 Codex function_call 的结果记录。request_user_input 在当前 Codex
        /// Default 模式下可能先产生 function_call，随后返回
        /// "request_user_input is unavailable in Default mode"；这不是用户等待状态。
        /// </summary>
        internal static bool TryReadCodexFunctionCallOutput(string line, out string callId)
        {
            callId = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var payload = root.TryGetProperty("payload", out var nestedPayload) &&
                              nestedPayload.ValueKind == JsonValueKind.Object
                    ? nestedPayload
                    : root;
                if (!string.Equals(ReadString(root, "type"), "response_item", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(payload, "type"), "function_call_output", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                callId = FirstNonEmpty(
                    ReadString(payload, "call_id"),
                    ReadString(payload, "callId"),
                    ReadString(root, "call_id"),
                    ReadString(root, "callId"));
                return !string.IsNullOrWhiteSpace(callId);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string BuildCodexUserInputTaskLog(JsonElement arguments)
        {
            var builder = new StringBuilder();
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (arguments.TryGetProperty("questions", out var questions) &&
                questions.ValueKind == JsonValueKind.Array)
            {
                var questionNumber = 0;
                foreach (var item in questions.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var question = FirstNonEmpty(
                        ReadString(item, "question"),
                        ReadString(item, "header"));
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        continue;
                    }

                    questionNumber++;
                    AppendCodexUserInputLine(builder, questionNumber + ". " + question);
                    if (!item.TryGetProperty("options", out var options) ||
                        options.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var option in options.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var label = FirstNonEmpty(
                            ReadString(option, "label"),
                            ReadString(option, "value"));
                        var description = ReadString(option, "description");
                        var optionText = label;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            optionText = string.IsNullOrWhiteSpace(optionText)
                                ? description
                                : optionText + "（" + description + "）";
                        }

                        if (!string.IsNullOrWhiteSpace(optionText))
                        {
                            AppendCodexUserInputLine(builder, "选项：" + optionText);
                        }
                    }
                }
            }

            if (builder.Length == 0)
            {
                AppendCodexUserInputLine(
                    builder,
                    FirstNonEmpty(
                        ReadString(arguments, "question"),
                        ReadString(arguments, "header"),
                        ReadString(arguments, "prompt")));
            }

            const int maxTaskLogLength = 4000;
            return builder.Length <= maxTaskLogLength
                ? builder.ToString().Trim()
                : builder.ToString(0, maxTaskLogLength).TrimEnd() + "...";
        }

        private static void AppendCodexUserInputLine(StringBuilder builder, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.Append(value.Trim());
        }

        internal static bool TryExtractChatGPTProjectName(string line, out string projectName)
        {
            projectName = string.Empty;
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var role = ReadDeepString(root, "role");
                var type = FirstNonEmpty(ReadString(root, "type"), ReadDeepString(root, "type"));
                var isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(type, "user", StringComparison.OrdinalIgnoreCase) ||
                             type.IndexOf("user_message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             type.IndexOf("user_input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             type.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isUser) return false;

                var candidate = FirstNonEmpty(
                    ReadDeepString(root, "project_name"),
                    ReadDeepString(root, "projectName"),
                    ReadDeepString(root, "task_name"),
                    ReadDeepString(root, "taskName"),
                    ReadDeepString(root, "conversation_title"),
                    ReadDeepString(root, "thread_title"),
                    ReadDeepString(root, "workspace_name"),
                    ReadDeepString(root, "workspaceName"),
                    ReadDeepString(root, "title"));
                projectName = ProjectNameResolver.Normalize(candidate);
                return projectName.Length > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static string TryExtractChatGPTWorkingDirectory(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var recordType = ReadString(root, "type");
                if (!string.Equals(recordType, "session_meta", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(recordType, "turn_context", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return FirstNonEmpty(
                    ReadDeepString(root, "cwd"),
                    ReadDeepString(root, "working_directory"),
                    ReadDeepString(root, "workingDirectory"),
                    ReadDeepString(root, "workspacePath"),
                    ReadDeepString(root, "workspace_path"));
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static string ClassifyChatGPTSignal(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');
            if (normalized.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("abort", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("interrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "cancelled";
            }

            if (normalized.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("failure", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "failed";
            }

            if (normalized.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("awaiting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("paused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("needs_input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("need_input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("input_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("approval_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("approval_request", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("request_user_input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("selection_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("requires_action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("action_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("confirmation_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.Equals("exec_approval_request", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("apply_patch_approval_request", StringComparison.OrdinalIgnoreCase))
            {
                return "waiting";
            }

            if (normalized.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("succeed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("done", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "completed";
            }

            return string.Empty;
        }

        // A task/turn/response-level status event whose `status`/`state`/`result`
        // field describes the whole task (so it may drive completion detection),
        // as opposed to a sub-operation event (patch_apply_end, exec_command_end,
        // custom_tool_call, ...) whose status only describes that single step.
        private static bool IsChatGPTStatusCarrier(string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                // Some ChatGPT event formats carry the status at the root with no
                // explicit event name; the status field is authoritative there.
                return true;
            }

            var normalized = eventName.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');
            if (normalized.IndexOf("item", StringComparison.Ordinal) >= 0)
            {
                // e.g. response_item / output_item 鈥?per-item records, not tasks.
                return false;
            }

            return normalized.IndexOf("task", StringComparison.Ordinal) >= 0
                || normalized.IndexOf("turn", StringComparison.Ordinal) >= 0
                || normalized.IndexOf("response", StringComparison.Ordinal) >= 0
                || normalized.IndexOf("session", StringComparison.Ordinal) >= 0
                || normalized.IndexOf("status", StringComparison.Ordinal) >= 0;
        }

        private static bool IsChatGPTPerItemEvent(string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return false;
            }

            var normalized = eventName.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');
            return normalized.StartsWith("item_", StringComparison.Ordinal)
                || normalized.EndsWith("_item_completed", StringComparison.Ordinal)
                || normalized.EndsWith("_item_started", StringComparison.Ordinal)
                || normalized.EndsWith("_item_failed", StringComparison.Ordinal);
        }

        // Codex `task_complete` events describe a failed turn by populating an
        // `error` object (with a human-readable `message` and a machine
        // `codex_error_info`). A successful turn omits `error` (or sets it null).
        private static bool HasCodexTaskError(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (payload.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    foreach (var _ in error.EnumerateObject())
                    {
                        // Any property on the error object => a real failure.
                        return true;
                    }
                }
                else if (error.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    return true;
                }
            }

            return !string.IsNullOrWhiteSpace(ReadString(payload, "codex_error_info"));
        }

        private static string BuildChatGPTMessage(string status, JsonElement payload, JsonElement root)
        {
            if (status == "waiting")
            {
                var detail = FirstNonEmpty(
                    ReadDeepString(payload, "prompt"),
                    ReadDeepString(payload, "question"),
                    ReadDeepString(payload, "message"),
                    ReadDeepString(root, "prompt"),
                    ReadDeepString(root, "question"),
                    ReadDeepString(root, "message"));
                detail = SummarizeChatGPTDetail(detail);
                return string.IsNullOrWhiteSpace(detail)
                    ? "ChatGPT \u4efb\u52a1\u5df2\u6682\u505c，\u7b49\u5f85\u4f60\u786e\u8ba4\u6216\u7ee7\u7eed\u64cd\u4f5c"
                    : "ChatGPT \u7b49\u5f85\u4f60\u7684\u64cd\u4f5c：" + detail;
            }

            if (status == "failed")
            {
                var detail = FirstNonEmpty(
                    ReadDeepString(payload, "error_message"),
                    ReadDeepString(payload, "error"),
                    ReadDeepString(payload, "reason"),
                    ReadDeepString(payload, "message"),
                    ReadDeepString(payload, "detail"),
                    ReadDeepString(root, "error_message"),
                    ReadDeepString(root, "error"),
                    ReadDeepString(root, "reason"),
                    ReadDeepString(root, "message"),
                    ReadDeepString(root, "detail"));
                detail = SummarizeChatGPTDetail(detail);
                return string.IsNullOrWhiteSpace(detail)
                    ? "ChatGPT \u4efb\u52a1\u5931\u8d25"
                    : "ChatGPT \u4efb\u52a1\u5931\u8d25：" + detail;
            }

            // 瀹屾垚锛氶檮甯︽渶鍚庝竴鏉″姪鎵嬫秷鎭憳瑕侊紝涓?Claude 閫氱煡淇濇寔涓€鑷寸殑淇℃伅閲忋€?
            var summary = SummarizeChatGPTDetail(FirstNonEmpty(
                ReadDeepString(payload, "last_agent_message"),
                ReadDeepString(payload, "last_assistant_message"),
                ReadDeepString(root, "last_agent_message"),
                ReadDeepString(root, "last_assistant_message")));
            return string.IsNullOrWhiteSpace(summary)
                ? "ChatGPT \u4efb\u52a1\u5df2\u5b8c\u6210"
                : "ChatGPT \u4efb\u52a1\u5df2\u5b8c\u6210：" + summary;
        }

        private static string SummarizeChatGPTDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return string.Empty;
            }

            var normalized = string.Join(" ", detail.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 240 ? normalized : normalized.Substring(0, 240) + "...";
        }

        internal static bool TryParseTraeLine(
            string line,
            string source,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            var eventMarker = line.IndexOf("event:", StringComparison.OrdinalIgnoreCase);
            var paramsMarker = line.IndexOf("params:", StringComparison.OrdinalIgnoreCase);
            if (eventMarker < 0 || paramsMarker < 0 || paramsMarker <= eventMarker)
            {
                return false;
            }

            var eventStart = eventMarker + "event:".Length;
            var eventEnd = line.IndexOf(';', eventStart);
            if (eventEnd < 0 || eventEnd > paramsMarker)
            {
                eventEnd = paramsMarker;
            }
            var eventName = line.Substring(eventStart, eventEnd - eventStart).Trim();
            // run_script_show 鏄€屾槸鍚﹁繍琛屽懡浠ゃ€嶇‘璁ゅ崱鐗囪灞曠ず鐨勭湡瀹炲煁鐐癸紙瀹炴祴浜?
            // TraeCode CN renderer.log锛沜ode_comp_confirm 绛夌寽娴嬩簨浠跺悕浠庢湭鍑虹幇杩囷級銆?
            var isRunScriptShow = string.Equals(eventName, "run_script_show", StringComparison.OrdinalIgnoreCase);
            if (!isRunScriptShow &&
                !IsTraeCompletedEvent(eventName) &&
                !string.Equals(eventName, "code_comp_fail", StringComparison.OrdinalIgnoreCase) &&
                !IsTraeWaitingEvent(eventName))
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(line.Substring(paramsMarker + "params:".Length).Trim());
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }
                if (isRunScriptShow)
                {
                    return TryBuildTraeRunScriptEvent(root, source, line, out result);
                }
                if (IsTraeCompletedEvent(eventName) && IsTruthy(
                        ReadString(root, "is_interrupted"),
                        ReadInt(root, "is_interrupted"),
                        ReadBool(root, "is_interrupted")))
                {
                    return false;
                }

                var sessionId = ReadString(root, "session_id");
                var agentName = ReadString(root, "agent_name");
                var cwd = FirstNonEmpty(
                    ReadString(root, "repo_working_dir"),
                    ReadString(root, "workspace_path"),
                    ReadString(root, "cwd"));
                var token = FirstNonEmpty(
                    ReadString(root, "turn_id"),
                    ReadString(root, "request_id"),
                    ReadString(root, "message_id"),
                    ReadString(root, "timestamp"),
                    HashText(line));
                var taskId = string.IsNullOrWhiteSpace(sessionId) ? token : sessionId + ":" + token;
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return false;
                }

                var status = string.Equals(eventName, "code_comp_fail", StringComparison.OrdinalIgnoreCase)
                    ? "failed"
                    : IsTraeWaitingEvent(eventName) ? "waiting" : "completed";
                var message = BuildTraeMessage(status);
                result = new AgentCompletionEventData
                {
                    Message = message,
                    Source = source.Trim().ToLowerInvariant(),
                    Status = status,
                    TaskId = taskId,
                    SessionId = sessionId,
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        FirstNonEmpty(ReadString(root, "project_name"), ReadString(root, "projectName"),
                            ReadString(root, "task_name"), ReadString(root, "taskName"), ReadString(root, "title")),
                        cwd,
                        taskId,
                        agentName),
                    DurationMs = FirstPositiveLong(
                        ReadPositiveLong(root, "duration_ms"),
                        ReadPositiveLong(root, "durationMs"),
                        ReadPositiveLong(root, "duration")),
                    EventKey = source.Trim().ToLowerInvariant() + ":" + status + ":" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // run_script_show + auto_run=0 琛ㄧず Trae 寮瑰嚭浜嗐€屾槸鍚﹁繍琛屽懡浠ゃ€嶇‘璁ゅ崱鐗囥€?
        // 姝ｅ湪绛夊緟鐢ㄦ埛鐐瑰嚮锛沘uto_run=1 琛ㄧず鍛戒护琚嚜鍔ㄦ斁琛岋紝鏃犻渶浜哄伐纭锛屼笉閫氱煡銆?
        // tool_id 姣忎釜寮圭獥鍞竴锛屼綔涓?taskId 涓€閮ㄥ垎鍙伩寮€鏈嶅姟绔?2 鍒嗛挓鍘婚噸锛圥004锛夈€?
        private static bool TryBuildTraeRunScriptEvent(
            JsonElement root,
            string source,
            string line,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (IsTruthy(
                    ReadString(root, "auto_run"),
                    ReadInt(root, "auto_run"),
                    ReadBool(root, "auto_run")))
            {
                return false;
            }

            var sessionId = ReadString(root, "session_id");
            var cwd = FirstNonEmpty(
                ReadString(root, "repo_working_dir"),
                ReadString(root, "workspace_path"),
                ReadString(root, "working_directory"),
                ReadString(root, "cwd"));
            var token = FirstNonEmpty(
                ReadString(root, "tool_id"),
                ReadString(root, "block_id"),
                ReadString(root, "message_id"),
                HashText(line));
            var taskId = string.IsNullOrWhiteSpace(sessionId) ? token : sessionId + ":" + token;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            var command = SummarizeTraeDetail(FirstNonEmpty(
                ReadString(root, "hit_red_list"),
                ReadString(root, "running_command_list"),
                ReadString(root, "hit_block_list")));
            result = new AgentCompletionEventData
            {
                Message = string.IsNullOrWhiteSpace(command)
                    ? "\u7b49\u5f85\u4f60\u786e\u8ba4\u662f\u5426\u8fd0\u884c\u547d\u4ee4"
                    : "\u7b49\u5f85\u4f60\u786e\u8ba4\u662f\u5426\u8fd0\u884c\u547d\u4ee4：" + command,
                Source = source.Trim().ToLowerInvariant(),
                Status = "waiting",
                TaskId = taskId,
                SessionId = sessionId,
                Cwd = cwd,
                ProjectName = ProjectNameResolver.ResolveFolderFirst(
                    FirstNonEmpty(ReadString(root, "project_name"), ReadString(root, "projectName"),
                        ReadString(root, "task_name"), ReadString(root, "taskName"), ReadString(root, "title")),
                    cwd,
                    taskId,
                    command),
                DurationMs = FirstPositiveLong(
                    ReadPositiveLong(root, "duration_ms"),
                    ReadPositiveLong(root, "durationMs"),
                    ReadPositiveLong(root, "duration")),
                EventKey = source.Trim().ToLowerInvariant() + ":waiting:" + taskId
            };
            return true;
        }

        // TraeWork CN（原 TRAE SOLO CN）的事件行解析；来源统一为 "traework"。
        internal static bool TryParseTraeWorkLine(string line, out AgentCompletionEventData result)
        {
            return TryParseTraeWorkLine(line, "traework", out result);
        }

        // TraeCode CN（原 Trae CN）3.3.91 与 TraeWork CN 的 renderer.log 共用同一套终态事件格式：
        //   [DoneHandler] Stream done event received {"sessionId":"...","status":"completed|canceled|failed","agentMessageId":".."}
        //   [NotificationPort] Sending waiting notification: {"sessionId":"...","agentMessageId":"...","planItemId":".."}
        // 老版 Trae 的 "event: code_comp_* ; params: {...}" 格式在 TraeCode CN 3.3.91 真实日志中已不再出现
        // （2026-08-21 排查：全量真实 renderer.log 中 code_comp_complete_shown 为 0 条，历史 trae 广播全部来自合成注入）。
        // 因此两个渠道都必须走本解析器，source 由调用方按日志路径传入（trae / traework）。
        internal static bool TryParseTraeWorkLine(string line, string source, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                (line.IndexOf("[DoneHandler] Stream done event received", StringComparison.OrdinalIgnoreCase) < 0 &&
                 line.IndexOf("[NotificationPort] Sending waiting notification", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            var jsonStart = line.IndexOf('{');
            if (jsonStart < 0)
            {
                return false;
            }

            var isWaiting = line.IndexOf("[NotificationPort] Sending waiting notification", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                using var json = JsonDocument.Parse(line.Substring(jsonStart));
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }
                var sessionId = ReadString(root, "sessionId");
                var agentMessageId = ReadString(root, "agentMessageId");
                var rawStatus = ReadString(root, "status");
                var cwd = FirstNonEmpty(
                    ReadString(root, "repo_working_dir"),
                    ReadString(root, "workspace_path"),
                    ReadString(root, "working_directory"),
                    ReadString(root, "cwd"));

                string status;
                if (isWaiting)
                {
                    status = "waiting";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(rawStatus))
                    {
                        // DoneHandler 琛岀己灏?status锛堟埅鏂?寮傚父锛夛紝鏃犳硶鍒ゅ畾锛岃烦杩囥€?
                        return false;
                    }

                    var normalizedStatus = rawStatus.Trim().ToLowerInvariant();
                    if (normalizedStatus.IndexOf("cancel", StringComparison.Ordinal) >= 0 ||
                        normalizedStatus.IndexOf("abort", StringComparison.Ordinal) >= 0 ||
                        normalizedStatus.IndexOf("interrupt", StringComparison.Ordinal) >= 0 ||
                        normalizedStatus.IndexOf("stop", StringComparison.Ordinal) >= 0)
                    {
                        // 鐢ㄦ埛涓诲姩鍋滄锛屼笉浣滀负澶辫触鍛婅銆?
                        return false;
                    }

                    var isDone = normalizedStatus.IndexOf("complet", StringComparison.Ordinal) >= 0 ||
                                 normalizedStatus.IndexOf("success", StringComparison.Ordinal) >= 0 ||
                                 normalizedStatus.IndexOf("succeed", StringComparison.Ordinal) >= 0 ||
                                 normalizedStatus.IndexOf("finish", StringComparison.Ordinal) >= 0 ||
                                 normalizedStatus == "done" || normalizedStatus == "ok";
                    // "Stream done" 鏄洖鍚堢粓姝簨浠讹細闈炴垚鍔熴€侀潪鍙栨秷鐨勪换浣曠粓姝㈢姸鎬佷竴寰嬫寜澶辫触澶勭悊锛?
                    // 閬垮厤 failed/error 涔嬪鐨勫紓甯哥粓姝紙濡?timeout锛夎闈欓粯涓㈠純鑰屾紡鎶ャ€?
                    status = isDone ? "completed" : "failed";
                }

                var token = isWaiting
                    ? FirstNonEmpty(ReadString(root, "planItemId"), agentMessageId)
                    : agentMessageId;
                if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(token))
                {
                    return false;
                }

                var taskId = string.IsNullOrWhiteSpace(sessionId)
                    ? token
                    : string.IsNullOrWhiteSpace(token) ? sessionId : sessionId + ":" + token;
                var normalizedSource = (source ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalizedSource))
                {
                    normalizedSource = "traework";
                }

                result = new AgentCompletionEventData
                {
                    Message = BuildTraeMessage(status),
                    Source = normalizedSource,
                    Status = status,
                    TaskId = taskId,
                    SessionId = sessionId,
                    AgentMessageId = agentMessageId,
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        FirstNonEmpty(ReadString(root, "project_name"), ReadString(root, "projectName"),
                            ReadString(root, "task_name"), ReadString(root, "taskName"), ReadString(root, "title")),
                        cwd,
                        taskId,
                        string.Empty),
                    DurationMs = FirstPositiveLong(
                        ReadPositiveLong(root, "duration_ms"),
                        ReadPositiveLong(root, "durationMs"),
                        ReadPositiveLong(root, "duration")),
                    EventKey = normalizedSource + ":" + status + ":" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// TraeCode CN 的积分明细事件（renderer.log）：
        ///   [ai-chat][ai-chat][☕] event:  cn_session_usage_tail ; params:  {"action":"show",...,"agent_message_id":"...","usage_summary":"172.69"}
        /// DONE 事件本身不含消耗数据；该 show 事件（UI 渲染用量时触发）是日志中唯一携带
        /// usage_summary 的位置。action=request_success 的预取回执不带数值，忽略。
        /// </summary>
        internal static bool TryParseTraeUsageLine(string line, out string agentMessageId, out string usageSummary)
        {
            agentMessageId = string.Empty;
            usageSummary = string.Empty;
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("cn_session_usage_tail", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            var jsonStart = line.IndexOf('{');
            if (jsonStart < 0)
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(line.Substring(jsonStart));
                var root = json.RootElement;
                var action = ReadString(root, "action");
                if (!string.Equals(action, "show", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                agentMessageId = ReadString(root, "agent_message_id");
                usageSummary = ReadString(root, "usage_summary");
                return !string.IsNullOrWhiteSpace(agentMessageId) &&
                       !string.IsNullOrWhiteSpace(usageSummary);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsTraeCompletedEvent(string eventName)
        {
            return string.Equals(eventName, "code_comp_complete_shown", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTraeWaitingEvent(string eventName)
        {
            switch (eventName.ToLowerInvariant())
            {
                case "code_comp_waiting":
                case "code_comp_wait":
                case "code_comp_paused":
                case "code_comp_pause":
                case "code_comp_need_input":
                case "code_comp_needs_input":
                case "code_comp_requires_action":
                case "code_comp_action_required":
                case "code_comp_selection_required":
                case "code_comp_select":
                case "code_comp_confirm":
                case "code_comp_confirmation_required":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsTruthy(string text, int number, bool boolean)
        {
            return boolean || number == 1 || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "1", StringComparison.OrdinalIgnoreCase);
        }

        // TraeCode/TraeWork 无法从 renderer.log 取得正文时的解析器兜底文案。
        // TraeCode 的 Monitor 层不会使用该兜底文案广播完成通知，只有匹配到真实 Toast
        // 正文才允许发送；TraeWork 仍保留该文案以兼容既有即时状态链路。
        internal static string BuildTraeMessage(string status)
        {
            if (status == "waiting")
            {
                return "\u4efb\u52a1\u5f85\u5904\u7406\uff0c\u8bf7\u5230\u7535\u8111\u7aef\u8fdb\u884c\u5904\u7406";
            }

            if (status == "failed")
            {
                return "\u4efb\u52a1\u5931\u8d25\uff0c\u8bf7\u5230\u7535\u8111\u7aef\u67e5\u770b\u5931\u8d25\u8be6\u60c5";
            }

            return "\u4efb\u52a1\u5df2\u5b8c\u6210\uff0c\u8bf7\u5230\u7535\u8111\u7aef\u67e5\u770b\u8be6\u7ec6\u65e5\u5fd7";
        }

        private static string SummarizeTraeDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return string.Empty;
            }

            var normalized = string.Join(" ", detail.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 240 ? normalized : normalized.Substring(0, 240) + "...";
        }

        private static string HashText(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes)
            {
                builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        internal static bool TryParseZCodeCliLine(string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }
                var eventName = ReadString(root, "event");
                var normalizedEvent = (eventName ?? string.Empty).Trim().ToLowerInvariant();
                // ZCode \u7684\u56de\u5408\u751f\u547d\u5468\u671f\u4e8b\u4ef6\u7edf\u4e00\u5728 "turn." \u547d\u540d\u7a7a\u95f4\u4e0b\uff08\u672c\u673a\u5b9e\u6d4b\u6709 turn.started/turn.failed\uff09\u3002
                // \u53ea\u8ba4 turn.* \u4e8b\u4ef6\uff0c\u907f\u514d\u628a model.request.completed\u3001zcode_protocol.*.completed \u7b49
                // \u5b50\u64cd\u4f5c\u8bef\u5224\u4e3a\u56de\u5408\u5b8c\u6210\uff1b\u6210\u529f\u4e8b\u4ef6\u5728\u4e0d\u540c ZCode \u7248\u672c\u53ef\u80fd\u662f
                // turn.completed / turn.finished / turn.succeeded\uff0c\u7edf\u4e00\u6309\u5173\u952e\u5b57\u8bc6\u522b\uff0c
                // \u786e\u4fdd\u6210\u529f\u56de\u5408\u4e5f\u80fd\u901a\u77e5\uff08turn.* \u524d\u7f00\u9650\u5b9a + \u5173\u952e\u5b57\u4e0d\u4f1a\u8bef\u4f24\u5b50\u64cd\u4f5c\uff09\u3002
                if (!normalizedEvent.StartsWith("turn.", StringComparison.Ordinal))
                {
                    return false;
                }
                var isFailEvent = normalizedEvent.IndexOf("fail", StringComparison.Ordinal) >= 0 ||
                                  normalizedEvent.IndexOf("error", StringComparison.Ordinal) >= 0;
                var isDoneEvent = normalizedEvent.IndexOf("complete", StringComparison.Ordinal) >= 0 ||
                                  normalizedEvent.IndexOf("finish", StringComparison.Ordinal) >= 0 ||
                                  normalizedEvent.IndexOf("succeed", StringComparison.Ordinal) >= 0 ||
                                  normalizedEvent.IndexOf("success", StringComparison.Ordinal) >= 0 ||
                                  normalizedEvent.IndexOf("done", StringComparison.Ordinal) >= 0;
                if (!isFailEvent && !isDoneEvent)
                {
                    // \u975e\u7ec8\u6b62\u4e8b\u4ef6\uff08turn.started / turn.progress \u7b49\uff09\u4e0d\u901a\u77e5\u3002
                    return false;
                }

                var rawStatus = ReadString(root, "status");
                if (string.Equals(rawStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    // A user cancellation is not a failed task and should not generate a misleading failure alert.
                    return false;
                }

                var sessionId = ReadString(root, "sessionId");
                var turnId = ReadString(root, "turnId");
                var timestamp = ReadString(root, "timestamp");
                var taskId = FirstNonEmpty(turnId, BuildFallbackTaskId(sessionId, timestamp));
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return false;
                }

                var context = root.TryGetProperty("context", out var contextElement)
                    ? contextElement
                    : default;
                var cwd = context.ValueKind == JsonValueKind.Object
                    ? FirstNonEmpty(ReadString(context, "workspacePath"), ReadString(context, "cwd"))
                    : string.Empty;
                var failed = isFailEvent;

                result = new AgentCompletionEventData
                {
                    Message = failed ? "ZCode \u667a\u80fd\u4f53\u4efb\u52a1\u5931\u8d25" : "ZCode \u667a\u80fd\u4f53\u4efb\u52a1\u5df2\u5b8c\u6210",
                    Source = "zcode",
                    Status = failed ? "failed" : "completed",
                    TaskId = taskId,
                    SessionId = sessionId,
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        FirstNonEmpty(ReadDeepString(root, "project_name"), ReadDeepString(root, "projectName"),
                            ReadDeepString(root, "task_name"), ReadDeepString(root, "taskName"), ReadDeepString(root, "title")),
                        cwd,
                        taskId,
                        string.Empty),
                    DurationMs = ReadDeepPositiveLong(root, "duration_ms", "durationMs"),
                    EventKey = "zcode:" + (failed ? "failed" : "completed") + ":" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// ZCode AskUserQuestion 等待状态的分类结果。
        /// </summary>
        internal enum ZCodeWaitingAction
        {
            /// <summary>非 ZCode 等待相关事件，无需处理。</summary>
            None,
            /// <summary>AskUserQuestion 已开始且非自动模式，登记为待确认等待。</summary>
            Register,
            /// <summary>AskUserQuestion 已结束（自动代答或用户已作答），取消登记。</summary>
            Cancel,
            /// <summary>AskUserQuestion 处于自动模式（yolo 等）或已代答，不广播、不登记。</summary>
            Ignore
        }

        /// <summary>
        /// 从单条 ZCode CLI JSONL 行判定其是否为 AskUserQuestion 等待事件。自动模式
        /// （yolo/bypassPermissions）下 ZCode 会自行代答、毫秒级自动结束，立即广播是
        /// 误报，故这类事件归为 <see cref="ZCodeWaitingAction.Ignore"/>；需等待真人回复
        /// 的事件先归为 <see cref="ZCodeWaitingAction.Register"/>，由调用方结合宽限期复核。
        /// </summary>
        internal static ZCodeWaitingAction ClassifyZCodeWaiting(string line,
            out string toolCallId, out string sessionId, out string turnId)
        {
            toolCallId = string.Empty;
            sessionId = string.Empty;
            turnId = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                return ZCodeWaitingAction.None;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var eventName = ReadString(root, "event");
                var context = root.TryGetProperty("context", out var contextElement) &&
                              contextElement.ValueKind == JsonValueKind.Object
                    ? contextElement
                    : default;
                var toolName = ReadString(context, "toolName");
                var isAskUserQuestion =
                    string.Equals(toolName, "AskUserQuestion", StringComparison.Ordinal) ||
                    string.Equals(toolName, "AskU", StringComparison.Ordinal);
                var normalizedEvent = (eventName ?? string.Empty).Trim().ToLowerInvariant();

                if (isAskUserQuestion &&
                    (normalizedEvent == "tool.call.completed" || normalizedEvent == "tool.call.failed"))
                {
                    toolCallId = FirstNonEmpty(
                        ReadString(root, "toolCallId"),
                        ReadString(root, "tool_call_id"));
                    return string.IsNullOrWhiteSpace(toolCallId)
                        ? ZCodeWaitingAction.None
                        : ZCodeWaitingAction.Cancel;
                }

                if (normalizedEvent != "tool.call.started" &&
                    normalizedEvent != "tool.permission.resolved")
                {
                    return ZCodeWaitingAction.None;
                }

                if (!isAskUserQuestion)
                {
                    return ZCodeWaitingAction.None;
                }

                toolCallId = FirstNonEmpty(
                    ReadString(root, "toolCallId"),
                    ReadString(root, "tool_call_id"));
                sessionId = ReadString(root, "sessionId");
                turnId = ReadString(root, "turnId");
                if (string.IsNullOrWhiteSpace(toolCallId))
                {
                    return ZCodeWaitingAction.None;
                }

                // 自动模式不会弹出交互卡片，等待毫无意义，忽略。
                if (IsPermissionAutoAllowMode(
                        FirstNonEmpty(ReadString(context, "mode"), ReadString(root, "mode"))))
                {
                    return ZCodeWaitingAction.Ignore;
                }

                var decision = ReadString(context, "decision");
                if (string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(decision, "modify", StringComparison.OrdinalIgnoreCase))
                {
                    // 已由 ZCode 自动代答，不广播 waiting。
                    return ZCodeWaitingAction.Ignore;
                }

                return ZCodeWaitingAction.Register;
            }
            catch (JsonException)
            {
                return ZCodeWaitingAction.None;
            }
        }

        internal static bool TryParseDoubaoRlogLine(string line, out AgentCompletionEventData result)
        {
            // Direct callers request rendered content. The monitor uses the
            // overload below so it can wait for the WebView to paint the row.
            return TryParseDoubaoRlogLine(line, out result, true);
        }

        internal static bool TryParseDoubaoRlogLine(
            string line,
            out AgentCompletionEventData result,
            bool readRenderedContent)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("[rlog]", StringComparison.OrdinalIgnoreCase) < 0 ||
                line.IndexOf("req_id=", StringComparison.OrdinalIgnoreCase) < 0 ||
                line.IndexOf("request_log=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var reqId = ReadBetween(line, "req_id=", " ret=");
            var ret = ReadBetween(line, " ret=", " request_log=");
            var jsonStart = line.IndexOf("request_log=", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(reqId) || jsonStart < 0)
            {
                return false;
            }

            jsonStart += "request_log=".Length;
            try
            {
                using var json = JsonDocument.Parse(line.Substring(jsonStart).Trim());
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }
                var @base = root.TryGetProperty("base", out var baseElement) ? baseElement : default;
                var response = root.TryGetProperty("response", out var responseElement) ? responseElement : default;
                var originUrl = ReadString(@base, "origin_url");
                if (!IsDoubaoChatUrl(originUrl))
                {
                    return false;
                }

                var baseStatus = ReadString(@base, "status");
                var responseCode = ReadInt(response, "code");
                var responseContentType = ReadString(response, "response_content_type");
                if (IsCancellation(ret, baseStatus))
                {
                    return false;
                }

                var completed = ret.StartsWith("Ok(", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(baseStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                                responseCode >= 200 && responseCode < 300 &&
                                responseContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
                // /im/chain/single is only the fast submit/ack request in the
                // current Doubao desktop client. Its 200 JSON response arrives
                // before the model starts; treating it as terminal fires a false
                // completed notification while /chat/completion is still streaming.
                var isSuccessfulNonStreamingResponse =
                    ret.StartsWith("Ok(", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(baseStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                    responseCode >= 200 && responseCode < 300 &&
                    !responseContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
                if (isSuccessfulNonStreamingResponse)
                {
                    return false;
                }
                var status = completed ? "completed" : "failed";
                var cwd = FirstNonEmpty(
                    ReadDeepString(root, "cwd"),
                    ReadDeepString(root, "working_directory"),
                    ReadDeepString(root, "workspacePath"),
                    ReadDeepString(root, "workspace_path"));
                string taskContent = string.Empty;
                string taskLog = string.Empty;
                string sessionTitle = string.Empty;
                string activeConversationId = string.Empty;
                try
                {
                    var logRoot = System.IO.Path.Combine(
                        AgentLogPaths.GetLocalApplicationDataRoot(),
                        "Doubao", "User Data", "sdk_storage", "log");
                    taskContent = DoubaoTaskContentSource.TryReadLatestInput(logRoot);
                }
                catch (Exception)
                {
                    // 内容提取失败不阻断失败状态解析；完成状态会在监控器中重试正文。
                }

                try
                {
                    // 新版豆包窗口标题 "<会话名> - 豆包" 用于项目归属显示。
                    sessionTitle = DoubaoTaskContentSource.TryReadSessionTitle();
                }
                catch (Exception)
                {
                }

                try
                {
                    activeConversationId = DoubaoTaskContentSource.TryReadActiveConversationId();
                }
                catch (Exception)
                {
                }

                if (completed && readRenderedContent)
                {
                    try
                    {
                        // rlog 只记录网络响应元数据，不包含 SSE 正文；从桌面
                        // WebView 可访问性树读取当前会话最后一条助手正文。
                        taskLog = DoubaoTaskContentSource.TryReadAssistantMessageForInput(taskContent);
                    }
                    catch (Exception)
                    {
                    }
                }

                // The network log is only a terminal transport signal. A completed
                // event without the rendered assistant row is not a valid task log;
                // never replace it with the prompt, title, or generic status text.
                if (completed && readRenderedContent && string.IsNullOrWhiteSpace(taskLog))
                {
                    return false;
                }

                var message = completed
                    ? BuildDoubaoCompletedMessage(taskLog)
                    : "\u8c46\u5305\u4efb\u52a1\u5931\u8d25";
                result = new AgentCompletionEventData
                {
                    Message = message,
                    Source = "doubao",
                    Status = status,
                    TaskId = reqId,
                    SessionId = BuildDoubaoSessionId(activeConversationId, sessionTitle, reqId),
                    TaskLog = taskLog,
                    CorrelationText = taskContent,
                    Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(
                        FirstNonEmpty(ReadDeepString(root, "project_name"), ReadDeepString(root, "projectName"),
                            ReadDeepString(root, "task_name"), ReadDeepString(root, "taskName"), ReadDeepString(root, "title"),
                            sessionTitle, taskContent),
                        cwd,
                        reqId,
                        message),
                    DurationMs = ReadDoubaoRequestDurationMs(root),
                    EventKey = "doubao:" + status + ":" + reqId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// 豆包对话接口白名单：旧版 /chat/completion 与新版 /im/chain/single
        /// （8-23 起新版桌面端走 IM 接口，origin_url 不再含 /chat/completion）。
        /// </summary>
        private static bool IsDoubaoChatUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return url.IndexOf("/chat/completion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("/im/chain/single", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("/im/chat", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 豆包完成通知：只显示当前回合的助手正文，不以会话标题或用户输入补位。
        /// </summary>
        private static string BuildDoubaoCompletedMessage(string taskLog)
        {
            return DoubaoTaskContentSource.BuildCompletedMessage(taskLog);
        }

        private static string BuildDoubaoSessionId(
            string activeConversationId,
            string sessionTitle,
            string requestId)
        {
            if (!string.IsNullOrWhiteSpace(activeConversationId))
            {
                return activeConversationId.Trim();
            }

            // Older clients do not expose the sidebar automation id. Keep a
            // deterministic title-based id so repeated turns in one conversation
            // remain actionable; request id is only the last-resort non-empty key.
            if (!string.IsNullOrWhiteSpace(sessionTitle))
            {
                return "doubao-title:" + sessionTitle.Trim();
            }

            return "doubao-request:" + (requestId ?? string.Empty).Trim();
        }

        private static string ReadDeepString(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            return property.Value.GetString() ?? string.Empty;
                        }

                        if (property.Value.ValueKind == JsonValueKind.Number ||
                            property.Value.ValueKind == JsonValueKind.True ||
                            property.Value.ValueKind == JsonValueKind.False)
                        {
                            return property.Value.ToString();
                        }
                    }
                }

                foreach (var property in root.EnumerateObject())
                {
                    var nested = ReadDeepString(property.Value, name);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var nested = ReadDeepString(item, name);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return string.Empty;
        }

        private static int ReadDeepInt(JsonElement root, string name)
        {
            var value = ReadDeepString(root, name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
        }

        private static long ReadDoubaoRequestDurationMs(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("timing", out var timing) ||
                timing.ValueKind != JsonValueKind.Object ||
                !timing.TryGetProperty("request", out var request) ||
                request.ValueKind != JsonValueKind.Object)
            {
                return 0L;
            }

            return ReadPositiveLong(request, "duration");
        }

        private static long ReadDeepPositiveLong(JsonElement root, params string[] names)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    foreach (var name in names)
                    {
                        if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            var value = ParsePositiveLong(property.Value);
                            if (value > 0L)
                            {
                                return value;
                            }
                        }
                    }
                }

                foreach (var property in root.EnumerateObject())
                {
                    var nested = ReadDeepPositiveLong(property.Value, names);
                    if (nested > 0L)
                    {
                        return nested;
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var nested = ReadDeepPositiveLong(item, names);
                    if (nested > 0L)
                    {
                        return nested;
                    }
                }
            }

            return 0L;
        }

        private static long ReadPositiveLong(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0L;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return ParsePositiveLong(property.Value);
                }
            }

            return 0L;
        }

        private static long ParsePositiveLong(JsonElement value)
        {
            const long maxDurationMs = 30L * 24L * 60L * 60L * 1000L;
            long parsed;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetInt64(out parsed))
                {
                    parsed = (long)value.GetDouble();
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (!long.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out parsed))
                {
                    return 0L;
                }
            }
            else
            {
                return 0L;
            }

            return parsed > 0L && parsed <= maxDurationMs ? parsed : 0L;
        }

        private static long FirstPositiveLong(params long[] values)
        {
            foreach (var value in values)
            {
                if (value > 0L)
                {
                    return value;
                }
            }
            return 0L;
        }

        internal static bool TryParseZCodeDesktopLine(string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (line.IndexOf("[repoWiki]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // ZCode writes repoWiki maintenance messages containing the text "task_complete".
                // They are not agent completion events and previously caused false notifications.
                return false;
            }

            var failed = line.IndexOf("task_error", StringComparison.OrdinalIgnoreCase) >= 0;
            var completed = line.IndexOf("task_complete", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!failed && !completed)
            {
                return false;
            }

            var taskId = ReadLogToken(line, "taskId");
            var sessionId = ReadLogToken(line, "sessionId");
            var cwd = ReadDesktopWorkspace(line);
            var durationMs = 0L;
            var jsonStart = line.IndexOf('{');
            if (jsonStart >= 0)
            {
                try
                {
                    using var json = JsonDocument.Parse(line.Substring(jsonStart));
                    var root = json.RootElement;
                    if (AgentRecordClassifier.IsSubagent(root))
                    {
                        return false;
                    }
                    taskId = FirstNonEmpty(taskId, ReadString(root, "taskId"), ReadString(root, "turnId"));
                        sessionId = FirstNonEmpty(sessionId, ReadString(root, "sessionId"));
                    cwd = FirstNonEmpty(
                        cwd,
                        ReadString(root, "workspacePath"),
                        ReadString(root, "workspaceKey"),
                        ReadString(root, "cwd"));
                    durationMs = ReadDeepPositiveLong(root, "duration_ms", "durationMs");
                }
                catch (JsonException)
                {
                    // Renderer/host log prefixes and partially structured text are expected.
                }
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            var status = failed ? "failed" : "completed";
            result = new AgentCompletionEventData
            {
                Message = failed ? "ZCode \u667a\u80fd\u4f53\u4efb\u52a1\u5931\u8d25" : "ZCode \u667a\u80fd\u4f53\u4efb\u52a1\u5df2\u5b8c\u6210",
                Source = "zcode",
                Status = status,
                TaskId = taskId,
                SessionId = sessionId,
                Cwd = cwd,
                    ProjectName = ProjectNameResolver.ResolveFolderFirst(string.Empty, cwd, taskId, string.Empty),
                DurationMs = durationMs,
                EventKey = "zcode-desktop:" + status + ":" + taskId
            };
            return true;
        }

        internal static bool TryParseWorkBuddyLine(string line, out AgentCompletionEventData result)
        {
            return TryParseWorkBuddyLine(line, string.Empty, out result);
        }

        /// <summary>
        /// 解析 WorkBuddy SessionManager 的"等待用户输入"状态转换行：
        ///   [SessionManager] task state transition {"sessionId":"...","signal":"awaiting_input","from":"working","to":"pending","turnSeq":N}
        /// 该信号在每次真正需要用户确认（权限确认、AskUserQuestion、批量删除确认等）
        /// 进入 pending 状态时只记录一次；普通工具权限（Read/Bash 自动放行）不会进入
        /// pending，不会误报。waiting 事件按 sessionId+turnSeq 生成唯一 taskId，
        /// 每轮等待只广播一次，无需额外去重状态。
        /// </summary>
        internal static bool TryParseWorkBuddyAwaitingInput(string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("task state transition", StringComparison.OrdinalIgnoreCase) < 0 ||
                line.IndexOf("awaiting_input", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var json = ExtractWorkBuddyInlineJson(line);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var signal = ReadString(root, "signal");
                var from = ReadString(root, "from");
                var to = ReadString(root, "to");
                if (!string.Equals(signal, "awaiting_input", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(from, "working", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(to, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var sessionId = ReadString(root, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return false;
                }

                var turnSeq = ReadWorkBuddyValue(root, "turnSeq");
                var taskToken = string.IsNullOrWhiteSpace(turnSeq)
                    ? "awaiting"
                    : "awaiting:" + turnSeq.Trim();
                var taskId = sessionId.Trim() + ":" + taskToken;

                result = new AgentCompletionEventData
                {
                    Message = "WorkBuddy \u7b49\u5f85\u4f60\u7684\u786e\u8ba4",
                    Source = "workbuddy",
                    Status = "waiting",
                    TaskId = taskId,
                    SessionId = sessionId,
                    EventKey = "workbuddy:waiting:" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 WorkBuddy 新版（8-23 起）的等待信号行：
        ///   [SessionManager] deriveExportedPendingInputKind: returning 'permission'
        ///   {"sessionId":"...","fromRequests":"permission","fromTaskState":"permission",
        ///    "pendingPermissionCount":1,"pendingQuestionCount":0,"runState":"awaiting_input","isProcessing":true}
        /// 该行在等待期间约每 30 秒轮询打印一次（与旧格式"每轮一次"不同），
        /// 因此调用方必须按 sessionId+kind 去重、并在用户处理（恢复工作）后清除去重标记，
        /// 否则同一轮等待会重复推送、下一轮等待又无法推送。
        /// </summary>
        internal static bool TryParseWorkBuddyPendingInput(string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("deriveExportedPendingInputKind", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var json = ExtractWorkBuddyInlineJson(line);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var runState = ReadString(root, "runState");
                if (!string.Equals(runState, "awaiting_input", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var permissionCount = ReadInt(root, "pendingPermissionCount");
                var questionCount = ReadInt(root, "pendingQuestionCount");
                var sessionId = ReadString(root, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId) ||
                    (permissionCount <= 0 && questionCount <= 0))
                {
                    return false;
                }

                var kind = permissionCount > 0 ? "permission" : "question";
                var message = permissionCount > 0
                    ? "WorkBuddy \u7b49\u5f85\u4f60\u7684\u786e\u8ba4\uff08\u6743\u9650\u8bf7\u6c42\uff09"
                    : "WorkBuddy \u7b49\u5f85\u4f60\u7684\u56de\u7b54";
                var taskId = sessionId.Trim() + ":awaiting:" + kind;

                result = new AgentCompletionEventData
                {
                    Message = message,
                    Source = "workbuddy",
                    Status = "waiting",
                    TaskId = taskId,
                    SessionId = sessionId,
                    EventKey = "workbuddy:waiting-p:" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string ExtractWorkBuddyInlineJson(string line)
        {
            var start = line.IndexOf('{', StringComparison.Ordinal);
            var end = line.LastIndexOf('}');
            return start >= 0 && end > start ? line.Substring(start, end - start + 1) : string.Empty;
        }

        internal static bool TryParseWorkBuddyLine(
            string line,
            string fallbackSessionId,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (line.IndexOf("outgoingWritable.write called, message=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return TryParseWorkBuddyResultLine(line, fallbackSessionId, out result);
            }

            var messageJson = ExtractWorkBuddyMessageJson(line);
            if (string.IsNullOrWhiteSpace(messageJson))
            {
                return TryParseWorkBuddyTruncatedQuestionToolLine(line, out result);
            }

            try
            {
                using var json = JsonDocument.Parse(messageJson);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var method = ReadString(root, "method");
                var parameters = GetWorkBuddyObject(root, "params");
                if (IsWorkBuddyWaitingMethod(method))
                {
                    return BuildWorkBuddyWaitingEvent(root, parameters, line, out result);
                }

                if (!string.Equals(method, "session/update", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "sessionUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var update = GetWorkBuddyObject(parameters, "update");
                if (update.ValueKind != JsonValueKind.Object)
                {
                    update = parameters;
                }

                var updateType = ReadString(update, "sessionUpdate");
                if (string.Equals(updateType, "tool_call", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildWorkBuddyQuestionToolEvent(root, parameters, update, line, out result);
                }

                if (!string.Equals(updateType, "session_end", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var stopReason = ReadWorkBuddyValue(update, "stopReason");
                if (string.IsNullOrWhiteSpace(stopReason) || IsWorkBuddyCancellation(stopReason))
                {
                    return false;
                }

                var status = string.Equals(
                    stopReason.Trim().Replace('-', '_'),
                    "end_turn",
                    StringComparison.OrdinalIgnoreCase)
                    ? "completed"
                    : "failed";
                if (status == "completed" &&
                    (HasWorkBuddyError(update) || HasWorkBuddyError(parameters) || HasWorkBuddyError(root)))
                {
                    status = "failed";
                }

                var sessionId = ReadWorkBuddySessionId(root, parameters, update);
                var taskToken = FirstNonEmpty(
                    ReadWorkBuddyRequestId(update),
                    ReadWorkBuddyRequestId(parameters),
                    ReadWorkBuddyRequestId(root),
                    ReadDeepString(update, "userMessageId"),
                    ReadDeepString(parameters, "userMessageId"));
                var taskId = BuildWorkBuddyTaskId(sessionId, taskToken, line, messageJson);
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    return false;
                }

                var detail = FirstNonEmpty(
                    ReadDeepString(update, "error"),
                    ReadDeepString(update, "reason"),
                    ReadDeepString(parameters, "error"),
                    ReadDeepString(root, "error"));
                detail = SummarizeWorkBuddyDetail(detail);
                result = new AgentCompletionEventData
                {
                    Message = status == "completed"
                        ? "WorkBuddy \u4efb\u52a1\u5df2\u5b8c\u6210"
                        : "WorkBuddy \u4efb\u52a1\u5931\u8d25" +
                          (string.IsNullOrWhiteSpace(detail) ? "" : "\uff1a" + detail),
                    Source = "workbuddy",
                    Status = status,
                    TaskId = taskId,
                    SessionId = sessionId,
                    Cwd = FirstNonEmpty(
                        ReadDeepString(update, "cwd"),
                        ReadDeepString(parameters, "cwd"),
                        ReadDeepString(root, "cwd"),
                        ReadDeepString(root, "workingDirectory"),
                        ReadDeepString(root, "workspacePath")),
                    ProjectName = FirstNonEmpty(
                        ReadDeepString(update, "projectName"),
                        ReadDeepString(parameters, "projectName"),
                        ReadDeepString(root, "projectName"),
                        ReadDeepString(root, "workspaceName")),
                    DurationMs = ReadDeepPositiveLong(update, "durationMs", "duration_ms"),
                    EventKey = "workbuddy:" + status + ":" + taskId
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static bool IsWorkBuddyFailureMarker(string line)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("[ACP Agent] RESULT:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var hasError = line.IndexOf("hasError=true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           line.IndexOf("hasError: true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           line.IndexOf("\"hasError\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasError && !ReadWorkBuddyResultFlag(line, "isFinal");
        }

        private static bool TryParseWorkBuddyTruncatedQuestionToolLine(
            string line,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (line.IndexOf("\"sessionUpdate\":\"tool_call\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var rawTitle = ReadWorkBuddyRawString(line, "title");
            if (!IsWorkBuddyQuestionToolName(rawTitle, true))
            {
                return false;
            }

            var sessionId = FirstNonEmpty(
                ReadWorkBuddyRawString(line, "sessionId"),
                ReadWorkBuddyRawString(line, "session_id"));
            var taskToken = FirstNonEmpty(
                ReadWorkBuddyRawString(line, "toolCallId"),
                ReadWorkBuddyRawString(line, "tool_call_id"));
            var taskId = BuildWorkBuddyTaskId(sessionId, taskToken, line, string.Empty);
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            result = new AgentCompletionEventData
            {
                Message = "WorkBuddy \u7b49\u5f85\u4f60\u7684\u64cd\u4f5c",
                Source = "workbuddy",
                Status = "waiting",
                TaskId = taskId,
                SessionId = sessionId,
                EventKey = "workbuddy:waiting:" + taskId
            };
            return true;
        }

        private static bool BuildWorkBuddyWaitingEvent(
            JsonElement root,
            JsonElement parameters,
            string line,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            var sessionId = ReadWorkBuddySessionId(root, parameters, default);
            var taskToken = FirstNonEmpty(
                ReadDeepString(parameters, "requestId"),
                ReadDeepString(parameters, "toolCallId"),
                ReadDeepString(parameters, "elicitationId"),
                ReadWorkBuddyRequestId(parameters),
                ReadWorkBuddyValue(root, "id"),
                ReadWorkBuddyRequestId(root));
            var taskId = BuildWorkBuddyTaskId(sessionId, taskToken, line, string.Empty);
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            var detail = FirstNonEmpty(
                ReadDeepString(parameters, "title"),
                ReadDeepString(parameters, "question"),
                ReadDeepString(parameters, "prompt"),
                ReadDeepString(parameters, "message"),
                ReadDeepString(parameters, "description"),
                ReadDeepString(parameters, "toolCallId"));
            detail = SummarizeWorkBuddyDetail(detail);
            result = new AgentCompletionEventData
            {
                Message = "WorkBuddy \u7b49\u5f85\u4f60\u7684\u64cd\u4f5c" +
                          (string.IsNullOrWhiteSpace(detail) ? "" : "\uff1a" + detail),
                Source = "workbuddy",
                Status = "waiting",
                TaskId = taskId,
                SessionId = sessionId,
                Cwd = FirstNonEmpty(
                    ReadDeepString(parameters, "cwd"),
                    ReadDeepString(root, "cwd"),
                    ReadDeepString(root, "workspacePath")),
                ProjectName = FirstNonEmpty(
                    ReadDeepString(parameters, "projectName"),
                    ReadDeepString(root, "projectName"),
                    ReadDeepString(root, "workspaceName")),
                EventKey = "workbuddy:waiting:" + taskId
            };
            return true;
        }

        private static bool BuildWorkBuddyQuestionToolEvent(
            JsonElement root,
            JsonElement parameters,
            JsonElement update,
            string line,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            var toolName = FirstNonEmpty(
                ReadDeepString(update, "name"),
                ReadDeepString(update, "title"),
                ReadWorkBuddyMeta(update, "codebuddy.ai/toolName"),
                ReadDeepString(update, "toolName"));
            if (!IsWorkBuddyQuestionToolName(toolName, false))
            {
                return false;
            }

            var toolStatus = ReadWorkBuddyValue(update, "status");
            if (!string.IsNullOrWhiteSpace(toolStatus) &&
                !IsWorkBuddyPendingToolStatus(toolStatus))
            {
                return false;
            }

            var sessionId = ReadWorkBuddySessionId(root, parameters, update);
            var taskToken = FirstNonEmpty(
                ReadDeepString(update, "toolCallId"),
                ReadDeepString(parameters, "toolCallId"),
                ReadWorkBuddyValue(root, "id"));
            var taskId = BuildWorkBuddyTaskId(sessionId, taskToken, line, string.Empty);
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            var detail = FirstNonEmpty(
                ReadDeepString(update, "question"),
                ReadDeepString(update, "prompt"),
                ReadDeepString(update, "title"),
                ReadDeepString(update, "message"),
                ReadDeepString(update, "toolCallId"));
            result = new AgentCompletionEventData
            {
                Message = "WorkBuddy \u7b49\u5f85\u4f60\u7684\u64cd\u4f5c" +
                          (string.IsNullOrWhiteSpace(detail) ? "" : "\uff1a" + SummarizeWorkBuddyDetail(detail)),
                Source = "workbuddy",
                Status = "waiting",
                TaskId = taskId,
                SessionId = sessionId,
                Cwd = FirstNonEmpty(
                    ReadDeepString(update, "cwd"),
                    ReadDeepString(parameters, "cwd"),
                    ReadDeepString(root, "cwd")),
                ProjectName = FirstNonEmpty(
                    ReadDeepString(update, "projectName"),
                    ReadDeepString(parameters, "projectName"),
                    ReadDeepString(root, "projectName")),
                EventKey = "workbuddy:waiting:" + taskId
            };
            return true;
        }

        private static bool IsWorkBuddyQuestionToolName(string toolName, bool allowTruncated)
        {
            var normalized = (toolName ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(".", string.Empty)
                .Replace("/", string.Empty);
            if (normalized == "askuserquestion" ||
                normalized == "askfollowupquestion" ||
                normalized == "picklocation")
            {
                return true;
            }

            // WorkBuddy truncates some ACP log lines mid-title (for example,
            // AskUserQuestion can be recorded only as "AskU").
            return allowTruncated && normalized == "asku";
        }

        private static bool IsWorkBuddyWaitingMethod(string method)
        {
            switch ((method ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "session/request_permission":
                case "session/requestpermission":
                case "request/permission":
                case "requestpermission":
                case "_codebuddy.ai/question":
                case "question/request":
                case "input_request":
                case "request_user_input":
                case "elicitation/create":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWorkBuddyPendingToolStatus(string status)
        {
            var normalized = (status ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
            return normalized == "pending" ||
                   normalized == "idle" ||
                   normalized == "executing" ||
                   normalized == "stream_executing" ||
                   normalized == "full_executing" ||
                   normalized == "running" ||
                   normalized == "in_progress" ||
                   normalized == "inprogress" ||
                   normalized == "started" ||
                   normalized == "active" ||
                   normalized == "waiting" ||
                   normalized == "awaiting_user" ||
                   normalized == "awaitinguser" ||
                   normalized == "requires_action" ||
                   normalized == "requiresaction";
        }

        private static bool IsWorkBuddyCancellation(string stopReason)
        {
            var normalized = (stopReason ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.IndexOf("cancel", StringComparison.Ordinal) >= 0 ||
                   normalized.IndexOf("abort", StringComparison.Ordinal) >= 0 ||
                   normalized.IndexOf("interrupt", StringComparison.Ordinal) >= 0;
        }

        private static bool HasWorkBuddyError(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasError = ReadWorkBuddyValue(root, "hasError");
            if (string.Equals(hasError, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var isError = ReadWorkBuddyValue(root, "isError");
            if (string.Equals(isError, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.Object ||
                       (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()));
            }

            return false;
        }

        /// <summary>
        /// 从 WorkBuddy ACP 日志行提取最近一次模型请求的 rootRequestId/requestId
        /// （形如 "Credit received: rootRequestId=bfda69014f0b42ea9c2fe312f2ba3b35, ..."、
        /// "[AcpUsagePublisher] ... requestId=bfda69..."），用于按请求精确匹配
        /// session_usage.credit_json 中的单请求积分。要求值形如 32 位十六进制或 UUID。
        /// </summary>
        internal static string ReadWorkBuddyRequestIdFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            foreach (var marker in new[] { "rootRequestId=", "requestId=" })
            {
                var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var start = index + marker.Length;
                var end = start;
                while (end < line.Length &&
                       (char.IsLetterOrDigit(line[end]) || line[end] == '-'))
                {
                    end++;
                }

                var value = end > start ? line.Substring(start, end - start) : string.Empty;
                if (IsPlausibleRequestId(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static bool IsPlausibleRequestId(string value)
        {
            if (value.Length == 32)
            {
                return IsHex(value);
            }

            // UUID 形如 8-4-4-4-12，共 36 字符。
            if (value.Length == 36)
            {
                return value[8] == '-' && value[13] == '-' &&
                       value[18] == '-' && value[23] == '-' &&
                       IsHex(value.Replace("-", string.Empty));
            }

            return false;
        }

        private static bool IsHex(string value)
        {
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        internal static string ReadWorkBuddyLogSessionId(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            var sessionId = FirstNonEmpty(
                ReadWorkBuddyRawString(line, "sessionId"),
                ReadWorkBuddyRawString(line, "session_id"));
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return sessionId;
            }

            return FirstNonEmpty(
                ReadWorkBuddyLogAssignment(line, "sessionId"),
                ReadWorkBuddyLogAssignment(line, "session_id"));
        }

        private static bool TryParseWorkBuddyResultLine(
            string line,
            string fallbackSessionId,
            out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (line.IndexOf("[ACP Agent] RESULT:", StringComparison.OrdinalIgnoreCase) < 0 ||
                line.IndexOf("skipping replayed result", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var isFinal = ReadWorkBuddyResultFlag(line, "isFinal");
            var hasError = ReadWorkBuddyResultFlag(line, "hasError");
            var isSuccessful = line.IndexOf("completed successfully", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               line.IndexOf("stopReason=end_turn", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((!isFinal || !hasError) && !isSuccessful)
            {
                return false;
            }

            if (line.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("aborted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("interrupted", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var sessionId = FirstNonEmpty(
                ReadWorkBuddyLogSessionId(line),
                fallbackSessionId);
            var status = hasError && !isSuccessful ? "failed" : "completed";
            // 与 executeStopHooks / Prompt completed 路径统一：sessionId 存在时直接以
            // sessionId 作为 taskId，使同一会话的多路结束信号收敛为同一条去重主键。
            var taskId = string.IsNullOrWhiteSpace(sessionId)
                ? BuildWorkBuddyTaskId(string.Empty, string.Empty, line, string.Empty)
                : sessionId.Trim();
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return false;
            }

            result = new AgentCompletionEventData
            {
                Message = status == "completed" ? "WorkBuddy \u4efb\u52a1\u5df2\u5b8c\u6210" : "WorkBuddy \u4efb\u52a1\u5931\u8d25",
                Source = "workbuddy",
                Status = status,
                TaskId = taskId,
                SessionId = sessionId,
                EventKey = "workbuddy:" + status + ":" + taskId
            };
            return true;
        }

        /// <summary>
        /// 读取 WorkBuddy 的 executeStopHooks 诊断行：
        ///   [SessionHookManager] executeStopHooks sessionId=... stopReason=completed
        ///   [SessionHookManager] executeStopHooks sessionId=... stopReason=cancelled
        /// executeStopHooks 在普通模型回合、上下文压缩和自动重试时都会打印；即使
        /// stopReason=completed，也只表示当前运行段结束，不能作为任务完成通知依据。
        /// 该解析方法保留给诊断/兼容调用，生产通知路径不得直接分发其结果。
        /// </summary>
        internal static bool TryParseWorkBuddyStopHookLine(
            string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("executeStopHooks", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var sessionId = ReadWorkBuddyLogAssignment(line, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var stopReason = ReadWorkBuddyLogAssignment(line, "stopReason");
            var status = WorkBuddyStatusFromStopReason(stopReason);
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            result = new AgentCompletionEventData
            {
                Message = status == "completed"
                    ? "WorkBuddy \u4efb\u52a1\u5df2\u5b8c\u6210"
                    : "WorkBuddy \u4efb\u52a1\u5931\u8d25",
                Source = "workbuddy",
                Status = status,
                TaskId = sessionId.Trim(),
                SessionId = sessionId.Trim(),
                EventKey = "workbuddy:" + status + ":" + sessionId.Trim()
            };
            return true;
        }

        /// <summary>
        /// 解析 WorkBuddy 新版（含 2026-09-24 起）的会话级结束信号：
        ///   [AcpView]  Prompt completed for session ..., stopReason: end_turn, finishReason: stop
        ///   [AcpView]  Prompt completed for session ..., stopReason: cancelled, finishReason: ...
        /// Prompt completed 在 AcpView 收到会话终态时打印一次，携带真实 sessionId 与
        /// 终态 stopReason。stopReason=end_turn/stop 表示成功；cancelled/refusal/aborted
        /// 表示取消/拒绝/中断。
        /// </summary>
        internal static bool TryParseWorkBuddyPromptCompletedLine(
            string line, out AgentCompletionEventData result)
        {
            result = new AgentCompletionEventData();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("Prompt completed for session", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var sessionId = ReadWorkBuddyPromptCompletedSessionId(line);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var stopReason = ReadWorkBuddyLogAssignment(line, "stopReason");
            var status = WorkBuddyStatusFromStopReason(stopReason);
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            result = new AgentCompletionEventData
            {
                Message = status == "completed"
                    ? "WorkBuddy \u4efb\u52a1\u5df2\u5b8c\u6210"
                    : "WorkBuddy \u4efb\u52a1\u5931\u8d25",
                Source = "workbuddy",
                Status = status,
                TaskId = sessionId.Trim(),
                SessionId = sessionId.Trim(),
                EventKey = "workbuddy:" + status + ":" + sessionId.Trim()
            };
            return true;
        }

        internal static string ReadWorkBuddyPromptCompletedSessionId(string line)
        {
            const string marker = "for session ";
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            var start = index + marker.Length;
            var end = start;
            while (end < line.Length &&
                   line[end] != ',' && line[end] != ' ' && line[end] != ')' && line[end] != ']')
            {
                end++;
            }

            return end > start ? line.Substring(start, end - start).Trim() : string.Empty;
        }

        /// <summary>
        /// 将 WorkBuddy 的 stopReason 归一到三态：
        ///   "completed" —— 会话正常结束（end_turn / completed / stop / finished / max_turns / success）；
        ///   "failed"    —— 会话确实以错误结束（failed / error 及其常见变体）；
        ///   ""（空）    —— 非任务失败的「中间/用户态」，绝不广播为失败、也不广播为完成：
        ///                 refusal=模型拒答（会话会继续）、interrupt*=被打断（会话继续）、
        ///                 cancel*/abort*=用户取消/中止、pending/paused=等待。
        /// 旧实现把 refusal/cancel/abort/interrupt 误归为 "failed"、又把真正的 "failed"/"error"
        /// 漏归成空，导致「模型拒答」被当成任务失败、而真正的失败信号被丢弃
        /// （见 09-24 世界杯会话 b91b6456 在 09:14:15 被误广播为 failed）。
        /// </summary>
        private static string WorkBuddyStatusFromStopReason(string stopReason)
        {
            var normalized = (stopReason ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            // 终态·成功：模型正常结束本轮
            if (string.Equals(normalized, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "end_turn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "stop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "finished", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "max_turns", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "success", StringComparison.OrdinalIgnoreCase))
            {
                return "completed";
            }

            // 终态·失败：会话确实以错误结束
            if (string.Equals(normalized, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "error", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("error", StringComparison.Ordinal) ||
                normalized.IndexOf("api_error", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("expired", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("timeout", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("context_window_exceeded", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("rate_limited", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("capacity", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("authentication", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("permission_denied", StringComparison.Ordinal) >= 0 ||
                normalized.IndexOf("invalid_request", StringComparison.Ordinal) >= 0)
            {
                return "failed";
            }

            // 以下为「非任务失败」：拒答/打断/取消/中止/等待，绝不广播为失败。
            return string.Empty;
        }

        private static bool ReadWorkBuddyResultFlag(string line, string name)
        {
            var marker = name + "=";
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                marker = name + ":";
                index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            }
            if (index < 0)
            {
                marker = "\"" + name + "\":";
                index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            }
            if (index < 0)
            {
                return false;
            }

            var value = line.Substring(index + marker.Length).TrimStart();
            return value.StartsWith("true", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadWorkBuddyLogAssignment(string line, string name)
        {
            var marker = name + "=";
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                marker = name + ":";
                index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            }
            if (index < 0)
            {
                return string.Empty;
            }

            var start = index + marker.Length;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            var end = start;
            while (end < line.Length &&
                   !char.IsWhiteSpace(line[end]) &&
                   line[end] != ',' &&
                   line[end] != '|' &&
                   line[end] != '}' &&
                   line[end] != ']' &&
                   line[end] != '"')
            {
                end++;
            }

            return end > start ? line.Substring(start, end - start).Trim() : string.Empty;
        }

        private static JsonElement GetWorkBuddyObject(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Object
                ? value
                : default;
        }

        private static string ReadWorkBuddySessionId(
            JsonElement root,
            JsonElement parameters,
            JsonElement update)
        {
            return FirstNonEmpty(
                ReadDeepString(parameters, "sessionId"),
                ReadDeepString(update, "sessionId"),
                ReadDeepString(root, "sessionId"),
                ReadDeepString(parameters, "session_id"),
                ReadDeepString(root, "session_id"));
        }

        private static string ReadWorkBuddyRequestId(JsonElement root)
        {
            return FirstNonEmpty(
                ReadWorkBuddyMeta(root, "codebuddy.ai/requestId"),
                ReadDeepString(root, "codebuddy.ai/requestId"),
                ReadDeepString(root, "requestId"),
                ReadDeepString(root, "request_id"));
        }

        private static string ReadWorkBuddyMeta(JsonElement root, string name)
        {
            var meta = GetWorkBuddyObject(root, "_meta");
            var direct = ReadWorkBuddyValue(meta, name);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            const string prefix = "codebuddy.ai/";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var codebuddy = GetWorkBuddyObject(meta, "codebuddy.ai");
                return ReadWorkBuddyValue(codebuddy, name.Substring(prefix.Length));
            }

            return string.Empty;
        }

        private static string ReadWorkBuddyValue(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind == JsonValueKind.String ||
                   value.ValueKind == JsonValueKind.Number ||
                   value.ValueKind == JsonValueKind.True ||
                   value.ValueKind == JsonValueKind.False
                ? value.ToString()
                : string.Empty;
        }

        private static string BuildWorkBuddyTaskId(
            string sessionId,
            string taskToken,
            string line,
            string messageJson)
        {
            var token = FirstNonEmpty(
                taskToken,
                ReadWorkBuddyLogTimestamp(line),
                HashText(string.IsNullOrWhiteSpace(messageJson) ? line : messageJson));
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(sessionId)
                ? token
                : sessionId.Trim() + ":" + token;
        }

        internal static string ReadWorkBuddyLogTimestamp(string line)
        {
            var value = line ?? string.Empty;
            var start = value.IndexOf('[', StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            var end = value.IndexOf(']', start + 1);
            return end > start + 1 ? value.Substring(start + 1, end - start - 1).Trim() : string.Empty;
        }

        private static string ExtractWorkBuddyMessageJson(string line)
        {
            const string marker = "outgoingWritable.write called, message=";
            var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var start = line.IndexOf('{', markerIndex + marker.Length);
            if (start < 0)
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < line.Length; index++)
            {
                var character = line[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '{')
                {
                    depth++;
                }
                else if (character == '}' && --depth == 0)
                {
                    return line.Substring(start, index - start + 1);
                }
            }

            return string.Empty;
        }

        private static string ReadWorkBuddyRawString(string line, string name)
        {
            var marker = "\"" + name + "\":\"";
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var builder = new StringBuilder();
            var escaped = false;
            for (var index = start; index < line.Length; index++)
            {
                var character = line[index];
                if (escaped)
                {
                    builder.Append(character);
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    break;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }

        private static string SummarizeWorkBuddyDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return string.Empty;
            }

            var normalized = string.Join(
                " ",
                detail.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 240 ? normalized : normalized.Substring(0, 240) + "...";
        }

        private static string BuildFallbackTaskId(string sessionId, string timestamp)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return timestamp;
            }

            return string.IsNullOrWhiteSpace(timestamp) ? sessionId : sessionId + ":" + timestamp;
        }

        private static string ReadBetween(string line, string startMarker, string endMarker)
        {
            var start = line.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += startMarker.Length;
            var end = line.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
            return (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
        }

        private static bool IsCancellation(string ret, string baseStatus)
        {
            return ret.IndexOf("request canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ret.IndexOf("request cancelled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ret.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ret.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(baseStatus, "IO_PENDING", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadLogToken(string line, string fieldName)
        {
            var marker = fieldName + "=";
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var end = line.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }, start);
            return (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
        }

        private static string ReadDesktopWorkspace(string line)
        {
            const string marker = "workspace=";
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var end = line.IndexOf(" taskId=", start, StringComparison.OrdinalIgnoreCase);
            return (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
        }

        /// <summary>
        /// 从 Claude 转录的 assistant 行中取出 <c>AskUserQuestion</c> 工具调用。这是
        /// “Claude 需要你的输入”交互卡片在转录里的唯一证据：一行 <c>stop_reason=tool_use</c>
        /// 的 assistant 记录，内含 <c>tool_use</c> 块。工具调用 id 天然唯一
        /// （<c>call_...</c> 或 <c>AskUserQuestion-&lt;ts&gt;-&lt;n&gt;</c>），
        /// 直接当 taskId 即可去重，不必像 Hook 那样退化成挂钟时间。
        /// </summary>
        internal static bool TryGetAskUserQuestionPrompt(JsonElement root, out string toolUseId, out string question)
        {
            toolUseId = string.Empty;
            question = string.Empty;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(block, "type"), "tool_use", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(block, "name"), "AskUserQuestion", StringComparison.Ordinal))
                {
                    continue;
                }

                toolUseId = ReadString(block, "id");
                if (string.IsNullOrWhiteSpace(toolUseId))
                {
                    continue;
                }

                question = ExtractAskUserQuestionText(block);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 读取转录行上的权限模式。<c>session-meta</c> 与真人 user 行都会带
        /// <c>permissionMode</c>（Haha 也会写 <c>permission_mode</c>）。
        /// </summary>
        internal static bool TryReadPermissionMode(JsonElement root, out string mode)
        {
            mode = string.Empty;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            mode = FirstNonEmpty(
                ReadString(root, "permissionMode"),
                ReadString(root, "permission_mode"));
            return !string.IsNullOrWhiteSpace(mode);
        }

        /// <summary>
        /// Haha/Claude 在这些模式下不会弹出“允许执行此命令?”审批卡：工具直接执行。
        /// 此时不能把“敏感工具已发出、结果尚未回传”推断为 waiting。
        /// </summary>
        internal static bool IsPermissionAutoAllowMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return false;
            }

            var normalized = mode.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
            return string.Equals(normalized, "bypassPermissions", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "dontAsk", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yolo", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从 Haha 转录的 assistant 行中取出“等审批”形态的工具调用：<c>stop_reason=tool_use</c>
        /// 且工具名属于权限敏感集合（Bash / Write / Edit / MultiEdit / Copy / Delete / WebFetch
        /// / WebSearch / Computer / ComputerUse / Screenshot / MCP 工具）。这些工具在
        /// “询问权限”模式下会弹出审批卡片阻塞用户，等价于 AskUserQuestion 的 waiting。
        /// 判定是否“真的在等”由调用方结合权限模式与后续 tool_result 完成（见
        /// <see cref="AgentCompletionMonitor"/> 的 pending 判定）。
        /// </summary>
        internal static bool TryGetPendingPermissionPrompt(JsonElement root, out string toolUseId, out string toolName, out string command)
        {
            toolUseId = string.Empty;
            toolName = string.Empty;
            command = string.Empty;
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(root, "type"), "assistant", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(message, "stop_reason"), "tool_use", StringComparison.OrdinalIgnoreCase) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(block, "type"), "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ReadString(block, "name");
                if (!IsPermissionSensitiveToolName(name))
                {
                    continue;
                }

                toolUseId = ReadString(block, "id");
                if (string.IsNullOrWhiteSpace(toolUseId))
                {
                    continue;
                }

                toolName = name;
                command = ExtractCommandPreview(block);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 读取 assistant 行里 tool_use.input.timeout（毫秒）。Haha Bash 长命令会带
        /// 这个字段；没有声明时返回 0。
        /// </summary>
        internal static long ReadToolUseTimeoutMs(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return 0L;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(block, "type"), "tool_use", StringComparison.OrdinalIgnoreCase) ||
                    !block.TryGetProperty("input", out var input) ||
                    input.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!input.TryGetProperty("timeout", out var timeout))
                {
                    continue;
                }

                if (timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt64(out var number) && number > 0L)
                {
                    return number;
                }

                if (timeout.ValueKind == JsonValueKind.String &&
                    long.TryParse(timeout.GetString(), out number) &&
                    number > 0L)
                {
                    return number;
                }
            }

            return 0L;
        }

        /// <summary>
        /// 收集一行 user 记录里的 tool_result 对应的 tool_use id（已回传结果）。
        /// </summary>
        internal static HashSet<string> CollectToolResultIds(JsonElement root)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in EnumerateToolBlocks(root))
            {
                if (string.Equals(ReadString(block, "type"), "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    var id = ReadString(block, "tool_use_id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        private static IEnumerable<JsonElement> EnumerateToolBlocks(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object)
                {
                    yield return block;
                }
            }
        }

        /// <summary>
        /// 权限敏感工具集合：在“询问权限”模式下会先弹审批卡片、由用户允许后才真正
        /// 执行的工具。Read / Grep / Glob / TaskCreate 等只读或纯内部工具不在此列，
        /// 避免自动执行时被误判为等待。
        /// 比较统一使用 OrdinalIgnoreCase：不同 Agent（Claude/PascalCase、DSH/lowercase）
        /// 的工具名大小写不一致，但语义相同。
        /// </summary>
        internal static bool IsPermissionSensitiveToolName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalized = name.Trim();
            if (normalized.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 使用 OrdinalIgnoreCase 比较，兼容 Claude（PascalCase: Bash/Write/Edit）
            // 与 DSH/其他 Agent（lowercase: bash/write/edit）的命名差异。
            if (string.Equals(normalized, "Bash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "PowerShell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Write", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Edit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "MultiEdit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "NotebookEdit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Copy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "CopyFile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Rename", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Delete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Rm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Mkdir", StringComparison.OrdinalIgnoreCase) ||
                // ExitPlanMode 是「请你审批这份方案」，阻塞在用户身上，语义等价于权限审批卡。
                // 实测真实等待 3.7s / 4.0s / 20.2s（用户在电脑前时很快答完，宽限期会正确保持静默）；
                // 用户离开时此前完全没有通知资格，这才是漏报的根因。
                // EnterPlanMode 实测恒为 0.0s（进入计划模式由系统自动确认，不阻塞），故不纳入。
                string.Equals(normalized, "ExitPlanMode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "WebFetch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "WebSearch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "WebNavigate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Computer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ComputerUse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Screenshot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ImageGen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ImageTool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Task", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从 tool_use 块里提取可读的命令 / 参数摘要，用于 waiting 通知正文。
        /// Bash 取 command，Write/Edit 取 file_path，其余工具取 input 的首个标量值。
        /// </summary>
        private static string ExtractCommandPreview(JsonElement block)
        {
            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var field in new[] { "command", "file_path", "path", "url", "prompt", "description", "tool" })
            {
                if (input.TryGetProperty(field, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }

            foreach (var property in input.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var text = property.Value.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return property.Name + ": " + text.Trim();
                    }
                }

                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    return property.Name + ": " + property.Value.GetRawText();
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Haha 的 <c>cc-haha-task-notification</c> 只为后台 Bash / 子代理写入，不是用户主任务终态。
        /// 摘要形如 <c>Background command "…"</c>、<c>Agent "…"</c>，或直接是子代理的
        /// 任务描述（例如 <c>探索 PC 端 UI 布局细节</c>、<c>Map backend AI services</c>）。
        /// 主任务完成走交互 <c>assistant.message.stop_reason=end_turn</c>。
        /// 该函数目前仅用于测试与文档对照；生产路径 <c>ProcessHahaTaskNotification</c>
        /// 已直接丢弃全部 <c>cc-haha-task-notification</c> 事件。
        /// </summary>
        internal static bool IsHahaBackgroundSubtaskSummary(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary))
            {
                return false;
            }

            var normalized = summary.Trim();
            if (normalized.StartsWith("Background command ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Background command\"", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Agent "Audit LibraryView UI/UX" completed / failed
            if (normalized.Length >= 7 &&
                normalized.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase))
            {
                var afterAgent = normalized.Substring(6).TrimStart();
                if (afterAgent.Length > 0 &&
                    (afterAgent[0] == '"' || afterAgent[0] == '\u201C' || afterAgent[0] == '\''))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 识别 Claude/Haha 转录里的“真人用户提示”，用于计算交互轮次耗时。
        /// 仅含 <c>tool_result</c> 的 user 行是工具回传，不能当作轮次起点。
        /// </summary>
        /// <summary>
        /// Haha 在后台 Bash / 子代理任务结束时，把完成报告以 <c>type=user</c> 行注入
        /// **同一个会话转录**，正文是 <c>&lt;task-notification&gt;</c> XML 片段（含
        /// <c>task-id</c>、<c>status</c>、<c>summary</c>），并且不带 <c>isMeta</c>。
        /// 这类行不是真人提问：它既不能当作轮次起点（会把耗时算成从注入点开始），
        /// 其后 Claude 的答复也不是用户主任务终态。
        /// 与 <c>cc-haha-task-notification</c>（根节点事件，见
        /// <see cref="IsHahaBackgroundSubtaskSummary"/>）是两种不同形态，必须分别识别。
        /// </summary>
        internal static bool IsHahaBackgroundTaskNotificationPrompt(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(root, "type"), "user", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return ContainsTaskNotificationMarker(content.GetString());
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            // 数组形态：只要存在文本块带该标记即视为注入行。
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(block, "type"), "text", StringComparison.OrdinalIgnoreCase) &&
                    ContainsTaskNotificationMarker(ReadString(block, "text")))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsTaskNotificationMarker(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            text.IndexOf("<task-notification>", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool IsClaudeHumanUserPrompt(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(root, "type"), "user", StringComparison.OrdinalIgnoreCase) ||
                ReadBool(root, "isMeta"))
            {
                return false;
            }

            // Haha 注入的后台任务完成报告不是真人提示，不得成为轮次起点。
            if (IsHahaBackgroundTaskNotificationPrompt(root))
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!message.TryGetProperty("content", out var content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return !string.IsNullOrWhiteSpace(content.GetString());
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var blockType = ReadString(block, "type");
                if (string.Equals(blockType, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // text / image / document / 其它非 tool_result 都算用户输入边界。
                if (string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(ReadString(block, "text")))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        internal static bool TryReadEventTimestamp(JsonElement root, out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var raw = FirstNonEmpty(ReadString(root, "timestamp"), ReadString(root, "stop_time"));
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out timestamp);
        }

        /// <summary>
        /// 由可靠起止时间计算耗时；非法区间或超过 30 天返回 0（手机端显示“耗时未知”）。
        /// </summary>
        internal static long ComputeElapsedDurationMs(DateTimeOffset startedAt, DateTimeOffset endedAt)
        {
            if (endedAt < startedAt)
            {
                return 0L;
            }

            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            const long maxDurationMs = 30L * 24L * 60L * 60L * 1000L;
            return durationMs > 0L && durationMs <= maxDurationMs ? durationMs : 0L;
        }

        /// <summary>
        /// 从 Claude/Haha assistant 行的 <c>message.usage</c>（或根级 usage）读取本步 token。
        /// 合计 = input + output + cache_creation_input + cache_read_input；任一字段缺失按 0。
        /// 全部为 0 或结构不存在时返回 0（不展示）。
        /// </summary>
        internal static long ReadUsageTokenCount(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0L;
            }

            JsonElement usage = default;
            var found = false;
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("usage", out usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                found = true;
            }
            else if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
            {
                found = true;
            }

            if (!found)
            {
                return 0L;
            }

            var input = ReadUsageLong(usage, "input_tokens", "inputTokens");
            var output = ReadUsageLong(usage, "output_tokens", "outputTokens");
            var cacheCreate = ReadUsageLong(
                usage,
                "cache_creation_input_tokens",
                "cacheCreationInputTokens");
            var cacheRead = ReadUsageLong(
                usage,
                "cache_read_input_tokens",
                "cacheReadInputTokens");
            // 部分 SDK 只给 total_tokens
            var total = ReadUsageLong(usage, "total_tokens", "totalTokens");
            var sum = input + output + cacheCreate + cacheRead;
            if (sum > 0L)
            {
                return sum;
            }

            return total > 0L ? total : 0L;
        }

        /// <summary>
        /// Codex（ChatGPT 桌面版）回合边界：<c>payload.type == "task_started"</c> 表示新回合，
        /// 用于把本轮 token 累计清零（与 Claude 的“新真人提示清零”语义一致）。
        /// </summary>
        internal static bool IsCodexTurnStartEvent(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                using var json = JsonDocument.Parse(line);
                return string.Equals(
                    ReadCodexEventType(json.RootElement),
                    "task_started",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 Codex <c>token_count</c> 事件，返回本次增量 token（<c>info.last_token_usage</c>
        /// 的 input+output+cached+reasoning，缺失时回退 total_tokens）。返回 true 表示这一行
        /// 是 token_count 事件（即便 info 为空）；调用方据此跳过它、避免落入完成事件解析。
        /// </summary>
        internal static bool TryReadCodexTokenCountEvent(string line, out long stepTokens)
        {
            stepTokens = 0L;
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var payload = root.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root;
                if (!string.Equals(ReadString(payload, "type"), "token_count", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                if (!info.TryGetProperty("last_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                var sum = ReadUsageLong(usage, "input_tokens", "inputTokens")
                          + ReadUsageLong(usage, "output_tokens", "outputTokens")
                          + ReadUsageLong(usage, "cached_input_tokens", "cachedInputTokens")
                          + ReadUsageLong(usage, "reasoning_output_tokens", "reasoningOutputTokens");
                if (sum <= 0L)
                {
                    sum = ReadUsageLong(usage, "total_tokens", "totalTokens");
                }
                stepTokens = sum;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string ReadCodexEventType(JsonElement root)
        {
            var payload = root.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            return ReadString(payload, "type");
        }

        private static long ReadUsageLong(JsonElement usage, params string[] names)
        {
            foreach (var name in names)
            {
                if (!usage.TryGetProperty(name, out var element))
                {
                    continue;
                }

                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
                {
                    return number > 0L ? number : 0L;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    long.TryParse(element.GetString(), out number) &&
                    number > 0L)
                {
                    return number;
                }
            }

            return 0L;
        }

        /// <summary>
        /// 拼出用户在手机上看到的提问文本。<c>input.questions[]</c> 每项带 <c>question</c>
        /// 与 <c>header</c>；单问题形态也可能把 <c>header</c> 直接放在 <c>input</c> 上。
        /// </summary>
        private static string ExtractAskUserQuestionText(JsonElement toolUse)
        {
            if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            if (input.TryGetProperty("questions", out var questions) &&
                questions.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in questions.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var text = FirstNonEmpty(ReadString(item, "question"), ReadString(item, "header"));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append("; ");
                    }
                    builder.Append(text.Trim());
                }
            }

            if (builder.Length == 0)
            {
                builder.Append(FirstNonEmpty(ReadString(input, "question"), ReadString(input, "header")).Trim());
            }

            return builder.ToString();
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int ReadInt(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var element))
            {
                return 0;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            {
                return number;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number))
            {
                return number;
            }

            return 0;
        }

        private static bool ReadBool(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var element) &&
                   element.ValueKind == JsonValueKind.True;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Codex（ChatGPT 桌面版）按轮 token 累计器。处理一条日志行：
    ///   - <c>task_started</c> 清零（新一轮开始）；
    ///   - <c>token_count</c> 累加 <c>info.last_token_usage</c> 的增量；
    ///   - 其它行返回 false，交回完成事件解析。
    /// 终态事件通过 <see cref="Take"/> 取出本轮累计并清零，避免跨轮串号。
    /// 独立成类以便用完整 session 文件做端到端回归。
    /// </summary>
    internal sealed class CodexTurnTokenAccumulator
    {
        private long _count;

        internal long Count => _count;

        /// <summary>若是回合边界/token 记账行则更新状态并返回 true；否则 false。</summary>
        internal bool TryObserve(string line)
        {
            if (AgentCompletionEventParser.IsCodexTurnStartEvent(line))
            {
                _count = 0L;
                return true;
            }

            if (AgentCompletionEventParser.TryReadCodexTokenCountEvent(line, out var step))
            {
                if (step > 0L)
                {
                    _count += step;
                }
                return true;
            }

            return false;
        }

        /// <summary>取出并清零本轮累计 token。</summary>
        internal long Take()
        {
            var value = _count;
            _count = 0L;
            return value;
        }
    }
}
