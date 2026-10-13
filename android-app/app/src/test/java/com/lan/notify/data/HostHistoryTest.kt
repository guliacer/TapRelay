package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class HostHistoryTest {

    // ---------- 去重键 ----------

    @Test
    fun samePcWrittenFourWaysCollapsesToSingleKey() {
        val keys = listOf(
            "192.168.5.6",
            "ws://192.168.5.6",
            "ws://192.168.5.6:1122",
            "http://192.168.5.6",
            "192.168.5.6/",
            "  WS://192.168.5.6  "
        ).map(::hostHistoryKey)

        assertEquals(1, keys.distinct().size)
        assertEquals("plain:192.168.5.6", keys.first())
    }

    @Test
    fun tlsTunnelIsKeptSeparateFromPlaintext() {
        // wss 隧道与局域网明文是两条不同的传输通道，合并会让用户以为选了隧道实际连的是局域网。
        assertNotEquals(hostHistoryKey("wss://tap.example.com"), hostHistoryKey("tap.example.com"))
        assertNotEquals(hostHistoryKey("wss://tap.example.com"), hostHistoryKey("ws://tap.example.com"))
        assertEquals("tls:tap.example.com", hostHistoryKey("wss://tap.example.com"))
        assertEquals("tls:tap.example.com", hostHistoryKey("https://tap.example.com"))
    }

    @Test
    fun nonDefaultPortStaysPartOfTheKey() {
        assertNotEquals(hostHistoryKey("192.168.5.6:8080"), hostHistoryKey("192.168.5.6"))
    }

    // ---------- 合并 / 去重 / 排序 ----------

    @Test
    fun equivalentHostDoesNotCreateASecondEntry() {
        val first = mergeHostHistory(emptyList(), "192.168.5.6", nowMs = 1_000L)
        val second = mergeHostHistory(first, "ws://192.168.5.6", nowMs = 2_000L)

        assertEquals(1, second.size)
        // 保留新写入的地址原文，保证历史值始终等于当前实际使用的值。
        assertEquals("ws://192.168.5.6", second.single().host)
        assertEquals(2_000L, second.single().lastUsedAt)
    }

    @Test
    fun newestUsageEndsUpOnTop() {
        var history = mergeHostHistory(emptyList(), "10.0.0.1", nowMs = 1_000L)
        history = mergeHostHistory(history, "10.0.0.2", nowMs = 2_000L)
        history = mergeHostHistory(history, "10.0.0.3", nowMs = 3_000L)
        assertEquals(listOf("10.0.0.3", "10.0.0.2", "10.0.0.1"), history.map { it.host })

        // 重新使用一个旧地址，它必须冒到最前。
        history = mergeHostHistory(history, "10.0.0.1", nowMs = 4_000L)
        assertEquals(listOf("10.0.0.1", "10.0.0.3", "10.0.0.2"), history.map { it.host })
        assertEquals(3, history.size)
    }

    @Test
    fun blankHostIsIgnored() {
        val history = mergeHostHistory(emptyList(), "   ", nowMs = 1_000L)
        assertTrue(history.isEmpty())
    }

    // ---------- 10 条上限 ----------

    @Test
    fun oldestEntryIsDroppedBeyondTheLimit() {
        var history = emptyList<HostHistoryEntry>()
        repeat(MAX_HOST_HISTORY + 3) { index ->
            history = mergeHostHistory(history, "10.0.0.$index", nowMs = 1_000L + index)
        }

        assertEquals(MAX_HOST_HISTORY, history.size)
        assertEquals("10.0.0.12", history.first().host)
        assertEquals("10.0.0.3", history.last().host)
        assertTrue(history.none { it.host == "10.0.0.0" })
    }

    @Test
    fun reusingAnAddressAtTheLimitDoesNotGrowTheList() {
        var history = emptyList<HostHistoryEntry>()
        repeat(MAX_HOST_HISTORY) { index ->
            history = mergeHostHistory(history, "10.0.0.$index", nowMs = 1_000L + index)
        }

        history = mergeHostHistory(history, "10.0.0.0", nowMs = 9_999L)

        assertEquals(MAX_HOST_HISTORY, history.size)
        assertEquals("10.0.0.0", history.first().host)
    }

    // ---------- 持久化 ----------

    @Test
    fun hostHistoryRoundTripsThroughLocalStore() {
        val original = listOf(
            HostHistoryEntry("wss://tap.example.com", 1_700_000_000_000L),
            HostHistoryEntry("192.168.5.6", 1_600_000_000_000L)
        )

        val loaded = LocalStore.decodeHostHistory(LocalStore.encodeHostHistory(original))

        assertEquals(original, loaded)
    }

    @Test
    fun brokenJsonDegradesToEmptyList() {
        assertTrue(LocalStore.decodeHostHistory("not json at all").isEmpty())
        assertTrue(LocalStore.decodeHostHistory("").isEmpty())
        assertTrue(LocalStore.decodeHostHistory("{}").isEmpty())
    }

    @Test
    fun decodeSkipsEntriesWithoutHostAndRepairsInvariants() {
        val raw = """
            [
              {"host":"","lastUsedAt":900},
              {"host":"   ","lastUsedAt":901},
              {"host":"10.0.0.1","lastUsedAt":100},
              {"host":"ws://10.0.0.1","lastUsedAt":500},
              {"host":"10.0.0.2"},
              {"host":"10.0.0.3","lastUsedAt":300}
            ]
        """.trimIndent()

        val loaded = LocalStore.decodeHostHistory(raw)

        // 空 host 被丢弃；同一去重键只保留最新的那条（ws://10.0.0.1）。
        assertEquals(listOf("ws://10.0.0.1", "10.0.0.3", "10.0.0.2"), loaded.map { it.host })
        assertEquals(500L, loaded.first().lastUsedAt)
        // 缺 lastUsedAt 的条目按 0 处理（渲染成「较早使用」）。
        assertEquals(0L, loaded.last().lastUsedAt)
    }

    // ---------- 老用户迁移 ----------

    @Test
    fun existingHistoryWinsOverSeeding() {
        val stored = listOf(HostHistoryEntry("10.0.0.9", 1_000L))
        assertEquals(stored, initialHostHistory(stored, savedHost = "192.168.5.6"))
    }

    @Test
    fun savedHostIsSeededWithUnknownTimestamp() {
        val seeded = initialHostHistory(emptyList(), savedHost = "192.168.5.6")

        assertEquals(1, seeded.size)
        assertEquals("192.168.5.6", seeded.single().host)
        // 0 表示真实使用时间不可知，不能谎报成"刚刚使用"。
        assertEquals(0L, seeded.single().lastUsedAt)
        assertEquals("较早使用", MessageTimeFormatter.formatHostUsedAt(0L))
    }

    @Test
    fun nothingIsSeededWithoutASavedHost() {
        assertTrue(initialHostHistory(emptyList(), savedHost = "").isEmpty())
    }

    // ---------- 相对时间文案 ----------

    @Test
    fun relativeUsageLabelCoversEveryBucket() {
        val now = 1_700_000_000_000L
        val minute = 60_000L
        val hour = 60 * minute
        val day = 24 * hour

        assertEquals("刚刚使用", MessageTimeFormatter.formatHostUsedAt(now - 1_000L, now))
        assertEquals("5 分钟前", MessageTimeFormatter.formatHostUsedAt(now - 5 * minute, now))
        assertEquals("3 小时前", MessageTimeFormatter.formatHostUsedAt(now - 3 * hour, now))
        assertEquals("昨天", MessageTimeFormatter.formatHostUsedAt(now - 30 * hour, now))
        assertEquals("4 天前", MessageTimeFormatter.formatHostUsedAt(now - 4 * day, now))
    }
}
