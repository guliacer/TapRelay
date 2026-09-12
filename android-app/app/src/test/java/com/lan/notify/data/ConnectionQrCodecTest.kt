package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionQrCodecTest {
    @Test
    fun tapRelayPayloadRoundTripsIpv4AndPort() {
        val raw = ConnectionQrCodec.encode("192.168.5.6", 2233)
        assertEquals(ConnectionEndpoint("192.168.5.6", 2233), ConnectionQrCodec.decode(raw))
    }

    @Test
    fun tapRelayPayloadRoundTripsDnsName() {
        // 443 端口推断为 wss 公网隧道。
        val raw = ConnectionQrCodec.encode("notify.example.com", 443)
        assertEquals(ConnectionEndpoint("notify.example.com", 443, "wss"), ConnectionQrCodec.decode(raw))
    }

    @Test
    fun rawWebSocketEndpointRemainsSupported() {
        assertEquals(
            ConnectionEndpoint("192.168.1.20", 1122),
            ConnectionQrCodec.decode("ws://192.168.1.20:1122")
        )
    }

    @Test
    fun invalidSchemeAndHostAreRejected() {
        assertNull(ConnectionQrCodec.decode("https://example.com:443"))
        assertNull(ConnectionQrCodec.decode("taprelay://connect?host=&port=1122"))
        assertNull(ConnectionQrCodec.decode("taprelay://wrong?host=192.168.1.2&port=1122"))
    }

    @Test
    fun editableHostKeepsOptionalPort() {
        assertEquals("192.168.1.2:2233", ServerConfig.normalizeHost("ws://192.168.1.2:2233/"))
        assertEquals("notify.example.com", ServerConfig.normalizeHost("https://notify.example.com"))
    }

    @Test
    fun decodeListReturnsPrimaryAndAlternativesInOrder() {
        val raw = "taprelay://connect?host=192.168.5.6&port=1122&alt1=win.example.com:1122"
        assertEquals(
            listOf(ConnectionEndpoint("192.168.5.6", 1122), ConnectionEndpoint("win.example.com", 1122)),
            ConnectionQrCodec.decodeList(raw)
        )
    }

    @Test
    fun decodeListParsesWssTunnelAlternative() {
        // PC 端对公网隧道广告地址生成 wss://域名（Cloudflare 隧道只暴露 443）。
        val raw = "taprelay://connect?host=192.168.5.6&port=1122&alt1=wss%3A%2F%2Fwin.guliacer.dpdns.org"
        val endpoints = ConnectionQrCodec.decodeList(raw)
        assertEquals(2, endpoints.size)
        assertEquals(ConnectionEndpoint("192.168.5.6", 1122, "ws"), endpoints[0])
        assertEquals(ConnectionEndpoint("win.guliacer.dpdns.org", 443, "wss"), endpoints[1])
        assertEquals("wss://win.guliacer.dpdns.org:443", endpoints[1].url)
    }

    @Test
    fun decodeWssRawEndpointKeepsScheme() {
        val endpoint = ConnectionQrCodec.decode("wss://win.guliacer.dpdns.org")
        assertEquals(ConnectionEndpoint("win.guliacer.dpdns.org", 443, "wss"), endpoint)
    }

    @Test
    fun serverConfigBuildsWssUrlForTunnelHost() {
        assertEquals(
            "wss://win.guliacer.dpdns.org:443",
            ServerConfig("wss://win.guliacer.dpdns.org").webSocketUrl
        )
        assertEquals(
            "wss://win.guliacer.dpdns.org:443",
            ServerConfig("wss://win.guliacer.dpdns.org:443").webSocketUrl
        )
        // LAN 地址保持 ws:// + 1122。
        assertEquals(
            "ws://192.168.5.6:1122",
            ServerConfig("192.168.5.6:1122").webSocketUrl
        )
    }

    @Test
    fun decodeListInfersWssForPrimaryPort443() {
        // 隧道优先：PC 端把 wss://域名 作为主地址（host/port 无 scheme），
        // 端口 443 应推断为 wss 隧道而非明文 ws。
        val raw = "taprelay://connect?host=win.guliacer.dpdns.org&port=443&alt1=192.168.5.6%3A1122"
        val endpoints = ConnectionQrCodec.decodeList(raw)
        assertEquals(2, endpoints.size)
        assertEquals(ConnectionEndpoint("win.guliacer.dpdns.org", 443, "wss"), endpoints[0])
        assertEquals("wss://win.guliacer.dpdns.org:443", endpoints[0].url)
        assertEquals(ConnectionEndpoint("192.168.5.6", 1122, "ws"), endpoints[1])
    }

    @Test
    fun decodeListTreatsSingleEndpointLikeDecode() {
        val raw = ConnectionQrCodec.encode("notify.example.com", 443)
        assertEquals(
            listOf(ConnectionEndpoint("notify.example.com", 443, "wss")),
            ConnectionQrCodec.decodeList(raw)
        )
    }

    @Test
    fun decodeListRejectsInvalidPayloads() {
        assertTrue(ConnectionQrCodec.decodeList("https://example.com").isEmpty())
        assertTrue(ConnectionQrCodec.decodeList("not-a-qr").isEmpty())
        assertTrue(ConnectionQrCodec.decodeList("").isEmpty())
    }

    @Test
    fun normalizeHostRejectsMalformedAuthorities() {
        assertEquals("", ServerConfig.normalizeHost("ws:"))
        assertEquals("", ServerConfig.normalizeHost("host:"))
        assertEquals("", ServerConfig.normalizeHost("host:abc"))
        assertEquals("", ServerConfig.normalizeHost("host:0"))
        assertEquals("", ServerConfig.normalizeHost("host:65536"))
        assertEquals("192.168.1.2:1122", ServerConfig.normalizeHost("192.168.1.2:1122"))
        assertEquals("host.example.com:1122", ServerConfig.normalizeHost("host.example.com:1122"))
    }

    @Test
    fun extractRelayParsesRoomAndRelayFromPayload() {
        val raw = "taprelay://connect?room=ABC123&relay=ws%3A%2F%2Frelay.example.com%3A8080"
        val relay = ConnectionQrCodec.extractRelay(raw)
        assertTrue(relay != null)
        assertEquals("ABC123", relay!!.roomId)
        assertEquals("ws://relay.example.com:8080", relay.url)
    }

    @Test
    fun extractRelayHandlesWssRelay() {
        val raw = "taprelay://connect?room=XYZ999&relay=wss%3A%2F%2Frelay.example.com"
        val relay = ConnectionQrCodec.extractRelay(raw)
        assertTrue(relay != null)
        assertEquals("XYZ999", relay!!.roomId)
        assertEquals("wss://relay.example.com:443", relay.url)
    }

    @Test
    fun extractRelayReturnsNullForLanOnlyPayload() {
        val raw = "taprelay://connect?host=192.168.5.6&port=1122"
        val relay = ConnectionQrCodec.extractRelay(raw)
        assertTrue(relay == null)
    }

    @Test
    fun extractRelayReturnsNullForRawWsUrl() {
        val relay = ConnectionQrCodec.extractRelay("ws://192.168.1.2:1122")
        assertTrue(relay == null)
    }

    @Test
    fun extractRelayReturnsNullForBlank() {
        assertTrue(ConnectionQrCodec.extractRelay("") == null)
        assertTrue(ConnectionQrCodec.extractRelay("not-a-qr") == null)
    }
}
