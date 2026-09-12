using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace TapRelay
{
    internal static class ClaudeProjectResolver
    {
        private static readonly Regex DirectoryChangePattern = new Regex(
            @"(?:^|[;&|\r\n])\s*!?\s*(?:cd|Set-Location|Push-Location)\b(?<arguments>[^;&|\r\n]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        internal static string ResolvePromptDirectory(string prompt, string baseDirectory = "")
        {
            return ResolveCommandDirectory(prompt, baseDirectory);
        }

        internal static string ResolveToolUseDirectory(JsonElement root, string baseDirectory = "")
        {
            return FindToolUseDirectory(root, baseDirectory, 0);
        }

        internal static string PreferWorkingDirectory(
            string rememberedDirectory,
            string candidateDirectory,
            string userProfileRoot)
        {
            var remembered = NormalizeDirectory(rememberedDirectory);
            var candidate = NormalizeDirectory(candidateDirectory);
            if (candidate.Length == 0)
            {
                return remembered;
            }

            if (remembered.Length > 0 &&
                IsUserProfileRoot(candidate, userProfileRoot) &&
                !IsUserProfileRoot(remembered, userProfileRoot))
            {
                return remembered;
            }

            return candidate;
        }

        private static string ResolveCommandDirectory(string command, string baseDirectory)
        {
            var resolved = string.Empty;
            var matches = DirectoryChangePattern.Matches(command ?? string.Empty);
            for (var index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                var candidate = ExtractDirectory(match.Groups["arguments"].Value, baseDirectory);
                if (candidate.Length > 0)
                {
                    resolved = candidate;
                }
            }

            return resolved;
        }

        private static string FindToolUseDirectory(JsonElement element, string baseDirectory, int depth)
        {
            if (depth > 12)
            {
                return string.Empty;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                var resolved = string.Empty;
                if (string.Equals(ReadString(element, "type"), "tool_use", StringComparison.OrdinalIgnoreCase) &&
                    element.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Object)
                {
                    var command = ReadString(input, "command");
                    var directory = ResolveCommandDirectory(command, baseDirectory);
                    if (directory.Length > 0)
                    {
                        resolved = directory;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    var directory = FindToolUseDirectory(property.Value, baseDirectory, depth + 1);
                    if (directory.Length > 0)
                    {
                        resolved = directory;
                    }
                }

                return resolved;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                var resolved = string.Empty;
                foreach (var item in element.EnumerateArray())
                {
                    var directory = FindToolUseDirectory(item, baseDirectory, depth + 1);
                    if (directory.Length > 0)
                    {
                        resolved = directory;
                    }
                }

                return resolved;
            }

            return string.Empty;
        }

        private static string ExtractDirectory(string arguments, string baseDirectory)
        {
            var value = (arguments ?? string.Empty).Trim();
            value = RemoveLeadingOptions(value);
            while (value.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(3).TrimStart();
            }

            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (value[0] == '\'' || value[0] == '"')
            {
                var quote = value[0];
                var end = value.IndexOf(quote, 1);
                return end > 1
                    ? ResolvePath(value.Substring(1, end - 1), baseDirectory)
                    : string.Empty;
            }

            var unquoted = value;
            var existingPrefix = FindExistingDirectoryPrefix(unquoted);
            if (existingPrefix.Length > 0)
            {
                return ResolvePath(existingPrefix, baseDirectory);
            }

            var separator = unquoted.IndexOfAny(new[] { ' ', '\t' });
            var firstToken = separator >= 0 ? unquoted.Substring(0, separator) : unquoted;
            return ResolvePath(firstToken, baseDirectory);
        }

        private static string RemoveLeadingOptions(string value)
        {
            var remaining = value;
            while (remaining.StartsWith("/d ", StringComparison.OrdinalIgnoreCase) ||
                   remaining.StartsWith("-LiteralPath ", StringComparison.OrdinalIgnoreCase) ||
                   remaining.StartsWith("-Path ", StringComparison.OrdinalIgnoreCase))
            {
                var separator = remaining.IndexOf(' ');
                remaining = separator >= 0 ? remaining.Substring(separator + 1).TrimStart() : string.Empty;
            }

            return remaining;
        }

        private static string FindExistingDirectoryPrefix(string value)
        {
            var candidate = value.Trim();
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            for (var index = candidate.Length - 1; index > 0; index--)
            {
                if (candidate[index] != ' ' && candidate[index] != '\t')
                {
                    continue;
                }

                var prefix = candidate.Substring(0, index).TrimEnd();
                if (prefix.Length > 0 && Directory.Exists(prefix))
                {
                    return prefix;
                }
            }

            return string.Empty;
        }

        private static string ResolvePath(string value, string baseDirectory)
        {
            var path = (value ?? string.Empty).Trim().Trim('"', '\'');
            if (path.Length == 0)
            {
                return string.Empty;
            }

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(baseDirectory))
            {
                try
                {
                    path = Path.Combine(baseDirectory, path);
                }
                catch (ArgumentException)
                {
                    return string.Empty;
                }
            }

            return NormalizeDirectory(path);
        }

        private static string NormalizeDirectory(string value)
        {
            var path = (value ?? string.Empty).Trim().Trim('"', '\'');
            if (path.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
                return path.TrimEnd('\\', '/');
            }
            catch (NotSupportedException)
            {
                return path.TrimEnd('\\', '/');
            }
        }

        private static bool IsUserProfileRoot(string candidate, string userProfileRoot)
        {
            var normalizedCandidate = NormalizeDirectory(candidate);
            var normalizedRoot = NormalizeDirectory(userProfileRoot);
            return normalizedCandidate.Length > 0 &&
                   normalizedRoot.Length > 0 &&
                   string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

    }
}
