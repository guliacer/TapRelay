using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// TraeWork CN（原 TRAE SOLO CN）的 renderer.log 路径 → 工作区本地目录反查。
    /// TraeWork 的 DONE 事件不带 cwd，会话正文又在云端；但每个工作区窗口的
    /// workspaceStorage\&lt;hash&gt;\state.vscdb 里记有 trae.workbench.logsHome
    /// （指向该窗口的 logs\&lt;时间戳&gt; 目录）。监控器手里正好有 renderer.log
    /// 全路径，按 "logs\&lt;时间戳&gt;" 段匹配即可定位 workspaceStorage，
    /// 再读 workspace.json（TraeWork 是多文件夹工作区，指向
    /// Workspaces\&lt;id&gt;\workspace.json，二级跳转后取 folders[0].path）。
    /// 与 TraeWorkspaceResolver 同为字节扫描手法，不依赖 SQLite 驱动。
    /// </summary>
    internal static class TraeWorkWorkspaceResolver
    {
        private static readonly object Gate = new object();
        // logs 目录名 → 工作区目录；未命中不缓存，下次文件变化后可能命中。
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private const long MaxDatabaseSizeBytes = 32L * 1024 * 1024;

        internal static bool TryResolveWorkspaceFolder(string logPath, out string workspaceFolder)
        {
            workspaceFolder = string.Empty;
            var logsDirectoryName = ExtractLogsDirectoryName(logPath);
            if (string.IsNullOrEmpty(logsDirectoryName))
            {
                return false;
            }

            lock (Gate)
            {
                if (Cache.TryGetValue(logsDirectoryName, out var cached))
                {
                    workspaceFolder = cached;
                    return true;
                }
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var root in new[]
                     {
                         Path.Combine(appData, "TRAE SOLO CN"),
                         Path.Combine(appData, "TraeWork CN")
                     })
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

                    // logsHome 值形如 c:\...\logs\20260821T133204；按 logs\<name> 匹配。
                    if (!FileContainsText(database, "logs\\" + logsDirectoryName) &&
                        !FileContainsText(database, "logs/" + logsDirectoryName))
                    {
                        continue;
                    }

                    if (TryReadWorkspaceFolder(meta, out var folder))
                    {
                        lock (Gate)
                        {
                            Cache[logsDirectoryName] = folder;
                        }
                        workspaceFolder = folder;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 从 ...\logs\20260821T133204\window1\renderer.log 提取 "20260821T133204"。
        /// </summary>
        internal static string ExtractLogsDirectoryName(string logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return string.Empty;
            }

            var normalized = logPath.Replace('/', '\\');
            var marker = normalized.LastIndexOf("\\logs\\", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return string.Empty;
            }

            var rest = normalized.Substring(marker + "\\logs\\".Length);
            var separator = rest.IndexOf('\\');
            var name = separator < 0 ? rest : rest.Substring(0, separator);
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
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

                return Encoding.ASCII.GetString(buffer, 0, offset)
                    .Contains(needle, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// 解析 workspaceStorage 的 workspace.json。两种形态：
        ///   单文件夹（TraeCode 式）：{"folder":"file:///w%3A/TapRelay"}
        ///   多文件夹（TraeWork 式）：{"workspace":"file:///.../Workspaces/&lt;id&gt;/workspace.json"}
        ///     → 二级文件 {"folders":[{"path":"W:/TapRelay"}]}
        /// </summary>
        internal static bool TryReadWorkspaceFolder(string metaPath, out string folder)
        {
            folder = string.Empty;
            try
            {
                if (!TryReadMetaTarget(metaPath, out var folderUri, out var workspaceUri))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(folderUri))
                {
                    return TryToLocalFolder(folderUri, out folder);
                }

                if (!string.IsNullOrEmpty(workspaceUri))
                {
                    // file:///c%3A/.../Workspaces/<id>/workspace.json → 本地路径后读取二级文件。
                    if (!Uri.TryCreate(workspaceUri, UriKind.Absolute, out var parsed) ||
                        !string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var nested = Uri.UnescapeDataString(parsed.AbsolutePath);
                    if (nested.StartsWith("/", StringComparison.Ordinal))
                    {
                        nested = nested.Substring(1);
                    }

                    nested = nested.Replace('/', '\\');
                    if (!File.Exists(nested))
                    {
                        return false;
                    }

                    var json = File.ReadAllText(nested);
                    using var document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty("folders", out var folders) ||
                        folders.ValueKind != JsonValueKind.Array ||
                        folders.GetArrayLength() == 0)
                    {
                        return false;
                    }

                    for (var i = 0; i < folders.GetArrayLength(); i++)
                    {
                        if (!folders[i].TryGetProperty("path", out var pathElement) ||
                            pathElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var candidate = pathElement.GetString();
                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            continue;
                        }

                        // "W:/TapRelay" 或 "file:///w%3A/TapRelay" 都接受。
                        if (candidate.IndexOf("file://", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (TryToLocalFolder(candidate, out var local))
                            {
                                folder = local;
                                return true;
                            }
                            continue;
                        }

                        var localPath = candidate.Replace('/', '\\');
                        if (Directory.Exists(localPath))
                        {
                            folder = localPath;
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool TryReadMetaTarget(string metaPath, out string folderUri, out string workspaceUri)
        {
            folderUri = string.Empty;
            workspaceUri = string.Empty;
            var json = File.ReadAllText(metaPath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("folder", out var folderElement) &&
                folderElement.ValueKind == JsonValueKind.String)
            {
                folderUri = folderElement.GetString() ?? string.Empty;
                return true;
            }

            if (document.RootElement.TryGetProperty("workspace", out var workspaceElement) &&
                workspaceElement.ValueKind == JsonValueKind.String)
            {
                workspaceUri = workspaceElement.GetString() ?? string.Empty;
                return true;
            }

            return false;
        }

        internal static bool TryToLocalFolder(string uriText, out string folder)
        {
            folder = string.Empty;
            if (string.IsNullOrWhiteSpace(uriText))
            {
                return false;
            }

            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var parsed) ||
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
    }
}
