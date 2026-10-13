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
        internal const string IndexedDbOverrideVariable = "TAPRELAY_DOUBAO_DB";

        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包进程窗口标题。</summary>
        internal static string? SessionTitleOverride = null;
        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包窗口正文。</summary>
        internal static string? AssistantMessageOverride = null;
        /// <summary>测试注入用：非 null 时优先返回，隔离真实豆包活动会话。</summary>
        internal static string? ActiveConversationIdOverride = null;
        /// <summary>测试注入用：非空时覆盖豆包 IndexedDB 目录。</summary>
        internal static string? IndexedDbDirectoryOverride = null;

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
                // 取最后一条;最后一条通常是最完整的输入。

                Match? last = null;
                foreach (Match? match in Regex.Matches(
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
            return TryReadAssistantMessageForInput(userInput, out _);
        }

        internal static string TryReadAssistantMessageForInput(string userInput, out string contentSource)
        {
            contentSource = string.Empty;
            if (AssistantMessageOverride != null)
            {
                contentSource = "test-override";
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
                                contentSource = "rendered-ui";
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
                            contentSource = "rendered-ui";
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

            // Chromium 子进程通常没有可用的 MainWindowHandle，即使豆包已经
            // 把完整答案渲染到当前会话，UI Automation 也会读不到正文。此时
            // 从豆包自己的 IndexedDB 只读快照补位；不读取窗口标题或用户输入。
            var indexedDbText = TryReadAssistantMessageFromIndexedDb(userInput);
            if (!string.IsNullOrWhiteSpace(indexedDbText))
            {
                contentSource = "indexeddb";
            }

            return indexedDbText;
        }

        internal static string TryReadAssistantMessageFromIndexedDb(string userInput)
        {
            var directory = ResolveIndexedDbDirectory();
            return DoubaoIndexedDbMessageReader.TryRead(directory, userInput);
        }

        internal static string BuildCompletedMessage(string taskLog)
        {
            if (string.IsNullOrWhiteSpace(taskLog))
            {
                return string.Empty;
            }

            var normalized = string.Join(
                " ",
                taskLog.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            const int maxSummaryLength = 240;
            return normalized.Length <= maxSummaryLength
                ? normalized
                : normalized.Substring(0, maxSummaryLength).TrimEnd() + "...";
        }

        private static string ResolveIndexedDbDirectory()
        {
            var overridePath = IndexedDbDirectoryOverride;
            if (string.IsNullOrWhiteSpace(overridePath))
            {
                overridePath = Environment.GetEnvironmentVariable(IndexedDbOverrideVariable);
            }

            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath.Trim());
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(
                localAppData,
                "Doubao",
                "User Data",
                "Default",
                "IndexedDB",
                "chrome_doubao-chat_0.indexeddb.leveldb");
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

    /// <summary>
    /// 豆包 Chromium IndexedDB 的只读读取器。豆包的数据库值是 LevelDB
    /// WriteBatch/table + V8 structured-clone 混合格式，不能按 JSON 或文本文件
    /// 直接读取。这里只提取消息对象中的稳定字段，避免把用户输入和标题当答案。
    /// </summary>
    internal static class DoubaoIndexedDbMessageReader
    {
        private const int BlockSize = 32 * 1024;
        private const int TableFooterLength = 48;
        private const int BlockTrailerLength = 5;
        private const ulong TableMagic = 0xdb4775248b80fb57UL;
        private const byte RecordFull = 1;
        private const byte RecordFirst = 2;
        private const byte RecordMiddle = 3;
        private const byte RecordLast = 4;
        private const int MaxTaskLogLength = 4000;

        internal static string TryRead(string directory, string userInput)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            var candidates = new List<AssistantCandidate>();
            try
            {
                var sourceOrder = 0L;
                var tables = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => IsTableFile(path))
                    .OrderBy(path => ParseFileNumber(path))
                    .ToArray();
                foreach (var path in tables)
                {
                    ReadTable(path, value => CollectCandidates(value, userInput, candidates, sourceOrder++));
                }

                var logs = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => ParseFileNumber(path))
                    .ToArray();
                foreach (var path in logs)
                {
                    ReadLog(path, value => CollectCandidates(value, userInput, candidates, sourceOrder++));
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }

            return candidates
                .OrderByDescending(candidate => candidate.SourceOrder)
                .ThenByDescending(candidate => candidate.Offset)
                .Select(candidate => candidate.Text)
                .FirstOrDefault() ?? string.Empty;
        }

        internal static string TryReadAssistantMessageFromRawValues(
            IEnumerable<byte[]> values,
            string userInput)
        {
            var candidates = new List<AssistantCandidate>();
            var sourceOrder = 0L;
            foreach (var value in values ?? Enumerable.Empty<byte[]>())
            {
                CollectCandidates(value, userInput, candidates, sourceOrder++);
            }

            return candidates
                .OrderByDescending(candidate => candidate.SourceOrder)
                .ThenByDescending(candidate => candidate.Offset)
                .Select(candidate => candidate.Text)
                .FirstOrDefault() ?? string.Empty;
        }

        private static void CollectCandidates(
            byte[] value,
            string userInput,
            List<AssistantCandidate> candidates,
            long sourceOrder = 0L)
        {
            if (value == null || value.Length == 0 ||
                !ContainsAscii(value, "user_type") ||
                !ContainsAscii(value, "text_block"))
            {
                return;
            }

            var promptPositions = FindPromptPositions(value, userInput);
            if (!string.IsNullOrWhiteSpace(userInput) && promptPositions.Count == 0)
            {
                return;
            }

            var cursor = 0;
            while (TryFindField(value, "user_type", cursor, value.Length, out var userTypeOffset, out var userTypeValueOffset))
            {
                var nextCursor = userTypeOffset + 1;
                var end = TryFindField(value, "user_type", nextCursor, value.Length, out var nextUserType, out _)
                    ? nextUserType
                    : value.Length;
                cursor = nextCursor;

                if (!TryReadInt32(value, userTypeValueOffset, end, out var userType) ||
                    (userType != 4 && userType != 3) ||
                    !HasCompletedMarker(value, userTypeOffset, end))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(userInput) &&
                    !promptPositions.Any(position => position < userTypeOffset))
                {
                    continue;
                }

                var text = ReadTextBlock(value, userTypeOffset, end);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    candidates.Add(new AssistantCandidate(
                        text,
                        sourceOrder,
                        userTypeOffset));
                }
            }
        }

        private static string ReadTextBlock(byte[] value, int start, int end)
        {
            var cursor = start;
            var parts = new List<string>();
            while (TryFindField(value, "text_block", cursor, end, out var blockOffset, out _))
            {
                cursor = blockOffset + 1;
                if (!TryFindField(value, "text", cursor, Math.Min(end, blockOffset + 160), out _, out var textValueOffset))
                {
                    continue;
                }

                if (!TryReadSerializedString(value, textValueOffset, end, out var text) ||
                    string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (!parts.Contains(text, StringComparer.Ordinal))
                {
                    parts.Add(text.Trim());
                }
            }

            var joined = string.Join("\n", parts).Trim();
            if (joined.Length <= MaxTaskLogLength)
            {
                return joined;
            }

            return joined.Substring(0, MaxTaskLogLength).TrimEnd() + "...";
        }

        private static bool HasCompletedMarker(byte[] value, int start, int end)
        {
            var cursor = start;
            while (TryFindField(value, "is_finish", cursor, end, out var fieldOffset, out var valueOffset))
            {
                cursor = fieldOffset + 1;
                if (valueOffset >= end)
                {
                    continue;
                }

                var tag = value[valueOffset];
                if (tag == (byte)'T')
                {
                    return true;
                }

                if (tag == (byte)'I' && TryReadVarint(value, valueOffset + 1, end, out var number, out _) && number != 0)
                {
                    return true;
                }

                if ((tag == (byte)'"' || tag == (byte)'c') &&
                    TryReadSerializedString(value, valueOffset, end, out var text) &&
                    (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     text.Equals("success", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadInt32(byte[] value, int offset, int end, out int result)
        {
            result = 0;
            if (offset >= end || value[offset] != (byte)'I' ||
                !TryReadVarint(value, offset + 1, end, out var number, out _))
            {
                return false;
            }

            result = unchecked((int)number);
            return true;
        }

        private static bool TryReadSerializedString(
            byte[] value,
            int offset,
            int end,
            out string result)
        {
            result = string.Empty;
            if (offset >= end)
            {
                return false;
            }

            // Chromium may insert a padding byte between an object property and
            // its V8 string tag. It is not part of the string value.
            while (offset < end && value[offset] == 0)
            {
                offset++;
            }

            if (offset >= end)
            {
                return false;
            }

            var tag = value[offset];
            if (tag != (byte)'"' && tag != (byte)'c')
            {
                return false;
            }

            if (!TryReadVarint(value, offset + 1, end, out var length, out var dataOffset) ||
                length > int.MaxValue)
            {
                return false;
            }

            if (tag == (byte)'c' && length > int.MaxValue / 2)
            {
                return false;
            }

            var byteLength = tag == (byte)'c' ? (int)length * 2 : (int)length;
            if (dataOffset < 0 || byteLength < 0 || dataOffset + byteLength > end)
            {
                return false;
            }

            try
            {
                result = tag == (byte)'c'
                    ? Encoding.Unicode.GetString(value, dataOffset, byteLength)
                    : Encoding.UTF8.GetString(value, dataOffset, byteLength);
                return !string.IsNullOrWhiteSpace(result);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static List<int> FindPromptPositions(byte[] value, string userInput)
        {
            var positions = new List<int>();
            if (string.IsNullOrWhiteSpace(userInput))
            {
                return positions;
            }

            AddMatches(value, Encoding.UTF8.GetBytes(userInput), positions);
            AddMatches(value, Encoding.Unicode.GetBytes(userInput), positions);
            return positions;
        }

        private static void AddMatches(byte[] value, byte[] needle, List<int> positions)
        {
            if (needle.Length == 0 || needle.Length > value.Length)
            {
                return;
            }

            for (var index = 0; index <= value.Length - needle.Length; index++)
            {
                var matched = true;
                for (var offset = 0; offset < needle.Length; offset++)
                {
                    if (value[index + offset] != needle[offset])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    positions.Add(index);
                }
            }
        }

        private static bool ContainsAscii(byte[] value, string text)
        {
            var needle = Encoding.ASCII.GetBytes(text);
            var positions = new List<int>();
            AddMatches(value, needle, positions);
            return positions.Count > 0;
        }

        private static bool TryFindField(
            byte[] value,
            string name,
            int start,
            int end,
            out int fieldOffset,
            out int valueOffset)
        {
            fieldOffset = -1;
            valueOffset = -1;
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var markerLength = nameBytes.Length <= 127 ? 2 + nameBytes.Length : 0;
            if (markerLength == 0)
            {
                return false;
            }

            for (var index = Math.Max(0, start); index + markerLength < end; index++)
            {
                if (value[index] != (byte)'"' || value[index + 1] != nameBytes.Length)
                {
                    continue;
                }

                var matched = true;
                for (var offset = 0; offset < nameBytes.Length; offset++)
                {
                    if (value[index + 2 + offset] != nameBytes[offset])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    fieldOffset = index;
                    valueOffset = index + markerLength;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadVarint(
            byte[] value,
            int offset,
            int end,
            out ulong result,
            out int nextOffset)
        {
            result = 0;
            nextOffset = offset;
            var shift = 0;
            while (nextOffset < end && shift <= 63)
            {
                var current = value[nextOffset++];
                result |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        private static bool IsTableFile(string path)
        {
            return path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".sst", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseFileNumber(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return int.TryParse(name, out var number) ? number : int.MaxValue;
        }

        private static void ReadLog(string path, Action<byte[]> onValue)
        {
            var bytes = ReadSharedBytes(path);
            if (bytes == null)
            {
                return;
            }

            using var fragmented = new MemoryStream();
            var offset = 0;
            while (offset + 7 <= bytes.Length)
            {
                var blockEnd = Math.Min(bytes.Length, ((offset / BlockSize) + 1) * BlockSize);
                if (blockEnd - offset < 7)
                {
                    offset = blockEnd;
                    continue;
                }

                var length = bytes[offset + 4] | bytes[offset + 5] << 8;
                var type = bytes[offset + 6];
                if (length == 0)
                {
                    offset = blockEnd;
                    continue;
                }

                if (offset + 7 + length > blockEnd || offset + 7 + length > bytes.Length)
                {
                    break;
                }

                if (type == RecordFull)
                {
                    EmitWriteBatch(bytes, offset + 7, length, onValue);
                }
                else if (type == RecordFirst)
                {
                    fragmented.SetLength(0);
                    fragmented.Write(bytes, offset + 7, length);
                }
                else if (type == RecordMiddle)
                {
                    fragmented.Write(bytes, offset + 7, length);
                }
                else if (type == RecordLast)
                {
                    fragmented.Write(bytes, offset + 7, length);
                    EmitWriteBatch(fragmented.ToArray(), 0, (int)fragmented.Length, onValue);
                    fragmented.SetLength(0);
                }

                offset += 7 + length;
            }
        }

        private static void EmitWriteBatch(byte[] bytes, int offset, int length, Action<byte[]> onValue)
        {
            var end = offset + length;
            if (length < 12 || end > bytes.Length)
            {
                return;
            }

            var cursor = offset + 8;
            var count = ReadUInt32(bytes, cursor);
            cursor += 4;
            for (var index = 0; index < count && cursor < end; index++)
            {
                if (cursor >= end)
                {
                    return;
                }

                var operation = bytes[cursor++];
                if (!TryReadVarint(bytes, cursor, end, out var keyLength, out cursor) ||
                    keyLength > int.MaxValue || cursor + (long)keyLength > end)
                {
                    return;
                }

                cursor += (int)keyLength;
                if (operation != 1)
                {
                    continue;
                }

                if (!TryReadVarint(bytes, cursor, end, out var valueLength, out cursor) ||
                    valueLength > int.MaxValue || cursor + (long)valueLength > end)
                {
                    return;
                }

                var value = new byte[(int)valueLength];
                Buffer.BlockCopy(bytes, cursor, value, 0, value.Length);
                cursor += value.Length;
                onValue(value);
            }
        }

        private static void ReadTable(string path, Action<byte[]> onValue)
        {
            var table = ReadSharedBytes(path);
            if (table == null || table.Length < TableFooterLength)
            {
                return;
            }

            var footerOffset = table.Length - TableFooterLength;
            if (ReadUInt64(table, footerOffset + 40) != TableMagic)
            {
                return;
            }

            var handleOffset = footerOffset + 20;
            if (!TryReadBlockHandle(table, ref handleOffset, out var indexOffset, out var indexSize))
            {
                return;
            }

            var indexBlock = ReadBlock(table, indexOffset, indexSize);
            if (indexBlock == null)
            {
                return;
            }

            foreach (var indexEntry in ReadBlockEntries(indexBlock))
            {
                var dataHandleOffset = 0;
                if (!TryReadBlockHandle(indexEntry.Value, ref dataHandleOffset, out var dataOffset, out var dataSize))
                {
                    continue;
                }

                var dataBlock = ReadBlock(table, dataOffset, dataSize);
                if (dataBlock == null)
                {
                    continue;
                }

                foreach (var entry in ReadBlockEntries(dataBlock))
                {
                    onValue(entry.Value);
                }
            }
        }

        private static byte[]? ReadSharedBytes(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= 0 || stream.Length > int.MaxValue)
                {
                    return null;
                }

                var bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count <= 0)
                    {
                        return null;
                    }

                    read += count;
                }

                return bytes;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static byte[]? ReadBlock(byte[] table, ulong offset, ulong size)
        {
            if (offset > int.MaxValue || size > int.MaxValue ||
                offset + size + BlockTrailerLength > (ulong)table.Length)
            {
                return null;
            }

            var data = new byte[(int)size];
            Buffer.BlockCopy(table, (int)offset, data, 0, data.Length);
            var compression = table[(int)(offset + size)];
            if (compression == 0)
            {
                return data;
            }

            return compression == 1 ? TryDecompressSnappy(data) : null;
        }

        private static byte[]? TryDecompressSnappy(byte[] input)
        {
            var offset = 0;
            if (!TryReadVarint(input, 0, input.Length, out var expectedLength, out offset) ||
                expectedLength > int.MaxValue)
            {
                return null;
            }

            var result = new byte[(int)expectedLength];
            var written = 0;
            while (offset < input.Length && written < result.Length)
            {
                var tag = input[offset++];
                var type = tag & 0x03;
                if (type == 0)
                {
                    var lengthCode = tag >> 2;
                    var length = 0;
                    if (lengthCode < 60)
                    {
                        length = lengthCode + 1;
                    }
                    else
                    {
                        var extraBytes = lengthCode - 59;
                        if (extraBytes > 4 || offset + extraBytes > input.Length)
                        {
                            return null;
                        }

                        for (var index = 0; index < extraBytes; index++)
                        {
                            length |= input[offset++] << (index * 8);
                        }

                        length++;
                    }

                    if (length < 0 || offset + length > input.Length || written + length > result.Length)
                    {
                        return null;
                    }

                    Buffer.BlockCopy(input, offset, result, written, length);
                    offset += length;
                    written += length;
                    continue;
                }

                var copyLength = type == 1
                    ? ((tag >> 2) & 0x07) + 4
                    : ((tag >> 2) & 0x3f) + 1;
                var copyOffset = 0;
                if (type == 1)
                {
                    if (offset >= input.Length)
                    {
                        return null;
                    }

                    copyOffset = ((tag >> 5) << 8) | input[offset++];
                }
                else if (type == 2)
                {
                    if (offset + 2 > input.Length)
                    {
                        return null;
                    }

                    copyOffset = input[offset] | input[offset + 1] << 8;
                    offset += 2;
                }
                else
                {
                    if (offset + 4 > input.Length)
                    {
                        return null;
                    }

                    copyOffset = input[offset] | input[offset + 1] << 8 |
                                 input[offset + 2] << 16 | input[offset + 3] << 24;
                    offset += 4;
                }

                if (copyOffset <= 0 || copyOffset > written || written + copyLength > result.Length)
                {
                    return null;
                }

                for (var index = 0; index < copyLength; index++)
                {
                    result[written] = result[written - copyOffset];
                    written++;
                }
            }

            return written == result.Length ? result : null;
        }

        private static IEnumerable<BlockEntry> ReadBlockEntries(byte[] block)
        {
            if (block.Length < 4)
            {
                yield break;
            }

            var restartCount = ReadUInt32(block, block.Length - 4);
            if (restartCount > (uint)((block.Length - 4) / 4))
            {
                yield break;
            }

            var entriesEnd = block.Length - 4 - (int)restartCount * 4;
            var offset = 0;
            var previousKey = Array.Empty<byte>();
            while (offset < entriesEnd)
            {
                if (!TryReadVarint(block, offset, entriesEnd, out var shared, out offset) ||
                    !TryReadVarint(block, offset, entriesEnd, out var nonShared, out offset) ||
                    !TryReadVarint(block, offset, entriesEnd, out var valueLength, out offset) ||
                    shared > (ulong)previousKey.Length ||
                    nonShared > int.MaxValue || valueLength > int.MaxValue ||
                    offset + (long)nonShared + (long)valueLength > entriesEnd)
                {
                    yield break;
                }

                if (shared + nonShared > int.MaxValue)
                {
                    yield break;
                }

                var key = new byte[(int)(shared + nonShared)];
                if (shared > 0)
                {
                    Buffer.BlockCopy(previousKey, 0, key, 0, (int)shared);
                }

                Buffer.BlockCopy(block, offset, key, (int)shared, (int)nonShared);
                offset += (int)nonShared;
                var value = new byte[(int)valueLength];
                Buffer.BlockCopy(block, offset, value, 0, value.Length);
                offset += value.Length;
                previousKey = key;
                yield return new BlockEntry(key, value);
            }
        }

        private static bool TryReadBlockHandle(byte[] input, ref int offset, out ulong blockOffset, out ulong blockSize)
        {
            blockOffset = 0;
            blockSize = 0;
            return TryReadVarint(input, offset, input.Length, out blockOffset, out offset) &&
                   TryReadVarint(input, offset, input.Length, out blockSize, out offset);
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 |
                          bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            var value = 0UL;
            for (var index = 0; index < 8; index++)
            {
                value |= (ulong)bytes[offset + index] << (index * 8);
            }

            return value;
        }

        private sealed class BlockEntry
        {
            internal BlockEntry(byte[] key, byte[] value)
            {
                Key = key;
                Value = value;
            }

            internal byte[] Key { get; }
            internal byte[] Value { get; }
        }

        private sealed class AssistantCandidate
        {
            internal AssistantCandidate(string text, long sourceOrder, int offset)
            {
                Text = text;
                SourceOrder = sourceOrder;
                Offset = offset;
            }

            internal string Text { get; }
            internal long SourceOrder { get; }
            internal int Offset { get; }
        }
    }
}
