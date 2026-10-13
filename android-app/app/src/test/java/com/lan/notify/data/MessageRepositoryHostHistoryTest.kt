package com.lan.notify.data

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * 覆盖历史地址的运行时入口 [MessageRepository.recordHostUsage]。
 *
 * [HostHistoryTest] 验证的是纯函数与 JSON 编解码；这里验证的是它们**串起来之后**的行为——
 * 也就是 `WebSocketService.markSocketVerified` 真正会走到的那条链路
 * （`normalizeServerHost` → `mergeHostHistory` → `_hostHistory`）。
 *
 * 单测环境没有 Context，`MessageRepository.initialize` 不会被调用，
 * `localStore` 始终未初始化，因此持久化分支被安全跳过，只断言内存状态。
 * 这也正是"未初始化时不崩"这条防御性写法的回归保护。
 */
class MessageRepositoryHostHistoryTest {

    @Before
    fun setUp() {
        MessageRepository.clearHostHistory()
        MessageRepository.updateServerHost("")
    }

    @After
    fun tearDown() {
        MessageRepository.clearHostHistory()
        MessageRepository.updateServerHost("")
    }

    @Test
    fun connectionsAreRecordedNewestFirst() {
        MessageRepository.recordHostUsage("10.0.0.1")
        MessageRepository.recordHostUsage("10.0.0.2")

        assertEquals(
            listOf("10.0.0.2", "10.0.0.1"),
            MessageRepository.hostHistory.value.map { it.host }
        )
    }

    @Test
    fun schemeVariantsOfTheSameHostCollapseIntoOneRecord() {
        // normalizeServerHost 会原样保留 ws:// 前缀，所以这几种写法在字符串层面完全不同。
        MessageRepository.recordHostUsage("192.168.5.6")
        MessageRepository.recordHostUsage("ws://192.168.5.6:1122")
        MessageRepository.recordHostUsage("  http://192.168.5.6/  ")

        assertEquals(1, MessageRepository.hostHistory.value.size)
    }

    @Test
    fun tlsTunnelStaysASeparateRecord() {
        MessageRepository.recordHostUsage("192.168.5.6")
        MessageRepository.recordHostUsage("wss://tap.example.com")

        assertEquals(2, MessageRepository.hostHistory.value.size)
        assertEquals("wss://tap.example.com", MessageRepository.hostHistory.value.first().host)
    }

    @Test
    fun unusableHostsAreRejected() {
        MessageRepository.recordHostUsage("")
        MessageRepository.recordHostUsage("   ")
        // normalizeServerHost 把本机回环地址归一化为空串，不允许进历史。
        MessageRepository.recordHostUsage("localhost")
        MessageRepository.recordHostUsage("127.0.0.1")

        assertTrue(MessageRepository.hostHistory.value.isEmpty())
    }

    @Test
    fun historyNeverExceedsTheCap() {
        repeat(MAX_HOST_HISTORY + 5) { MessageRepository.recordHostUsage("10.0.0.$it") }

        assertEquals(MAX_HOST_HISTORY, MessageRepository.hostHistory.value.size)
        assertEquals("10.0.0.14", MessageRepository.hostHistory.value.first().host)
    }

    @Test
    fun clearingHistoryDoesNotTouchTheActiveServerHost() {
        MessageRepository.updateServerHost("10.0.0.7")
        MessageRepository.recordHostUsage("10.0.0.7")

        MessageRepository.clearHostHistory()

        assertTrue(MessageRepository.hostHistory.value.isEmpty())
        // server_host 是 hasKnownGoodHost / onStart 自动重连的数据源，清历史不能碰它。
        assertEquals("10.0.0.7", MessageRepository.state.value.serverHost)
    }
}
