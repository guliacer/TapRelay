package com.lan.notify.notification

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.content.ContextCompat
import com.lan.notify.data.MessageRepository
import com.lan.notify.service.WebSocketService

/**
 * 接收通知被划掉（swipe dismiss）的回调。
 *
 * 用户在状态栏划掉通知就说明已经看过了，此处直接把对应消息标记为已查看，
 * 不再要求打开 App 手动点确认。点击通知进入 App 的路径由
 * MainActivity 的 EXTRA_MESSAGE_ID 处理，两条路径最终都落到
 * MessageRepository.markMessageRead。
 */
class NotificationDismissReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        MessageRepository.initialize(context.applicationContext)

        when (intent.action) {
            ACTION_DISMISS_MESSAGE -> {
                val messageId = intent.getStringExtra(EXTRA_MESSAGE_ID) ?: return
                MessageRepository.markMessageRead(messageId)
            }

            ACTION_DISMISS_SUMMARY -> {
                // The group summary represents every unread child, so
                // dismissing it acknowledges the complete notification group.
                val unreadIds = MessageRepository.state.value.messages
                    .filter { !it.isRead }
                    .map { it.id }
                MessageRepository.markMessagesRead(unreadIds)
            }

            ACTION_DISMISS_ARCHIVE -> {
                // 归档通知代表可见窗口之外的全部未查看消息，划掉即全部确认。
                val archivedIds = MessageRepository.state.value.messages
                    .filter { !it.isRead }
                    .drop(NotificationHelper.VISIBLE_TASK_LIMIT)
                    .map { it.id }
                MessageRepository.markMessagesRead(archivedIds)
            }

            ACTION_APPROVAL_ALLOW, ACTION_APPROVAL_DENY -> {
                val requestId = intent.getStringExtra(EXTRA_APPROVAL_REQUEST_ID)?.trim().orEmpty()
                if (requestId.isBlank()) return
                val decision = if (intent.action == ACTION_APPROVAL_ALLOW) "allow" else "deny"

                val controlIntent = Intent(context, WebSocketService::class.java)
                    .setAction(WebSocketService.ACTION_RESOLVE_APPROVAL)
                    .putExtra(WebSocketService.EXTRA_APPROVAL_REQUEST_ID, requestId)
                    .putExtra(WebSocketService.EXTRA_APPROVAL_DECISION, decision)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    ContextCompat.startForegroundService(context, controlIntent)
                } else {
                    context.startService(controlIntent)
                }
                // Reflect the user's decision immediately; the server resolution event may
                // arrive later or be unavailable while the phone is reconnecting.
                MessageRepository.resolveApproval(requestId, decision)
            }

            ACTION_AGENT_STOP -> {
                val messageId = intent.getStringExtra(EXTRA_MESSAGE_ID) ?: return
                val message = MessageRepository.state.value.messages
                    .firstOrNull { it.id == messageId } ?: return
                WebSocketService.requestAgentStop(context, message)
                // Don't sync notifications — keep the message visible.
                return
            }

            else -> return
        }

        NotificationHelper.syncTaskNotifications(
            context.applicationContext,
            MessageRepository.state.value.messages,
            registeredApps = MessageRepository.state.value.registeredApps
        )
    }

    companion object {
        const val ACTION_DISMISS_MESSAGE = "com.lan.notify.notification.ACTION_DISMISS_MESSAGE"
        const val ACTION_DISMISS_SUMMARY = "com.lan.notify.notification.ACTION_DISMISS_SUMMARY"
        const val ACTION_DISMISS_ARCHIVE = "com.lan.notify.notification.ACTION_DISMISS_ARCHIVE"
        const val ACTION_APPROVAL_ALLOW = "com.lan.notify.notification.ACTION_APPROVAL_ALLOW"
        const val ACTION_APPROVAL_DENY = "com.lan.notify.notification.ACTION_APPROVAL_DENY"
        const val ACTION_AGENT_STOP = "com.lan.notify.notification.ACTION_AGENT_STOP"
        const val EXTRA_MESSAGE_ID = "extra_message_id"
        const val EXTRA_APPROVAL_REQUEST_ID = "extra_approval_request_id"
    }
}
