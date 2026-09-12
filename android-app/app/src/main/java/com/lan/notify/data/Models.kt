package com.lan.notify.data

data class NotifyMessage(
    val type: String,
    val content: String,
    val time: String,
    /** 服务端 status 字段：completed / failed / waiting / stopped；缺省按 completed 处理。 */
    val status: String = STATUS_COMPLETED,
    /** 服务端 source 字段：claude / codex / trae / traework / zcode。UI 层据此显示渠道 badge。 */
    val source: String = "",
    /** 服务端任务唯一标识；用于区分同一秒内文案相同的多个任务。 */
    val taskId: String = "",
    /** 任务工作目录；保留给历史记录和后续项目维度展示。 */
    val cwd: String = "",
    /** 服务端识别出的实际项目或任务名称；不再用应用名冒充项目名。 */
    val projectName: String = "",
    /** 任务实际耗时，单位毫秒；0 表示来源没有提供可靠的开始时间。 */
    val durationMs: Long = 0L,
    /** 本轮任务累计 token；0 表示来源未提供可靠用量，卡片不展示。 */
    val tokenCount: Long = 0L,
    /** TraeCode CN 本轮消耗积分（usage_summary 原文）；空串表示未提供，卡片展示 token 或不展示。 */
    val credits: String = "",
    /**
     * 会话 ID（Claude transcript 文件名 / Codex session / OpenCode session），
     * 用于在通知/主界面中标识所属会话。
     */
    val sessionId: String = "",
    /** 可选的通知标题；关闭提醒等非任务状态通知使用该字段保持标题精确。 */
    val notificationTitle: String = "",
    /** 任务日志正文（如 ComfyUI K 采样器正向提示词）；空串表示未提供，卡片正文回退到 content。 */
    val taskLog: String = "",
    /** 可选的安全跳转地址；仅接受 https 或 weixin scheme。 */
    val actionUrl: String = "",
    /** 实时事件唯一标识；旧版 message 消息为空。 */
    val eventId: String = "",
    /** 实时事件类型，例如 approval.requested / tool.finished。 */
    val eventType: String = "",
    /** 审批请求关联 ID；同一请求的 resolved 事件会更新原消息。 */
    val requestId: String = "",
    /** 触发审批的工具名称。 */
    val toolName: String = "",
    /** 面向用户的受限输入摘要，不保存原始敏感参数。 */
    val inputSummary: String = "",
    /** 是否需要用户在通知栏执行动作。 */
    val requiresAction: Boolean = false,
    /** 审批过期时间，Unix 毫秒。 */
    val expiresAtMs: Long = 0L,
    /** allow / deny；未处理时为空。 */
    val approvalDecision: String = "",
    /**
     * 手机端「继续」按钮发给 agent 的指令原文。持久化到历史，供 agent.result
     * 回执回显与重启后追溯，不再依赖 WebSocketService 的内存缓存。
     */
    val sentInstruction: String = "",
    val isRead: Boolean = false,
    val id: String = eventId
        .takeIf { it.isNotBlank() }
        ?: taskId
        .takeIf { it.isNotBlank() }
        ?.let { "$source|$status|$it" }
        ?: "$time|$type|$content"
) {
    companion object {
        const val STATUS_COMPLETED = "completed"
        const val STATUS_FAILED = "failed"
        const val STATUS_WAITING = "waiting"
        const val STATUS_STOPPED = "stopped"
        const val STATUS_APPROVED = "approved"
        const val STATUS_REJECTED = "rejected"
    }
}

/**
 * Compose LazyColumn keys must remain unique when messages lack a taskId.
 */
internal fun NotifyMessage.withUniqueMessageId(usedIds: Set<String>): NotifyMessage {
    val baseId = id.ifBlank { "$time|$type|$content" }
    var uniqueId = baseId
    var suffix = 2
    while (uniqueId in usedIds) {
        uniqueId = "$baseId#$suffix"
        suffix++
    }
    return if (id == uniqueId) this else copy(id = uniqueId)
}

internal fun ensureUniqueMessageIds(messages: List<NotifyMessage>): List<NotifyMessage> {
    val usedIds = HashSet<String>(messages.size)
    return messages.map { message ->
        val normalized = message.withUniqueMessageId(usedIds)
        usedIds += normalized.id
        normalized
    }
}

/** 通知标题按状态区分，等待确认必须让用户一眼看出需要操作。 */
enum class TaskState(val title: String) {
    Waiting("需要确认"),
    Failed("任务失败"),
    Stopped("已停止"),
    Approved("已允许"),
    Rejected("已拒绝"),
    Completed("任务完成");

    companion object {
        fun from(status: String): TaskState = when (status.trim().lowercase()) {
            NotifyMessage.STATUS_WAITING -> Waiting
            NotifyMessage.STATUS_FAILED -> Failed
            NotifyMessage.STATUS_STOPPED -> Stopped
            NotifyMessage.STATUS_APPROVED -> Approved
            NotifyMessage.STATUS_REJECTED -> Rejected
            else -> Completed
        }
    }
}

enum class ConnectionStatus(val label: String) {
    Connected("已连接"),
    Connecting("连接中"),
    Reconnecting("重连中"),
    Disconnected("未连接")
}

/**
 * 消息历史列表的展示模式。
 * - TIME：按时间倒序平铺（默认，原行为）。
 * - PROJECT：按实际项目/任务名称分组堆叠，组内最新在上，组间留白。
 */
enum class ListMode(val label: String) {
    TIME("时间"),
    PROJECT("\u5e94\u7528")
}

/**
 * 项目模式下每个分组默认展示的最新消息条数。
 * 超过此数量时通过"查看6条 / 查看全部"按钮按需展开，状态持久化以记忆用户选择。
 */
const val DEFAULT_PROJECT_VISIBLE_COUNT = 6

data class AppUiState(
    val serverHost: String,
    val connectionStatus: ConnectionStatus,
    val messages: List<NotifyMessage>,
    val registeredApps: Map<String, RegisteredApp> = emptyMap()
)

/** Metadata registered by a local AI adapter through the PC app. */
data class RegisteredApp(
    val appKey: String,
    val displayName: String,
    val brandColor: String,
    val iconBase64: String = ""
) {
    val normalizedKey: String
        get() = appKey.trim().lowercase()
}

/**
 * Prefer the explicitly configured project name, then the actual working folder.
 * The desktop relay resolves Codex project settings from the selected folder;
 * using cwd first here would silently turn a configured name back into a folder
 * basename (for example, "任务通知" -> "TapRelay"). User-message attachment
 * wrappers are not task names and must not reach the UI.
 */
fun NotifyMessage.projectTitle(fallback: String): String {
    val explicit = projectName.trim()
    if (explicit.isNotBlank() && !looksLikeAttachmentWrapper(explicit)) return explicit

    val normalized = cwd.trim().trimEnd('/', '\\')
    if (normalized.isNotBlank()) {
        val leaf = normalized.substringAfterLast('/').substringAfterLast('\\')
        if (leaf.isNotBlank()) return leaf
    }
    val token = taskId.trim()
    if (token.isNotBlank()) return "任务 " + token.take(24)
    return fallback
}

private fun looksLikeAttachmentWrapper(value: String): Boolean {
    return value.startsWith("# Files mentioned by the user:", ignoreCase = true) ||
        value.startsWith("# Files mentioned by user:", ignoreCase = true)
}

fun NotifyMessage.projectGroupKey(fallback: String): String {
    val sourceKey = canonicalSourceKey(source).ifBlank { "unknown" }
    return sourceKey + "\u001f" + projectTitle(fallback)
}

/** 应用模式只按规范化来源分组，同一应用的不同项目在组内作为独立任务日志展示。 */
fun NotifyMessage.appGroupKey(): String =
    canonicalSourceKey(source).ifBlank { "unknown" }

fun projectGroupSourceKey(groupKey: String): String = groupKey.substringBefore('\u001f').ifBlank { "unknown" }

object TaskDurationFormatter {
    fun format(durationMs: Long): String {
        if (durationMs <= 0L) return "耗时未知"
        if (durationMs < 1_000L) return "<1秒"

        val totalSeconds = durationMs / 1_000L
        val hours = totalSeconds / 3_600L
        val minutes = (totalSeconds % 3_600L) / 60L
        val seconds = totalSeconds % 60L
        return when {
            hours > 0L && minutes > 0L -> "${hours}时${minutes}分"
            hours > 0L -> "${hours}时"
            minutes > 0L -> "${minutes}分${seconds}秒"
            else -> "${seconds}秒"
        }
    }
}

object TaskTokenFormatter {
    /** 仅 tokenCount > 0 时返回展示文案；否则空串（调用方不渲染）。 */
    fun format(tokenCount: Long): String {
        if (tokenCount <= 0L) return ""
        return "${formatCount(tokenCount)} tokens"
    }

    /**
     * 统一用量文案：有多个可靠用量时全部保留，避免只显示积分或 token 之一项。
     * 这样任务卡片能同时说明耗时、token 和积分；两者都缺失时返回空串。
     */
    fun formatUsage(credits: String, tokenCount: Long): String {
        val trimmedCredits = credits.trim()
        val tokenLabel = format(tokenCount)
        val creditLabel = trimmedCredits.takeIf { it.isNotEmpty() }?.let { "积分 $it" }
        return listOfNotNull(tokenLabel.takeIf { it.isNotEmpty() }, creditLabel)
            .joinToString(" · ")
    }

    private fun formatCount(value: Long): String {
        if (value < 10_000L) return value.toString()
        if (value < 1_000_000L) {
            val whole = value / 1_000L
            val frac = (value % 1_000L) / 100L
            return if (frac == 0L) "${whole}k" else "$whole.${frac}k"
        }
        val whole = value / 1_000_000L
        val frac = (value % 1_000_000L) / 100_000L
        return if (frac == 0L) "${whole}M" else "$whole.${frac}M"
    }
}

/** Compact usage line shared by history cards and the expanded capsule card. */
fun NotifyMessage.taskUsageLabel(): String {
    val tokenLabel = TaskTokenFormatter.formatUsage(credits, tokenCount)
    return buildString {
        append("耗时 ")
        append(TaskDurationFormatter.format(durationMs))
        if (tokenLabel.isNotEmpty()) {
            append(" · ")
            append(tokenLabel)
        }
    }
}

fun normalizeAppKey(source: String): String = source.trim().lowercase()

private val BUILT_IN_APP_KEYS = setOf(
    "claude", "claude-haha", "codex", "trae", "trae-solo", "traework", "zcode", "doubao", "qwen",
    "suyan", "baidunetdisk", "quarknetdisk", "quarknetdisk-upload", "adrive", "adrive-upload", "workbuddy", "comfyui", "taprelay"
)

fun isBuiltInAppKey(source: String): Boolean = normalizeAppKey(source) in BUILT_IN_APP_KEYS

/**
 * Canonical source keys whose agents historically supported session.stop via
 * agent.call RPC. Kept as a fallback allow-list for the window before the first
 * batch `agent.probe` result arrives (or when the PC is an older build without
 * batch probe support). Once the capability cache has an entry for the agent,
 * [AgentCapabilityCache] is authoritative — this list is no longer consulted.
 */
private val STOPPABLE_AGENT_SOURCES = setOf(
    "claude", "claude-haha", "codex", "opencode", "deepseekharness", "freebuff"
)

/**
 * Canonical source keys whose agents support session.send (继续发送指令 /
 * 手机上继续 vibe coding). Superset of [STOPPABLE_AGENT_SOURCES] plus Freebuff,
 * which queues messages through its orchestrator HTTP API. Like the stop list,
 * this is only a fallback — a probed capability
 * ("session.send") is authoritative once present.
 */
private val SENDABLE_AGENT_SOURCES = setOf(
    "claude", "claude-haha", "codex", "opencode", "deepseekharness", "freebuff",
    // Electron/GUI agents continue by bringing their existing session window
    // to the foreground through the PC window-focus proxy.
    "trae", "traework", "zcode", "workbuddy", "doubao"
)

/**
 * Older PC builds exposed only agent.probe for the GUI proxy even though its
 * session.send handler already supported the window-focus continue action.
 * Keep those builds compatible while newer probes advertise session.send.
 */
private val WINDOW_FOCUS_AGENT_SOURCES = setOf("trae", "traework", "zcode", "workbuddy", "doubao")

/**
 * True when the phone can send session.stop for this message's agent.
 *
 * Capability-driven first: if the PC has already reported this agent's
 * capabilities through the batch probe, trust the real `session.stop`
 * capability. Otherwise fall back to the legacy allow-list so a freshly
 * installed phone (or an unprobed agent) never loses the stop button.
 */
fun NotifyMessage.isStoppable(): Boolean {
    if (sessionId.trim().isBlank()) return false
    val key = canonicalSourceKey(source).trim().lowercase()
    if (key.isBlank()) return false
    return if (AgentCapabilityCache.hasEntry(key)) {
        AgentCapabilityCache.supports(key, AgentCapabilityCache.CAP_SESSION_STOP)
    } else {
        key in STOPPABLE_AGENT_SOURCES
    }
}

/**
 * True when the phone can send a new instruction to this message's agent
 * (session.send) — the "在手机上继续 vibe coding" entry point.
 *
 * Same capability-first rule as [isStoppable]: a probed "session.send"
 * capability is authoritative, otherwise the [SENDABLE_AGENT_SOURCES]
 * fallback list applies.
 */
fun NotifyMessage.canSendMessage(): Boolean {
    if (sessionId.trim().isBlank()) return false
    val key = canonicalSourceKey(source).trim().lowercase()
    if (key.isBlank()) return false
    return if (AgentCapabilityCache.hasEntry(key)) {
        AgentCapabilityCache.supports(key, AgentCapabilityCache.CAP_SESSION_SEND) ||
            key in WINDOW_FOCUS_AGENT_SOURCES
    } else {
        key in SENDABLE_AGENT_SOURCES
    }
}

/**
 * A waiting task always needs an explicit user-facing processing entry.
 *
 * A few adapters expose the same state as `pending`/`awaiting_input` while
 * setting `requiresAction=true`; accepting those aliases keeps the overlay's
 * 处理 button tied to the real action state instead of the adapter's wording.
 */
fun NotifyMessage.isWaitingTask(): Boolean {
    val normalized = status.trim().lowercase().replace('-', '_')
    if (normalized == NotifyMessage.STATUS_WAITING) return true
    if (!requiresAction) return false
    return normalized in setOf(
        "pending",
        "awaiting",
        "awaiting_input",
        "awaiting_user",
        "requires_action",
        "requiresaction",
        "idle"
    )
}

/** Only a real request id can produce an allow/deny approval action. */
fun NotifyMessage.hasApprovalAction(): Boolean =
    isWaitingTask() && requestId.trim().isNotBlank()

data class ServerConfig(
    val host: String
) {
    private val scheme: String
        get() {
            if (host.startsWith("wss://", ignoreCase = true) ||
                host.startsWith("https://", ignoreCase = true)) return "wss"
            if (host.startsWith("ws://", ignoreCase = true)) return "ws"
            // 无 scheme：端口 443 推断为 wss 隧道（Cloudflare 公网只暴露 443），
            // 兼容早期版本持久化时被 normalizeHost 剥掉 wss:// 的旧地址。
            val normalized = ConnectionQrCodec.normalizeHost(host)
            return if (normalized.substringAfterLast(':').toIntOrNull() == 443) "wss" else "ws"
        }

    private val authority: String
        get() {
            val normalized = ConnectionQrCodec.normalizeHost(host)
            if (normalized.startsWith("[") && normalized.contains("]:")) return normalized
            if (!normalized.startsWith("[") && normalized.count { it == ':' } == 1 &&
                normalized.substringAfterLast(':').toIntOrNull() != null) return normalized
            // wss 隧道（公网域名）默认 443（Cloudflare 隧道只暴露 443）；LAN 默认 1122。
            val defaultPort = if (scheme == "wss") 443 else DEFAULT_PORT
            return if (normalized.contains(":") && !normalized.startsWith("[")) {
                "[$normalized]:$defaultPort"
            } else {
                "$normalized:$defaultPort"
            }
        }

    val webSocketUrl: String
        get() = "$scheme://$authority"
    val httpBaseUrl: String
        // wss 隧道（443）只接受 TLS，注册表/图标同步必须走 https，否则 400。
        get() = "${if (scheme == "wss") "https" else "http"}://$authority"

    companion object {
        /** 首次安装、尚未成功连接过时预填到输入框的 PC 局域网 IP，用户可直接改。 */
        const val DEFAULT_HOST = "192.168.5.6"
        const val DEFAULT_PORT = 1122

        fun normalizeHost(input: String): String = ConnectionQrCodec.normalizeHost(input)
    }
}
