package com.lan.notify.data

import org.junit.Assert.assertEquals
import org.junit.Test

class ChannelTest {
    @Test
    fun codexSourceUsesChatGptLabel() {
        assertEquals(Channel.ChatGPT, Channel.from("codex"))
        assertEquals(Channel.ChatGPT, Channel.from("chatgpt"))
        assertEquals("ChatGPT", Channel.ChatGPT.label)
    }

    @Test
    fun legacyCodexMessageUsesChatGptLabel() {
        val message = NotifyMessage(
            type = "message",
            content = "[codex] WorkBuddy 任务已完成",
            time = "2026-08-01 19:51:41",
            source = "codex"
        )

        assertEquals("ChatGPT 任务已完成", message.displayContent())
    }

    @Test
    fun chatGptCompletionPrefixIsRemovedFromTaskLog() {
        val message = NotifyMessage(
            type = "message",
            content = "ChatGPT 任务已完成：已完成应用模式卡片调整。",
            time = "2026-08-25 12:00:00",
            source = "codex"
        )

        assertEquals("已完成应用模式卡片调整。", message.displayContent())
    }

    @Test
    fun freebuffCompletionPrefixIsRemovedFromLegacyTaskLog() {
        val message = NotifyMessage(
            type = "message",
            content = "[freebuff] Freebuff 任务完成：旧版本日志内容",
            time = "2026-08-25 20:00:00",
            source = "freebuff"
        )

        assertEquals("旧版本日志内容", message.displayContent())
    }

    @Test
    fun freebuffAssistantTaskLogTakesPriorityOverUserPromptContent() {
        val message = NotifyMessage(
            type = "message",
            content = "[freebuff] Freebuff 任务完成：用户发送的指令不应展示为日志",
            taskLog = "执行总结：已生成分析文档并更新日期索引",
            time = "2026-08-25 20:00:00",
            source = "freebuff"
        )

        assertEquals("执行总结：已生成分析文档并更新日期索引", message.displayContent())
    }

    @Test
    fun qwenSourceMapsToQwenChannel() {
        assertEquals(Channel.Qwen, Channel.from("qwen"))
        assertEquals("\u5343\u95ee", Channel.Qwen.label)
        assertEquals("Q", Channel.Qwen.letter)
    }

    @Test
    fun qwenSourceMappingIsCaseAndWhitespaceInsensitive() {
        assertEquals(Channel.Qwen, Channel.from("  QWEN  "))
    }

    @Test
    fun suyanAliasesMapToSuyanChannel() {
        assertEquals(Channel.Suyan, Channel.from("suyan"))
        assertEquals(Channel.Suyan, Channel.from("SUYAN-APP"))
        assertEquals("\u7d20\u8a00", Channel.Suyan.label)
    }

    @Test
    fun quarkNetdiskAliasesMapToQuarkChannel() {
        assertEquals(Channel.QuarkNetdisk, Channel.from("quarknetdisk"))
        assertEquals(Channel.QuarkNetdisk, Channel.from("QUARK-NETDISK"))
        assertEquals(Channel.QuarkNetdisk, Channel.from("quark"))
        // 上传方向使用独立 source 标识，但仍归并到同一渠道，便于统一图标与分组
        assertEquals(Channel.QuarkNetdisk, Channel.from("quarknetdisk-upload"))
        assertEquals("\u5938\u514b\u7f51\u76d8", Channel.QuarkNetdisk.label)
        assertEquals("K", Channel.QuarkNetdisk.letter)
    }

    @Test
    fun adriveAliasesUseOneProjectAndLogoChannel() {
        assertEquals(Channel.Adrive, Channel.from("adrive"))
        assertEquals(Channel.Adrive, Channel.from("adrive-upload"))
        assertEquals(Channel.Adrive, Channel.from("AliyunDrive"))
        assertEquals("adrive", canonicalSourceKey("aliyun-drive-upload"))
        assertEquals("\u963f\u91cc\u4e91\u76d8", Channel.Adrive.label)
    }

    @Test
    fun workBuddySourceUsesDedicatedChannel() {
        assertEquals(Channel.WorkBuddy, Channel.from("workbuddy"))
        assertEquals("workbuddy", canonicalSourceKey(" WorkBuddy "))
        assertEquals("WorkBuddy", Channel.WorkBuddy.label)
        assertEquals("W", Channel.WorkBuddy.letter)
    }

    @Test
    fun workBuddyAiUsesIndependentChannelAndSourceKey() {
        assertEquals(Channel.WorkBuddyAi, Channel.from("workbuddy-ai"))
        assertEquals("workbuddy-ai", canonicalSourceKey(" WorkBuddy-AI "))
        assertEquals("WorkBuddy AI", Channel.WorkBuddyAi.label)
        assertEquals("A", Channel.WorkBuddyAi.letter)
    }

    @Test
    fun comfyUiSourceUsesOfficialChannel() {
        assertEquals(Channel.ComfyUI, Channel.from("comfyui"))
        assertEquals(Channel.ComfyUI, Channel.from("COMFY-UI"))
        assertEquals(Channel.ComfyUI, Channel.from("comfy_ui"))
        assertEquals("comfyui", canonicalSourceKey(" ComfyUI "))
        assertEquals("ComfyUI", Channel.ComfyUI.label)
    }

    @Test
    fun opencodeSourceUsesDedicatedChannel() {
        assertEquals(Channel.Opencode, Channel.from("opencode"))
        assertEquals(Channel.Opencode, Channel.from("OPEN-CODE"))
        assertEquals(Channel.Opencode, Channel.from("open_code"))
        assertEquals("opencode", canonicalSourceKey(" OpenCode "))
        assertEquals("OpenCode", Channel.Opencode.label)
        assertEquals("O", Channel.Opencode.letter)
    }

    @Test
    fun deepseekHarnessSourceUsesDedicatedChannel() {
        assertEquals(Channel.DeepseekHarness, Channel.from("deepseekharness"))
        assertEquals(Channel.DeepseekHarness, Channel.from("DEEPSEEK-HARNESS"))
        assertEquals(Channel.DeepseekHarness, Channel.from("deepseek_harness"))
        assertEquals("deepseekharness", canonicalSourceKey(" DeepSeek Harness "))
        assertEquals("DeepSeek Harness", Channel.DeepseekHarness.label)
        assertEquals("N", Channel.DeepseekHarness.letter)
    }

    @Test
    fun freebuffSourceUsesDedicatedChannel() {
        assertEquals(Channel.Freebuff, Channel.from("freebuff"))
        assertEquals(Channel.Freebuff, Channel.from("FREE-BUFF"))
        assertEquals(Channel.Freebuff, Channel.from("free_buff"))
        assertEquals(Channel.Freebuff, Channel.from("codebuff"))
        assertEquals("freebuff", canonicalSourceKey(" Freebuff "))
        assertEquals("Freebuff", Channel.Freebuff.label)
        assertEquals("B", Channel.Freebuff.letter)
    }

    @Test
    fun freebuffSourceInContentPrefixIsRestored() {
        assertEquals(
            "freebuff",
            normalizeMessageSource("", "[freebuff] Freebuff 任务已完成")
        )
    }

    @Test
    fun linuxDoCdkSourceUsesDedicatedChannelAndCanonicalKey() {
        assertEquals(Channel.LinuxDoCdk, Channel.from("linuxdo-cdk"))
        assertEquals(Channel.LinuxDoCdk, Channel.from("LINUXDO_CDK"))
        assertEquals("linuxdo-cdk", canonicalSourceKey(" LinuxDO CDK "))
        assertEquals("Linux DO CDK", Channel.LinuxDoCdk.label)
    }

    @Test
    fun legacyContentPrefixRestoresAdriveSource() {
        assertEquals(
            "adrive",
            normalizeMessageSource("", "[adrive] \u963f\u91cc\u4e91\u76d8\u4e0b\u8f7d\u4efb\u52a1")
        )
    }

    @Test
    fun taskLogOverridesGenericContentAsDisplayBody() {
        val message = NotifyMessage(
            type = "message",
            content = "[comfyui] ComfyUI 工作流已完成",
            time = "2026-08-22 10:00:00",
            source = "comfyui",
            status = NotifyMessage.STATUS_COMPLETED,
            taskLog = "a beautiful cat, highly detailed, best quality"
        )

        assertEquals("a beautiful cat, highly detailed, best quality", message.displayContent())
    }

    @Test
    fun emptyTaskLogFallsBackToContentBody() {
        val message = NotifyMessage(
            type = "message",
            content = "[comfyui] ComfyUI 工作流已完成",
            time = "2026-08-22 10:00:00",
            source = "comfyui",
            status = NotifyMessage.STATUS_COMPLETED,
            taskLog = "  "
        )

        assertEquals("ComfyUI 工作流已完成", message.displayContent())
    }
}
