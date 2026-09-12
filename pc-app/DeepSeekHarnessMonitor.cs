using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ZstdSharp;

namespace TapRelay
{
    /// <summary>
    /// Event-driven DeepSeek Harness monitor. It watches the per-workspace session
    /// logs under <c>%APPDATA%\deepseek-harness-desktop\harness-home\sessions</c>
    /// (each session is written as <c>session.jsonl.zstd</c>, a Zstandard-compressed
    /// JSONL stream) and emits a notification when a **main** session turn reaches a
    /// terminal state.
    ///
    /// Completion signal from the harness's own session log (<c>turn/end</c>):
    ///   - reason.kind == "completed"              -> completed
    ///   - reason.kind == "error"                  -> failed (body from error.message)
    ///   - reason.kind == "blocked"                -> waiting (turn paused awaiting input)
    ///   - reason.kind == "approval_required"      -> waiting (tool approval gate)
    ///   - reason.kind == "tool_approval"          -> waiting (tool approval gate)
    ///   - reason.kind == "permission_required"    -> waiting (permission gate)
    ///   - approval/asked event                    -> waiting (tool approval requested
    ///                                               via dsh-user-approval; the turn is
    ///                                               blocked until approval/decided)
    ///   - reason.kind == "aborted" (reason.kind == "user"|"legacy") -> cancelled, silent
    ///   - reason.kind in aborted(parent/disposed/hook), interrupted, max-tokens
    ///                                             -> ignored (not a user-visible outcome)
    ///
    /// Only sessions whose <c>session</c> event has <c>delegationDepth == 0</c> are
    /// reported: subagents are separate sessions with depth &gt; 0 and must never be
    /// reported as the main task finishing. Turn duration is derived from the owning
    /// <c>turn/start</c> timestamp to the <c>turn/end</c> timestamp.
    /// </summary>
    internal sealed class DeepSeekHarnessMonitor : IDisposable
    {
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);

        private readonly Action<DeepSeekHarnessCompletion> _completed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SessionState> _states = new Dictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);
        private NonReentrantMonitorTimer? _pollTimer;
        private bool _processRunning;
        private bool _disposed;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;

        internal DeepSeekHarnessMonitor(
            Action<DeepSeekHarnessCompletion> completed,
            Action<string> log,
            Func<bool>? isProcessRunning = null)
        {
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isProcessRunning = isProcessRunning ?? (() => AdaptedProcessGate.IsRunningForAppKey("deepseekharness"));
        }

        internal void Start()
        {
            var processRunning = IsProcessRunningNow();
            lock (_gate)
            {
                if (_disposed) return;
                _processRunning = processRunning;
                _pollTimer = new NonReentrantMonitorTimer(
                    PollMonitor,
                    ProcessRecoveryInterval,
                    ProcessRecoveryInterval,
                    error => _log("DeepSeek Harness 会话日志恢复检查失败：" + error.Message));
            }

            if (!processRunning)
            {
                _log("DeepSeek Harness process is not running; task monitoring is paused until it starts");
                return;
            }

            var roots = DeepSeekHarnessDataLocator.FindRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var root in roots)
            {
                var active = AddRoot(root);
                if (active > 0)
                {
                    _log("已接入 DeepSeek Harness 会话目录：" + root + "（" + active + " 个会话日志）");
                }
            }
            _lastDiscoveryUtc = DateTime.UtcNow;

            var anyActive = _states.Count > 0;
            _log(anyActive
                ? "已启用 DeepSeek Harness 任务状态监听（Zstandard 会话日志事件驱动）"
                : "未找到 DeepSeek Harness 会话日志；启动 DeepSeek Harness 并运行一次任务后重启 TapRelay 可自动接入");
        }

        internal void Refresh()
        {
            var roots = DeepSeekHarnessDataLocator.FindRoots()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var active = 0;
            foreach (var root in roots)
            {
                active += AddRoot(root);
            }
            _log("DeepSeek Harness 会话监控刷新：roots=" + roots.Length + " files=" + active);
        }

        private void PollMonitor()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            if (DateTime.UtcNow - _lastDiscoveryUtc >= DiscoveryInterval)
            {
                _lastDiscoveryUtc = DateTime.UtcNow;
                Refresh();
                return;
            }

            PollSessionChanges();
        }

        private bool UpdateProcessState()
        {
            var processRunning = IsProcessRunningNow();
            bool changed;
            lock (_gate)
            {
                if (_disposed) return false;
                changed = _processRunning != processRunning;
                if (!changed) return false;
                _processRunning = processRunning;
                if (!processRunning) StopMonitoringLocked();
            }

            _log(processRunning
                ? "DeepSeek Harness process started; session monitoring resumed"
                : "DeepSeek Harness process stopped; session monitoring paused");
            if (processRunning) Refresh();
            return true;
        }

        private bool IsProcessRunningNow()
        {
            try { return _isProcessRunning(); }
            catch { return false; }
        }

        private int AddRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            lock (_gate)
            {
                if (_disposed || !_processRunning) return 0;
                if (_states.Values.Any(s => string.Equals(s.Root, fullRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    // Root already tracked; make sure new session files are watched.
                    return EnsureWatcherLocked(fullRoot);
                }

                var state = new SessionState(fullRoot);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(fullRoot, "session.jsonl*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFileName(file), "session.jsonl.zstd", StringComparison.OrdinalIgnoreCase))
                        {
                            AddSessionLocked(state, file);
                        }
                    }

                    state.Watcher = new FileSystemWatcher(fullRoot, "session.jsonl*")
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = false
                    };
                    state.Watcher.Changed += (_, __) => Signal(state);
                    state.Watcher.Created += (_, __) => Signal(state);
                    state.Watcher.Renamed += (_, __) => Signal(state);
                    state.Watcher.Error += (_, __) => Signal(state);
                    state.Watcher.EnableRaisingEvents = true;
                    _states[fullRoot] = state;
                    return state.Files.Count;
                }
                catch (Exception error)
                {
                    _log("DeepSeek Harness 会话目录接入失败：" + fullRoot + "；" + error.Message);
                    return 0;
                }
            }
        }

        /// <summary>Ensures the watcher for an already-tracked root exists (no-op guard).</summary>
        private int EnsureWatcherLocked(string fullRoot)
        {
            return _states.Values.FirstOrDefault(s => string.Equals(s.Root, fullRoot, StringComparison.OrdinalIgnoreCase))?.Files.Count ?? 0;
        }

        private void AddSessionLocked(SessionState state, string file)
        {
            var fullPath = Path.GetFullPath(file);
            if (state.Files.ContainsKey(fullPath)) return;
            try
            {
                // Prime: read the current terminal state of an existing file
                // (found at startup) without notifying.  Historical turn/end events
                // are recorded in Emitted but never broadcast.
                state.Files[fullPath] = DeepSeekHarnessReader.Prime(fullPath, out _);
            }
            catch (Exception error)
            {
                _log("DeepSeek Harness 会话日志初始化失败：" + fullPath + "；" + error.Message);
                state.Files[fullPath] = new DeepSeekHarnessFileState();
            }
        }

        /// <summary>
        /// Called when a new session file appears while the monitor is already active
        /// (created after <c>Start</c>).  Terminal turns are reported immediately.
        /// </summary>
        private void DiscoverFileLocked(SessionState state, string file)
        {
            var fullPath = Path.GetFullPath(file);
            if (state.Files.ContainsKey(fullPath)) return;
            try
            {
                var fileState = DeepSeekHarnessReader.Discover(fullPath, out var completions);
                state.Files[fullPath] = fileState;
                foreach (var completion in completions)
                {
                    if (completion.Status == "cancelled") continue;
                    if (!TryDeduplicate(fullPath, fileState, completion)) continue;
                    if (IsProcessActiveForNotification()) _completed(completion);
                }
            }
            catch (Exception error)
            {
                _log("DeepSeek Harness 会话日志初始化失败：" + fullPath + "；" + error.Message);
                state.Files[fullPath] = new DeepSeekHarnessFileState();
            }
        }

        private void Signal(SessionState state)
        {
            lock (_gate)
            {
                if (_disposed || !_processRunning) return;
                state.LastSignalUtc = DateTime.UtcNow;
                state.Timer?.Dispose();
                state.Timer = new Timer(_ => ProcessStableChange(state), null, SettleDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void PollSessionChanges()
        {
            if (!IsProcessActiveForNotification()) return;
            SessionState[] states;
            lock (_gate)
            {
                if (_disposed) return;
                states = _states.Values.ToArray();
            }

            foreach (var state in states)
            {
                bool changed = false;
                try
                {
                    lock (_gate)
                    {
                        if (_disposed || !_processRunning) continue;
                        // New session files created at runtime under the watched root
                        // should be discovered with immediate notification so that
                        // blocked/waiting turns are surfaced without delay.
                        foreach (var file in Directory.EnumerateFiles(state.Root, "session.jsonl*", SearchOption.AllDirectories))
                        {
                            var fullPath = Path.GetFullPath(file);
                            if (string.Equals(Path.GetFileName(fullPath), "session.jsonl.zstd", StringComparison.OrdinalIgnoreCase) &&
                                !state.Files.ContainsKey(fullPath))
                            {
                                DiscoverFileLocked(state, fullPath);
                            }
                        }

                        // Recovery fallback: if a file changed but the watcher missed it,
                        // scan it immediately through ProcessStableChange so high-frequency
                        // writes (e.g. assistant/chunk streaming) never starve the debounce
                        // timer and permanently delay terminal turn notifications.
                        foreach (var fileState in state.Files)
                        {
                            var length = DeepSeekHarnessReader.FileLength(fileState.Key);
                            var writeUtc = DeepSeekHarnessReader.FileWriteUtc(fileState.Key);
                            if (length >= 0 && (length != fileState.Value.LastCompressedLength || writeUtc != fileState.Value.LastCompressedWriteUtc))
                            {
                                changed = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Directory may be transient while a workspace is being created.
                }

                if (changed)
                {
                    _log("DeepSeek Harness 会话日志发生变化：root=" + state.Root);
                    // Bypass the debounce timer for poll-initiated scans: the file
                    // has likely been stable for up to 15 seconds.  Forcing an
                    // immediate read guarantees that terminal turn/end events are not
                    // starved by streaming chunk writes that continuously reset the
                    // 3-second debounce timer in the FileSystemWatcher path.
                    ProcessStableChangeNoDebounce(state);
                }
            }
        }

        /// <summary>Like <c>ProcessStableChange</c> but skips the debounce gate (used from the poll path).</summary>
        private void ProcessStableChangeNoDebounce(SessionState state)
        {
            if (state == null || !IsProcessActiveForNotification()) return;
            try
            {
                var completions = new List<DeepSeekHarnessCompletion>();
                lock (_gate)
                {
                    if (_disposed || !_processRunning || !_states.ContainsKey(state.Root)) return;
                    foreach (var fileState in state.Files.ToArray())
                    {
                        var fileStateValue = fileState.Value;
                        if (!DeepSeekHarnessReader.TryScan(fileState.Key, fileStateValue, out var thisFileCompletions))
                        {
                            continue;
                        }
                        foreach (var item in thisFileCompletions)
                        {
                            if (item.Status == "cancelled") continue;
                            if (!TryDeduplicate(fileState.Key, fileStateValue, item)) continue;
                            completions.Add(item);
                        }
                        if (thisFileCompletions.Count > 0)
                        {
                            _log("DeepSeek Harness 会话扫描命中终态：file=" + fileState.Key + " count=" + thisFileCompletions.Count);
                        }
                    }
                }
                if (!IsProcessActiveForNotification()) return;
                foreach (var completion in completions)
                {
                    if (IsProcessActiveForNotification()) _completed(completion);
                }
            }
            catch (Exception error)
            {
                _log("DeepSeek Harness 会话事件解析失败：" + error.Message);
            }
        }

        private void ProcessStableChange(SessionState? state)
        {
            if (state == null || !IsProcessActiveForNotification()) return;

            try
            {
                var completions = new List<DeepSeekHarnessCompletion>();
                lock (_gate)
                {
                    if (_disposed || !_processRunning || !_states.ContainsKey(state.Root) || state.Processing) return;
                    var remaining = SettleDelay - (DateTime.UtcNow - state.LastSignalUtc);
                    if (remaining > TimeSpan.Zero)
                    {
                        state.Timer?.Dispose();
                        state.Timer = new Timer(_ => ProcessStableChange(state), null, remaining, Timeout.InfiniteTimeSpan);
                        return;
                    }
                    state.Processing = true;
                }

                try
                {
                    lock (_gate)
                    {
                        if (_disposed || !_processRunning) return;
                        foreach (var fileState in state.Files.ToArray())
                        {
                            var fileStateValue = fileState.Value;
                            if (!DeepSeekHarnessReader.TryScan(fileState.Key, fileStateValue, out var thisFileCompletions))
                            {
                                continue;
                            }
                            foreach (var item in thisFileCompletions)
                            {
                                if (item.Status == "cancelled") continue;
                                if (!TryDeduplicate(fileState.Key, fileStateValue, item)) continue;
                                completions.Add(item);
                            }
                        }
                    }
                }
                finally
                {
                    lock (_gate) state.Processing = false;
                }

                if (!IsProcessActiveForNotification()) return;
                foreach (var completion in completions)
                {
                    if (IsProcessActiveForNotification()) _completed(completion);
                }
            }
            catch (Exception error)
            {
                _log("DeepSeek Harness 会话事件解析失败：" + error.Message);
            }
        }

        private static bool TryDeduplicate(string path, DeepSeekHarnessFileState fileState, DeepSeekHarnessCompletion item)
        {
            var key = item.SessionId + ":" + item.Turn + ":" + item.Status;
            lock (fileState.Seen)
            {
                if (!fileState.Seen.Add(key))
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsProcessActiveForNotification()
        {
            if (!IsProcessRunningNow())
            {
                UpdateProcessState();
                return false;
            }

            lock (_gate) return !_disposed && _processRunning;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pollTimer?.Dispose();
                _pollTimer = null;
                StopMonitoringLocked();
            }
        }

        private void StopMonitoringLocked()
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Dispose();
                state.Watcher?.Dispose();
            }
            _states.Clear();
        }

        private sealed class SessionState
        {
            internal SessionState(string root) { Root = root; }
            internal string Root { get; }
            internal FileSystemWatcher? Watcher { get; set; }
            internal Timer? Timer { get; set; }
            internal DateTime LastSignalUtc { get; set; }
            internal bool Processing { get; set; }
            internal Dictionary<string, DeepSeekHarnessFileState> Files { get; } = new Dictionary<string, DeepSeekHarnessFileState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Per-session-log watermark and de-dup state.</summary>
    internal sealed class DeepSeekHarnessFileState
    {
        internal long LastCompressedLength { get; set; }
        internal DateTime LastCompressedWriteUtc { get; set; }
        internal long LastSeq { get; set; }
        internal string? SessionId { get; set; }
        internal string? Cwd { get; set; }
        /// <summary>
        /// Explicit workspace/project metadata, when a Harness version writes it in
        /// the session record.  The dispatcher still prefers the cwd leaf, but this
        /// value gives sessions without a cwd a stable project fallback.
        /// </summary>
        internal string ExplicitProjectName { get; set; } = string.Empty;
        internal string SessionTitle { get; set; } = string.Empty;
        internal int DelegationDepth { get; set; } = -1;
        /// <summary>The turn currently receiving user messages without an explicit turn id.</summary>
        internal long CurrentTurn { get; set; } = -1L;
        internal Dictionary<long, long> TurnStartTimes { get; } = new Dictionary<long, long>();
        /// <summary>本轮 assistant/chunk usage 累计（input+output+cacheRead+cacheWrite）。</summary>
        internal Dictionary<long, long> TurnTokenCounts { get; } = new Dictionary<long, long>();
        internal Dictionary<long, string> TurnUserMessages { get; } = new Dictionary<long, string>();
        internal Dictionary<long, string> TurnAssistantMessages { get; } = new Dictionary<long, string>();
        internal Dictionary<long, string> TurnAssistantChunks { get; } = new Dictionary<long, string>();
        internal HashSet<string> Seen { get; } = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Prevents re-emission of historical turn completions across scans.</summary>
        internal HashSet<string> Emitted { get; } = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Prevents re-emission of approval requests across scans.</summary>
        internal HashSet<string> EmittedApprovals { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>A single completed/blocked/failed DeepSeek Harness turn.</summary>
    internal sealed class DeepSeekHarnessCompletion
    {
        internal DeepSeekHarnessCompletion(
            string sessionId,
            long turn,
            string status,
            string message,
            string cwd,
            long durationMs,
            string projectName = "",
            long tokenCount = 0L)
        {
            SessionId = sessionId;
            Turn = turn;
            Status = status;
            Message = message;
            Cwd = cwd ?? string.Empty;
            DurationMs = durationMs;
            ProjectName = projectName ?? string.Empty;
            TokenCount = tokenCount;
        }
        internal string SessionId { get; }
        internal long Turn { get; }
        internal string Status { get; }
        internal string Message { get; }
        internal string Cwd { get; }
        internal long DurationMs { get; }
        internal string ProjectName { get; }
        /// <summary>本轮 token 统计；0 表示来源未提供可靠用量，手机端不展示。</summary>
        internal long TokenCount { get; }
        internal string ProjectLog => Message;
        internal string TaskId => "deepseekharness-" + Short(SessionId) + "-turn" + Turn;

        private static string Short(string value)
        {
            var s = (value ?? string.Empty).Trim();
            return s.Length <= 16 ? s : s.Substring(0, 16);
        }
    }

    /// <summary>
    /// Reads a DeepSeek Harness session log (<c>session.jsonl.zstd</c>).
    ///
    /// The harness appends each event batch as an independently decodable
    /// Zstandard frame, so the file can grow incrementally. A torn final frame
    /// (write interrupted mid-frame) is recovered by keeping the decoded prefix
    /// before the first failure. Every completed/turn/end event is surfaced only
    /// once via the per-file <see cref="DeepSeekHarnessFileState.Seen"/> set.
    /// </summary>
    internal static class DeepSeekHarnessReader
    {
        private const string SessionEventType = "session";
        private const string SessionTitleEventType = "session/title";
        private const string TurnStartEventType = "turn/start";
        private const string TurnEndEventType = "turn/end";
        private const string UserMessageEventType = "user/message";
        private const string AssistantMessageEventType = "assistant/message";
        private const string AssistantChunkEventType = "assistant/chunk";
        /// <summary>DSH writes this when a tool needs user approval (see dsh-user-approval).</summary>
        private const string ApprovalAskedEventType = "approval/asked";
        private const string ApprovalDecidedEventType = "approval/decided";

        /// <summary>Reads the current state of a log without notifying.</summary>
        internal static DeepSeekHarnessFileState Prime(string path, out List<DeepSeekHarnessCompletion> completions)
        {
            var state = new DeepSeekHarnessFileState
            {
                LastCompressedLength = FileLength(path),
                LastCompressedWriteUtc = FileWriteUtc(path)
            };
            completions = new List<DeepSeekHarnessCompletion>();
            ScanInto(path, state, completions, notify: false);
            return state;
        }

        /// <summary>
        /// First scan of a newly discovered file. Unlike <c>Prime</c>, this returns
        /// terminal turns immediately so the monitor can issue a notification for a
        /// just-appeared session log (e.g. a new workspace created while the monitor
        /// is already running).
        /// </summary>
        internal static DeepSeekHarnessFileState Discover(string path, out List<DeepSeekHarnessCompletion> completions)
        {
            var state = new DeepSeekHarnessFileState
            {
                LastCompressedLength = FileLength(path),
                LastCompressedWriteUtc = FileWriteUtc(path)
            };
            completions = new List<DeepSeekHarnessCompletion>();
            ScanInto(path, state, completions, notify: true);
            return state;
        }

        /// <summary>
        /// Scans a log for new terminal events since the last scan. Returns false if
        /// the file did not change or was unreadable.
        /// </summary>
        internal static bool TryScan(string path, DeepSeekHarnessFileState state, out List<DeepSeekHarnessCompletion> completions)
        {
            completions = new List<DeepSeekHarnessCompletion>();
            var length = FileLength(path);
            var writeUtc = FileWriteUtc(path);
            if (length == state.LastCompressedLength && writeUtc == state.LastCompressedWriteUtc)
            {
                return false;
            }
            state.LastCompressedLength = length;
            state.LastCompressedWriteUtc = writeUtc;
            ScanInto(path, state, completions, notify: true);
            return true;
        }

        private static void ScanInto(string path, DeepSeekHarnessFileState state, List<DeepSeekHarnessCompletion> completions, bool notify)
        {
            byte[] compressed;
            try
            {
                compressed = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            string plaintext;
            try
            {
                plaintext = DecompressCommittedPrefix(compressed);
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrEmpty(plaintext)) return;

            // Harness packs independently compressed chunks.  A chunk boundary is not
            // guaranteed to be a JSONL boundary, so parsing per line can join two
            // events or hide a turn/start behind a partial preceding record.  Re-sync
            // on complete event objects instead and let JsonDocument validate each one.
            foreach (var eventJson in EnumerateEventObjects(plaintext))
            {
                try
                {
                    using var json = JsonDocument.Parse(eventJson);
                    var root = json.RootElement;
                    var type = ReadString(root, "type");
                    if (string.Equals(type, SessionEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        state.SessionId = FirstNonEmpty(ReadString(root, "id"), state.SessionId);
                        state.Cwd = FirstNonEmpty(ReadString(root, "cwd"), state.Cwd);
                        state.ExplicitProjectName = FirstNonEmpty(
                            ReadString(root, "projectName"),
                            ReadString(root, "project_name"),
                            ReadString(root, "workspaceName"),
                            ReadString(root, "workspace_name"),
                            state.ExplicitProjectName);
                        state.DelegationDepth = ReadInt(root, "delegationDepth", state.DelegationDepth);
                        continue;
                    }

                    if (string.Equals(type, SessionTitleEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadCompoundJson(root, "data", out var titleData))
                        {
                            state.SessionTitle = FirstNonEmpty(ReadString(titleData, "title"), state.SessionTitle);
                        }
                        else
                        {
                            state.SessionTitle = FirstNonEmpty(ReadString(root, "title"), state.SessionTitle);
                        }
                        continue;
                    }

                    if (TryReadSeq(root, out var seq))
                    {
                        state.LastSeq = Math.Max(state.LastSeq, seq);
                    }

                    if (string.Equals(type, TurnStartEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadDataTurn(root, out var turn))
                        {
                            state.CurrentTurn = turn;
                            if (TryReadTime(root, out var startTime))
                            {
                                state.TurnStartTimes[turn] = startTime;
                            }
                        }
                        continue;
                    }

                    if (string.Equals(type, UserMessageEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadUserMessage(root, state, out var turn, out var message) && turn > 0)
                        {
                            state.TurnUserMessages[turn] = message;
                        }
                        continue;
                    }

                    if (string.Equals(type, AssistantMessageEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadAssistantMessage(root, state, out var turn, out var message) && turn > 0)
                        {
                            state.TurnAssistantMessages[turn] = message;
                        }
                        continue;
                    }

                    if (string.Equals(type, AssistantChunkEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadAssistantChunk(root, state, out var turn, out var chunk) && turn > 0)
                        {
                            state.TurnAssistantChunks[turn] = AppendText(
                                state.TurnAssistantChunks.TryGetValue(turn, out var existing) ? existing : string.Empty,
                                chunk);
                        }
                        else if (TryReadAssistantUsage(root, state, out turn, out var stepTokens) && turn > 0 && stepTokens > 0L)
                        {
                            state.TurnTokenCounts[turn] = state.TurnTokenCounts.TryGetValue(turn, out var existing)
                                ? existing + stepTokens
                                : stepTokens;
                        }
                        continue;
                    }

                    if (string.Equals(type, ApprovalAskedEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        // DSH writes approval/asked inside an open turn when a tool
                        // needs user permission (see dsh-user-approval).  The turn is
                        // blocked until approval/decided resolves it.
                        if (state.DelegationDepth != 0) continue;

                        if (!TryReadCompoundJson(root, "data", out var approvalData)) continue;
                        var approvalId = ReadString(approvalData, "id");
                        if (string.IsNullOrWhiteSpace(approvalId)) continue;
                        if (!state.EmittedApprovals.Add(approvalId)) continue;

                        if (!notify) continue;

                        var toolName = ReadString(approvalData, "toolName");
                        var reason = ReadString(approvalData, "reason");
                        var message = "\u7b49\u5f85\u4f60\u786e\u8ba4\uff1a" +
                            (string.IsNullOrWhiteSpace(toolName) ? "DeepSeek Harness" : toolName);
                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            message += "\uff1a" + reason;
                        }

                        completions.Add(new DeepSeekHarnessCompletion(
                            state.SessionId ?? string.Empty,
                            -1L, // approval events don't have a turn number
                            "waiting",
                            message,
                            state.Cwd ?? string.Empty,
                            0L,
                            FirstNonEmpty(state.ExplicitProjectName, state.SessionTitle),
                            0L));
                        continue;
                    }

                    if (string.Equals(type, ApprovalDecidedEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        // Approval resolved — we don't emit a separate notification
                        // for the resolution (the phone already has the waiting card).
                        // Just track it so the approval/asked pair is acknowledged.
                        continue;
                    }

                    if (string.Equals(type, TurnEndEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        // Only report main-task turns (depth 0). Subagent sessions are
                        // separate logs with delegationDepth &gt; 0 and must not be
                        // reported as the main task finishing.
                        if (state.DelegationDepth != 0)
                        {
                            continue;
                        }

                        if (!TryReadCompoundJson(root, "data", out var data) ||
                            !TryReadLong(data, "turn", out var turn) ||
                            !TryReadCompoundJson(data, "reason", out var reason))
                        {
                            continue;
                        }

                        var kind = ReadString(reason, "kind");
                        var mapped = MapReason(kind, reason);
                        if (string.IsNullOrEmpty(mapped))
                        {
                            // Not a user-visible terminal outcome (max-tokens, internal
                            // aborts, interrupted): acknowledge it and move on.
                            continue;
                        }

                        // Mark every terminal turn as emitted during Prime so that a
                        // subsequent re-scan never replays historical turns.
                        var emitKey = turn + ":" + mapped;
                        if (!state.Emitted.Add(emitKey)) continue;

                        if (!notify) continue;

                        var detail = GetTurnDetail(state, turn);
                        var message = mapped == "failed"
                            ? BuildFailureMessage(reason, detail)
                            : BuildMessage(mapped, detail);
                        var durationMs = 0L;
                        if (TryReadTime(root, out var endTime) && state.TurnStartTimes.TryGetValue(turn, out var startTime))
                        {
                            durationMs = ComputeDurationMs(startTime, endTime);
                        }
                        completions.Add(new DeepSeekHarnessCompletion(
                            state.SessionId ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "session",
                            turn,
                            mapped,
                            message,
                            state.Cwd ?? string.Empty,
                            durationMs,
                            FirstNonEmpty(state.ExplicitProjectName, state.SessionTitle),
                            // waiting 中途不展示 token，与 Claude/Codex/Freebuff 保持一致。
                            mapped == "waiting"
                                ? 0L
                                : (state.TurnTokenCounts.TryGetValue(turn, out var tokenCount) ? tokenCount : 0L)));
                    }
                }
                catch (JsonException)
                {
                    // A partially flushed line is normal while a turn is active.
                }
            }
        }

        /// <summary>Maps a harness terminate reason to TapRelay status ("" = ignore).</summary>
        private static string MapReason(string kind, JsonElement reason)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "completed":
                    return "completed";
                case "error":
                    return "failed";
                case "blocked":
                case "approval_required":
                case "tool_approval":
                case "permission_required":
                    // The turn paused awaiting input / approval: surface as pending.
                    // DSH may use "blocked" for general input waits or more specific
                    // kinds for tool-approval gates. All are user-visible waiting states.
                    return "waiting";
                case "aborted":
                    var cancelReason = ReadString(reason, "reason");
                    var cancelKind = string.Empty;
                    if (reason.ValueKind == JsonValueKind.Object && reason.TryGetProperty("reason", out var inner) &&
                        inner.ValueKind == JsonValueKind.Object)
                    {
                        cancelKind = ReadString(inner, "kind");
                    }
                    // User stop and legacy cancel are deliberate cancellations, never a failure.
                    // They are silently discarded — no notification to the phone.
                    if (string.Equals(cancelKind, "user", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cancelKind, "legacy", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cancelReason, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                    return string.Empty; // parent / disposed / hook teardown, not user-visible.
                case "interrupted":
                    return string.Empty; // interrupted turn, not a definitive user outcome.
                default:
                    // max-tokens / interrupted / unknown kinds are non-terminal-for-user.
                    return string.Empty;
            }
        }

        private static string BuildMessage(string status, string detail)
        {
            string label;
            if (string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                label = "等待输入：DeepSeek Harness 任务等待你继续";
            }
            else
            {
                label = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "DeepSeek Harness 任务失败"
                    : "DeepSeek Harness 任务完成";
            }

            var summary = Summarize(detail);
            return string.IsNullOrWhiteSpace(summary) ? label : label + "：" + summary;
        }

        private static string BuildFailureMessage(JsonElement reason, string turnDetail)
        {
            var failureDetail = string.Empty;
            if (reason.ValueKind == JsonValueKind.Object && reason.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var message = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    failureDetail = message;
                }
                else
                {
                    failureDetail = ReadString(error, "code");
                }
            }

            var detail = FirstNonEmpty(failureDetail, turnDetail);
            if (!string.IsNullOrWhiteSpace(failureDetail) &&
                !string.IsNullOrWhiteSpace(turnDetail) &&
                failureDetail.IndexOf(turnDetail, StringComparison.OrdinalIgnoreCase) < 0)
            {
                detail = failureDetail + "；" + turnDetail;
            }
            return BuildMessage("failed", detail);
        }

        private static string Summarize(string value)
        {
            var normalized = string.Join(" ", (value ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            const int maxLength = 360;
            return normalized.Length <= maxLength ? normalized : normalized.Substring(0, maxLength) + "...";
        }

        private static string GetTurnDetail(DeepSeekHarnessFileState state, long turn)
        {
            if (state.TurnAssistantMessages.TryGetValue(turn, out var assistant) &&
                !string.IsNullOrWhiteSpace(assistant))
            {
                return assistant;
            }
            if (state.TurnAssistantChunks.TryGetValue(turn, out var chunks) &&
                !string.IsNullOrWhiteSpace(chunks))
            {
                return chunks;
            }
            return state.TurnUserMessages.TryGetValue(turn, out var user) ? user : string.Empty;
        }

        private static bool TryReadUserMessage(
            JsonElement root,
            DeepSeekHarnessFileState state,
            out long turn,
            out string message)
        {
            turn = ResolveTurn(root, state);
            message = string.Empty;
            if (!TryReadCompoundJson(root, "data", out var data)) return false;

            var sourceKind = string.Empty;
            if (TryReadCompoundJson(data, "source", out var source))
            {
                sourceKind = ReadString(source, "kind");
            }
            // System snapshots, injected instructions and tool payloads share the
            // user/message event shape.  Only human-origin messages are task context.
            if (!string.IsNullOrWhiteSpace(sourceKind) &&
                !string.Equals(sourceKind, "user", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            message = Summarize(FirstNonEmpty(
                ReadTextProperty(data, "content", textBlocksOnly: false),
                ReadTextProperty(data, "message", textBlocksOnly: false),
                ReadTextProperty(root, "content", textBlocksOnly: false)));
            return !IsInjectedUserMessage(message);
        }

        private static bool TryReadAssistantMessage(
            JsonElement root,
            DeepSeekHarnessFileState state,
            out long turn,
            out string message)
        {
            turn = ResolveTurn(root, state);
            message = string.Empty;
            if (!TryReadCompoundJson(root, "data", out var data)) return false;

            if (data.TryGetProperty("message", out var assistantMessage))
            {
                message = Summarize(ReadTextContent(assistantMessage, textBlocksOnly: true));
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                message = Summarize(ReadTextProperty(data, "content", textBlocksOnly: true));
            }
            return !string.IsNullOrWhiteSpace(message);
        }

        private static bool TryReadAssistantChunk(
            JsonElement root,
            DeepSeekHarnessFileState state,
            out long turn,
            out string chunk)
        {
            turn = ResolveTurn(root, state);
            chunk = string.Empty;
            if (!TryReadCompoundJson(root, "data", out var data) ||
                !TryReadCompoundJson(data, "chunk", out var value))
            {
                return false;
            }

            var kind = ReadString(value, "type");
            if (kind.IndexOf("text", StringComparison.OrdinalIgnoreCase) < 0) return false;
            chunk = Summarize(ReadString(value, "text"));
            return !string.IsNullOrWhiteSpace(chunk);
        }

        /// <summary>
        /// 从 <c>assistant/chunk</c> 的 <c>chunk.type == "usage"</c> 读取本步增量 token：
        /// input + output + cacheRead + cacheWrite（cacheWrite 缺失按 0）。与 Claude 的
        /// 累计语义一致，手机端展示本轮 token 消耗。
        /// </summary>
        private static bool TryReadAssistantUsage(
            JsonElement root,
            DeepSeekHarnessFileState state,
            out long turn,
            out long stepTokens)
        {
            turn = ResolveTurn(root, state);
            stepTokens = 0L;
            if (!TryReadCompoundJson(root, "data", out var data) ||
                !TryReadCompoundJson(data, "chunk", out var value))
            {
                return false;
            }

            if (!string.Equals(ReadString(value, "type"), "usage", StringComparison.OrdinalIgnoreCase) ||
                !TryReadCompoundJson(value, "usage", out var usage))
            {
                return false;
            }

            stepTokens = ReadUsageLong(usage, "inputTokens")
                         + ReadUsageLong(usage, "outputTokens")
                         + ReadUsageLong(usage, "cacheReadTokens")
                         + ReadUsageLong(usage, "cacheWriteTokens");
            return true;
        }

        private static long ReadUsageLong(JsonElement usage, string name)
        {
            return TryReadLong(usage, name, out var value) && value > 0L ? value : 0L;
        }

        private static long ResolveTurn(JsonElement root, DeepSeekHarnessFileState state)
        {
            return TryReadDataTurn(root, out var explicitTurn) && explicitTurn > 0
                ? explicitTurn
                : state.CurrentTurn;
        }

        private static string ReadTextProperty(JsonElement root, string name, bool textBlocksOnly)
        {
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
                ? ReadTextContent(value, textBlocksOnly)
                : string.Empty;
        }

        private static string ReadTextContent(JsonElement value, bool textBlocksOnly)
        {
            var parts = new List<string>();
            CollectTextContent(value, textBlocksOnly, parts);
            return string.Join(" ", parts);
        }

        private static void CollectTextContent(JsonElement value, bool textBlocksOnly, List<string> parts)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                return;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    CollectTextContent(item, textBlocksOnly, parts);
                }
                return;
            }

            if (value.ValueKind != JsonValueKind.Object) return;

            var type = ReadString(value, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = ReadString(value, "text");
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                return;
            }

            if (textBlocksOnly)
            {
                if (value.TryGetProperty("content", out var assistantContent))
                {
                    CollectTextContent(assistantContent, textBlocksOnly: true, parts);
                }
                return;
            }

            var directText = ReadString(value, "text");
            if (!string.IsNullOrWhiteSpace(directText)) parts.Add(directText);
            if (value.TryGetProperty("content", out var content))
            {
                CollectTextContent(content, textBlocksOnly: false, parts);
            }
            else if (value.TryGetProperty("message", out var message))
            {
                CollectTextContent(message, textBlocksOnly: false, parts);
            }
        }

        private static bool IsInjectedUserMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return true;
            return message.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("Current runtime context.", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("The approval policy changed", StringComparison.OrdinalIgnoreCase);
        }

        private static string AppendText(string existing, string addition)
        {
            if (string.IsNullOrWhiteSpace(existing)) return Summarize(addition);
            if (string.IsNullOrWhiteSpace(addition)) return Summarize(existing);
            return Summarize(existing + " " + addition);
        }

        private static long ComputeDurationMs(long startedAt, long endedAt)
        {
            const long maxDurationMs = 30L * 24L * 60L * 60L * 1000L;
            var durationMs = endedAt - startedAt;
            return durationMs > 0L && durationMs <= maxDurationMs ? durationMs : 0L;
        }

        /// <summary>
        /// Decompresses a Zstandard multi-frame buffer, keeping the committed prefix
        /// even when the final frame is torn. The harness appends each event batch as
        /// a structurally complete, independently decodable frame, so frames are
        /// split structurally (mirroring ZSTD frame headers) and each complete frame
        /// is decompressed alone; an incomplete trailing frame is dropped.
        /// </summary>
        internal static string DecompressCommittedPrefix(byte[] compressed)
        {
            var frames = SplitFrames(compressed);
            if (frames.Count == 0)
            {
                // Structural split may fail on an untracked variation; fall back to a
                // single whole-buffer decode (first frame only) rather than returning empty.
                return DecompressSingle(compressed);
            }

            using var output = new MemoryStream(compressed.Length * 2);
            foreach (var frame in frames)
            {
                if (frame.End > compressed.Length || frame.Start < 0 || frame.End <= frame.Start) continue;
                var frameBytes = new byte[frame.End - frame.Start];
                Buffer.BlockCopy(compressed, frame.Start, frameBytes, 0, frameBytes.Length);
                try
                {
                    var decoded = DecompressSingle(frameBytes);
                    if (decoded.Length > 0)
                    {
                        var decodedBytes = Encoding.UTF8.GetBytes(decoded);
                        output.Write(decodedBytes, 0, decodedBytes.Length);
                    }
                }
                catch
                {
                    // A frame that fails to decode ends the committed prefix.
                    break;
                }
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }

        /// <summary>Decompresses exactly one complete Zstandard frame.</summary>
        private static string DecompressSingle(byte[] frameBytes)
        {
            using var source = new MemoryStream(frameBytes, writable: false);
            using var decoder = new DecompressionStream(source);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                int read;
                try
                {
                    read = decoder.Read(chunk, 0, chunk.Length);
                }
                catch (IOException)
                {
                    break; // torn frame: keep whatever decoded before the failure
                }
                catch (ZstdException)
                {
                    break;
                }
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private const uint ZstdMagic = 0xFD2FB528;

        /// <summary>
        /// Splits a concatenated Zstandard stream into structurally complete frames.
        /// Returns frames up to (and excluding) the first torn / incomplete frame.
        /// Throws only on a corrupt reserved header/block bit (not a partial tail).
        /// </summary>
        internal static List<FrameRange> SplitFrames(byte[] buffer)
        {
            var frames = new List<FrameRange>();
            int offset = 0;
            while (offset < buffer.Length)
            {
                int start = offset;
                if (buffer.Length - offset < 4) break;
                if (BitConverter.ToUInt32(buffer, offset) != ZstdMagic) break;
                offset += 4;
                if (offset >= buffer.Length) break;
                int descriptor = buffer[offset];
                offset += 1;
                if ((descriptor & 24) != 0) throw new InvalidDataException("corrupt Zstandard session log: reserved frame-header bit");

                int contentSizeFlag = descriptor >> 6;
                bool singleSegment = (descriptor & 32) != 0;
                bool checksum = (descriptor & 4) != 0;
                int dictionaryFlag = descriptor & 3;
                int dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
                int contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
                int remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
                if (buffer.Length - offset < remainingHeaderBytes) break;
                offset += remainingHeaderBytes;

                bool complete = true;
                while (true)
                {
                    if (buffer.Length - offset < 3)
                    {
                        complete = false;
                        break;
                    }
                    int b0 = buffer[offset];
                    int b1 = buffer[offset + 1];
                    int b2 = buffer[offset + 2];
                    int blockHeader = b0 | (b1 << 8) | (b2 << 16);
                    offset += 3;
                    bool lastBlock = (blockHeader & 1) != 0;
                    int blockType = (int)((uint)blockHeader >> 1) & 3;
                    int blockSize = (int)((uint)blockHeader >> 3);
                    if (blockType == 3) throw new InvalidDataException("corrupt Zstandard session log: reserved block type");
                    int payloadBytes = blockType == 1 ? 1 : blockSize;
                    if (buffer.Length - offset < payloadBytes)
                    {
                        complete = false;
                        break;
                    }
                    offset += payloadBytes;
                    if (lastBlock) break;
                }
                if (!complete) break;
                if (checksum)
                {
                    if (buffer.Length - offset < 4) break;
                    offset += 4;
                }
                frames.Add(new FrameRange(start, offset));
            }
            return frames;
        }

        internal readonly struct FrameRange
        {
            internal FrameRange(int start, int end)
            {
                Start = start;
                End = end;
            }
            internal int Start { get; }
            internal int End { get; }
        }

        /// <summary>
        /// Enumerates validated Harness event objects from concatenated Zstandard
        /// frames.  The writer may split or join JSONL batches at frame boundaries,
        /// so line-oriented parsing cannot be trusted.  When an interrupted record
        /// is encountered, advancing by one character lets the next event marker
        /// re-synchronize instead of discarding the rest of the session.
        /// </summary>
        private static IEnumerable<string> EnumerateEventObjects(string text)
        {
            const string marker = "{\"type\"";
            var offset = 0;
            while (offset < text.Length)
            {
                var start = text.IndexOf(marker, offset, StringComparison.Ordinal);
                if (start < 0) yield break;

                if (!TryFindJsonObjectEnd(text, start, out var end))
                {
                    offset = start + 1;
                    continue;
                }

                var candidate = text.Substring(start, end - start);
                var valid = false;
                try
                {
                    using var _ = JsonDocument.Parse(candidate);
                    valid = true;
                }
                catch (JsonException)
                {
                    // A frame can end inside a record. Continue at the next event
                    // marker so a later complete turn/start or turn/end is retained.
                }

                if (valid)
                {
                    yield return candidate;
                    offset = end;
                }
                else
                {
                    offset = start + 1;
                }
            }
        }

        private static bool TryFindJsonObjectEnd(string text, int start, out int end)
        {
            end = start;
            if (start < 0 || start >= text.Length || text[start] != '{') return false;

            var depth = 0;
            var inString = false;
            var escaping = false;
            for (var index = start; index < text.Length; index++)
            {
                var value = text[index];
                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (value == '\\')
                    {
                        escaping = true;
                    }
                    else if (value == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (value == '"')
                {
                    inString = true;
                }
                else if (value == '{')
                {
                    depth++;
                }
                else if (value == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = index + 1;
                        return true;
                    }
                    if (depth < 0) return false;
                }
            }
            return false;
        }

        internal static long FileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
        }

        internal static DateTime FileWriteUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static bool TryReadSeq(JsonElement root, out long seq)
        {
            return TryReadLong(root, "seq", out seq);
        }

        private static bool TryReadTime(JsonElement root, out long time)
        {
            return TryReadLong(root, "time", out time) ||
                   TryReadLong(root, "timestamp", out time);
        }

        private static bool TryReadDataTurn(JsonElement root, out long turn)
        {
            turn = 0;
            return root.TryGetProperty("data", out var data) &&
                   data.ValueKind == JsonValueKind.Object &&
                   data.TryGetProperty("turn", out var turnProp) &&
                   TryReadLongValue(turnProp, out turn);
        }

        private static bool TryReadCompoundJson(JsonElement root, string name, out JsonElement value)
        {
            value = default;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out value) &&
                   value.ValueKind == JsonValueKind.Object;
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? string.Empty;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 &&
                    name == "delegationDepth") return n.ToString(CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        private static int ReadInt(JsonElement root, string name, int fallback = 0)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) &&
                TryReadLongValue(v, out var n) && n >= int.MinValue && n <= int.MaxValue)
            {
                return (int)n;
            }
            return fallback;
        }

        private static bool TryReadLong(JsonElement root, string name, out long value)
        {
            value = 0;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var v) &&
                   TryReadLongValue(v, out value);
        }

        private static bool TryReadLongValue(JsonElement value, out long result)
        {
            result = 0L;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out result)) return true;
                if (value.TryGetDouble(out var doubleValue) &&
                    doubleValue >= long.MinValue && doubleValue <= long.MaxValue)
                {
                    result = (long)doubleValue;
                    return true;
                }
                return false;
            }

            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }
            if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
            {
                return false;
            }
            result = timestamp.ToUnixTimeMilliseconds();
            return true;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
            return string.Empty;
        }
    }

    /// <summary>Locates the DeepSeek Harness session-log root(s).</summary>
    internal static class DeepSeekHarnessDataLocator
    {
        private const string PersistedPathsFile = "deepseek-harness-roots.json";
        private const string OverrideVariable = "TAPRELAY_DEEPSEEKHARNESS_ROOT";

        internal static IEnumerable<string> FindRoots()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var candidate = Path.GetFullPath(overridePath.Trim());
                if (Directory.Exists(candidate))
                {
                    found.Add(candidate);
                }
            }

            var appData = AgentLogPaths.GetApplicationDataRoot();
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var defaultRoot = Path.Combine(appData, "deepseek-harness-desktop", "harness-home", "sessions");
                if (Directory.Exists(defaultRoot))
                {
                    found.Add(defaultRoot);
                }
            }

            foreach (var remembered in LoadPersistedPaths())
            {
                try { if (Directory.Exists(remembered)) found.Add(Path.GetFullPath(remembered)); }
                catch { }
            }

            SavePersistedPaths(found);
            return found;
        }

        private static string PersistedPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            PersistedPathsFile);

        private static IEnumerable<string> LoadPersistedPaths()
        {
            try
            {
                var file = PersistedPath;
                if (!File.Exists(file)) return Array.Empty<string>();
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                var list = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
                }
                return list;
            }
            catch { return Array.Empty<string>(); }
        }

        private static void SavePersistedPaths(IEnumerable<string> paths)
        {
            try
            {
                var ordered = paths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var file = PersistedPath;
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(file, JsonSerializer.Serialize(ordered), Encoding.UTF8);
            }
            catch { }
        }
    }
}
