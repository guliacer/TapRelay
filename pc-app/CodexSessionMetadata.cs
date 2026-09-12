using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    internal enum CodexSessionKind
    {
        Unknown,
        Primary,
        Subagent
    }

    internal static class CodexSessionMetadata
    {
        internal static CodexSessionKind Merge(CodexSessionKind current, CodexSessionKind candidate)
        {
            if (current == CodexSessionKind.Subagent || candidate == CodexSessionKind.Subagent)
            {
                return CodexSessionKind.Subagent;
            }

            if (current == CodexSessionKind.Primary || candidate == CodexSessionKind.Primary)
            {
                return CodexSessionKind.Primary;
            }

            return CodexSessionKind.Unknown;
        }

        internal static CodexSessionKind ClassifyLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return CodexSessionKind.Unknown;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!string.Equals(ReadString(root, "type"), "session_meta", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    return CodexSessionKind.Unknown;
                }

                if (AgentRecordClassifier.IsSubagent(payload))
                {
                    return CodexSessionKind.Subagent;
                }

                return CodexSessionKind.Primary;
            }
            catch (JsonException)
            {
                return CodexSessionKind.Unknown;
            }
        }

        internal static CodexSessionKind ReadFromFile(string path)
        {
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
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        return ClassifyLine(line);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return CodexSessionKind.Unknown;
        }

        /// <summary>
        /// Reads the resumable Codex session identifier from a session metadata record.
        /// Terminal task events do not consistently repeat this value, so callers cache it
        /// when the metadata line is seen and fall back to scanning the session file.
        /// </summary>
        internal static string TryReadSessionId(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!string.Equals(ReadString(root, "type"), "session_meta", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                return FirstNonEmpty(
                    ReadString(payload, "session_id"),
                    ReadString(payload, "sessionId"),
                    ReadString(payload, "id"));
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        internal static string ReadSessionIdFromFile(string path)
        {
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
                    var sessionId = TryReadSessionId(line);
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        return sessionId;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return string.Empty;
        }

        internal static string ReadWorkingDirectoryFromFile(string path)
        {
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
                    var workingDirectory = AgentCompletionEventParser.TryExtractChatGPTWorkingDirectory(line);
                    if (!string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        return workingDirectory;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return string.Empty;
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

        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
