using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TapRelay
{
    internal sealed class AdriveTransferMonitor : IDisposable
    {
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);
        private readonly Action<AdriveTaskEvent> _changed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DatabaseState> _states = new Dictionary<string, DatabaseState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LogState> _logStates = new Dictionary<string, LogState>(StringComparer.OrdinalIgnoreCase);
        private NonReentrantMonitorTimer? _pollTimer;
        private bool _processRunning;
        private bool _disposed;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;

        internal AdriveTransferMonitor(
            Action<AdriveTaskEvent> changed,
            Action<string> log,
            Func<bool>? isProcessRunning = null)
        {
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isProcessRunning = isProcessRunning ?? (() => AdaptedProcessGate.IsRunningForAppKey("adrive"));
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
                    error => _log("阿里云盘进程恢复检查失败：" + error.Message));
            }

            if (!processRunning)
            {
                _log("aDrive process is not running; transfer monitoring is paused until it starts");
                return;
            }

            var databases = AdriveTransferDataLocator.FindDatabases().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var database in databases) AddDatabase(database);
            var logs = AdriveTransferDataLocator.FindLogs().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var log in logs) AddLog(log);
            _lastDiscoveryUtc = DateTime.UtcNow;

            if (databases.Length == 0 && logs.Length == 0)
            {
                _log("\u672a\u627e\u5230\u963f\u91cc\u4e91\u76d8\u4f20\u8f93\u4efb\u52a1\u6570\u636e\u6216\u65e5\u5fd7\uff1b\u767b\u5f55\u5e76\u521b\u5efa\u4efb\u52a1\u540e\u5c06\u81ea\u52a8\u63a5\u5165");
            }
            else
            {
                _log("\u5df2\u542f\u7528\u963f\u91cc\u4e91\u76d8\u4e0a\u4f20/\u4e0b\u8f7d\u4efb\u52a1\u72b6\u6001\u76d1\u63a7\uff1a" + databases.Length + " \u4e2a IndexedDB\u6570\u636e\u5e93\uff0c" + logs.Length + " \u4e2a\u4f20\u8f93\u65e5\u5fd7");
            }
        }

        internal void Refresh()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            foreach (var database in AdriveTransferDataLocator.FindDatabases()) AddDatabase(database);
            foreach (var log in AdriveTransferDataLocator.FindLogs()) AddLog(log);
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

            PollFileChanges();
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
                if (!processRunning) StopTransferMonitoringLocked();
            }

            _log(processRunning
                ? "aDrive process started; transfer monitoring resumed"
                : "aDrive process stopped; transfer monitoring paused");
            if (processRunning) Refresh();
            return true;
        }

        private bool IsProcessRunningNow()
        {
            try { return _isProcessRunning(); }
            catch { return false; }
        }

        private void AddDatabase(string databasePath)
        {
            if (!Directory.Exists(databasePath)) return;
            var fullPath = Path.GetFullPath(databasePath);
            lock (_gate)
            {
                if (_disposed || !_processRunning || _states.ContainsKey(fullPath)) return;
                try
                {
                    var state = new DatabaseState(fullPath)
                    {
                        Tasks = AdriveTransferDatabaseReader.Read(fullPath),
                        FileSignature = AdriveTransferDatabaseReader.GetFileSignature(fullPath)
                    };
                    foreach (var task in state.Tasks.Values)
                    {
                        if (AdriveBatchState.IsTrackable(task))
                        {
                            GetBatch(state, task.Direction).AddOrUpdate(task);
                        }
                    }
                    state.Watcher = new FileSystemWatcher(fullPath, "*")
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    state.Watcher.Changed += (_, e) => SignalIfDatabaseFile(state, e.Name);
                    state.Watcher.Created += (_, e) => SignalIfDatabaseFile(state, e.Name);
                    state.Watcher.Renamed += (_, e) => SignalIfDatabaseFile(state, e.Name);
                    state.Watcher.Error += (_, __) => SignalDatabaseWatcherError(state);
                    state.Watcher.EnableRaisingEvents = true;
                    _states.Add(fullPath, state);
                    _log("\u963f\u91cc\u4e91\u76d8 IndexedDB\u5df2\u63a5\u5165\uff1a" + fullPath);
                }
                catch (Exception error)
                {
                    _log("\u963f\u91cc\u4e91\u76d8 IndexedDB\u63a5\u5165\u5931\u8d25\uff1a" + fullPath + "\uff1a" + error.Message);
                }
            }
        }

        private void AddLog(string logPath)
        {
            if (!File.Exists(logPath)) return;
            var fullPath = Path.GetFullPath(logPath);
            lock (_gate)
            {
                if (_disposed || !_processRunning || _logStates.ContainsKey(fullPath)) return;
                try
                {
                    var info = new FileInfo(fullPath);
                    var state = new LogState(fullPath) { Offset = info.Length };
                    var directory = Path.GetDirectoryName(fullPath) ?? ".";
                    var fileName = Path.GetFileName(fullPath);
                    state.Watcher = new FileSystemWatcher(directory, fileName)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    state.Watcher.Changed += (_, __) => PollLogFile(state);
                    state.Watcher.Created += (_, __) => PollLogFile(state);
                    state.Watcher.Renamed += (_, __) => PollLogFile(state);
                    state.Watcher.Error += (_, __) => PollLogFile(state);
                    state.Watcher.EnableRaisingEvents = true;
                    _logStates.Add(fullPath, state);
                    _log("\u963f\u91cc\u4e91\u76d8\u4f20\u8f93\u65e5\u5fd7\u5df2\u63a5\u5165\uff1a" + fullPath);
                }
                catch (Exception error)
                {
                    _log("\u963f\u91cc\u4e91\u76d8\u4f20\u8f93\u65e5\u5fd7\u63a5\u5165\u5931\u8d25\uff1a" + fullPath + "\uff1a" + error.Message);
                }
            }
        }

        /// <summary>
        /// 立即执行一次文件轮询（数据库签名 + 日志追加行）。测试专用：绕过
        /// FileSystemWatcher 在快速连续写入时可能丢事件的不确定性，以及 15 秒
        /// 轮询兜底过慢的问题，让断言可以确定性地等到结果。
        /// </summary>
        internal void PollNow()
        {
            PollFileChanges();
        }

        private void PollFileChanges()
        {
            if (!IsProcessActiveForNotification()) return;

            DatabaseState[] states;
            LogState[] logs;
            lock (_gate)
            {
                if (_disposed) return;
                states = _states.Values.ToArray();
                logs = _logStates.Values.ToArray();
            }

            foreach (var state in states)
            {
                try
                {
                    var signature = AdriveTransferDatabaseReader.GetFileSignature(state.Path);
                    lock (_gate)
                    {
                        if (_disposed || !_processRunning || signature == state.FileSignature) continue;
                        state.FileSignature = signature;
                        SignalLocked(state);
                    }
                }
                catch
                {
                    // The database may be rotated while the application is writing it.
                }
            }

            foreach (var log in logs) PollLogFile(log);
        }

        private void PollLogFile(LogState state)
        {
            if (!IsProcessActiveForNotification()) return;

            List<AdriveTaskEvent> events;
            try
            {
                lock (_gate)
                {
                    if (_disposed || !TryReadAppendedLogLines(state, out var lines)) return;
                    events = new List<AdriveTaskEvent>();
                    foreach (var line in lines)
                    {
                        if (!AdriveTransferLogParser.TryParse(line, out var entry)) continue;
                        ProcessLogEntry(state, entry, events);
                    }
                }
            }
            catch (Exception error)
            {
                _log("\u963f\u91cc\u4e91\u76d8\u4f20\u8f93\u65e5\u5fd7\u8bfb\u53d6\u5931\u8d25\uff1a" + error.Message);
                return;
            }

            if (!IsProcessActiveForNotification()) return;
            foreach (var item in events)
            {
                if (IsProcessActiveForNotification()) _changed(item);
            }
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

        private static bool TryReadAppendedLogLines(LogState state, out List<string> lines)
        {
            lines = new List<string>();
            if (!File.Exists(state.Path)) return false;

            using var stream = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryReadAppendedLogLines(state, stream, out lines);
        }

        private static bool TryReadAppendedLogLines(LogState state, Stream stream, out List<string> lines)
        {
            lines = new List<string>();
            if (stream.Length < state.Offset)
            {
                state.Offset = 0;
                state.PendingBytes = Array.Empty<byte>();
                state.Active.Clear();
                state.TerminalIds.Clear();
                state.TerminalLogKeys.Clear();
            }
            if (stream.Length == state.Offset) return true;
            if (stream.Length - state.Offset > int.MaxValue) return false;

            stream.Position = state.Offset;
            var appended = new byte[(int)(stream.Length - state.Offset)];
            var read = 0;
            while (read < appended.Length)
            {
                var count = stream.Read(appended, read, appended.Length - read);
                if (count <= 0) return false;
                read += count;
            }
            // The writer may append while Read is in progress. Advance only by
            // bytes consumed, so the next callback can still read that suffix.
            state.Offset += read;

            var combined = new byte[state.PendingBytes.Length + appended.Length];
            Buffer.BlockCopy(state.PendingBytes, 0, combined, 0, state.PendingBytes.Length);
            Buffer.BlockCopy(appended, 0, combined, state.PendingBytes.Length, appended.Length);
            var lastNewLine = Array.LastIndexOf(combined, (byte)'\n');
            if (lastNewLine < 0)
            {
                state.PendingBytes = combined;
                return true;
            }

            var complete = Encoding.UTF8.GetString(combined, 0, lastNewLine + 1);
            state.PendingBytes = combined.Skip(lastNewLine + 1).ToArray();
            foreach (var line in complete.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.TrimEnd('\r');
                if (value.Length > 0) lines.Add(value);
            }
            return true;
        }

        private static void ProcessLogEntry(LogState state, AdriveTransferLogEntry entry, List<AdriveTaskEvent> events)
        {
            if (string.Equals(entry.Component, "RendererDownload", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.HasTaskCount && entry.TaskCount == 0)
                {
                    FlushDownloadBatch(state, events);
                }
                return;
            }
            if (!string.Equals(entry.Component, "Downloader", StringComparison.OrdinalIgnoreCase)) return;
            if (entry.Worker <= 0) return;
            var operation = entry.Operation;
            if (operation == "create")
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    state.Active[entry.Worker] = new ActiveDownload(entry.Worker, entry.Path, entry.Timestamp);
                }
                return;
            }

            if (operation.Contains("cancel", StringComparison.Ordinal) ||
                operation.Contains("abort", StringComparison.Ordinal) ||
                operation == "stop" || operation == "stopped")
            {
                state.Active.Remove(entry.Worker);
                return;
            }

            var isComplete = operation == "complete" || operation == "completed" || operation == "success" || operation == "succeeded";
            var isFailed = operation == "failed" || operation == "failure" || operation == "error";
            if (!isComplete && !isFailed) return;

            var terminalLogKey = entry.Worker.ToString(CultureInfo.InvariantCulture) + "\u001f" +
                                 entry.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "\u001f" + operation;
            if (!state.TerminalLogKeys.Add(terminalLogKey)) return;

            if (!state.Active.TryGetValue(entry.Worker, out var active))
            {
                active = new ActiveDownload(entry.Worker, entry.Path, entry.Timestamp);
            }
            else if (string.IsNullOrWhiteSpace(active.Path) && !string.IsNullOrWhiteSpace(entry.Path))
            {
                active.Path = entry.Path;
            }

            var taskId = BuildLogTaskId(active);
            if (!state.TerminalIds.Add(taskId)) return;
            state.Active.Remove(entry.Worker);
            var task = new AdriveTaskRecord
            {
                Direction = AdriveTransferDirection.Download,
                TaskId = taskId,
                Status = isComplete ? "SUCCESSED" : "FAILED",
                Name = AdriveTransferLogParser.GetDisplayName(active.Path),
                Path = active.Path,
                CreateTimeMs = active.Timestamp.ToUnixTimeMilliseconds(),
                EndTimeMs = entry.Timestamp.ToUnixTimeMilliseconds()
            };
            state.PendingDownloads[taskId] = task;
        }

        private static void FlushDownloadBatch(LogState state, List<AdriveTaskEvent> events)
        {
            if (state.PendingDownloads.Count == 0) return;
            var completed = state.PendingDownloads.Values.Count(task => AdriveStatus.Map(task.Status) == "done");
            var failed = state.PendingDownloads.Values.Count(task => AdriveStatus.Map(task.Status) == "failed");
            var start = state.PendingDownloads.Values
                .Select(task => task.CreateTimeMs)
                .Where(value => value > 0)
                .DefaultIfEmpty(0)
                .Min();
            var end = state.PendingDownloads.Values
                .Select(task => task.EndTimeMs)
                .Where(value => value > 0)
                .DefaultIfEmpty(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Max();
            if (start <= 0 || start > end) start = end;
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, end - start));
            var batchId = BuildBatchId(state.Path, AdriveTransferDirection.Download, state.PendingDownloads.Values, completed, failed);
            var taskSnapshot = state.PendingDownloads.Values.ToArray();
            state.PendingDownloads.Clear();
            if (completed > 0 || failed > 0)
            {
                events.Add(AdriveTaskEvent.FromBatch(AdriveTransferDirection.Download, completed, failed, duration, batchId, taskSnapshot));
            }
        }

        private static string BuildBatchId(string path, AdriveTransferDirection direction, IEnumerable<AdriveTaskRecord> tasks, int completed, int failed)
        {
            var input = (path ?? string.Empty).ToLowerInvariant() + "\u001f" + direction + "\u001f" + completed + "\u001f" + failed + "\u001f" +
                        string.Join("|", tasks.OrderBy(task => task.Identity, StringComparer.Ordinal)
                            .Select(task => task.Identity + ":" + task.CreateTimeMs + ":" + task.EndTimeMs));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var prefix = direction == AdriveTransferDirection.Upload ? "upload" : "download";
            return "adrive-" + prefix + "-batch-" + BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string BuildLogTaskId(ActiveDownload active)
        {
            var input = active.Worker.ToString(CultureInfo.InvariantCulture) + "\u001f" +
                        (active.Path ?? string.Empty) + "\u001f" + active.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return "download-log-" + active.Worker.ToString(CultureInfo.InvariantCulture) + "-" +
                   BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
        }

        private void SignalIfDatabaseFile(DatabaseState state, string? fileName)
        {
            if (!AdriveTransferDatabaseReader.IsDatabaseFile(fileName)) return;
            lock (_gate)
            {
                if (_disposed || !_processRunning) return;
                SignalLocked(state);
            }
        }

        private void SignalDatabaseWatcherError(DatabaseState state)
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
                var current = AdriveTransferDatabaseReader.Read(state.Path);
                var events = new List<AdriveTaskEvent>();
                lock (_gate)
                {
                    if (_disposed || !_processRunning) return;
                    foreach (var item in current.Values)
                    {
                        if (!state.Tasks.TryGetValue(item.Identity, out var previous) ||
                            !string.Equals(previous.Status, item.Status, StringComparison.OrdinalIgnoreCase))
                        {
                            GetBatch(state, item.Direction).AddOrUpdate(item);
                        }
                    }

                    foreach (var previous in state.Tasks.Values)
                    {
                        if (!current.ContainsKey(previous.Identity))
                        {
                            GetBatch(state, previous.Direction).RemoveIfActive(previous.Identity);
                        }
                    }

                    foreach (var batch in state.Batches.Values.ToArray())
                    {
                        if (!batch.ShouldComplete) continue;
                        var taskEvent = batch.CreateCompletion(state.Path);
                        if (taskEvent != null) events.Add(taskEvent);
                    }
                    state.Tasks = current;
                }

                if (!IsProcessActiveForNotification()) return;
                foreach (var item in events)
                {
                    if (IsProcessActiveForNotification()) _changed(item);
                }
            }
            catch (Exception error)
            {
                _log("\u963f\u91cc\u4e91\u76d8\u4efb\u52a1\u72b6\u6001\u8bfb\u53d6\u5931\u8d25\uff1a" + error.Message);
            }
            finally
            {
                lock (_gate) state.Processing = false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pollTimer?.Dispose();
                _pollTimer = null;
                StopTransferMonitoringLocked();
            }
        }

        private void StopTransferMonitoringLocked()
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Dispose();
                state.Watcher?.Dispose();
            }
            foreach (var state in _logStates.Values)
            {
                state.Watcher?.Dispose();
            }
            _states.Clear();
            _logStates.Clear();
        }

        private sealed class DatabaseState
        {
            internal DatabaseState(string path) { Path = path; }
            internal string Path { get; }
            internal Dictionary<string, AdriveTaskRecord> Tasks { get; set; } = new Dictionary<string, AdriveTaskRecord>(StringComparer.Ordinal);
            internal Dictionary<AdriveTransferDirection, AdriveBatchState> Batches { get; } = new Dictionary<AdriveTransferDirection, AdriveBatchState>();
            internal string FileSignature { get; set; } = string.Empty;
            internal FileSystemWatcher? Watcher { get; set; }
            internal Timer? Timer { get; set; }
            internal DateTime LastSignalUtc { get; set; }
            internal bool Processing { get; set; }
        }

        private static AdriveBatchState GetBatch(DatabaseState state, AdriveTransferDirection direction)
        {
            if (!state.Batches.TryGetValue(direction, out var batch))
            {
                batch = new AdriveBatchState(direction);
                state.Batches[direction] = batch;
            }
            return batch;
        }

        private sealed class AdriveBatchState
        {
            private readonly Dictionary<string, AdriveTaskRecord> _tasks = new Dictionary<string, AdriveTaskRecord>(StringComparer.Ordinal);

            internal AdriveBatchState(AdriveTransferDirection direction)
            {
                Direction = direction;
            }

            internal AdriveTransferDirection Direction { get; }

            internal static bool IsTrackable(AdriveTaskRecord task)
            {
                var status = AdriveStatus.Map(task.Status);
                return status != "done" && status != "failed" && status != "cancelled";
            }

            internal void AddOrUpdate(AdriveTaskRecord task)
            {
                _tasks[task.Identity] = task;
            }

            internal void RemoveIfActive(string identity)
            {
                if (_tasks.TryGetValue(identity, out var task) && IsTrackable(task)) _tasks.Remove(identity);
            }

            internal bool ShouldComplete
            {
                get
                {
                    if (_tasks.Count == 0) return false;
                    var hasResult = false;
                    foreach (var task in _tasks.Values)
                    {
                        var status = AdriveStatus.Map(task.Status);
                        if (status == "pending" || status == "in_progress" || status == "paused") return false;
                        if (status == "done" || status == "failed") hasResult = true;
                    }
                    return hasResult;
                }
            }

            internal AdriveTaskEvent? CreateCompletion(string path)
            {
                var completed = _tasks.Values.Count(task => AdriveStatus.Map(task.Status) == "done");
                var failed = _tasks.Values.Count(task => AdriveStatus.Map(task.Status) == "failed");
                if (completed == 0 && failed == 0)
                {
                    _tasks.Clear();
                    return null;
                }

                var start = _tasks.Values
                    .Where(task => AdriveStatus.Map(task.Status) == "done" || AdriveStatus.Map(task.Status) == "failed")
                    .Select(task => task.CreateTimeMs)
                    .Where(value => value > 0)
                    .DefaultIfEmpty(0)
                    .Min();
                var end = _tasks.Values
                    .Where(task => AdriveStatus.Map(task.Status) == "done" || AdriveStatus.Map(task.Status) == "failed")
                    .Select(task => task.EndTimeMs)
                    .Where(value => value > 0)
                    .DefaultIfEmpty(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .Max();
                if (start <= 0 || start > end) start = end;
                var duration = TimeSpan.FromMilliseconds(Math.Max(0, end - start));
                var batchId = BuildBatchId(path, Direction, _tasks.Values, completed, failed);
                var taskSnapshot = _tasks.Values.ToArray();
                _tasks.Clear();
                return AdriveTaskEvent.FromBatch(Direction, completed, failed, duration, batchId, taskSnapshot);
            }
        }

        private sealed class LogState
        {
            internal LogState(string path) { Path = path; }
            internal string Path { get; }
            internal long Offset { get; set; }
            internal byte[] PendingBytes { get; set; } = Array.Empty<byte>();
            internal Dictionary<int, ActiveDownload> Active { get; } = new Dictionary<int, ActiveDownload>();
            internal Dictionary<string, AdriveTaskRecord> PendingDownloads { get; } = new Dictionary<string, AdriveTaskRecord>(StringComparer.Ordinal);
            internal HashSet<string> TerminalIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal HashSet<string> TerminalLogKeys { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal FileSystemWatcher? Watcher { get; set; }
        }

        private sealed class ActiveDownload
        {
            internal ActiveDownload(int worker, string path, DateTimeOffset timestamp)
            {
                Worker = worker;
                Path = path ?? string.Empty;
                Timestamp = timestamp;
            }

            internal int Worker { get; }
            internal string Path { get; set; }
            internal DateTimeOffset Timestamp { get; }
        }
    }

    internal sealed class AdriveTaskEvent
    {
        private AdriveTaskEvent(AdriveTaskRecord task, string status, string message)
        {
            // Upload and download share the single stable application key so every
            // client resolves the source to Aliyun Drive instead of "Other".
            Source = "adrive";
            Status = status;
            TaskId = "adrive-" + task.Direction.ToString().ToLowerInvariant() + "-" + task.TaskId;
            Message = message;
            var folder = BuildProjectName(task);
            ProjectName = string.IsNullOrWhiteSpace(folder)
                ? (task.Direction == AdriveTransferDirection.Upload ? "上传任务" : "下载任务")
                : folder;
            DurationMs = task.DurationMs;
        }

        internal string Source { get; }
        internal string Status { get; }
        internal string TaskId { get; }
        internal string Message { get; }
        internal string ProjectName { get; }
        internal long DurationMs { get; }

        internal static AdriveTaskEvent? From(AdriveTaskRecord task)
        {
            var status = AdriveStatus.Map(task.Status);
            if (status == "cancelled") return null;
            return new AdriveTaskEvent(task, status, BuildMessage(task, status));
        }

        internal static AdriveTaskEvent FromLog(AdriveTaskRecord task, string status)
        {
            return new AdriveTaskEvent(task, status, BuildMessage(task, status));
        }

        internal static AdriveTaskEvent FromBatch(
            AdriveTransferDirection direction,
            int completedCount,
            int failedCount,
            TimeSpan duration,
            string batchId,
            IEnumerable<AdriveTaskRecord> tasks)
        {
            var status = completedCount == 0 && failedCount > 0 ? "failed" : "done";
            var directionLabel = direction == AdriveTransferDirection.Upload ? "上传" : "下载";
            var message = "阿里云盘" + directionLabel + "批量结束：完成 " + completedCount + " 个，失败 " + failedCount + " 个，耗时 " + FormatDuration(duration);
            return new AdriveTaskEvent(
                status,
                batchId,
                message,
                (long)Math.Max(0, duration.TotalMilliseconds),
                BuildBatchProjectName(direction, tasks));
        }

        private AdriveTaskEvent(string status, string taskId, string message, long durationMs, string projectName)
        {
            Source = "adrive";
            Status = status;
            TaskId = taskId;
            Message = message;
            ProjectName = projectName;
            DurationMs = durationMs;
        }

        private static string BuildBatchProjectName(AdriveTransferDirection direction, IEnumerable<AdriveTaskRecord> tasks)
        {
            var names = (tasks ?? Array.Empty<AdriveTaskRecord>())
                .Select(task => BuildProjectName(task))
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            if (names.Count == 1) return names[0];
            if (names.Count > 1) return string.Join(", ", names) + (names.Count == 3 ? ", ..." : string.Empty);
            return (direction == AdriveTransferDirection.Upload ? "上传" : "下载") + "批量任务";
        }

        private static string BuildProjectName(AdriveTaskRecord task)
        {
            var folder = ProjectNameResolver.ResolveNetdiskFolder(task.Path, task.Name);
            if (!string.IsNullOrWhiteSpace(folder)) return folder;
            return string.Empty;
        }

        private static string BuildMessage(AdriveTaskRecord task, string status)
        {
            var direction = task.Direction == AdriveTransferDirection.Upload ? "\u4e0a\u4f20" : "\u4e0b\u8f7d";
            var state = status == "done" ? "\u5df2\u5b8c\u6210" :
                status == "failed" ? "\u5931\u8d25" :
                status == "paused" ? "\u5df2\u6682\u505c" :
                status == "in_progress" ? "\u8fdb\u884c\u4e2d" : "\u7b49\u5f85\u4e2d";
            var title = string.IsNullOrWhiteSpace(task.Name) ? task.TaskId : task.Name;
            if (title.Length > 80) title = title.Substring(0, 80);
            return "\u963f\u91cc\u4e91\u76d8" + direction + "\u4efb\u52a1\u300c" + title + "\u300d" + state;
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return ((int)value.TotalHours) + "时" + value.Minutes + "分";
            if (value.TotalMinutes >= 1) return ((int)value.TotalMinutes) + "分" + value.Seconds + "秒";
            return Math.Max(0, (int)value.TotalSeconds) + "秒";
        }
    }

    internal enum AdriveTransferDirection
    {
        Download,
        Upload
    }

    internal sealed class AdriveTaskRecord
    {
        internal AdriveTransferDirection Direction { get; set; }
        internal string TaskId { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal string Type { get; set; } = string.Empty;
        internal string Name { get; set; } = string.Empty;
        internal string Path { get; set; } = string.Empty;
        internal string StorageIdentity { get; set; } = string.Empty;
        internal long CreateTimeMs { get; set; }
        internal long EndTimeMs { get; set; }
        internal long DurationMs => CreateTimeMs > 0 && EndTimeMs >= CreateTimeMs ? EndTimeMs - CreateTimeMs : 0;
        internal string Identity => string.IsNullOrWhiteSpace(StorageIdentity) ?
            Direction.ToString() + "\u001f" + TaskId : StorageIdentity;
    }

    internal static class AdriveStatus
    {
        internal static string Map(string status)
        {
            var value = (status ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "SUCCESSED" || value == "SUCCEEDED" || value == "SUCCESS" ||
                value == "COMPLETED" || value == "DONE") return "done";
            if (value == "FAILED" || value == "FAILURE" || value == "ERROR") return "failed";
            if (value == "CANCELLED" || value == "CANCELED" || value == "CANCEL") return "cancelled";
            if (value == "SUSPENDED" || value == "PAUSED" || value == "PAUSE") return "paused";
            if (value == "IN_PROGRESS" || value == "VERIFYING" || value == "RUNNING" ||
                value == "UPLOADING" || value == "DOWNLOADING") return "in_progress";
            return "pending";
        }
    }

    internal static class AdriveTransferDataLocator
    {
        internal static IEnumerable<string> FindDatabases()
        {
            var explicitPaths = (Environment.GetEnvironmentVariable("TAPRELAY_ADRIVE_DB") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (explicitPaths.Length > 0) return explicitPaths;

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCandidate(roots, AppInstallRoots.Get("adrive"));
            AddCandidate(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aDrive"));
            AddCandidate(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aDrive"));

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var database in Directory.EnumerateDirectories(root, "*.indexeddb.leveldb", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFileName(database), "file__0.indexeddb.leveldb", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(Path.GetFullPath(database));
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        internal static IEnumerable<string> FindLogs()
        {
            var explicitPaths = (Environment.GetEnvironmentVariable("TAPRELAY_ADRIVE_LOG") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (explicitPaths.Length > 0) return explicitPaths;

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCandidate(roots, AppInstallRoots.Get("adrive"));
            AddCandidate(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aDrive"));
            AddCandidate(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aDrive"));

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var direct = Path.Combine(root, "logs", "main.log");
                if (File.Exists(direct)) result.Add(Path.GetFullPath(direct));
                try
                {
                    foreach (var log in Directory.EnumerateFiles(root, "main.log", SearchOption.AllDirectories))
                    {
                        result.Add(Path.GetFullPath(log));
                    }
                }
                catch { }
            }
            return result;
        }

        private static void AddCandidate(HashSet<string> roots, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path.Trim());
            if (string.Equals(Path.GetExtension(full), ".leveldb", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(Path.GetDirectoryName(full) ?? full);
            }
            else
            {
                roots.Add(full);
            }
        }
    }

    internal sealed class AdriveTransferLogEntry
    {
        internal DateTimeOffset Timestamp { get; set; }
        internal string Component { get; set; } = string.Empty;
        internal int Worker { get; set; }
        internal bool HasTaskCount { get; set; }
        internal int TaskCount { get; set; }
        internal string Operation { get; set; } = string.Empty;
        internal string Path { get; set; } = string.Empty;
    }

    internal static class AdriveTransferLogParser
    {
        internal static bool TryParse(string line, out AdriveTransferLogEntry entry)
        {
            entry = new AdriveTransferLogEntry();
            if (string.IsNullOrWhiteSpace(line)) return false;

            var timestampEnd = line.IndexOf(']');
            if (timestampEnd <= 1 || !DateTimeOffset.TryParse(
                    line.Substring(1, timestampEnd - 1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp)) return false;

            var componentName = string.Empty;
            var componentOffset = -1;
            foreach (var candidate in new[] { "Downloader", "RendererDownload" })
            {
                var marker = "][" + candidate + "]";
                var found = line.IndexOf(marker, timestampEnd, StringComparison.OrdinalIgnoreCase);
                if (found >= 0 && found > componentOffset)
                {
                    componentName = candidate;
                    componentOffset = found + marker.Length;
                }
            }
            if (componentOffset < 0) return false;
            var messageOffset = componentOffset;
            while (messageOffset < line.Length && char.IsWhiteSpace(line[messageOffset])) messageOffset++;

            if (string.Equals(componentName, "RendererDownload", StringComparison.OrdinalIgnoreCase))
            {
                var rendererJsonStart = line.IndexOf('{', messageOffset);
                var operationEnd = rendererJsonStart >= 0 ? rendererJsonStart : line.Length;
                var operation = line.Substring(messageOffset, operationEnd - messageOffset).Trim().ToLowerInvariant();
                if (operation.Length == 0) return false;
                var hasTaskCount = false;
                var taskCount = 0;
                if (rendererJsonStart >= 0)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line.Substring(rendererJsonStart));
                        var root = document.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("taskList", out var taskList) && taskList.TryGetInt32(out taskCount))
                        {
                            hasTaskCount = true;
                        }
                    }
                    catch (JsonException)
                    {
                        return false;
                    }
                }
                entry.Timestamp = timestamp;
                entry.Component = componentName;
                entry.Operation = operation;
                entry.HasTaskCount = hasTaskCount;
                entry.TaskCount = taskCount;
                return true;
            }

            if (messageOffset >= line.Length || line[messageOffset] != '#') return false;
            messageOffset++;

            var workerStart = messageOffset;
            while (messageOffset < line.Length && char.IsDigit(line[messageOffset])) messageOffset++;
            if (workerStart == messageOffset || !int.TryParse(line.Substring(workerStart, messageOffset - workerStart), NumberStyles.None, CultureInfo.InvariantCulture, out var worker)) return false;
            while (messageOffset < line.Length && char.IsWhiteSpace(line[messageOffset])) messageOffset++;
            var operationStart = messageOffset;
            while (messageOffset < line.Length && !char.IsWhiteSpace(line[messageOffset]) && line[messageOffset] != '{') messageOffset++;
            if (operationStart == messageOffset) return false;

            var path = string.Empty;
            var jsonStart = line.IndexOf('{', messageOffset);
            if (jsonStart >= 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(line.Substring(jsonStart));
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String)
                    {
                        path = pathValue.GetString() ?? string.Empty;
                    }
                }
                catch (JsonException)
                {
                    // Ignore a partially written line; the next poll will read the completed line.
                    return false;
                }
            }

            entry.Timestamp = timestamp;
            entry.Component = componentName;
            entry.Worker = worker;
            entry.Operation = line.Substring(operationStart, messageOffset - operationStart).Trim().ToLowerInvariant();
            entry.Path = path;
            return true;
        }

        internal static string GetDisplayName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var value = path.Trim();
            if (value.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 5);
            var slash = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
            return slash >= 0 && slash + 1 < value.Length ? value.Substring(slash + 1) : value;
        }
    }

    internal static class AdriveTransferDatabaseReader
    {
        private const int BlockSize = 32 * 1024;
        private const byte RecordFull = 1;
        private const byte RecordFirst = 2;
        private const byte RecordMiddle = 3;
        private const byte RecordLast = 4;
        private const int TableFooterLength = 48;
        private const int BlockTrailerLength = 5;
        private const ulong TableMagic = 0xdb4775248b80fb57UL;

        internal static Dictionary<string, AdriveTaskRecord> Read(string directory)
        {
            var versions = new Dictionary<string, VersionedTask>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) return new Dictionary<string, AdriveTaskRecord>(StringComparer.Ordinal);
            var tables = Directory.EnumerateFiles(directory, "*.ldb", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.sst", SearchOption.TopDirectoryOnly))
                .Select(path => new { Path = path, Number = ParseFileNumber(path) })
                .Where(item => item.Number >= 0)
                .OrderBy(item => item.Number)
                .Select(item => item.Path)
                .ToArray();
            foreach (var file in tables) ReadTable(file, versions);

            var files = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Number = ParseFileNumber(path) })
                .Where(item => item.Number >= 0)
                .OrderBy(item => item.Number)
                .Select(item => item.Path)
                .ToArray();
            foreach (var file in files) ReadLog(file, versions);

            var tasks = new Dictionary<string, AdriveTaskRecord>(StringComparer.Ordinal);
            foreach (var pair in versions)
            {
                if (!pair.Value.Deleted && pair.Value.Task != null)
                {
                    tasks[pair.Key] = pair.Value.Task;
                }
            }
            return tasks;
        }

        internal static bool IsDatabaseFile(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.Equals("CURRENT", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".sst", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetFileSignature(string directory)
        {
            if (!Directory.Exists(directory)) return string.Empty;
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsDatabaseFile(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder();
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    builder.Append(info.Name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('|');
                }
                catch { }
            }
            return builder.ToString();
        }

        private static void ReadLog(string path, Dictionary<string, VersionedTask> tasks)
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0 || stream.Length > int.MaxValue) return;
                bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count <= 0) break;
                    read += count;
                }
                if (read != bytes.Length) return;
            }
            catch { return; }

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
                if (offset + 7 + length > blockEnd || offset + 7 + length > bytes.Length) break;
                var payload = new byte[length];
                Buffer.BlockCopy(bytes, offset + 7, payload, 0, length);
                offset += 7 + length;

                if (type == RecordFull)
                {
                    ApplyWriteBatch(payload, tasks);
                }
                else if (type == RecordFirst)
                {
                    fragmented.SetLength(0);
                    fragmented.Write(payload, 0, payload.Length);
                }
                else if (type == RecordMiddle)
                {
                    fragmented.Write(payload, 0, payload.Length);
                }
                else if (type == RecordLast)
                {
                    fragmented.Write(payload, 0, payload.Length);
                    ApplyWriteBatch(fragmented.ToArray(), tasks);
                    fragmented.SetLength(0);
                }
            }
        }

        private static void ReadTable(string path, Dictionary<string, VersionedTask> tasks)
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < TableFooterLength || stream.Length > int.MaxValue) return;
                bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count <= 0) return;
                    read += count;
                }
            }
            catch
            {
                return;
            }

            var footerOffset = bytes.Length - TableFooterLength;
            if (ReadUInt64(bytes, footerOffset + 40) != TableMagic) return;

            var handleOffset = footerOffset + 20;
            if (!TryReadBlockHandle(bytes, ref handleOffset, out var indexOffset, out var indexSize)) return;
            var indexBlock = ReadBlock(bytes, indexOffset, indexSize);
            if (indexBlock == null) return;

            foreach (var indexEntry in ReadBlockEntries(indexBlock))
            {
                var blockOffset = 0;
                if (!TryReadBlockHandle(indexEntry.Value, ref blockOffset, out var dataOffset, out var dataSize)) continue;
                var dataBlock = ReadBlock(bytes, dataOffset, dataSize);
                if (dataBlock == null) continue;

                foreach (var entry in ReadBlockEntries(dataBlock))
                {
                    if (entry.Key.Length <= 8) continue;
                    var internalKeyLength = entry.Key.Length - 8;
                    var internalType = entry.Key[internalKeyLength] & 0xff;
                    var sequence = ReadUInt64(entry.Key, internalKeyLength) >> 8;
                    var userKey = new byte[internalKeyLength];
                    Buffer.BlockCopy(entry.Key, 0, userKey, 0, internalKeyLength);
                    if (!TryGetTaskIdentity(userKey, out var direction, out var taskKey)) continue;

                    var identity = direction + "\u001f" + taskKey;
                    if (internalType == 0)
                    {
                        ApplyVersion(tasks, identity, sequence, null, true);
                    }
                    else if (internalType == 1 && AdriveTaskValueReader.TryRead(entry.Value, direction, taskKey, out var task))
                    {
                        // The IndexedDB key is the stable record identity. New aDrive
                        // builds can store a different taskId inside the value.
                        task.StorageIdentity = identity;
                        ApplyVersion(tasks, identity, sequence, task, false);
                    }
                }
            }
        }

        private static void ApplyVersion(
            Dictionary<string, VersionedTask> tasks,
            string identity,
            ulong sequence,
            AdriveTaskRecord? task,
            bool deleted)
        {
            if (!tasks.TryGetValue(identity, out var current) || sequence >= current.Sequence)
            {
                tasks[identity] = new VersionedTask(sequence, task, deleted);
            }
        }

        private static byte[]? ReadBlock(byte[] table, ulong offset, ulong size)
        {
            if (offset > int.MaxValue || size > int.MaxValue || size > (ulong)table.Length ||
                offset + size + BlockTrailerLength > (ulong)table.Length)
            {
                return null;
            }

            var data = new byte[(int)size];
            Buffer.BlockCopy(table, (int)offset, data, 0, (int)size);
            var compression = table[(int)(offset + size)];
            if (compression == 0)
            {
                return data;
            }
            if (compression == 1 && TryDecompressSnappy(data, out var decompressed))
            {
                return decompressed;
            }
            return null;
        }

        private static IEnumerable<BlockEntry> ReadBlockEntries(byte[] block)
        {
            if (block.Length < 4) yield break;
            var restartCount = ReadUInt32(block, block.Length - 4);
            if (restartCount > (block.Length - 4) / 4) yield break;
            var entriesEnd = block.Length - 4 - (int)restartCount * 4;
            var offset = 0;
            var previousKey = Array.Empty<byte>();
            while (offset < entriesEnd)
            {
                if (!TryReadVarint(block, ref offset, out var shared) ||
                    !TryReadVarint(block, ref offset, out var nonShared) ||
                    !TryReadVarint(block, ref offset, out var valueLength) ||
                    shared < 0 || nonShared < 0 || valueLength < 0 || shared > previousKey.Length ||
                    offset + nonShared + valueLength > entriesEnd)
                {
                    yield break;
                }

                var key = new byte[shared + nonShared];
                if (shared > 0) Buffer.BlockCopy(previousKey, 0, key, 0, shared);
                Buffer.BlockCopy(block, offset, key, shared, nonShared);
                offset += nonShared;
                var value = new byte[valueLength];
                Buffer.BlockCopy(block, offset, value, 0, valueLength);
                offset += valueLength;
                previousKey = key;
                yield return new BlockEntry(key, value);
            }
        }

        private static bool TryReadBlockHandle(byte[] input, ref int offset, out ulong blockOffset, out ulong blockSize)
        {
            blockOffset = 0;
            blockSize = 0;
            return TryReadVarint64(input, ref offset, out blockOffset) &&
                   TryReadVarint64(input, ref offset, out blockSize);
        }

        private static bool TryDecompressSnappy(byte[] input, out byte[] output)
        {
            output = Array.Empty<byte>();
            var offset = 0;
            if (!TryReadVarint64(input, ref offset, out var expectedLength) || expectedLength > int.MaxValue)
            {
                return false;
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
                        if (extraBytes > 4 || offset + extraBytes > input.Length) return false;
                        for (var index = 0; index < extraBytes; index++)
                        {
                            length |= input[offset++] << (index * 8);
                        }
                        length++;
                    }
                    if (length < 0 || offset + length > input.Length || written + length > result.Length) return false;
                    Buffer.BlockCopy(input, offset, result, written, length);
                    offset += length;
                    written += length;
                    continue;
                }

                var copyLength = type == 1 ? ((tag >> 2) & 0x07) + 4 : ((tag >> 2) & 0x3f) + 1;
                var copyOffset = 0;
                if (type == 1)
                {
                    if (offset >= input.Length) return false;
                    copyOffset = ((tag >> 5) << 8) | input[offset++];
                }
                else if (type == 2)
                {
                    if (offset + 2 > input.Length) return false;
                    copyOffset = input[offset] | input[offset + 1] << 8;
                    offset += 2;
                }
                else
                {
                    if (offset + 4 > input.Length) return false;
                    copyOffset = input[offset] | input[offset + 1] << 8 | input[offset + 2] << 16 | input[offset + 3] << 24;
                    offset += 4;
                }

                if (copyOffset <= 0 || copyOffset > written || written + copyLength > result.Length) return false;
                for (var index = 0; index < copyLength; index++)
                {
                    result[written] = result[written - copyOffset];
                    written++;
                }
            }

            if (written != result.Length) return false;
            output = result;
            return true;
        }

        private static void ApplyWriteBatch(byte[] batch, Dictionary<string, VersionedTask> tasks)
        {
            if (batch.Length < 12) return;
            var offset = 12;
            var count = ReadUInt32(batch, 8);
            for (var index = 0; index < count && offset < batch.Length; index++)
            {
                var sequence = ReadUInt64(batch, 0) + (ulong)index;
                var operation = batch[offset++];
                if (!TryReadVarint(batch, ref offset, out var keyLength) || keyLength < 0 || offset + keyLength > batch.Length) return;
                var key = new byte[keyLength];
                Buffer.BlockCopy(batch, offset, key, 0, keyLength);
                offset += keyLength;
                if (!TryGetTaskIdentity(key, out var direction, out var taskKey))
                {
                    if (operation == 1 && !TrySkipValue(batch, ref offset)) return;
                    continue;
                }

                var identity = direction + "\u001f" + taskKey;
                if (operation == 0)
                {
                    ApplyVersion(tasks, identity, sequence, null, true);
                    continue;
                }
                if (operation != 1 || !TryReadBytes(batch, ref offset, out var value)) return;
                if (AdriveTaskValueReader.TryRead(value, direction, taskKey, out var task))
                {
                    // Keep updates and deletes on the same identity even when the
                    // value's taskId differs from the IndexedDB key.
                    task.StorageIdentity = identity;
                    ApplyVersion(tasks, identity, sequence, task, false);
                }
            }
        }

        private static bool TryGetTaskIdentity(byte[] key, out AdriveTransferDirection direction, out string taskId)
        {
            direction = AdriveTransferDirection.Download;
            taskId = string.Empty;
            if (key.Length < 6 || key[0] != 0 || key[3] != 1) return false;

            // Older aDrive builds used object-store prefix 00 01 and encoded
            // direction in the third byte. Current builds use object stores
            // 00 02 (upload) and 00 03 (download). Keep both formats because
            // the same profile can retain records written by either version.
            if (key[1] == 1)
            {
                if (key[2] == 1) direction = AdriveTransferDirection.Download;
                else if (key[2] == 2) direction = AdriveTransferDirection.Upload;
                else return false;
            }
            else if (key[1] == 2)
            {
                direction = AdriveTransferDirection.Upload;
            }
            else if (key[1] == 3)
            {
                direction = AdriveTransferDirection.Download;
            }
            else
            {
                return false;
            }

            return TryDecodeIndexedDbKey(key, 4, out taskId);
        }

        private static bool TryDecodeIndexedDbKey(byte[] key, int offset, out string value)
        {
            value = string.Empty;
            if (offset >= key.Length) return false;
            var type = key[offset++];
            if (type == 0x0A && offset + 1 < key.Length && (key.Length - offset) % 2 == 0)
            {
                var chars = new char[(key.Length - offset) / 2];
                for (var i = 0; i < chars.Length; i++) chars[i] = (char)(key[offset + i * 2] << 8 | key[offset + i * 2 + 1]);
                value = new string(chars);
                return value.Length > 0;
            }
            value = BitConverter.ToString(key, offset - 1, key.Length - offset + 1).Replace("-", string.Empty);
            return true;
        }

        private static bool TryReadBytes(byte[] input, ref int offset, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TryReadVarint(input, ref offset, out var length) || length < 0 || offset + length > input.Length) return false;
            value = new byte[length];
            Buffer.BlockCopy(input, offset, value, 0, length);
            offset += length;
            return true;
        }

        private static bool TrySkipValue(byte[] input, ref int offset)
        {
            return TryReadBytes(input, ref offset, out _);
        }

        private static bool TryReadVarint(byte[] input, ref int offset, out int value)
        {
            value = 0;
            var shift = 0;
            while (offset < input.Length && shift < 32)
            {
                var part = input[offset++];
                value |= (part & 0x7f) << shift;
                if ((part & 0x80) == 0) return true;
                shift += 7;
            }
            value = 0;
            return false;
        }

        private static uint ReadUInt32(byte[] input, int offset)
        {
            return (uint)(input[offset] | input[offset + 1] << 8 | input[offset + 2] << 16 | input[offset + 3] << 24);
        }

        private static ulong ReadUInt64(byte[] input, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; index++) value |= (ulong)input[offset + index] << (index * 8);
            return value;
        }

        private static bool TryReadVarint64(byte[] input, ref int offset, out ulong value)
        {
            value = 0;
            var shift = 0;
            while (offset < input.Length && shift < 64)
            {
                var part = input[offset++];
                value |= (ulong)(part & 0x7f) << shift;
                if ((part & 0x80) == 0) return true;
                shift += 7;
            }
            value = 0;
            return false;
        }

        private static int ParseFileNumber(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
        }

        private sealed class VersionedTask
        {
            internal VersionedTask(ulong sequence, AdriveTaskRecord? task, bool deleted)
            {
                Sequence = sequence;
                Task = task;
                Deleted = deleted;
            }

            internal ulong Sequence { get; }
            internal AdriveTaskRecord? Task { get; }
            internal bool Deleted { get; }
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
    }

    internal static class AdriveTaskValueReader
    {
        internal static bool TryRead(byte[] bytes, AdriveTransferDirection direction, string fallbackId, out AdriveTaskRecord task)
        {
            task = new AdriveTaskRecord { Direction = direction, TaskId = fallbackId };
            var objectStart = Array.IndexOf(bytes, (byte)0x6f);
            if (objectStart < 0) return false;
            var offset = objectStart + 1;
            var foundStatus = false;
            while (offset < bytes.Length)
            {
                if (bytes[offset] == 0x7b) break;
                if (bytes[offset] != 0x22)
                {
                    offset++;
                    continue;
                }
                if (!TryReadString(bytes, ref offset, out var name)) return false;
                if (!TryReadScalar(bytes, ref offset, out var scalar)) return false;
                switch (name)
                {
                    case "taskId": task.TaskId = scalar.StringValue ?? scalar.NumberValue.ToString(CultureInfo.InvariantCulture); break;
                    case "status": task.Status = scalar.StringValue ?? string.Empty; foundStatus = task.Status.Length > 0; break;
                    case "taskStatus": task.Status = scalar.StringValue ?? string.Empty; foundStatus = task.Status.Length > 0; break;
                    case "type": task.Type = scalar.StringValue ?? string.Empty; break;
                    case "fileName":
                        if (string.IsNullOrWhiteSpace(task.Name)) task.Name = scalar.StringValue ?? string.Empty;
                        break;
                    case "name":
                        if (string.IsNullOrWhiteSpace(task.Name)) task.Name = scalar.StringValue ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(task.Path) &&
                            !string.IsNullOrWhiteSpace(task.Name) &&
                            (task.Name.IndexOf('\\') >= 0 || task.Name.IndexOf('/') >= 0))
                        {
                            task.Path = task.Name;
                            task.Name = AdriveTransferLogParser.GetDisplayName(task.Path);
                        }
                        break;
                    case "path":
                    case "filePath":
                        if (string.IsNullOrWhiteSpace(task.Path)) task.Path = scalar.StringValue ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(task.Name)) task.Name = AdriveTransferLogParser.GetDisplayName(task.Path);
                        break;
                    case "createTime":
                    case "createdTime": task.CreateTimeMs = ToUnixMilliseconds(scalar.NumberValue); break;
                    case "endTime":
                    case "finishTime":
                    case "updateTime": task.EndTimeMs = Math.Max(task.EndTimeMs, ToUnixMilliseconds(scalar.NumberValue)); break;
                }
            }
            return foundStatus && !string.IsNullOrWhiteSpace(task.TaskId);
        }

        private static bool TryReadString(byte[] bytes, ref int offset, out string value)
        {
            value = string.Empty;
            if (offset >= bytes.Length || bytes[offset++] != 0x22) return false;
            if (!TryReadVarint(bytes, ref offset, out var length) || length < 0 || offset + length > bytes.Length) return false;
            value = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
            return true;
        }

        private static bool TryReadScalar(byte[] bytes, ref int offset, out ScalarValue value)
        {
            value = new ScalarValue();
            if (offset >= bytes.Length) return false;
            var tag = bytes[offset++];
            if (tag == 0x22)
            {
                offset--;
                if (!TryReadString(bytes, ref offset, out var text)) return false;
                value.StringValue = text;
                return true;
            }
            if (tag == 0x53)
            {
                if (!TryReadVarint(bytes, ref offset, out var utf8Length) || utf8Length < 0 || offset + utf8Length > bytes.Length) return false;
                value.StringValue = Encoding.UTF8.GetString(bytes, offset, utf8Length);
                offset += utf8Length;
                return true;
            }
            if (tag == 0x63)
            {
                // aDrive stores this structured-clone string length in bytes,
                // although the payload itself is UTF-16LE.
                if (!TryReadVarint(bytes, ref offset, out var utf16ByteLength) ||
                    utf16ByteLength < 0 || (utf16ByteLength & 1) != 0 || utf16ByteLength > bytes.Length - offset) return false;
                value.StringValue = Encoding.Unicode.GetString(bytes, offset, utf16ByteLength);
                offset += utf16ByteLength;
                return true;
            }
            if (tag == 0x4e && offset + 8 <= bytes.Length)
            {
                value.NumberValue = BitConverter.ToDouble(bytes, offset);
                offset += 8;
                return true;
            }
            if (tag == 0x49 || tag == 0x55)
            {
                if (!TryReadVarint(bytes, ref offset, out var encoded)) return false;
                value.NumberValue = tag == 0x49 ? (encoded >> 1) ^ -(encoded & 1) : encoded;
                return true;
            }
            if (tag == 0x44 && offset + 8 <= bytes.Length)
            {
                value.NumberValue = BitConverter.ToDouble(bytes, offset);
                offset += 8;
                return true;
            }
            if (tag == 0x54 || tag == 0x46 || tag == 0x30 || tag == 0x5f) return true;
            if (tag == 0x6f) return SkipObject(bytes, ref offset);
            if (tag == 0x41) return SkipDenseArray(bytes, ref offset);

            // Newer aDrive builds may add a structured-clone field that is not
            // relevant to task state. Keep scanning so taskId/status later in the
            // object can still be observed instead of dropping the whole record.
            return true;
        }

        private static bool SkipObject(byte[] bytes, ref int offset)
        {
            while (offset < bytes.Length)
            {
                if (bytes[offset] == 0x7b)
                {
                    offset++;
                    return true;
                }
                if (bytes[offset] != 0x22)
                {
                    offset++;
                    continue;
                }
                if (!TryReadString(bytes, ref offset, out _)) return false;
                if (!TryReadScalar(bytes, ref offset, out _)) return false;
            }
            return false;
        }

        private static bool SkipDenseArray(byte[] bytes, ref int offset)
        {
            if (!TryReadVarint(bytes, ref offset, out var count) || count < 0) return false;
            for (var index = 0; index < count; index++)
            {
                if (!TryReadScalar(bytes, ref offset, out _)) return false;
            }
            while (offset < bytes.Length && bytes[offset] != 0x24) offset++;
            if (offset < bytes.Length) offset++;
            return true;
        }

        private static bool TryReadVarint(byte[] input, ref int offset, out int value)
        {
            value = 0;
            var shift = 0;
            while (offset < input.Length && shift < 32)
            {
                var part = input[offset++];
                value |= (part & 0x7f) << shift;
                if ((part & 0x80) == 0) return true;
                shift += 7;
            }
            value = 0;
            return false;
        }

        private static long ToUnixMilliseconds(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return value >= 100000000000L ? (long)value : (long)(value * 1000D);
        }

        private sealed class ScalarValue
        {
            internal string? StringValue { get; set; }
            internal double NumberValue { get; set; }
        }
    }
}
