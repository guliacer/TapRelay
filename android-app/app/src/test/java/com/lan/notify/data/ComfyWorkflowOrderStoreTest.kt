package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ComfyWorkflowOrderStoreTest {
    private fun workflow(id: String) = ComfyWorkflowSummary(id = id, name = id, category = "")

    @Test
    fun pinnedWorkflowsStayAboveOthersAndKeepTheirOrder() {
        val workflows = listOf(workflow("a"), workflow("b"), workflow("c"))
        val order = ComfyWorkflowOrderStore.Order(
            ids = listOf("a", "b", "c"),
            pinned = setOf("c")
        )

        assertEquals(
            listOf("c", "a", "b"),
            ComfyWorkflowOrderStore.apply(workflows, order).map { it.id }
        )
    }

    @Test
    fun togglePinMovesWorkflowToTheTopAndBack() {
        val initial = ComfyWorkflowOrderStore.Order(ids = listOf("a", "b", "c"))
        val pinned = ComfyWorkflowOrderStore.togglePin(initial, "b")

        assertTrue("b" in pinned.pinned)
        assertEquals(listOf("b", "a", "c"), pinned.ids)
        assertEquals(
            listOf("b", "a", "c"),
            ComfyWorkflowOrderStore.apply(
                listOf(workflow("a"), workflow("b"), workflow("c")),
                pinned
            ).map { it.id }
        )

        val unpinned = ComfyWorkflowOrderStore.togglePin(pinned, "b")
        assertEquals(
            listOf("b", "a", "c"),
            ComfyWorkflowOrderStore.apply(
                listOf(workflow("a"), workflow("b"), workflow("c")),
                unpinned
            ).map { it.id }
        )
    }

    @Test
    fun normalizeDropsDeletedIdsAndAppendsNewOnes() {
        val order = ComfyWorkflowOrderStore.Order(
            ids = listOf("gone", "b", "a"),
            pinned = setOf("gone", "a")
        )

        val normalized = ComfyWorkflowOrderStore.normalize(
            listOf(workflow("a"), workflow("b"), workflow("c")),
            order
        )

        assertEquals(listOf("b", "a", "c"), normalized.ids)
        assertEquals(setOf("a"), normalized.pinned)
    }

    @Test
    fun reorderKeepsUnknownIdsAndPinnedState() {
        val order = ComfyWorkflowOrderStore.Order(
            ids = listOf("a", "b", "c", "d"),
            pinned = setOf("a")
        )

        val reordered = ComfyWorkflowOrderStore.reorder(order, listOf("a", "c", "b"))

        assertEquals(listOf("a", "c", "b", "d"), reordered.ids)
        assertEquals(setOf("a"), reordered.pinned)
    }

    @Test
    fun applyKeepsServerOrderForNewWorkflows() {
        val workflows = listOf(workflow("a"), workflow("b"), workflow("new2"), workflow("new1"))
        val order = ComfyWorkflowOrderStore.Order(ids = listOf("b", "a"))

        assertEquals(
            listOf("b", "a", "new2", "new1"),
            ComfyWorkflowOrderStore.apply(workflows, order).map { it.id }
        )
    }
}
