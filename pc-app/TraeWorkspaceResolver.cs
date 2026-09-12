using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TapRelay
{
    /// <summary>
    /// TraeCode CN（原 Trae CN）sessionId → 工作区本地目录反查。
    /// 会话按工作区存储在 User\workspaceStorage\&lt;hash&gt;\state.vscdb（SQLite），
    /// 以只读共享方式打开（运行中的 IDE 持有写锁），字节包含 sessionId 即命中，
    /// 再读同目录 workspace.json 的 file URI 得到本地路径，供完成通知解析项目名使用。
    /// </summary>
    internal static class TraeWorkspaceResolver
    {
        private static readonly object Gate = new object();
        // 仅缓存命中结果；未命中不缓存——state.vscdb 在会话期间持续写入，晚些时候可能命中。
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private const long MaxDatabaseSizeBytes = 32L * 1024 * 1024;

        internal static bool TryResolveWorkspaceFolder(string sessionId, out string workspaceFolder)
        {
            workspaceFolder = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            lock (Gate)
            {
                if (Cache.TryGetValue(sessionId, out var cached))
                {
                    workspaceFolder = cached;
                    return true;
                }
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var productRoots = new[]
            {
                Path.Combine(appData, "Trae CN"),
                Path.Combine(appData, "TraeCode CN")
            };

            foreach (var root in productRoots)
            {
                var storage = Path.Combine(root, "User", "workspaceStorage");
                if (!Directory.Exists(storage))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateDirectories(storage))
                {
                    var database = Path.Combine(entry, "state.vscdb");
                    var meta = Path.Combine(entry, "workspace.json");
                    if (!File.Exists(database) || !File.Exists(meta))
                    {
                        continue;
                    }

                    if (!FileContainsText(database, sessionId))
                    {
                        continue;
                    }

                    if (TryReadWorkspaceFolder(meta, out var folder))
                    {
                        lock (Gate)
                        {
                            Cache[sessionId] = folder;
                        }
                        workspaceFolder = folder;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool FileContainsText(string path, string needle)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= 0 || stream.Length > MaxDatabaseSizeBytes)
                {
                    return false;
                }

                var buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }
                    offset += read;
                }

                return Encoding.ASCII.GetString(buffer, 0, offset).Contains(needle, StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryReadWorkspaceFolder(string metaPath, out string folder)
        {
            folder = string.Empty;
            try
            {
                var json = File.ReadAllText(metaPath);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("folder", out var folderElement) ||
                    folderElement.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    return false;
                }

                var uri = folderElement.GetString();
                if (string.IsNullOrWhiteSpace(uri))
                {
                    return false;
                }

                // file:///w%3A/%E7%94%B5... → "/w:/..." → "w:\..."
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
                    !string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var local = Uri.UnescapeDataString(parsed.AbsolutePath);
                if (local.StartsWith("/", StringComparison.Ordinal))
                {
                    local = local.Substring(1);
                }

                local = local.Replace('/', '\\');
                if (local.Length == 0 || !Directory.Exists(local))
                {
                    return false;
                }

                folder = local;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
