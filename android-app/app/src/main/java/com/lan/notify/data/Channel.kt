package com.lan.notify.data

import androidx.compose.ui.graphics.Color
import com.lan.notify.ui.theme.AppTokens

/**
 * 通知来源渠道。用于卡片 / 通知栏上给不同 Agent 打颜色标签，与正文文本区分开。
 *
 * `source` 字段由 PC 端 NotifyServer 广播时写入 payload，见
 * `pc-app/NotifyServer.cs` 的 `MessagePayload.source`。若日后新增 Agent，
 * 只需在这里加一条枚举 + 一个语义色 token（见 `ui/theme/Color.kt`）。
 */
enum class Channel(val label: String, val color: Color, val letter: String) {
    Claude("Claude", AppTokens.ChannelClaude, "C"),
    ClaudeHaha("Claude Code Haha", AppTokens.ChannelClaudeHaha, "H"),
    ChatGPT("ChatGPT", AppTokens.ChannelChatGPT, "C"),
    Trae("TraeCode", AppTokens.ChannelTrae, "T"),
    TraeWork("TraeWork", AppTokens.ChannelTraeWork, "W"),
    ZCode("ZCode", AppTokens.ChannelZCode, "Z"),
    Doubao("\u8c46\u5305", AppTokens.ChannelDoubao, "D"),
    Qwen("\u5343\u95ee", AppTokens.ChannelQwen, "Q"),
    Suyan("\u7d20\u8a00", AppTokens.ChannelSuyan, "Y"),
    BaiduNetdisk("\u767e\u5ea6\u7f51\u76d8", AppTokens.ChannelBaiduNetdisk, "B"),
    QuarkNetdisk("\u5938\u514b\u7f51\u76d8", AppTokens.ChannelQuarkNetdisk, "K"),
    Adrive("\u963f\u91cc\u4e91\u76d8", AppTokens.ChannelAdrive, "A"),
    WorkBuddy("WorkBuddy", AppTokens.ChannelWorkBuddy, "W"),
    ComfyUI("ComfyUI", AppTokens.ChannelComfyUI, "F"),
    Opencode("OpenCode", AppTokens.ChannelOpencode, "O"),
    DeepseekHarness("DeepSeek Harness", AppTokens.ChannelDeepseekHarness, "N"),
    Freebuff("Freebuff", AppTokens.ChannelFreebuff, "B"),
    LinuxDoCdk("Linux DO CDK", AppTokens.ChannelLinuxDoCdk, "L"),
    TapRelay("TapRelay", AppTokens.ChannelTapRelay, "R"),
    Unknown("其他", AppTokens.ChannelUnknown, "?");

    companion object {
        fun from(source: String): Channel = when (source.trim().lowercase()) {
            "claude" -> Claude
            "claude-haha" -> ClaudeHaha
            // `codex` remains the persisted/protocol source key for ChatGPT.
            "codex", "chatgpt" -> ChatGPT
            "trae" -> Trae
            // `traework` 是 TraeWork CN 的协议 source key；`trae-solo` 是更名前的
            // 旧 key，历史持久化消息仍使用它，两者归入同一渠道。
            "traework", "trae-solo" -> TraeWork
            "zcode" -> ZCode
            "doubao" -> Doubao
            "qwen" -> Qwen
            "suyan", "suyan-app", "su-yan", "\u7d20\u8a00", "\u7d20\u8a00app", "\u7d20\u8a00 app" -> Suyan
            "baidunetdisk", "baidu-netdisk", "baidu", "\u767e\u5ea6\u7f51\u76d8" -> BaiduNetdisk
            "quarknetdisk", "quark-netdisk", "quark", "quarknetdisk-upload", "\u5938\u514b\u7f51\u76d8" -> QuarkNetdisk
            "adrive", "adrive-upload", "aliyundrive", "aliyundrive-upload",
            "aliyun-drive", "aliyun-drive-upload", "a-drive", "\u963f\u91cc\u4e91\u76d8" -> Adrive
            // `workbuddy-remote` 是 PC 端 NotifyServer 对 workbuddy 来源在广播 payload
            // 上的线路别名（见 NotifyServer.ToWireSource），手机端必须归并到同一渠道，
            // 否则卡片落入 Unknown。
            "workbuddy", "workbuddy-remote" -> WorkBuddy
            "comfyui", "comfy-ui", "comfy_ui" -> ComfyUI
            "opencode", "open-code", "open_code" -> Opencode
            "deepseekharness", "deepseek-harness", "deepseek_harness", "deepseek harness" -> DeepseekHarness
            "freebuff", "free-buff", "free_buff", "codebuff", "code-buff", "code_buff" -> Freebuff
            "linuxdo-cdk", "linuxdo_cdk", "linuxdo cdk" -> LinuxDoCdk
            "taprelay" -> TapRelay
            else -> Unknown
        }
    }
}

/**
 * Stable UI/protocol key for a source. Direction-specific aliases must not
 * create a second project card or a second notification identity.
 */
fun canonicalSourceKey(source: String): String = when (Channel.from(source)) {
    Channel.Claude -> "claude"
    Channel.ClaudeHaha -> "claude-haha"
    Channel.ChatGPT -> "codex"
    Channel.Trae -> "trae"
    Channel.TraeWork -> "traework"
    Channel.ZCode -> "zcode"
    Channel.Doubao -> "doubao"
    Channel.Qwen -> "qwen"
    Channel.Suyan -> "suyan"
    Channel.BaiduNetdisk -> "baidunetdisk"
    Channel.QuarkNetdisk -> "quarknetdisk"
    Channel.Adrive -> "adrive"
    Channel.WorkBuddy -> "workbuddy"
    Channel.ComfyUI -> "comfyui"
    Channel.Opencode -> "opencode"
    Channel.DeepseekHarness -> "deepseekharness"
    Channel.Freebuff -> "freebuff"
    Channel.LinuxDoCdk -> "linuxdo-cdk"
    Channel.TapRelay -> "taprelay"
    Channel.Unknown -> source.trim().lowercase()
}

/**
 * Normalize live and persisted messages. Older server payloads can carry the
 * source only in the standard `[source]` content prefix.
 */
fun normalizeMessageSource(source: String, content: String = ""): String {
    val direct = source.trim()
    if (Channel.from(direct) != Channel.Unknown) {
        return canonicalSourceKey(direct)
    }

    val trimmedContent = content.trimStart()
    if (trimmedContent.startsWith("[")) {
        val end = trimmedContent.indexOf(']')
        if (end > 1) {
            val prefixedSource = trimmedContent.substring(1, end)
            if (Channel.from(prefixedSource) != Channel.Unknown) {
                return canonicalSourceKey(prefixedSource)
            }
        }
    }

    return direct.lowercase()
}

/**
 * 真实任务日志保留原文。仅旧 content 清理可确认的来源前缀。
 */
fun NotifyMessage.displayContent(): String {
    // 服务端可随消息下发细化任务日志（如 ComfyUI K 采样器正向提示词），
    // 覆盖笼统的“工作流已完成”；未提供时回退到 content 原文。
    if (taskLog.isNotBlank()) return taskLog
    val trimmed = content.trimStart()
    val withoutSource = if (!trimmed.startsWith("[")) {
        trimmed
    } else {
        val end = trimmed.indexOf(']')
        val prefix = if (end > 1) trimmed.substring(1, end) else ""
        val hasSeparator = end >= 0 && (end == trimmed.lastIndex || trimmed[end + 1].isWhitespace())
        val matchesSource = prefix.isNotBlank() && (
            canonicalSourceKey(prefix) == canonicalSourceKey(source) ||
                (source.isBlank() && Channel.from(prefix) != Channel.Unknown)
            )
        if (hasSeparator && matchesSource) trimmed.substring(end + 1).trimStart() else trimmed
    }

    // Older ChatGPT events put a generic status label in front of the actual
    // agent log. The card already renders status and source separately, so
    // keep only the real log text for both new and persisted messages.
    val withoutGenericStatus = withoutSource
        .replace(Regex("^ChatGPT\\s*任务已完成\\s*[：:]\\s*"), "")
        .replace(Regex("^ChatGPT\\s*任务完成\\s*[：:]\\s*"), "")
        .replace(Regex("^(?:Freebuff|Codebuff)\\s*任务(?:已)?完成\\s*[：:]\\s*", RegexOption.IGNORE_CASE), "")
        .replace(Regex("^(?:Freebuff|Codebuff)\\s*任务失败\\s*[：:]\\s*", RegexOption.IGNORE_CASE), "")
        .replace(Regex("^(?:Freebuff|Codebuff)\\s*任务已停止\\s*[：:]\\s*", RegexOption.IGNORE_CASE), "")
        .replace(Regex("^(?:Freebuff|Codebuff)\\s*需要你确认\\s*[：:]\\s*", RegexOption.IGNORE_CASE), "")

    // Normalize labels written by older builds when rendering persisted history.
    if (Channel.from(source) == Channel.ChatGPT) {
        for (legacyLabel in listOf("WorkBuddy", "Codex")) {
            if (withoutGenericStatus.startsWith(legacyLabel)) {
                return "ChatGPT" + withoutGenericStatus.removePrefix(legacyLabel)
            }
        }
    }
    val summary = inputSummary.trim()
    return if (requiresAction && summary.isNotBlank() && !withoutGenericStatus.contains(summary)) {
        "$withoutGenericStatus\n$summary"
    } else {
        withoutGenericStatus
    }
}
