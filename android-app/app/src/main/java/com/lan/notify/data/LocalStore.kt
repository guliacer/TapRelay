package com.lan.notify.data

import android.content.Context
import com.lan.notify.ui.theme.ThemeMode
import org.json.JSONArray
import org.json.JSONObject

class LocalStore(context: Context) {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    fun loadServerHost(): String {
        return prefs.getString(KEY_SERVER_HOST, "") ?: ""
    }

    fun saveServerHost(host: String) {
        prefs.edit().putString(KEY_SERVER_HOST, host).apply()
    }

    /** 首页显示的 PC 设备名称；首次使用时采用通用名称，用户可在设备管理中修改。 */
    fun loadDeviceName(): String {
        return prefs.getString(KEY_DEVICE_NAME, null)
            ?.trim()
            ?.takeIf { it.isNotBlank() }
            ?: DEFAULT_DEVICE_NAME
    }

    fun saveDeviceName(name: String) {
        prefs.edit().putString(KEY_DEVICE_NAME, name).apply()
    }

    fun loadMessages(): List<NotifyMessage> {
        val raw = prefs.getString(KEY_MESSAGES, "[]") ?: "[]"
        return decodeMessages(raw)
    }

    fun saveMessages(messages: List<NotifyMessage>) {
        prefs.edit().putString(KEY_MESSAGES, encodeMessages(messages)).apply()
    }

    fun loadThemeMode(): String {
        return prefs.getString(KEY_THEME_MODE, ThemeMode.LIGHT.name) ?: ThemeMode.LIGHT.name
    }

    fun saveThemeMode(mode: String) {
        prefs.edit().putString(KEY_THEME_MODE, mode).apply()
    }

    fun loadListMode(): String {
        return prefs.getString(KEY_LIST_MODE, ListMode.TIME.name) ?: ListMode.TIME.name
    }

    fun saveListMode(mode: String) {
        prefs.edit().putString(KEY_LIST_MODE, mode).apply()
    }

    /**
     * 加载每个项目分组的展开条数（source -> visibleCount）。
     * 未记录的 source 在 UI 层回退到 [DEFAULT_PROJECT_VISIBLE_COUNT]。
     */
    fun loadProjectExpandCounts(): Map<String, Int> {
        val raw = prefs.getString(KEY_PROJECT_EXPAND_COUNTS, "{}") ?: "{}"
        return runCatching {
            val obj = JSONObject(raw)
            buildMap {
                for (key in obj.keys()) {
                    put(key, obj.optInt(key, DEFAULT_PROJECT_VISIBLE_COUNT))
                }
            }
        }.getOrElse { emptyMap() }
    }

    fun saveProjectExpandCounts(counts: Map<String, Int>) {
        val obj = JSONObject()
        counts.forEach { (k, v) -> obj.put(k, v) }
        prefs.edit().putString(KEY_PROJECT_EXPAND_COUNTS, obj.toString()).apply()
    }

    fun loadRegisteredApps(): Map<String, RegisteredApp> {
        val raw = prefs.getString(KEY_REGISTERED_APPS, "[]") ?: "[]"
        return decodeRegisteredApps(raw)
    }

    fun saveRegisteredApps(apps: Collection<RegisteredApp>) {
        prefs.edit().putString(KEY_REGISTERED_APPS, encodeRegisteredApps(apps)).apply()
    }

    fun loadOverlayDebugEnabled(): Boolean = prefs.getBoolean(KEY_OVERLAY_DEBUG, false)

    fun saveOverlayDebugEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_OVERLAY_DEBUG, enabled).apply()
    }

    /** 隐藏 IP 开关（地址框脱敏显示）。默认关闭，重启后保持用户上次的选择。 */
    fun loadHostMasked(): Boolean = prefs.getBoolean(KEY_HOST_MASKED, false)

    fun saveHostMasked(masked: Boolean) {
        prefs.edit().putBoolean(KEY_HOST_MASKED, masked).apply()
    }

    /** 首次启动权限向导是否已完成（true=已引导过，不再自动弹出）。 */
    fun loadOnboardingDone(): Boolean = prefs.getBoolean(KEY_ONBOARDING_DONE, false)

    fun saveOnboardingDone(done: Boolean) {
        prefs.edit().putBoolean(KEY_ONBOARDING_DONE, done).apply()
    }

    /** 动效开关默认开启；关闭后跳过状态色过渡/呼吸光晕/圆点缩放，仅保留档位形变过渡。 */
    fun loadDynamicEffectsEnabled(): Boolean =
        prefs.getBoolean(KEY_DYNAMIC_EFFECTS, DEFAULT_DYNAMIC_EFFECTS)

    fun saveDynamicEffectsEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_DYNAMIC_EFFECTS, enabled).apply()
    }

    companion object {
        private const val PREFS_NAME = "notify_local_store"
        private const val KEY_SERVER_HOST = "server_host"
        private const val KEY_DEVICE_NAME = "device_name"
        private const val KEY_MESSAGES = "messages"
        private const val KEY_THEME_MODE = "theme_mode"
        private const val KEY_LIST_MODE = "list_mode"
        private const val KEY_PROJECT_EXPAND_COUNTS = "project_expand_counts"
        private const val KEY_REGISTERED_APPS = "registered_apps"
        private const val KEY_OVERLAY_DEBUG = "overlay_debug"
        private const val KEY_DYNAMIC_EFFECTS = "dynamic_effects"
        private const val KEY_HOST_MASKED = "host_masked"
        private const val KEY_ONBOARDING_DONE = "onboarding_done"
        const val DEFAULT_DYNAMIC_EFFECTS = true
        const val DEFAULT_DEVICE_NAME = "PC设备"

        internal fun decodeRegisteredApps(raw: String): Map<String, RegisteredApp> {
            return runCatching {
                val array = JSONArray(raw)
                buildMap {
                    for (index in 0 until array.length()) {
                        val item = array.optJSONObject(index) ?: continue
                        val app = RegisteredApp(
                            appKey = item.optString("appKey").trim().lowercase(),
                            displayName = item.optString("displayName").trim(),
                            brandColor = item.optString("brandColor").trim(),
                            iconBase64 = item.optString("iconBase64")
                        )
                        if (app.normalizedKey.isNotBlank() &&
                            app.displayName.isNotBlank()
                        ) {
                            put(app.normalizedKey, app)
                        }
                    }
                }
            }.getOrElse { emptyMap() }
        }

        internal fun encodeRegisteredApps(apps: Collection<RegisteredApp>): String {
            val array = JSONArray()
            apps.sortedBy { it.normalizedKey }.forEach { app ->
                array.put(
                    JSONObject().apply {
                        put("appKey", app.normalizedKey)
                        put("displayName", app.displayName)
                        put("brandColor", app.brandColor)
                        put("iconBase64", app.iconBase64)
                    }
                )
            }
            return array.toString()
        }

        internal fun decodeMessages(raw: String): List<NotifyMessage> {
            return runCatching {
                val array = JSONArray(raw)
                val decoded = buildList {
                    for (index in 0 until array.length()) {
                        val item = array.optJSONObject(index) ?: continue
                        val type = item.optString("type")
                        val content = item.optString("content")
                        val time = item.optString("time")
                        val status = item.optString("status")
                            .ifBlank { NotifyMessage.STATUS_COMPLETED }
                        val source = normalizeMessageSource(item.optString("source"), content)
                        val taskId = item.optString("taskId")
                        add(
                            NotifyMessage(
                                type = type,
                                content = content,
                                time = time,
                                status = status,
                                source = source,
                                taskId = taskId,
                                cwd = item.optString("cwd"),
                                projectName = item.optString("projectName"),
                                durationMs = item.optLong("durationMs").coerceAtLeast(0L),
                                tokenCount = item.optLong("tokenCount").coerceAtLeast(0L),
                                credits = item.optString("credits").trim(),
                                sessionId = item.optString("sessionId"),
                                notificationTitle = item.optString("notificationTitle"),
                                taskLog = item.optString("taskLog"),
                                actionUrl = item.optString("actionUrl"),
                                eventId = item.optString("eventId"),
                                eventType = item.optString("eventType"),
                                requestId = item.optString("requestId"),
                                toolName = item.optString("toolName"),
                                inputSummary = item.optString("inputSummary"),
                                requiresAction = item.optBoolean("requiresAction", false),
                                expiresAtMs = item.optLong("expiresAtMs").coerceAtLeast(0L),
                                approvalDecision = item.optString("approvalDecision"),
                                sentInstruction = item.optString("sentInstruction"),
                                isRead = if (item.has("isRead")) item.optBoolean("isRead") else true,
                                id = item.optString("id").ifBlank {
                                    item.optString("eventId").takeIf { it.isNotBlank() }
                                        ?: taskId.takeIf { it.isNotBlank() }
                                        ?.let { "$source|$status|$it" }
                                        ?: "$time|$type|$content"
                                }
                            )
                        )
                    }
                }
                ensureUniqueMessageIds(decoded)
            }.getOrElse { emptyList() }
        }

        internal fun encodeMessages(messages: List<NotifyMessage>): String {
            val array = JSONArray()
            messages.forEach { message ->
                array.put(
                    JSONObject().apply {
                        put("type", message.type)
                        put("content", message.content)
                        put("time", message.time)
                        put("status", message.status)
                        put("source", message.source)
                        put("taskId", message.taskId)
                        put("cwd", message.cwd)
                        put("projectName", message.projectName)
                        put("durationMs", message.durationMs)
                        put("tokenCount", message.tokenCount)
                        put("credits", message.credits)
                        put("sessionId", message.sessionId)
                        put("notificationTitle", message.notificationTitle)
                        put("taskLog", message.taskLog)
                        put("actionUrl", message.actionUrl)
                        put("eventId", message.eventId)
                        put("eventType", message.eventType)
                        put("requestId", message.requestId)
                        put("toolName", message.toolName)
                        put("inputSummary", message.inputSummary)
                        put("requiresAction", message.requiresAction)
                        put("expiresAtMs", message.expiresAtMs)
                        put("approvalDecision", message.approvalDecision)
                        put("sentInstruction", message.sentInstruction)
                        put("isRead", message.isRead)
                        put("id", message.id)
                    }
                )
            }
            return array.toString()
        }
    }
}
