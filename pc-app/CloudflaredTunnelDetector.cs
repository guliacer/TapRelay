using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace TapRelay
{
    /// <summary>
    /// 自动发现本机已配置的 Cloudflare Tunnel，让用户"零配置"即可在二维码中
    /// 带上 wss://域名 候选。读取 cloudflared 的 config.yml（用户目录 + LocalSystem
    /// 服务目录），解析每个 ingress 的 hostname 与 service 目标端口，命中本机
    /// 服务端口（默认 1122）且 hostname 为域名的即视为可用隧道。
    /// 用户手动配置的对外地址仍优先于自动检测结果。
    /// </summary>
    internal static class CloudflaredTunnelDetector
    {
        /// <summary>返回完整端点（wss://域名）。Cloudflare 隧道只暴露公网 443。</summary>
        internal static IReadOnlyList<string> DetectTunnelHosts(int localPort)
        {
            var hosts = new List<string>();
            foreach (var candidate in CandidateConfigPaths())
            {
                var host = TryParseTunnelHost(candidate, localPort);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    var endpoint = "wss://" + host;
                    if (!hosts.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                    {
                        hosts.Add(endpoint);
                    }
                }
            }

            return hosts;
        }

        internal static IEnumerable<string> CandidateConfigPaths()
        {
            // 用户手动运行时：%USERPROFILE%\.cloudflared\config.yml
            var user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cloudflared", "config.yml");
            if (File.Exists(user))
            {
                yield return user;
            }

            // LocalSystem 服务运行时：C:\Windows\System32\config\systemprofile\.cloudflared\config.yml
            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "config", "systemprofile", ".cloudflared", "config.yml");
            if (File.Exists(system))
            {
                yield return system;
            }
        }

        /// <summary>
        /// 从 config.yml 解析第一个"hostname 是域名且 service 目标端口等于 localPort"
        /// 的 ingress。ingress 结构为成对的 "- hostname: xxx" + "service: yyy"。
        /// </summary>
        internal static string TryParseTunnelHost(string configPath, int localPort)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return string.Empty;
                }

                string hostname = string.Empty;
                foreach (var raw in File.ReadAllLines(configPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("- hostname:", StringComparison.OrdinalIgnoreCase))
                    {
                        hostname = line.Substring("- hostname:".Length).Trim();
                    }
                    else if (line.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
                    {
                        var service = line.Substring("service:".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(hostname) &&
                            ServiceTargetsPort(service, localPort) &&
                            IsDnsName(hostname))
                        {
                            return hostname;
                        }
                        hostname = string.Empty;
                    }
                }

                return string.Empty;
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

        private static bool ServiceTargetsPort(string service, int port)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                return false;
            }

            var value = service.Trim().ToLowerInvariant().TrimEnd('/');
            // http://localhost:1122 / https://localhost:1122 / tcp://localhost:1122
            var index = value.LastIndexOf(':');
            if (index < 0)
            {
                return false;
            }

            return int.TryParse(value.Substring(index + 1), out var parsed) && parsed == port;
        }

        private static bool IsDnsName(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _) ||
                !host.Contains('.'))
            {
                return false;
            }

            return host.All(character =>
                char.IsLetterOrDigit(character) || character == '.' || character == '-');
        }
    }
}
