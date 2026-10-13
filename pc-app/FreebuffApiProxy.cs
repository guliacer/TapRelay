using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// Agent API proxy for Freebuff (Codebuff desktop edition).
    ///
    /// Freebuff's queue endpoint is asynchronous. A plain queue request can
    /// therefore leave a phone continuation paused or behind older prompts.
    /// This proxy treats a phone continuation as a replacement: stop the
    /// current turn when necessary, remove queued prompts, then submit only
    /// the newest prompt with now=true.
    /// </summary>
    public sealed class FreebuffApiProxy : IAgentApiProxy, IDisposable
    {
        private const string DefaultPrompt = "查看当前任务进度，在现有基础上继续执行。";
        private const string ContinueLabel = "taprelay-continue";
        private const int CleanupAttempts = 50;
        private const int AcceptanceAttempts = 30;
        private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(100);
        private static readonly HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session.stop",
            "agent.probe"
        };

        private readonly HttpClient _client;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public string AgentKey => "freebuff";
        public HashSet<string> Capabilities => Caps;

        public FreebuffApiProxy(Action<string> log)
            : this(log, new HttpClientHandler())
        {
        }

        // The handler overload keeps the HTTP protocol testable without
        // sending prompts to a user's real Freebuff session.
        public FreebuffApiProxy(Action<string> log, HttpMessageHandler handler)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)))
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public async Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            var port = FreebuffDataLocator.ReadOrchestratorPort();
            if (port == null)
                return AgentProbeResult.NotRunning("orchestrator port not found in log");

            try
            {
                var response = await SendHttpAsync(
                    HttpMethod.Get,
                    BuildUrl(port.Value, "/api/auth/status"),
                    null,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return AgentProbeResult.NotRunning($"auth endpoint returned {response.StatusCode}");

                using var doc = ParseJson(response.Body, "Freebuff 鉴权接口返回格式无效");
                var authed = ReadBoolean(doc.RootElement, "authed");
                return authed
                    ? AgentProbeResult.Up(Caps, $"orchestrator port {port}, authed")
                    : AgentProbeResult.NotRunning("orchestrator not authed");
            }
            catch (Exception ex)
            {
                return AgentProbeResult.NotRunning($"probe failed: {ex.Message}");
            }
        }

        public async Task<AgentApiResult> SendAsync(
            string sessionId,
            string text,
            string cwd,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");

            var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            var entered = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;
                return await SendCoreAsync(sessionId, text, cwd, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return AgentApiResult.Failure("CANCELLED", "Freebuff 继续请求已取消");
            }
            catch (TaskCanceledException)
            {
                return AgentApiResult.Failure("TIMEOUT", "Freebuff 请求超时");
            }
            catch (Exception ex)
            {
                _log($"Freebuff agent.send error: thread={sessionId} {ex.Message}");
                return AgentApiResult.Failure("ERROR", $"请求异常：{ex.Message}");
            }
            finally
            {
                if (entered) gate.Release();
            }
        }

        private async Task<AgentApiResult> SendCoreAsync(
            string sessionId,
            string text,
            string cwd,
            CancellationToken cancellationToken)
        {
            var port = FreebuffDataLocator.ReadOrchestratorPort();
            if (port == null)
                return AgentApiResult.Failure("AGENT_NOT_RUNNING", "Freebuff orchestrator 未运行");

            var projectPath = cwd ?? string.Empty;
            var auth = await SendHttpAsync(
                HttpMethod.Get,
                BuildUrl(port.Value, "/api/auth/status"),
                null,
                cancellationToken);
            if (!auth.IsSuccessStatusCode)
                return AgentApiResult.Failure("AUTH_FAILED", "Freebuff 鉴权接口不可用", Summarize(auth.Body));

            using (var authDocument = ParseJson(auth.Body, "Freebuff 鉴权接口返回格式无效"))
            {
                if (!ReadBoolean(authDocument.RootElement, "authed"))
                    return AgentApiResult.Failure("NOT_AUTHED", "Freebuff 未登录");
            }

            var prepared = await PrepareThreadAsync(port.Value, sessionId, projectPath, cancellationToken);
            if (!prepared.Accepted)
                return AgentApiResult.Failure("INPUT_FAILED", prepared.Error, prepared.Detail);

            // Preserve the user's text exactly. Only an empty/whitespace-only
            // input receives the standard continuation prompt.
            var prompt = string.IsNullOrWhiteSpace(text) ? DefaultPrompt : text;
            var queueResponse = await SendHttpAsync(
                HttpMethod.Post,
                BuildUrl(port.Value, "/api/thread/" + Uri.EscapeDataString(sessionId) + "/queue"),
                new
                {
                    text = prompt,
                    projectPath,
                    label = ContinueLabel,
                    now = true
                },
                cancellationToken);
            if (!queueResponse.IsSuccessStatusCode)
            {
                _log($"Freebuff agent.send queue failed: thread={sessionId} status={queueResponse.StatusCode} body={Summarize(queueResponse.Body)}");
                return AgentApiResult.Failure(
                    "QUEUE_FAILED",
                    $"继续请求失败 ({queueResponse.StatusCode})",
                    Summarize(queueResponse.Body));
            }

            QueueItem acceptedItem;
            try
            {
                acceptedItem = ParseQueueItem(queueResponse.Body);
            }
            catch (InvalidOperationException ex)
            {
                _log($"Freebuff agent.send queue returned invalid item: thread={sessionId} {ex.Message}");
                return AgentApiResult.Failure("INPUT_FAILED", "Freebuff 未返回有效的队列项目", ex.Message);
            }

            if (!string.Equals(acceptedItem.ThreadId, sessionId, StringComparison.OrdinalIgnoreCase))
                return AgentApiResult.Failure("INPUT_FAILED", "Freebuff 返回的队列项目不属于当前会话");

            var acceptedState = await WaitForAcceptedStateAsync(
                port.Value,
                sessionId,
                projectPath,
                prepared.Snapshot!.LastPromptAt,
                cancellationToken);
            if (!acceptedState.Accepted)
            {
                _log($"Freebuff agent.send not resumed: thread={sessionId} {acceptedState.Error}");
                return AgentApiResult.Failure("INPUT_FAILED", "Freebuff 未恢复当前会话", acceptedState.Detail);
            }

            _log($"Freebuff agent.send started: thread={sessionId} queueItem={acceptedItem.Id} now=true textLength={prompt.Length}");
            return AgentApiResult.Success("已开始继续", $"thread {sessionId}, queue item {acceptedItem.Id}");
        }

        public async Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return AgentApiResult.Failure("INVALID_SESSION", "缺少会话 ID");

            var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            var entered = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;

                var port = FreebuffDataLocator.ReadOrchestratorPort();
                if (port == null)
                    return AgentApiResult.Failure("AGENT_NOT_RUNNING", "Freebuff orchestrator 未运行");

                var auth = await SendHttpAsync(
                    HttpMethod.Get,
                    BuildUrl(port.Value, "/api/auth/status"),
                    null,
                    cancellationToken);
                if (!auth.IsSuccessStatusCode)
                    return AgentApiResult.Failure("AUTH_FAILED", "Freebuff 鉴权接口不可用", Summarize(auth.Body));
                using (var authDocument = ParseJson(auth.Body, "Freebuff 鉴权接口返回格式无效"))
                {
                    if (!ReadBoolean(authDocument.RootElement, "authed"))
                        return AgentApiResult.Failure("NOT_AUTHED", "Freebuff 未登录");
                }

                var response = await SendHttpAsync(
                    HttpMethod.Post,
                    BuildUrl(port.Value, "/api/thread/" + Uri.EscapeDataString(sessionId) + "/stop"),
                    new { projectPath = string.Empty },
                    cancellationToken);
                if (!response.IsSuccessStatusCode || !IsOkResponse(response.Body))
                    return AgentApiResult.Failure("STOP_FAILED", "Freebuff 停止失败", Summarize(response.Body));

                _log($"Freebuff agent.stop accepted: thread={sessionId}");
                return AgentApiResult.Success("已停止", $"thread {sessionId}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return AgentApiResult.Failure("CANCELLED", "Freebuff 停止请求已取消");
            }
            catch (TaskCanceledException)
            {
                return AgentApiResult.Failure("TIMEOUT", "Freebuff 停止请求超时");
            }
            catch (Exception ex)
            {
                _log($"Freebuff agent.stop error: thread={sessionId} {ex.Message}");
                return AgentApiResult.Failure("ERROR", $"停止异常：{ex.Message}");
            }
            finally
            {
                if (entered) gate.Release();
            }
        }

        private async Task<PreparationResult> PrepareThreadAsync(
            int port,
            string sessionId,
            string projectPath,
            CancellationToken cancellationToken)
        {
            var stopRequested = false;
            ThreadSnapshot? snapshot = null;

            for (var attempt = 0; attempt < CleanupAttempts; attempt++)
            {
                snapshot = await GetThreadSnapshotAsync(port, sessionId, projectPath, cancellationToken);
                if (snapshot == null)
                    return PreparationResult.Failed("无法读取 Freebuff 当前会话状态");

                var queuedItems = snapshot.Items
                    .Where(item => string.Equals(item.State, "queued", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var activeItems = snapshot.Items.Any(item =>
                    string.Equals(item.State, "running", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.State, "injected", StringComparison.OrdinalIgnoreCase));
                var hasPendingWork = snapshot.PendingWorkCount > 0 || queuedItems.Length > 0 || activeItems;

                if (!stopRequested && (string.Equals(snapshot.TurnState, "running", StringComparison.OrdinalIgnoreCase) || hasPendingWork))
                {
                    var stop = await SendHttpAsync(
                        HttpMethod.Post,
                        BuildUrl(port, "/api/thread/" + Uri.EscapeDataString(sessionId) + "/stop"),
                        new { projectPath },
                        cancellationToken);
                    if (!stop.IsSuccessStatusCode || !IsOkResponse(stop.Body))
                        return PreparationResult.Failed("无法停止 Freebuff 当前回合", Summarize(stop.Body));

                    stopRequested = true;
                    _log($"Freebuff agent.send stopping previous work: thread={sessionId}");
                    await Task.Delay(PollDelay, cancellationToken);
                    continue;
                }

                if (string.Equals(snapshot.TurnState, "running", StringComparison.OrdinalIgnoreCase) || activeItems)
                {
                    await Task.Delay(PollDelay, cancellationToken);
                    continue;
                }

                if (queuedItems.Length > 0)
                {
                    foreach (var item in queuedItems)
                    {
                        var deleted = await SendHttpAsync(
                            HttpMethod.Post,
                            BuildUrl(port, "/api/queue/" + Uri.EscapeDataString(item.Id) + "/delete"),
                            new { },
                            cancellationToken);
                        if (!deleted.IsSuccessStatusCode || !IsOkResponse(deleted.Body))
                        {
                            return PreparationResult.Failed(
                                "无法清理 Freebuff 之前未处理的消息",
                                $"queue item {item.Id}: {Summarize(deleted.Body)}");
                        }
                    }

                    _log($"Freebuff agent.send cleared queued prompts: thread={sessionId} count={queuedItems.Length}");
                    await Task.Delay(PollDelay, cancellationToken);
                    continue;
                }

                if (snapshot.PendingWorkCount > 0)
                {
                    await Task.Delay(PollDelay, cancellationToken);
                    continue;
                }

                return PreparationResult.Ready(snapshot);
            }

            return PreparationResult.Failed(
                "Freebuff 当前会话仍有未处理消息，未发送新消息",
                snapshot == null ? string.Empty : $"turnState={snapshot.TurnState}, pending={snapshot.PendingWorkCount}");
        }

        private async Task<AcceptanceResult> WaitForAcceptedStateAsync(
            int port,
            string sessionId,
            string projectPath,
            long previousLastPromptAt,
            CancellationToken cancellationToken)
        {
            ThreadSnapshot? latest = null;
            for (var attempt = 0; attempt < AcceptanceAttempts; attempt++)
            {
                latest = await GetThreadSnapshotAsync(port, sessionId, projectPath, cancellationToken);
                if (latest == null)
                    return AcceptanceResult.Failed("无法读取 Freebuff 继续后的会话状态");

                var promptAdvanced = latest.LastPromptAt > previousLastPromptAt;
                var resumed = string.Equals(latest.TurnState, "running", StringComparison.OrdinalIgnoreCase) ||
                              latest.PendingWorkCount > 0 ||
                              promptAdvanced;
                if (string.Equals(latest.Status, "open", StringComparison.OrdinalIgnoreCase) &&
                    !latest.QueuePaused && resumed)
                {
                    return AcceptanceResult.Success();
                }

                if (attempt + 1 < AcceptanceAttempts)
                    await Task.Delay(PollDelay, cancellationToken);
            }

            return AcceptanceResult.Failed(
                "queue 接口已返回成功，但会话未恢复执行",
                latest == null ? string.Empty :
                    $"status={latest.Status}, turnState={latest.TurnState}, queuePaused={latest.QueuePaused}, pending={latest.PendingWorkCount}, lastPromptAt={latest.LastPromptAt}");
        }

        private async Task<ThreadSnapshot?> GetThreadSnapshotAsync(
            int port,
            string sessionId,
            string projectPath,
            CancellationToken cancellationToken)
        {
            var url = BuildUrl(
                port,
                "/api/thread/" + Uri.EscapeDataString(sessionId) +
                "?projectPath=" + Uri.EscapeDataString(projectPath));
            var response = await SendHttpAsync(HttpMethod.Get, url, null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log($"Freebuff thread snapshot failed: thread={sessionId} status={response.StatusCode} body={Summarize(response.Body)}");
                return null;
            }

            try
            {
                using var document = ParseJson(response.Body, "Freebuff 会话状态返回格式无效");
                return ParseThreadSnapshot(document.RootElement, sessionId);
            }
            catch (InvalidOperationException ex)
            {
                _log($"Freebuff thread snapshot invalid: thread={sessionId} {ex.Message}");
                return null;
            }
        }

        private static ThreadSnapshot ParseThreadSnapshot(JsonElement root, string sessionId)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("thread", out var thread) ||
                thread.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("missing thread");
            }

            var id = ReadString(thread, "id");
            if (!string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("thread id mismatch");
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("missing items");

            var result = new ThreadSnapshot
            {
                Id = id,
                Status = ReadString(thread, "status"),
                TurnState = ReadString(thread, "turnState"),
                QueuePaused = ReadBoolean(thread, "queuePaused"),
                PendingWorkCount = ReadInt32(thread, "pendingWorkCount"),
                LastPromptAt = ReadInt64(thread, "lastPromptAt")
            };
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemId = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(itemId))
                    throw new InvalidOperationException("queue item missing id");
                result.Items.Add(new QueueItem
                {
                    Id = itemId,
                    ThreadId = ReadString(item, "threadId"),
                    State = ReadString(item, "state")
                });
            }

            return result;
        }

        private static QueueItem ParseQueueItem(string body)
        {
            using var document = ParseJson(body, "Freebuff 队列接口返回格式无效");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("queue response is not an object");

            var item = new QueueItem
            {
                Id = ReadString(root, "id"),
                ThreadId = ReadString(root, "threadId"),
                State = ReadString(root, "state")
            };
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ThreadId))
                throw new InvalidOperationException("queue response has no id or threadId");
            return item;
        }

        private static bool IsOkResponse(string body)
        {
            try
            {
                using var document = ParseJson(body, "Freebuff 操作接口返回格式无效");
                return ReadBoolean(document.RootElement, "ok");
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static JsonDocument ParseJson(string body, string error)
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(error, ex);
            }
        }

        private async Task<HttpResponse> SendHttpAsync(
            HttpMethod method,
            string url,
            object? payload,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url);
            if (payload != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await _client.SendAsync(request, cancellationToken);
            return new HttpResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync());
        }

        private static string BuildUrl(int port, string path)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path;
        }

        private static string ReadString(JsonElement root, string propertyName)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool ReadBoolean(JsonElement root, string propertyName)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static int ReadInt32(JsonElement root, string propertyName)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var result)
                ? result
                : 0;
        }

        private static long ReadInt64(JsonElement root, string propertyName)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt64(out var result)
                ? result
                : 0;
        }

        private static string Summarize(string body)
        {
            var value = (body ?? string.Empty).Trim();
            return value.Length <= 240 ? value : value.Substring(0, 237) + "...";
        }

        public void Dispose()
        {
            _client.Dispose();
            foreach (var gate in _sessionGates.Values)
                gate.Dispose();
            _sessionGates.Clear();
        }

        private sealed class HttpResponse
        {
            internal HttpResponse(bool success, int statusCode, string body)
            {
                IsSuccessStatusCode = success;
                StatusCode = statusCode;
                Body = body ?? string.Empty;
            }

            internal bool IsSuccessStatusCode { get; }
            internal int StatusCode { get; }
            internal string Body { get; }
        }

        private sealed class ThreadSnapshot
        {
            internal string Id { get; set; } = string.Empty;
            internal string Status { get; set; } = string.Empty;
            internal string TurnState { get; set; } = string.Empty;
            internal bool QueuePaused { get; set; }
            internal int PendingWorkCount { get; set; }
            internal long LastPromptAt { get; set; }
            internal List<QueueItem> Items { get; } = new List<QueueItem>();
        }

        private sealed class QueueItem
        {
            internal string Id { get; set; } = string.Empty;
            internal string ThreadId { get; set; } = string.Empty;
            internal string State { get; set; } = string.Empty;
        }

        private sealed class PreparationResult
        {
            private PreparationResult(bool accepted, ThreadSnapshot? snapshot, string error, string detail)
            {
                Accepted = accepted;
                Snapshot = snapshot;
                Error = error;
                Detail = detail;
            }

            internal bool Accepted { get; }
            internal ThreadSnapshot? Snapshot { get; }
            internal string Error { get; }
            internal string Detail { get; }

            internal static PreparationResult Ready(ThreadSnapshot snapshot) =>
                new PreparationResult(true, snapshot, string.Empty, string.Empty);

            internal static PreparationResult Failed(string error, string detail = "") =>
                new PreparationResult(false, null, error, detail);
        }

        private sealed class AcceptanceResult
        {
            private AcceptanceResult(bool accepted, string error, string detail)
            {
                Accepted = accepted;
                Error = error;
                Detail = detail;
            }

            internal bool Accepted { get; }
            internal string Error { get; }
            internal string Detail { get; }

            internal static AcceptanceResult Success() =>
                new AcceptanceResult(true, string.Empty, string.Empty);

            internal static AcceptanceResult Failed(string error, string detail = "") =>
                new AcceptanceResult(false, error, detail);
        }
    }
}
