package com.lan.notify.service

import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import android.widget.Toast
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import com.lan.notify.data.AgentCapabilityCache
import com.lan.notify.data.ConnectionStatus
import com.lan.notify.data.MessageRepository
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.RelayConfig
import com.lan.notify.data.ServerConfig
import com.lan.notify.data.canonicalSourceKey
import com.lan.notify.data.normalizeMessageSource
import com.lan.notify.data.safeActionUri
import com.lan.notify.data.OverlayExpansion
import com.lan.notify.data.advance
import com.lan.notify.data.deriveOverlayState
import com.lan.notify.data.maskHostInText
import com.lan.notify.MainActivity
import com.lan.notify.notification.NotificationHelper
import com.lan.notify.overlay.TapRelayOverlayController
import com.lan.notify.overlay.TransientTaskHeadsUpController
import java.time.OffsetDateTime
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.TimeUnit
import kotlin.math.min
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject

class WebSocketService : LifecycleService() {
    private val tag = "NotifyWebSocket"
    private val client = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        // Bound the time spent on a single candidate so an unreachable host fails
        // fast and the service can move on to the next candidate in the QR list.
        // 4s: LAN 同一路由握手+心跳足够；蜂窝网络下局域网 IP 不可达时能快速
        // 切换到 wss 隧道候选，避免用户干等 8 秒误以为连接失败。
        .connectTimeout(4, TimeUnit.SECONDS)
        .build()
    private val relayClient = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        // Relay may be over the internet; a slightly longer timeout is appropriate.
        .connectTimeout(10, TimeUnit.SECONDS)
        .build()
    private val handler = Handler(Looper.getMainLooper())
    private val overlayController by lazy { TapRelayOverlayController(this) }
    private val transientHeadsUpController by lazy { TransientTaskHeadsUpController(this) }
    /** 悬浮层点击档位；默认 Peek 横向胶囊。新消息只刷新事件，不会把 Expanded 抢回 Peek。 */
    private var expansion = OverlayExpansion.Peek
    /** 防止同一手指事件/重组把 onOpen 连发两次。 */
    private var lastOverlayAdvanceAtMs = 0L
    /** Activity dialogs must own the top visual layer while they are visible. */
    @Volatile private var overlaySuppressedForModal = false

    private var socket: WebSocket? = null
    private var socketGeneration = 0
    private var shouldReconnect = false
    private var reconnectAttempt = 0
    private var serverHost = ""
    /** Ordered candidate hosts from the QR code; tried in sequence until one connects. */
    private var candidateHosts = emptyList<String>()
    /** Index into [candidateHosts] of the host currently being attempted. */
    private var candidateIndex = 0
    /** Host that last verified a successful connection; empty until first connect. */
    private var connectedHost = ""
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile private var lastSocketOpenTimeMs = 0L
    @Volatile private var lastSendTimeMs = 0L
    @Volatile private var lastReceiveTimeMs = 0L
    @Volatile private var lastHeartbeatAckTimeMs = 0L
    /** Relay config set by QR scan; when non-null, the service connects through relay instead of LAN. */
    @Volatile private var relayConfig: RelayConfig? = null
    private val pendingApprovalControls = ConcurrentLinkedQueue<ApprovalControl>()
    private val pendingPcPowerControls = ConcurrentLinkedQueue<PcPowerControl>()
    /** 远程控制请求只保留最新一条尚未写入 WebSocket 的指令，避免断线期间累积旧消息。 */
    private val pendingAgentCall = AtomicReference<AgentCall?>(null)

    /**
     * 最近一次前台通知的文案**原文**（未脱敏）与其中的地址。
     *
     * 刻意保存未脱敏的原文：「隐藏地址」开关是随时可切的，切换后要按最新开关重发通知，
     * 若这里存的是已脱敏串，重发也只会重复同一个掩码，开关看起来失效。
     */
    private var lastForegroundContent: String = "\u672a\u8fde\u63a5"
    private var lastForegroundHost: String? = null

    private val reconnectRunnable = Runnable { connectSocket() }
    private val heartbeatRunnable = object : Runnable {
        override fun run() {
            runHeartbeatTick()
        }
    }
    private val connectionMonitorRunnable = object : Runnable {
        override fun run() {
            logConnectionMonitor()
        }
    }

    override fun onCreate() {
        super.onCreate()
        MessageRepository.initialize(applicationContext)
        NotificationHelper.createChannels(this)
        // Restore the grouped notification entry after a process restart so
        // persisted unread messages remain visible before the next socket event.
        NotificationHelper.syncTaskNotifications(
            this,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
        val serviceNotification = NotificationHelper.createServiceNotification(this, "\u672a\u8fde\u63a5")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NotificationHelper.SERVICE_NOTIFICATION_ID,
                serviceNotification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_REMOTE_MESSAGING
            )
        } else {
            startForeground(NotificationHelper.SERVICE_NOTIFICATION_ID, serviceNotification)
        }

        // \u8ba2\u9605\u6d88\u606f\u5217\u8868\u53d8\u5316\uff0c\u81ea\u52a8\u540c\u6b65\u60ac\u6d6e\u5361\u7247\uff1a
        // \u4efb\u4f55\u8def\u5f84\uff08APP\u5185\u70b9\u51fb\u6807\u8bb0\u3001\u901a\u77e5 dismiss\u3001\u5361\u7247 dismiss \u56de\u8c03\uff09\u6539\u4e86\u5df2\u8bfb\u72b6\u6001\u540e\u90fd\u4f1a
        // \u901a\u8fc7 StateFlow \u6d41\u5230 service\uff0c\u89e6\u53d1 showIsland() \u5237\u65b0\u52a8\u7075\u5c9b\u3002
        // \u907f\u514d\u51fa\u73b0\u201cAPP\u5df2\u8bfb\u4f46\u5361\u7247\u8fd8\u505c\u7559\u201d\u7684\u72b6\u6001\u4e0d\u4e00\u81f4\u3002
        // \u53ea\u5173\u6ce8 messages \u5b57\u6bb5\u53d8\u5316\uff0c\u907f\u514d connectionStatus/serverHost \u53d8\u5316\u65f6\u591a\u4f59\u5237\u65b0\u3002
        lifecycleScope.launch {
            MessageRepository.state
                .map { it.messages to it.registeredApps }
                .distinctUntilChanged()
                .collect {
                    handler.post { showIsland() }
                }
        }
        lifecycleScope.launch {
            MessageRepository.overlayDebugEnabled.collect {
                handler.post { showIsland() }
            }
        }
        lifecycleScope.launch {
            MessageRepository.dynamicEffectsEnabled.collect {
                handler.post { showIsland() }
            }
        }
        // 「隐藏地址」开关变化时立即重发前台通知。
        // 已连接状态下通知文案不会自己刷新：不重发的话，用户打开开关后锁屏上仍是明文地址，
        // 要等到下一次连接事件才生效，开关看起来是坏的。
        // 与上面几个收集同属「开关 -> 重渲染」模式；首帧会重发一次相同文案，无副作用。
        lifecycleScope.launch {
            MessageRepository.hostMasked.collect {
                handler.post { postForeground() }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        when (intent?.action) {
            ACTION_CONNECT -> {
                // Check for relay mode
                val relayUrl = intent.getStringExtra(EXTRA_RELAY_URL)?.trim().takeIf { !it.isNullOrBlank() }
                val relayRoomId = intent.getStringExtra(EXTRA_RELAY_ROOM_ID)?.trim().takeIf { !it.isNullOrBlank() }
                if (relayUrl != null && relayRoomId != null) {
                    relayConfig = RelayConfig(url = relayUrl, roomId = relayRoomId)
                    candidateHosts = listOf(relayUrl)
                } else {
                    relayConfig = null
                    val hosts = intent.getStringArrayListExtra(EXTRA_SERVER_HOSTS)
                        ?.mapNotNull { it?.trim()?.takeIf { h -> h.isNotBlank() } }
                        ?: emptyList()
                    candidateHosts = if (hosts.isNotEmpty()) {
                        hosts
                    } else {
                        val single = intent.getStringExtra(EXTRA_SERVER_HOST)?.trim()
                            .takeIf { !it.isNullOrBlank() }
                            ?: MessageRepository.currentServerHost.takeIf { it.isNotBlank() }
                        single?.let { listOf(it) } ?: emptyList()
                    }
                    // 傻瓜式容错：公网隧道（cloudflared 等）不可用时，自动把
                    // "曾经连接成功过的主机"（通常是同一路由下的局域网地址）追加为
                    // 兜底候选，失败后自动切换，用户零操作。不抢用户指定地址的优先顺序。
                    val remembered = MessageRepository.lastKnownGoodHost
                    if (remembered.isNotBlank() &&
                        candidateHosts.none { it.equals(remembered, ignoreCase = true) }
                    ) {
                        candidateHosts = candidateHosts + remembered
                    }
                }
                candidateIndex = 0
                connectedHost = ""
                shouldReconnect = true
                acquireWakeLock()
                connectNextCandidate()
            }

            ACTION_ENSURE_CONNECTED -> {
                // \u524d\u53f0\u6062\u590d\u65f6\u4f7f\u7528\uff1a\u4ec5\u5f53\u8fde\u63a5\u53ef\u80fd\u5df2\u5931\u6548\u65f6\u624d\u91cd\u8fde\u3002
                // HANS \u53ef\u80fd\u9759\u9ed8\u5173\u95ed socket \u4e14\u4e0d\u89e6\u53d1 OkHttp \u56de\u8c03\uff0c
                // \u5982\u679c\u8fd1 45s \u5185\u6709\u5fc3\u8df3 ACK \u6216\u6d88\u606f\u5230\u8fbe\uff0c\u8bf4\u660e\u8fde\u63a5\u4ecd\u6d3b\u7740\uff0c\u4e0d\u5e94\u7834\u574f\u3002
                serverHost = intent.getStringExtra(EXTRA_SERVER_HOST)?.trim().orEmpty()
                if (serverHost.isBlank()) {
                    serverHost = MessageRepository.currentServerHost
                }
                candidateHosts = listOf(serverHost)
                candidateIndex = 0
                shouldReconnect = true
                acquireWakeLock()
                if (isConnectionLikelyStale()) {
                    Log.i(tag, "connection stale on resume, force reconnect host=$serverHost")
                    connectSocket()
                } else {
                    // \u8fde\u63a5\u4e0d\u9648\u65e7\uff0c\u4f46 HANS \u53ef\u80fd\u5df2\u9759\u9ed8\u5173\u95ed socket\u3002
                    // \u53d1\u9001\u63a2\u6d4b\u5fc3\u8df3\u9a8c\u8bc1\uff1a
                    // - send() \u8fd4\u56de false \u2192 failCurrentSocket \u7acb\u5373\u91cd\u8fde
                    // - send() \u8fd4\u56de true \u4f46\u8fde\u63a5\u5df2\u65ad \u2192 OkHttp \u5199\u7ebf\u7a0b\u68c0\u6d4b\u5230\u540e\u8c03 onFailure \u2192 \u91cd\u8fde
                    // - send() \u8fd4\u56de true \u4e14\u8fde\u63a5\u5b58\u6d3b \u2192 ACK \u5230\u8fbe\uff0c\u8fde\u63a5\u7ee7\u7eed
                    val currentSocket = socket
                    if (currentSocket != null) {
                        Log.i(tag, "connection possibly healthy on resume, sending probe heartbeat host=$serverHost")
                        sendHeartbeat(currentSocket, System.currentTimeMillis())
                    } else {
                        connectSocket()
                    }
                }
            }

            ACTION_RESOLVE_APPROVAL -> {
                val requestId = intent.getStringExtra(EXTRA_APPROVAL_REQUEST_ID)?.trim().orEmpty()
                val decision = intent.getStringExtra(EXTRA_APPROVAL_DECISION)?.trim()?.lowercase().orEmpty()
                if (requestId.isBlank() || (decision != "allow" && decision != "deny")) {
                    Log.w(tag, "ignored invalid approval control requestId=$requestId decision=$decision")
                    return START_STICKY
                }

                serverHost = MessageRepository.currentServerHost
                candidateHosts = listOf(serverHost)
                candidateIndex = 0
                shouldReconnect = true
                acquireWakeLock()
                val control = ApprovalControl(requestId, decision)
                val currentSocket = socket
                if (currentSocket == null || !sendApprovalResolution(currentSocket, control)) {
                    pendingApprovalControls.add(control)
                    if (currentSocket == null) {
                        connectSocket()
                    }
                }
            }

            ACTION_PC_POWER -> {
                val command = intent.getStringExtra(EXTRA_PC_POWER_COMMAND)?.trim().orEmpty()
                if (command != PC_POWER_SLEEP &&
                    command != PC_POWER_SCREEN_OFF &&
                    command != PC_POWER_SHUTDOWN) {
                    Log.w(tag, "ignored invalid pc power command=$command")
                    return START_STICKY
                }

                serverHost = MessageRepository.currentServerHost
                candidateHosts = listOf(serverHost)
                candidateIndex = 0
                shouldReconnect = true
                acquireWakeLock()
                val control = PcPowerControl(command)
                val currentSocket = socket
                if (currentSocket == null || !sendPcPowerControl(currentSocket, control)) {
                    pendingPcPowerControls.add(control)
                    if (currentSocket == null) {
                        connectSocket()
                    }
                }
            }

            ACTION_AGENT_CALL -> {
                val agentKey = intent.getStringExtra(EXTRA_AGENT_KEY)?.trim().orEmpty()
                val method = intent.getStringExtra(EXTRA_AGENT_METHOD)?.trim().orEmpty()
                val sessionId = intent.getStringExtra(EXTRA_AGENT_SESSION_ID)?.trim().orEmpty()
                if (agentKey.isBlank() || method != METHOD_SESSION_STOP) {
                    Log.w(tag, "ignored invalid agent call agentKey=$agentKey method=$method")
                    return START_STICKY
                }

                serverHost = MessageRepository.currentServerHost
                candidateHosts = listOf(serverHost)
                candidateIndex = 0
                shouldReconnect = true
                acquireWakeLock()
                Log.i(
                    tag,
                    "queue agent.call agentKey=$agentKey method=$method sessionId=$sessionId"
                )
                val currentSocket = socket
                if (currentSocket == null || !sendAgentCall(currentSocket, agentKey, method, sessionId)) {
                    val replacement = AgentCall(agentKey, method, sessionId)
                    val previous = pendingAgentCall.getAndSet(replacement)
                    if (previous != null) {
                        Log.i(
                            tag,
                            "replaced pending agent.call oldSessionId=${previous.sessionId} " +
                                "newSessionId=$sessionId"
                        )
                    }
                    if (currentSocket == null) {
                        connectSocket()
                    }
                }
            }

            ACTION_MODAL_OPEN -> {
                overlaySuppressedForModal = true
                handler.post {
                    overlayController.hide()
                    transientHeadsUpController.hide()
                }
            }

            ACTION_MODAL_CLOSE -> {
                overlaySuppressedForModal = false
                handler.post { showIsland() }
            }

            ACTION_DISCONNECT -> {
                shouldReconnect = false
                pendingApprovalControls.clear()
                pendingPcPowerControls.clear()
                pendingAgentCall.set(null)
                disconnectSocket()
                releaseWakeLock()
                MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
                updateForeground("\u672a\u8fde\u63a5")
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }

            else -> {
                // START_STICKY \u91cd\u542f\u670d\u52a1\u65f6 intent \u4e3a null\u3002\u6b64\u65f6\u82e5\u672c\u5730\u5b58\u8fc7\u6210\u529f\u8fde\u63a5\u8fc7\u7684
                // host \u5c31\u81ea\u52a8\u91cd\u8fde\uff0c\u5426\u5219\u670d\u52a1\u4f1a\u6d3b\u7740\u4f46 socket \u6c38\u8fdc\u4e0d\u8fde\uff0c\u524d\u53f0\u901a\u77e5\u8fd8\u505c\u7559\u5728
                // \u91cd\u542f\u524d\u7684"\u5df2\u8fde\u63a5"\u6587\u6848\uff0c\u7528\u6237\u770b\u4e0d\u51fa\u5df2\u7ecf\u6536\u4e0d\u5230\u6d88\u606f\u4e86\u3002
                resumeConnectionIfPossible()
            }
        }
        return START_STICKY
    }

    /** \u8fdb\u7a0b\u88ab\u7cfb\u7edf\u56de\u6536\u91cd\u542f\u540e\u81ea\u6108\uff1a\u7528\u4e0a\u6b21\u6210\u529f\u8fde\u63a5\u8fc7\u7684 host \u91cd\u65b0\u5efa\u7acb\u8fde\u63a5\u3002 */
    private fun resumeConnectionIfPossible() {
        val host = serverHost.ifBlank { MessageRepository.currentServerHost }
        if (host.isBlank()) {
            MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
            updateForeground("\u672a\u8fde\u63a5")
            return
        }

        Log.i(tag, "resume after process restart host=$host")
        serverHost = host
        candidateHosts = listOf(host)
        candidateIndex = 0
        shouldReconnect = true
        acquireWakeLock()
        connectSocket()
    }

    /**
     * \u5224\u65ad\u5f53\u524d\u8fde\u63a5\u662f\u5426\u53ef\u80fd\u5df2\u5931\u6548\u3002
     *
     * HANS \u53ef\u80fd\u9759\u9ed8\u5173\u95ed socket \u4e14\u4e0d\u89e6\u53d1 OkHttp \u7684 onClosed/onFailure \u56de\u8c03\uff0c
     * \u56e0\u6b64\u4e0d\u80fd\u4ec5\u9760 socket != null \u5224\u65ad\u3002\u7528\u6700\u8fd1\u4e00\u6b21\u5fc3\u8df3 ACK \u6216
     * \u6d88\u606f\u5230\u8fbe\u7684\u65f6\u95f4\u6233\u5224\u65ad\uff1a\u8d85\u8fc7 STALE_THRESHOLD_MS \u672a\u6536\u5230\u4efb\u4f55
     * \u54cd\u5e94\uff0c\u8bf4\u660e\u8fde\u63a5\u53ef\u80fd\u5df2\u88ab HANS \u5207\u65ad\uff0c\u9700\u8981\u91cd\u8fde\u3002
     */
    private fun isConnectionLikelyStale(): Boolean {
        if (socket == null) return true
        val now = System.currentTimeMillis()
        val lastActivity = maxOf(lastHeartbeatAckTimeMs, lastReceiveTimeMs)
        if (lastActivity <= 0L) return true
        val silence = now - lastActivity
        if (silence > STALE_THRESHOLD_MS) {
            Log.i(tag, "connection stale: lastActivity=${ageText(now, lastActivity)}")
            return true
        }
        return false
    }

    /**
     * 送达一条新消息：优先用悬浮卡片逐条独立展示，无悬浮窗权限时降级为普通通知。
     *
     * 两条路径互斥，**绝不能对同一 messageNotificationId 连续两次 alert notify**：
     * NotificationManagerService 把同 key 的第二次 notify 判为「更新」而非「新建」，
     * 会取消第一次正在排队的声音，第二次又因同 key 已 alert 过而不再响，最终静音
     * （见 P021）。因此这里用 if/else 二选一：
     * - 悬浮卡片可见 → `postFullScreenAlert`：响铃 + 挂 fullScreenIntent 覆盖锁屏场景，
     *   同时刷新共享通知组；markMessageRead → syncTaskNotifications 会更新汇总项和子项。
     * - 悬浮卡片不可见 → `syncTaskNotifications`：alert 分支已挂 fullScreenIntent 并响铃，
     *   不再额外 postFullScreenAlert，避免同 ID 重复 alert。
     *
     * 必须切到主线程：本方法由 OkHttp 的 reader thread 调用，而 ComposeView 构造、
     * WindowManager.addView 与 Compose state 赋值都只能在主线程执行；直接在读线程
     * 操作会把异常抛回 OkHttp 读循环，导致连接被判定读失败而断开。
     */
    private fun publishMessage(message: NotifyMessage) {
        handler.post {
            try {
                val islandShown = showIsland()
                val messages = MessageRepository.state.value.messages
                val registeredApps = MessageRepository.state.value.registeredApps
                val transientShown = if (!overlaySuppressedForModal && canUseTransientHeadsUp()) {
                    transientHeadsUpController.show(
                        message = message,
                        apps = registeredApps,
                        onDismiss = { dismissed ->
                            MessageRepository.markMessageRead(dismissed.id)
                            NotificationHelper.syncTaskNotifications(
                                applicationContext,
                                MessageRepository.state.value.messages,
                                registeredApps = MessageRepository.state.value.registeredApps
                            )
                            showIsland()
                        },
                        onAutoDismiss = { showIsland() },
                        onResolveApproval = { target, decision -> resolveApprovalFromOverlay(target, decision) },
                        onProcessWaiting = { target -> processWaitingFromOverlay(target) }
                    )
                } else {
                    false
                }
                Log.i(
                    tag,
                    "published message taskId=${message.taskId} islandShown=$islandShown " +
                        "transientHeadsUpShown=$transientShown"
                )
                if (transientShown) {
                    // The transient card owns this visual slot. Hide the persistent
                    // dynamic-island window until the transient card is gone, so no
                    // older peek surface leaks above it.
                    overlayController.hide()
                    // The app-owned transient card is visible. Keep the status-bar entry,
                    // but make it silent so ColorOS does not render its duplicate app icon.
                    NotificationHelper.syncTaskNotifications(
                        applicationContext,
                        messages,
                        registeredApps = registeredApps
                    )
                } else if (islandShown) {
                    NotificationHelper.postFullScreenAlert(
                        applicationContext,
                        message,
                        messages = messages,
                        registeredApps = registeredApps
                    )
                } else {
                    NotificationHelper.syncTaskNotifications(
                        applicationContext,
                        messages,
                        alertMessage = message,
                        registeredApps = registeredApps
                    )
                }
            } catch (error: Exception) {
                Log.e(tag, "overlay publish failed, falling back to task notification", error)
                runCatching {
                    NotificationHelper.syncTaskNotifications(
                        applicationContext,
                        MessageRepository.state.value.messages,
                        alertMessage = message,
                        registeredApps = MessageRepository.state.value.registeredApps
                    )
                }.onFailure { fallbackError ->
                    Log.e(tag, "task notification fallback failed", fallbackError)
                }
            }
        }
    }

    private fun handleLegacyMessage(payload: JSONObject) {
        val content = payload.optString("content")
        val status = payload.optString("status")
            .ifBlank { NotifyMessage.STATUS_COMPLETED }
        val isWaiting = status.equals(NotifyMessage.STATUS_WAITING, ignoreCase = true)
        val message = NotifyMessage(
            type = payload.optString("type"),
            content = content,
            time = payload.optString("time"),
            status = status,
            source = normalizeMessageSource(payload.optString("source"), content),
            taskId = payload.optString("taskId"),
            cwd = payload.optString("cwd"),
            projectName = payload.optString("projectName"),
            durationMs = payload.optLong("durationMs").coerceAtLeast(0L),
            tokenCount = payload.optLong("tokenCount").coerceAtLeast(0L),
            credits = payload.optString("credits").trim(),
            sessionId = payload.optString("sessionId"),
            notificationTitle = payload.optString("notificationTitle"),
            taskLog = payload.optString("taskLog"),
            actionUrl = payload.optString("actionUrl"),
            // 旧版 type=message 消息在 status=waiting 时也需要标记为待处理，
            // 这样手机端 overlayForeground 才会将其优先展示为等待卡片（P067 补充修复）。
            requiresAction = isWaiting,
            eventType = if (isWaiting) "approval.requested" else "",
            // 优先取 PC 端下发的专用 requestId（如 workbuddy:<sessionId>），
            // 空时回退 taskId 兼容旧审批链路。
            // A legacy waiting message is display-only unless the producer supplied
            // a real requestId (for example WorkBuddy's remote approval bridge).
            // Never turn taskId/eventId into an approval handle: /send does not
            // register a pending approval and such a button could not resolve it.
            requestId = if (isWaiting) payload.optString("requestId").trim() else "",
            toolName = payload.optString("toolName"),
            inputSummary = payload.optString("inputSummary"),
            expiresAtMs = payload.optLong("expiresAt").coerceAtLeast(0L)
        )
        Log.i(
            tag,
            "received message source=${message.source} status=${message.status} " +
                "taskId=${message.taskId} requiresAction=${message.requiresAction} " +
                "contentLength=${message.content.length}"
        )
        publishMessage(MessageRepository.appendMessage(message))
    }

    private fun handleEventPayload(payload: JSONObject) {
        val message = eventMessage(payload)
        if (message.eventType.equals("approval.resolved", ignoreCase = true)) {
            val updated = MessageRepository.resolveApproval(
                requestId = message.requestId,
                decision = message.approvalDecision,
                status = message.status,
                resolutionContent = message.content
            )
            if (updated != null) {
                syncNotifications()
                return
            }
        }

        Log.i(
            tag,
            "received event type=${message.eventType} source=${message.source} " +
                "status=${message.status} requestId=${message.requestId}"
        )
        publishMessage(MessageRepository.appendMessage(message))
    }

    private fun handleControlResult(payload: JSONObject) {
        val action = payload.optString("action")
        if (action.equals("pc.sleep", ignoreCase = true) ||
            action.equals("pc.screenOff", ignoreCase = true) ||
            action.equals("pc.shutdown", ignoreCase = true)
        ) {
            val succeeded = payload.optBoolean("succeeded", false)
            val error = payload.optString("error").ifBlank { null }
            Log.i(
                tag,
                "control result action=$action succeeded=$succeeded" +
                    (error?.let { " error=$it" } ?: "")
            )
            val resultText = when {
                succeeded && action.equals("pc.shutdown", ignoreCase = true) ->
                    "电脑将在 10 秒后关机"
                succeeded -> "电脑正在进入睡眠"
                else -> "电脑操作失败：${error ?: "未返回错误详情"}"
            }
            handler.post {
                Toast.makeText(applicationContext, resultText, Toast.LENGTH_LONG).show()
            }
            return
        }

        if (!action.equals("approval.resolve", ignoreCase = true) ||
            !payload.optBoolean("succeeded", false)
        ) {
            return
        }

        val requestId = payload.optString("requestId").trim()
        val decision = payload.optString("decision").trim().lowercase()
        if (requestId.isBlank() || (decision != "allow" && decision != "deny")) return
        if (MessageRepository.resolveApproval(requestId, decision) != null) {
            syncNotifications()
        }
    }

    private fun eventMessage(payload: JSONObject): NotifyMessage {
        val eventType = payload.optString("eventType").ifBlank { payload.optString("event") }
        val eventId = payload.optString("eventId")
        val content = payload.optString("message").ifBlank { payload.optString("content") }
        val status = payload.optString("status").ifBlank {
            if (eventType.equals("approval.requested", ignoreCase = true)) {
                NotifyMessage.STATUS_WAITING
            } else {
                NotifyMessage.STATUS_COMPLETED
            }
        }
        val isWaiting = status.equals(NotifyMessage.STATUS_WAITING, ignoreCase = true)
        val requestId = payload.optString("requestId").ifBlank {
            if (eventType.equals("approval.requested", ignoreCase = true)) eventId else ""
        }
        return NotifyMessage(
            type = "event",
            content = content,
            time = payload.optString("time").ifBlank { OffsetDateTime.now().toString() },
            status = status,
            source = normalizeMessageSource(payload.optString("source"), content),
            taskId = payload.optString("taskId"),
            cwd = payload.optString("cwd"),
            projectName = payload.optString("projectName"),
            durationMs = payload.optLong("durationMs").coerceAtLeast(0L),
            tokenCount = payload.optLong("tokenCount").coerceAtLeast(0L),
            credits = payload.optString("credits").trim(),
            sessionId = payload.optString("sessionId"),
            toolName = payload.optString("toolName").ifBlank { payload.optString("tool") },
            inputSummary = payload.optString("inputSummary"),
            taskLog = payload.optString("taskLog"),
            actionUrl = payload.optString("actionUrl"),
            eventId = eventId,
            eventType = eventType,
            requestId = requestId,
            requiresAction = payload.optBoolean("requiresAction", false) || isWaiting ||
                eventType.equals("approval.requested", ignoreCase = true),
            expiresAtMs = payload.optLong("expiresAt").coerceAtLeast(0L),
            approvalDecision = payload.optString("decision").trim().lowercase()
        )
    }

    private fun syncNotifications() {
        handler.post {
            NotificationHelper.syncTaskNotifications(
                applicationContext,
                MessageRepository.state.value.messages,
                registeredApps = MessageRepository.state.value.registeredApps
            )
        }
    }

    /**
     * 根据「当前未读消息 + 点击档位」用单一状态机派生 overlay 状态并刷新悬浮层。
     * 返回 false 表示没有悬浮窗权限，调用方应降级为普通通知。
     *
     * - 无未读 → Hidden（关闭最后一张卡后胶囊消失）。
     * - Peek：默认横向短提示（一眼可见新消息；取消被摄像头盖住的极小岛入场）。
     * - Expanded：点击胶囊后的卡片。
     * 新消息到来只刷新事件列表，不会把 Expanded 抢回 Peek。消息不得自动标已读。只允许在主线程调用。
     */
    private fun showIsland(): Boolean {
        if (overlaySuppressedForModal) {
            overlayController.hide()
            transientHeadsUpController.hide()
            return false
        }
        val unread = MessageRepository.state.value.messages.filter { !it.isRead }
        if (unread.isEmpty()) {
            expansion = OverlayExpansion.Peek
        }
        val state = deriveOverlayState(unread, expansion)
        return overlayController.show(
            state = state,
            registeredApps = MessageRepository.state.value.registeredApps,
            onOpen = onOpen@{
                val now = SystemClock.uptimeMillis()
                if (now - lastOverlayAdvanceAtMs < OVERLAY_ADVANCE_DEBOUNCE_MS) {
                    Log.d(tag, "overlay tap ignored debounce expansion=$expansion")
                    return@onOpen
                }
                val next = expansion.advance()
                if (next == expansion) return@onOpen
                lastOverlayAdvanceAtMs = now
                Log.i(tag, "overlay tap $expansion -> $next unread=${unread.size}")
                expansion = next
                showIsland()
            },
            onCardDismiss = { dismissed ->
                MessageRepository.markMessageRead(dismissed.id)
                NotificationHelper.syncTaskNotifications(
                    applicationContext,
                    MessageRepository.state.value.messages,
                    registeredApps = MessageRepository.state.value.registeredApps
                )
                showIsland()
            },
            onDismissAll = {
                val ids = MessageRepository.state.value.messages
                    .filter { !it.isRead }
                    .map { it.id }
                MessageRepository.markMessagesRead(ids)
                NotificationHelper.syncTaskNotifications(
                    applicationContext,
                    MessageRepository.state.value.messages,
                    registeredApps = MessageRepository.state.value.registeredApps
                )
                showIsland()
            },
            onResolveApproval = { message, decision ->
                resolveApprovalFromOverlay(message, decision)
            },
            onProcessWaiting = { message -> processWaitingFromOverlay(message) },
            debugEnabled = MessageRepository.overlayDebugEnabled.value,
            dynamicEffectsEnabled = MessageRepository.dynamicEffectsEnabled.value
        )
    }

    /** Resolve a real approval request from any app-owned overlay surface. */
    private fun resolveApprovalFromOverlay(message: NotifyMessage, decision: String) {
        val requestId = message.requestId.trim()
        if (requestId.isBlank() || (decision != "allow" && decision != "deny")) return
        val controlIntent = Intent(this, WebSocketService::class.java)
            .setAction(ACTION_RESOLVE_APPROVAL)
            .putExtra(EXTRA_APPROVAL_REQUEST_ID, requestId)
            .putExtra(EXTRA_APPROVAL_DECISION, decision)
        startForegroundService(controlIntent)
        MessageRepository.resolveApproval(requestId, decision)
        NotificationHelper.syncTaskNotifications(
            applicationContext,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
        showIsland()
    }

    /** Ordinary waiting tasks are actionable but are not approvals; open their safe target or app history. */
    private fun processWaitingFromOverlay(message: NotifyMessage) {
        MessageRepository.markMessageRead(message.id)
        NotificationHelper.syncTaskNotifications(
            applicationContext,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
        val target = safeActionUri(message.actionUrl)
        val intent = if (target != null) {
            Intent(Intent.ACTION_VIEW, target)
        } else {
            Intent(this, MainActivity::class.java).apply {
                putExtra(MainActivity.EXTRA_MESSAGE_ID, message.id)
                putExtra(MainActivity.EXTRA_MESSAGE_ACTION_URL, message.actionUrl)
            }
        }.apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        }
        runCatching { startActivity(intent) }
            .onFailure { Log.w(tag, "waiting task action launch failed", it) }
        showIsland()
    }

    /**
     * The custom transient card is only safe while the user can see and touch it.
     * Locked or non-interactive screens keep using the original full-screen intent
     * and status-bar layouts preserved in the notification-layout backup.
     */
    private fun canUseTransientHeadsUp(): Boolean {
        val powerManager = getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return false
        val keyguardManager = getSystemService(Context.KEYGUARD_SERVICE) as? KeyguardManager ?: return false
        return powerManager.isInteractive && !keyguardManager.isKeyguardLocked
    }

    override fun onDestroy() {
        shouldReconnect = false
        handler.removeCallbacks(reconnectRunnable)
        stopConnectionWatchdog()
        disconnectSocket()
        releaseWakeLock()
        transientHeadsUpController.destroy()
        overlayController.destroy()
        super.onDestroy()
    }

    /**
     * Attempt the next candidate host from the QR list. Used on a fresh connect and
     * whenever the active candidate fails before any host has connected. Once a host
     * verifies, [connectedHost] is set and later failures retry that host instead of
     * cycling through the list.
     */
    private fun connectNextCandidate() {
        if (candidateHosts.isEmpty()) {
            MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
            updateForeground("\u8bf7\u5148\u586b\u5199 PC \u5c40\u57df\u7f51 IP")
            return
        }
        if (candidateIndex >= candidateHosts.size) {
            candidateIndex = candidateHosts.lastIndex
        }
        serverHost = candidateHosts[candidateIndex]
        MessageRepository.updateServerHost(serverHost)
        connectSocket()
    }

    private fun connectSocket() {
        val host = serverHost.ifBlank { MessageRepository.currentServerHost }
        if (host.isBlank()) {
            MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
            updateForeground("\u8bf7\u5148\u586b\u5199 PC \u5c40\u57df\u7f51 IP")
            return
        }

        disconnectSocket()
        val generation = ++socketGeneration
        val candidateInfo = if (candidateHosts.size > 1) " (${candidateIndex + 1}/${candidateHosts.size})" else ""
        // \u5728\u521b\u5efa WebSocket \u524d\u9884\u8bbe\u65f6\u95f4\u6233\uff0c\u9632\u6b62 onOpen \u56de\u8c03\u5c1a\u672a\u89e6\u53d1\u65f6
        // onStart \u2192 isConnectionLikelyStale \u8bef\u5224\u8fde\u63a5\u9648\u65e7\u5bfc\u81f4\u91cd\u590d\u91cd\u8fde\u3002
        val now = System.currentTimeMillis()
        lastSocketOpenTimeMs = now
        lastReceiveTimeMs = now
        MessageRepository.setConnectionStatus(
            if (reconnectAttempt == 0) ConnectionStatus.Connecting else ConnectionStatus.Reconnecting
        )
        updateForeground("\u8fde\u63a5\u4e2d$candidateInfo\uff1a$host", host)

        Log.i(tag, "connect generation=$generation host=$host relayConfig=$relayConfig")

        val isRelayMode = relayConfig != null
        val wsUrl = if (isRelayMode) {
            relayConfig!!.url
        } else {
            ServerConfig(host).webSocketUrl
        }
        val wsClient = if (isRelayMode) relayClient else client

        // URL 非法（畸形 host/端口）时 OkHttp 会抛 IllegalArgumentException，
        // 不能让它冒泡到 Service 线程导致整个 App 闪退；降级为断开状态并重连。
        val request = try {
            Request.Builder().url(wsUrl).build()
        } catch (error: IllegalArgumentException) {
            Log.e(tag, "invalid ws url=$wsUrl, host=$host", error)
            MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
            updateForeground("\u8fde\u63a5\u5730\u5740\u65e0\u6548\uff1a$host", host)
            socket = null
            scheduleReconnect()
            return
        }

        socket = wsClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (!isCurrentSocket(webSocket, generation)) return
                val now = System.currentTimeMillis()
                Log.i(tag, "opened generation=$generation host=$host relay=${relayConfig != null}")
                lastSocketOpenTimeMs = now
                lastSendTimeMs = 0L
                lastReceiveTimeMs = now
                lastHeartbeatAckTimeMs = 0L
                MessageRepository.setConnectionStatus(
                    if (reconnectAttempt == 0) ConnectionStatus.Connecting else ConnectionStatus.Reconnecting
                )

                if (isRelayMode) {
                    // Relay handshake: send relay.hello before normal protocol
                    val rc = relayConfig!!
                    val helloPayload = JSONObject().apply {
                        put("type", "relay.hello")
                        put("role", "client")
                        put("roomId", rc.roomId)
                    }
                    webSocket.send(helloPayload.toString())
                    updateForeground("\u4e2d\u7ee7\u8fde\u63a5\u4e2d\uff0c\u623f\u95f4 ${rc.roomId} \u914d\u5bf9\u4e2d\u2026")
                    startConnectionWatchdog()
                } else {
                    updateForeground("\u5df2\u5efa\u7acb\u8fde\u63a5\uff0c\u7b49\u5f85\u5fc3\u8df3\u786e\u8ba4\uff1a$host", host)
                    startConnectionWatchdog()
                    sendHeartbeat(webSocket, now)
                    flushPendingApprovalControls(webSocket)
                    flushPendingPcPowerControls(webSocket)
                    flushPendingAgentCalls(webSocket)
                    syncRegisteredApps(host)
                    sendAgentProbe(webSocket)
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (!isCurrentSocket(webSocket, generation)) {
                    Log.i(tag, "ignored stale message generation=$generation")
                    return
                }
                val now = System.currentTimeMillis()
                lastReceiveTimeMs = now
                val payload = try {
                    JSONObject(text)
                } catch (error: Exception) {
                    Log.w(tag, "ignored invalid WebSocket message length=${text.length}", error)
                    return
                }
                when (payload.optString("type")) {
                    TYPE_HEARTBEAT_ACK -> {
                        lastHeartbeatAckTimeMs = now
                        markSocketVerified(host, "heartbeat_ack")
                        return
                    }

                    // Relay handshake acknowledgement
                    "relay.hello_ack" -> {
                        lastHeartbeatAckTimeMs = now
                        val hostOnline = payload.optBoolean("hostOnline", false)
                        if (hostOnline) {
                            markSocketVerified(host, "relay_ack")
                            // Now that the relay handshake is complete, send the usual heartbeat
                            sendHeartbeat(webSocket, now)
                            flushPendingApprovalControls(webSocket)
                            flushPendingPcPowerControls(webSocket)
                            flushPendingAgentCalls(webSocket)
                            syncRegisteredApps(host)
                            sendAgentProbe(webSocket)
                        } else {
                            updateForeground("\u623f\u95f4 ${relayConfig?.roomId ?: "?"} \u4e3b\u673a\u672a\u5728\u7ebf\uff0c\u7b49\u5f85\u4e2d\u2026")
                            MessageRepository.setConnectionStatus(ConnectionStatus.Connecting)
                        }
                        return
                    }

                    // Relay error: host gone, room full, etc.
                    "relay.error" -> {
                        val code = payload.optString("code", "")
                        val message = payload.optString("message", "")
                        Log.w(tag, "relay error code=$code message=$message")
                        if (code == "HOST_GONE") {
                            updateForeground("\u4e3b\u673a\u5df2\u65ad\u5f00\uff0c\u7b49\u5f85\u91cd\u8fde\u2026")
                            lastHeartbeatAckTimeMs = 0L  // Trigger reconnect check
                        }
                        return
                    }

                    "message" -> {
                        markSocketVerified(host, "message")
                        handleLegacyMessage(payload)
                    }

                    "event" -> {
                        markSocketVerified(host, "event")
                        handleEventPayload(payload)
                    }

                    "control_result" -> {
                        markSocketVerified(host, "control_result")
                        handleControlResult(payload)
                    }

                    // Agent API RPC responses
                    "agent.probe_result" -> {
                        markSocketVerified(host, "agent_probe")
                        handleAgentProbeResult(payload)
                    }

                    "agent.result" -> {
                        markSocketVerified(host, "agent_result")
                        handleAgentResult(payload)
                    }

                    else -> return
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (!isCurrentSocket(webSocket, generation)) {
                    Log.i(tag, "ignored stale close generation=$generation code=$code reason=$reason")
                    return
                }
                Log.w(tag, "closed generation=$generation code=$code reason=$reason")
                stopConnectionWatchdog()
                socket = null
                if (shouldReconnect) {
                    if (connectedHost.isBlank() && candidateIndex < candidateHosts.lastIndex) {
                        // Never connected yet: advance to the next candidate host.
                        candidateIndex++
                        Log.i(tag, "candidate ${candidateIndex - 1} failed, trying next host=${candidateHosts[candidateIndex]}")
                        showCandidateSwitchHint()
                        connectNextCandidate()
                    } else {
                        scheduleReconnect()
                    }
                } else {
                    MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
                    updateForeground("\u672a\u8fde\u63a5")
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (!isCurrentSocket(webSocket, generation)) {
                    Log.i(tag, "ignored stale failure generation=$generation error=${t.message}")
                    return
                }
                Log.w(tag, "failure generation=$generation code=${response?.code} error=${t.message}", t)
                stopConnectionWatchdog()
                socket = null
                if (shouldReconnect) {
                    if (connectedHost.isBlank() && candidateIndex < candidateHosts.lastIndex) {
                        // Never connected yet: advance to the next candidate host.
                        candidateIndex++
                        Log.i(tag, "candidate ${candidateIndex - 1} failed, trying next host=${candidateHosts[candidateIndex]}")
                        showCandidateSwitchHint()
                        connectNextCandidate()
                    } else {
                        scheduleReconnect()
                    }
                } else {
                    MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
                    updateForeground("\u672a\u8fde\u63a5")
                }
            }
        })
    }

    /**
     * 候选切换时给用户一个明确提示：不是连接失败，而是当前地址不可达、
     * 正在尝试下一个备选（如局域网不可达时切换到公网隧道）。仅在首次连接、
     * 尚未建立任何连接的候选切换时提示一次，避免重连阶段刷屏。
     */
    private fun showCandidateSwitchHint() {
        if (connectedHost.isNotBlank()) return
        // onFailure 跑在 OkHttp Dispatcher 线程，没有 Looper，直接 Toast 会
        // 抛 "Can't toast on a thread that has not called Looper.prepare()" 闪退。
        handler.post {
            Toast.makeText(this, "局域网地址不可达，正在尝试备选地址", Toast.LENGTH_SHORT).show()
        }
    }

    private fun markSocketVerified(host: String, reason: String) {
        val previousHost = connectedHost
        connectedHost = host
        val wasConnected = MessageRepository.state.value.connectionStatus == ConnectionStatus.Connected

        // 历史地址记录一次「本次连接用到了哪个地址」。
        // 必须放在下面的早退判断之前，否则「已连接状态下切到历史里的另一个地址」不会刷新使用时间，
        // 新地址也就不会冒到列表顶部。
        // 同时必须挡住心跳 ack：心跳会高频进入本函数，host 未变且已处于已连接态时不再重复记录，
        // 否则每个心跳都会写一次 SharedPreferences。
        if (!(wasConnected && previousHost == host)) {
            MessageRepository.recordHostUsage(host)
        }

        if (wasConnected) return

        reconnectAttempt = 0
        MessageRepository.setConnectionStatus(ConnectionStatus.Connected)
        MessageRepository.rememberSuccessfulHost(host)
        updateForeground("\u5df2\u8fde\u63a5\uff1a$host", host)
        Log.i(tag, "connection verified reason=$reason host=$host")
    }

    /** Fetch metadata after each connection; a failed sync keeps the last local registry. */
    private fun syncRegisteredApps(host: String) {
        val baseUrl = ServerConfig(host).httpBaseUrl
        val request = Request.Builder().url("$baseUrl/apps").get().build()
        client.newCall(request).enqueue(object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) {
                Log.w(tag, "app registry sync failed: ${e.message}")
            }

            override fun onResponse(call: Call, response: Response) {
                response.use { result ->
                    if (!result.isSuccessful) {
                        Log.w(tag, "app registry sync returned ${result.code}")
                        return
                    }

                    val root = runCatching { JSONObject(result.body.string()) }.getOrNull() ?: return
                    val array = root.optJSONArray("apps") ?: return
                    val apps = mutableListOf<RegisteredApp>()
                    for (index in 0 until array.length()) {
                        val item = array.optJSONObject(index) ?: continue
                        val key = item.optString("appKey").trim().lowercase()
                        val name = item.optString("displayName").trim()
                        val color = item.optString("brandColor").trim()
                        if (key.isBlank() || name.isBlank()) continue

                        val iconPath = item.optString("iconPath")
                        val iconBase64 = if (iconPath.startsWith("/apps/icon/")) {
                            downloadRegisteredIcon("$baseUrl$iconPath")
                        } else {
                            ""
                        }
                        apps += RegisteredApp(key, name, color, iconBase64)
                     }
                     MessageRepository.replaceRegisteredApps(apps)
                     handler.post {
                         // Rebuild existing notifications too. This matters when
                         // an old client receives refreshed app metadata while
                         // notifications are already visible.
                         NotificationHelper.syncTaskNotifications(
                             applicationContext,
                             MessageRepository.state.value.messages,
                             registeredApps = MessageRepository.state.value.registeredApps
                         )
                         showIsland()
                     }
                 }
             }
         })
    }

    private fun downloadRegisteredIcon(url: String): String {
        return runCatching {
            client.newCall(Request.Builder().url(url).get().build()).execute().use { response ->
                if (!response.isSuccessful) return ""
                val bytes = response.body.bytes()
                if (bytes.isEmpty() || bytes.size > 512 * 1024) return ""
                Base64.encodeToString(bytes, Base64.NO_WRAP)
            }
        }.getOrElse {
            Log.w(tag, "registered app icon download failed: ${it.message}")
            ""
        }
    }

    private fun startConnectionWatchdog() {
        stopConnectionWatchdog()
        handler.postDelayed(heartbeatRunnable, HEARTBEAT_INTERVAL_MS)
        handler.postDelayed(connectionMonitorRunnable, CONNECTION_MONITOR_INTERVAL_MS)
    }

    private fun stopConnectionWatchdog() {
        handler.removeCallbacks(heartbeatRunnable)
        handler.removeCallbacks(connectionMonitorRunnable)
    }

    private fun runHeartbeatTick() {
        val currentSocket = socket
        if (!shouldReconnect || currentSocket == null) {
            stopConnectionWatchdog()
            return
        }

        val now = System.currentTimeMillis()
        val heartbeatBaseTimeMs = if (lastHeartbeatAckTimeMs > 0L) {
            lastHeartbeatAckTimeMs
        } else {
            lastSocketOpenTimeMs
        }
        val heartbeatSilenceMs = if (heartbeatBaseTimeMs > 0L) now - heartbeatBaseTimeMs else 0L
        if (heartbeatSilenceMs > HEARTBEAT_TIMEOUT_MS) {
            failCurrentSocket(
                "heartbeat ack timeout=${heartbeatSilenceMs}ms " +
                    "lastReceive=${ageText(now, lastReceiveTimeMs)}"
            )
            return
        }

        if (!sendHeartbeat(currentSocket, now)) return
        handler.postDelayed(heartbeatRunnable, HEARTBEAT_INTERVAL_MS)
    }

    private fun sendHeartbeat(webSocket: WebSocket, now: Long): Boolean {
        val ok = webSocket.send(
            JSONObject()
                .put("type", TYPE_HEARTBEAT)
                .put("time", now)
                .toString()
        )
        lastSendTimeMs = now
        if (!ok) {
            failCurrentSocket("heartbeat send returned false")
        }
        return ok
    }

    private fun sendApprovalResolution(webSocket: WebSocket, control: ApprovalControl): Boolean {
        val ok = webSocket.send(
            JSONObject()
                .put("type", TYPE_CONTROL)
                .put("action", ACTION_APPROVAL_RESOLVE)
                .put("requestId", control.requestId)
                .put("decision", control.decision)
                .toString()
        )
        lastSendTimeMs = System.currentTimeMillis()
        if (!ok) {
            Log.w(tag, "approval control send returned false requestId=${control.requestId}")
            failCurrentSocket("approval control send returned false")
        }
        return ok
    }

    private fun flushPendingApprovalControls(webSocket: WebSocket) {
        while (true) {
            val control = pendingApprovalControls.poll() ?: return
            if (!sendApprovalResolution(webSocket, control)) {
                pendingApprovalControls.add(control)
                return
            }
            Log.i(tag, "sent queued approval control requestId=${control.requestId}")
        }
    }

    private fun sendPcPowerControl(webSocket: WebSocket, control: PcPowerControl): Boolean {
        val action = when (control.command) {
            PC_POWER_SHUTDOWN -> "pc.shutdown"
            PC_POWER_SLEEP -> "pc.sleep"
            // 旧命令仍可被本版本接收；发给新 PC 时统一请求睡眠。
            PC_POWER_SCREEN_OFF -> "pc.sleep"
            else -> return false
        }
        val ok = webSocket.send(
            JSONObject()
                .put("type", TYPE_CONTROL)
                .put("action", action)
                .toString()
        )
        lastSendTimeMs = System.currentTimeMillis()
        if (!ok) {
            Log.w(tag, "pc power control send returned false command=${control.command}")
            failCurrentSocket("pc power control send returned false")
        }
        return ok
    }

    private fun flushPendingPcPowerControls(webSocket: WebSocket) {
        while (true) {
            val control = pendingPcPowerControls.poll() ?: return
            if (!sendPcPowerControl(webSocket, control)) {
                pendingPcPowerControls.add(control)
                return
            }
            Log.i(tag, "sent queued pc power control command=${control.command}")
        }
    }

    private fun sendAgentCall(
        webSocket: WebSocket,
        agentKey: String,
        method: String,
        sessionId: String
    ): Boolean {
        val body = JSONObject()
            .put("type", TYPE_AGENT_CALL)
            .put("agentKey", agentKey)
            .put("method", method)
            .put("sessionId", sessionId)
        Log.i(
            tag,
            "send agent.call agentKey=$agentKey method=$method sessionId=$sessionId"
        )
        val ok = webSocket.send(body.toString())
        lastSendTimeMs = System.currentTimeMillis()
        if (!ok) {
            Log.w(tag, "agent call send returned false agentKey=$agentKey method=$method")
            failCurrentSocket("agent call send returned false")
        }
        return ok
    }

    /**
     * Batch capability discovery: an empty `agentKey` makes the PC probe every
     * registered agent and return one `agent.probe_result` with an `agents`
     * array. The phone stores the results so notification actions are generated
     * from the agent's real capabilities.
     */
    private fun sendAgentProbe(webSocket: WebSocket): Boolean {
        val ok = webSocket.send(
            JSONObject()
                .put("type", TYPE_AGENT_PROBE)
                .toString()
        )
        lastSendTimeMs = System.currentTimeMillis()
        if (!ok) {
            Log.w(tag, "agent probe send returned false")
            failCurrentSocket("agent probe send returned false")
        }
        return ok
    }

    /** Store single-agent or batch probe results in the capability cache. */
    private fun handleAgentProbeResult(payload: JSONObject) {
        val agents = payload.optJSONArray("agents")
        if (agents != null) {
            // Batch probe: {"agentKey":"*", "agents":[{agentKey, running, capabilities:[...], detail}]}
            val entries = mutableMapOf<String, Set<String>>()
            for (i in 0 until agents.length()) {
                val entry = agents.optJSONObject(i) ?: continue
                val key = entry.optString("agentKey").trim().lowercase()
                if (key.isBlank()) continue
                val caps = entry.optJSONArray("capabilities")
                val capSet = if (caps == null) {
                    emptySet()
                } else {
                    buildSet {
                        for (j in 0 until caps.length()) {
                            caps.optString(j).takeIf { it.isNotBlank() }?.let { add(it) }
                        }
                    }
                }
                entries[key] = capSet
            }
            AgentCapabilityCache.replace(entries)
            Log.i(tag, "agent.probe_result batch count=${entries.size}")
        } else {
            // Single probe: {agentKey, running, capabilities:[...]}
            val key = payload.optString("agentKey").trim().lowercase()
            if (key.isBlank()) return
            val caps = payload.optJSONArray("capabilities")
            val capSet = if (caps == null) {
                emptySet()
            } else {
                buildSet {
                    for (j in 0 until caps.length()) {
                        caps.optString(j).takeIf { it.isNotBlank() }?.let { add(it) }
                    }
                }
            }
            AgentCapabilityCache.put(key, capSet)
            Log.i(tag, "agent.probe_result single agentKey=$key capabilities=$capSet")
        }
    }

    /**
     * 处理 agent.call 的立即回执：以 Toast 形式提示，3 秒后自动消失。
     * 不再追加到会话卡片——已发送的指令原文由卡片上的气泡展示，回执只是瞬时反馈。
     */
    private fun handleAgentResult(payload: JSONObject) {
        val accepted = payload.optBoolean("accepted", false)
        val code = payload.optString("code", "")
        val status = payload.optString("status", "")
        val detail = payload.optString("detail", "")

        Log.i(
            tag,
            "agent.result agentKey=${payload.optString("agentKey")} " +
                "method=${payload.optString("method")} accepted=$accepted code=$code " +
                "status=$status detailLength=${detail.length}"
        )

        handler.post {
            val toastText = buildString {
                if (accepted) {
                    append(status.trim().ifBlank { "指令已发送" })
                } else {
                    append(status.trim().ifBlank { code.trim().ifBlank { "指令发送失败" } })
                }
                if (detail.isNotBlank()) {
                    append('\n').append(detail.trim().take(160))
                }
            }
            showTimedToast(toastText, AGENT_RESULT_TOAST_MS)
        }
    }

    /** 显示一个 [durationMs] 毫秒后自动消失的 Toast。可能在 OkHttp 回调线程调用，必须切主线程。 */
    private fun showTimedToast(message: String, durationMs: Long) {
        handler.post {
            val toast = Toast.makeText(applicationContext, message, Toast.LENGTH_LONG)
            toast.show()
            handler.postDelayed({ toast.cancel() }, durationMs)
        }
    }

    private fun flushPendingAgentCalls(webSocket: WebSocket) {
        while (true) {
            val call = pendingAgentCall.getAndSet(null) ?: return
            if (!sendAgentCall(webSocket, call.agentKey, call.method, call.sessionId)) {
                // If a newer call arrived while this send was in flight, preserve it.
                pendingAgentCall.compareAndSet(null, call)
                return
            }
            Log.i(tag, "sent queued agent call agentKey=${call.agentKey} method=${call.method}")
        }
    }

    private fun failCurrentSocket(reason: String) {
        val staleSocket = socket ?: return
        Log.w(tag, "connection unhealthy: $reason")
        stopConnectionWatchdog()
        socketGeneration++
        socket = null
        // \u4f18\u5148\u4f7f\u7528 close() \u53d1\u9001 close \u5e27\uff0c\u8ba9\u670d\u52a1\u7aef\u770b\u5230\u6b63\u5e38\u5173\u95ed\u800c\u975e\u5f02\u5e38\u65ad\u5f00\u3002
        // \u5982\u679c close() \u8fd4\u56de false\uff08\u5df2\u5173\u95ed\u6216\u65e0\u6cd5\u6392\u961f\uff09\uff0c\u56de\u9000\u5230 cancel() \u5f3a\u5236\u5173\u95ed\u3002
        if (!staleSocket.close(1000, "unhealthy")) {
            staleSocket.cancel()
        }
        if (shouldReconnect) {
            scheduleReconnect()
        } else {
            MessageRepository.setConnectionStatus(ConnectionStatus.Disconnected)
            updateForeground("\u672a\u8fde\u63a5")
        }
    }

    private fun logConnectionMonitor() {
        val now = System.currentTimeMillis()
        Log.i(
            tag,
            "monitor socketState=${if (socket != null) "OPEN" else "NONE"} " +
                "appStatus=${MessageRepository.state.value.connectionStatus.name} " +
                "lastSend=${ageText(now, lastSendTimeMs)} " +
                "lastReceive=${ageText(now, lastReceiveTimeMs)} " +
                "lastHeartbeatAck=${ageText(now, lastHeartbeatAckTimeMs)} " +
                "network=${currentNetworkLabel()}"
        )

        if (shouldReconnect && socket != null) {
            handler.postDelayed(connectionMonitorRunnable, CONNECTION_MONITOR_INTERVAL_MS)
        }
    }

    private fun ageText(now: Long, timestamp: Long): String {
        if (timestamp <= 0L) return "never"
        return "${(now - timestamp).coerceAtLeast(0L) / 1000}s ago"
    }

    private fun currentNetworkLabel(): String {
        val connectivityManager = getSystemService(ConnectivityManager::class.java) ?: return "unknown"
        val network = connectivityManager.activeNetwork ?: return "none"
        val capabilities = connectivityManager.getNetworkCapabilities(network) ?: return "unknown"
        return when {
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "WIFI"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "CELLULAR"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ETHERNET"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_BLUETOOTH) -> "BLUETOOTH"
            else -> "OTHER"
        }
    }

    private fun scheduleReconnect() {
        MessageRepository.setConnectionStatus(ConnectionStatus.Reconnecting)
        val delayMs = reconnectDelay(reconnectAttempt)
        reconnectAttempt = min(reconnectAttempt + 1, 5)
        updateForeground("\u91cd\u8fde\u4e2d\uff0c${delayMs / 1000} \u79d2\u540e\u91cd\u8bd5")
        Log.i(tag, "schedule reconnect delayMs=$delayMs attempt=$reconnectAttempt")
        handler.removeCallbacks(reconnectRunnable)
        handler.postDelayed(reconnectRunnable, delayMs)
    }

    private fun reconnectDelay(attempt: Int): Long {
        return when (attempt) {
            0 -> 1_000L
            1 -> 2_000L
            2 -> 4_000L
            3 -> 8_000L
            4 -> 16_000L
            else -> 30_000L
        }
    }

    private fun disconnectSocket() {
        handler.removeCallbacks(reconnectRunnable)
        stopConnectionWatchdog()
        socketGeneration++
        socket?.close(1000, "disconnect")
        socket = null
        lastSocketOpenTimeMs = 0L
        lastSendTimeMs = 0L
        lastReceiveTimeMs = 0L
        lastHeartbeatAckTimeMs = 0L
    }

    private fun isCurrentSocket(webSocket: WebSocket, generation: Int): Boolean {
        return generation == socketGeneration && socket === webSocket
    }

    /**
     * 更新前台通知。
     *
     * [host] 传本次文案里出现的连接地址（没有地址的文案传 null），
     * 由 [postForeground] 按「隐藏地址」开关决定是否脱敏。
     */
    private fun updateForeground(content: String, host: String? = null) {
        lastForegroundContent = content
        lastForegroundHost = host
        postForeground()
    }

    /**
     * 真正下发前台通知。
     *
     * 脱敏必须在**这里**做，而不是在调用处拼文案时：调用处拿不到最新开关，
     * 且开关变化后要重发（见 `onCreate` 里对 `hostMasked` 的收集）。
     * 锁屏通知比应用界面更显眼，「隐藏地址」开着却把 IP 留在锁屏上等于没隐藏。
     */
    private fun postForeground() {
        val content = if (MessageRepository.hostMasked.value) {
            maskHostInText(lastForegroundContent, lastForegroundHost)
        } else {
            lastForegroundContent
        }
        val notification = NotificationHelper.createServiceNotification(this, content)
        NotificationHelper.notifyService(this, notification)
    }

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return

        val powerManager = getSystemService(PowerManager::class.java)
        wakeLock = powerManager.newWakeLock(
            PowerManager.PARTIAL_WAKE_LOCK,
            "NotifyPc:WebSocketService"
        ).apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.let { lock ->
            if (lock.isHeld) {
                lock.release()
            }
        }
        wakeLock = null
    }

    companion object {
        const val ACTION_CONNECT = "com.lan.notify.service.ACTION_CONNECT"
        const val ACTION_DISCONNECT = "com.lan.notify.service.ACTION_DISCONNECT"
        const val ACTION_ENSURE_CONNECTED = "com.lan.notify.service.ACTION_ENSURE_CONNECTED"
        const val ACTION_RESOLVE_APPROVAL = "com.lan.notify.service.ACTION_RESOLVE_APPROVAL"
        const val ACTION_PC_POWER = "com.lan.notify.service.ACTION_PC_POWER"
        const val ACTION_AGENT_CALL = "com.lan.notify.service.ACTION_AGENT_CALL"
        const val ACTION_MODAL_OPEN = "com.lan.notify.service.ACTION_MODAL_OPEN"
        const val ACTION_MODAL_CLOSE = "com.lan.notify.service.ACTION_MODAL_CLOSE"
        const val EXTRA_SERVER_HOST = "extra_server_host"
        const val EXTRA_SERVER_HOSTS = "extra_server_hosts"
        const val EXTRA_RELAY_URL = "extra_relay_url"
        const val EXTRA_RELAY_ROOM_ID = "extra_relay_room_id"
        const val EXTRA_PC_POWER_COMMAND = "extra_pc_power_command"
        const val EXTRA_AGENT_KEY = "extra_agent_key"
        const val EXTRA_AGENT_METHOD = "extra_agent_method"
        const val EXTRA_AGENT_SESSION_ID = "extra_agent_session_id"
        const val PC_POWER_SLEEP = "sleep"
        /** Legacy command accepted for upgrades from versions that only turned off the display. */
        const val PC_POWER_SCREEN_OFF = "screen_off"
        const val PC_POWER_SHUTDOWN = "shutdown"
        const val EXTRA_APPROVAL_REQUEST_ID = "extra_approval_request_id"
        const val EXTRA_APPROVAL_DECISION = "extra_approval_decision"
        private const val TYPE_HEARTBEAT = "heartbeat"
        private const val TYPE_HEARTBEAT_ACK = "heartbeat_ack"
        private const val TYPE_CONTROL = "control"
        private const val TYPE_AGENT_CALL = "agent.call"
        private const val TYPE_AGENT_PROBE = "agent.probe"
        private const val METHOD_SESSION_STOP = "session.stop"
        private const val ACTION_APPROVAL_RESOLVE = "approval.resolve"
        private const val HEARTBEAT_INTERVAL_MS = 20_000L
        private const val HEARTBEAT_TIMEOUT_MS = 60_000L
        private const val CONNECTION_MONITOR_INTERVAL_MS = 30_000L

        // \u524d\u53f0\u6062\u590d\u65f6\u5224\u65ad\u8fde\u63a5\u662f\u5426\u9648\u65e7\u7684\u9608\u503c\uff1a
        // HANS \u53ef\u80fd\u9759\u9ed8\u5173\u95ed socket \u4e14\u4e0d\u89e6\u53d1 OkHttp \u56de\u8c03\uff0c
        // \u8d85\u8fc7\u6b64\u9608\u503c\u624d\u8ba4\u4e3a\u8fde\u63a5\u53ef\u80fd\u5df2\u5931\u6548\uff0c\u9700\u8981\u91cd\u8fde\u3002
        // \u5fc3\u8df3\u6bcf 20s \u4e00\u6b21\uff0c45s \u5141\u8bb8\u6700\u591a\u9519\u8fc7 2 \u6b21\u5fc3\u8df3\u5468\u671f\u3002
        private const val STALE_THRESHOLD_MS = 45_000L
        /** 同一点击/重组不得连跳两档。 */
        private const val OVERLAY_ADVANCE_DEBOUNCE_MS = 280L

        /** agent.result 回执 Toast 的展示时长：3 秒后自动消失。 */
        private const val AGENT_RESULT_TOAST_MS = 3_000L

        fun requestAgentStop(context: Context, message: NotifyMessage): Boolean {
            return enqueueAgentCall(context, message, METHOD_SESSION_STOP)
        }

        private fun enqueueAgentCall(
            context: Context,
            message: NotifyMessage,
            method: String
        ): Boolean {
            val agentKey = canonicalSourceKey(message.source).trim().lowercase()
            val sessionId = message.sessionId.trim()
            if (agentKey.isBlank() || sessionId.isBlank()) return false

            val intent = Intent(context, WebSocketService::class.java)
                .setAction(ACTION_AGENT_CALL)
                .putExtra(EXTRA_AGENT_KEY, agentKey)
                .putExtra(EXTRA_AGENT_METHOD, method)
                .putExtra(EXTRA_AGENT_SESSION_ID, sessionId)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                ContextCompat.startForegroundService(context, intent)
            } else {
                context.startService(intent)
            }
            return true
        }
    }

    private data class ApprovalControl(
        val requestId: String,
        val decision: String
    )

    private data class PcPowerControl(
        val command: String
    )

    private data class AgentCall(
        val agentKey: String,
        val method: String,
        val sessionId: String
    )
}
