using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using UiButtonStyle = TapRelay.UiButtonStyler.UiButtonStyle;

namespace TapRelay
{
    internal sealed class ConnectionQrForm : Form
    {
        private readonly List<string> _endpoints;
        private readonly string _primary;
        private readonly string _relayUrl;
        private readonly string _relayRoomId;
        private readonly bool _darkMode;
        private Bitmap? _qrImage;
        private string _payload = string.Empty;
        private Button _checkButton = null!;
        private Button _repairButton = null!;
        private Label _checkResultLabel = null!;

        public ConnectionQrForm(List<string> endpoints, bool darkMode, string relayUrl = "", string relayRoomId = "")
        {
            _endpoints = endpoints ?? new List<string>();
            _darkMode = darkMode;
            _relayUrl = (relayUrl ?? string.Empty).Trim();
            _relayRoomId = (relayRoomId ?? string.Empty).Trim();
            _primary = _endpoints.Count > 0 ? _endpoints[0] : string.Empty;

            if (!string.IsNullOrWhiteSpace(_relayUrl) && !string.IsNullOrWhiteSpace(_relayRoomId))
            {
                if (!ConnectionQrProtocol.TryCreatePayload(_endpoints, _relayUrl, _relayRoomId, out _payload, out var relayError))
                    throw new InvalidOperationException(relayError);
            }
            else if (!ConnectionQrProtocol.TryCreatePayload(_endpoints, out _payload, out var error))
            {
                throw new InvalidOperationException(error);
            }

            Text = "扫码连接 TapRelay";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 560);
            Font = new Font(UiTokens.UiFontFamily, UiTokens.BodyFontSize);
            BuildLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _qrImage?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildLayout()
        {
            var colors = _darkMode ? DarkColors : LightColors;
            BackColor = colors.Background;

            var title = new Label
            {
                Text = "手机扫码快速连接",
                AutoSize = true,
                Font = new Font(UiTokens.UiFontFamily, 15F, FontStyle.Bold),
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                Location = new Point(24, 20)
            };
            Controls.Add(title);

            var hint = new Label
            {
                Text = "请使用 TapRelay 手机端的“扫码连接”按钮扫描此二维码",
                AutoSize = true,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                Location = new Point(24, 52)
            };
            Controls.Add(hint);

            var picture = new PictureBox
            {
                Size = new Size(320, 320),
                Location = new Point(55, 84),
                BackColor = Color.White,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };
            _qrImage = CreateQrImage(_payload);
            picture.Image = _qrImage;
            Controls.Add(picture);

            if (!HasPublicTunnelCandidate(_endpoints))
            {
                // 没有任何公网隧道/域名候选：只有局域网地址，跨网络将无法连接。
                var tunnelHint = new Label
                {
                    Text = "仅限同一路由使用；跨网络请在电脑配置 Cloudflare/frp/ngrok 等隧道工具后自动出现在此",
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = colors.WarningText,
                    BackColor = colors.WarningBackground,
                    Location = new Point(24, 382),
                    Size = new Size(382, 28),
                    Padding = new Padding(8, 0, 8, 0)
                };
                Controls.Add(tunnelHint);
            }
            else if (_endpoints.Count > 1)
            {
                var multiHint = new Label
                {
                    Text = "已内置 " + _endpoints.Count + " 个候选地址，手机端自动择优连接",
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = colors.WarningText,
                    BackColor = colors.WarningBackground,
                    Location = new Point(24, 382),
                    Size = new Size(382, 28),
                    Padding = new Padding(8, 0, 8, 0)
                };
                Controls.Add(multiHint);
            }

            var endpointLabel = new Label
            {
                Text = "主连接地址：" + _primary,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = colors.TextPrimary,
                BackColor = colors.Surface,
                Location = new Point(24, 418),
                Size = new Size(382, 36),
                Padding = new Padding(8, 0, 8, 0)
            };
            Controls.Add(endpointLabel);

            // 公网检查：一键验证 cloudflared 等公网隧道是否可用。
            var resultY = 460;
            var buttonY = 502;
            if (!string.IsNullOrWhiteSpace(_relayRoomId))
            {
                resultY = 500;
                buttonY = 544;
            }

            // 检查结果：按钮上方一行，全宽居中显示。
            _checkResultLabel = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                Location = new Point(24, resultY),
                Size = new Size(382, 36),
                Padding = new Padding(4, 0, 4, 0)
            };
            Controls.Add(_checkResultLabel);

            // 同一排四个按钮居中：检查公网 / 修复隧道 / 复制 / 关闭。
            _checkButton = UiButtonStyler.Create("检查公网", UiButtonStyle.Secondary, ToButtonColors(colors));
            _checkButton.Size = new Size(92, UiButtonStyler.DialogButtonHeight);
            _checkButton.Click += OnCheckPublicTunnel;
            Controls.Add(_checkButton);

            _repairButton = UiButtonStyler.Create("修复隧道", UiButtonStyle.Secondary, ToButtonColors(colors));
            _repairButton.Size = new Size(92, UiButtonStyler.DialogButtonHeight);
            _repairButton.Click += OnRepairPublicTunnel;
            Controls.Add(_repairButton);

            var copyButton = UiButtonStyler.Create("复制", UiButtonStyle.Primary, ToButtonColors(colors));
            copyButton.Size = new Size(92, UiButtonStyler.DialogButtonHeight);
            copyButton.Click += (_, __) =>
            {
                Clipboard.SetText(_payload);
                MessageBox.Show(this, "二维码数据已复制。", "已复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Controls.Add(copyButton);

            var closeButton = UiButtonStyler.Create("关闭", UiButtonStyle.Secondary, ToButtonColors(colors));
            closeButton.Size = new Size(92, UiButtonStyler.DialogButtonHeight);
            closeButton.DialogResult = DialogResult.Cancel;
            Controls.Add(closeButton);
            CancelButton = closeButton;

            var buttonX = (430 - (92 * 4 + 8 * 3)) / 2; // 同排居中
            _checkButton.Location = new Point(buttonX, buttonY);
            _repairButton.Location = new Point(buttonX + 100, buttonY);
            copyButton.Location = new Point(buttonX + 200, buttonY);
            closeButton.Location = new Point(buttonX + 300, buttonY);

            // Show relay room ID if relay is enabled
            if (!string.IsNullOrWhiteSpace(_relayRoomId))
            {
                var relayLabel = new Label
                {
                    Text = "中继房间号：" + _relayRoomId,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = colors.Accent,
                    BackColor = colors.Surface,
                    Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                    Location = new Point(24, 464),
                    Size = new Size(382, 28)
                };
                Controls.Add(relayLabel);
                ClientSize = new Size(430, 600);
            }
            else
            {
                ClientSize = new Size(430, 560);
            }
        }

        /// <summary>
        /// 公网检查：验证 cloudflared 进程是否在跑，以及公网候选地址（wss 域名等）
        /// 是否真的可以从外网访问。结果按 成功(绿) / 警告(橙) / 失败(红) 分级显示，
        /// 并提示手机端会自动切回局域网兜底。
        /// </summary>
        private async void OnCheckPublicTunnel(object? sender, EventArgs e)
        {
            var colors = _darkMode ? DarkColors : LightColors;
            _checkButton.Enabled = false;
            _repairButton.Enabled = false;
            _checkResultLabel.ForeColor = colors.TextSecondary;
            _checkResultLabel.Text = "正在检查公网隧道…";

            try
            {
                var (level, message) = await Task.Run(() => ProbePublicTunnel());
                _checkResultLabel.ForeColor = level switch
                {
                    2 => colors.Success,
                    1 => colors.WarningText,
                    _ => colors.Danger
                };
                _checkResultLabel.Text = message;
            }
            catch (Exception ex)
            {
                _checkResultLabel.ForeColor = colors.Danger;
                _checkResultLabel.Text = "检查异常：" + ex.Message;
            }
            finally
            {
                _checkButton.Enabled = true;
                _repairButton.Enabled = true;
            }
        }

        /// <summary>按用户请求修复 Cloudflare Tunnel 配置并重启已有隧道。</summary>
        private async void OnRepairPublicTunnel(object? sender, EventArgs e)
        {
            var colors = _darkMode ? DarkColors : LightColors;
            _checkButton.Enabled = false;
            _repairButton.Enabled = false;
            _checkResultLabel.ForeColor = colors.TextSecondary;
            _checkResultLabel.Text = "正在修复公网隧道…";

            try
            {
                var result = await Task.Run(() => CloudflaredTunnelRepair.Repair(1122));
                _checkResultLabel.ForeColor = result.Level switch
                {
                    2 => colors.Success,
                    1 => colors.WarningText,
                    _ => colors.Danger
                };
                _checkResultLabel.Text = result.Message;
            }
            catch (Exception ex)
            {
                _checkResultLabel.ForeColor = colors.Danger;
                _checkResultLabel.Text = "修复异常：" + ex.Message;
            }
            finally
            {
                _checkButton.Enabled = true;
                _repairButton.Enabled = true;
            }
        }

        /// <summary>探测公网候选；返回 (0=失败, 1=警告, 2=正常) 与用户可读消息。</summary>
        private (int Level, string Message) ProbePublicTunnel()
        {
            var candidates = _endpoints
                .Concat(TunnelDetectorRegistry.DetectAll(1122))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var publicEndpoints = candidates.Where(IsPublicCandidate).ToList();
            if (publicEndpoints.Count == 0)
            {
                return (1, "无公网候选，跨网络需配置隧道工具");
            }

            var cloudflaredRunning = Process.GetProcessesByName("cloudflared").Length > 0;
            foreach (var endpoint in publicEndpoints)
            {
                var (ok, _) = ProbeEndpoint(endpoint);
                if (ok)
                {
                    return (2, $"公网正常：{endpoint}");
                }
            }

            var tip = cloudflaredRunning ? "手机自动切局域网" : "cloudflared 未运行，手机自动切局域网";
            return (0, $"公网不可达（{publicEndpoints[0]}），{tip}");
        }

        /// <summary>对单个端点做 HTTP(S) /health 探测（Cloudflare 只暴露 443，wss 域名用 https）。</summary>
        private static (bool Ok, string Detail) ProbeEndpoint(string endpoint)
        {
            var host = endpoint;
            var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
            {
                host = host.Substring(schemeIndex + 3);
            }

            var port = 443;
            var colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out var parsedPort))
            {
                host = host.Substring(0, colon);
                port = parsedPort;
            }

            var useTls = endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                         endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                         port == 443;
            var url = (useTls ? "https://" : "http://") + host + ":" + port + "/health";
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = client.GetAsync(url).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return (true, url + " OK");
                }

                return (false, url + " → HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return (false, url + " → " + ex.GetBaseException().Message);
            }
        }

        /// <summary>端点是否为公网候选（域名，或非私网 IP）。</summary>
        private static bool IsPublicCandidate(string endpoint)
        {
            var host = endpoint;
            var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
            {
                host = host.Substring(schemeIndex + 3);
            }

            var colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                host = host.Substring(0, colon);
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                return !IsPrivateAddress(ip);
            }

            return TunnelHostHelper.IsDnsName(host);
        }

        private static Bitmap CreateQrImage(string payload)
        {
            return SimpleQrCode.CreateBitmap(payload, 8);
        }

        /// <summary>
        /// 判断候选里是否存在公网隧道/域名入口（wss:// 域名、公网 IP 或域名 host）。
        /// 仅当全部候选都是局域网/虚拟局域网地址时才返回 false，用于"跨网络需配置隧道"提示。
        /// </summary>
        private static bool HasPublicTunnelCandidate(List<string> endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var host = endpoint;
                var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
                if (schemeIndex >= 0)
                {
                    host = host.Substring(schemeIndex + 3);
                }

                var colon = host.LastIndexOf(':');
                if (colon > 0)
                {
                    host = host.Substring(0, colon);
                }

                if (System.Net.IPAddress.TryParse(host, out var ip))
                {
                    if (IsPrivateAddress(ip))
                    {
                        continue;
                    }

                    return true; // 公网 IP
                }

                if (TunnelHostHelper.IsDnsName(host))
                {
                    return true; // 域名（公网/隧道入口）
                }
            }

            return false;
        }

        private static bool IsPrivateAddress(System.Net.IPAddress address)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];
            // 10/8、172.16/12、192.168/16、127/8、100.64/10（Tailscale/运营商 CGNAT 段）
            return first == 10 || first == 127 ||
                   (first == 172 && second >= 16 && second <= 31) ||
                   (first == 192 && second == 168) ||
                   (first == 100 && second >= 64 && second <= 127);
        }

        private static UiButtonStyler.UiButtonColors ToButtonColors(Palette colors)
        {
            return new UiButtonStyler.UiButtonColors
            {
                Accent = colors.Accent,
                AccentHover = colors.AccentHover,
                Danger = colors.Danger,
                Surface = colors.Surface,
                Border = colors.Border,
                TextPrimary = colors.TextPrimary,
                SurfaceAlt = colors.SurfaceAlt
            };
        }

        private sealed class Palette
        {
            public Color Background { get; set; }
            public Color Surface { get; set; }
            public Color TextPrimary { get; set; }
            public Color TextSecondary { get; set; }
            public Color Border { get; set; }
            public Color Accent { get; set; }
            public Color AccentHover { get; set; }
            public Color Danger { get; set; }
            public Color Success { get; set; }
            public Color SurfaceAlt { get; set; }
            public Color WarningText { get; set; }
            public Color WarningBackground { get; set; }
        }

        private static Palette LightColors => new Palette
        {
            Background = Color.FromArgb(244, 247, 245),
            Surface = Color.White,
            TextPrimary = Color.FromArgb(24, 45, 38),
            TextSecondary = Color.FromArgb(99, 118, 109),
            Border = Color.FromArgb(208, 222, 215),
            Accent = Color.FromArgb(63, 112, 95),
            AccentHover = Color.FromArgb(48, 91, 76),
            Danger = Color.FromArgb(190, 76, 73),
            Success = Color.FromArgb(39, 139, 91),
            SurfaceAlt = Color.FromArgb(241, 247, 244),
            WarningText = Color.FromArgb(120, 66, 18),
            WarningBackground = Color.FromArgb(255, 243, 205)
        };

        private static Palette DarkColors => new Palette
        {
            Background = Color.FromArgb(16, 25, 22),
            Surface = Color.FromArgb(24, 38, 32),
            TextPrimary = Color.FromArgb(239, 248, 242),
            TextSecondary = Color.FromArgb(165, 190, 177),
            Border = Color.FromArgb(58, 82, 70),
            Accent = Color.FromArgb(142, 174, 161),
            AccentHover = Color.FromArgb(165, 192, 180),
            Danger = Color.FromArgb(235, 113, 105),
            Success = Color.FromArgb(82, 193, 126),
            SurfaceAlt = Color.FromArgb(31, 49, 43),
            WarningText = Color.FromArgb(255, 220, 170),
            WarningBackground = Color.FromArgb(66, 46, 18)
        };
    }
}
