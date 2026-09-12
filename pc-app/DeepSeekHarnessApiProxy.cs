using System;
using System.Collections.Generic;
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
    /// Sends follow-up prompts through DeepSeek Harness Desktop's local API.
    /// The shipped dsh CLI does not expose a session subcommand; the desktop
    /// session is continued by POST /api/session.prompt.
    /// </summary>
    public sealed class DeepSeekHarnessApiProxy : IAgentApiProxy
    {
        private const int KnownApiPort = 17653;
        private const string ApiOverrideVariable = "TAPRELAY_DEEPSEEKHARNESS_API_URL";
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent.probe",
            "session.send",
            "session.stop"
        };
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly Action<string> _log;
        private readonly object _endpointGate = new object();
        private string? _endpoint;

        public string AgentKey => "deepseekharness";
        public HashSet<string> Capabilities => Caps;

        public DeepSeekHarnessApiProxy(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            if (!AdaptedProcessGate.IsRunningForAppKey(AgentKey))
            {
                return AgentProbeResult.NotRunning("DeepSeek Harness Desktop 未运行");
            }

            var endpoint = await ResolveEndpointAsync(cancellationToken);
            return endpoint == null
                ? AgentProbeResult.NotRunning("DeepSeek Harness Desktop 本地 API 不可用")
                : AgentProbeResult.Up(Caps, endpoint);
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

            if (!AdaptedProcessGate.IsRunningForAppKey(AgentKey))
            {
                return AgentApiResult.Failure("AGENT_NOT_RUNNING", "DeepSeek Harness Desktop 未运行");
            }

            var prompt = string.IsNullOrWhiteSpace(text)
                ? "查看当前任务进度，在现有基础上继续执行。"
                : text.Trim();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var lastError = "DeepSeek Harness Desktop 本地 API 不可用";

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoint = await ResolveEndpointAsync(cancellationToken);
                if (endpoint != null)
                {
                    var result = await CallAsync(
                        endpoint,
                        "session.prompt",
                        new
                        {
                            sessionId,
                            mode = "queue",
                            content = new[] { new { type = "text", text = prompt } }
                        },
                        cancellationToken);
                    if (result.Accepted)
                    {
                        _log($"deepseekharness agent.call accepted session={sessionId} endpoint={endpoint} textLength={prompt.Length}");
                        return AgentApiResult.Success("已提交指令", $"DeepSeek Harness API session={sessionId}");
                    }

                    lastError = result.Detail;
                    if (!string.Equals(result.Code, "internal", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(result.Code, "agent-busy", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }

                await Task.Delay(250, cancellationToken);
            }

            _log($"deepseekharness agent.call rejected session={sessionId}: {lastError}");
            return AgentApiResult.Failure("INPUT_FAILED", "无法把指令提交到 DeepSeek Harness 当前会话", lastError);
        }

        public async Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");
            }

            var endpoint = await ResolveEndpointAsync(cancellationToken);
            if (endpoint == null)
            {
                return AgentApiResult.Failure("AGENT_NOT_RUNNING", "DeepSeek Harness Desktop 本地 API 不可用");
            }

            var result = await CallAsync(endpoint, "session.cancel", new { sessionId }, cancellationToken);
            if (result.Accepted)
            {
                _log($"deepseekharness agent.stop accepted session={sessionId} endpoint={endpoint}");
                return AgentApiResult.Success("已停止", $"DeepSeek Harness API session={sessionId}");
            }

            return AgentApiResult.Failure("STOP_FAILED", "DeepSeek Harness 停止失败", result.Detail);
        }

        private async Task<string?> ResolveEndpointAsync(CancellationToken cancellationToken)
        {
            var candidates = GetEndpointCandidates();
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discovery.CancelAfter(TimeSpan.FromSeconds(1.5));
            var pending = candidates.Select(endpoint => ProbeEndpointAsync(endpoint, discovery.Token)).ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                try
                {
                    var endpoint = await completed;
                    if (endpoint != null)
                    {
                        lock (_endpointGate) _endpoint = endpoint;
                        discovery.Cancel();
                        return endpoint;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }
            }

            return null;
        }

        private static async Task<string?> ProbeEndpointAsync(string endpoint, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(650));
            try
            {
                var result = await CallAsync(endpoint, "session.list", new { }, timeout.Token);
                return result.Accepted ? endpoint : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<string> GetEndpointCandidates()
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return;
                if (uri.Scheme != Uri.UriSchemeHttp) return;
                if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                    (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))) return;
                var normalized = "http://" + uri.Authority;
                if (seen.Add(normalized)) candidates.Add(normalized);
            }

            Add(Environment.GetEnvironmentVariable(ApiOverrideVariable));
            lock (_endpointGate) Add(_endpoint ?? string.Empty);
            Add("http://127.0.0.1:" + KnownApiPort);

            try
            {
                foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Where(item => IPAddress.IsLoopback(item.Address) && item.Port > 1024)
                    .OrderBy(item => item.Port))
                {
                    Add("http://127.0.0.1:" + listener.Port);
                }
            }
            catch (Exception error)
            {
                _log("deepseekharness API 端口探测失败：" + error.Message);
            }

            return candidates;
        }

        private static async Task<RpcCallResult> CallAsync(
            string endpoint,
            string method,
            object payload,
            CancellationToken cancellationToken)
        {
            var envelope = JsonSerializer.Serialize(new
            {
                type = "client-request",
                rpcId = "taprelay-" + Guid.NewGuid().ToString("N"),
                method,
                payload
            });
            using var content = new StringContent(envelope, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(endpoint + "/api/" + method, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return RpcCallResult.Failure("http-" + (int)response.StatusCode, response.StatusCode + ": " + Truncate(body));
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (!root.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("ok", out var ok))
                {
                    return RpcCallResult.Failure("invalid-response", "DeepSeek Harness API 返回格式无效");
                }

                if (ok.GetBoolean())
                {
                    return RpcCallResult.Success();
                }

                if (result.TryGetProperty("error", out var error))
                {
                    var code = ReadString(error, "code");
                    var message = ReadString(error, "message");
                    return RpcCallResult.Failure(code, string.IsNullOrWhiteSpace(message) ? code : message);
                }

                return RpcCallResult.Failure("rpc-error", "DeepSeek Harness API 请求失败");
            }
            catch (JsonException error)
            {
                return RpcCallResult.Failure("invalid-response", "DeepSeek Harness API 返回格式无效：" + error.Message);
            }
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string Truncate(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length <= 240 ? normalized : normalized.Substring(0, 239) + "...";
        }

        private sealed class RpcCallResult
        {
            private RpcCallResult(bool accepted, string code, string detail)
            {
                Accepted = accepted;
                Code = code;
                Detail = detail;
            }

            internal bool Accepted { get; }
            internal string Code { get; }
            internal string Detail { get; }

            internal static RpcCallResult Success() => new RpcCallResult(true, string.Empty, string.Empty);
            internal static RpcCallResult Failure(string code, string detail) => new RpcCallResult(false, code, detail);
        }
    }
}
