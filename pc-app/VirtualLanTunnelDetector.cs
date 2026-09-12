using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TapRelay
{
    /// <summary>
    /// 自动发现本机虚拟局域网（VLAN）网卡的 IP，作为跨网段兜底候选：
    ///  - Tailscale：网卡描述含 "Tailscale"，地址通常为 100.x.y.z（100.64.0.0/10）
    ///  - ZeroTier：网卡描述含 "ZeroTier"，地址通常为 10.x / 172.2x.x.x
    /// 手机需安装同一工具并加入同一网络即可直连（ws://VLAN-IP:端口）。
    /// 无需任何配置文件，检测网卡即得。
    /// </summary>
    internal static class VirtualLanTunnelDetector
    {
        internal static IReadOnlyList<string> DetectTunnelHosts(int localPort)
        {
            var results = new List<string>();
            try
            {
                foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var description = networkInterface.Description ?? string.Empty;
                    if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                        (!description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) &&
                         !description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        var endpoint = "ws://" + address.Address + ":" + localPort;
                        if (!results.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                        {
                            results.Add(endpoint);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 网卡枚举失败静默返回。
            }

            return results;
        }
    }
}
