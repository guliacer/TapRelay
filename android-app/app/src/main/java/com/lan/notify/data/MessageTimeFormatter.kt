package com.lan.notify.data

import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

object MessageTimeFormatter {
    private val outputFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")

    fun format(rawTime: String): String {
        if (rawTime.isBlank()) return "时间未知"

        return runCatching {
            OffsetDateTime.parse(rawTime)
                .atZoneSameInstant(ZoneId.systemDefault())
                .format(outputFormatter)
        }.getOrElse {
            rawTime.replace('T', ' ').removeSuffix("Z")
        }
    }

    /**
     * 服务端 time 字段（带时区的 ISO 8601）转毫秒时间戳，用于通知的 setWhen 排序。
     * 解析失败返回 null，由调用方决定回退值。
     */
    fun toEpochMillis(rawTime: String): Long? {
        if (rawTime.isBlank()) return null
        return runCatching { OffsetDateTime.parse(rawTime).toInstant().toEpochMilli() }.getOrNull()
    }

    /** 用于卡片标题行的短相对时间，避免系统通知头部单独占一行。 */
    fun formatRecent(rawTime: String, nowMillis: Long = System.currentTimeMillis()): String {
        val timestamp = toEpochMillis(rawTime) ?: return "时间未知"
        val elapsed = (nowMillis - timestamp).coerceAtLeast(0L)
        val minute = 60_000L
        val hour = 60L * minute
        val day = 24L * hour
        return when {
            elapsed < minute -> "刚刚"
            elapsed < hour -> "${elapsed / minute}分钟前"
            elapsed < day -> "${elapsed / hour}小时前"
            else -> format(rawTime)
        }
    }

    /**
     * 历史地址列表的「最近使用」文案。
     *
     * `lastUsedAtMillis <= 0` 表示真实使用时间不可知（老用户升级时由既有 server_host 播种），
     * 明确渲染成「较早使用」，不谎报「刚刚使用」。
     */
    fun formatHostUsedAt(
        lastUsedAtMillis: Long,
        nowMillis: Long = System.currentTimeMillis()
    ): String {
        if (lastUsedAtMillis <= 0L) return "较早使用"
        val elapsed = (nowMillis - lastUsedAtMillis).coerceAtLeast(0L)
        val minute = 60_000L
        val hour = 60L * minute
        val day = 24L * hour
        return when {
            elapsed < minute -> "刚刚使用"
            elapsed < hour -> "${elapsed / minute} 分钟前"
            elapsed < day -> "${elapsed / hour} 小时前"
            elapsed < 2 * day -> "昨天"
            else -> "${elapsed / day} 天前"
        }
    }
}
