using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TapRelay
{
    /// <summary>
    /// 自动发现本机 frp 客户端（frpc）已配置的穿透隧道。
    /// 支持新版 frpc.toml（v0.52+）与旧版 frpc.ini：
    ///  - http / https 代理且 local_port 命中本机服务端口 → ws://域名 / wss://域名
    ///  - tcp 代理且 local_port 命中本机服务端口 → ws://server_addr:remote_port
    /// 配置来源：运行中 frpc.exe 同目录、%USERPROFILE%\.frp、%USERPROFILE%、Program Files\frp。
    /// </summary>
    internal static class FrpTunnelDetector
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

        /// <summary>解析单个 frp 配置文件，返回命中端口的完整端点列表。</summary>
        internal static IReadOnlyList<string> TryParseConfig(string configPath, int localPort)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return Array.Empty<string>();
                }

                var extension = Path.GetExtension(configPath);
                var lines = File.ReadAllLines(configPath);
                return extension.Equals(".toml", StringComparison.OrdinalIgnoreCase)
                    ? ParseToml(lines, localPort)
                    : ParseIni(lines, localPort);
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

            // 1. 运行中的 frpc.exe 同目录（最贴近实际使用的配置）。
            foreach (var directory in FrpcProcessDirectories())
            {
                foreach (var name in new[] { "frpc.toml", "frpc.ini" })
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path) && seen.Add(path))
                    {
                        yield return path;
                    }
                }
            }

            // 2. 常见安装 / 存放位置。
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new[]
            {
                Path.Combine(user, ".frp", "frpc.toml"),
                Path.Combine(user, ".frp", "frpc.ini"),
                Path.Combine(user, "frpc.toml"),
                Path.Combine(user, "frpc.ini"),
                Path.Combine(programFiles, "frp", "frpc.toml"),
                Path.Combine(programFiles, "frp", "frpc.ini"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static IEnumerable<string> FrpcProcessDirectories()
        {
            var directories = new List<string>();
            try
            {
                foreach (var process in Process.GetProcessesByName("frpc"))
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            var directory = Path.GetDirectoryName(fileName);
                            if (!string.IsNullOrWhiteSpace(directory) &&
                                !directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                            {
                                directories.Add(directory);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 访问其他用户 / 受保护进程的 MainModule 可能抛异常，跳过。
                    }
                }
            }
            catch (Exception)
            {
                // 进程枚举失败不影响配置扫描。
            }

            return directories;
        }

        // ---------- frpc.ini（旧版） ----------

        private static IReadOnlyList<string> ParseIni(string[] lines, int localPort)
        {
            var endpoints = new List<string>();
            var serverAddr = string.Empty;
            var current = string.Empty;
            var type = string.Empty;
            var localPortText = string.Empty;
            var customDomains = string.Empty;
            var remotePort = string.Empty;

            void Flush()
            {
                if (string.IsNullOrWhiteSpace(current) ||
                    current.Equals("common", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                AddSectionEndpoint(endpoints, type, localPortText, customDomains, remotePort, serverAddr, localPort);
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    Flush();
                    current = line.Substring(1, line.Length - 2).Trim();
                    type = string.Empty;
                    localPortText = string.Empty;
                    customDomains = string.Empty;
                    remotePort = string.Empty;
                    continue;
                }

                var equal = line.IndexOf('=');
                if (equal <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, equal).Trim().ToLowerInvariant();
                var value = line.Substring(equal + 1).Trim();

                if (current.Equals("common", StringComparison.OrdinalIgnoreCase))
                {
                    if (key == "server_addr")
                    {
                        serverAddr = value;
                    }
                }
                else
                {
                    if (key == "type")
                    {
                        type = value.ToLowerInvariant();
                    }
                    else if (key == "local_port")
                    {
                        localPortText = value;
                    }
                    else if (key == "custom_domains")
                    {
                        customDomains = value;
                    }
                    else if (key == "remote_port")
                    {
                        remotePort = value;
                    }
                }
            }

            Flush();
            return endpoints;
        }

        // ---------- frpc.toml（新版 v0.52+） ----------

        private static IReadOnlyList<string> ParseToml(string[] lines, int localPort)
        {
            var endpoints = new List<string>();
            var serverAddr = string.Empty;
            var inProxy = false;
            var type = string.Empty;
            var localPortText = string.Empty;
            var customDomains = string.Empty;
            var remotePort = string.Empty;

            void Flush()
            {
                if (!inProxy)
                {
                    return;
                }

                AddSectionEndpoint(endpoints, type, localPortText, customDomains, remotePort, serverAddr, localPort);
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[[proxies]]", StringComparison.Ordinal))
                {
                    Flush();
                    inProxy = true;
                    type = string.Empty;
                    localPortText = string.Empty;
                    customDomains = string.Empty;
                    remotePort = string.Empty;
                    continue;
                }

                var equal = line.IndexOf('=');
                if (equal <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, equal).Trim();
                var value = TunnelHostHelper.StripQuotes(line.Substring(equal + 1).Trim());

                if (!inProxy)
                {
                    if (key.Equals("serverAddr", StringComparison.OrdinalIgnoreCase))
                    {
                        serverAddr = value;
                    }
                }
                else
                {
                    if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        type = value.ToLowerInvariant();
                    }
                    else if (key.Equals("localPort", StringComparison.OrdinalIgnoreCase))
                    {
                        localPortText = value;
                    }
                    else if (key.Equals("remotePort", StringComparison.OrdinalIgnoreCase))
                    {
                        remotePort = value;
                    }
                    else if (key.Equals("customDomains", StringComparison.OrdinalIgnoreCase))
                    {
                        customDomains = ParseStringArray(value);
                    }
                }
            }

            Flush();
            return endpoints;
        }

        /// <summary>按 section / proxy 块产出端点。local_port 不命中本机端口则跳过。</summary>
        private static void AddSectionEndpoint(
            List<string> endpoints, string type, string localPortText,
            string customDomains, string remotePort, string serverAddr, int localPort)
        {
            if (!int.TryParse(localPortText, out var parsedPort) || parsedPort != localPort)
            {
                return;
            }

            if (type == "http" || type == "https")
            {
                // frp http/https 代理：域名由 vhost 端口（默认 80/443）对外暴露。
                var scheme = type == "https" ? "wss" : "ws";
                foreach (var domain in customDomains.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = domain.Trim();
                    if (TunnelHostHelper.IsDnsName(trimmed))
                    {
                        endpoints.Add(scheme + "://" + trimmed);
                    }
                }
            }
            else if (type == "tcp")
            {
                // tcp 代理：对外地址为 server_addr:remote_port（明文 ws）。
                if (string.IsNullOrWhiteSpace(remotePort) || string.IsNullOrWhiteSpace(serverAddr) ||
                    !int.TryParse(remotePort, out _))
                {
                    return;
                }

                var (host, _) = TunnelHostHelper.SplitHostPort(serverAddr);
                if (string.IsNullOrWhiteSpace(host))
                {
                    host = serverAddr;
                }

                if (!string.IsNullOrWhiteSpace(host))
                {
                    endpoints.Add("ws://" + host + ":" + remotePort);
                }
            }
        }

        /// <summary>解析 TOML 字符串数组：["a", "b"] → "a,b"。</summary>
        private static string ParseStringArray(string value)
        {
            var text = value.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
            {
                text = text.Substring(1, text.Length - 2);
            }

            var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(",", parts.Select(TunnelHostHelper.StripQuotes).Where(p => p.Length > 0));
        }
    }
}
