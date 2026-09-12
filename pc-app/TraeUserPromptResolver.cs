using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// TraeCode CN / TraeWork CN 的 sessionId → 本轮用户提示词反查。
    /// 两款 IDE 都把"输入框补全缓存"按会话存进 state.vscdb（SQLite，明文页）：
    ///   TraeCode CN：User\workspaceStorage\&lt;hash&gt;\state.vscdb，键
    ///     ai-chat.chatQueryCompletion.v2.&lt;sessionId&gt;
    ///   TraeWork CN：User\globalStorage\state.vscdb，键
    ///     chat-suggest:cache:&lt;sessionId&gt;
    /// 值结构相同：{"version":1,"response":{"result":[{"text":"..."}]},"savedAt":...}。
    /// 缓存在用户输入/发送时更新，任务完成（DONE）时刻读取即得本轮指令。
    /// 不用 SQLite 驱动：以只读共享方式读文件字节，在叶子页里键名后紧跟 JSON 值，
    /// 括号配对提取（与 TraeWorkspaceResolver 同一手法）；同一键的多个历史 cell
    /// 取 savedAt 最大者。
    /// </summary>
    internal static class TraeUserPromptResolver
    {
        private const long MaxDatabaseSizeBytes = 32L * 1024 * 1024;
        // 提示词摘要上限，与通知正文其它来源的 Summarize 上限保持一致。
        internal const int MaxPromptLength = 240;

        internal static bool TryResolvePrompt(string source, string sessionId, out string prompt)
        {
            prompt = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var isTraeWork = string.Equals(source, "traework", StringComparison.OrdinalIgnoreCase);
            var databases = isTraeWork
                ? TraeWorkGlobalStorageDatabases(appData)
                : TraeWorkspaceStorageDatabases(appData);

            var key = isTraeWork
                ? "chat-suggest:cache:" + sessionId
                : "ai-chat.chatQueryCompletion.v2." + sessionId;

            long bestSavedAt = -1;
            var bestPrompt = string.Empty;
            foreach (var database in databases)
            {
                foreach (var candidate in ExtractPromptValues(database, key))
                {
                    if (candidate.SavedAt > bestSavedAt)
                    {
                        bestSavedAt = candidate.SavedAt;
                        bestPrompt = candidate.Text;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestPrompt))
            {
                return false;
            }

            prompt = bestPrompt;
            return true;
        }

        private static IEnumerable<string> TraeWorkspaceStorageDatabases(string appData)
        {
            foreach (var root in new[] { Path.Combine(appData, "Trae CN"), Path.Combine(appData, "TraeCode CN") })
            {
                var storage = Path.Combine(root, "User", "workspaceStorage");
                if (!Directory.Exists(storage))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateDirectories(storage))
                {
                    var database = Path.Combine(entry, "state.vscdb");
                    if (File.Exists(database))
                    {
                        yield return database;
                    }
                }
            }
        }

        private static IEnumerable<string> TraeWorkGlobalStorageDatabases(string appData)
        {
            // 官方更名后目录未迁移，新旧两个目录名都视为 TraeWork。
            foreach (var root in new[] { Path.Combine(appData, "TRAE SOLO CN"), Path.Combine(appData, "TraeWork CN") })
            {
                var database = Path.Combine(root, "User", "globalStorage", "state.vscdb");
                if (File.Exists(database))
                {
                    yield return database;
                }
            }
        }

        private readonly struct PromptCandidate
        {
            public PromptCandidate(string text, long savedAt)
            {
                Text = text;
                SavedAt = savedAt;
            }

            public string Text { get; }
            public long SavedAt { get; }
        }

        /// <summary>
        /// 在 state.vscdb 字节流中找键名（可能多处出现：内部页索引 + 叶子页 cell +
        /// 历史 cell 残留），仅叶子页 cell 里键名后紧跟 JSON 值。对每个出现位置向后
        /// 在小窗口内找 '{' 并做括号配对；解析成功的按 savedAt 竞争。
        /// </summary>
        internal static IEnumerable<(string Text, long SavedAt)> ExtractPromptValues(string databasePath, string key)
        {
            byte[] buffer;
            try
            {
                using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= 0 || stream.Length > MaxDatabaseSizeBytes)
                {
                    yield break;
                }

                buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }
                    offset += read;
                }

                if (offset != buffer.Length)
                {
                    yield break;
                }
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            var keyBytes = Encoding.ASCII.GetBytes(key);
            var searchFrom = 0;
            while (TryFindBytes(buffer, keyBytes, searchFrom, out var keyIndex))
            {
                searchFrom = keyIndex + 1;

                // 键名结束后跳过 SQLite cell header（payload 长度/rowid/列头，通常 10 字节内），
                // 第一个 '{' 视为值起点；找不到说明此处只是内部页路由键或碎片残留。
                // 窗口收紧到 16 字节，避免把相邻 cell（其它键）的值误配到本键。
                var valueStart = -1;
                var scan = keyIndex + keyBytes.Length;
                var scanLimit = Math.Min(buffer.Length, scan + 16);
                while (scan < scanLimit)
                {
                    if (buffer[scan] == (byte)'{')
                    {
                        valueStart = scan;
                        break;
                    }
                    scan++;
                }

                if (valueStart < 0)
                {
                    continue;
                }

                if (!TryFindJsonEnd(buffer, valueStart, out var valueEnd))
                {
                    continue;
                }

                string json;
                try
                {
                    json = Encoding.UTF8.GetString(buffer, valueStart, valueEnd - valueStart + 1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (TryParsePromptValue(json, out var candidate))
                {
                    yield return (candidate.Text, candidate.SavedAt);
                }
            }
        }

        /// <summary>
        /// 从 valueStart 的 '{' 开始做括号配对（处理字符串内的花括号与反斜杠转义），
        /// 输出配对 '}' 的下标。UTF-8 多字节字符均 >= 0x80，不会与 ASCII 语法字符
        /// 混淆，可在字节层安全配对。
        /// </summary>
        private static bool TryFindJsonEnd(byte[] buffer, int valueStart, out int valueEnd)
        {
            valueEnd = -1;
            var depth = 0;
            var inString = false;
            for (var i = valueStart; i < buffer.Length; i++)
            {
                var current = buffer[i];
                if (inString)
                {
                    if (current == (byte)'\\')
                    {
                        i++; // 跳过转义字符。
                    }
                    else if (current == (byte)'"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == (byte)'"')
                {
                    inString = true;
                }
                else if (current == (byte)'{')
                {
                    depth++;
                }
                else if (current == (byte)'}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        valueEnd = i;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryParsePromptValue(string json, out PromptCandidate candidate)
        {
            candidate = default;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("response", out var response) ||
                    !response.TryGetProperty("result", out var result) ||
                    result.ValueKind != JsonValueKind.Array ||
                    result.GetArrayLength() == 0)
                {
                    return false;
                }

                var first = result[0];
                if (!first.TryGetProperty("text", out var textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                long savedAt = 0;
                if (root.TryGetProperty("savedAt", out var savedAtElement) &&
                    savedAtElement.ValueKind == JsonValueKind.Number &&
                    savedAtElement.TryGetInt64(out var parsed))
                {
                    savedAt = parsed;
                }

                candidate = new PromptCandidate(text, savedAt);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool TryFindBytes(byte[] buffer, byte[] needle, int start, out int index)
        {
            index = -1;
            if (needle.Length == 0 || buffer.Length < needle.Length)
            {
                return false;
            }

            var limit = buffer.Length - needle.Length;
            for (var i = Math.Max(0, start); i <= limit; i++)
            {
                var matched = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (buffer[i + j] != needle[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>压缩空白并截断到通知正文可承受的长度。</summary>
        internal static string Summarize(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return string.Empty;
            }

            var normalized = string.Join(
                " ",
                prompt.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= MaxPromptLength
                ? normalized
                : normalized.Substring(0, MaxPromptLength) + "...";
        }
    }
}
