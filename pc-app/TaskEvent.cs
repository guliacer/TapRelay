using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Cross-agent event envelope. The event wire format is deliberately separate
    /// from the legacy notification message so live state and user alerts can
    /// evolve without overloading status/content fields.
    /// </summary>
    public sealed class TaskEvent
    {
        private const int MaxEventIdLength = 100;
        private const int MaxEventTypeLength = 40;
        private const int MaxSourceLength = 40;
        private const int MaxSessionIdLength = 160;
        private const int MaxTaskIdLength = 200;
        private const int MaxProjectNameLength = 120;
        private const int MaxCwdLength = 240;
        private const int MaxToolNameLength = 120;
        private const int MaxToolUseIdLength = 160;
        private const int MaxMessageLength = 500;
        private const int MaxTaskLogLength = 4000;
        private const int MaxCreditsLength = 64;

        private static readonly HashSet<string> SupportedEventTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "session.started",
                "session.ended",
                "tool.started",
                "tool.finished",
                "tool.failed",
                "approval.requested",
                "approval.resolved",
                "subagent.started",
                "subagent.ended",
                "agent.status"
            };

        public string EventId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string Cwd { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string TaskLog { get; set; } = string.Empty;
        public long DurationMs { get; set; }
        public long TokenCount { get; set; }
        public string Credits { get; set; } = string.Empty;
        public string InputSummary { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public bool RequiresAction { get; set; }
        public long ExpiresAtUnixMs { get; set; }
        public string ApprovalDecision { get; set; } = string.Empty;

        public bool IsApprovalRequest =>
            string.Equals(EventType, "approval.requested", StringComparison.OrdinalIgnoreCase);

        public static bool TryParse(JsonElement root, out TaskEvent taskEvent, out string error)
        {
            taskEvent = new TaskEvent();
            error = string.Empty;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "event body must be a JSON object";
                return false;
            }

            taskEvent.EventId = Read(root, "eventId", MaxEventIdLength);
            if (string.IsNullOrWhiteSpace(taskEvent.EventId))
            {
                taskEvent.EventId = Guid.NewGuid().ToString("N");
            }

            taskEvent.EventType = Read(root, "eventType", MaxEventTypeLength);
            if (string.IsNullOrWhiteSpace(taskEvent.EventType))
            {
                taskEvent.EventType = Read(root, "event", MaxEventTypeLength);
            }
            if (!SupportedEventTypes.Contains(taskEvent.EventType))
            {
                error = "unsupported eventType";
                return false;
            }
            taskEvent.EventType = taskEvent.EventType.Trim().ToLowerInvariant();

            taskEvent.Source = Read(root, "source", MaxSourceLength).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(taskEvent.Source))
            {
                error = "source is required";
                return false;
            }

            taskEvent.Status = Read(root, "status", 30).Trim().ToLowerInvariant();
            taskEvent.SessionId = Read(root, "sessionId", MaxSessionIdLength);
            taskEvent.TaskId = Read(root, "taskId", MaxTaskIdLength);
            taskEvent.ProjectName = Read(root, "projectName", MaxProjectNameLength);
            taskEvent.Cwd = Read(root, "cwd", MaxCwdLength);
            taskEvent.ToolName = Read(root, "toolName", MaxToolNameLength);
            if (string.IsNullOrWhiteSpace(taskEvent.ToolName))
            {
                taskEvent.ToolName = Read(root, "tool", MaxToolNameLength);
            }
            taskEvent.ToolUseId = Read(root, "toolUseId", MaxToolUseIdLength);
            taskEvent.Message = Read(root, "message", MaxMessageLength);
            if (string.IsNullOrWhiteSpace(taskEvent.Message))
            {
                taskEvent.Message = Read(root, "content", MaxMessageLength);
            }
            taskEvent.TaskLog = Read(root, "taskLog", MaxTaskLogLength);
            taskEvent.DurationMs = NormalizeDurationMs(ReadLong(root, "durationMs"));
            taskEvent.TokenCount = NormalizeTokenCount(ReadLong(root, "tokenCount"));
            taskEvent.Credits = Read(root, "credits", MaxCreditsLength).Trim();
            taskEvent.InputSummary = Read(root, "inputSummary", MaxMessageLength);
            taskEvent.RequestId = Read(root, "requestId", MaxEventIdLength);
            taskEvent.ApprovalDecision = Read(root, "decision", 20).Trim().ToLowerInvariant();
            taskEvent.RequiresAction = ReadBool(root, "requiresAction");
            taskEvent.ExpiresAtUnixMs = ReadLong(root, "expiresAt");

            if (string.IsNullOrWhiteSpace(taskEvent.Status))
            {
                taskEvent.Status = DefaultStatus(taskEvent.EventType);
            }
            if (string.IsNullOrWhiteSpace(taskEvent.TaskId))
            {
                taskEvent.TaskId = taskEvent.SessionId;
            }
            if (taskEvent.IsApprovalRequest)
            {
                taskEvent.RequiresAction = true;
                if (string.IsNullOrWhiteSpace(taskEvent.RequestId))
                {
                    taskEvent.RequestId = taskEvent.EventId;
                }
                if (taskEvent.ExpiresAtUnixMs <= 0)
                {
                    taskEvent.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
                }
                else
                {
                    var maxExpiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
                    taskEvent.ExpiresAtUnixMs = Math.Min(taskEvent.ExpiresAtUnixMs, maxExpiry);
                }
                if (string.IsNullOrWhiteSpace(taskEvent.Message))
                {
                    taskEvent.Message = string.IsNullOrWhiteSpace(taskEvent.ToolName)
                        ? "需要确认一项操作"
                        : "需要确认：" + taskEvent.ToolName;
                }
            }

            return true;
        }

        public Dictionary<string, object> ToPayload()
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "event",
                ["eventId"] = EventId,
                ["eventType"] = EventType,
                ["source"] = Source,
                ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                ["status"] = Status,
                ["sessionId"] = SessionId,
                ["taskId"] = TaskId,
                ["projectName"] = ProjectName,
                ["cwd"] = Cwd,
                ["toolName"] = ToolName,
                ["toolUseId"] = ToolUseId,
                ["message"] = SecretMasker.Mask(Message),
                ["taskLog"] = SecretMasker.Mask(TaskLog),
                ["durationMs"] = DurationMs,
                ["tokenCount"] = TokenCount,
                ["credits"] = Credits,
                ["inputSummary"] = SecretMasker.Mask(InputSummary),
                ["requestId"] = RequestId,
                ["requiresAction"] = RequiresAction,
                ["expiresAt"] = ExpiresAtUnixMs,
                ["decision"] = ApprovalDecision
            };
            return payload;
        }

        public TaskEvent CreateResolution(string decision)
        {
            return new TaskEvent
            {
                EventId = EventId + ":resolved",
                EventType = "approval.resolved",
                Source = Source,
                Status = string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase)
                    ? "approved"
                    : "rejected",
                SessionId = SessionId,
                TaskId = TaskId,
                ProjectName = ProjectName,
                Cwd = Cwd,
                ToolName = ToolName,
                ToolUseId = ToolUseId,
                Message = string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase)
                    ? "已允许：" + ToolName
                    : "已拒绝：" + ToolName,
                TaskLog = TaskLog,
                DurationMs = DurationMs,
                TokenCount = TokenCount,
                Credits = Credits,
                RequestId = RequestId,
                RequiresAction = false,
                ExpiresAtUnixMs = ExpiresAtUnixMs,
                ApprovalDecision = decision
            };
        }

        private static string DefaultStatus(string eventType)
        {
            switch (eventType)
            {
                case "session.started":
                case "tool.started":
                case "subagent.started":
                    return "running";
                case "tool.failed":
                    return "failed";
                case "approval.requested":
                    return "waiting";
                case "session.ended":
                case "tool.finished":
                case "subagent.ended":
                    return "completed";
                default:
                    return "processing";
            }
        }

        private static string Read(JsonElement root, string name, int maxLength)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }
            var text = value.GetString() ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }

        private static bool ReadBool(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static long ReadLong(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var number))
            {
                return number;
            }
            return 0L;
        }

        private static long NormalizeDurationMs(long durationMs)
        {
            const long maxDurationMs = 30L * 24L * 60L * 60L * 1000L;
            return durationMs > 0L && durationMs <= maxDurationMs ? durationMs : 0L;
        }

        private static long NormalizeTokenCount(long tokenCount)
        {
            const long maxTokenCount = 2_000_000_000L;
            return tokenCount > 0L && tokenCount <= maxTokenCount ? tokenCount : 0L;
        }
    }
}
