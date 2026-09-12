using System;

namespace TapRelay
{
    internal static class NotificationTitleResolver
    {
        private const int MaxTitleLength = 120;

        internal static string Resolve(
            string source,
            string status,
            string title,
            string fallbackDisplayName)
        {
            var provided = (title ?? string.Empty).Trim();
            if (provided.Length > 0)
            {
                return Truncate(provided);
            }

            if (!string.Equals(source, "workbuddy", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var displayName = (fallbackDisplayName ?? string.Empty).Trim();
            if (displayName.Length == 0)
            {
                displayName = "WorkBuddy";
            }

            var stateTitle = (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "waiting" => "\u9700\u8981\u786e\u8ba4",
                "failed" => "\u4efb\u52a1\u5931\u8d25",
                "stopped" => "\u5df2\u505c\u6b62",
                _ => "\u4efb\u52a1\u5b8c\u6210"
            };
            return Truncate(displayName + " \u00b7 " + stateTitle);
        }

        private static string Truncate(string value) =>
            value.Length <= MaxTitleLength ? value : value.Substring(0, MaxTitleLength);
    }
}
