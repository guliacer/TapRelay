package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 覆盖 [maskHostInText]：把整句文案里出现的地址替换为脱敏形式。
 *
 * 前台通知的文案是把地址拼进整句的（`已连接：<host>`、`连接中 (1/2)：<host>`），
 * 没法逐段拼装，只能事后按原文替换。`WebSocketService` 那边只剩接线，逻辑都在这里。
 */
class MaskHostInTextTest {

    @Test
    fun masksTheHostInsideASentence() {
        assertEquals(
            "已连接：1*.0.2.2:1122",
            maskHostInText("已连接：10.0.2.2:1122", "10.0.2.2:1122")
        )
    }

    @Test
    fun masksHostFollowedByCandidateSuffix() {
        // 多候选地址时文案形如「连接中 (1/2)：<host>」，host 前面还有别的内容。
        assertEquals(
            "连接中 (1/2)：1**.1**.5.6:1122",
            maskHostInText("连接中 (1/2)：192.168.5.6:1122", "192.168.5.6:1122")
        )
    }

    @Test
    fun masksEveryOccurrence() {
        assertEquals(
            "1*.0.2.2 与 1*.0.2.2",
            maskHostInText("10.0.2.2 与 10.0.2.2", "10.0.2.2")
        )
    }

    @Test
    fun nullOrBlankHostLeavesTextAlone() {
        // 「未连接」这类文案没有地址，调用方传 null，不能因此破坏文案。
        assertEquals("未连接", maskHostInText("未连接", null))
        assertEquals("未连接", maskHostInText("未连接", ""))
        assertEquals("未连接", maskHostInText("未连接", "   "))
    }

    @Test
    fun textWithoutTheHostIsReturnedUnchanged() {
        // 中继模式文案只有房间号，不含地址；传入的 host 不在文案里，必须原样返回。
        assertEquals(
            "中继连接中，房间 abc123 配对中…",
            maskHostInText("中继连接中，房间 abc123 配对中…", "10.0.2.2")
        )
    }

    @Test
    fun nonAddressHostIsLeftAlone() {
        // maskIp 对非地址文本是恒等的，替换后文案不变（不会把中文/提示语改坏）。
        assertEquals(
            "请先填写 PC 局域网 IP",
            maskHostInText("请先填写 PC 局域网 IP", "请先填写 PC 局域网 IP")
        )
    }
}
