using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TapRelay
{
    /// <summary>
    /// 读取 WorkBuddy 自身持久化的真实任务数据——与 Codex 直接读
    /// .codex/sessions 会话日志同一思路，而非依赖 Windows 系统通知。
    /// 数据源：
    ///   1) %USERPROFILE%\.workbuddy\workbuddy.db
    ///      sessions 表（id=ACP 事件 sessionId → 真实任务标题）
    ///      session_usage 表（credit_json: requestId→单请求积分；used: token）
    ///   2) %USERPROFILE%\.workbuddy\projects\&lt;工作区&gt;\&lt;sessionId&gt;.jsonl
    ///      真实会话日志（role=user/assistant 的 output_text），即任务日志。
    /// 仅当以上不可用（库/文件被独占或结构变化）时才回退 Windows toast。
    /// </summary>
    internal static class WorkBuddySessionReader
    {
        // 注意：Microsoft.Data.Sqlite 3.1.32 的 SqliteConnectionStringBuilder 不支持
        // "Default Timeout" 关键字（5.0+ 才有），加了会抛 ArgumentException 导致
        // 整个读取失败。workbuddy.db 是 WAL 模式，只读查询毫秒级，无需超时设置。
        private const string ConnectionOptions = ";Mode=ReadOnly";

        // 单元测试注入的数据库覆盖路径；非空时替代 ~/.workbuddy/workbuddy.db。
        internal static string? DatabaseOverride { get; set; }

        // 会话日志只读尾部窗口：足够覆盖最近几条消息，避免大文件全量读取。
        private const int SessionLogTailBytes = 512 * 1024;

        internal static bool TryResolveTitle(string sessionId, out string title)
        {
            title = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var database = ResolveDatabase();
            if (database.Length == 0)
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ConnectionOptions);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT title FROM sessions WHERE id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                {
                    return false;
                }

                title = (reader.GetString(0) ?? string.Empty).Trim();
                return title.Length > 0;
            }
            catch (SqliteException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 读取 WorkBuddy 会话保存的权限模式。ACP session/resume 不会自动
        /// 恢复这个字段，调用方必须在发送新 prompt 前显式同步它。
        /// </summary>
        internal static bool TryResolvePermissionMode(
            string sessionId,
            out string permissionMode)
        {
            permissionMode = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var database = ResolveDatabase();
            if (database.Length == 0)
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ConnectionOptions);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT permission_mode FROM sessions WHERE id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                {
                    return false;
                }

                permissionMode = (reader.GetString(0) ?? string.Empty).Trim();
                return permissionMode.Length > 0;
            }
            catch (SqliteException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 读取会话保存的 sandbox 开关。缺失时返回 false，调用方保持 ACP
        /// 默认配置，不猜测用户的安全设置。
        /// </summary>
        internal static bool TryResolveSandboxEnabled(
            string sessionId,
            out bool enabled)
        {
            enabled = false;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var database = ResolveDatabase();
            if (database.Length == 0)
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ConnectionOptions);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT use_sandbox_cli FROM sessions WHERE id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                {
                    return false;
                }

                enabled = Convert.ToInt64(reader.GetValue(0)) != 0L;
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 按 WorkBuddy 的空间模型解析卡片主名称：固定空间会话使用 cwd 的项目目录名，
        /// playground（无固定文件夹）会话使用 sessions.title 的任务名。
        /// </summary>
        internal static bool TryResolveProjectOrTaskName(
            string sessionId,
            out string displayName,
            out bool isProjectName)
        {
            displayName = string.Empty;
            isProjectName = false;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var database = ResolveDatabase();
            if (database.Length == 0)
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ConnectionOptions);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT title, cwd, is_playground FROM sessions WHERE id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var title = reader.IsDBNull(0) ? string.Empty : (reader.GetString(0) ?? string.Empty).Trim();
                var cwd = reader.IsDBNull(1) ? string.Empty : (reader.GetString(1) ?? string.Empty).Trim();
                var isPlayground = !reader.IsDBNull(2) && Convert.ToInt64(reader.GetValue(2)) != 0L;

                if (!isPlayground)
                {
                    var project = DirectoryLeaf(cwd);
                    if (project.Length > 0)
                    {
                        displayName = project;
                        isProjectName = true;
                        return true;
                    }
                }

                if (title.Length > 0)
                {
                    displayName = title;
                    return true;
                }

                return false;
            }
            catch (SqliteException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 读取会话用量：积分优先（credit_json 为 requestId→积分 的映射，
        /// 先按本次请求 requestId 精确匹配，未命中则取最后一条=最近完成请求），
        /// 无积分则取 used（token），两者都无则返回 false（调用方忽略，不展示）。
        /// </summary>
        internal static bool TryResolveUsage(
            string sessionId,
            string requestId,
            out string credits,
            out long tokenCount)
        {
            credits = string.Empty;
            tokenCount = 0L;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var database = ResolveDatabase();
            if (database.Length == 0)
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ConnectionOptions);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT used, credit_json FROM session_usage WHERE session_id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var used = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                var creditJson = reader.IsDBNull(1) ? string.Empty : (reader.GetString(1) ?? string.Empty).Trim();
                if (creditJson.Length > 0)
                {
                    var value = ResolveCreditValue(creditJson, requestId);
                    if (value > 0)
                    {
                        credits = value.ToString("0.##");
                        return true;
                    }
                }

                if (used > 0L)
                {
                    tokenCount = used;
                    return true;
                }

                return false;
            }
            catch (SqliteException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 读取会话真实日志：projects/&lt;工作区&gt;/&lt;sessionId&gt;.jsonl 中
        /// 最后一条 assistant 的 output_text（Agent 真实回复），经 Summarize 截断。
        /// </summary>
        internal static bool TryResolveTaskLog(string sessionId, out string taskLog)
        {
            taskLog = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var file = ResolveSessionFile(sessionId);
            if (file.Length == 0)
            {
                return false;
            }

            try
            {
                var tail = ReadLogTail(file, SessionLogTailBytes);
                if (tail.Length == 0)
                {
                    return false;
                }

                var lines = tail.Split('\n');
                for (var index = lines.Length - 1; index >= 0; index--)
                {
                    var line = lines[index].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (TryReadAssistantOutput(line, out var text) &&
                        !string.IsNullOrWhiteSpace(text))
                    {
                        taskLog = TraeUserPromptResolver.Summarize(text);
                        return taskLog.Length > 0;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        private static double ResolveCreditValue(string creditJson, string requestId)
        {
            try
            {
                using var document = JsonDocument.Parse(creditJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return 0D;
                }

                // 优先精确匹配本次请求的积分。
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, requestId, StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind == JsonValueKind.Number)
                        {
                            return property.Value.GetDouble();
                        }
                    }
                }

                // 未命中则取最后一条（最近完成的请求）。
                var last = 0D;
                var found = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        last = property.Value.GetDouble();
                        found = true;
                    }
                }

                return found ? last : 0D;
            }
            catch (JsonException)
            {
                return 0D;
            }
        }

        private static string ResolveSessionFile(string sessionId)
        {
            var projects = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".workbuddy", "projects");
            if (!Directory.Exists(projects))
            {
                return string.Empty;
            }

            foreach (var directory in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(directory, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string ReadLogTail(string path, int maxBytes)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0L, length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
            var text = reader.ReadToEnd();
            if (start > 0)
            {
                // 对齐到行首，避免首行 JSON 被截断。
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    text = text.Substring(firstNewline + 1);
                }
            }

            return text;
        }

        private static bool TryReadAssistantOutput(string line, out string text)
        {
            text = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!string.Equals(ReadString(root, "type"), "message", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!root.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var item in content.EnumerateArray())
                {
                    if (string.Equals(ReadString(item, "type"), "output_text", StringComparison.OrdinalIgnoreCase))
                    {
                        text = ReadString(item, "text");
                        return !string.IsNullOrWhiteSpace(text);
                    }
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
        }

        private static string DirectoryLeaf(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (normalized.Length == 0 || normalized.EndsWith(":", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var separator = normalized.LastIndexOf('\\');
            return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
        }

        private static string ResolveDatabase()
        {
            var database = DatabaseOverride;
            if (!string.IsNullOrWhiteSpace(database) && File.Exists(database))
            {
                return database;
            }

            database = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".workbuddy", "workbuddy.db");
            return File.Exists(database) ? database : string.Empty;
        }
    }
}
