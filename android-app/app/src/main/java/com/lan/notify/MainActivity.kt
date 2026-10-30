package com.lan.notify

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.app.AlertDialog
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.lan.notify.data.ConnectionQrCodec
import com.lan.notify.data.MessageRepository
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RelayConfig
import com.lan.notify.data.UpdateChecker
import com.lan.notify.data.UpdatePromptStore
import com.lan.notify.data.UpdateRelease
import com.lan.notify.data.safeActionUri
import com.lan.notify.notification.NotificationHelper
import com.lan.notify.service.WebSocketService
import com.lan.notify.ui.MainScreen
import com.lan.notify.ui.ComfyUiScreen
import com.lan.notify.ui.AppDestination
import com.lan.notify.ui.DeviceScreen
import com.lan.notify.ui.PermissionCheckScreen
import com.lan.notify.ui.UpdateDialog
import com.lan.notify.ui.WorkstationShell
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
        // 主页面与顶部/底部系统栏使用同一层背景，避免真机上出现明显的白色空带。
        // Compose 各页面的 TopAppBar / NavigationBar 会自行消费安全区 inset，
        // 内容仍不会被状态栏和手势条遮挡。
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            window.isNavigationBarContrastEnforced = false
        }
        handleMessageIntent(intent)
        setContent {
            val themeMode by MessageRepository.themeMode.collectAsState()
            val listMode by MessageRepository.listMode.collectAsState()
            val projectExpandCounts by MessageRepository.projectExpandCounts.collectAsState()
            val dynamicEffectsEnabled by MessageRepository.dynamicEffectsEnabled.collectAsState()
            val deviceName by MessageRepository.deviceName.collectAsState()
            val hostHistory by MessageRepository.hostHistory.collectAsState()
            val hostMasked by MessageRepository.hostMasked.collectAsState()
            val updatePromptStore = remember { UpdatePromptStore(this@MainActivity) }
            var updateRelease by remember { mutableStateOf<UpdateRelease?>(null) }
            LaunchedEffect(Unit) {
                val release = UpdateChecker.fetchLatest()
                if (release != null && updatePromptStore.shouldPrompt(release.version)) {
                    updateRelease = release
                }
            }
            // 首次启动自动进入权限向导（onboarding 模式），用户完成/跳过后才进主界面。
            var showPermissionCheck by remember { mutableStateOf(!MessageRepository.isOnboardingDone) }
            var activeDestination by rememberSaveable { mutableStateOf(AppDestination.TASK) }
            NotifyTheme(themeMode = themeMode) {
                val uiState by MessageRepository.state.collectAsState()
                BackHandler(enabled = showPermissionCheck) {
                    showPermissionCheck = false
                    MessageRepository.completeOnboarding()
                }
                BackHandler(enabled = !showPermissionCheck && activeDestination != AppDestination.TASK) {
                    activeDestination = AppDestination.TASK
                }
                Box(Modifier.fillMaxSize()) {
                    WorkstationShell(
                        active = activeDestination,
                        onSelect = { activeDestination = it },
                        task = {
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
                                onResolveApproval = ::resolveApprovalFromMessage,
                                onProcessWaiting = ::processWaitingMessage,
                                dynamicEffectsEnabled = dynamicEffectsEnabled,
                                onDynamicEffectsChange = MessageRepository::setDynamicEffectsEnabled,
                                deviceName = deviceName,
                                onDeviceNameChange = MessageRepository::setDeviceName,
                                hostHistory = hostHistory,
                                hostMasked = hostMasked,
                                onHostMaskedChange = MessageRepository::setHostMasked,
                                onClearHostHistory = MessageRepository::clearHostHistory,
                                onOpenDeviceManagement = {
                                    activeDestination = AppDestination.DEVICE
                                },
                                onClearHistory = {
                                    NotificationHelper.clearTaskNotifications(
                                        this@MainActivity,
                                        MessageRepository.state.value.messages
                                    )
                                    MessageRepository.clearMessages()
                                },
                                onOpenComfyUi = {
                                    activeDestination = AppDestination.COMFY_UI
                                },
                            )
                        },
                        comfyUi = {
                            ComfyUiScreen(
                                serverHost = uiState.serverHost,
                                onBack = { activeDestination = AppDestination.TASK },
                                embedded = true
                            )
                        },
                        device = {
                            DeviceScreen(
                                uiState = uiState,
                                deviceName = deviceName,
                                onDeviceNameChange = MessageRepository::setDeviceName,
                                onServerHostChange = MessageRepository::updateServerHost,
                                dynamicEffectsEnabled = dynamicEffectsEnabled,
                                onDynamicEffectsChange = MessageRepository::setDynamicEffectsEnabled,
                                onScanQr = ::scanConnectionQr,
                                onConnect = { startSocketService(WebSocketService.ACTION_CONNECT) },
                                onDisconnect = { startSocketService(WebSocketService.ACTION_DISCONNECT) },
                                onSleep = { sendPcPowerCommand(WebSocketService.PC_POWER_SLEEP) },
                                onShutdown = ::confirmPcShutdown,
                                onOpenPermissionCheck = { showPermissionCheck = true },
                                hostHistory = hostHistory,
                                hostMasked = hostMasked,
                                onHostMaskedChange = MessageRepository::setHostMasked,
                                onClearHostHistory = MessageRepository::clearHostHistory
                            )
                        }
                    )
                    if (showPermissionCheck) {
                        PermissionCheckScreen(
                            onboarding = !MessageRepository.isOnboardingDone,
                            onBack = {
                                showPermissionCheck = false
                                MessageRepository.completeOnboarding()
                            },
                            onOpenAutoStartSettings = ::openAutoStartSettings
                        )
                    }
                    updateRelease?.let { release ->
                        UpdateDialog(
                            release = release,
                            currentVersion = UpdateChecker.currentVersion,
                            onOpenUpdate = {
                                updateRelease = null
                                openExternalUrl(release.targetUrl)
                            },
                            onSkipOnce = {
                                updatePromptStore.skipVersion(release.version)
                                updateRelease = null
                            },
                            onNeverPrompt = {
                                updatePromptStore.neverPrompt()
                                updateRelease = null
                            },
                            onDismiss = { updateRelease = null }
                        )
                    }
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

    private fun openExternalUrl(rawUrl: String) {
        val uri = runCatching { Uri.parse(rawUrl) }.getOrNull() ?: return
        if (uri.scheme != "http" && uri.scheme != "https") return
        runCatching {
            startActivity(Intent(Intent.ACTION_VIEW, uri))
        }.onFailure {
            Toast.makeText(this, "无法打开更新链接", Toast.LENGTH_SHORT).show()
        }
    }

    private fun markMessageRead(messageId: String) {
        MessageRepository.markMessageRead(messageId)
        syncTaskNotifications()
    }

    private fun markAllMessagesRead() {
        MessageRepository.markAllMessagesRead()
        syncTaskNotifications()
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
    }
}
