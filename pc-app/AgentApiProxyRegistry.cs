using System;
using System.Collections.Generic;

namespace TapRelay
{
    public static class AgentApiProxyRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, IAgentApiProxy> Proxies =
            new Dictionary<string, IAgentApiProxy>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Action> DiscoveryCallbacks = new List<Action>();
        private static bool _discoveryRun;

        public static void OnDiscover(Action callback)
        {
            lock (Gate)
            {
                DiscoveryCallbacks.Add(callback);
                if (_discoveryRun)
                {
                    try { callback(); } catch { }
                }
            }
        }

        public static void Register(IAgentApiProxy proxy)
        {
            if (proxy == null) throw new ArgumentNullException(nameof(proxy));
            var key = NormalizeKey(proxy.AgentKey);
            lock (Gate)
            {
                Proxies[key] = proxy;
            }
        }

        public static IAgentApiProxy GetOrNull(string agentKey)
        {
            var key = NormalizeKey(agentKey);
            lock (Gate)
            {
                if (!_discoveryRun)
                {
                    _discoveryRun = true;
                    foreach (var callback in DiscoveryCallbacks)
                    {
                        try { callback(); } catch { }
                    }
                }

                Proxies.TryGetValue(key, out var proxy);
                return proxy;
            }
        }

        public static List<string> GetRegisteredKeys()
        {
            lock (Gate)
            {
                if (!_discoveryRun)
                {
                    _discoveryRun = true;
                    foreach (var callback in DiscoveryCallbacks)
                    {
                        try { callback(); } catch { }
                    }
                }

                return new List<string>(Proxies.Keys);
            }
        }

        private static string NormalizeKey(string key)
        {
            return (key ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}