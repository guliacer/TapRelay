using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// Sends follow-up prompts to a WorkBuddy session through its local ACP
    /// server. WorkBuddy's renderer is Chromium and exposes no editable UI
    /// Automation element, so coordinate-based clipboard injection cannot prove
    /// that a message was accepted by the real session.
    /// </summary>
    public sealed class WorkBuddyAcpAgentProxy : IAgentApiProxy
    {
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent.probe",
            "session.send"
        };

        private static readonly HashSet<string> SupportedPermissionModes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "default",
                "fullAccess",
                "bypassPermissions"
            };

        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly TimeSpan WorkBuddyCancelSettleDelay =
            TimeSpan.FromMilliseconds(650);
        private const string QueueFlushItemId = "__taprelay_queue_flush__";

        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private string? _resolvedBinary;
        private bool _binaryProbed;

        public string AgentKey => "workbuddy";
        public HashSet<string> Capabilities => Caps;

        public WorkBuddyAcpAgentProxy(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var binary = ResolveBinary();
            if (binary == null)
            {
                return Task.FromResult(AgentProbeResult.NotRunning("WorkBuddy 未安装"));
            }

            return Task.FromResult(IsProcessRunning()
                ? AgentProbeResult.Up(Caps, binary)
                : AgentProbeResult.NotRunning("WorkBuddy 未运行"));
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

            var sessionGate = _sessionGates.GetOrAdd(
                sessionId,
                _ => new SemaphoreSlim(1, 1));
            await sessionGate.WaitAsync(cancellationToken);
            try
            {
                return await SendCoreAsync(sessionId, text, cwd, cancellationToken);
            }
            finally
            {
                sessionGate.Release();
            }
        }

        private async Task<AgentApiResult> SendCoreAsync(
            string sessionId,
            string text,
            string cwd,
            CancellationToken cancellationToken)
        {

            var binary = ResolveBinary();
            if (binary == null)
            {
                return AgentApiResult.Failure("NOT_INSTALLED", "WorkBuddy 未安装");
            }

            if (!IsProcessRunning())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = binary,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                    _log("workbuddy launched for ACP session=" + sessionId);
                }
                catch (Exception ex)
                {
                    return AgentApiResult.Failure("LAUNCH_FAILED", "无法启动 WorkBuddy", ex.Message);
                }
            }

            var prompt = string.IsNullOrWhiteSpace(text)
                ? "查看当前任务进度，在现有基础上继续执行。"
                : text.Trim();
            var effectiveCwd = ResolveSessionWorkingDirectory(sessionId, cwd);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var lastError = "未找到承载该会话的 WorkBuddy ACP 服务";

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoints = GetEndpointCandidates(sessionId).ToArray();
                foreach (var endpoint in endpoints)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        lastError = "WorkBuddy ACP 端点发现超时";
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    AcpConnection? connection = null;
                    try
                    {
                        connection = await ConnectSessionAsync(endpoint, sessionId, effectiveCwd, cancellationToken);
                        if (connection == null)
                        {
                            continue;
                        }

                        // A phone continuation is a replacement for stale input,
                        // not another item to append to WorkBuddy's queue. Cancel
                        // the previous run before submitting the newest prompt.
                        if (!await connection.CancelSessionAsync(
                                sessionId,
                                _log,
                                cancellationToken))
                        {
                            lastError = "WorkBuddy ACP 无法清理上一条未处理指令";
                            await connection.DisposeAsync();
                            connection = null;
                            continue;
                        }

                        // WorkBuddy preserves native queued messages when a run is
                        // cancelled. Remove them before and after its cancellation
                        // barrier so only the newest phone message can be executed.
                        if (!await connection.ClearQueuedPromptsAsync(
                                sessionId,
                                _log,
                                cancellationToken))
                        {
                            lastError = "WorkBuddy ACP 无法清空上一轮未处理消息";
                            await connection.DisposeAsync();
                            connection = null;
                            continue;
                        }

                        await Task.Delay(WorkBuddyCancelSettleDelay, cancellationToken);

                        if (!await connection.ClearQueuedPromptsAsync(
                                sessionId,
                                _log,
                                cancellationToken))
                        {
                            lastError = "WorkBuddy ACP 无法在取消完成后清空未处理消息";
                            await connection.DisposeAsync();
                            connection = null;
                            continue;
                        }

                        var accepted = await connection.SendPromptAsync(
                            sessionId,
                            prompt,
                            _log,
                            cancellationToken);
                        if (accepted)
                        {
                            _log($"workbuddy ACP prompt accepted session={sessionId} endpoint={endpoint} textLength={prompt.Length}");
                            // The connection remains alive while the background SSE
                            // reader drains session/update events until session_end.
                            connection = null;
                            return AgentApiResult.Success("已提交指令", $"WorkBuddy ACP session={sessionId}");
                        }

                        lastError = "WorkBuddy ACP 未接受该会话的指令";
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _log($"workbuddy ACP endpoint failed endpoint={endpoint}: {ex.Message}");
                    }
                    finally
                    {
                        if (connection != null)
                        {
                            await connection.DisposeAsync();
                        }
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(250, cancellationToken);
            }

            _log($"workbuddy ACP prompt rejected session={sessionId}: {lastError}");
            return AgentApiResult.Failure(
                "INPUT_FAILED",
                "无法把指令提交到 WorkBuddy 当前会话",
                lastError);
        }

        public Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentApiResult.Failure(
                "NOT_SUPPORTED",
                "WorkBuddy 不支持远程停止，请在电脑上手动操作"));
        }

        private async Task<AcpConnection?> ConnectSessionAsync(
            string endpoint,
            string sessionId,
            string cwd,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // WorkBuddy may need to replay a session transcript before replying
            // to initialize/resume. Keep this separate from the outer discovery
            // deadline so a slow but healthy endpoint is not treated as absent.
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var connectResponse = await SendHttpAsync(
                endpoint + "/api/v1/acp/connect",
                "{}",
                null,
                timeout.Token);
            if (!TryReadConnection(connectResponse, out var connectionId, out var sessionToken))
            {
                return null;
            }

            var connection = new AcpConnection(endpoint, connectionId, sessionToken);
            try
            {
                var initialize = await connection.PostAsync(
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = "taprelay-initialize-" + Guid.NewGuid().ToString("N"),
                        method = "initialize",
                        @params = new
                        {
                            protocolVersion = 1,
                            capabilities = new { },
                            clientInfo = new { name = "TapRelay", version = "1.0" }
                        }
                    }),
                    timeout.Token);
                if (!HasSuccessfulResult(initialize))
                {
                    _log("workbuddy ACP initialize rejected: " + SummarizeAcpResponse(initialize));
                    await connection.DisposeAsync();
                    return null;
                }

                // resume avoids replaying the entire transcript. Older sidecars
                // may only expose load, so fall back to it when resume is rejected.
                var resume = await connection.PostAsync(
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = "taprelay-resume-" + Guid.NewGuid().ToString("N"),
                        method = "session/resume",
                        @params = new { sessionId, cwd }
                    }),
                    timeout.Token);
                if (!HasSuccessfulResult(resume))
                {
                    _log("workbuddy ACP session/resume rejected session=" + sessionId + ": " +
                         SummarizeAcpResponse(resume));
                    var load = await connection.PostAsync(
                        JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = "taprelay-load-" + Guid.NewGuid().ToString("N"),
                            method = "session/load",
                            @params = new
                            {
                                sessionId,
                                cwd = cwd ?? string.Empty,
                                mcpServers = Array.Empty<object>()
                            }
                        }),
                        timeout.Token);
                    if (!HasSuccessfulResult(load))
                    {
                        _log("workbuddy ACP session/load rejected session=" + sessionId + ": " +
                             SummarizeAcpResponse(load));
                        await connection.DisposeAsync();
                        return null;
                    }
                }

                if (!await RestoreSessionConfigurationAsync(
                        connection, sessionId, timeout.Token))
                {
                    await connection.DisposeAsync();
                    return null;
                }

                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        private async Task<bool> RestoreSessionConfigurationAsync(
            AcpConnection connection,
            string sessionId,
            CancellationToken cancellationToken)
        {
            // WorkBuddy's own startup flow restores the sandbox setting before
            // the permission mode. Keep the same order so the first tool call
            // receives the same rule profile as the original session.
            if (WorkBuddySessionReader.TryResolveSandboxEnabled(
                    sessionId, out var sandboxEnabled))
            {
                var sandboxResponse = await connection.PostAsync(
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = "taprelay-sandbox-" + Guid.NewGuid().ToString("N"),
                        method = "session/set_config_option",
                        @params = new
                        {
                            sessionId,
                            configId = "sandbox",
                            // WorkBuddy validates the sandbox config as a string,
                            // even though the stored session value is a boolean.
                            value = ToAcpSandboxValue(sandboxEnabled)
                        }
                    }),
                    cancellationToken);
                if (!HasSuccessfulResult(sandboxResponse))
                {
                    _log("workbuddy ACP sandbox restore rejected session=" + sessionId + ": " +
                         SummarizeAcpResponse(sandboxResponse));
                    return false;
                }

                _log("workbuddy ACP sandbox restored session=" + sessionId +
                     " enabled=" + sandboxEnabled);
            }

            if (!WorkBuddySessionReader.TryResolvePermissionMode(
                    sessionId, out var storedMode))
            {
                connection.AutoApprovePermissions = false;
                _log("workbuddy ACP permission mode not found session=" + sessionId +
                     "; preserving ACP default");
                return true;
            }

            if (!TryNormalizePermissionMode(storedMode, out var permissionMode))
            {
                connection.AutoApprovePermissions = false;
                _log("workbuddy ACP permission mode unsupported session=" + sessionId +
                     " mode=" + storedMode + "; preserving ACP default");
                return true;
            }

            var modeResponse = await connection.PostAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "taprelay-mode-" + Guid.NewGuid().ToString("N"),
                    method = "session/set_mode",
                    @params = new
                    {
                        sessionId,
                        modeId = permissionMode
                    }
                }),
                cancellationToken);
            if (!HasSuccessfulResult(modeResponse))
            {
                _log("workbuddy ACP permission mode restore rejected session=" + sessionId +
                     " mode=" + permissionMode + ": " +
                     SummarizeAcpResponse(modeResponse));
                return false;
            }

            _log("workbuddy ACP permission mode restored session=" + sessionId +
                 " mode=" + permissionMode);
            connection.AutoApprovePermissions =
                string.Equals(permissionMode, "fullAccess", StringComparison.Ordinal) ||
                string.Equals(permissionMode, "bypassPermissions", StringComparison.Ordinal);
            return true;
        }

        internal static bool TryNormalizePermissionMode(
            string value,
            out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var candidate = value.Trim();
            if (!SupportedPermissionModes.Contains(candidate))
            {
                return false;
            }

            if (string.Equals(candidate, "fullaccess", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "fullAccess";
            }
            else if (string.Equals(candidate, "bypasspermissions", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "bypassPermissions";
            }
            else
            {
                normalized = "default";
            }

            return true;
        }

        internal static string ToAcpSandboxValue(bool enabled)
        {
            return enabled ? "true" : "false";
        }

        internal static string BuildSessionCancelRequest(string sessionId)
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/cancel",
                @params = new { sessionId }
            });
        }

        internal static string BuildSessionGetMessageQueueRequest(string sessionId)
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "taprelay-queue-get-" + Guid.NewGuid().ToString("N"),
                method = "session/get_message_queue",
                @params = new { sessionId }
            });
        }

        internal static string BuildSessionRemoveQueueItemRequest(
            string sessionId,
            string itemId)
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "taprelay-queue-remove-" + Guid.NewGuid().ToString("N"),
                method = "session/remove_queue_item",
                @params = new { sessionId, itemId }
            });
        }

        internal static bool TryReadQueueItemIds(
            string response,
            out List<string> itemIds)
        {
            itemIds = new List<string>();
            if (!TryReadAcpResult(response, out var result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!result.TryGetProperty("items", out var items))
            {
                return true;
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                {
                    itemIds.Add(value);
                }
            }

            return true;
        }

        private static bool TryReadAcpResult(
            string response,
            out JsonElement result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            foreach (var line in response.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var json = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? line.Substring(5).Trim()
                    : line.Trim();
                if (json.Length == 0 || json.StartsWith(":", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out _))
                    {
                        return false;
                    }

                    if (root.TryGetProperty("result", out var responseResult))
                    {
                        result = responseResult.Clone();
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // SSE comments and non-JSON event lines are harmless.
                }
            }

            return false;
        }

        internal static bool TryBuildPermissionResponse(
            string payload,
            bool autoApprove,
            out string response,
            out string sessionId,
            out string decision)
        {
            response = string.Empty;
            sessionId = string.Empty;
            decision = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var requestId) ||
                    !root.TryGetProperty("method", out var method) ||
                    !string.Equals(method.GetString(), "session/request_permission", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("params", out var parameters) ||
                    parameters.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (parameters.TryGetProperty("sessionId", out var sessionElement) &&
                    sessionElement.ValueKind == JsonValueKind.String)
                {
                    sessionId = sessionElement.GetString() ?? string.Empty;
                }

                string? optionId = autoApprove
                    ? FindPermissionOptionId(parameters)
                    : null;
                object outcome;
                if (optionId == null)
                {
                    outcome = new { outcome = "cancelled" };
                }
                else
                {
                    outcome = new { outcome = "selected", optionId };
                }
                var envelope = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = requestId.Clone(),
                    ["result"] = new { outcome }
                };
                response = JsonSerializer.Serialize(envelope);
                decision = optionId == null ? "cancelled" : "selected:" + optionId;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? FindPermissionOptionId(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("options", out var options) ||
                options.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallback = null;
            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("optionId", out var optionElement) ||
                    optionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = optionElement.GetString();
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (string.Equals(candidate, "allow", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (fallback == null &&
                    (string.Equals(candidate, "allow_once", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(candidate, "allow_always", StringComparison.OrdinalIgnoreCase)))
                {
                    fallback = candidate;
                }
            }

            return fallback;
        }

        private static string ResolveSessionWorkingDirectory(string sessionId, string cwd)
        {
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                return cwd.Trim();
            }

            var configDir = Environment.GetEnvironmentVariable("WORKBUDDY_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
            {
                configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".workbuddy");
            }

            var sessionDir = Path.Combine(configDir, "sessions");
            if (!Directory.Exists(sessionDir))
            {
                return cwd?.Trim() ?? string.Empty;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(sessionDir, "*.json"))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(file));
                        var root = document.RootElement;
                        if (!root.TryGetProperty("sessionId", out var id) ||
                            !string.Equals(id.GetString(), sessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (root.TryGetProperty("cwd", out var directory) &&
                            directory.ValueKind == JsonValueKind.String)
                        {
                            var resolved = directory.GetString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
                            {
                                return resolved;
                            }
                        }
                    }
                    catch
                    {
                        // Session registry files can be written concurrently.
                    }
                }
            }
            catch
            {
                // Fall back to the caller-provided cwd when registry enumeration is unavailable.
            }

            return cwd?.Trim() ?? string.Empty;
        }

        private static async Task<string> SendHttpAsync(
            string url,
            string body,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                request.Headers.TryAddWithoutValidation("acp-connection-id", connectionId);
            }

            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    "WorkBuddy ACP HTTP " + (int)response.StatusCode + ": " +
                    SummarizeAcpResponse(content));
            }

            return content;
        }

        private static bool TryReadConnection(
            string response,
            out string connectionId,
            out string sessionToken)
        {
            connectionId = string.Empty;
            sessionToken = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                connectionId = root.TryGetProperty("connectionId", out var id)
                    ? id.GetString() ?? string.Empty
                    : string.Empty;
                sessionToken = root.TryGetProperty("sessionToken", out var token)
                    ? token.GetString() ?? string.Empty
                    : string.Empty;
                return !string.IsNullOrWhiteSpace(connectionId);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasSuccessfulResult(string response)
        {
            var sawResult = false;
            foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var json = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? line.Substring(5).Trim()
                    : line.Trim();
                if (json.Length == 0 || json.StartsWith(":", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out _)) return false;
                    if (root.TryGetProperty("result", out _)) sawResult = true;
                }
                catch
                {
                    // SSE comments and non-JSON event lines are harmless.
                }
            }

            return sawResult;
        }

        private static string SummarizeAcpResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return "空响应";

            foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var json = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? line.Substring(5).Trim()
                    : line.Trim();
                if (json.Length == 0 || json.StartsWith(":", StringComparison.Ordinal)) continue;

                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var codeElement)
                            ? codeElement.ToString()
                            : string.Empty;
                        var message = error.TryGetProperty("message", out var messageElement)
                            ? messageElement.ToString()
                            : error.ToString();
                        return "error=" + code + " " + Truncate(message, 220);
                    }

                    if (root.TryGetProperty("result", out _)) return "result received";
                }
                catch
                {
                    // Ignore SSE comments and continue looking for a JSON-RPC error.
                }
            }

            return Truncate(response.Replace('\r', ' ').Replace('\n', ' '), 220);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }

        private static IEnumerable<string> GetEndpointCandidates(string sessionId)
        {
            var registeredCandidates = new List<(string SessionId, string Endpoint)>();
            var activeEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configDir = Environment.GetEnvironmentVariable("WORKBUDDY_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
            {
                configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".workbuddy");
            }

            var sessionDir = Path.Combine(configDir, "sessions");
            if (Directory.Exists(sessionDir))
            {
                foreach (var file in Directory.EnumerateFiles(sessionDir, "*.json"))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(file));
                        var root = document.RootElement;
                        var registeredSessionId = root.TryGetProperty("sessionId", out var sessionElement) &&
                                                  sessionElement.ValueKind == JsonValueKind.String
                            ? sessionElement.GetString() ?? string.Empty
                            : string.Empty;
                        foreach (var propertyName in new[] { "endpoint", "url" })
                        {
                            if (!root.TryGetProperty(propertyName, out var value)) continue;
                            if (TryNormalizeEndpoint(value.GetString(), out var endpoint))
                            {
                                registeredCandidates.Add((registeredSessionId, endpoint));
                            }
                        }
                    }
                    catch
                    {
                        // Session registry files can be written concurrently.
                    }
                }
            }

            try
            {
                foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (!IPAddress.IsLoopback(listener.Address) || listener.Port < 1024) continue;
                    if (TryNormalizeEndpoint("http://127.0.0.1:" + listener.Port, out var endpoint))
                    {
                        activeEndpoints.Add(endpoint);
                    }
                }
            }
            catch
            {
                // Endpoint registry entries are still useful when TCP enumeration
                // is unavailable due to local security policy.
            }

            return OrderEndpointCandidates(sessionId, registeredCandidates, activeEndpoints);
        }

        internal static IReadOnlyList<string> OrderEndpointCandidates(
            string sessionId,
            IEnumerable<(string SessionId, string Endpoint)> registeredCandidates,
            IEnumerable<string> activeEndpoints)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string endpoint)
            {
                if (!string.IsNullOrWhiteSpace(endpoint) && seen.Add(endpoint))
                {
                    result.Add(endpoint);
                }
            }

            // Prefer a live listener registered for the requested session. This
            // avoids spending the discovery window on stale ports left by older
            // WorkBuddy sessions with no current process behind them.
            var active = new HashSet<string>(activeEndpoints, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in registeredCandidates)
            {
                if (string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    active.Contains(candidate.Endpoint))
                {
                    Add(candidate.Endpoint);
                }
            }

            foreach (var candidate in registeredCandidates)
            {
                if (string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    Add(candidate.Endpoint);
                }
            }

            foreach (var endpoint in activeEndpoints)
            {
                Add(endpoint);
            }

            foreach (var candidate in registeredCandidates)
            {
                Add(candidate.Endpoint);
            }

            return result;
        }

        private static bool TryNormalizeEndpoint(string? value, out string endpoint)
        {
            endpoint = string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(uri.Host, out var address) ||
                !IPAddress.IsLoopback(address) || uri.Port < 1)
            {
                return false;
            }

            endpoint = "http://127.0.0.1:" + uri.Port;
            return true;
        }

        private bool IsProcessRunning()
        {
            try
            {
                return Process.GetProcessesByName("WorkBuddy").Any();
            }
            catch
            {
                return false;
            }
        }

        private string? ResolveBinary()
        {
            if (_binaryProbed) return _resolvedBinary;
            _binaryProbed = true;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkBuddy", "WorkBuddy.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WorkBuddy", "WorkBuddy.exe")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    _resolvedBinary = candidate;
                    _log("workbuddy binary resolved: " + candidate);
                    return candidate;
                }
            }

            _log("workbuddy binary not found");
            return null;
        }

        private sealed class AcpConnection
        {
            private readonly string _endpoint;
            private readonly string _connectionId;
            private readonly string _sessionToken;
            private int _closed;

            internal bool AutoApprovePermissions { get; set; }

            internal AcpConnection(string endpoint, string connectionId, string sessionToken)
            {
                _endpoint = endpoint;
                _connectionId = connectionId;
                _sessionToken = sessionToken;
            }

            internal async Task<string> PostAsync(string body, CancellationToken cancellationToken)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/v1/acp");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                request.Headers.TryAddWithoutValidation("acp-connection-id", _connectionId);
                if (!string.IsNullOrWhiteSpace(_sessionToken))
                {
                    request.Headers.TryAddWithoutValidation("acp-session-token", _sessionToken);
                }

                using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        "WorkBuddy ACP HTTP " + (int)response.StatusCode + ": " +
                        SummarizeAcpResponse(content));
                }

                return content;
            }

            internal async Task<bool> CancelSessionAsync(
                string sessionId,
                Action<string> log,
                CancellationToken cancellationToken)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var response = await PostAsync(
                        BuildSessionCancelRequest(sessionId),
                        timeout.Token);
                    if (!HasSuccessfulResult(response) && !IsAcpAcknowledgement(response))
                    {
                        log("workbuddy ACP session/cancel rejected session=" + sessionId + ": " +
                            SummarizeAcpResponse(response));
                        return false;
                    }

                    log("workbuddy ACP previous run cancelled session=" + sessionId +
                        "; latest prompt replaces queued input");
                    return true;
                }
                catch (Exception ex)
                {
                    log("workbuddy ACP session/cancel failed session=" + sessionId + ": " + ex.Message);
                    return false;
                }
            }

            internal async Task<bool> ClearQueuedPromptsAsync(
                string sessionId,
                Action<string> log,
                CancellationToken cancellationToken)
            {
                try
                {
                    using var getTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    getTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var queueResponse = await PostAsync(
                        BuildSessionGetMessageQueueRequest(sessionId),
                        getTimeout.Token);
                    if (!TryReadQueueItemIds(queueResponse, out var itemIds))
                    {
                        log("workbuddy ACP session/get_message_queue rejected session=" +
                            sessionId + ": " + SummarizeAcpResponse(queueResponse));
                        return false;
                    }

                    var removed = 0;
                    foreach (var itemId in itemIds)
                    {
                        if (!await RemoveQueueItemAsync(
                                sessionId,
                                itemId,
                                log,
                                cancellationToken))
                        {
                            return false;
                        }

                        removed++;
                    }

                    // removeItem() always synchronizes the native session.queue.
                    // The sentinel also clears native messages preserved by
                    // session/cancel when the rich queue has no item IDs.
                    if (!await RemoveQueueItemAsync(
                            sessionId,
                            QueueFlushItemId,
                            log,
                            cancellationToken))
                    {
                        return false;
                    }

                    log("workbuddy ACP queued prompts cleared session=" + sessionId +
                        " removed=" + removed);
                    return true;
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log("workbuddy ACP queue cleanup failed session=" + sessionId +
                        ": " + ex.Message);
                    return false;
                }
            }

            private async Task<bool> RemoveQueueItemAsync(
                string sessionId,
                string itemId,
                Action<string> log,
                CancellationToken cancellationToken)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var response = await PostAsync(
                    BuildSessionRemoveQueueItemRequest(sessionId, itemId),
                    timeout.Token);
                if (HasSuccessfulResult(response) || IsAcpAcknowledgement(response))
                {
                    return true;
                }

                log("workbuddy ACP session/remove_queue_item rejected session=" +
                    sessionId + ": " + SummarizeAcpResponse(response));
                return false;
            }

            private async Task<bool> SendPermissionResponseAsync(
                string payload,
                Action<string> log)
            {
                if (!TryBuildPermissionResponse(
                        payload,
                        AutoApprovePermissions,
                        out var response,
                        out var sessionId,
                        out var decision))
                {
                    return false;
                }

                try
                {
                    var acknowledgement = await PostAsync(response, CancellationToken.None);
                    if (!HasSuccessfulResult(acknowledgement) &&
                        !IsAcpAcknowledgement(acknowledgement))
                    {
                        log("workbuddy ACP permission response rejected session=" + sessionId + ": " +
                            SummarizeAcpResponse(acknowledgement));
                        return false;
                    }

                    log("workbuddy ACP permission request handled session=" + sessionId +
                        " decision=" + decision);
                    return true;
                }
                catch (Exception ex)
                {
                    log("workbuddy ACP permission response failed session=" + sessionId + ": " + ex.Message);
                    return false;
                }
            }

            private static bool IsAcpAcknowledgement(string response)
            {
                if (string.IsNullOrWhiteSpace(response)) return true;
                foreach (var line in response.Split(
                             new[] { '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(line.Trim(), ":ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            internal async Task<bool> SendPromptAsync(
                string sessionId,
                string text,
                Action<string> log,
                CancellationToken cancellationToken)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/v1/acp");
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "taprelay-prompt-" + Guid.NewGuid().ToString("N"),
                    method = "session/prompt",
                    @params = new
                    {
                        sessionId,
                        prompt = new[] { new { type = "text", text } }
                    }
                }), Encoding.UTF8, "application/json");
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                request.Headers.TryAddWithoutValidation("acp-connection-id", _connectionId);
                if (!string.IsNullOrWhiteSpace(_sessionToken))
                {
                    request.Headers.TryAddWithoutValidation("acp-session-token", _sessionToken);
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(8));
                var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    log("workbuddy ACP prompt HTTP " + (int)response.StatusCode + ": " +
                        SummarizeAcpResponse(errorBody));
                    response.Dispose();
                    return false;
                }

                var stream = await response.Content.ReadAsStreamAsync();
                var reader = new StreamReader(stream, Encoding.UTF8);
                var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (line == ":ok")
                            {
                                accepted.TrySetResult(true);
                                continue;
                            }

                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            var payload = line.Substring(5).Trim();
                            if (await SendPermissionResponseAsync(payload, log))
                            {
                                accepted.TrySetResult(true);
                                continue;
                            }

                            if (payload.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log("workbuddy ACP prompt rejected by server: " +
                                    SummarizeAcpResponse(payload));
                                accepted.TrySetResult(false);
                            }

                            if (HasSuccessfulResult(payload))
                            {
                                accepted.TrySetResult(true);
                            }

                            if (payload.IndexOf("session_end", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log("workbuddy ACP session update received session_end");
                            }
                        }

                        if (!accepted.Task.IsCompleted)
                        {
                            log("workbuddy ACP prompt stream ended before acknowledgement");
                            accepted.TrySetResult(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        accepted.TrySetException(ex);
                    }
                    finally
                    {
                        reader.Dispose();
                        response.Dispose();
                        await DisposeAsync();
                    }
                });

                try
                {
                    var completed = await Task.WhenAny(
                        accepted.Task,
                         Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
                    if (completed != accepted.Task)
                    {
                        return false;
                    }

                    return await accepted.Task;
                }
                catch
                {
                    return false;
                }
            }

            internal async Task DisposeAsync()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint + "/api/v1/acp");
                    request.Headers.TryAddWithoutValidation("acp-connection-id", _connectionId);
                    if (!string.IsNullOrWhiteSpace(_sessionToken))
                    {
                        request.Headers.TryAddWithoutValidation("acp-session-token", _sessionToken);
                    }

                    using var timeout = new CancellationTokenSource(1000);
                    using var response = await Client.SendAsync(request, timeout.Token);
                }
                catch
                {
                    // Closing a completed/expired ACP stream is best effort.
                }
            }
        }
    }
}
