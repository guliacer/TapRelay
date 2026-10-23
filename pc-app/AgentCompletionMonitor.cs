using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    public sealed class AgentCompletionMonitor : IDisposable
    {
        private static readonly TimeSpan ClaudeStateHistoryWindow = TimeSpan.FromDays(7);
        // 恢复扫描间隔：事件丢失（如 watcher 缓冲区溢出）时的兜底上限，
        // 5 秒可把异常恢复延迟控制在可感知范围内。
        private static readonly TimeSpan RecoveryScanInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FullRecoveryScanInterval = TimeSpan.FromMinutes(5);
        // 权限审批 waiting 的默认确认延迟。敏感工具发出后，窗口内回传结果则取消广播。
        // 命令自带 timeout 时用该超时覆盖（见 ResolvePermissionWaitGracePeriod），避免
        // Gradle 这类长命令在转录静止 90 秒后被误报成“需要确认”。
        private static readonly TimeSpan PermissionWaitGracePeriod = TimeSpan.FromSeconds(90);
        // 到期复核时「转录仍在写入」只是任务仍在推进的**旁证**，会被同会话的其他活动
        // （尤其是 Haha 注入的 <task-notification> 后台报告）无限延长，导致真实审批
        // 等待被一直顺延、永不通知。权威信号是该 tool_use 有没有回传 tool_result；
        // 因此顺延设总时长上限：超过该上限且仍未回传，即判定确实卡在用户身上并广播。
        private static readonly TimeSpan PermissionWaitMaxDeferral = TimeSpan.FromMinutes(10);
        // Freebuff elevation_request（管理员命令等待确认）的优雅期：窗口内同一
        // 线程若已本地解决（数据库出现终态）则取消广播，避免手机收到一条马上
        // 就过期的“需要确认”提示。对齐 tlive 的 approvalGraceSec 思路，但取更短的
        // 默认值，因为 Freebuff 的确认通常就在桌面几秒内点掉。
        private static readonly TimeSpan FreebuffElevationGracePeriod = TimeSpan.FromSeconds(10);
        // TraeCode 完成事件挂起等待积分明细（cn_session_usage_tail）的窗口；
        // 超时后由 1 秒节拍的专用冲刷定时器直接发送（15 秒轮询节拍太粗，
        // 会让短窗口形同虚设）。
        private static readonly TimeSpan TraeUsageWaitWindow = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan TraeUsageFlushInterval = TimeSpan.FromSeconds(1);
        // Codex request_user_input 不是可靠的 waiting 信号：Default 模式会先写入
        // function_call，随后以 function_call_output 返回 unavailable。给对应输出
        // 留出一个短窗口，避免把调用失败误报为手机待处理任务。
        private static readonly TimeSpan CodexInputValidationGracePeriod = TimeSpan.FromSeconds(2);
        // 豆包 rlog 在 SSE 结束时先落盘，WebView 的助手节点随后才完成渲染；
        // 在这段窗口内重试可避免把网络终态误当成任务正文。
        private static readonly TimeSpan[] DoubaoAssistantReadDelays =
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4)
        };
        private readonly NotifyServer _server;
        private readonly Action<string> _log;
        private readonly Action<string> _detailLog;
        private readonly Func<string, bool> _isSourceProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, FileState> _files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _traeSessionKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _eventKeys = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// 本次监控启动时刻。上下文压缩/会话恢复会把旧消息原样重新追加到转录末尾
        /// （uuid、timestamp 都不变），而 <see cref="_eventKeys"/> 只在内存中存在，
        /// PC 重启后为空，历史终态会被再次广播。追加区里时间戳早于启动时刻的记录
        /// 只可能是重放，必须跳过。
        /// </summary>
        private DateTimeOffset _startedAtUtc;
        private static readonly TimeSpan ReplayedTranscriptClockSkew = TimeSpan.FromMinutes(2);
        /// <summary>
        /// 正在等待用户处理的 WorkBuddy 会话（sessionId 级去重）。
        /// WorkBuddy 等待期间会周期轮询打印等待信号（旧格式 transition 行 turnSeq 递增、
        /// 新格式 deriveExportedPendingInputKind 约 30 秒/次），两条解析路径会产生不同
        /// EventKey；若只用 EventKey 去重，用户未处理时同一轮等待会重复推送到手机。
        /// 因此按 sessionId 统一去重：同一会话仅推一次，直到任务恢复（working/completed）
        /// 才清除，允许下一轮等待再次推送。
        /// </summary>
        private readonly HashSet<string> _workbuddyWaitingSessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingPermissionWait> _pendingPermissionWaits = new Dictionary<string, PendingPermissionWait>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingZCodeWait> _pendingZCodeWaits = new Dictionary<string, PendingZCodeWait>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingCodexInput> _pendingCodexInputs = new Dictionary<string, PendingCodexInput>(StringComparer.Ordinal);
        private readonly List<PendingTraeCompletion> _pendingTraeCompletions = new List<PendingTraeCompletion>();
        private readonly HashSet<string> _pendingDoubaoCompletions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CancellationTokenSource> _freebuffElevationWaits =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly HashSet<string> _watchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly AgentTaskTiming _taskTiming = new AgentTaskTiming();
        private readonly BaiduNetdiskBatchMonitor _baiduNetdiskMonitor;
        private readonly QuarkNetdiskBatchMonitor _quarkNetdiskMonitor;
        private readonly AdriveTransferMonitor _adriveTransferMonitor;
        private readonly OpenCodeMonitor _opencodeMonitor;
        private readonly DeepSeekHarnessMonitor _deepseekHarnessMonitor;
        private readonly FreebuffMonitor _freebuffMonitor;
        private NonReentrantMonitorTimer? _pollTimer;
        private NonReentrantMonitorTimer? _traeUsageFlushTimer;
        private DateTime _lastFullRecoveryScanUtc = DateTime.MinValue;
        private int _refreshRequested;
        private int _refreshWorkerRunning;
        private bool _started;
        private bool _disposed;

        public AgentCompletionMonitor(
            NotifyServer server,
            Action<string> log,
            Func<string, bool>? isSourceProcessRunning = null,
            Func<string, DateTime, (string Title, string Body)?>? readTraeToast = null,
            Action<string>? detailLog = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            // 下游监视器的轮询细节只写诊断文件，首页只保留任务事件。
            detailLog ??= log;
            _detailLog = detailLog;
            _isSourceProcessRunning = isSourceProcessRunning ?? AdaptedProcessGate.IsRunningForSource;
            _readTraeToast = readTraeToast ?? ReadTraeToast;
            _baiduNetdiskMonitor = new BaiduNetdiskBatchMonitor(DispatchBaiduBatch, _log);
            _quarkNetdiskMonitor = new QuarkNetdiskBatchMonitor(DispatchQuarkBatch, _log);
            _adriveTransferMonitor = new AdriveTransferMonitor(DispatchAdriveTask, _log);
            _opencodeMonitor = new OpenCodeMonitor(DispatchOpenCodeTask, _log);
            _deepseekHarnessMonitor = new DeepSeekHarnessMonitor(
                DispatchDeepSeekHarnessTask, _log, isProcessRunning: null, detailLog: detailLog);
            _freebuffMonitor = new FreebuffMonitor(DispatchFreebuffTask, _log);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }

                _started = true;
                _startedAtUtc = DateTimeOffset.UtcNow;
                EnsureWatchersLocked();
                PrimeExistingFiles();
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = true;
                }
                _pollTimer = new NonReentrantMonitorTimer(
                    ScanFiles,
                    RecoveryScanInterval,
                    RecoveryScanInterval,
                    error => _log("智能体日志恢复扫描失败：" + error.Message));
                // 4 秒积分等待窗口的专用冲刷节拍；空列表时仅一次锁开销。
                _traeUsageFlushTimer = new NonReentrantMonitorTimer(
                    FlushExpiredTraeCompletions,
                    TraeUsageFlushInterval,
                    TraeUsageFlushInterval,
                    error => _log("TraeCode 积分等待冲刷失败：" + error.Message));
            }

            _baiduNetdiskMonitor.Start();
            _quarkNetdiskMonitor.Start();
            _adriveTransferMonitor.Start();
            _opencodeMonitor.Start();
            _deepseekHarnessMonitor.Start();
            _freebuffMonitor.Start();
            _log("\u5df2\u542f\u7528 Claude/ChatGPT/Trae/TraeWork/ZCode/\u8c46\u5305/\u767e\u5ea6\u7f51\u76d8/\u5938\u514b\u7f51\u76d8/\u963f\u91cc\u4e91\u76d8/WorkBuddy/OpenCode/DeepSeek Harness/Freebuff \u5b8c\u6210\u4e8b\u4ef6\u76d1\u63a7");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _pollTimer?.Dispose();
                _pollTimer = null;
                _traeUsageFlushTimer?.Dispose();
                _traeUsageFlushTimer = null;
                foreach (var watcher in _watchers)
                {
                    watcher.Dispose();
                }

                _watchers.Clear();
                _watchedDirectories.Clear();
                _traeSessionKinds.Clear();
                _pendingDoubaoCompletions.Clear();
                _pendingCodexInputs.Clear();
                _files.Clear();
            }

            _baiduNetdiskMonitor.Dispose();
            _quarkNetdiskMonitor.Dispose();
            _adriveTransferMonitor.Dispose();
            _opencodeMonitor.Dispose();
            _deepseekHarnessMonitor.Dispose();
            _freebuffMonitor.Dispose();
        }

        public void RefreshConfiguredRoots()
        {
            Interlocked.Exchange(ref _refreshRequested, 1);
            if (Interlocked.CompareExchange(ref _refreshWorkerRunning, 1, 0) == 0)
            {
                _ = Task.Run(RefreshConfiguredRootsWorker);
            }
        }

        private void RefreshConfiguredRootsWorker()
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshRequested, 0) == 1)
                {
                    lock (_gate)
                    {
                        if (_disposed || !_started)
                        {
                            return;
                        }

                        EnsureWatchersLocked();
                        PrimeExistingFiles();
                    }

                    _baiduNetdiskMonitor.Refresh();
                    _quarkNetdiskMonitor.Refresh();
                    _adriveTransferMonitor.Refresh();
                    _opencodeMonitor.Refresh();
                    _deepseekHarnessMonitor.Refresh();
                    _freebuffMonitor.Refresh();
                }
            }
            catch (Exception error)
            {
                _log("后台刷新智能体监控目录失败：" + error.Message);
            }
            finally
            {
                Volatile.Write(ref _refreshWorkerRunning, 0);
                if (Volatile.Read(ref _refreshRequested) == 1 &&
                    Interlocked.CompareExchange(ref _refreshWorkerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(RefreshConfiguredRootsWorker);
                }
            }
        }

        private void EnsureWatchersLocked()
        {
            foreach (var directory in AgentLogPaths.GetWatchDirectories())
            {
                AddWatcher(directory);
            }
        }

        private void AddWatcher(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (!_watchedDirectories.Add(normalizedDirectory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                // WorkBuddy 等日志写入频繁，缓冲区太小容易溢出丢事件（事件丢失后
                // 只能靠 5 秒恢复扫描兜底），加大缓冲降低溢出概率。
                InternalBufferSize = 256 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            _watchers.Add(watcher);
            PrimeExistingFiles(watcher);
            watcher.EnableRaisingEvents = _started;
        }

        private void PrimeExistingFiles()
        {
            foreach (var watcher in _watchers)
            {
                PrimeExistingFiles(watcher);
            }
        }

        private void PrimeExistingFiles(FileSystemWatcher watcher)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(watcher.Path, "*", SearchOption.AllDirectories))
                {
                    if (!AgentLogPaths.IsSupportedFile(file))
                    {
                        continue;
                    }

                    if (_files.ContainsKey(file))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(file);
                    var length = fileInfo.Length;
                    var state = new FileState
                    {
                        Position = length,
                        LastLength = length,
                        LastWriteUtc = fileInfo.LastWriteTimeUtc
                    };
                    if (AgentLogPaths.IsClaudeTranscript(file) &&
                        fileInfo.LastWriteTime >= DateTime.Now.Subtract(ClaudeStateHistoryWindow))
                    {
                        PrimeClaudeState(file, state);
                    }

                    _files[file] = state;
                }
            }
            catch (Exception error)
            {
                _log("\u521d\u59cb\u5316\u5b8c\u6210\u4e8b\u4ef6\u76d1\u63a7\u5931\u8d25\uff1a" + error.Message);
            }
        }

        private static void PrimeClaudeState(string path, FileState state)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    ObserveClaudeStateLine(state, line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void ObserveClaudeStateLine(FileState state, string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return;
                }

                var type = ReadString(root, "type");
                var sessionId = FirstNonEmpty(ReadString(root, "session_id"), ReadString(root, "sessionId"));
                var cwd = FirstNonEmpty(
                    ReadString(root, "cwd"),
                    ReadString(root, "working_directory"),
                    ReadString(root, "workDir"));
                state.LastClaudeSessionId = FirstNonEmpty(sessionId, state.LastClaudeSessionId);
                RememberClaudePermissionModeLocked(state, root);

                if (string.Equals(type, "session-meta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase))
                {
                    RememberClaudeDirectoryLocked(state, cwd, false);
                    return;
                }

                if (string.Equals(type, "last-prompt", StringComparison.OrdinalIgnoreCase))
                {
                    var prompt = FirstNonEmpty(ReadString(root, "lastPrompt"), ReadString(root, "last_prompt"));
                    var promptDirectory = ClaudeProjectResolver.ResolvePromptDirectory(
                        prompt,
                        FirstNonEmpty(cwd, state.LastClaudeCwd));
                    RememberClaudeDirectoryLocked(
                        state,
                        FirstNonEmpty(promptDirectory, cwd),
                        !string.IsNullOrWhiteSpace(promptDirectory));
                    return;
                }

                if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                {
                    RememberClaudeDirectoryLocked(state, cwd, false);
                    RememberClaudePermissionModeLocked(state, root);
                    // 真人用户提示才是 Haha 交互轮次起点；tool_result 回传不算。
                    if (AgentCompletionEventParser.IsClaudeHumanUserPrompt(root) &&
                        AgentCompletionEventParser.TryReadEventTimestamp(root, out var humanPromptAt))
                    {
                        state.LastClaudeHumanPromptAt = humanPromptAt;
                        // 新一轮真人提示：本轮 token 从 0 重新累计。
                        state.LastClaudeTokenCount = 0L;
                    }
                    return;
                }

                if (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var toolDirectory = ClaudeProjectResolver.ResolveToolUseDirectory(
                        root,
                        FirstNonEmpty(cwd, state.LastClaudeCwd));
                    RememberClaudeDirectoryLocked(state, toolDirectory, !string.IsNullOrWhiteSpace(toolDirectory));
                    RememberClaudeDirectoryLocked(state, cwd, false);
                    var assistantMessage = ExtractClaudeMessage(root);
                    if (!string.IsNullOrWhiteSpace(assistantMessage))
                    {
                        state.LastClaudeMessage = assistantMessage;
                    }
                    state.LastClaudeFailed = ReadInt(root, "isApiErrorMessage") == 1 ||
                                             root.TryGetProperty("error", out _);
                    AccumulateClaudeTokenCountLocked(state, root);
                    return;
                }

                if (string.Equals(type, "system", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ReadString(root, "subtype"), "turn_duration", StringComparison.OrdinalIgnoreCase))
                {
                    state.LastClaudeMessage = string.Empty;
                    state.LastClaudeFailed = false;
                    state.LastClaudeProjectName = string.Empty;
                    state.LastClaudeHumanPromptAt = null;
                    state.LastClaudeTokenCount = 0L;
                }
            }
            catch (JsonException)
            {
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs args)
        {
            ProcessFile(args.FullPath);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs args)
        {
            ProcessFile(args.FullPath);
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _lastFullRecoveryScanUtc = DateTime.MinValue;
                }
            }

            _log("智能体日志监听缓冲区异常，将在下一轮执行恢复扫描：" + args.GetException().Message);
        }

        private void ScanFiles()
        {
            string[] paths;
            FileSystemWatcher[] watchers;
            bool runFullRecoveryScan;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                EnsureWatchersLocked();

                paths = new string[_files.Count];
                _files.Keys.CopyTo(paths, 0);
                watchers = _watchers.ToArray();
                runFullRecoveryScan = DateTime.UtcNow - _lastFullRecoveryScanUtc >= FullRecoveryScanInterval;
                if (runFullRecoveryScan)
                {
                    _lastFullRecoveryScanUtc = DateTime.UtcNow;
                }
            }

            foreach (var path in paths)
            {
                ProcessFile(path);
            }

            FlushExpiredTraeCompletions();
            TryFirePendingPermissionWaits();
            TryFireZCodeWaits();
            TryFirePendingCodexInputs();

            if (!runFullRecoveryScan)
            {
                return;
            }

            foreach (var watcher in watchers)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(watcher.Path, "*", SearchOption.AllDirectories))
                    {
                        if (AgentLogPaths.IsSupportedFile(file))
                        {
                            ProcessFile(file);
                        }
                    }
                }
                catch
                {
                    // Log files can rotate while they are being enumerated.
                }
            }
        }

        private void ProcessFile(string path)
        {
            if (!AgentLogPaths.IsSupportedFile(path))
            {
                return;
            }

            var source = GetSourceForPath(path);
            if (source.Length > 0 && !IsFileProcessRunning(path, source))
            {
                BaselineFile(path);
                return;
            }

            List<string> lines;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_files.TryGetValue(path, out var state))
                {
                    state = new FileState();
                    _files[path] = state;
                }

                if (TryGetFileSignature(path, out var length, out var lastWriteUtc) &&
                    state.Position <= length &&
                    state.LastLength == length &&
                    state.LastWriteUtc == lastWriteUtc)
                {
                    return;
                }

                lines = ReadNewLines(path, state);
                if (lines.Count > 0 &&
                    AgentLogPaths.IsChatGPTSession(path) &&
                    state.CodexSessionKind == CodexSessionKind.Unknown)
                {
                    state.CodexSessionKind = CodexSessionMetadata.ReadFromFile(path);
                }
            }

            foreach (var line in lines)
            {
                if (source.Length > 0 && !IsFileProcessRunning(path, source))
                {
                    BaselineFile(path);
                    return;
                }
                ProcessLine(path, line);
            }
        }

        private List<string> ReadNewLines(string path, FileState state)
        {
            var lines = new List<string>();
            state.LastLength = -1L;
            state.LastWriteUtc = DateTime.MinValue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < state.Position)
                {
                    // 文件被截断或轮转后，当前位置之前的内容已经处理过；从头重放
                    // 会把历史 request_user_input/完成事件再次推送。直接建立新基线，
                    // 后续追加内容由下一次扫描处理。
                    CancelPendingCodexInputsForPathLocked(path);
                    state.Position = stream.Length;
                    state.Pending = string.Empty;
                    state.CodexSessionKind = CodexSessionKind.Unknown;
                    state.LastCodexSessionId = string.Empty;
                    state.LastCodexProjectName = string.Empty;
                    state.LastCodexCwd = string.Empty;
                    state.LastLength = stream.Length;
                    state.LastWriteUtc = File.GetLastWriteTimeUtc(path);
                    return lines;
                }

                stream.Seek(state.Position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
                var appended = reader.ReadToEnd();
                state.Position = stream.Position;
                state.LastLength = stream.Length;
                state.LastWriteUtc = File.GetLastWriteTimeUtc(path);
                if (appended.Length == 0)
                {
                    return lines;
                }

                var text = state.Pending + appended;
                var parts = text.Split(new[] { '\n' }, StringSplitOptions.None);
                state.Pending = parts[parts.Length - 1];
                for (var index = 0; index < parts.Length - 1; index++)
                {
                    var line = parts[index].TrimEnd('\r');
                    if (line.Length > 0)
                    {
                        lines.Add(line);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return lines;
        }

        private static bool TryGetFileSignature(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 0L;
            lastWriteUtc = DateTime.MinValue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                length = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
                return true;
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

        private void ProcessLine(string path, string line)
        {
            var source = GetSourceForPath(path);
            if (source.Length > 0 && !IsFileProcessRunning(path, source)) return;

            if (AgentLogPaths.IsClaudeTranscript(path))
            {
                ProcessClaudeLine(path, line);
                return;
            }

            if (AgentLogPaths.IsChatGPTSession(path))
            {
                _taskTiming.ObserveLine("codex", line);
                ProcessChatGPTLine(path, line);
                return;
            }

            if (AgentLogPaths.IsWorkBuddyLog(path) ||
                AgentLogPaths.IsWorkBuddyAiLog(path))
            {
                ProcessWorkBuddyLine(path, line);
                return;
            }

            if (AgentLogPaths.IsTraeLog(path))
            {
                _taskTiming.ObserveLine(GetTraeSource(path), line);
            }

            if (AgentLogPaths.IsZCodeDesktopLog(path))
            {
                _taskTiming.ObserveLine("zcode", line);
            }

            if (AgentLogPaths.IsTraeLog(path) &&
                AgentRecordClassifier.TryParseTraeSessionLine(line, out var traeSessionId, out var isTraeSubagent))
            {
                RememberTraeSessionKind(GetTraeSource(path), traeSessionId, isTraeSubagent);
                return;
            }

            if (AgentLogPaths.IsTraeLog(path))
            {
                // TraeCode CN 的积分明细事件（cn_session_usage_tail show）先于终态/老格式判断：
                // 它同样带 "event: ; params:" 结构但语义独立，用于补齐挂起完成通知的消耗积分。
                if (line.IndexOf("cn_session_usage_tail", StringComparison.Ordinal) >= 0)
                {
                    ProcessTraeUsageLine(path, line);
                    return;
                }

                // TraeCode CN 与 TraeWork CN 共用 DoneHandler/NotificationPort 终态事件格式，
                // 必须先于老版 "event: ; params:" 格式判断（P078：真实 TraeCode CN 3.3.91
                // 日志只产生新格式，老格式 code_comp_* 在真实日志中已绝迹）。
                if (line.IndexOf("[DoneHandler] Stream done event received", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("[NotificationPort] Sending waiting notification", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ProcessTraeWorkLine(path, line);
                    return;
                }

                if (line.IndexOf("event:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("params:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ProcessTraeLine(path, line);
                    return;
                }
            }

            if (AgentLogPaths.IsZCodeCliLog(path))
            {
                _taskTiming.ObserveLine("zcode", line);
                ProcessZCodeLine(path, line);
                return;
            }

            // 豆包对话接口自 8-23 起从 /chat/completion 迁移到 /im/chain/single，
            // 若这里仍按旧的 "chat/completion" 过滤，新版完成/失败事件全部被丢弃。
            // 这里只按日志类型 + [rlog] 粗筛，具体是否对话终态交给
            // AgentCompletionEventParser.IsDoubaoChatUrl 判断（已兼容三个端点）。
            if (AgentLogPaths.IsDoubaoLog(path) &&
                line.IndexOf("[rlog]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ProcessDoubaoLine(line);
                return;
            }

            if (AgentLogPaths.IsZCodeDesktopLog(path) &&
                (line.IndexOf("task_complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 line.IndexOf("task_error", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ProcessZCodeDesktopLine(line);
            }
        }

        /// <summary>
        /// 处理 Claude Code Haha 改版的元事件 <c>cc-haha-task-notification</c>。
        /// 该事件只在后台 Bash / 子代理结束时写入，不是用户主任务终态。
        /// 实测跨全部历史转录：这类事件的 taskId 有 53 个被当作 completed 广播到手机
        /// （子代理/后台命令中途完成即误报），而真正的主任务完成全部走
        /// <see cref="TryProcessHahaInteractiveTurn"/> 的 <c>end_turn</c>（taskId 形如
        /// "sessionId:hash"）。因此这里一律不广播，主任务终态由 end_turn 负责。
        /// 相关的 <see cref="AgentCompletionEventParser.IsHahaBackgroundSubtaskSummary"/>
        /// 过滤逻辑保留用于测试与文档对照。
        /// </summary>
        private void ProcessHahaTaskNotification(JsonElement root, string rememberedCwd = "")
        {
            // 全部丢弃：见上方注释，cc-haha-task-notification 不是主任务终态。
            // 保留空方法体以维持调用方结构，避免 ProcessClaudeLine 分支改动。
        }

        private void ProcessClaudeLine(string path, string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (AgentRecordClassifier.IsSubagent(root))
                {
                    return;
                }
                var type = ReadString(root, "type");
                var sessionId = FirstNonEmpty(ReadString(root, "session_id"), ReadString(root, "sessionId"));
                var cwd = FirstNonEmpty(
                    ReadString(root, "cwd"),
                    ReadString(root, "working_directory"),
                    ReadString(root, "workDir"));

                if (string.Equals(type, "session-meta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_gate)
                    {
                        var state = GetClaudeStateLocked(path);
                        state.LastClaudeSessionId = FirstNonEmpty(sessionId, state.LastClaudeSessionId);
                        RememberClaudeDirectoryLocked(state, cwd, false);
                        RememberClaudePermissionModeLocked(state, root);
                    }
                    return;
                }

                // Claude Code Haha 改版不写标准的 system/turn_duration 结束事件；
                // cc-haha-task-notification 只覆盖后台 Bash/子代理，主任务终态看 end_turn。
                // ProcessHahaTaskNotification 内部会丢弃 Background command / Agent 子任务。
                if (string.Equals(type, "cc-haha-task-notification", StringComparison.OrdinalIgnoreCase))
                {
                    string hahaCwd;
                    lock (_gate)
                    {
                        hahaCwd = GetClaudeStateLocked(path).LastClaudeCwd;
                    }
                    ProcessHahaTaskNotification(root, hahaCwd);
                    return;
                }

                if (string.Equals(type, "last-prompt", StringComparison.OrdinalIgnoreCase))
                {
                    var lastPrompt = FirstNonEmpty(ReadString(root, "lastPrompt"), ReadString(root, "last_prompt"));
                    lock (_gate)
                    {
                        var state = GetClaudeStateLocked(path);
                        state.LastClaudeSessionId = FirstNonEmpty(sessionId, state.LastClaudeSessionId);
                        var baseDirectory = FirstNonEmpty(cwd, state.LastClaudeCwd);
                        var promptDirectory = ClaudeProjectResolver.ResolvePromptDirectory(lastPrompt, baseDirectory);
                        if (!string.IsNullOrWhiteSpace(promptDirectory))
                        {
                            RememberClaudeDirectoryLocked(state, promptDirectory, true);
                        }
                        else
                        {
                            RememberClaudeDirectoryLocked(state, cwd, false);
                        }
                    }
                    return;
                }

                if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_gate)
                    {
                        var state = GetClaudeStateLocked(path);
                        state.LastClaudeSessionId = FirstNonEmpty(sessionId, state.LastClaudeSessionId);
                        RememberClaudeDirectoryLocked(state, cwd, false);
                        RememberClaudePermissionModeLocked(state, root);
                        // 先归因这条 user 行：注入的后台任务报告不算真人提问。
                        // tool_result-only 行不是输入边界，不改变归因。
                        var isInjection = AgentCompletionEventParser.IsHahaBackgroundTaskNotificationPrompt(root);
                        if (isInjection)
                        {
                            state.LastUserLineWasHahaTaskInjection = true;
                        }
                        else if (AgentCompletionEventParser.IsClaudeHumanUserPrompt(root) &&
                                 AgentCompletionEventParser.TryReadEventTimestamp(root, out var humanPromptAt))
                        {
                            state.LastClaudeHumanPromptAt = humanPromptAt;
                            // 新一轮真人提示：本轮 token 从 0 重新累计。
                            state.LastClaudeTokenCount = 0L;
                            state.LastUserLineWasHahaTaskInjection = false;
                        }

                        // 记录已回传结果的 tool_use id：权限 waiting 判定里，已回传说明该工具
                        // 已允许并执行（或已拒绝并返回错误），不能当作还卡在审批。
                        var answeredIds = AgentCompletionEventParser.CollectToolResultIds(root);
                        if (answeredIds.Count > 0)
                        {
                            state.AnsweredToolUseIds.UnionWith(answeredIds);
                            foreach (var answeredId in answeredIds)
                            {
                                _pendingPermissionWaits.Remove(answeredId);
                            }
                        }
                    }
                    return;
                }

                if (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var assistantMessage = ExtractClaudeMessage(root);
                    var lineFailed = ReadInt(root, "isApiErrorMessage") == 1 ||
                                     root.TryGetProperty("error", out _);
                    string rememberedSessionId;
                    string rememberedCwd;
                    string rememberedProjectName;
                    lock (_gate)
                    {
                        var state = GetClaudeStateLocked(path);
                        state.LastClaudeSessionId = FirstNonEmpty(sessionId, state.LastClaudeSessionId);
                        var toolDirectory = ClaudeProjectResolver.ResolveToolUseDirectory(
                            root,
                            FirstNonEmpty(cwd, state.LastClaudeCwd));
                        if (!string.IsNullOrWhiteSpace(toolDirectory))
                        {
                            RememberClaudeDirectoryLocked(state, toolDirectory, true);
                        }
                        RememberClaudeDirectoryLocked(state, cwd, false);
                        if (!string.IsNullOrWhiteSpace(assistantMessage))
                        {
                            state.LastClaudeMessage = assistantMessage;
                        }
                        state.LastClaudeFailed = lineFailed;
                        AccumulateClaudeTokenCountLocked(state, root);
                        rememberedSessionId = state.LastClaudeSessionId;
                        rememberedCwd = state.LastClaudeCwd;
                        rememberedProjectName = state.LastClaudeProjectName;
                    }

                    // Haha interactive turns never emit system/turn_duration; the terminal
                    // signal is assistant.message.stop_reason (end_turn / failed stop_sequence).
                    // Official Claude still completes via turn_duration below and is not matched
                    // by IsHahaTranscriptEvent, so the two paths do not double-fire.
                    TryProcessHahaInteractiveTurn(
                        root,
                        path,
                        sessionId,
                        cwd,
                        assistantMessage,
                        lineFailed,
                        rememberedSessionId,
                        rememberedCwd,
                        rememberedProjectName);
                    return;
                }

                var subtype = ReadString(root, "subtype");
                if (!string.Equals(type, "system", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(subtype, "turn_duration", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (IsReplayedTranscriptEvent(
                        FirstNonEmpty(ReadString(root, "timestamp"), ReadString(root, "stop_time"))))
                {
                    return;
                }

                string message;
                string rememberedSessionIdOfficial;
                string rememberedCwdOfficial;
                string rememberedProjectNameOfficial;
                bool failed;
                long tokenCount;
                lock (_gate)
                {
                    var state = GetClaudeStateLocked(path);
                    message = state.LastClaudeMessage;
                    rememberedSessionIdOfficial = state.LastClaudeSessionId;
                    rememberedCwdOfficial = state.LastClaudeCwd;
                    rememberedProjectNameOfficial = state.LastClaudeProjectName;
                    failed = state.LastClaudeFailed;
                    tokenCount = state.LastClaudeTokenCount;
                    state.LastClaudeMessage = string.Empty;
                    state.LastClaudeFailed = false;
                    state.LastClaudeProjectName = string.Empty;
                    state.LastClaudeTokenCount = 0L;
                }

                sessionId = FirstNonEmpty(sessionId, rememberedSessionIdOfficial, Path.GetFileNameWithoutExtension(path));
                cwd = ClaudeProjectResolver.PreferWorkingDirectory(
                    rememberedCwdOfficial,
                    cwd,
                    AgentLogPaths.GetUserProfileRoot());
                cwd = FirstNonEmpty(cwd, rememberedCwdOfficial);
                var timestamp = FirstNonEmpty(ReadString(root, "timestamp"), ReadString(root, "stop_time"));
                var durationMs = ReadLong(root, "durationMs");
                var taskId = BuildClaudeEventId(sessionId, message, timestamp, path);
                var status = failed ? "failed" : "completed";
                if (!TryRememberEvent("claude:" + status + ":" + taskId))
                {
                    return;
                }

                var notification = failed ? "Claude task failed" : "Claude task completed";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    notification += ": " + SummarizeClaudeMessage(message);
                }
                SendNotification(
                    notification,
                    "claude",
                    status,
                    taskId,
                    cwd,
                    durationMs,
                    ProjectNameResolver.ResolveFolderFirst(rememberedProjectNameOfficial, cwd, taskId, message),
                    tokenCount,
                    sessionId);
            }
            catch (JsonException)
            {
                // Claude writes JSONL, but partially flushed lines are normal while a turn is active.
            }
        }

        /// <summary>
        /// 当前 assistant 终态行是否只是在答复 Haha 注入的 <c>&lt;task-notification&gt;</c>
        /// 后台任务报告。真则不广播，避免一次真人提问被推成多条完成通知。
        /// </summary>
        private bool IsReplyToHahaTaskInjection(string path)
        {
            lock (_gate)
            {
                return GetClaudeStateLocked(path).LastUserLineWasHahaTaskInjection;
            }
        }

        /// <summary>
        /// Claude Code Haha interactive turns do not emit <c>system/turn_duration</c>.
        /// Terminal assistant rows carry <c>message.stop_reason</c>:
        /// <c>end_turn</c> = completed, error <c>stop_sequence</c> = failed.
        /// Mid-turn <c>tool_use</c> and hook noise ("No response requested.") are ignored.
        /// Only Haha-marked transcript rows are accepted so official Claude stays on turn_duration.
        /// </summary>
        private void TryProcessHahaInteractiveTurn(
            JsonElement root,
            string path,
            string sessionId,
            string cwd,
            string assistantMessage,
            bool lineFailed,
            string rememberedSessionId,
            string rememberedCwd,
            string rememberedProjectName)
        {
            if (!IsHahaTranscriptEvent(root))
            {
                return;
            }

            string status;
            var taskId = string.Empty;
            var message = assistantMessage;

            if (AgentCompletionEventParser.TryGetAskUserQuestionPrompt(root, out var promptToolUseId, out var promptQuestion))
            {
                // AskUserQuestion 阻塞在用户身上：Haha 既不写 cc-haha-task-notification
                // 也不写 system/turn_duration，唯一证据就是这行带 tool_use 块的 assistant
                // 记录。官方 Claude 由 Notification Hook 覆盖同样的状态，两条路互不重叠。
                status = "waiting";
                taskId = promptToolUseId;
                message = promptQuestion;
            }
            else if (AgentCompletionEventParser.TryGetPendingPermissionPrompt(
                         root,
                         out var permissionToolUseId,
                         out var permissionToolName,
                         out var permissionCommand))
            {
                // 权限审批卡（“允许 Claude 执行此命令?”）在 Haha 转录里同样只有这行
                // stop_reason=tool_use 的 assistant 记录。但工具发出不代表一定在等用户：
                // 自动执行（grep/adb/python 等）在结果回传前也有这么一行。所以这里只“登记”
                // 一个待确认项，先不广播；由 ScanFiles 在每轮检查：若超过宽限期仍未回传
                // tool_result，才判定“卡在审批”并广播 waiting（见 TryFirePendingPermissionWaits）。
                // bypassPermissions 等免确认模式不会弹审批卡，直接跳过。
                lock (_gate)
                {
                    var pendingState = GetClaudeStateLocked(path);
                    RememberClaudePermissionModeLocked(pendingState, root);
                    if (AgentCompletionEventParser.IsPermissionAutoAllowMode(pendingState.LastClaudePermissionMode) ||
                        pendingState.AnsweredToolUseIds.Contains(permissionToolUseId) ||
                        _pendingPermissionWaits.ContainsKey(permissionToolUseId))
                    {
                        return;
                    }

                    var permissionHeader = "\u7b49\u5f85\u4f60\u786e\u8ba4";
                    var permissionMessage = SummarizeClaudeMessage(permissionCommand);
                    if (string.IsNullOrWhiteSpace(permissionMessage))
                    {
                        permissionMessage = permissionToolName;
                    }

                    if (!string.IsNullOrWhiteSpace(permissionMessage))
                    {
                        permissionHeader += "\uff1a" + SummarizeClaudeMessage(permissionMessage);
                    }

                    var gracePeriod = ResolvePermissionWaitGracePeriod(root);
                    _pendingPermissionWaits[permissionToolUseId] = new PendingPermissionWait(
                        permissionToolUseId,
                        permissionHeader,
                        path,
                        cwd,
                        rememberedProjectName,
                        DateTimeOffset.UtcNow,
                        gracePeriod);
                }

                return;
            }
            else
            {
                var stopReason = ReadClaudeStopReason(root);
                if (string.IsNullOrWhiteSpace(stopReason))
                {
                    return;
                }

                var normalizedStop = stopReason.Trim().Replace('-', '_');
                if (string.Equals(normalizedStop, "end_turn", StringComparison.OrdinalIgnoreCase))
                {
                    // Compact/hook 占位也会偶发 end_turn（日志见 “No response requested.”），
                    // 与 stop_sequence 分支同样丢弃，避免空完成通知。
                    if (IsHahaNoResponsePlaceholder(assistantMessage))
                    {
                        return;
                    }

                    // 这条 end_turn 是在答复 Haha 注入的 <task-notification> 后台报告，
                    // 不是用户主任务终态；否则同一个任务会反复推送到手机。
                    if (IsReplyToHahaTaskInjection(path))
                    {
                        return;
                    }

                    status = lineFailed ? "failed" : "completed";
                }
                else if (string.Equals(normalizedStop, "stop_sequence", StringComparison.OrdinalIgnoreCase))
                {
                    // Compact/hook placeholders are not user-visible task outcomes.
                    if (IsHahaNoResponsePlaceholder(assistantMessage))
                    {
                        return;
                    }

                    if (!lineFailed)
                    {
                        return;
                    }

                    // 注入答复上的 API 错误属于后台报告那一小轮，不是用户主任务失败。
                    if (IsReplyToHahaTaskInjection(path))
                    {
                        return;
                    }

                    status = "failed";
                }
                else
                {
                    // tool_use / max_tokens / unknown: not a Haha interactive completion signal.
                    return;
                }
            }

            sessionId = FirstNonEmpty(sessionId, rememberedSessionId, Path.GetFileNameWithoutExtension(path));
            cwd = ClaudeProjectResolver.PreferWorkingDirectory(
                rememberedCwd,
                cwd,
                AgentLogPaths.GetUserProfileRoot());
            cwd = FirstNonEmpty(cwd, rememberedCwd);
            var timestamp = FirstNonEmpty(ReadString(root, "timestamp"), ReadString(root, "stop_time"));
            if (IsReplayedTranscriptEvent(timestamp))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                taskId = BuildClaudeEventId(sessionId, message, timestamp, path);
            }
            if (!TryRememberEvent("claude-haha:" + status + ":" + taskId))
            {
                return;
            }

            // Haha 不写 system/turn_duration.durationMs；用“最近一次真人 user 提示 → 本行 timestamp”
            // 计算交互轮次耗时。起点只由下一次真人提示覆盖，end_turn/waiting 都不清掉，
            // 否则中途 end_turn 会把后续真正完成算成 durationMs=0（手机显示“耗时未知”）。
            long durationMs = 0L;
            long tokenCount = 0L;
            DateTimeOffset? turnStartedAt = null;
            lock (_gate)
            {
                var state = GetClaudeStateLocked(path);
                turnStartedAt = state.LastClaudeHumanPromptAt;
                tokenCount = state.LastClaudeTokenCount;
                if (status != "waiting")
                {
                    // Consume remembered turn text so a later official turn_duration (if any) cannot
                    // re-broadcast the same assistant summary under source=claude. A waiting prompt is
                    // mid-turn, so its text must stay for the end_turn broadcast that follows.
                    state.LastClaudeMessage = string.Empty;
                    state.LastClaudeFailed = false;
                    // 完成/失败后清零本轮 token，避免下一轮无 usage 时误带旧值。
                    state.LastClaudeTokenCount = 0L;
                }
            }

            if (status != "waiting" &&
                turnStartedAt.HasValue &&
                AgentCompletionEventParser.TryReadEventTimestamp(root, out var endedAt))
            {
                durationMs = AgentCompletionEventParser.ComputeElapsedDurationMs(turnStartedAt.Value, endedAt);
            }

            string header;
            if (status == "waiting")
            {
                header = "\u7b49\u5f85\u4f60\u786e\u8ba4";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    header += "\uff1a" + SummarizeClaudeMessage(message);
                }
            }
            else
            {
                header = status == "failed"
                    ? "Claude Code Haha task failed"
                    : "Claude Code Haha task completed";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    header += ": " + SummarizeClaudeMessage(message);
                }
            }

            SendNotification(
                header,
                "claude-haha",
                status,
                taskId,
                cwd,
                durationMs,
                ProjectNameResolver.ResolveFolderFirst(rememberedProjectName, cwd, taskId, message),
                // waiting 中途不展示 token；完成/失败才带本轮累计。
                status == "waiting" ? 0L : tokenCount,
                sessionId);
        }

        /// <summary>
        /// 权限审批 waiting 的延迟确认：每轮扫描时检查登记过的待确认工具，只有当文件在宽限期
/// （<see cref="PermissionWaitGracePeriod"/>）内**没有新写入**（既无 tool_result 回传、
/// 也无新行）才判定“卡在审批”并广播。文件持续推进说明工具在自动执行或任务在正常前进，
/// 一律顺延，避免长命令 / 快速连续工具被误报。
/// </summary>
        private void TryFirePendingPermissionWaits()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var due = new List<PendingPermissionWait>();
            lock (_gate)
            {
                foreach (var pending in _pendingPermissionWaits.Values)
                {
                    if (nowUtc - pending.CreatedAtUtc >= pending.GracePeriod)
                    {
                        due.Add(pending);
                    }
                }
            }

            foreach (var pending in due)
            {
                bool fire;
                lock (_gate)
                {
                    fire = _pendingPermissionWaits.Remove(pending.ToolUseId);
                }

                if (!fire)
                {
                    continue;
                }

                // 到期复查：若有结果回传、或文件在宽限期内仍持续写入（自动执行/推进中），
                // 不广播并重新登记（顺延下一个周期）。
                if (!string.IsNullOrWhiteSpace(pending.Path))
                {
                    ProcessFile(pending.Path);
                }

                lock (_gate)
                {
                    var state = string.IsNullOrWhiteSpace(pending.Path) ? null : GetClaudeStateLocked(pending.Path);
                    if (state != null &&
                        (state.AnsweredToolUseIds.Contains(pending.ToolUseId) ||
                         AgentCompletionEventParser.IsPermissionAutoAllowMode(state.LastClaudePermissionMode)))
                    {
                        continue;
                    }

                    if (_eventKeys.Contains("claude-haha:waiting:" + pending.ToolUseId))
                    {
                        continue;
                    }

                    // 顺延总时长已达上限：该 tool_use 始终没有回传 tool_result（上面已复核），
                    // 说明确实卡在用户身上，不再因「文件仍在写入」这一旁证继续压制。
                    var deferredFor = nowUtc - pending.FirstRegisteredAtUtc;
                    if (deferredFor < PermissionWaitMaxDeferral &&
                        (string.IsNullOrWhiteSpace(pending.Path) ||
                         (TryGetFileSignature(pending.Path, out _, out var lastWriteUtc) &&
                          lastWriteUtc > pending.CreatedAtUtc.UtcDateTime)))
                    {
                        // 文件在宽限期内有新写入：任务仍在推进，顺延。
                        _pendingPermissionWaits[pending.ToolUseId] = new PendingPermissionWait(
                            pending.ToolUseId,
                            pending.Header,
                            pending.Path,
                            pending.Cwd,
                            pending.ProjectName,
                            nowUtc,
                            pending.GracePeriod,
                            pending.FirstRegisteredAtUtc);
                        continue;
                    }
                }

                SendNotification(
                    pending.Header,
                    "claude-haha",
                    "waiting",
                    pending.ToolUseId,
                    pending.Cwd,
                    0L,
                    ProjectNameResolver.ResolveFolderFirst(pending.ProjectName, pending.Cwd, pending.ToolUseId, pending.Header));
            }
        }

        /// <summary>
        /// 到期复核 ZCode 的待确认 AskUserQuestion。登记后超过宽限期仍未收到
        /// tool.call.completed（自动代答或用户已答）才会广播 waiting；若注册后
        /// ZCode 日志仍在持续写入（任务仍在推进/代答），则顺延到下一周期。
        /// </summary>
        private void TryFireZCodeWaits()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var due = new List<PendingZCodeWait>();
            lock (_gate)
            {
                foreach (var pending in _pendingZCodeWaits.Values)
                {
                    if (nowUtc - pending.CreatedAtUtc >= PermissionWaitGracePeriod)
                    {
                        due.Add(pending);
                    }
                }
            }

            foreach (var pending in due)
            {
                bool fire;
                lock (_gate)
                {
                    fire = _pendingZCodeWaits.Remove(pending.ToolCallId);
                }

                if (!fire)
                {
                    continue;
                }

                // 问题已结束（后续 turn.completed 可能已广播完成通知），不再补发 waiting。
                if (_eventKeys.Contains("zcode:completed:" + pending.TaskId) ||
                    _eventKeys.Contains("zcode:failed:" + pending.TaskId))
                {
                    continue;
                }

                lock (_gate)
                {
                    // 该 ZCode 日志在宽限期内仍有新写入：任务仍在推进/代答，顺延到下一周期。
                    if (TryGetFileWriteLaterThan(pending.Path, pending.CreatedAtUtc) &&
                        _pendingZCodeWaits.ContainsKey(pending.ToolCallId))
                    {
                        _pendingZCodeWaits[pending.ToolCallId] = new PendingZCodeWait(
                            pending.ToolCallId,
                            pending.TaskId,
                            pending.SessionId,
                            pending.Path,
                            nowUtc);
                        continue;
                    }
                }

                var projectName = string.Empty;
                if (!string.IsNullOrWhiteSpace(pending.SessionId) &&
                    ZCodeSessionReader.TryResolveTitle(pending.SessionId, out var resolvedTitle))
                {
                    projectName = resolvedTitle;
                }

                SendNotification(
                    "\u7b49\u5f85\u4f60\u7684\u64cd\u4f5c\uff1aZCode \u6b63\u5728\u7b49\u5f85\u56de\u590d",
                    "zcode",
                    "waiting",
                    pending.TaskId,
                    string.Empty,
                    0L,
                    ProjectNameResolver.ResolveFolderFirst(projectName, string.Empty, pending.TaskId, "\u7b49\u5f85\u4f60\u7684\u64cd\u4f5c"),
                    sessionId: pending.SessionId);
            }
        }

        private bool TryGetFileWriteLaterThan(string path, DateTimeOffset threshold)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.LastWriteTimeUtc > threshold.UtcDateTime;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHahaTranscriptEvent(JsonElement root)
        {
            var entrypoint = FirstNonEmpty(
                ReadString(root, "entrypoint"),
                ReadString(root, "entry_point"));
            if (string.Equals(entrypoint, "claude-desktop", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var version = ReadString(root, "version");
            if (version.IndexOf("haha", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Haha desktop builds stamp assistant rows as 999.0.0-local.
            return version.StartsWith("999.", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadClaudeStopReason(JsonElement root)
        {
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object)
            {
                var nested = FirstNonEmpty(
                    ReadString(message, "stop_reason"),
                    ReadString(message, "stopReason"));
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }

            return FirstNonEmpty(
                ReadString(root, "stop_reason"),
                ReadString(root, "stopReason"));
        }

        private static bool IsHahaNoResponsePlaceholder(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var normalized = message.Trim();
            return string.Equals(normalized, "No response requested.", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "No response requested", StringComparison.OrdinalIgnoreCase);
        }

        private void ProcessChatGPTLine(string path, string line)
        {
            if (AgentCompletionEventParser.TryExtractChatGPTProjectName(line, out var projectName))
            {
                lock (_gate)
                {
                    if (!_files.TryGetValue(path, out var state))
                    {
                        state = new FileState();
                        _files[path] = state;
                    }

                    state.LastCodexProjectName = projectName;
                }
            }

            var workingDirectory = AgentCompletionEventParser.TryExtractChatGPTWorkingDirectory(line);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                lock (_gate)
                {
                    if (!_files.TryGetValue(path, out var state))
                    {
                        state = new FileState();
                        _files[path] = state;
                    }

                    state.LastCodexCwd = workingDirectory;
                }
            }

            var sessionKind = CodexSessionMetadata.ClassifyLine(line);
            var metadataSessionId = CodexSessionMetadata.TryReadSessionId(line);
            if (sessionKind != CodexSessionKind.Unknown)
            {
                lock (_gate)
                {
                    if (!_files.TryGetValue(path, out var state))
                    {
                        state = new FileState();
                        _files[path] = state;
                    }

                    // A subagent rollout can replay the parent session's original
                    // `session_meta` immediately after its own subagent metadata.
                    // Subagent is therefore a sticky classification and must never
                    // be downgraded to Primary by replayed parent history.
                    state.CodexSessionKind = CodexSessionMetadata.Merge(
                        state.CodexSessionKind,
                        sessionKind);
                    state.LastCodexSessionId = FirstNonEmpty(
                        state.LastCodexSessionId,
                        metadataSessionId);
                }
                return;
            }

            // task_started / token_count 是回合记账行：交给累计器更新本轮 token，
            // 不计为完成事件。
            CodexTurnTokenAccumulator tokens;
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var tokenState))
                {
                    tokenState = new FileState();
                    _files[path] = tokenState;
                }
                tokens = tokenState.CodexTokens;
            }
            if (tokens.TryObserve(line))
            {
                return;
            }

            if (AgentCompletionEventParser.TryReadCodexFunctionCallOutput(line, out var outputCallId))
            {
                ResolvePendingCodexInput(path, outputCallId);
                return;
            }

            if (!AgentCompletionEventParser.TryParseChatGPTLine(line, out var completionEvent))
            {
                return;
            }

            if (!string.Equals(completionEvent.Status, "waiting", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(completionEvent.TurnId) &&
                CodexSessionReader.TryReadLastAssistantMessage(path, completionEvent.TurnId, out var taskLog))
            {
                completionEvent.TaskLog = taskLog;
                _log("Codex 完成正文来自会话 JSONL：turnId=" + completionEvent.TurnId +
                     " textLength=" + taskLog.Length);
            }
            else if (!string.Equals(completionEvent.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                _log("Codex 完成事件没有可读取的当轮 assistant 正文：turnId=" + completionEvent.TurnId);
            }

            completionEvent.SessionId = FirstNonEmpty(
                completionEvent.SessionId,
                GetCodexSessionId(path));

            if (GetCodexSessionKind(path) == CodexSessionKind.Subagent)
            {
                _log("\u5df2\u5ffd\u7565 Codex \u5b50\u4ee3\u7406\u72b6\u6001\u4e8b\u4ef6 (status=" +
                     completionEvent.Status + ", taskId=" + completionEvent.TaskId + "): " +
                     Path.GetFileName(path));
                return;
            }

            // Include the source log path in the in-memory dedupe key. The same
            // turn id can legitimately occur in separate local session files.
            completionEvent.EventKey = "codex:" + path + ":" + completionEvent.EventKey;
            completionEvent.DurationMs = _taskTiming.Resolve(completionEvent, line);
            // waiting 中途不展示 token；完成/失败才携带本轮 token_count 累计。
            completionEvent.TokenCount = completionEvent.Status == "waiting"
                ? 0L
                : TakeCodexTokenCount(path);
            var codexCwd = FirstNonEmpty(completionEvent.Cwd, GetCodexWorkingDirectory(path));
            completionEvent.Cwd = codexCwd;
            var codexProjectName = CodexProjectResolver.Resolve(codexCwd);
            completionEvent.ProjectName = FirstNonEmpty(
                codexProjectName,
                ProjectNameResolver.ResolveFolderFirst(
                    GetCodexProjectName(path),
                    codexCwd,
                    completionEvent.TaskId,
                    completionEvent.Message));

            if (string.Equals(completionEvent.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                RegisterPendingCodexInput(path, completionEvent);
                return;
            }

            DispatchParsedEvent(completionEvent);
        }

        private void RegisterPendingCodexInput(string path, AgentCompletionEventData completionEvent)
        {
            var callId = FirstNonEmpty(completionEvent.CodexInputCallId, completionEvent.TaskId);
            var key = BuildPendingCodexInputKey(path, callId);
            completionEvent.CodexInputCallId = callId;

            lock (_gate)
            {
                if (_disposed || _eventKeys.Contains(completionEvent.EventKey) ||
                    _pendingCodexInputs.ContainsKey(key))
                {
                    return;
                }

                _pendingCodexInputs[key] = new PendingCodexInput(
                    key,
                    completionEvent,
                    DateTimeOffset.UtcNow);
            }

            _log("Codex request_user_input 等待结果校验：callId=" + callId);
        }

        private void ResolvePendingCodexInput(string path, string callId)
        {
            var key = BuildPendingCodexInputKey(path, callId);
            PendingCodexInput? pending = null;
            lock (_gate)
            {
                if (_pendingCodexInputs.TryGetValue(key, out pending))
                {
                    _pendingCodexInputs.Remove(key);
                    // 标记该调用已处理，避免文件轮转/重复日志再次登记并广播。
                    RememberEventLocked(pending.Event.EventKey);
                }
            }

            if (pending != null)
            {
                _log("Codex request_user_input 已收到 function_call_output，抑制 waiting 通知：callId=" +
                     callId);
            }
        }

        private void TryFirePendingCodexInputs()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var due = new List<PendingCodexInput>();
            lock (_gate)
            {
                foreach (var pending in _pendingCodexInputs.Values)
                {
                    if (nowUtc - pending.CreatedAtUtc >= CodexInputValidationGracePeriod)
                    {
                        due.Add(pending);
                    }
                }
            }

            foreach (var pending in due)
            {
                lock (_gate)
                {
                    if (!_pendingCodexInputs.Remove(pending.Key))
                    {
                        continue;
                    }
                }

                _log("Codex request_user_input 未收到 function_call_output，确认是真实 waiting：callId=" +
                     pending.Event.CodexInputCallId);
                DispatchParsedEvent(pending.Event);
            }
        }

        private static string BuildPendingCodexInputKey(string path, string callId)
        {
            return path + "\u0000" + callId;
        }

        private void CancelPendingCodexInputsForPathLocked(string path)
        {
            var prefix = path + "\u0000";
            var keys = new List<string>();
            foreach (var key in _pendingCodexInputs.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }

            foreach (var key in keys)
            {
                _pendingCodexInputs.Remove(key);
            }
        }

        private string GetCodexProjectName(string path)
        {
            lock (_gate)
            {
                return _files.TryGetValue(path, out var state) ? state.LastCodexProjectName : string.Empty;
            }
        }

        private string GetCodexWorkingDirectory(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var state))
                {
                    state = new FileState();
                    _files[path] = state;
                }

                if (string.IsNullOrWhiteSpace(state.LastCodexCwd))
                {
                    state.LastCodexCwd = CodexSessionMetadata.ReadWorkingDirectoryFromFile(path);
                }

                return state.LastCodexCwd;
            }
        }

        /// <summary>取出并清零本轮 Codex token 累计，供终态事件附带（避免跨轮串号）。</summary>
        private long TakeCodexTokenCount(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var state)) return 0L;
                return state.CodexTokens.Take();
            }
        }

        private CodexSessionKind GetCodexSessionKind(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var state))
                {
                    state = new FileState();
                    _files[path] = state;
                }

                if (state.CodexSessionKind == CodexSessionKind.Unknown)
                {
                    state.CodexSessionKind = CodexSessionMetadata.ReadFromFile(path);
                }

                return state.CodexSessionKind;
            }
        }

        private string GetCodexSessionId(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var state))
                {
                    state = new FileState();
                    _files[path] = state;
                }

                if (string.IsNullOrWhiteSpace(state.LastCodexSessionId))
                {
                    state.LastCodexSessionId = CodexSessionMetadata.ReadSessionIdFromFile(path);
                }

                return state.LastCodexSessionId;
            }
        }

        private void ProcessChatGPTLineLegacy(string path, string line)
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
                    ReadString(root, "type"),
                    ReadString(root, "event"));
                if (!IsChatGPTCompletionEvent(eventName))
                {
                    return;
                }

                var taskId = FirstNonEmpty(
                    ReadString(payload, "turn_id"),
                    ReadString(payload, "turn-id"),
                    ReadString(payload, "taskId"),
                    ReadString(payload, "task_id"),
                    ReadString(root, "turn_id"),
                    ReadString(root, "turn-id"),
                    ReadString(root, "taskId"),
                    ReadString(root, "task_id"));
                var timestamp = FirstNonEmpty(ReadString(root, "timestamp"), ReadString(payload, "timestamp"));
                var failed = IsChatGPTFailureEvent(eventName);
                var key = "codex:" + path + ":" + (failed ? "failed" : "completed") + ":" +
                          (string.IsNullOrWhiteSpace(taskId) ? line.GetHashCode().ToString() : taskId) + ":" + timestamp;
                if (!TryRememberEvent(key))
                {
                    return;
                }

                SendNotification(
                    failed ? "ChatGPT task failed" : "ChatGPT task completed",
                    "codex",
                    failed ? "failed" : "completed",
                    taskId,
                    FirstNonEmpty(ReadString(payload, "cwd"), ReadString(root, "cwd")),
                    0L,
                    ProjectNameResolver.ResolveFolderFirst(string.Empty, FirstNonEmpty(ReadString(payload, "cwd"), ReadString(root, "cwd")), taskId, string.Empty));
            }
            catch (JsonException)
            {
                _log("蹇界暐鏃犳硶瑙ｆ瀽鐨?ChatGPT 浼氳瘽浜嬩欢");
            }
        }

        private static bool IsChatGPTCompletionEvent(string eventName)
        {
            return eventName.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("finished", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0
                || IsChatGPTFailureEvent(eventName);
        }

        private static bool IsChatGPTFailureEvent(string eventName)
        {
            return eventName.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("abort", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ProcessTraeLine(string path, string line)
        {
            var source = GetTraeSource(path);
            if (AgentCompletionEventParser.TryParseTraeLine(line, source, out var completionEvent))
            {
                if (IsKnownTraeSubagent(path, completionEvent))
                {
                    LogIgnoredTraeSubagent(completionEvent);
                    return;
                }

                completionEvent.DurationMs = _taskTiming.Resolve(completionEvent, line);
                completionEvent.EventTimeUtc = TryReadLogEventTimeUtc(line);
                if (!TryApplyTraeToastContent(completionEvent))
                {
                    return;
                }
                DispatchParsedEvent(completionEvent);
            }
        }

        // TraeWork CN（原 TRAE SOLO CN）与 TraeCode CN（原 Trae CN）的信号都在 renderer.log 里，格式相同：
        //   [DoneHandler] Stream done event received {"sessionId":"...","status":"completed|canceled|failed","agentMessageId":".."}
        //   [NotificationPort] Sending waiting notification: {"sessionId":"...","agentMessageId":"...","planItemId":".."}
        // canceled 表示用户自己按了停止，不发通知；completed / failed / waiting 各自映射到既有语义。
        // source 由日志路径决定（TraeWork 目录 → traework，Trae CN 目录 → trae），两渠道共用本解析器。
        private void ProcessTraeWorkLine(string path, string line)
        {
            if (AgentCompletionEventParser.TryParseTraeWorkLine(line, GetTraeSource(path), out var completionEvent))
            {
                if (IsKnownTraeSubagent(path, completionEvent))
                {
                    LogIgnoredTraeSubagent(completionEvent);
                    return;
                }

                completionEvent.DurationMs = _taskTiming.Resolve(completionEvent, line);
                ApplyTraeWorkspaceFolder(completionEvent);
                ApplyTraeWorkWorkspaceFolder(path, completionEvent);
                completionEvent.EventTimeUtc = TryReadLogEventTimeUtc(line);

                // TraeCode CN 完成通知挂起等待积分明细：DONE 事件不含消耗数据，日志中唯一
                // 来源是 cn_session_usage_tail 的 show 事件（UI 渲染用量时触发，通常在 DONE 后
                // 约 10 秒，用户未查看面板时可能更晚甚至缺失）。超时则无积分直接发送，
                // TraeWork 不产生稳定的 usage 事件，不能走 TraeCode 的积分等待队列；
                // 否则它会在严格正文校验失败后整条消息被丢弃。
                if (string.Equals(completionEvent.Source, "trae", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(completionEvent.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(completionEvent.AgentMessageId))
                {
                    DeferTraeCompletion(completionEvent);
                    return;
                }

                if (!TryApplyTraeToastContent(completionEvent))
                {
                    if (string.Equals(completionEvent.Source, "traework", StringComparison.OrdinalIgnoreCase))
                    {
                        // TraeWork 当前回合没有可关联的 Windows Toast 时，仍推送可靠的
                        // completed/failed 状态，避免完成事件被静默吞掉。taskLog 保持为空，
                        // Android 会按协议回退到 Message；一旦取得真实 Toast，仍优先展示真实正文。
                        _log("TraeWork 未找到真实任务日志，保留状态通知：" + completionEvent.TaskId);
                        DispatchParsedEvent(completionEvent);
                    }
                    return;
                }
                DispatchParsedEvent(completionEvent);
            }
        }

        /// <summary>
        /// TraeCode 的 DONE 事件不带工作区信息（cwd 恒空），项目名只能得到 "任务 &lt;sessionId&gt;"
        /// 兜底。按 sessionId 反查 workspaceStorage 得到真实工作区目录，注入 Cwd 后由
        /// DispatchParsedEvent 的 ResolveFolderFirst 提取目录名作为项目名。
        /// </summary>
        private void ApplyTraeWorkspaceFolder(AgentCompletionEventData completionEvent)
        {
            if (!string.Equals(completionEvent.Source, "trae", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(completionEvent.Cwd) ||
                string.IsNullOrWhiteSpace(completionEvent.SessionId))
            {
                return;
            }

            try
            {
                if (TraeWorkspaceResolver.TryResolveWorkspaceFolder(completionEvent.SessionId, out var folder))
                {
                    completionEvent.Cwd = folder;
                }
            }
            catch (Exception error)
            {
                _log("TraeCode 工作区反查失败：" + error.Message);
            }
        }

        // TraeWork DONE 事件不带 cwd：按 renderer.log 的 logs\<时间戳> 目录反查
        // workspaceStorage（trae.workbench.logsHome），得到工作区本地目录。
        private void ApplyTraeWorkWorkspaceFolder(string path, AgentCompletionEventData completionEvent)
        {
            if (!string.Equals(completionEvent.Source, "traework", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(completionEvent.Cwd))
            {
                return;
            }

            try
            {
                if (TraeWorkWorkspaceResolver.TryResolveWorkspaceFolder(path, out var folder))
                {
                    completionEvent.Cwd = folder;
                }
            }
            catch (Exception error)
            {
                _log("TraeWork 工作区反查失败：" + error.Message);
            }
        }

        // TraeCode CN 的 renderer.log 不含 Agent 回复正文，只有与当前终态时间匹配的
        // Windows toast 才能作为真实任务日志。state.vscdb 中的 parsed_query 是用户
        // 提示词，绝不能作为完成日志回退。
        private readonly Func<string, DateTime, (string Title, string Body)?> _readTraeToast;

        private static (string Title, string Body)? ReadTraeToast(string source, DateTime eventTimeUtc) =>
            ToastNotificationReader.TryReadLatestRealTaskLog(source, eventTimeUtc, out var title, out var body)
                ? (title, body) : ((string, string)?)null;

        private bool TryApplyTraeToastContent(AgentCompletionEventData completionEvent)
        {
            var source = completionEvent.Source;
            if ((!string.Equals(source, "trae", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(source, "traework", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            try
            {
                var eventTimeUtc = completionEvent.EventTimeUtc?.UtcDateTime ?? DateTime.UtcNow;
                if (string.Equals(source, "trae", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source, "traework", StringComparison.OrdinalIgnoreCase))
                {
                    var toast = _readTraeToast(source, eventTimeUtc);
                    if (toast.HasValue &&
                        ToastNotificationReader.TryBuildRealTaskMessage(
                            toast.Value.Title, toast.Value.Body, out var strictMessage, out var strictTaskLog))
                    {
                        completionEvent.Message = strictMessage;
                        completionEvent.TaskLog = strictTaskLog;
                        _log(source + " 真实任务日志已匹配：agentMessageId=" +
                             completionEvent.AgentMessageId + " textLength=" + strictTaskLog.Length);
                        return true;
                    }

                    _log(source + " 未找到与终态时间匹配的真实任务日志：agentMessageId=" +
                         completionEvent.AgentMessageId + " eventTime=" +
                         eventTimeUtc.ToString("O", CultureInfo.InvariantCulture));
                    return false;
                }

            }
            catch (Exception error)
            {
                _log("Trae toast 通知读取失败：" + error.Message);
            }
            return false;
        }

        private void DeferTraeCompletion(AgentCompletionEventData completionEvent)
        {
            var pending = new PendingTraeCompletion
            {
                Event = completionEvent,
                AgentMessageId = completionEvent.AgentMessageId,
                DeadlineUtc = DateTime.UtcNow + TraeUsageWaitWindow
            };
            lock (_gate)
            {
                _pendingTraeCompletions.Add(pending);
            }

            _log("TraeCode 任务完成，等待积分明细（最长 " +
                 (int)TraeUsageWaitWindow.TotalSeconds + " 秒）：" + completionEvent.TaskId);
        }

        private void ProcessTraeUsageLine(string path, string line)
        {
            if (!string.Equals(GetTraeSource(path), "trae", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!AgentCompletionEventParser.TryParseTraeUsageLine(line, out var agentMessageId, out var usageSummary))
            {
                return;
            }

            PendingTraeCompletion? matched = null;
            lock (_gate)
            {
                for (var i = 0; i < _pendingTraeCompletions.Count; i++)
                {
                    if (string.Equals(_pendingTraeCompletions[i].AgentMessageId, agentMessageId, StringComparison.Ordinal))
                    {
                        matched = _pendingTraeCompletions[i];
                        _pendingTraeCompletions.RemoveAt(i);
                        break;
                    }
                }
            }

            if (matched == null)
            {
                // 无挂起项：晚到的 show（用户回看旧用量）或已超时发出，直接忽略。
                return;
            }

            matched.Event.Credits = usageSummary;
            _log("已捕获 TraeCode 积分明细：agentMessageId=" + agentMessageId + " usage=" + usageSummary);
            if (!TryApplyTraeToastContent(matched.Event))
            {
                return;
            }
            DispatchParsedEvent(matched.Event);
        }

        private void FlushExpiredTraeCompletions()
        {
            List<PendingTraeCompletion> expired;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                expired = new List<PendingTraeCompletion>();
                for (var i = _pendingTraeCompletions.Count - 1; i >= 0; i--)
                {
                    if (_pendingTraeCompletions[i].DeadlineUtc <= now)
                    {
                        expired.Add(_pendingTraeCompletions[i]);
                        _pendingTraeCompletions.RemoveAt(i);
                    }
                }
            }

            foreach (var pending in expired)
            {
                _log("TraeCode 积分明细未在窗口内到达，直接发送：" + pending.Event.TaskId);
                if (!TryApplyTraeToastContent(pending.Event))
                {
                    continue;
                }
                DispatchParsedEvent(pending.Event);
            }
        }

        private static string GetTraeSource(string path)
        {
            return path.IndexOf("TRAE SOLO CN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("TraeWork CN", StringComparison.OrdinalIgnoreCase) >= 0
                ? "traework"
                : "trae";
        }

        /// <summary>
        /// Claude 转录在上下文压缩或会话恢复时会把旧行重新追加到文件末尾，时间戳仍是
        /// 事件发生时的旧值。文件基线只负责启动瞬间，内存去重又随进程重启丢失，因此
        /// 追加区里早于本次启动的记录必须按重放丢弃，否则会把已通知过的完成再推一次。
        /// </summary>
        private bool IsReplayedTranscriptEvent(string timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(
                    timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var eventTime))
            {
                return false;
            }

            if (eventTime.ToUniversalTime() >= _startedAtUtc - ReplayedTranscriptClockSkew)
            {
                return false;
            }

            _detailLog("忽略 Claude 转录重放事件：事件时间 " + timestamp + " 早于本次监控启动");
            return true;
        }

        private static DateTimeOffset? TryReadLogEventTimeUtc(string line)
        {
            var trimmed = (line ?? string.Empty).TrimStart();
            if (trimmed.Length == 0)
            {
                return null;
            }

            var firstSpace = trimmed.IndexOf(' ');
            var token = firstSpace > 0 ? trimmed.Substring(0, firstSpace) : trimmed;
            return DateTimeOffset.TryParse(
                token,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var timestamp)
                ? timestamp.ToUniversalTime()
                : (DateTimeOffset?)null;
        }

        private void RememberTraeSessionKind(string source, string sessionId, bool isSubagent)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (_gate)
            {
                _traeSessionKinds[BuildTraeSessionKey(source, sessionId)] = isSubagent;
            }
        }

        private bool IsKnownTraeSubagent(string path, AgentCompletionEventData completionEvent)
        {
            if (string.IsNullOrWhiteSpace(completionEvent.SessionId))
            {
                return false;
            }

            var key = BuildTraeSessionKey(completionEvent.Source, completionEvent.SessionId);
            lock (_gate)
            {
                if (_traeSessionKinds.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            if (!TryReadTraeSessionKind(path, completionEvent.SessionId, out var isSubagent))
            {
                return false;
            }

            RememberTraeSessionKind(completionEvent.Source, completionEvent.SessionId, isSubagent);
            return isSubagent;
        }

        private static bool TryReadTraeSessionKind(string path, string sessionId, out bool isSubagent)
        {
            isSubagent = false;
            var candidates = new List<string> { path };
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    foreach (var candidate in Directory.EnumerateFiles(directory, "renderer*.log", SearchOption.TopDirectoryOnly))
                    {
                        if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(candidate);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    using var stream = new FileStream(
                        candidate,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
                    string? line;
                    var found = false;
                    var candidateKind = false;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf(sessionId, StringComparison.OrdinalIgnoreCase) < 0 ||
                            !AgentRecordClassifier.TryParseTraeSessionLine(line, out var parsedSessionId, out var parsedKind) ||
                            !string.Equals(parsedSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        found = true;
                        candidateKind = parsedKind;
                    }

                    if (found)
                    {
                        isSubagent = candidateKind;
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return false;
        }

        private void LogIgnoredTraeSubagent(AgentCompletionEventData completionEvent)
        {
            _log("\u5df2\u5ffd\u7565 " + completionEvent.Source + " \u5b50\u4ee3\u7406\u72b6\u6001\u4e8b\u4ef6 (status=" +
                 completionEvent.Status + ", taskId=" + completionEvent.TaskId + ")");
        }

        private static string BuildTraeSessionKey(string source, string sessionId)
        {
            return (source ?? string.Empty).Trim().ToLowerInvariant() + "\u001f" +
                   (sessionId ?? string.Empty).Trim();
        }

        private void ProcessDoubaoLine(string line)
        {
            // Parse only the reliable network terminal signal here. The assistant
            // DOM is often updated shortly afterwards, so reading it synchronously
            // produces either an empty result or an older turn.
            if (!AgentCompletionEventParser.TryParseDoubaoRlogLine(
                    line, out var completionEvent, readRenderedContent: false))
            {
                return;
            }

            if (!string.Equals(completionEvent.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                DispatchParsedEvent(completionEvent);
                return;
            }

            lock (_gate)
            {
                if (_disposed ||
                    _eventKeys.Contains(completionEvent.EventKey) ||
                    !_pendingDoubaoCompletions.Add(completionEvent.EventKey))
                {
                    return;
                }
            }

            _ = CompleteDoubaoAfterRenderAsync(completionEvent);
        }

        private async Task CompleteDoubaoAfterRenderAsync(AgentCompletionEventData completionEvent)
        {
            try
            {
                foreach (var delay in DoubaoAssistantReadDelays)
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }

                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }
                    }

                    var taskLog = DoubaoTaskContentSource.TryReadAssistantMessageForInput(
                        completionEvent.CorrelationText,
                        out var contentSource);
                    if (string.IsNullOrWhiteSpace(taskLog))
                    {
                        continue;
                    }

                    completionEvent.TaskLog = taskLog;
                    completionEvent.Message = DoubaoTaskContentSource.BuildCompletedMessage(taskLog);
                    _log("豆包完成正文已读取：source=" + contentSource + " taskId=" +
                         completionEvent.TaskId + " textLength=" + taskLog.Length);
                    DispatchParsedEvent(completionEvent);
                    return;
                }

                // A terminal rlog without a correlated assistant row is not a
                // valid user-facing task log. Dropping it is safer than showing a
                // prompt, title, or generic completion phrase as if it were the
                // agent's answer.
                _log("豆包完成事件未读取到对应助手正文，跳过通知：taskId=" +
                     completionEvent.TaskId);
            }
            catch (Exception error)
            {
                _log("豆包完成正文读取失败，跳过通知：taskId=" +
                     completionEvent.TaskId + " error=" + error.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _pendingDoubaoCompletions.Remove(completionEvent.EventKey);
                }
            }
        }

        private void ProcessWorkBuddyLine(string path, string line)
        {
            var workBuddySource = AgentLogPaths.IsWorkBuddyAiLog(path)
                ? "workbuddy-ai"
                : "workbuddy";
            var fallbackSessionId = RememberWorkBuddySessionId(path, line);
            RememberWorkBuddyRequestId(path, line);
            ObserveWorkBuddyCompaction(path, line);

            // 真正需要用户确认的等待（批量删除确认、权限确认、AskUserQuestion 等）：
            // SessionManager 进入 pending（signal=awaiting_input, from=working, to=pending）
            // 时每轮只记录一次；普通工具权限不会进入 pending，不会误报。每轮等待广播
            // 一次 waiting 到手机，用户确认后（input_resolved）不补发完成通知。
            if (AgentCompletionEventParser.TryParseWorkBuddyAwaitingInput(line, out var awaitingEvent))
            {
                // session 级去重：同一会话等待中重复的轮询/transition 行只推一次。
                lock (_gate)
                {
                    if (!_workbuddyWaitingSessions.Add(awaitingEvent.SessionId))
                    {
                        return;
                    }
                }
                awaitingEvent.EventKey = "workbuddy:" + path + ":" + awaitingEvent.EventKey;
                awaitingEvent.RequestId = "workbuddy:" + awaitingEvent.SessionId;
                PrepareWorkBuddyEvent(awaitingEvent, workBuddySource);
                awaitingEvent.ProjectName = FirstNonEmpty(
                    awaitingEvent.ProjectName,
                    GetWorkBuddyProjectName(path));
                if (!ApplyWorkBuddySessionContent(awaitingEvent, path))
                {
                    ApplyToastNotificationContent(awaitingEvent);
                }
                DispatchParsedEvent(awaitingEvent);
                return;
            }

            // 新版（8-23 起）等待信号：deriveExportedPendingInputKind 轮询行（约 30 秒/次），
            // 携带 runState=awaiting_input。同 session+kind 只推一次；用户在界面处理后
            // 任务恢复（出现 task state transition 到 working/completed/planning），
            // 下面恢复检测清除去重标记，允许下一轮等待再次推送。
            if (AgentCompletionEventParser.TryParseWorkBuddyPendingInput(line, out var pendingEvent))
            {
                lock (_gate)
                {
                    var state = GetWorkBuddyStateLocked(path);
                    if (!state.WorkBuddyAwaitingKeys.Add(pendingEvent.TaskId))
                    {
                        return; // 同一轮等待已推送过
                    }
                    // session 级兜底：旧格式 transition 行（turnSeq 递增）可能已把该会话
                    // 标记为等待中，此时新格式轮询行不得再推第二条。
                    if (!_workbuddyWaitingSessions.Add(pendingEvent.SessionId))
                    {
                        return;
                    }
                }

                pendingEvent.EventKey = "workbuddy:waiting-p:" + pendingEvent.TaskId;
                pendingEvent.RequestId = "workbuddy:" + pendingEvent.SessionId;
                PrepareWorkBuddyEvent(pendingEvent, workBuddySource);
                pendingEvent.ProjectName = FirstNonEmpty(
                    pendingEvent.ProjectName,
                    GetWorkBuddyProjectName(path));
                if (!ApplyWorkBuddySessionContent(pendingEvent, path))
                {
                    ApplyToastNotificationContent(pendingEvent);
                }
                DispatchParsedEvent(pendingEvent);
                return;
            }

            // 恢复检测：任务恢复工作（working/planning/completed/turn_started）说明上一轮
            // 等待已被用户处理，清除该 session 的等待去重标记（含全局 EventKey），
            // 否则同会话下一轮等待无法再次推送。
            if (line.IndexOf("task state transition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var transitionSessionId = AgentCompletionEventParser.ReadWorkBuddyLogSessionId(line);
                if (!string.IsNullOrWhiteSpace(transitionSessionId))
                {
                    lock (_gate)
                    {
                        var state = GetWorkBuddyStateLocked(path);
                        state.WorkBuddyAwaitingKeys.RemoveWhere(
                            key => key.StartsWith(transitionSessionId + ":", StringComparison.Ordinal));
                        _workbuddyWaitingSessions.Remove(transitionSessionId);
                    }
                    _eventKeys.RemoveWhere(
                        key => key.StartsWith(workBuddySource + ":waiting-p:" + transitionSessionId + ":", StringComparison.Ordinal));
                }
            }

            if (AgentCompletionEventParser.IsWorkBuddyFailureMarker(line))
            {
                lock (_gate)
                {
                    if (!_files.TryGetValue(path, out var state))
                    {
                        state = new FileState();
                        _files[path] = state;
                    }

                    state.WorkBuddyFailureMarker = true;
                    state.WorkBuddyFailureMarkerTime = DateTimeOffset.UtcNow;
                }
                return;
            }

            // executeStopHooks 只表示当前模型运行段结束。它会在普通回合、上下文
            // 压缩和自动重试时都出现，即使 stopReason=completed 也不代表用户任务
            // 完成；真正终态由 Prompt completed / session_end / 最终 RESULT 提供。
            if (line.IndexOf("executeStopHooks", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var stopHookSessionId = AgentCompletionEventParser.ReadWorkBuddyLogAssignment(line, "sessionId");
                var stopHookReason = AgentCompletionEventParser.ReadWorkBuddyLogAssignment(line, "stopReason");
                _log("忽略 WorkBuddy 非权威 executeStopHooks 终态：session=" +
                     (string.IsNullOrWhiteSpace(stopHookSessionId) ? "unknown" : stopHookSessionId) +
                     " stopReason=" +
                     (string.IsNullOrWhiteSpace(stopHookReason) ? "unknown" : stopHookReason));
                return;
            }

            if (AgentCompletionEventParser.TryParseWorkBuddyPromptCompletedLine(line, out var promptDoneEvent))
            {
                if (ShouldSuppressWorkBuddyTerminal(path, promptDoneEvent.Status))
                {
                    _log("WorkBuddy 正在进行上下文压缩/自动续跑，忽略中间终态：session=" +
                         promptDoneEvent.SessionId + " status=" + promptDoneEvent.Status);
                    return;
                }

                promptDoneEvent.EventKey = WorkBuddyCompletionEventKey(
                    promptDoneEvent,
                    AgentCompletionEventParser.ReadWorkBuddyLogTimestamp(line));
                PrepareWorkBuddyEvent(promptDoneEvent, workBuddySource);
                promptDoneEvent.ProjectName = FirstNonEmpty(
                    promptDoneEvent.ProjectName,
                    GetWorkBuddyProjectName(path));
                if (!ApplyWorkBuddySessionContent(promptDoneEvent, path))
                {
                    ApplyToastNotificationContent(promptDoneEvent);
                }
                DispatchParsedEvent(promptDoneEvent);
                return;
            }

            if (!AgentCompletionEventParser.TryParseWorkBuddyLine(line, fallbackSessionId, out var completionEvent))
            {
                return;
            }

            if (completionEvent.Status == "completed" || completionEvent.Status == "failed")
            {
                if (ShouldSuppressWorkBuddyTerminal(path, completionEvent.Status))
                {
                    _log("WorkBuddy 正在进行上下文压缩/自动续跑，忽略中间终态：session=" +
                         completionEvent.SessionId + " status=" + completionEvent.Status);
                    return;
                }

                var failureMarked = ShouldTreatWorkBuddyTerminalAsFailure(
                    path,
                    completionEvent.TaskId,
                    completionEvent.Status);
                if (failureMarked && completionEvent.Status == "completed")
                {
                    completionEvent.Status = "failed";
                    completionEvent.Message = "WorkBuddy \u4efb\u52a1\u5931\u8d25";
                    completionEvent.EventKey = "workbuddy:failed:" + completionEvent.TaskId;
                }
            }

            // WorkBuddy broadcasts the same ACP update to several streams. Keep
            // the file in the monitor key so identical task ids in separate
            // workspace logs remain independent before server-level dedupe.
            // WorkBuddy 完成/失败事件以全局 sessionId 为主键去重，避免同一会话在主
            // 线程日志与会话日志各出现一次时重复推送手机。
            completionEvent.EventKey = WorkBuddyCompletionEventKey(
                completionEvent,
                AgentCompletionEventParser.ReadWorkBuddyLogTimestamp(line));
            PrepareWorkBuddyEvent(completionEvent, workBuddySource);
            completionEvent.ProjectName = FirstNonEmpty(
                completionEvent.ProjectName,
                GetWorkBuddyProjectName(path));
            // 正文优先级（与 Codex 直接读会话日志一致）：先取 WorkBuddy 自身会话库
            // （workbuddy.db sessions 表）的真实任务标题与 projects/*.jsonl 的真实
            // 任务日志；仅当真实内容不可得时才回退 Windows toast；再不可得则保留
            // 解析器固定文案。
            if (!ApplyWorkBuddySessionContent(completionEvent, path))
            {
                ApplyToastNotificationContent(completionEvent);
            }
            DispatchParsedEvent(completionEvent);
        }

        private static void PrepareWorkBuddyEvent(
            AgentCompletionEventData completionEvent,
            string source)
        {
            completionEvent.Source = source;
            if (completionEvent.EventKey.StartsWith("workbuddy:", StringComparison.OrdinalIgnoreCase))
            {
                completionEvent.EventKey = source + completionEvent.EventKey.Substring("workbuddy".Length);
            }

            if (completionEvent.RequestId.StartsWith("workbuddy:", StringComparison.OrdinalIgnoreCase))
            {
                completionEvent.RequestId = source + ":" +
                    completionEvent.RequestId.Substring("workbuddy:".Length);
            }
        }

        private void RememberWorkBuddyRequestId(string path, string line)
        {
            var requestId = AgentCompletionEventParser.ReadWorkBuddyRequestIdFromLine(line);
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            lock (_gate)
            {
                GetWorkBuddyStateLocked(path).LastWorkBuddyRequestId = requestId;
            }
        }

        private string RememberWorkBuddySessionId(string path, string line)
        {
            var sessionId = AgentCompletionEventParser.ReadWorkBuddyLogSessionId(line);
            lock (_gate)
            {
                var state = GetWorkBuddyStateLocked(path);
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    state.LastWorkBuddySessionId = sessionId;
                }
                return state.LastWorkBuddySessionId;
            }
        }

        /// <summary>
        /// WorkBuddy 在上下文压缩或自动重试前会先调用 executeStopHooks，并把
        /// stopReason 写成 completed。这个 completed 只表示本次模型运行段结束，
        /// 后面还会继续 compact/resume；记录该生命周期，供终态分支统一屏蔽。
        /// </summary>
        private void ObserveWorkBuddyCompaction(
            string path,
            string line)
        {
            var startsCompaction =
                line.IndexOf("Aborting session for context compaction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("willRetry=true reason=compacting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("COMPACT_STARTED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("broadcastCompactStarted", StringComparison.OrdinalIgnoreCase) >= 0;
            var endsCompaction =
                line.IndexOf("COMPACT_ENDED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("broadcastCompactCompleted", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!startsCompaction && !endsCompaction)
            {
                return;
            }

            lock (_gate)
            {
                var state = GetWorkBuddyStateLocked(path);
                if (startsCompaction)
                {
                    state.WorkBuddyCompactionInProgress = true;
                }

                if (endsCompaction)
                {
                    state.WorkBuddyCompactionInProgress = false;
                }
            }
        }

        private bool ShouldSuppressWorkBuddyTerminal(string path, string status)
        {
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lock (_gate)
            {
                return _files.TryGetValue(path, out var state) &&
                       state.WorkBuddyCompactionInProgress;
            }
        }

        private FileState GetWorkBuddyStateLocked(string path)
        {
            if (!_files.TryGetValue(path, out var state))
            {
                state = new FileState();
                _files[path] = state;
            }
            return state;
        }

        // 完成键 = 状态 + 会话 + 行时间戳。同一条 WorkBuddy 完成行在主线程日志与
        // 会话日志中时间戳相同,仍会被去重;同一会话的下一轮完成时间戳不同,不再被吞掉。
        private static string WorkBuddyCompletionEventKey(AgentCompletionEventData completionEvent, string logTimestamp)
        {
            var id = string.IsNullOrWhiteSpace(completionEvent.SessionId)
                ? completionEvent.TaskId
                : completionEvent.SessionId.Trim();
            return "workbuddy:" + completionEvent.Status + ":" + id + ":" + (logTimestamp ?? string.Empty);
        }

        private bool ShouldTreatWorkBuddyTerminalAsFailure(
            string path,
            string taskId,
            string status)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var state))
                {
                    return false;
                }

                // 校验机制：仅当失败标记是近 30 秒内打下的（真正与本轮完成相邻的错误），
                // 才允许把 completed 翻转为 failed；陈旧的标记（来自更早的轮次/工具重试）
                // 不得污染后续正常完成，避免「完成被误判为失败」（见 09-24 b91b6456）。
                var markerFresh = state.WorkBuddyFailureMarkerTime.HasValue &&
                    (DateTimeOffset.UtcNow - state.WorkBuddyFailureMarkerTime.Value) <= TimeSpan.FromSeconds(30);
                var failureMarked = state.WorkBuddyFailureMarker && markerFresh;
                var repeatedFailure = string.Equals(
                    state.LastWorkBuddyFailedTaskId,
                    taskId,
                    StringComparison.Ordinal);
                state.WorkBuddyFailureMarker = false;
                state.WorkBuddyFailureMarkerTime = null;

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    state.LastWorkBuddyFailedTaskId = taskId;
                }
                else if (failureMarked || repeatedFailure)
                {
                    state.LastWorkBuddyFailedTaskId = taskId;
                }
                else
                {
                    state.LastWorkBuddyFailedTaskId = string.Empty;
                }

                return failureMarked || repeatedFailure;
            }
        }

        private static string GetWorkBuddyProjectName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var separator = name.IndexOf("__", StringComparison.Ordinal);
            var candidate = separator > 0 ? name.Substring(0, separator) : name;
            if (string.Equals(candidate, ".workbuddy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, ".workbuddy-ai", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, "workbuddy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, "unknown-workspace", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return ProjectNameResolver.Normalize(candidate);
        }

        private void ProcessZCodeDesktopLine(string line)
        {
            if (!AgentCompletionEventParser.TryParseZCodeDesktopLine(line, out var completionEvent))
            {
                return;
            }

            completionEvent.DurationMs = _taskTiming.Resolve(completionEvent, line);
            if (!ApplyZCodeSessionContent(completionEvent))
            {
                ApplyToastNotificationContent(completionEvent);
            }
            DispatchParsedEvent(completionEvent);
        }

        private void ProcessZCodeLine(string path, string line)
        {
            if (ProcessZCodeWaitingLine(path, line))
            {
                return;
            }

            if (!AgentCompletionEventParser.TryParseZCodeCliLine(line, out var completionEvent))
            {
                return;
            }

            completionEvent.DurationMs = _taskTiming.Resolve(completionEvent, line);
            if (!ApplyZCodeSessionContent(completionEvent))
            {
                ApplyToastNotificationContent(completionEvent);
            }
            DispatchParsedEvent(completionEvent);
        }

        /// <summary>
        /// ZCode 的“待处理（等待用户）”状态。ZCode 在等待真人回复时会调用
        /// <see cref="AskUserQuestion"/> 工具（CLI JSONL 中体现为 tool.call.started /
        /// tool.permission.resolved，需 <see cref="Context"/> 里 toolName=AskUserQuestion）。
        /// 但自动模式（yolo/bypassPermissions 等）下 ZCode 会自行代答、毫秒级自动结束，
        /// 立即广播就是误报。这里沿用 claude-haha 的“宽限期复核”模式：先登记，等
        /// tool.call.completed 回传则取消；超过宽限期仍未结束且日志无新写入，才广播 waiting。
        /// </summary>
        private bool ProcessZCodeWaitingLine(string path, string line)
        {
            var action = AgentCompletionEventParser.ClassifyZCodeWaiting(
                line, out var toolCallId, out var sessionId, out var turnId);

            if (action == AgentCompletionEventParser.ZCodeWaitingAction.Register)
            {
                lock (_gate)
                {
                    if (!_pendingZCodeWaits.ContainsKey(toolCallId))
                    {
                        var fallbackTaskId = !string.IsNullOrWhiteSpace(sessionId)
                            ? "zcode:" + sessionId + ":" + toolCallId
                            : "zcode:" + toolCallId;
                        _pendingZCodeWaits[toolCallId] = new PendingZCodeWait(
                            toolCallId,
                            FirstNonEmpty(turnId, fallbackTaskId),
                            sessionId,
                            path,
                            DateTimeOffset.UtcNow);
                    }
                }

                return true;
            }

            if (action == AgentCompletionEventParser.ZCodeWaitingAction.Cancel)
            {
                lock (_gate)
                {
                    _pendingZCodeWaits.Remove(toolCallId);
                }

                return true;
            }

            // None / Ignore：不登记、不取消、不回传给完成解析器是交给下面的 ZCode 完成解析。
            // Ignore（自动模式/已代答）本就是不广播，返回 false 让该行继续走完成解析无意义
            // （tool.* 事件本就不会被 TryParseZCodeCliLine 命中），统一返回 true 收敛即可。
            return action == AgentCompletionEventParser.ZCodeWaitingAction.Ignore;
        }

        // 通知正文统一注入：应用发了 Windows toast（含真实任务名与 Agent 回复，
        // 如 ZCode 的 "任务已完成 / 任务：修复打包软件logo与名称问题"）时优先采用，
        // 否则保留解析器给出的消息。新适配应用优先参考此方案。
        private void ApplyToastNotificationContent(AgentCompletionEventData completionEvent)
        {
            try
            {
                if (ToastNotificationReader.TryReadLatest(
                        completionEvent.Source, DateTime.UtcNow, out var toastTitle, out var toastBody))
                {
                    completionEvent.Message = ToastNotificationReader.BuildMessage(toastTitle, toastBody);
                }
            }
            catch (Exception error)
            {
                _log("Toast 通知读取失败（" + completionEvent.Source + "）：" + error.Message);
            }
        }

        // WorkBuddy 专用正文/标题注入：像 Codex 直接读会话日志那样，按 ACP 事件的
        // sessionId 反查 workbuddy.db sessions 表拿到真实任务标题（→ 通知标题/任务型名称），
        // 并从 projects/*.jsonl 取最后一条 assistant 回复作为真实任务日志（→ 正文）。
        // 卡片主名称遵循 WorkBuddy 空间模型：固定文件夹取项目名，无固定文件夹取任务名；
        // 只有日志文件名推导出的工作区兜底名（例如时间戳目录）才允许被替换。
        // 仅在真实内容不可得时才由调用方回退到 Windows toast。
        private bool ApplyWorkBuddySessionContent(AgentCompletionEventData completionEvent, string path)
        {
            var found = false;
            try
            {
                if (string.IsNullOrWhiteSpace(completionEvent.SessionId))
                {
                    return false;
                }

                var title = string.Empty;
                if (WorkBuddySessionReader.TryResolveTitle(
                        completionEvent.SessionId, out var resolvedTitle) &&
                    !string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    title = resolvedTitle;
                    // WorkBuddy 的 ACP 日志文件名通常只能推导出时间戳工作区目录；
                    // 会话库标题才是任务型事件的真实任务名称。若事件明确给出了
                    // 项目名，则保留项目名，项目型与任务型两种展示方式都能成立。
                    var existingProject = completionEvent.ProjectName ?? string.Empty;
                    var fallbackProject = GetWorkBuddyProjectName(path);
                    if (string.IsNullOrWhiteSpace(existingProject) ||
                        string.Equals(existingProject.Trim(), fallbackProject,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        completionEvent.ProjectName = resolvedTitle;
                    }
                    completionEvent.NotificationTitle =
                        BuildWorkBuddySessionTitle(completionEvent.Status, title);
                    found = true;
                }

                if (WorkBuddySessionReader.TryResolveProjectOrTaskName(
                        completionEvent.SessionId, out var resolvedDisplayName, out var isProjectName))
                {
                    var existingProject = completionEvent.ProjectName ?? string.Empty;
                    var fallbackProject = GetWorkBuddyProjectName(path);
                    // playground 没有固定目录，任务标题始终是主名称；固定空间则只
                    // 在事件没有项目名或仍是日志文件兜底名时补入项目目录名。
                    if (!isProjectName || string.IsNullOrWhiteSpace(existingProject) ||
                        string.Equals(existingProject.Trim(), fallbackProject,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        completionEvent.ProjectName = resolvedDisplayName;
                    }
                    if (string.IsNullOrWhiteSpace(title) && !isProjectName)
                    {
                        title = resolvedDisplayName;
                    }
                    found = true;
                }

                // 任务日志：真实会话日志（最后一条助手回复）优先；等待事件不采用
                // （助手最后回复与"等待你的操作"问题无关，保留解析器细节）。
                var isWaiting = string.Equals(
                    completionEvent.Status, "waiting", StringComparison.OrdinalIgnoreCase);
                if (!isWaiting &&
                    WorkBuddySessionReader.TryResolveTaskLog(
                        completionEvent.SessionId, out var taskLog) &&
                    !string.IsNullOrWhiteSpace(taskLog))
                {
                    completionEvent.Message = taskLog;
                    found = true;
                }
                else if (found && !string.IsNullOrWhiteSpace(title))
                {
                    completionEvent.Message = BuildWorkBuddySessionMessage(
                        completionEvent.Status, title, completionEvent.Message);
                }
            }
            catch (Exception error)
            {
                _log("WorkBuddy 会话库读取失败：" + error.Message);
            }

            // 用量（积分优先、其次 token、都没有则忽略）即使标题/日志未命中也尝试。
            ApplyWorkBuddyUsage(completionEvent, path);
            return found;
        }

        // WorkBuddy 用量注入：session_usage 有积分明细（credit_json 按请求匹配）则放
        // credits；否则 used 作为 token；都没有则忽略（通知卡不展示该项）。
        private void ApplyWorkBuddyUsage(AgentCompletionEventData completionEvent, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(completionEvent.SessionId))
                {
                    return;
                }

                var requestId = string.Empty;
                lock (_gate)
                {
                    requestId = GetWorkBuddyStateLocked(path).LastWorkBuddyRequestId;
                }

                if (WorkBuddySessionReader.TryResolveUsage(
                        completionEvent.SessionId, requestId, out var credits, out var tokenCount))
                {
                    if (!string.IsNullOrWhiteSpace(credits))
                    {
                        completionEvent.Credits = credits;
                    }
                    else if (tokenCount > 0L)
                    {
                        completionEvent.TokenCount = tokenCount;
                    }
                }
            }
            catch (Exception error)
            {
                _log("WorkBuddy 用量读取失败：" + error.Message);
            }
        }

        private static string BuildWorkBuddySessionTitle(string eventStatus, string title)
        {
            return "「" + title + "」 " + WorkBuddyStatusLabel(eventStatus);
        }

        private static string WorkBuddyStatusLabel(string eventStatus)
        {
            return string.Equals(eventStatus, "completed", StringComparison.OrdinalIgnoreCase)
                ? "任务已完成"
                : string.Equals(eventStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "任务失败"
                    : "等待你的操作";
        }

        private static string BuildWorkBuddySessionMessage(
            string eventStatus, string title, string originalMessage)
        {
            // 复用解析器已提取的「：」之后真实细节（失败原因/等待问题）。
            var colon = (originalMessage ?? string.Empty).LastIndexOf('：');
            var detail = colon >= 0 ? (originalMessage ?? string.Empty).Substring(colon + 1).Trim() : string.Empty;

            return "「" + title + "」 " + WorkBuddyStatusLabel(eventStatus) +
                   (string.IsNullOrWhiteSpace(detail) ? string.Empty : "：" + detail);
        }

        // ZCode 专用正文/标题/用量注入：与 WorkBuddy 同思路，按事件的 sessionId
        // 反查 ~/.zcode/cli/db/db.sqlite —— session 表拿到真实任务标题（→ 通知标题），
        // turn_usage 表拿到该 turn 的真实耗时与 computed_total_tokens（日志里是
        // "[Redacted]" 脱敏值，数据库才是可靠来源），part 表取最后一条 assistant
        // 正文作为真实任务日志。仅在真实内容不可得时才由调用方回退 Windows toast。
        private bool ApplyZCodeSessionContent(AgentCompletionEventData completionEvent)
        {
            var found = false;
            try
            {
                if (string.IsNullOrWhiteSpace(completionEvent.SessionId))
                {
                    return false;
                }

                // 任务名称（通知标题 + 项目名兜底）。
                if (ZCodeSessionReader.TryResolveTitle(
                        completionEvent.SessionId, out var resolvedTitle) &&
                    !string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    // 标题随当轮对话变化：优先取该 turn 的第一条用户提问作为标题主体，
                    // 让手机卡片能看到"这次在做什么"，而非永远固定为会话名。会话名仍作
                    // 为稳定项目名保留（手机端据此归类/分组同会话的多轮）。
                    var turnQuery = string.Empty;
                    ZCodeSessionReader.TryResolveTurnQuery(
                        completionEvent.SessionId, completionEvent.TaskId ?? string.Empty, out turnQuery);
                    if (string.IsNullOrWhiteSpace(completionEvent.NotificationTitle))
                    {
                        completionEvent.NotificationTitle =
                            BuildZCodeSessionTitle(completionEvent.Status, resolvedTitle, turnQuery);
                    }
                    // 真实会话标题是第一手任务名，优先于解析器按 taskId 拼出的占位
                    // （如「任务 turn_...」）；当解析器已给出更具体的项目名时不覆盖。
                    var existingProject = completionEvent.ProjectName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(existingProject) ||
                        existingProject.TrimStart().StartsWith("\u4efb\u52a1 ", StringComparison.Ordinal) ||
                        existingProject.IndexOf(resolvedTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        completionEvent.ProjectName = resolvedTitle;
                    }
                    found = true;
                }

                // 任务日志：真实会话日志（该 turn 的最后一条助手正文）优先；等待事件不采用
                // （助手最后回复与"等待你的操作"问题无关）。
                var isWaiting = string.Equals(
                    completionEvent.Status, "waiting", StringComparison.OrdinalIgnoreCase);
                if (!isWaiting &&
                    ZCodeSessionReader.TryResolveTaskLog(
                        completionEvent.SessionId, completionEvent.TaskId ?? string.Empty, out var taskLog) &&
                    !string.IsNullOrWhiteSpace(taskLog))
                {
                    completionEvent.Message = taskLog;
                    found = true;
                }
                else if (found)
                {
                    completionEvent.Message = BuildZCodeSessionMessage(
                        completionEvent.Status, resolvedTitle, completionEvent.Message);
                }
            }
            catch (Exception error)
            {
                _log("ZCode 会话库读取失败：" + error.Message);
            }

            // 用量与耗时：turn_usage 即使标题/日志未命中也尝试。
            ApplyZCodeTurnUsage(completionEvent);
            return found;
        }

        // ZCode 用量/耗时注入：turn_usage 表按 turnId 精确匹配真实
        // computed_total_tokens 与 duration_ms；未命中则回退最近一条 completed turn。
        private void ApplyZCodeTurnUsage(AgentCompletionEventData completionEvent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(completionEvent.SessionId))
                {
                    return;
                }

                // CLI 解析的 TaskId 即 turnId（见 TryParseZCodeCliLine）；
                // Desktop 只有 sessionId 时传空，走最近 completed 回退。
                var turnId = completionEvent.TaskId ?? string.Empty;
                if (!ZCodeSessionReader.TryResolveTurnUsage(
                        completionEvent.SessionId, turnId, out var durationMs, out var tokenCount))
                {
                    return;
                }

                if (durationMs > 0L && completionEvent.DurationMs <= 0L)
                {
                    completionEvent.DurationMs = durationMs;
                }
                if (tokenCount > 0L)
                {
                    completionEvent.TokenCount = tokenCount;
                }
            }
            catch (Exception error)
            {
                _log("ZCode 用量读取失败：" + error.Message);
            }
        }

        // 标题正文限额：通知标题不宜过长，截断后加省略号，过长提问由此浓缩。
        private const int ZCodeTitleQueryLimit = 28;

        private static string BuildZCodeSessionTitle(
            string eventStatus, string title, string turnQuery)
        {
            var statusLabel = ZCodeStatusLabel(eventStatus);
            var leading = FirstNonEmpty(turnQuery, title);
            if (!string.IsNullOrWhiteSpace(turnQuery))
            {
                // 随当轮对话：标题主体为当轮提问（浓缩），会话名放括号作归属。
                leading = Truncate(turnQuery, ZCodeTitleQueryLimit);
                return leading + " " + statusLabel + "（" + title + "）";
            }

            return "「" + title + "」 " + statusLabel;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }
            var value = TraeUserPromptResolver.Summarize(text);
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private static string ZCodeStatusLabel(string eventStatus)
        {
            return string.Equals(eventStatus, "completed", StringComparison.OrdinalIgnoreCase)
                ? "任务已完成"
                : string.Equals(eventStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "任务失败"
                    : "等待你的操作";
        }

        private static string BuildZCodeSessionMessage(
            string eventStatus, string title, string originalMessage)
        {
            var colon = (originalMessage ?? string.Empty).LastIndexOf('：');
            var detail = colon >= 0 ? (originalMessage ?? string.Empty).Substring(colon + 1).Trim() : string.Empty;

            return "「" + title + "」 " + ZCodeStatusLabel(eventStatus) +
                   (string.IsNullOrWhiteSpace(detail) ? string.Empty : "：" + detail);
        }

        private void DispatchBaiduBatch(BaiduBatchCompletion completion)
        {
            SendNotification(
                completion.Message,
                "baidunetdisk",
                completion.Status,
                completion.BatchId,
                string.Empty,
                (long)Math.Max(0D, completion.Duration.TotalMilliseconds),
                completion.ProjectName);
        }

        private void DispatchQuarkBatch(QuarkBatchCompletion completion)
        {
            SendNotification(
                completion.Message,
                completion.Source,
                completion.Status,
                completion.BatchId,
                string.Empty,
                (long)Math.Max(0D, completion.Duration.TotalMilliseconds),
                completion.TaskProjectName);
        }

        private void DispatchAdriveTask(AdriveTaskEvent task)
        {
            SendNotification(task.Message, task.Source, task.Status, task.TaskId, string.Empty, task.DurationMs, task.ProjectName);
        }

        private void DispatchOpenCodeTask(OpenCodeCompletion completion)
        {
            var projectName = ProjectNameResolver.ResolveFolderFirst(
                string.Empty,
                completion.Cwd,
                completion.TaskId,
                completion.Message);

            SendNotification(
                completion.Message,
                "opencode",
                completion.Status,
                completion.TaskId,
                completion.Cwd,
                completion.DurationMs,
                projectName,
                completion.TokenCount,
                completion.SessionId,
                processAlreadyValidated: true,
                taskLog: completion.Message);
        }

        private void DispatchFreebuffTask(FreebuffCompletion completion)
        {
            var projectName = ProjectNameResolver.ResolveFolderFirst(
                string.Empty,
                completion.Cwd,
                completion.TaskId,
                completion.Message);

            // 优雅期：elevation_request 产生的 waiting 先缓一缓，同一线程在窗口内
            // 出现终态（本地已确认/拒绝/完成）就取消推送，避免手机收到过期提醒。
            if (string.Equals(completion.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleFreebuffWaiting(completion, projectName);
                return;
            }

            // 终态事件把同一线程还在优雅期内的 waiting 一并取消。
            CancelFreebuffWaiting(completion.ThreadId);

            SendNotification(
                completion.Message,
                "freebuff",
                completion.Status,
                completion.TaskId,
                completion.Cwd,
                completion.DurationMs,
                projectName,
                completion.TokenCount,
                sessionId: completion.ThreadId,
                processAlreadyValidated: true,
                taskLog: completion.TaskLog);
        }

        private void ScheduleFreebuffWaiting(FreebuffCompletion completion, string projectName)
        {
            var threadId = completion.ThreadId;
            if (string.IsNullOrWhiteSpace(threadId))
            {
                // 无法关联线程的 waiting 直接发送，不做优雅期。
                SendNotification(
                    completion.Message,
                    "freebuff",
                    completion.Status,
                    completion.TaskId,
                    completion.Cwd,
                    completion.DurationMs,
                    projectName,
                    completion.TokenCount,
                    sessionId: threadId,
                    processAlreadyValidated: true,
                    taskLog: completion.TaskLog);
                return;
            }

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                if (_disposed)
                {
                    cts.Dispose();
                    return;
                }
                if (_freebuffElevationWaits.TryGetValue(threadId, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }
                _freebuffElevationWaits[threadId] = cts;
            }

            _ = Task.Delay(FreebuffElevationGracePeriod, cts.Token)
                .ContinueWith(task =>
                {
                    bool disposed;
                    lock (_gate)
                    {
                        _freebuffElevationWaits.Remove(threadId);
                        disposed = _disposed;
                    }
                    cts.Dispose();
                    if (task.IsCanceled || disposed)
                    {
                        _log("Freebuff waiting 已被本地终态取消，不推送手机：" + threadId);
                        return;
                    }
                    SendNotification(
                        completion.Message,
                        "freebuff",
                        completion.Status,
                        completion.TaskId,
                        completion.Cwd,
                        completion.DurationMs,
                        projectName,
                        completion.TokenCount,
                        sessionId: threadId,
                        processAlreadyValidated: true,
                        taskLog: completion.TaskLog);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void CancelFreebuffWaiting(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                if (_freebuffElevationWaits.TryGetValue(threadId, out var pending))
                {
                    cts = pending;
                    _freebuffElevationWaits.Remove(threadId);
                }
            }
            cts?.Cancel();
            cts?.Dispose();
        }

        private void DispatchDeepSeekHarnessTask(DeepSeekHarnessCompletion completion)
        {
            var projectName = ProjectNameResolver.ResolveFolderFirst(
                completion.ProjectName,
                completion.Cwd,
                completion.TaskId,
                completion.Message);
            var message = completion.Message;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var durationSuffix = completion.DurationMs > 0
                    ? "（耗时 " + FormatDuration(completion.DurationMs) + "）"
                    : string.Empty;
                message = projectName + durationSuffix + " \u2014 " + completion.Message;
            }
            SendNotification(
                message,
                "deepseekharness",
                completion.Status,
                completion.TaskId,
                completion.Cwd,
                completion.DurationMs,
                projectName,
                completion.TokenCount,
                completion.SessionId);
        }

        private static string FormatDuration(long durationMs)
        {
            if (durationMs <= 0) return "<1秒";
            if (durationMs < 1000) return "<1秒";
            var totalSeconds = durationMs / 1000;
            if (totalSeconds < 60) return totalSeconds + "秒";
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return seconds > 0 ? minutes + "分" + seconds + "秒" : minutes + "分钟";
        }

        private void DispatchParsedEvent(AgentCompletionEventData completionEvent)
        {
            if (!TryRememberEvent(completionEvent.EventKey))
            {
                return;
            }

            completionEvent.ProjectName = string.Equals(
                    completionEvent.Source,
                    "codex",
                    StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(
                    CodexProjectResolver.Resolve(completionEvent.Cwd),
                    completionEvent.ProjectName,
                    ProjectNameResolver.ResolveFolderFirst(
                        string.Empty,
                        completionEvent.Cwd,
                        completionEvent.TaskId,
                        completionEvent.Message))
                : string.Equals(completionEvent.Source, "doubao", StringComparison.OrdinalIgnoreCase)
                    // 豆包的窗口会话名是显式任务名；不要被 rlog 中偶然的
                    // cwd（通常只是应用启动目录）覆盖。
                    ? ProjectNameResolver.Resolve(
                        completionEvent.ProjectName,
                        completionEvent.Cwd,
                        completionEvent.TaskId,
                        completionEvent.Message)
                    : ProjectNameResolver.ResolveFolderFirst(
                        completionEvent.ProjectName,
                        completionEvent.Cwd,
                        completionEvent.TaskId,
                        completionEvent.Message);

            SendNotification(
                completionEvent.Message,
                completionEvent.Source,
                completionEvent.Status,
                completionEvent.TaskId,
                completionEvent.Cwd,
                completionEvent.DurationMs,
                completionEvent.ProjectName,
                completionEvent.TokenCount,
                completionEvent.SessionId,
                credits: completionEvent.Credits,
                notificationTitle: completionEvent.NotificationTitle,
                requestId: completionEvent.RequestId,
                taskLog: completionEvent.TaskLog);
        }

        private void SendNotification(
            string message,
            string source,
            string status,
            string taskId,
            string cwd,
            long durationMs,
            string projectName,
            long tokenCount = 0L,
            string sessionId = "",
            bool processAlreadyValidated = false,
            string credits = "",
            string notificationTitle = "",
            string requestId = "",
            string taskLog = "")
        {
            try
            {
                // Database/SSE adapters validate their own source lifetime before
                // dispatching. Re-probing here can race an Electron shell restart
                // (or fail when the source runs elevated), which used to silently
                // discard Freebuff elevation_request waiting notifications.
                if (!processAlreadyValidated && !_isSourceProcessRunning(source))
                {
                    _log("来源进程未运行，忽略任务状态通知：[" + source + "] status=" + status);
                    return;
                }
                // 不在监视线程上同步等待广播：WS 发送存在背压（串行锁 + 逐客户端
                // await）时，同步等待会阻塞整个事件管线，导致后续通知排队延迟。
                // 改为后台完成（方法内部已 try/catch，异常不会逸出）。
                _ = BroadcastInBackground(
                    message,
                    source,
                    status,
                    taskId,
                    cwd,
                    durationMs,
                    projectName,
                    tokenCount,
                    sessionId,
                    credits,
                    notificationTitle,
                    requestId,
                    taskLog);
            }
            catch (Exception error)
            {
                _log("\u53d1\u9001\u4efb\u52a1\u72b6\u6001\u901a\u77e5\u5931\u8d25\uff1a" + error.Message);
            }
        }

        private async Task BroadcastInBackground(
            string message,
            string source,
            string status,
            string taskId,
            string cwd,
            long durationMs,
            string projectName,
            long tokenCount,
            string sessionId,
            string credits,
            string notificationTitle,
            string requestId = "",
            string taskLog = "")
        {
            try
            {
                await _server.BroadcastFromMonitorAsync(
                    message,
                    source,
                    status,
                    taskId,
                    cwd,
                    durationMs,
                    projectName,
                    notificationTitle: notificationTitle,
                    actionUrl: string.Empty,
                    tokenCount: tokenCount,
                    sessionId: sessionId,
                    credits: credits,
                    requestId: requestId,
                    taskLog: taskLog);
            }
            catch (Exception error)
            {
                _log("\u53d1\u9001\u4efb\u52a1\u72b6\u6001\u901a\u77e5\u5931\u8d25\uff1a" + error.Message);
            }
        }

        private static void AccumulateClaudeTokenCountLocked(FileState state, JsonElement root)
        {
            var step = AgentCompletionEventParser.ReadUsageTokenCount(root);
            if (step > 0L)
            {
                state.LastClaudeTokenCount += step;
            }
        }

        private bool IsSourceProcessRunning(string source)
        {
            try
            {
                return _isSourceProcessRunning(source);
            }
            catch
            {
                // A failed probe is not evidence that the app is active.
                return false;
            }
        }

        /// <summary>
        /// File-level gate for whether a watched file may be consumed. `.claude/projects`
        /// transcripts are shared by official Claude Code and the Claude Code Haha variant:
        /// the path-derived source is always "claude", but the file can be appended by either
        /// process, so it is readable while either one is alive. Per-event broadcast routing
        /// (SendNotification) still uses the exact source and keeps Haha events on claude-haha.
        /// </summary>
        private bool IsFileProcessRunning(string path, string source)
        {
            if (AgentLogPaths.IsClaudeTranscript(path) && source == "claude")
            {
                try
                {
                    return _isSourceProcessRunning("claude") || _isSourceProcessRunning("claude-haha");
                }
                catch
                {
                    return false;
                }
            }
            return IsSourceProcessRunning(source);
        }

        private void BaselineFile(string path)
        {
            lock (_gate)
            {
                if (_disposed) return;
                CancelPendingCodexInputsForPathLocked(path);
                try
                {
                    var info = new FileInfo(path);
                    var exists = info.Exists;
                    var length = exists ? info.Length : 0L;
                    _files[path] = new FileState
                    {
                        Position = length,
                        LastLength = length,
                        LastWriteUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue
                    };
                }
                catch
                {
                    _files[path] = new FileState();
                }
            }
        }

        private static string GetSourceForPath(string path)
        {
            if (AgentLogPaths.IsClaudeTranscript(path)) return "claude";
            if (AgentLogPaths.IsChatGPTSession(path)) return "codex";
            if (AgentLogPaths.IsWorkBuddyAiLog(path)) return "workbuddy-ai";
            if (AgentLogPaths.IsWorkBuddyLog(path)) return "workbuddy";
            if (AgentLogPaths.IsTraeLog(path)) return GetTraeSource(path);
            if (AgentLogPaths.IsZCodeCliLog(path) || AgentLogPaths.IsZCodeDesktopLog(path)) return "zcode";
            if (AgentLogPaths.IsDoubaoLog(path)) return "doubao";
            return string.Empty;
        }

        private bool TryRememberEvent(string key)
        {
            lock (_gate)
            {
                return RememberEventLocked(key);
            }
        }

        private bool RememberEventLocked(string key)
        {
            if (!_eventKeys.Add(key))
            {
                return false;
            }

            if (_eventKeys.Count > 500)
            {
                _eventKeys.Clear();
                _eventKeys.Add(key);
            }

            return true;
        }

        private sealed class PendingCodexInput
        {
            public PendingCodexInput(
                string key,
                AgentCompletionEventData @event,
                DateTimeOffset createdAtUtc)
            {
                Key = key;
                Event = @event;
                CreatedAtUtc = createdAtUtc;
            }

            public string Key { get; }
            public AgentCompletionEventData Event { get; }
            public DateTimeOffset CreatedAtUtc { get; }
        }

        private FileState GetClaudeStateLocked(string path)
        {
            if (!_files.TryGetValue(path, out var state))
            {
                state = new FileState();
                _files[path] = state;
            }

            return state;
        }

        /// <summary>
        /// 返回指定 Claude 会话已记忆的工作目录。转录解析器会从用户输入和工具调用中的
        /// cd/Set-Location 推导真实项目目录；Claude Code 的 Hook 事件只带启动目录，
        /// 因此 /send 需要按 sessionId 回查这里。找不到时返回空字符串。
        /// </summary>
        internal string GetClaudeWorkingDirectory(string sessionId)
        {
            var normalized = (sessionId ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            lock (_gate)
            {
                // 优先按解析器记录的会话 ID 匹配。
                foreach (var pair in _files)
                {
                    var state = pair.Value;
                    if (state == null || string.IsNullOrWhiteSpace(state.LastClaudeCwd))
                    {
                        continue;
                    }

                    if (string.Equals(state.LastClaudeSessionId, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return state.LastClaudeCwd;
                    }
                }

                // Claude 转录文件名就是 <sessionId>.jsonl；会话 ID 还没写入状态时按路径兜底。
                foreach (var pair in _files)
                {
                    var state = pair.Value;
                    if (state == null || string.IsNullOrWhiteSpace(state.LastClaudeCwd))
                    {
                        continue;
                    }

                    if (AgentLogPaths.IsClaudeTranscript(pair.Key) &&
                        pair.Key.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return state.LastClaudeCwd;
                    }
                }
            }

            return string.Empty;
        }

        private static void RememberClaudePermissionModeLocked(FileState state, JsonElement root)
        {
            if (AgentCompletionEventParser.TryReadPermissionMode(root, out var mode))
            {
                state.LastClaudePermissionMode = mode;
            }
        }

        /// <summary>
        /// 权限 waiting 宽限期：默认 90 秒；Bash 等工具若声明了 timeout，用该超时
        /// （毫秒）覆盖，避免长编译在转录静止后被误报成审批卡。
        /// </summary>
        private static TimeSpan ResolvePermissionWaitGracePeriod(JsonElement root)
        {
            var timeoutMs = AgentCompletionEventParser.ReadToolUseTimeoutMs(root);
            if (timeoutMs <= 0L)
            {
                return PermissionWaitGracePeriod;
            }

            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            return timeout > PermissionWaitGracePeriod ? timeout : PermissionWaitGracePeriod;
        }

        private static void RememberClaudeDirectoryLocked(FileState state, string candidate, bool force)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var userProfileRoot = AgentLogPaths.GetUserProfileRoot();
            state.LastClaudeCwd = force
                ? ClaudeProjectResolver.PreferWorkingDirectory(string.Empty, candidate, userProfileRoot)
                : ClaudeProjectResolver.PreferWorkingDirectory(state.LastClaudeCwd, candidate, userProfileRoot);
            if (!string.IsNullOrWhiteSpace(state.LastClaudeCwd))
            {
                state.LastClaudeProjectName = ProjectNameResolver.ResolveFolderFirst(
                    string.Empty,
                    state.LastClaudeCwd,
                    state.LastClaudeSessionId,
                    string.Empty);
            }
        }

        private static string ExtractClaudeMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message))
            {
                return string.Empty;
            }

            if (message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }

            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content))
            {
                return string.Empty;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(block, "type"), "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = ReadString(block, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.Append(text);
            }

            return builder.ToString().Trim();
        }

        private static string BuildClaudeEventId(string sessionId, string message, string timestamp, string path)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                return sessionId + ":" + HashText(sessionId + "|" + message);
            }

            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                return sessionId + ":" + timestamp;
            }

            return sessionId + ":" + HashText(path);
        }

        private static string HashText(string value)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return (BitConverter.ToString(hash).Replace("-", string.Empty)).ToLowerInvariant();
        }

        private static string SummarizeClaudeMessage(string message)
        {
            var normalized = message.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= 360 ? normalized : normalized.Substring(0, 360) + " [truncated]";
        }


        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int ReadInt(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var element))
            {
                return 0;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            {
                return number;
            }

            return element.ValueKind == JsonValueKind.True ? 1 : 0;
        }

        private static long ReadLong(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var element))
            {
                return 0L;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            {
                return number > 0L ? number : 0L;
            }

            return element.ValueKind == JsonValueKind.String &&
                   long.TryParse(element.GetString(), out number) &&
                   number > 0L
                ? number
                : 0L;
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

        /// <summary>
        /// 一个挂起等待积分明细（cn_session_usage_tail show 事件）的 TraeCode 完成事件。
        /// 积分按 agentMessageId 匹配后补充并广播；超过 <see cref="TraeUsageWaitWindow"/>
        /// 仍未到达则由 <see cref="FlushExpiredTraeCompletions"/> 原样发送。
        /// </summary>
        private sealed class PendingTraeCompletion
        {
            public AgentCompletionEventData Event { get; set; } = new AgentCompletionEventData();
            public string AgentMessageId { get; set; } = string.Empty;
            public DateTime DeadlineUtc { get; set; }
        }

        /// <summary>
        /// 一个已登记待确认的权限敏感工具调用。发出时不广播，超过宽限期仍未回传
        /// tool_result 才由 <see cref="TryFirePendingPermissionWaits"/> 广播 waiting。
        /// </summary>
        private sealed class PendingPermissionWait
        {
            public PendingPermissionWait(
                string toolUseId,
                string header,
                string path,
                string cwd,
                string projectName,
                DateTimeOffset createdAtUtc,
                TimeSpan gracePeriod,
                DateTimeOffset? firstRegisteredAtUtc = null)
            {
                // 顺延时保留最初登记时间，用于计算顺延总时长上限。
                FirstRegisteredAtUtc = firstRegisteredAtUtc ?? createdAtUtc;
                ToolUseId = toolUseId;
                Header = header;
                Path = path;
                Cwd = cwd;
                ProjectName = projectName;
                CreatedAtUtc = createdAtUtc;
                GracePeriod = gracePeriod <= TimeSpan.Zero ? PermissionWaitGracePeriod : gracePeriod;
            }

            public string ToolUseId { get; }
            public string Header { get; }
            public string Path { get; }
            public string Cwd { get; }
            public string ProjectName { get; }
            public DateTimeOffset CreatedAtUtc { get; }
            public TimeSpan GracePeriod { get; }
            /// <summary>最初登记时间；顺延不重置，用于判定是否超过顺延总时长上限。</summary>
            public DateTimeOffset FirstRegisteredAtUtc { get; }
        }

        /// <summary>
        /// 一个已登记待确认的 ZCode <c>AskUserQuestion</c>。发出时不广播，超过宽限期
        /// 仍未回传 tool.call.completed 且该日志无新写入，才由 <see cref="TryFireZCodeWaits"/>
        /// 广播 waiting（避免 yolo 自动代答造成的误解）。
        /// </summary>
        private sealed class PendingZCodeWait
        {
            public PendingZCodeWait(
                string toolCallId,
                string taskId,
                string sessionId,
                string path,
                DateTimeOffset createdAtUtc)
            {
                ToolCallId = toolCallId;
                TaskId = taskId;
                SessionId = sessionId;
                Path = path;
                CreatedAtUtc = createdAtUtc;
            }

            public string ToolCallId { get; }
            public string TaskId { get; }
            public string SessionId { get; }
            public string Path { get; }
            public DateTimeOffset CreatedAtUtc { get; }
        }

        private sealed class FileState
        {
            public long Position { get; set; }
            public long LastLength { get; set; } = -1L;
            public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;
            public string Pending { get; set; } = string.Empty;
            public string LastClaudeSessionId { get; set; } = string.Empty;
            public string LastClaudeCwd { get; set; } = string.Empty;
            public string LastClaudeProjectName { get; set; } = string.Empty;
            public string LastClaudeMessage { get; set; } = string.Empty;
            public bool LastClaudeFailed { get; set; }
            /// <summary>
            /// 新版 WorkBuddy（8-23 起）等待信号按 sessionId:awaiting:kind 每 30 秒轮询重复打印；
            /// 该集合记录已推送的等待键，避免同轮重复推送；用户恢复处理后由
            /// ProcessWorkBuddyLine 清除，允许下一轮等待再次推送。
            /// </summary>
            public HashSet<string> WorkBuddyAwaitingKeys { get; } = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>
            /// 最近一次真人用户提示的时间戳；Haha 交互 end_turn 用它计算 durationMs。
            /// </summary>
            public DateTimeOffset? LastClaudeHumanPromptAt { get; set; }
            /// <summary>
            /// 最近一条 user 角色行是否为 Haha 注入的 <c>&lt;task-notification&gt;</c> 后台任务报告。
            /// Haha 在后台 Bash / 子代理结束时把报告注入同一转录，Claude 会逐条答复，
            /// 每条答复都带 <c>stop_reason=end_turn</c>；若把它们当成主任务终态，
            /// 同一个任务就会反复推送到手机（实测一次真人提问推了 5 条）。
            /// 用「上一条 user 行是不是注入」判定与顺序无关：assistant 要再产出一个
            /// end_turn 必须先有新的输入行，因此该标志总能正确归因。
            /// </summary>
            public bool LastUserLineWasHahaTaskInjection { get; set; }
            /// <summary>
            /// 本轮 Claude/Haha assistant 行累计 token；0 表示尚未读到可靠 usage。
            /// </summary>
            public long LastClaudeTokenCount { get; set; }
            /// <summary>
            /// 本会话中已回传 tool_result 的 tool_use id（跨轮次累积），用于压制权限 waiting：
            /// 已回传说明工具已允许并执行完（或已拒绝），不能再当作“卡在审批”。
            /// </summary>
            public HashSet<string> AnsweredToolUseIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>
            /// 最近一次 session-meta / user 行上的 permissionMode。bypassPermissions 等
            /// 免确认模式不会弹出审批卡，不能把长命令推断为 waiting。
            /// </summary>
            public string LastClaudePermissionMode { get; set; } = string.Empty;
            public string LastCodexProjectName { get; set; } = string.Empty;
            public string LastCodexCwd { get; set; } = string.Empty;
            public string LastCodexSessionId { get; set; } = string.Empty;
            /// <summary>本轮 Codex token_count 事件累计（info.last_token_usage 增量求和）。</summary>
            public CodexTurnTokenAccumulator CodexTokens { get; } = new CodexTurnTokenAccumulator();
            public CodexSessionKind CodexSessionKind { get; set; }
            public bool WorkBuddyFailureMarker { get; set; }
            public DateTimeOffset? WorkBuddyFailureMarkerTime { get; set; }
            public string LastWorkBuddyFailedTaskId { get; set; } = string.Empty;
            public string LastWorkBuddySessionId { get; set; } = string.Empty;
            /// <summary>
            /// 上下文压缩会先输出一个 stopReason=completed 的中间钩子，随后继续
            /// compact/resume。该标志覆盖这段生命周期，避免把中间钩子当任务完成。
            /// </summary>
            public bool WorkBuddyCompactionInProgress { get; set; }
            /// <summary>最近一次模型请求的 rootRequestId，用于按请求匹配单次积分。</summary>
            public string LastWorkBuddyRequestId { get; set; } = string.Empty;
        }
    }
}
