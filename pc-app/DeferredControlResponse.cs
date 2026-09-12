using System;

namespace TapRelay
{
    /// <summary>
    /// A control response that must reach the phone before its local side effect starts.
    /// This is used by power actions: suspending the PC before the WebSocket send completes
    /// would otherwise leave the phone with no acknowledgement.
    /// </summary>
    public sealed class DeferredControlResponse
    {
        public DeferredControlResponse(object payload, Action onResponseSent)
        {
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            OnResponseSent = onResponseSent ?? throw new ArgumentNullException(nameof(onResponseSent));
        }

        public object Payload { get; }

        public Action OnResponseSent { get; }
    }
}
