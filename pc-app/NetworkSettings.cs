using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// 审批/等待类通知的粗粒度姿态总开关（对齐 tlive 的 off/notify/full 阶梯）。
    /// 只影响“推送到手机”的形态，绝不改变 agent 本地的审批语义：
    /// - Full：完整远程控制，审批卡片带允许/拒绝按钮（默认，即原行为）。
    /// - Notify：只读观察，审批卡片去掉按钮、不阻塞 agent（本地对话接管）。
    /// - Off：总闸关闭，waiting/审批通知一律不推送；完成/失败照常。
    /// </summary>
    public enum ApprovalPosture
    {
        Full,
        Notify,
        Off
    }

    /// <summary>
    /// Network settings used by the local WebSocket server.
    /// ListenAddress controls the local interface; AdvertisedAddress/Port is the
    /// endpoint printed to the UI and embedded in the phone QR code. This keeps
    /// public port-forwarding/DNS settings separate from the local bind address.
    /// </summary>
    public sealed class NotifyServerConfig
    {
        public const int DefaultPort = 1122;
        /// <summary>Default relay server URL for TapRelay cloud relay.</summary>
        public const string DefaultRelayUrl = "";

        public string ListenAddress { get; set; } = "0.0.0.0";
        public int ListenPort { get; set; } = DefaultPort;
        public string AdvertisedAddress { get; set; } = string.Empty;
        public int AdvertisedPort { get; set; } = DefaultPort;

        /// <summary>
        /// Relay server URL (ws:// or wss://). Empty = relay disabled.
        /// When set, TapRelay connects to this relay so phones on different
        /// networks can receive notifications without port forwarding.
        /// </summary>
        public string RelayUrl { get; set; } = string.Empty;

        /// <summary>
        /// Room identifier shared between PC and phone for relay pairing.
        /// Auto-generated on first use; persisted to survive restarts.
        /// </summary>
        public string RelayRoomId { get; set; } = string.Empty;

        /// <summary>
        /// 审批/等待通知的姿态总开关。默认 Full（原行为）。
        /// </summary>
        public ApprovalPosture ApprovalPosture { get; set; } = ApprovalPosture.Full;

        public int EffectiveAdvertisedPort => AdvertisedPort > 0 ? AdvertisedPort : ListenPort;

        /// <summary>True when relay mode is configured and enabled.</summary>
        public bool RelayEnabled => !string.IsNullOrWhiteSpace(RelayUrl) && !string.IsNullOrWhiteSpace(RelayRoomId);

        public NotifyServerConfig Clone()
        {
            return new NotifyServerConfig
            {
                ListenAddress = ListenAddress,
                ListenPort = ListenPort,
                AdvertisedAddress = AdvertisedAddress,
                AdvertisedPort = AdvertisedPort,
                RelayUrl = RelayUrl,
                RelayRoomId = RelayRoomId,
                ApprovalPosture = ApprovalPosture
            };
        }

        public NotifyServerConfig Normalize()
        {
            var normalized = Clone();
            normalized.ListenAddress = NormalizeListenAddress(normalized.ListenAddress);
            normalized.ListenPort = NormalizePort(normalized.ListenPort, "监听端口");
            normalized.AdvertisedAddress = NormalizeAdvertisedAddress(normalized.AdvertisedAddress);
            normalized.AdvertisedPort = normalized.AdvertisedPort == 0
                ? normalized.ListenPort
                : NormalizePort(normalized.AdvertisedPort, "对外端口");
            normalized.RelayUrl = (normalized.RelayUrl ?? string.Empty).Trim();
            normalized.RelayRoomId = (normalized.RelayRoomId ?? string.Empty).Trim();
            // Auto-generate a room ID if relay is configured but no room ID set.
            if (!string.IsNullOrWhiteSpace(normalized.RelayUrl) && string.IsNullOrWhiteSpace(normalized.RelayRoomId))
            {
                normalized.RelayRoomId = GenerateRoomId();
            }
            return normalized;
        }

        /// <summary>Generate a short, memorable room ID (6 uppercase alphanumeric chars).</summary>
        internal static string GenerateRoomId()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var random = new Random();
            var id = new char[6];
            for (var index = 0; index < id.Length; index++)
            {
                id[index] = chars[random.Next(chars.Length)];
            }
            return new string(id);
        }

        public static string NormalizeListenAddress(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed == "*")
            {
                return "0.0.0.0";
            }

            if (!IPAddress.TryParse(trimmed, out _))
            {
                throw new ArgumentException("监听地址必须是本机 IPv4/IPv6 地址，或 0.0.0.0。", nameof(value));
            }

            return trimmed;
        }

        public static string NormalizeAdvertisedAddress(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            foreach (var scheme in new[] { "ws://", "wss://", "http://", "https://" })
            {
                if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(scheme.Length);
                    break;
                }
            }

            var slash = trimmed.IndexOf('/');
            if (slash >= 0)
            {
                trimmed = trimmed.Substring(0, slash);
            }

            // Accept a host or domain here, but not a host:port: the port has a
            // dedicated field so it cannot be accidentally encoded twice.
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var closing = trimmed.IndexOf(']');
                if (closing < 0 || closing != trimmed.Length - 1)
                {
                    throw new ArgumentException("对外地址只填写 IP、域名或 IPv6 地址，不要附带端口。", nameof(value));
                }
                trimmed = trimmed.Substring(1, closing - 1);
            }

            if (trimmed.Length == 0 || trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            {
                throw new ArgumentException("对外地址不能为空或包含空格。", nameof(value));
            }

            if (IPAddress.TryParse(trimmed, out _))
            {
                return trimmed;
            }

            if (trimmed.IndexOf(':') >= 0)
            {
                throw new ArgumentException("对外地址只填写 IP、域名或 IPv6 地址，端口请填写在单独的对外端口中。", nameof(value));
            }

            // A conservative hostname check is enough for an advertised DNS
            // name; actual reachability is intentionally left to the user's
            // firewall/NAT/DNS setup.
            foreach (var character in trimmed)
            {
                if (!(char.IsLetterOrDigit(character) || character == '.' || character == '-'))
                {
                    throw new ArgumentException("对外地址必须是 IP 或合法域名。", nameof(value));
                }
            }

            return trimmed;
        }

        private static int NormalizePort(int port, string fieldName)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentException(fieldName + "必须在 1 到 65535 之间。", fieldName);
            }
            return port;
        }
    }

    internal static class NetworkSettingsStore
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "network-settings.json");

        internal static NotifyServerConfig Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var config = JsonSerializer.Deserialize<NotifyServerConfig>(File.ReadAllText(SettingsPath));
                    if (config != null)
                    {
                        return PinPort(config.Normalize());
                    }
                }
            }
            catch
            {
                // A malformed/old settings file should never prevent the PC UI
                // from opening; fall back to the safe all-interface LAN default.
            }

            return PinPort(new NotifyServerConfig().Normalize());
        }

        /// <summary>
        /// 铁律 R2：端口不可漂移。手机端把 1122 写死在代码里，服务端必须固定监听 1122，
        /// 因此历史配置文件里遗留的其他端口一律忽略。
        /// <para>
        /// 固定动作放在「配置入口」而不是 <c>NotifyServer</c> 内部：服务只负责按给定配置监听，
        /// 这样测试仍能用独立端口起一个真实的服务实例，不会和开发机上正在运行的实例抢 1122。
        /// 生产路径全部经由本方法取配置，R2 依然成立。
        /// </para>
        /// </summary>
        private static NotifyServerConfig PinPort(NotifyServerConfig config)
        {
            config.ListenPort = NotifyServerConfig.DefaultPort;
            config.AdvertisedPort = NotifyServerConfig.DefaultPort;
            return config;
        }

        internal static void Save(NotifyServerConfig config)
        {
            var normalized = config.Normalize();
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Small, versionable QR payload understood by the Android client.
    /// The QR never contains credentials; it only carries the endpoint that the
    /// user has explicitly configured/displayed in the PC app.
    /// </summary>
    public static class ConnectionQrProtocol
    {
        public const string Scheme = "taprelay";

        public static bool TryCreatePayload(string endpoint, out string payload, out string error)
        {
            payload = string.Empty;
            error = string.Empty;
            if (!Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss") ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                uri.Port < 1 || uri.Port > 65535)
            {
                error = "当前连接地址无效，无法生成二维码。";
                return false;
            }

            payload = Scheme + "://connect?host=" +
                      Uri.EscapeDataString(uri.Host) +
                      "&port=" + uri.Port;
            return true;
        }

        /// <summary>
        /// Encodes multiple candidate endpoints into a single QR payload so the
        /// phone can try them in order. The first endpoint becomes the primary
        /// <c>host</c>/<c>port</c>; the rest are appended as <c>alt1</c>, <c>alt2</c>...
        /// (each an escaped <c>host:port</c>). Older phone clients that only read
        /// <c>host</c>/<c>port</c> still work because the primary is always first.
        /// </summary>
        public static bool TryCreatePayload(IEnumerable<string> endpoints, out string payload, out string error)
        {
            payload = string.Empty;
            error = string.Empty;
            var list = (endpoints ?? Enumerable.Empty<string>())
                .Select(item => (item ?? string.Empty).Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (list.Count == 0)
            {
                error = "当前没有可用连接地址，请在网络设置中填写对外地址。";
                return false;
            }

            if (!TryParseEndpoint(list[0], out var host, out var port))
            {
                error = "当前连接地址无效，无法生成二维码。";
                return false;
            }

            var builder = new StringBuilder();
            builder.Append(Scheme)
                .Append("://connect?host=")
                .Append(Uri.EscapeDataString(host))
                .Append("&port=")
                .Append(port);

            var altIndex = 1;
            foreach (var endpoint in list.Skip(1))
            {
                // altN 携带完整端点（含 ws:// 或 wss:// scheme）：Cloudflare 隧道
                // 走 wss://域名（443），LAN 走 ws://host:port，拆成 host:port 会
                // 丢失 wss 导致手机连 1122 超时。
                if (TryParseEndpoint(endpoint, out var altHost, out _) &&
                    altHost.Length > 0)
                {
                    builder.Append("&alt")
                        .Append(altIndex++)
                        .Append('=')
                        .Append(Uri.EscapeDataString(endpoint));
                }
            }

            payload = builder.ToString();
            return true;
        }

        /// <summary>
        /// Encodes endpoints plus optional relay metadata into a QR payload.
        /// When relay is configured, the phone can use relay mode instead of
        /// trying LAN/WAN endpoints.
        /// </summary>
        public static bool TryCreatePayload(
            IEnumerable<string> endpoints,
            string relayUrl,
            string relayRoomId,
            out string payload,
            out string error)
        {
            payload = string.Empty;
            error = string.Empty;

            if (!string.IsNullOrWhiteSpace(relayUrl) && !string.IsNullOrWhiteSpace(relayRoomId))
            {
                // Relay-only payload: phone connects through relay, no LAN fallback needed.
                payload = Scheme + "://connect?room=" +
                          Uri.EscapeDataString(relayRoomId) +
                          "&relay=" +
                          Uri.EscapeDataString(relayUrl);
                return true;
            }

            return TryCreatePayload(endpoints, out payload, out error);
        }

        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (!Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss") ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                uri.Port < 1 || uri.Port > 65535)
            {
                return false;
            }

            host = uri.Host;
            port = uri.Port;
            return true;
        }
    }
}
