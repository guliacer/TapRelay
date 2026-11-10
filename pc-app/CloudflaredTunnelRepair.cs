using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace TapRelay
{
    /// <summary>
    /// 修复用户明确配置的 Cloudflare Tunnel。
    ///
    /// 设计要点（2026-10-31 重写）：
    /// 1. 不再"无条件改配置 + 重启服务"。重启会断开已建立的边缘连接，重连要几秒到几十秒，
    ///    原来一重启就立刻返回，用户点"检查公网"必然看到"公网不可达"，修复看起来永远失败。
    /// 2. 配置与服务命令都正常时不再动手，直接返回当前真实状态。
    /// 3. 只有在确实改了东西（配置、服务命令、进程状态）之后才动作，并且动作完必须
    ///    先等隧道重新连上边缘、再轮询公网地址，把真实结果报回去。
    /// 4. 不再覆写一个本来就能用的服务启动命令（只修缺 --config / 配置不存在的情况）。
    /// </summary>
    internal static class CloudflaredTunnelRepair
    {
        internal sealed class RepairResult
        {
            internal RepairResult(int level, string message)
            {
                Level = level;
                Message = message;
            }

            internal int Level { get; }
            internal string Message { get; }
        }

        private sealed class Ingress
        {
            internal string Hostname { get; set; } = string.Empty;
            internal string Service { get; set; } = string.Empty;
            internal int ServiceLine { get; set; } = -1;
        }

        private sealed class ConfigInfo
        {
            internal string Path { get; set; } = string.Empty;
            internal string Tunnel { get; set; } = string.Empty;
            internal string CredentialsPath { get; set; } = string.Empty;
            internal int CredentialsLine { get; set; } = -1;
            internal int TunnelLine { get; set; } = -1;
            internal List<string> Lines { get; set; } = new List<string>();
            internal List<Ingress> Ingresses { get; } = new List<Ingress>();
        }

        private sealed class ServiceInfo
        {
            internal string Name { get; set; } = string.Empty;
            internal string PathName { get; set; } = string.Empty;
            internal string State { get; set; } = string.Empty;
        }

        // 实测：cloudflared 重启后重连按 2s→4s→8s→16s→32s 指数退避，撞上网络差的时段
        // 需要比 60s 更长的耐心，否则又会误报"修复失败"。
        private static int EdgeWaitMs => ReadIntEnv("TAPRELAY_CF_REPAIR_EDGE_WAIT_MS", 90000);
        private static int VerifyMs => ReadIntEnv("TAPRELAY_CF_REPAIR_VERIFY_MS", 45000);

        internal static RepairResult Repair(int localPort, IEnumerable<string> publicEndpoints,
            IProgress<string>? progress = null)
        {
            var endpoints = NormalizeEndpoints(publicEndpoints);

            // ---- 1. 本机源站必须在监听，否则隧道再正常也没用。 ----
            progress?.Report("检查本机服务…");
            if (!IsLocalPortListening(localPort))
            {
                return new RepairResult(1,
                    $"本机 {localPort} 端口没有服务在监听，请先在主界面启动 TapRelay 服务");
            }

            // ---- 2. 读取 cloudflared 配置 ----
            var selected = SelectConfig(localPort);
            if (selected == null)
            {
                if (!CloudflaredTunnelDetector.CandidateConfigPaths().Any())
                {
                    return new RepairResult(1,
                        "未找到 cloudflared 配置；其它公网工具仍可检测，但暂不自动改写其配置");
                }

                return new RepairResult(1, "找到 cloudflared 配置，但没有可识别的 tunnel/hostname/ingress");
            }

            var configResult = RepairConfig(selected, localPort);
            if (!configResult.Ok)
            {
                return new RepairResult(0, configResult.Message);
            }

            selected = ReadConfig(selected.Path);
            if (selected == null)
            {
                return new RepairResult(0, "配置修复后无法重新读取 config.yml");
            }

            var executable = ResolveExecutable(FindCloudflaredService()?.PathName);
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new RepairResult(0, Append(configResult.Message,
                    "找不到 cloudflared.exe，请安装或把它所在目录加入 PATH"));
            }

            // ---- 3. 先看现状：一切正常就别重启（重启=主动断线） ----
            var health = CloudflaredTunnelHealth.Read();
            var service = FindCloudflaredService();
            var manualProcess = !health.ProcessRunning
                ? false
                : health.ProcessCount > 0 && service == null;

            var probe = endpoints.Count > 0 ? VerifyPublicEndpoints(endpoints, 0, null) : null;
            if (!configResult.Changed &&
                service != null && IsRunning(service.State) &&
                health.EdgeConnected &&
                probe != null && probe.Ok)
            {
                return new RepairResult(2, "隧道无需修复，" + probe.Summary);
            }

            // ---- 4. 按"服务 / 手动进程 / 都没有"三种情形分别处理 ----
            var needsRestart = NeedsServiceRestart(
                configResult.Changed,
                IsRunning(service?.State ?? string.Empty),
                health.EdgeConnected);
            if (service != null)
            {
                if (needsRestart)
                {
                    var replaceCommand = !HasUsableCommand(service.PathName);
                    var expected = replaceCommand
                        ? Quote(executable) + " --config " + Quote(selected.Path) + " tunnel run " + Quote(selected.Tunnel)
                        : null;
                    progress?.Report(replaceCommand ? "修正服务启动命令并重启隧道…" : "重启 cloudflared 服务…");
                    if (!RestartService(service.Name, expected))
                    {
                        return new RepairResult(1,
                            Append(configResult.Message, "服务重启失败或用户取消了管理员授权"));
                    }
                }
                else
                {
                    progress?.Report("配置无变化，不改动服务，等待隧道状态…");
                }
            }
            else if (manualProcess)
            {
                if (configResult.Changed)
                {
                    return new RepairResult(1, Append(configResult.Message,
                        "配置已更新，请关闭当前手动运行的 cloudflared 进程后重新启动隧道"));
                }

                progress?.Report("等待手动运行的 cloudflared 建立连接…");
            }
            else
            {
                // 没有服务、也没有已在跑的 cloudflared：直接拉起一个隧道进程。
                progress?.Report("未安装 cloudflared 服务，直接启动隧道进程…");
                if (!TryStartManualTunnel(executable, selected, out var startError))
                {
                    return new RepairResult(0, Append(configResult.Message, startError));
                }
            }

            // ---- 5. 重启必然断线：先等重新连上边缘 ----
            health = WaitForEdge(EdgeWaitMs, progress);

            // ---- 6. 再等公网地址真正可用 ----
            if (endpoints.Count == 0)
            {
                return new RepairResult(1, health.EdgeConnected
                    ? Append(configResult.Message, "隧道已连上边缘，但没有可用的公网候选地址")
                    : Append(configResult.Message, "隧道尚未连上边缘：" + health.Describe()));
            }

            var verified = VerifyPublicEndpoints(endpoints, VerifyMs, progress);
            if (verified.Ok)
            {
                return new RepairResult(2, verified.Summary);
            }

            // 公网轮询期间隧道状态可能又变了（本机链路会 0↔N 波动），
            // 判定前重新读一次，避免用过期快照下结论。
            health = CloudflaredTunnelHealth.Read();
            if (health.EdgeConnected)
            {
                // 只连上一次不算数：本机实测会「连上→立刻断」，间隔 2 秒复核一次，
                // 两次都在线才认为是真的在线，否则按"未连上"处理。
                System.Threading.Thread.Sleep(2000);
                var confirm = CloudflaredTunnelHealth.Read();
                if (!confirm.EdgeConnected)
                {
                    health = confirm;
                }
            }

            if (!health.EdgeConnected)
            {
                // 配置已排除，用实测把"网络层"说清楚，避免用户反复去翻 config.yml。
                var edge = CloudflaredTunnelHealth.ProbeEdge();
                if (!edge.TunnelPortReachable)
                {
                    return new RepairResult(0,
                        $"隧道连不上 Cloudflare 边缘，实测{edge.Describe()}。" +
                        "7844 端口连不上时任何配置都无效：请换网络或换一条线路后重试");
                }

                if (edge.TlsInterfered)
                {
                    return new RepairResult(0,
                        $"隧道连不上边缘，实测{edge.Describe()}：7844 端口能连但 TLS 握手被中断，" +
                        "属链路层干扰（配置无法解决）。请让代理节点真正承载该流量（当前 FlClash 分流为直连），" +
                        "或换网络后重试；手机可继续走中继房间");
                }

                return new RepairResult(0,
                    $"隧道未连上边缘（{health.Describe()}），但实测{edge.Describe()}；" +
                    "网络本身可达，属 cloudflared 本机握手问题，请重启服务后重试");
            }

            // 隧道在线但公网 1033：本次会话代理过流量说明 DNS 是通的，只是路由生效有延迟；
            // 一次都没代理过才可能真没配 DNS 路由。
            var hostToCheck = endpoints.Count > 0 ? HostOf(endpoints[0]) : string.Empty;
            if (health.TotalRequests > 0)
            {
                return new RepairResult(1, verified.Detail +
                    $"；该隧道累计已成功代理过 {health.TotalRequests} 次请求（DNS 路由本身没问题），" +
                    "属 Cloudflare 路由生效延迟，稍后重新点「检查公网」即可");
            }

            return new RepairResult(0, verified.Detail +
                $"；该隧道本次运行尚无成功代理记录，请确认 {hostToCheck} 的 DNS 记录已指向本隧道" +
                "（cloudflared tunnel route dns <隧道名> " + hostToCheck + "）");
        }

        /// <summary>
        /// 没有 Windows 服务时直接拉起 cloudflared 进程（与原行为一致：进程独立于本程序存活）。
        /// 启动后 1.2 秒内退出说明配置/凭据有问题。长驻进程不重定向输出，
        /// 避免无人读取的 stdout/stderr 管道写满后卡住隧道。
        /// </summary>
        private static bool TryStartManualTunnel(string executable, ConfigInfo config, out string error)
        {
            error = string.Empty;
            try
            {
                using var process = Process.Start(
                    BuildManualTunnelStartInfo(executable, config.Path, config.Tunnel));
                if (process == null)
                {
                    error = "无法启动 cloudflared 进程";
                    return false;
                }

                if (process.WaitForExit(1200))
                {
                    error = "cloudflared 启动后立即退出（退出代码 " + process.ExitCode +
                            "），请检查凭据与配置";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "启动 cloudflared 失败：" + ex.GetBaseException().Message;
                return false;
            }
        }

        internal static ProcessStartInfo BuildManualTunnelStartInfo(
            string executable, string configPath, string tunnel)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? string.Empty,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            startInfo.ArgumentList.Add("tunnel");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(tunnel);
            return startInfo;
        }

        internal static bool NeedsServiceRestart(bool configChanged, bool serviceRunning, bool edgeConnected)
        {
            return configChanged || !serviceRunning || !edgeConnected;
        }

        internal static bool HasCloudflare1033(IEnumerable<string> failures)
        {
            return failures.Any(failure =>
                failure.IndexOf("1033", StringComparison.OrdinalIgnoreCase) >= 0 ||
                failure.IndexOf("HTTP 530", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static string FormatEndpointFailures(IEnumerable<string> failures)
        {
            return string.Join("；", failures);
        }

        /// <summary>轮询等待 Tunnel 重新注册到边缘。</summary>
        private static CloudflaredTunnelHealth.Snapshot WaitForEdge(int timeoutMs, IProgress<string>? progress)
        {
            var deadline = Environment.TickCount + Math.Max(1000, timeoutMs);
            var snapshot = CloudflaredTunnelHealth.Read();
            while (!snapshot.EdgeConnected && Environment.TickCount < deadline)
            {
                progress?.Report("等待隧道重连 Cloudflare 边缘…（" + snapshot.Describe() + "）");
                System.Threading.Thread.Sleep(3000);
                snapshot = CloudflaredTunnelHealth.Read();
            }

            return snapshot;
        }

        private sealed class VerifyOutcome
        {
            internal bool Ok { get; set; }
            internal string Summary { get; set; } = string.Empty;
            internal string Detail { get; set; } = string.Empty;
        }

        /// <summary>
        /// 轮询公网候选地址，直到其中一个返回 2xx。budgetMs=0 表示只探测一次。
        /// </summary>
        private static VerifyOutcome VerifyPublicEndpoints(List<string> endpoints, int budgetMs,
            IProgress<string>? progress)
        {
            var deadline = Environment.TickCount + Math.Max(0, budgetMs);
            var outcome = new VerifyOutcome();
            string? lastHost = null;
            var failures = new List<string>();
            var attempts = 0;
            do
            {
                foreach (var endpoint in endpoints)
                {
                    attempts++;
                    lastHost = endpoint;
                    var result = ProbeEndpoint(endpoint);
                    if (result.Ok)
                    {
                        outcome.Ok = true;
                        outcome.Summary = "公网可用：" + endpoint + "（/health 返回 " + result.Status + "）";
                        outcome.Detail = outcome.Summary;
                        return outcome;
                    }

                    failures.Add(endpoint + " → " + result.Detail);
                    outcome.Detail = FormatEndpointFailures(failures);
                    if (budgetMs > 0)
                    {
                        progress?.Report("公网探测 " + endpoint + "：" + result.Detail);
                    }
                }

                if (budgetMs <= 0 || Environment.TickCount >= deadline)
                {
                    break;
                }

                System.Threading.Thread.Sleep(4000);
            }
            while (Environment.TickCount < deadline);

            outcome.Ok = false;
            if (string.IsNullOrWhiteSpace(outcome.Detail))
            {
                outcome.Detail = "未获得公网响应";
            }

            // 区分"隧道在线但域名没路由过来"和"地址压根解析不了/拒绝连接"。
            if (HasCloudflare1033(failures))
            {
                outcome.Detail = "至少一个公网候选返回 Cloudflare 1033（没有活动隧道承载该主机名）：" +
                                 FormatEndpointFailures(failures);
            }
            else if (attempts > 0)
            {
                outcome.Detail = "公网不可达（最后探测 " + lastHost + "）：" +
                                 FormatEndpointFailures(failures);
            }

            return outcome;
        }

        /// <summary>对单个端点做 HTTPS /health 探测，返回是否成功与可读细节。</summary>
        private static (bool Ok, int Status, string Detail) ProbeEndpoint(string endpoint)
        {
            var host = StripScheme(endpoint);
            var port = 443;
            var colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out var parsedPort))
            {
                host = host.Substring(0, colon);
                port = parsedPort;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return (false, 0, "地址为空");
            }

            var url = "https://" + host + ":" + port + "/health";
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                var response = client.GetAsync(url).GetAwaiter().GetResult();
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return (true, status, "HTTP " + status);
                }

                // Cloudflare 5xx 错误页正文里带 "error code: 1033" 这类身份码，取出来给 actionable 提示。
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return (false, status, CloudflaredTunnelHealth.DescribeHttpStatus(status, body));
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                if (message.Length > 60)
                {
                    message = message.Substring(0, 60) + "…";
                }

                return (false, 0, message);
            }
        }

        private static List<string> NormalizeEndpoints(IEnumerable<string> raw)
        {
            var endpoints = new List<string>();
            foreach (var candidate in raw)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var value = candidate.Trim().TrimEnd('/');
                if (!value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                    !value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                    !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 只探测域名类公网入口，局域网 IP 不参与公网校验。
                }

                if (!endpoints.Contains(value, StringComparer.OrdinalIgnoreCase) &&
                    IsPublicCandidate(value))
                {
                    endpoints.Add(value);
                }
            }

            return endpoints;
        }

        private static bool IsPublicCandidate(string endpoint)
        {
            var host = StripScheme(endpoint);
            var colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                host = host.Substring(0, colon);
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return !IsPrivateAddress(ip);
            }

            return TunnelHostHelper.IsDnsName(host);
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];
            return first == 10 || first == 127 ||
                   (first == 172 && second >= 16 && second <= 31) ||
                   (first == 192 && second == 168) ||
                   (first == 100 && second >= 64 && second <= 127);
        }

        private static string StripScheme(string endpoint)
        {
            var index = endpoint.IndexOf("://", StringComparison.Ordinal);
            return index >= 0 ? endpoint.Substring(index + 3) : endpoint;
        }

        private static string HostOf(string endpoint)
        {
            var host = StripScheme(endpoint);
            var colon = host.LastIndexOf(':');
            return colon > 0 ? host.Substring(0, colon) : host;
        }

        private static bool IsLocalPortListening(int port)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(IPAddress.Loopback, port);
                if (!task.Wait(1200))
                {
                    return false;
                }

                return client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ConfigInfo? SelectConfig(int localPort)
        {
            ConfigInfo? fallback = null;
            foreach (var path in CloudflaredTunnelDetector.CandidateConfigPaths())
            {
                var info = ReadConfig(path);
                if (info == null || info.Ingresses.Count == 0 || string.IsNullOrWhiteSpace(info.Tunnel))
                {
                    continue;
                }

                fallback ??= info;
                if (info.Ingresses.Any(item => TargetsPort(item.Service, localPort)))
                {
                    return info;
                }
            }

            return fallback;
        }

        private static ConfigInfo? ReadConfig(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path).ToList();
                var info = new ConfigInfo { Path = path, Lines = lines };
                var currentHost = string.Empty;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("tunnel:", StringComparison.OrdinalIgnoreCase))
                    {
                        info.TunnelLine = i;
                        info.Tunnel = Clean(line.Substring("tunnel:".Length));
                    }
                    else if (line.StartsWith("credentials-file:", StringComparison.OrdinalIgnoreCase))
                    {
                        info.CredentialsLine = i;
                        info.CredentialsPath = ResolvePath(path,
                            Clean(line.Substring("credentials-file:".Length)));
                    }
                    else if (line.StartsWith("- hostname:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentHost = TunnelHostHelper.StripQuotes(
                            line.Substring("- hostname:".Length).Trim());
                    }
                    else if (line.StartsWith("service:", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(currentHost))
                    {
                        info.Ingresses.Add(new Ingress
                        {
                            Hostname = currentHost,
                            Service = Clean(line.Substring("service:".Length)),
                            ServiceLine = i
                        });
                        currentHost = string.Empty;
                    }
                }

                return info;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private sealed class ConfigRepairOutcome
        {
            internal bool Ok { get; set; }
            internal bool Changed { get; set; }
            internal string Message { get; set; } = string.Empty;
        }

        private static ConfigRepairOutcome RepairConfig(ConfigInfo info, int localPort)
        {
            var lines = info.Lines.ToList();
            var changed = false;
            var credentialsChanged = false;
            var ingressChanged = false;
            var changes = new List<string>();

            if (!File.Exists(info.CredentialsPath) || Directory.Exists(info.CredentialsPath))
            {
                var replacement = FindCredential(info);
                if (string.IsNullOrWhiteSpace(replacement))
                {
                    return new ConfigRepairOutcome
                    {
                        Ok = false,
                        Message = "Cloudflared credentials-file 不存在，且未找到与 tunnel 同名的凭据 JSON"
                    };
                }

                var value = "credentials-file: " + replacement;
                if (info.CredentialsLine >= 0)
                {
                    lines[info.CredentialsLine] = value;
                }
                else
                {
                    lines.Insert(info.TunnelLine >= 0 ? info.TunnelLine + 1 : 0, value);
                }

                changed = true;
                credentialsChanged = true;
                changes.Add("credentials-file");
            }

            if (!info.Ingresses.Any(item => TargetsPort(item.Service, localPort)))
            {
                var loopbacks = info.Ingresses.Where(item => IsLoopback(item.Service)).ToList();
                if (loopbacks.Count > 1)
                {
                    return new ConfigRepairOutcome
                    {
                        Ok = false,
                        Message = "检测到多个回环 ingress，无法安全判断要修复哪一个源站"
                    };
                }

                if (loopbacks.Count == 1)
                {
                    var target = loopbacks[0];
                    lines[target.ServiceLine] = ReplaceService(lines[target.ServiceLine], localPort);
                    changed = true;
                    ingressChanged = true;
                    changes.Add("origin 端口=" + localPort);
                }
            }

            if (!changed)
            {
                return new ConfigRepairOutcome { Ok = true, Changed = false, Message = "Cloudflared 配置结构正常" };
            }

            var backup = info.Path + ".taprelay-backup-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            try
            {
                File.Copy(info.Path, backup, overwrite: false);
                File.WriteAllLines(info.Path, lines,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var repaired = ReadConfig(info.Path);
                var credentialsReady = !credentialsChanged ||
                    (repaired != null && File.Exists(repaired.CredentialsPath));
                var ingressReady = !ingressChanged ||
                    (repaired != null &&
                     repaired.Ingresses.Any(item => TargetsPort(item.Service, localPort)));
                if (repaired == null || !credentialsReady || !ingressReady)
                {
                    TryRestore(info.Path, backup);
                    return new ConfigRepairOutcome
                    {
                        Ok = false,
                        Message = "配置已备份但修复后的配置校验失败"
                    };
                }

                return new ConfigRepairOutcome
                {
                    Ok = true,
                    Changed = true,
                    Message = "已修复 " + string.Join("、", changes) + "，备份：" + backup
                };
            }
            catch (Exception ex)
            {
                TryRestore(info.Path, backup);
                return new ConfigRepairOutcome
                {
                    Ok = false,
                    Message = "写入 Cloudflared 配置失败：" + ex.GetBaseException().Message
                };
            }
        }

        private static void TryRestore(string configPath, string backupPath)
        {
            try
            {
                File.Copy(backupPath, configPath, overwrite: true);
            }
            catch (Exception)
            {
                // 保留备份供用户手动恢复；原始写入失败时不再覆盖异常信息。
            }
        }

        private static string FindCredential(ConfigInfo info)
        {
            var directory = Path.GetDirectoryName(info.Path) ?? string.Empty;
            var expected = Path.Combine(directory, info.Tunnel + ".json");
            if (File.Exists(expected)) return expected;

            try
            {
                var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
                return files.Length == 1 ? files[0] : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        private static ServiceInfo? FindCloudflaredService()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PathName, State FROM Win32_Service");
                using var services = searcher.Get();
                foreach (ManagementObject service in services)
                {
                    using (service)
                    {
                        var name = Convert.ToString(service["Name"]) ?? string.Empty;
                        var path = Convert.ToString(service["PathName"]) ?? string.Empty;
                        if (name.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) < 0 &&
                            path.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        return new ServiceInfo
                        {
                            Name = name,
                            PathName = path,
                            State = Convert.ToString(service["State"]) ?? string.Empty
                        };
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>
        /// 现有服务命令是否还能用（指向存在的 config.yml 且带 tunnel run）。
        /// 能用就不覆写——改写 ImagePath 会把用户原本跑着的配置换掉，属于越权修改。
        /// </summary>
        private static bool HasUsableCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (command.IndexOf("tunnel run", StringComparison.OrdinalIgnoreCase) < 0) return false;

            var configIndex = command.IndexOf("--config", StringComparison.OrdinalIgnoreCase);
            if (configIndex < 0) return false;

            var rest = command.Substring(configIndex + "--config".Length).Trim().TrimStart('=');
            var value = TakeArgument(rest);
            return !string.IsNullOrWhiteSpace(value) && File.Exists(TunnelHostHelper.StripQuotes(value));
        }

        /// <summary>从命令串里取第一个参数（支持空格分隔与 = 赋值）。</summary>
        private static string TakeArgument(string rest)
        {
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = rest.IndexOf('"', 1);
                return end > 1 ? rest.Substring(1, end - 1) : string.Empty;
            }

            var space = rest.IndexOf(' ');
            return space >= 0 ? rest.Substring(0, space) : rest;
        }

        private static bool RestartService(string serviceName, string? imagePath)
        {
            var script = new StringBuilder();
            script.Append("$ErrorActionPreference='Stop';");
            script.Append("$name=").Append(PsQuote(serviceName)).Append(";");
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                script.Append("$image=").Append(PsQuote(imagePath)).Append(";");
                script.Append("Set-ItemProperty -Path ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\' + $name) ");
                script.Append("-Name ImagePath -Value $image;");
            }

            script.Append("$svc=Get-Service -Name $name;");
            script.Append("if($svc.Status -eq 'Running'){Restart-Service -Name $name -Force -ErrorAction Stop}");
            script.Append("else{Start-Service -Name $name -ErrorAction Stop};");
            script.Append("Start-Sleep -Milliseconds 800;");
            script.Append("if((Get-Service -Name $name).Status -ne 'Running'){exit 2};");

            try
            {
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var process = Process.Start(psi);
                if (process == null || !process.WaitForExit(30000))
                {
                    try { process?.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception) { return false; }
        }

        private static string ResolveExecutable(string? servicePath)
        {
            var fromService = ExtractExecutable(servicePath);
            if (File.Exists(fromService)) return fromService;

            var configured = Environment.GetEnvironmentVariable("TAPRELAY_CLOUDFLARED_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "cloudflared", "cloudflared.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "cloudflared", "cloudflared.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cloudflared", "cloudflared.exe")
            };
            var installed = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(installed)) return installed;

            try
            {
                var psi = new ProcessStartInfo("where.exe", "cloudflared")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var process = Process.Start(psi);
                if (process == null) return string.Empty;
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .FirstOrDefault(File.Exists) ?? string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        private static string ExtractExecutable(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
            var text = commandLine.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : string.Empty;
            }

            var space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        private static bool TargetsPort(string service, int port)
        {
            var value = (service ?? string.Empty).Trim().TrimEnd('/');
            var colon = value.LastIndexOf(':');
            return colon >= 0 && int.TryParse(value.Substring(colon + 1), out var parsed) &&
                   parsed == port;
        }

        private static bool IsLoopback(string service)
        {
            if (!Uri.TryCreate(service, UriKind.Absolute, out var uri)) return false;
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
        }

        private static bool IsRunning(string? state)
        {
            return string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReplaceService(string line, int port)
        {
            var indent = line.Substring(0, line.Length - line.TrimStart().Length);
            return indent + "service: http://127.0.0.1:" + port;
        }

        private static string ResolvePath(string configPath, string value)
        {
            value = Environment.ExpandEnvironmentVariables(Clean(value));
            if (value.Length == 0) return string.Empty;
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, value));
        }

        private static string Clean(string value)
        {
            var text = TunnelHostHelper.StripQuotes(value.Trim());
            var comment = text.IndexOf(" #", StringComparison.Ordinal);
            return comment >= 0 ? text.Substring(0, comment).Trim() : text;
        }

        private static string Append(string first, string second)
        {
            return first.EndsWith("。", StringComparison.Ordinal) ? first + second : first + "；" + second;
        }

        private static int ReadIntEnv(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                   value >= 0
                ? value
                : fallback;
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
