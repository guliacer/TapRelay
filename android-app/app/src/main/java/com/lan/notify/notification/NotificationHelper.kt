package com.lan.notify.notification

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import android.graphics.Rect
import android.graphics.RectF
import android.media.AudioAttributes
import android.os.Build
import android.provider.Settings
import android.text.SpannableString
import android.text.Spanned
import android.text.style.ForegroundColorSpan
import android.util.Base64
import android.util.Log
import android.view.View
import android.widget.RemoteViews
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.graphics.drawable.IconCompat
import com.lan.notify.MainActivity
import com.lan.notify.R
import com.lan.notify.data.Channel
import com.lan.notify.data.MessageTimeFormatter
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TaskState
import com.lan.notify.data.TaskDurationFormatter
import com.lan.notify.data.TaskTokenFormatter
import com.lan.notify.data.canonicalSourceKey
import com.lan.notify.data.displayContent
import com.lan.notify.data.hasApprovalAction
import com.lan.notify.data.isStoppable
import com.lan.notify.data.isWaitingTask
import com.lan.notify.data.projectTitle
import com.lan.notify.data.safeActionUri
import com.lan.notify.ui.channelIconRes

object NotificationHelper {
    // Android does not allow an app update to raise a user-disabled channel.
    const val TASK_CHANNEL_ID = "task_alerts_v3"
    const val SUYAN_CHANNEL_ID = "suyan_messages_v1"
    const val BAIDU_NETDISK_CHANNEL_ID = "baidunetdisk_batches_v1"
    const val QUARK_NETDISK_CHANNEL_ID = "quarknetdisk_batches_v1"
    const val ADRIVE_CHANNEL_ID = "adrive_batches_v1"
    private const val SERVICE_CHANNEL_ID = "service_status"

    /**
     * Shared group key keeps the notification shade compact.  The group
     * summary is the single TapRelay entry; child notifications still carry
     * per-message actions such as Allow / Deny.
     */
    private const val TASK_GROUP_KEY = "task_messages"
    private val ALERT_VIBRATION_PATTERN = longArrayOf(0, 300, 120, 300)
    private const val UNREAD_TEXT_COLOR = 0xFFFF0000.toInt()
    private const val READ_TEXT_COLOR = 0xFFFFFFFF.toInt()

    const val SERVICE_NOTIFICATION_ID = 1001
    private const val TASK_ARCHIVE_NOTIFICATION_ID = 1002
    private const val TASK_SUMMARY_NOTIFICATION_ID = 1003
    const val VISIBLE_TASK_LIMIT = 5
    private const val ARCHIVE_LINE_LIMIT = 5
    private const val SUMMARY_LINE_LIMIT = 5
    private const val FULL_SCREEN_REQUEST_OFFSET = 10_000
    private const val APPROVAL_ALLOW_REQUEST_OFFSET = 20_000
    private const val APPROVAL_DENY_REQUEST_OFFSET = 30_000
    private const val AGENT_STOP_REQUEST_OFFSET = 40_000

    fun createChannels(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val alertAudio = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_NOTIFICATION)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
            .build()
        val manager = context.getSystemService(NotificationManager::class.java)
        val taskChannel = NotificationChannel(
            TASK_CHANNEL_ID,
            "\u4efb\u52a1\u901a\u77e5",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "\u4efb\u52a1\u72b6\u6001\u63d0\u9192"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            setShowBadge(true)
            enableLights(true)
            enableVibration(true)
            vibrationPattern = ALERT_VIBRATION_PATTERN
            setSound(Settings.System.DEFAULT_NOTIFICATION_URI, alertAudio)
        }
        val suyanChannel = NotificationChannel(
            SUYAN_CHANNEL_ID,
            "\u7d20\u8a00",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "\u7d20\u8a00\u6d88\u606f\u901a\u77e5"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            setShowBadge(true)
            enableLights(true)
            enableVibration(true)
            vibrationPattern = ALERT_VIBRATION_PATTERN
            setSound(Settings.System.DEFAULT_NOTIFICATION_URI, alertAudio)
        }
        val baiduChannel = NotificationChannel(
            BAIDU_NETDISK_CHANNEL_ID,
            "\u767e\u5ea6\u7f51\u76d8",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "\u767e\u5ea6\u7f51\u76d8\u6279\u91cf\u4efb\u52a1\u7ed3\u675f"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            setShowBadge(true)
            enableLights(true)
            enableVibration(true)
            vibrationPattern = ALERT_VIBRATION_PATTERN
            setSound(Settings.System.DEFAULT_NOTIFICATION_URI, alertAudio)
        }
        val quarkChannel = NotificationChannel(
            QUARK_NETDISK_CHANNEL_ID,
            "\u5938\u514b\u7f51\u76d8",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "\u5938\u514b\u7f51\u76d8\u6279\u91cf\u4efb\u52a1\u7ed3\u675f"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            setShowBadge(true)
            enableLights(true)
            enableVibration(true)
            vibrationPattern = ALERT_VIBRATION_PATTERN
            setSound(Settings.System.DEFAULT_NOTIFICATION_URI, alertAudio)
        }
        val adriveChannel = NotificationChannel(
            ADRIVE_CHANNEL_ID,
            "\u963f\u91cc\u4e91\u76d8",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "\u963f\u91cc\u4e91\u76d8\u6279\u91cf\u4efb\u52a1\u7ed3\u675f"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            setShowBadge(true)
            enableLights(true)
            enableVibration(true)
            vibrationPattern = ALERT_VIBRATION_PATTERN
            setSound(Settings.System.DEFAULT_NOTIFICATION_URI, alertAudio)
        }
        val serviceChannel = NotificationChannel(
            SERVICE_CHANNEL_ID,
            "\u670d\u52a1\u72b6\u6001",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "\u8fde\u63a5\u72b6\u6001"
        }
        manager.createNotificationChannel(taskChannel)
        manager.createNotificationChannel(suyanChannel)
        manager.createNotificationChannel(baiduChannel)
        manager.createNotificationChannel(quarkChannel)
        manager.createNotificationChannel(adriveChannel)
        manager.createNotificationChannel(serviceChannel)
        Log.i(
            TAG,
            "channels ready task=$TASK_CHANNEL_ID " +
                "taskImportance=${manager.getNotificationChannel(TASK_CHANNEL_ID)?.importance} " +
                "notificationsEnabled=${NotificationManagerCompat.from(context).areNotificationsEnabled()}"
        )
    }

    fun createServiceNotification(context: Context, content: String) =
        NotificationCompat.Builder(context, SERVICE_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_taprelay)
            .setContentTitle("TapRelay")
            .setContentText(content)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(openAppIntent(context, SERVICE_NOTIFICATION_ID))
            .build()

    fun syncTaskNotifications(
        context: Context,
        messages: List<NotifyMessage>,
        alertMessage: NotifyMessage? = null,
        registeredApps: Map<String, RegisteredApp> = emptyMap()
    ) {
        val manager = NotificationManagerCompat.from(context)
        Log.i(
            TAG,
            "sync tasks total=${messages.size} unread=${messages.count { !it.isRead }} " +
                "alert=${alertMessage?.id ?: "none"} enabled=${manager.areNotificationsEnabled()}"
        )
        // Persisted history is normally newest-first, but notification posting order must not
        // depend on that incidental storage detail. Sort explicitly for the summary, then post
        // oldest-first below so SystemUI leaves the newest child at the top of the group.
        val unread = messages.filter { !it.isRead }
            .sortedByDescending { MessageTimeFormatter.toEpochMillis(it.time) ?: Long.MIN_VALUE }
        val visible = unread.take(VISIBLE_TASK_LIMIT)
        val archived = unread.drop(VISIBLE_TASK_LIMIT)
        val visibleIds = visible.map { messageNotificationId(it) }.toSet()

        // Cancel both current and legacy child IDs before rebuilding.  The
        // same IDs are reused, so this also migrates notifications created by
        // the pre-grouped implementation without leaving stale cards behind.
        messages.forEach { message ->
            val id = messageNotificationId(message)
            if (id !in visibleIds) manager.cancel(id)
        }
        if (unread.isEmpty()) {
            manager.cancel(TASK_SUMMARY_NOTIFICATION_ID)
        }
        visible.asReversed().forEach { message ->
            postNotification(
                context,
                manager,
                messageNotificationId(message),
                buildTaskNotification(
                    context,
                    message,
                    alert = false,
                    registeredApps = registeredApps
                )
            )
        }
        refreshArchiveNotification(context, archived)
        if (unread.isNotEmpty()) {
            val alertTarget = alertMessage?.takeIf { candidate ->
                unread.any { it.id == candidate.id }
            }
            postNotification(
                context,
                manager,
                TASK_SUMMARY_NOTIFICATION_ID,
                buildTaskSummaryNotification(
                    context = context,
                    unread = unread,
                    alertMessage = alertTarget,
                    registeredApps = registeredApps
                )
            )
            // 系统 heads-up 弹窗与全屏卡片同时触发：显式 startActivity 与上面的
            // notify 同帧执行，避免依赖系统对 fullScreenIntent 的异步派发而出现
            // "先弹窗、后全屏"的先后间隔。全屏提醒不依赖 USE_FULL_SCREEN_INTENT：
            // App 持 SYSTEM_ALERT_WINDOW 豁免后台启动限制，显式拉起即可，锁屏/熄屏
            // 由 Activity 的 showWhenLocked/turnScreenOn 处理。
            // Activity 为 singleInstance，系统后续派发 fullScreenIntent 时会去重。
            if (alertTarget != null) {
                runCatching { context.startActivity(fullScreenAlertStartIntent(context, alertTarget)) }
                    .onFailure { error ->
                        Log.w(TAG, "full-screen alert launch failed (heads-up still shown): ${error.message}")
                    }
            }
        }
    }

    fun clearTaskNotifications(context: Context, messages: List<NotifyMessage>) {
        val manager = NotificationManagerCompat.from(context)
        manager.cancel(TASK_SUMMARY_NOTIFICATION_ID)
        manager.cancel(TASK_ARCHIVE_NOTIFICATION_ID)
        messages.forEach { manager.cancel(messageNotificationId(it)) }
    }

    /**
     * Posts a notification only while the app-level notification permission is
     * enabled.  The explicit guard keeps Android 13+ permission handling in
     * one place and prevents a revoked permission from breaking the socket
     * service's message path.
     */
    @SuppressLint("MissingPermission")
    fun notifyService(context: Context, notification: Notification) {
        postNotification(
            context,
            NotificationManagerCompat.from(context),
            SERVICE_NOTIFICATION_ID,
            notification
        )
    }

    @SuppressLint("MissingPermission")
    private fun postNotification(
        context: Context,
        manager: NotificationManagerCompat,
        id: Int,
        notification: Notification
    ) {
        if (!manager.areNotificationsEnabled()) return
        runCatching { manager.notify(id, notification) }
            .onFailure { error ->
                if (error is SecurityException) {
                    Log.w(TAG, "notification permission revoked while posting id=$id", error)
                } else {
                    throw error
                }
            }
    }

    fun postFullScreenAlert(
        context: Context,
        message: NotifyMessage,
        messages: List<NotifyMessage> = listOf(message),
        registeredApps: Map<String, RegisteredApp> = emptyMap()
    ) {
        Log.i(
            TAG,
            "post grouped task alert id=${message.id.hashCode()} source=${message.source} " +
                "status=${message.status}"
        )
        val allMessages = if (messages.any { it.id == message.id }) {
            messages
        } else {
            listOf(message) + messages
        }
        syncTaskNotifications(
            context = context,
            messages = allMessages,
            alertMessage = message,
            registeredApps = registeredApps
        )
    }

    private fun buildTaskNotification(
        context: Context,
        message: NotifyMessage,
        alert: Boolean,
        registeredApps: Map<String, RegisteredApp>
    ): Notification {
        val display = taskDisplayText(message, registeredApps)
        val channel = display.channel
        val registered = display.registered
        val displayBody = display.body
        val statusTitle = message.notificationTitle.trim()
            .takeIf { it.isNotBlank() }
            ?: TaskState.from(message.status).title
        val statusTitleColor = taskAccentColor(message)
        val timeText = MessageTimeFormatter.format(message.time)
        // The system small icon must remain a monochrome notification asset, but the
        // app-owned card must use the source application's original artwork.
        val smallIcon = taskSmallIcon(channel)
        val cardIconBitmap = registeredIconBitmap(context, registered) ?: taskIconBitmap(context, channelIconRes(channel))
        val customContent = RemoteViews(context.packageName, R.layout.notification_task_collapsed)
        populateCard(
            views = customContent,
            message = message,
            cardIconBitmap = cardIconBitmap,
            showControls = true,
            process = contentIntent(context, message),
            allow = message.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_ALLOW,
                    APPROVAL_ALLOW_REQUEST_OFFSET
                )
            },
            deny = message.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_DENY,
                    APPROVAL_DENY_REQUEST_OFFSET
                )
            },
            stop = message.takeIf { it.isStoppable() }?.let {
                agentStopIntent(context, it.id)
            },
            dismiss = dismissIntent(context, message)
        )
        val customBigContent = RemoteViews(context.packageName, R.layout.notification_task_expanded).apply {
            setTextViewText(R.id.notification_content, coloredText(displayBody, message.isRead))
            setTextViewText(R.id.notification_time, coloredText(timeText, message.isRead))
            setTextViewText(
                R.id.notification_duration,
                coloredText(TaskDurationFormatter.format(message.durationMs), message.isRead)
            )
            setTextColorForState(this, R.id.notification_content, message.isRead)
            setTextColorForState(this, R.id.notification_time, message.isRead)
            setTextColorForState(this, R.id.notification_duration, message.isRead)
            val tokenLabel = TaskTokenFormatter.formatUsage(message.credits, message.tokenCount)
            if (tokenLabel.isNotEmpty()) {
                setTextViewText(R.id.notification_token, coloredText(tokenLabel, message.isRead))
                setTextColorForState(this, R.id.notification_token, message.isRead)
                setViewVisibility(R.id.notification_token, View.VISIBLE)
            } else {
                setViewVisibility(R.id.notification_token, View.GONE)
            }
        }
        populateExpandedActions(
            views = customBigContent,
            message = message,
            process = contentIntent(context, message),
            allow = message.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_ALLOW,
                    APPROVAL_ALLOW_REQUEST_OFFSET
                )
            },
            deny = message.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_DENY,
                    APPROVAL_DENY_REQUEST_OFFSET
                )
            },
            stop = message.takeIf { it.isStoppable() }?.let {
                agentStopIntent(context, it.id)
            },
            dismiss = dismissIntent(context, message)
        )

        val builder = NotificationCompat.Builder(context, channelIdForSource(message.source))
            .setSmallIcon(
                registeredIconBitmap(context, registered)?.let { IconCompat.createWithBitmap(it) }
                    ?: IconCompat.createWithResource(context, smallIcon)
            )
            .setColor(statusTitleColor)
            // Do not set a large icon: ColorOS renders it as the duplicate right-side
            // app icon beside the custom status-bar card.
            // Keep the status-bar notification as the custom RemoteViews card. On
            // Android 12+/ColorOS, SystemUI may still impose its mandatory header;
            // the full-screen Activity does not depend on that decoration.
            // Supply the task state through the platform title/subtext fields so OEM SystemUI
            // can surface it in the mandatory app/time header row.
            .setContentTitle(coloredTextWithColor(statusTitle, statusTitleColor))
            .setSubText(coloredTextWithColor(statusTitle, statusTitleColor))
            .setContentText(coloredText(displayBody, message.isRead))
            .setContentIntent(contentIntent(context, message))
            .setDeleteIntent(dismissIntent(context, message))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setGroup(TASK_GROUP_KEY)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setCustomContentView(customContent)
            .setCustomBigContentView(customBigContent)
            .setCustomHeadsUpContentView(customContent)
            .setWhen(0L)
            .setShowWhen(false)

        if (alert) {
            builder.setPriority(NotificationCompat.PRIORITY_MAX)
                .setTicker(coloredText(displayBody, message.isRead))
                .setOnlyAlertOnce(false)
                .setFullScreenIntent(fullScreenAlertIntent(context, message), true)
        } else {
            builder.setPriority(NotificationCompat.PRIORITY_DEFAULT)
                .setSilent(true)
                .setOnlyAlertOnce(true)
        }

        // All actions live in the app-owned RemoteViews rows above. Native notification
        // actions are intentionally omitted because SystemUI sizes them by label length,
        // which makes "全部关闭" wider than the neighboring actions.
        return builder.build()
    }

    private data class TaskDisplayText(
        val channel: Channel,
        val registered: RegisteredApp?,
        val title: String,
        val body: String
    )

    private fun taskDisplayText(
        message: NotifyMessage,
        registeredApps: Map<String, RegisteredApp>
    ): TaskDisplayText {
        val state = TaskState.from(message.status)
        val channel = Channel.from(message.source)
        val registered = registeredApps[canonicalSourceKey(message.source)]
        val defaultTitle = when {
            registered != null -> "${registered.displayName} \u00b7 ${state.title}"
            channel == Channel.Suyan -> "\u7d20\u8a00 \u00b7 ${state.title}"
            channel == Channel.BaiduNetdisk -> "\u767e\u5ea6\u7f51\u76d8 \u00b7 ${state.title}"
            channel == Channel.QuarkNetdisk -> "\u5938\u514b\u7f51\u76d8 \u00b7 ${state.title}"
            channel == Channel.Adrive -> "\u963f\u91cc\u4e91\u76d8 \u00b7 ${state.title}"
            channel == Channel.ComfyUI -> "ComfyUI \u00b7 ${state.title}"
            channel == Channel.Opencode -> "OpenCode \u00b7 ${state.title}"
            channel == Channel.DeepseekHarness -> "DeepSeek Harness \u00b7 ${state.title}"
            channel == Channel.Freebuff -> "Freebuff \u00b7 ${state.title}"
            channel == Channel.LinuxDoCdk -> "Linux DO CDK \u00b7 ${state.title}"
            else -> state.title
        }
        val customTitle = message.notificationTitle.trim()
        val hasCustomTitle = customTitle.isNotBlank()
        val title = if (hasCustomTitle) customTitle else defaultTitle
        // Do not use the status title as a project fallback: the status belongs in the
        // platform header, while this app-owned card is reserved for log details.
        val projectTitle = message.projectTitle("")
        val body = when {
            hasCustomTitle || projectTitle.isBlank() -> message.displayContent()
            else -> "[$projectTitle] ${message.displayContent()}"
        }
        return TaskDisplayText(
            channel = channel,
            registered = registered,
            title = title,
            body = body
        )
    }

    private fun taskSummaryLine(
        message: NotifyMessage,
        registeredApps: Map<String, RegisteredApp>
    ): String {
        val display = taskDisplayText(message, registeredApps)
        return "${MessageTimeFormatter.format(message.time)} ${display.body}"
    }

    /**
     * Populate the app-owned notification card. The duplicate app icon, app label and relative
     * time are intentionally omitted from this RemoteViews card. The separate ColorOS/SystemUI
     * header (including its icon and `android:id/time`) is outside this tree and cannot be
     * removed or repositioned by the application.
     */
    private fun populateCard(
        views: RemoteViews,
        message: NotifyMessage,
        cardIconBitmap: Bitmap,
        showControls: Boolean,
        footerRight: String? = null,
        dismissAll: PendingIntent? = null,
        process: PendingIntent? = null,
        allow: PendingIntent? = null,
        deny: PendingIntent? = null,
        stop: PendingIntent? = null,
        dismiss: PendingIntent? = null
    ) {
        // The task state is carried by the platform notification title/subtext above.
        // Keep the app-owned card focused on the task log itself.
        // ColorOS may apply an automatic monochrome tint to RemoteViews drawable resources.
        // Supplying the original decoded bitmap keeps every built-in app icon's intended colors.
        views.setImageViewBitmap(R.id.notification_icon, cardIconBitmap)
        views.setTextViewText(R.id.notification_content, cardContentText(message))
        views.setTextViewText(R.id.notification_time, MessageTimeFormatter.format(message.time))
        views.setTextViewText(
            R.id.notification_duration,
            footerRight ?: TaskDurationFormatter.format(message.durationMs)
        )
        val tokenLabel = if (footerRight == null) TaskTokenFormatter.formatUsage(message.credits, message.tokenCount) else ""
        if (tokenLabel.isNotEmpty()) {
            views.setTextViewText(R.id.notification_token, tokenLabel)
            views.setTextColor(R.id.notification_token, 0xFF536276.toInt())
            views.setViewVisibility(R.id.notification_token, View.VISIBLE)
        } else {
            views.setViewVisibility(R.id.notification_token, View.GONE)
        }
        views.setTextColor(R.id.notification_content, 0xFF1B2A3C.toInt())
        views.setTextColor(R.id.notification_time, 0xFF536276.toInt())
        views.setTextColor(R.id.notification_duration, 0xFF536276.toInt())
        val approval = showControls && message.hasApprovalAction() && allow != null && deny != null
        val waiting = showControls && message.isWaitingTask() && !approval && process != null
        val showDismissAll = showControls && !approval && !waiting && dismissAll != null
        val showStop = showControls && !approval && stop != null
        val showDismiss = showControls && !approval && dismiss != null
        val controlVisibility = if (
            approval || waiting || showDismissAll || showStop || showDismiss
        ) View.VISIBLE else View.GONE
        views.setViewVisibility(R.id.notification_actions, controlVisibility)
        views.setViewVisibility(R.id.notification_dismiss_all, if (showDismissAll) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_process, if (waiting) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_allow, if (approval) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_deny, if (approval) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_stop, if (showStop) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_dismiss, if (showDismiss) View.VISIBLE else View.GONE)
        dismissAll?.takeIf { showDismissAll }?.let { views.setOnClickPendingIntent(R.id.notification_dismiss_all, it) }
        process?.takeIf { waiting }?.let { views.setOnClickPendingIntent(R.id.notification_process, it) }
        allow?.takeIf { approval }?.let { views.setOnClickPendingIntent(R.id.notification_allow, it) }
        deny?.takeIf { approval }?.let { views.setOnClickPendingIntent(R.id.notification_deny, it) }
        stop?.takeIf { showStop }?.let { views.setOnClickPendingIntent(R.id.notification_stop, it) }
        dismiss?.takeIf { showDismiss }?.let { views.setOnClickPendingIntent(R.id.notification_dismiss, it) }
    }

    /** Expanded notification cards keep the same explicit text controls as heads-up cards. */
    private fun populateExpandedActions(
        views: RemoteViews,
        message: NotifyMessage,
        dismissAll: PendingIntent? = null,
        process: PendingIntent? = null,
        allow: PendingIntent? = null,
        deny: PendingIntent? = null,
        stop: PendingIntent? = null,
        dismiss: PendingIntent?
    ) {
        val approval = message.hasApprovalAction() && allow != null && deny != null
        val waiting = message.isWaitingTask() && !approval && process != null
        val showDismissAll = !approval && !waiting && dismissAll != null
        val showStop = !approval && stop != null
        val showDismiss = !approval && dismiss != null
        val showActions = approval || waiting || showDismissAll || showStop || showDismiss
        val visibleActionCount = listOf(
            approval, waiting, showDismissAll, showStop, showDismiss
        ).count { it } + if (approval) 1 else 0
        views.setViewVisibility(
            R.id.notification_expanded_actions,
            if (showActions) View.VISIBLE else View.GONE
        )
        val centerSingleAction = showActions && visibleActionCount == 1
        views.setViewVisibility(
            R.id.notification_expanded_action_spacer_start,
            if (centerSingleAction) View.VISIBLE else View.GONE
        )
        views.setViewVisibility(
            R.id.notification_expanded_action_spacer_end,
            if (centerSingleAction) View.VISIBLE else View.GONE
        )
        views.setViewVisibility(R.id.notification_expanded_process, if (waiting) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_expanded_allow, if (approval) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_expanded_deny, if (approval) View.VISIBLE else View.GONE)
        views.setViewVisibility(
            R.id.notification_expanded_dismiss_all,
            if (showDismissAll) View.VISIBLE else View.GONE
        )
        views.setViewVisibility(R.id.notification_expanded_stop, if (showStop) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.notification_expanded_dismiss, if (showDismiss) View.VISIBLE else View.GONE)
        dismissAll?.takeIf { showDismissAll }?.let {
            views.setOnClickPendingIntent(R.id.notification_expanded_dismiss_all, it)
        }
        process?.takeIf { waiting }?.let { views.setOnClickPendingIntent(R.id.notification_expanded_process, it) }
        allow?.takeIf { approval }?.let { views.setOnClickPendingIntent(R.id.notification_expanded_allow, it) }
        deny?.takeIf { approval }?.let { views.setOnClickPendingIntent(R.id.notification_expanded_deny, it) }
        stop?.takeIf { showStop }?.let {
            views.setOnClickPendingIntent(R.id.notification_expanded_stop, it)
        }
        dismiss?.let { views.setOnClickPendingIntent(R.id.notification_expanded_dismiss, it) }
    }

    private fun taskAccentColor(message: NotifyMessage): Int =
        when (TaskState.from(message.status)) {
            TaskState.Waiting -> 0xFFE39A20.toInt()
            TaskState.Failed, TaskState.Rejected -> 0xFFD44747.toInt()
            TaskState.Stopped -> 0xFF718096.toInt()
            TaskState.Approved, TaskState.Completed -> 0xFF1BAA59.toInt()
        }

    /** Android status-bar small icons are intentionally monochrome. */
    private fun taskSmallIcon(channel: Channel): Int = when (channel) {
        Channel.Suyan -> R.drawable.ic_stat_suyan
        Channel.BaiduNetdisk -> R.drawable.ic_stat_baidunetdisk
        Channel.QuarkNetdisk -> R.drawable.ic_stat_quarknetdisk
        Channel.Adrive -> R.drawable.ic_adrive_logo
        Channel.ComfyUI -> R.drawable.ic_stat_comfyui
        Channel.Opencode -> R.drawable.ic_stat_opencode
        Channel.DeepseekHarness -> R.drawable.ic_stat_deepseekharness
        Channel.LinuxDoCdk -> R.drawable.ic_linuxdo_cdk_logo
        Channel.TapRelay -> R.drawable.ic_stat_taprelay
        else -> channelIconRes(channel)
    }

    /** Keep the task state prominent even when ColorOS ignores title text spans in its header. */
    private fun cardContentText(message: NotifyMessage): CharSequence {
        val content = message.displayContent()
        val state = TaskState.from(message.status)
        val stateTitle = state.title
        val start = content.indexOf(stateTitle)
        if (start < 0) return content

        return SpannableString(content).apply {
            setSpan(
                ForegroundColorSpan(taskAccentColor(message)),
                start,
                start + stateTitle.length,
                Spanned.SPAN_EXCLUSIVE_EXCLUSIVE
            )
        }
    }

    /** Build the single collapsed entry used by the notification group. */
    private fun buildTaskSummaryNotification(
        context: Context,
        unread: List<NotifyMessage>,
        alertMessage: NotifyMessage?,
        registeredApps: Map<String, RegisteredApp>
    ): Notification {
        val latest = unread.first()
        val latestDisplay = taskDisplayText(latest, registeredApps)
        val statusTitle = latest.notificationTitle.trim()
            .takeIf { it.isNotBlank() }
            ?: TaskState.from(latest.status).title
        val statusTitleColor = taskAccentColor(latest)
        val latestRegistered = registeredApps[canonicalSourceKey(latest.source)]
        val latestIconBitmap = registeredIconBitmap(context, latestRegistered)
        val latestSmallIcon = taskSmallIcon(latestDisplay.channel)
        val preview = latestDisplay.body

        val customContent = RemoteViews(context.packageName, R.layout.notification_group_collapsed).apply {
            populateCard(
                views = this,
                message = latest,
                cardIconBitmap = latestIconBitmap
                    ?: taskIconBitmap(context, channelIconRes(latestDisplay.channel)),
                showControls = true,
                dismissAll = summaryDismissIntent(context),
                process = contentIntent(context, latest),
                allow = latest.takeIf { it.hasApprovalAction() }?.let {
                    approvalIntent(
                        context,
                        it.requestId,
                        NotificationDismissReceiver.ACTION_APPROVAL_ALLOW,
                        APPROVAL_ALLOW_REQUEST_OFFSET
                    )
                },
                deny = latest.takeIf { it.hasApprovalAction() }?.let {
                    approvalIntent(
                        context,
                        it.requestId,
                        NotificationDismissReceiver.ACTION_APPROVAL_DENY,
                        APPROVAL_DENY_REQUEST_OFFSET
                    )
                },
                stop = latest.takeIf { it.isStoppable() }?.let {
                    agentStopIntent(context, it.id)
                },
                dismiss = dismissIntent(context, latest)
            )
        }
        val customBigContent = RemoteViews(context.packageName, R.layout.notification_group_expanded)
        unread.take(SUMMARY_LINE_LIMIT).forEachIndexed { index, message ->
            val lineId = when (index) {
                0 -> R.id.notification_line_1
                1 -> R.id.notification_line_2
                2 -> R.id.notification_line_3
                3 -> R.id.notification_line_4
                else -> R.id.notification_line_5
            }
            customBigContent.setViewVisibility(lineId, View.VISIBLE)
            customBigContent.setTextViewText(
                lineId,
                coloredText(taskSummaryLine(message, registeredApps), false)
            )
            setTextColorForState(customBigContent, lineId, false)
        }
        populateExpandedActions(
            views = customBigContent,
            message = latest,
            dismissAll = summaryDismissIntent(context),
            process = contentIntent(context, latest),
            allow = latest.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_ALLOW,
                    APPROVAL_ALLOW_REQUEST_OFFSET
                )
            },
            deny = latest.takeIf { it.hasApprovalAction() }?.let {
                approvalIntent(
                    context,
                    it.requestId,
                    NotificationDismissReceiver.ACTION_APPROVAL_DENY,
                    APPROVAL_DENY_REQUEST_OFFSET
                )
            },
                stop = latest.takeIf { it.isStoppable() }?.let {
                agentStopIntent(context, it.id)
            },
            dismiss = dismissIntent(context, latest)
        )

        val builder = NotificationCompat.Builder(context, TASK_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_taprelay)
            .setColor(statusTitleColor)
            // Do not set a large icon or DecoratedCustomViewStyle: both add the
            // duplicate right-side decoration on supported SystemUI implementations.
            // Put the latest task state into the platform title/subtext fields; the custom
            // card below is reserved for logs and metrics.
            .setContentTitle(coloredTextWithColor(statusTitle, statusTitleColor))
            .setSubText(coloredTextWithColor(statusTitle, statusTitleColor))
            .setContentText(coloredText(preview, false))
            .setContentIntent(openAppIntent(context, TASK_SUMMARY_NOTIFICATION_ID))
            .setDeleteIntent(summaryDismissIntent(context))
            .setAutoCancel(false)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setGroup(TASK_GROUP_KEY)
            .setGroupSummary(true)
            .setNumber(unread.size)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setCustomContentView(customContent)
            .setCustomBigContentView(customBigContent)
            .setCustomHeadsUpContentView(customContent)
            .setWhen(0L)
            .setShowWhen(false)

        if (alertMessage != null) {
            builder.setPriority(NotificationCompat.PRIORITY_MAX)
                .setTicker(coloredText(preview, false))
                .setOnlyAlertOnce(false)
                .setFullScreenIntent(fullScreenAlertIntent(context, alertMessage), true)
        } else {
            builder.setPriority(NotificationCompat.PRIORITY_DEFAULT)
                .setSilent(true)
                .setOnlyAlertOnce(true)
        }
        return builder.build()
    }

    private fun refreshArchiveNotification(context: Context, archived: List<NotifyMessage>) {
        val manager = NotificationManagerCompat.from(context)
        if (archived.isEmpty()) {
            manager.cancel(TASK_ARCHIVE_NOTIFICATION_ID)
            return
        }

        val headline = "${archived.size} \u6761\u66f4\u65e9\u672a\u8bfb"
        val style = NotificationCompat.InboxStyle()
            .setBigContentTitle("\u66f4\u65e9\u6d88\u606f")
        archived.take(ARCHIVE_LINE_LIMIT).forEach { message ->
            style.addLine(coloredText("\u25cf ${MessageTimeFormatter.format(message.time)} ${message.content}", false))
        }
        if (archived.size > ARCHIVE_LINE_LIMIT) {
            style.setSummaryText("\u8fd8\u6709 ${archived.size - ARCHIVE_LINE_LIMIT} \u6761")
        }

        val customContent = RemoteViews(context.packageName, R.layout.notification_summary_collapsed).apply {
            setTextViewText(R.id.notification_title, "\u66f4\u65e9\u6d88\u606f")
            setTextViewText(R.id.notification_summary, coloredText(headline, false))
            setTextColor(R.id.notification_summary, UNREAD_TEXT_COLOR)
        }
        val customBigContent = RemoteViews(context.packageName, R.layout.notification_summary_expanded)
        archived.take(ARCHIVE_LINE_LIMIT).forEachIndexed { index, message ->
            val lineId = when (index) {
                0 -> R.id.notification_line_1
                1 -> R.id.notification_line_2
                2 -> R.id.notification_line_3
                3 -> R.id.notification_line_4
                else -> R.id.notification_line_5
            }
            customBigContent.setViewVisibility(lineId, View.VISIBLE)
            customBigContent.setTextViewText(
                lineId,
                coloredText("\u25cf ${MessageTimeFormatter.format(message.time)} ${message.content}", false)
            )
            setTextColorForState(customBigContent, lineId, false)
        }

        val notification = NotificationCompat.Builder(context, TASK_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_taprelay)
            .setContentTitle("\u66f4\u65e9\u6d88\u606f")
            .setContentText(headline)
            .setStyle(style)
            .setContentIntent(archiveContentIntent(context))
            .setDeleteIntent(archiveDismissIntent(context))
            .setAutoCancel(false)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setGroup(TASK_GROUP_KEY)
            .setNumber(archived.size)
            .setSilent(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setCustomContentView(customContent)
            .setCustomBigContentView(customBigContent)
            .build()
        postNotification(context, manager, TASK_ARCHIVE_NOTIFICATION_ID, notification)
    }

    fun channelIdForSource(source: String): String = when {
        Channel.from(source) == Channel.Suyan -> SUYAN_CHANNEL_ID
        Channel.from(source) == Channel.BaiduNetdisk -> BAIDU_NETDISK_CHANNEL_ID
        Channel.from(source) == Channel.QuarkNetdisk -> QUARK_NETDISK_CHANNEL_ID
        Channel.from(source) == Channel.Adrive -> ADRIVE_CHANNEL_ID
        // OpenCode reuses the shared task channel (like other agents), so its
        // notifications are grouped with agent task alerts.
        else -> TASK_CHANNEL_ID
    }

    private fun registeredIconBitmap(context: Context, app: RegisteredApp?) = app?.iconBase64
        ?.takeIf { it.isNotBlank() }
        ?.let { encoded ->
            runCatching {
                val bytes = Base64.decode(encoded, Base64.DEFAULT)
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.let(::circularIconBitmap)
            }.getOrNull()
        }

    /**
     * Decoding before assigning the image prevents ColorOS from tinting a drawable resource
     * white in RemoteViews. All built-in channel artwork is rasterized in drawable-nodpi.
     */
    private fun taskIconBitmap(context: Context, resourceId: Int): Bitmap =
        requireNotNull(BitmapFactory.decodeResource(context.resources, resourceId)) {
            "Unable to decode notification card icon resource $resourceId"
        }.let(::circularIconBitmap)

    /**
     * RemoteViews does not guarantee that a bitmap's transparent canvas is rendered as a
     * circular icon on every Android skin. Apply the mask before handing it to the system.
     */
    private fun circularIconBitmap(source: Bitmap): Bitmap {
        val size = minOf(source.width, source.height)
        val output = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(output)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        val radius = size / 2f
        val sourceRect = Rect(
            (source.width - size) / 2,
            (source.height - size) / 2,
            (source.width + size) / 2,
            (source.height + size) / 2
        )
        val targetRect = RectF(0f, 0f, size.toFloat(), size.toFloat())
        canvas.drawCircle(radius, radius, radius, paint)
        paint.xfermode = PorterDuffXfermode(PorterDuff.Mode.SRC_IN)
        canvas.drawBitmap(source, sourceRect, targetRect, paint)
        paint.xfermode = null
        return output
    }

    private fun contentIntent(context: Context, message: NotifyMessage): PendingIntent =
        PendingIntent.getActivity(
            context,
            messageNotificationId(message),
            Intent(context, MainActivity::class.java).apply {
                action = Intent.ACTION_MAIN
                addCategory(Intent.CATEGORY_LAUNCHER)
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
                putExtra(MainActivity.EXTRA_MESSAGE_ID, message.id)
                putExtra(
                    MainActivity.EXTRA_MESSAGE_ACTION_URL,
                    safeActionUri(message.actionUrl)?.toString().orEmpty()
                )
            },
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun fullScreenAlertIntent(context: Context, message: NotifyMessage): PendingIntent =
        PendingIntent.getActivity(
            context,
            messageNotificationId(message) + FULL_SCREEN_REQUEST_OFFSET,
            fullScreenAlertStartIntent(context, message),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    /**
     * 全屏卡片 Activity 的普通 Intent。除供 fullScreenAlertIntent 使用外，
     * 还在发通知的同帧显式 startActivity，保证系统 heads-up 弹窗与全屏卡片
     * 同时触发（系统对 fullScreenIntent 的派发是异步的，会造成先后间隔；
     * Activity 为 singleInstance，即使系统后续再派发也会去重）。
     */
    private fun fullScreenAlertStartIntent(context: Context, message: NotifyMessage): Intent =
        Intent(context, FullScreenAlertActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            putExtra(FullScreenAlertActivity.EXTRA_MESSAGE_ID, message.id)
            putExtra(
                FullScreenAlertActivity.EXTRA_MESSAGE_ACTION_URL,
                safeActionUri(message.actionUrl)?.toString().orEmpty()
            )
        }

    private fun archiveContentIntent(context: Context): PendingIntent =
        openAppIntent(context, TASK_ARCHIVE_NOTIFICATION_ID)

    private fun summaryDismissIntent(context: Context): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            TASK_SUMMARY_NOTIFICATION_ID,
            Intent(context, NotificationDismissReceiver::class.java)
                .setAction(NotificationDismissReceiver.ACTION_DISMISS_SUMMARY),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun openAppIntent(context: Context, requestCode: Int): PendingIntent =
        PendingIntent.getActivity(
            context,
            requestCode,
            Intent(context, MainActivity::class.java).apply {
                action = Intent.ACTION_MAIN
                addCategory(Intent.CATEGORY_LAUNCHER)
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun dismissIntent(context: Context, message: NotifyMessage): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            messageNotificationId(message),
            Intent(context, NotificationDismissReceiver::class.java)
                .setAction(NotificationDismissReceiver.ACTION_DISMISS_MESSAGE)
                .putExtra(NotificationDismissReceiver.EXTRA_MESSAGE_ID, message.id),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun agentStopIntent(context: Context, messageId: String): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            messageId.hashCode() + AGENT_STOP_REQUEST_OFFSET,
            Intent(context, NotificationDismissReceiver::class.java)
                .setAction(NotificationDismissReceiver.ACTION_AGENT_STOP)
                .putExtra(NotificationDismissReceiver.EXTRA_MESSAGE_ID, messageId),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun approvalIntent(
        context: Context,
        requestId: String,
        action: String,
        requestOffset: Int
    ): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            requestId.hashCode() + requestOffset,
            Intent(context, NotificationDismissReceiver::class.java)
                .setAction(action)
                .putExtra(NotificationDismissReceiver.EXTRA_APPROVAL_REQUEST_ID, requestId),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun archiveDismissIntent(context: Context): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            TASK_ARCHIVE_NOTIFICATION_ID,
            Intent(context, NotificationDismissReceiver::class.java)
                .setAction(NotificationDismissReceiver.ACTION_DISMISS_ARCHIVE),
            PendingIntent.FLAG_UPDATE_CURRENT or pendingIntentImmutableFlag()
        )

    private fun coloredTextWithColor(text: CharSequence, color: Int): CharSequence =
        SpannableString(text).apply {
            if (length > 0) {
                setSpan(
                    ForegroundColorSpan(color),
                    0,
                    length,
                    Spanned.SPAN_EXCLUSIVE_EXCLUSIVE
                )
            }
        }

    private fun coloredText(text: CharSequence, isRead: Boolean): CharSequence =
        SpannableString(text).apply {
            if (length > 0) {
                setSpan(
                    ForegroundColorSpan(if (isRead) READ_TEXT_COLOR else UNREAD_TEXT_COLOR),
                    0,
                    length,
                    Spanned.SPAN_EXCLUSIVE_EXCLUSIVE
                )
            }
        }

    private fun setTextColorForState(views: RemoteViews, viewId: Int, isRead: Boolean) {
        views.setTextColor(viewId, if (isRead) READ_TEXT_COLOR else UNREAD_TEXT_COLOR)
    }

    private fun messageNotificationId(message: NotifyMessage): Int = message.id.hashCode()

    private const val TAG = "NotifyNotification"

    private fun pendingIntentImmutableFlag(): Int =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0
}
