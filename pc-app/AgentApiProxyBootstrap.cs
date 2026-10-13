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
        /// <param name="log">用户可见的日志出口（首页活动摘要 + 诊断文件）。</param>
        /// <param name="detailLog">
        /// 仅诊断文件用的出口。代理在解析可执行文件路径、探测候选目录这类场景会刷出
        /// 大量实现细节，它们对排查有用、对用户没意义，所以走这条通道。
        /// 省略时回退到 <paramref name="log"/>，保持既有调用方行为不变。
        /// </param>
        public static void Bootstrap(Action<string> log, Action<string>? detailLog = null)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            detailLog ??= log;

            // Freebuff: local orchestrator proxy for task control and capability probing.
            AgentApiProxyRegistry.Register(new FreebuffApiProxy(log));

            // CLI agents: shell command proxies for stop control and capability probing.
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.Claude, log, detailLog));
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.Codex, log, detailLog));
            AgentApiProxyRegistry.Register(new ShellAgentApiProxy(ShellAgentConfig.OpenCode, log, detailLog));
            AgentApiProxyRegistry.Register(new DeepSeekHarnessApiProxy(log));

            // GUI agents remain registered for discovery and future local controls.
            AgentApiProxyRegistry.Register(new ClaudeHahaAgentProxy(log, detailLog));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.TraeCode, log, detailLog));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.TraeWork, log, detailLog));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.ZCode, log, detailLog));
            AgentApiProxyRegistry.Register(new WorkBuddyAcpAgentProxy(log, detailLog));
            AgentApiProxyRegistry.Register(new WindowFocusAgentProxy(WindowFocusAgentProxy.Doubao, log, detailLog));
        }
    }
}
