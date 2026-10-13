using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Runtime.InteropServices;

namespace TapRelay
{
    /// <summary>
    /// Agent proxy for GUI-based agents. The proxy always brings the existing
    /// window to the foreground; agents that opt into <see cref="WindowFocusConfig.InjectText"/>
    /// also receive the phone's edited instruction in their composer and submit it.
    /// This is intentionally opt-in because different GUI agents place their
    /// composer at different coordinates.
    /// </summary>
    public sealed class WindowFocusAgentProxy : IAgentApiProxy
    {
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent.probe"
        };

        private readonly WindowFocusConfig _config;
        private readonly Action<string> _log;
        private readonly Action<string> _detailLog;
        private string? _resolvedBinary;
        private bool _binaryProbed;

        public string AgentKey => _config.AgentKey;
        public HashSet<string> Capabilities => Caps;

        public WindowFocusAgentProxy(WindowFocusConfig config, Action<string> log, Action<string>? detailLog = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            // 可执行文件探测细节只写诊断文件，首页只保留用户可读的告警。
            _detailLog = detailLog ?? log;
        }

        public Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
                return Task.FromResult(AgentProbeResult.NotRunning($"{_config.DisplayName} 未安装"));

            var running = IsProcessRunning();
            return Task.FromResult(running
                ? AgentProbeResult.Up(Caps, binary)
                : AgentProbeResult.NotRunning($"{_config.DisplayName} 未运行"));
        }

        public async Task<AgentApiResult> SendAsync(string sessionId, string text, string cwd, CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
                return AgentApiResult.Failure("NOT_INSTALLED", $"{_config.DisplayName} 未安装");

            if (string.IsNullOrWhiteSpace(sessionId))
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");

            if (!IsProcessRunning())
            {
                // Try to launch the app if a workspace/session path is available.
                if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
                {
                    return await LaunchWithWorkspaceAsync(sessionId, cwd, text, cancellationToken);
                }

                return AgentApiResult.Failure("NOT_RUNNING", $"{_config.DisplayName} 未运行，请在电脑上打开后再试");
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = binary,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                if (!_config.AvoidRelaunchWhenRunning)
                {
                    Process.Start(startInfo); // This usually focuses the existing instance
                }

                if (_config.InjectText && !string.IsNullOrWhiteSpace(text))
                {
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    var inputError = "窗口尚未就绪";
                    while (DateTime.UtcNow < deadline)
                    {
                        if (TryFocusWindow(out var windowHandle) &&
                            TryInjectText(windowHandle, sessionId, text, out inputError))
                        {
                            _log($"{_config.AgentKey} remote text submitted session={sessionId} textLength={text.Length}");
                            return AgentApiResult.Success("已提交指令", $"{_config.DisplayName} session={sessionId}");
                        }

                        await Task.Delay(250, cancellationToken);
                    }

                    _log($"{_config.AgentKey} remote text injection failed: {inputError}");
                    return AgentApiResult.Failure("INPUT_FAILED", $"无法把指令写入 {_config.DisplayName} 输入框", inputError);
                }

                await Task.Delay(300, cancellationToken);
                if (!TryFocusWindow(out _))
                {
                    _log($"{_config.AgentKey} window not found title={_config.WindowTitle}");
                    return AgentApiResult.Failure("FOCUS_FAILED", $"无法找到 {_config.DisplayName} 窗口");
                }

                _log($"{_config.AgentKey} window focused session={sessionId}");
                return AgentApiResult.Success("已激活窗口", binary);
            }
            catch (Exception ex)
            {
                _log($"{_config.AgentKey} window focus failed: {ex.Message}");
                return AgentApiResult.Failure("FOCUS_FAILED", $"无法激活窗口：{ex.Message}");
            }
        }

        public Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentApiResult.Failure(
                "NOT_SUPPORTED",
                $"{_config.DisplayName} 不支持远程停止，请在电脑上手动操作"));
        }

        private async Task<AgentApiResult> LaunchWithWorkspaceAsync(
            string sessionId,
            string cwd,
            string text,
            CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _resolvedBinary!,
                    Arguments = _config.BuildLaunchArgs(cwd),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                    WorkingDirectory = cwd
                };
                var process = Process.Start(startInfo);
                _log($"{_config.AgentKey} launched with workspace: {cwd} pid={process?.Id}");

                if (_config.InjectText && !string.IsNullOrWhiteSpace(text))
                {
                    // Electron apps often publish their process before the renderer
                    // creates a top-level window. Retry the complete focus/inject
                    // operation so startup timing cannot produce a false failure.
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    var inputError = "窗口尚未就绪";
                    while (DateTime.UtcNow < deadline)
                    {
                        if (IsProcessRunning() &&
                            TryFocusWindow(out var windowHandle) &&
                            TryInjectText(windowHandle, sessionId, text, out inputError))
                        {
                            _log($"{_config.AgentKey} launched and submitted remote text session={sessionId} textLength={text.Length}");
                            return AgentApiResult.Success("已提交指令", $"{_config.DisplayName} pid={process?.Id}");
                        }

                        await Task.Delay(250, cancellationToken);
                    }

                    var detail = string.IsNullOrWhiteSpace(inputError)
                        ? "窗口启动后仍不可用"
                        : inputError;
                    return AgentApiResult.Failure("INPUT_FAILED", $"无法把指令写入 {_config.DisplayName} 输入框", detail);
                }

                return AgentApiResult.Success("已启动", $"pid={process?.Id}");
            }
            catch (Exception ex)
            {
                return AgentApiResult.Failure("LAUNCH_FAILED", $"启动失败：{ex.Message}");
            }
        }

        private bool IsProcessRunning()
        {
            try
            {
                foreach (var name in _config.ProcessNames)
                {
                    var processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0)
                    {
                        foreach (var proc in processes) { try { proc.Dispose(); } catch { } }
                        return true;
                    }
                }
            }
            catch { /* best-effort */ }
            return false;
        }

        private string? ResolveBinary()
        {
            if (_binaryProbed) return _resolvedBinary;
            _binaryProbed = true;

            foreach (var candidate in _config.BinaryCandidates)
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(expanded))
                {
                    _resolvedBinary = expanded;
                    _detailLog($"{_config.AgentKey} binary resolved: {expanded}");
                    return expanded;
                }
            }

            _detailLog($"{_config.AgentKey} binary not found in any candidate path");
            return null;
        }

        private bool TryFocusWindow(out IntPtr windowHandle)
        {
            windowHandle = FindWindow();
            if (windowHandle == IntPtr.Zero) return false;

            try
            {
                if (NativeMethods.IsIconic(windowHandle))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
                }

                // Windows may reject SetForegroundWindow when TapRelay is not the
                // current foreground process. BringWindowToTop is still sufficient
                // for UI Automation and avoids reporting a false FOCUS_FAILED.
                return NativeMethods.SetForegroundWindow(windowHandle) ||
                       NativeMethods.BringWindowToTop(windowHandle);
            }
            catch
            {
                return false;
            }
        }

        private IntPtr FindWindow()
        {
            try
            {
                foreach (var name in _config.ProcessNames)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            var handle = process.MainWindowHandle;
                            if (handle == IntPtr.Zero) continue;
                            if (string.IsNullOrWhiteSpace(_config.WindowTitle) ||
                                process.MainWindowTitle.IndexOf(_config.WindowTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return handle;
                            }
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // Best effort; the title lookup below can still find the window when
                // process.MainWindowHandle is temporarily unavailable.
            }

            // Electron/ZCode can expose the real top-level window from a child
            // process while the parent process reports MainWindowHandle == 0.
            // Resolve the visible title directly before reporting FOCUS_FAILED.
            if (!string.IsNullOrWhiteSpace(_config.WindowTitle))
            {
                var titledWindow = NativeMethods.FindWindowByTitle(_config.WindowTitle);
                if (titledWindow != IntPtr.Zero)
                {
                    return titledWindow;
                }
            }

            return IntPtr.Zero;
        }

        private bool TryInjectText(
            IntPtr windowHandle,
            string sessionId,
            string text,
            out string error)
        {
            error = string.Empty;

            // Electron agents expose their composer through UI Automation. Set the
            // value on that real edit element first so a successful result means the
            // instruction reached the composer, not merely that SendKeys completed.
            var automationError = string.Empty;
            var useAutomationComposer =
                string.Equals(_config.AgentKey, "zcode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_config.AgentKey, "doubao", StringComparison.OrdinalIgnoreCase);
            if (useAutomationComposer &&
                TrySetAutomationComposer(windowHandle, _config.AgentKey, sessionId, text, out automationError))
            {
                return true;
            }

            // For Doubao, a coordinate fallback could silently send the instruction
            // into the wrong conversation when the requested sidebar item is not
            // available. Fail explicitly instead of reporting a false success.
            if (string.Equals(_config.AgentKey, "doubao", StringComparison.OrdinalIgnoreCase))
            {
                error = string.IsNullOrWhiteSpace(automationError)
                    ? "豆包编辑框不可用"
                    : automationError;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(automationError))
            {
                _log($"{_config.AgentKey} UI Automation input unavailable: {automationError}; using clipboard fallback");
            }

            if (!SetClipboardText(text, out error)) return false;

            if (!NativeMethods.GetWindowRect(windowHandle, out var rect))
            {
                error = "无法读取窗口位置";
                return false;
            }

            var x = rect.Left + Math.Max(1, (rect.Right - rect.Left) / 2);
            var y = rect.Bottom - Math.Max(24, _config.InputClickBottomOffset);
            if (!NativeMethods.SetCursorPos(x, y))
            {
                error = "无法定位输入框";
                return false;
            }

            NativeMethods.MouseClick();
            Thread.Sleep(120);
            try
            {
                SendKeys.SendWait("^v");
                Thread.Sleep(80);
                SendKeys.SendWait("{ENTER}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TrySetAutomationComposer(
            IntPtr windowHandle,
            string agentKey,
            string sessionId,
            string text,
            out string error)
        {
            error = string.Empty;
            try
            {
                var root = AutomationElement.FromHandle(windowHandle);
                if (string.Equals(agentKey, "doubao", StringComparison.OrdinalIgnoreCase) &&
                    !TryActivateDoubaoConversation(root, sessionId, out error))
                {
                    return false;
                }

                var edits = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                AutomationElement? target = null;
                AutomationElement? fallback = null;
                for (var index = 0; index < edits.Count; index++)
                {
                    var candidate = edits[index];
                    var current = candidate.Current;
                    if (!current.IsEnabled || current.IsOffscreen)
                    {
                        continue;
                    }

                    var name = current.Name ?? string.Empty;
                    var help = current.HelpText ?? string.Empty;
                    var className = current.ClassName ?? string.Empty;
                    var isDoubaoComposer =
                        string.Equals(agentKey, "doubao", StringComparison.OrdinalIgnoreCase) &&
                        (className.IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("发消息或按住空格说话", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         help.IndexOf("发消息或按住空格说话", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (isDoubaoComposer ||
                        name.IndexOf("提出后续修改要求", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        help.IndexOf("提出后续修改要求", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("follow-up", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        target = candidate;
                        break;
                    }

                    // Keep the last visible enabled edit as a conservative fallback;
                    // ZCode normally exposes only its composer in the main document.
                    fallback = candidate;
                }

                target ??= fallback;

                if (target == null || !target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    error = string.Equals(agentKey, "doubao", StringComparison.OrdinalIgnoreCase)
                        ? "未找到支持 ValuePattern 的豆包编辑框"
                        : "未找到支持 ValuePattern 的 ZCode 编辑框";
                    return false;
                }

                target.SetFocus();
                ((ValuePattern)pattern).SetValue(text);
                var currentValue = ((ValuePattern)target.GetCurrentPattern(ValuePattern.Pattern)).Current.Value ?? string.Empty;
                if (!string.Equals(currentValue, text, StringComparison.Ordinal))
                {
                    error = string.Equals(agentKey, "doubao", StringComparison.OrdinalIgnoreCase)
                        ? "豆包编辑框未接受完整指令"
                        : "ZCode 编辑框未接受完整指令";
                    return false;
                }

                Thread.Sleep(80);
                SendKeys.SendWait("{ENTER}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryActivateDoubaoConversation(
            AutomationElement root,
            string sessionId,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !sessionId.StartsWith("conversation_", StringComparison.OrdinalIgnoreCase))
            {
                // Older Doubao builds do not expose a conversation automation id;
                // the current window is the only safe target in that case.
                return true;
            }

            try
            {
                var links = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Hyperlink));
                for (var index = 0; index < links.Count; index++)
                {
                    var candidate = links[index];
                    var current = candidate.Current;
                    if (!string.Equals(
                            current.AutomationId,
                            sessionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var className = current.ClassName ?? string.Empty;
                    if (className.IndexOf("e2e-test-active", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }

                    if (candidate.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        Thread.Sleep(180);
                        return true;
                    }

                    error = "豆包会话不支持激活操作：" + sessionId;
                    return false;
                }

                error = "未找到豆包会话：" + sessionId;
                return false;
            }
            catch (Exception ex)
            {
                error = "激活豆包会话失败：" + ex.Message;
                return false;
            }
        }

        private static bool SetClipboardText(string text, out string error)
        {
            error = string.Empty;
            var completed = false;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    completed = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            try
            {
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(1500);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (completed) return true;
            error = failure?.Message ?? "剪贴板线程超时";
            return false;
        }

        /// <summary>
        /// Pre-built configurations for known GUI agents.
        /// </summary>
        public static WindowFocusConfig TraeCode => new WindowFocusConfig
        {
            AgentKey = "trae",
            DisplayName = "TraeCode CN",
            BinaryCandidates = new[]
            {
                @"%LOCALAPPDATA%\Programs\Trae CN\Trae CN.exe",
                @"%ProgramFiles%\Trae CN\Trae CN.exe",
                @"%LOCALAPPDATA%\Programs\TraeCode CN\TraeCode CN.exe"
            },
            WindowTitle = "Trae",
            ProcessNames = new[] { "Trae CN", "TraeCode CN" },
            BuildLaunchArgs = cwd => $"--reuse-window \"{cwd}\"",
            InjectText = true
        };

        public static WindowFocusConfig TraeWork => new WindowFocusConfig
        {
            AgentKey = "traework",
            DisplayName = "TraeWork",
            BinaryCandidates = new[]
            {
                @"%LOCALAPPDATA%\Programs\TRAE SOLO CN\TRAE SOLO CN.exe",
                @"%LOCALAPPDATA%\Programs\TraeWork CN\TraeWork CN.exe"
            },
            WindowTitle = "TraeWork",
            ProcessNames = new[] { "TRAE SOLO CN", "TraeWork CN" },
            BuildLaunchArgs = cwd => $"--reuse-window \"{cwd}\"",
            InjectText = true
        };

        public static WindowFocusConfig ClaudeHaha => new WindowFocusConfig
        {
            AgentKey = "claude-haha",
            DisplayName = "Claude Code Haha",
            BinaryCandidates = new[]
            {
                @"%ProgramFiles%\Claude Code Haha\Claude Code Haha.exe",
                @"%LOCALAPPDATA%\Programs\Claude Code Haha\Claude Code Haha.exe"
            },
            WindowTitle = "Claude Code Haha",
            ProcessNames = new[] { "Claude Code Haha" },
            BuildLaunchArgs = _ => string.Empty,
            InjectText = true
        };

        public static WindowFocusConfig ZCode => new WindowFocusConfig
        {
            AgentKey = "zcode",
            DisplayName = "ZCode",
            BinaryCandidates = new[]
            {
                @"%ProgramFiles%\ZCode\ZCode.exe",
                @"%LOCALAPPDATA%\Programs\ZCode\ZCode.exe"
            },
            WindowTitle = "ZCode",
            ProcessNames = new[] { "ZCode" },
            BuildLaunchArgs = _ => string.Empty,
            InjectText = true,
            InputClickBottomOffset = 82
        };

        public static WindowFocusConfig WorkBuddy => new WindowFocusConfig
        {
            AgentKey = "workbuddy",
            DisplayName = "WorkBuddy",
            BinaryCandidates = new[]
            {
                @"%LOCALAPPDATA%\Programs\WorkBuddy\WorkBuddy.exe",
                @"%ProgramFiles%\WorkBuddy\WorkBuddy.exe",
                @"%ProgramFiles(x86)%\WorkBuddyAI\WorkBuddyAI.exe"
            },
            WindowTitle = "WorkBuddy",
            // WorkBuddyAI（国际版）复用 workbuddy 键，进程基名为 WorkBuddyAI。
            ProcessNames = new[] { "WorkBuddy", "WorkBuddyAI" },
            BuildLaunchArgs = _ => string.Empty,
            InjectText = true
        };

        public static WindowFocusConfig Doubao => new WindowFocusConfig
        {
            AgentKey = "doubao",
            DisplayName = "豆包",
            BinaryCandidates = new[]
            {
                @"%LOCALAPPDATA%\Doubao\Application\app\Doubao.exe",
                @"%LOCALAPPDATA%\Programs\Doubao\Doubao.exe",
                @"%ProgramFiles%\Doubao\Doubao.exe"
            },
            // The desktop client uses the active conversation title, e.g.
            // "箱庭都市攻略 - 豆包", rather than a fixed application title.
            WindowTitle = "豆包",
            ProcessNames = new[] { "Doubao" },
            BuildLaunchArgs = _ => string.Empty,
            InjectText = true,
            AvoidRelaunchWhenRunning = true,
            InputClickBottomOffset = 96
        };
    }

    /// <summary>
    /// Configuration for a GUI agent proxy.
    /// </summary>
    public sealed class WindowFocusConfig
    {
        /// <summary>Stable agent key matching the notification source.</summary>
        public string AgentKey { get; set; } = string.Empty;

        /// <summary>Human-readable display name.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Candidate binary paths (expanded via Environment.ExpandEnvironmentVariables).</summary>
        public string[] BinaryCandidates { get; set; } = Array.Empty<string>();

        /// <summary>Substring to match in window titles for focus activation.</summary>
        public string WindowTitle { get; set; } = string.Empty;

        /// <summary>Process names (without .exe) for running check.</summary>
        public string[] ProcessNames { get; set; } = Array.Empty<string>();

        /// <summary>Build launch arguments when the app is not running. Parameter: cwd.</summary>
        public Func<string, string> BuildLaunchArgs { get; set; } = _ => string.Empty;

        /// <summary>Whether the adapter can interact with the GUI composer.</summary>
        public bool InjectText { get; set; }

        /// <summary>Whether a running single-instance app should be focused without starting another process.</summary>
        public bool AvoidRelaunchWhenRunning { get; set; }

        /// <summary>Distance from the bottom edge used to click the GUI composer.</summary>
        public int InputClickBottomOffset { get; set; } = 82;
    }

    internal static class NativeMethods
    {
        internal const int SW_RESTORE = 9;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        internal static IntPtr FindWindowByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((handle, lParam) =>
                {
                    var length = GetWindowTextLength(handle);
                    if (length <= 0) return true;

                    var buffer = new System.Text.StringBuilder(length + 1);
                    GetWindowText(handle, buffer, buffer.Capacity);
                    if (buffer.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = handle;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                return found;
            }

            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        internal static void MouseClick()
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
