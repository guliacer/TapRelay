using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Reads visible assistant output from a Codex rollout without treating user
    /// prompts, tool output, reasoning, or subagent items as the task log.
    /// </summary>
    internal static class CodexSessionReader
    {
        internal static bool TryReadLastAssistantMessage(
            string path,
            string turnId,
            out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(turnId) ||
                !File.Exists(path))
            {
                return false;
            }

            var latestMessage = string.Empty;
            var finalMessage = string.Empty;
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!TryReadAssistantMessageLine(line, turnId, out var candidate, out var isFinal))
                    {
                        continue;
                    }

                    if (isFinal)
                    {
                        finalMessage = candidate;
                    }
                    else
                    {
                        latestMessage = candidate;
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            message = FirstNonEmpty(finalMessage, latestMessage);
            return message.Length > 0;
        }

        internal static bool TryReadAssistantMessageLine(
            string line,
            string turnId,
            out string message,
            out bool isFinal)
        {
            message = string.Empty;
            isFinal = false;
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(turnId))
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return false;
                }

                var rootType = ReadString(root, "type");
                var payload = root.TryGetProperty("payload", out var nestedPayload) &&
                              nestedPayload.ValueKind == JsonValueKind.Object
                    ? nestedPayload
                    : root;

                if (string.Equals(rootType, "event_msg", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ReadString(payload, "type"), "item_completed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ReadString(payload, "turn_id"), turnId, StringComparison.Ordinal))
                {
                    if (!payload.TryGetProperty("item", out var item) ||
                        item.ValueKind != JsonValueKind.Object ||
                        !string.Equals(ReadString(item, "type"), "AgentMessage", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    message = ReadTextBlocks(item, restrictToOutputText: false);
                    isFinal = string.Equals(ReadString(item, "phase"), "final_answer", StringComparison.OrdinalIgnoreCase);
                    return message.Length > 0;
                }

                if (!string.Equals(rootType, "response_item", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(payload, "type"), "message", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(payload, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var responseTurnId = FirstNonEmpty(
                    ReadString(payload, "turn_id"),
                    ReadString(payload, "turnId"),
                    ReadString(root, "turn_id"),
                    ReadString(root, "turnId"),
                    ReadNestedString(payload, "internal_chat_message_metadata_passthrough", "turn_id"),
                    ReadNestedString(payload, "internal_chat_message_metadata_passthrough", "turnId"));
                if (!string.Equals(responseTurnId, turnId, StringComparison.Ordinal))
                {
                    return false;
                }

                message = ReadTextBlocks(payload, restrictToOutputText: true);
                return message.Length > 0;
            }
            catch (JsonException)
            {
                // A watcher can observe a partially flushed JSONL line.
                return false;
            }
        }

        private static string ReadTextBlocks(JsonElement owner, bool restrictToOutputText)
        {
            if (!owner.TryGetProperty("content", out var content))
            {
                return ReadString(owner, "output_text");
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return (content.GetString() ?? string.Empty).Trim();
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String)
                {
                    AppendText(builder, block.GetString());
                    continue;
                }

                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var blockType = ReadString(block, "type");
                if (restrictToOutputText &&
                    !string.IsNullOrWhiteSpace(blockType) &&
                    !string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppendText(builder, ReadString(block, "text"));
            }

            return builder.ToString().Trim();
        }

        private static void AppendText(StringBuilder builder, string value)
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

        private static string ReadNestedString(JsonElement root, string objectName, string valueName)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(objectName, out var nested) &&
                   nested.ValueKind == JsonValueKind.Object
                ? ReadString(nested, valueName)
                : string.Empty;
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
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
}
