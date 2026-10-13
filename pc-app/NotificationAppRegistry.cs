using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Stores user-supplied application metadata. This is configuration, not message history.
    /// </summary>
    public sealed class RegisteredAppDefinition
    {
        public string AppKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string BrandColor { get; set; } = string.Empty;
        public string IconBase64 { get; set; } = string.Empty;
    }

    public static class NotificationAppRegistry
    {
        private const int MaxKeyLength = 64;
        private const int MaxDisplayNameLength = 80;
        private const int MaxIconBytes = 512 * 1024;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> ReservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "claude", "codex", "trae", "trae-solo", "traework", "zcode", "doubao",
            "suyan", "baidunetdisk", "quarknetdisk", "quarknetdisk-upload", "adrive", "adrive-upload", "workbuddy", "workbuddy-ai", "comfyui", "opencode", "deepseekharness", "freebuff", "taprelay"
        };
        private static readonly string RegistryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "notification-app-registry.json");
        private static Dictionary<string, RegisteredAppDefinition>? _apps;

        public static IReadOnlyList<RegisteredAppDefinition> GetAll()
        {
            lock (Gate)
            {
                EnsureLoadedLocked();
                return _apps!.Values
                    .OrderBy(item => item.AppKey, StringComparer.OrdinalIgnoreCase)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public static bool Contains(string appKey)
        {
            var key = NormalizeKey(appKey);
            if (key.Length == 0) return false;
            lock (Gate)
            {
                EnsureLoadedLocked();
                return _apps!.ContainsKey(key);
            }
        }

        public static bool TryGet(string appKey, out RegisteredAppDefinition definition)
        {
            var key = NormalizeKey(appKey);
            lock (Gate)
            {
                EnsureLoadedLocked();
                if (key.Length > 0 && _apps!.TryGetValue(key, out var found))
                {
                    definition = Clone(found);
                    return true;
                }
            }

            definition = new RegisteredAppDefinition();
            return false;
        }

        public static bool TryGetIcon(string appKey, out byte[] iconBytes)
        {
            if (TryGet(appKey, out var definition))
            {
                try
                {
                    iconBytes = Convert.FromBase64String(definition.IconBase64);
                    return iconBytes.Length > 0;
                }
                catch (FormatException)
                {
                    // Treat a damaged persisted icon as a missing icon.
                }
            }

            iconBytes = Array.Empty<byte>();
            return false;
        }

        public static bool TryRegister(
            string appKey,
            string displayName,
            string brandColor,
            string iconBase64,
            out RegisteredAppDefinition definition,
            out string error)
        {
            return TryRegisterInternal(
                appKey,
                displayName,
                brandColor,
                iconBase64,
                allowReservedKey: false,
                out definition,
                out error);
        }

        private static bool TryRegisterInternal(
            string appKey,
            string displayName,
            string brandColor,
            string iconBase64,
            bool allowReservedKey,
            out RegisteredAppDefinition definition,
            out string error)
        {
            definition = new RegisteredAppDefinition();
            error = string.Empty;

            var key = NormalizeKey(appKey);
            if (!IsValidKey(key))
            {
                error = "appKey must match [a-z0-9][a-z0-9._-]{0,63}";
                return false;
            }
            if (!allowReservedKey && ReservedKeys.Contains(key))
            {
                error = "appKey is reserved by the built-in application catalog";
                return false;
            }
            if (UnsupportedAppRegistry.TryGetMessage(key, out var unsupportedMessage))
            {
                error = unsupportedMessage;
                return false;
            }

            var name = (displayName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaxDisplayNameLength)
            {
                error = "displayName must be 1..80 characters";
                return false;
            }

            var color = (brandColor ?? string.Empty).Trim();
            if (!IsValidBrandColor(color))
            {
                error = "brandColor must be a #RRGGBB value";
                return false;
            }

            if (!TryDecodePng(iconBase64, out _, out error))
            {
                return false;
            }

            var item = new RegisteredAppDefinition
            {
                AppKey = key,
                DisplayName = name,
                BrandColor = color.ToUpperInvariant(),
                IconBase64 = StripDataUrlPrefix(iconBase64).Trim()
            };

            lock (Gate)
            {
                EnsureLoadedLocked();
                _apps![key] = item;
                SaveLocked();
                definition = Clone(item);
            }
            return true;
        }

        private static void EnsureLoadedLocked()
        {
            if (_apps != null) return;

            _apps = new Dictionary<string, RegisteredAppDefinition>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(RegistryPath)) return;
                var document = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(RegistryPath));
                var removedUnsupportedApp = false;
                foreach (var item in document?.Apps ?? new List<RegisteredAppDefinition>())
                {
                    var key = NormalizeKey(item.AppKey);
                    if (!IsValidKey(key) || ReservedKeys.Contains(key) ||
                        UnsupportedAppRegistry.IsUnsupported(key) ||
                        string.IsNullOrWhiteSpace(item.DisplayName) ||
                        item.DisplayName.Trim().Length > MaxDisplayNameLength ||
                        !IsValidBrandColor(item.BrandColor) ||
                        !TryDecodePng(item.IconBase64, out _, out _))
                    {
                        removedUnsupportedApp |= UnsupportedAppRegistry.IsUnsupported(key);
                        continue;
                    }

                    item.AppKey = key;
                    item.DisplayName = item.DisplayName.Trim();
                    item.BrandColor = item.BrandColor.Trim().ToUpperInvariant();
                    item.IconBase64 = StripDataUrlPrefix(item.IconBase64).Trim();
                    _apps[key] = item;
                }

                if (removedUnsupportedApp)
                {
                    SaveLocked();
                }
            }
            catch
            {
                // A broken registry must not prevent the PC app from starting.
                _apps.Clear();
            }
        }

        private static void SaveLocked()
        {
            var directory = Path.GetDirectoryName(RegistryPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var payload = new RegistryDocument
            {
                Apps = _apps!.Values.OrderBy(item => item.AppKey, StringComparer.OrdinalIgnoreCase).ToList()
            };
            var tempPath = RegistryPath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
                File.Copy(tempPath, RegistryPath, true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static bool TryDecodePng(string input, out byte[] bytes, out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                bytes = Convert.FromBase64String(StripDataUrlPrefix(input).Trim());
            }
            catch (FormatException)
            {
                error = "iconBase64 must be valid base64 PNG data";
                return false;
            }

            if (bytes.Length == 0 || bytes.Length > MaxIconBytes)
            {
                error = "iconBase64 PNG must be between 1 byte and 512 KiB";
                return false;
            }

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width != 256 || image.Height != 256)
                {
                    error = "iconBase64 must contain a 256x256 PNG";
                    return false;
                }
            }
            catch (ArgumentException)
            {
                error = "iconBase64 must contain a valid PNG";
                return false;
            }

            return true;
        }

        private static string StripDataUrlPrefix(string input)
        {
            var value = input ?? string.Empty;
            const string prefix = "data:image/png;base64,";
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length)
                : value;
        }

        private static string NormalizeKey(string input) => (input ?? string.Empty).Trim().ToLowerInvariant();

        private static bool IsValidKey(string key)
        {
            if (key.Length == 0 || key.Length > MaxKeyLength) return false;
            if (!IsAsciiLetterOrDigit(key[0])) return false;
            return key.All(ch => IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        private static bool IsAsciiLetterOrDigit(char value) =>
            value >= 'a' && value <= 'z' || value >= '0' && value <= '9';

        private static bool IsValidBrandColor(string value)
        {
            if (value.Length != 7 || value[0] != '#') return false;
            return value.Skip(1).All(ch =>
                ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f' || ch >= 'A' && ch <= 'F');
        }

        private static RegisteredAppDefinition Clone(RegisteredAppDefinition item) => new RegisteredAppDefinition
        {
            AppKey = item.AppKey,
            DisplayName = item.DisplayName,
            BrandColor = item.BrandColor,
            IconBase64 = item.IconBase64
        };

        private sealed class RegistryDocument
        {
            public List<RegisteredAppDefinition> Apps { get; set; } = new List<RegisteredAppDefinition>();
        }
    }
}
