using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static async Task TestFreebuffApiProxy()
    {
        var previousPort = Environment.GetEnvironmentVariable("TAPRELAY_FREEBUFF_ORCHESTRATOR_PORT");
        Environment.SetEnvironmentVariable("TAPRELAY_FREEBUFF_ORCHESTRATOR_PORT", "37978");
        try
        {
            await Test("Freebuff continuation uses now and preserves the latest text", async () =>
            {
                const string sessionId = "freebuff-idle";
                var snapshotCount = 0;
                var handler = new FreebuffProbeHandler((request, _) =>
                {
                    if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/auth/status", StringComparison.Ordinal))
                        return JsonResponse("{\"authed\":true}");
                    if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/api/thread/", StringComparison.Ordinal))
                        return JsonResponse(ThreadSnapshot(sessionId, ++snapshotCount == 1 ? "idle" : "running", false, snapshotCount == 1 ? 0 : 1, snapshotCount == 1 ? 100 : 101));
                    if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal))
                        return JsonResponse(Item(sessionId, "new-item"));
                    throw new InvalidOperationException("Unexpected Freebuff request: " + request.Method + " " + request.RequestUri);
                });

                using var proxy = new TapRelay.FreebuffApiProxy(_ => { }, handler);
                var result = await proxy.SendAsync(sessionId, "  latest prompt  ", @"W:\TapRelay", CancellationToken.None);
                Require(result.Accepted && result.Status == "已开始继续", "Continuation was not accepted: " + result.Status);

                var queue = handler.Requests.Single(request => request.RequestUri!.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal));
                using var body = JsonDocument.Parse(queue.Body);
                Require(body.RootElement.GetProperty("now").GetBoolean(), "Freebuff now flag was not set");
                Require(body.RootElement.GetProperty("text").GetString() == "  latest prompt  ", "Latest text was changed");
            });

            await Test("Freebuff continuation replaces running work and clears queued prompts", async () =>
            {
                const string sessionId = "freebuff-replace";
                var snapshotCount = 0;
                var handler = new FreebuffProbeHandler((request, _) =>
                {
                    if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/auth/status", StringComparison.Ordinal))
                        return JsonResponse("{\"authed\":true}");
                    if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal))
                        return JsonResponse("{\"ok\":true}");
                    if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.Contains("/api/queue/", StringComparison.Ordinal))
                        return JsonResponse("{\"ok\":true}");
                    if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal))
                        return JsonResponse(Item(sessionId, "latest-item"));
                    if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/api/thread/", StringComparison.Ordinal))
                    {
                        snapshotCount++;
                        if (snapshotCount == 1)
                            return JsonResponse(ThreadSnapshot(sessionId, "running", false, 2, 100, "old-1", "old-2"));
                        if (snapshotCount == 2)
                            return JsonResponse(ThreadSnapshot(sessionId, "idle", true, 2, 100, "old-1", "old-2"));
                        if (snapshotCount == 3)
                            return JsonResponse(ThreadSnapshot(sessionId, "idle", true, 0, 100));
                        return JsonResponse(ThreadSnapshot(sessionId, "running", false, 1, 101));
                    }
                    throw new InvalidOperationException("Unexpected Freebuff request: " + request.Method + " " + request.RequestUri);
                });

                using var proxy = new TapRelay.FreebuffApiProxy(_ => { }, handler);
                var result = await proxy.SendAsync(sessionId, "newest", @"W:\TapRelay", CancellationToken.None);
                Require(result.Accepted, "Replacement was not accepted: " + result.Status + " " + result.Detail);

                var paths = handler.Requests.Select(request => request.RequestUri!.AbsolutePath).ToArray();
                var firstDelete = Array.FindIndex(paths, path => path.Contains("/api/queue/old-1/delete", StringComparison.Ordinal));
                var secondDelete = Array.FindIndex(paths, path => path.Contains("/api/queue/old-2/delete", StringComparison.Ordinal));
                var queue = Array.FindIndex(paths, path => path.EndsWith("/queue", StringComparison.Ordinal));
                Require(firstDelete >= 0 && secondDelete > firstDelete && queue > secondDelete, "Old queued items were not removed before the latest prompt");
                Require(handler.Requests.Count(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal)) == 1, "Running turn was not stopped once");
            });

            await Test("Freebuff continuation rejects a queue acknowledgement without resumed state", async () =>
            {
                const string sessionId = "freebuff-not-resumed";
                var handler = new FreebuffProbeHandler((request, _) =>
                {
                    if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/auth/status", StringComparison.Ordinal))
                        return JsonResponse("{\"authed\":true}");
                    if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/api/thread/", StringComparison.Ordinal))
                        return JsonResponse(ThreadSnapshot(sessionId, "idle", false, 0, 100));
                    if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal))
                        return JsonResponse(Item(sessionId, "unverified-item"));
                    throw new InvalidOperationException("Unexpected Freebuff request: " + request.Method + " " + request.RequestUri);
                });

                using var proxy = new TapRelay.FreebuffApiProxy(_ => { }, handler);
                var result = await proxy.SendAsync(sessionId, "not really started", "", CancellationToken.None);
                Require(!result.Accepted && result.Code == "INPUT_FAILED", "Unverified queue request was reported as success");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAPRELAY_FREEBUFF_ORCHESTRATOR_PORT", previousPort);
        }
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string Item(string threadId, string itemId)
    {
        return JsonSerializer.Serialize(new { id = itemId, threadId, state = "queued" });
    }

    private static string ThreadSnapshot(
        string threadId,
        string turnState,
        bool queuePaused,
        int pendingWorkCount,
        long lastPromptAt,
        params string[] itemIds)
    {
        var items = itemIds.Select(id => new { id, threadId, state = "queued" }).ToArray();
        return JsonSerializer.Serialize(new
        {
            thread = new
            {
                id = threadId,
                status = "open",
                turnState,
                queuePaused,
                pendingWorkCount,
                lastPromptAt
            },
            items
        });
    }

    private sealed class FreebuffProbeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        private int _requestNumber;

        internal FreebuffProbeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        internal List<ProbeRequest> Requests { get; } = new List<ProbeRequest>();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
            lock (Requests)
            {
                Requests.Add(new ProbeRequest(request.Method, request.RequestUri!, body));
            }

            return _responder(request, Interlocked.Increment(ref _requestNumber));
        }
    }

    private sealed class ProbeRequest
    {
        internal ProbeRequest(HttpMethod method, Uri uri, string body)
        {
            Method = method;
            RequestUri = uri;
            Body = body;
        }

        internal HttpMethod Method { get; }
        internal Uri RequestUri { get; }
        internal string Body { get; }
    }
}
