package com.lan.notify

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.app.AlertDialog
import android.app.Dialog
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.text.InputType
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.lan.notify.data.ConnectionQrCodec
import com.lan.notify.data.MessageRepository
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RelayConfig
import com.lan.notify.data.projectTitle
import com.lan.notify.data.safeActionUri
import com.lan.notify.data.taskUsageLabel
import com.lan.notify.notification.NotificationHelper
import com.lan.notify.service.WebSocketService
import com.lan.notify.ui.MainScreen
import com.lan.notify.ui.PermissionCheckScreen
import com.lan.notify.ui.theme.NotifyTheme
import com.lan.notify.util.AutoStartHelper

class MainActivity : ComponentActivity() {
    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (!granted) {
                Toast.makeText(this, "未授权通知权限，任务通知将无法显示", Toast.LENGTH_LONG).show()
            }
        }

    private val cameraPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) {
                launchQrScanner()
            } else {
                Toast.makeText(this, "需要相机权限才能扫描 PC 二维码", Toast.LENGTH_LONG).show()
            }
        }

    private val qrScanLauncher =
        registerForActivityResult(ScanContract()) { result ->
            val raw = result.contents
            if (raw.isNullOrBlank()) {
                return@registerForActivityResult
            }

            // Check for relay metadata first
            val relay = ConnectionQrCodec.extractRelay(raw)
            if (relay != null) {
                // Relay mode: connect through relay server with room ID
                MessageRepository.updateServerHost(relay.url)
                startSocketServiceWithRelay(WebSocketService.ACTION_CONNECT, relay)
                Toast.makeText(this, "已读取中继房间 ${relay.roomId}，正在连接", Toast.LENGTH_SHORT).show()
                return@registerForActivityResult
            }

            val endpoints = ConnectionQrCodec.decodeList(raw)
            if (endpoints.isEmpty()) {
                Toast.makeText(this, "二维码不是有效的 TapRelay 连接地址", Toast.LENGTH_LONG).show()
                return@registerForActivityResult
            }

            MessageRepository.updateServerHost(endpoints.first().url)
            startSocketServiceWithHosts(WebSocketService.ACTION_CONNECT, endpoints.map { it.url })
            val hint = if (endpoints.size > 1) "（含 ${endpoints.size} 个候选地址，自动择优）" else ""
            Toast.makeText(this, "已读取 ${endpoints.first().url}，正在连接$hint", Toast.LENGTH_SHORT).show()
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        handleMessageIntent(intent)
        handleContinueIntent(intent)
        setContent {
            val themeMode by MessageRepository.themeMode.collectAsState()
            val listMode by MessageRepository.listMode.collectAsState()
            val projectExpandCounts by MessageRepository.projectExpandCounts.collectAsState()
            val dynamicEffectsEnabled by MessageRepository.dynamicEffectsEnabled.collectAsState()
            val deviceName by MessageRepository.deviceName.collectAsState()
            // 首次启动自动进入权限向导（onboarding 模式），用户完成/跳过后才进主界面。
            var showPermissionCheck by remember { mutableStateOf(!MessageRepository.isOnboardingDone) }
            NotifyTheme(themeMode = themeMode) {
                val uiState by MessageRepository.state.collectAsState()
                BackHandler(enabled = showPermissionCheck) {
                    showPermissionCheck = false
                    MessageRepository.completeOnboarding()
                }
                if (showPermissionCheck) {
                    PermissionCheckScreen(
                        onboarding = !MessageRepository.isOnboardingDone,
                        onBack = {
                            showPermissionCheck = false
                            MessageRepository.completeOnboarding()
                        },
                        onOpenAutoStartSettings = ::openAutoStartSettings
                    )
                } else {
                    MainScreen(
                        uiState = uiState,
                        themeMode = themeMode,
                        onThemeModeChange = MessageRepository::setThemeMode,
                        listMode = listMode,
                        onListModeChange = MessageRepository::setListMode,
                        projectExpandCounts = projectExpandCounts,
                        onSetProjectExpandCount = MessageRepository::setProjectExpandCount,
                        onServerHostChange = MessageRepository::updateServerHost,
                        onScanQr = ::scanConnectionQr,
                        onConnect = { startSocketService(WebSocketService.ACTION_CONNECT) },
                        onDisconnect = { startSocketService(WebSocketService.ACTION_DISCONNECT) },
                        onSleep = { sendPcPowerCommand(WebSocketService.PC_POWER_SLEEP) },
                        onShutdown = ::confirmPcShutdown,
                        onOpenPermissionCheck = { showPermissionCheck = true },
                        onMarkMessageRead = ::markMessageRead,
                        onMarkAllMessagesRead = ::markAllMessagesRead,
                        onSendToAgent = ::sendToAgent,
                        onResolveApproval = ::resolveApprovalFromMessage,
                        onProcessWaiting = ::processWaitingMessage,
                        dynamicEffectsEnabled = dynamicEffectsEnabled,
                        onDynamicEffectsChange = MessageRepository::setDynamicEffectsEnabled,
                        deviceName = deviceName,
                        onDeviceNameChange = MessageRepository::setDeviceName,
                        onClearHistory = {
                            NotificationHelper.clearTaskNotifications(
                                this,
                                MessageRepository.state.value.messages
                            )
                            MessageRepository.clearMessages()
                        },
                        onContinueDialogVisibilityChanged = ::setOverlaySuppressedForModal
                    )
                }
            }
        }
        requestNotificationPermissionIfNeeded()
    }

    /** Android 13+ blocks every notification (including the foreground service card) until granted. */
    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED
        ) return
        notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    override fun onStart() {
        super.onStart()
        // ColorOS HANS 可能在应用退到后台后直接按 UID 切断 socket，且不触发
        // OkHttp 的 onClosed/onFailure。回到前台时检查连接是否陈旧，仅在可能
        // 失效时才重建，避免对健康连接的不必要断开。
        if (MessageRepository.hasKnownGoodHost) {
            startSocketService(WebSocketService.ACTION_ENSURE_CONNECTED)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleMessageIntent(intent)
        handleContinueIntent(intent)
    }

    private fun handleMessageIntent(intent: Intent?) {
        val messageId = intent?.getStringExtra(EXTRA_MESSAGE_ID)
        if (!messageId.isNullOrBlank()) {
            markMessageRead(messageId)
        }

        val actionUrl = intent?.getStringExtra(EXTRA_MESSAGE_ACTION_URL).orEmpty()
        safeActionUri(actionUrl)?.let { uri ->
            runCatching {
                startActivity(Intent(Intent.ACTION_VIEW, uri))
            }
        }
        intent?.removeExtra(EXTRA_MESSAGE_ID)
        intent?.removeExtra(EXTRA_MESSAGE_ACTION_URL)
    }

    private fun markMessageRead(messageId: String) {
        MessageRepository.markMessageRead(messageId)
        syncTaskNotifications()
    }

    private fun markAllMessagesRead() {
        MessageRepository.markAllMessagesRead()
        syncTaskNotifications()
    }

    /** 在手机上给 agent 会话发送下一条指令（继续 vibe coding）。 */
    private fun sendToAgent(message: NotifyMessage, text: String) {
        if (!MessageRepository.hasKnownGoodHost) {
            Toast.makeText(this, "请先连接 PC 后再操作", Toast.LENGTH_SHORT).show()
            return
        }
        val accepted = WebSocketService.requestAgentSend(this, message, text)
        if (accepted) {
            Toast.makeText(this, "已发送指令", Toast.LENGTH_SHORT).show()
        }
    }

    /** 在手机上直接处理等待确认（WorkBuddy 权限/批量删除确认等）。 */
    private fun resolveApprovalFromMessage(message: NotifyMessage, decision: String) {
        if (message.requestId.isBlank()) return
        if (!MessageRepository.hasKnownGoodHost) {
            Toast.makeText(this, "请先连接 PC 后再操作", Toast.LENGTH_SHORT).show()
            return
        }
        val controlIntent = Intent(this, WebSocketService::class.java)
            .setAction(WebSocketService.ACTION_RESOLVE_APPROVAL)
            .putExtra(WebSocketService.EXTRA_APPROVAL_REQUEST_ID, message.requestId)
            .putExtra(WebSocketService.EXTRA_APPROVAL_DECISION, decision)
        ContextCompat.startForegroundService(this, controlIntent)
        Toast.makeText(this, if (decision == "allow") "已发送允许" else "已发送拒绝", Toast.LENGTH_SHORT).show()
    }

    /** 普通 waiting 没有远程 approval.resolve 通道，明确引导用户处理而不是伪造审批。 */
    private fun processWaitingMessage(message: NotifyMessage) {
        safeActionUri(message.actionUrl)?.let { uri ->
            runCatching { startActivity(Intent(Intent.ACTION_VIEW, uri)) }
        } ?: Toast.makeText(this, "请在电脑端处理此任务", Toast.LENGTH_LONG).show()
        markMessageRead(message.id)
    }

    /**
     * 通知卡上的「继续」按钮：拉起本 Activity 后弹输入框，
     * 发送时复用与主界面历史一致的 [sendToAgent] 链路。
     */
    private fun handleContinueIntent(intent: Intent?) {
        val messageId = intent?.getStringExtra(EXTRA_CONTINUE_MESSAGE_ID) ?: return
        intent.removeExtra(EXTRA_CONTINUE_MESSAGE_ID)
        val message = MessageRepository.state.value.messages
            .firstOrNull { it.id == messageId } ?: return
        showContinueDialog(message)
    }

    private fun showContinueDialog(message: NotifyMessage) {
        setOverlaySuppressedForModal(true)
        val density = resources.displayMetrics.density
        fun dp(value: Int): Int = (value * density + 0.5f).toInt()
        fun textView(text: String, sizeSp: Float, color: Int): TextView = TextView(this).apply {
            this.text = text
            textSize = sizeSp
            setTextColor(color)
        }
        fun actionButton(text: String, primary: Boolean): TextView = textView(
            text,
            14f,
            if (primary) Color.WHITE else Color.rgb(83, 105, 98)
        ).apply {
            gravity = Gravity.CENTER
            minHeight = dp(42)
            setPadding(dp(18), 0, dp(18), 0)
            background = GradientDrawable().apply {
                cornerRadius = dp(10).toFloat()
                setColor(if (primary) Color.rgb(111, 134, 126) else Color.TRANSPARENT)
                if (!primary) setStroke(dp(1), Color.rgb(111, 134, 126))
            }
        }

        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(18), dp(20), dp(18))
            background = GradientDrawable().apply {
                cornerRadius = dp(16).toFloat()
                setColor(Color.WHITE)
            }
        }
        panel.addView(
            textView("继续会话", 20f, Color.rgb(15, 23, 42)),
            LinearLayout.LayoutParams(-1, -2)
        )
        panel.addView(
            textView(message.projectTitle("此会话"), 14f, Color.rgb(71, 85, 105)).apply {
                maxLines = 1
                ellipsize = android.text.TextUtils.TruncateAt.END
            },
            LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(6) }
        )
        panel.addView(
            textView(message.taskUsageLabel(), 11f, Color.rgb(100, 116, 139)),
            LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4) }
        )

        val input = EditText(this).apply {
            hint = "输入给 agent 的下一条指令"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE
            gravity = Gravity.TOP or Gravity.START
            minLines = 3
            maxLines = 10
            setHorizontallyScrolling(false)
            setPadding(dp(12), dp(10), dp(12), dp(10))
            backgroundTintList = ColorStateList.valueOf(Color.rgb(183, 200, 192))
        }
        panel.addView(
            input,
            LinearLayout.LayoutParams(-1, dp(116)).apply { topMargin = dp(12) }
        )

        var dialog: Dialog? = null
        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
        }
        val cancel = actionButton("取消", primary = false).apply {
            setOnClickListener { dialog?.dismiss() }
        }
        val send = actionButton("发送", primary = true).apply {
            setOnClickListener {
                input.text?.toString()?.trim()?.takeIf { it.isNotEmpty() }?.let { value ->
                    sendToAgent(message, value)
                    dialog?.dismiss()
                }
            }
        }
        actions.addView(cancel, LinearLayout.LayoutParams(0, dp(42)).apply { weight = 1f })
        actions.addView(send, LinearLayout.LayoutParams(0, dp(42)).apply {
            weight = 1f
            leftMargin = dp(8)
        })
        panel.addView(actions, LinearLayout.LayoutParams(-1, dp(42)).apply { topMargin = dp(14) })

        dialog = Dialog(this).apply {
            setContentView(panel)
            setCanceledOnTouchOutside(true)
            setOnDismissListener { setOverlaySuppressedForModal(false) }
            setOnShowListener {
                window?.apply {
                    setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
                    setLayout(dp(360), WindowManager.LayoutParams.WRAP_CONTENT)
                    setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
                }
            }
        }
        dialog?.show()
        dialog?.window?.apply {
                setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
                setLayout(dp(360), WindowManager.LayoutParams.WRAP_CONTENT)
                setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
    }

    /** Keep application dialogs above the app-owned TYPE_APPLICATION_OVERLAY cards. */
    private fun setOverlaySuppressedForModal(suppressed: Boolean) {
        val intent = Intent(this, WebSocketService::class.java)
            .setAction(
                if (suppressed) {
                    WebSocketService.ACTION_MODAL_OPEN
                } else {
                    WebSocketService.ACTION_MODAL_CLOSE
                }
            )
        ContextCompat.startForegroundService(this, intent)
    }

    private fun syncTaskNotifications() {
        NotificationHelper.syncTaskNotifications(
            this,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
    }

    private fun scanConnectionQr() {
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED
        if (granted) {
            launchQrScanner()
        } else {
            cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    private fun launchQrScanner() {
        val options = ScanOptions().apply {
            setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            setPrompt("扫描 PC 端 TapRelay 二维码")
            setBeepEnabled(true)
            setOrientationLocked(false)
            // 使用自定义取景框（280dp，比二维码稍大），避免默认窗口过大。
            setCaptureActivity(CustomCaptureActivity::class.java)
        }
        qrScanLauncher.launch(options)
    }

    private fun startSocketService(action: String, configure: Intent.() -> Unit = {}) {
        val intent = Intent(this, WebSocketService::class.java).apply {
            this.action = action
            putExtra(WebSocketService.EXTRA_SERVER_HOST, MessageRepository.currentServerHost)
            configure()
        }
        ContextCompat.startForegroundService(this, intent)
    }

    /** Like [startSocketService] but hands the service an ordered list of
     *  candidate hosts so it can fail over to the next one if the first is unreachable. */
    private fun startSocketServiceWithHosts(action: String, hosts: List<String>) {
        val intent = Intent(this, WebSocketService::class.java).apply {
            this.action = action
            putStringArrayListExtra(WebSocketService.EXTRA_SERVER_HOSTS, ArrayList(hosts))
            putExtra(WebSocketService.EXTRA_SERVER_HOST, MessageRepository.currentServerHost)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    /** Connect through a relay server with a shared room ID. */
    private fun startSocketServiceWithRelay(action: String, relay: RelayConfig) {
        val intent = Intent(this, WebSocketService::class.java).apply {
            this.action = action
            putExtra(WebSocketService.EXTRA_RELAY_URL, relay.url)
            putExtra(WebSocketService.EXTRA_RELAY_ROOM_ID, relay.roomId)
            putExtra(WebSocketService.EXTRA_SERVER_HOST, relay.url)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    /** 向 PC 下发电源控制指令（睡眠 / 关机），由 WebSocketService 走 control 链路发送。 */
    private fun sendPcPowerCommand(command: String) {
        if (MessageRepository.hasKnownGoodHost.not()) {
            Toast.makeText(this, "请先连接 PC 后再操作", Toast.LENGTH_SHORT).show()
            return
        }
        startSocketService(WebSocketService.ACTION_PC_POWER) {
            putExtra(WebSocketService.EXTRA_PC_POWER_COMMAND, command)
        }
        Toast.makeText(
            this,
            if (command == WebSocketService.PC_POWER_SHUTDOWN) "\u6b63\u5728\u8bf7\u6c42\u7535\u8111\u5173\u673a" else "\u6b63\u5728\u8bf7\u6c42\u7535\u8111\u8fdb\u5165\u7761\u7720",
            Toast.LENGTH_SHORT
        ).show()
    }

    /** 关机是不可逆操作，先弹确认框再下发。 */
    private fun confirmPcShutdown() {
        AlertDialog.Builder(this)
            .setTitle("\u5173\u673a\u786e\u8ba4")
            .setMessage("\u786e\u5b9a\u8981\u5173\u95ed\u7535\u8111\u5417\uff1f\u6267\u884c\u540e\u7535\u8111\u5c06\u5728\u7ea6 10 \u79d2\u540e\u5173\u673a\u3002")
            .setPositiveButton("\u5173\u673a") { _, _ -> sendPcPowerCommand(WebSocketService.PC_POWER_SHUTDOWN) }
            .setNegativeButton("\u53d6\u6d88", null)
            .show()
    }

    private fun openBatterySettings() {        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val powerManager = getSystemService(PowerManager::class.java)
            if (!powerManager.isIgnoringBatteryOptimizations(packageName)) {
                val requestIntent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                    data = Uri.parse("package:$packageName")
                }
                runCatching {
                    startActivity(requestIntent)
                }.onSuccess { return }
            }
        }

        val appSettingsIntent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
            data = Uri.parse("package:$packageName")
        }
        startActivity(appSettingsIntent)
    }

    /** 打开"显示在其他应用上层"授权页；有了该权限消息才以悬浮卡片逐条独立展示。 */
    private fun openOverlaySettings() {
        val overlayIntent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION).apply {
            data = Uri.parse("package:$packageName")
        }
        runCatching { startActivity(overlayIntent) }.onFailure {
            startActivity(
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                    data = Uri.parse("package:$packageName")
                }
            )
        }
    }

    /**
     * 打开厂商自启动权限管理页面。
     *
     * Android 原生没有标准的自启动权限 API，各厂商在定制系统中各自实现。
     * AutoStartHelper 按厂商逐个尝试已知 Intent，全部失败时回退到应用详情页。
     */
    private fun openAutoStartSettings() {
        AutoStartHelper.openAutoStartSettings(this)
    }

    /**
     * 打开系统无障碍服务设置页面。
     *
     * Android 标准 API 只能跳到无障碍服务列表页（ACTION_ACCESSIBILITY_SETTINGS），
     * 用户需要在列表里找到 TapRelay 并手动开启 KeepAliveAccessibilityService。
     * 该服务仅用于进程保活，不读取任何窗口内容（详见
     * 09-问题与优化记录-内容.md P016/P028/P029）。
     */
    private fun openAccessibilitySettings() {
        val intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching { startActivity(intent) }.onFailure {
            startActivity(
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                    data = Uri.parse("package:$packageName")
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
            )
        }
    }

    companion object {
        const val EXTRA_MESSAGE_ID = "extra_message_id"
        const val EXTRA_MESSAGE_ACTION_URL = "extra_message_action_url"
        const val EXTRA_CONTINUE_MESSAGE_ID = "extra_continue_message_id"
    }
}
