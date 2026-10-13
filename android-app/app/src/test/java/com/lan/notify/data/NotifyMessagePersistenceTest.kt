package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class NotifyMessagePersistenceTest {
    @Test
    fun sameSecondAndContentWithDifferentTaskIdsHaveDifferentIds() {
        val first = NotifyMessage(
            type = "message",
            content = "[trae] 任务已完成",
            time = "2026-07-31 18:00:00",
            source = "trae",
            taskId = "session-1:turn-1"
        )
        val second = NotifyMessage(
            type = "message",
            content = "[trae] 任务已完成",
            time = "2026-07-31 18:00:00",
            source = "trae",
            taskId = "session-1:turn-2"
        )

        assertNotEquals(first.id, second.id)
        assertNotEquals(first.id.hashCode(), second.id.hashCode())
        assertEquals("trae|completed|session-1:turn-1", first.id)
        assertEquals("trae|completed|session-1:turn-2", second.id)
    }

    @Test
    fun emptyTaskIdKeepsLegacyId() {
        val message = NotifyMessage(
            type = "message",
            content = "旧消息",
            time = "2026-07-31 18:00:00",
            source = "zcode"
        )

        assertEquals("2026-07-31 18:00:00|message|旧消息", message.id)
    }

    @Test
    fun legacyStoredMessageWithoutTaskIdStillLoads() {
        val raw = """[{"type":"message","content":"旧消息","time":"2026-07-31 18:00:00","source":"zcode"}]"""

        val loaded = LocalStore.decodeMessages(raw).single()

        assertEquals("", loaded.taskId)
        assertEquals("", loaded.cwd)
        assertEquals(0L, loaded.durationMs)
        assertEquals(0L, loaded.tokenCount)
        assertEquals("2026-07-31 18:00:00|message|旧消息", loaded.id)
        assertTrue(loaded.isRead)
    }

    @Test
    fun duplicateLegacyMessagesAreAssignedUniqueIds() {
        val raw = """[
            {"type":"message","content":"重复消息","time":"2026-08-04T12:43:47+08:00","source":"codex"},
            {"type":"message","content":"重复消息","time":"2026-08-04T12:43:47+08:00","source":"codex"}
        ]"""

        val loaded = LocalStore.decodeMessages(raw)

        assertEquals(2, loaded.map { it.id }.toSet().size)
        assertEquals(loaded[0].id + "#2", loaded[1].id)
    }

    @Test
    fun taskIdAndCwdRoundTripThroughStorage() {
        val original = NotifyMessage(
            type = "message",
            content = "[zcode] ZCode 智能体任务已完成",
            time = "2026-07-31 18:00:00",
            status = NotifyMessage.STATUS_COMPLETED,
            source = "zcode",
            taskId = "session-1:turn-99",
            cwd = "W:/TapRelay",
            projectName = "真实任务名称",
            durationMs = 192_345L,
            tokenCount = 42_850L,
            isRead = false
        )

        val loaded = LocalStore.decodeMessages(LocalStore.encodeMessages(listOf(original))).single()

        assertEquals(original, loaded)
        assertEquals(42_850L, loaded.tokenCount)
    }

    @Test
    fun closeReminderFieldsRoundTripThroughStorage() {
        val original = NotifyMessage(
            type = "message",
            content = "[leigod] 提醒正文",
            time = "2026-08-04T19:00:00+08:00",
            status = NotifyMessage.STATUS_WAITING,
            source = "leigod",
            taskId = "close:leigod:123:456",
            notificationTitle = "雷神加速器关闭提醒",
            actionUrl = "weixin://dl/business/?t=leigod"
        )

        val loaded = LocalStore.decodeMessages(LocalStore.encodeMessages(listOf(original))).single()

        assertEquals(original, loaded)
    }

    @Test
    fun approvalEventFieldsRoundTripThroughStorage() {
        val original = NotifyMessage(
            type = "event",
            eventId = "event-approval-1",
            eventType = "approval.requested",
            content = "需要确认：Bash",
            time = "2026-08-17T09:00:00+08:00",
            status = NotifyMessage.STATUS_WAITING,
            source = "codex",
            taskId = "session-1",
            requestId = "request-1",
            toolName = "Bash",
            inputSummary = "执行受限命令",
            requiresAction = true,
            expiresAtMs = 1_786_900_000_000L,
            isRead = false
        )

        val loaded = LocalStore.decodeMessages(LocalStore.encodeMessages(listOf(original))).single()

        assertEquals(original, loaded)
        assertEquals("event-approval-1", loaded.id)
    }

    @Test
    fun projectTitlePrefersActualProjectName() {
        val withWindowsPath = NotifyMessage(
            type = "message",
            content = "完成",
            time = "2026-08-03T14:00:00+08:00",
            cwd = "W:\\TapRelay\\"
        )
        val withoutPath = withWindowsPath.copy(cwd = "")
        val named = withoutPath.copy(projectName = "并行任务 A", taskId = "turn-a")

        assertEquals("TapRelay", withWindowsPath.projectTitle("ChatGPT"))
        assertEquals("ChatGPT", withoutPath.projectTitle("ChatGPT"))
        assertEquals("并行任务 A", named.projectTitle("ChatGPT"))
        assertNotEquals(
            named.projectGroupKey("ChatGPT"),
            named.copy(projectName = "并行任务 B").projectGroupKey("ChatGPT")
        )
    }

    @Test
    fun projectTitlePrefersConfiguredNameOverWorkingFolder() {
        val message = NotifyMessage(
            type = "message",
            content = "完成",
            time = "2026-08-25T12:00:00+08:00",
            cwd = "W:\\TapRelay\\android-app",
            projectName = "任务设置名称"
        )

        assertEquals("任务设置名称", message.projectTitle("ChatGPT"))
    }

    @Test
    fun projectTitleRejectsAttachmentWrapperAsTaskName() {
        val message = NotifyMessage(
            type = "message",
            content = "完成",
            time = "2026-08-25T12:00:00+08:00",
            taskId = "turn-attachment",
            projectName = "# Files mentioned by the user: ## screenshot.png"
        )

        assertEquals("任务 turn-attachment", message.projectTitle("ChatGPT"))
    }

    @Test
    fun appGroupKeyMergesProjectsFromTheSameApplication() {
        val first = NotifyMessage(
            type = "message",
            content = "完成 A",
            time = "2026-08-04T12:00:00+08:00",
            source = "codex",
            projectName = "项目 A"
        )
        val second = first.copy(
            content = "完成 B",
            projectName = "项目 B"
        )
        val otherApp = first.copy(source = "claude")

        assertEquals(first.appGroupKey(), second.appGroupKey())
        assertNotEquals(first.appGroupKey(), otherApp.appGroupKey())
        assertEquals("codex", first.appGroupKey())
    }

    @Test
    fun durationFormatterKeepsUnknownAndFormatsUsefulPrecision() {
        assertEquals("耗时未知", TaskDurationFormatter.format(0L))
        assertEquals("<1秒", TaskDurationFormatter.format(850L))
        assertEquals("2分5秒", TaskDurationFormatter.format(125_900L))
        assertEquals("1时4分", TaskDurationFormatter.format(3_845_000L))
    }

    @Test
    fun recentTimeFormatterUsesCompactRelativeLabels() {
        val now = 1_787_000_000_000L
        val recent = java.time.Instant.ofEpochMilli(now - 30_000L)
            .atOffset(java.time.ZoneOffset.ofHours(8))
            .toString()
        val minutesAgo = java.time.Instant.ofEpochMilli(now - 2 * 60_000L)
            .atOffset(java.time.ZoneOffset.ofHours(8))
            .toString()
        val hoursAgo = java.time.Instant.ofEpochMilli(now - 3 * 60 * 60_000L)
            .atOffset(java.time.ZoneOffset.ofHours(8))
            .toString()

        assertEquals("刚刚", MessageTimeFormatter.formatRecent(recent, now))
        assertEquals("2分钟前", MessageTimeFormatter.formatRecent(minutesAgo, now))
        assertEquals("3小时前", MessageTimeFormatter.formatRecent(hoursAgo, now))
    }

    @Test
    fun tokenFormatterHidesZeroAndFormatsCounts() {
        assertEquals("", TaskTokenFormatter.format(0L))
        assertEquals("", TaskTokenFormatter.format(-3L))
        assertEquals("850 tokens", TaskTokenFormatter.format(850L))
        assertEquals("42.8k tokens", TaskTokenFormatter.format(42_850L))
        assertEquals("1.2M tokens", TaskTokenFormatter.format(1_250_000L))
    }

    @Test
    fun usageFormatterKeepsTokenAndCreditMetricsTogether() {
        assertEquals("375.4k tokens · 积分 12.5", TaskTokenFormatter.formatUsage("12.5", 375_400L))
        assertEquals("积分 12.5", TaskTokenFormatter.formatUsage("12.5", 0L))
        assertEquals("375.4k tokens", TaskTokenFormatter.formatUsage("", 375_400L))
    }

    @Test
    fun qwenWaitingAndStoppedStatusesRemainDistinct() {
        assertEquals(TaskState.Waiting, TaskState.from(NotifyMessage.STATUS_WAITING))
        assertEquals(TaskState.Stopped, TaskState.from(NotifyMessage.STATUS_STOPPED))
        assertEquals("已停止", TaskState.Stopped.title)
    }
}
