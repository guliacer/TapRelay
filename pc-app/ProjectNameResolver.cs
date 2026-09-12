using System;
using System.Linq;

namespace TapRelay
{
    internal static class ProjectNameResolver
    {
        internal static string ResolveFolderFirst(string explicitName, string cwd, string taskId, string message)
        {
            var folder = DirectoryLeaf(cwd);
            if (!string.IsNullOrWhiteSpace(folder)) return folder;
            return Resolve(explicitName, cwd, taskId, message);
        }

        internal static string NormalizeForSource(string source, string projectName)
        {
            if (string.Equals(source, "suyan", StringComparison.OrdinalIgnoreCase))
            {
                return "生图任务";
            }
            return (projectName ?? string.Empty).Trim();
        }

        internal static string ResolveNetdiskFolder(string path, string itemName = "", bool pathIsDirectory = false)
        {
            var value = (path ?? string.Empty).Trim().Trim('"').Replace('/', '\\');
            if (value.Length == 0) return string.Empty;

            var hasTrailingSeparator = value.EndsWith("\\", StringComparison.Ordinal);
            var trimmed = value.TrimEnd('\\');
            if (trimmed.Length == 0) return string.Empty;
            if (hasTrailingSeparator || pathIsDirectory) return DirectoryLeaf(trimmed);

            var separator = trimmed.LastIndexOf('\\');
            if (separator < 0) return string.Empty;

            var leaf = trimmed.Substring(separator + 1);
            var itemLeaf = LastPathSegment(itemName);
            if (!string.IsNullOrWhiteSpace(itemLeaf) &&
                string.Equals(leaf, itemLeaf, StringComparison.OrdinalIgnoreCase))
            {
                return DirectoryLeaf(trimmed.Substring(0, separator));
            }

            // Some task stores keep the destination folder in Path and the file
            // name separately. In that shape the path leaf is already the folder.
            return string.IsNullOrWhiteSpace(itemLeaf)
                ? DirectoryLeaf(trimmed.Substring(0, separator))
                : Limit(leaf, 120);
        }

        internal static string Resolve(string explicitName, string cwd, string taskId, string message)
        {
            var name = Normalize(explicitName);
            if (!string.IsNullOrWhiteSpace(name)) return name;

            var directory = Normalize(cwd);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var leaf = DirectoryLeaf(directory);
                if (!string.IsNullOrWhiteSpace(leaf)) return Limit(leaf, 120);
            }

            var detail = Normalize(message);
            if (!string.IsNullOrWhiteSpace(detail) && !LooksLikeGeneratedStatus(detail))
            {
                var firstLine = detail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine)) return Limit(firstLine, 120);
            }

            var token = Normalize(taskId);
            if (!string.IsNullOrWhiteSpace(token))
            {
                var shortToken = token.Length > 24 ? token.Substring(0, 24) : token;
                return "任务 " + shortToken;
            }

            return "未命名任务";
        }

        private static string DirectoryLeaf(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (normalized.Length == 0 || normalized.EndsWith(":", StringComparison.Ordinal)) return string.Empty;
            return LastPathSegment(normalized);
        }

        private static string LastPathSegment(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (normalized.Length == 0) return string.Empty;
            var separator = normalized.LastIndexOf('\\');
            return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
        }

        internal static string Normalize(string value)
        {
            var normalized = string.Join(" ", (value ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return Limit(normalized, 120);
        }

        private static bool LooksLikeGeneratedStatus(string value)
        {
            return value.StartsWith("[", StringComparison.Ordinal) ||
                   value.IndexOf("任务已完成", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("task completed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("任务失败", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("task failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("批量结束", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Limit(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength) + "...";
        }
    }
}
