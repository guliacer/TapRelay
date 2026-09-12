using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace TapRelay
{
    /// <summary>
    /// 从豆包桌面端 sdk_storage 的 saman_*.log 中提取用户最近一次输入内容。
    ///
    /// 豆包与 Claude/Codex 不同，其 /chat/completion 网络请求日志（saman rlog）只包含请求头与
    /// 网络遥测，不含会话标题或对话正文；Local Storage / IndexedDB 中的会话标题又是 LevelDB
    /// 二进制、无稳定明文入口。唯一持续可读的用户内容来源是豆包地址栏自动补全日志：
    ///
    ///   [saman_omnibox] QueryAutocomplete input: 摄影拍白底图要怎么用三色卡
    ///
    /// 该行以 UTF-8 明文写在 saman_YYYY.MMDD.0.log 中，随用户每输入一个字符追加一条，
    /// 因此文件里最后一条 input 就是“当前最完整的那次提问”。豆包通常是单会话顺序执行，
    /// 用“最近一次输入”作为当前任务的会话内容，比显示 req_id 对用户有意义得多。
    /// </summary>
    internal static class DoubaoTaskContentSource
    {
        private const int TailReadBytes = 4 * 1024 * 1024;

        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包进程窗口标题。</summary>
        internal static string? SessionTitleOverride;
        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包窗口正文。</summary>
        internal static string? AssistantMessageOverride;
        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包活动会话。</summary>
        internal static string? ActiveConversationIdOverride;

        internal static string TryReadLatestInput(string logDirectory)
        {
            var newest = FindNewestSamanLog(logDirectory);
            if (string.IsNullOrWhiteSpace(newest))
            {
                return string.Empty;
            }

            try
            {
                var length = new FileInfo(newest).Length;
                using var stream = new FileStream(
                    newest,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var offset = Math.Max(0, length - TailReadBytes);
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[length - offset];
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = stream.Read(buffer, read, buffer.Length - read);
                    if (count <= 0)
                    {
                        break;
                    }
                    read += count;
                }

                var tail = Encoding.UTF8.GetString(buffer, 0, read);
                // 兼容文件头被截断：用可读子串（\uFFFD 表示截断的半个字符，不影响匹配）。
                foreach (Match match in Regex.Matches(
                    tail,
                    "QueryAutocomplete input:\\s*(?<text>[^\\n,]{2,})",
                    RegexOptions.CultureInvariant))
                {
                    // 覆盖最后一条；最后一条通常是最完整的输入。
                }

                Match last = null;
                foreach (Match match in Regex.Matches(
                    tail,
                    "QueryAutocomplete input:\\s*(?<text>[^\\n,]{2,})",
                    RegexOptions.CultureInvariant))
                {
                    last = match;
                }

                if (last != null)
                {
                    var text = last.Groups["text"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !IsGarbageInput(text))
                    {
                        return text.Length <= 120 ? text : text.Substring(0, 120);
                    }
                }
            }
            catch (IOException)
            {
                // 豆包运行中可能正在写日志；读取失败时跳过，回落旧文案即可。
            }
            catch (UnauthorizedAccessException)
            {
            }

            return string.Empty;
        }

        /// <summary>
        /// 读取豆包主窗口标题中的当前会话名。新版豆包窗口标题格式为
        /// "<会话名> - 豆包"（如 "Ubuntu项目外网访问 - 豆包"），所有发起方式
        /// （地址栏或会话内）都能拿到真实任务标题，作为 omnibox 输入之外的
        /// 兜底内容来源。
        /// </summary>
        internal static string TryReadSessionTitle()
        {
            if (SessionTitleOverride != null)
            {
                return SessionTitleOverride;
            }

            try
            {
                foreach (var process in Process.GetProcessesByName("Doubao"))
                {
                    string title;
                    try
                    {
                        title = process.MainWindowTitle;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    const string suffix = " - 豆包";
                    if (title.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        title = title.Substring(0, title.Length - suffix.Length);
                    }

                    title = title.Trim();
                    if (title.Length > 0)
                    {
                        return title.Length <= 80 ? title : title.Substring(0, 80);
                    }
                }
            }
            catch (Exception)
            {
                // 进程枚举失败静默返回。
            }

            return string.Empty;
        }

        /// <summary>
        /// 读取豆包当前活动会话的稳定 UI Automation ID。
        /// 豆包侧栏的活动会话是 conversation_... 超链接；它比窗口标题稳定，
        /// 也能让手机端的继续指令在多个会话之间回到正确的会话。
        /// </summary>
        internal static string TryReadActiveConversationId()
        {
            if (ActiveConversationIdOverride != null)
            {
                return ActiveConversationIdOverride;
            }

            try
            {
                foreach (var process in Process.GetProcessesByName("Doubao"))
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    var links = window.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.ControlTypeProperty,
                            ControlType.Hyperlink));
                    for (var index = 0; index < links.Count; index++)
                    {
                        var current = links[index].Current;
                        var className = current.ClassName ?? string.Empty;
                        if (className.IndexOf("conversation-item", StringComparison.OrdinalIgnoreCase) < 0 ||
                            className.IndexOf("e2e-test-active", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var automationId = (current.AutomationId ?? string.Empty).Trim();
                        if (automationId.Length > 0)
                        {
                            return automationId;
                        }
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception)
            {
                // Accessibility is best effort; completion parsing must continue.
            }

            return string.Empty;
        }

        /// <summary>
        /// 读取豆包当前窗口中最后一条助手正文。
        /// 豆包的 rlog 只记录网络响应元数据，不包含 SSE 正文；桌面 WebView
        /// 暴露了可访问性树，因此在完成事件到达后从当前会话 DOM 读取可见文本。
        /// </summary>
        internal static string TryReadLatestAssistantMessage()
        {
            return TryReadAssistantMessageForInput(string.Empty);
        }

        /// <summary>
        /// 读取与指定用户输入对应的下一条助手正文。豆包会话可能包含多轮历史，
        /// 先按输入匹配用户行，避免把其它轮次的回答当作当前任务日志。
        /// </summary>
        internal static string TryReadAssistantMessageForInput(string userInput)
        {
            if (AssistantMessageOverride != null)
            {
                return AssistantMessageOverride;
            }

            try
            {
                foreach (var process in Process.GetProcessesByName("Doubao"))
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    var document = window.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    if (document == null)
                    {
                        continue;
                    }

                    var rows = new List<ConversationRow>();
                    CollectConversationRows(document, TreeWalker.RawViewWalker, null, rows, 0, false, false);
                    var matchedUserIndex = FindMatchingUserRow(rows, userInput);
                    if (matchedUserIndex >= 0)
                    {
                        for (var index = matchedUserIndex + 1; index < rows.Count; index++)
                        {
                            var row = rows[index];
                            if (row.IsUser)
                            {
                                break;
                            }

                            var text = NormalizeAssistantText(row.Text);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text.Length <= 4000 ? text : text.Substring(0, 4000) + "...";
                            }
                        }
                    }

                    // A non-empty user input is a correlation key, not a hint. If it
                    // is absent from the rendered conversation, returning another
                    // turn would put a real answer from the wrong task on the card.
                    if (!string.IsNullOrWhiteSpace(userInput))
                    {
                        continue;
                    }

                    for (var index = rows.Count - 1; index >= 0; index--)
                    {
                        var row = rows[index];
                        if (row.IsUser)
                        {
                            continue;
                        }

                        var text = NormalizeAssistantText(row.Text);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Length <= 4000 ? text : text.Substring(0, 4000) + "...";
                        }
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception)
            {
                // Accessibility is a best-effort fallback; it must never block
                // the terminal notification path.
            }

            return string.Empty;
        }

        private static int FindMatchingUserRow(List<ConversationRow> rows, string userInput)
        {
            var input = NormalizeForMatch(userInput);
            if (input.Length == 0)
            {
                return -1;
            }

            var bestIndex = -1;
            var bestScore = 0;
            for (var index = 0; index < rows.Count; index++)
            {
                if (!rows[index].IsUser)
                {
                    continue;
                }

                var userText = NormalizeForMatch(string.Join(" ", rows[index].Text));
                if (userText.Length == 0)
                {
                    continue;
                }

                var score = 0;
                if (userText.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    input.IndexOf(userText, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score = Math.Min(input.Length, userText.Length) + 1000;
                }
                else
                {
                    var commonPrefix = 0;
                    var limit = Math.Min(input.Length, userText.Length);
                    while (commonPrefix < limit && input[commonPrefix] == userText[commonPrefix])
                    {
                        commonPrefix++;
                    }

                    if (commonPrefix >= 8)
                    {
                        score = commonPrefix;
                    }
                }

                // Prefer the latest matching turn when the same prompt appears
                // more than once in a long-lived conversation.
                if (score > 0 && score >= bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        private static string NormalizeForMatch(string value)
        {
            return string.Join("", (value ?? string.Empty)
                .Where(ch => !char.IsWhiteSpace(ch) && ch != '\u3000'))
                .Trim();
        }

        private sealed class ConversationRow
        {
            internal bool IsUser { get; set; }
            internal List<string> Text { get; } = new List<string>();
        }

        private static void CollectConversationRows(
            AutomationElement element,
            TreeWalker walker,
            ConversationRow? currentRow,
            List<ConversationRow> rows,
            int depth,
            bool directChildOfRow,
            bool insideSuggestion)
        {
            if (element == null || depth > 24)
            {
                return;
            }

            var current = element.Current;
            var className = current.ClassName ?? string.Empty;
            var isRow = className.IndexOf("v_list_row", StringComparison.OrdinalIgnoreCase) >= 0;
            var isSuggestion = IsSuggestionSubtree(className);
            insideSuggestion |= isSuggestion;
            if (isRow)
            {
                currentRow = new ConversationRow();
                rows.Add(currentRow);
            }

            if (currentRow != null && !insideSuggestion)
            {
                if (directChildOfRow &&
                    className.IndexOf("container-enLQFx", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    currentRow.IsUser = true;
                }

                if (!string.IsNullOrWhiteSpace(current.Name) && IsConversationText(element))
                {
                    currentRow.Text.Add(current.Name.Trim());
                }
            }

            var child = walker.GetFirstChild(element);
            var childCount = 0;
            while (child != null && childCount++ < 1200)
            {
                CollectConversationRows(
                    child,
                    walker,
                    currentRow,
                    rows,
                    depth + 1,
                    isRow,
                    insideSuggestion);
                child = walker.GetNextSibling(child);
            }
        }

        /// <summary>
        /// 豆包会把输入框联想项挂在最近一条会话行下面；它们不是助手正文，
        /// 且必须连同后代节点一起排除，避免联想词污染任务日志。
        /// </summary>
        internal static bool IsSuggestionSubtree(string className)
        {
            return (className ?? string.Empty).IndexOf(
                "suggest-list-item",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsConversationText(AutomationElement element)
        {
            var type = element.Current.ControlType;
            return type == ControlType.Text ||
                   type == ControlType.ListItem ||
                   type == ControlType.DataItem;
        }

        private static string NormalizeAssistantText(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var value in values)
            {
                var text = (value ?? string.Empty).Trim();
                if (text.Length == 0 || text == "朗读" || text == "复制" ||
                    text.StartsWith("搜索 ", StringComparison.Ordinal) ||
                    text.StartsWith("搜索", StringComparison.Ordinal) && text.Contains("关键词", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(text))
                {
                    parts.Add(text);
                }
            }

            return string.Join(" ", parts);
        }

        private static string FindNewestSamanLog(string logDirectory)
        {
            if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
            {
                return string.Empty;
            }

            string best = string.Empty;
            var bestTime = DateTime.MinValue;
            foreach (var path in Directory.EnumerateFiles(logDirectory, "saman_*.log"))
            {
                if (Path.GetFileName(path).IndexOf("netlog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Path.GetFileName(path).IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    var time = File.GetLastWriteTimeUtc(path);
                    if (time > bestTime)
                    {
                        bestTime = time;
                        best = path;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return best;
        }

        /// <summary>
        /// 地址栏自动补全偶尔会出现半截无意义输入（例如只有拼音字母或 1-2 个汉字）。
        /// 过滤掉这些无法作为任务内容的片段。
        /// </summary>
        private static bool IsGarbageInput(string text)
        {
            var cjkCount = text.Count(char.IsSurrogate) == 0
                ? text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF)
                : text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            var trimmed = text.Trim();
            if (trimmed.Length < 2)
            {
                return true;
            }

            // 全英文/数字的自动补全（如 url、英文单词）不是对话内容。
            if (cjkCount == 0)
            {
                return true;
            }

            // 纯拼音输入（a-z 开头且无汉字）不是有效提问。
            return cjkCount < 2 && trimmed.Length > 8;
        }
    }
}
