using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace TapRelay
{
    /// <summary>
    /// Event-driven Baidu Netdisk batch monitor. It listens for SQLite database file changes and
    /// evaluates the queue as one aggregate; it never polls individual tasks or emits per-file alerts.
    /// </summary>
    internal sealed class BaiduNetdiskBatchMonitor : IDisposable
    {
        private const string DatabaseName = "BaiduYunGuanjia.db";
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
        private readonly Action<BaiduBatchCompletion> _completed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DatabaseState> _states = new Dictionary<string, DatabaseState>(StringComparer.OrdinalIgnoreCase);
        private NonReentrantMonitorTimer? _processTimer;
        private bool _processRunning;
        private bool _disposed;

        internal BaiduNetdiskBatchMonitor(
            Action<BaiduBatchCompletion> completed,
            Action<string> log,
            Func<bool>? isProcessRunning = null)
        {
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isProcessRunning = isProcessRunning ?? (() => AdaptedProcessGate.IsRunningForAppKey("baidunetdisk"));
        }

        internal void Start()
        {
            var processRunning = IsProcessRunningNow();
            lock (_gate)
            {
                if (_disposed) return;
                _processRunning = processRunning;
                _processTimer = new NonReentrantMonitorTimer(
                    PollProcessState,
                    ProcessRecoveryInterval,
                    ProcessRecoveryInterval,
                    error => _log("百度网盘进程恢复检查失败：" + error.Message));
            }

            if (!processRunning)
            {
                _log("BaiduNetdisk process is not running; task monitoring is paused until it starts");
                return;
            }

            var databases = BaiduNetdiskDataLocator.FindDatabases().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var database in databases)
            {
                AddDatabase(database);
            }

            if (databases.Length == 0)
            {
                _log("未找到百度网盘任务数据库；百度网盘登录并创建任务后重启 TapRelay 可自动接入");
            }
            else
            {
                _log("已启用百度网盘批量下载/上传结束监听（数据库变更事件驱动，不逐任务轮询）：" + databases.Length + " 个账户库");
            }
        }

        internal void Refresh()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            var databases = BaiduNetdiskDataLocator.FindDatabases().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var database in databases) AddDatabase(database);
        }

        private void PollProcessState()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            var known = 0;
            lock (_gate) known = _states.Count;
            if (known == 0) Refresh();
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
                if (!processRunning) StopDatabaseMonitoringLocked();
            }

            _log(processRunning
                ? "BaiduNetdisk process started; task monitoring resumed"
                : "BaiduNetdisk process stopped; task monitoring paused");
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
            if (!File.Exists(databasePath)) return;
            var fullPath = Path.GetFullPath(databasePath);
            lock (_gate)
            {
                if (_disposed || !_processRunning || _states.ContainsKey(fullPath)) return;
                try
                {
                    var kinds = BaiduNetdiskDatabaseReader.DiscoverKinds(fullPath);
                    if (kinds.Count == 0)
                    {
                        _log("百度网盘任务库无可监控的下载/上传表，跳过：" + fullPath);
                        return;
                    }

                    var state = new DatabaseState(fullPath);
                    foreach (var kind in kinds)
                    {
                        var snapshot = BaiduNetdiskDatabaseReader.Read(fullPath, kind, 0);
                        var queue = new QueueState(kind, snapshot.HistoryMaxId);
                        if (snapshot.Tasks.Count > 0)
                        {
                            queue.Batch = BatchState.Start(snapshot, queue.LastHistoryId);
                        }
                        state.Queues[kind] = queue;
                    }

                    state.Watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath) ?? ".", DatabaseName + "*")
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
                    _states.Add(fullPath, state);
                    _log("百度网盘任务库已接入（监控：" + string.Join("、", kinds) + "）：" + fullPath);
                }
                catch (Exception error)
                {
                    _log("百度网盘任务库接入失败：" + fullPath + "；" + error.Message);
                }
            }
        }

        private void Signal(DatabaseState state)
        {
            lock (_gate)
            {
                if (_disposed || !_processRunning) return;
                state.LastSignalUtc = DateTime.UtcNow;
                state.Timer?.Dispose();
                state.Timer = new Timer(_ => ProcessStableChange(state), null, SettleDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void ProcessStableChange(DatabaseState state)
        {
            if (!IsProcessActiveForNotification()) return;

            try
            {
                var completions = new List<BaiduBatchCompletion>();
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
                    // 每种任务类型（下载/上传/…）各自独立结算，互不影响。
                    foreach (var queue in state.Queues.Values.ToArray())
                    {
                        BaiduDatabaseSnapshot snapshot;
                        try
                        {
                            var baseline = queue.Batch?.BaselineHistoryId ?? queue.LastHistoryId;
                            snapshot = BaiduNetdiskDatabaseReader.Read(state.Path, queue.Kind, baseline);
                        }
                        catch (Exception error)
                        {
                            _log("百度网盘[" + queue.Kind + "]队列读取失败：" + error.Message);
                            continue;
                        }

                        lock (_gate)
                        {
                            if (_disposed) return;
                            var completion = ApplySnapshot(queue, snapshot, state.Path);
                            if (completion != null) completions.Add(completion);
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
                _log("百度网盘队列事件解析失败：" + error.Message);
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

        private static BaiduBatchCompletion? ApplySnapshot(QueueState state, BaiduDatabaseSnapshot snapshot, string path)
        {
            var allCurrentFailed = snapshot.Tasks.Count > 0 && snapshot.Tasks.All(task => task.ErrorCode != 0);
            var signature = snapshot.TaskSignature;

            if (state.Batch == null)
            {
                if (snapshot.Tasks.Count > 0)
                {
                    if (allCurrentFailed && signature == state.LastTerminalTaskSignature) return null;
                    state.Batch = BatchState.Start(snapshot, state.LastHistoryId);
                }
                else if (snapshot.HistoryMaxId > state.LastHistoryId)
                {
                    state.Batch = BatchState.Start(snapshot, state.LastHistoryId);
                }
                else
                {
                    state.LastHistoryId = Math.Max(state.LastHistoryId, snapshot.HistoryMaxId);
                    return null;
                }
            }

            var batch = state.Batch;
            batch.Update(snapshot);
            var runningCount = snapshot.Tasks.Count(task => task.ErrorCode == 0);
            if (runningCount > 0)
            {
                state.LastTerminalTaskSignature = string.Empty;
                return null;
            }

            var completedCount = snapshot.NewHistoryCount;
            var failedCount = batch.FailedTaskIds.Count;
            if (completedCount == 0 && failedCount == 0)
            {
                state.LastHistoryId = Math.Max(state.LastHistoryId, snapshot.HistoryMaxId);
                state.Batch = null;
                return null;
            }

            var startUnix = MinPositive(batch.StartUnix, snapshot.NewHistoryStartUnix);
            var endUnix = Math.Max(batch.EndUnix, snapshot.NewHistoryEndUnix);
            if (endUnix <= 0) endUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (startUnix <= 0 || startUnix > endUnix) startUnix = endUnix;

            var result = new BaiduBatchCompletion(
                state.Kind,
                completedCount,
                failedCount,
                TimeSpan.FromSeconds(Math.Max(0, endUnix - startUnix)),
                BuildBatchId(path, state.Kind, startUnix, snapshot.HistoryMaxId, completedCount, failedCount),
                batch.ProjectName(state.Kind));

            state.LastHistoryId = Math.Max(state.LastHistoryId, snapshot.HistoryMaxId);
            state.LastTerminalTaskSignature = allCurrentFailed ? signature : string.Empty;
            state.Batch = null;
            return result;
        }

        private static long MinPositive(long left, long right)
        {
            if (left <= 0) return right;
            if (right <= 0) return left;
            return Math.Min(left, right);
        }

        private static string BuildBatchId(string path, string kind, long startUnix, long historyId, int completed, int failed)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            var shortHash = BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            return "baidu-" + kind.ToLowerInvariant() + "-" + shortHash + "-" + startUnix + "-" + historyId + "-" + completed + "-" + failed;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _processTimer?.Dispose();
                _processTimer = null;
                StopDatabaseMonitoringLocked();
            }
        }

        private void StopDatabaseMonitoringLocked()
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Dispose();
                state.Watcher?.Dispose();
            }
            _states.Clear();
        }

        private sealed class DatabaseState
        {
            internal DatabaseState(string path) { Path = path; }
            internal string Path { get; }
            internal FileSystemWatcher? Watcher { get; set; }
            internal Timer? Timer { get; set; }
            internal DateTime LastSignalUtc { get; set; }
            internal bool Processing { get; set; }
            // 每种任务类型（download / upload / …）各自独立跟踪批次与历史基线。
            internal Dictionary<string, QueueState> Queues { get; } =
                new Dictionary<string, QueueState>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class QueueState
        {
            internal QueueState(string kind, long lastHistoryId) { Kind = kind; LastHistoryId = lastHistoryId; }
            internal string Kind { get; }
            internal long LastHistoryId { get; set; }
            internal string LastTerminalTaskSignature { get; set; } = string.Empty;
            internal BatchState? Batch { get; set; }
        }

        private sealed class BatchState
        {
            internal long BaselineHistoryId { get; private set; }
            internal long StartUnix { get; private set; }
            internal long EndUnix { get; private set; }
            internal HashSet<long> FailedTaskIds { get; } = new HashSet<long>();
            internal HashSet<string> ProjectNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal static BatchState Start(BaiduDatabaseSnapshot snapshot, long baseline)
            {
                var state = new BatchState { BaselineHistoryId = baseline };
                state.Update(snapshot);
                return state;
            }

            internal void Update(BaiduDatabaseSnapshot snapshot)
            {
                foreach (var task in snapshot.Tasks)
                {
                    if (task.ErrorCode != 0) FailedTaskIds.Add(task.TaskId);
                    StartUnix = MinPositive(StartUnix, task.AddTime);
                    EndUnix = Math.Max(EndUnix, task.StatusChangeTime);
                }
                StartUnix = MinPositive(StartUnix, snapshot.NewHistoryStartUnix);
                EndUnix = Math.Max(EndUnix, snapshot.NewHistoryEndUnix);
                foreach (var folder in snapshot.NewHistoryFolders)
                {
                    if (!string.IsNullOrWhiteSpace(folder)) ProjectNames.Add(folder);
                }
            }

            internal string ProjectName(string kind)
            {
                var names = ProjectNames.Take(3).ToArray();
                if (names.Length == 1) return names[0];
                if (names.Length > 1) return string.Join(", ", names) + (ProjectNames.Count > 3 ? ", ..." : string.Empty);
                return (string.Equals(kind, "upload", StringComparison.OrdinalIgnoreCase) ? "upload" : "download") + " batch tasks";
            }
        }
    }

    internal sealed class BaiduBatchCompletion
    {
        internal BaiduBatchCompletion(string kind, int completedCount, int failedCount, TimeSpan duration, string batchId, string projectName)
        { Kind = kind; CompletedCount = completedCount; FailedCount = failedCount; Duration = duration; BatchId = batchId; ProjectName = projectName ?? string.Empty; }
        internal string Kind { get; }
        internal int CompletedCount { get; }
        internal int FailedCount { get; }
        internal TimeSpan Duration { get; }
        internal string BatchId { get; }
        internal string ProjectName { get; }
        internal string Status => CompletedCount == 0 && FailedCount > 0 ? "failed" : "completed";
        private string KindLabel => string.Equals(Kind, "upload", StringComparison.OrdinalIgnoreCase) ? "上传"
            : string.Equals(Kind, "download", StringComparison.OrdinalIgnoreCase) ? "下载" : Kind;
        internal string Message => "百度网盘" + KindLabel + "批量结束：完成 " + CompletedCount + " 个，失败 " + FailedCount + " 个，耗时 " + FormatDuration(Duration);
        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return ((int)value.TotalHours) + "时" + value.Minutes + "分";
            if (value.TotalMinutes >= 1) return ((int)value.TotalMinutes) + "分" + value.Seconds + "秒";
            return Math.Max(0, (int)value.TotalSeconds) + "秒";
        }
    }

    internal sealed class BaiduDatabaseSnapshot
    {
        internal List<BaiduTaskRow> Tasks { get; } = new List<BaiduTaskRow>();
        internal long HistoryMaxId { get; set; }
        internal int NewHistoryCount { get; set; }
        internal long NewHistoryStartUnix { get; set; }
        internal long NewHistoryEndUnix { get; set; }
        internal List<string> NewHistoryFolders { get; } = new List<string>();
        internal string TaskSignature => string.Join(",", Tasks.Select(task => task.TaskId).OrderBy(id => id));
    }

    internal sealed class BaiduTaskRow
    {
        internal long TaskId { get; set; }
        internal long AddTime { get; set; }
        internal long StatusChangeTime { get; set; }
        internal long ErrorCode { get; set; }
    }

    internal static class BaiduNetdiskDatabaseReader
    {
        // 发现库中所有 "<kind>_file" + "<kind>_history_file" 成对的任务类型（如 download、upload）。
        // backup_file 没有对应的 backup_history_file，不构成批量任务队列，自动排除。
        internal static List<string> DiscoverKinds(string path)
        {
            var kinds = new List<string>();
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }

            const string suffix = "_history_file";
            foreach (var table in tables)
            {
                if (table.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var kind = table.Substring(0, table.Length - suffix.Length);
                    if (!string.IsNullOrEmpty(kind) && tables.Contains(kind + "_file"))
                    {
                        kinds.Add(kind.ToLowerInvariant());
                    }
                }
            }
            return kinds;
        }

        internal static BaiduDatabaseSnapshot Read(string path, string kind, long historyAfterId)
        {
            var taskTable = kind + "_file";
            var historyTable = kind + "_history_file";
            var snapshot = new BaiduDatabaseSnapshot();
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var busy = connection.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout=2000"; busy.ExecuteNonQuery(); }
            if (!HasTable(connection, taskTable) || !HasTable(connection, historyTable))
                throw new InvalidDataException("任务数据库缺少 " + taskTable + "/" + historyTable + " 表");

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT task_id, COALESCE(add_time,0), COALESCE(status_changetime,0), COALESCE(error_code,0) FROM " + taskTable;
                using var reader = command.ExecuteReader();
                while (reader.Read()) snapshot.Tasks.Add(new BaiduTaskRow { TaskId = reader.GetInt64(0), AddTime = reader.GetInt64(1), StatusChangeTime = reader.GetInt64(2), ErrorCode = reader.GetInt64(3) });
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COALESCE(MAX(id),0) FROM " + historyTable;
                snapshot.HistoryMaxId = Convert.ToInt64(command.ExecuteScalar());
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*), COALESCE(MIN(NULLIF(op_starttime,0)),0), COALESCE(MAX(op_endtime),0) FROM " + historyTable + " WHERE id > $id";
                command.Parameters.AddWithValue("$id", historyAfterId);
                using var reader = command.ExecuteReader();
                if (reader.Read()) { snapshot.NewHistoryCount = reader.GetInt32(0); snapshot.NewHistoryStartUnix = reader.GetInt64(1); snapshot.NewHistoryEndUnix = reader.GetInt64(2); }
            }
            using (var command = connection.CreateCommand())
            {
                var isDirectoryColumn = HasColumn(connection, historyTable, "isdir") ? "COALESCE(isdir,0)" : "0";
                command.CommandText = "SELECT COALESCE(server_path,''), COALESCE(local_path,''), " + isDirectoryColumn + " FROM " + historyTable + " WHERE id > $id";
                command.Parameters.AddWithValue("$id", historyAfterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var serverPath = reader.GetString(0);
                    var localPath = reader.GetString(1);
                    var isDirectory = reader.GetInt32(2) != 0;
                    var folder = ProjectNameResolver.ResolveNetdiskFolder(localPath, string.Empty, isDirectory);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        folder = ProjectNameResolver.ResolveNetdiskFolder(serverPath, string.Empty, isDirectory);
                    }
                    if (!string.IsNullOrWhiteSpace(folder)) snapshot.NewHistoryFolders.Add(folder);
                }
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

    internal static class BaiduNetdiskDataLocator
    {
        internal static IEnumerable<string> FindDatabases()
        {
            var explicitPaths = (Environment.GetEnvironmentVariable("TAPRELAY_BAIDU_DB") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
            var explicitDatabases = explicitPaths
                .Select(path => path.Trim())
                .Where(path => File.Exists(path) && string.Equals(Path.GetFileName(path), "BaiduYunGuanjia.db", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (explicitDatabases.Length > 0)
            {
                // 测试注入路径：直接返回，不参与持久化。
                return explicitDatabases;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configuredRoot = AppInstallRoots.Get("baidunetdisk");
            Add(roots, configuredRoot);
            Add(roots, Path.Combine(configuredRoot ?? string.Empty, "users"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "baidu", "BaiduNetdisk", "users"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "baidu", "BaiduNetdisk", "users"));
            AddInstallRoots(roots);
            try
            {
                foreach (var process in Process.GetProcessesByName("BaiduNetdisk"))
                {
                    try { Add(roots, Path.Combine(Path.GetDirectoryName(process.MainModule?.FileName) ?? string.Empty, "users")); } catch { }
                    process.Dispose();
                }
            }
            catch { }

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "BaiduYunGuanjia.db", SearchOption.AllDirectories))
                    {
                        found.Add(Path.GetFullPath(file));
                    }
                }
                catch { }
            }

            // 加固：把之前发现过、当前仍存在的库补进来，规避"TapRelay 启动时百度网盘未运行、
            // 安装目录又无法从注册表/进程定位"导致的漏接；随后把当前已知集合持久化，供下次启动直接使用。
            foreach (var remembered in LoadPersistedPaths())
            {
                try { if (File.Exists(remembered)) found.Add(Path.GetFullPath(remembered)); }
                catch { }
            }
            SavePersistedPaths(found);
            return found;
        }

        private static string PersistedPathsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "baidu-db-paths.json");

        private static IEnumerable<string> LoadPersistedPaths()
        {
            try
            {
                var file = PersistedPathsFile;
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
                var file = PersistedPathsFile;
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(file, JsonSerializer.Serialize(ordered), Encoding.UTF8);
            }
            catch { }
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
                        if (displayName.IndexOf("百度网盘", StringComparison.OrdinalIgnoreCase) < 0 && displayName.IndexOf("BaiduNetdisk", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var install = Convert.ToString(app?.GetValue("InstallLocation")) ?? string.Empty;
                        var uninstall = (Convert.ToString(app?.GetValue("UninstallString")) ?? string.Empty).Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(uninstall)) install = Path.GetDirectoryName(uninstall) ?? string.Empty;
                        Add(roots, Path.Combine(install, "users"));
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
