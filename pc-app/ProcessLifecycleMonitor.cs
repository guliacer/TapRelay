using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TapRelay
{
    /// <summary>
    /// Watches declarative application image rules and emits one reminder when a
    /// previously observed matching process group terminates. A group is one
    /// appKey plus install root, which prevents Electron renderer/GPU processes
    /// from producing one reminder per child process. WMI is the low-latency
    /// path; a bounded name-based scan repairs missed events and also handles
    /// processes that were already running when the monitor started.
    /// </summary>
    internal sealed class ProcessLifecycleMonitor : IDisposable
    {
        private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);
        private const int ProcessPathRetryCount = 5;
        private const int ProcessPathRetryDelayMilliseconds = 100;
        private const int ShutdownDispatchTimeoutMilliseconds = 2000;
        private const int MaxTaskIdLength = 200;
        private const int MaxMessageLength = 500;

        private readonly Func<string, string, string, string, string, long, string, string, string, Task> _dispatch;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, TrackedProcess> _active =
            new Dictionary<string, TrackedProcess>(StringComparer.Ordinal);
        private readonly List<ManagementEventWatcher> _watchers = new List<ManagementEventWatcher>();
        private LifecycleRuntime[] _rules = Array.Empty<LifecycleRuntime>();
        private NonReentrantMonitorTimer? _scanTimer;
        private bool _started;
        private bool _sessionEnding;
        private bool _disposed;
        private int _fastRecoveryScansRemaining;

        private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        internal ProcessLifecycleMonitor(
            Func<string, string, string, string, string, long, string, string, string, Task> dispatch,
            Action<string> log)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal void Start(IReadOnlyList<RemoteAdapterRule> rules)
        {
            lock (_gate)
            {
                if (_disposed || _started) return;
                _started = true;
                _rules = BuildRuntimes(rules);
                _fastRecoveryScansRemaining = 3;
                _scanTimer = new NonReentrantMonitorTimer(
                    Scan,
                    GetNextScanInterval,
                    TimeSpan.FromSeconds(2),
                    error => _log("适配应用进程恢复扫描失败：" + error.Message));
            }

            try
            {
                SystemEvents.SessionEnding += OnSessionEnding;
            }
            catch (Exception error)
            {
                _log("系统会话结束事件注册失败，将继续使用进程监听：" + error.Message);
            }

            RebuildWmiWatchers();
            Scan();
            _log("已启用适配应用关闭提醒进程监控");
        }

        internal void ReplaceRules(IReadOnlyList<RemoteAdapterRule> rules)
        {
            LifecycleRuntime[] runtimes = BuildRuntimes(rules);
            lock (_gate)
            {
                if (_disposed) return;
                _rules = runtimes;
                // Existing tracked entries contain the old runtime and rule
                // payload. Rebuild them from the current process snapshot so a
                // hot lifecycle-mode change takes effect without restarting
                // the PC application.
                _active.Clear();
            }

            if (_started)
            {
                RebuildWmiWatchers();
                Scan();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _scanTimer?.Dispose();
                _scanTimer = null;
                _active.Clear();
                DisposeWmiWatchersLocked();
            }

            try
            {
                SystemEvents.SessionEnding -= OnSessionEnding;
            }
            catch
            {
                // Shutdown cleanup must not prevent the PC app from exiting.
            }
        }

        private void RebuildWmiWatchers()
        {
            LifecycleRuntime[] rules;
            lock (_gate)
            {
                if (_disposed || !_started) return;
                DisposeWmiWatchersLocked();
                rules = _rules.ToArray();
            }

            var names = rules
                .SelectMany(item => item.ImageNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0) return;

            var where = string.Join(
                " OR ",
                names.Select(name => "ProcessName = '" + name.Replace("'", "''") + "'"));
            foreach (var className in new[] { "Win32_ProcessStartTrace", "Win32_ProcessStopTrace" })
            {
                try
                {
                    var watcher = new ManagementEventWatcher(
                        new WqlEventQuery("SELECT * FROM " + className + " WHERE " + where));
                    if (className.EndsWith("StartTrace", StringComparison.Ordinal))
                    {
                        watcher.EventArrived += OnProcessStarted;
                    }
                    else
                    {
                        watcher.EventArrived += OnProcessStopped;
                    }

                    watcher.Start();
                    lock (_gate)
                    {
                        if (_disposed || !_started)
                        {
                            watcher.Stop();
                            watcher.Dispose();
                            return;
                        }

                        _watchers.Add(watcher);
                    }
                }
                catch (Exception error)
                {
                    _log("WMI 进程事件监听不可用，将使用轮询兜底：" + error.Message);
                    return;
                }
            }

            _log("已启用 WMI 进程启动/停止事件监听");
        }

        private void DisposeWmiWatchersLocked()
        {
            foreach (var watcher in _watchers)
            {
                try { watcher.Stop(); } catch { }
                try { watcher.Dispose(); } catch { }
            }
            _watchers.Clear();
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs args)
        {
            var processId = ReadProcessId(args);
            if (processId <= 0) return;
            var processName = ReadStringProperty(args, "ProcessName");
            ThreadPool.QueueUserWorkItem(_ => ObserveStartedProcess(processId, processName));
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs args)
        {
            var processId = ReadProcessId(args);
            if (processId > 0)
            {
                ObserveStoppedProcess(processId, "process-event");
            }
        }

        private void ObserveStartedProcess(int processId, string processName)
        {
            for (var attempt = 0; attempt < ProcessPathRetryCount; attempt++)
            {
                if (TryReadProcess(processId, out var executablePath, out var startTimeUtc))
                {
                    ObserveProcess(
                        processId,
                        processName,
                        executablePath,
                        startTimeUtc,
                        ReadProcessCommandLine(processId));
                    return;
                }

                Thread.Sleep(ProcessPathRetryDelayMilliseconds);
            }
        }

        private void Scan()
        {
            LifecycleRuntime[] rules;
            TrackedProcess[] active;
            lock (_gate)
            {
                if (_disposed || !_started) return;
                rules = _rules.ToArray();
                active = _active.Values.ToArray();
            }

            var processNames = rules
                .SelectMany(item => item.ImageNames)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var processName in processNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            if (TryReadProcess(process, out var path, out var startTimeUtc))
                            {
                                ObserveProcess(
                                    process.Id,
                                    process.ProcessName,
                                    path,
                                    startTimeUtc,
                                    ReadProcessCommandLine(process.Id));
                            }
                        }
                    }
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                    _log("进程轮询读取失败，将在下次扫描重试：" + error.Message);
                }
            }

            foreach (var item in active)
            {
                if (!IsProcessAlive(item.ProcessId))
                {
                    ObserveStoppedProcess(item.ProcessId, "scan");
                }
                else if (item.Runtime.RequiresWindowObservation)
                {
                    ObserveWindowState(item);
                }
            }
        }

        private TimeSpan GetNextScanInterval()
        {
            lock (_gate)
            {
                if (_fastRecoveryScansRemaining > 0)
                {
                    _fastRecoveryScansRemaining--;
                    return TimeSpan.FromSeconds(2);
                }

                return ScanInterval;
            }
        }

        private void ObserveProcess(
            int processId,
            string processName,
            string executablePath,
            DateTime startTimeUtc,
            string commandLine)
        {
            var matchingRules = GetMatchingRules(processName, executablePath, commandLine);
            if (matchingRules.Length == 0) return;

            foreach (var rule in matchingRules)
            {
                var groupKey = BuildGroupKey(rule, executablePath);
                var identityKey = BuildIdentityKey(rule.Rule.AppKey, processId, executablePath, startTimeUtc);
                var windowObserved = rule.RequiresWindowObservation && HasVisibleTopLevelWindow(processId);
                TrackedProcess[] stale = Array.Empty<TrackedProcess>();
                lock (_gate)
                {
                    if (_disposed || !_started) return;
                    if (_active.TryGetValue(identityKey, out var existing))
                    {
                        existing.WindowObserved |= windowObserved;
                        continue;
                    }

                    stale = _active.Values
                        .Where(item => item.ProcessId == processId &&
                            !string.Equals(item.IdentityKey, identityKey, StringComparison.Ordinal))
                        .ToArray();
                    foreach (var old in stale)
                    {
                        _active.Remove(old.IdentityKey);
                    }

                    _active[identityKey] = new TrackedProcess(
                        identityKey,
                        groupKey,
                        rule,
                        rule.Rule.AppKey,
                        rule.Rule,
                        processId,
                        executablePath,
                        startTimeUtc,
                        windowObserved);
                }

                foreach (var old in stale)
                {
                    QueueReminderIfGroupEnded(old, "pid-reused");
                }
            }
        }

        private void ObserveStoppedProcess(int processId, string reason)
        {
            TrackedProcess[] endedGroups;
            lock (_gate)
            {
                if (_disposed) return;
                var stopped = _active.Values.Where(item => item.ProcessId == processId).ToArray();
                foreach (var item in stopped)
                {
                    _active.Remove(item.IdentityKey);
                }

                endedGroups = stopped
                    .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Where(item => !_active.Values.Any(active =>
                        string.Equals(active.GroupKey, item.GroupKey, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }

            foreach (var item in endedGroups)
            {
                QueueReminderIfGroupEnded(item, reason);
            }
        }

        private void OnSessionEnding(object? sender, SessionEndingEventArgs args)
        {
            TrackedProcess[] active;
            lock (_gate)
            {
                if (_disposed || _sessionEnding) return;
                _sessionEnding = true;
                active = _active.Values
                    .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
                _active.Clear();
            }

            var tasks = active
                .Where(item => item.Rule.NotifyOnSessionEnding)
                .Select(item => DispatchCloseReminderAsync(item, "session-ending"))
                .ToArray();
            if (tasks.Length == 0) return;

            try
            {
                Task.WhenAll(tasks).Wait(ShutdownDispatchTimeoutMilliseconds);
            }
            catch (Exception error)
            {
                _log("系统关机/重启关闭提醒发送失败：" + error.Message);
            }
        }

        private async Task DispatchCloseReminderAsync(TrackedProcess process, string reason)
        {
            if (!process.Rule.NotifyOnSessionEnding && reason == "session-ending") return;

            var title = Limit(
                string.IsNullOrWhiteSpace(process.Rule.CloseReminderTitle)
                    ? process.Rule.DisplayName + "关闭提醒"
                    : process.Rule.CloseReminderTitle,
                120);
            var message = Limit(
                string.IsNullOrWhiteSpace(process.Rule.CloseReminderMessage)
                    ? "应用已关闭，请确认相关服务是否已停止"
                    : process.Rule.CloseReminderMessage,
                MaxMessageLength);
            var actionUrl = ResolveActionUrl(process.Rule);
            var taskId = Limit(
                "close:" + process.Rule.AppKey + ":" + process.ProcessId + ":" +
                process.StartTimeUtc.Ticks,
                MaxTaskIdLength);

            try
            {
                await _dispatch(
                    message,
                    process.Rule.AppKey,
                    "waiting",
                    taskId,
                    process.ExecutablePath,
                    0L,
                    process.Rule.DisplayName,
                    title,
                    actionUrl).ConfigureAwait(false);
                _log("已发送应用关闭提醒：[" + process.Rule.AppKey + "] reason=" + reason +
                     " pid=" + process.ProcessId);
            }
            catch (Exception error)
            {
                _log("应用关闭提醒发送失败：[" + process.Rule.AppKey + "] " + error.Message);
            }
        }

        private void QueueReminderIfGroupEnded(TrackedProcess process, string reason)
        {
            lock (_gate)
            {
                if (_disposed || _sessionEnding || _active.Values.Any(item =>
                        string.Equals(item.GroupKey, process.GroupKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            // WMI can report a child stop before its sibling start event has
            // been observed. Confirm the group is actually gone before sending
            // the reminder; otherwise Electron child-process churn looks like
            // an application close.
            if (HasLiveProcessInGroup(process)) return;
            _ = DispatchCloseReminderAsync(process, reason);
        }

        private bool HasLiveProcessInGroup(TrackedProcess sample)
        {
            var processNames = sample.Runtime.ImageNames
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var processName in processNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            if (process.Id == sample.ProcessId) continue;
                            if (TryReadProcess(process, out var path, out _))
                            {
                                var commandLine = ReadProcessCommandLine(process.Id);
                                if (!sample.Runtime.IsProcessEligible(commandLine)) continue;
                                var groupKey = BuildGroupKey(sample.Runtime, path);
                                if (string.Equals(groupKey, sample.GroupKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                            else if (IsProcessAlive(process.Id))
                            {
                                // An inaccessible same-name process is treated
                                // conservatively as alive to avoid a false close.
                                return true;
                            }
                        }
                    }
                }
                catch (Exception error) when (error is InvalidOperationException ||
                                               error is System.ComponentModel.Win32Exception)
                {
                    // A failed probe is not proof that the group ended.
                    return true;
                }
            }

            return false;
        }

        private void ObserveWindowState(TrackedProcess process)
        {
            var visible = HasVisibleTopLevelWindow(process.ProcessId);
            var windowClosed = false;
            lock (_gate)
            {
                if (_disposed || !_active.ContainsKey(process.IdentityKey)) return;
                if (visible)
                {
                    process.WindowObserved = true;
                }
                else if (process.WindowObserved)
                {
                    _active.Remove(process.IdentityKey);
                    windowClosed = true;
                }
            }

            if (windowClosed)
            {
                QueueReminderIfGroupEnded(process, "window-closed");
            }
        }

        private static bool HasVisibleTopLevelWindow(int processId)
        {
            var found = false;
            EnumWindows((windowHandle, _) =>
            {
                if (!IsWindowVisible(windowHandle)) return true;
                GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
                if (ownerProcessId == (uint)processId)
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string ReadProcessCommandLine(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId);
                using var results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    return item["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
            catch
            {
                // Command-line access is best effort. Rules without a native
                // lifecycle mode do not depend on it.
            }

            return string.Empty;
        }

        private LifecycleRuntime[] GetMatchingRules(
            string processName,
            string executablePath,
            string commandLine)
        {
            LifecycleRuntime[] rules;
            lock (_gate)
            {
                if (_disposed) return Array.Empty<LifecycleRuntime>();
                rules = _rules.ToArray();
            }

            var fileName = Path.GetFileName(executablePath);
            var observedName = string.IsNullOrWhiteSpace(fileName) ? processName : fileName;
            return rules
                .Where(rule => rule.ImageNames.Contains(observedName))
                .Where(rule => rule.InstallRoots.Any(root => IsUnderRoot(executablePath, root)))
                .Where(rule => rule.IsProcessEligible(commandLine))
                .ToArray();
        }

        private static LifecycleRuntime[] BuildRuntimes(IReadOnlyList<RemoteAdapterRule> rules)
        {
            return (rules ?? Array.Empty<RemoteAdapterRule>())
                .Where(rule => rule != null && rule.CloseReminderEnabled)
                .Select(rule => new LifecycleRuntime(rule))
                .Where(runtime => runtime.ImageNames.Count > 0 && runtime.InstallRoots.Length > 0)
                .ToArray();
        }

        private static bool TryReadProcess(int processId, out string executablePath, out DateTime startTimeUtc)
        {
            executablePath = string.Empty;
            startTimeUtc = DateTime.MinValue;
            try
            {
                using var process = Process.GetProcessById(processId);
                return TryReadProcess(process, out executablePath, out startTimeUtc);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static bool TryReadProcess(Process process, out string executablePath, out DateTime startTimeUtc)
        {
            executablePath = string.Empty;
            startTimeUtc = DateTime.MinValue;
            try
            {
                if (process.HasExited) return false;
                executablePath = process.MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath)) return false;
                try
                {
                    startTimeUtc = process.StartTime.ToUniversalTime();
                }
                catch
                {
                    // The path and PID still provide a stable fallback identity.
                }

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // An access failure is not proof of termination.
                return true;
            }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string BuildGroupKey(LifecycleRuntime runtime, string executablePath)
        {
            var root = runtime.InstallRoots.FirstOrDefault(item => IsUnderRoot(executablePath, item)) ?? string.Empty;
            return runtime.Rule.AppKey + "|" + root.Trim().ToLowerInvariant();
        }

        private static string BuildIdentityKey(string appKey, int processId, string path, DateTime startTimeUtc)
        {
            return appKey + "|" + processId + "|" + startTimeUtc.Ticks + "|" +
                   path.Trim().ToLowerInvariant();
        }

        private static int ReadProcessId(EventArrivedEventArgs args)
        {
            try
            {
                var value = args.NewEvent.Properties["ProcessID"]?.Value;
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static string ReadStringProperty(EventArrivedEventArgs args, string name)
        {
            try
            {
                return args.NewEvent.Properties[name]?.Value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveActionUrl(RemoteAdapterRule rule)
        {
            var configured = (rule.ActionUrl ?? string.Empty).Trim();
            if (configured.Length == 0 &&
                string.Equals(rule.AppKey, RemoteAdapterRule.LeigodAppKey, StringComparison.OrdinalIgnoreCase))
            {
                configured = (Environment.GetEnvironmentVariable(RemoteAdapterRule.LeigodMiniProgramUrlVariable) ?? string.Empty).Trim();
            }

            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return string.Empty;
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return configured;
            }

            if (string.Equals(uri.Scheme, "weixin", StringComparison.OrdinalIgnoreCase))
            {
                return configured;
            }

            return string.Empty;
        }

        private static string Limit(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private sealed class LifecycleRuntime
        {
            internal LifecycleRuntime(RemoteAdapterRule rule)
            {
                Rule = rule;
                ImageNames = new HashSet<string>(
                    (rule.ImageNames ?? new List<string>())
                        .Select(NormalizeImageName)
                        .Where(item => item.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                InstallRoots = (rule.InstallRoots ?? new List<string>())
                    .Select(Environment.ExpandEnvironmentVariables)
                    .Select(Path.GetFullPath)
                    .Select(item => item.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                RuleKeys = new[] { rule.AppKey };
            }

            internal RemoteAdapterRule Rule { get; }
            internal HashSet<string> ImageNames { get; }
            internal string[] InstallRoots { get; }
            internal string[] RuleKeys { get; }
            internal bool RequiresWindowObservation =>
                string.Equals(
                    Rule.LifecycleMode,
                    RemoteAdapterRule.LeigodClientLifecycleMode,
                    StringComparison.OrdinalIgnoreCase);

            internal bool IsProcessEligible(string commandLine)
            {
                if (!RequiresWindowObservation) return true;
                var value = (commandLine ?? string.Empty).Trim();
                return value.IndexOf("-launch", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       value.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0;
            }

            private static string NormalizeImageName(string value)
            {
                var name = Path.GetFileName((value ?? string.Empty).Trim());
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name
                    : name + ".exe";
            }
        }

        private sealed class TrackedProcess
        {
            internal TrackedProcess(
                string identityKey,
                string groupKey,
                LifecycleRuntime runtime,
                string ruleKey,
                RemoteAdapterRule rule,
                int processId,
                string executablePath,
                DateTime startTimeUtc,
                bool windowObserved)
            {
                IdentityKey = identityKey;
                GroupKey = groupKey;
                Runtime = runtime;
                RuleKey = ruleKey;
                Rule = rule;
                ProcessId = processId;
                ExecutablePath = executablePath;
                StartTimeUtc = startTimeUtc;
                WindowObserved = windowObserved;
            }

            internal string IdentityKey { get; }
            internal string GroupKey { get; }
            internal LifecycleRuntime Runtime { get; }
            internal string RuleKey { get; }
            internal RemoteAdapterRule Rule { get; }
            internal int ProcessId { get; }
            internal string ExecutablePath { get; }
            internal DateTime StartTimeUtc { get; }
            internal bool WindowObserved { get; set; }
        }
    }
}
