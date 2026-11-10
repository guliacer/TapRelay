using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// 读取本机 cloudflared 的真实运行状态。
    /// cloudflared 从 2026.8 起不再提供固定 --metrics 端口，每次启动会随机挑一个本机端口
    /// （日志里能看到 "Starting metrics server on 127.0.0.1:NNNNN/metrics"），因此这里
    /// 通过扫描本机回环监听端口 + 校验指标特征串来定位它。
    /// 关键指标：
    ///   cloudflared_tunnel_ha_connections  —— 与 Cloudflare 边缘建立的 HA 连接数（&gt;0 才算隧道在线）
    ///   cloudflared_tunnel_server_locations —— 已连上的边缘机房
    ///   cloudflared_tunnel_total_requests   —— 已经代理进来的请求数（公网真正通了才会增长）
    /// </summary>
    internal static class CloudflaredTunnelHealth
    {
        internal const string MetricsEnvVar = "TAPRELAY_CLOUDFLARED_METRICS_PORT";
        private const string Marker = "cloudflared_tunnel_ha_connections";

        internal sealed class Snapshot
        {
            internal bool ProcessRunning { get; set; }
            internal int ProcessCount { get; set; }
            internal bool MetricsFound { get; set; }
            internal int MetricsPort { get; set; }
            internal int HaConnections { get; set; }
            internal int TotalRequests { get; set; }
            internal int RegisterSuccess { get; set; }
            internal List<string> Locations { get; } = new List<string>();

            /// <summary>已经和 Cloudflare 边缘握手成功（隧道在线），不代表公网域名已路由。</summary>
            internal bool EdgeConnected => MetricsFound && HaConnections > 0;

            internal string Describe()
            {
                if (!ProcessRunning)
                {
                    return "cloudflared 未运行";
                }

                if (!MetricsFound)
                {
                    return "cloudflared 已运行但读不到内部状态端口";
                }

                if (HaConnections <= 0)
                {
                    return "cloudflared 正在重试连接 Cloudflare 边缘";
                }

                var where = Locations.Count > 0 ? string.Join("/", Locations) : "未知机房";
                return "已连边缘 " + HaConnections + " 条（" + where + "）";
            }
        }

        // 0 表示尚未缓存；volatile 保证多线程（含修复时的后台轮询）可见。
        private static volatile int _cachedPortValue;

        /// <summary>读取一次快照。socket 探测总成本约几百毫秒（未命中缓存时最多 ~1s）。</summary>
        internal static Snapshot Read()
        {
            var snapshot = new Snapshot();
            var processes = SafeProcessCheck();
            snapshot.ProcessCount = processes;
            snapshot.ProcessRunning = processes > 0;

            var port = DiscoverMetricsPort();
            if (port <= 0)
            {
                return snapshot;
            }

            var text = FetchMetrics(port);
            if (string.IsNullOrEmpty(text))
            {
                return snapshot;
            }

            snapshot.MetricsFound = true;
            snapshot.MetricsPort = port;
            snapshot.HaConnections = ReadMetricValue(text, "cloudflared_tunnel_ha_connections");
            snapshot.TotalRequests = ReadMetricValue(text, "cloudflared_tunnel_total_requests");
            snapshot.RegisterSuccess = ReadMetricValue(text, "cloudflared_tunnel_tunnel_register_success");
            foreach (var location in ReadLocations(text))
            {
                snapshot.Locations.Add(location);
            }

            _cachedPortValue = port;
            return snapshot;
        }

        /// <summary>
        /// 轮询等待隧道真正连上边缘。用于"修复 → 重启服务"之后：重启会断开原有连接，
        /// 重连需要几秒到几十秒，不等待就判定失败必然误报。
        /// </summary>
        internal static bool WaitForEdgeConnection(TimeSpan timeout, Action<Snapshot>? onTick = null)
        {
            var deadline = Environment.TickCount + (int)timeout.TotalMilliseconds;
            while (Environment.TickCount < deadline)
            {
                var snapshot = Read();
                onTick?.Invoke(snapshot);
                if (snapshot.EdgeConnected)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(2000);
            }

            return false;
        }

        /// <summary>
        /// 定位 cloudflared 的指标端口：优先环境变量（TAPRELAY_CLOUDFLARED_METRICS_PORT），
        /// 否则并发扫描本机回环监听端口，找返回 Prometheus 指标且含特征串的那个。
        /// 只连 127.0.0.1，且对每个端口 300ms 超时，避免拖慢 UI。
        /// </summary>
        internal static int DiscoverMetricsPort()
        {
            var configured = Environment.GetEnvironmentVariable(MetricsEnvVar);
            if (int.TryParse(configured, out var explicitPort) && explicitPort > 0 && explicitPort < 65536)
            {
                var t = FetchMetrics(explicitPort);
                if (!string.IsNullOrEmpty(t) && t.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                {
                    return explicitPort;
                }
            }

            var cachedPort = _cachedPortValue;
            if (cachedPort > 0)
            {
                var cached = FetchMetrics(cachedPort);
                if (!string.IsNullOrEmpty(cached) && cached.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                {
                    return cachedPort;
                }

                _cachedPortValue = 0;
            }

            var ports = LoopbackListeners();
            if (ports.Count == 0)
            {
                return 0;
            }

            var hit = 0;
            Parallel.ForEach(ports, new ParallelOptions { MaxDegreeOfParallelism = 8 }, port =>
            {
                if (Volatile.Read(ref hit) != 0)
                {
                    return;
                }

                var text = FetchMetrics(port, 300);
                if (!string.IsNullOrEmpty(text) && text.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                {
                    Interlocked.CompareExchange(ref hit, port, 0);
                }
            });

            return Volatile.Read(ref hit);
        }

        private static List<int> LoopbackListeners()
        {
            var ports = new List<int>();
            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (var endpoint in listeners)
                {
                    // 只扫回环 IPv4：cloudflared 的指标服务默认只监听 127.0.0.1。
                    if (!IPAddress.IsLoopback(endpoint.Address) || endpoint.Port < 1024)
                    {
                        continue;
                    }

                    if (!ports.Contains(endpoint.Port))
                    {
                        ports.Add(endpoint.Port);
                    }
                }
            }
            catch (Exception)
            {
                // 取不到监听表就退化成空候选，调用方按"读不到状态"处理。
            }

            ports.Sort();
            return ports;
        }

        /// <summary>同步取一次 /metrics 文本；失败返回 null。用原始 socket 避免 HttpClient 走代理。</summary>
        private static string? FetchMetrics(int port, int timeoutMs = 800)
        {
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                if (!connect.Wait(timeoutMs) || !client.Connected)
                {
                    return null;
                }

                client.SendTimeout = timeoutMs;
                client.ReceiveTimeout = timeoutMs;
                using var stream = client.GetStream();
                var request = Encoding.ASCII.GetBytes(
                    "GET /metrics HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nConnection: close\r\n\r\n");
                stream.Write(request, 0, request.Length);
                stream.Flush();

                var builder = new StringBuilder();
                var buffer = new byte[8192];
                var deadline = Environment.TickCount + timeoutMs + 400;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (builder.Length > 2 * 1024 * 1024 || Environment.TickCount > deadline)
                    {
                        break;
                    }
                }

                var text = builder.ToString();
                return text.IndexOf(Marker, StringComparison.Ordinal) >= 0 ? text : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>读取形如 "metric 4" 或 "metric{labels} 1" 的数值；找不到返回 0。</summary>
        internal static int ReadMetricValue(string text, string metricName)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(metricName))
            {
                return 0;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (!line.StartsWith(metricName + " ", StringComparison.Ordinal) &&
                    !line.StartsWith(metricName + "{", StringComparison.Ordinal))
                {
                    continue;
                }

                var space = line.LastIndexOf(' ');
                if (space < 0 || space >= line.Length - 1)
                {
                    continue;
                }

                if (double.TryParse(line.Substring(space + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value))
                {
                    return (int)Math.Round(value);
                }
            }

            return 0;
        }

        /// <summary>提取 cloudflared_tunnel_server_locations 中标记为当前(值=1)的机房列表。</summary>
        internal static List<string> ReadLocations(string text)
        {
            var locations = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return locations;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("cloudflared_tunnel_server_locations{", StringComparison.Ordinal))
                {
                    continue;
                }

                var match = Regex.Match(line, "edge_location=\"([^\"]+)\"");
                if (!match.Success)
                {
                    continue;
                }

                var space = line.LastIndexOf(' ');
                if (space < 0 || space >= line.Length - 1 ||
                    !double.TryParse(line.Substring(space + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value) || (int)Math.Round(value) != 1)
                {
                    continue;
                }

                if (!locations.Contains(match.Groups[1].Value))
                {
                    locations.Add(match.Groups[1].Value);
                }
            }

            locations.Sort();
            return locations;
        }

        /// <summary>把 HTTP 状态 + 响应正文压成一句可读诊断，识别 Cloudflare 边缘错误码。</summary>
        internal static string DescribeHttpStatus(int status, string body)
        {
            if (status >= 200 && status < 300)
            {
                return "HTTP " + status;
            }

            var match = Regex.Match(body ?? string.Empty, "error code:\\s*(\\d+)");
            return match.Success
                ? "HTTP " + status + " / error code " + match.Groups[1].Value
                : "HTTP " + status;
        }

        /// <summary>到 Cloudflare 边缘的实测结果（不依赖 cloudflared 自身状态）。</summary>
        internal sealed class EdgeProbe
        {
            internal int Targets { get; set; }
            internal int Tcp7844 { get; set; }
            internal int Tls7844 { get; set; }
            internal int Tcp443 { get; set; }
            internal int LatencyMs { get; set; }

            /// <summary>TCP 层能连上 7844。</summary>
            internal bool TunnelPortReachable => Tcp7844 > 0;

            /// <summary>TCP 通但 TLS 被中断：典型的 SNI 干扰，配置层面无解。</summary>
            internal bool TlsInterfered => Tcp7844 > 0 && Tls7844 == 0;

            internal string Describe()
            {
                if (Targets == 0)
                {
                    return "边缘地址解析失败";
                }

                return $"7844 端口 TCP {Tcp7844}/{Targets} 可连、TLS {Tls7844}/{Targets} 握手成功，" +
                       $"443 端口 TCP {Tcp443}/{Targets} 可连（{LatencyMs}ms）";
            }
        }

        /// <summary>
        /// 对 Cloudflare 边缘做三层探测：TCP 7844（隧道端口）→ TLS 握手 → TCP 443。
        /// 三层结果能直接定位问题性质：
        ///   TCP 不通      → 端口/线路被拦
        ///   TCP 通 TLS 断 → SNI 干扰（换网络或走代理节点）
        ///   都通          → 网络没问题，问题在 cloudflared 进程本身
        /// 不实测就下结论，用户会一直以为"配置没修好"。
        /// </summary>
        internal static EdgeProbe ProbeEdge(int timeoutMs = 1500)
        {
            var probe = new EdgeProbe();
            var watch = Stopwatch.StartNew();
            var hosts = new List<(string Host, IPAddress Address)>();
            foreach (var host in new[] { "region1.v2.argotunnel.com", "region2.v2.argotunnel.com" })
            {
                try
                {
                    // 每个区域只取一个 IPv4：全部 IP 都测会让探测拖到几十秒（实测 20 个目标
                    // 串行需要 35s），而结论只需要一个可用样本。
                    var address = Dns.GetHostAddresses(host)
                        .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
                    if (address != null)
                    {
                        hosts.Add((host, address));
                    }
                }
                catch (Exception)
                {
                    // 解析失败不阻断另一个区域。
                }
            }

            probe.Targets = hosts.Count;
            var tcp7844 = 0;
            var tls7844 = 0;
            var tcp443 = 0;
            Parallel.ForEach(hosts, target =>
            {
                if (TcpReachable(target.Address, 7844, timeoutMs))
                {
                    Interlocked.Increment(ref tcp7844);
                    if (TlsReachable(target.Host, target.Address, 7844, timeoutMs))
                    {
                        Interlocked.Increment(ref tls7844);
                    }
                }

                if (TcpReachable(target.Address, 443, timeoutMs))
                {
                    Interlocked.Increment(ref tcp443);
                }
            });

            probe.Tcp7844 = tcp7844;
            probe.Tls7844 = tls7844;
            probe.Tcp443 = tcp443;
            probe.LatencyMs = (int)watch.ElapsedMilliseconds;
            return probe;
        }

        private static bool TcpReachable(IPAddress address, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                var task = client.ConnectAsync(address, port);
                return task.Wait(timeoutMs) && client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>用真实 SNI 做一次 TLS 握手；证书内容不校验，只看握手能否完成。</summary>
        private static bool TlsReachable(string sniHost, IPAddress address, int port, int timeoutMs)
        {
            try
            {
                var task = Task.Run(() =>
                {
                    using var client = new TcpClient(AddressFamily.InterNetwork);
                    var connect = client.ConnectAsync(address, port);
                    if (!connect.Wait(timeoutMs) || !client.Connected)
                    {
                        return false;
                    }

                    using var ssl = new System.Net.Security.SslStream(client.GetStream(), false,
                        (sender, certificate, chain, errors) => true);
                    ssl.AuthenticateAsClient(sniHost);
                    return ssl.IsAuthenticated;
                });
                return task.Wait(timeoutMs) && task.Result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int SafeProcessCheck()
        {
            try
            {
                return Process.GetProcessesByName("cloudflared").Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
