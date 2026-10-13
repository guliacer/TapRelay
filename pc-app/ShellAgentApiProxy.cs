using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// Agent proxy that runs a CLI command to interact with an agent session.
    ///
    /// Each CLI agent (Claude Code, Codex, OpenCode, DeepSeek Harness) is configured
    /// with its binary discovery paths and command templates. None of these agents
    /// exposes a CLI "stop session" subcommand, so <see cref="StopAsync"/> is
    /// implemented with a process signal instead: it terminates the process tree
    /// that TapRelay itself launched via <see cref="SendAsync"/> (the Ctrl+C
    /// equivalent for a turn TapRelay owns).
    ///
    /// Processes are tracked per sessionId. A new send for the same session
    /// replaces (and terminates) any still-running TapRelay-started turn.
    /// </summary>
    public sealed class ShellAgentApiProxy : IAgentApiProxy
    {
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session.stop",
            "agent.probe"
        };

        private readonly ShellAgentConfig _config;
        private readonly Action<string> _log;
        private readonly Action<string> _detailLog;
        private readonly ConcurrentDictionary<string, RunningTurn> _running =
            new ConcurrentDictionary<string, RunningTurn>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim[] _sessionLocks = CreateSessionLocks();
        private readonly object _binaryLock = new object();
        private string? _resolvedBinary;
        private bool _binaryProbed;

        public string AgentKey => _config.AgentKey;
        public HashSet<string> Capabilities => Caps;

        public ShellAgentApiProxy(ShellAgentConfig config, Action<string> log, Action<string>? detailLog = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            // 可执行文件路径解析属于实现细节：成功时是噪音，失败时业务层会另发一条
            // 用户可读的告警，所以这里只写诊断文件。
            _detailLog = detailLog ?? log;
        }

        public Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
                return Task.FromResult(AgentProbeResult.NotRunning($"{_config.DisplayName} 未安装"));

            return Task.FromResult(AgentProbeResult.Up(Caps, binary));
        }

        public async Task<AgentApiResult> SendAsync(string sessionId, string text, string cwd, CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
                return AgentApiResult.Failure("NOT_INSTALLED", $"{_config.DisplayName} 未安装");

            if (string.IsNullOrWhiteSpace(sessionId))
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");

            var normalizedText = string.IsNullOrWhiteSpace(text)
                ? "查看当前任务进度，在现有基础上继续执行。"
                : text.Trim();

            var args = _config.BuildCommand(sessionId, normalizedText, cwd ?? string.Empty);

            try
            {
                var gate = GetSessionLock(sessionId);
                RunningTurn turn;
                await gate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_running.TryGetValue(sessionId, out var previous))
                    {
                        if (!await StopTurnAsync(previous))
                            return AgentApiResult.Failure("STOP_FAILED", "上一条任务未能停止，请重试");
                        _log($"{_config.AgentKey} agent.send replaced turn sessionId={sessionId} oldPid={previous.ProcessId}");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var process = Process.Start(BuildProcessStartInfo(binary, args, cwd ?? string.Empty));
                    if (process == null)
                        return AgentApiResult.Failure("PROCESS_FAILED", $"无法启动 {_config.DisplayName}");

                    turn = new RunningTurn(process);
                    // Readers must start before waiting, including for turns lasting over five seconds.
                    var stdout = ReadTailAsync(process.StandardOutput);
                    var stderr = ReadTailAsync(process.StandardError);
                    _running[sessionId] = turn;
                    turn.Completion = MonitorExitAsync(sessionId, turn, stdout, stderr);
                    _log($"{_config.AgentKey} agent.send started sessionId={sessionId} pid={turn.ProcessId}");
                }
                finally
                {
                    gate.Release();
                }

                if (await Task.WhenAny(turn.Completion, Task.Delay(5000, cancellationToken)) == turn.Completion)
                    return await turn.Completion;
                cancellationToken.ThrowIfCancellationRequested();
                return AgentApiResult.Success("已启动", $"pid={turn.ProcessId}");
            }
            catch (OperationCanceledException)
            {
                return AgentApiResult.Failure("CANCELLED", "请求已取消；已启动的任务可通过停止按钮终止");
            }
            catch (Exception ex)
            {
                _log($"{_config.AgentKey} agent.send error: {ex.Message}");
                return AgentApiResult.Failure("ERROR", $"启动异常：{ex.Message}");
            }
        }

        public async Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");

            var gate = GetSessionLock(sessionId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!_running.TryGetValue(sessionId, out var turn) || turn.Process.HasExited)
                    return AgentApiResult.Failure("NOT_RUNNING", "没有找到由 TapRelay 启动的运行中任务");
                if (!await StopTurnAsync(turn))
                    return AgentApiResult.Failure("STOP_FAILED", "任务未能停止，请重试");
                _log($"{_config.AgentKey} agent.stop terminated sessionId={sessionId} pid={turn.ProcessId}");
                return AgentApiResult.Success("已停止", $"pid={turn.ProcessId}");
            }
            catch (Exception ex)
            {
                _log($"{_config.AgentKey} agent.stop error: {ex.Message}");
                return AgentApiResult.Failure("ERROR", $"停止异常：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Build a diagnostic string from both stderr and stdout for a failed CLI invocation.
        /// Prefers stderr (the canonical error stream), then falls back to stdout tail.
        /// </summary>
        private static string BuildFailureDiagnostic(string stderr, string stdout)
        {
            var err = (stderr ?? string.Empty).Trim();
            if (err.Length > 0) return ExtractTail(err, 400);

            var tail = ExtractTail(stdout ?? string.Empty, 400);
            return tail.Length > 0 ? tail : "(no output)";
        }

        /// <summary>Truncate a string to at most maxLength characters, appending "…" when cut.</summary>
        private static string Truncate(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength - 1) + "…";
        }

        /// <summary>Return the tail of a multi-line string, up to maxLength characters.</summary>
        private static string ExtractTail(string value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            // Lines at the end are usually more informative (error messages tend to be at the bottom).
            return "…" + trimmed.Substring(trimmed.Length - maxLength);
        }

        private async Task<AgentApiResult> MonitorExitAsync(
            string sessionId, RunningTurn turn, Task<string> stdoutTask, Task<string> stderrTask)
        {
            try
            {
                await Task.Run(() => turn.Process.WaitForExit());
                await Task.WhenAll(stdoutTask, stderrTask);
                if (turn.StopRequested)
                    return AgentApiResult.Failure("INTERRUPTED", "任务已被替换或停止");
                var exitCode = turn.Process.ExitCode;
                if (exitCode == 0)
                    return AgentApiResult.Success("已完成", $"pid={turn.ProcessId}");
                var diagnostic = BuildFailureDiagnostic(stderrTask.Result, stdoutTask.Result);
                _log($"{_config.AgentKey} agent.send failed: exit={exitCode} detail={diagnostic}");
                return AgentApiResult.Failure("COMMAND_FAILED", $"{_config.DisplayName} 返回错误 (exit {exitCode})", diagnostic);
            }
            catch (Exception ex)
            {
                _log($"{_config.AgentKey} agent.send monitor error: {ex.Message}");
                return AgentApiResult.Failure("ERROR", $"任务监控异常：{ex.Message}");
            }
            finally
            {
                var gate = GetSessionLock(sessionId);
                await gate.WaitAsync();
                try
                {
                    if (_running.TryGetValue(sessionId, out var current) && ReferenceEquals(current, turn))
                        _running.TryRemove(sessionId, out _);
                    turn.Process.Dispose();
                }
                finally { gate.Release(); }
            }
        }

        private static async Task<string> ReadTailAsync(StreamReader reader)
        {
            const int retainedCharacters = 8192;
            var tail = new StringBuilder(retainedCharacters);
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > retainedCharacters)
                    tail.Remove(0, tail.Length - retainedCharacters);
            }
            return tail.ToString();
        }

        private static async Task<bool> StopTurnAsync(RunningTurn turn)
        {
            if (turn.Process.HasExited) return true;
            turn.StopRequested = true;
            KillTree(turn.Process);
            return await Task.Run(() => turn.Process.WaitForExit(5000));
        }

        // Fixed stripes keep lock storage bounded even after many distinct sessions.
        private static SemaphoreSlim[] CreateSessionLocks()
        {
            var locks = new SemaphoreSlim[64];
            for (var i = 0; i < locks.Length; i++) locks[i] = new SemaphoreSlim(1, 1);
            return locks;
        }

        private SemaphoreSlim GetSessionLock(string sessionId) =>
            _sessionLocks[(StringComparer.OrdinalIgnoreCase.GetHashCode(sessionId) & int.MaxValue) % _sessionLocks.Length];

        private sealed class RunningTurn
        {
            public Process Process { get; }
            public int ProcessId { get; }
            public volatile bool StopRequested;
            public Task<AgentApiResult> Completion { get; set; } = null!;
            public RunningTurn(Process process) { Process = process; ProcessId = process.Id; }
        }

        /// <summary>
        /// Terminates a process and its descendants. This is the Ctrl+C equivalent
        /// for a TapRelay-owned turn: the agent's session state is left in the same
        /// shape as a user interrupting the CLI.
        /// </summary>
        private static void KillTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        /// <summary>
        /// Builds a <see cref="ProcessStartInfo"/> for a resolved binary. Windows
        /// <c>.cmd</c>/<c>.bat</c> files are not PE executables and cannot be started
        /// with <c>UseShellExecute=false</c>; they must be wrapped through cmd.exe.
        /// </summary>
        internal static ProcessStartInfo BuildProcessStartInfo(string binary, string args, string cwd)
        {
            var isBatch = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (isBatch)
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                // Batch files need CALL when launched from cmd.exe. Without it,
                // cmd's /s quote normalization can turn a path containing spaces
                // into a command lookup failure (for example npm's codex.cmd).
                // Keep the batch path as one quoted argument and leave the CLI
                // arguments untouched so their own quoting remains authoritative.
                var batchPath = (binary ?? string.Empty).Replace("\"", "\"\"");
                startInfo.Arguments = "/d /c call \"" + batchPath + "\"" +
                                      (string.IsNullOrWhiteSpace(args) ? string.Empty : " " + args);
            }
            else
            {
                startInfo.FileName = binary;
                startInfo.Arguments = args;
            }

            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                startInfo.WorkingDirectory = cwd;
            }

            return startInfo;
        }

        private string? ResolveBinary()
        {
            lock (_binaryLock)
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
        }
    }

    /// <summary>
    /// Configuration for a CLI-based agent proxy.
    /// </summary>
    public sealed class ShellAgentConfig
    {
        /// <summary>Stable agent key matching the notification source.</summary>
        public string AgentKey { get; set; } = string.Empty;

        /// <summary>Human-readable display name for error messages.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Candidate binary paths to probe in order. First existing path wins.
        /// Environment variables like %LOCALAPPDATA% are expanded automatically.
        /// </summary>
        public string[] BinaryCandidates { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Build the CLI arguments for sending text to a session.
        /// Parameters: sessionId, text, cwd.
        /// </summary>
        public Func<string, string, string, string> BuildCommand { get; set; } =
            (sessionId, text, _) => $"--resume {EscapeArg(sessionId)} -p {EscapeArg(text)}";

        /// <summary>
        /// Pre-built configurations for known agents.
        /// </summary>
        public static ShellAgentConfig Claude => new ShellAgentConfig
        {
            AgentKey = "claude",
            DisplayName = "Claude Code",
            BinaryCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "npm", "claude.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "claude.cmd")
            },
            BuildCommand = (sessionId, text, _) =>
                $"--resume {EscapeArg(sessionId)} -p {EscapeArg(text)}"
        };

        public static ShellAgentConfig Codex => new ShellAgentConfig
        {
            AgentKey = "codex",
            DisplayName = "Codex CLI",
            BinaryCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "npm", "codex.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "codex.cmd")
            },
            BuildCommand = (sessionId, text, _) =>
                // `resume` starts the interactive TUI and cannot run with the
                // redirected streams used by TapRelay. `exec resume` is the
                // supported non-interactive continuation path.
                // A phone request does not carry the original project cwd. Codex
                // stores sessions globally, so --all is required to resume a
                // session created outside TapRelay's publish directory.
                $"exec resume --all --skip-git-repo-check {EscapeArg(sessionId)} {EscapeArg(text)}"
        };

        public static ShellAgentConfig OpenCode => new ShellAgentConfig
        {
            AgentKey = "opencode",
            DisplayName = "OpenCode",
            BinaryCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "npm", "opencode.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "opencode.cmd")
            },
            BuildCommand = (sessionId, text, _) =>
                // OpenCode's `session` command only manages sessions (list/delete).
                // Non-interactive continuation is provided by `run --session`.
                $"run --session {EscapeArg(sessionId)} {EscapeArg(text)}"
        };

        public static ShellAgentConfig DeepSeekHarness => new ShellAgentConfig
        {
            AgentKey = "deepseekharness",
            DisplayName = "DeepSeek Harness",
            BinaryCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "npm", "dsh.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "dsh.cmd")
            },
            BuildCommand = (sessionId, text, _) =>
                $"session {EscapeArg(sessionId)} {EscapeArg(text)}"
        };

        /// <summary>
        /// Escape a shell argument for safe passing to cmd.exe.
        /// Wraps in double quotes and escapes internal quotes.
        /// </summary>
        private static string EscapeArg(string value)
        {
            var escaped = (value ?? string.Empty).Replace("\"", "\"\"\"");
            return "\"" + escaped + "\"";
        }
    }
}
