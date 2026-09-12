using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TapRelay
{
    /// <summary>
    /// Freebuff (Codebuff 桌面版) 任务状态监控器。
    ///
    /// Freebuff 桌面版把每个项目的会话持久化到本地 SQLite
    /// <c>&lt;state&gt;/projects/&lt;项目名&gt;-&lt;uuid&gt;/desktop-v2.db</c>，其中
    /// <c>&lt;state&gt;</c> 默认是 <c>~/.config/freebuff-desktop</c>
    /// （与 orchestrator 的 <c>FREEBUFF_DESKTOP_STATE_PATH</c> 一致）。threads 表
    /// 记录了每个会话线程的终态：
    ///   - last_turn_outcome == "completed" -> completed
    ///   - last_turn_outcome == "error"     -> failed（正文取 harness_state.output.message）
    ///   - last_turn_outcome == "stopped"/"closed" -> stopped（用户停止，不误报失败）
    ///   - attention_reason == "merge-conflict" -> waiting（合并冲突需用户处理）
    ///
    /// “任务暂停待用户确认”中，等待管理员命令确认（request_elevation）的信号不在
    /// 数据库里：它由 orchestrator 进程内维护，只通过本地 HTTP/SSE
    /// <c>GET /api/events</c> 推送 <c>elevation_request</c> 事件。因此本监控器还
    /// 解析 <c>%APPDATA%\Freebuff\logs\orchestrator-stderr.log</c> 中
    /// <c>[orchestrator-ready]</c> 行获得动态端口，并连接 SSE 流接收 elevation 事件。
    /// 全部为只读被动观察，不修改 Freebuff 配置、不注入进程。
    /// </summary>
    internal sealed class FreebuffMonitor : IDisposable
    {
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);

        private readonly Action<FreebuffCompletion> _completed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DatabaseState> _databases = new Dictionary<string, DatabaseState>(StringComparer.OrdinalIgnoreCase);
        private readonly ElevationListener _elevationListener;
        private NonReentrantMonitorTimer? _pollTimer;
        private bool _processRunning;
        private bool _disposed;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;

        internal FreebuffMonitor(
            Action<FreebuffCompletion> completed,
            Action<string> log,
            Func<bool>? isProcessRunning = null)
        {
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isProcessRunning = isProcessRunning ?? (() => AdaptedProcessGate.IsRunningForAppKey("freebuff"));
            _elevationListener = new ElevationListener(DispatchElevation, _log);
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
                    error => _log("Freebuff 状态恢复检查失败：" + error.Message));
            }

            if (!processRunning)
            {
                _log("Freebuff process is not running; task monitoring is paused until it starts");
                return;
            }

            AttachDatabases();
            _lastDiscoveryUtc = DateTime.UtcNow;
            if (_processRunning)
            {
                _elevationListener.EnsureConnected();
            }

            lock (_gate)
            {
                _log(_databases.Count > 0
                    ? "已启用 Freebuff 任务状态监听（数据库变更事件驱动 + orchestrator 等待确认事件）：" + _databases.Count + " 个库"
                    : "未找到 Freebuff 会话数据库；启动 Freebuff 并打开一个项目后重启 TapRelay 可自动接入");
            }
        }

        internal void Refresh()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            AttachDatabases();
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
                AttachDatabases();
            }

            // 保持 orchestrator SSE 连接：断线后由轮询重新发现端口并重连。
            _elevationListener.EnsureConnected();
            PollDatabaseChanges();
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
                if (!processRunning)
                {
                    StopDatabaseMonitoringLocked();
                    _elevationListener.Stop();
                }
            }

            _log(processRunning
                ? "Freebuff process started; task monitoring resumed"
                : "Freebuff process stopped; task monitoring paused");
            if (processRunning)
            {
                Refresh();
                _elevationListener.EnsureConnected();
            }
            return true;
        }

        private bool IsProcessRunningNow()
        {
            try { return _isProcessRunning(); }
            catch { return false; }
        }

        private void AttachDatabases()
        {
            foreach (var database in FreebuffDataLocator.FindDatabases())
            {
                AddDatabase(database);
            }
        }

        private void AddDatabase(string databasePath)
        {
            if (!File.Exists(databasePath)) return;
            var fullPath = Path.GetFullPath(databasePath);
            lock (_gate)
            {
                if (_disposed || !_processRunning || _databases.ContainsKey(fullPath)) return;
                try
                {
                    var state = new DatabaseState(fullPath)
                    {
                        AttachUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    state.Watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath) ?? ".", "desktop-v2.db*")
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    state.Watcher.Changed += (_, __) => Signal(state);
                    state.Watcher.Created += (_, __) => Signal(state);
                    state.Watcher.Renamed += (_, __) => Signal(state);
                    state.Watcher.Error += (_, __) => Signal(state);
                    state.Watcher.EnableRaisingEvents = true;

                    SeedBaselineLocked(state);
                    _databases.Add(fullPath, state);
                    _log("Freebuff 数据库已接入：" + fullPath);
                }
                catch (Exception error)
                {
                    _log("Freebuff 数据库接入失败：" + fullPath + "；" + error.Message);
                }
            }
        }

        private void SeedBaselineLocked(DatabaseState state)
        {
            try
            {
                foreach (var row in FreebuffDatabaseReader.ReadThreads(state.Path))
                {
                    state.Snapshots[row.Id] = ThreadSnapshot.From(row);
                }
            }
            catch
            {
                // 首次读取失败不阻塞接入；后续轮询会重试。
            }
        }

        private void PollDatabaseChanges()
        {
            if (!IsProcessActiveForNotification()) return;

            DatabaseState[] states;
            lock (_gate)
            {
                if (_disposed) return;
                states = _databases.Values.ToArray();
            }

            foreach (var state in states)
            {
                try
                {
                    // 只发信号，不在轮询路径推进基线：基线的推进与事件读取在
                    // ProcessStableChange 的同一快照里完成，避免基线越过未读事件。
                    if (FreebuffDatabaseReader.ReadMaxUpdated(state.Path) > state.LastMaxUpdated)
                    {
                        Signal(state);
                    }
                }
                catch
                {
                    // 数据库写入期间可能被短暂锁定。
                }
            }
        }

        private void Signal(DatabaseState state)
        {
            lock (_gate)
            {
                if (_disposed || !_processRunning) return;
                SignalLocked(state);
            }
        }

        private void SignalLocked(DatabaseState state)
        {
            state.LastSignalUtc = DateTime.UtcNow;
            state.Timer?.Dispose();
            state.Timer = new Timer(_ => ProcessStableChange(state), null, SettleDelay, Timeout.InfiniteTimeSpan);
        }

        private void ProcessStableChange(DatabaseState state)
        {
            if (!IsProcessActiveForNotification()) return;

            try
            {
                var completions = new List<FreebuffCompletion>();
                lock (_gate)
                {
                    if (_disposed || !_processRunning || state.Processing) return;
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
                        var (threads, observedMax) = FreebuffDatabaseReader.ReadThreadsWithMax(state.Path);
                        if (observedMax > state.LastMaxUpdated) state.LastMaxUpdated = observedMax;
                        foreach (var item in DetectTransitionsLocked(state, threads))
                        {
                            completions.Add(item);
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
                _log("Freebuff 任务事件解析失败：" + error.Message);
            }
        }

        private static IEnumerable<FreebuffCompletion> DetectTransitionsLocked(DatabaseState state, IReadOnlyList<FreebuffThreadRow> rows)
        {
            var events = new List<FreebuffCompletion>();
            foreach (var row in rows)
            {
                if (!state.Snapshots.TryGetValue(row.Id, out var previous))
                {
                    // 新线程：如果是在监控器接入之后才更新且已处于终态，直接上报；
                    // 否则仅建立基线，不重放历史。
                    if (row.UpdatedAt >= state.AttachUtcMs)
                    {
                        var fresh = TryBuildTerminalEvent(row, null);
                        if (fresh != null) events.Add(fresh);
                    }
                    state.Snapshots[row.Id] = ThreadSnapshot.From(row);
                    continue;
                }

                if (row.UpdatedAt <= previous.UpdatedAt) continue;

                var completion = TryBuildTerminalEvent(row, previous);
                if (completion != null) events.Add(completion);
                state.Snapshots[row.Id] = ThreadSnapshot.From(row);
            }

            // 线程被删除（项目关闭清库）时清理快照，避免长期内存残留。
            if (state.Snapshots.Count > rows.Count * 2 + 16)
            {
                var alive = new HashSet<string>(rows.Select(item => item.Id), StringComparer.Ordinal);
                foreach (var stale in state.Snapshots.Keys.Where(key => !alive.Contains(key)).ToArray())
                {
                    state.Snapshots.Remove(stale);
                }
            }
            return events;
        }

        /// <summary>
        /// 依据 last_turn_outcome 从 NULL 变为终值（或新线程已处终态）判断终态事件；
        /// attention_reason 进入 merge-conflict 判等待（需用户处理合并冲突）。
        /// </summary>
        internal static FreebuffCompletion? TryBuildTerminalEvent(FreebuffThreadRow row, ThreadSnapshot? previous)
        {
            var previousOutcome = previous?.LastTurnOutcome;
            var previousAttention = previous?.AttentionReason;

            if (string.Equals(row.TurnState, "idle", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(row.LastTurnOutcome) &&
                !string.Equals(previousOutcome, row.LastTurnOutcome, StringComparison.Ordinal))
            {
                var status = MapOutcome(row.LastTurnOutcome);
                if (status == null) return null;

                var taskLog = BuildTaskLog(status, row);
                var message = Truncate(taskLog, 480);
                if (message.Length == 0)
                {
                    message = BuildFallbackMessage(status);
                }
                var durationMs = row.LastTurnFinishedAt > row.LastPromptAt && row.LastPromptAt > 0
                    ? Math.Max(0L, row.LastTurnFinishedAt - row.LastPromptAt)
                    : 0L;
                return new FreebuffCompletion(
                    row.Id,
                    status,
                    message,
                    row.ProjectPath,
                    durationMs,
                    "freebuff-" + row.Id,
                    row.TokenCount,
                    taskLog);
            }

            if (string.Equals(row.AttentionReason, "merge-conflict", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousAttention, "merge-conflict", StringComparison.OrdinalIgnoreCase))
            {
                return new FreebuffCompletion(
                    row.Id,
                    "waiting",
                    "需要处理合并冲突",
                    row.ProjectPath,
                    0L,
                    "freebuff-mc-" + row.Id + "-" + row.AttentionRevision,
                    taskLog: "需要处理合并冲突");
            }

            return null;
        }

        private static string? MapOutcome(string outcome)
        {
            if (string.Equals(outcome, "completed", StringComparison.OrdinalIgnoreCase)) return "completed";
            if (string.Equals(outcome, "error", StringComparison.OrdinalIgnoreCase)) return "failed";
            // stopped/closed 都是用户停止或关闭标签页，映射为“已停止”，不误报失败。
            if (string.Equals(outcome, "stopped", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "closed", StringComparison.OrdinalIgnoreCase))
            {
                return "stopped";
            }
            return null;
        }

        private static string BuildTaskLog(string status, FreebuffThreadRow row)
        {
            if (status == "failed" || status == "completed")
            {
                var harnessLog = FreebuffDatabaseReader.ReadErrorFromHarnessState(row.HarnessState);
                if (harnessLog.Length > 0) return harnessLog;
            }

            var assistantLog = FreebuffDatabaseReader.ReadAssistantTaskLog(row.AssistantPartsJson);
            if (assistantLog.Length > 0) return assistantLog;

            return string.Empty;
        }

        private static string BuildFallbackMessage(string status)
        {
            if (status == "failed")
            {
                return "任务失败";
            }
            if (status == "stopped") return "任务已停止";
            return "任务完成";
        }

        private static string Truncate(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength - 3) + "...";
        }

        private void DispatchElevation(FreebuffCompletion completion)
        {
            _completed(completion);
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
                StopDatabaseMonitoringLocked();
            }
            _elevationListener.Stop();
        }

        private void StopDatabaseMonitoringLocked()
        {
            foreach (var state in _databases.Values)
            {
                state.Timer?.Dispose();
                state.Watcher?.Dispose();
            }
            _databases.Clear();
        }

        private sealed class DatabaseState
        {
            internal DatabaseState(string path) { Path = path; }
            internal string Path { get; }
            internal FileSystemWatcher? Watcher { get; set; }
            internal Timer? Timer { get; set; }
            internal DateTime LastSignalUtc { get; set; }
            internal bool Processing { get; set; }
            internal long AttachUtcMs { get; set; }
            internal long LastMaxUpdated { get; set; }
            internal Dictionary<string, ThreadSnapshot> Snapshots { get; } = new Dictionary<string, ThreadSnapshot>(StringComparer.Ordinal);
        }

        internal sealed class ThreadSnapshot
        {
            internal string? LastTurnOutcome { get; set; }
            internal string? AttentionReason { get; set; }
            internal long UpdatedAt { get; set; }

            internal static ThreadSnapshot From(FreebuffThreadRow row)
            {
                return new ThreadSnapshot
                {
                    LastTurnOutcome = row.LastTurnOutcome,
                    AttentionReason = row.AttentionReason,
                    UpdatedAt = row.UpdatedAt
                };
            }
        }

        /// <summary>
        /// 监听 orchestrator 的 <c>GET /api/events</c> SSE 流，接收
        /// <c>elevation_request</c> 事件（管理员命令等待用户确认）。
        /// </summary>
        private sealed class ElevationListener
        {
            private readonly Action<FreebuffCompletion> _onElevation;
            private readonly Action<string> _log;
            private readonly object _gate = new object();
            private CancellationTokenSource? _cts;
            private bool _connecting;

            internal ElevationListener(Action<FreebuffCompletion> onElevation, Action<string> log)
            {
                _onElevation = onElevation ?? throw new ArgumentNullException(nameof(onElevation));
                _log = log ?? throw new ArgumentNullException(nameof(log));
            }

            internal void EnsureConnected()
            {
                lock (_gate)
                {
                    if (_cts != null || _connecting) return;
                    _connecting = true;
                }

                int? port;
                try
                {
                    port = FreebuffDataLocator.ReadOrchestratorPort();
                }
                catch
                {
                    port = null;
                }

                lock (_gate)
                {
                    if (port == null)
                    {
                        _connecting = false;
                        return;
                    }
                    _cts = new CancellationTokenSource();
                }

                var capturedPort = port.Value;
                var token = _cts.Token;
                _ = Task.Run(() => ReadLoopAsync(capturedPort, token), CancellationToken.None);
            }

            internal void Stop()
            {
                CancellationTokenSource? cts;
                lock (_gate)
                {
                    cts = _cts;
                    _cts = null;
                    _connecting = false;
                }
                cts?.Cancel();
                cts?.Dispose();
            }

            private async Task ReadLoopAsync(int port, CancellationToken token)
            {
                var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                try
                {
                    var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/api/events";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096);
                    _log("Freebuff orchestrator 事件流已连接（端口 " + port + "）");
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (token.IsCancellationRequested) return;
                        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                        var payload = line.Substring(5).Trim();
                        if (payload.Length == 0) continue;
                        TryHandleEvent(payload);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 主动停止。
                }
                catch (Exception error)
                {
                    _log("Freebuff orchestrator 事件流断开：" + error.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _cts?.Dispose();
                        _cts = null;
                        _connecting = false;
                    }
                    try { client.Dispose(); } catch { }
                }
            }

            private void TryHandleEvent(string payload)
            {
                if (FreebuffOrchestratorEventParser.TryParseElevationRequest(payload, out var completion))
                {
                    _onElevation(completion);
                }
            }
        }
    }

    internal static class FreebuffOrchestratorEventParser
    {
        internal const int MaxCommandLength = 160;

        /// <summary>
        /// 解析 orchestrator SSE 事件帧；仅 <c>elevation_request</c> 事件会生成
        /// status=waiting 的完成事件（管理员命令等待用户确认）。
        /// </summary>
        internal static bool TryParseElevationRequest(string payload, out FreebuffCompletion completion)
        {
            completion = null!;
            if (string.IsNullOrWhiteSpace(payload)) return false;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!TryReadString(root, "type", out var type) || type == null) return false;
                if (!string.Equals(type, "elevation_request", StringComparison.OrdinalIgnoreCase)) return false;

                if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object) return false;
                var requestId = ReadString(request, "id");
                var threadId = ReadString(request, "threadId");
                var command = ReadString(request, "command");
                var cwd = ReadString(request, "cwd");
                var reason = ReadString(request, "reason");
                if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(threadId)) return false;

                var message = "需要你确认";
                if (!string.IsNullOrWhiteSpace(reason)) message += "：" + reason.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    message += "；命令：" + Truncate(command.Trim(), MaxCommandLength);
                }

                completion = new FreebuffCompletion(
                    threadId,
                    "waiting",
                    message,
                    cwd ?? string.Empty,
                    0L,
                    "freebuff-elev-" + requestId,
                    taskLog: message);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static string Truncate(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength - 3) + "...";
        }

        private static string? ReadString(JsonElement root, string name)
        {
            if (TryReadString(root, name, out var value)) return value;
            return null;
        }

        private static bool TryReadString(JsonElement root, string name, out string? value)
        {
            value = null;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var element)) return false;
            if (element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }
            return element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined;
        }
    }

    internal sealed class FreebuffCompletion
    {
        internal FreebuffCompletion(
            string threadId,
            string status,
            string message,
            string cwd,
            long durationMs,
            string taskId,
            long tokenCount = 0L,
            string taskLog = "")
        {
            ThreadId = threadId;
            Status = status;
            Message = message;
            Cwd = cwd ?? string.Empty;
            DurationMs = durationMs;
            TaskId = taskId;
            TokenCount = tokenCount;
            TaskLog = taskLog ?? string.Empty;
        }

        internal string ThreadId { get; }
        internal string Status { get; }
        internal string Message { get; }
        internal string Cwd { get; }
        internal long DurationMs { get; }
        internal string TaskId { get; }
        /// <summary>本轮 token 统计（usage.totalTokens）；0 表示来源未提供可靠用量，手机端不展示。</summary>
        internal long TokenCount { get; }
        /// <summary>本轮最后一条 assistant 总结；与用户输入标题分离，作为手机端任务日志。</summary>
        internal string TaskLog { get; }
    }

    internal sealed class FreebuffThreadRow
    {
        internal string Id { get; set; } = string.Empty;
        internal string ProjectPath { get; set; } = string.Empty;
        internal string Title { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal string TurnState { get; set; } = string.Empty;
        internal string? LastTurnOutcome { get; set; }
        internal string? AttentionReason { get; set; }
        internal long AttentionRevision { get; set; }
        internal long LastPromptAt { get; set; }
        internal long LastTurnFinishedAt { get; set; }
        internal long UpdatedAt { get; set; }
        internal string? HarnessState { get; set; }
        /// <summary>本轮 token 统计，来自 messages 表最后一个 assistant 消息的 metrics_json.usage.totalTokens。</summary>
        internal long TokenCount { get; set; }
        /// <summary>最后一条 assistant 消息的 parts_json，用于提取执行总结。</summary>
        internal string AssistantPartsJson { get; set; } = string.Empty;
    }

    internal static class FreebuffDatabaseReader
    {
        private const string ThreadsColumns =
            "id, project_path, title, status, turn_state, last_turn_outcome, attention_reason, " +
            "attention_revision, last_prompt_at, last_turn_finished_at, updated_at, harness_state";

        /// <summary>
        /// 本轮 token 统计：最后一个 assistant 消息的 metrics_json.usage.totalTokens。
        /// 独立列便于在无 messages 表的旧库上直接退化为 0，不破坏 threads 读取。
        /// </summary>
        private const string LastTurnTokensColumn =
            "(SELECT COALESCE(CAST(json_extract(m.metrics_json, '$.usage.totalTokens') AS INTEGER), 0) " +
            "   FROM messages m WHERE m.thread_id = threads.id AND m.role = 'assistant' " +
            "   ORDER BY m.seq DESC LIMIT 1) AS last_turn_tokens";

        private const string LastAssistantPartsColumn =
            "(SELECT COALESCE(m.parts_json, '[]') " +
            "   FROM messages m WHERE m.thread_id = threads.id AND m.role = 'assistant' " +
            "     AND EXISTS (SELECT 1 FROM json_each(m.parts_json) p " +
            "                   WHERE json_extract(p.value, '$.kind') = 'text' " +
            "                     AND length(trim(COALESCE(json_extract(p.value, '$.text'), ''))) > 0) " +
            "   ORDER BY m.seq DESC LIMIT 1) AS last_assistant_parts";

        internal static long ReadMaxUpdated(string path)
        {
            using var connection = OpenConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(updated_at), 0) FROM threads";
            var value = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return value;
        }

        /// <summary>
        /// 读取 threads 表全部行，并返回本次读取观察到的 MAX(updated_at)。
        /// 基线只推进到同一快照观察到的值，避免把基线越过本次未读到的终态。
        /// </summary>
        internal static (List<FreebuffThreadRow> Rows, long ObservedMax) ReadThreadsWithMax(string path)
        {
            var rows = ReadThreads(path);
            var observedMax = 0L;
            foreach (var row in rows)
            {
                if (row.UpdatedAt > observedMax) observedMax = row.UpdatedAt;
            }
            return (rows, observedMax);
        }

        internal static List<FreebuffThreadRow> ReadThreads(string path)
        {
            var rows = new List<FreebuffThreadRow>();
            using var connection = OpenConnection(path);
            var hasMessages = TableExists(connection, "messages");
            var hasAssistantParts = hasMessages && HasColumn(connection, "messages", "parts_json");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + ThreadsColumns +
                (hasMessages ? ", " + LastTurnTokensColumn : string.Empty) +
                (hasAssistantParts ? ", " + LastAssistantPartsColumn : string.Empty) +
                " FROM threads";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new FreebuffThreadRow
                {
                    Id = reader.GetString(0),
                    ProjectPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Status = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    TurnState = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    LastTurnOutcome = reader.IsDBNull(5) ? null : reader.GetString(5),
                    AttentionReason = reader.IsDBNull(6) ? null : reader.GetString(6),
                    AttentionRevision = reader.IsDBNull(7) ? 0L : reader.GetInt64(7),
                    LastPromptAt = reader.IsDBNull(8) ? 0L : reader.GetInt64(8),
                    LastTurnFinishedAt = reader.IsDBNull(9) ? 0L : reader.GetInt64(9),
                    UpdatedAt = reader.IsDBNull(10) ? 0L : reader.GetInt64(10),
                    HarnessState = reader.IsDBNull(11) ? null : reader.GetString(11),
                    TokenCount = hasMessages && !reader.IsDBNull(12) ? reader.GetInt64(12) : 0L,
                    AssistantPartsJson = hasAssistantParts && reader.FieldCount > 13 && !reader.IsDBNull(13)
                        ? reader.GetString(13)
                        : string.Empty
                });
            }
            return rows;
        }

        private static bool TableExists(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() != null;
        }

        private static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column LIMIT 1";
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$column", column);
            return command.ExecuteScalar() != null;
        }

        /// <summary>
        /// Freebuff stores visible assistant output as parts_json. Only text parts
        /// are user-facing logs; reasoning/tool parts must never become the task log.
        /// </summary>
        internal static string ReadAssistantTaskLog(string? partsJson)
        {
            if (string.IsNullOrWhiteSpace(partsJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(partsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;

                var visibleParts = new List<string>();
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object ||
                        !part.TryGetProperty("kind", out var kind) ||
                        !string.Equals(kind.GetString(), "text", StringComparison.OrdinalIgnoreCase) ||
                        !part.TryGetProperty("text", out var text) ||
                        text.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = text.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value)) visibleParts.Add(value.Trim());
                }

                var combined = string.Join("\n\n", visibleParts);
                return combined.Length <= 4000 ? combined : combined.Substring(0, 4000);
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 从 harness_state JSON 中提取失败原因（根对象 output.message），
        /// 供 last_turn_outcome == "error" 的线程生成失败通知正文。
        /// </summary>
        internal static string ReadErrorFromHarnessState(string? harnessState)
        {
            if (string.IsNullOrWhiteSpace(harnessState)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(harnessState);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("output", out var output)) return string.Empty;
                if (output.ValueKind != JsonValueKind.Object) return string.Empty;
                if (!output.TryGetProperty("message", out var message)) return string.Empty;
                if (message.ValueKind == JsonValueKind.String)
                {
                    var value = (message.GetString() ?? string.Empty).Trim();
                    if (value.Length == 0) return string.Empty;
                    return value.Replace("\r", " ").Replace("\n", " ").Trim();
                }
                return string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static SqliteConnection OpenConnection(string path)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                // 只读 + 共享缓存：TapRelay 从不写用户的活动数据库，共享缓存让读
                // 连接参与 WAL，能读到 orchestrator 尚未 checkpoint 的最新终态。
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=2000;";
                pragma.ExecuteNonQuery();
            }
            return connection;
        }
    }

    internal static class FreebuffDataLocator
    {
        private const int MaxOrchestratorLogTailBytes = 256 * 1024;

        private const string DatabaseName = "desktop-v2.db";
        private const string StateFileName = "state.json";
        private const string ProjectsDirName = "projects";
        private const string PersistedPathsFile = "freebuff-db-paths.json";

        internal static IEnumerable<string> FindDatabases()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var stateRoot = GetStateRoot();
            if (!string.IsNullOrWhiteSpace(stateRoot))
            {
                var projectsDir = Path.Combine(stateRoot, ProjectsDirName);
                if (Directory.Exists(projectsDir))
                {
                    foreach (var directory in Directory.EnumerateDirectories(projectsDir))
                    {
                        var candidate = Path.Combine(directory, DatabaseName);
                        if (File.Exists(candidate))
                        {
                            try { found.Add(Path.GetFullPath(candidate)); }
                            catch { }
                        }
                    }
                }
            }

            // 旧版存储：项目目录 .freebuff/desktop-v2.db（从 state.json 的项目根推导）。
            foreach (var projectRoot in ReadProjectRootsFromState())
            {
                var legacy = Path.Combine(projectRoot, ".freebuff", DatabaseName);
                if (File.Exists(legacy))
                {
                    try { found.Add(Path.GetFullPath(legacy)); }
                    catch { }
                }
            }

            // 记住历史发现路径，Freebuff 未运行时也能在重启后立即接回。
            foreach (var remembered in LoadPersistedPaths())
            {
                try { if (File.Exists(remembered)) found.Add(Path.GetFullPath(remembered)); }
                catch { }
            }
            SavePersistedPaths(found);
            return found;
        }

        internal static bool IsInstalled()
        {
            // 显式环境变量覆盖视为“用户已声明安装”。
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAPRELAY_FREEBUFF_STATE_PATH")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FREEBUFF_DESKTOP_STATE_PATH")))
            {
                return true;
            }

            var exe = Path.Combine(
                AgentLogPaths.GetLocalApplicationDataRoot(),
                "Programs",
                "@codebufffreebuff-desktop",
                "Freebuff.exe");
            if (File.Exists(exe)) return true;

            try
            {
                return FindDatabases().Any();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从 orchestrator 日志解析当前 HTTP 端口。端口每次启动随机分配，只打印在
        /// <c>%APPDATA%\Freebuff\logs\orchestrator-stderr.log</c> 的
        /// <c>[orchestrator-ready]</c> / <c>listening on</c> 行；从最后一行往前找。
        /// </summary>
        internal static int? ReadOrchestratorPort()
        {
            var overridePort = Environment.GetEnvironmentVariable("TAPRELAY_FREEBUFF_ORCHESTRATOR_PORT");
            if (!string.IsNullOrWhiteSpace(overridePort) && int.TryParse(overridePort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var overrideValue) && overrideValue > 0 && overrideValue <= 65535)
            {
                return overrideValue;
            }

            var logPath = Path.Combine(
                AgentLogPaths.GetApplicationDataRoot(),
                "Freebuff",
                "logs",
                "orchestrator-stderr.log");
            if (!File.Exists(logPath)) return null;

            string[] lines;
            try
            {
                lines = ReadLogTail(logPath);
            }
            catch
            {
                return null;
            }

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var port = ReadOrchestratorPortFromLine(lines[index]);
                if (port != null) return port;
            }
            return null;
        }

        private static string[] ReadLogTail(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var skipBytes = Math.Max(0L, stream.Length - MaxOrchestratorLogTailBytes);
            if (skipBytes > 0)
            {
                stream.Seek(skipBytes, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true);
            if (skipBytes > 0)
            {
                // The first partial line may begin in the middle of the tail.
                reader.ReadLine();
            }

            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            return lines.ToArray();
        }

        /// <summary>
        /// 从单行日志解析端口；支持 <c>[orchestrator-ready] {...}</c> 与
        /// <c>listening on http://127.0.0.1:PORT</c> 两种格式。
        /// </summary>
        internal static int? ReadOrchestratorPortFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var readyIndex = line.IndexOf("[orchestrator-ready]", StringComparison.OrdinalIgnoreCase);
            if (readyIndex >= 0)
            {
                var jsonStart = line.IndexOf('{', readyIndex);
                var jsonEnd = line.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = line.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("port", out var portElement) &&
                            portElement.ValueKind == JsonValueKind.Number &&
                            portElement.TryGetInt32(out var port) &&
                            port > 0 && port <= 65535)
                        {
                            return port;
                        }
                    }
                    catch (JsonException)
                    {
                        // 尝试其他格式。
                    }
                }
            }

            var listeningIndex = line.IndexOf("listening on http://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
            if (listeningIndex >= 0)
            {
                var candidate = line.Substring(listeningIndex + "listening on http://127.0.0.1:".Length).Trim();
                var end = 0;
                while (end < candidate.Length && char.IsDigit(candidate[end])) end++;
                if (end > 0 && int.TryParse(candidate.Substring(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port <= 65535)
                {
                    return port;
                }
            }
            return null;
        }

        internal static string? GetStateRoot()
        {
            var statePath = Environment.GetEnvironmentVariable("TAPRELAY_FREEBUFF_STATE_PATH") ??
                            Environment.GetEnvironmentVariable("FREEBUFF_DESKTOP_STATE_PATH");
            if (!string.IsNullOrWhiteSpace(statePath))
            {
                try
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(statePath.Trim()));
                    if (!string.IsNullOrWhiteSpace(directory)) return directory;
                }
                catch
                {
                    // 回退默认路径。
                }
            }
            return Path.Combine(AgentLogPaths.GetUserProfileRoot(), ".config", "freebuff-desktop");
        }

        private static IEnumerable<string> ReadProjectRootsFromState()
        {
            var stateRoot = GetStateRoot();
            if (string.IsNullOrWhiteSpace(stateRoot)) return Array.Empty<string>();
            var statePath = Path.Combine(stateRoot, StateFileName);
            if (!File.Exists(statePath)) return Array.Empty<string>();

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = doc.RootElement;
                AddStrings(root, "recentProjects", roots);
                if (root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object)
                {
                    AddNestedStrings(workspace, "tabs", "projectPath", roots);
                    AddNestedStrings(workspace, "windows", "projectPath", roots);
                }
            }
            catch
            {
                // state.json 损坏不影响数据库发现。
            }
            return roots;
        }

        private static void AddStrings(JsonElement parent, string property, HashSet<string> target)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var array)) return;
            if (array.ValueKind != JsonValueKind.Array) return;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    target.Add(item.GetString()!);
                }
            }
        }

        private static void AddNestedStrings(JsonElement parent, string property, string childProperty, HashSet<string> target)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var array)) return;
            if (array.ValueKind != JsonValueKind.Array) return;
            foreach (var item in array.EnumerateArray())
            {
                AddStrings(item, childProperty, target);
            }
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
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void SavePersistedPaths(IEnumerable<string> paths)
        {
            try
            {
                var ordered = paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var file = PersistedPath;
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(file, JsonSerializer.Serialize(ordered), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
