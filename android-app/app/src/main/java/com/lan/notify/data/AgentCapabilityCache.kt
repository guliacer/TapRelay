package com.lan.notify.data

/**
 * In-memory cache of agent capabilities discovered through the `agent.probe` RPC.
 *
 * The PC exposes a batch probe (`agent.probe` without an `agentKey`) that returns
 * every registered agent and its capability set. The phone consumes this on connect
 * so notification actions ("停止任务", "发送指令", …) are generated from the agent's
 * *actual* capabilities instead of a hard-coded allow-list — the same "接口即入口"
 * model used by dsh-island.
 *
 * The cache is keyed by normalized source key (see [canonicalSourceKey]) and is
 * deliberately non-authoritative: when an agent has not been probed yet (offline PC,
 * older PC without batch probe support) the legacy allow-list in
 * [NotifyMessage.isStoppable] still applies, so no capability regression is possible.
 */
object AgentCapabilityCache {

    const val CAP_SESSION_SEND = "session.send"
    const val CAP_SESSION_STOP = "session.stop"
    const val CAP_AGENT_PROBE = "agent.probe"

    @Volatile
    private var capabilities: Map<String, Set<String>> = emptyMap()

    /** Replace the whole cache with the result of a batch probe. */
    fun replace(entries: Map<String, Set<String>>) {
        capabilities = entries.mapValues { (_, caps) -> caps.toSet() }
    }

    /** Merge a single agent's capabilities into the cache. */
    fun put(agentKey: String, caps: Set<String>) {
        val key = agentKey.trim().lowercase()
        if (key.isBlank()) return
        capabilities = capabilities + (key to caps.toSet())
    }

    /** True when the agent's capability set is already known locally. */
    fun hasEntry(agentKey: String): Boolean =
        capabilities.containsKey(agentKey.trim().lowercase())

    fun supports(agentKey: String, capability: String): Boolean =
        capabilities[agentKey.trim().lowercase()]?.contains(capability) == true

    fun clear() {
        capabilities = emptyMap()
    }
}
