using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TapRelay
{
    /// <summary>
    /// Resolves the executable names that activate ordinary adapters. A process
    /// name is only an activation signal; it never grants permission to execute
    /// catalog-supplied code. LeiGod is intentionally absent because its close
    /// reminder uses the dedicated lifecycle monitor and install-root matching.
    /// </summary>
    internal static class AdaptedProcessGate
    {
        private static readonly TimeSpan ProcessProbeCacheDuration = TimeSpan.FromMilliseconds(500);
        private static readonly object ProcessProbeGate = new object();
        private static readonly Dictionary<string, ProcessProbeState> ProcessProbeCache =
            new Dictionary<string, ProcessProbeState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string[]> DefaultProcessNames =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // GetProcessesByName matches whole base names, not substrings, so
                // the repackaged Electron shell and its Rust sidecar must both be
                // listed: either one can be alive while a turn is in flight.
                //
                // Claude (官方版): 仅匹配官方 Claude Code 的进程名。Haha 改版用单独
                // 的 "claude-haha" key 探测，避免两个 app 同时命中同一进程造成双重
                // 匹配/消息错位。Haha 的外壳进程名 "Claude Code Haha" 与其 sidecar
                // "claude-sidecar-..." 留给 claude-haha，不放在 claude 项。
                ["claude"] = new[] { "Claude", "claude" },
                ["claude-haha"] = new[]
                {
                    "Claude Code Haha", "claude-sidecar-x86_64-pc-windows-msvc"
                },
                ["codex"] = new[] { "ChatGPT", "codex" },
                ["trae"] = new[] { "Trae CN", "TraeCode CN" },
                ["traework"] = new[] { "TRAE SOLO CN", "TraeWork CN" },
                ["zcode"] = new[] { "zcode" },
                ["doubao"] = new[] { "Doubao" },
                ["suyan"] = new[] { "suyan", "素言" },
                ["baidunetdisk"] = new[] { "BaiduNetdisk", "BaiduNetdiskHost", "BaiduNetdiskRender" },
                ["quarknetdisk"] = new[] { "quark_cloud_drive", "QuarkCloudDrive" },
                ["adrive"] = new[] { "aDrive" },
                ["workbuddy"] = new[] { "WorkBuddy" },
                ["opencode"] = new[] { "OpenCode" },
                ["deepseekharness"] = new[] { "DeepSeek Harness Desktop" },
                ["freebuff"] = new[] { "Freebuff" }
            };

        internal static bool IsRunningForSource(string source)
        {
            return IsRunningForAppKey(GetBaseAppKey(source));
        }

        internal static bool IsRunningForAppKey(string appKey)
        {
            return TryGetDefaultProcessNames(appKey, out var processNames) &&
                   IsAnyProcessRunning(processNames);
        }

        /// <summary>
        /// 判断 appKey 对应的进程是否正从可信安装根下运行。裸进程名只能作为
        /// 运行线索（通知门控继续用 IsRunningForAppKey）；“已安装”声明必须附加
        /// 路径校验——临时目录里改名的同名 exe 不能冒充安装证据。
        /// </summary>
        internal static bool IsRunningFromTrustedRoot(string appKey, IReadOnlyList<string?> trustedRoots)
        {
            return TryGetDefaultProcessNames(appKey, out var processNames) &&
                   AreProcessesRunningFromRoots(processNames, trustedRoots);
        }

        /// <summary>
        /// 枚举给定名称的活动进程，仅当其可执行文件位于任一可信根之下时返回 true。
        /// 路径读取失败（权限不足等）的进程不计入证据，交由磁盘/注册表证据判断。
        /// </summary>
        internal static bool AreProcessesRunningFromRoots(
            IReadOnlyList<string> processNames,
            IReadOnlyList<string?> trustedRoots)
        {
            if (processNames == null || processNames.Count == 0 ||
                trustedRoots == null || trustedRoots.Count == 0)
            {
                return false;
            }

            var roots = new List<string>();
            foreach (var root in trustedRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var normalized = NormalizeRootPath(root);
                if (normalized.Length > 0 && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(normalized);
                }
            }
            if (roots.Count == 0)
            {
                return false;
            }

            foreach (var configuredName in processNames)
            {
                var processName = NormalizeProcessName(configuredName);
                if (processName.Length == 0) continue;

                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    // 探测失败不构成证据，继续尝试其余名称。
                    continue;
                }

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (process.HasExited) continue;
                            var executablePath = process.MainModule?.FileName;
                            if (string.IsNullOrEmpty(executablePath)) continue;
                            if (roots.Any(root => IsPathUnderRoot(executablePath, root)))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // 无法读取路径的进程不能作为安装证据。
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 判断 path 是否位于 root 目录之下（前缀必须是完整的目录段，
        /// 避免 "D:\Tools2" 误匹配 "D:\Tools"）。
        /// </summary>
        internal static bool IsPathUnderRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var normalizedRoot = NormalizeRootPath(root);
                var normalizedPath = Path.GetFullPath(path.Trim());
                if (normalizedRoot.Length == 0 || normalizedPath.Length == 0)
                {
                    return false;
                }
                if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return normalizedPath.Length == normalizedRoot.Length ||
                       normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
                       normalizedPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeRootPath(string root)
        {
            try
            {
                return Path.GetFullPath(root.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool TryGetDefaultProcessNames(
            string appKey,
            out IReadOnlyList<string> processNames)
        {
            var key = GetBaseAppKey(appKey);
            if (DefaultProcessNames.TryGetValue(key, out var names))
            {
                processNames = names;
                return true;
            }

            processNames = Array.Empty<string>();
            return false;
        }

        internal static string NormalizeProcessName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                return string.Empty;
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return name.Trim().ToLowerInvariant();
        }

        internal static bool IsAnyProcessRunning(IReadOnlyList<string> processNames)
        {
            if (processNames == null || processNames.Count == 0) return false;

            var normalizedNames = new List<string>(processNames.Count);
            foreach (var configuredName in processNames)
            {
                var processName = NormalizeProcessName(configuredName);
                if (processName.Length == 0) continue;

                if (!normalizedNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    normalizedNames.Add(processName);
                }
            }

            if (normalizedNames.Count == 0) return false;

            var cacheKey = string.Join("|", normalizedNames);
            var now = DateTime.UtcNow;
            lock (ProcessProbeGate)
            {
                if (ProcessProbeCache.TryGetValue(cacheKey, out var cached) &&
                    now - cached.CheckedAtUtc < ProcessProbeCacheDuration)
                {
                    return cached.Running;
                }
            }

            var running = ProbeProcesses(normalizedNames);
            lock (ProcessProbeGate)
            {
                ProcessProbeCache[cacheKey] = new ProcessProbeState(now, running);
                if (ProcessProbeCache.Count > 64)
                {
                    foreach (var staleKey in new List<string>(ProcessProbeCache.Keys))
                    {
                        if (now - ProcessProbeCache[staleKey].CheckedAtUtc >= ProcessProbeCacheDuration)
                        {
                            ProcessProbeCache.Remove(staleKey);
                        }
                    }
                }
            }
            return running;
        }

        private static bool ProbeProcesses(IReadOnlyList<string> processNames)
        {
            foreach (var processName in processNames)
            {

                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    // A failed probe is not evidence that the adapted app is
                    // active. Fail closed to avoid stale notifications.
                    return false;
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
                            // An inaccessible process is not treated as active.
                        }
                    }
                }
            }

            return false;
        }

        private sealed class ProcessProbeState
        {
            internal ProcessProbeState(DateTime checkedAtUtc, bool running)
            {
                CheckedAtUtc = checkedAtUtc;
                Running = running;
            }

            internal DateTime CheckedAtUtc { get; }
            internal bool Running { get; }
        }

        private static string GetBaseAppKey(string value)
        {
            var key = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (key == "quarknetdisk-upload") return "quarknetdisk";
            if (key == "adrive-upload") return "adrive";
            return key;
        }
    }
}
