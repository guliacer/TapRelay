using System;
using System.Collections.Generic;
using System.IO;

namespace TapRelay
{
    /// <summary>
    /// Applications that are deliberately excluded from adaptation.
    /// This is only a user-facing blacklist; it contains no install or monitor configuration.
    /// </summary>
    internal static class UnsupportedAppRegistry
    {
        internal const string QwenKey = "qwen";
        internal const string QwenMessage =
            "\u5343\u95ee\u6682\u4e0d\u652f\u6301\u4efb\u52a1\u901a\u77e5\uff0c\u6682\u4e0d\u9002\u914d\u3002";

        private static readonly HashSet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "qwen",
            "qianwen",
            "\u5343\u95ee"
        };

        internal static bool IsUnsupported(string value)
        {
            return TryGetMessage(value, out _);
        }

        internal static bool TryGetMessage(string value, out string message)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (Aliases.Contains(normalized))
            {
                message = QwenMessage;
                return true;
            }

            message = string.Empty;
            return false;
        }

        internal static bool LooksLikeUnsupportedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return Aliases.Contains(leaf) ||
                   leaf.IndexOf("qwen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   leaf.IndexOf("qianwen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string GetMessage()
        {
            return QwenMessage;
        }
    }
}
