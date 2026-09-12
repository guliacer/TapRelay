using System;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    internal static class AgentRecordClassifier
    {
        internal static bool IsSubagent(JsonElement root)
        {
            return IsSubagent(root, 0);
        }

        internal static bool TryParseTraeSessionLine(
            string line,
            out string sessionId,
            out bool isSubagent)
        {
            sessionId = string.Empty;
            isSubagent = false;
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("[ai-chat][sendChatMessage]", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            sessionId = ReadLogValue(line, "sessionId=");
            var subagentType = ReadLogValue(line, "session.subagentType=");
            if (string.IsNullOrWhiteSpace(sessionId) ||
                IsEmptySentinel(sessionId) ||
                string.IsNullOrWhiteSpace(subagentType))
            {
                sessionId = string.Empty;
                return false;
            }

            isSubagent = IsActualSubagentType(subagentType);
            return true;
        }

        private static bool IsSubagent(JsonElement element, int depth)
        {
            if (depth > 32)
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var name = Normalize(property.Name);
                    if ((name == "issidechain" || name == "issubagent") && IsTruthy(property.Value))
                    {
                        return true;
                    }

                    if (name == "threadsource" && IsSubagentRole(property.Value))
                    {
                        return true;
                    }

                    if (name == "subagenttype" && IsActualSubagentType(property.Value))
                    {
                        return true;
                    }

                    if ((name == "agenttype" || name == "chattype" || name == "messagetype") &&
                        IsSubagentRole(property.Value))
                    {
                        return true;
                    }

                    if (name == "source" && HasSubagentSource(property.Value))
                    {
                        return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if ((property.Value.ValueKind == JsonValueKind.Object ||
                         property.Value.ValueKind == JsonValueKind.Array) &&
                        IsSubagent(property.Value, depth + 1))
                    {
                        return true;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (IsSubagent(item, depth + 1))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasSubagentSource(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in source.EnumerateObject())
            {
                if (Normalize(property.Name) == "subagent" && IsMeaningful(property.Value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSubagentRole(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.String && IsSubagentRole(value.GetString());
        }

        private static bool IsSubagentRole(string? value)
        {
            var normalized = Normalize(value ?? string.Empty);
            return normalized == "subagent" || normalized == "sidechain";
        }

        private static bool IsActualSubagentType(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return IsActualSubagentType(value.GetString() ?? string.Empty);
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.Number:
                    return value.TryGetInt64(out var number) && number != 0;
                case JsonValueKind.Object:
                    foreach (var _ in value.EnumerateObject())
                    {
                        return true;
                    }
                    return false;
                case JsonValueKind.Array:
                    return value.GetArrayLength() > 0;
                default:
                    return false;
            }
        }

        private static bool IsActualSubagentType(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !IsEmptySentinel(value);
        }

        private static bool IsTruthy(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.Number:
                    return value.TryGetInt64(out var number) && number != 0;
                case JsonValueKind.String:
                    var normalized = Normalize(value.GetString() ?? string.Empty);
                    return normalized == "true" || normalized == "1" || normalized == "yes";
                default:
                    return false;
            }
        }

        private static bool IsMeaningful(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    return !IsEmptySentinel(value.GetString() ?? string.Empty);
                case JsonValueKind.Number:
                    return !value.TryGetInt64(out var number) || number != 0;
                default:
                    return true;
            }
        }

        private static bool IsEmptySentinel(string value)
        {
            switch (Normalize(value))
            {
                case "":
                case "0":
                case "false":
                case "undefined":
                case "null":
                case "none":
                case "unknown":
                case "primary":
                case "main":
                case "user":
                case "normal":
                    return true;
                default:
                    return false;
            }
        }

        private static string ReadLogValue(string line, string marker)
        {
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var end = line.IndexOf(',', start);
            var value = (end < 0 ? line.Substring(start) : line.Substring(start, end - start)).Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                value = value.Substring(1, value.Length - 2).Trim();
            }

            return value;
        }

        private static string Normalize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
            {
                if (character == '_' || character == '-' || character == '.' || char.IsWhiteSpace(character))
                {
                    continue;
                }

                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }
    }
}
