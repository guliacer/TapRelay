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
        private bool _updateCheckStarted;
        private readonly AgentCompletionMonitor _agentMonitor;
        private readonly ProcessLifecycleMonitor _processLifecycleMonitor;
        private readonly ClaudeHahaPermissionBridge _hahaPermissionBridge;
        private readonly CenteredTextBox _addressBox = new CenteredTextBox();
        private readonly Label _addressMarqueeLabel = new Label();
        // 方案 B-4：地址行保留一个轻量复制入口，避免把「复制地址」藏进更多菜单。
        private readonly StripIconButton _copyAddressButton =
            new StripIconButton(StripIconButton.Glyph.Copy, "复制完整连接地址");
        // 脱敏只改变地址行的显示文本，不改变 _realAddressText，
        // 因此复制、二维码和手机连接仍使用完整地址。
        private readonly StripIconButton _maskAddressButton =
            new StripIconButton(StripIconButton.Glyph.Mask, "脱敏显示连接地址");
        private bool _addressMasked;
        private readonly CenteredTextBox _messageBox = new CenteredTextBox();
        private readonly Label _messageMarqueeLabel = new Label();
        private readonly TextBox _logBox = new TextBox();
        private readonly Button _startButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _sendButton = new Button();
        private readonly Button _autoDetectAppsButton = new Button();
        private readonly Button _selectAppButton = new Button();
        private readonly ContextMenuStrip _serviceMoreMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _networkSettingsMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _autoStartMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _personalProjectsMenuItem = new ToolStripMenuItem();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly NotifyIcon _trayIcon = new NotifyIcon();
        private TrayToastForm? _trayToast;
        private PersonalProjectsForm? _testProjectsForm;
        // 方案 B-3：顶部状态条。原先分散在三处的图标按钮（服务卡里的文字「二维码」、
        // 页头的书本与主题、服务卡里的更多）全部收拢到条内右侧，统一 28×28 自绘图标。
        private readonly StatusStripPanel _stripCard = new StatusStripPanel();
        private readonly StripIconButton _iconQr =
            new StripIconButton(StripIconButton.Glyph.Qr, "二维码", "二维码");
        private readonly StripIconButton _iconProjects =
            new StripIconButton(StripIconButton.Glyph.Book, "个人项目", "个人项目");
        private readonly StripIconButton _iconTheme =
            new StripIconButton(StripIconButton.Glyph.Theme, "切换深浅色主题", "主题");
        // 「更多」是低频菜单入口，保持纯图标方钮，由一条竖线与上面两个高频入口分开。
        private readonly StripIconButton _iconMore =
            new StripIconButton(StripIconButton.Glyph.More, "更多服务操作");
        private readonly LogScrollBar _messageScrollBar;
        private readonly LogScrollBar _logScrollBar;
        private readonly LinkedList<string> _uiLogLines = new LinkedList<string>();
        private readonly System.Windows.Forms.Timer _uiStateSaveTimer =
            new System.Windows.Forms.Timer { Interval = 400 };
        // 地址、测试消息和日志摘要都可能比其可视宽度长；定时横向巡航让完整内容
        // 在不增加首屏高度的前提下可读，获得焦点时自动暂停，避免干扰用户编辑。
        private readonly System.Windows.Forms.Timer _marqueeTimer =
            new System.Windows.Forms.Timer { Interval = 300 };
        private int _addressMarqueeOffset;
        private int _messageMarqueeOffset;
        private int _logPreviewMarqueeOffset;
        private int _logPreviewMarqueePauseTicks;
        private readonly NotificationAppsPanel _notificationAppsPanel = new NotificationAppsPanel();
        private readonly VisualCardPanel _serviceCard = new VisualCardPanel();
        private readonly VisualCardPanel _notificationCard = new VisualCardPanel();
        private readonly VisualCardPanel _logCard = new VisualCardPanel();
        private readonly ToolStripMenuItem _compactModeMenuItem = new ToolStripMenuItem();
        private bool _isDarkMode;
        private bool _compactMode;
        private bool _exitRequested;
        // 退出看门狗只允许启动一次：三条退出路径可能相互嵌套
        // （托盘退出 → Close() → OnFormClosing），重复启动只会多出无用的线程。
        private int _exitWatchdogArmed;
        // 看门狗等待上限。它的作用不是"加速正常退出"，而是保证异常时进程也一定会死。
        // 取值必须覆盖最坏清理链路：托盘退出等 1600ms + 二次 StopAsync（此时 _host 已为
        // null，立即返回）≈ 1.7s，留出余量取 2500ms。正常退出实测约 0.7~1.2s，用不到它。
        private const int ExitWatchdogMilliseconds = 2500;
        // 看门狗落盘最后几行日志的等待上限，计入总时长后仍远小于用户的感知阈值。
        private const int ExitWatchdogFlushMilliseconds = 500;
        // 托盘退出里后台清理的等待上限。
        // 必须 >= StopAsync 的自身上限（1000ms 优雅停止 + 400ms 资源释放），否则
        // 等待超时后 Close() 会走进 OnFormClosing 再调一次 StopAsync —— 那时 _host
        // 还没被置空，于是又完整等一轮，总耗时翻倍并撞上看门狗。
        private const int ExitTrayStopWaitMilliseconds = 1600;
        // 布局控件就绪前（构造函数早期 OnResize 被触发时）RecalculateLayout 会访问到
        // 尚未赋值的 _messageScrollBar 等字段，用该标志避免在 BuildLayout 完成前重排。
        private bool _layoutReady;
        // RecalculateLayout 重入保护，见该方法注释。
        private bool _relayouting;
        // 布局过程按实测控件高度算出的窗口最小高度，由 ApplyPendingMinimumHeight
        // 在重入保护释放后应用。见 LayoutNotificationAndLogCards 末尾的说明。
        private int _requiredMinHeight;
        // 展开运行日志前后的窗口高度。收起时要据此把窗口还原回去，见 ToggleLogExpanded。
        private int _heightBeforeLogExpand;
        private int _heightAfterLogExpand;
        private readonly Label _serviceCardLabel = new Label();
        private readonly Label _addressLabel = new Label();
        private readonly Label _addressHintLabel = new Label();
        private readonly Label _messageLabel = new Label();
        private readonly Label _notificationAppsLabel = new Label();
        private readonly Label _logLabel = new Label();
        private readonly Label _logHintLabel = new Label();
        // 方案 B：运行记录卡默认折叠成一行摘要（级别点 + 最新一行 + 条数 + chevron），
        // 展开后才显示完整日志框。首屏由此省下约 198px 交给通知应用网格。
        private readonly Label _logPreviewLabel = new Label();
        private readonly Label _logEmptyLabel = new Label();
        private readonly StatusDot _logDot = new StatusDot("运行记录级别指示");
        private bool _logExpanded;
        // 地址框始终展示真实完整连接地址；复制按钮与悬停提示也使用同一份值。
        private string _realAddressText = string.Empty;

        public MainForm(EventWaitHandle exitSignal)
        {
            _exitSignal = exitSignal;
            StartExitWatcher();
            Text = "TapRelay";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            // 这里只是首帧的起始值，真正的下限由布局按实测控件高度算出
            // （见 LayoutNotificationAndLogCards 末尾与 ApplyPendingMinimumHeight）。
            // 首帧先用折叠日志所需的紧凑高度，布局完成后再按 DPI 与字体实测值修正下限。
            // 展开日志时 ToggleLogExpanded 会临时把窗口顶高，收起后恢复到这里，
            // 避免默认状态在底部留下大块无内容区域。
            MinimumSize = new Size(860, 760);
            Size = new Size(960, 820);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont(9.5F);
            // 卡片投影画在窗体背景上（见 OnPaintBackground），必须开双缓冲，
            // 否则窗口缩放时投影与卡片会互相闪烁。
            DoubleBuffered = true;
            BackColor = LightTheme.Background;

            _server.SnapshotChanged += OnSnapshotChanged;
            _server.LogLine += AppendLog;
            // 协议回执等只对诊断有价值的日志走单独通道：写诊断文件，但不进首页摘要。
            _server.DetailLogLine += AppendDetailLog;
            _server.AppsChanged += OnAppsChanged;
            // 门控在运行期自动启用新安装来源后（如 TapRelay 运行期间重装的 ZCode），
            // 重跑面板自动检测，让启用集与"已安装"芯片收敛到新状态。
            _server.SourceAutoEnabled += OnSourceAutoEnabled;

            // Relay bridge: forward broadcasts to remote phones and process incoming messages.
            _server.OutboundBroadcast += OnOutboundBroadcast;

            // Agent API proxy bootstrap: lazy-init proxies so the phone can send
            // agent.call / agent.probe RPCs through the relay.
            AgentApiProxyBootstrap.Bootstrap(AppendLog, AppendDetailLog);

            _agentMonitor = new AgentCompletionMonitor(
                _server, AppendLog, isSourceProcessRunning: null, readTraeToast: null,
                detailLog: AppendDetailLog);
            // Hook 事件只带 Claude Code 的启动目录，真实项目目录由转录解析器记忆，
            // 这里把回查入口交给服务端，避免所有会话的项目名都退化成用户主目录名。
            _server.ClaudeDirectoryResolver = _agentMonitor.GetClaudeWorkingDirectory;
            _server.ControlRequestHandler = HandleControlRequestAsync;
            // Claude Code Haha 权限审批：监听本地 sidecar 的 permission_request，
            // 让手机通知带上真实 requestId（否则「允许/拒绝」按钮不渲染），
            // 并把手机端决策原路回写。既有等待启发式不受影响。
            _hahaPermissionBridge = new ClaudeHahaPermissionBridge(_server, AppendLog, AppendDetailLog);
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
                if (_logExpanded)
                {
                    _logScrollBar.Recalculate();
                }
                else
                {
                    _logScrollBar.Visible = false;
                }
            };
            _messageBox.MouseWheel += (_, __) => _messageScrollBar.Recalculate();
            _messageBox.TextChanged += (_, __) =>
            {
                _messageMarqueeOffset = 0;
                ResetTextMarquee(_messageBox);
                UpdateFieldMarquee(_messageBox, _messageMarqueeLabel, ref _messageMarqueeOffset);
                _toolTip.SetToolTip(_messageBox, "完整测试通知：" + _messageBox.Text);
                _toolTip.SetToolTip(_messageMarqueeLabel, "完整测试通知：" + _messageBox.Text);
                _messageScrollBar.Recalculate();
                ScheduleUiStateSave();
            };
            // 失焦时也保存一次，确保输入未触发 TextChanged 的边界情况（IME 组合等）。
            _messageBox.LostFocus += (_, __) =>
            {
                _messageMarqueeOffset = 0;
                ResetTextMarquee(_messageBox);
                UpdateFieldMarquee(_messageBox, _messageMarqueeLabel, ref _messageMarqueeOffset);
                _uiStateSaveTimer.Stop();
                SaveUiState();
            };
            _messageBox.Enter += (_, __) =>
            {
                _messageMarqueeOffset = 0;
                ResetTextMarquee(_messageBox);
                _messageMarqueeLabel.Visible = false;
                _messageBox.Visible = true;
            };
            _addressBox.Enter += (_, __) =>
            {
                _addressMarqueeOffset = 0;
                ResetTextMarquee(_addressBox);
                _addressMarqueeLabel.Visible = false;
                _addressBox.Visible = true;
            };
            _addressBox.Leave += (_, __) =>
            {
                _addressMarqueeOffset = 0;
                ResetTextMarquee(_addressBox);
                UpdateFieldMarquee(_addressBox, _addressMarqueeLabel, ref _addressMarqueeOffset);
            };
            _marqueeTimer.Tick += (_, __) =>
            {
                AdvanceTextMarquee(_addressBox, _addressMarqueeLabel, ref _addressMarqueeOffset);
                AdvanceTextMarquee(_messageBox, _messageMarqueeLabel, ref _messageMarqueeOffset);
                AdvanceLogPreviewMarquee();
            };
            _uiStateSaveTimer.Tick += (_, __) =>
            {
                _uiStateSaveTimer.Stop();
                SaveUiState();
            };
            _logBox.MouseWheel += (_, e) => _logScrollBar.ScrollByMouseWheel(e.Delta);
            _logBox.TextChanged += (_, __) =>
            {
                if (_logExpanded)
                {
                    _logScrollBar.Recalculate();
                }
                else
                {
                    _logScrollBar.Visible = false;
                }
            };
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
        /// 退出看门狗：把"进程一定会死"变成硬保证。
        /// <para>
        /// 清理链路里任何一步卡住都会让进程滞留，而用户看到的现象就是
        /// "软件退出了但端口还被占着" —— 占着端口的其实是那个没退干净的进程。
        /// 实测（2026-10-12）<see cref="NotifyServer.StopAsync"/> 里的
        /// <c>host.StopAsync</c> 会永久挂住：监听端口约 667ms 就已释放，但那个 await
        /// 永不返回，诊断日志停在"开始停止服务"，进程滞留 130MB+ 不退。
        /// </para>
        /// <para>
        /// 端口由操作系统在进程退出时释放，所以只要保证进程一定死，
        /// "退出即解除占用"就必然成立。这里用独立线程兜底，到点无论清理走到哪一步
        /// 都结束进程；<c>IsBackground</c> 保证它自己不会反过来阻止进程退出。
        /// </para>
        /// </summary>
        private void ArmExitWatchdog()
        {
            if (Interlocked.Exchange(ref _exitWatchdogArmed, 1) != 0)
            {
                return;
            }

            var watchdog = new Thread(() =>
            {
                Thread.Sleep(ExitWatchdogMilliseconds);
                try
                {
                    // 先把已排队的诊断日志落盘，否则看门狗路径会丢掉最后几行线索。
                    // 用有上限的等待：DiagnosticLogWriter.Dispose 自己会 Join 3s，
                    // 直接调用会把看门狗的总时长拖到 5s，削弱它"必死"的保证。
                    var flush = Task.Run(() =>
                    {
                        try
                        {
                            _diagnosticLogWriter.Dispose();
                        }
                        catch
                        {
                            // 落盘失败不影响退出。
                        }
                    });
                    flush.Wait(ExitWatchdogFlushMilliseconds);
                }
                catch
                {
                    // 看门狗不做任何可能失败的事。
                }

                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "TapRelay.ExitWatchdog"
            };
            watchdog.Start();
        }

        /// <summary>
        /// 新实例接管：设置退出标志后释放服务资源并确定性退出当前进程。
        /// </summary>
        private void ExitForNewInstance()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _trayIcon.Visible = false;
            ArmExitWatchdog();
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
            if (_logExpanded)
            {
                _logScrollBar.Recalculate();
            }
            else
            {
                _logScrollBar.Visible = false;
            }
            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_updateCheckStarted)
            {
                return;
            }

            _updateCheckStarted = true;
            var release = await UpdateChecker.TryGetLatestAsync();
            if (release == null || IsDisposed || Disposing ||
                _uiState.NeverPromptUpdates ||
                string.Equals(_uiState.SkippedUpdateVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var dialog = new UpdateDialog(release, UpdateChecker.CurrentVersion);
            dialog.ShowDialog(this);
            switch (dialog.Result)
            {
                case UpdateDialogResult.Update:
                    OpenExternalUrl(release.TargetUrl);
                    break;
                case UpdateDialogResult.SkipOnce:
                    _uiState.SkippedUpdateVersion = release.Version;
                    SaveUiState();
                    break;
                case UpdateDialogResult.Never:
                    _uiState.NeverPromptUpdates = true;
                    SaveUiState();
                    break;
            }
        }

        private void OpenExternalUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "无法打开更新链接：" + error.Message, "打开更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // 这里已经确定要退出了，先武装看门狗：后面每一步清理都可能卡住。
            ArmExitWatchdog();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _uiStateSaveTimer.Stop();
            _uiStateSaveTimer.Dispose();
            _marqueeTimer.Stop();
            _marqueeTimer.Dispose();
            SaveUiState();
            _agentMonitor.Dispose();
            _processLifecycleMonitor.Dispose();
            _hahaPermissionBridge.Dispose();
            // 同步等待停止：StopAsync 会立即中止手机客户端，并自带硬上限
            // （优雅停止 1000ms + 资源释放 400ms），不会无限阻塞。
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
            // 状态条是自绘控件，位置与尺寸由 RecalculateLayout 定，颜色由 ApplyTheme 刷。
            // 这里只登记它承载的四个图标按钮与它们的行为。
            _iconQr.Click += (_, __) => ShowConnectionQr();
            _iconProjects.Click += (_, __) => ShowPersonalProjects();
            _iconTheme.Click += (_, __) => ToggleTheme();
            _iconMore.Click += (_, __) =>
                _serviceMoreMenu.Show(_iconMore, new Point(0, _iconMore.Height));
            _copyAddressButton.Click += (_, __) => CopyAddressToClipboard();
            _maskAddressButton.Click += (_, __) => ToggleAddressMask();
            // 图标按钮没有文字标签，ToolTip 是唯一的口头说明；AccessibleName 已由构造函数给出。
            _toolTip.SetToolTip(_iconQr, "显示连接二维码");
            _toolTip.SetToolTip(_iconProjects, "个人项目");
            _toolTip.SetToolTip(_iconTheme, "切换深浅色主题");
            _toolTip.SetToolTip(_iconMore, "更多服务操作");
            _toolTip.SetToolTip(_copyAddressButton, "复制完整连接地址");
            _toolTip.SetToolTip(_maskAddressButton, "脱敏显示连接地址");
            // 图标按钮的 hover 底色与图标色在 ApplyTheme 里统一刷，这里给出浅色主题的初值，
            // 避免 BuildLayout 与首次 ApplyTheme 之间的短暂窗口出现纯黑图标。
            _iconQr.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);
            _iconProjects.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);
            _iconTheme.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);
            _iconMore.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);
            _copyAddressButton.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);
            _maskAddressButton.ApplyColors(
                LightTheme.Surface, LightTheme.TextSecondary, LightTheme.SurfaceAlt, LightTheme.InputFocusBorder);

            _serviceCard.BackColor = LightTheme.Surface;
            _serviceCard.BorderColor = LightTheme.CardBorder;

            _serviceCardLabel.Text = "服务与连接";
            _serviceCardLabel.AutoSize = true;
            _serviceCardLabel.Font = UiFont(12F, FontStyle.Bold);
            _serviceCardLabel.ForeColor = LightTheme.TextPrimary;

            // 方案 B-3：原先挤在这张卡标题带里的「服务状态 / 手机连接数」已上移到状态条。
            // 两者原本与卡片标题共用一行（同为 10.5pt），所以移走不改变本卡高度，
            // 只是把首屏最高处的信息从第三行提到了第一行。
            _addressLabel.Text = "手机连接地址";
            _addressLabel.AutoSize = true;
            _addressLabel.Font = UiFont(11F, FontStyle.Bold);
            _addressLabel.ForeColor = LightTheme.TextPrimary;

            _addressHintLabel.Text = "扫码连接 · 公网地址在网络设置中配置";
            _addressHintLabel.AutoSize = true;
            _addressHintLabel.Font = UiFont(8.5F);
            _addressHintLabel.ForeColor = LightTheme.TextSecondary;
            _addressHintLabel.Tag = "muted";

            // 所有控件的位置与尺寸统一由 RecalculateLayout 计算，这里只设置
            // 与布局无关的外观、文案与事件，避免两处坐标互相覆盖。
            _addressBox.ReadOnly = true;
            // 服务端可能返回多个候选端点。单行显示并把换行转换为分隔符，
            // 让地址框沿同一条轨道巡航，避免固定高度只显示第一行。
            // WinForms 的单行 TextBox 会忽略外部设置的高度，自动收缩到字体高度；
            // 保持单行行为但使用多行宿主，才能让字段稳定占满 40px 并与按钮对齐。
            _addressBox.Multiline = true;
            _addressBox.WordWrap = false;
            _addressBox.AcceptsReturn = false;
            // 保留原生水平滚动样式，但把滚动条本身隐藏；巡航通过 Win32
            // 横向滚动消息推进，避免 TextBox 因无 WS_HSCROLL 而忽略请求。
            _addressBox.ScrollBars = ScrollBars.Horizontal;
            _addressBox.TabStop = false;
            // 描边交给所属卡片自绘（见 VisualCardPanel.SetFieldLayout）：
            // FixedSingle 只能画系统色，改不了、也不随主题变。
            _addressBox.BorderStyle = BorderStyle.None;
            _addressBox.BackColor = LightTheme.InputSurface;
            _addressBox.Font = new Font("Consolas", 10F);
            _addressBox.WordWrap = false;
            _addressBox.Padding = new Padding(10, 7, 10, 5);
            ConfigureMarqueeLabel(_addressMarqueeLabel, _addressBox.Font);
            _addressMarqueeLabel.Visible = false;
            _addressMarqueeLabel.Click += (_, __) => FocusMarqueeEditor(_addressBox, _addressMarqueeLabel);

            _startButton.Text = "启动服务";
            _startButton.Click += async (_, __) => await StartServerAsync();

            _stopButton.Text = "停止服务";
            _stopButton.Click += async (_, __) => await StopServerAsync();

            // 「二维码」与「更多」原先在这张卡内，方案 B-3 已上移到顶部状态条
            // （见 BuildLayout 开头的图标按钮登记），这里只剩它们的下拉菜单内容。
            _personalProjectsMenuItem.Text = "个人项目";
            _personalProjectsMenuItem.Click += (_, __) => ShowPersonalProjects();
            _networkSettingsMenuItem.Text = "网络设置";
            _networkSettingsMenuItem.Click += (_, __) => ShowNetworkSettings();
            _compactModeMenuItem.Click += (_, __) => ToggleCompactMode();
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
                _personalProjectsMenuItem,
                new ToolStripSeparator(),
                _networkSettingsMenuItem,
                _compactModeMenuItem,
                _autoStartMenuItem
            });

            _messageLabel.Text = "测试通知";
            _messageLabel.AutoSize = true;
            _messageLabel.Font = UiFont(10.5F, FontStyle.Bold);
            _messageLabel.ForeColor = LightTheme.TextPrimary;

            _messageBox.Font = UiFont(10F);
            // 方案 B-4：测试通知改成单行输入，常用场景不再为一段多行说明占 80px。
            // 旧版本可能保存过换行文本，迁移为单行空格，避免切换 Multiline=false 后内容被截断。
            // 与地址框使用同一固定高度宿主，避免测试通知框在不同 DPI 下变成细线。
            _messageBox.Multiline = true;
            _messageBox.WordWrap = false;
            _messageBox.AcceptsReturn = false;
            // 旧版本把已删除的“继续”操作写进了测试文案。它不是用户真正的输入，
            // 继续保留会让新界面看起来像仍支持该操作，因此只迁移这类历史默认值。
            var savedTestMessage = string.IsNullOrWhiteSpace(_uiState.TestMessage) ||
                                   _uiState.TestMessage.Contains("【继续】", StringComparison.Ordinal)
                ? "Agent 任务已完成"
                : _uiState.TestMessage;
            _messageBox.Text = savedTestMessage.Replace("\r", " ").Replace("\n", " ");
            // 同地址框一样保留水平滚动样式，实际滚动条由 CenteredTextBox 隐藏。
            _messageBox.ScrollBars = ScrollBars.Horizontal;
            _messageBox.BorderStyle = BorderStyle.None;
            _messageBox.BackColor = LightTheme.InputSurface;
            _messageBox.Padding = new Padding(10, 4, 10, 4);
            ConfigureMarqueeLabel(_messageMarqueeLabel, _messageBox.Font);
            _messageMarqueeLabel.Visible = false;
            _messageMarqueeLabel.Click += (_, __) => FocusMarqueeEditor(_messageBox, _messageMarqueeLabel);
            // 单行输入不需要独立滚动条；保留对象是为了不破坏既有状态更新路径，实际隐藏。
            _messageScrollBar.Visible = false;

            _sendButton.Text = "发送测试";
            _sendButton.Click += async (_, __) => await SendTestAsync();

            _notificationAppsLabel.Text = "通知应用";
            _notificationAppsLabel.AutoSize = true;
            _notificationAppsLabel.Font = UiFont(11F, FontStyle.Bold);
            _notificationAppsLabel.ForeColor = LightTheme.TextPrimary;
            _autoDetectAppsButton.Text = "自动检测";
            _autoDetectAppsButton.Click += async (_, __) => await RunNotificationAutoDetectAsync();
            _selectAppButton.Text = "应用选择";
            _selectAppButton.Click += (_, __) => _notificationAppsPanel.OpenApplicationSelection();
            _notificationCard.BackColor = LightTheme.Surface;
            _notificationCard.BorderColor = LightTheme.CardBorder;
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

            // 折叠态摘要行：级别点 + 最新一行（AutoEllipsis 截断）。
            // 用 Consolas 与展开态的日志正文同族，视觉上能看出"这是同一份内容的摘要"。
            _logPreviewLabel.AutoSize = false;
            _logPreviewLabel.Font = new Font(UiTokens.MonoFontFamily, UiTokens.LogPreviewFontSize);
            _logPreviewLabel.ForeColor = LightTheme.TextSecondary;
            _logPreviewLabel.Tag = "muted";
            _logPreviewLabel.TextAlign = ContentAlignment.MiddleLeft;
            // 文本由 AdvanceLogPreviewMarquee 按可视宽度切片，不使用省略号，
            // 否则用户看到的仍然只是“最新一行的一部分”。
            _logPreviewLabel.AutoEllipsis = false;
            _logPreviewLabel.Text = string.Empty;

            _logEmptyLabel.AutoSize = false;
            _logEmptyLabel.Font = UiFont(UiTokens.CaptionFontSize);
            _logEmptyLabel.ForeColor = LightTheme.TextSecondary;
            _logEmptyLabel.BackColor = LightTheme.InputSurface;
            _logEmptyLabel.Tag = "muted";
            _logEmptyLabel.Text = "暂无运行记录\r\n服务启动后会在这里显示实时日志";
            _logEmptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _logEmptyLabel.Visible = false;

            // 折叠态下整行可点：标题、摘要、右侧提示、级别点都触发展开/收起，
            // 避免用户必须精确点到某个小字上。
            foreach (var toggle in new Control[] { _logLabel, _logPreviewLabel, _logHintLabel, _logDot })
            {
                toggle.Cursor = Cursors.Hand;
                toggle.Click += (_, __) => ToggleLogExpanded();
            }

            // 折叠态下整张卡就是那一行，把卡片本身也接上：标题与右侧提示之间有一大段
            // 空白，用户在字缝里点下去不该毫无反应。展开态不接 —— 那时卡片包含日志正文，
            // 点空白就收起会与「点正文定位光标」打架。
            _logCard.Click += (_, __) =>
            {
                if (!_logExpanded)
                {
                    ToggleLogExpanded();
                }
            };

            _logCard.BackColor = LightTheme.Surface;
            _logCard.BorderColor = LightTheme.CardBorder;

            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            // 原生滚动条在深色主题下仍是系统浅色；改用自绘 LogScrollBar。
            _logBox.ScrollBars = ScrollBars.None;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.BackColor = LightTheme.InputSurface;
            _logBox.ForeColor = LightTheme.TextSecondary;
            _logBox.Font = new Font(UiTokens.MonoFontFamily, UiTokens.LogFontSize);
            _logBox.WordWrap = true;
            _logBox.Padding = new Padding(10, 8, 10, 8);
            // 首帧就是折叠态：在第一次 RecalculateLayout 之前先藏起来，
            // 否则会有一帧日志框盖在摘要行上。
            _logBox.Visible = false;

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

            // 个人项目已经收进“更多”菜单，顶部状态条只保留连接二维码、主题和更多。
            // 主题图标的深浅态由 ApplyTheme 统一同步，这里不重复设置。
            _iconProjects.Visible = false;

            ApplyButtonStyle(_startButton, UiButtonStyler.UiButtonStyle.Primary);
            ApplyButtonStyle(_stopButton, UiButtonStyler.UiButtonStyle.Danger);
            ApplyButtonStyle(_sendButton, UiButtonStyler.UiButtonStyle.Primary);
            ApplyButtonStyle(_autoDetectAppsButton, UiButtonStyler.UiButtonStyle.Secondary);
            ApplyButtonStyle(_selectAppButton, UiButtonStyler.UiButtonStyle.Secondary);

            // 原生 TextBox 没有任何焦点反馈，把焦点态转给所属卡片自绘描边。
            // 序号必须与 RecalculateLayout 里 SetFieldLayout 的传参顺序一致。
            _addressBox.Enter += (_, __) =>
            {
                _serviceCard.SetFocusedField(0);
                // 只读地址框一旦获得焦点就被全选，窗口启动时呈现一整块高亮，
                // 看上去像出错。鼠标点进来（想复制地址）时保留全选，程序化
                // 获得焦点时再清掉选区——延后到消息处理结束，否则会被系统
                // 的全选覆盖回去。
                if (Control.MouseButtons == MouseButtons.None && IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (_addressBox.Focused)
                        {
                            _addressBox.SelectionLength = 0;
                        }
                    }));
                }
            };
            _addressBox.Leave += (_, __) => _serviceCard.SetFocusedField(-1);
            _messageBox.Enter += (_, __) => _serviceCard.SetFocusedField(1);
            _messageBox.Leave += (_, __) => _serviceCard.SetFocusedField(-1);
            _logBox.Enter += (_, __) => _logCard.SetFocusedField(0);
            _logBox.Leave += (_, __) => _logCard.SetFocusedField(-1);

            Controls.AddRange(new Control[]
            {
                _stripCard,
                _serviceCard,
                _notificationCard,
                _logCard,
                _serviceCardLabel,
                _addressLabel,
                _addressHintLabel,
                _addressBox,
                _addressMarqueeLabel,
                _maskAddressButton,
                _copyAddressButton,
                _startButton,
                _stopButton,
                _messageLabel,
                _messageBox,
                _messageMarqueeLabel,
                _messageScrollBar,
                _sendButton,
                _notificationAppsLabel,
                _autoDetectAppsButton,
                _selectAppButton,
                _notificationAppsPanel,
                _logLabel,
                _logPreviewLabel,
                _logDot,
                _logHintLabel,
                _logBox,
                _logEmptyLabel,
                _logScrollBar,
                _iconQr,
                _iconProjects,
                _iconTheme,
                _iconMore
            });

            // Keep the visual cards behind the interactive controls.
            // 状态条也是自绘面板，必须和卡片一起沉到最底层，否则会盖住四个图标按钮。
            _stripCard.SendToBack();
            _serviceCard.SendToBack();
            _notificationCard.SendToBack();
            _logCard.SendToBack();

            // 主题从 ui-state.json 恢复；旧版本的 AddressMasked 字段会被安全忽略。
            if (_uiState.DarkMode == null)
            {
                _uiState.DarkMode = LoadTheme();
                // 迁移保存前同步当前输入框文本，避免把用户已保存的 TestMessage 覆盖成空值。
                _uiState.TestMessage = _messageBox.Text ?? string.Empty;
                UiStateStore.Save(_uiState);
            }
            _isDarkMode = _uiState.DarkMode.Value;
            _compactMode = _uiState.CompactMode;
            UpdateCompactModeMenuText();
            ApplyTheme();
            OnSnapshotChanged(_server.GetSnapshot());
            _layoutReady = true;
            RecalculateLayout();
            if (_compactMode)
            {
                Height = Math.Max(MinWindowHeightFloor, _requiredMinHeight);
            }
            _marqueeTimer.Start();
        }

        // 统一布局度量。所有间距只允许取这些常量，禁止在 RecalculateLayout 中
        // 出现新的字面量偏移，否则会重新回到"改一处坏一处"的硬编码坐标布局。
        private const int PageMargin = UiTokens.PageMargin;       // 卡片到窗口边缘
        private const int CardPad = UiTokens.CardPadding;          // 卡片内边距
        private const int Gap = UiTokens.Gap;               // 同组控件间距
        private const int SectionGap = UiTokens.SectionGap;       // 卡片之间的纵向间距
        private const int RowGap = UiTokens.RowGap;           // 卡片内两行之间的间距
        private const int InputHeight = UiButtonStyler.MainWindowButtonHeight; // 地址框 / 主按钮统一高度（UiTokens.MainButtonHeight）
        private const int MessageHeight = UiTokens.MessageHeight;    // 方案 B-4：测试通知改为单行输入，保留一行舒适内边距
        // 运行日志展开态可视区最小高度：Consolas 9.5pt 实测行高 15px，240px 约 15 行。
        // 折叠态的高度不在这里写死，由 CardPad + 标题行高 + CardPad 实测得出（约 38px），
        // 这样字体行高变化时折叠行也会跟着自适应。
        private const int LogExpandedMinHeight = 240;
        // 顶部状态条（方案 B-3）。取代原先「品牌 + 大标题 + 副标题」三行页头：
        // 用户打开这个窗口 90% 的时间只关心两件事——服务通不通、手机连上没有，
        // 把这两件事提到最高处，同时把原先散落三处的图标按钮收拢到条内右侧。
        // 条高写死 48 而不是实测：条内文字全部由 StatusStripPanel 自绘，
        // 字号固定（10.5pt 品牌 / 9.5pt 指标），48px 对 10.5pt 有充足余量。
        private const int StripHeight = UiTokens.StatusStripHeight;
        private const int StripPad = UiTokens.StatusStripPadding;         // 状态条内边距（比 CardPad 小：48px 高的条留 18 会显局促）
        private const int StripIconSize = UiTokens.IconButtonSize;    // 条内图标按钮边长
        private const int StripIconGap = UiTokens.IconButtonGap;      // 图标按钮之间的间距（比 Gap 小，一组按钮应读作一体）
        private const int StripIconDividerGap = 11;                   // 「更多」与高频入口之间的分隔线两侧留白

        // 主操作按钮宽度按「启动服务」四个字的实测宽度反推，每次重排都要量一次。
        // 字体必须缓存：Font 是 GDI 句柄的托管包装，原先写成 UiFont(9.5F) 是每趟重排
        // 新建一个、永不释放，窗口缩放时会持续泄漏 GDI 句柄。
        private static readonly Font PrimaryActionFont = UiFont(9.5F);

        // MinimumSize 的硬底线。窗口真正的最小高度由布局按实测度量算出（_requiredMinHeight），
        // 这里只兜住极端 DPI / 字体下布局算出荒谬小值的可能。
        private const int MinWindowHeightFloor = 480;

        /// <summary>
        /// 集中式响应式布局。规则：
        /// 1. 状态条、服务卡、通知卡自顶向下依次堆叠，每段高度由内容算出，不写死 Y；
        ///    状态条取代了原先的三行页头，卡片的纵向起点因此直接由条底算出；
        /// 2. 运行日志卡默认折叠为一行摘要（约 38px）；展开后底部锚定，
        ///    高度 = 通知卡底部到窗口底部的剩余空间，最小 <see cref="LogExpandedMinHeight"/>；
        /// 3. 通知应用面板拿到"剩余空间 - 日志预留高度"作为高度上限，超出时面板内部滚动。
        ///    折叠态下日志只预留一行，省出的空间因此全部归通知面板（方案 B 的主要收益）；
        ///    展开态预留变大，面板自动让位，日志始终留在可视范围内。
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
        /// 应用布局过程算出的窗口最小高度。可升可降。
        ///
        /// 必须允许降低：运行日志卡展开时会额外要求约 240px，布局据此把下限抬高；
        /// 收起后若只升不降，下限就永久停在展开态的值，省下来的两百多像素会变成
        /// 窗口底部一块空白（实测：展开一次后收起，卡下方留白 278px）。
        /// 之所以不怕"与用户手动缩放来回争夺"：<c>_requiredMinHeight</c> 只由窗口宽度
        /// 与内容决定、与窗口高度无关，因此升降都是确定值，不会形成振荡。
        /// </summary>
        private void ApplyPendingMinimumHeight()
        {
            if (_requiredMinHeight <= 0)
            {
                return;
            }

            var target = Math.Max(MinWindowHeightFloor, _requiredMinHeight);
            if (MinimumSize.Height != target)
            {
                MinimumSize = new Size(MinimumSize.Width, target);
            }

            // MinimumSize 只约束后续用户拖拽，在部分 WinForms/Windows 组合下不会
            // 自动修正已经偏矮的窗口。内容分区变化时必须立即补齐，否则通知面板
            // 虽然按顺序排好了，底部仍会落到客户区外，看起来像被未安装区遮挡。
            if (Height < target)
            {
                Height = target;
            }
        }

        private void RecalculateLayoutCore()
        {
            var clientW = Math.Max(360, ClientSize.Width);
            var clientH = Math.Max(360, ClientSize.Height);
            var cardWidth = Math.Max(320, clientW - 2 * PageMargin);
            var cardLeft = PageMargin;
            var innerLeft = cardLeft + CardPad;
            var innerRight = cardLeft + cardWidth - CardPad;

            // ---- 顶部状态条（方案 B-3）：取代原「品牌 + 大标题 + 副标题」三行页头。
            // 条内文案由 StatusStripPanel 自绘，这里只定它的外框与右侧四个图标按钮的位置。
            _stripCard.Location = new Point(cardLeft, PageMargin);
            _stripCard.Size = new Size(cardWidth, StripHeight);
            // 左侧强调竖线的长度按条高算，与三张卡片的标题竖线共用同一套画法。
            _stripCard.SetSectionLayout(StripHeight);

            // 图标按钮从右往左排：无论条内文案多长，这一组始终钉住右边缘。
            // 宽度一律取 PreferredWidth —— 带文字的按钮比 28px 方钮宽得多，
            // 按 StripIconSize 排会互相叠住，文字也被右边的按钮盖掉。
            var stripRight = cardLeft + cardWidth - StripPad;
            var iconTop = PageMargin + (StripHeight - StripIconSize) / 2;
            var iconX = stripRight - _iconMore.PreferredWidth;
            _iconMore.Location = new Point(iconX, iconTop);
            // 「更多」与高频入口之间留一条分隔线，位置取两者中点。
            var dividerX = iconX - StripIconDividerGap;
            iconX = dividerX - StripIconDividerGap - _iconTheme.PreferredWidth;
            _iconTheme.Location = new Point(iconX, iconTop);
            iconX -= StripIconGap + _iconQr.PreferredWidth;
            _iconQr.Location = new Point(iconX, iconTop);

            var iconBlockWidth = stripRight - iconX;
            _iconQr.Size = new Size(_iconQr.PreferredWidth, StripIconSize);
            _iconTheme.Size = new Size(_iconTheme.PreferredWidth, StripIconSize);
            _iconMore.Size = new Size(_iconMore.PreferredWidth, StripIconSize);

            // 自绘文案区的右边界 = 图标块左沿再让出两组间距，避免文案顶到图标上。
            // 传的是控件局部坐标（以面板自身为原点），面板因此不需要知道自己在窗体上的位置。
            _stripCard.SetContentLayout(
                StripPad,
                cardWidth - StripPad - iconBlockWidth - Gap * 2,
                dividerX - cardLeft);

            // ---- 服务与连接卡：卡片标题独占标题带；「服务状态 / 手机连接数」已上移到状态条，
            // 两者原本与标题共用同一行，所以这一改动不增加也不减少本卡高度。
            var cardTop = PageMargin + StripHeight + SectionGap;
            var y = cardTop + CardPad;

            _serviceCardLabel.Location = new Point(innerLeft, y);
            y = _serviceCardLabel.Bottom + RowGap;

            _addressLabel.Location = new Point(innerLeft, y);
            _addressHintLabel.Location = new Point(
                _addressLabel.Right + Gap,
                y + Math.Max(0, (_addressLabel.Height - _addressHintLabel.Height) / 2));
            y = Math.Max(_addressLabel.Bottom, _addressHintLabel.Bottom) + Gap;

            // 地址行只剩当前服务的主操作：二维码与更多菜单已上移到顶部状态条（方案 B-3）。
            // 方案 B-4 再加一组 28px 地址操作图标，地址输入框仍保留最大可读宽度。
            // 脱敏与复制都使用纯图标，地址框只需让出两个方形按钮的总宽度。
            var primaryActionWidth = Math.Max(96,
                TextRenderer.MeasureText("启动服务", PrimaryActionFont).Width + 28);
            _startButton.Size = new Size(primaryActionWidth, InputHeight);
            _stopButton.Size = new Size(primaryActionWidth, InputHeight);
            _stopButton.Location = new Point(innerRight - primaryActionWidth, y);
            _startButton.Location = _stopButton.Location;
            _copyAddressButton.Size = new Size(StripIconSize, StripIconSize);
            _copyAddressButton.Location = new Point(
                _startButton.Left - Gap - StripIconSize,
                y + (InputHeight - StripIconSize) / 2);
            _maskAddressButton.Size = new Size(_maskAddressButton.PreferredWidth, StripIconSize);
            _maskAddressButton.Location = new Point(
                _copyAddressButton.Left - Gap - _maskAddressButton.Width,
                y + (InputHeight - StripIconSize) / 2);
            _addressBox.Location = new Point(innerLeft, y);
            _addressBox.Size = new Size(
                Math.Max(120, _maskAddressButton.Left - Gap - innerLeft),
                InputHeight);
            _addressMarqueeLabel.Location = _addressBox.Location;
            _addressMarqueeLabel.Size = _addressBox.Size;
            y = _addressBox.Bottom + RowGap;

            _messageLabel.Location = new Point(innerLeft, y);
            y = _messageLabel.Bottom + Gap;

            // 测试通知行：单行输入与发送按钮并排，按钮继续保持主操作视觉权重。
            // 两者顶端对齐且等高（MessageHeight 与 InputHeight 同值，都是主输入高度）。
            // 按钮宽度先给输入框让出可读下限：旧写法对输入框宽度取 Math.Max(120, …)，
            // 空间不足时输入框会越过 Gap 盖到按钮上；这里改为压缩按钮宽度来保证输入框。
            const int sendPreferredWidth = 148;
            const int messageBoxMinWidth = 200;
            var sendW = Math.Min(
                sendPreferredWidth,
                Math.Max(88, innerRight - innerLeft - Gap - messageBoxMinWidth));
            var sendX = innerRight - sendW;
            _sendButton.Size = new Size(sendW, InputHeight);
            _sendButton.Location = new Point(sendX, y);
            _messageScrollBar.Visible = false;
            _messageBox.Location = new Point(innerLeft, y);
            _messageBox.Size = new Size(
                sendX - Gap - innerLeft,
                MessageHeight);
            _messageMarqueeLabel.Location = _messageBox.Location;
            _messageMarqueeLabel.Size = _messageBox.Size;

            _serviceCard.Location = new Point(cardLeft, cardTop);
            _serviceCard.Size = new Size(cardWidth, _messageBox.Bottom + CardPad - cardTop);
            _serviceCard.SetSectionLayout(
                _addressLabel.Top - cardTop - RowGap / 2,
                _messageLabel.Top - cardTop - RowGap / 2);

            // 输入框描边由卡片自绘，尺寸定稿后再同步外框与圆角区域，
            // 顺序不能颠倒：SetFieldLayout 用的是输入框的最终坐标。
            ApplyFieldShape(_addressBox);
            ApplyFieldShape(_messageBox);
            _serviceCard.SetFieldLayout(
                ToCardRect(_serviceCard, _addressBox),
                ToCardRect(_serviceCard, _messageBox));

            LayoutNotificationAndLogCards(cardLeft, cardWidth, innerLeft, innerRight, clientH);
            UpdateFieldMarquee(_addressBox, _addressMarqueeLabel, ref _addressMarqueeOffset);
            UpdateFieldMarquee(_messageBox, _messageMarqueeLabel, ref _messageMarqueeOffset);

            // 卡片位置变了，画在窗体背景上的投影必须跟着重画，否则会留下旧位置的残影。
            // 只失效窗体自身（不含子控件），每次布局最多触发一次背景重绘，不构成重绘循环。
            Invalidate();
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

            var showApps = !_compactMode;
            _notificationCard.Visible = showApps;
            _notificationAppsPanel.Visible = showApps;
            _notificationAppsLabel.Visible = showApps;
            _autoDetectAppsButton.Visible = showApps;
            _selectAppButton.Visible = showApps;

            if (_compactMode)
            {
                // 简洁视图默认只显示一行最新日志，但点击摘要后仍允许展开查看完整记录。
                // 不要在每次布局重算时重置 _logExpanded，否则点击展开会被本次重排立即撤销。
                var compactLogCollapsedHeight = CardPad + _logLabel.Height + CardPad;
                var compactLogExpandedReserved =
                    CardPad + _logLabel.Height + Gap + LogExpandedMinHeight + CardPad;
                var compactLogTop = notifyTop;
                var compactLogHeight = _logExpanded
                    ? Math.Max(compactLogExpandedReserved, pageBottom - compactLogTop)
                    : compactLogCollapsedHeight;
                _logCard.Visible = true;
                _logCard.Location = new Point(cardLeft, compactLogTop);
                _logCard.Size = new Size(cardWidth, compactLogHeight);
                _logLabel.Location = new Point(innerLeft, compactLogTop + CardPad);
                UpdateLogPreview();

                _logBox.Visible = _logExpanded;
                _logScrollBar.Visible = _logExpanded;
                if (_logExpanded)
                {
                    var logBoxTop = _logLabel.Bottom + Gap;
                    const int logScrollW = ScrollBarRenderer.PreferredWidth;
                    var logBoxHeight = Math.Max(
                        LogExpandedMinHeight,
                        compactLogTop + compactLogHeight - CardPad - logBoxTop);
                    _logScrollBar.Location = new Point(innerRight - logScrollW, logBoxTop);
                    _logScrollBar.Size = new Size(logScrollW, logBoxHeight);
                    _logBox.Location = new Point(innerLeft, logBoxTop);
                    _logBox.Size = new Size(
                        Math.Max(120, innerRight - logScrollW - Gap - innerLeft),
                        logBoxHeight);
                    _logEmptyLabel.Location = _logBox.Location;
                    _logEmptyLabel.Size = _logBox.Size;
                    _logEmptyLabel.Visible = _uiLogLines.Count == 0;
                    _logScrollBar.Recalculate();
                    _logCard.SetSectionLayout(logBoxTop - compactLogTop - Gap / 2);
                    ApplyFieldShape(_logBox);
                    _logCard.SetFieldLayout(ToCardRect(_logCard, _logBox));
                }
                else
                {
                    _logEmptyLabel.Visible = false;
                    _logCard.SetSectionLayout(compactLogHeight);
                    _logCard.SetFieldLayout();
                }

                _requiredMinHeight = compactLogTop
                    + compactLogHeight
                    + PageMargin
                    + (Height - ClientSize.Height);
                return;
            }

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

            // 为运行日志卡预留空间，剩下的才允许通知面板使用。
            // 折叠态只预留一行标题的高度 —— 这是方案 B 的核心收益：省下的约 198px
            // 全部交给通知应用网格（图标可以变大、行数可以变多），而"日志仍随时可见"
            // 由那一行摘要保证，不是靠把它藏起来。
            var logCollapsedHeight = CardPad + _logLabel.Height + CardPad;
            var logExpandedReserved = CardPad + _logLabel.Height + Gap + LogExpandedMinHeight + CardPad;
            var logReserved = _logExpanded ? logExpandedReserved : logCollapsedHeight;
            _notificationAppsPanel.MaxContentHeight =
                pageBottom - logReserved - SectionGap - panelTop - CardPad;
            // 同时要求面板撑满这个上限：图标少的时候面板按内容算高，会比上限矮一截，
            // 而日志卡折叠后只占一行，这段差额就落到窗口底部变成一条灰色空白带
            // （默认 880x780 下实测约 159px）。让面板吸收它，日志卡便正好贴到窗口下边距。
            // 读回值而不是直接传算式结果：MaxContentHeight 的 setter 会按 MinContentHeight 兜底。
            _notificationAppsPanel.FillHeight = _notificationAppsPanel.MaxContentHeight;

            var panelHeight = _notificationAppsPanel.Height;

            _notificationCard.Location = new Point(cardLeft, notifyTop);
            _notificationCard.Size = new Size(
                cardWidth,
                panelTop + panelHeight + CardPad - notifyTop);
            _notificationCard.SetSectionLayout(panelTop - notifyTop - RowGap / 2);

            // 运行日志卡：顶部紧随通知卡。两种状态都向下吃到窗口下边距，
            // 不再在卡下方留下窗体底色的空白。折叠态的正常高度就是一行摘要
            // （面板已按 FillHeight 撑满，pageBottom - logTop 恰等于 logCollapsedHeight）；
            // 只有面板被 MinContentHeight 兜底顶高时才靠这里的 Math.Max 兜住残差。
            var logTop = _notificationCard.Bottom + SectionGap;
            var logCardHeight = Math.Max(
                _logExpanded ? logExpandedReserved : logCollapsedHeight,
                pageBottom - logTop);
            _logCard.Location = new Point(cardLeft, logTop);
            _logCard.Size = new Size(cardWidth, logCardHeight);

            _logLabel.Location = new Point(innerLeft, logTop + CardPad);

            // 折叠行（右侧提示 + 级别点 + 摘要）统一由 UpdateLogPreview 定位：
            // 日志每写入一行都要重算一次，不能只在布局时算。
            UpdateLogPreview();

            // 展开态才有日志框与滚动条；折叠态把它们藏起来而不是压成 0 高，
            // 否则会留下一条看不见但仍参与 Tab 顺序的控件。
            _logBox.Visible = _logExpanded;
            _logScrollBar.Visible = _logExpanded;
            if (_logExpanded)
            {
                var logBoxTop = _logLabel.Bottom + Gap;
                // 与消息框、通知面板共用同一共享宽度常量，避免滚动条粗细各自漂移。
                const int logScrollW = ScrollBarRenderer.PreferredWidth;
                var logBoxHeight = Math.Max(LogExpandedMinHeight, logTop + logCardHeight - CardPad - logBoxTop);
                _logScrollBar.Location = new Point(innerRight - logScrollW, logBoxTop);
                _logScrollBar.Size = new Size(logScrollW, logBoxHeight);
                _logBox.Location = new Point(innerLeft, logBoxTop);
                _logBox.Size = new Size(
                    Math.Max(120, innerRight - logScrollW - Gap - innerLeft),
                    logBoxHeight);
                _logEmptyLabel.Location = _logBox.Location;
                _logEmptyLabel.Size = _logBox.Size;
                _logEmptyLabel.Visible = _uiLogLines.Count == 0;
                _logScrollBar.Recalculate();
                _logCard.SetSectionLayout(logBoxTop - logTop - Gap / 2);
                ApplyFieldShape(_logBox);
                _logCard.SetFieldLayout(ToCardRect(_logCard, _logBox));
            }
            else
            {
                // 整张卡就是标题行，没有下方分区，也不需要字段描边。
                _logCard.SetSectionLayout(logCardHeight);
                _logCard.SetFieldLayout();
                _logEmptyLabel.Visible = false;
            }

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

        /// <summary>
        /// 输入框自身不再画边框（BorderStyle = None），改为裁出圆角区域：
        /// 四边各向内缩 1px，把最外那一圈让给所属卡片绘制的描边线；
        /// 圆角半径比描边小 1px，让两者贴合。
        /// </summary>
        private static void ApplyFieldShape(TextBox box)
        {
            if (box.Width <= 6 || box.Height <= 6)
            {
                return;
            }

            var previous = box.Region;
            using (var path = RoundedPath(
                new RectangleF(1f, 1f, box.Width - 2, box.Height - 2),
                FieldCornerRadius - 1))
            {
                box.Region = new Region(path);
            }

            previous?.Dispose();
        }

        /// <summary>把输入框的窗体坐标换算成所属卡片的局部坐标，供卡片自绘描边。</summary>
        private static Rectangle ToCardRect(Control card, Control field)
        {
            return new Rectangle(
                field.Left - card.Left,
                field.Top - card.Top,
                field.Width,
                field.Height);
        }

        /// <summary>输入框圆角半径。卡片 16px、输入框 8px，形成两级圆角层级。</summary>
        private const int FieldCornerRadius = UiTokens.FieldRadius;

        /// <summary>
        /// 圆角矩形路径。放在窗体这一层而不是 VisualCardPanel 里：C# 的 private
        /// 可达性只向内延伸到嵌套类，外层类访问不到嵌套类的私有成员，反过来可以，
        /// 所以共享的绘图工具必须由外层持有。
        /// </summary>
        private static GraphicsPath RoundedPath(RectangleF rect, int radius)
        {
            var path = new GraphicsPath();
            // 宽/高为 0 时 d=0 会让 AddArc 抛 ArgumentException（与 LogScrollBar 同源），保底为 1。
            var d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(Math.Max(1f, rect.Width), Math.Max(1f, rect.Height))));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateLayout();
        }

        /// <summary>
        /// 在页面底色之上、卡片之下绘制投影。三张卡片随后会把自身区域盖住，
        /// 只有卡片外扩的那一圈留下来，形成卡片「浮起」的层次。
        /// 之所以画在窗体背景而不是卡片自己身上：VisualCardPanel 设了 Region，
        /// 它的绘制被裁剪在自身圆角矩形内，画不出向外的投影。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (!_layoutReady)
            {
                return;
            }

            var colors = _isDarkMode ? DarkTheme : LightTheme;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawCardShadow(e.Graphics, _stripCard, colors.Shadow);
            DrawCardShadow(e.Graphics, _serviceCard, colors.Shadow);
            DrawCardShadow(e.Graphics, _notificationCard, colors.Shadow);
            DrawCardShadow(e.Graphics, _logCard, colors.Shadow);
        }

        /// <summary>
        /// 三层递进投影：越靠外越淡，整体向下偏移 1px，模拟光源在正上方。
        /// 卡片自身区域会被卡片覆盖，所以不会压在内容上。
        /// </summary>
        private static void DrawCardShadow(Graphics graphics, Control card, Color shadow)
        {
            if (card == null || !card.Visible || card.Width <= 8 || card.Height <= 8)
            {
                return;
            }

            var bounds = card.Bounds;
            var radius = card is VisualCardPanel panel ? panel.CornerRadius : UiTokens.CardRadius;
            // 工作台采用轻层次：保留一圈非常淡的下沿阴影，避免四块容器像漂浮卡片。
            for (var step = 1; step >= 1; step--)
            {
                const int alpha = 7;
                var rect = new Rectangle(
                    bounds.X - step,
                    bounds.Y - step + 1,
                    bounds.Width + step * 2,
                    bounds.Height + step * 2);
                using (var path = RoundedPath(rect, radius + step))
                using (var brush = new SolidBrush(Color.FromArgb(alpha, shadow)))
                {
                    graphics.FillPath(brush, path);
                }
            }
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
                // 铁律 R2：端口固定 1122，不自动切端口。占用即启动失败并明确提示用户释放。
                AppendLog("启动失败：" + error.Message);
                var message = error is InvalidOperationException ioError
                    ? ioError.Message
                    : "监听端口 1122 被占用或不可用，请关闭其他占用进程（常见为另一个 TapRelay 实例）后重试。";
                MessageBox.Show(
                    this,
                    message,
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
            // 状态条进入「正在停止…」过渡态。必须显式切换：停止过程有几秒，
            // 期间状态条若还停在绿色的「服务运行中」，用户会以为点击没生效。
            _stripCard.SetStatus(StripState.Stopping, _server.GetSnapshot().ClientCount);
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

        /// <summary>
        /// 复制可直接连接的完整地址。显示框可能正在巡航其中一个候选端点，
        /// 复制始终使用服务端的原始地址集合，避免把展示状态带进手机配置。
        /// </summary>
        private void CopyAddressToClipboard()
        {
            var address = _realAddressText;
            if (string.IsNullOrWhiteSpace(address) ||
                address.StartsWith("未找到", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                Clipboard.SetText(address);
                AppendLog("已复制手机连接地址。");
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "复制地址失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 脱敏只影响地址框的显示；_realAddressText 保持不变，复制与二维码仍拿到完整地址。
        /// </summary>
        private void ToggleAddressMask()
        {
            if (string.IsNullOrWhiteSpace(_realAddressText) ||
                _realAddressText.StartsWith("未找到", StringComparison.Ordinal))
            {
                return;
            }

            _addressMasked = !_addressMasked;
            RefreshAddressDisplay();
            AppendLog(_addressMasked ? "连接地址已脱敏显示。" : "连接地址已恢复完整显示。");
        }

        private void RefreshAddressDisplay()
        {
            _addressBox.Text = _addressMasked
                ? MaskAddressForDisplay(_realAddressText)
                : FormatAddressForDisplay(_realAddressText);
            _addressMarqueeOffset = 0;
            ResetTextMarquee(_addressBox);
            UpdateFieldMarquee(_addressBox, _addressMarqueeLabel, ref _addressMarqueeOffset);
            // 悬停提示也不能泄露完整地址：脱敏态只给状态说明。
            var addressToolTip = _addressMasked
                ? "连接地址已脱敏显示，点击眼睛按钮可恢复完整地址。"
                : "完整手机连接地址：\r\n" + (_realAddressText ?? string.Empty);
            _toolTip.SetToolTip(_addressBox, addressToolTip);
            _toolTip.SetToolTip(_addressMarqueeLabel, addressToolTip);
            _toolTip.SetToolTip(_maskAddressButton, _addressMasked ? "显示完整连接地址" : "脱敏显示连接地址");
            _maskAddressButton.Masked = _addressMasked;
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
        /// 把测试消息文本和深/浅色主题持久化到 ui-state.json，
        /// 重启 / 重新部署后恢复，不再回退到默认值。
        /// </summary>
        private void SaveUiState()
        {
            try
            {
                _uiState.TestMessage = _messageBox.Text ?? string.Empty;
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
            ArmExitWatchdog();
            AppendLog("正在退出软件...");

            // 后台优雅关闭。等够 StopAsync 的自身上限，让 Close() 之后的二次调用
            // 立即返回（_host 已置空）；健康时 stopTask 几毫秒就完成，这里不会真的等。
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
            await Task.WhenAny(stopTask, Task.Delay(ExitTrayStopWaitMilliseconds));

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
                // 「服务通不通、手机连上没有」这两件事现在都在顶部状态条上：
                // 状态点与状态文字同色，形成「圆点 + 文字」双通道表达。
                _stripCard.SetStatus(
                    snapshot.IsRunning ? StripState.Running : StripState.Stopped,
                    snapshot.ClientCount);
                _serviceCard.AccentColor = snapshot.IsRunning ? colors.Success : colors.Border;
                _serviceCard.Invalidate();
                // 真实地址始终存入 _realAddressText；显示框把多个候选端点
                // 压成一条可巡航文本，复制与提示仍使用原始完整集合。
                _realAddressText = snapshot.AddressText;
                RefreshAddressDisplay();
                _startButton.Enabled = !snapshot.IsRunning;
                _startButton.Visible = !snapshot.IsRunning;
                _stopButton.Enabled = snapshot.IsRunning;
                _stopButton.Visible = snapshot.IsRunning;
                // 二维码按钮仍在状态条上，只是从文字按钮换成了图标按钮：
                // 没有可扫的地址时点开只会得到一张空码，所以按同一个条件置灰。
                _iconQr.Enabled = snapshot.IsRunning && !string.IsNullOrWhiteSpace(snapshot.AddressText) &&
                    !snapshot.AddressText.StartsWith("未找到", StringComparison.Ordinal);
                _copyAddressButton.Enabled = !string.IsNullOrWhiteSpace(snapshot.AddressText) &&
                    !snapshot.AddressText.StartsWith("未找到", StringComparison.Ordinal);
                _maskAddressButton.Enabled = _copyAddressButton.Enabled;
                // 保持菜单可点击，由 ShowNetworkSettings 在运行中给出“请先停止服务”提示。
                _networkSettingsMenuItem.Enabled = true;
                _sendButton.Enabled = snapshot.IsRunning;
            });
        }

        /// <summary>
        /// 手机端 control 消息分发入口，处理 PC 电源控制。
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
        /// 展开 / 收起运行日志卡。
        ///
        /// 展开态需要约 240px 可视高度，而窗口最小高度是按折叠态算出来的，
        /// 所以展开时布局会把窗口顶高；收起时则必须显式把窗口还原 ——
        /// MinimumSize 降回去并不会让 WinForms 自动缩小窗口，不还原的话
        /// 那两百多像素会永久留在窗口底部（实测留白 278px）。
        /// </summary>
        private void ToggleLogExpanded()
        {
            if (_logExpanded)
            {
                _logExpanded = false;
                RecalculateLayout();
                // 只在窗口还停在"展开把窗口顶到的高度"时才还原：用户若在展开期间
                // 手动改过高度，那是他的意图，不能覆盖。下限此时已降回折叠态，
                // 还原目标再取下限兜底，避免还原成一个装不下内容的尺寸。
                if (_heightBeforeLogExpand > 0 && Height == _heightAfterLogExpand)
                {
                    Height = Math.Max(MinimumSize.Height, _heightBeforeLogExpand);
                }

                _heightBeforeLogExpand = 0;
                _heightAfterLogExpand = 0;
                return;
            }

            _heightBeforeLogExpand = Height;
            _logExpanded = true;
            RecalculateLayout();
            // MinimumSize 只限制后续缩放，不会把已经打开的窗口自动撑高。
            // 展开日志时立即补齐实际高度，否则日志框会落到窗口客户区之外，
            // 用户只能看到空白的卡片底部，无法阅读真实运行记录。
            if (Height < MinimumSize.Height)
            {
                Height = MinimumSize.Height;
            }
            ScrollLogToTop();
            _logScrollBar.Recalculate();
            _heightAfterLogExpand = Height;
        }

        /// <summary>
        /// 刷新折叠行的三个部分：右侧「N 条 · 展开/收起」提示、级别色点、最新一行摘要。
        /// 布局与每次日志写入都会调用它 —— 条数位数变化会让提示文字变宽，位置必须重算，
        /// 否则摘要和提示会重叠。
        /// </summary>
        private void UpdateLogPreview()
        {
            var latest = _uiLogLines.First?.Value ?? string.Empty;
            _logPreviewLabel.Text = string.IsNullOrWhiteSpace(latest) ? "暂无运行记录" : latest;
            _logPreviewMarqueeOffset = 0;
            _logPreviewMarqueePauseTicks = 3;
            _logDot.ApplyColors(LogLevelColor(latest, _isDarkMode ? DarkTheme : LightTheme), _logCard.BackColor);
            _logHintLabel.Text = _uiLogLines.Count + " 条 · " + (_logExpanded ? "收起 ▾" : "展开 ▸");

            if (!_layoutReady)
            {
                // 构造函数早期 AppendLog 就可能被触发（服务启动日志），
                // 此时卡片还没定位，算出来的坐标没有意义；首趟布局会重新算。
                return;
            }

            // 用卡片右沿反推 innerRight，避免为了一个 X 坐标再往下传一个参数。
            var innerRight = _logCard.Right - CardPad;
            _logHintLabel.Location = new Point(
                innerRight - _logHintLabel.Width,
                _logLabel.Top + (_logLabel.Height - _logHintLabel.Height) / 2);

            _logDot.Visible = !_logExpanded;
            _logPreviewLabel.Visible = !_logExpanded;
            _logEmptyLabel.Visible = _logExpanded && _uiLogLines.Count == 0;
            if (_logExpanded)
            {
                return;
            }

            var dotLeft = _logLabel.Right + Gap + 4;
            _logDot.Location = new Point(
                dotLeft,
                _logLabel.Top + (_logLabel.Height - _logDot.Height) / 2);
            _logPreviewLabel.Height = _logLabel.Height;
            _logPreviewLabel.Location = new Point(_logDot.Right + 6, _logLabel.Top);
            // 摘要宽度 = 到右侧提示之间的全部剩余空间，AutoEllipsis 负责截断。
            _logPreviewLabel.Width = Math.Max(80, _logHintLabel.Left - 12 - (_logDot.Right + 6));
        }

        /// <summary>
        /// 地址框保留服务端返回的所有候选端点，但在单行展示区内用分隔符连接。
        /// 这样地址不会因为多行文本框高度不足而只显示首个端点；复制按钮和提示
        /// 仍使用原始换行文本，不改变二维码或手机连接行为。
        /// </summary>
        private static string FormatAddressForDisplay(string addressText)
        {
            if (string.IsNullOrWhiteSpace(addressText))
            {
                return string.Empty;
            }

            var lines = addressText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            return string.Join("   |   ", lines);
        }

        /// <summary>
        /// 脱敏展示：保留协议与端口，隐藏主机中可识别本机的部分。
        /// IPv4 保留前两段，域名只保留后缀，用户信息与查询串整体替换为星号。
        /// </summary>
        private static string MaskAddressForDisplay(string addressText)
        {
            if (string.IsNullOrWhiteSpace(addressText))
            {
                return string.Empty;
            }

            var lines = addressText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(MaskEndpointForDisplay);
            return string.Join("   |   ", lines);
        }

        private static string MaskEndpointForDisplay(string endpoint)
        {
            var schemeIndex = endpoint.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex < 0)
            {
                // 「未找到可用地址…」之类的提示原样保留。
                return endpoint;
            }

            var scheme = endpoint.Substring(0, schemeIndex + 3);
            var rest = endpoint.Substring(schemeIndex + 3);
            var authorityEnd = rest.IndexOfAny(new[] { '/', '?', '#' });
            var authority = authorityEnd < 0 ? rest : rest.Substring(0, authorityEnd);
            var tail = authorityEnd < 0 ? string.Empty : rest.Substring(authorityEnd);
            var queryIndex = tail.IndexOfAny(new[] { '?', '#' });
            if (queryIndex >= 0)
            {
                tail = tail.Substring(0, queryIndex) + "***";
            }

            var atIndex = authority.LastIndexOf('@');
            var userInfo = atIndex >= 0 ? "***@" : string.Empty;
            var hostPort = atIndex >= 0 ? authority.Substring(atIndex + 1) : authority;

            var host = hostPort;
            var port = string.Empty;
            if (hostPort.StartsWith("[", StringComparison.Ordinal))
            {
                var closeIndex = hostPort.IndexOf(']');
                if (closeIndex >= 0)
                {
                    host = hostPort.Substring(0, closeIndex + 1);
                    if (closeIndex + 1 < hostPort.Length)
                    {
                        port = hostPort.Substring(closeIndex + 1);
                    }
                }
            }
            else
            {
                var colonIndex = hostPort.LastIndexOf(':');
                if (colonIndex > 0)
                {
                    host = hostPort.Substring(0, colonIndex);
                    port = hostPort.Substring(colonIndex);
                }
            }

            return scheme + userInfo + MaskHostForDisplay(host) + port + tail;
        }

        private static string MaskHostForDisplay(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            var ipv4Parts = host.Split('.');
            if (ipv4Parts.Length == 4 &&
                ipv4Parts.All(part => part.Length > 0 && part.All(char.IsDigit)))
            {
                return ipv4Parts[0] + "." + ipv4Parts[1] + ".*.*";
            }

            if (host.Length > 2 && host[0] == '[' && host[host.Length - 1] == ']')
            {
                var groups = host.Substring(1, host.Length - 2).Split(':');
                return groups.Length > 1
                    ? "[" + groups[0] + ":" + string.Join(":", groups.Skip(1).Select(_ => "*")) + "]"
                    : host;
            }

            var labels = host.Split('.');
            if (labels.Length <= 1)
            {
                return "***";
            }

            return labels.Length == 2
                ? "***." + labels[1]
                : "***." + labels[labels.Length - 2] + "." + labels[labels.Length - 1];
        }

        private static void ConfigureMarqueeLabel(Label label, Font font)
        {
            label.AutoSize = false;
            label.Font = font;
            label.Padding = new Padding(10, 0, 10, 0);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = false;
            label.BackColor = LightTheme.InputSurface;
            label.ForeColor = LightTheme.TextPrimary;
            label.Tag = "field-marquee";
            label.Cursor = Cursors.IBeam;
        }

        private static void FocusMarqueeEditor(CenteredTextBox editor, Label marquee)
        {
            marquee.Visible = false;
            editor.Visible = true;
            editor.Focus();
            editor.SelectionStart = editor.TextLength;
            editor.SelectionLength = 0;
        }

        /// <summary>
        /// 失焦且超长时切换到同尺寸的只读显示层。输入框本身保留真实文本，
        /// 不依赖 WinForms 的水平滚动条实现，避免不同主题/DPI 下显示仍被裁切。
        /// </summary>
        private static void UpdateFieldMarquee(
            CenteredTextBox editor,
            Label marquee,
            ref int offset)
        {
            offset = 0;
            var text = editor.Text ?? string.Empty;
            if (editor.Focused || string.IsNullOrEmpty(text) || editor.ClientSize.Width <= 0)
            {
                marquee.Visible = false;
                editor.Visible = true;
                marquee.Text = text;
                return;
            }

            var availableWidth = Math.Max(24, marquee.ClientSize.Width - editor.Padding.Horizontal - 8);
            var textWidth = TextRenderer.MeasureText(
                text,
                marquee.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (textWidth <= availableWidth)
            {
                marquee.Visible = false;
                editor.Visible = true;
                marquee.Text = text;
                return;
            }

            marquee.Text = text;
            marquee.Visible = true;
            editor.Visible = false;
        }

        /// <summary>
        /// 更新失焦显示层的左滑文本。显示层只读，不影响编辑框中的原始值。
        /// </summary>
        private static void AdvanceTextMarquee(
            CenteredTextBox editor,
            Label marquee,
            ref int offset)
        {
            if (editor.IsDisposed || editor.Focused || !marquee.Visible || string.IsNullOrEmpty(editor.Text))
            {
                return;
            }

            var availableWidth = Math.Max(24, marquee.ClientSize.Width - editor.Padding.Horizontal - 8);
            var text = editor.Text;
            var textWidth = TextRenderer.MeasureText(
                text,
                marquee.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (textWidth <= availableWidth)
            {
                UpdateFieldMarquee(editor, marquee, ref offset);
                return;
            }

            const string separator = "   |   ";
            var cycle = text + separator + text;
            var cycleStart = text.Length + separator.Length;
            offset++;
            if (offset >= cycleStart)
            {
                offset = 0;
            }

            var start = Math.Min(offset, Math.Max(0, cycle.Length - 1));
            var display = cycle.Substring(start);
            while (display.Length > 1 && TextRenderer.MeasureText(
                       display,
                       marquee.Font,
                       new Size(int.MaxValue, int.MaxValue),
                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > availableWidth)
            {
                display = display.Substring(0, display.Length - 1);
            }

            marquee.Text = display;
        }

        /// <summary>把输入框滚回开头，避免窗口重新布局后从中间文本开始显示。</summary>
        private static void ResetTextMarquee(CenteredTextBox box)
        {
            if (!box.IsDisposed && box.IsHandleCreated)
            {
                box.ScrollToTextOffset(0);
            }
        }

        /// <summary>
        /// 日志摘要使用“正文 + 间隔 + 正文”的循环串切片，按固定节拍左移一字，
        /// 到末尾后回到开头。只有内容确实超出摘要宽度时才启动动画。
        /// </summary>
        private void AdvanceLogPreviewMarquee()
        {
            if (_logExpanded || !_logPreviewLabel.Visible || string.IsNullOrWhiteSpace(_logPreviewLabel.Text))
            {
                return;
            }

            var latest = _uiLogLines.First?.Value ?? string.Empty;
            var availableWidth = Math.Max(80, _logPreviewLabel.Width);
            var textWidth = TextRenderer.MeasureText(
                latest,
                _logPreviewLabel.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (textWidth <= availableWidth)
            {
                _logPreviewMarqueeOffset = 0;
                _logPreviewMarqueePauseTicks = 0;
                _logPreviewLabel.Text = latest;
                return;
            }

            // 每轮从完整日志开头短暂停留，让用户先读到最新记录，再开始左滑。
            if (_logPreviewMarqueePauseTicks > 0)
            {
                _logPreviewMarqueePauseTicks--;
                return;
            }

            const string separator = "   •   ";
            var cycle = latest + separator + latest;
            var cycleStart = latest.Length + separator.Length;
            _logPreviewMarqueeOffset++;
            if (_logPreviewMarqueeOffset >= cycleStart)
            {
                _logPreviewMarqueeOffset = 0;
                _logPreviewMarqueePauseTicks = 3;
            }

            var start = Math.Min(_logPreviewMarqueeOffset, Math.Max(0, cycle.Length - 1));
            var display = cycle.Substring(start);
            while (display.Length > 1 && TextRenderer.MeasureText(
                       display,
                       _logPreviewLabel.Font,
                       new Size(int.MaxValue, int.MaxValue),
                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > availableWidth)
            {
                display = display.Substring(0, display.Length - 1);
            }

            _logPreviewLabel.Text = display;
        }

        /// <summary>
        /// 从日志文本粗略推断级别，只用于折叠态那个 8px 色点。
        /// 日志本身没有结构化级别（全是拼接出来的字符串），所以只能按关键词判定；
        /// 判错只影响一个圆点的颜色，不影响任何功能，因此不必为此引入日志级别模型。
        /// </summary>
        private static Color LogLevelColor(string line, ThemeColors colors)
        {
            if (string.IsNullOrEmpty(line))
            {
                return colors.TextSecondary;
            }

            if (line.Contains("失败") || line.Contains("错误") || line.Contains("异常"))
            {
                return colors.Danger;
            }

            return colors.Info;
        }

        /// <summary>
        /// 诊断级日志出口：只落盘，不进首页活动摘要。
        /// 与 <see cref="AppendLog"/> 共用同一份诊断文件格式，所以排查问题时
        /// 文件里仍然是完整时间线，只是首页不再被底层细节淹没。
        /// </summary>
        private void AppendDetailLog(string text)
        {
            var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " " + text + Environment.NewLine;
            _diagnosticLogWriter.Enqueue(line);
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
                if (_logExpanded)
                {
                    _logScrollBar.Recalculate();
                }
                else
                {
                    _logScrollBar.Visible = false;
                }
                // 折叠态下这一行摘要是用户唯一能看到的日志内容，必须跟着刷新。
                UpdateLogPreview();
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
            return new Font(UiTokens.UiFontFamily, size, style);
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
            /// <summary>信息色：地址、连接数等「可读数据」，与品牌绿区分语义。</summary>
            public Color Info;
            /// <summary>卡片描边。比控件描边 Border 更淡，卡片的层次感交给投影承担。</summary>
            public Color CardBorder;
            /// <summary>输入框描边。介于 CardBorder 与 Border 之间的中间层。
            /// 原先用 TextBox.BorderStyle = FixedSingle，画的是系统色 WindowFrame：
            /// 既改不了、又不随主题变，深色主题下会在深底上直接消失。</summary>
            public Color InputBorder;
            /// <summary>输入框获得焦点时的描边色。TextBox 原生边框没有任何焦点反馈。</summary>
            public Color InputFocusBorder;
            /// <summary>卡片投影基色，按 alpha 叠加在页面底色上。</summary>
            public Color Shadow;
        }

        private static ThemeColors LightTheme => new ThemeColors
        {
            Background = Color.FromArgb(244, 247, 245),
            Surface = Color.FromArgb(255, 255, 255),
            SurfaceAlt = Color.FromArgb(241, 247, 244),
            InputSurface = Color.FromArgb(249, 252, 250),
            TextPrimary = Color.FromArgb(24, 45, 38),
            TextSecondary = Color.FromArgb(99, 118, 109),
            // 深 sage 绿色，确保运行状态与主按钮在浅色表面上有足够对比度。
            Success = Color.FromArgb(39, 139, 91),
            Danger = Color.FromArgb(196, 77, 77),
            Accent = Color.FromArgb(63, 112, 95),
            AccentHover = Color.FromArgb(48, 91, 76),
            Border = Color.FromArgb(208, 222, 215),
            AppsAccent = Color.FromArgb(55, 133, 116),
            Info = Color.FromArgb(45, 105, 132),
            CardBorder = Color.FromArgb(224, 234, 228),
            InputBorder = Color.FromArgb(183, 204, 194),
            InputFocusBorder = Color.FromArgb(39, 139, 91),
            Shadow = Color.FromArgb(20, 55, 41)
        };

        private static ThemeColors DarkTheme => new ThemeColors
        {
            Background = Color.FromArgb(16, 25, 22),
            Surface = Color.FromArgb(24, 38, 32),
            SurfaceAlt = Color.FromArgb(34, 52, 44),
            InputSurface = Color.FromArgb(20, 31, 26),
            TextPrimary = Color.FromArgb(239, 248, 242),
            TextSecondary = Color.FromArgb(165, 190, 177),
            Success = Color.FromArgb(84, 196, 128),
            Danger = Color.FromArgb(240, 124, 118),
            Accent = Color.FromArgb(142, 174, 161),
            AccentHover = Color.FromArgb(165, 192, 180),
            Border = Color.FromArgb(58, 82, 70),
            AppsAccent = Color.FromArgb(106, 199, 174),
            Info = Color.FromArgb(126, 178, 230),
            CardBorder = Color.FromArgb(48, 69, 59),
            InputBorder = Color.FromArgb(76, 107, 91),
            InputFocusBorder = Color.FromArgb(84, 196, 128),
            Shadow = Color.FromArgb(0, 0, 0)
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
        // 方案 B-2 的折叠/展开是纯视觉状态，不落盘、也没有对应按钮，
        // 进程外无法靠点击可靠命中那一行；与上面几条同源，暴露一个确定性入口。
        private const int WM_APP_TOGGLE_LOG = 0x8000 + 6;

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
            if (m.Msg == WM_APP_TOGGLE_LOG)
            {
                ToggleLogExpanded();
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

        private void ToggleCompactMode()
        {
            _compactMode = !_compactMode;
            if (_compactMode)
            {
                // 切入简洁视图时回到摘要态；之后用户仍可点击摘要展开完整日志。
                _logExpanded = false;
                _heightBeforeLogExpand = 0;
                _heightAfterLogExpand = 0;
            }
            _uiState.CompactMode = _compactMode;
            UpdateCompactModeMenuText();
            SaveUiState();
            RecalculateLayout();
            if (_compactMode)
            {
                var compactHeight = Math.Max(MinWindowHeightFloor, _requiredMinHeight);
                if (Height > compactHeight)
                {
                    Height = compactHeight;
                }
            }
        }

        private void UpdateCompactModeMenuText()
        {
            _compactModeMenuItem.Text = _compactMode ? "标准视图" : "简洁视图";
            _compactModeMenuItem.Checked = _compactMode;
        }

        private static bool IsMuted(Label label)
        {
            return label.Tag is string role && role == "muted";
        }

        private void ApplyTheme()
        {
            var colors = _isDarkMode ? DarkTheme : LightTheme;
            BackColor = colors.Background;

            var iconColor = colors.Accent;
            // 三张卡片统一描边与圆角，层次交给投影承担。强调竖线只保留在
            // 「服务与连接」这一张主卡上——三张卡各挂一条同款竖线时，
            // 它不承载任何区分语义，等于把主次又抹平了。
            _serviceCard.BackColor = colors.Surface;
            _serviceCard.BorderColor = colors.CardBorder;
            _serviceCard.AccentColor = colors.Success;
            _serviceCard.ShowAccent = true;
            _notificationCard.BackColor = colors.Surface;
            _notificationCard.BorderColor = colors.CardBorder;
            _notificationCard.ShowAccent = false;
            _logCard.BackColor = colors.Surface;
            _logCard.BorderColor = colors.CardBorder;
            _logCard.ShowAccent = false;

            // 输入框描边与焦点色三张卡片统一，避免只有主卡跟着主题走。
            foreach (var card in new[] { _serviceCard, _notificationCard, _logCard })
            {
                card.FieldBorderColor = colors.InputBorder;
                card.FieldFocusColor = colors.InputFocusBorder;
            }

            _serviceCard.Invalidate();
            _notificationCard.Invalidate();
            _logCard.Invalidate();

            foreach (Control? control in Controls)
            {
                if (control == null) continue;
                switch (control)
                {
                    case Label label:
                        var isFieldMarquee = label.Tag is string role && role == "field-marquee";
                        label.ForeColor = isFieldMarquee
                            ? colors.TextPrimary
                            : IsMuted(label)
                                ? colors.TextSecondary
                                : colors.TextPrimary;
                        // 卡片内的文字必须以卡片底色作背景。Label 是窗体的直接子控件，
                        // 默认继承窗体 Background，在卡片上会画出一块与卡片不同色的矩形，
                        // 深色主题下尤其明显。方案 B-3 之后所有 Label 都落在卡片或状态条上
                        // （原先露在页面底色上的页头标题已并入状态条，改由 StatusStripPanel 自绘）。
                        label.BackColor = isFieldMarquee ? colors.InputSurface : colors.Surface;
                        break;
                    case TextBox textBox:
                        textBox.BackColor = colors.InputSurface;
                        textBox.ForeColor = colors.TextPrimary;
                        break;
                    case StatusStripPanel strip:
                        // 必须排在 VisualCardPanel 分支之前：状态条派生自卡片面板，
                        // 放到后面会被那条「什么都不做」的分支吃掉，条内配色永远停在初值。
                        strip.ApplyColors(colors);
                        break;
                    case VisualCardPanel card:
                        break;
                    case StripIconButton icon:
                        // 四个图标按钮共用一套底色 / 图标色 / hover 底色 / 焦点框色，
                        // 避免条内图标各挂一套色而再次漂移。
                        icon.ApplyColors(
                            colors.Surface, colors.TextSecondary, colors.SurfaceAlt, colors.InputFocusBorder);
                        break;
                    case Button button:
                        if (button == _startButton || button == _sendButton)
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

            _iconTheme.IsDarkMode = _isDarkMode;
            _iconTheme.Invalidate();
            // 语义着色必须在通用 Label / TextBox 规则之后覆盖一遍：
            // 上面按「是否 muted」统一刷色，会把地址这类语义色抹掉。
            // 状态条不在 Controls 遍历里按 Label 规则刷色，它的语义色由自己的
            // ApplyColors + SetStatus 组合决定（见 StatusStripPanel.OnPaint）。
            _addressBox.ForeColor = colors.Info;
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
            if (_logExpanded)
            {
                _logScrollBar.Recalculate();
            }
            else
            {
                _logScrollBar.Visible = false;
            }
            // 面板沿用卡片内容底色，内部已安装/未安装分区再用轻量底色和强调线分组。
            _notificationAppsPanel.ApplyTheme(
                colors.Surface,
                colors.SurfaceAlt,
                colors.TextPrimary,
                colors.TextSecondary,
                colors.Border,
                iconColor,
                colors.AccentHover);
            // 折叠行的级别色点用的是语义色，必须在主题切换后重算一次；
            // 否则切到深色主题时它仍留着浅色主题的 Info/Danger。
            UpdateLogPreview();
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
        private class VisualCardPanel : Panel
        {
            private int _headerHeight;
            private int[] _dividerOffsets = Array.Empty<int>();

            public Color BorderColor { get; set; } = Color.FromArgb(198, 214, 206);
            public Color AccentColor { get; set; } = Color.FromArgb(111, 134, 126);
            public bool ShowAccent { get; set; } = true;
            public int CornerRadius { get; set; } = UiTokens.CardRadius;
            public Color FieldBorderColor { get; set; } = Color.FromArgb(178, 197, 190);
            public Color FieldFocusColor { get; set; } = Color.FromArgb(31, 122, 77);

            private Rectangle[] _fieldRects = Array.Empty<Rectangle>();
            private int _focusedField = -1;

            /// <summary>
            /// 登记本卡内输入框的外框矩形（卡片局部坐标）。输入框自身的描边改由
            /// 卡片统一绘制：TextBox.BorderStyle 只能画系统色 WindowFrame，
            /// 改不了色、也不随主题变，深色主题下那条深灰边框会直接看不见。
            /// 输入框已按 ApplyFieldShape 向内缩 1px 并裁成圆角，所以这条描边
            /// 不会被控件自身盖住。
            /// </summary>
            public void SetFieldLayout(params Rectangle[] fieldRects)
            {
                _fieldRects = fieldRects ?? Array.Empty<Rectangle>();
                Invalidate();
            }

            /// <summary>标记当前获得焦点的输入框序号，-1 表示无。原生 TextBox 没有焦点反馈。</summary>
            public void SetFocusedField(int index)
            {
                if (_focusedField == index)
                {
                    return;
                }

                _focusedField = index;
                Invalidate();
            }

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

                // 输入框描边。画在卡片上、输入框下：控件已向内缩 1px 并裁圆角，
                // 这一圈不会被盖住。焦点框加粗到 1.6px 并换成品牌色，作为原生
                // TextBox 缺失的焦点反馈。
                for (var i = 0; i < _fieldRects.Length; i++)
                {
                    var field = _fieldRects[i];
                    if (field.Width <= 4 || field.Height <= 4)
                    {
                        continue;
                    }

                    var focused = i == _focusedField;
                    // 外框路径取 0.5px 偏移：1px 的笔正好铺满输入框让出的那一圈像素，
                    // 落在整数坐标上会被抗锯齿摊成两行、看起来发虚。
                    using (var fieldPen = new Pen(
                        focused ? FieldFocusColor : FieldBorderColor,
                        focused ? 1.6f : 1f))
                    using (var fieldPath = RoundedPath(
                        new RectangleF(field.X + 0.5f, field.Y + 0.5f, field.Width - 1f, field.Height - 1f),
                        FieldCornerRadius))
                    {
                        e.Graphics.DrawPath(fieldPen, fieldPath);
                    }
                }
            }
        }

        /// <summary>
        /// 服务状态指示点（8px 实心圆）。
        /// 状态必须同时用「颜色 + 形状」两个通道表达：原先只把「运行中」三个字染成绿色，
        /// 对色觉障碍用户与低质量屏幕都不可靠，且扫视时抓不住。
        /// </summary>
        private sealed class StatusDot : Control
        {
            private Color _dotColor = Color.FromArgb(39, 139, 91);

            public StatusDot(string accessibleName = "服务状态指示")
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.SupportsTransparentBackColor,
                    true);
                Size = new Size(8, 8);
                TabStop = false;
                AccessibleRole = AccessibleRole.Indicator;
                AccessibleName = accessibleName;
            }

            public void ApplyColors(Color dotColor, Color surface)
            {
                _dotColor = dotColor;
                BackColor = surface;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(_dotColor))
                {
                    e.Graphics.FillEllipse(
                        brush,
                        0,
                        0,
                        Math.Max(1, Width - 1),
                        Math.Max(1, Height - 1));
                }
            }
        }

        /// <summary>顶部状态条的服务状态三态。Stopping 是点击「停止服务」到快照回来之间的过渡态。</summary>
        private enum StripState
        {
            Stopped,
            Running,
            Stopping
        }

        /// <summary>
        /// 顶部状态条（方案 B-3）：48px 高的一条状态带，取代原先「品牌 + 大标题 + 副标题」
        /// 三行页头，同时把散落三处的图标按钮收拢到条内右侧。
        ///
        /// 条内文案全部自绘而不是用 Label：左侧一段话里混了四种语义色
        /// （品牌 sage、辅助灰、成败绿/红、可读数据蓝）加一个状态点，用 5 个 AutoSize
        /// Label 拼出来要写一长串顺序定位，且文案一改就得重排一次。自绘只需在 OnPaint
        /// 里从左往右推进一个游标，每段宽度由 TextRenderer 实测，改文案不必动布局代码。
        ///
        /// 派生自 <see cref="VisualCardPanel"/> 而不是从零画：描边、圆角区域、左侧强调竖线
        /// 与三张卡片共用同一套画法，视觉上才读得出「这是同一套界面里的第四个面」。
        /// </summary>
        private sealed class StatusStripPanel : VisualCardPanel
        {
            // 字号只在本类出现，布局代码不重复。品牌与状态提高一级，形成清晰的工作台页头。
            private readonly Font _brandFont = UiFont(12F, FontStyle.Bold);
            private readonly Font _titleFont = UiFont(10F);
            private readonly Font _metricFont = UiFont(UiTokens.BodyFontSize);
            private readonly Font _statusFont = UiFont(11F, FontStyle.Bold);

            private Color _textSecondary = Color.FromArgb(101, 118, 111);
            private Color _success = Color.FromArgb(31, 122, 77);
            private Color _danger = Color.FromArgb(190, 76, 73);
            private Color _info = Color.FromArgb(44, 107, 176);

            private int _contentLeft = StripPad;
            private int _contentRight = 0;
            private int _iconDividerX = -1;
            private StripState _state = StripState.Stopped;
            private int _clientCount;

            public StatusStripPanel()
            {
                // 48px 高的条用卡片默认的 16px 圆角会吃掉三分之一的高度，收到 12。
                CornerRadius = UiTokens.StatusStripRadius;
                TabStop = false;
                AccessibleRole = AccessibleRole.StaticText;
                AccessibleName = BuildAccessibleName();
            }

            /// <summary>
            /// 登记自绘文案区（面板局部坐标）。右侧要避开图标块，由调用方算好传入。
            /// <paramref name="iconDividerX"/> 是「更多」左侧那条分隔线的横坐标，
            /// 传负数表示不画（例如图标块被挤没了的极端窄窗）。
            /// </summary>
            public void SetContentLayout(int left, int right, int iconDividerX = -1)
            {
                _contentLeft = left;
                _contentRight = right;
                _iconDividerX = iconDividerX;
                Invalidate();
            }

            public void ApplyColors(ThemeColors colors)
            {
                _textSecondary = colors.TextSecondary;
                _success = colors.Success;
                _danger = colors.Danger;
                _info = colors.Info;
                BackColor = colors.Surface;
                BorderColor = colors.CardBorder;
                // 强调竖线用品牌 sage，与服务卡那条成败绿竖线区分语义：
                // 这条说的是「我是谁」，那条说的是「服务现在什么状态」。
                AccentColor = colors.Accent;
                ShowAccent = true;
                Invalidate();
            }

            public void SetStatus(StripState state, int clientCount)
            {
                _state = state;
                _clientCount = clientCount;
                // 条内文字是自绘的，读屏软件取不到，只能把整句挂到无障碍名称上。
                AccessibleName = BuildAccessibleName();
                Invalidate();
            }

            private string BuildAccessibleName()
            {
                var status = _state == StripState.Running
                    ? "服务运行中"
                    : _state == StripState.Stopping ? "服务正在停止" : "服务未启动";
                return "TapRelay 任务通知。" + status + "，端口 " + NotifyServer.DefaultPort +
                    "，" + _clientCount + " 台手机在线。开发工具任务完成后推送到手机。";
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // 先让基类画描边、圆角区域与左侧强调竖线。
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var right = Math.Max(_contentLeft, _contentRight);
                var mid = Height / 2f;
                var x = _contentLeft;

                // 品牌字标 + 原大标题。两者连读成「TapRelay 任务通知」，
                // 18pt 的页头标题在这里降为 9.5pt —— 页头的存在意义是标题，
                // 而首屏最该被一眼看到的已经换成了右边的服务状态。
                x = DrawSegment(g, "TapRelay", _brandFont, AccentColor, x, right);
                x += Gap;
                x = DrawSegment(g, "任务通知", _titleFont, _textSecondary, x, right);

                // 一条 1px 竖线，把「我是谁」和「现在什么状态」分开。
                x += StripPad;
                if (x + 1 + StripPad <= right)
                {
                    using (var pen = new Pen(BorderColor, 1f))
                    {
                        g.DrawLine(pen, x + 0.5f, mid - 9f, x + 0.5f, mid + 9f);
                    }
                    x += 1 + StripPad;
                }

                // 状态点与状态文字同色，形成「圆点 + 文字」双通道表达，
                // 色觉障碍用户与低质量屏幕都能抓住。
                var statusColor = _state == StripState.Running
                    ? _success
                    : _state == StripState.Stopping ? _textSecondary : _danger;
                const int dotSize = 8;
                using (var brush = new SolidBrush(statusColor))
                {
                    g.FillEllipse(brush, x, mid - dotSize / 2f, dotSize, dotSize);
                }
                x += dotSize + StripIconGap;

                var statusText = _state == StripState.Running
                    ? "服务运行中"
                    : _state == StripState.Stopping ? "正在停止…" : "服务未启动";
                x = DrawSegment(g, statusText, _statusFont, statusColor, x, right);

                x += Gap;
                x = DrawSegment(
                    g, "· 端口 " + NotifyServer.DefaultPort, _metricFont, _textSecondary, x, right);

                // 连接数用信息色：它是可读数据，与「运行中」的成败语义区分开。
                // 0 台时降级为辅助灰，避免「0 台手机在线」被染成醒目的蓝色。
                x += Gap;
                var clientsText = _clientCount > 0
                    ? "· " + _clientCount + " 台手机在线"
                    : "· 暂无手机连接";
                DrawSegment(
                    g, clientsText, _metricFont,
                    _clientCount > 0 ? _info : _textSecondary, x, right);

                // 「更多」与两个带文字的高频入口之间画一条 1px 竖线：
                // 前两个是"点一下就有反馈"的动作，最后一个是弹出菜单，语义不同，
                // 靠间距区分不出来，加一条线才读得出分组。
                if (_iconDividerX > right)
                {
                    using (var pen = new Pen(BorderColor, 1f))
                    {
                        g.DrawLine(
                            pen,
                            _iconDividerX + 0.5f,
                            mid - 9f,
                            _iconDividerX + 0.5f,
                            mid + 9f);
                    }
                }
            }

            /// <summary>
            /// 从 x 起画一段文字，返回下一段的起点。
            /// 用 TextRenderer（GDI）而不是 Graphics.DrawString：前者与 Label / Button
            /// 走同一条文字渲染路径，字形粗细才与三张卡片里的文字一致；
            /// NoPadding 去掉 GDI 默认的 3px 内边距，否则每段之间会凭空多出 6px 缝隙。
            /// </summary>
            private int DrawSegment(Graphics g, string text, Font font, Color color, int x, int right)
            {
                var width = TextRenderer.MeasureText(
                    text, font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                if (x + width > right)
                {
                    // 条内文案在最小窗口宽度（800）下也远不到需要截断的程度；
                    // 真被挤到就整段不画，也不画半句让人误读。
                    return x;
                }

                TextRenderer.DrawText(
                    g,
                    text,
                    font,
                    new Point(x, (int)Math.Round((Height - font.Height) / 2f)),
                    color,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return x + width;
            }
        }

        /// <summary>
        /// 顶部状态条上的自绘按钮：默认是 28×28 纯图标方钮，带标签时变成「图标 + 文字」横排。
        /// 方案 B-3「散落图标收拢」把原先分散在三处的入口统一到状态条：
        /// 服务卡里的「二维码」、页头的主题日月、服务卡里的更多。
        /// 个人项目移入更多菜单，顶部只保留三个入口，降低扫视成本。
        /// 图标路径沿用原实现（24×24 视图盒、1.5 描边、圆头圆角），只做归一化。
        /// </summary>
        private sealed class StripIconButton : Control
        {
            public enum Glyph
            {
                Qr,
                Book,
                Theme,
                More,
                Copy,
                Mask
            }

            private readonly Glyph _glyph;
            // 有标签时按「图标 + 文字」横排；为空则退回 28x28 的纯图标方钮。
            // 顶栏图标此前只有 ToolTip，指针不悬停就读不出语义（二维码尤其容易被看成普通网格），
            // 所以高频入口补文字，低频的「更多」保持方钮并用一条竖线与它们分开。
            private readonly string _label;
            private readonly Font _labelFont = UiFont(UiTokens.BodyFontSize);
            private Color _surface = Color.White;
            private Color _iconColor = Color.FromArgb(101, 118, 111);
            private Color _hoverSurface = Color.FromArgb(240, 246, 243);
            private Color _focusRing = Color.FromArgb(31, 122, 77);
            private bool _hovered;

            private const int LabelPadLeft = 9;
            private const int LabelPadRight = 10;
            private const int LabelGlyphSize = 18;
            private const int LabelGlyphGap = 5;

            public StripIconButton(Glyph glyph, string accessibleName, string label = "")
            {
                _glyph = glyph;
                _label = label;
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.Selectable,
                    true);
                BackColor = _surface;
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = accessibleName;
                // 原先的「二维码」是标准 Button、「更多」显式设了 TabStop = true，
                // 两者都能用 Tab 走到。改成自绘后必须自己保留这一点，否则键盘用户
                // 会彻底失去这两个入口；配套的焦点框见 OnPaint。
                TabStop = true;
                Cursor = Cursors.Hand;
            }

            /// <summary>
            /// 本按钮希望的宽度：无标签时是正方形边长，有标签时由实测文字宽度算出。
            /// 顶栏布局必须用它来定宽度和计算图标块总宽，否则会按 28px 排布，
            /// 文字被后面钉住右边缘的按钮盖住。
            /// </summary>
            public int PreferredWidth => string.IsNullOrEmpty(_label)
                ? StripIconSize
                : LabelPadLeft + LabelGlyphSize + LabelGlyphGap +
                  TextRenderer.MeasureText(_label, _labelFont).Width + LabelPadRight;

            /// <summary>主题图标需要区分当前模式：浅色主题画太阳、深色主题画月亮。</summary>
            public bool IsDarkMode { get; set; }

            /// <summary>
            /// 脱敏按钮的当前状态：未脱敏画「带斜线眼睛」（提示可隐藏），
            /// 脱敏后画普通眼睛（提示可还原）。
            /// </summary>
            public bool Masked
            {
                get => _masked;
                set
                {
                    if (_masked == value) return;
                    _masked = value;
                    Invalidate();
                }
            }

            private bool _masked;

            public void ApplyColors(Color surface, Color iconColor, Color hoverSurface, Color focusRing)
            {
                _surface = surface;
                _iconColor = iconColor;
                _hoverSurface = hoverSurface;
                _focusRing = focusRing;
                BackColor = surface;
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

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                Invalidate();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                // 自绘控件不会因为 Enabled = false 自动变灰，必须自己重画。
                // 原先的「二维码」是标准 Button，服务未启动时会自动变灰；改成自绘后
                // 不补这一段，它看起来仍然可点，点了却没反应。
                base.OnEnabledChanged(e);
                Cursor = Enabled ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // 与 ThemeIconButton 一致：先清背景再自绘，避免系统按钮底色透出。
                e.Graphics.Clear(BackColor);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(_surface);

                if (_hovered && Enabled)
                {
                    // 28px 的热区在 48px 高的条上很小，hover 时铺一层圆角底才看得出点中了哪里。
                    using (var path = RoundedPath(
                        new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8))
                    using (var brush = new SolidBrush(_hoverSurface))
                    {
                        g.FillPath(brush, path);
                    }
                }

                // 键盘焦点框。自绘控件拿不到系统焦点反馈，没有它 Tab 过来根本看不出停在哪。
                // 用与输入框焦点同一档的品牌色，全应用只有这一个「你现在在这里」的语义。
                if (Focused && Enabled)
                {
                    using (var path = RoundedPath(
                        new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8))
                    using (var pen = new Pen(_focusRing, 1.6f))
                    {
                        g.DrawPath(pen, path);
                    }
                }

                // 图标色随启用状态变化：向面板底色靠 60%，得到一档明显变淡但不消失的灰。
                var color = Enabled ? _iconColor : Blend(_iconColor, _surface, 0.6f);

                var hasLabel = !string.IsNullOrEmpty(_label);
                RectangleF viewport;
                var textRect = Rectangle.Empty;
                if (hasLabel)
                {
                    viewport = new RectangleF(
                        LabelPadLeft,
                        (Height - LabelGlyphSize) / 2f,
                        LabelGlyphSize,
                        LabelGlyphSize);
                    var textLeft = LabelPadLeft + LabelGlyphSize + LabelGlyphGap;
                    textRect = new Rectangle(
                        textLeft,
                        0,
                        Math.Max(1, Width - LabelPadRight - textLeft),
                        Height);
                }
                else
                {
                    var square = Math.Min(Width, Height) * (20f / 28f);
                    viewport = new RectangleF(
                        (Width - square) / 2f,
                        (Height - square) / 2f,
                        square,
                        square);
                }

                var viewportSize = viewport.Width;
                PointF P(float x, float y) => new PointF(
                    viewport.Left + x * viewport.Width / 24f,
                    viewport.Top + y * viewport.Height / 24f);

                if (_glyph == Glyph.More)
                {
                    // 三点用填充而不是描边：1.5px 的描边画在 3px 直径的圆上会糊成一团。
                    using (var brush = new SolidBrush(color))
                    {
                        var r = viewportSize * (2f / 24f);
                        for (var i = 0; i < 3; i++)
                        {
                            var center = P(5f + i * 7f, 12f);
                            g.FillEllipse(brush, center.X - r, center.Y - r, r * 2f, r * 2f);
                        }
                    }
                }
                else
                {
                    using (var pen = new Pen(color, 1.5f * viewportSize / 24f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;

                        switch (_glyph)
                        {
                            case Glyph.Qr:
                                DrawQrIcon(g, pen, P);
                                break;
                            case Glyph.Book:
                                DrawBookIcon(g, pen, P);
                                break;
                            case Glyph.Theme:
                                if (IsDarkMode)
                                {
                                    DrawMoonIcon(g, pen, P);
                                }
                                else
                                {
                                    DrawSunIcon(g, pen, P);
                                }

                                break;
                            case Glyph.Copy:
                                DrawCopyIcon(g, pen, P);
                                break;
                            case Glyph.Mask:
                                DrawEyeIcon(g, pen, P, !Masked);
                                break;
                        }
                    }
                }

                if (hasLabel)
                {
                    TextRenderer.DrawText(
                        g,
                        _label,
                        _labelFont,
                        textRect,
                        color,
                        TextFormatFlags.Left |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix |
                        TextFormatFlags.EndEllipsis);
                }
            }

            /// <summary>
            /// 两张重叠的纸片：前页向右下错开，保留 28px 热区里的复制语义。
            /// </summary>
            private static void DrawCopyIcon(Graphics g, Pen pen, Func<float, float, PointF> p)
            {
                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddLine(p(8f, 5f), p(17f, 5f));
                    path.AddLine(p(17f, 5f), p(17f, 15f));
                    path.AddLine(p(14f, 15f), p(14f, 18f));
                    path.AddLine(p(6f, 18f), p(6f, 8f));
                    path.AddLine(p(8f, 8f), p(8f, 5f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }

                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddLine(p(10f, 8f), p(19f, 8f));
                    path.AddLine(p(19f, 8f), p(19f, 19f));
                    path.AddLine(p(10f, 19f), p(10f, 8f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
            }

            /// <summary>
            /// 脱敏开关：眼睛表示「可见」，斜线表示当前已隐藏。
            /// 眼形用两段贝塞尔闭合，中间补一个实心瞳孔，28px 下仍能读出语义。
            /// </summary>
            private static void DrawEyeIcon(
                Graphics g, Pen pen, Func<float, float, PointF> p, bool slashed)
            {
                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddBezier(p(2.5f, 12f), p(6f, 6.5f), p(18f, 6.5f), p(21.5f, 12f));
                    path.AddBezier(p(21.5f, 12f), p(18f, 17.5f), p(6f, 17.5f), p(2.5f, 12f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }

                var center = p(12f, 12f);
                var edge = p(14.2f, 12f);
                var radius = Math.Max(0.8f, edge.X - center.X);
                using (var brush = new SolidBrush(pen.Color))
                {
                    g.FillEllipse(
                        brush,
                        center.X - radius,
                        center.Y - radius,
                        radius * 2f,
                        radius * 2f);
                }

                if (slashed)
                {
                    g.DrawLine(pen, p(4.5f, 19.5f), p(19.5f, 4.5f));
                }
            }

            /// <summary>
            /// 三个「回」字定位角 + 右下数据区。二维码最可辨识的特征就是这三个角，
            /// 28px 下画满 21×21 个模块只会糊成一片灰，反而认不出来。
            /// </summary>
            private static void DrawQrIcon(Graphics g, Pen pen, Func<float, float, PointF> p)
            {
                DrawModule(g, pen, p, 2f, 2f, 7f);
                DrawModule(g, pen, p, 4.5f, 4.5f, 2f);
                DrawModule(g, pen, p, 15f, 2f, 7f);
                DrawModule(g, pen, p, 17.5f, 4.5f, 2f);
                DrawModule(g, pen, p, 2f, 15f, 7f);
                DrawModule(g, pen, p, 4.5f, 17.5f, 2f);
                DrawModule(g, pen, p, 15.2f, 15.2f, 2.4f);
                DrawModule(g, pen, p, 18.4f, 15.2f, 2.4f);
                DrawModule(g, pen, p, 15.2f, 18.4f, 2.4f);
                DrawModule(g, pen, p, 18.4f, 18.4f, 2.4f);
            }

            private static void DrawModule(
                Graphics g, Pen pen, Func<float, float, PointF> p, float x, float y, float size)
            {
                var topLeft = p(x, y);
                var bottomRight = p(x + size, y + size);
                g.DrawRectangle(
                    pen,
                    topLeft.X,
                    topLeft.Y,
                    bottomRight.X - topLeft.X,
                    bottomRight.Y - topLeft.Y);
            }

            /// <summary>翻开的书：左页脊线 → 上沿 → 右页脊线 → 下沿 → 合拢，再补一条书脊短线。</summary>
            private static void DrawBookIcon(Graphics g, Pen pen, Func<float, float, PointF> p)
            {
                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddLine(p(6f, 4f), p(12f, 7f));
                    path.AddLine(p(12f, 7f), p(18f, 4f));
                    path.AddLine(p(18f, 4f), p(18f, 20f));
                    path.AddLine(p(18f, 20f), p(12f, 17f));
                    path.AddLine(p(12f, 17f), p(6f, 20f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);

                    g.DrawLine(pen, p(12f, 7f), p(12f, 17f));
                }
            }

            private static void DrawSunIcon(Graphics g, Pen pen, Func<float, float, PointF> p)
            {
                g.DrawLine(pen, p(12f, 3f), p(12f, 5f));
                g.DrawLine(pen, p(12f, 19f), p(12f, 21f));
                g.DrawLine(pen, p(4.2f, 4.2f), p(5.6f, 5.6f));
                g.DrawLine(pen, p(18.4f, 18.4f), p(19.8f, 19.8f));
                g.DrawLine(pen, p(3f, 12f), p(5f, 12f));
                g.DrawLine(pen, p(19f, 12f), p(21f, 12f));
                g.DrawLine(pen, p(4.2f, 19.8f), p(5.6f, 18.4f));
                g.DrawLine(pen, p(18.4f, 5.6f), p(19.8f, 4.2f));

                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddBezier(p(12f, 16f), p(14.2f, 16f), p(16f, 14.2f), p(16f, 12f));
                    path.AddBezier(p(16f, 12f), p(16f, 9.8f), p(14.2f, 8f), p(12f, 8f));
                    path.AddBezier(p(12f, 8f), p(9.8f, 8f), p(8f, 9.8f), p(8f, 12f));
                    path.AddBezier(p(8f, 12f), p(8f, 14.2f), p(9.8f, 16f), p(12f, 16f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
            }

            private static void DrawMoonIcon(Graphics g, Pen pen, Func<float, float, PointF> p)
            {
                using (var path = new GraphicsPath())
                {
                    path.StartFigure();
                    path.AddBezier(p(18.5f, 15.5f), p(17.1f, 16.5f), p(15.5f, 17f), p(13.7f, 17f));
                    path.AddBezier(p(13.7f, 17f), p(9.8f, 17f), p(6.7f, 13.9f), p(6.7f, 10f));
                    path.AddBezier(p(6.7f, 10f), p(6.7f, 8.2f), p(7.3f, 6.5f), p(8.3f, 5.2f));
                    path.AddBezier(p(8.3f, 5.2f), p(5.2f, 6f), p(3f, 8.8f), p(3f, 12f));
                    path.AddBezier(p(3f, 12f), p(3f, 17f), p(7f, 21f), p(12f, 21f));
                    path.AddBezier(p(12f, 21f), p(15.2f, 21f), p(18.1f, 19.3f), p(19.7f, 16.7f));
                    path.AddBezier(p(19.7f, 16.7f), p(19.3f, 16.4f), p(18.9f, 16f), p(18.5f, 15.5f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);
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

        private class CenteredTextBox : TextBox
        {
            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, ref RECT lParam);

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int Left, Top, Right, Bottom; }

            private const int EM_SETRECT = 0x00B3;
            private const int EM_LINESCROLL = 0x00B6;
            private const int EM_SCROLLCARET = 0x00B7;
            private const int SB_HORZ = 0;
            private int _lastScrollOffset;

            [DllImport("user32.dll")]
            private static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

            public CenteredTextBox()
            {
                Multiline = true;
                HideSelection = true;
            }

            private void UpdateRect()
            {
                if (!IsHandleCreated) return;
                // 地址和测试通知都是单行字段。TextBox 使用 Multiline=true 只是为了
                // 保住固定高度，这里统一把文本区域垂直居中，避免不同 DPI 下两行
                // 出现一个居中、一个贴顶的视觉差异。
                var fontHeight = FontHeight;
                // 格式矩形是「顶部对齐」绘制单行的，Top 直接决定文字落点，
                // 所以上下留白必须对称。旧式 `(Height - fontHeight - 4) / 2`
                // 把 Top 又向上偏了 2px（实测文字比字段中心高约 2.5px，
                // 在 40px 高的地址框里肉眼可辨），这里去掉那 2px 偏移。
                var verticalPadding = Math.Max(0, (Height - fontHeight) / 2);
                var rect = new RECT
                {
                    Left = 2,
                    Top = verticalPadding,
                    Right = Width - 2,
                    // 下限保证格式矩形至少能容下一行，避免高 DPI 下把文字压扁。
                    Bottom = Math.Max(verticalPadding + fontHeight + 2, Height - verticalPadding)
                };
                SendMessage(Handle, EM_SETRECT, 0, ref rect);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                UpdateRect();
                ShowScrollBar(Handle, SB_HORZ, false);
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

            public void ScrollToTextOffset(int offset)
            {
                if (!IsHandleCreated)
                {
                    return;
                }

                var target = Math.Max(0, Math.Min(TextLength, offset));
                // 失焦的 WinForms TextBox 有时会忽略 ScrollToCaret。直接发送编辑框
                // 的横向滚动消息，确保地址和测试消息的巡航不依赖当前焦点状态。
                var delta = target == 0
                    ? -32768
                    : target - _lastScrollOffset;

                // 先把光标定位到目标字符，再滚动编辑区。原先的顺序是先
                // EM_LINESCROLL、后设置 SelectionStart，WinForms 可能在第二步
                // 重新把视口拉回起点，导致失焦时看起来没有巡航。
                SelectionStart = target;
                SelectionLength = 0;
                if (delta != 0)
                {
                    SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(delta));
                }

                _lastScrollOffset = target;
                SendMessage(Handle, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
            }

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
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
