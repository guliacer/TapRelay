package com.lan.notify.ui

import org.junit.Assert.assertEquals
import org.junit.Test

class AppDestinationTest {
    @Test
    fun destinationsKeepStableUserOrder() {
        assertEquals(
            listOf(AppDestination.TASK, AppDestination.COMFY_UI, AppDestination.DEVICE),
            AppDestination.entries
        )
        assertEquals("任务", AppDestination.TASK.label)
        assertEquals("ComfyUI", AppDestination.COMFY_UI.label)
        assertEquals("设备", AppDestination.DEVICE.label)
    }
}
