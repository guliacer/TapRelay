package com.lan.notify.data

import android.content.Context
import com.lan.notify.ui.theme.ThemeMode
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

object MessageRepository {
    private const val MAX_HISTORY_SIZE = 100

    private lateinit var localStore: LocalStore
    private val _state = MutableStateFlow(
        AppUiState(
            serverHost = "",
            connectionStatus = ConnectionStatus.Disconnected,
            messages = emptyList(),
            registeredApps = emptyMap()
        )
    )
    private val _themeMode = MutableStateFlow(ThemeMode.LIGHT)
    private val _listMode = MutableStateFlow(ListMode.TIME)
    private val _projectExpandCounts = MutableStateFlow<Map<String, Int>>(emptyMap())
    private val _overlayDebugEnabled = MutableStateFlow(false)
    private val _dynamicEffectsEnabled = MutableStateFlow(LocalStore.DEFAULT_DYNAMIC_EFFECTS)
    private val _hostMasked = MutableStateFlow(false)
    private val _deviceName = MutableStateFlow(LocalStore.DEFAULT_DEVICE_NAME)

    val state: StateFlow<AppUiState> = _state.asStateFlow()
    val themeMode: StateFlow<ThemeMode> = _themeMode.asStateFlow()
    val listMode: StateFlow<ListMode> = _listMode.asStateFlow()
    val projectExpandCounts: StateFlow<Map<String, Int>> = _projectExpandCounts.asStateFlow()
    val overlayDebugEnabled: StateFlow<Boolean> = _overlayDebugEnabled.asStateFlow()
    val dynamicEffectsEnabled: StateFlow<Boolean> = _dynamicEffectsEnabled.asStateFlow()
    val hostMasked: StateFlow<Boolean> = _hostMasked.asStateFlow()
    val deviceName: StateFlow<String> = _deviceName.asStateFlow()

    val currentServerHost: String
        get() = _state.value.serverHost

    /** 本地是否存过"曾经连接成功过"的 host，用于判断能否自动重连。 */
    val hasKnownGoodHost: Boolean
        get() = ::localStore.isInitialized &&
            normalizeServerHost(localStore.loadServerHost()).isNotBlank()

    /**
     * 曾经连接成功过的地址（rememberSuccessfulHost 写入）。
     * 手动输入公网地址失败时，用它作为"同路由局域网"兜底候选，
     * 实现公网隧道不可用时自动切回局域网的傻瓜式容错。
     */
    val lastKnownGoodHost: String
        get() = if (::localStore.isInitialized) {
            normalizeServerHost(localStore.loadServerHost())
        } else {
            ""
        }

    /** 首次启动权限向导是否已完成（未完成时 App 首启自动弹出权限引导）。 */
    val isOnboardingDone: Boolean
        get() = !::localStore.isInitialized || localStore.loadOnboardingDone()

    fun completeOnboarding() {
        if (::localStore.isInitialized) {
            localStore.saveOnboardingDone(true)
        }
    }

    fun initialize(context: Context) {
        if (::localStore.isInitialized) return
        localStore = LocalStore(context.applicationContext)
        // 本地只保存"曾经连接成功过"的 IP；没有时退回内置默认 IP，用户可在界面上直接改。
        val savedHost = normalizeServerHost(localStore.loadServerHost())
        _state.value = _state.value.copy(
            serverHost = savedHost.ifBlank { ServerConfig.DEFAULT_HOST },
            messages = localStore.loadMessages(),
            registeredApps = localStore.loadRegisteredApps(),
            connectionStatus = ConnectionStatus.Disconnected
        )
        _themeMode.value = runCatching {
            ThemeMode.valueOf(localStore.loadThemeMode())
        }.getOrDefault(ThemeMode.LIGHT)
        _listMode.value = runCatching {
            ListMode.valueOf(localStore.loadListMode())
        }.getOrDefault(ListMode.TIME)
        _projectExpandCounts.value = localStore.loadProjectExpandCounts()
        _overlayDebugEnabled.value = localStore.loadOverlayDebugEnabled()
        _dynamicEffectsEnabled.value = localStore.loadDynamicEffectsEnabled()
        _hostMasked.value = localStore.loadHostMasked()
        _deviceName.value = localStore.loadDeviceName()
    }

    fun setOverlayDebugEnabled(enabled: Boolean) {
        _overlayDebugEnabled.value = enabled
        if (::localStore.isInitialized) {
            localStore.saveOverlayDebugEnabled(enabled)
        }
    }

    fun setDynamicEffectsEnabled(enabled: Boolean) {
        _dynamicEffectsEnabled.value = enabled
        if (::localStore.isInitialized) {
            localStore.saveDynamicEffectsEnabled(enabled)
        }
    }

    /** 切换隐藏 IP 开关并持久化，重启后保持。 */
    fun setHostMasked(masked: Boolean) {
        _hostMasked.value = masked
        if (::localStore.isInitialized) {
            localStore.saveHostMasked(masked)
        }
    }

    /** 更新首页显示的 PC 名称；空名称由首页回退为默认名称。 */
    fun setDeviceName(name: String) {
        val normalized = name.trim().take(40)
        _deviceName.value = normalized
        if (::localStore.isInitialized) {
            localStore.saveDeviceName(normalized)
        }
    }

    fun setThemeMode(mode: ThemeMode) {
        _themeMode.value = mode
        if (::localStore.isInitialized) {
            localStore.saveThemeMode(mode.name)
        }
    }

    fun setListMode(mode: ListMode) {
        _listMode.value = mode
        if (::localStore.isInitialized) {
            localStore.saveListMode(mode.name)
        }
    }

    /**
     * 设置某项目分组的可见消息条数（记忆用户展开/折叠选择）。
     * - 默认 [DEFAULT_PROJECT_VISIBLE_COUNT]
     * - "查看6条"：count += 6
     * - "查看全部"：count = Int.MAX_VALUE（UI 层 take 实际数量）
     * - "折叠"：count = [DEFAULT_PROJECT_VISIBLE_COUNT]
     */
    fun setProjectExpandCount(source: String, count: Int) {
        val next = _projectExpandCounts.value.toMutableMap().apply {
            if (count <= DEFAULT_PROJECT_VISIBLE_COUNT) {
                // 折叠回默认值时清除记录，避免无意义的持久化条目
                remove(source)
            } else {
                put(source, count)
            }
        }
        _projectExpandCounts.value = next
        if (::localStore.isInitialized) {
            localStore.saveProjectExpandCounts(next)
        }
    }

    fun updateServerHost(host: String) {
        val normalized = normalizeServerHost(host)
        _state.value = _state.value.copy(serverHost = normalized)
    }

    /**
     * 仅在成功建立连接后调用，把当前 host 记为下次启动的默认值。
     * 避免把用户输入到一半、还没验证过的 IP 当作默认值存下来覆盖旧的可用值。
     */
    fun rememberSuccessfulHost(host: String) {
        val normalized = normalizeServerHost(host)
        if (normalized.isBlank()) return
        if (::localStore.isInitialized) {
            localStore.saveServerHost(normalized)
        }
    }

    private fun normalizeServerHost(input: String): String {
        val trimmed = input.trim()
        // 保留 ws:// / wss:// scheme：ServerConfig 依赖前缀识别隧道地址
        // （wss://域名 = 443 公网隧道），剥掉后重连会退化成 ws:// 连 443 而失败。
        if (trimmed.startsWith("wss://", ignoreCase = true) ||
            trimmed.startsWith("ws://", ignoreCase = true) ||
            trimmed.startsWith("https://", ignoreCase = true)) {
            return trimmed
        }
        return ServerConfig.normalizeHost(trimmed)
    }

    fun setConnectionStatus(status: ConnectionStatus) {
        _state.value = _state.value.copy(connectionStatus = status)
    }

    fun replaceRegisteredApps(apps: Collection<RegisteredApp>) {
        val normalized = apps.asSequence()
            .map { app -> app.copy(appKey = app.normalizedKey) }
            .filter {
                it.normalizedKey.isNotBlank() &&
                    it.displayName.isNotBlank()
            }
            .distinctBy { it.normalizedKey }
            .associateBy { it.normalizedKey }
        _state.value = _state.value.copy(registeredApps = normalized)
        if (::localStore.isInitialized) {
            localStore.saveRegisteredApps(normalized.values)
        }
    }

    fun appendMessage(message: NotifyMessage): NotifyMessage {
        val existingIds = _state.value.messages.map { it.id }.toHashSet()
        val storedMessage = message.withUniqueMessageId(existingIds)
        val next = (listOf(storedMessage) + _state.value.messages).take(MAX_HISTORY_SIZE)
        saveMessages(next)
        return storedMessage
    }

    /**
     * Resolves an approval in place so the notification action cannot leave a
     * stale waiting item in history. Returns null when the request is unknown;
     * callers may then treat the incoming resolution as a standalone event.
     */
    fun resolveApproval(
        requestId: String,
        decision: String,
        status: String = "",
        resolutionContent: String = ""
    ): NotifyMessage? {
        val normalizedRequestId = requestId.trim()
        if (normalizedRequestId.isBlank()) return null
        val normalizedDecision = decision.trim().lowercase()
        if (normalizedDecision != "allow" && normalizedDecision != "deny") return null

        val current = _state.value.messages.firstOrNull { it.requestId == normalizedRequestId }
            ?: return null
        val resolvedStatus = status.trim().ifBlank {
            if (normalizedDecision == "allow") "approved" else "rejected"
        }
        val fallbackContent = if (normalizedDecision == "allow") {
            "已允许：${current.toolName}".trimEnd('：', ' ')
        } else {
            "已拒绝：${current.toolName}".trimEnd('：', ' ')
        }
        val updated = current.copy(
            status = resolvedStatus,
            content = resolutionContent.trim().ifBlank { fallbackContent },
            eventType = "approval.resolved",
            requiresAction = false,
            approvalDecision = normalizedDecision,
            isRead = true
        )
        saveMessages(_state.value.messages.map { message ->
            if (message.id == current.id) updated else message
        })
        return updated
    }

    /**
     * 把手机端发出的指令原文记录到对应消息并持久化。agent.result 回执回显时
     * 直接从消息读取，重启后历史仍可追溯；不再依赖 WebSocketService 的内存缓存。
     */
    fun recordSentInstruction(messageId: String, instruction: String) {
        val trimmed = instruction.trim()
        if (messageId.isBlank() || trimmed.isBlank()) return
        saveMessages(_state.value.messages.map { message ->
            if (message.id == messageId && message.sentInstruction != trimmed) {
                message.copy(sentInstruction = trimmed)
            } else message
        })
    }

    fun markMessageRead(messageId: String) {
        saveMessages(_state.value.messages.map { message ->
            if (message.id == messageId && !message.isRead) message.copy(isRead = true) else message
        })
    }

    /** 批量标记已查看，用于一次划掉归档通知时确认其中的多条消息。 */
    fun markMessagesRead(messageIds: Collection<String>) {
        if (messageIds.isEmpty()) return
        val targets = messageIds.toSet()
        saveMessages(_state.value.messages.map { message ->
            if (message.id in targets && !message.isRead) message.copy(isRead = true) else message
        })
    }

    fun markAllMessagesRead() {
        saveMessages(_state.value.messages.map { message ->
            if (message.isRead) message else message.copy(isRead = true)
        })
    }

    fun clearMessages() {
        saveMessages(emptyList())
    }

    private fun saveMessages(messages: List<NotifyMessage>) {
        _state.value = _state.value.copy(messages = messages)
        if (::localStore.isInitialized) {
            localStore.saveMessages(messages)
        }
    }
}
