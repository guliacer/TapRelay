using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TapRelay
{
    internal static class AppInstallRoots
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static string PersistencePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "app-install-roots.json");

        internal static void Set(string appKey, string path)
        {
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(path)) return;
            var fullPath = Path.GetFullPath(path.Trim());
            var key = appKey.Trim().ToLowerInvariant();
            lock (Gate)
            {
                EnsureLoadedLocked();
                Overrides[key] = fullPath;
                SaveLocked();
            }
            Environment.SetEnvironmentVariable("TAPRELAY_" + key.ToUpperInvariant().Replace('-', '_') + "_ROOT", fullPath);
        }

        internal static string? Get(string appKey)
        {
            var key = appKey.Trim().ToLowerInvariant();
            lock (Gate)
            {
                EnsureLoadedLocked();
                if (Overrides.TryGetValue(key, out var path)) return path;
            }
            return Environment.GetEnvironmentVariable("TAPRELAY_" + key.ToUpperInvariant().Replace('-', '_') + "_ROOT");
        }

        internal static IReadOnlyList<(string AppKey, string Path)> Snapshot()
        {
            lock (Gate)
            {
                EnsureLoadedLocked();
                return Overrides.Select(item => (item.Key, item.Value)).ToArray();
            }
        }

        private static void EnsureLoadedLocked()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(PersistencePath)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PersistencePath));
                if (data == null) return;
                foreach (var item in data)
                {
                    if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) continue;
                    var path = Path.GetFullPath(item.Value.Trim());
                    if (Directory.Exists(path))
                    {
                        Overrides[item.Key.Trim().ToLowerInvariant()] = path;
                    }
                }
            }
            catch
            {
                // 损坏的本地缓存不应阻断应用目录检测。
            }
        }

        private static void SaveLocked()
        {
            try
            {
                var folder = Path.GetDirectoryName(PersistencePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(PersistencePath, JsonSerializer.Serialize(Overrides));
            }
            catch
            {
                // 手动选择仍在当前会话中有效，持久化失败不影响使用。
            }
        }
    }
}
