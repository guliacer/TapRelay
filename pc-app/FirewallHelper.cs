using System;
using System.ComponentModel;
using System.Diagnostics;

namespace TapRelay
{
    /// <summary>
    /// Windows 防火墙自动放行助手：确保本机服务端口（默认 1122）的入站 TCP
    /// 规则存在，否则首次使用手机将无法连接（Windows 默认拦截陌生端口入站）。
    /// 检测规则不需要管理员权限；添加规则通过 runas 提权（UAC 弹窗一次），
    /// 用户取消时降级为提示，绝不静默失败。
    /// </summary>
    internal static class FirewallHelper
    {
        internal const string RuleName = "TapRelay TCP 1122";

        /// <summary>规则是否已存在（读取不需要管理员权限）。兼容旧版手动添加的规则名，避免重复添加。</summary>
        internal static bool IsRulePresent(int port)
        {
            return RuleExists(RuleName) || RuleExists("LAN Notify TCP 1122");
        }

        private static bool RuleExists(string ruleName)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "netsh",
                    $"advfirewall firewall show rule name=\"{ruleName}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    // 读取输出避免子进程缓冲阻塞，但不依赖输出内容判断（netsh 中文输出为
                    // GBK，.NET 默认按 UTF-8 解码会乱码）；以退出码为准：0=规则存在，1=不存在。
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                // netsh 不可用/被禁时视为"未知"，交给 TryAddRule 或提示兜底。
                return false;
            }
        }

        /// <summary>
        /// 尝试添加入站放行规则。需要管理员权限：通过 runas 触发一次 UAC，
        /// 用户批准返回 true；用户取消或执行失败返回 false（调用方降级提示）。
        /// </summary>
        internal static bool TryAddRule(int port)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "netsh",
                    $"advfirewall firewall add rule name=\"{RuleName}\" " +
                    "dir=in action=allow protocol=TCP localport=" + port)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }

                    return process.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                // 用户取消 UAC 或拒绝提权。
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 一键确保规则存在：已存在直接返回 true；缺失则尝试提权添加。
        /// 返回是否最终放行成功。
        /// </summary>
        internal static bool EnsureRule(int port)
        {
            if (IsRulePresent(port))
            {
                return true;
            }

            return TryAddRule(port);
        }
    }
}
