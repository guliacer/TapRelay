package com.lan.notify.notification

import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.MainActivity
import com.lan.notify.data.Channel
import com.lan.notify.data.MessageRepository
import com.lan.notify.data.MessageTimeFormatter
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TaskState
import com.lan.notify.data.canSendMessage
import com.lan.notify.data.displayContent
import com.lan.notify.data.hasApprovalAction
import com.lan.notify.data.isWaitingTask
import com.lan.notify.data.projectTitle
import com.lan.notify.data.taskUsageLabel
import com.lan.notify.service.WebSocketService
import com.lan.notify.ui.AppIcon
import com.lan.notify.ui.appPresentation
import com.lan.notify.ui.PlainButton
import com.lan.notify.ui.PlainButtonVariant
import com.lan.notify.ui.theme.NotifyTheme
import com.lan.notify.ui.theme.appColors

/**
 * 全屏提醒 Activity：由「发通知同帧的显式 startActivity」在锁屏/熄屏时被直接拉起，
 * 是「确保用户一定能看到通知」的最后一道防线。
 *
 * 不依赖 USE_FULL_SCREEN_INTENT：App 持 SYSTEM_ALERT_WINDOW 豁免后台启动限制，
 * 锁屏/熄屏由 showWhenLocked/turnScreenOn 处理。
 *
 * 触发条件：
 * - 屏幕熄灭 → 点亮屏幕并拉起本 Activity
 * - 设备锁屏 → 在锁屏之上拉起本 Activity
 * - 解锁且亮屏 → 由悬浮卡片或通知栏接管
 *
 * 全屏视频播放场景下，前台视频应用调用了 `setHideOverlayWindows(true)`，
 * 悬浮卡片被系统隐藏，heads-up 通知也常被沉浸式应用抑制；此时本 Activity 是
 * 唯一可被用户立即看到的视觉提醒路径。
 *
 * Activity 拉起后：
 * 1. 30 秒内用户无操作则自动 finish，避免长时间遮挡屏幕；
 * 2. 「查看」和「关闭」会把对应消息标记为已读并同步通知栏。
 * 3. 「查看」按钮额外跳转到 MainActivity，把消息 id 通过 EXTRA_MESSAGE_ID 传过去。
 */
class FullScreenAlertActivity : ComponentActivity() {

    private val handler = Handler(Looper.getMainLooper())
    private val autoDismissRunnable = Runnable { finish() }
    private var messageHandled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // API 27+ 用 setShowWhenLocked / setTurnScreenOn；API 26 退回 window flag。
        // 必须在 setContentView 之前调用，否则首次启动时窗口属性可能不生效。
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        } else {
            @Suppress("DEPRECATION")
            window.addFlags(
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or
                    WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
            )
        }
        // 保持屏幕常亮直到用户处理，避免刚点亮就熄灭看不清。
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        MessageRepository.initialize(applicationContext)

        val messageId = intent?.getStringExtra(EXTRA_MESSAGE_ID)
        val message = messageId?.let { id ->
            MessageRepository.state.value.messages.find { it.id == id }
        }
        if (message == null) {
            finish()
            return
        }

        // 收到消息后过 30 秒自动消失。本 Activity 由系统从锁屏拉起时，
        // 用户可能没在看屏幕（比如手机在桌上），30 秒足够看到/听到提醒，
        // 但不至于长时间遮挡锁屏。返回桌面/解锁会通过 noHistory 自动 finish。
        handler.postDelayed(autoDismissRunnable, AUTO_DISMISS_MS)

        setContent {
            val themeMode by MessageRepository.themeMode.collectAsState()
            val state by MessageRepository.state.collectAsState()
            NotifyTheme(themeMode = themeMode) {
                FullScreenAlertContent(
                    message = message,
                    registeredApps = state.registeredApps,
                    onView = { handleView(message) },
                    onDismiss = { handleDismiss(message) },
                    onResolveApproval = { decision -> handleResolveApproval(message, decision) },
                    onProcessWaiting = { handleView(message) },
                    onContinue = { handleContinue(message) }
                )
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        handler.removeCallbacks(autoDismissRunnable)
    }

    /**
     * 「查看」：标记已读 + 同步通知栏 + 跳转主界面。
     * 跳转后再 finish，让 MainActivity 接管显示。
     */
    private fun handleView(message: NotifyMessage) {
        if (messageHandled) return
        messageHandled = true
        markReadAndSync(message.id)
        startActivity(
            Intent(this, MainActivity::class.java).apply {
                action = Intent.ACTION_MAIN
                addCategory(Intent.CATEGORY_LAUNCHER)
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
                putExtra(MainActivity.EXTRA_MESSAGE_ID, message.id)
                putExtra(MainActivity.EXTRA_MESSAGE_ACTION_URL, message.actionUrl)
            }
        )
        finish()
    }

    /** Open the main Activity and let its shared continue dialog collect input. */
    private fun handleContinue(message: NotifyMessage) {
        if (messageHandled) return
        messageHandled = true
        startActivity(
            Intent(this, MainActivity::class.java).apply {
                action = Intent.ACTION_MAIN
                addCategory(Intent.CATEGORY_LAUNCHER)
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
                putExtra(MainActivity.EXTRA_CONTINUE_MESSAGE_ID, message.id)
            }
        )
        finish()
    }

    /**
     * 「关闭」：仅标记已读 + 同步通知栏。
     * 不跳转主界面，用户可继续看视频或锁屏。
     */
    private fun handleDismiss(message: NotifyMessage) {
        if (messageHandled) return
        messageHandled = true
        markReadAndSync(message.id)
        finish()
    }

    /**
     * 「允许/拒绝」：直接把审批结果发回 PC 处理 WorkBuddy 等待，无需打开 App。
     * 发完后本地标记已读 + 同步通知栏，避免重复提醒。
     */
    private fun handleResolveApproval(message: NotifyMessage, decision: String) {
        if (messageHandled) return
        messageHandled = true
        val requestId = message.requestId
        if (requestId.isNotBlank()) {
            val controlIntent = Intent(this, WebSocketService::class.java)
                .setAction(WebSocketService.ACTION_RESOLVE_APPROVAL)
                .putExtra(WebSocketService.EXTRA_APPROVAL_REQUEST_ID, requestId)
                .putExtra(WebSocketService.EXTRA_APPROVAL_DECISION, decision)
            startForegroundService(controlIntent)
            MessageRepository.resolveApproval(requestId, decision)
        }
        markReadAndSync(message.id)
        finish()
    }

    private fun markReadAndSync(messageId: String) {
        MessageRepository.markMessageRead(messageId)
        NotificationHelper.syncTaskNotifications(
            applicationContext,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
    }

    companion object {
        const val EXTRA_MESSAGE_ID = "extra_message_id"
        const val EXTRA_MESSAGE_ACTION_URL = MainActivity.EXTRA_MESSAGE_ACTION_URL

        /** 自动消失时长：30 秒。够长以避免用户来不及看，够短以避免长时间遮挡。 */
        private const val AUTO_DISMISS_MS = 30_000L
    }
}

@Composable
private fun FullScreenAlertContent(
    message: NotifyMessage,
    registeredApps: Map<String, RegisteredApp>,
    onView: () -> Unit,
    onDismiss: () -> Unit,
    onResolveApproval: (String) -> Unit,
    onProcessWaiting: () -> Unit,
    onContinue: () -> Unit
) {
    val state = TaskState.from(message.status)
    val accent = when (state) {
        TaskState.Waiting -> appColors.OverlayWaiting
        TaskState.Failed -> appColors.Danger
        TaskState.Stopped -> appColors.Danger
        TaskState.Approved -> appColors.Success
        TaskState.Rejected -> appColors.Danger
        TaskState.Completed -> appColors.Success
    }
    val presentation = appPresentation(message.source, registeredApps)
    val projectTitle = message.projectTitle(presentation.label)

    Box(
        modifier = Modifier
            .fillMaxSize()
            // 半透明黑色遮罩，让卡片视觉聚焦；点击遮罩区域不触发关闭，
            // 避免用户误触丢失消息（要看消息必须点「查看」或「关闭」按钮）。
            .background(Color.Black.copy(alpha = 0.55f)),
        contentAlignment = Alignment.Center
    ) {
        Card(
            modifier = Modifier
                .padding(24.dp)
                .fillMaxWidth(),
            shape = RoundedCornerShape(20.dp),
            colors = CardDefaults.cardColors(containerColor = appColors.Surface),
            elevation = CardDefaults.cardElevation(defaultElevation = 12.dp)
        ) {
            Column(modifier = Modifier.padding(20.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        modifier = Modifier
                            .size(12.dp)
                            .background(accent, CircleShape)
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        text = state.title,
                        color = accent,
                        fontWeight = FontWeight.Bold,
                        fontSize = 18.sp
                    )
                }

                if (presentation.channel != Channel.Unknown || registeredApps.containsKey(presentation.key)) {
                    Spacer(modifier = Modifier.height(12.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        AppIcon(
                            source = message.source,
                            registeredApps = registeredApps,
                            modifier = Modifier.size(30.dp)
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            text = presentation.label,
                            color = appColors.TextSecondary,
                            fontWeight = FontWeight.Medium,
                            fontSize = 14.sp
                        )
                    }
                }

                Spacer(modifier = Modifier.height(12.dp))
                Text(
                    text = projectTitle,
                    color = appColors.TextPrimary,
                    fontWeight = FontWeight.Bold,
                    fontSize = 18.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )

                Spacer(modifier = Modifier.height(5.dp))
                Text(
                    text = message.taskUsageLabel(),
                    color = appColors.TextTertiary,
                    fontSize = 12.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )

                Spacer(modifier = Modifier.height(14.dp))
                Text(
                    text = message.displayContent(),
                    color = appColors.TextSecondary,
                    fontSize = 14.sp,
                    lineHeight = 22.sp,
                    maxLines = 8,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth()
                )

                Spacer(modifier = Modifier.height(8.dp))
                Text(
                    text = MessageTimeFormatter.format(message.time),
                    color = appColors.TextSecondary,
                    fontSize = 13.sp
                )

                Spacer(modifier = Modifier.height(24.dp))
                if (message.hasApprovalAction()) {
                    // 等待确认：直接在通知卡片上允许/拒绝，无需打开 App。
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        PlainButton(
                            text = "拒绝",
                            onClick = { onResolveApproval("deny") },
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.SECONDARY
                        )
                        PlainButton(
                            text = "允许",
                            onClick = { onResolveApproval("allow") },
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.PRIMARY
                        )
                    }
                } else if (message.isWaitingTask()) {
                    // A waiting task without a real requestId is actionable, but it is
                    // not an approval and must open the task details instead.
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        PlainButton(
                            text = "关闭",
                            onClick = onDismiss,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.SECONDARY
                        )
                        PlainButton(
                            text = "处理",
                            onClick = onProcessWaiting,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.PRIMARY
                        )
                        if (message.canSendMessage()) {
                            PlainButton(
                                text = "继续",
                                onClick = onContinue,
                                modifier = Modifier.weight(1f),
                                variant = PlainButtonVariant.PRIMARY
                            )
                        }
                    }
                } else if (message.canSendMessage()) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        PlainButton(
                            text = "关闭",
                            onClick = onDismiss,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.SECONDARY
                        )
                        PlainButton(
                            text = "继续",
                            onClick = onContinue,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.PRIMARY
                        )
                    }
                } else {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        PlainButton(
                            text = "关闭",
                            onClick = onDismiss,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.SECONDARY
                        )
                        PlainButton(
                            text = "查看",
                            onClick = onView,
                            modifier = Modifier.weight(1f),
                            variant = PlainButtonVariant.PRIMARY
                        )
                    }
                }
            }
        }
    }
}
