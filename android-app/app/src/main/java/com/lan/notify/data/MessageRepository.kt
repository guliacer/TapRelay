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
    private val _hostHistory = MutableStateFlow<List<HostHistoryEntry>>(emptyList())

    val state: StateFlow<AppUiState> = _state.asStateFlow()
    val themeMode: StateFlow<ThemeMode> = _themeMode.asStateFlow()
    val listMode: StateFlow<ListMode> = _listMode.asStateFlow()
    val projectExpandCounts: StateFlow<Map<String, Int>> = _projectExpandCounts.asStateFlow()
    val overlayDebugEnabled: StateFlow<Boolean> = _overlayDebugEnabled.asStateFlow()
    val dynamicEffectsEnabled: StateFlow<Boolean> = _dynamicEffectsEnabled.asStateFlow()
    val hostMasked: StateFlow<Boolean> = _hostMasked.asStateFlow()
    val deviceName: StateFlow<String> = _deviceName.asStateFlow()

    /** 历史连接地址，最近使用在前，最多 [MAX_HOST_HISTORY] 条。 */
    val hostHistory: StateFlow<List<HostHistoryEntry>> = _hostHistory.asStateFlow()

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
        _hostHistory.value = initialHostHistory(localStore.loadHostHistory(), savedHost)
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
        val sanitized = sanitizeServerHostInput(host)
        _state.value = _state.value.copy(serverHost = sanitized)
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

    /**
     * 记录一次「地址被成功使用」，供设备管理里的历史地址列表使用。
     *
     * 调用方必须是**连接建立成功**的时机（`WebSocketService.markSocketVerified`），
     * 不要在输入框变更或点击连接时调用：前者会把 `1`、`19`、`192` 这类半成品记进历史，
     * 后者会把打错的地址记进历史。
     *
     * 去重、倒序与 10 条上限由 [mergeHostHistory] 统一处理。
     */
    fun recordHostUsage(host: String) {
        val normalized = normalizeServerHost(host)
        if (normalized.isBlank()) return
        val next = mergeHostHistory(_hostHistory.value, normalized, System.currentTimeMillis())
        _hostHistory.value = next
        if (::localStore.isInitialized) {
            localStore.saveHostHistory(next)
        }
    }

    /**
     * 清空历史地址。
     *
     * 只清 `host_history` 这个键，**不动** `server_host`：
     * 后者是 [hasKnownGoodHost] / [lastKnownGoodHost] 的数据源，
     * 一并清掉会让 `MainActivity.onStart` 的自动重连直接失效。
     */
    fun clearHostHistory() {
        _hostHistory.value = emptyList()
        if (::localStore.isInitialized) {
            localStore.saveHostHistory(emptyList())
        }
    }

    /**
     * 严格闸门：用于**持久化与读取**（[rememberSuccessfulHost] / [hasKnownGoodHost] /
     * [lastKnownGoodHost] / [recordHostUsage]）。空串表示「没有可用地址」。
     *
     * 不要把输入框的原始文本直接喂给本函数，见 [sanitizeServerHostInput]。
     */
    private fun normalizeServerHost(input: String): String {
        val trimmed = input.trim()
        // 回环地址在任何写法下都不可用，必须**先于** scheme 判断，
        // 否则 `ws://127.0.0.1:1122` 会被下面的分支原样放行。
        if (isLoopbackAddress(trimmed)) return ""
        // 保留 ws:// / wss:// scheme：ServerConfig 依赖前缀识别隧道地址
        // （wss://域名 = 443 公网隧道），剥掉后重连会退化成 ws:// 连 443 而失败。
        if (trimmed.startsWith("wss://", ignoreCase = true) ||
            trimmed.startsWith("ws://", ignoreCase = true) ||
            trimmed.startsWith("https://", ignoreCase = true)) {
            return trimmed
        }
        return ServerConfig.normalizeHost(trimmed)
    }

    /**
     * 地址输入框的逐字符清洗。
     *
     * 与 [normalizeServerHost] 的唯一区别是：**容忍「还没输完」的中间态**。
     * 输入框每次按键都要过一遍归一化，输到 `10.0.2.2:` 时端口为空、
     * `ServerConfig.normalizeHost` 会判定非法 authority 返回空串；一旦把这个空串
     * 写回输入框，用户刚敲的整段地址就被清空，永远输不进端口（实测踩过）。
     *
     * 注意**不能**对空结果一律回退成原串：回环地址是「明确拒绝」，
     * 必须继续归一化成空串，否则 `localhost` 会被原样回填进输入框。
     * 真正的合法性校验交给连接时——连不上会正常回到未连接态。
     */
    private fun sanitizeServerHostInput(input: String): String {
        val trimmed = input.trim()
        // 回环地址是「明确拒绝」，不是「还没输完」，不能走下面的 ifBlank 回退。
        // 这行与 normalizeServerHost 内的同名判断是**故意重复**的：少了它，
        // normalizeServerHost 返回的空串会被 ifBlank 换回原文，localhost 就复活了。
        if (isLoopbackAddress(trimmed)) return ""
        return normalizeServerHost(trimmed).ifBlank { trimmed }
    }

    /**
     * 判断地址是否指向本机回环。
     *
     * App 跑在手机上，`localhost` / `127.0.0.1` / `[::1]` 永远指手机自己，不可能连到 PC，
     * 因此一律视为不可用地址，不允许保存，也不允许在输入框里当「中间态」回填。
     *
     * 这里必须把 scheme、路径**和端口**都剥掉再比较：
     * `ConnectionQrCodec.normalizeHost` 只拒绝裸的 `localhost` / `127.0.0.1`，
     * `http://localhost:1122`、`ws://127.0.0.1:1122` 会被它归一化成可用的 authority。
     * 若本函数照抄那种严格比较，回环地址就会顺着「容忍中间态」的通道重新泄漏回输入框。
     */
    private fun isLoopbackAddress(value: String): Boolean {
        val authority = value.substringAfter("://", value).substringBefore('/').trim()
        val host = if (authority.startsWith("[") && authority.contains("]")) {
            authority.substring(1, authority.indexOf(']'))
        } else {
            authority.substringBefore(':')
        }
        return host.equals("localhost", ignoreCase = true) || host == "127.0.0.1" || host == "::1"
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
