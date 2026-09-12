package com.lan.notify.ui

import com.lan.notify.data.TaskState
import org.junit.Assert.assertEquals
import org.junit.Test

class IslandPresentationTest {

    @Test
    fun peekUsesActionableStatusTitlesAndCompactFallbacks() {
        assertEquals("需要处理", peekStatusTitle(TaskState.Waiting, compact = false))
        assertEquals("待处理", peekStatusTitle(TaskState.Waiting, compact = true))
        assertEquals("执行失败", peekStatusTitle(TaskState.Failed, compact = false))
        assertEquals("失败", peekStatusTitle(TaskState.Failed, compact = true))
        assertEquals("已完成", peekStatusTitle(TaskState.Completed, compact = false))
    }

    @Test
    fun unreadCountCapsAtNinetyNinePlus() {
        assertEquals("", unreadCountLabel(0))
        assertEquals("1", unreadCountLabel(1))
        assertEquals("99", unreadCountLabel(99))
        assertEquals("99+", unreadCountLabel(100))
    }
}
