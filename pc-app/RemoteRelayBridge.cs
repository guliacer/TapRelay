using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    /// <summary>
    /// Outbound WebSocket bridge that connects TapRelay PC to a relay server,
    /// enabling cross-network phone notifications without requiring LAN access.
    ///
    /// The relay is transparent: after a simple hello exchange (roomId-based
    /// pairing), every text frame from the phone is forwarded to the PC via
    /// <see cref="OnMessageFromPhone"/>, and every <c>SendToPhoneAsync</c>
    /// call is forwarded to all phones in the room.
    ///
    /// Reconnects automatically with exponential backoff.
    /// </summary>
    public sealed class RemoteRelayBridge : IDisposable
    {
        private static readonly TimeSpan ReconnectBaseDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private const int MaxMessageBytes = 512 * 1024;

        private readonly string _relayUrl;
        private readonly string _roomId;
        private readonly Action<string> _log;
        private readonly Func<string, CancellationToken, Task> _onMessageFromPhone;
        private readonly object _gate = new object();
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private int _reconnectAttempt;
        private bool _disposed;
        private bool _started;
        private bool _relayReady;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Fired when the relay connection state changes.
        /// </summary>
        public event Action<bool>? ConnectionStateChanged;

        /// <summary>
        /// Whether the bridge is currently connected to the relay.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _relayReady && _socket?.State == WebSocketState.Open;
                }
            }
        }

        /// <summary>
        /// The room ID shown to the user for phone pairing.
        /// </summary>
        public string RoomId => _roomId;

        /// <summary>
        /// The full relay URL (e.g. ws://relay.example.com:8080).
        /// </summary>
        public string RelayUrl => _relayUrl;

        /// <summary>
        /// Creates a relay bridge.
        /// </summary>
        /// <param name="relayUrl">WebSocket URL of the relay server (ws:// or wss://).</param>
        /// <param name="roomId">Shared room identifier for pairing.</param>
        /// <param name="onMessageFromPhone">Called with raw JSON when the phone sends a message through the relay.</param>
        /// <param name="log">Diagnostic log sink.</param>
        public RemoteRelayBridge(
            string relayUrl,
            string roomId,
            Func<string, CancellationToken, Task> onMessageFromPhone,
            Action<string> log)
        {
            _relayUrl = (relayUrl ?? throw new ArgumentNullException(nameof(relayUrl))).Trim();
            _roomId = (roomId ?? throw new ArgumentNullException(nameof(roomId))).Trim();
            _onMessageFromPhone = onMessageFromPhone ?? throw new ArgumentNullException(nameof(onMessageFromPhone));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            if (_roomId.Length == 0 || _roomId.Length > 64)
                throw new ArgumentException("roomId must be 1-64 characters", nameof(roomId));

            // Normalize URL: ensure ws:// or wss:// scheme
            if (!_relayUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !_relayUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                _relayUrl = "ws://" + _relayUrl;
            }
        }

        /// <summary>
        /// Start connecting to the relay. Safe to call multiple times.
        /// </summary>
        public void Start()
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || _started) return;
                _started = true;
                _reconnectAttempt = 0;
                var cts = new CancellationTokenSource();
                _cts = cts;
                token = cts.Token;
            }
            _log("Relay bridge starting: " + _relayUrl + " room=" + _roomId);
            _ = ConnectLoopAsync(token);
        }

        /// <summary>
        /// Send a JSON frame to the phone through the relay. Thread-safe.
        /// Returns immediately if not connected. Concurrent sends are serialized
        /// per bridge and have a bounded wait so notifications are not silently
        /// discarded merely because another send is in progress.
        /// </summary>
        public async Task SendToPhoneAsync(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            if (bytes.Length > MaxMessageBytes)
            {
                _log("Relay send rejected: message too large");
                return;
            }

            var acquired = false;
            try
            {
                using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _sendGate.WaitAsync(sendCts.Token);
                acquired = true;

                ClientWebSocket? socket;
                lock (_gate)
                {
                    if (_disposed || !_relayReady) return;
                    socket = _socket;
                }

                if (socket?.State != WebSocketState.Open) return;

                var segment = new ArraySegment<byte>(bytes);

                await socket.SendAsync(segment, WebSocketMessageType.Text, true, sendCts.Token);
            }
            catch (OperationCanceledException) when (_disposed)
            {
                // Disposal cancels an in-flight send; it is not a relay error.
            }
            catch (Exception ex)
            {
                _log("Relay send failed: " + ex.Message);
            }
            finally
            {
                if (acquired)
                {
                    _sendGate.Release();
                }
            }
        }

        /// <summary>
        /// Stop and disconnect. Can be restarted with <see cref="Start"/> after disposing and recreating.
        /// </summary>
        public void Dispose()
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _started = false;
                cts = _cts;
                _cts = null;
            }
            cts?.Cancel();
            DisconnectSocket();
            cts?.Dispose();
            // Keep the gate alive until pending send continuations finish. Disposing
            // it here races with WaitAsync/Release and is unnecessary for this object.
        }

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndReadAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("Relay connection error: " + ex.Message);
                }

                if (token.IsCancellationRequested) break;

                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, _reconnectAttempt),
                             MaxReconnectDelay.TotalMilliseconds));
                _reconnectAttempt++;
                _log($"Relay reconnecting in {delay.TotalSeconds:F0}s (attempt {_reconnectAttempt})");
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ConnectAndReadAsync(CancellationToken token)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent", "TapRelay-RelayBridge/1.0");

            var url = _relayUrl;

            var accepted = false;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _socket = socket;
                    _relayReady = false;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                socket.Dispose();
                return;
            }

            var connected = false;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new Uri(url), connectCts.Token);

                _log("Relay WebSocket connected: " + url);

                // Send hello
                var hello = JsonSerializer.Serialize(new
                {
                    type = "relay.hello",
                    role = "host",
                    roomId = _roomId
                });
                await SendRawAsync(socket, hello, token);

                // Read hello_ack
                var ackJson = await ReadRawAsync(socket, token);
                if (ackJson == null) throw new InvalidOperationException("Relay closed before hello_ack");

                try
                {
                    using var ackDoc = JsonDocument.Parse(ackJson);
                    var ackType = ackDoc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (ackType != "relay.hello_ack")
                    {
                        var code = ackDoc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : "unknown";
                        throw new InvalidOperationException($"Relay rejected hello: {code}");
                    }
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("Invalid hello_ack from relay");
                }

                _log($"Relay room joined: {_roomId}, ready for phone connections");
                _reconnectAttempt = 0;
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_socket, socket)) return;
                    _relayReady = true;
                }
                connected = true;
                ConnectionStateChanged?.Invoke(true);

                // Read loop: forward relay -> phone messages to the handler.
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var message = await ReadRawAsync(socket, token);
                    if (message == null) break;

                    // Ignore relay control frames (hello_ack after host replace, errors).
                    try
                    {
                        using var doc = JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("type", out var typeEl))
                        {
                            var type = typeEl.GetString();
                            if (type == "relay.hello_ack" || type == "relay.error")
                            {
                                _log("Relay control: " + type + " " + message);
                                continue;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Not JSON - unlikely but forward anyway.
                    }

                    // Forward to the message handler (same as direct WebSocket client).
                    try
                    {
                        await _onMessageFromPhone(message, token);
                    }
                    catch (Exception ex)
                    {
                        _log("Relay message handler error: " + ex.Message);
                    }
                }
            }
            finally
            {
                ReleaseSocket(socket, connected);
            }
        }

        private static async Task<string?> ReadRawAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new System.IO.MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (stream.Length + result.Count > MaxMessageBytes)
                    {
                        throw new InvalidOperationException("Relay message too large");
                    }
                    stream.Write(buffer, 0, result.Count);
                }
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private async Task SendRawAsync(ClientWebSocket socket, string json, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var acquired = false;
            try
            {
                await _sendGate.WaitAsync(token);
                acquired = true;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                if (acquired)
                {
                    _sendGate.Release();
                }
            }
        }

        private void DisconnectSocket()
        {
            ClientWebSocket? socket;
            lock (_gate)
            {
                socket = _socket;
                _socket = null;
                _relayReady = false;
            }

            if (socket == null) return;

            ConnectionStateChanged?.Invoke(false);

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cts.Token).ContinueWith(_ => { });
                }
            }
            catch { /* best-effort */ }
            finally
            {
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }

        private void ReleaseSocket(ClientWebSocket socket, bool notifyDisconnected)
        {
            var ownsSocket = false;
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _relayReady = false;
                    ownsSocket = true;
                }
            }

            if (!ownsSocket)
            {
                return;
            }

            if (notifyDisconnected)
            {
                ConnectionStateChanged?.Invoke(false);
            }

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "connection ended", cts.Token).ContinueWith(_ => { });
                }
            }
            catch { /* best-effort */ }
            finally
            {
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
