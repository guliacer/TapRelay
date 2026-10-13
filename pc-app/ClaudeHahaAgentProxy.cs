using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// Sends a follow-up prompt through Claude Code Haha's local sidecar.
    /// Haha's desktop UI uses the same /ws/{sessionId} channel and
    /// { type: "user_message", content: "..." } frame, so this avoids the
    /// unreliable clipboard/coordinate injection used by generic GUI agents.
    /// </summary>
    public sealed class ClaudeHahaAgentProxy : IAgentApiProxy
    {
        private const string DefaultEndpoint = "http://127.0.0.1:32619";
        private const string SidecarProcessName = "claude-sidecar-x86_64-pc-windows-msvc";
        private static readonly Regex SidecarPortPattern = new Regex(
            @"(?:^|\s)--port\s+[""']?(\d{1,5})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent.probe"
        };

        private readonly Action<string> _log;
        private readonly Action<string> _detailLog;
        private string? _resolvedBinary;
        private bool _binaryProbed;

        public string AgentKey => "claude-haha";
        public HashSet<string> Capabilities => Caps;

        public ClaudeHahaAgentProxy(Action<string> log, Action<string>? detailLog = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            // 可执行文件探测细节只写诊断文件，首页只保留用户可读的告警。
            _detailLog = detailLog ?? log;
        }

        public Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
            {
                return Task.FromResult(AgentProbeResult.NotRunning("Claude Code Haha 未安装"));
            }

            return Task.FromResult(IsProcessRunning()
                ? AgentProbeResult.Up(Caps, binary)
                : AgentProbeResult.NotRunning("Claude Code Haha 未运行"));
        }

        public async Task<AgentApiResult> SendAsync(
            string sessionId,
            string text,
            string cwd,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");
            }

            var binary = ResolveBinary();
            if (binary == null)
            {
                return AgentApiResult.Failure("NOT_INSTALLED", "Claude Code Haha 未安装");
            }

            if (!IsProcessRunning())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = binary,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal,
                        WorkingDirectory = Directory.Exists(cwd) ? cwd : string.Empty
                    });
                    _log("claude-haha launched for websocket session=" + sessionId);
                }
                catch (Exception ex)
                {
                    return AgentApiResult.Failure("LAUNCH_FAILED", "无法启动 Claude Code Haha", ex.Message);
                }
            }

            var prompt = string.IsNullOrWhiteSpace(text)
                ? "查看当前任务进度，在现有基础上继续执行。"
                : text.Trim();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var lastError = "未找到 Claude Code Haha sidecar 服务";

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var endpoint in GetEndpointCandidates())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attempt = await TrySendAsync(endpoint, sessionId, prompt, cancellationToken);
                    if (attempt.Accepted)
                    {
                        _log($"claude-haha websocket prompt accepted session={sessionId} endpoint={endpoint} signal={attempt.Signal} textLength={prompt.Length}");
                        return AgentApiResult.Success("已提交指令", $"Claude Code Haha websocket session={sessionId}");
                    }

                    if (!string.IsNullOrWhiteSpace(attempt.Detail))
                    {
                        lastError = attempt.Detail;
                    }
                }

                await Task.Delay(250, cancellationToken);
            }

            _log($"claude-haha websocket prompt rejected session={sessionId}: {lastError}");
            return AgentApiResult.Failure(
                "INPUT_FAILED",
                "无法把指令提交到 Claude Code Haha 当前会话",
                lastError);
        }

        public Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentApiResult.Failure(
                "NOT_SUPPORTED",
                "Claude Code Haha 不支持远程停止，请在电脑上手动操作"));
        }

        private async Task<SendAttempt> TrySendAsync(
            string endpoint,
            string sessionId,
            string prompt,
            CancellationToken cancellationToken)
        {
            using var socket = new ClientWebSocket();
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(1.5));
                await socket.ConnectAsync(BuildWebSocketUri(endpoint, sessionId), connectTimeout.Token);

                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "user_message",
                    content = prompt
                }));
                await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);

                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                while (socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveTextAsync(socket, receiveTimeout.Token);
                    if (message == null)
                    {
                        break;
                    }

                    if (!TryParse(message, out var root))
                    {
                        continue;
                    }

                    if (IsUserMessageReplay(root, prompt))
                    {
                        return SendAttempt.Success("user_message_replay");
                    }

                    if (IsAcceptedStartSignal(root))
                    {
                        return SendAttempt.Success(ReadAcceptanceSignal(root));
                    }

                    if (IsError(root, out var error))
                    {
                        return SendAttempt.Rejected(error);
                    }
                }

                return SendAttempt.Rejected("sidecar 未返回消息已接受或会话开始运行的确认");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SendAttempt.Rejected("sidecar 连接或回执等待超时");
            }
            catch (WebSocketException ex)
            {
                return SendAttempt.Rejected("sidecar WebSocket 连接失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return SendAttempt.Rejected(ex.Message);
            }
        }

        private static Uri BuildWebSocketUri(string endpoint, string sessionId)
        {
            var baseUri = new Uri(endpoint, UriKind.Absolute);
            var scheme = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";
            return new Uri(
                scheme + "://" + baseUri.Authority + "/ws/" + Uri.EscapeDataString(sessionId));
        }

        private static async Task<string?> ReceiveTextAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > 1024 * 1024)
                {
                    return null;
                }

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }

        private static bool TryParse(string message, out JsonElement root)
        {
            root = default;
            try
            {
                using var document = JsonDocument.Parse(message);
                root = document.RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUserMessageReplay(JsonElement root, string prompt)
        {
            if (!ReadString(root, "type").Equals("user_message_replay", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (root.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                !string.Equals(content.GetString(), prompt, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool IsAcceptedStartSignal(JsonElement root)
        {
            var type = ReadString(root, "type");
            if (type.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var state = ReadString(root, "state");
                return state.Equals("thinking", StringComparison.OrdinalIgnoreCase) ||
                       state.Equals("streaming", StringComparison.OrdinalIgnoreCase) ||
                       state.Equals("tool_use", StringComparison.OrdinalIgnoreCase) ||
                       state.Equals("compacting", StringComparison.OrdinalIgnoreCase);
            }

            if (!type.Equals("system_notification", StringComparison.OrdinalIgnoreCase) ||
                !ReadString(root, "subtype").Equals("session_state_changed", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return ReadString(data, "state").Equals("running", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadAcceptanceSignal(JsonElement root)
        {
            var type = ReadString(root, "type");
            if (type.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                return "status." + ReadString(root, "state");
            }

            return "system_notification.session_state_changed";
        }

        private static bool IsError(JsonElement root, out string error)
        {
            error = string.Empty;
            if (!ReadString(root, "type").Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            error = ReadString(root, "message");
            return true;
        }

        private static string ReadString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static IEnumerable<string> GetEndpointCandidates()
        {
            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DefaultEndpoint
            };

            // The app chooses the server port at startup. Reading the sidecar's
            // own command line avoids relying on the currently observed default.
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name LIKE 'claude-sidecar-%'");
                using var results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        var commandLine = item["CommandLine"]?.ToString() ?? string.Empty;
                        if (!Regex.IsMatch(commandLine, @"(?:^|\s)server(?:\s|$)", RegexOptions.IgnoreCase))
                        {
                            continue;
                        }

                        var match = SidecarPortPattern.Match(commandLine);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
                        {
                            AddPort(endpoints, port);
                        }
                    }
                }
            }
            catch
            {
                // Listener enumeration below remains available when WMI is restricted.
            }

            try
            {
                foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (listener.Port < 1024 ||
                        (!IPAddress.IsLoopback(listener.Address) &&
                         !listener.Address.Equals(IPAddress.Any) &&
                         !listener.Address.Equals(IPAddress.IPv6Any)))
                    {
                        continue;
                    }

                    AddPort(endpoints, listener.Port);
                }
            }
            catch
            {
                // The fixed default and WMI-derived endpoint are sufficient in the usual case.
            }

            var ordered = new List<string>(endpoints);
            ordered.Sort(StringComparer.OrdinalIgnoreCase.Compare);
            ordered.Remove(DefaultEndpoint);
            ordered.Insert(0, DefaultEndpoint);
            return ordered;
        }

        private static void AddPort(HashSet<string> endpoints, int port)
        {
            if (port >= 1 && port <= 65535)
            {
                endpoints.Add("http://127.0.0.1:" + port);
            }
        }

        private bool IsProcessRunning()
        {
            try
            {
                foreach (var processName in new[] { "Claude Code Haha", SidecarProcessName })
                {
                    var processes = Process.GetProcessesByName(processName);
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                return true;
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
                // Best effort probe; binary resolution still distinguishes installed from absent.
            }

            return false;
        }

        private string? ResolveBinary()
        {
            if (_binaryProbed)
            {
                return _resolvedBinary;
            }

            _binaryProbed = true;
            foreach (var candidate in new[]
            {
                @"%ProgramFiles%\Claude Code Haha\Claude Code Haha.exe",
                @"%LOCALAPPDATA%\Programs\Claude Code Haha\Claude Code Haha.exe"
            })
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(expanded))
                {
                    _resolvedBinary = expanded;
                    _detailLog("claude-haha binary resolved: " + expanded);
                    return expanded;
                }
            }

            _detailLog("claude-haha binary not found in any candidate path");
            return null;
        }

        private sealed class SendAttempt
        {
            public bool Accepted { get; private set; }
            public string Signal { get; private set; } = string.Empty;
            public string Detail { get; private set; } = string.Empty;

            public static SendAttempt Success(string signal)
            {
                return new SendAttempt { Accepted = true, Signal = signal };
            }

            public static SendAttempt Rejected(string detail)
            {
                return new SendAttempt { Detail = detail };
            }
        }
    }
}
