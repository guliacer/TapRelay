using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Keeps only reliable start timestamps that can be paired with a terminal task event.
    /// Sources with a native duration keep that value and use this tracker only as a fallback.
    /// </summary>
    internal sealed class AgentTaskTiming
    {
        private const long MaxDurationMs = 30L * 24L * 60L * 60L * 1000L;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTimeOffset> _startedAt =
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        internal void ObserveLine(string source, string line)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (line.IndexOf("[ai-chat][sendChatMessage]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                RecordStart(source, string.Empty, ReadLogToken(line, "sessionId"), ReadTimestamp(line));
                return;
            }

            // TraeCode CN / TraeWork CN 的回合起点：每个用户回合触发一次（同一时刻重复两条），
            // 行内 JSON 携带 sessionId，行首时间戳即起点。配合 DoneHandler 终态事件计算真实回合耗时。
            if (line.IndexOf("[StreamDomainService] Stream started", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                RecordStart(source, string.Empty, ReadInlineJsonString(line, "sessionId"), ReadTimestamp(line));
                return;
            }

            if (line.IndexOf("live_turn_started_during_fetch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("send chat message start", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var sessionId = ReadInlineJsonString(line, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    sessionId = ReadLogToken(line, "sessionId");
                }
                RecordStart(source, string.Empty, sessionId, ReadTimestamp(line));
                return;
            }

            if (line.IndexOf("task_started", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("turn.started", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("turn_started", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            TryObserveJsonStart(source, line);
        }

        internal long Resolve(AgentCompletionEventData completionEvent, string terminalLine)
        {
            if (completionEvent == null)
            {
                return 0L;
            }

            var keys = BuildCandidateKeys(
                completionEvent.Source,
                completionEvent.TaskId,
                completionEvent.SessionId);
            var isTerminal = !string.Equals(
                completionEvent.Status,
                "waiting",
                StringComparison.OrdinalIgnoreCase);
            var directDuration = NormalizeDuration(completionEvent.DurationMs);
            if (directDuration > 0L)
            {
                if (isTerminal)
                {
                    Forget(keys);
                }
                return directDuration;
            }

            var endedAt = ReadTimestamp(terminalLine);
            if (!endedAt.HasValue || keys.Count == 0)
            {
                return 0L;
            }

            DateTimeOffset? matchedStart = null;
            lock (_gate)
            {
                foreach (var key in keys)
                {
                    if (_startedAt.TryGetValue(key, out var startedAt) &&
                        startedAt <= endedAt.Value &&
                        (!matchedStart.HasValue || startedAt > matchedStart.Value))
                    {
                        matchedStart = startedAt;
                    }
                }

                if (isTerminal)
                {
                    foreach (var key in keys)
                    {
                        _startedAt.Remove(key);
                    }
                }
            }

            if (!matchedStart.HasValue)
            {
                return 0L;
            }

            return NormalizeDuration((long)(endedAt.Value - matchedStart.Value).TotalMilliseconds);
        }

        private void TryObserveJsonStart(string source, string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var payload = root.TryGetProperty("payload", out var nestedPayload) &&
                              nestedPayload.ValueKind == JsonValueKind.Object
                    ? nestedPayload
                    : root;
                var eventName = FirstNonEmpty(
                    ReadString(payload, "type"),
                    ReadString(payload, "event"),
                    ReadString(root, "event"),
                    ReadString(root, "type"));
                var normalized = eventName.Trim().ToLowerInvariant().Replace('-', '_');
                if (normalized != "task_started" &&
                    normalized != "turn.started" &&
                    normalized != "turn_started")
                {
                    return;
                }

                var taskToken = FirstNonEmpty(
                    ReadDeepString(payload, "turn_id"),
                    ReadDeepString(payload, "turnId"),
                    ReadDeepString(payload, "task_id"),
                    ReadDeepString(payload, "taskId"));
                var sessionId = FirstNonEmpty(
                    ReadDeepString(payload, "session_id"),
                    ReadDeepString(payload, "sessionId"),
                    ReadDeepString(root, "session_id"),
                    ReadDeepString(root, "sessionId"));
                var timestamp = FirstNonEmpty(
                    ReadString(root, "timestamp"),
                    ReadString(payload, "timestamp"));
                RecordStart(source, taskToken, sessionId, ParseTimestamp(timestamp));
            }
            catch (JsonException)
            {
                // A partially flushed JSONL line cannot provide a reliable start time.
            }
        }

        private void RecordStart(
            string source,
            string taskToken,
            string sessionId,
            DateTimeOffset? startedAt)
        {
            if (!startedAt.HasValue)
            {
                return;
            }

            var keys = BuildCandidateKeys(source, taskToken, sessionId);
            if (keys.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var key in keys)
                {
                    _startedAt[key] = startedAt.Value;
                }

                if (_startedAt.Count > 1000)
                {
                    foreach (var key in _startedAt
                                 .OrderBy(item => item.Value)
                                 .Take(_startedAt.Count - 750)
                                 .Select(item => item.Key)
                                 .ToArray())
                    {
                        _startedAt.Remove(key);
                    }
                }
            }
        }

        private void Forget(IReadOnlyList<string> keys)
        {
            lock (_gate)
            {
                foreach (var key in keys)
                {
                    _startedAt.Remove(key);
                }
            }
        }

        private static List<string> BuildCandidateKeys(string source, string taskId, string sessionId)
        {
            var normalizedSource = (source ?? string.Empty).Trim().ToLowerInvariant();
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddValue(values, taskId);
            AddValue(values, sessionId);

            var normalizedTaskId = taskId ?? string.Empty;
            var separator = normalizedTaskId.LastIndexOf(':');
            if (separator >= 0 && separator < normalizedTaskId.Length - 1)
            {
                AddValue(values, normalizedTaskId.Substring(separator + 1));
            }

            return string.IsNullOrWhiteSpace(normalizedSource)
                ? new List<string>()
                : values.Select(value => normalizedSource + ":" + value).ToList();
        }

        private static void AddValue(ISet<string> values, string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                values.Add(normalized);
            }
        }

        private static DateTimeOffset? ReadTimestamp(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var firstSpace = trimmed.IndexOf(' ');
                var prefix = firstSpace > 0 ? trimmed.Substring(0, firstSpace) : trimmed;
                return ParseTimestamp(prefix);
            }

            try
            {
                using var json = JsonDocument.Parse(trimmed);
                var root = json.RootElement;
                var payload = root.TryGetProperty("payload", out var nestedPayload) &&
                              nestedPayload.ValueKind == JsonValueKind.Object
                    ? nestedPayload
                    : root;
                return ParseTimestamp(FirstNonEmpty(
                    ReadString(root, "timestamp"),
                    ReadString(payload, "timestamp")));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static DateTimeOffset? ParseTimestamp(string value)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var timestamp)
                ? timestamp
                : (DateTimeOffset?)null;
        }

        private static string ReadInlineJsonString(string line, string name)
        {
            var jsonStart = line.IndexOf('{');
            if (jsonStart < 0)
            {
                return string.Empty;
            }

            try
            {
                using var json = JsonDocument.Parse(line.Substring(jsonStart));
                return ReadDeepString(json.RootElement, name);
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static string ReadLogToken(string line, string name)
        {
            var marker = name + "=";
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var end = start;
            while (end < line.Length &&
                   line[end] != ',' &&
                   !char.IsWhiteSpace(line[end]))
            {
                end++;
            }
            return line.Substring(start, end - start).Trim().Trim('"');
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.ValueKind == JsonValueKind.Number
                        ? value.ToString()
                        : string.Empty;
            }
            return string.Empty;
        }

        private static string ReadDeepString(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value.ValueKind == JsonValueKind.String ||
                               property.Value.ValueKind == JsonValueKind.Number
                            ? property.Value.ToString()
                            : string.Empty;
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

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private static long NormalizeDuration(long durationMs)
        {
            return durationMs > 0L && durationMs <= MaxDurationMs ? durationMs : 0L;
        }
    }
}
