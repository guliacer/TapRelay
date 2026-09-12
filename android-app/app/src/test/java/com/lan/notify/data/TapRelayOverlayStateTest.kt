package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TapRelayOverlayStateTest {

    private fun message(
        id: String,
        status: String = NotifyMessage.STATUS_COMPLETED,
        requiresAction: Boolean = false
    ) = NotifyMessage(
        type = "event",
        content = "task",
        time = "2026-08-17T10:00:00+08:00",
        status = status,
        source = "claude",
        taskId = id,
        eventId = id,
        requiresAction = requiresAction
    )

    @Test
    fun noUnreadAlwaysHides() {
        assertEquals(
            TapRelayOverlayState.Hidden,
            deriveOverlayState(emptyList(), OverlayExpansion.Compact)
        )
        assertEquals(
            TapRelayOverlayState.Hidden,
            deriveOverlayState(emptyList(), OverlayExpansion.Peek)
        )
        assertEquals(
            TapRelayOverlayState.Hidden,
            deriveOverlayState(emptyList(), OverlayExpansion.Expanded)
        )
    }

    @Test
    fun unreadDefaultsToPeekCapsule() {
        val state = deriveOverlayState(listOf(message("a")), OverlayExpansion.Peek)
        assertEquals(OverlayPhase.Peek, state.phase())
        assertEquals(1, (state as TapRelayOverlayState.Peek).events.size)
    }

    @Test
    fun legacyCompactExpansionStillShowsPeek() {
        val state = deriveOverlayState(listOf(message("a")), OverlayExpansion.Compact)
        assertEquals(OverlayPhase.Peek, state.phase())
        assertEquals(1, (state as TapRelayOverlayState.Peek).events.size)
    }

    @Test
    fun tapAdvancesPeekToExpanded() {
        val state = deriveOverlayState(listOf(message("a")), OverlayExpansion.Peek.advance())
        assertEquals(OverlayPhase.Expanded, state.phase())
    }

    @Test
    fun expandedStaysExpandedWhenNewEventsArrive() {
        val state = deriveOverlayState(listOf(message("a")), OverlayExpansion.Expanded)
        assertEquals(OverlayPhase.Expanded, state.phase())
    }

    @Test
    fun peekStaysPeekWhenNewEventsArrive() {
        val state = deriveOverlayState(listOf(message("a"), message("b")), OverlayExpansion.Peek)
        assertEquals(OverlayPhase.Peek, state.phase())
        assertEquals(2, state.events().size)
    }

    @Test
    fun phaseDiscriminatesAllFourStates() {
        assertEquals(OverlayPhase.Hidden, TapRelayOverlayState.Hidden.phase())
        assertEquals(OverlayPhase.Compact, TapRelayOverlayState.Compact(emptyList()).phase())
        assertEquals(OverlayPhase.Peek, TapRelayOverlayState.Peek(emptyList()).phase())
        assertEquals(OverlayPhase.Expanded, TapRelayOverlayState.Expanded(emptyList()).phase())
    }

    @Test
    fun pendingApprovalIsForegroundOverNewerCompleted() {
        val completed = message("newer", status = NotifyMessage.STATUS_COMPLETED)
        val waiting = message("older", status = NotifyMessage.STATUS_WAITING, requiresAction = true)
        assertEquals("older", overlayForeground(listOf(completed, waiting))?.id)
        assertTrue(hasPendingApproval(listOf(completed, waiting)))
    }

    @Test
    fun adapterWaitingAliasesRemainActionable() {
        listOf("pending", "awaiting_input", "awaiting-user", "requires_action", "idle").forEach { status ->
            assertTrue(message("id-$status", status = status, requiresAction = true).isWaitingTask())
        }
        assertFalse(message("id-pending", status = "pending", requiresAction = false).isWaitingTask())
    }

    @Test
    fun noApprovalFallsBackToNewest() {
        val first = message("first")
        val second = message("second")
        assertEquals("first", overlayForeground(listOf(first, second))?.id)
        assertFalse(hasPendingApproval(listOf(first, second)))
    }

    @Test
    fun emptyListHasNoForeground() {
        assertNull(overlayForeground(emptyList()))
        assertFalse(hasPendingApproval(emptyList()))
    }

    @Test
    fun advanceStopsAtExpanded() {
        assertEquals(OverlayExpansion.Expanded, OverlayExpansion.Compact.advance())
        assertEquals(OverlayExpansion.Expanded, OverlayExpansion.Peek.advance())
        assertEquals(OverlayExpansion.Expanded, OverlayExpansion.Expanded.advance())
    }
}
