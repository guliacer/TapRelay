using System;

namespace TapRelay
{
    /// <summary>
    /// Registers all built-in agent API proxies during app startup.
    /// Called once from MainForm after the log sink is available.
    /// </summary>
    public static class AgentApiProxyBootstrap
    {
        /// <summary>
        /// Register all proxies into <see cref="AgentApiProxyRegistry"/>.
        /// Safe to call multiple times; proxies are registered once.
        /// </summary>
        public static void Bootstrap(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            // Freebuff: local orchestrator HTTP proxy (session.send, session.stop, agent.probe)
            AgentApiProxyRegistry.Register(new FreebuffApiProxy(log));

            // CLI agents: shell command proxies (session.send, agent.probe)
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.Claude, log));
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.Codex, log));
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.OpenCode, log));
            // DeepSeek Desktop's bundled dsh does not implement a session CLI
            // command. Its supported continuation path is the local session API.
            AgentApiProxyRegistry.Register(new DeepSeekHarnessApiProxy(log));

            // GUI agents: window-focus proxies. ZCode additionally pastes and
            // submits session.send text in its desktop composer. Claude Code
            // Haha exposes a local sidecar WebSocket, so use its session API
            // instead of treating the Electron renderer as a coordinate target.
            AgentApiProxyRegistry.Register(new ClaudeHahaAgentProxy(log));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.TraeCode, log));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.TraeWork, log));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.ZCode, log));
            // WorkBuddy is a Chromium UI with no accessible Edit control. Use its
            // per-session local ACP endpoint so a successful send is a real prompt
            // accepted by the session rather than a best-effort clipboard paste.
            AgentApiProxyRegistry.Register(new WorkBuddyAcpAgentProxy(log));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.Doubao, log));
        }
    }
}
