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
    fun sendableFallbackCoversCliAndFreebuff() {
        // 未探到能力时退回白名单兜底。
        AgentCapabilityCache.clear()
        assertTrue(message("claude").canSendMessage())
        assertTrue(message("codex").canSendMessage())
        assertTrue(message("opencode").canSendMessage())
        assertTrue(message("deepseekharness").canSendMessage())
        assertTrue(message("freebuff").canSendMessage())
        assertTrue(message("trae").canSendMessage())
        assertTrue(message("traework").canSendMessage())
        assertTrue(message("zcode").canSendMessage())
        assertTrue(message("workbuddy").canSendMessage())
        assertTrue(message("workbuddy-remote").canSendMessage())
        assertTrue(message("doubao").canSendMessage())
    }

    @Test
    fun blankSessionIdNeverSendable() {
        AgentCapabilityCache.clear()
        assertFalse(message("claude", sessionId = "  ").canSendMessage())
    }

    @Test
    fun probedCapabilityOverridesFallback() {
        // 探到 zcode 支持 session.send 后，即使不在兜底白名单里也能发送。
        AgentCapabilityCache.replace(mapOf("zcode" to setOf("session.send", "agent.probe")))
        assertTrue(message("zcode").canSendMessage())
        // Freebuff advertises both continuation and native orchestrator stop.
        assertTrue(message("freebuff").canSendMessage())
        assertTrue(message("freebuff").isStoppable())
    }

    @Test
    fun probedWithoutSendCapabilityIsNotSendable() {
        // 探到某 agent 但没有 session.send 能力，即使它在兜底白名单里也不显示发送。
        AgentCapabilityCache.replace(mapOf("claude" to setOf("agent.probe")))
        assertFalse(message("claude").canSendMessage())
    }

    @Test
    fun legacyGuiProbeStillShowsContinue() {
        // Older PC builds reported only agent.probe for the window-focus proxy,
        // while the session.send/focus route was already available.
        AgentCapabilityCache.replace(mapOf("workbuddy" to setOf("agent.probe")))
        assertTrue(message("workbuddy").canSendMessage())
        assertTrue(message("workbuddy-remote").canSendMessage())
    }
}
