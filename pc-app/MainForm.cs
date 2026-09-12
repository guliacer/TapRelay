using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TapRelay
{
    public sealed class MainForm : Form
    {
        // 单实例退出信号：Program.Main 创建，新实例启动时触发，
        // 本实例收到后走正常退出路径（停止服务、释放 1122 端口）。
        private readonly EventWaitHandle _exitSignal;
        private Thread? _exitWatcher;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Windows 10 2004+ 的沉浸式暗色标题栏属性；1809-1909 使用旧值 19。
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        // Win11 / 较新 Win10：直接上色标题栏与文字，避免仅 immersive 标志仍显示系统浅色条。
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

        private const int ShutdownDelaySeconds = 10;
        private const int SleepAcknowledgementDelayMilliseconds = 350;
        private const int MaxUiLogLines = 500;
        private int _sleepRequestPending;

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.Bool)] bool hibernate,
            [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
            [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

        /// <summary>
        /// 标题栏跟随应用深/浅主题：深色模式用暗色标题栏与浅色文字，
        /// 浅色模式恢复系统默认。旧系统不支持时静默保持系统默认。
        /// </summary>
        private static void ApplyTitleBarTheme(IntPtr hwnd, bool dark, Color? caption = null, Color? text = null)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
                }

                // COLORREF 是 0x00BBGGRR
                var captionColor = dark
                    ? ToColorRef(caption ?? DarkTheme.Background)
                    : DWMWA_COLOR_DEFAULT;
                var textColor = dark
                    ? ToColorRef(text ?? DarkTheme.TextPrimary)
                    : DWMWA_COLOR_DEFAULT;
                var borderColor = dark
                    ? ToColorRef(caption ?? DarkTheme.Border)
                    : DWMWA_COLOR_DEFAULT;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch (Exception)
            {
                // dwmapi 不可用（旧系统）时保持系统默认标题栏。
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private readonly NotifyServer _server = new NotifyServer(NetworkSettingsStore.Load());
        private readonly DiagnosticLogWriter _diagnosticLogWriter = new DiagnosticLogWriter(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanNotify",
                "pc-server.log"));
        private RemoteRelayBridge? _relayBridge;
        private readonly UiState _uiState = UiStateStore.Load();
        private readonly AgentCompletionMonitor _agentMonitor;
        private readonly ProcessLifecycleMonitor _processLifecycleMonitor;
        private readonly ClaudeHahaPermissionBridge _hahaPermissionBridge;
        private readonly Label _statusValue = new Label();
        private readonly Label _clientsValue = new Label();
        private readonly CenteredTextBox _addressBox = new CenteredTextBox();
        private readonly CenteredTextBox _messageBox = new CenteredTextBox();
        private readonly TextBox _logBox = new TextBox();
        private readonly Button _startButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _sendButton = new Button();
        private readonly Button _qrButton = new Button();
        private readonly Button _autoDetectAppsButton = new Button();
        private readonly Button _selectAppButton = new Button();
        private readonly MoreIconButton _serviceMoreButton = new MoreIconButton();
        private readonly ContextMenuStrip _serviceMoreMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _toggleAddressMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _networkSettingsMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _autoStartMenuItem = new ToolStripMenuItem();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly NotifyIcon _trayIcon = new NotifyIcon();
        private TrayToastForm? _trayToast;
        private PersonalProjectsForm? _testProjectsForm;
        private readonly BookIconButton _personalProjectsButton = new BookIconButton();
        private readonly ThemeIconButton _themeButton = new ThemeIconButton();
        private readonly LogScrollBar _messageScrollBar;
        private readonly LogScrollBar _logScrollBar;
        private readonly LinkedList<string> _uiLogLines = new LinkedList<string>();
        private readonly System.Windows.Forms.Timer _uiStateSaveTimer =
            new System.Windows.Forms.Timer { Interval = 400 };
        private readonly NotificationAppsPanel _notificationAppsPanel = new NotificationAppsPanel();
        private readonly VisualCardPanel _serviceCard = new VisualCardPanel();
        private readonly VisualCardPanel _notificationCard = new VisualCardPanel();
        private readonly VisualCardPanel _logCard = new VisualCardPanel();
        private bool _isDarkMode;
        private bool _exitRequested;
        // 布局控件就绪前（构造函数早期 OnResize 被触发时）RecalculateLayout 会访问到
        // 尚未赋值的 _messageScrollBar 等字段，用该标志避免在 BuildLayout 完成前重排。
        private bool _layoutReady;
        // RecalculateLayout 重入保护，见该方法注释。
        private bool _relayouting;
        // 布局过程按实测控件高度算出的窗口最小高度，由 ApplyPendingMinimumHeight
        // 在重入保护释放后应用。见 LayoutNotificationAndLogCards 末尾的说明。
        private int _requiredMinHeight;
        private readonly Label _brandLabel = new Label();
        private readonly Label _titleLabel = new Label();
        private readonly Label _subtitleLabel = new Label();
        private readonly Label _serviceCardLabel = new Label();
        private readonly Label _statusLabel = new Label();
        private readonly Label _clientsLabel = new Label();
        private readonly Label _addressLabel = new Label();
        private readonly Label _addressHintLabel = new Label();
        private readonly Label _messageLabel = new Label();
        private readonly Label _notificationAppsLabel = new Label();
        private readonly Label _logLabel = new Label();
        private readonly Label _logHintLabel = new Label();
        // IP 脱敏开关：与 APP 端 isHostMasked 对齐，默认 false（显示真实 IP）。
        // _realAddressText 始终保存来自 snapshot 的真实地址，切换时只改 _addressBox 显示。
        private bool _isAddressMasked;
        private string _realAddressText = string.Empty;

        public MainForm(EventWaitHandle exitSignal)
        {
            _exitSignal = exitSignal;
            StartExitWatcher();
            Text = "TapRelay";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            // 这里只是首帧的起始值，真正的下限由布局按实测控件高度算出并抬高
            // （见 LayoutNotificationAndLogCards 末尾与 ApplyPendingMinimumHeight）。
            // 1008 = 本机 100% DPI 估算值：通知面板已改为完整预留已安装分区的两行图标
            // （见 NotificationAppsPanel.MinContentHeight），最小高度需容纳"两行图标 +
            // 未安装分区最小可视区 + 运行日志 130px"。写在这里让首帧就足够高、避免
            // "先显示再被顶大"的跳动；换 DPI 或字体时会被 ApplyPendingMinimumHeight 修正。
            MinimumSize = new Size(800, 1008);
            // 默认高度略高于最小值：最小态下两行图标刚好完整、运行日志卡在 LogMinHeight；
            // 1040 额外给运行日志约 32px 呼吸空间。
            Size = new Size(880, 1040);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont(9.5F);
            BackColor = LightTheme.Background;

            _server.SnapshotChanged += OnSnapshotChanged;
            _server.LogLine += AppendLog;
            _server.AppsChanged += OnAppsChanged;
            // 门控在运行期自动启用新安装来源后（如 TapRelay 运行期间重装的 ZCode），
            // 重跑面板自动检测，让启用集与"已安装"芯片收敛到新状态。
            _server.SourceAutoEnabled += OnSourceAutoEnabled;

            // Relay bridge: forward broadcasts to remote phones and process incoming messages.
            _server.OutboundBroadcast += OnOutboundBroadcast;

            // Agent API proxy bootstrap: lazy-init proxies so the phone can send
            // agent.call / agent.probe RPCs through the relay.
            AgentApiProxyBootstrap.Bootstrap(AppendLog);

            _agentMonitor = new AgentCompletionMonitor(_server, AppendLog);
            // Hook 事件只带 Claude Code 的启动目录，真实项目目录由转录解析器记忆，
            // 这里把回查入口交给服务端，避免所有会话的项目名都退化成用户主目录名。
            _server.ClaudeDirectoryResolver = _agentMonitor.GetClaudeWorkingDirectory;
            _server.ControlRequestHandler = HandleControlRequestAsync;
            // Claude Code Haha 权限审批：监听本地 sidecar 的 permission_request，
            // 让手机通知带上真实 requestId（否则「允许/拒绝」按钮不渲染），
            // 并把手机端决策原路回写。既有等待启发式不受影响。
            _hahaPermissionBridge = new ClaudeHahaPermissionBridge(_server, AppendLog);
            _server.ClaudeHahaApprovalResolver = _hahaPermissionBridge.TryResolveAsync;
            // 雷神加速器关闭提醒为内置功能：进程组结束时推送一次提醒，经与其它
            // 监视器一致的广播入口发送；来源 leigod 需在通知应用面板中启用才会下发。
            _processLifecycleMonitor = new ProcessLifecycleMonitor(
                (message, source, status, taskId, cwd, durationMs, projectName, notificationTitle, actionUrl) =>
                    _server.BroadcastFromMonitorAsync(
                        message,
                        source,
                        status,
                        taskId,
                        cwd,
                        durationMs,
                        projectName,
                        notificationTitle,
                        actionUrl),
                AppendLog);
            _messageScrollBar = new LogScrollBar(_messageBox);
            _logScrollBar = new LogScrollBar(_logBox);

            BuildLayout();
            BuildTrayIcon();
            Application.Idle += (_, __) =>
            {
                _messageScrollBar.Recalculate();
                _logScrollBar.Recalculate();
            };
            _messageBox.MouseWheel += (_, __) => _messageScrollBar.Recalculate();
            _messageBox.TextChanged += (_, __) =>
            {
                _messageScrollBar.Recalculate();
                ScheduleUiStateSave();
            };
            // 失焦时也保存一次，确保输入未触发 TextChanged 的边界情况（IME 组合等）。
            _messageBox.LostFocus += (_, __) =>
            {
                _uiStateSaveTimer.Stop();
                SaveUiState();
            };
            _uiStateSaveTimer.Tick += (_, __) =>
            {
                _uiStateSaveTimer.Stop();
                SaveUiState();
            };
            _logBox.MouseWheel += (_, e) => _logScrollBar.ScrollByMouseWheel(e.Delta);
            _logBox.TextChanged += (_, __) => _logScrollBar.Recalculate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme(Handle, _isDarkMode);
        }

        /// <summary>
        /// 后台线程监听单实例退出信号：新实例启动时由 Program.Main 触发，
        /// 本实例随即在 UI 线程执行正常退出（释放 1122 端口后进程结束）。
        /// </summary>
        private void StartExitWatcher()
        {
            _exitWatcher = new Thread(() =>
            {
                try
                {
                    while (_exitSignal.WaitOne())
                    {
                        BeginInvoke(new Action(ExitForNewInstance));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 窗体退出时事件被释放，监听线程随之结束。
                }
            })
            {
                IsBackground = true,
                Name = "TapRelayExitWatcher"
            };
            _exitWatcher.Start();
        }

        /// <summary>
        /// 新实例接管：设置退出标志后关闭主窗体，
        /// OnFormClosing 走正常清理路径（停止服务、释放托盘）。
        /// </summary>
        private void ExitForNewInstance()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _trayIcon.Visible = false;
            try
            {
                // 新实例正等待互斥锁与 1122 端口，不能依赖 Form.Close() 后的
                // 事件循环何时结束；先释放服务资源，再确定性退出当前进程。
                _agentMonitor.Dispose();
                _processLifecycleMonitor.Dispose();
                _hahaPermissionBridge.Dispose();
                _server.StopAsync().GetAwaiter().GetResult();
                _diagnosticLogWriter.Dispose();
            }
            catch
            {
                // 接管退出不能因监控或 socket 清理异常而留下残留进程。
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await StartServerAsync();
            _agentMonitor.Start();
            // Initialize built-in notification sources before remote apps can
            // publish an enabled-source snapshot that temporarily hides them.
            await _notificationAppsPanel.RunAutoDetectAsync();
            // 无论是否检测到应用，都渲染完整已适配应用目录（已安装在前、未安装在后）。
            _notificationAppsPanel.RenderFullCatalog();
            _processLifecycleMonitor.Start(BuildBuiltInLifecycleRules());
            _hahaPermissionBridge.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 首帧显示后再刷一次标题栏，避免句柄重建/系统主题抢回浅色 caption。
            ApplyTitleBarTheme(Handle, _isDarkMode);
            // 首次显示后把运行日志滚动到顶部，展示最新一行（启动日志是在窗口不可见时写入的）。
            ScrollLogToTop();
            _logScrollBar.Recalculate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _uiStateSaveTimer.Stop();
            _uiStateSaveTimer.Dispose();
            SaveUiState();
            _agentMonitor.Dispose();
            _processLifecycleMonitor.Dispose();
            _hahaPermissionBridge.Dispose();
            // 同步等待停止：StopAsync 会立即中止手机客户端并在约 300ms 后强制释放，不会无限阻塞。
            // 不能 await：async void + 窗体句柄销毁后，StopAsync 里的 Log/PublishSnapshot
            // 通过 BeginInvoke 回 UI 线程会抛 InvalidOperationException（见 08-19 崩溃日志）。
            try
            {
                _server.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 关闭阶段吞掉所有异常，确保进程一定退出。
            }
            _server.Dispose();
            _diagnosticLogWriter.Dispose();
            _exitSignal.Dispose();
            base.OnFormClosing(e);
        }

        private void BuildLayout()
        {
            _brandLabel.Text = "TapRelay";
            _brandLabel.AutoSize = true;
            _brandLabel.Font = new Font("Microsoft YaHei UI", 12.5F, FontStyle.Bold);
            _brandLabel.ForeColor = LightTheme.Accent;

            _titleLabel.Text = "任务通知";
            _titleLabel.AutoSize = true;
            _titleLabel.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
            _titleLabel.ForeColor = LightTheme.TextPrimary;

            _subtitleLabel.Text = "开发工具任务完成后推送到手机。";
            _subtitleLabel.AutoSize = true;
            _subtitleLabel.Font = UiFont(9.5F);
            _subtitleLabel.ForeColor = LightTheme.TextSecondary;
            _subtitleLabel.Tag = "muted";

            _serviceCard.BackColor = LightTheme.Surface;
            _serviceCard.BorderColor = LightTheme.Border;

            _serviceCardLabel.Text = "服务与连接";
            _serviceCardLabel.AutoSize = true;
            _serviceCardLabel.Font = UiFont(10.5F, FontStyle.Bold);
            _serviceCardLabel.ForeColor = LightTheme.TextPrimary;

            _statusLabel.Text = "服务状态";
            _statusLabel.AutoSize = true;
            _statusLabel.Font = UiFont(9.5F);
            _statusLabel.ForeColor = LightTheme.TextSecondary;
            _statusLabel.Tag = "muted";
            _statusValue.AutoSize = true;
            _statusValue.Font = UiFont(10.5F, FontStyle.Bold);

            _clientsLabel.Text = "手机连接数";
            _clientsLabel.AutoSize = true;
            _clientsLabel.Font = UiFont(9.5F);
            _clientsLabel.ForeColor = LightTheme.TextSecondary;
            _clientsLabel.Tag = "muted";
            _clientsValue.AutoSize = true;
            _clientsValue.Font = UiFont(10.5F, FontStyle.Bold);

            _addressLabel.Text = "手机连接地址";
            _addressLabel.AutoSize = true;
            _addressLabel.Font = UiFont(9.5F, FontStyle.Bold);
            _addressLabel.ForeColor = LightTheme.TextPrimary;

            _addressHintLabel.Text = "可扫码连接；公网请在网络设置中填写对外地址";
            _addressHintLabel.AutoSize = true;
            _addressHintLabel.Font = UiFont(8.5F);
            _addressHintLabel.ForeColor = LightTheme.TextSecondary;
            _addressHintLabel.Tag = "muted";

            // 所有控件的位置与尺寸统一由 RecalculateLayout 计算，这里只设置
            // 与布局无关的外观、文案与事件，避免两处坐标互相覆盖。
            _addressBox.ReadOnly = true;
            _addressBox.BorderStyle = BorderStyle.FixedSingle;
            _addressBox.BackColor = LightTheme.InputSurface;
            _addressBox.Font = new Font("Consolas", 10F);
            _addressBox.WordWrap = false;
            _addressBox.Padding = new Padding(10, 7, 10, 5);

            _startButton.Text = "启动服务";
            _startButton.Click += async (_, __) => await StartServerAsync();

            _stopButton.Text = "停止服务";
            _stopButton.Click += async (_, __) => await StopServerAsync();

            _qrButton.Text = "二维码";
            _qrButton.Click += (_, __) => ShowConnectionQr();

            _serviceMoreButton.Size = new Size(42, InputHeight);
            _serviceMoreButton.Cursor = Cursors.Hand;
            _serviceMoreButton.Click += (_, __) =>
                _serviceMoreMenu.Show(_serviceMoreButton, new Point(0, _serviceMoreButton.Height));
            _toolTip.SetToolTip(_serviceMoreButton, "更多服务操作");

            _toggleAddressMenuItem.Click += (_, __) => ToggleAddressMask();
            _networkSettingsMenuItem.Text = "网络设置";
            _networkSettingsMenuItem.Click += (_, __) => ShowNetworkSettings();
            _autoStartMenuItem.Text = "开机自启动";
            _autoStartMenuItem.CheckOnClick = true;
            _autoStartMenuItem.Checked = AutoStartHelper.IsEnabled();
            _autoStartMenuItem.CheckedChanged += (_, __) =>
            {
                AutoStartHelper.SetEnabled(_autoStartMenuItem.Checked);
                UiStateStore.Save(_uiState);
            };
            _serviceMoreMenu.ShowImageMargin = false;
            _serviceMoreMenu.Items.AddRange(new ToolStripItem[]
            {
                _toggleAddressMenuItem,
                new ToolStripSeparator(),
                _networkSettingsMenuItem,
                _autoStartMenuItem
            });

            _messageLabel.Text = "测试通知";
            _messageLabel.AutoSize = true;
            _messageLabel.Font = UiFont(9.5F, FontStyle.Bold);
            _messageLabel.ForeColor = LightTheme.TextPrimary;

            _messageBox.Font = UiFont(10F);
            // 测试文本持久化：上次保存过的内容优先恢复，没存过才用默认值。
            _messageBox.Text = string.IsNullOrWhiteSpace(_uiState.TestMessage)
                ? "Agent 任务已完成"
                : _uiState.TestMessage;
            _messageBox.ScrollBars = ScrollBars.None;
            _messageBox.BorderStyle = BorderStyle.FixedSingle;
            _messageBox.BackColor = LightTheme.InputSurface;
            _messageBox.Padding = new Padding(10, 7, 10, 5);

            _sendButton.Text = "发送测试";
            _sendButton.Click += async (_, __) => await SendTestAsync();

            _notificationAppsLabel.Text = "通知应用";
            _notificationAppsLabel.AutoSize = true;
            _notificationAppsLabel.Font = UiFont(10.5F, FontStyle.Bold);
            _notificationAppsLabel.ForeColor = LightTheme.TextPrimary;
            _autoDetectAppsButton.Text = "自动检测";
            _autoDetectAppsButton.Click += async (_, __) => await RunNotificationAutoDetectAsync();
            _selectAppButton.Text = "应用选择";
            _selectAppButton.Click += (_, __) => _notificationAppsPanel.OpenApplicationSelection();
            _notificationCard.BackColor = LightTheme.Surface;
            _notificationCard.BorderColor = LightTheme.Border;
            _notificationAppsPanel.BackColor = LightTheme.Surface;
            _notificationAppsPanel.EnabledAppsChanged += OnEnabledAppsChanged;

            _logLabel.Text = "运行记录";
            _logLabel.AutoSize = true;
            _logLabel.Font = UiFont(9.5F);
            _logLabel.ForeColor = LightTheme.TextPrimary;
            _logLabel.Tag = "muted";

            _logHintLabel.Text = "最新一行在顶部";
            _logHintLabel.AutoSize = true;
            _logHintLabel.Font = UiFont(8.5F);
            _logHintLabel.ForeColor = LightTheme.TextSecondary;
            _logHintLabel.Tag = "muted";

            _logCard.BackColor = LightTheme.Surface;
            _logCard.BorderColor = LightTheme.Border;

            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            // 原生滚动条在深色主题下仍是系统浅色；改用自绘 LogScrollBar。
            _logBox.ScrollBars = ScrollBars.None;
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            _logBox.BackColor = LightTheme.InputSurface;
            _logBox.ForeColor = LightTheme.TextSecondary;
            _logBox.Font = new Font("Consolas", 9.5F);
            _logBox.WordWrap = true;
            _logBox.Padding = new Padding(10, 8, 10, 8);

            // 通知应用面板高度变化（图标换行）时，统一走集中式布局重算。
            _notificationAppsPanel.PreferredHeightChanged += _ =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)RecalculateLayout);
                }
                else
                {
                    RecalculateLayout();
                }
            };

            _personalProjectsButton.Size = new Size(30, 30);
            _personalProjectsButton.Cursor = Cursors.Hand;
            _personalProjectsButton.Click += (_, __) => ShowPersonalProjects();

            _themeButton.IsDarkMode = _isDarkMode;
            _themeButton.Size = new Size(30, 30);
            _themeButton.Cursor = Cursors.Hand;
            _themeButton.Click += (_, __) => ToggleTheme();

            ApplyButtonStyle(_startButton, UiButtonStyler.UiButtonStyle.Primary);
            ApplyButtonStyle(_stopButton, UiButtonStyler.UiButtonStyle.Danger);
            ApplyButtonStyle(_qrButton, UiButtonStyler.UiButtonStyle.Secondary);
            ApplyButtonStyle(_sendButton, UiButtonStyler.UiButtonStyle.Primary);
            ApplyButtonStyle(_autoDetectAppsButton, UiButtonStyler.UiButtonStyle.Secondary);
            ApplyButtonStyle(_selectAppButton, UiButtonStyler.UiButtonStyle.Secondary);

            Controls.AddRange(new Control[]
            {
                _serviceCard,
                _notificationCard,
                _logCard,
                _brandLabel,
                _titleLabel,
                _subtitleLabel,
                _serviceCardLabel,
                _statusLabel,
                _statusValue,
                _clientsLabel,
                _clientsValue,
                _addressLabel,
                _addressHintLabel,
                _addressBox,
                _startButton,
                _stopButton,
                _qrButton,
                _serviceMoreButton,
                _messageLabel,
                _messageBox,
                _messageScrollBar,
                _sendButton,
                _notificationAppsLabel,
                _autoDetectAppsButton,
                _selectAppButton,
                _notificationAppsPanel,
                _logLabel,
                _logHintLabel,
                _logBox,
                _logScrollBar,
                _personalProjectsButton,
                _themeButton
            });

            // Keep the visual cards behind the interactive controls.
            _serviceCard.SendToBack();
            _notificationCard.SendToBack();
            _logCard.SendToBack();

            // 主题与隐藏 IP 开关从 ui-state.json 恢复；
            // DarkMode 为 null（从未写入）时兼容读取旧 settings.json，并立即回写迁移。
            if (_uiState.DarkMode == null)
            {
                _uiState.DarkMode = LoadTheme();
                // 迁移保存前同步当前输入框文本，避免把用户已保存的 TestMessage 覆盖成空值。
                _uiState.TestMessage = _messageBox.Text ?? string.Empty;
                UiStateStore.Save(_uiState);
            }
            _isDarkMode = _uiState.DarkMode.Value;
            _isAddressMasked = _uiState.AddressMasked;
            UpdateAddressMaskMenuText();
            ApplyTheme();
            OnSnapshotChanged(_server.GetSnapshot());
            _layoutReady = true;
            RecalculateLayout();
        }

        // 统一布局度量。所有间距只允许取这些常量，禁止在 RecalculateLayout 中
        // 出现新的字面量偏移，否则会重新回到"改一处坏一处"的硬编码坐标布局。
        private const int PageMargin = 20;       // 卡片到窗口边缘
        private const int CardPad = 18;          // 卡片内边距
        private const int Gap = 8;               // 同组控件间距
        private const int MetricStartGap = 28;   // 卡片标题与第一组状态指标的间距
        private const int MetricGroupGap = 32;   // 两组状态指标之间的最小间距
        private const int SectionGap = 14;       // 卡片之间的纵向间距
        private const int RowGap = 12;           // 卡片内两行之间的间距
        private const int InputHeight = UiButtonStyler.MainWindowButtonHeight; // 地址框 / 主按钮统一 42px
        private const int MessageHeight = 80;    // 测试消息输入框高度：约 4 行文本 + 上下留白，超出滚动查看
        // 运行日志可视区最小高度：Consolas 9.5pt 实测行高 15px，130px 约 8 行。
        // 窗口最小高度由该值反推（见 LayoutNotificationAndLogCards 末尾），
        // 调大该值会自动抬高 MinimumSize，不需要手工同步两处数字。
        private const int LogMinHeight = 130;

        /// <summary>
        /// 集中式响应式布局。规则：
        /// 1. 标题区、服务卡、通知卡自顶向下依次堆叠，每段高度由内容算出，不写死 Y；
        /// 2. 运行日志卡底部锚定，高度 = 通知卡底部到窗口底部的剩余空间，最小
        ///    <see cref="LogMinHeight"/>，因此窗口变高时日志变长，变矮时其他区域先压缩；
        /// 3. 通知应用面板拿到"剩余空间 - 日志最小高度"作为高度上限，超出时面板内部
        ///    滚动，保证运行日志始终留在可视范围内。
        /// </summary>
        private void RecalculateLayout()
        {
            // 设置面板尺寸会触发它的 PreferredHeightChanged，回调又会调用本方法。
            // 用该标志把一次窗口变化折叠为一趟布局，避免递归和可见的抖动。
            if (IsDisposed || !_layoutReady || _relayouting)
            {
                return;
            }

            _relayouting = true;
            try
            {
                RecalculateLayoutCore();
            }
            finally
            {
                _relayouting = false;
            }

            // 最小高度由布局过程按实测度量算出，但必须在重入保护释放后才能生效：
            // 抬高 MinimumSize 可能把窗口顶高并触发 OnResize，放在保护内会被
            // 直接丢弃，窗口停在新尺寸而布局仍是旧尺寸的结果。
            ApplyPendingMinimumHeight();
        }

        /// <summary>
        /// 应用布局过程算出的窗口最小高度。只抬高不压低，避免与用户手动缩放
        /// 来回争夺尺寸；生效后由 OnResize 自然触发一次针对新尺寸的重排。
        /// </summary>
        private void ApplyPendingMinimumHeight()
        {
            if (_requiredMinHeight <= 0 || MinimumSize.Height >= _requiredMinHeight)
            {
                return;
            }

            MinimumSize = new Size(MinimumSize.Width, _requiredMinHeight);
        }

        private void RecalculateLayoutCore()
        {
            var clientW = Math.Max(360, ClientSize.Width);
            var clientH = Math.Max(360, ClientSize.Height);
            var cardWidth = Math.Max(320, clientW - 2 * PageMargin);
            var cardLeft = PageMargin;
            var innerLeft = cardLeft + CardPad;
            var innerRight = cardLeft + cardWidth - CardPad;
            var innerWidth = innerRight - innerLeft;

            // ---- 页头：产品名、标题与副标题形成轻量品牌层级。
            _brandLabel.Location = new Point(cardLeft + 8, 14);
            _titleLabel.Location = new Point(cardLeft + 8, _brandLabel.Bottom + 1);
            _subtitleLabel.Location = new Point(
                cardLeft + 10,
                _titleLabel.Bottom + 3);

            _themeButton.Location = new Point(
                clientW - PageMargin - _themeButton.Width,
                _titleLabel.Top + (_titleLabel.Height - _themeButton.Height) / 2);
            _personalProjectsButton.Location = new Point(
                _themeButton.Left - Gap - _personalProjectsButton.Width,
                _themeButton.Top);

            // ---- 服务与连接卡：标题与关键状态共用卡片标题带，避免增加纵向高度。
            var cardTop = _subtitleLabel.Bottom + SectionGap;
            var y = cardTop + CardPad;

            _serviceCardLabel.Location = new Point(innerLeft, y);
            var statusLeft = Math.Max(
                _serviceCardLabel.Right + MetricStartGap,
                innerLeft + innerWidth / 3);
            _statusLabel.Location = new Point(statusLeft, y + 2);
            _statusValue.Location = new Point(_statusLabel.Right + Gap, y);
            var clientsLeft = Math.Max(
                _statusValue.Right + MetricGroupGap,
                innerLeft + 2 * innerWidth / 3);
            _clientsLabel.Location = new Point(clientsLeft, y + 2);
            _clientsValue.Location = new Point(_clientsLabel.Right + Gap, y);
            y = Math.Max(
                    _serviceCardLabel.Bottom,
                    Math.Max(_statusValue.Bottom, _clientsValue.Bottom))
                + RowGap;

            _addressLabel.Location = new Point(innerLeft, y);
            _addressHintLabel.Location = new Point(
                _addressLabel.Right + Gap,
                y + Math.Max(0, (_addressLabel.Height - _addressHintLabel.Height) / 2));
            y = Math.Max(_addressLabel.Bottom, _addressHintLabel.Bottom) + Gap;

            // 地址行只保留当前服务的主操作、二维码和更多菜单；启动与停止互斥显示，
            // 隐藏 IP / 网络设置留在更多菜单，既不牺牲功能也不让五个按钮抢占首屏。
            var primaryActionWidth = Math.Max(96,
                TextRenderer.MeasureText("启动服务", UiFont(9.5F)).Width + 28);
            const int qrButtonWidth = 78;
            const int moreButtonWidth = 42;
            _startButton.Size = new Size(primaryActionWidth, InputHeight);
            _stopButton.Size = new Size(primaryActionWidth, InputHeight);
            _qrButton.Size = new Size(qrButtonWidth, InputHeight);
            _serviceMoreButton.Size = new Size(moreButtonWidth, InputHeight);
            _serviceMoreButton.Location = new Point(innerRight - moreButtonWidth, y);
            _qrButton.Location = new Point(_serviceMoreButton.Left - Gap - qrButtonWidth, y);
            _stopButton.Location = new Point(_qrButton.Left - Gap - primaryActionWidth, y);
            _startButton.Location = _stopButton.Location;
            _addressBox.Location = new Point(innerLeft, y);
            _addressBox.Size = new Size(
                Math.Max(120, _startButton.Left - Gap - innerLeft),
                InputHeight);
            y = _addressBox.Bottom + RowGap;

            _messageLabel.Location = new Point(innerLeft, y);
            y = _messageLabel.Bottom + Gap;

            // 测试消息行：发送按钮右对齐并与输入框垂直居中对齐。
            const int sendW = 148;
            const int scrollW = ScrollBarRenderer.PreferredWidth;
            var sendX = innerRight - sendW;
            var scrollX = sendX - Gap - scrollW;
            _sendButton.Size = new Size(sendW, InputHeight);
            _sendButton.Location = new Point(sendX, y + (MessageHeight - InputHeight) / 2);
            _messageScrollBar.Location = new Point(scrollX, y);
            _messageScrollBar.Size = new Size(scrollW, MessageHeight);
            _messageBox.Location = new Point(innerLeft, y);
            _messageBox.Size = new Size(
                Math.Max(120, scrollX - Gap - innerLeft),
                MessageHeight);

            _serviceCard.Location = new Point(cardLeft, cardTop);
            _serviceCard.Size = new Size(cardWidth, _messageBox.Bottom + CardPad - cardTop);
            _serviceCard.SetSectionLayout(
                _addressLabel.Top - cardTop - RowGap / 2,
                _messageLabel.Top - cardTop - RowGap / 2);

            LayoutNotificationAndLogCards(cardLeft, cardWidth, innerLeft, innerRight, clientH);
        }

        /// <summary>
        /// 排布通知应用卡与运行日志卡。日志卡底部锚定，先按最小高度为它预留空间，
        /// 把剩余空间作为上限交给通知面板；面板确定实际高度后，日志卡再吃掉
        /// 剩下的全部空间，因此"日志区始终可见"由预留步骤保证，"不浪费空白"
        /// 由回填步骤保证。
        /// </summary>
        private void LayoutNotificationAndLogCards(
            int cardLeft,
            int cardWidth,
            int innerLeft,
            int innerRight,
            int clientH)
        {
            var notifyTop = _serviceCard.Bottom + SectionGap;
            var pageBottom = clientH - PageMargin;

            // 标题与两个标准次要按钮共用同一标题行，保证操作入口不再与分组标题抢行。
            var headerY = notifyTop + CardPad;
            const int notificationActionWidth = 106;
            const int notificationActionGap = 8;
            _autoDetectAppsButton.Size = new Size(notificationActionWidth, InputHeight);
            _selectAppButton.Size = new Size(notificationActionWidth, InputHeight);
            _selectAppButton.Location = new Point(innerRight - notificationActionWidth, headerY);
            _autoDetectAppsButton.Location = new Point(
                _selectAppButton.Left - notificationActionGap - notificationActionWidth,
                headerY);
            _notificationAppsLabel.Location = new Point(
                innerLeft,
                headerY + (InputHeight - _notificationAppsLabel.Height) / 2);

            // 标题行下方保留一个完整行距，避免面板分区标题紧贴操作按钮。
            var panelTop = headerY + InputHeight + RowGap;

            // 先定宽再给高度上限：面板按宽度决定每行图标数（进而决定行数），
            // 顺序颠倒会让它先按旧宽度算一遍高度，产生一次多余的重排和跳动。
            _notificationAppsPanel.Location = new Point(innerLeft, panelTop);
            _notificationAppsPanel.Width = innerRight - innerLeft;

            // 为运行日志卡预留最小空间，剩下的才允许通知面板使用。
            var logReserved = CardPad + _logLabel.Height + Gap + LogMinHeight + CardPad;
            _notificationAppsPanel.MaxContentHeight =
                pageBottom - logReserved - SectionGap - panelTop - CardPad;

            var panelHeight = _notificationAppsPanel.Height;

            _notificationCard.Location = new Point(cardLeft, notifyTop);
            _notificationCard.Size = new Size(
                cardWidth,
                panelTop + panelHeight + CardPad - notifyTop);
            _notificationCard.SetSectionLayout(panelTop - notifyTop - RowGap / 2);

            // 运行日志卡：顶部紧随通知卡，底部固定贴到窗口下边距。
            var logTop = _notificationCard.Bottom + SectionGap;
            var logCardHeight = Math.Max(
                CardPad + _logLabel.Height + Gap + LogMinHeight + CardPad,
                pageBottom - logTop);
            _logCard.Location = new Point(cardLeft, logTop);
            _logCard.Size = new Size(cardWidth, logCardHeight);

            _logLabel.Location = new Point(innerLeft, logTop + CardPad);
            _logHintLabel.Location = new Point(
                innerRight - _logHintLabel.Width,
                _logLabel.Top + (_logLabel.Height - _logHintLabel.Height) / 2);

            var logBoxTop = _logLabel.Bottom + Gap;
            // 与消息框、通知面板共用同一共享宽度常量，避免滚动条粗细各自漂移。
            const int logScrollW = ScrollBarRenderer.PreferredWidth;
            var logBoxHeight = Math.Max(LogMinHeight, logTop + logCardHeight - CardPad - logBoxTop);
            _logScrollBar.Location = new Point(innerRight - logScrollW, logBoxTop);
            _logScrollBar.Size = new Size(logScrollW, logBoxHeight);
            _logBox.Location = new Point(innerLeft, logBoxTop);
            _logBox.Size = new Size(
                Math.Max(120, innerRight - logScrollW - Gap - innerLeft),
                logBoxHeight);
            _logScrollBar.Recalculate();
            _logCard.SetSectionLayout(logBoxTop - logTop - Gap / 2);

            // 用本次实测高度反推窗口最小高度，交给 ApplyPendingMinimumHeight 生效。
            // 字体行高随系统 DPI 和字体版本变化，构造函数里写死的值在别的机器上
            // 可能不足，导致运行日志被挤出窗口底部；这里让"日志常驻可见"由运行时
            // 的真实度量保证，而不是依赖开发机上的一次测量。
            _requiredMinHeight = panelTop
                + _notificationAppsPanel.MinContentHeight
                + CardPad
                + SectionGap
                + logReserved
                + PageMargin
                + (Height - ClientSize.Height);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateLayout();
        }

        private void BuildTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开窗口", null, (_, __) => ShowFromTray());
            menu.Items.Add("退出软件", null, async (_, __) => await ExitFromTrayAsync());

            _trayIcon.Text = "TapRelay";
            _trayIcon.Icon = Icon ?? SystemIcons.Application;
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;
            _trayIcon.MouseDoubleClick += (_, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    ShowFromTray();
                }
            };
        }

        private async Task StartServerAsync()
        {
            try
            {
                await _server.StartAsync();
                EnsureFirewallRuleAsync();
                StartRelayBridgeIfConfigured();
            }
            catch (Exception error)
            {
                // 端口被占用时自动探测空闲端口并切换，避免新用户卡在"手动换端口"。
                var currentPort = _server.Config.ListenPort;
                var availablePort = NotifyServer.FindAvailablePort(currentPort);
                if (availablePort != currentPort)
                {
                    var config = _server.Config.Clone();
                    config.ListenPort = availablePort;
                    config.AdvertisedPort = availablePort;
                    NetworkSettingsStore.Save(config.Normalize());
                    _server.ApplyConfig(config.Normalize());
                    AppendLog($"端口 {currentPort} 被占用，已自动改用端口 {availablePort}，正在重新启动…");
                    try
                    {
                        await _server.StartAsync();
                        EnsureFirewallRuleAsync();
                        AppendLog($"已切换端口 {availablePort}：手机需重新扫码获取新地址。");
                        return;
                    }
                    catch (Exception retryError)
                    {
                        AppendLog("切换端口后仍启动失败：" + retryError.Message);
                    }
                }

                AppendLog("启动失败：" + error.Message);
                MessageBox.Show(
                    this,
                    "监听端口被占用，请关闭其他占用进程或在网络设置中更换端口后重试。",
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 服务启动后检查 Windows 防火墙是否放行监听端口：缺失则后台尝试自动添加
        /// （UAC 提权一次），成功记日志；用户取消/失败时给出可复制的修复命令，绝不静默。
        /// </summary>
        private void EnsureFirewallRuleAsync()
        {
            var port = _server.Config.EffectiveAdvertisedPort;
            Task.Run(() =>
            {
                if (FirewallHelper.IsRulePresent(port))
                {
                    return;
                }

                if (FirewallHelper.TryAddRule(port))
                {
                    AppendLog($"已自动放行 Windows 防火墙 TCP {port}（{FirewallHelper.RuleName}）。");
                }
                else
                {
                    AppendLog(
                        "Windows 防火墙未放行 " + port +
                        " 端口：手机可能无法连接。请以管理员运行：netsh advfirewall firewall " +
                        $"add rule name=\"{FirewallHelper.RuleName}\" dir=in action=allow protocol=TCP localport={port}");
                }
            });
        }

        private async Task StopServerAsync()
        {
            // 点击后立即反馈：禁用启动/停止按钮并提示，避免"点了没反应"的观感。
            // 停止过程（立即中止客户端 + 短暂 host 回收）在后台线程执行，
            // UI 线程不被占用，按钮禁用状态由 OnSnapshotChanged 恢复。
            _stopButton.Enabled = false;
            _startButton.Enabled = false;
            _statusValue.Text = "正在停止...";
            _statusValue.ForeColor = (_isDarkMode ? DarkTheme : LightTheme).TextSecondary;
            try
            {
                // 先让 UI 消息循环绘制禁用态和状态文案，再开始网络资源释放。
                await Task.Yield();
                StopRelayBridge();
                await Task.Run(() => _server.StopAsync());
            }
            catch (Exception error)
            {
                AppendLog("停止服务失败：" + error.Message);
            }
            finally
            {
                // StopAsync 的快照通常已投递到 UI；这里覆盖异常路径，确保按钮恢复可用。
                OnSnapshotChanged(_server.GetSnapshot());
            }
        }

        private void ShowNetworkSettings()
        {
            if (_server.GetSnapshot().IsRunning)
            {
                MessageBox.Show(
                    this,
                    "请先停止服务，再修改监听地址或端口。",
                    "网络设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new NetworkSettingsForm(_server.Config, _isDarkMode))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _server.ApplyConfig(dialog.Config);
                NetworkSettingsStore.Save(dialog.Config);
                AppendLog("已更新网络设置：监听 " + dialog.Config.ListenAddress + ":" + dialog.Config.ListenPort +
                    ", 对外 " + (string.IsNullOrWhiteSpace(dialog.Config.AdvertisedAddress)
                        ? "自动发现"
                        : dialog.Config.AdvertisedAddress + ":" + dialog.Config.EffectiveAdvertisedPort));
                OnSnapshotChanged(_server.GetSnapshot());
            }
        }

        private void ShowConnectionQr()
        {
            var endpoints = _server.GetConnectionEndpoints();
            if (endpoints.Count == 0 && !_server.Config.RelayEnabled)
            {
                MessageBox.Show(
                    this,
                    "当前没有可用连接地址，请在网络设置中填写对外地址。",
                    "二维码",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                using (var dialog = new ConnectionQrForm(endpoints, _isDarkMode, _server.Config.RelayUrl, _server.Config.RelayRoomId))
                {
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "二维码生成失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task SendTestAsync()
        {
            try
            {
                await _server.BroadcastFromAppAsync(_messageBox.Text);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "发送失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task RunNotificationAutoDetectAsync()
        {
            if (!_autoDetectAppsButton.Enabled)
            {
                return;
            }

            _autoDetectAppsButton.Enabled = false;
            _autoDetectAppsButton.Text = "检测中…";
            try
            {
                await _notificationAppsPanel.RunAutoDetectAsync();
            }
            catch (Exception error)
            {
                AppendLog("自动检测通知应用失败：" + error.Message);
                MessageBox.Show(this, error.Message, "自动检测失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!IsDisposed)
                {
                    _autoDetectAppsButton.Text = "自动检测";
                    _autoDetectAppsButton.Enabled = true;
                }
            }
        }

        /// <summary>
        /// 把测试消息文本、隐藏 IP 开关、深/浅色主题持久化到 ui-state.json，
        /// 重启 / 重新部署后恢复，不再回退到默认值。
        /// </summary>
        private void SaveUiState()
        {
            try
            {
                _uiState.TestMessage = _messageBox.Text ?? string.Empty;
                _uiState.AddressMasked = _isAddressMasked;
                _uiState.DarkMode = _isDarkMode;
                UiStateStore.Save(_uiState);
            }
            catch
            {
                // 保存失败不应影响 UI 使用。
            }
        }

        private void HideToTray()
        {
            Hide();
            AppendLog("已隐藏到托盘。");
            ShowTrayToast(
                "仍在运行",
                "已最小化到托盘，点击返回。",
                ShowFromTray);
        }

        private void ShowTrayToast(string title, string message, Action onClick)
        {
            _trayToast?.Close();
            var toast = new TrayToastForm(title, message, onClick);
            _trayToast = toast;
            toast.FormClosed += (_, __) =>
            {
                if (ReferenceEquals(_trayToast, toast))
                {
                    _trayToast = null;
                }
            };

            var area = Screen.FromControl(this).WorkingArea;
            toast.StartPosition = FormStartPosition.Manual;
            toast.Location = new Point(
                Math.Max(area.Left + 8, area.Right - toast.Width - 16),
                Math.Max(area.Top + 8, area.Bottom - toast.Height - 16));
            toast.Show();
        }

        private void ShowFromTray()
        {
            _trayToast?.Close();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            // 从托盘打开时窗口此前不可见，滚动请求会被忽略；显示后再补一次滚动到最新行。
            BeginInvoke((Action)ScrollLogToTop);
        }

        private async Task ExitFromTrayAsync()
        {
            // 立即隐藏托盘图标，让用户视觉上感知到"已退出"，不等待服务停止。
            // 之前先 await StopAsync 再 Close，冻结手机的 WebSocket 关闭会拖慢退出，
            // 期间托盘图标一直可见，
            // 用户感觉"点了退出但软件还一直在那里"。
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip.Enabled = false;
            _exitRequested = true;
            AppendLog("正在退出软件...");

            // 后台优雅关闭，最多等 3 秒；超时则强制退出，避免死锁卡住进程。
            var stopTask = Task.Run(() =>
            {
                try
                {
                    _agentMonitor.Dispose();
                    _processLifecycleMonitor.Dispose();
                    _hahaPermissionBridge.Dispose();
                    StopRelayBridge();
                    _server.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // 退出阶段吞掉所有异常，确保进程一定能结束。
                }
            });
            await Task.WhenAny(stopTask, Task.Delay(3000));

            // Close() 会触发 OnFormClosing，其中同步 Dispose 多个对象；若任一在
            // UI 线程抛异常，Close() 会往上抛，导致 Environment.Exit 被跳过，
            // 进程残留并继续占用 1122 端口。用 finally 保证无论如何都强制退出。
            try
            {
                Close();
            }
            catch
            {
                // OnFormClosing 里的清理异常不得阻止进程退出。
            }
            finally
            {
                // 强制终止进程：FileSystemWatcher 的后台线程、Timer 回调等可能仍在运行，
                // 会导致进程在 Close() 后不立即退出。Environment.Exit 确保即时回收。
                Environment.Exit(0);
            }
        }

        private void OnSnapshotChanged(ServerSnapshot snapshot)
        {
            RunOnUiThread(() =>
            {
                var colors = _isDarkMode ? DarkTheme : LightTheme;
                _statusValue.Text = snapshot.IsRunning ? "运行中" : "未启动";
                _statusValue.ForeColor = snapshot.IsRunning ? colors.Success : colors.Danger;
                _serviceCard.AccentColor = snapshot.IsRunning ? colors.Success : colors.Border;
                _serviceCard.Invalidate();
                _clientsValue.Text = snapshot.ClientCount.ToString();
                // 真实地址始终存入 _realAddressText；显示根据脱敏开关切换。
                _realAddressText = snapshot.AddressText;
                _addressBox.Text = _isAddressMasked ? MaskIp(_realAddressText) : _realAddressText;
                _startButton.Enabled = !snapshot.IsRunning;
                _startButton.Visible = !snapshot.IsRunning;
                _stopButton.Enabled = snapshot.IsRunning;
                _stopButton.Visible = snapshot.IsRunning;
                _qrButton.Enabled = snapshot.IsRunning && !string.IsNullOrWhiteSpace(snapshot.AddressText) &&
                    !snapshot.AddressText.StartsWith("未找到", StringComparison.Ordinal);
                // 保持菜单可点击，由 ShowNetworkSettings 在运行中给出“请先停止服务”提示。
                _networkSettingsMenuItem.Enabled = true;
                _sendButton.Enabled = snapshot.IsRunning;
            });
        }

        /// <summary>
        /// 手机端 control 消息分发入口，处理 Agent 续接与 PC 电源控制。
        /// </summary>
        private Task<object?> HandleControlRequestAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var action = root.TryGetProperty("action", out var actionElement) &&
                         actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.Equals(action, "pc.sleep", StringComparison.OrdinalIgnoreCase) ||
                // 兼容旧版手机客户端：旧动作名称曾错误地只关闭显示器，现统一进入睡眠。
                string.Equals(action, "pc.screenOff", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "pc.screen_off", StringComparison.OrdinalIgnoreCase))
            {
                // 先把“请求已受理”回执发给手机。直接在 WebSocket 处理线程调用
                // SetSuspendState 会暂停该线程，手机可能直到电脑被再次唤醒才收到回执。
                return Task.FromResult<object?>(new DeferredControlResponse(
                    new
                    {
                        type = "control_result",
                        action,
                        succeeded = true,
                        status = "accepted"
                    },
                    () => QueuePcSleep(action)));
            }

            if (string.Equals(action, "pc.shutdown", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<object?>(SchedulePcShutdown(action));
            }

            return Task.FromResult<object?>(new
            {
                type = "control_result",
                action,
                succeeded = false,
                error = "unsupported action"
            });
        }

        // ── Relay bridge ──

        private void StartRelayBridgeIfConfigured()
        {
            var config = _server.Config;
            if (!config.RelayEnabled)
            {
                AppendLog("中继未配置：手机需与电脑处于同一局域网。");
                return;
            }

            if (_relayBridge != null)
            {
                AppendLog("中继桥已启动，跳过重复创建。");
                return;
            }

            try
            {
                _relayBridge = new RemoteRelayBridge(
                    config.RelayUrl,
                    config.RelayRoomId,
                    HandleRelayMessageFromPhoneAsync,
                    AppendLog);
                _relayBridge.ConnectionStateChanged += OnRelayConnectionChanged;
                _relayBridge.Start();
                AppendLog($"中继桥已启动：{config.RelayUrl} 房间 {config.RelayRoomId}");
            }
            catch (Exception ex)
            {
                AppendLog("中继桥启动失败：" + ex.Message);
            }
        }

        private void StopRelayBridge()
        {
            var bridge = _relayBridge;
            if (bridge == null) return;
            _relayBridge = null;
            try
            {
                bridge.Dispose();
                AppendLog("中继桥已停止");
            }
            catch (Exception ex)
            {
                AppendLog("中继桥停止异常：" + ex.Message);
            }
        }

        private void OnRelayConnectionChanged(bool connected)
        {
            AppendLog(connected
                ? "中继桥已连接：手机可通过中继服务器接收通知"
                : "中继桥已断开：正在自动重连");
            // Update snapshot with relay status
            BeginInvoke((Action)(() => OnSnapshotChanged(_server.GetSnapshot())));
        }

        /// <summary>
        /// Fires on every local broadcast. Forward to relay bridge so remote
        /// phones receive the same notification transparently.
        /// </summary>
        private void OnOutboundBroadcast(string json)
        {
            var bridge = _relayBridge;
            if (bridge != null && bridge.IsConnected)
            {
                _ = bridge.SendToPhoneAsync(json);
            }
        }

        /// <summary>
        /// Called by the relay bridge when the phone sends a message through
        /// the relay (e.g. control action, heartbeat).
        /// </summary>
        private async Task HandleRelayMessageFromPhoneAsync(string json, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _server.ProcessExternalClientMessageAsync(json, cancellationToken);
                if (response != null)
                {
                    var bridge = _relayBridge;
                    if (bridge != null && bridge.IsConnected)
                    {
                        var responseJson = JsonSerializer.Serialize(response);
                        await bridge.SendToPhoneAsync(responseJson);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("中继消息处理失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 在睡眠回执已发送后排队执行。短延迟为手机处理 WebSocket 帧预留时间，
        /// 同一时刻的重复点击只会触发一次系统睡眠。
        /// </summary>
        private void QueuePcSleep(string action)
        {
            if (Interlocked.CompareExchange(ref _sleepRequestPending, 1, 0) != 0)
            {
                AppendLog("已忽略重复的手机端睡眠请求：已有睡眠操作正在执行。");
                return;
            }

            AppendLog("手机端睡眠请求已确认，准备进入 S3 睡眠模式。 action=" + action);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SleepAcknowledgementDelayMilliseconds).ConfigureAwait(false);
                    ExecutePcSleep(action);
                }
                catch (Exception error)
                {
                    AppendLog("手机端睡眠指令异常：" + error.Message);
                }
                finally
                {
                    Volatile.Write(ref _sleepRequestPending, 0);
                }
            });
        }

        /// <summary>
        /// 请求 Windows 进入标准 S3 睡眠。使用强制模式绕过后台应用的电源请求，
        /// 同时禁用本次睡眠周期的唤醒事件，避免请求被接收后立刻被设备或计时器唤醒。
        /// </summary>
        private void ExecutePcSleep(string action)
        {
            AppendLog("正在执行手机端睡眠请求。 action=" + action);
            var requested = SetSuspendState(hibernate: false, forceCritical: true, disableWakeEvent: true);
            if (requested)
            {
                AppendLog("Windows 已接受手机端睡眠请求，正在强制进入睡眠模式（已禁止唤醒事件）。");
                return;
            }

            var errorCode = Marshal.GetLastWin32Error();
            var error = errorCode == 0
                ? "Windows rejected the sleep request"
                : new Win32Exception(errorCode).Message;
            AppendLog("手机端睡眠指令失败：" + error);
        }

        /// <summary>
        /// 通过系统目录中的 shutdown.exe 安排关机，并以其退出码作为真实结果。
        /// </summary>
        private object SchedulePcShutdown(string action)
        {
            var shutdownPath = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            if (!File.Exists(shutdownPath))
            {
                const string error = "shutdown.exe was not found in the Windows system directory";
                AppendLog("手机端关机指令失败：" + error);
                return new
                {
                    type = "control_result",
                    action,
                    succeeded = false,
                    error
                };
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = shutdownPath,
                    Arguments = "/s /f /t " + ShutdownDelaySeconds,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    const string error = "shutdown.exe did not start";
                    AppendLog("手机端关机指令失败：" + error);
                    return new
                    {
                        type = "control_result",
                        action,
                        succeeded = false,
                        error
                    };
                }

                if (!process.WaitForExit(3000))
                {
                    const string error = "shutdown.exe did not finish within 3 seconds";
                    AppendLog("手机端关机指令失败：" + error);
                    return new
                    {
                        type = "control_result",
                        action,
                        succeeded = false,
                        error
                    };
                }

                var commandOutput = (process.StandardError.ReadToEnd() + " " + process.StandardOutput.ReadToEnd()).Trim();
                if (process.ExitCode != 0)
                {
                    var error = string.IsNullOrWhiteSpace(commandOutput)
                        ? "shutdown.exe exited with code " + process.ExitCode
                        : commandOutput;
                    AppendLog("手机端关机指令失败：" + error);
                    return new
                    {
                        type = "control_result",
                        action,
                        succeeded = false,
                        error
                    };
                }

                AppendLog("已安排电脑在 " + ShutdownDelaySeconds + " 秒后关机（手机端指令）。");
                return new
                {
                    type = "control_result",
                    action = action,
                    succeeded = true
                };
            }
            catch (Exception error)
            {
                AppendLog("手机端关机指令失败：" + error.Message);
                return new
                {
                    type = "control_result",
                    action,
                    succeeded = false,
                    error = error.Message
                };
            }
        }

        /// <summary>
        /// 切换地址框 IP 脱敏显示。与 APP 端 isHostMasked 行为一致：
        /// 切换 _isAddressMasked 并刷新 _addressBox.Text，按钮文案同步切换。
        /// </summary>
        private void ToggleAddressMask()
        {
            _isAddressMasked = !_isAddressMasked;
            UpdateAddressMaskMenuText();
            _addressBox.Text = _isAddressMasked ? MaskIp(_realAddressText) : _realAddressText;
            SaveUiState();
        }

        private void UpdateAddressMaskMenuText()
        {
            _toggleAddressMenuItem.Text = _isAddressMasked ? "显示 IP" : "隐藏 IP";
        }

        /// <summary>
        /// IP/域名脱敏：保留每段首字符，其余替换为 *。
        /// AddressText 实际格式是 `wss://win.guliacer.dpdns.org` 或 `ws://192.168.5.35:1122`
        /// （带前缀和端口，可能多行），因此分别提取 IPv4 与域名部分脱敏，保留 scheme 前缀
        /// 和 :port 后缀。
        /// - ws://192.168.5.35:1122 -> ws://1**.1**.*.3*:1122
        /// - wss://win.guliacer.dpdns.org -> wss://w**.g********.d****.o**
        /// - 非 IP/域名文本（如"未找到局域网..."提示）原样返回。
        /// </summary>
        private static string MaskIp(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            // 1) 脱敏 IPv4。
            var ipv4 = new System.Text.RegularExpressions.Regex(
                @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b");
            var masked = ipv4.Replace(input, match =>
            {
                var segments = match.Value.Split('.');
                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i].Length > 1)
                    {
                        segments[i] = segments[i][0] + new string('*', segments[i].Length - 1);
                    }
                }
                return string.Join(".", segments);
            });

            // 2) 脱敏域名标签（每段保留首字符）。IPv4 已被替换为含 * 的文本，
            //    不满足域名正则字符集，不会重复匹配。
            var host = new System.Text.RegularExpressions.Regex(
                @"(?<scheme>[a-z][a-z0-9+.\-]*://)?" +
                @"(?<host>(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,})" +
                @"(?<port>:\d+)?");
            return host.Replace(masked, m =>
            {
                var name = m.Groups["host"].Value;
                var maskedName = string.Join(".", name.Split('.').Select(segment =>
                    segment.Length > 0 ? segment[0] + new string('*', segment.Length - 1) : "*"));
                return m.Groups["scheme"].Value + maskedName + m.Groups["port"].Value;
            });
        }

        private void AppendLog(string text)
        {
            var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " " + text + Environment.NewLine;
            _diagnosticLogWriter.Enqueue(line);
            RunOnUiThread(() =>
            {
                var uiLine = DateTime.Now.ToString("HH:mm:ss") + " " + text;
                _uiLogLines.AddFirst(uiLine);
                while (_uiLogLines.Count > MaxUiLogLines)
                {
                    _uiLogLines.RemoveLast();
                }
                _logBox.Text = string.Join(Environment.NewLine, _uiLogLines) + Environment.NewLine;
                ScrollLogToTop();
                _logScrollBar.Recalculate();
            });
        }

        private void ScheduleUiStateSave()
        {
            _uiStateSaveTimer.Stop();
            _uiStateSaveTimer.Start();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private const int EM_LINESCROLL = 0x00B6;
        private const int WM_VSCROLL = 0x0115;
        private const int SB_TOP = 6;

        /// <summary>
        /// 把只读运行日志滚动到第一行。日志按最新在顶部排列，
        /// 直接使用 WM_VSCROLL 不抢焦点也能可靠刷新可视区。
        /// </summary>
        private void ScrollLogToTop()
        {
            if (_logBox.IsDisposed || !_logBox.IsHandleCreated)
            {
                return;
            }

            _logBox.SelectionStart = 0;
            _logBox.SelectionLength = 0;
            SendMessage(_logBox.Handle, WM_VSCROLL, SB_TOP, 0);
        }

        private void RunOnUiThread(Action action)
        {
            // 关闭/销毁阶段句柄可能已失效：BeginInvoke 或 InvokeRequired 会抛
            // InvalidOperationException（08-19 崩溃日志：Invoke or BeginInvoke
            // cannot be called on a control until the window handle has been created）。
            // 此时直接丢弃 UI 更新即可，不能让异常逃逸到线程池/async 续体。
            try
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    return;
                }

                if (InvokeRequired)
                {
                    BeginInvoke(action);
                    return;
                }

                action();
            }
            catch (ObjectDisposedException)
            {
                // 控件已释放，同样丢弃。
            }
            catch (InvalidOperationException)
            {
                // 窗体正在关闭，UI 更新被丢弃是预期行为。
            }
        }

        private static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", size, style);
        }

        private static Color Blend(Color first, Color second, float secondWeight)
        {
            var weight = Math.Max(0f, Math.Min(1f, secondWeight));
            return Color.FromArgb(
                (int)Math.Round(first.R * (1f - weight) + second.R * weight),
                (int)Math.Round(first.G * (1f - weight) + second.G * weight),
                (int)Math.Round(first.B * (1f - weight) + second.B * weight));
        }

        private struct ThemeColors
        {
            public Color Background;
            public Color Surface;
            public Color SurfaceAlt;
            public Color InputSurface;
            public Color TextPrimary;
            public Color TextSecondary;
            public Color Success;
            public Color Danger;
            public Color Accent;
            public Color AccentHover;
            public Color Border;
            public Color AppsAccent;
        }

        private static ThemeColors LightTheme => new ThemeColors
        {
            Background = Color.FromArgb(238, 243, 241),
            Surface = Color.FromArgb(255, 255, 255),
            SurfaceAlt = Color.FromArgb(240, 246, 243),
            InputSurface = Color.FromArgb(248, 251, 250),
            TextPrimary = Color.FromArgb(25, 48, 42),
            TextSecondary = Color.FromArgb(101, 118, 111),
            Success = Color.FromArgb(39, 139, 91),
            Danger = Color.FromArgb(190, 76, 73),
            Accent = Color.FromArgb(111, 134, 126),
            AccentHover = Color.FromArgb(94, 118, 109),
            Border = Color.FromArgb(198, 214, 206),
            AppsAccent = Color.FromArgb(66, 129, 122)
        };

        private static ThemeColors DarkTheme => new ThemeColors
        {
            Background = Color.FromArgb(14, 22, 20),
            Surface = Color.FromArgb(23, 36, 32),
            SurfaceAlt = Color.FromArgb(31, 49, 43),
            InputSurface = Color.FromArgb(18, 29, 26),
            TextPrimary = Color.FromArgb(236, 246, 241),
            TextSecondary = Color.FromArgb(165, 187, 177),
            Success = Color.FromArgb(82, 193, 126),
            Danger = Color.FromArgb(235, 113, 105),
            Accent = Color.FromArgb(142, 174, 161),
            AccentHover = Color.FromArgb(165, 192, 180),
            Border = Color.FromArgb(53, 76, 67),
            AppsAccent = Color.FromArgb(88, 166, 154)
        };

        private void ShowPersonalProjects()
        {
            using (var dialog = new PersonalProjectsForm(_isDarkMode))
            {
                dialog.ShowDialog(this);
            }
        }

        // 仅供 WM_APP_OPEN_PROJECTS 测试消息使用。主窗体驻留托盘时不具备可见
        // 的 owner，ShowDialog(this) 会让项目窗口无法被窗口枚举器可靠探测；测试
        // 窗口改为无 owner 的非模态窗体，正常书本按钮仍保持模态交互不变。
        private void ShowPersonalProjectsForTest()
        {
            if (_testProjectsForm != null && !_testProjectsForm.IsDisposed)
            {
                _testProjectsForm.Activate();
                return;
            }

            var dialog = new PersonalProjectsForm(_isDarkMode);
            _testProjectsForm = dialog;
            dialog.FormClosed += (_, __) =>
            {
                if (ReferenceEquals(_testProjectsForm, dialog))
                {
                    _testProjectsForm = null;
                }
                dialog.Dispose();
            };
            dialog.Show();
        }

        // 测试专用入口：verify-pc-layout.ps1 通过该自定义消息直接打开个人项目窗口。
        // 主窗体隐藏（托盘）时，从进程外模拟点击书本按钮会被 WinForms 丢弃
        // （ShowWindow 不同步 Visible 标志），因此暴露 WM_APP+1 作为确定性入口；
        // 该消息不会被任何正常 UI 操作产生，不影响产品行为。
        private const int WM_APP_OPEN_PROJECTS = 0x8000 + 1;
        private const int WM_APP_STOP_SERVER = 0x8000 + 2;
        private const int WM_APP_START_SERVER = 0x8000 + 3;
        private const int WM_APP_TRIGGER_AUTODETECT = 0x8000 + 4;
        private const int WM_APP_OPEN_QR = 0x8000 + 5;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_OPEN_PROJECTS)
            {
                ShowPersonalProjectsForTest();
                return;
            }
            if (m.Msg == WM_APP_OPEN_QR)
            {
                ShowConnectionQr();
                return;
            }
            if (m.Msg == WM_APP_STOP_SERVER)
            {
                _ = StopServerAsync();
                return;
            }
            if (m.Msg == WM_APP_START_SERVER)
            {
                _ = StartServerAsync();
                return;
            }
            if (m.Msg == WM_APP_TRIGGER_AUTODETECT)
            {
                // E2E 专用：通知面板重跑自动检测。stub 进程位于可信安装根下时，
                // 重渲染会按 IsRunningFromTrustedRoot 启用对应来源；stub 退出后
                // 再次触发即可恢复“未安装”状态，不留测试残留。
                _ = RunNotificationAutoDetectAsync();
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// 通知应用启用集合变化时的回调。当前实现仅记录日志，
        /// 后续可扩展为动态调整 AgentCompletionMonitor 的监听目录。
        /// </summary>
        private void OnEnabledAppsChanged(IReadOnlyList<string> enabledKeys)
        {
            var list = enabledKeys.Count == 0
                ? "（无）"
                : string.Join("、", enabledKeys);
            AppendLog("已启用通知应用：" + list);
            _server.SetEnabledSources(enabledKeys);
            _agentMonitor.RefreshConfiguredRoots();
        }

        /// <summary>
        /// 通知门控在运行期自动启用新安装来源后的回调：重跑面板自动检测，
        /// 让启用集与"已安装"分区同步显示新装应用。可能来自 WS/监控线程，
        /// 统一切回 UI 线程执行。
        /// </summary>
        private void OnSourceAutoEnabled(string sourceKey)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnSourceAutoEnabled(sourceKey)));
                return;
            }
            _ = RefreshNotificationAppsAsync(sourceKey);
        }

        private async Task RefreshNotificationAppsAsync(string sourceKey)
        {
            try
            {
                await _notificationAppsPanel.RunAutoDetectAsync();
            }
            catch (Exception error)
            {
                AppendLog("自动启用 [" + sourceKey + "] 后刷新应用面板失败：" + error.Message);
            }
        }

        private void OnAppsChanged(IReadOnlyList<RegisteredAppDefinition> apps)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnAppsChanged(apps)));
                return;
            }

            _notificationAppsPanel.AddRegisteredApps(apps);
            AppendLog("已更新动态通知应用：" + apps.Count + " 个");
        }

        // 雷神加速器关闭提醒的内置规则。原先由 GitHub 远程适配目录下发，
        // 该功能已下线，这里改为本地固定规则，行为与之前一致。
        private static IReadOnlyList<RemoteAdapterRule> BuildBuiltInLifecycleRules()
        {
            return new[]
            {
                new RemoteAdapterRule
                {
                    AppKey = "leigod",
                    DisplayName = "雷神加速器",
                    BrandColor = "#FF7A00",
                    CloseReminderEnabled = true,
                    ImageNames = new List<string> { "leigod.exe" },
                    InstallRoots = new List<string> { @"%PROGRAMFILES(X86)%\LeiGod_Acc" },
                    LifecycleMode = RemoteAdapterRule.LeigodClientLifecycleMode,
                    NotifyOnSessionEnding = true,
                    CloseReminderTitle = "雷神加速器关闭提醒",
                    CloseReminderMessage = "您已关闭雷神加速器客户端，请前往雷神小程序确认是否已关闭加速服务，避免闲置消耗时长",
                }
            };
        }

        private void ToggleTheme()
        {
            _isDarkMode = !_isDarkMode;
            SaveUiState();
            ApplyTheme();
            ApplyTitleBarTheme(Handle, _isDarkMode);
            OnSnapshotChanged(_server.GetSnapshot());
            // 主题切换后强制重绘非客户区，避免 caption 残留系统浅色。
            if (IsHandleCreated)
            {
                Invalidate(true);
                Update();
            }
        }

        private static bool IsMuted(Label label)
        {
            return label.Tag is string role && role == "muted";
        }

        /// <summary>
        /// 该文字是否位于某张卡片内部。用于决定 Label 的背景色取卡片色还是页面底色，
        /// 只有页头的标题与副标题位于卡片之外。
        /// </summary>
        private bool IsOnCard(Label label)
        {
            return label != _brandLabel && label != _titleLabel && label != _subtitleLabel;
        }

        private void ApplyTheme()
        {
            var colors = _isDarkMode ? DarkTheme : LightTheme;
            BackColor = colors.Background;

            var iconColor = colors.Accent;
            _serviceCard.BackColor = colors.Surface;
            _serviceCard.BorderColor = colors.Border;
            _serviceCard.AccentColor = colors.Success;
            _serviceCard.ShowAccent = true;
            _notificationCard.BackColor = colors.Surface;
            _notificationCard.BorderColor = colors.Border;
            _notificationCard.AccentColor = colors.AppsAccent;
            _notificationCard.ShowAccent = true;
            _logCard.BackColor = colors.Surface;
            _logCard.BorderColor = Blend(colors.Border, colors.Surface, 0.55f);
            _logCard.ShowAccent = false;
            _serviceCard.Invalidate();
            _notificationCard.Invalidate();
            _logCard.Invalidate();

            foreach (Control? control in Controls)
            {
                if (control == null) continue;
                switch (control)
                {
                    case Label label:
                        label.ForeColor = IsMuted(label)
                            ? colors.TextSecondary
                            : colors.TextPrimary;
                        // 卡片内的文字必须以卡片底色作背景。Label 是窗体的直接子控件，
                        // 默认继承窗体 Background，在卡片上会画出一块与卡片不同色的矩形，
                        // 深色主题下尤其明显。
                        label.BackColor = IsOnCard(label) ? colors.Surface : colors.Background;
                        break;
                    case TextBox textBox:
                        textBox.BackColor = colors.InputSurface;
                        textBox.ForeColor = colors.TextPrimary;
                        break;
                    case VisualCardPanel card:
                        break;
                    case ThemeIconButton themeButton:
                        themeButton.ApplyColors(colors.Background, iconColor);
                        break;
                    case BookIconButton personalProjectsButton:
                        personalProjectsButton.ApplyColors(colors.Background, iconColor);
                        break;
                    case MoreIconButton serviceMoreButton:
                        serviceMoreButton.ApplyColors(colors.Surface, colors.TextSecondary, colors.SurfaceAlt);
                        break;
                    case Button button:
                        if (button == _startButton || button == _sendButton || button == _qrButton)
                        {
                            ApplyButtonStyle(button, UiButtonStyler.UiButtonStyle.Primary);
                        }
                        else if (button == _stopButton)
                        {
                            ApplyButtonStyle(button, UiButtonStyler.UiButtonStyle.Danger);
                        }
                        else
                        {
                            ApplyButtonStyle(button, UiButtonStyler.UiButtonStyle.Secondary);
                        }
                        break;
                }
            }

            _themeButton.IsDarkMode = _isDarkMode;
            _themeButton.Invalidate();
            _serviceMoreMenu.BackColor = colors.Surface;
            _serviceMoreMenu.ForeColor = colors.TextPrimary;
            _serviceMoreMenu.Renderer = new ThemedMenuRenderer(colors.Surface, colors.SurfaceAlt, colors.Border);
            // 滚动条以 muted 语义色绘制低对比轨道和胶囊滑块；不用强调色，
            // 避免在表单中看起来像一根彩色进度条。
            var scrollThumb = colors.TextSecondary;
            var scrollTrack = colors.TextSecondary;
            _messageScrollBar.BackColor = colors.Surface;
            _messageScrollBar.ThumbColor = scrollThumb;
            _messageScrollBar.TrackColor = scrollTrack;
            _messageScrollBar.Recalculate();
            _logScrollBar.BackColor = colors.Surface;
            _logScrollBar.ThumbColor = scrollThumb;
            _logScrollBar.TrackColor = scrollTrack;
            _logScrollBar.Recalculate();
            // 面板沿用卡片内容底色，内部已安装/未安装分区再用轻量底色和强调线分组。
            _notificationAppsPanel.ApplyTheme(
                colors.Surface,
                colors.SurfaceAlt,
                colors.TextPrimary,
                colors.TextSecondary,
                colors.Border,
                iconColor,
                colors.AccentHover);
            if (IsHandleCreated)
            {
                ApplyTitleBarTheme(Handle, _isDarkMode);
            }
        }

        private static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TapRelay", "settings.json");

        private static bool LoadTheme()
        {
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return json.Contains("\"darkMode\":true") || json.Contains("\"darkMode\": true");
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 全应用统一按钮样式入口（见 UiButtonStyler）。
        /// 从当前主题色映射出统一配色后委托给共享样式器。
        /// </summary>
        private void ApplyButtonStyle(Button button, UiButtonStyler.UiButtonStyle style)
        {
            var c = _isDarkMode ? DarkTheme : LightTheme;
            UiButtonStyler.Apply(button, style, new UiButtonStyler.UiButtonColors
            {
                Accent = c.Accent,
                AccentHover = c.AccentHover,
                Danger = c.Danger,
                Surface = c.Surface,
                Border = c.Border,
                TextPrimary = c.TextPrimary,
                SurfaceAlt = c.SurfaceAlt
            });
        }

        /// <summary>
        /// Theme toggle icon. PC and Android share PixGo's 24 x 24
        /// LightMode / DarkMode paths, 1.5 stroke, round caps, and round joins.
        /// </summary>
        private sealed class VisualCardPanel : Panel
        {
            private int _headerHeight;
            private int[] _dividerOffsets = Array.Empty<int>();

            public Color BorderColor { get; set; } = Color.FromArgb(198, 214, 206);
            public Color AccentColor { get; set; } = Color.FromArgb(111, 134, 126);
            public bool ShowAccent { get; set; } = true;
            public int CornerRadius { get; set; } = 16;

            public VisualCardPanel()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                TabStop = false;
            }

            public void SetSectionLayout(int headerHeight, params int[] dividerOffsets)
            {
                _headerHeight = Math.Max(0, headerHeight);
                _dividerOffsets = dividerOffsets ?? Array.Empty<int>();
                Invalidate();
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                using (var path = RoundedPath(ClientRectangle, CornerRadius))
                {
                    Region = new Region(path);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

                var visibleHeaderHeight = Math.Min(_headerHeight, Math.Max(0, Height - 1));
                if (ShowAccent && visibleHeaderHeight > 0)
                {
                    using (var accentBrush = new SolidBrush(AccentColor))
                    {
                        // 仅在标题附近保留一小段左侧色线，避免完整标题色带让
                        // 三张卡片像互不相关的设置入口。
                        var accentTop = Math.Min(12, Math.Max(2, visibleHeaderHeight - 4));
                        var accentBottom = Math.Max(accentTop + 1, visibleHeaderHeight - 8);
                        e.Graphics.FillRectangle(
                            accentBrush,
                            new Rectangle(0, accentTop, 4, Math.Max(1, accentBottom - accentTop)));
                    }
                }

                using (var dividerPen = new Pen(BorderColor, 1f))
                {
                    foreach (var offset in _dividerOffsets)
                    {
                        if (offset <= visibleHeaderHeight || offset >= Height - 1)
                        {
                            continue;
                        }

                        e.Graphics.DrawLine(dividerPen, CardPad, offset, Math.Max(CardPad, Width - CardPad), offset);
                    }
                }

                using (var pen = new Pen(BorderColor, 1f))
                using (var path = RoundedPath(rect, CornerRadius))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }

            private static GraphicsPath RoundedPath(Rectangle rect, int radius)
            {
                var path = new GraphicsPath();
                // 宽/高为 0 时 d=0 会让 AddArc 抛 ArgumentException（与 LogScrollBar 同源），保底为 1。
                var d = Math.Max(1, Math.Min(radius * 2, Math.Min(Math.Max(1, rect.Width), Math.Max(1, rect.Height))));
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class MoreIconButton : Control
        {
            private Color _backgroundColor = Color.White;
            private Color _iconColor = Color.FromArgb(101, 118, 111);
            private Color _hoverColor = Color.FromArgb(240, 246, 243);
            private bool _hovered;

            public MoreIconButton()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = "更多服务操作";
                TabStop = true;
            }

            public void ApplyColors(Color backgroundColor, Color iconColor, Color hoverColor)
            {
                _backgroundColor = backgroundColor;
                _iconColor = iconColor;
                _hoverColor = hoverColor;
                BackColor = backgroundColor;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hovered = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(_hovered ? _hoverColor : _backgroundColor);
                using (var brush = new SolidBrush(_iconColor))
                {
                    var centerX = Width / 2;
                    var centerY = Height / 2;
                    for (var i = -1; i <= 1; i++)
                    {
                        e.Graphics.FillEllipse(brush, centerX + i * 6 - 2, centerY - 2, 4, 4);
                    }
                }
            }
        }

        private sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _surface;
            private readonly Color _hover;
            private readonly Color _border;

            public ThemedMenuRenderer(Color surface, Color hover, Color border)
            {
                _surface = surface;
                _hover = hover;
                _border = border;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(_surface);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                using (var brush = new SolidBrush(e.Item.Selected ? _hover : _surface))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(_border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, e.ToolStrip.Width - 1), Math.Max(0, e.ToolStrip.Height - 1));
                }
            }
        }

        private class ThemeIconButton : Control
        {
            public bool IsDarkMode { get; set; }

            private Color _backgroundColor = Color.FromArgb(248, 250, 252);
            private Color _iconColor = Color.FromArgb(111, 134, 126);

            public ThemeIconButton()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                BackColor = _backgroundColor;
                ForeColor = _iconColor;
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = "Theme";
                TabStop = false;
            }

            public void ApplyColors(Color backgroundColor, Color iconColor)
            {
                _backgroundColor = backgroundColor;
                _iconColor = iconColor;
                BackColor = backgroundColor;
                ForeColor = iconColor;
                Region = null;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // A WinForms Button paints its own themed surface before OnPaint. Clear it explicitly
                // so this matches Compose's background-free Icon instead of becoming a dark capsule.
                e.Graphics.Clear(BackColor);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(_backgroundColor);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                var viewportSize = Math.Min(Width, Height) * (20f / 28f);
                var viewport = new RectangleF(
                    (Width - viewportSize) / 2f,
                    (Height - viewportSize) / 2f,
                    viewportSize,
                    viewportSize);

                using (var pen = new Pen(_iconColor, 1.5f * viewportSize / 24f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                    if (IsDarkMode)
                    {
                        DrawDarkModeIcon(g, pen, viewport);
                    }
                    else
                    {
                        DrawLightModeIcon(g, pen, viewport);
                    }
                }
            }

            private static void DrawLightModeIcon(Graphics g, Pen pen, RectangleF viewport)
            {
                PointF P(float x, float y) => new PointF(
                    viewport.Left + x * viewport.Width / 24f,
                    viewport.Top + y * viewport.Height / 24f);

                g.DrawLine(pen, P(12f, 3f), P(12f, 5f));
                g.DrawLine(pen, P(12f, 19f), P(12f, 21f));
                g.DrawLine(pen, P(4.2f, 4.2f), P(5.6f, 5.6f));
                g.DrawLine(pen, P(18.4f, 18.4f), P(19.8f, 19.8f));
                g.DrawLine(pen, P(3f, 12f), P(5f, 12f));
                g.DrawLine(pen, P(19f, 12f), P(21f, 12f));
                g.DrawLine(pen, P(4.2f, 19.8f), P(5.6f, 18.4f));
                g.DrawLine(pen, P(18.4f, 5.6f), P(19.8f, 4.2f));

                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.StartFigure();
                    path.AddBezier(P(12f, 16f), P(14.2f, 16f), P(16f, 14.2f), P(16f, 12f));
                    path.AddBezier(P(16f, 12f), P(16f, 9.8f), P(14.2f, 8f), P(12f, 8f));
                    path.AddBezier(P(12f, 8f), P(9.8f, 8f), P(8f, 9.8f), P(8f, 12f));
                    path.AddBezier(P(8f, 12f), P(8f, 14.2f), P(9.8f, 16f), P(12f, 16f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
            }

            private static void DrawDarkModeIcon(Graphics g, Pen pen, RectangleF viewport)
            {
                PointF P(float x, float y) => new PointF(
                    viewport.Left + x * viewport.Width / 24f,
                    viewport.Top + y * viewport.Height / 24f);

                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.StartFigure();
                    path.AddBezier(P(18.5f, 15.5f), P(17.1f, 16.5f), P(15.5f, 17f), P(13.7f, 17f));
                    path.AddBezier(P(13.7f, 17f), P(9.8f, 17f), P(6.7f, 13.9f), P(6.7f, 10f));
                    path.AddBezier(P(6.7f, 10f), P(6.7f, 8.2f), P(7.3f, 6.5f), P(8.3f, 5.2f));
                    path.AddBezier(P(8.3f, 5.2f), P(5.2f, 6f), P(3f, 8.8f), P(3f, 12f));
                    path.AddBezier(P(3f, 12f), P(3f, 17f), P(7f, 21f), P(12f, 21f));
                    path.AddBezier(P(12f, 21f), P(15.2f, 21f), P(18.1f, 19.3f), P(19.7f, 16.7f));
                    path.AddBezier(P(19.7f, 16.7f), P(19.3f, 16.4f), P(18.9f, 16f), P(18.5f, 15.5f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>
        /// 文本垂直居中的 TextBox。EM_SETRECT 仅对多行框生效，因此强制 Multiline。
        /// 通过设置上下留白相等的格式化矩形，让文本绘制在控件垂直中央。
        /// 单行 IP 框（只读）和多行测试框共用此实现。
        /// </summary>
        private class CenteredTextBox : TextBox
        {
            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, ref RECT lParam);

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int Left, Top, Right, Bottom; }

            private const int EM_SETRECT = 0x00B3;

            public CenteredTextBox()
            {
                Multiline = true;
            }

            private void UpdateRect()
            {
                if (!IsHandleCreated) return;
                if (ReadOnly)
                {
                    // 只读单行框（IP 地址）垂直居中显示。
                    var fontHeight = FontHeight;
                    var verticalPadding = Math.Max(0, (Height - fontHeight - 4) / 2);
                    var rect = new RECT
                    {
                        Left = 2,
                        Top = verticalPadding,
                        Right = Width - 2,
                        Bottom = Height - verticalPadding
                    };
                    SendMessage(Handle, EM_SETRECT, 0, ref rect);
                }
                else
                {
                    // 可编辑多行框（测试消息）：顶对齐并占满整高，
                    // 保证多行内容都能看到，超出部分通过自绘滚动条滚动查看。
                    var rect = new RECT
                    {
                        Left = 2,
                        Top = 4,
                        Right = Width - 2,
                        Bottom = Height - 4
                    };
                    SendMessage(Handle, EM_SETRECT, 0, ref rect);
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                UpdateRect();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                UpdateRect();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                UpdateRect();
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                UpdateRect();
            }
        }

        /// <summary>
        /// 日志框自定义滚动条：与 ThemedScrollBar 共用 ScrollBarRenderer 的
        /// "细分轨道 + 胶囊滑块 + 悬停/拖动反馈"视觉。隐藏 WinForms 原生
        /// 滚动条，用自绘控件替代以实现统一的自绘效果。
        /// </summary>
        private class LogScrollBar : Control
        {
            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
            private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
            private const int EM_LINESCROLL = 0x00B6;
            private const int EM_GETLINECOUNT = 0x00BA;

            private readonly TextBox _target;
            private int _lineHeight;
            private int _thumbTop;
            private int _thumbHeight;
            private bool _dragging;
            private int _dragStartY;
            private int _dragStartFirstLine;

            // 与通知应用分区共用同一套轨道/滑块视觉，避免日志卡里出现
            // 一根孤零零的彩色进度条。默认取浅色 muted 语义色，主题应用后覆盖。
            public Color ThumbColor { get; set; } = Color.FromArgb(101, 118, 111);
            public Color TrackColor { get; set; } = Color.FromArgb(101, 118, 111);
            private bool _hovered;

            public LogScrollBar(TextBox target)
            {
                _target = target;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                Width = ScrollBarRenderer.PreferredWidth;
                Cursor = Cursors.Default;
                // WinForms 透明子控件会在独立双缓冲表面上把半透明笔刷合成到黑底。
                // 主题应用时会覆盖为所在卡片的真实底色。
                BackColor = Color.White;
                // 默认不显示：只有内容超出可视区域时才显示滑块。
                Visible = false;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // 先清为卡片底色，半透明轨道和滑块才不会出现黑色合成伪影。
                e.Graphics.Clear(BackColor);
            }

            public void Recalculate()
            {
                if (_target.IsDisposed || _target.Font == null) return;
                using (var g = _target.CreateGraphics())
                {
                    _lineHeight = TextRenderer.MeasureText(g, "Mg", _target.Font).Height;
                }
                var firstLine = FirstVisibleLine();
                var totalLines = TotalLineCount();
                var visibleLines = VisibleLineCount();

                var hasOverflow = totalLines > visibleLines;
                int newThumbTop = 0;
                int newThumbHeight;
                if (!hasOverflow)
                {
                    newThumbHeight = 0;
                }
                else
                {
                    var trackTop = ScrollBarRenderer.TrackVerticalInset;
                    var trackHeight = ScrollBarRenderer.TrackHeightFor(Height);
                    newThumbHeight = Math.Max(ScrollBarRenderer.MinThumbHeight, trackHeight * visibleLines / totalLines);
                    newThumbHeight = Math.Min(newThumbHeight, trackHeight);
                    var scrollable = totalLines - visibleLines;
                    var travel = Math.Max(1, trackHeight - newThumbHeight);
                    newThumbTop = trackTop + (scrollable > 0
                        ? travel * firstLine / scrollable
                        : 0);
                }

                // 仅在实际状态变化时更新可见性与重绘。Application.Idle 会高频调用
                // Recalculate，若无条件 Invalidate 会持续产生 WM_PAINT 消息，
                // 形成 UI 线程 100% CPU 忙循环。
                var changed = _thumbTop != newThumbTop || _thumbHeight != newThumbHeight || Visible != hasOverflow;
                _thumbTop = newThumbTop;
                _thumbHeight = newThumbHeight;
                if (Visible != hasOverflow) Visible = hasOverflow;
                if (changed) Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_thumbHeight <= 0) return;

                var feedback = _dragging
                    ? ScrollBarRenderer.Feedback.Dragging
                    : _hovered ? ScrollBarRenderer.Feedback.Hovered : ScrollBarRenderer.Feedback.Normal;
                ScrollBarRenderer.Render(
                    e.Graphics,
                    Width,
                    Height,
                    _thumbTop,
                    _thumbHeight,
                    ThumbColor,
                    TrackColor,
                    feedback);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hovered = true;
                UpdateCursor(PointToClient(Control.MousePosition).Y);
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (!_dragging)
                {
                    _hovered = false;
                    Cursor = Cursors.Default;
                    Invalidate();
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (_thumbHeight <= 0 || e.Button != MouseButtons.Left)
                {
                    return;
                }

                if (IsOverThumb(e.Y))
                {
                    _dragging = true;
                    _dragStartY = e.Y;
                    _dragStartFirstLine = FirstVisibleLine();
                    Capture = true;
                    Cursor = Cursors.SizeNS;
                    Invalidate();
                    return;
                }

                // 点击轨道按一页文本滚动，与其他自绘滚动条保持一致。
                ScrollByLines(e.Y < _thumbTop ? -VisibleLineCount() : VisibleLineCount());
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging)
                {
                    UpdateCursor(e.Y);
                    return;
                }

                var totalLines = TotalLineCount();
                var visibleLines = VisibleLineCount();
                if (totalLines <= visibleLines)
                {
                    return;
                }

                var scrollable = totalLines - visibleLines;
                var travel = ScrollBarRenderer.TrackHeightFor(Height) - _thumbHeight;
                if (travel <= 0)
                {
                    return;
                }

                var delta = e.Y - _dragStartY;
                var lineDelta = (int)Math.Round(delta * (double)scrollable / travel);
                var newFirstLine = Math.Max(0, Math.Min(scrollable, _dragStartFirstLine + lineDelta));
                ScrollToFirstLine(newFirstLine);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (_dragging && e.Button == MouseButtons.Left)
                {
                    EndDrag();
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ScrollByMouseWheel(e.Delta);
            }

            /// <summary>
            /// 将文本框收到的滚轮事件转发到自绘滚动条，避免仅重算滑块而不移动正文。
            /// </summary>
            public void ScrollByMouseWheel(int delta)
            {
                if (_thumbHeight <= 0 || delta == 0)
                {
                    return;
                }

                var configuredLines = SystemInformation.MouseWheelScrollLines;
                if (configuredLines == 0)
                {
                    return;
                }

                var linesPerStep = configuredLines < 0
                    ? VisibleLineCount()
                    : Math.Max(1, configuredLines);
                var steps = Math.Max(1, Math.Abs(delta) / 120);
                var direction = delta > 0 ? -1 : 1;
                ScrollByLines(direction * linesPerStep * steps);
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (_dragging && !Capture)
                {
                    EndDrag();
                }
            }

            private int FirstVisibleLine()
            {
                return _target.IsHandleCreated
                    ? Math.Max(0, SendMessage(_target.Handle, EM_GETFIRSTVISIBLELINE, 0, 0))
                    : 0;
            }

            private int TotalLineCount()
            {
                var nativeLineCount = _target.IsHandleCreated
                    ? SendMessage(_target.Handle, EM_GETLINECOUNT, 0, 0)
                    : 0;
                return Math.Max(_target.Lines.Length, nativeLineCount);
            }

            private int VisibleLineCount()
            {
                return Math.Max(1, _lineHeight > 0 ? _target.Height / _lineHeight : 1);
            }

            private bool IsOverThumb(int y)
            {
                return _thumbHeight > 0 && y >= _thumbTop && y < _thumbTop + _thumbHeight;
            }

            private void UpdateCursor(int y)
            {
                Cursor = IsOverThumb(y) ? Cursors.SizeNS : Cursors.Default;
            }

            private void ScrollToFirstLine(int firstLine)
            {
                ScrollByLines(firstLine - FirstVisibleLine());
            }

            private void ScrollByLines(int lineDelta)
            {
                if (lineDelta == 0 || _target.IsDisposed || !_target.IsHandleCreated)
                {
                    return;
                }

                SendMessage(_target.Handle, EM_LINESCROLL, 0, lineDelta);
                Recalculate();
            }

            private void EndDrag()
            {
                if (!_dragging)
                {
                    return;
                }

                _dragging = false;
                Capture = false;
                UpdateCursor(PointToClient(Control.MousePosition).Y);
                Invalidate();
            }
        }
    }
}
