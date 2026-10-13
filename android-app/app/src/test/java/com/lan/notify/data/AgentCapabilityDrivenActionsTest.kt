package com.lan.notify.data

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentCapabilityDrivenActionsTest {

    private fun message(source: String, sessionId: String = "session-1") = NotifyMessage(
        type = "message",
        content = "[$source] task",
        time = "2026-08-24 10:00:00",
        source = source,
        sessionId = sessionId
    )

    @Test
    fun stoppableFallbackCoversSupportedCliAgents() {
        AgentCapabilityCache.clear()
        assertTrue(message("claude").isStoppable())
        assertTrue(message("codex").isStoppable())
        assertTrue(message("opencode").isStoppable())
        assertTrue(message("deepseekharness").isStoppable())
        assertTrue(message("freebuff").isStoppable())
    }

    @Test
    fun blankSessionIdIsNeverStoppable() {
        AgentCapabilityCache.clear()
        assertFalse(message("claude", sessionId = "  ").isStoppable())
    }

    @Test
    fun probedCapabilityControlsStopAction() {
        AgentCapabilityCache.replace(mapOf("codex" to setOf("agent.probe")))
        assertFalse(message("codex").isStoppable())

        AgentCapabilityCache.replace(mapOf("codex" to setOf("session.stop", "agent.probe")))
        assertTrue(message("codex").isStoppable())
    }
}
