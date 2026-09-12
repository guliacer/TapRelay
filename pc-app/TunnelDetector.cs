using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace TapRelay
{
    /// <summary>
    /// 隧道检测器注册表：聚合本机所有已支持的内网穿透 / 虚拟局域网工具的自动检测，
    /// 让用户"零配置"即可在二维码中带上公网或跨网入口候选。
    /// 每个检测器返回带 scheme 的完整端点（如 "wss://win.example.org"、"ws://1.2.3.4:7000"、
    /// "ws://100.101.102.103:1122"），注册表统一去重、按注册顺序排列。
    /// </summary>
    internal static class TunnelDetectorRegistry
    {
        internal sealed class TunnelDetector
        {
            internal TunnelDetector(string name, Func<int, IReadOnlyList<string>> detect)
            {
                Name = name;
                Detect = detect;
            }

            internal string Name { get; }
            internal Func<int, IReadOnlyList<string>> Detect { get; }
        }

        internal static IReadOnlyList<TunnelDetector> Detectors { get; } = new[]
        {
            new TunnelDetector("cloudflared", CloudflaredTunnelDetector.DetectTunnelHosts),
            new TunnelDetector("frp", FrpTunnelDetector.DetectTunnelHosts),
            new TunnelDetector("ngrok/cpolar", NgrokTunnelDetector.DetectTunnelHosts),
            new TunnelDetector("tailscale/zerotier", VirtualLanTunnelDetector.DetectTunnelHosts),
        };

        /// <summary>
        /// 收集所有检测器结果并去重（保留首个出现顺序）。单个检测器抛异常不阻断其余。
        /// </summary>
        internal static IReadOnlyList<string> DetectAll(int localPort)
        {
            var hosts = new List<string>();
            foreach (var detector in Detectors)
            {
                try
                {
                    foreach (var endpoint in detector.Detect(localPort))
                    {
                        if (!hosts.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                        {
                            hosts.Add(endpoint);
                        }
                    }
                }
                catch (Exception)
                {
                    // 单个检测器失败不应阻断其他检测器。
                }
            }

            return hosts;
        }
    }

    /// <summary>隧道解析共用的字符串 / 校验辅助方法。</summary>
    internal static class TunnelHostHelper
    {
        /// <summary>判断字符串是否为合法的公网域名（非 IP、含点、只含域名字符）。</summary>
        internal static bool IsDnsName(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _) ||
                !host.Contains('.'))
            {
                return false;
            }

            return host.All(character =>
                char.IsLetterOrDigit(character) || character == '.' || character == '-');
        }

        /// <summary>去掉简单引号（单引号 / 双引号）包裹。</summary>
        internal static string StripQuotes(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var text = value.Trim();
            if (text.Length >= 2 &&
                ((text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal)) ||
                 (text.StartsWith("'", StringComparison.Ordinal) && text.EndsWith("'", StringComparison.Ordinal))))
            {
                return text.Substring(1, text.Length - 2);
            }

            return text;
        }

        /// <summary>解析 "host:port" / "host" 形式的地址，返回 (host, port?)。</summary>
        internal static (string Host, int? Port) SplitHostPort(string value)
        {
            var text = value?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return (string.Empty, null);
            }

            var colon = text.LastIndexOf(':');
            if (colon > 0 && colon < text.Length - 1 &&
                int.TryParse(text.Substring(colon + 1), out var port))
            {
                return (text.Substring(0, colon), port);
            }

            return (text, null);
        }
    }
}
