using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace TapRelay
{
    /// <summary>
    /// 读取各智能体应用发出的 Windows 系统通知（toast）正文——统一数据源。
    /// TraeCode/TraeWork/ZCode/OpenCode/WorkBuddy 等桌面应用在任务完成/失败/等待时，
    /// 除了写各自的日志外还会发一条 Windows toast，标题含任务名（如
    /// "任务完成 · 适配 TraeCode CN"、"OpenCode · 灵感库AI大模型配置自动匹配"、
    /// WorkBuddy 的「任务已完成」+「「<任务名>」 已成功完成，您可以在编辑器中查看结果。」），
    /// 正文是 Agent 回复摘要。Windows 把所有 toast 持久化在：
    ///   %LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db
    /// Notification.HandlerId 关联 NotificationHandler.RecordId，PrimaryId 区分应用。
    /// ArrivalTime 是 FILETIME（UTC，100ns），按完成事件时间选择最近候选，避免把
    /// 同一来源的上一条任务通知错配给当前任务。用 SQL 查询（只读模式），Payload 为 toast XML，
    /// 两种模板：ToastText02（text 带 id，1=标题 2=正文）与 ToastGeneric
    /// （text 无 id，按顺序 1=标题 2=正文）。
    /// 新适配应用时优先接入本方案（优先级：toast 真实内容 > 应用自有日志 > 固定文案）。
    /// </summary>
    internal static class ToastNotificationReader
    {
        // 普通应用的 Toast 兜底窗口，保持既有兼容行为。
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);
        // TraeCode 的真实日志必须严格接近终态，否则不能证明属于当前回合。
        private static readonly TimeSpan TraeCorrelationMaxAge = TimeSpan.FromSeconds(15);
        private const int CandidateLimit = 64;

        // 这些正文是状态模板或旧版本 TapRelay 拼出的用户提示词，不是 Agent 结果。
        private static readonly string[] NonTaskLogBodies =
        {
            "任务已完成",
            "任务完成",
            "智能体任务已完成",
            "任务失败",
            "任务待处理",
            "需要确认",
            "请到电脑端查看详细日志",
            "请到电脑端查看失败详情",
            "请到电脑端进行处理",
            "Task completed",
            "Agent task completed",
            "Task failed",
            "Waiting for your response"
        };

        // 测试只读查询使用；生产环境保持默认系统通知数据库路径。
        internal static string? DatabaseOverride { get; set; }

        // source → Windows 通知 PrimaryId 列表（同一应用可能有多个 handler，
        // 如 OpenCode 的新旧版本，全部尝试取最新）。
        private static readonly Dictionary<string, string[]> PrimaryIds =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "trae", new[] { "ByteDance.TraeCN" } },
                { "traework", new[] { "ByteDance.TraeSoloCN" } },
                { "zcode", new[] { "electron.app.ZCode" } },
                { "opencode", new[] { "OpenCode", "ai.opencode.desktop" } },
                // WorkBuddy 是 Electron 应用，任务完成/失败/等待时同样发 Windows toast
                // （ToastText02：text id=1 标题「任务已完成」、id=2 正文「「<任务名>」 已成功完成…」）。
                // 本映射是 WorkBuddy 通知的兜底源：主源为 workbuddy.db sessions 表
                // （WorkBuddySessionReader），仅当会话库不可得时才回退到本 toast。
                { "workbuddy", new[] { "WorkBuddy.WorkBuddy" } }
            };

        internal static bool TryReadLatest(
            string source,
            DateTime eventTimeUtc,
            out string title,
            out string body,
            TimeSpan? maxAge = null)
        {
            return TryReadCandidate(source, eventTimeUtc, out title, out body, maxAge, false);
        }

        /// <summary>
        /// 读取与完成事件时间接近、且正文不是状态模板的 Toast。
        /// TraeCode CN 的 renderer.log 不含 Agent 回复正文，因此该方法是其唯一
        /// 可接受的完成日志来源；找不到时调用方必须跳过通知，不能伪造正文。
        /// </summary>
        internal static bool TryReadLatestRealTaskLog(
            string source,
            DateTime eventTimeUtc,
            out string title,
            out string body,
            TimeSpan? maxAge = null)
        {
            return TryReadCandidate(source, eventTimeUtc, out title, out body, maxAge, true);
        }

        private static bool TryReadCandidate(
            string source,
            DateTime eventTimeUtc,
            out string title,
            out string body,
            TimeSpan? maxAge,
            bool requireRealTaskLog)
        {
            title = string.Empty;
            body = string.Empty;
            if (!PrimaryIds.TryGetValue(source ?? string.Empty, out var primaryIds))
            {
                return false;
            }

            var age = maxAge ?? (requireRealTaskLog ? TraeCorrelationMaxAge : MaxAge);
            if (age < TimeSpan.Zero)
            {
                return false;
            }

            var eventUtc = eventTimeUtc.Kind == DateTimeKind.Utc
                ? eventTimeUtc
                : eventTimeUtc.ToUniversalTime();
            var eventFileTime = eventUtc.ToFileTimeUtc();
            var minFileTime = eventUtc.Subtract(age).ToFileTimeUtc();
            var maxFileTime = eventUtc.Add(age).ToFileTimeUtc();

            var database = DatabaseOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");
            if (!File.Exists(database))
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + database + ";Mode=ReadOnly");
                connection.Open();

                var bestFileTime = 0L;
                var bestDistance = long.MaxValue;
                var bestTitle = string.Empty;
                var bestBody = string.Empty;
                foreach (var primaryId in primaryIds)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT n.Payload, n.ArrivalTime FROM Notification n " +
                        "JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                        "WHERE h.PrimaryId = @primaryId " +
                        "AND n.ArrivalTime >= @minFileTime AND n.ArrivalTime <= @maxFileTime " +
                        "ORDER BY n.ArrivalTime DESC LIMIT " + CandidateLimit;
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@primaryId";
                    parameter.Value = primaryId;
                    command.Parameters.Add(parameter);
                    var minParameter = command.CreateParameter();
                    minParameter.ParameterName = "@minFileTime";
                    minParameter.Value = minFileTime;
                    command.Parameters.Add(minParameter);
                    var maxParameter = command.CreateParameter();
                    maxParameter.ParameterName = "@maxFileTime";
                    maxParameter.Value = maxFileTime;
                    command.Parameters.Add(maxParameter);

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var payload = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        var arrivalFileTime = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        if (string.IsNullOrWhiteSpace(payload) || arrivalFileTime <= 0)
                        {
                            continue;
                        }

                        if (!TryParseToastPayload(payload, out var candidateTitle, out var candidateBody) ||
                            (requireRealTaskLog && !IsRealTaskLog(candidateTitle, candidateBody)))
                        {
                            continue;
                        }

                        var distance = arrivalFileTime >= eventFileTime
                            ? arrivalFileTime - eventFileTime
                            : eventFileTime - arrivalFileTime;
                        if (distance > bestDistance ||
                            (distance == bestDistance && arrivalFileTime <= bestFileTime))
                        {
                            continue;
                        }

                        bestFileTime = arrivalFileTime;
                        bestDistance = distance;
                        bestTitle = candidateTitle;
                        bestBody = candidateBody;
                    }
                }

                if (bestFileTime <= 0 || string.IsNullOrWhiteSpace(bestTitle))
                {
                    return false;
                }

                title = bestTitle;
                body = bestBody;
                return true;
            }
            catch (SqliteException)
            {
                // 数据库被系统独占或结构变化时静默回退。
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
        /// 解析 toast XML，两种模板都支持：
        ///   ToastText02：text 带 id 属性，id=1 标题、id=2 正文；
        ///   ToastGeneric：text 无 id 属性，按出现顺序第一个为标题、第二个为正文。
        /// </summary>
        internal static bool TryParseToastPayload(string payload, out string title, out string body)
        {
            title = string.Empty;
            body = string.Empty;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var document = XDocument.Parse(payload);
                var texts = document.Descendants("text")
                    .Select(t => new { Id = (string?)t.Attribute("id"), Value = t.Value?.Trim() ?? string.Empty })
                    .Where(t => t.Value.Length > 0)
                    .ToList();
                if (texts.Count == 0)
                {
                    return false;
                }

                if (texts.Any(t => t.Id != null))
                {
                    // id 模板（ToastText02）。
                    title = texts.FirstOrDefault(t => t.Id == "1")?.Value ?? string.Empty;
                    body = texts.FirstOrDefault(t => t.Id == "2")?.Value ?? string.Empty;
                }
                else
                {
                    // 位置模板（ToastGeneric）：依次为标题、正文。
                    title = texts[0].Value;
                    if (texts.Count > 1)
                    {
                        body = texts[1].Value;
                    }
                }

                return title.Length > 0;
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// toast 内容 → 通知正文：标题（含任务名与状态）+ 空行 + 正文摘要。
        /// 正文复用 TraeUserPromptResolver.Summarize 截断。
        /// </summary>
        internal static string BuildMessage(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var summary = TraeUserPromptResolver.Summarize(body);
            return summary.Length == 0
                ? title
                : title + "\n\n" + summary;
        }

        internal static bool IsRealTaskLog(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            var normalizedBody = CollapseWhitespace(body);
            if (normalizedBody.Length == 0 ||
                normalizedBody.StartsWith("指令：", StringComparison.Ordinal) ||
                normalizedBody.StartsWith("指令:", StringComparison.Ordinal))
            {
                return false;
            }

            if (NonTaskLogBodies.Any(item =>
                    string.Equals(item, normalizedBody, StringComparison.OrdinalIgnoreCase)) ||
                normalizedBody.IndexOf("请到电脑端查看", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return !string.Equals(
                CollapseWhitespace(title), normalizedBody, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryBuildRealTaskMessage(
            string title,
            string body,
            out string message,
            out string taskLog)
        {
            message = string.Empty;
            taskLog = string.Empty;
            if (string.IsNullOrWhiteSpace(title) || !IsRealTaskLog(title, body))
            {
                return false;
            }

            taskLog = body.Trim();
            message = BuildMessage(title.Trim(), taskLog);
            return message.Length > 0;
        }

        private static string CollapseWhitespace(string value)
        {
            return string.Join(" ", (value ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
