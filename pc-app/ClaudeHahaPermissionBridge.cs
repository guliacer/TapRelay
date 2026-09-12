using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// 监听 Claude Code Haha 本地 sidecar 的权限事件，并把手机端的允许/拒绝回写过去。
    ///
    /// 合规依据：`06-通信与数据获取规范.md` R9 明确把 Haha 本地 sidecar 的
    /// <c>ws://127.0.0.1:&lt;port&gt;/ws/&lt;sessionId&gt;</c> 列为该适配器可用的公开本地接口。
    /// 因此这里既不读屏、也不模拟点击或发送按键（后者被 R9 与
    /// `11-应用识别与适配规则.md` R1 禁止）。
    ///
    /// 实测帧契约（sidecar server 模式，命令行 <c>--port</c>）：
    /// 入站 <c>permission_request</c>（requestId / toolName / toolUseId / input）、
    /// <c>permission_requests_snapshot</c>（toolRequestIds 为当前待处理集合的权威列表）；
    /// 出站 <c>permission_response</c>（requestId / allowed）。
    ///
    /// 与既有启发式（<c>TryFirePendingPermissionWaits</c>）并存：本类广播 waiting 时
    /// 用 <c>toolUseId</c> 作为 taskId，与启发式落在同一个去重键上，因此同一次审批
    /// 只会通知一次；启发式逻辑本身不改动。
    /// </summary>
    public sealed class ClaudeHahaPermissionBridge : IDisposable
    {
        private const string SidecarProcessName = "claude-sidecar-x86_64-pc-windows-msvc";
        private static readonly Regex PortPattern = new Regex(
            @"--port\s+[""']?(\d{1,5})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SdkUrlSessionPattern = new Regex(
            @"--sdk-url\s+[""']?ws://127\.0\.0\.1:(\d{1,5})/sdk/([0-9a-fA-F-]{16,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly NotifyServer _server;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<string, SessionListener> _listeners =
            new ConcurrentDictionary<string, SessionListener>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private Task? _discoveryLoop;
        private int _disposed;

        public ClaudeHahaPermissionBridge(NotifyServer server, Action<string> log)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>手机端 requestId 是否由本桥接负责。</summary>
        public static bool OwnsRequestId(string requestId) =>
            ClaudeHahaApprovalRequestId.Owns(requestId);

        public void Start()
        {
            if (_discoveryLoop != null)
            {
                return;
            }

            _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(_shutdown.Token));
        }

        /// <summary>
        /// 把手机端决策回写 sidecar。requestId 形如
        /// <c>claude-haha:&lt;sessionId&gt;:&lt;permissionRequestId&gt;</c>。
        /// </summary>
        public async Task<bool> TryResolveAsync(string requestId, bool allowed, CancellationToken cancellationToken)
        {
            if (!TryParseRequestId(requestId, out var sessionId, out var permissionRequestId))
            {
                return false;
            }

            if (!_listeners.TryGetValue(sessionId, out var listener))
            {
                _log("claude-haha permission resolve failed: 会话已不在监听中 session=" + sessionId);
                return false;
            }

            var sent = await listener.SendPermissionResponseAsync(permissionRequestId, allowed, cancellationToken);
            _log("claude-haha permission resolve session=" + sessionId +
                 " requestId=" + permissionRequestId +
                 " allowed=" + allowed +
                 " ok=" + sent);
            return sent;
        }

        internal static bool TryParseRequestId(string requestId, out string sessionId, out string permissionRequestId) =>
            ClaudeHahaApprovalRequestId.TryParse(requestId, out sessionId, out permissionRequestId);

        internal static string BuildRequestId(string sessionId, string permissionRequestId) =>
            ClaudeHahaApprovalRequestId.Build(sessionId, permissionRequestId);

        private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SyncListeners(DiscoverSessions());
                }
                catch (Exception ex)
                {
                    _log("claude-haha permission bridge discovery error: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static bool HasSidecarProcess()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(SidecarProcessName);
            }
            catch
            {
                // 枚举失败时不能直接放弃发现，交给 WMI 再判一次。
                return true;
            }

            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// 只读枚举 sidecar 进程命令行，得到 server 端口与各 cli 会话 ID。
        /// 不启动、不修改任何目标进程。
        /// </summary>
        private Dictionary<string, int> DiscoverSessions()
        {
            var sessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var serverPort = 0;
            var commandLines = new List<string>();

            // WMI 取命令行的开销远高于按名字枚举进程，而 Haha 多数时间没在运行。
            // 先用便宜的进程名探测挡一层，避免 10s 一次的空转查询常驻占 CPU。
            if (!HasSidecarProcess())
            {
                return sessions;
            }

            using (var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name LIKE '%" + SidecarProcessName + "%'"))
            {
                foreach (var item in searcher.Get())
                {
                    var commandLine = item["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        continue;
                    }

                    commandLines.Add(commandLine);
                    // server 模式行形如：... server --app-root ... --host 0.0.0.0 --port 32619
                    if (commandLine.IndexOf(" server ", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var portMatch = PortPattern.Match(commandLine);
                        if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var parsedPort))
                        {
                            serverPort = parsedPort;
                        }
                    }
                }
            }

            foreach (var commandLine in commandLines)
            {
                // cli 模式行携带 --sdk-url ws://127.0.0.1:<port>/sdk/<sessionId>，
                // 端口与会话 ID 一并给出，比单独解析 --session-id/--resume 更可靠。
                var match = SdkUrlSessionPattern.Match(commandLine);
                if (!match.Success)
                {
                    continue;
                }

                var port = int.TryParse(match.Groups[1].Value, out var sdkPort) ? sdkPort : serverPort;
                if (port <= 0)
                {
                    continue;
                }

                sessions[match.Groups[2].Value] = port;
            }

            return sessions;
        }

        private void SyncListeners(Dictionary<string, int> sessions)
        {
            foreach (var pair in sessions)
            {
                if (_listeners.ContainsKey(pair.Key))
                {
                    continue;
                }

                var listener = new SessionListener(pair.Key, pair.Value, _server, _log);
                if (_listeners.TryAdd(pair.Key, listener))
                {
                    listener.Start(_shutdown.Token);
                    _log("claude-haha permission listener attached session=" + pair.Key + " port=" + pair.Value);
                }
            }

            // 会话结束（cli 进程退出）后停止监听，避免无谓重连。
            foreach (var key in new List<string>(_listeners.Keys))
            {
                if (sessions.ContainsKey(key))
                {
                    continue;
                }

                if (_listeners.TryRemove(key, out var stale))
                {
                    stale.Dispose();
                    _log("claude-haha permission listener detached session=" + key);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                _shutdown.Cancel();
            }
            catch
            {
                // Shutdown races are not actionable.
            }

            foreach (var key in new List<string>(_listeners.Keys))
            {
                if (_listeners.TryRemove(key, out var listener))
                {
                    listener.Dispose();
                }
            }

            _shutdown.Dispose();
        }

        /// <summary>
        /// 单个会话的 sidecar 连接。只读接收权限事件，并在需要时回写一帧
        /// <c>permission_response</c>；不发送 <c>user_message</c>（续接由
        /// <see cref="ClaudeHahaAgentProxy"/> 负责）。
        /// </summary>
        private sealed class SessionListener : IDisposable
        {
            private readonly string _sessionId;
            private readonly int _port;
            private readonly NotifyServer _server;
            private readonly Action<string> _log;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);
            private ClientWebSocket? _socket;
            private CancellationTokenSource? _linked;
            private Task? _loop;
            private int _disposed;

            public SessionListener(string sessionId, int port, NotifyServer server, Action<string> log)
            {
                _sessionId = sessionId;
                _port = port;
                _server = server;
                _log = log;
            }

            public void Start(CancellationToken outerToken)
            {
                // 链接源必须留引用：否则它对 outerToken 的注册会一直挂在进程级
                // token 上，会话反复挂载/卸载后累积泄漏。
                _linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _stop.Token);
                var token = _linked.Token;
                _loop = Task.Run(() => ReceiveLoopAsync(token));
            }

            public async Task<bool> SendPermissionResponseAsync(
                string permissionRequestId,
                bool allowed,
                CancellationToken cancellationToken)
            {
                var socket = _socket;
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    return false;
                }

                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "permission_response",
                    requestId = permissionRequestId,
                    allowed
                }));

                // 取锁本身也要在 try 内：会话在手机决策抵达的同一刻结束时，
                // Dispose 与这里并发，WaitAsync 会抛 ObjectDisposedException。
                // 那属于"没送达"，必须变成 false 让手机看到真实失败，不能崩进程。
                var acquired = false;
                try
                {
                    await _sendLock.WaitAsync(cancellationToken);
                    acquired = true;
                    await socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                    return true;
                }
                catch (Exception ex)
                {
                    _log("claude-haha permission_response send failed session=" + _sessionId + ": " + ex.Message);
                    return false;
                }
                finally
                {
                    if (acquired)
                    {
                        try
                        {
                            _sendLock.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            // 与 Dispose 竞争时无需处理。
                        }
                    }
                }
            }

            private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
            {
                var backoff = TimeSpan.FromSeconds(1);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using var socket = new ClientWebSocket();
                        var uri = new Uri("ws://127.0.0.1:" + _port + "/ws/" + _sessionId);
                        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await socket.ConnectAsync(uri, connectTimeout.Token);
                        }

                        _socket = socket;
                        backoff = TimeSpan.FromSeconds(1);
                        await PumpAsync(socket, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log("claude-haha permission listener session=" + _sessionId + " reconnecting: " + ex.Message);
                    }
                    finally
                    {
                        _socket = null;
                    }

                    try
                    {
                        await Task.Delay(backoff, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    // 1s -> 2s -> 4s -> ... -> 30s，与 Android 侧退避策略同风格。
                    var next = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
                    backoff = next;
                }
            }

            private async Task PumpAsync(ClientWebSocket socket, CancellationToken cancellationToken)
            {
                var buffer = new byte[64 * 1024];
                var builder = new StringBuilder();
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    builder.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    await HandleFrameAsync(builder.ToString());
                }
            }

            private async Task HandleFrameAsync(string frame)
            {
                if (string.IsNullOrWhiteSpace(frame))
                {
                    return;
                }

                JsonElement root;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(frame);
                    root = document.RootElement;
                }
                catch (JsonException)
                {
                    return;
                }

                using (document)
                {
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String)
                    {
                        return;
                    }

                    var type = typeElement.GetString() ?? string.Empty;
                    if (!string.Equals(type, "permission_request", StringComparison.OrdinalIgnoreCase))
                    {
                        // snapshot / connected / computer_use_* 在最小实现里不产生通知：
                        // snapshot 是连接时的存量回放，直接广播会把用户早已在电脑上
                        // 处理过的历史审批重新推到手机。
                        return;
                    }

                    var requestId = ReadString(root, "requestId");
                    if (string.IsNullOrWhiteSpace(requestId) || !_announced.Add(requestId))
                    {
                        return;
                    }

                    var toolName = ReadString(root, "toolName");
                    var toolUseId = ReadString(root, "toolUseId");
                    var header = "\u7b49\u5f85\u4f60\u786e\u8ba4";
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        header += "\uff1a" + toolName;
                    }

                    var commandPreview = ReadCommandPreview(root);
                    if (!string.IsNullOrWhiteSpace(commandPreview))
                    {
                        header += " " + commandPreview;
                    }

                    // taskId 用 toolUseId：与启发式路径 (_pendingPermissionWaits) 落在
                    // 同一个去重键上，同一次审批只通知一次。sidecar 未给 toolUseId 时
                    // 退回 requestId，仍能保证自身幂等。
                    var taskId = string.IsNullOrWhiteSpace(toolUseId) ? requestId : toolUseId;

                    await _server.BroadcastFromMonitorAsync(
                        header,
                        "claude-haha",
                        "waiting",
                        taskId,
                        string.Empty,
                        0L,
                        sessionId: _sessionId,
                        requestId: BuildRequestId(_sessionId, requestId),
                        taskLog: header);
                }
            }

            private static string ReadCommandPreview(JsonElement root)
            {
                if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                foreach (var name in new[] { "command", "file_path", "path", "url", "pattern" })
                {
                    var value = ReadString(input, name);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Length <= 200 ? value : value.Substring(0, 200) + " [truncated]";
                    }
                }

                return string.Empty;
            }

            private static string ReadString(JsonElement root, string name) =>
                root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : string.Empty;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    _stop.Cancel();
                }
                catch
                {
                    // Shutdown races are not actionable.
                }

                // 只释放链接源（它持有对进程级 token 的注册）。_stop 与 _sendLock
                // 不在这里 Dispose：接收循环与 TryResolveAsync 可能仍在飞行中，
                // 提前释放会把竞争变成 ObjectDisposedException。取消已足够让
                // 循环退出，两者随对象一起被 GC 回收。
                try
                {
                    _linked?.Dispose();
                }
                catch
                {
                    // Shutdown races are not actionable.
                }
            }
        }
    }
}
