package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Test

/** 覆盖「隐藏地址」的脱敏函数：IPv4 与域名都必须脱敏，非地址文本原样返回。 */
class MaskIpTest {

    @Test
    fun masksIpv4KeepingPort() {
        assertEquals("1**.1**.5.6:1122", maskIp("192.168.5.6:1122"))
    }

    @Test
    fun masksIpv4WithSchemeAndPort() {
        assertEquals("ws://1**.1**.5.6:1122", maskIp("ws://192.168.5.6:1122"))
    }

    @Test
    fun masksDomainKeepingPort() {
        // 当前默认地址是隧道域名（隧道优先），必须能被脱敏，否则「隐藏地址」点击无反应。
        assertEquals("w**.g*******.d****.o**:443", maskIp("win.guliacer.dpdns.org:443"))
    }

    @Test
    fun masksDomainWithScheme() {
        assertEquals("wss://w**.g*******.d****.o**", maskIp("wss://win.guliacer.dpdns.org"))
    }

    @Test
    fun masksBareIpv4() {
        assertEquals("1**.1**.5.6", maskIp("192.168.5.6"))
    }

    @Test
    fun masksBareDomain() {
        assertEquals("e******.c**", maskIp("example.com"))
    }

    @Test
    fun singleCharacterSegmentsHaveNothingToHide() {
        // 模拟器验证时用的地址：段长为 1 的段没有可隐藏的字符，原样保留。
        // （旧 KDoc 里写的示例 `1**.1**.*.3*` 与实现不符，这里以实现为准。）
        assertEquals("1*.0.2.2", maskIp("10.0.2.2"))
        assertEquals("1*.0.2.2:1122", maskIp("10.0.2.2:1122"))
    }

    @Test
    fun leavesNonAddressTextUntouched() {
        assertEquals(
            "未找到可用地址，请填写网络设置中的对外地址。",
            maskIp("未找到可用地址，请填写网络设置中的对外地址。")
        )
        assertEquals("", maskIp(""))
        assertEquals("   ", maskIp("   "))
    }
}
