using System;
using System.IO;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Resolves a Codex working directory to the name shown by the Codex desktop
    /// project's sidebar. The rollout JSONL contains the directory, while the
    /// human-facing project name is persisted in .codex-global-state.json.
    /// </summary>
    internal static class CodexProjectResolver
    {
        private static readonly object Gate = new object();
        private static string _statePath = string.Empty;
        private static long _stateLength = -1L;
        private static DateTime _stateLastWriteUtc = DateTime.MinValue;
        private static string _stateJson = string.Empty;

        internal static string Resolve(string workingDirectory)
        {
            var cwd = NormalizePath(workingDirectory);
            if (cwd.Length == 0)
            {
                return string.Empty;
            }

            var json = ReadStateJson();
            return json.Length == 0 ? string.Empty : ResolveFromStateJson(json, cwd);
        }

        internal static string ResolveFromStateJson(string json, string workingDirectory)
        {
            var cwd = NormalizePath(workingDirectory);
            if (cwd.Length == 0 || string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("local-projects", out var projects) ||
                    projects.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                var bestRoot = string.Empty;
                var bestName = string.Empty;
                foreach (var project in projects.EnumerateObject())
                {
                    if (project.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = ReadString(project.Value, "name");
                    if (string.IsNullOrWhiteSpace(name) ||
                        !project.Value.TryGetProperty("rootPaths", out var roots) ||
                        roots.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var root in roots.EnumerateArray())
                    {
                        if (root.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var normalizedRoot = NormalizePath(root.GetString());
                        if (!IsSameOrChildPath(cwd, normalizedRoot) || normalizedRoot.Length <= bestRoot.Length)
                        {
                            continue;
                        }

                        bestRoot = normalizedRoot;
                        bestName = ProjectNameResolver.Normalize(name);
                    }
                }

                return bestName;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        internal static string NormalizePath(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(normalized).TrimEnd('\\');
            }
            catch (ArgumentException)
            {
                return normalized;
            }
            catch (NotSupportedException)
            {
                return normalized;
            }
        }

        private static bool IsSameOrChildPath(string path, string root)
        {
            if (path.Length == 0 || root.Length == 0)
            {
                return false;
            }

            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.Length > root.Length &&
                   path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   path[root.Length] == '\\';
        }

        private static string ReadStateJson()
        {
            var userProfile = AgentLogPaths.GetUserProfileRoot();
            var path = Path.Combine(userProfile, ".codex", ".codex-global-state.json");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return string.Empty;
                }

                lock (Gate)
                {
                    if (string.Equals(_statePath, path, StringComparison.OrdinalIgnoreCase) &&
                        _stateLength == info.Length &&
                        _stateLastWriteUtc == info.LastWriteTimeUtc)
                    {
                        return _stateJson;
                    }

                    _statePath = path;
                    _stateLength = info.Length;
                    _stateLastWriteUtc = info.LastWriteTimeUtc;
                    try
                    {
                        _stateJson = File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        _stateJson = string.Empty;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _stateJson = string.Empty;
                    }

                    return _stateJson;
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
