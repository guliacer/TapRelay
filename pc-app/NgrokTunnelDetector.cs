using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TapRelay
{
    /// <summary>
    /// 自动发现本机 ngrok / cpolar 客户端已配置的隧道。
    /// 两者配置文件同为 ngrok 风格 YAML（tunnels: 段）：
    ///  - http / https 隧道且 addr 命中本机服务端口、配置了绑定 hostname → ws://域名 / wss://域名
    ///  - tcp 隧道且 addr 命中本机服务端口、配置了 remote_addr → ws://remote_addr
    /// 免费用户通常没有 hostname / remote_addr（域名由服务端随机分配），此时无法从配置
    /// 获知对外地址，检测器跳过。
    /// 配置来源：运行中进程同目录、%USERPROFILE%\.ngrok2、%USERPROFILE%\.config\ngrok、
    /// %USERPROFILE%\.ngrok、%USERPROFILE%\.cpolar、%USERPROFILE%\.config\cpolar。
    /// </summary>
    internal static class NgrokTunnelDetector
    {
        internal static IReadOnlyList<string> DetectTunnelHosts(int localPort)
        {
            var results = new List<string>();
            foreach (var config in CandidateConfigPaths())
            {
                foreach (var endpoint in TryParseConfig(config, localPort))
                {
                    if (!results.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                    {
                        results.Add(endpoint);
                    }
                }
            }

            return results;
        }

        /// <summary>解析单个 ngrok 风格 yml，返回命中端口的完整端点列表。</summary>
        internal static IReadOnlyList<string> TryParseConfig(string configPath, int localPort)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return Array.Empty<string>();
                }

                return ParseYaml(File.ReadAllLines(configPath), localPort);
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> CandidateConfigPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 运行中进程（ngrok.exe / cpolar.exe）同目录的配置文件。
            foreach (var tuple in ProcessConfigPairs())
            {
                if (File.Exists(tuple.Path) && seen.Add(tuple.Path))
                {
                    yield return tuple.Path;
                }
            }

            // 2. 常见用户目录位置。
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                Path.Combine(user, ".ngrok2", "ngrok.yml"),
                Path.Combine(user, ".ngrok2", "ngrok.yaml"),
                Path.Combine(user, ".config", "ngrok", "ngrok.yml"),
                Path.Combine(user, ".config", "ngrok", "ngrok.yaml"),
                Path.Combine(user, ".ngrok", "ngrok.yml"),
                Path.Combine(user, ".cpolar", "cpolar.yml"),
                Path.Combine(user, ".config", "cpolar", "cpolar.yml"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static IEnumerable<(string Path, string FileName)> ProcessConfigPairs()
        {
            var pairs = new List<(string Path, string FileName)>();
            foreach (var processName in new[] { "ngrok", "cpolar" })
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            var fileName = process.MainModule?.FileName;
                            if (string.IsNullOrWhiteSpace(fileName))
                            {
                                continue;
                            }

                            var directory = Path.GetDirectoryName(fileName);
                            if (string.IsNullOrWhiteSpace(directory))
                            {
                                continue;
                            }

                            var configName = processName == "cpolar" ? "cpolar.yml" : "ngrok.yml";
                            pairs.Add((Path.Combine(directory, configName), configName));
                        }
                        catch (Exception)
                        {
                            // 访问受保护进程 MainModule 可能抛异常，跳过。
                        }
                    }
                }
                catch (Exception)
                {
                    // 进程枚举失败不影响配置扫描。
                }
            }

            return pairs;
        }

        /// <summary>
        /// 解析 ngrok 风格 YAML 的 tunnels 段：
        ///   tunnels:
        ///     web:
        ///       proto: http
        ///       addr: 1122
        ///       hostname: example.ngrok.app
        ///     ssh:
        ///       proto: tcp
        ///       addr: 1122
        ///       remote_addr: 0.tcp.ngrok.io:12345
        /// 缩进级别识别：2 空格 = 隧道名，4 空格 = 键值。
        /// </summary>
        private static IReadOnlyList<string> ParseYaml(string[] lines, int localPort)
        {
            var endpoints = new List<string>();
            var inTunnels = false;
            var tunnelIndent = -1;
            var proto = string.Empty;
            var addr = string.Empty;
            var hostname = string.Empty;
            var remoteAddr = string.Empty;

            void Flush()
            {
                if (string.IsNullOrWhiteSpace(proto) && string.IsNullOrWhiteSpace(hostname) &&
                    string.IsNullOrWhiteSpace(remoteAddr))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(addr) && AddressTargetsPort(addr, localPort))
                {
                    if (!string.IsNullOrWhiteSpace(hostname))
                    {
                        var scheme = proto == "https" ? "wss" : "ws";
                        if (TunnelHostHelper.IsDnsName(hostname))
                        {
                            endpoints.Add(scheme + "://" + hostname);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(remoteAddr))
                    {
                        endpoints.Add("ws://" + remoteAddr);
                    }
                }
            }

            foreach (var raw in lines)
            {
                if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart().Length;

                if (raw.Trim().Equals("tunnels:", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    inTunnels = true;
                    tunnelIndent = -1;
                    continue;
                }

                if (!inTunnels)
                {
                    continue;
                }

                // 隧道名：2 空格缩进、以 ':' 结尾（如 "    web:"）。
                if (indent >= 2 && tunnelIndent < 0 && raw.TrimEnd().EndsWith(":", StringComparison.Ordinal))
                {
                    Flush();
                    tunnelIndent = indent;
                    proto = string.Empty;
                    addr = string.Empty;
                    hostname = string.Empty;
                    remoteAddr = string.Empty;
                    continue;
                }

                if (tunnelIndent < 0 || indent <= tunnelIndent)
                {
                    // 离开了 tunnels 段（缩进回到隧道名级别或更浅）。
                    if (indent <= tunnelIndent && !raw.Trim().StartsWith("-", StringComparison.Ordinal))
                    {
                        Flush();
                        inTunnels = false;
                        continue;
                    }
                }

                if (indent <= tunnelIndent)
                {
                    continue;
                }

                var colon = raw.Trim().IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = raw.Trim().Substring(0, colon).Trim().ToLowerInvariant();
                var value = raw.Trim().Substring(colon + 1).Trim();

                if (key == "proto")
                {
                    proto = value.ToLowerInvariant();
                }
                else if (key == "addr")
                {
                    addr = value;
                }
                else if (key == "hostname")
                {
                    hostname = value;
                }
                else if (key == "remote_addr")
                {
                    remoteAddr = value;
                }
            }

            Flush();
            return endpoints;
        }

        /// <summary>判断 addr 值（"1122" / "localhost:1122" / "127.0.0.1:1122" / "http://localhost:1122"）是否命中端口。</summary>
        private static bool AddressTargetsPort(string addr, int port)
        {
            var value = addr.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(value.IndexOf("://", StringComparison.Ordinal) + 3);
            }

            var (_, parsedPort) = TunnelHostHelper.SplitHostPort(value);
            return parsedPort == port ||
                   (parsedPort == null && int.TryParse(value, out var bare) && bare == port);
        }
    }
}
