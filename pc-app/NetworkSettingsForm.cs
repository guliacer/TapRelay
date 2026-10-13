using System;
using System.Drawing;
using System.Windows.Forms;
using UiButtonStyle = TapRelay.UiButtonStyler.UiButtonStyle;

namespace TapRelay
{
    internal sealed class NetworkSettingsForm : Form
    {
        private readonly TextBox _listenAddressBox = new TextBox();
        private readonly TextBox _listenPortBox = new TextBox();
        private readonly TextBox _advertisedAddressBox = new TextBox();
        private readonly TextBox _advertisedPortBox = new TextBox();
        private readonly TextBox _relayUrlBox = new TextBox();
        private readonly TextBox _relayRoomIdBox = new TextBox();
        private readonly ComboBox _postureCombo = new ComboBox();
        private readonly bool _darkMode;

        public NotifyServerConfig Config { get; private set; }

        public NetworkSettingsForm(NotifyServerConfig config, bool darkMode)
        {
            Config = config.Clone().Normalize();
            _darkMode = darkMode;

            Text = "网络连接设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 570);
            Font = new Font("Microsoft YaHei UI", 9.5F);

            BuildLayout();
            ApplyTheme();
        }

        private void BuildLayout()
        {
            var colors = _darkMode ? DarkColors : LightColors;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(22),
                ColumnCount = 2,
                RowCount = 13,
                BackColor = colors.Background
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));

            var title = new Label
            {
                Text = "局域网 / 公网连接",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                Margin = new Padding(0, 0, 0, 4)
            };
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);

            ConfigureTextBox(_listenAddressBox, Config.ListenAddress, colors);
            ConfigureTextBox(_listenPortBox, Config.ListenPort.ToString(), colors);
            ConfigureTextBox(_advertisedAddressBox, Config.AdvertisedAddress, colors);
            ConfigureTextBox(_advertisedPortBox, Config.EffectiveAdvertisedPort.ToString(), colors);
            ConfigureTextBox(_relayUrlBox, Config.RelayUrl, colors);
            ConfigureTextBox(_relayRoomIdBox, Config.RelayRoomId, colors);

            // 铁律 R2：端口固定 1122，不可漂移。禁用端口输入框，避免用户误改后
            // PC 实际监听与手机端硬编码 1122 不一致导致连不上。
            _listenPortBox.ReadOnly = true;
            _advertisedPortBox.ReadOnly = true;

            AddRow(root, 1, "监听地址", _listenAddressBox, colors,
                "0.0.0.0 = 监听本机所有网卡；127.0.0.1 仅本机可用");
            AddRow(root, 2, "监听端口", _listenPortBox, colors,
                "固定为 1122（铁律 R2），不可修改");
            AddRow(root, 3, "对外地址（可选）", _advertisedAddressBox, colors,
                "公网 IP / 域名；留空则显示自动发现的局域网地址");
            AddRow(root, 4, "对外端口", _advertisedPortBox, colors,
                "固定为 1122（铁律 R2），不可修改");

            // Separator
            var separatorLabel = new Label
            {
                Text = "跨网络中继（Relay）",
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.BottomLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 10, 0, 2)
            };
            root.Controls.Add(separatorLabel, 0, 5);
            root.SetColumnSpan(separatorLabel, 2);

            AddRow(root, 6, "中继服务器", _relayUrlBox, colors,
                "如 ws://relay.example.com:8080；留空禁用中继");
            AddRowWithButton(root, 7, "房间号", _relayRoomIdBox, "重新生成", colors,
                "手机扫码自动填入；也可手动抄给手机",
                (_, __) =>
                {
                    _relayRoomIdBox.Text = NotifyServerConfig.GenerateRoomId();
                });

            var sectionNote = new Label
            {
                Text = "中继模式：PC 和手机都连接到中继服务器，不再需要同处一个局域网。\n手机扫码后自动配对，或手动输入房间号。",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(sectionNote, 0, 8);
            root.SetColumnSpan(sectionNote, 2);

            // ── 审批姿态 ──
            _postureCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _postureCombo.Font = Font;
            _postureCombo.BackColor = colors.Input;
            _postureCombo.ForeColor = colors.TextPrimary;
            _postureCombo.FlatStyle = FlatStyle.Flat;
            _postureCombo.Dock = DockStyle.Fill;
            _postureCombo.Items.AddRange(new object[]
            {
                "完整远程控制（Full）—— 审批卡片带允许/拒绝按钮",
                "只读观察（Notify）—— 审批卡片仅通知，不阻塞 agent",
                "总闸关闭（Off）—— 不推送审批/waiting 通知"
            });
            _postureCombo.SelectedIndex = Config.ApprovalPosture == ApprovalPosture.Notify
                ? 1
                : Config.ApprovalPosture == ApprovalPosture.Off
                    ? 2
                    : 0;
            var postureLabel = new Label
            {
                Text = "审批/等待通知",
                AutoSize = true,
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };
            root.Controls.Add(postureLabel, 0, 9);
            root.Controls.Add(_postureCombo, 1, 9);

            var warning = new Label
            {
                Text = "公网使用前请在路由器做端口转发，并确认防火墙规则。当前服务使用明文 WebSocket，\n不包含账号鉴权；建议仅在可信网络或通过带 TLS/鉴权的反向代理暴露。",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = colors.Warning,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 0, 0)
            };
            root.Controls.Add(warning, 0, 10);
            root.SetColumnSpan(warning, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = colors.Background,
                Margin = new Padding(0, 4, 0, 0)
            };
            var cancel = CreateButton("取消", false, colors);
            cancel.DialogResult = DialogResult.Cancel;
            var save = CreateButton("保存设置", true, colors);
            save.Click += (_, __) => SaveConfig();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons, 0, 11);
            root.SetColumnSpan(buttons, 2);

            var footer = new Label
            {
                Text = "修改监听地址或端口后，请停止服务再重新启动。",
                AutoSize = true,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                Margin = new Padding(0, 2, 0, 0)
            };
            root.Controls.Add(footer, 0, 12);
            root.SetColumnSpan(footer, 2);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static void AddRow(
            TableLayoutPanel root,
            int row,
            string labelText,
            TextBox editor,
            Palette colors,
            string hint)
        {
            var label = new Label
            {
                Text = labelText,
                Dock = DockStyle.Fill,
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 10, 0)
            };
            var field = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = colors.Background,
                Margin = new Padding(0, 0, 0, 2)
            };
            editor.Location = new Point(0, 4);
            editor.Size = new Size(190, 28);
            field.Controls.Add(editor);
            var note = new Label
            {
                Text = hint,
                AutoSize = true,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                Location = new Point(202, 8)
            };
            field.Controls.Add(note);
            root.Controls.Add(label, 0, row);
            root.Controls.Add(field, 1, row);
        }

        private static void ConfigureTextBox(TextBox box, string value, Palette colors)
        {
            box.Text = value;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = colors.Input;
            box.ForeColor = colors.TextPrimary;
            box.Font = new Font("Microsoft YaHei UI", 9.5F);
        }

        private void AddRowWithButton(
            TableLayoutPanel root,
            int row,
            string labelText,
            TextBox editor,
            string buttonText,
            Palette colors,
            string hint,
            EventHandler onClick)
        {
            var label = new Label
            {
                Text = labelText,
                Dock = DockStyle.Fill,
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 10, 0)
            };
            var field = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = colors.Background,
                Margin = new Padding(0, 0, 0, 2)
            };
            editor.Location = new Point(0, 4);
            editor.Size = new Size(140, 28);
            field.Controls.Add(editor);

            var button = UiButtonStyler.Create(
                buttonText,
                UiButtonStyle.Secondary,
                new UiButtonStyler.UiButtonColors
                {
                    Accent = colors.Accent,
                    AccentHover = colors.AccentHover,
                    Danger = colors.Danger,
                    Surface = colors.Surface,
                    Border = colors.Border,
                    TextPrimary = colors.TextPrimary,
                    SurfaceAlt = colors.SurfaceAlt
                });
            button.Location = new Point(150, 2);
            button.Size = new Size(90, 30);
            button.Click += onClick;
            field.Controls.Add(button);

            var note = new Label
            {
                Text = hint,
                AutoSize = true,
                ForeColor = colors.TextSecondary,
                BackColor = colors.Background,
                Location = new Point(248, 8)
            };
            field.Controls.Add(note);
            root.Controls.Add(label, 0, row);
            root.Controls.Add(field, 1, row);
        }

        private Button CreateButton(string text, bool primary, Palette colors)
        {
            var button = UiButtonStyler.Create(
                text,
                primary ? UiButtonStyle.Primary : UiButtonStyle.Secondary,
                new UiButtonStyler.UiButtonColors
                {
                    Accent = colors.Accent,
                    AccentHover = colors.AccentHover,
                    Danger = colors.Danger,
                    Surface = colors.Surface,
                    Border = colors.Border,
                    TextPrimary = colors.TextPrimary,
                    SurfaceAlt = colors.SurfaceAlt
                });
            button.Width = primary ? 112 : 84;
            button.Height = UiButtonStyler.DialogButtonHeight;
            button.Margin = new Padding(6, 2, 0, 0);
            button.DialogResult = DialogResult.None;
            return button;
        }

        private void SaveConfig()
        {
            if (!int.TryParse(_listenPortBox.Text.Trim(), out var listenPort))
            {
                ShowValidationError("监听端口必须是数字。");
                return;
            }
            if (!int.TryParse(_advertisedPortBox.Text.Trim(), out var advertisedPort))
            {
                ShowValidationError("对外端口必须是数字。");
                return;
            }

            try
            {
                Config = new NotifyServerConfig
                {
                    ListenAddress = _listenAddressBox.Text,
                    ListenPort = listenPort,
                    AdvertisedAddress = _advertisedAddressBox.Text,
                    AdvertisedPort = advertisedPort,
                    RelayUrl = _relayUrlBox.Text.Trim(),
                    RelayRoomId = _relayRoomIdBox.Text.Trim(),
                    ApprovalPosture = _postureCombo.SelectedIndex == 1
                        ? ApprovalPosture.Notify
                        : _postureCombo.SelectedIndex == 2
                            ? ApprovalPosture.Off
                            : ApprovalPosture.Full
                }.Normalize();
            }
            catch (ArgumentException error)
            {
                ShowValidationError(error.Message);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowValidationError(string message)
        {
            MessageBox.Show(this, message, "网络设置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ApplyTheme()
        {
            var colors = _darkMode ? DarkColors : LightColors;
            BackColor = colors.Background;
        }

        private sealed class Palette
        {
            public Color Background { get; set; }
            public Color Surface { get; set; }
            public Color Input { get; set; }
            public Color TextPrimary { get; set; }
            public Color TextSecondary { get; set; }
            public Color Border { get; set; }
            public Color Accent { get; set; }
            public Color AccentHover { get; set; }
            public Color Danger { get; set; }
            public Color SurfaceAlt { get; set; }
            public Color Warning { get; set; }
        }

        private static Palette LightColors => new Palette
        {
            Background = Color.FromArgb(238, 243, 241),
            Surface = Color.White,
            Input = Color.White,
            TextPrimary = Color.FromArgb(25, 48, 42),
            TextSecondary = Color.FromArgb(101, 118, 111),
            Border = Color.FromArgb(198, 214, 206),
            Accent = Color.FromArgb(111, 134, 126),
            AccentHover = Color.FromArgb(94, 118, 109),
            Danger = Color.FromArgb(190, 76, 73),
            SurfaceAlt = Color.FromArgb(240, 246, 243),
            Warning = Color.FromArgb(155, 103, 35)
        };

        private static Palette DarkColors => new Palette
        {
            Background = Color.FromArgb(14, 22, 20),
            Surface = Color.FromArgb(23, 36, 32),
            Input = Color.FromArgb(18, 29, 26),
            TextPrimary = Color.FromArgb(236, 246, 241),
            TextSecondary = Color.FromArgb(165, 187, 177),
            Border = Color.FromArgb(53, 76, 67),
            Accent = Color.FromArgb(142, 174, 161),
            AccentHover = Color.FromArgb(165, 192, 180),
            Danger = Color.FromArgb(235, 113, 105),
            SurfaceAlt = Color.FromArgb(31, 49, 43),
            Warning = Color.FromArgb(232, 184, 101)
        };
    }
}
