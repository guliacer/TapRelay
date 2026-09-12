using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TapRelay
{
    internal static class AgentLogPaths
    {
        internal const string UserHomeOverrideVariable = "TAPRELAY_MONITOR_USER_HOME";
        internal const string AppDataOverrideVariable = "TAPRELAY_MONITOR_APPDATA";
        internal const string LocalAppDataOverrideVariable = "TAPRELAY_MONITOR_LOCALAPPDATA";

        internal static IReadOnlyList<string> GetWatchDirectories()
        {
            var userProfile = ResolveRoot(
                UserHomeOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var appData = ResolveRoot(
                AppDataOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            var localAppData = ResolveRoot(
                LocalAppDataOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            var directories = new List<string>
            {
                Path.Combine(appData, "Trae CN", "logs"),
                Path.Combine(appData, "TRAE SOLO CN", "logs"),
                Path.Combine(appData, "TraeWork CN", "logs"),
                Path.Combine(userProfile, ".zcode", "cli", "log"),
                Path.Combine(userProfile, ".zcode", "v2", "logs"),
                Path.Combine(userProfile, ".codex", "sessions"),
                Path.Combine(userProfile, ".claude", "projects"),
                Path.Combine(userProfile, ".claude", "transcripts"),
                Path.Combine(localAppData, "Doubao", "User Data", "sdk_storage", "log"),
                Path.Combine(userProfile, ".workbuddy", "logs")
            };

            foreach (var item in AppInstallRoots.Snapshot())
            {
                switch (item.AppKey)
                {
                    case "claude":
                        directories.Add(Path.Combine(item.Path, ".claude", "projects"));
                        directories.Add(Path.Combine(item.Path, ".claude", "transcripts"));
                        break;
                    case "codex":
                        directories.Add(Path.Combine(item.Path, ".codex", "sessions"));
                        directories.Add(Path.Combine(item.Path, "sessions"));
                        break;
                    case "trae":
                    case "trae-solo":
                    case "traework":
                    case "zcode":
                        directories.Add(Path.Combine(item.Path, "logs"));
                        directories.Add(Path.Combine(item.Path, "cli", "log"));
                        directories.Add(Path.Combine(item.Path, "v2", "logs"));
                        break;
                    case "doubao":
                        directories.Add(Path.Combine(item.Path, "User Data", "sdk_storage", "log"));
                        directories.Add(Path.Combine(item.Path, "sdk_storage", "log"));
                        break;
                }
            }

            return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string GetUserProfileRoot()
        {
            return ResolveRoot(
                UserHomeOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        internal static string GetApplicationDataRoot()
        {
            return ResolveRoot(
                AppDataOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }

        internal static string GetLocalApplicationDataRoot()
        {
            return ResolveRoot(
                LocalAppDataOverrideVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        internal static bool IsSupportedFile(string path)
        {
            var name = Path.GetFileName(path);
            return string.Equals(name, "renderer.log", StringComparison.OrdinalIgnoreCase)
                || IsZCodeCliLog(path)
                || IsZCodeDesktopLog(path)
                || IsChatGPTSession(path)
                || IsClaudeTranscript(path)
                || IsDoubaoLog(path)
                || IsWorkBuddyLog(path);
        }

        internal static bool IsTraeLog(string path)
        {
            return IsInside(path, "Trae CN/logs") || IsInside(path, "TraeCode CN/logs") ||
                   IsInside(path, "TRAE SOLO CN/logs") ||
                   IsInside(path, "TraeWork CN/logs");
        }

        // TraeWork CN（原 TRAE SOLO CN）的 renderer 日志；官方更名后目录未迁移，
        // 因此新旧两个目录名都视为 TraeWork 的日志。
        internal static bool IsTraeWorkRendererLog(string path)
        {
            return (IsInside(path, "TRAE SOLO CN/logs") || IsInside(path, "TraeWork CN/logs")) &&
                   string.Equals(Path.GetFileName(path), "renderer.log", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsZCodeCliLog(string path)
        {
            return IsInside(path, ".zcode/cli/log") &&
                   string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsZCodeDesktopLog(string path)
        {
            return IsInside(path, ".zcode/v2/logs") &&
                   string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsChatGPTSession(string path)
        {
            return IsInside(path, ".codex/sessions") &&
                   string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDoubaoLog(string path)
        {
            return IsInside(path, "Doubao/User Data/sdk_storage/log") &&
                   Path.GetFileName(path).StartsWith("saman_", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWorkBuddyLog(string path)
        {
            return IsInside(path, ".workbuddy/logs") &&
                   string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsClaudeTranscript(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = WrapNormalizedPath(path);
            var isClaudePath = normalized.IndexOf("/.claude/projects/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               normalized.IndexOf("/.claude/transcripts/", StringComparison.OrdinalIgnoreCase) >= 0;
            return isClaudePath &&
                   normalized.IndexOf("/subagents/", StringComparison.OrdinalIgnoreCase) < 0 &&
                   normalized.IndexOf("/tool-results/", StringComparison.OrdinalIgnoreCase) < 0;
        }

        internal static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static bool IsInside(string path, string relativeDirectory)
        {
            var normalized = WrapNormalizedPath(path);
            var marker = "/" + NormalizePath(relativeDirectory).Trim('/') + "/";
            return normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string WrapNormalizedPath(string path)
        {
            return "/" + NormalizePath(path).Trim('/') + "/";
        }

        private static string ResolveRoot(string environmentVariable, string fallback)
        {
            var overridden = Environment.GetEnvironmentVariable(environmentVariable);
            var selected = string.IsNullOrWhiteSpace(overridden) ? fallback : overridden.Trim();
            return string.IsNullOrWhiteSpace(selected) ? string.Empty : Path.GetFullPath(selected);
        }
    }
}
