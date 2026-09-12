using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    internal sealed class WebSocketSendChannel
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public WebSocketSendChannel(WebSocket socket) { _socket = socket; }

        public async Task ExecuteAsync(Func<CancellationToken, Task> send, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var acquired = false;
            try
            {
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                deadline.Token.ThrowIfCancellationRequested();
                if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived)
                    throw new WebSocketException("连接已关闭");

                var operation = send(deadline.Token);
                var cancelled = Task.Delay(Timeout.Infinite, deadline.Token);
                if (await Task.WhenAny(operation, cancelled).ConfigureAwait(false) != operation)
                {
                    // Observe late failures even if a transport ignores cancellation.
                    _ = operation.ContinueWith(task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw new OperationCanceledException("WebSocket 发送超时或取消", deadline.Token);
                }
                await operation.ConfigureAwait(false);
            }
            catch
            {
                try { _socket.Abort(); } catch (ObjectDisposedException) { }
                throw;
            }
            finally
            {
                deadline.Cancel();
                if (acquired) _gate.Release();
            }
        }
    }
}
