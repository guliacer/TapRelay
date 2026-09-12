using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace TapRelay
{
    /// <summary>
    /// Event-driven Quark Cloud Drive batch monitor. Quark keeps every transfer task
    /// (download/upload) in a single <c>download_task</c>/<c>upload_task</c> table forever
    /// and flips <c>status</c> to <c>FINISH</c> on completion; this monitor watches the
    /// SQLite files, waits for a 3-second settle window, and emits one aggregate event
    /// per batch when all newly-observed tasks reach a terminal status. It never polls
    /// individual tasks and never emits per-file alerts.
    /// </summary>
    internal sealed class QuarkNetdiskBatchMonitor : IDisposable
    {
        internal const string DownloadDbName = "download.db";
        internal const string UploadDbName = "upload.db";
        internal const string DownloadTableName = "download_task";
        internal const string UploadTableName = "upload_task";
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        // Keep this list aligned with tools/watch-adapted-processes.ps1. A
        // historical Quark database is not evidence that Quark is in use.
        internal static readonly string[] QuarkProcessNames =
        {
            "quark_cloud_drive",
            "QuarkCloudDrive"
        };
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

        private readonly Action<QuarkBatchCompletion> _completed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isQuarkProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DatabaseState> _states = new Dictionary<string, DatabaseState>(StringComparer.OrdinalIgnoreCase);
        private NonReentrantMonitorTimer? _pollTimer;
        private bool _quarkProcessRunning;
        private bool _disposed;

        internal QuarkNetdiskBatchMonitor(
            Action<QuarkBatchCompletion> completed,
            Action<string> log,
            Func<bool>? isQuarkProcessRunning = null)
        {
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isQuarkProcessRunning = isQuarkProcessRunning ?? DetectQuarkProcess;
        }

        internal void Start()
        {
            var processRunning = IsQuarkProcessRunningNow();
            lock (_gate)
            {
                if (_disposed) return;
                _quarkProcessRunning = processRunning;
            }

            if (processRunning)
            {
                StartDatabaseMonitoring(writeStatusLog: true);
            }
            else
            {
                _log("\u672a\u68c0\u6d4b\u5230\u5938\u514b\u7f51\u76d8\u8fdb\u7a0b\uff0c\u6682\u4e0d\u542f\u52a8\u4efb\u52a1\u901a\u77e5\u76d1\u63a7\uff1b\u542f\u52a8\u540e\u5c06\u81ea\u52a8\u63a5\u5165");
            }

            lock (_gate)
            {
                if (_disposed) return;
                // This timer tracks process usage. Database polling is enabled
                // only while the adapted application is running.
                _pollTimer = new NonReentrantMonitorTimer(
                    PollMonitor,
                    ProcessRecoveryInterval,
                    ProcessRecoveryInterval,
                    error => _log("夸克网盘进程恢复检查失败：" + error.Message));
            }
        }

        internal void Refresh()
        {
            if (UpdateProcessState()) return;

            bool processRunning;
            lock (_gate) processRunning = !_disposed && _quarkProcessRunning;
            if (!processRunning) return;

            StartDatabaseMonitoring(writeStatusLog: false);
        }

        private void PollMonitor()
        {
            UpdateProcessState();

            bool processRunning;
            lock (_gate) processRunning = !_disposed && _quarkProcessRunning;
            if (processRunning)
            {
                PollFileChanges();
            }
        }

        private bool UpdateProcessState()
        {
            var processRunning = IsQuarkProcessRunningNow();
            bool changed;
            lock (_gate)
            {
                if (_disposed) return false;
                changed = _quarkProcessRunning != processRunning;
                if (!changed) return false;

                _quarkProcessRunning = processRunning;
                if (!processRunning)
                {
                    StopDatabaseMonitoringLocked();
                }
            }

            if (processRunning)
            {
                _log("\u68c0\u6d4b\u5230\u5938\u514b\u7f51\u76d8\u8fdb\u7a0b\u5df2\u542f\u52a8\uff0c\u5f00\u59cb\u4efb\u52a1\u901a\u77e5\u76d1\u63a7");
                StartDatabaseMonitoring(writeStatusLog: true);
            }
            else
            {
                _log("\u68c0\u6d4b\u5230\u5938\u514b\u7f51\u76d8\u8fdb\u7a0b\u5df2\u9000\u51fa\uff0c\u5df2\u505c\u6b62\u4efb\u52a1\u901a\u77e5\u76d1\u63a7");
            }

            return true;
        }

        private void StartDatabaseMonitoring(bool writeStatusLog)
        {
            if (!IsQuarkProcessRunningNow())
            {
                UpdateProcessState();
                return;
            }

            var databases = QuarkNetdiskDataLocator.FindDatabases()
                .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
            foreach (var database in databases) AddDatabase(database.Path, database.Direction);

            if (!writeStatusLog) return;

            if (databases.Length == 0)
            {
                _log("未找到夸克网盘任务数据库；夸克网盘登录并创建任务后重启 TapRelay 可自动接入");
            }
            else
            {
                var downloadCount = databases.Count(d => d.Direction == QuarkDirection.Download);
                var uploadCount = databases.Count(d => d.Direction == QuarkDirection.Upload);
                _log("已启用夸克网盘批量任务结束监听（FileSystemWatcher + 15秒轮询兜底）：下载库 " + downloadCount + " 个，上传库 " + uploadCount + " 个");
            }
        }

        private bool IsQuarkProcessRunningNow()
        {
            try
            {
                return _isQuarkProcessRunning();
            }
            catch
            {
                // An inaccessible process query is not evidence that Quark is
                // active; fail closed so stale files cannot trigger a notice.
                return false;
            }
        }

        private static bool DetectQuarkProcess()
        {
            foreach (var processName in QuarkProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited) return true;
                        }
                        catch
                        {
                            // A process that cannot be queried is not treated
                            // as active for notification purposes.
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 轮询所有已接入数据库文件（含 -wal / -shm）的 LastWriteTime，
        /// 作为 FileSystemWatcher 在 SQLite WAL 模式下漏事件的兜底。
        /// 只 stat 文件，不读数据库内容，开销极低。
        /// </summary>
        private void PollFileChanges()
        {
            List<DatabaseState>? statesToCheck = null;
            lock (_gate)
            {
                if (_disposed || !_quarkProcessRunning || _states.Count == 0) return;
                statesToCheck = _states.Values.ToList();
            }
            foreach (var state in statesToCheck)
            {
                try
                {
                    // 检查主 db + WAL + SHM 三个文件，取最新 LastWriteTime
                    DateTime latest = DateTime.MinValue;
                    foreach (var file in new[] { state.Path, state.Path + "-wal", state.Path + "-shm" })
                    {
                        if (File.Exists(file))
                        {
                            var write = File.GetLastWriteTimeUtc(file);
                            if (write > latest) latest = write;
                        }
                    }
                    if (latest == DateTime.MinValue) continue;

                    lock (_gate)
                    {
                        if (_disposed || !_quarkProcessRunning ||
                            !_states.TryGetValue(state.Path, out var current) ||
                            !ReferenceEquals(current, state)) return;
                        if (latest > state.LastFileWriteUtc)
                        {
                            _log("轮询检测到夸克网盘数据库文件变化：" + state.Path + "（" + state.LastFileWriteUtc.ToString("HH:mm:ss") + " -> " + latest.ToString("HH:mm:ss") + "）");
                            state.LastFileWriteUtc = latest;
                            Signal(state);
                        }
                    }
                }
                catch
                {
                    // 单个文件检查失败不应中断其他文件的轮询
                }
            }
        }

        private void AddDatabase(string databasePath, QuarkDirection direction)
        {
            if (!File.Exists(databasePath)) return;
            var fullPath = Path.GetFullPath(databasePath);
            lock (_gate)
            {
                if (_disposed || !_quarkProcessRunning || _states.ContainsKey(fullPath)) return;
                try
                {
                    var tableName = direction == QuarkDirection.Download ? DownloadTableName : UploadTableName;
                    var snapshot = QuarkNetdiskDatabaseReader.Read(fullPath, tableName, 0);
                    // 启动时把 baseline 设为「所有已完成任务中最大的 createTime」，而不是
                    // 「所有任务中最大的 createTime」。否则启动前已创建但未完成的任务
                    // (createTime <= baseline) 会被 Read 的 WHERE createTime > $baseline 过滤掉，
                    // 这些任务完成时永远不触发通知。这里只把 terminal 任务的最大 createTime 作为
                    // baseline，running 任务的 createTime 都 > baseline，会被监控到。
                    var initialBaseline = snapshot.NewTasks
                        .Where(t => IsTerminalStatus(t.Status))
                        .Select(t => t.CreateMs)
                        .DefaultIfEmpty(0)
                        .Max();
                    var state = new DatabaseState(fullPath, direction, initialBaseline);
                    // 夸克网盘 SQLite 用 WAL 模式：写入只更新 download.db-wal，主 download.db
                    // 只在 checkpoint 时才更新。FileSystemWatcher 若只监控主 db 文件，任务状态
                    // 变化永远不触发 Changed 事件。这里改为监控整个目录，在事件处理中按文件名
                    // 前缀过滤（download.db / download.db-wal / download.db-shm 都会触发）。
                    var baseName = Path.GetFileNameWithoutExtension(fullPath);
                    state.Watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath) ?? ".")
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };
                    FileSystemEventHandler onChange = (s, e) =>
                    {
                        if (e.Name != null && e.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        {
                            Signal(state);
                        }
                    };
                    RenamedEventHandler onRename = (s, e) =>
                    {
                        if (e.Name != null && e.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        {
                            Signal(state);
                        }
                    };
                    state.Watcher.Changed += onChange;
                    state.Watcher.Created += onChange;
                    state.Watcher.Renamed += onRename;
                    state.Watcher.Error += (_, __) => Signal(state);
                    state.Watcher.EnableRaisingEvents = true;
                    // 初始化文件写入时间基线，避免启动后第一次轮询立即误触发
                    DateTime initLatest = DateTime.MinValue;
                    foreach (var file in new[] { fullPath, fullPath + "-wal", fullPath + "-shm" })
                    {
                        if (File.Exists(file))
                        {
                            var write = File.GetLastWriteTimeUtc(file);
                            if (write > initLatest) initLatest = write;
                        }
                    }
                    state.LastFileWriteUtc = initLatest;
                    _states.Add(fullPath, state);
                    _log("夸克网盘任务库已接入：" + fullPath + "（baseline=" + initialBaseline + "，监控文件前缀=" + baseName + "）");
                }
                catch (Exception error)
                {
                    _log("夸克网盘任务库接入失败：" + fullPath + "；" + error.Message);
                }
            }
        }

        private void Signal(DatabaseState state)
        {
            lock (_gate)
            {
                if (_disposed || !_quarkProcessRunning ||
                    !_states.TryGetValue(state.Path, out var current) ||
                    !ReferenceEquals(current, state)) return;
                state.LastSignalUtc = DateTime.UtcNow;
                state.Timer?.Dispose();
                state.Timer = new Timer(_ => ProcessStableChange(state), null, SettleDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void ProcessStableChange(DatabaseState state)
        {
            try
            {
                if (!IsProcessActiveForNotification()) return;

                QuarkBatchCompletion? completion = null;
                lock (_gate)
                {
                    if (_disposed || !_quarkProcessRunning || state.Processing ||
                        !_states.TryGetValue(state.Path, out var current) ||
                        !ReferenceEquals(current, state)) return;
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
                    var tableName = state.Direction == QuarkDirection.Download ? DownloadTableName : UploadTableName;
                    var snapshot = QuarkNetdiskDatabaseReader.Read(state.Path, tableName, state.BaselineCreateMs);
                    lock (_gate)
                    {
                        if (_disposed || !_quarkProcessRunning ||
                            !_states.TryGetValue(state.Path, out var current) ||
                            !ReferenceEquals(current, state)) return;
                        completion = ApplySnapshot(state, snapshot);
                    }
                }
                finally
                {
                    lock (_gate) state.Processing = false;
                }

                if (completion != null && IsProcessActiveForNotification()) _completed(completion);
            }
            catch (Exception error)
            {
                _log("夸克网盘队列事件解析失败：" + error.Message);
            }
        }

        private bool IsProcessActiveForNotification()
        {
            if (!IsQuarkProcessRunningNow())
            {
                UpdateProcessState();
                return false;
            }

            lock (_gate) return !_disposed && _quarkProcessRunning;
        }

        private static QuarkBatchCompletion? ApplySnapshot(DatabaseState state, QuarkDatabaseSnapshot snapshot)
        {
            // No new tasks since baseline -> nothing to notify. Keep baseline stable so
            // we do not accidentally swallow tasks created later in the same session.
            if (snapshot.NewTasks.Count == 0)
            {
                return null;
            }

            // Paused/suspended tasks are a deliberate user action: they must neither
            // block the rest of the batch forever nor be counted as failures. Any other
            // non-terminal status (RUNNING, queued, unknown) still blocks until settled.
            var runningCount = snapshot.NewTasks.Count(task => !IsTerminalStatus(task.Status) && !IsPausedStatus(task.Status));
            if (runningCount > 0)
            {
                return null;
            }

            var completedCount = snapshot.NewTasks.Count(task => IsSuccessStatus(task.Status));
            var failedCount = snapshot.NewTasks.Count(task => IsFailureStatus(task.Status));
            if (completedCount == 0 && failedCount == 0)
            {
                // Everything new was cancelled or paused. Cancellation is the user's own
                // action (never an alert, consistent with the agent parsers); a paused
                // task may resume later. Keep the baseline untouched so a resumed task
                // is still evaluated when it eventually reaches a terminal status.
                return null;
            }

            var countedTasks = snapshot.NewTasks
                .Where(task => IsSuccessStatus(task.Status) || IsFailureStatus(task.Status))
                .ToList();
            var startMs = countedTasks.Min(task => task.CreateMs);
            var endMs = countedTasks.Max(task => task.FinishMs > 0 ? task.FinishMs : task.UpdateMs);
            if (endMs <= 0) endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (startMs <= 0 || startMs > endMs) startMs = endMs;

            var result = new QuarkBatchCompletion(
                state.Direction,
                completedCount,
                failedCount,
                TimeSpan.FromMilliseconds(Math.Max(0, endMs - startMs)),
                BuildBatchId(state.Path, state.Direction, startMs, endMs, completedCount, failedCount),
                QuarkBatchCompletion.BuildBatchProjectName(snapshot.NewTasks));

            // Advance the baseline over terminal tasks only, so a paused task with a
            // newer createTime stays under watch until it reaches a terminal status.
            var terminalMaxCreateMs = snapshot.NewTasks
                .Where(task => IsTerminalStatus(task.Status))
                .Select(task => task.CreateMs)
                .DefaultIfEmpty(0)
                .Max();
            state.BaselineCreateMs = Math.Max(state.BaselineCreateMs, terminalMaxCreateMs);
            return result;
        }

        /// <summary>
        /// Quark confirmed status values: <c>RUNNING</c> (active) and <c>FINISH</c> (terminal-success),
        /// verified against the local <c>download.db</c> snapshot (6594 rows, all FINISH). Failure,
        /// cancel and pause states are not publicly documented; this defensive matcher treats
        /// FAIL/ERROR/EXCEPTION as terminal failure, CANCEL/DELETE as terminal user cancellation
        /// (never alerted, consistent with the agent parsers), PAUSE/STOP/SUSPEND as a user-paused
        /// state (skipped, batch not blocked), and every other value as still active so we never
        /// fire a premature completion notification for an unknown intermediate state.
        /// </summary>
        private static bool IsTerminalStatus(string status)
        {
            if (string.Equals(status, "FINISH", StringComparison.OrdinalIgnoreCase)) return true;
            return IsFailureStatus(status) || IsCancelStatus(status);
        }

        private static bool IsSuccessStatus(string status)
        {
            return string.Equals(status, "FINISH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFailureStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            var upper = status.ToUpperInvariant();
            if (IsCancelStatus(status)) return false;
            return upper.Contains("FAIL") || upper.Contains("ERROR") || upper.Contains("EXCEPTION");
        }

        private static bool IsCancelStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            var upper = status.ToUpperInvariant();
            return upper.Contains("CANCEL") || upper.Contains("DELETE");
        }

        private static bool IsPausedStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            var upper = status.ToUpperInvariant();
            return upper.Contains("PAUSE") || upper.Contains("STOP") || upper.Contains("SUSPEND");
        }

        private static string BuildBatchId(string path, QuarkDirection direction, long startMs, long endMs, int completed, int failed)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            var shortHash = BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            var prefix = direction == QuarkDirection.Download ? "quark-dl" : "quark-ul";
            return prefix + "-" + shortHash + "-" + startMs + "-" + endMs + "-" + completed + "-" + failed;
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
        }

        private void StopDatabaseMonitoringLocked()
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Dispose();
                state.Timer = null;
                state.Watcher?.Dispose();
                state.Watcher = null;
            }
            _states.Clear();
        }

        private sealed class DatabaseState
        {
            internal DatabaseState(string path, QuarkDirection direction, long baselineCreateMs)
            {
                Path = path;
                Direction = direction;
                BaselineCreateMs = baselineCreateMs;
            }
            internal string Path { get; }
            internal QuarkDirection Direction { get; }
            internal long BaselineCreateMs { get; set; }
            internal FileSystemWatcher? Watcher { get; set; }
            internal Timer? Timer { get; set; }
            internal DateTime LastSignalUtc { get; set; }
            /// <summary>轮询兜底用的文件 LastWriteTime 基线，避免重复触发。</summary>
            internal DateTime LastFileWriteUtc { get; set; }
            internal bool Processing { get; set; }
        }
    }

    internal enum QuarkDirection
    {
        Download,
        Upload
    }

    internal sealed class QuarkBatchCompletion
    {
        internal QuarkBatchCompletion(QuarkDirection direction, int completedCount, int failedCount, TimeSpan duration, string batchId, string projectName)
        {
            Direction = direction;
            CompletedCount = completedCount;
            FailedCount = failedCount;
            Duration = duration;
            BatchId = batchId;
            TaskProjectName = projectName ?? string.Empty;
        }
        internal QuarkDirection Direction { get; }
        internal int CompletedCount { get; }
        internal int FailedCount { get; }
        internal TimeSpan Duration { get; }
        internal string BatchId { get; }
        internal string TaskProjectName { get; }
        internal string ProjectName => (Direction == QuarkDirection.Download ? "下载" : "上传") + "批量任务";
        internal string Status => CompletedCount == 0 && FailedCount > 0 ? "failed" : "completed";
        internal string Source => Direction == QuarkDirection.Download ? "quarknetdisk" : "quarknetdisk-upload";
        internal string Message
        {
            get
            {
                var verb = Direction == QuarkDirection.Download ? "下载" : "上传";
                return "夸克网盘" + verb + "批量结束：完成 " + CompletedCount + " 个，失败 " + FailedCount + " 个，耗时 " + FormatDuration(Duration);
            }
        }
        internal static string BuildBatchProjectName(IEnumerable<QuarkTaskRow> tasks)
        {
            var names = (tasks ?? Array.Empty<QuarkTaskRow>())
                .Select(task => ProjectNameResolver.ResolveNetdiskFolder(task.Path, task.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            if (names.Length == 1) return names[0];
            if (names.Length > 1) return string.Join(", ", names) + (names.Length == 3 ? ", ..." : string.Empty);
            return "batch tasks";
        }
        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return ((int)value.TotalHours) + "时" + value.Minutes + "分";
            if (value.TotalMinutes >= 1) return ((int)value.TotalMinutes) + "分" + value.Seconds + "秒";
            return Math.Max(0, (int)value.TotalSeconds) + "秒";
        }
    }

    internal sealed class QuarkDatabaseSnapshot
    {
        internal List<QuarkTaskRow> NewTasks { get; } = new List<QuarkTaskRow>();
        internal long MaxCreateMs { get; set; }
    }

    internal sealed class QuarkTaskRow
    {
        internal string Id { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal long CreateMs { get; set; }
        internal long FinishMs { get; set; }
        internal long UpdateMs { get; set; }
        internal string Name { get; set; } = string.Empty;
        internal string Path { get; set; } = string.Empty;
    }

    internal static class QuarkNetdiskDatabaseReader
    {
        internal static QuarkDatabaseSnapshot Read(string path, string tableName, long createAfterMs)
        {
            var snapshot = new QuarkDatabaseSnapshot();
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var busy = connection.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout=2000"; busy.ExecuteNonQuery(); }
            if (!HasTable(connection, tableName))
                throw new InvalidDataException("夸克任务数据库缺少 " + tableName + " 表");

            using (var command = connection.CreateCommand())
            {
                // Exclude the synthetic MANAGER row (id='') that aggregates queue state;
                // it is not a real transfer task and would inflate batch counts.
                var nameColumn = HasColumn(connection, tableName, "name") ? "COALESCE(name,'')" : "''";
                var pathColumn = HasColumn(connection, tableName, "path") ? "COALESCE(path,'')" : "''";
                command.CommandText = "SELECT COALESCE(id,''), " + nameColumn + ", " + pathColumn + ", COALESCE(status,''), COALESCE(createTime,0), COALESCE(finishTime,0), COALESCE(updateTime,0) FROM " + tableName + " WHERE id != '' AND createTime > $baseline";
                command.Parameters.AddWithValue("$baseline", createAfterMs);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    snapshot.NewTasks.Add(new QuarkTaskRow
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Path = reader.GetString(2),
                        Status = reader.GetString(3),
                        CreateMs = reader.GetInt64(4),
                        FinishMs = reader.GetInt64(5),
                        UpdateMs = reader.GetInt64(6)
                    });
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COALESCE(MAX(createTime),0) FROM " + tableName + " WHERE id != ''";
                snapshot.MaxCreateMs = Convert.ToInt64(command.ExecuteScalar());
            }
            return snapshot;
        }

        private static bool HasTable(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }

        private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(" + tableName + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    internal static class QuarkNetdiskDataLocator
    {
        internal static IEnumerable<(string Path, QuarkDirection Direction)> FindDatabases()
        {
            // Explicit test override: semicolon-separated paths to specific .db files.
            // Direction is inferred from the file name (download.db / upload.db).
            var explicitPaths = (Environment.GetEnvironmentVariable("TAPRELAY_QUARK_DB") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
            var explicitDatabases = explicitPaths
                .Select(path => path.Trim())
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (explicitDatabases.Length > 0)
            {
                foreach (var database in explicitDatabases)
                {
                    var direction = TryDirectionFromName(database);
                    if (direction.HasValue) yield return (database, direction.Value);
                }
                yield break;
            }

            // Default discovery: %LOCALAPPDATA%\QuarkCloudDrive\User Data\persistence\<userId>\{download,upload}.db
            // plus running-process and uninstall-registry hints for non-standard installs.
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configuredRoot = AppInstallRoots.Get("quarknetdisk");
            Add(roots, configuredRoot);
            Add(roots, Path.Combine(configuredRoot ?? string.Empty, "User Data", "persistence"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuarkCloudDrive", "User Data", "persistence"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuarkCloudDrive", "User Data", "persistence"));
            AddInstallRoots(roots);
            try
            {
                foreach (var processName in new[] { "QuarkCloudDrive", "quark_cloud_drive" })
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            var dir = Path.GetDirectoryName(process.MainModule?.FileName);
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                Add(roots, Path.Combine(dir, "User Data", "persistence"));
                                Add(roots, Path.Combine(dir, "..", "User Data", "persistence"));
                            }
                        }
                        catch { }
                        finally { process.Dispose(); }
                    }
                }
            }
            catch { }

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] userDirs;
                try { userDirs = Directory.GetDirectories(root); }
                catch { continue; }
                foreach (var userDir in userDirs)
                {
                    foreach (var fileName in new[] { QuarkNetdiskBatchMonitor.DownloadDbName, QuarkNetdiskBatchMonitor.UploadDbName })
                    {
                        var file = Path.Combine(userDir, fileName);
                        if (File.Exists(file)) yield return (file, TryDirectionFromName(file)!.Value);
                    }
                }
            }
        }

        private static QuarkDirection? TryDirectionFromName(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, QuarkNetdiskBatchMonitor.DownloadDbName, StringComparison.OrdinalIgnoreCase)) return QuarkDirection.Download;
            if (string.Equals(name, QuarkNetdiskBatchMonitor.UploadDbName, StringComparison.OrdinalIgnoreCase)) return QuarkDirection.Upload;
            return null;
        }

        private static void AddInstallRoots(HashSet<string> roots)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var keyPath in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var root = hive.OpenSubKey(keyPath);
                    if (root == null) continue;
                    foreach (var name in root.GetSubKeyNames())
                    using (var app = root.OpenSubKey(name))
                    {
                        var displayName = Convert.ToString(app?.GetValue("DisplayName")) ?? string.Empty;
                        if (displayName.IndexOf("夸克网盘", StringComparison.OrdinalIgnoreCase) < 0 &&
                            displayName.IndexOf("QuarkCloudDrive", StringComparison.OrdinalIgnoreCase) < 0 &&
                            displayName.IndexOf("Quark Cloud Drive", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var install = Convert.ToString(app?.GetValue("InstallLocation")) ?? string.Empty;
                        var uninstall = (Convert.ToString(app?.GetValue("UninstallString")) ?? string.Empty).Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(uninstall)) install = Path.GetDirectoryName(uninstall) ?? string.Empty;
                        Add(roots, Path.Combine(install, "User Data", "persistence"));
                        // The user-data directory is always under %LOCALAPPDATA% regardless of install location;
                        // the registry hint is only a fallback signal that Quark is installed.
                        Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuarkCloudDrive", "User Data", "persistence"));
                    }
                }
                catch { }
            }
        }

        private static void Add(HashSet<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
    }
}
