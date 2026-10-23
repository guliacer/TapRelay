using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TapRelay
{
    public sealed class NotifyServer : IDisposable
    {
        public const int DefaultPort = NotifyServerConfig.DefaultPort;
        private const int MaxMessageLength = 500;
        private const int MaxSourceLength = 40;
        private const int MaxEventBodyBytes = 64 * 1024;
        private const int MaxSendBodyBytes = 512 * 1024;
        private const int MaxRegistrationBodyBytes = 2 * 1024 * 1024;
        private const int MaxWebSocketMessageBytes = 512 * 1024;
        private const string WorkBuddyCompatibilityAppKey = "workbuddy-remote";

        // Claude 完成事件的 taskId 形如 <sessionId>:<sha256>，长度 36+1+64=101。
        // 上限必须大于该值，否则 HTTP 路径截断后与 BroadcastFromMonitorAsync 的
        // 完整值不一致，去重 key 不同会让同一次完成事件双发。
        private const int MaxTaskIdLength = 200;
        private const string HeartbeatType = "heartbeat";
        private const string HeartbeatAckPayload = "{\"type\":\"heartbeat_ack\"}";
        private const int StopGracePeriodMilliseconds = 300;

        // 停止流程的硬上限。通用主机的 StopAsync 并不完整遵守传入的 CancellationToken：
        // 它在等待 ApplicationLifetime 的停止信号与托管服务停止时是"无超时"的。
        // 实测（2026-10-12，本机）：手机客户端 0 个、监听端口约 667ms 就已释放，
        // 但这个 await 永不返回 —— 诊断日志停在"开始停止服务"，
        // 既没有"服务优雅停止超时"也没有"服务已停止"，进程滞留 130MB+ 不退。
        // 用户看到的现象就是"软件退出了但端口还被占着"：占着端口的是那个没退干净的进程。
        // 因此不再信任单个 CancellationToken，用 WhenAny 兜住上限，到点就强制释放。
        private const int StopHardCeilingMilliseconds = 1000;

        // host.Dispose() 会逐个释放 DI 单例，其中任何一个卡住都会把进程留在这里。
        // 同样设上限：清理不完整无所谓，进程马上退出，资源由系统回收。
        private const int HostDisposeCeilingMilliseconds = 400;

        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new ConcurrentDictionary<Guid, WebSocket>();
        private readonly ConditionalWeakTable<WebSocket, WebSocketSendChannel> _sendChannels =
            new ConditionalWeakTable<WebSocket, WebSocketSendChannel>();
        private readonly object _eventDedupLock = new object();
        private readonly object _logFileLock = new object();
        private readonly string _diagnosticLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "pc-server.log");
        private readonly Dictionary<string, DateTimeOffset> _recentEvents = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals =
            new ConcurrentDictionary<string, PendingApproval>(StringComparer.Ordinal);
        private IHost? _host;
        private int _appRegistryRefreshScheduled;
        private NotifyServerConfig _config;
        private readonly ComfyUiConsoleHost _comfyUiConsole = new ComfyUiConsoleHost();

        public NotifyServer(NotifyServerConfig? config = null)
        {
            _config = (config ?? new NotifyServerConfig()).Normalize();
        }

        public NotifyServerConfig Config => _config.Clone();

        public void ApplyConfig(NotifyServerConfig config)
        {
            if (_host != null)
            {
                throw new InvalidOperationException("请先停止服务，再修改网络设置。");
            }

            _config = (config ?? throw new ArgumentNullException(nameof(config))).Normalize();
        }

        // 用户可禁用的通知来源。null 表示尚未同步（默认全部放行）；非 null 时，
        // 属于该集合的已知来源才允许推送；黑名单来源由 UnsupportedAppRegistry 单独返回原因。
        private volatile HashSet<string>? _enabledSources;
        private readonly object _enabledSourcesLock = new object();

        /// <summary>
        /// 通知门控在运行期自动启用新安装来源后触发（参数为来源 key）。
        /// MainForm 订阅后重跑面板自动检测，让启用集与 UI 收敛到新状态。
        /// </summary>
        public event Action<string>? SourceAutoEnabled;

        // 与 NotificationAppsPanel.NotifiableAppKeys 保持一致：只有这些来源受启用/禁用开关约束。
        // taprelay 自身消息、空来源与未知来源始终放行。
        // Upload sources share the parent application's enable switch.
        private static readonly HashSet<string> GatedSources =
            new HashSet<string>(AppCatalogMetadata.BuiltInKeys, StringComparer.OrdinalIgnoreCase)
            {
                "quarknetdisk-upload",
                "adrive-upload"
            };

        public event Action<ServerSnapshot>? SnapshotChanged;
        public event Action<string>? LogLine;

        /// <summary>
        /// 只对诊断有价值的日志（协议回执、心跳确认等），不会进入首页活动摘要，
        /// 仅写入诊断文件。原先这些内容靠上层按文案前缀过滤，一旦新增回执类型就会漏；
        /// 改为在发出点决定通道，新增回执只要走 <see cref="LogDetail"/> 就不会污染首页。
        /// </summary>
        public event Action<string>? DetailLogLine;

        public event Action<IReadOnlyList<RegisteredAppDefinition>>? AppsChanged;

        /// <summary>
        /// Fires after every successful broadcast. Subscribers (e.g. relay bridge)
        /// receive the raw JSON that was sent to WebSocket clients.
        /// The string is the serialized broadcast payload.
        /// </summary>
        public event Action<string>? OutboundBroadcast;

        /// <summary>
        /// Handles control requests sent by the mobile client. The handler returns
        /// a JSON-serializable response that is sent only to the requesting socket.
        /// </summary>
        public Func<JsonElement, CancellationToken, Task<object?>>? ControlRequestHandler { get; set; }

        /// <summary>
        /// Claude Code Haha 权限审批的远程回写入口（requestId, allowed, ct）。
        /// 由 <see cref="ClaudeHahaPermissionBridge"/> 在启动时注入；未注入时
        /// approval.resolve 对 claude-haha 请求返回真实失败，不伪造"已发送"。
        /// </summary>
        public Func<string, bool, CancellationToken, Task<bool>>? ClaudeHahaApprovalResolver { get; set; }

        /// <summary>
        /// Processes a raw JSON message from an external client (relay bridge).
        /// Handles control messages (approval, heartbeat) and returns a JSON-serializable
        /// response that should be sent back to the requesting client.
        /// Returns null if no response is needed or the message was not actionable.
        /// </summary>
        public async Task<object?> ProcessExternalClientMessageAsync(string json, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var type = typeElement.GetString() ?? string.Empty;
                if (string.Equals(type, HeartbeatType, StringComparison.Ordinal))
                {
                    return new { type = "heartbeat_ack" };
                }

                // ── Agent API RPC: universal remote agent control ──
                if (string.Equals(type, "agent.probe", StringComparison.Ordinal))
                {
                    return await HandleAgentProbeAsync(doc.RootElement, cancellationToken);
                }

                if (string.Equals(type, "agent.call", StringComparison.Ordinal))
                {
                    Log("收到手机 agent.call：agentKey=" +
                        ReadOptionalString(doc.RootElement, "agentKey", 40) +
                        " method=" + ReadOptionalString(doc.RootElement, "method", 40) +
                        " sessionId=" + ReadOptionalString(doc.RootElement, "sessionId", 160));
                    return await HandleAgentCallAsync(doc.RootElement, cancellationToken);
                }

                if (!string.Equals(type, "control", StringComparison.Ordinal))
                {
                    return null;
                }

                var action = doc.RootElement.TryGetProperty("action", out var actionElement) &&
                             actionElement.ValueKind == JsonValueKind.String
                    ? actionElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.Equals(action, "approval.resolve", StringComparison.OrdinalIgnoreCase))
                {
                    var requestId = ReadOptionalString(doc.RootElement, "requestId", 100);
                    var decision = (ReadOptionalString(doc.RootElement, "decision", 20) ?? string.Empty).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(requestId) ||
                        (decision != "allow" && decision != "deny"))
                    {
                        return new
                        {
                            type = "control_result",
                            action = "approval.resolve",
                            requestId,
                            succeeded = false,
                            error = "requestId and decision allow|deny are required"
                        };
                    }

                    // WorkBuddy 等待确认（批量删除/权限/问题确认）：requestId 形如
                    // workbuddy:<sessionId>。手机端「允许 / 拒绝」直接在此远程确认——
                    // 将 WorkBuddy 主窗口置前并发送 Enter（允许）/ Esc（拒绝）。
                    if (requestId.StartsWith("workbuddy:", StringComparison.OrdinalIgnoreCase) ||
                        requestId.StartsWith("workbuddy-ai:", StringComparison.OrdinalIgnoreCase))
                    {
                        var isWorkBuddyAi = requestId.StartsWith("workbuddy-ai:", StringComparison.OrdinalIgnoreCase);
                        var prefixLength = isWorkBuddyAi ? "workbuddy-ai:".Length : "workbuddy:".Length;
                        var sessionId = requestId.Substring(prefixLength).Trim();
                        var resolved = ResolveWorkBuddyWaiting(sessionId, decision, isWorkBuddyAi);
                        Log((isWorkBuddyAi ? "workbuddy-ai" : "workbuddy") +
                            " approval resolved by remote client: session=" + sessionId +
                            " decision=" + decision + " ok=" + resolved);
                        return new
                        {
                            type = "control_result",
                            action = "approval.resolve",
                            requestId,
                            decision,
                            succeeded = resolved,
                            error = resolved ? string.Empty : "WorkBuddy 窗口不可用，请在电脑上确认"
                        };
                    }

                    // Claude Code Haha 权限审批：requestId 形如
                    // claude-haha:<sessionId>:<permissionRequestId>。这些请求不经过
                    // _pendingApprovals（它只装 HTTP /approval 发起的等待），而是回写
                    // 到 Haha 本地 sidecar 的 permission_response 帧（06-R9 白名单接口）。
                    if (ClaudeHahaPermissionBridge.OwnsRequestId(requestId))
                    {
                        var resolver = ClaudeHahaApprovalResolver;
                        if (resolver == null)
                        {
                            return new
                            {
                                type = "control_result",
                                action = "approval.resolve",
                                requestId,
                                decision,
                                succeeded = false,
                                error = "Haha 权限通道未就绪，请在电脑上确认"
                            };
                        }

                        var accepted = await resolver(
                            requestId,
                            string.Equals(decision, "allow", StringComparison.Ordinal),
                            cancellationToken);
                        Log("claude-haha approval resolved by remote client: " + requestId +
                            " decision=" + decision + " ok=" + accepted);
                        return new
                        {
                            type = "control_result",
                            action = "approval.resolve",
                            requestId,
                            decision,
                            succeeded = accepted,
                            error = accepted ? string.Empty : "Haha 会话已结束或连接断开，请在电脑上确认"
                        };
                    }

                    if (!_pendingApprovals.TryGetValue(requestId, out var pending) ||
                        !pending.Completion.TrySetResult(decision))
                    {
                        return new
                        {
                            type = "control_result",
                            action = "approval.resolve",
                            requestId,
                            succeeded = false,
                            error = "approval request is missing or already resolved"
                        };
                    }

                    Log("approval resolved by remote client: " + requestId + " decision=" + decision);
                    await Task.Yield();
                    return new
                    {
                        type = "control_result",
                        action = "approval.resolve",
                        requestId,
                        decision,
                        succeeded = true
                    };
                }

                if (ControlRequestHandler != null)
                {
                    return await ControlRequestHandler(doc.RootElement.Clone(), cancellationToken);
                }

                return new
                {
                    type = "control_result",
                    action,
                    succeeded = false,
                    error = "control handler unavailable"
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 按 Claude 会话 ID 查询该会话已记忆的真实工作目录。
        /// Claude Code 的 Hook 事件只带启动目录（例如 C:\Users\Admin），
        /// 用户 cd 之后的实际项目目录只存在于转录解析器的会话状态里，
        /// 因此 /send 需要回查它才能得出正确的项目名。
        /// 返回空字符串表示该会话没有可用的记忆目录。
        /// </summary>
        public Func<string, string>? ClaudeDirectoryResolver { get; set; }

        /// <summary>
        /// 同步当前启用的通知来源集合（来自 NotificationAppsPanel）。被禁用的已知来源将不再推送。
        /// </summary>
        public void SetEnabledSources(IReadOnlyList<string> enabledKeys)
        {
            _enabledSources = new HashSet<string>(enabledKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public void NotifyAppsChanged()
        {
            AppsChanged?.Invoke(NotificationAppRegistry.GetAll());
            ScheduleAppRegistryRefresh();
        }

        /// <summary>
        /// Older mobile builds only fetch /apps during WebSocket connection setup.
        /// Ask connected clients to reconnect after the control response has had
        /// time to reach the caller, keeping this compatible with those builds.
        /// </summary>
        private void ScheduleAppRegistryRefresh()
        {
            if (Interlocked.Exchange(ref _appRegistryRefreshScheduled, 1) != 0)
            {
                return;
            }

            _ = RefreshAppRegistryClientsAsync();
        }

        private async Task RefreshAppRegistryClientsAsync()
        {
            try
            {
                await Task.Delay(300).ConfigureAwait(false);
                await Task.WhenAll(_clients.ToArray().Select(async item =>
                {
                    var socket = item.Value;
                    if (socket.State != WebSocketState.Open) return;
                    try
                    {
                        using var closeToken = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await GetSendChannel(socket).ExecuteAsync(token => socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "app registry updated",
                                token), closeToken.Token).ConfigureAwait(false);
                        Log("requested mobile app registry refresh client=" + item.Key);
                    }
                    catch (Exception error)
                    {
                        _clients.TryRemove(item.Key, out _);
                        Log("mobile app registry refresh failed client=" + item.Key + " error=" + error.Message);
                    }
                })).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Log("mobile app registry refresh scheduling failed: " + error.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _appRegistryRefreshScheduled, 0);
            }
        }

        private bool IsSourceEnabled(string source)
        {
            if (UnsupportedAppRegistry.IsUnsupported(source))
            {
                return false;
            }

            var enabled = _enabledSources;
            if (enabled == null || string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            var key = source.Trim();
            // 面板开关只有 quarknetdisk 一项；上传来源共用该开关。
            if (string.Equals(key, "quarknetdisk-upload", StringComparison.OrdinalIgnoreCase))
            {
                key = "quarknetdisk";
            }
            if (string.Equals(key, "adrive-upload", StringComparison.OrdinalIgnoreCase))
            {
                key = "adrive";
            }
            return (!GatedSources.Contains(key) && !NotificationAppRegistry.Contains(key)) || enabled.Contains(key);
        }

        /// <summary>
        /// 启用集是启动/上次自动检测时的快照：运行期间新安装的应用会先产生日志事件，
        /// 再等用户手动检测才被启用，期间的完成通知会被陈旧快照丢弃（2026-08-21
        /// zcode 重装后完成事件被拦截的案例）。门控拦截时按面板同一套"当前安装证据"
        /// 标准复核一次：证据成立且用户未手动禁用则自动放行并补录启用集；证据不成立
        /// （如卸载后残留日志）仍按原逻辑丢弃。
        /// </summary>
        private bool TryAutoEnableNewlyInstalledSource(string source)
        {
            var key = (source ?? string.Empty).Trim();
            if (key.Length == 0 || !GatedSources.Contains(key) ||
                NotificationAppsPanel.IsUserDisabled(key) ||
                !NotificationAppsPanel.HasInstallEvidence(key))
            {
                return false;
            }

            lock (_enabledSourcesLock)
            {
                var current = _enabledSources;
                if (current == null)
                {
                    return true;
                }
                if (!current.Contains(key))
                {
                    var updated = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase) { key };
                    _enabledSources = updated;
                }
            }

            Log("检测到 [" + key + "] 已在本机安装，自动启用通知来源");
            SourceAutoEnabled?.Invoke(key);
            return true;
        }

        public async Task StartAsync()
        {
            if (_host != null)
            {
                return;
            }

            _config = _config.Normalize();

            // 铁律 R2（端口固定 1122）在配置入口强制：见 NetworkSettingsStore.PinPort。
            // 服务本身只按给定配置监听 —— 把「固定端口」写在这里会让任何自定义端口的
            // 调用方（含测试）静默连到 1122 上，既掩盖配置错误也会和运行中的实例互抢端口。
            var listenAddress = IPAddress.Parse(_config.ListenAddress);
            var listenPort = _config.ListenPort;

            // 预检：端口是否被占用。占用则直接给出明确提示并启动失败，绝不漂移、不切换。
            try
            {
                var probe = new TcpListener(listenAddress, listenPort);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new InvalidOperationException(
                    "端口 " + listenPort + " 已被占用，无法启动 TapRelay 服务。请先关闭占用该端口的程序（常见为另一个 TapRelay 实例），再重新启动。",
                    ex);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    "端口 " + listenPort + " 不可用（" + ex.SocketErrorCode + "），无法启动 TapRelay 服务。",
                    ex);
            }

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel(options =>
                    {
                        options.Listen(listenAddress, listenPort, listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1;
                        });
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWebSockets(new WebSocketOptions
                        {
                            KeepAliveInterval = TimeSpan.FromSeconds(30)
                        });
                        app.Run(HandleRequestAsync);
                    });
                })
                .Build();

            try
            {
                await _host.StartAsync();
                try
                {
                    await _comfyUiConsole.StartAsync(Log);
                    Log("ComfyUI 手机控制台已接入 /comfy（内部服务仅监听本机回环地址）");
                }
                catch (Exception error)
                {
                    Log("ComfyUI 手机控制台启动失败：" + error.Message);
                }
                Log("服务已启动：http://localhost:" + listenPort + "/send，监听 " + _config.ListenAddress);
                PublishSnapshot();
            }
            catch
            {
                _host.Dispose();
                _host = null;
                PublishSnapshot();
                throw;
            }
        }

        public async Task StopAsync()
        {
            var host = _host;
            if (host == null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var abortedClients = AbortConnectedClients();
            Log("开始停止服务，已立即中止 " + abortedClients + " 个手机客户端连接");
            await _comfyUiConsole.StopAsync();
            try
            {
                // 用户主动停止时不等待 WebSocket close handshake。后台被冻结的手机
                // 无法响应 CloseAsync，原先会先耗尽每个客户端的超时，再等待 Kestrel。
                // Abort 会立刻取消服务端的 ReceiveAsync，让监听端口尽快释放。
                //
                // stopToken 只覆盖托管服务；通用主机在等 ApplicationLifetime 的停止
                // 信号时完全不看它，所以真正兜底的是下面的 WhenAny 硬上限。
                using var stopToken = new CancellationTokenSource(StopGracePeriodMilliseconds);
                var gracefulStop = host.StopAsync(stopToken.Token);
                var winner = await Task.WhenAny(gracefulStop, Task.Delay(StopHardCeilingMilliseconds))
                    .ConfigureAwait(false);
                if (ReferenceEquals(winner, gracefulStop))
                {
                    // 正常路径：让异常照旧冒到下面的 catch，保持既有日志行为。
                    await gracefulStop.ConfigureAwait(false);
                }
                else
                {
                    Log("服务优雅停止超过硬上限，直接强制释放（" + StopHardCeilingMilliseconds + "ms）");
                }
            }
            catch (OperationCanceledException)
            {
                Log("服务优雅停止超时，改为强制释放（" + StopGracePeriodMilliseconds + "ms）");
            }
            catch (Exception error)
            {
                Log("服务停止时遇到异常，改为强制释放：" + error.Message);
            }
            finally
            {
                try
                {
                    // 放到线程池并设上限：Dispose 卡住时只泄漏一个线程，
                    // 不能让它把整个退出流程拖在这里。
                    var dispose = Task.Run(() => host.Dispose());
                    var disposed = await Task.WhenAny(dispose, Task.Delay(HostDisposeCeilingMilliseconds))
                        .ConfigureAwait(false);
                    if (ReferenceEquals(disposed, dispose))
                    {
                        // 让 Dispose 自身的异常冒到下面的 catch，保持既有日志行为。
                        await dispose.ConfigureAwait(false);
                    }
                    else
                    {
                        Log("服务资源释放超过上限，放弃等待（" + HostDisposeCeilingMilliseconds + "ms）");
                    }
                }
                catch (Exception error)
                {
                    Log("服务资源释放异常：" + error.Message);
                }
                finally
                {
                    if (ReferenceEquals(_host, host))
                    {
                        _host = null;
                    }
                    stopwatch.Stop();
                    Log("服务已停止，耗时 " + stopwatch.ElapsedMilliseconds + "ms");
                    PublishSnapshot();
                }
            }
        }

        public async Task BroadcastFromAppAsync(string rawMessage)
        {
            var message = NormalizeMessage(rawMessage);
            // 标记 source=taprelay，让 APP 端识别为 TapRelay 项目自身分组（Channel.TapRelay），
            // 不再落入 Unknown。taskId 留空：测试消息无需去重，与外部 Agent 完成事件区分。
            await BroadcastAsync(CreatePayload(
                message,
                "taprelay",
                "completed",
                string.Empty,
                string.Empty,
                0L,
                string.Empty,
                string.Empty,
                string.Empty));
            Log("已发送测试消息：" + message);
            PublishSnapshot();
        }

        public async Task BroadcastFromMonitorAsync(
            string rawMessage,
            string source,
            string status,
            string taskId,
            string cwd,
            long durationMs,
            string projectName = "",
            string notificationTitle = "",
            string actionUrl = "",
            long tokenCount = 0L,
            string sessionId = "",
            string credits = "",
            string requestId = "",
            string taskLog = "")
        {
            var message = NormalizeMessage(rawMessage);
            source = NormalizeSource(source);
            // Monitor adapters must only publish terminal task states. Keep this
            // guard at the shared broadcast boundary so a lifecycle signal from
            // any adapter cannot become a false completion notification.
            if (string.IsNullOrWhiteSpace(status))
            {
                Log("忽略缺少状态的监控通知：" + source);
                return;
            }

            var normalizedStatus = NormalizeNotificationStatus(status);
            if (string.IsNullOrWhiteSpace(normalizedStatus))
            {
                Log("忽略非终态监控通知：[" + source + "] status=" + status);
                return;
            }
            status = normalizedStatus;

            // 与 HTTP 路径用同一个上限，保证两条路径对同一次完成事件算出相同的去重 key。
            taskId = TruncateTaskId(taskId);
            if (!IsSourceEnabled(source) && !TryAutoEnableNewlyInstalledSource(source))
            {
                Log("来源已禁用，忽略通知：[" + source + "] status=" + status);
                return;
            }
            // 姿态总开关：Off 时 waiting/审批类通知不推送手机，完成/失败照常。
            if (_config.ApprovalPosture == ApprovalPosture.Off &&
                string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                Log("姿态 Off，忽略 waiting 通知：[" + source + "] " + taskId);
                return;
            }

            if (!TryAcceptEvent(source, status, taskId))
            {
                Log("忽略重复任务事件：[" + source + "] status=" + status + " " + taskId);
                return;
            }

            notificationTitle = NormalizeNotificationTitle(notificationTitle);
            actionUrl = NormalizeActionUrl(actionUrl);

            // 脱敏后再广播：命令/审批正文里的 API key、token、密码不应出现在手机屏幕上。
            message = SecretMasker.Mask(message);
            notificationTitle = SecretMasker.Mask(notificationTitle);
            taskLog = SecretMasker.Mask(NormalizeTaskLog(taskLog));

            await BroadcastAsync(CreatePayload(
                "[" + source + "] " + message,
                source,
                status,
                taskId,
                cwd,
                NormalizeDurationMs(durationMs),
                projectName,
                notificationTitle,
                actionUrl,
                NormalizeTokenCount(tokenCount),
                NormalizeSessionId(sessionId),
                NormalizeCredits(credits),
                taskLog: taskLog,
                requestId: requestId));
            Log("本地任务状态已广播[" + source + "] status=" + status +
                " durationMs=" + NormalizeDurationMs(durationMs) +
                " tokenCount=" + NormalizeTokenCount(tokenCount) +
                " sessionId=" + (string.IsNullOrWhiteSpace(sessionId) ? "-" : NormalizeSessionId(sessionId)) +
                " credits=" + (string.IsNullOrWhiteSpace(credits) ? "-" : NormalizeCredits(credits)) +
                "：" + message);
            PublishSnapshot();
        }

        /// <summary>
        /// Broadcasts a normalized live event to current mobile clients. The
        /// legacy /send message path remains separate for older integrations.
        /// </summary>
        public async Task<int> BroadcastEventAsync(TaskEvent taskEvent)
        {
            if (taskEvent == null) throw new ArgumentNullException(nameof(taskEvent));
            if (!IsSourceEnabled(taskEvent.Source)) return 0;
            var sentClients = await BroadcastJsonAsync(taskEvent.ToPayload());
            PublishSnapshot();
            return sentClients;
        }

        public ServerSnapshot GetSnapshot()
        {
            return new ServerSnapshot(_host != null, _clients.Count, GetAddressText());
        }

        public void Dispose()
        {
            AbortConnectedClients();
        }

        private async Task HandleRequestAsync(HttpContext context)
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                await AcceptWebSocketAsync(context);
                return;
            }

            var requestPath = context.Request.Path.Value ?? string.Empty;
            if (requestPath.Equals("/comfy", StringComparison.OrdinalIgnoreCase) ||
                requestPath.StartsWith("/comfy/", StringComparison.OrdinalIgnoreCase))
            {
                await _comfyUiConsole.HandleRequestAsync(context);
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health")
            {
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    status = "ok",
                    port = _config.ListenPort,
                    clients = _clients.Count
                });
                return;
            }

            if (context.Request.Path == "/apps")
            {
                await HandleAppsAsync(context);
                return;
            }

            if (context.Request.Path == "/apps/register")
            {
                await HandleAppRegisterAsync(context);
                return;
            }

            const string iconPrefix = "/apps/icon/";
            var path = context.Request.Path.Value ?? string.Empty;
            if (HttpMethods.IsGet(context.Request.Method) &&
                path.StartsWith(iconPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandleAppIconAsync(context, path.Substring(iconPrefix.Length));
                return;
            }

            if (context.Request.Path == "/send")
            {
                await HandleSendAsync(context);
                return;
            }

            if (context.Request.Path == "/events")
            {
                await HandleEventAsync(context);
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status404NotFound, new { error = "not found" });
        }

        private static async Task HandleAppsAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed, new { error = "method not allowed" });
                return;
            }

            var apps = GetMobileApps()
                .Select(ToPublicApp)
                .ToArray();
            await WriteJsonAsync(context, StatusCodes.Status200OK, new { apps });
        }

        private static IReadOnlyList<RegisteredAppDefinition> GetMobileApps()
        {
            // The mobile client needs the complete built-in catalog even when a
            // corresponding desktop app is not installed. Dynamic registrations
            // are additive metadata and must not replace built-in entries.
            var apps = NotificationAppsPanel.GetBuiltInAppDefinitions().ToList();
            var builtInKeys = new HashSet<string>(
                apps.Select(item => item.AppKey),
                StringComparer.OrdinalIgnoreCase);
            foreach (var registered in NotificationAppRegistry.GetAll())
            {
                if (!builtInKeys.Contains(registered.AppKey))
                {
                    apps.Add(registered);
                }
            }

            var workBuddy = apps.FirstOrDefault(item =>
                string.Equals(item.AppKey, "workbuddy", StringComparison.OrdinalIgnoreCase));
            if (workBuddy != null && !apps.Any(item =>
                    string.Equals(item.AppKey, WorkBuddyCompatibilityAppKey, StringComparison.OrdinalIgnoreCase)))
            {
                apps.Add(new RegisteredAppDefinition
                {
                    AppKey = WorkBuddyCompatibilityAppKey,
                    DisplayName = workBuddy.DisplayName,
                    BrandColor = workBuddy.BrandColor,
                    IconBase64 = workBuddy.IconBase64
                });
            }
            return apps;
        }

        private async Task HandleAppRegisterAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed, new { error = "method not allowed" });
                return;
            }

            if ((context.Request.ContentLength ?? 0) > MaxRegistrationBodyBytes)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge, new { error = "registration payload too large" });
                return;
            }

            var contentType = context.Request.ContentType ?? string.Empty;
            if (contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                await WriteJsonAsync(context, StatusCodes.Status415UnsupportedMediaType, new { error = "content-type must be application/json" });
                return;
            }

            try
            {
                var body = await ReadRequestBodyAsync(context, MaxRegistrationBodyBytes);
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (!TryReadRequiredString(json.RootElement, "appKey", out var appKey) ||
                    !TryReadRequiredString(json.RootElement, "displayName", out var displayName) ||
                    !TryReadRequiredString(json.RootElement, "brandColor", out var brandColor) ||
                    !TryReadRequiredString(json.RootElement, "iconBase64", out var iconBase64))
                {
                    throw new InvalidOperationException("missing app registration field");
                }

                if (!NotificationAppRegistry.TryRegister(
                    appKey,
                    displayName,
                    brandColor,
                    iconBase64,
                    out var registered,
                    out var error))
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error });
                    return;
                }

                NotifyAppsChanged();
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    status = "registered",
                    app = ToPublicApp(registered)
                });
                Log("应用适配已注册：[" + registered.AppKey + "] " + registered.DisplayName);
            }
            catch (RequestBodyTooLargeException)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge, new { error = "registration payload too large" });
            }
            catch
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid app registration JSON" });
            }
        }

        private static async Task HandleAppIconAsync(HttpContext context, string encodedKey)
        {
            if (encodedKey.Contains('/'))
            {
                await WriteJsonAsync(context, StatusCodes.Status404NotFound, new { error = "not found" });
                return;
            }

            var key = Uri.UnescapeDataString(encodedKey);
            if (!NotificationAppRegistry.TryGetIcon(key, out var bytes) &&
                !NotificationAppsPanel.TryGetBuiltInIconBytes(key, out bytes) &&
                (!string.Equals(key, WorkBuddyCompatibilityAppKey, StringComparison.OrdinalIgnoreCase) ||
                 !NotificationAppRegistry.TryGetIcon("workbuddy", out bytes)))
            {
                await WriteJsonAsync(context, StatusCodes.Status404NotFound, new { error = "icon not found" });
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "image/png";
            context.Response.Headers["Cache-Control"] = "no-cache";
            await context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }

        private static object ToPublicApp(RegisteredAppDefinition app) => new
        {
            appKey = app.AppKey,
            displayName = app.DisplayName,
            brandColor = app.BrandColor,
            iconPath = "/apps/icon/" + Uri.EscapeDataString(app.AppKey)
        };

        private static bool TryReadRequiredString(JsonElement root, string name, out string value)
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }

            value = string.Empty;
            return false;
        }

        private async Task HandleEventAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed, new { error = "method not allowed" });
                return;
            }

            var contentType = context.Request.ContentType ?? string.Empty;
            if (contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                await WriteJsonAsync(context, StatusCodes.Status415UnsupportedMediaType, new { error = "content-type must be application/json" });
                return;
            }

            try
            {
                var body = await ReadRequestBodyAsync(context, MaxEventBodyBytes);

                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (!TaskEvent.TryParse(json.RootElement, out var taskEvent, out var error))
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error });
                    return;
                }

                if (UnsupportedAppRegistry.TryGetMessage(taskEvent.Source, out var unsupportedMessage))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "unsupported",
                        reason = unsupportedMessage,
                        eventId = taskEvent.EventId
                    });
                    return;
                }

                if (!IsSourceEnabled(taskEvent.Source))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "disabled",
                        eventId = taskEvent.EventId
                    });
                    return;
                }

                if (!TryAcceptEvent(taskEvent.Source, taskEvent.EventType, taskEvent.EventId))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "duplicate",
                        eventId = taskEvent.EventId
                    });
                    return;
                }

                if (!taskEvent.IsApprovalRequest)
                {
                    var sentClients = await BroadcastEventAsync(taskEvent);
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "sent",
                        eventId = taskEvent.EventId,
                        clients = sentClients
                    });
                    return;
                }

                // ── 姿态总开关：off/notify 都不阻塞 agent，只影响推送到手机的形态 ──
                var posture = _config.ApprovalPosture;
                if (posture == ApprovalPosture.Off)
                {
                    // 总闸关闭：不推送、不阻塞，让 agent 退回本地审批。
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "unavailable",
                        decision = "unavailable",
                        requestId = taskEvent.RequestId
                    });
                    return;
                }

                if (posture == ApprovalPosture.Notify)
                {
                    // 只读观察：推送去掉按钮的信息卡片，但不阻塞 agent 等手机决策。
                    taskEvent.RequiresAction = false;
                    await BroadcastEventAsync(taskEvent);
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "unavailable",
                        decision = "unavailable",
                        requestId = taskEvent.RequestId
                    });
                    return;
                }

                var pending = new PendingApproval(taskEvent);
                if (!_pendingApprovals.TryAdd(taskEvent.RequestId, pending))
                {
                    await WriteJsonAsync(context, StatusCodes.Status409Conflict, new
                    {
                        error = "approval request already exists",
                        requestId = taskEvent.RequestId
                    });
                    return;
                }

                try
                {
                    var sent = await BroadcastEventAsync(taskEvent);
                    if (sent == 0)
                    {
                        await WriteJsonAsync(context, StatusCodes.Status200OK, new
                        {
                            status = "unavailable",
                            decision = "unavailable",
                            requestId = taskEvent.RequestId
                        });
                        return;
                    }

                    var decision = await WaitForApprovalAsync(context, pending);
                    if (decision == null)
                    {
                        decision = "deny";
                    }

                    await BroadcastEventAsync(taskEvent.CreateResolution(decision));
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = decision == "deny" ? "expired" : "resolved",
                        decision,
                        requestId = taskEvent.RequestId
                    });
                }
                finally
                {
                    _pendingApprovals.TryRemove(taskEvent.RequestId, out _);
                }
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid event JSON" });
            }
            catch (OperationCanceledException)
            {
                // The event producer disconnected while waiting for a decision.
                // The pending request is removed by the finally path below in the
                // normal server-side timeout/cancellation flow.
            }
            catch (RequestBodyTooLargeException)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge, new { error = "event body too large" });
            }
            catch
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid event request" });
            }
        }

        private async Task<string?> WaitForApprovalAsync(HttpContext context, PendingApproval pending)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var remainingMs = pending.Event.ExpiresAtUnixMs > now
                ? pending.Event.ExpiresAtUnixMs - now
                : 0L;
            var timeoutMs = (int)Math.Min(Math.Max(remainingMs, 1L), 5 * 60 * 1000L);
            var timeoutTask = Task.Delay(timeoutMs);
            var cancellationTask = Task.Delay(Timeout.Infinite, context.RequestAborted);
            var completed = await Task.WhenAny(pending.Completion.Task, timeoutTask, cancellationTask);
            return completed == pending.Completion.Task
                ? await pending.Completion.Task
                : null;
        }

        private async Task HandleSendAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed, new { error = "method not allowed" });
                return;
            }

            var contentType = context.Request.ContentType ?? string.Empty;
            if (contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                await WriteJsonAsync(context, StatusCodes.Status415UnsupportedMediaType, new { error = "content-type must be application/json" });
                return;
            }

            try
            {
                var body = await ReadRequestBodyAsync(context, MaxSendBodyBytes);
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (!json.RootElement.TryGetProperty("message", out var messageElement) ||
                    messageElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("message must be a string");
                }

                var message = NormalizeMessage(messageElement.GetString() ?? string.Empty);
                var source = NormalizeSource(ReadOptionalString(json.RootElement, "source", MaxSourceLength));
                var status = NormalizeNotificationStatus(
                    ReadOptionalString(json.RootElement, "status", 20));
                if (string.IsNullOrWhiteSpace(status))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "ignored",
                        reason = "non-terminal task lifecycle status",
                        clients = _clients.Count
                    });
                    Log("忽略非终态 HTTP 通知：" + ReadOptionalString(json.RootElement, "status", 20));
                    return;
                }
                var taskId = ReadOptionalString(json.RootElement, "taskId", MaxTaskIdLength);
                var cwd = ReadOptionalString(json.RootElement, "cwd", 240);
                var requestId = ReadOptionalString(json.RootElement, "requestId", 100);
                var requiresAction = json.RootElement.TryGetProperty("requiresAction", out var requiresActionElement) &&
                                      requiresActionElement.ValueKind == JsonValueKind.True;
                if (string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase))
                {
                    // Every waiting task needs an explicit processing affordance. Only a
                    // supplied requestId is later eligible for allow/deny approval actions.
                    requiresAction = true;
                }

                // ComfyUI 触发节点可选上传工作流/执行 prompt 图；若其中含 K 采样器
                // 正向提示词，则用作任务日志（避免只有笼统的“工作流已完成”）。
                // 注意 body 受 MaxEventBodyBytes(64KB) 约束，建议仅携带不含节点坐标
                // 的精简 prompt 或提示词原文；此处只读取，"log" 由下方 taskLog 承载。
                //
                // promptJson: API 执行图 (graphToPrompt.output)
                // workflowJson: UI 工作流 (graph.serialize), widgets_values 更可靠
                // 优先级：workflow > prompt（素言策略）
                var promptJson = ReadOptionalString(json.RootElement, "prompt", MaxEventBodyBytes);
                var workflowJson = ReadOptionalString(json.RootElement, "workflow", MaxEventBodyBytes);
                var explicitTaskLog = json.RootElement.TryGetProperty("taskLog", out var taskLogElement) &&
                                      taskLogElement.ValueKind == JsonValueKind.String
                    ? taskLogElement.GetString() ?? string.Empty : string.Empty;
                var taskLog = NormalizeTaskLog(explicitTaskLog);
                if (string.IsNullOrWhiteSpace(taskLog))
                {
                    taskLog = ComfyUIPromptParser.ExtractBestPositive(promptJson, workflowJson);
                }

                // Hook 传来的 cwd 可能是 Claude Code 的启动目录（如 C:\Users\Admin），
                // 而不是用户 cd 后的实际工作目录。若来源是 claude 且提供了 sessionId，
                // 尝试从 monitor 的记忆状态读取该会话的真实项目目录。
                if (string.Equals(source, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    var sessionId = ReadOptionalString(json.RootElement, "sessionId", 100);
                    var resolver = ClaudeDirectoryResolver;
                    if (!string.IsNullOrWhiteSpace(sessionId) && resolver != null)
                    {
                        var resolvedCwd = resolver(sessionId);
                        if (!string.IsNullOrWhiteSpace(resolvedCwd))
                        {
                            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            var normalizedPayloadCwd = string.IsNullOrWhiteSpace(cwd)
                                ? string.Empty
                                : Path.GetFullPath(cwd.Trim()).TrimEnd(Path.DirectorySeparatorChar);
                            var normalizedUserProfile = Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar);
                            if (string.Equals(normalizedPayloadCwd, normalizedUserProfile, StringComparison.OrdinalIgnoreCase))
                            {
                                cwd = resolvedCwd;
                            }
                        }
                    }
                }

                var projectName = ReadOptionalString(json.RootElement, "projectName", 120);
                projectName = ProjectNameResolver.ResolveFolderFirst(projectName, cwd, taskId, message);
                var durationMs = ReadOptionalDurationMs(json.RootElement);
                var tokenCount = ReadOptionalTokenCount(json.RootElement);
                var credits = NormalizeCredits(
                    ReadOptionalString(json.RootElement, "credits", 64));
                var notificationTitle = NormalizeNotificationTitle(
                    ReadOptionalString(json.RootElement, "notificationTitle", 120));
                var actionUrl = NormalizeActionUrl(
                    ReadOptionalString(json.RootElement, "actionUrl", 2048));
                var displayMessage = string.IsNullOrWhiteSpace(source)
                    ? message
                    : "[" + source + "] " + message;

                if (UnsupportedAppRegistry.TryGetMessage(source, out var unsupportedMessage))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "unsupported",
                        reason = unsupportedMessage,
                        clients = _clients.Count
                    });
                    Log("\u5df2\u62d2\u7edd\u9ed1\u540d\u5355\u5e94\u7528\u901a\u77e5\uff1a[" + source + "] " + unsupportedMessage);
                    return;
                }

                if (!IsSourceEnabled(source) && !TryAutoEnableNewlyInstalledSource(source))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "disabled",
                        clients = _clients.Count
                    });
                    Log("来源已禁用，忽略 HTTP 通知：[" + source + "]");
                    return;
                }

                if (!TryAcceptEvent(source, status, taskId))
                {
                    await WriteJsonAsync(context, StatusCodes.Status200OK, new
                    {
                        status = "duplicate",
                        clients = _clients.Count
                    });
                    Log("忽略重复 HTTP 完成事件：[" + source + "] " + taskId);
                    return;
                }

                var sentClients = await BroadcastAsync(CreatePayload(
                    displayMessage,
                    source,
                    status,
                    taskId,
                    cwd,
                    durationMs,
                    projectName,
                    notificationTitle,
                    actionUrl,
                    tokenCount,
                    NormalizeSessionId(ReadOptionalString(json.RootElement, "sessionId", 160)),
                    credits,
                    taskLog,
                    requestId: requestId,
                    requiresAction: requiresAction));
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    status = "sent",
                    clients = sentClients,
                    connectedClients = _clients.Count
                });
                Log("收到并广播" + (string.IsNullOrWhiteSpace(source) ? string.Empty : "[" + source + "]") + "：" + message);
                PublishSnapshot();
            }
            catch (RequestBodyTooLargeException)
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    new { error = "send body too large" });
            }
            catch
            {
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = "message must be a non-empty string with max length 500" });
            }
        }

        private async Task AcceptWebSocketAsync(HttpContext context)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            _clients[id] = socket;
            Log("手机已连接：" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown") +
                " client=" + id);
            PublishSnapshot();

            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var messageStream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            if (messageStream.Length + result.Count > MaxWebSocketMessageBytes)
                            {
                                Log("手机 WebSocket 消息超过大小上限，已断开 client=" + id);
                                socket.Abort();
                                return;
                            }
                            messageStream.Write(buffer, 0, result.Count);
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(messageStream.ToArray());
                        await HandleWebSocketTextMessageAsync(id, socket, text, context.RequestAborted);
                    }
                }
            }
            catch (Exception error)
            {
                // ColorOS HANS 等系统后台冻结机制会直接切断 socket，导致两类异常：
                // 1. OperationCanceledException: Kestrel 的 HttpContext.RequestAborted 被取消，
                //    ReceiveAsync 抛出，消息为 "Aborted"。
                // 2. WebSocketException "The remote party closed the WebSocket connection
                //    without completing the close handshake": HANS 直接关闭 TCP 连接（可能发
                //    RST），客户端来不及发送 WebSocket close 帧，服务端 ReceiveAsync 看到的是
                //    协议层异常。
                // 两者都是客户端被系统冻结导致的正常断开，不属于真正的协议错误。客户端回到前台后
                // 会通过 ACTION_ENSURE_CONNECTED 自动重连。
                if (error is OperationCanceledException)
                {
                    Log("手机已断开（系统后台冻结）");
                }
                else if (error is WebSocketException wsError &&
                         wsError.Message != null &&
                         wsError.Message.Contains("The remote party closed", StringComparison.OrdinalIgnoreCase))
                {
                    Log("手机已断开（系统后台冻结）");
                }
                else
                {
                    Log("手机连接异常断开：" + error.GetType().Name + " - " + error.Message);
                }
            }
            finally
            {
                _clients.TryRemove(id, out _);
                await CloseSocketAsync(socket);
                Log("手机已断开 client=" + id);
                PublishSnapshot();
            }
        }

        private async Task HandleWebSocketTextMessageAsync(
            Guid clientId,
            WebSocket socket,
            string text,
            CancellationToken cancellationToken)
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (!json.RootElement.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                var type = typeElement.GetString() ?? string.Empty;
                if (string.Equals(type, HeartbeatType, StringComparison.Ordinal))
                {
                    await SendHeartbeatAckAsync(clientId, socket, cancellationToken);
                    return;
                }

                // 手机端直连也会发送 agent.probe/agent.call。此前这些 RPC 只在
                // RemoteRelayBridge 的外部消息入口处理，直连消息在这里被静默丢弃，
                // 导致手机显示“已发送”但桌面智能体完全没有收到指令。
                if (string.Equals(type, "agent.probe", StringComparison.Ordinal) ||
                    string.Equals(type, "agent.call", StringComparison.Ordinal))
                {
                    var agentResponse = await ProcessExternalClientMessageAsync(text, cancellationToken);
                    if (agentResponse != null)
                    {
                        await SendJsonAsync(clientId, socket, agentResponse, cancellationToken);
                        LogDetail("手机 agent.call 回执已发送：client=" + clientId +
                            " messageId=" + ReadOptionalString(json.RootElement, "messageId", 80));
                    }
                    return;
                }

                if (!string.Equals(type, "control", StringComparison.Ordinal))
                {
                    return;
                }

                object? response;
                Action? onResponseSent = null;
                try
                {
                    var action = json.RootElement.TryGetProperty("action", out var actionElement) &&
                                 actionElement.ValueKind == JsonValueKind.String
                        ? actionElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.Equals(action, "approval.resolve", StringComparison.OrdinalIgnoreCase))
                    {
                        response = await HandleApprovalControlAsync(json.RootElement.Clone());
                    }
                    else
                    {
                        var handler = ControlRequestHandler;
                        if (handler == null)
                        {
                            response = new
                            {
                                type = "control_result",
                                action,
                                succeeded = false,
                                error = "control handler unavailable"
                            };
                        }
                        else
                        {
                            response = await handler(json.RootElement.Clone(), cancellationToken);
                            if (response is DeferredControlResponse deferredResponse)
                            {
                                response = deferredResponse.Payload;
                                onResponseSent = deferredResponse.OnResponseSent;
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    Log("control request failed client=" + clientId + " error=" + error.Message);
                    response = new
                    {
                        type = "control_result",
                        action = json.RootElement.TryGetProperty("action", out var action) &&
                                 action.ValueKind == JsonValueKind.String
                            ? action.GetString() ?? string.Empty
                            : string.Empty,
                        succeeded = false,
                        error = "control request failed"
                    };
                }

                if (response != null)
                {
                    await SendJsonAsync(clientId, socket, response, cancellationToken);
                }

                // Certain commands, such as putting this PC to sleep, stop the server before a
                // response can traverse the network. Run those side effects only after the
                // acknowledgement has been handed to the connected WebSocket.
                onResponseSent?.Invoke();
            }
            catch (JsonException)
            {
                Log("忽略无法解析的 WebSocket 消息 client=" + clientId);
            }
        }

        // ── Agent API RPC handlers ──

        private static async Task<object> HandleAgentProbeAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var agentKey = ReadOptionalString(root, "agentKey", 40);

            // ── Batch probe: no agentKey → probe all registered agents ──
            if (string.IsNullOrWhiteSpace(agentKey))
            {
                var results = new List<object>();
                foreach (var key in AgentApiProxyRegistry.GetRegisteredKeys())
                {
                    var p = AgentApiProxyRegistry.GetOrNull(key);
                    if (p == null) continue;
                    try
                    {
                        var r = await p.ProbeAsync(cancellationToken);
                        results.Add(new
                        {
                            agentKey = key,
                            running = r.Running,
                            capabilities = r.Capabilities,
                            detail = r.Detail
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            agentKey = key,
                            running = false,
                            capabilities = Array.Empty<string>(),
                            detail = "probe error: " + ex.Message
                        });
                    }
                }

                return new
                {
                    type = "agent.probe_result",
                    agentKey = "*",
                    count = results.Count,
                    agents = results
                };
            }

            // ── Single-agent probe ──
            HashSet<string> capabilities = new HashSet<string>();
            var running = false;
            var detail = string.Empty;

            var proxy = AgentApiProxyRegistry.GetOrNull(agentKey);
            if (proxy != null)
            {
                try
                {
                    var result = await proxy.ProbeAsync(cancellationToken);
                    running = result.Running;
                    capabilities = result.Capabilities;
                    detail = result.Detail;
                }
                catch (Exception ex)
                {
                    detail = "probe error: " + ex.Message;
                }
            }
            else
            {
                detail = "no proxy registered for agent '" + agentKey + "'";
            }

            return new
            {
                type = "agent.probe_result",
                agentKey,
                running,
                capabilities = capabilities,
                detail
            };
        }

        private static async Task<object> HandleAgentCallAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var agentKey = ReadOptionalString(root, "agentKey", 40);
            var method = ReadOptionalString(root, "method", 40);
            var sessionId = ReadOptionalString(root, "sessionId", 160);

            if (string.IsNullOrWhiteSpace(agentKey) || string.IsNullOrWhiteSpace(method))
            {
                return new
                {
                    type = "agent.result",
                    agentKey = agentKey ?? string.Empty,
                    method = method ?? string.Empty,
                    messageId = ReadOptionalString(root, "messageId", 80),
                    accepted = false,
                    code = "INVALID_PARAMS",
                     status = "缺少 agentKey 或 method"
                 };
            }

            var proxy = AgentApiProxyRegistry.GetOrNull(agentKey);
            if (proxy == null)
            {
                return new
                {
                    type = "agent.result",
                    agentKey,
                    method,
                    messageId = ReadOptionalString(root, "messageId", 80),
                    accepted = false,
                    code = "NO_PROXY",
                     status = $"未找到 {agentKey} 的 API 代理"
                 };
            }

            try
            {
                AgentApiResult result;
                if (string.Equals(method, "session.stop", StringComparison.OrdinalIgnoreCase))
                {
                    result = await proxy.StopAsync(sessionId, cancellationToken);
                }
                else
                {
                    return new
                    {
                        type = "agent.result",
                        agentKey,
                        method,
                        messageId = ReadOptionalString(root, "messageId", 80),
                        accepted = false,
                        code = "UNKNOWN_METHOD",
                         status = $"未知方法: {method}"
                     };
                }

                // 显式小写字段名：手机端按 accepted/code/status/detail 读取，
                // 用 result.Xxx 匿名属性会序列化成大写首字母（Accepted/Code/...），
                // 导致成功回执字段对不上。
                return new
                {
                    type = "agent.result",
                    agentKey,
                    method,
                    messageId = ReadOptionalString(root, "messageId", 80),
                    accepted = result.Accepted,
                    code = result.Code,
                    status = result.Status,
                     detail = result.Detail
                 };
            }
            catch (Exception ex)
            {
                return new
                {
                    type = "agent.result",
                    agentKey,
                    method,
                    messageId = ReadOptionalString(root, "messageId", 80),
                    accepted = false,
                    code = "ERROR",
                     status = $"执行异常：{ex.Message}"
                 };
            }
        }

        private async Task<object> HandleApprovalControlAsync(JsonElement root)
        {
            var requestId = ReadOptionalString(root, "requestId", 100);
            var decision = ReadOptionalString(root, "decision", 20).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(requestId) ||
                (decision != "allow" && decision != "deny"))
            {
                return new
                {
                    type = "control_result",
                    action = "approval.resolve",
                    requestId,
                    succeeded = false,
                    error = "requestId and decision allow|deny are required"
                };
            }

            if (!_pendingApprovals.TryGetValue(requestId, out var pending) ||
                !pending.Completion.TrySetResult(decision))
            {
                return new
                {
                    type = "control_result",
                    action = "approval.resolve",
                    requestId,
                    succeeded = false,
                    error = "approval request is missing or already resolved"
                };
            }

            Log("approval resolved by mobile: " + requestId + " decision=" + decision);
            await Task.Yield();
            return new
            {
                type = "control_result",
                action = "approval.resolve",
                requestId,
                decision,
                succeeded = true
            };
        }

        private async Task SendHeartbeatAckAsync(
            Guid clientId,
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            await SendJsonAsync(
                clientId,
                socket,
                new { type = "heartbeat_ack" },
                cancellationToken);
        }

        private async Task SendJsonAsync(
            Guid clientId,
            WebSocket socket,
            object body,
            CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            var responseType = body is JsonElement element &&
                               element.TryGetProperty("type", out var elementType)
                ? elementType.GetString() ?? string.Empty
                : body.GetType().GetProperty("type")?.GetValue(body)?.ToString() ?? string.Empty;
            if (await SendSocketAsync(clientId, socket, bytes, cancellationToken))
            {
                LogDetail("WebSocket response sent client=" + clientId +
                    " type=" + responseType + " bytes=" + bytes.Length);
            }
        }

        private Task<int> BroadcastAsync(MessagePayload payload)
        {
            return BroadcastJsonAsync(payload);
        }

        private async Task<int> BroadcastJsonAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            var results = await Task.WhenAll(_clients.ToArray().Select(item =>
                SendSocketAsync(item.Key, item.Value, bytes, CancellationToken.None)));
            var sentClients = results.Count(sent => sent);

            if (payload is MessagePayload message)
            {
                Log("broadcast source=" + message.source + " status=" + message.status +
                    " taskId=" + message.taskId + " projectName=" + message.projectName +
                    " sentClients=" + sentClients +
                    " connectedClients=" + _clients.Count);
            }
            else
            {
                Log("broadcast event sentClients=" + sentClients +
                    " connectedClients=" + _clients.Count);
            }

            OutboundBroadcast?.Invoke(json);
            return sentClients;
        }

        private WebSocketSendChannel GetSendChannel(WebSocket socket) =>
            _sendChannels.GetValue(socket, key => new WebSocketSendChannel(key));

        private async Task<bool> SendSocketAsync(Guid clientId, WebSocket socket, byte[] bytes,
            CancellationToken cancellationToken)
        {
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    _clients.TryRemove(clientId, out _);
                    return false;
                }
                await GetSendChannel(socket).ExecuteAsync(token => socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token), cancellationToken);
                return true;
            }
            catch (Exception error)
            {
                _clients.TryRemove(clientId, out _);
                Log("WebSocket send failed client=" + clientId + " error=" + error.Message);
                PublishSnapshot();
                return false;
            }
        }

        private bool TryAcceptEvent(string source, string status, string taskId)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(taskId))
            {
                return true;
            }

            var key = source.Trim().ToLowerInvariant() + ":" +
                      (string.IsNullOrWhiteSpace(status) ? "completed" : status.Trim().ToLowerInvariant()) + ":" +
                      taskId.Trim();
            var now = DateTimeOffset.UtcNow;
            lock (_eventDedupLock)
            {
                // 清理窗口 2 分钟即可：只需比去重窗口略长，防止内存无限增长；
                // 保留过久反而挤占探测新事件的空间。
                foreach (var item in _recentEvents.ToArray())
                {
                    if (now - item.Value > TimeSpan.FromMinutes(2))
                    {
                        _recentEvents.Remove(item.Key);
                    }
                }

                // 去重窗口从 2 分钟收窄到 15 秒：真正的重复是日志被重扫或 hook 同一秒内
                // 双发（毫秒级重复），15 秒足以吸收；再长就会把用户"发一条、几十秒后又发一条"
                // 这种正常的短会话误判为重复，导致后一条通知丢失。
                if (_recentEvents.TryGetValue(key, out var seenAt) && now - seenAt <= TimeSpan.FromSeconds(15))
                {
                    return false;
                }

                _recentEvents[key] = now;
                return true;
            }
        }

        private async Task CloseSocketAsync(WebSocket socket)
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                // 手机被冻结时 CloseAsync 不会完成握手，会等满超时；
                // 用短超时并捕获取消，避免每个客户端拖慢停止/断开流程。
                using var closeToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await GetSendChannel(socket).ExecuteAsync(token =>
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", token), closeToken.Token)
                    .ContinueWith(_ => { });
                // 握手仍挂起时强制释放底层连接，避免 Kestrel 等待该请求阻塞 host 停止。
                if (socket.State == WebSocketState.Open)
                {
                    socket.Abort();
                }
            }
        }

        private int AbortConnectedClients()
        {
            var aborted = 0;
            foreach (var item in _clients.ToArray())
            {
                if (!_clients.TryRemove(item.Key, out var socket))
                {
                    continue;
                }

                try
                {
                    socket.Abort();
                    aborted++;
                }
                catch (WebSocketException)
                {
                    // The peer may have disconnected between snapshot and abort.
                }
                catch (ObjectDisposedException)
                {
                    // The request handler can dispose the socket concurrently.
                }
            }
            return aborted;
        }

        private static MessagePayload CreatePayload(
            string message,
            string source,
            string status,
            string taskId,
            string cwd,
            long durationMs,
            string projectName,
            string notificationTitle,
            string actionUrl,
            long tokenCount = 0L,
            string sessionId = "",
            string credits = "",
            string taskLog = "",
            string requestId = "",
            bool requiresAction = false)
        {
            var wireSource = ToWireSource(source);
            var resolvedNotificationTitle = ResolveNotificationTitle(source, status, notificationTitle);
            return new MessagePayload
            {
                type = "message",
                content = message,
                time = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                source = wireSource,
                status = status,
                taskId = taskId,
                cwd = cwd,
                projectName = NormalizeProjectName(source, projectName),
                durationMs = NormalizeDurationMs(durationMs),
                tokenCount = NormalizeTokenCount(tokenCount),
                credits = NormalizeCredits(credits),
                notificationTitle = NormalizeNotificationTitle(resolvedNotificationTitle),
                actionUrl = NormalizeActionUrl(actionUrl),
                sessionId = NormalizeSessionId(sessionId),
                taskLog = NormalizeTaskLog(taskLog),
                requestId = TruncateRequestId(requestId),
                requiresAction = requiresAction || string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase)
            };
        }

        private static string ResolveNotificationTitle(string source, string status, string title)
        {
            var appKey = string.Equals(source, "workbuddy-ai", StringComparison.OrdinalIgnoreCase)
                ? "workbuddy-ai"
                : "workbuddy";
            var displayName = string.Equals(appKey, "workbuddy-ai", StringComparison.OrdinalIgnoreCase)
                ? "WorkBuddy AI"
                : "WorkBuddy";
            if ((string.Equals(source, "workbuddy", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(source, "workbuddy-ai", StringComparison.OrdinalIgnoreCase)) &&
                NotificationAppRegistry.TryGet(appKey, out var registered) &&
                !string.IsNullOrWhiteSpace(registered.DisplayName))
            {
                displayName = registered.DisplayName;
            }

            return NotificationTitleResolver.Resolve(source, status, title, displayName);
        }

        private static string ToWireSource(string source)
        {
            return string.Equals(source, "workbuddy", StringComparison.OrdinalIgnoreCase)
                ? WorkBuddyCompatibilityAppKey
                : source;
        }

        private static string NormalizeProjectName(string source, string projectName)
        {
            return ProjectNameResolver.NormalizeForSource(source, projectName);
        }

        private static long ReadOptionalDurationMs(JsonElement root)
        {
            if (!root.TryGetProperty("durationMs", out var element))
            {
                return 0L;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
            {
                return NormalizeDurationMs(value);
            }

            return element.ValueKind == JsonValueKind.String &&
                   long.TryParse(element.GetString(), out value)
                ? NormalizeDurationMs(value)
                : 0L;
        }

        private static string NormalizeNotificationStatus(string status)
        {
            var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                // Legacy /send callers omitted status to mean a completion.
                return "completed";
            }

            if (new[] { "completed", "complete", "done", "success", "succeeded" }
                .Contains(normalized, StringComparer.Ordinal))
            {
                return "completed";
            }

            if (new[] { "failed", "failure", "error" }
                .Contains(normalized, StringComparer.Ordinal))
            {
                return "failed";
            }

            if (new[] { "waiting", "pending", "approval", "approval_required",
                    "input_required", "needs_input", "awaiting_input" }
                .Contains(normalized, StringComparer.Ordinal))
            {
                return "waiting";
            }

            return string.Empty;
        }

        private static long NormalizeDurationMs(long durationMs)
        {
            const long maxDurationMs = 30L * 24L * 60L * 60L * 1000L;
            return durationMs > 0L && durationMs <= maxDurationMs ? durationMs : 0L;
        }

        private static long ReadOptionalTokenCount(JsonElement root)
        {
            if (!root.TryGetProperty("tokenCount", out var element))
            {
                return 0L;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
            {
                return NormalizeTokenCount(value);
            }

            return element.ValueKind == JsonValueKind.String &&
                   long.TryParse(element.GetString(), out value)
                ? NormalizeTokenCount(value)
                : 0L;
        }

        /// <summary>
        /// 仅接受正整数 token；无法可靠获取或越界时返回 0（手机端不展示）。
        /// </summary>
        private static long NormalizeTokenCount(long tokenCount)
        {
            const long maxTokenCount = 2_000_000_000L;
            return tokenCount > 0L && tokenCount <= maxTokenCount ? tokenCount : 0L;
        }

        /// <summary>
        /// TraeCode CN 积分明细（usage_summary 原文）；去空白并截断到 64 字符。
        /// 空字符串表示来源未提供，手机端不展示。
        /// </summary>
        private static string NormalizeCredits(string credits)
        {
            var trimmed = (credits ?? string.Empty).Trim();
            return trimmed.Length <= 64 ? trimmed : trimmed.Substring(0, 64);
        }

        /// <summary>
        /// 会话 ID 用于任务关联和停止控制；去空白并截断到 160 字符，
        /// 与 TaskEvent.MaxSessionIdLength 一致。空字符串表示来源未提供。
        /// </summary>
        private static string NormalizeSessionId(string sessionId)
        {
            var trimmed = (sessionId ?? string.Empty).Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed.Substring(0, 160);
        }

        /// <summary>
        /// 任务日志正文（如 ComfyUI K 采样器正向提示词）压平换行并截断，
        /// 避免长提示词撑爆通知卡片或广播报文。空串表示未解析出细化日志。
        /// </summary>
        private static string NormalizeTaskLog(string taskLog)
        {
            if (string.IsNullOrWhiteSpace(taskLog))
            {
                return string.Empty;
            }
            const int maxTaskLogLength = 4000;
            if (taskLog.Length <= maxTaskLogLength) return taskLog;
            var length = maxTaskLogLength;
            if (char.IsHighSurrogate(taskLog[length - 1]) && char.IsLowSurrogate(taskLog[length])) length--;
            return taskLog.Substring(0, length);
        }

        private static string NormalizeNotificationTitle(string title)
        {
            var trimmed = (title ?? string.Empty).Trim();
            return trimmed.Length <= 120 ? trimmed : trimmed.Substring(0, 120);
        }

        private static string NormalizeActionUrl(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 2048 ||
                !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return trimmed;
            }

            return string.Equals(uri.Scheme, "weixin", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : string.Empty;
        }

        private static string ReadOptionalString(JsonElement root, string name, int maxLength)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var value = element.GetString() ?? string.Empty;
            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static string NormalizeSource(string source)
        {
            var key = (source ?? string.Empty).Trim().ToLowerInvariant();
            return key switch
            {
                "adrive-upload" => "adrive",
                "aliyundrive" => "adrive",
                "aliyundrive-upload" => "adrive",
                "aliyun-drive" => "adrive",
                "aliyun-drive-upload" => "adrive",
                "quarknetdisk-upload" => "quarknetdisk",
                _ => key
            };
        }

        private static string TruncateTaskId(string taskId)
        {
            var value = (taskId ?? string.Empty).Trim();
            return value.Length <= MaxTaskIdLength ? value : value.Substring(0, MaxTaskIdLength);
        }

        private static string TruncateRequestId(string requestId)
        {
            var value = (requestId ?? string.Empty).Trim();
            return value.Length <= MaxTaskIdLength ? value : value.Substring(0, MaxTaskIdLength);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 远程处理 WorkBuddy 等待确认：找到 WorkBuddy 主窗口置前，
        /// 发送 Enter（允许）或 Esc（拒绝）触发当前确认框。仅响应手机端
        /// 明确发来的 approval.resolve，不做任何未授权的界面操作。
        /// </summary>
        private bool ResolveWorkBuddyWaiting(string sessionId, string decision, bool workBuddyAi = false)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            try
            {
                var processNames = workBuddyAi
                    ? new[] { "WorkBuddyAI" }
                    : new[] { "WorkBuddy" };
                foreach (var processName in processNames)
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        if (process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        SetForegroundWindow(process.MainWindowHandle);
                        Thread.Sleep(350);
                        // 允许 → Enter 触发确认框默认按钮；拒绝 → Esc 取消。
                        SendKeys.SendWait(decision == "allow" ? "{ENTER}" : "{ESC}");
                        return true;
                    }
                }
            }
            catch (Exception error)
            {
                Log("resolve workbuddy waiting failed: " + error.Message);
            }

            return false;
        }

        private static string NormalizeMessage(string message)
        {
            var trimmed = (message ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxMessageLength)
            {
                throw new InvalidOperationException("消息必须是 1 到 500 个字符。");
            }

            return trimmed;
        }

        private static async Task WriteJsonAsync(HttpContext context, int statusCode, object body)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(body), Encoding.UTF8);
        }

        private static async Task<string> ReadRequestBodyAsync(HttpContext context, int maxBytes)
        {
            if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > maxBytes)
            {
                throw new RequestBodyTooLargeException();
            }

            var initialCapacity = context.Request.ContentLength.HasValue &&
                                  context.Request.ContentLength.Value >= 0 &&
                                  context.Request.ContentLength.Value <= maxBytes
                ? (int)context.Request.ContentLength.Value
                : 0;
            using var stream = new MemoryStream(initialCapacity);
            var buffer = new byte[8192];
            var total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(
                       buffer, 0, buffer.Length, context.RequestAborted)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new RequestBodyTooLargeException();
                }
                stream.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private sealed class RequestBodyTooLargeException : Exception
        {
        }

        public string GetPrimaryAddress()
        {
            var text = GetAddressText();
            return text
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }

        /// <summary>
        /// Returns every candidate endpoint the phone QR code should carry, ordered
        /// by connection preference. The configured public/tunnel address comes first
        /// so the phone connects through it by default (stable across networks);
        /// automatically-detected local tunnels (cloudflared / frp / ngrok / cpolar,
        /// plus Tailscale / ZeroTier virtual LAN IPs) come next (zero-config);
        /// LAN addresses follow as fallback. The phone tries each in order until one
        /// succeeds.
        /// </summary>
        public List<string> GetConnectionEndpoints()
        {
            var endpoints = new List<string>();

            // 1. 用户手动配置的对外地址（显式意图，最高优先）。
            if (!string.IsNullOrWhiteSpace(_config.AdvertisedAddress))
            {
                // Cloudflare 隧道只暴露公网 443/80：域名广告地址必须走 wss://域名
                // （443 加密 WebSocket），否则手机连 ws://域名:1122 必然超时；
                // IP 形式的广告地址保持 ws://host:port。
                var isDnsName = !IPAddress.TryParse(_config.AdvertisedAddress, out _);
                var advertised = isDnsName
                    ? "wss://" + _config.AdvertisedAddress.Trim()
                    : "ws://" + FormatEndpoint(_config.AdvertisedAddress, _config.EffectiveAdvertisedPort);
                if (!endpoints.Contains(advertised, StringComparer.OrdinalIgnoreCase))
                {
                    endpoints.Add(advertised);
                }
            }

            // 2. 自动检测本机各类隧道（零配置可用）：cloudflared / frp / ngrok / cpolar
            //    等公网隧道优先（返回完整端点，如 wss://域名、ws://server:port），
            //    Tailscale / ZeroTier 虚拟局域网 IP 其次。
            foreach (var endpoint in TunnelDetectorRegistry.DetectAll(_config.EffectiveAdvertisedPort))
            {
                if (!endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                {
                    endpoints.Add(endpoint);
                }
            }

            // 3. 局域网地址兜底（同一路由开箱即用）。
            foreach (var lan in GetLanAddresses())
            {
                if (!endpoints.Contains(lan, StringComparer.OrdinalIgnoreCase))
                {
                    endpoints.Add(lan);
                }
            }

            return endpoints;
        }

        /// <summary>
        /// Returns the address that should be shown in the phone QR code.
        /// If the configured advertised address looks like a public endpoint but
        /// is not reachable from this PC, we fall back to a LAN address so the
        /// phone can connect immediately on the same network.
        /// </summary>
        public async Task<string> GetBestAddressAsync(CancellationToken cancellationToken = default)
        {
            var primary = GetPrimaryAddress();
            if (string.IsNullOrWhiteSpace(primary))
            {
                return string.Empty;
            }

            if (IsPublicDnsHost(primary) && !await IsEndpointReachableAsync(primary, cancellationToken))
            {
                var fallback = GetLanAddresses().FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return fallback;
                }
            }

            return primary;
        }

        private string GetAddressText()
        {
            // 与二维码候选完全一致：手动对外地址 → 自动检测隧道 → 局域网地址。
            var endpoints = GetConnectionEndpoints();
            if (endpoints.Count == 0)
            {
                return "未找到可用地址，请填写网络设置中的对外地址。";
            }

            return string.Join(Environment.NewLine, endpoints);
        }

        private IEnumerable<string> GetLanAddresses()
        {
            if (IPAddress.TryParse(_config.ListenAddress, out var listenAddress) &&
                !listenAddress.Equals(IPAddress.Any) &&
                !listenAddress.Equals(IPAddress.IPv6Any))
            {
                yield return "ws://" + FormatEndpoint(_config.ListenAddress, _config.EffectiveAdvertisedPort);
            }

            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Unknown)
                .Where(item => !item.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                .Where(item => !item.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .Where(item => !item.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.GetIPProperties().UnicastAddresses)
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => item.Address.ToString())
                .Where(address => !address.StartsWith("169.254.", StringComparison.Ordinal))
                .Where(IsPrivateLanAddress)
                .Distinct()
                .Select(address => "ws://" + FormatEndpoint(address, _config.EffectiveAdvertisedPort));

            foreach (var address in addresses)
            {
                yield return address;
            }
        }

        private static string FormatEndpoint(string host, int port)
        {
            return host.Contains(":", StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
                ? "[" + host + "]:" + port
                : host + ":" + port;
        }

        private static bool IsPrivateLanAddress(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        private static bool IsPrivateLanAddress(string address)
        {
            return IPAddress.TryParse(address, out var parsed) && IsPrivateLanAddress(parsed);
        }

        private static bool IsPublicDnsHost(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                return !IsPrivateLanAddress(ip);
            }

            return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> IsEndpointReachableAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host;
            var port = uri.Port;
            if (port < 1 || port > 65535)
            {
                return false;
            }

            using var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(3000, cancellationToken);
                var completed = await Task.WhenAny(connectTask, timeoutTask);
                if (completed == timeoutTask)
                {
                    return false;
                }

                await connectTask;
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private void PublishSnapshot()
        {
            SnapshotChanged?.Invoke(GetSnapshot());
        }

        private void Log(string text)
        {
            // 文件写入由订阅 LogLine 事件的上层（MainForm.AppendLog）统一负责，
            // 避免与 AppendLog 的文件写入重复。这里只触发事件，让 UI 与文件写入
            // 都由调用方决定。
            LogLine?.Invoke(text);
        }

        /// <summary>
        /// 诊断级日志：同样写入诊断文件，但不进入首页活动摘要。
        /// 判断标准是「这条日志对用户是否构成一个可读的事件」，而不是内容匹配——
        /// 底层回执、协议帧、二进制探测这类内容属于实现细节，用户看到只会淹没真事件。
        /// </summary>
        private void LogDetail(string text)
        {
            DetailLogLine?.Invoke(text);
        }

        private sealed class MessagePayload
        {
            public string type { get; set; } = "message";
            public string content { get; set; } = string.Empty;
            public string time { get; set; } = string.Empty;
            public string source { get; set; } = string.Empty;
            public string status { get; set; } = string.Empty;
            public string taskId { get; set; } = string.Empty;
            public string cwd { get; set; } = string.Empty;
            public string projectName { get; set; } = string.Empty;
            public long durationMs { get; set; }
            /// <summary>本轮累计 token；0 表示来源未提供，手机端不展示。</summary>
            public long tokenCount { get; set; }
            /// <summary>TraeCode CN 的本轮消耗积分（usage_summary 原文）；空串表示未提供，手机端不展示。</summary>
            public string credits { get; set; } = string.Empty;
            public string notificationTitle { get; set; } = string.Empty;
            public string actionUrl { get; set; } = string.Empty;
            /// <summary>
            /// 会话 ID（Claude transcript 文件名 / Codex session / ZCode 会话），
            /// 用于任务关联、停止控制和来源适配；空字符串表示没有可关联的会话。
            /// </summary>
            public string sessionId { get; set; } = string.Empty;
            /// <summary>任务日志正文（如 ComfyUI K 采样器正向提示词）；空串表示未解析出细化日志，手机端回退到 content。</summary>
            public string taskLog { get; set; } = string.Empty;
            /// <summary>
            /// 等待确认请求 ID（如 workbuddy:&lt;sessionId&gt;）；空串表示该等待不可远程处理。
            /// 手机端据此显示「允许 / 拒绝」按钮，通过 approval.resolve 回到 PC 端处理。
            /// </summary>
            public string requestId { get; set; } = string.Empty;
            /// <summary>waiting 状态统一标记为需要用户处理；只有 requestId 才显示审批按钮。</summary>
            public bool requiresAction { get; set; }
        }

        private sealed class PendingApproval
        {
            internal PendingApproval(TaskEvent taskEvent)
            {
                Event = taskEvent;
                Completion = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal TaskEvent Event { get; }
            internal TaskCompletionSource<string> Completion { get; }
        }
    }

    public sealed class ServerSnapshot
    {
        public ServerSnapshot(bool isRunning, int clientCount, string addressText)
        {
            IsRunning = isRunning;
            ClientCount = clientCount;
            AddressText = addressText;
        }

        public bool IsRunning { get; }
        public int ClientCount { get; }
        public string AddressText { get; }
    }
}
