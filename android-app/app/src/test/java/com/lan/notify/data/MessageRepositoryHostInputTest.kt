package com.lan.notify.data

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * 覆盖地址输入框的逐字符清洗 [MessageRepository.updateServerHost]。
 *
 * 背景（实测踩过的坑）：输入框是每次按键都过一遍归一化的，而
 * `ConnectionQrCodec.normalizeHost("10.0.2.2:")` 会因为「端口为空」判定非法
 * authority 返回空串。空串一旦被写回输入框，用户刚敲的整段地址就被清空，
 * **永远输不进端口**，也就无法连接任何非默认端口的地址。
 *
 * 修法是把两个角色拆开：
 * - `normalizeServerHost`：严格闸门，用于持久化与读取（空串 = 不可用）；
 * - `sanitizeServerHostInput`：在严格闸门之上容忍「还没输完」的中间态。
 *
 * 因此本文件有两组测试：一组证明输入框不再清空，另一组证明**持久化闸门没有被一起放宽**
 * （这是拆分后最容易发生的回归——把 `10.0.2.2:` 这种半成品写进 `server_host`）。
 */
class MessageRepositoryHostInputTest {

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

    private fun hostAfterTyping(text: String): String {
        MessageRepository.updateServerHost(text)
        return MessageRepository.state.value.serverHost
    }

    // ---- 输入框：必须容忍中间态 ------------------------------------------------

    @Test
    fun typingAnAddressWithPortNeverBlanksTheField() {
        val target = "10.0.2.2:1122"
        var typed = ""
        for (ch in target) {
            typed += ch
            val shown = hostAfterTyping(typed)
            // 逐字符输入过程中，输入框内容任何时候都不允许被清空。
            assertTrue("输入 \"$typed\" 时输入框被清空", shown.isNotBlank())
            // 而且必须与用户敲的内容完全一致，否则光标会跳、后缀会丢。
            assertEquals("输入 \"$typed\" 后输入框内容被改写", typed, shown)
        }
        assertEquals(target, MessageRepository.state.value.serverHost)
    }

    @Test
    fun trailingColonIsKeptWhilePortIsStillEmpty() {
        // 这是本次修复的最小复现：端口还没开始输，但地址本身必须留在框里。
        assertEquals("10.0.2.2:", hostAfterTyping("10.0.2.2:"))
    }

    @Test
    fun incompleteSchemeIsKept() {
        // "ws:" 还没敲完 "//"，normalizeHost 同样判非法；不能因此清空。
        assertEquals("ws:", hostAfterTyping("ws:"))
        assertEquals("wss:/", hostAfterTyping("wss:/"))
    }

    @Test
    fun surroundingWhitespaceIsStillTrimmed() {
        // 容忍中间态不等于放弃清洗：首尾空白仍要去掉。
        assertEquals("10.0.2.2", hostAfterTyping("  10.0.2.2  "))
    }

    @Test
    fun nonDefaultPortSurvivesIntact() {
        // 隧道/自定义端口场景：地址原文必须原样保留，不做二次推导。
        assertEquals("10.0.2.2:1122", hostAfterTyping("10.0.2.2:1122"))
        assertEquals("192.168.5.35:8080", hostAfterTyping("192.168.5.35:8080"))
    }

    @Test
    fun schemePrefixedTunnelAddressIsKeptVerbatim() {
        assertEquals("wss://tap.example.com", hostAfterTyping("wss://tap.example.com"))
    }

    // ---- 输入框：回环地址仍然是「明确拒绝」 ------------------------------------

    @Test
    fun loopbackIsRejectedInsteadOfBeingTreatedAsIntermediateState() {
        // 这条是上面「容忍中间态」的边界：如果清洗函数对空结果一律回退成原串，
        // `localhost` 就会被原样回填进输入框，重新变成可用地址。
        assertEquals("", hostAfterTyping("localhost"))
        assertEquals("", hostAfterTyping("127.0.0.1"))
        assertEquals("", hostAfterTyping("  LOCALHOST  "))
        assertEquals("", hostAfterTyping("http://localhost:1122"))
        assertEquals("", hostAfterTyping("ws://127.0.0.1:1122"))
    }

    // ---- 持久化闸门：不能被一起放宽 --------------------------------------------

    @Test
    fun halfFinishedAddressIsNotRecordedIntoHistory() {
        // 输入框容忍中间态，但历史地址只记「连接成功过」的地址。
        // 若 recordHostUsage 复用了宽松的清洗函数，这里就会混进 "10.0.2.2:"。
        MessageRepository.recordHostUsage("10.0.2.2:")
        MessageRepository.recordHostUsage("ws:")
        MessageRepository.recordHostUsage("10.0.2.2:99999")

        assertTrue(MessageRepository.hostHistory.value.isEmpty())
    }

    @Test
    fun strictGateStillAcceptsNormalisedAddresses() {
        // 反向保护：拆分不能把闸门改得过严，正常地址必须照常记录。
        MessageRepository.recordHostUsage("10.0.2.2:1122")

        assertEquals(listOf("10.0.2.2:1122"), MessageRepository.hostHistory.value.map { it.host })
    }
}
