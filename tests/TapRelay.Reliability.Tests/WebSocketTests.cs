using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TapRelay;

internal static partial class Program
{
    private static async Task TestWebSockets()
    {
        await Test("Broadcast and heartbeat reach healthy client while another stalls", async () =>
        {
            using var server = CreateServer();
            using var slow = new ProbeSocket { Stall = true };
            using var healthy = new ProbeSocket();
            var clients = GetClients(server);
            clients[Guid.NewGuid()] = slow;
            var healthyId = Guid.NewGuid();
            clients[healthyId] = healthy;

            var broadcast = Broadcast(server, new { type = "message", content = "reliability probe" });
            await Until(() => slow.SendEntered && healthy.Messages.Count == 1, "Healthy client blocked by slow peer", 1500);
            var response = SendResponse(server, healthyId, healthy, new { type = "heartbeat_ack" });
            Require(await Task.WhenAny(response, Task.Delay(1500)) == response, "Heartbeat blocked by slow peer");
            await response;
            Require(healthy.Messages.Any(message => message.Contains("heartbeat_ack")), "Missing heartbeat");
            Require(await Task.WhenAny(broadcast, Task.Delay(4000)) == broadcast, "Broadcast exceeded deadline");
            Require(await broadcast == 1 && slow.State == WebSocketState.Aborted && clients.Count == 1,
                "Failed socket was not removed or successful count is wrong");
        });
        await Test("Concurrent broadcasts and replies serialize writes per socket", async () =>
        {
            using var server = CreateServer();
            using var socket = new ProbeSocket { SendDelayMs = 20 };
            var id = Guid.NewGuid();
            GetClients(server)[id] = socket;
            var sends = Enumerable.Range(0, 10).Select(i => (Task)Broadcast(server, new { type = "message", i }))
                .Concat(Enumerable.Range(0, 10).Select(i => SendResponse(server, id, socket, new { type = "agent.result", i })));
            await Task.WhenAll(sends);
            Require(socket.Messages.Count == 20 && socket.MaximumConcurrentSends == 1, "Overlapping WebSocket writes");
        });
        await Test("Broadcast without clients drops immediately", async () =>
        {
            using var server = CreateServer();
            Require(await Broadcast(server, new { type = "message" }) == 0, "Offline broadcast was retained");
            using var socket = new ProbeSocket();
            GetClients(server)[Guid.NewGuid()] = socket;
            Require(socket.Messages.IsEmpty, "Offline message was replayed");
        });
    }

    private static NotifyServer CreateServer()
    {
        var server = new NotifyServer();
        typeof(NotifyServer).GetField("_diagnosticLogPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(server, Path.Combine(AppContext.BaseDirectory, "probe-server.log"));
        return server;
    }

    private static ConcurrentDictionary<Guid, WebSocket> GetClients(NotifyServer server) =>
        (ConcurrentDictionary<Guid, WebSocket>)typeof(NotifyServer)
            .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;

    private static Task<int> Broadcast(NotifyServer server, object payload) =>
        (Task<int>)typeof(NotifyServer).GetMethod("BroadcastJsonAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, new[] { payload })!;

    private static Task SendResponse(NotifyServer server, Guid id, WebSocket socket, object payload) =>
        (Task)typeof(NotifyServer).GetMethod("SendJsonAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, new object[] { id, socket, payload, CancellationToken.None })!;

    private sealed class ProbeSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _activeSends;
        private readonly TaskCompletionSource<bool> _blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stall;
        public int SendDelayMs;
        public volatile bool SendEntered;
        public int MaximumConcurrentSends;
        public readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _blocked.TrySetCanceled(); }
        public override void Dispose() { Abort(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) =>
            CloseAsync(status, description, token);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) =>
            throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _activeSends);
            MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, active);
            SendEntered = true;
            try
            {
                if (Stall) await _blocked.Task; // Deliberately ignores cancellation until Abort.
                if (SendDelayMs > 0) await Task.Delay(SendDelayMs, token);
                Messages.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            }
            finally { Interlocked.Decrement(ref _activeSends); }
        }
    }
}
