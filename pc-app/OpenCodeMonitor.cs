using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace TapRelay
{
    /// <summary>
    /// Event-driven OpenCode monitor. It watches the OpenCode SQLite database
    /// (~/.local/share/opencode/opencode.db) and emits a notification when an
    /// assistant turn reaches a terminal state:
    ///   - finish == "stop"                                -> completed
    ///   - error.name == "MessageAbortedError"             -> cancelled (user stop, ignored)
    ///   - other error (e.g. APIError)                     -> failed
    /// The project directory is taken from the owning session's directory.
    /// </summary>
    internal sealed class OpenCodeMonitor : IDisposable
    {
        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ProcessRecoveryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);
        private readonly Action<OpenCodeCompletion> _completed;
        private readonly Action<string> _log;
        private readonly Func<bool> _isProcessRunning;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DatabaseState> _states = new Dictionary<string, DatabaseState>(StringComparer.OrdinalIgnoreCase);
        private NonReentrantMonitorTimer? _pollTimer;
        private bool _processRunning;
        private bool _disposed;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;

        internal OpenCodeMonitor(
            Action<OpenCodeCompletion> completed,
            Action<string> log,
            Func<bool>? isProcessRunning = null)
        {
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _isProcessRunning = isProcessRunning ?? (() => AdaptedProcessGate.IsRunningForAppKey("opencode"));
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
                    error => _log("OpenCode 数据库恢复检查失败：" + error.Message));
            }

            if (!processRunning)
            {
                _log("OpenCode process is not running; task monitoring is paused until it starts");
                return;
            }

            var databases = OpenCodeDataLocator.FindDatabases().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var database in databases) AddDatabase(database);
            _lastDiscoveryUtc = DateTime.UtcNow;

            if (databases.Length == 0)
            {
                _log("未找到 OpenCode 数据库；启动 OpenCode 并运行一次任务后重启 TapRelay 可自动接入");
            }
            else
            {
                _log("已启用 OpenCode 任务状态监听（数据库变更事件驱动）：" + databases.Length + " 个库");
            }
        }

        internal void Refresh()
        {
            if (UpdateProcessState()) return;
            bool processRunning;
            lock (_gate) processRunning = !_disposed && _processRunning;
            if (!processRunning) return;

            foreach (var database in OpenCodeDataLocator.FindDatabases()) AddDatabase(database);
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
                if (!processRunning) StopDatabaseMonitoringLocked();
            }

            _log(processRunning
                ? "OpenCode process started; task monitoring resumed"
                : "OpenCode process stopped; task monitoring paused");
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
                    var state = new DatabaseState(fullPath)
                    {
                        LastMessageUpdated = OpenCodeDatabaseReader.ReadLastUpdated(fullPath)
                    };
                    state.Watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath) ?? ".", "opencode.db*")
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
                    _log("OpenCode 数据库已接入：" + fullPath);
                }
                catch (Exception error)
                {
                    _log("OpenCode 数据库接入失败：" + fullPath + "；" + error.Message);
                }
            }
        }

        private void PollDatabaseChanges()
        {
            if (!IsProcessActiveForNotification()) return;

            DatabaseState[] states;
            lock (_gate)
            {
                if (_disposed) return;
                states = _states.Values.ToArray();
            }

            foreach (var state in states)
            {
                try
                {
                    var lastUpdated = OpenCodeDatabaseReader.ReadLastUpdated(state.Path);
                    lock (_gate)
                    {
                        if (_disposed || !_processRunning || lastUpdated <= state.LastMessageUpdated) continue;
                        // 只发信号，不在这里推进基线：基线推进交给 ProcessStableChange 在读取
                        // 终端事件之后完成。若在此处就推进 LastMessageUpdated，ProcessStableChange
                        // 会在读取终端事件前就跳过它们，真实完成事件会被永久吞掉。
                        SignalLocked(state);
                    }
                }
                catch
                {
                    // The database may be locked briefly while OpenCode writes it.
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
                var completions = new List<OpenCodeCompletion>();
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
                        // Read the terminal events and the new watermark from ONE snapshot, then
                        // advance the baseline only to what that snapshot observed. Never advance
                        // to a value read on a separate connection: a stale cross-process WAL
                        // snapshot could otherwise push the baseline past an unread completion.
                        var (events, observedMax) = OpenCodeDatabaseReader.ReadTerminalSince(state.Path, state.LastMessageUpdated);
                        if (observedMax > state.LastMessageUpdated) state.LastMessageUpdated = observedMax;
                        foreach (var item in events)
                        {
                            if (item.Status == "cancelled") continue;
                            if (!TryDeduplicate(state, item)) continue;
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
                _log("OpenCode 任务事件解析失败：" + error.Message);
            }
        }

        private static bool TryDeduplicate(DatabaseState state, OpenCodeCompletion item)
        {
            var key = item.MessageId + "|" + item.Status;
            return state.SeenKeys.Add(key);
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
            internal long LastMessageUpdated { get; set; }
            internal HashSet<string> SeenKeys { get; } = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal sealed class OpenCodeCompletion
    {
        internal OpenCodeCompletion(string messageId, string sessionId, string status, string message, string cwd, long durationMs, string projectName = "", long tokenCount = 0L)
        {
            MessageId = messageId;
            SessionId = sessionId ?? string.Empty;
            Status = status;
            Message = message;
            Cwd = cwd ?? string.Empty;
            DurationMs = durationMs;
            ProjectName = projectName ?? string.Empty;
            TokenCount = tokenCount;
        }
        internal string MessageId { get; }
        internal string SessionId { get; }
        internal string Status { get; }
        internal string Message { get; }
        internal string Cwd { get; }
        internal long DurationMs { get; }
        internal string ProjectName { get; }
        internal long TokenCount { get; }
        internal string TaskId => "opencode-" + MessageId;
    }

    internal static class OpenCodeDatabaseReader
    {
        /// <summary>
        /// Returns the max message.time_updated (epoch ms) across all messages.
        /// Used as a cheap "database changed" watermark. It must track time_updated,
        /// not the JSON $.time.created: OpenCode stamps an assistant row's created
        /// time when the turn starts and never moves it, whereas the terminal
        /// finish=="stop" transition is a later row UPDATE that only advances
        /// time_updated. Watermarking on created time would let the baseline pass a
        /// message before it completes, making the completion permanently invisible.
        /// </summary>
        internal static long ReadLastUpdated(string path)
        {
            using var connection = OpenConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(time_updated), 0) FROM message";
            var value = Convert.ToInt64(command.ExecuteScalar());
            return value;
        }

        /// <summary>
        /// Reads assistant messages whose row was updated after <paramref name="afterUpdated"/>
        /// and reached a terminal state, resolving each to a completion event. User-initiated
        /// stops are mapped to "cancelled" and filtered by the caller.
        ///
        /// <c>ObservedMax</c> is the largest <c>time_updated</c> among the rows this query actually
        /// returned (or <paramref name="afterUpdated"/> if none). The caller advances its baseline
        /// only to that value. This is deliberate: the watermark is derived from the SAME row read
        /// that produces the events, so the baseline can never move past a completion this read did
        /// not see. A separate <c>MAX(time_updated)</c> query could in principle observe a newer
        /// value than this row scan and advance the baseline past a completion this read did not
        /// return, making that completion permanently invisible.
        /// </summary>
        internal static (List<OpenCodeCompletion> Events, long ObservedMax) ReadTerminalSince(string path, long afterUpdated)
        {
            var result = new List<OpenCodeCompletion>();
            var observedMax = afterUpdated;
            using var connection = OpenConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT m.id, m.session_id, m.data,
                       s.directory, m.time_updated,
                       s.title, s.time_created, s.time_updated
                FROM message m
                LEFT JOIN session s ON s.id = m.session_id
                WHERE json_extract(m.data, '$.role') = 'assistant'
                  AND s.parent_id IS NULL
                  AND m.time_updated > $after";
            command.Parameters.AddWithValue("$after", afterUpdated);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowUpdated = reader.GetInt64(4);
                if (rowUpdated > observedMax) observedMax = rowUpdated;
                var data = reader.GetString(2);
                var id = reader.GetString(0);
                var sessionTitle = reader.IsDBNull(5) ? null : reader.GetString(5);
                var sessionCreated = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);
                var sessionUpdated = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
                var completion = TryParseCompletion(id, reader.GetString(1), data, reader.IsDBNull(3) ? null : reader.GetString(3), sessionTitle, sessionCreated, sessionUpdated, path);
                if (completion != null) result.Add(completion);
            }
            return (result, observedMax);
        }

        private static OpenCodeCompletion? TryParseCompletion(string messageId, string sessionId, string data, string? directory, string? sessionTitle, long sessionCreated, long sessionUpdated, string? databasePath)
        {
            var finish = ExtractString(data, "$.finish");
            var errorName = ExtractString(data, "$.error.name");
            var created = ExtractLong(data, "$.time.created");
            var completed = ExtractLong(data, "$.time.completed");

            var hasError = !string.IsNullOrWhiteSpace(errorName);
            var isStop = string.Equals(finish, "stop", StringComparison.OrdinalIgnoreCase);

            string status;
            if (!hasError && isStop)
            {
                status = "completed";
            }
            else if (hasError && string.Equals(errorName, "MessageAbortedError", StringComparison.OrdinalIgnoreCase))
            {
                // User pressed stop / Ctrl+C or aborted the turn. Treat as cancelled,
                // not a failure, so it does not generate an alarm.
                status = "cancelled";
            }
            else if (hasError)
            {
                status = "failed";
            }
            else
            {
                // finish == "tool-calls" or transient non-terminal state.
                return null;
            }

            var message = string.Empty;
            if (status == "failed")
            {
                message = ExtractString(data, "$.error.message");
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "OpenCode 任务失败";
                }
            }
            else
            {
                // The assistant message's visible text parts are the authoritative
                // completion log. Never substitute the user's prompt or session title.
                message = ExtractAssistantText(databasePath, messageId);
            }

            // Use message-level duration (completed - created) for accurate task timing.
            // Session-level duration includes idle time between messages and can be misleading.
            long durationMs = completed > created && created > 0 ? Math.Max(0, completed - created) : 0L;

            // Task token usage: prefer tokens.total (OpenCode counts input + output +
            // cache reads/writes there, matching the Claude aggregate semantics).
            // Fall back to input + output when total is missing or zero so a missing
            // usage block does not surface as a reliable "0 tokens".
            var tokenCount = ExtractLong(data, "$.tokens.total");
            if (tokenCount <= 0L)
            {
                tokenCount = ExtractLong(data, "$.tokens.input") + ExtractLong(data, "$.tokens.output");
            }

            // ProjectName is resolved from session.directory by the dispatcher. The
            // session title is a conversation label, not a project name.
            return new OpenCodeCompletion(messageId, sessionId, status, message, directory ?? string.Empty, durationMs, string.Empty, tokenCount);
        }

        private static SqliteConnection OpenConnection(string path)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                // Read-only, shared cache. TapRelay never writes to the user's live OpenCode
                // database; it only observes terminal message rows. Shared cache lets the
                // read participate in the WAL without opening the database for writing.
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

        private static string ExtractString(string json, string jsonPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var node = doc.RootElement;
                foreach (var segment in jsonPath.Substring(1).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out var child)) return string.Empty;
                    node = child;
                }
                return node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static long ExtractLong(string json, string jsonPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var node = doc.RootElement;
                foreach (var segment in jsonPath.Substring(1).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out var child)) return 0L;
                    node = child;
                }
                if (node.ValueKind == JsonValueKind.Number)
                {
                    if (node.TryGetInt64(out var value)) return value;
                    if (node.TryGetDouble(out var d)) return (long)d;
                }
                return 0L;
            }
            catch { return 0L; }
        }

        /// <summary>
        /// Extracts visible assistant text parts for the completed message.
        /// Reasoning and tool parts are intentionally ignored.
        /// </summary>
        private static string ExtractAssistantText(string? databasePath, string assistantMessageId)
        {
            if (string.IsNullOrWhiteSpace(databasePath)) return string.Empty;

            try
            {
                using var connection = OpenConnection(databasePath);
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT json_extract(data, '$.text')
                    FROM part
                    WHERE message_id = $messageId
                      AND json_extract(data, '$.type') = 'text'
                    ORDER BY time_updated
                ";
                command.Parameters.AddWithValue("$messageId", assistantMessageId);
                using var reader = command.ExecuteReader();
                var builder = new StringBuilder();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    var partText = reader.GetString(0).Trim();
                    if (partText.Length == 0) continue;
                    if (builder.Length > 0) builder.AppendLine();
                    builder.Append(partText);
                    if (builder.Length >= 4000) break;
                }

                return TruncateMessage(builder.ToString(), 4000);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Truncates a message to the specified maximum length, adding ellipsis if needed.
        /// </summary>
        private static string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            var trimmed = message.Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength - 3) + "...";
        }
    }

    internal static class OpenCodeDataLocator
    {
        private const string DatabaseName = "opencode.db";
        private const string PersistedPathsFile = "opencode-db-paths.json";

        internal static IEnumerable<string> FindDatabase(string? userProfile)
        {
            userProfile ??= AgentLogPaths.GetUserProfileRoot();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var overridePath = Environment.GetEnvironmentVariable("TAPRELAY_OPENCODE_DB");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var full = Path.GetFullPath(overridePath.Trim());
                if (File.Exists(full) && string.Equals(Path.GetFileName(full), DatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(full);
                }
            }

            var roots = new List<string>
            {
                Path.Combine(userProfile, ".local", "share", "opencode"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode")
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var candidate = Path.Combine(root, DatabaseName);
                if (File.Exists(candidate)) found.Add(Path.GetFullPath(candidate));
            }

            // Remember previously discovered paths so the app can reconnect even if
            // OpenCode is not running at TapRelay startup and its install root cannot
            // be located.
            foreach (var remembered in LoadPersistedPaths())
            {
                try { if (File.Exists(remembered)) found.Add(Path.GetFullPath(remembered)); }
                catch { }
            }
            SavePersistedPaths(found);
            return found;
        }

        internal static IEnumerable<string> FindDatabases()
        {
            return FindDatabase(null);
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
