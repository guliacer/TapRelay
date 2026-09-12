using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TapRelay
{
    /// <summary>
    /// 读取 ZCode 自身持久化的真实任务数据——与 Codex 直接读 .codex/sessions
    /// 会话日志、WorkBuddy 直接读 workbuddy.db 同一思路，而非依赖 Windows 系统通知。
    /// 数据源：
    ///   1) %USERPROFILE%\.zcode\cli\db\db.sqlite
    ///      session 表（id=sess_* 会话 → 真实任务标题 directory/title）
    ///      turn_usage 表（turn 的真实耗时 duration_ms 与 computed_total_tokens）
    ///      message 表（role=assistant 判定） + part 表（type=text 的助手正文）
    /// 仅当数据库不可用（独占锁/结构变化）时才回退 Windows toast。
    /// </summary>
    internal static class ZCodeSessionReader
    {
        // Microsoft.Data.Sqlite 3.1.32 不支持 "Default Timeout" 连接串关键字（5.0+ 才有），
        // 加了会抛 ArgumentException 导致整个读取失败（见 WorkBuddySessionReader 同类教训）。
        // db.sqlite 是 WAL 模式，只读查询毫秒级，无需超时设置。
        private const string ConnectionOptions = ";Mode=ReadOnly";

        // 单元测试注入的数据库覆盖路径；非空时替代 ~/.zcode/cli/db/db.sqlite。
        internal static string? DatabaseOverride { get; set; }

        private static string ResolveDatabase()
        {
            var database = DatabaseOverride;
            if (!string.IsNullOrWhiteSpace(database) && File.Exists(database))
            {
                return database;
            }

            database = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zcode", "cli", "db", "db.sqlite");
            return File.Exists(database) ? database : string.Empty;
        }

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
                    "SELECT title, directory FROM session WHERE id = @id LIMIT 1";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = sessionId;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                // 任务名称优先用真实会话标题；标题为空（如首轮尚未生成）时回退目录。
                var resolvedTitle = reader.IsDBNull(0) ? string.Empty : (reader.GetString(0) ?? string.Empty).Trim();
                if (resolvedTitle.Length == 0)
                {
                    resolvedTitle = reader.IsDBNull(1) ? string.Empty : (reader.GetString(1) ?? string.Empty).Trim();
                }

                title = resolvedTitle;
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
        /// 读取会话终态用量与耗时：turn_usage 表按 turnId 精确匹配（该 turn 的
        /// computed_total_tokens 与 duration_ms 是真实且未被日志脱敏的值）。
        /// 未命中则回退该会话最近一条 completed turn。tokenCount 为 0 时调用方忽略。
        /// </summary>
        internal static bool TryResolveTurnUsage(
            string sessionId,
            string turnId,
            out long durationMs,
            out long tokenCount)
        {
            durationMs = 0L;
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
                    "SELECT duration_ms, computed_total_tokens FROM turn_usage " +
                    "WHERE session_id = @sid AND turn_id = @tid LIMIT 1";
                var sidParameter = command.CreateParameter();
                sidParameter.ParameterName = "@sid";
                sidParameter.Value = sessionId;
                command.Parameters.Add(sidParameter);
                var tidParameter = command.CreateParameter();
                tidParameter.ParameterName = "@tid";
                tidParameter.Value = turnId;
                command.Parameters.Add(tidParameter);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    durationMs = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                    tokenCount = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                    return durationMs > 0L || tokenCount > 0L;
                }

                // 未精确命中（如 Desktop 事件只有 sessionId）时回退最近一条 completed turn。
                using var fallback = connection.CreateCommand();
                fallback.CommandText =
                    "SELECT duration_ms, computed_total_tokens FROM turn_usage " +
                    "WHERE session_id = @sid AND status = 'completed' " +
                    "ORDER BY completed_at DESC LIMIT 1";
                var fallbackSid = fallback.CreateParameter();
                fallbackSid.ParameterName = "@sid";
                fallbackSid.Value = sessionId;
                fallback.Parameters.Add(fallbackSid);

                using var fallbackReader = fallback.ExecuteReader();
                if (!fallbackReader.Read())
                {
                    return false;
                }

                durationMs = fallbackReader.IsDBNull(0) ? 0L : fallbackReader.GetInt64(0);
                tokenCount = fallbackReader.IsDBNull(1) ? 0L : fallbackReader.GetInt64(1);
                return durationMs > 0L || tokenCount > 0L;
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
        /// 读取会话真实日志：最后一条 role=assistant 且 type=text 的正文，经 Summarize 截断。
        /// 传 turnId（可选）时只取该 turn（message.data.anchor.turnId 匹配）的助手正文，避免
        /// 同会话多 turn 时取到其它轮次的内容。role 与 turnId 都存在于 message 表的 data JSON。
        /// </summary>
        internal static bool TryResolveTaskLog(string sessionId, out string taskLog)
        {
            return TryResolveTaskLog(sessionId, string.Empty, out taskLog);
        }

        internal static bool TryResolveTaskLog(string sessionId, string turnId, out string taskLog)
        {
            taskLog = string.Empty;
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
                // part 联查 message 取 role 与 turnId。part.sequence 只在单条 message
                // 内递增，不能单独用它判断会话中的最后一条正文；真实库中每条消息的
                // 文本 part 都可能是 sequence=3。用 message.sequence + 创建时间先定位
                // 最新消息，再用 part.sequence 定位该消息里的最后一个文本 part。
                // 用 json_extract 识别类型，兼容紧凑/带空格的 JSON。
                var where = "p.session_id = @sid AND json_extract(p.data, '$.type') = 'text' " +
                            "AND json_extract(m.data, '$.role') = 'assistant'";
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    where += " AND json_extract(m.data, '$.anchor.turnId') = @tid";
                }

                command.CommandText =
                    "SELECT p.message_id, p.data FROM part p " +
                    "JOIN message m ON m.id = p.message_id " +
                    "WHERE " + where + " " +
                    "ORDER BY COALESCE(m.sequence, m.time_created) DESC, " +
                    "m.time_created DESC, COALESCE(p.sequence, p.time_created) DESC, " +
                    "p.time_created DESC LIMIT 60";
                var sidParameter = command.CreateParameter();
                sidParameter.ParameterName = "@sid";
                sidParameter.Value = sessionId;
                command.Parameters.Add(sidParameter);
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    var tidParameter = command.CreateParameter();
                    tidParameter.ParameterName = "@tid";
                    tidParameter.Value = turnId;
                    command.Parameters.Add(tidParameter);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var raw = reader.IsDBNull(1) ? string.Empty : (reader.GetString(1) ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(raw) ||
                        !TryReadAssistantText(raw, out var text) ||
                        string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    taskLog = TraeUserPromptResolver.Summarize(text);
                    return taskLog.Length > 0;
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
        /// 读取该 turn 的第一条 role=user 且 type=text 的正文（当轮用户提问），用于让
        /// 通知标题随每次对话变化，而非固定为会话名。turnId 为空则取该会话最近一条
        /// 用户提问；过滤掉常见系统注入文本（TodoWrite 提醒、userselect 快照等），
        /// 避免标题被内部工具文案污染。返回内容经 Summarize 截断。
        /// </summary>
        internal static bool TryResolveTurnQuery(
            string sessionId, string turnId, out string query)
        {
            query = string.Empty;
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
                // 首个真实用户提问：role=user 的 text 正文，跳过系统注入的行
                // （TodoWrite 低字面量提醒、# userselect 快照）。tunId 为空时回退
                // 整个会话最近一条用户提问。
                var where = "p.session_id = @sid AND json_extract(p.data, '$.type') = 'text' " +
                            "AND json_extract(m.data, '$.role') = 'user' " +
                            "AND json_extract(p.data, '$.text') NOT LIKE '%TodoWrite%' " +
                            "AND json_extract(p.data, '$.text') NOT LIKE '%userselect%' " +
                            "AND length(json_extract(p.data, '$.text')) > 0";
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    where += " AND json_extract(m.data, '$.anchor.turnId') = @tid";
                }

                command.CommandText =
                    "SELECT p.data FROM part p " +
                    "JOIN message m ON m.id = p.message_id " +
                    "WHERE " + where + " " +
                    "ORDER BY COALESCE(m.sequence, m.time_created), m.time_created, " +
                    "COALESCE(p.sequence, p.time_created), p.time_created LIMIT 60";
                var sidParameter = command.CreateParameter();
                sidParameter.ParameterName = "@sid";
                sidParameter.Value = sessionId;
                command.Parameters.Add(sidParameter);
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    var tidParameter = command.CreateParameter();
                    tidParameter.ParameterName = "@tid";
                    tidParameter.Value = turnId;
                    command.Parameters.Add(tidParameter);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var raw = reader.IsDBNull(0) ? string.Empty : (reader.GetString(0) ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(raw) ||
                        !TryReadAssistantText(raw, out var text) ||
                        string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    query = TraeUserPromptResolver.Summarize(text);
                    return query.Length > 0;
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

        private static bool TryReadAssistantText(string partData, out string text)
        {
            text = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(partData);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(root, "type"), "text", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                text = ReadString(root, "text");
                return !string.IsNullOrWhiteSpace(text);
            }
            catch (JsonException)
            {
                return false;
            }
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
    }
}
