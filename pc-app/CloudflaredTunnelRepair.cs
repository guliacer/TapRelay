using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text;

namespace TapRelay
{
    /// <summary>
    /// 修复用户明确配置的 Cloudflare Tunnel。配置改写前保留 .taprelay-backup，
    /// 不创建 Windows 服务；已有服务会在修复后重启。
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
        }

        internal static RepairResult Repair(int localPort)
        {
            var selected = SelectConfig(localPort);
            if (selected == null)
            {
                if (!CloudflaredTunnelDetector.CandidateConfigPaths().Any())
                {
                    return new RepairResult(1,
                        "未找到 cloudflared 配置；其它公网工具仍可检测，但暂不自动改写其配置");
                }

                return new RepairResult(1,
                    "找到 cloudflared 配置，但没有可识别的 tunnel/hostname/ingress");
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

            var service = FindCloudflaredService();
            var executable = ResolveExecutable(service?.PathName);
            if (service != null)
            {
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return new RepairResult(0,
                        Append(configResult.Message, "找不到 cloudflared.exe，无法重启服务"));
                }

                var expected = Quote(executable) + " --config " + Quote(selected.Path) +
                               " tunnel run " + Quote(selected.Tunnel);
                var replaceCommand = !ServiceCommandMatches(service.PathName, selected.Path, selected.Tunnel);
                if (!RestartService(service.Name, expected, replaceCommand))
                {
                    return new RepairResult(1,
                        Append(configResult.Message, "服务重启失败或用户取消了管理员授权"));
                }

                return new RepairResult(2,
                    Append(configResult.Message, "Cloudflared 服务已修复并重启"));
            }

            if (Process.GetProcessesByName("cloudflared").Length > 0)
            {
                return new RepairResult(1,
                    Append(configResult.Message, "检测到手动运行的 cloudflared 进程，请关闭旧进程后重新运行隧道"));
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                return new RepairResult(0,
                    Append(configResult.Message, "找不到 cloudflared.exe，请安装或加入 PATH"));
            }

            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(selected.Path) ?? string.Empty,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("--config");
                startInfo.ArgumentList.Add(selected.Path);
                startInfo.ArgumentList.Add("tunnel");
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add(selected.Tunnel);
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return new RepairResult(0, Append(configResult.Message, "无法启动 cloudflared"));
                }

                if (process.WaitForExit(1200))
                {
                    return new RepairResult(0,
                        Append(configResult.Message, "cloudflared 启动后立即退出，请检查凭据和终端日志"));
                }

                return new RepairResult(2,
                    Append(configResult.Message, "cloudflared 隧道已启动，请重新打开二维码刷新候选地址"));
            }
            catch (Exception ex)
            {
                return new RepairResult(0,
                    Append(configResult.Message, "启动 cloudflared 失败：" + ex.GetBaseException().Message));
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

        private static (bool Ok, string Message) RepairConfig(ConfigInfo info, int localPort)
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
                    return (false, "Cloudflared credentials-file 不存在，且未找到与 tunnel 同名的凭据 JSON");
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
                    return (false, "检测到多个回环 ingress，无法安全判断要修复哪一个源站");
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
                return (true, "Cloudflared 配置结构正常");
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
                    return (false, "配置已备份但修复后的配置校验失败");
                }

                return (true, "已修复 " + string.Join("、", changes) + "，备份：" + backup);
            }
            catch (Exception ex)
            {
                TryRestore(info.Path, backup);
                return (false, "写入 Cloudflared 配置失败：" + ex.GetBaseException().Message);
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
                    "SELECT Name, PathName FROM Win32_Service");
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

                        return new ServiceInfo { Name = name, PathName = path };
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private static bool RestartService(string serviceName, string imagePath, bool replaceCommand)
        {
            var script = new StringBuilder();
            script.Append("$ErrorActionPreference='Stop';");
            script.Append("$name=").Append(PsQuote(serviceName)).Append(";");
            if (replaceCommand)
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

        private static bool ServiceCommandMatches(string command, string config, string tunnel)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var value = command.Replace('/', '\\').ToLowerInvariant();
            return value.Contains("--config") &&
                   value.Contains(config.Replace('/', '\\').ToLowerInvariant()) &&
                   value.Contains("tunnel run") &&
                   value.Contains(tunnel.ToLowerInvariant());
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
