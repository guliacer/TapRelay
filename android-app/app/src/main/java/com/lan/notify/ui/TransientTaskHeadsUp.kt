package com.lan.notify.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.MessageTimeFormatter
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TaskDurationFormatter
import com.lan.notify.data.TaskState
import com.lan.notify.data.TaskTokenFormatter
import com.lan.notify.data.canSendMessage
import com.lan.notify.data.displayContent
import com.lan.notify.data.hasApprovalAction
import com.lan.notify.data.isWaitingTask
import com.lan.notify.ui.theme.appColors

/**
 * Bright-screen replacement for the OEM heads-up surface.
 *
 * The OEM header always injects TapRelay's application icon outside RemoteViews.
 * This app-owned overlay keeps the approved task/source/action card intact without
 * exposing that duplicate system icon. It is intentionally separate from the
 * lock-screen and status-bar notification layouts.
 */
@Composable
fun TransientTaskHeadsUp(
    message: NotifyMessage,
    registeredApps: Map<String, RegisteredApp>,
    onDismiss: () -> Unit,
    onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
    onProcessWaiting: ((NotifyMessage) -> Unit)? = null,
    onContinue: ((NotifyMessage) -> Unit)? = null
) {
    val state = TaskState.from(message.status)
    val accent = when (state) {
        TaskState.Waiting -> appColors.OverlayWaiting
        TaskState.Failed, TaskState.Rejected, TaskState.Stopped -> appColors.Danger
        TaskState.Approved, TaskState.Completed -> appColors.Success
    }
    val surfaceShape = RoundedCornerShape(18.dp)

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 8.dp),
        shape = surfaceShape,
        color = appColors.OverlaySurface,
        shadowElevation = 10.dp
    ) {
        Column(modifier = Modifier.padding(8.dp)) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 4.dp, vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(7.dp)
                        .background(accent, CircleShape)
                )
                Spacer(modifier = Modifier.width(7.dp))
                Text(
                    text = state.title,
                    color = accent,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1
                )
                Spacer(modifier = Modifier.weight(1f))
                Text(
                    text = MessageTimeFormatter.formatRecent(message.time),
                    color = appColors.TextTertiary,
                    fontSize = 11.sp,
                    maxLines = 1
                )
            }

            Spacer(modifier = Modifier.height(7.dp))

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(
                        if (!message.hasApprovalAction() &&
                            message.isWaitingTask() &&
                            message.canSendMessage()
                        ) {
                            124.dp
                        } else {
                            96.dp
                        }
                    )
            ) {
                TransientActionSurface(
                     message = message,
                     registeredApps = registeredApps,
                     onDismiss = onDismiss,
                     onResolveApproval = onResolveApproval,
                     onProcessWaiting = onProcessWaiting,
                     onContinue = onContinue,
                     modifier = Modifier
                        .width(108.dp)
                        .fillMaxHeight()
                )
                Spacer(modifier = Modifier.width(6.dp))
                TransientMessageSurface(
                    message = message,
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight()
                )
            }
        }
    }
}

@Composable
private fun TransientActionSurface(
    message: NotifyMessage,
    registeredApps: Map<String, RegisteredApp>,
    onDismiss: () -> Unit,
    onResolveApproval: ((NotifyMessage, String) -> Unit)?,
    onProcessWaiting: ((NotifyMessage) -> Unit)?,
    onContinue: ((NotifyMessage) -> Unit)?,
    modifier: Modifier = Modifier
) {
    val shape = RoundedCornerShape(14.dp)
    Surface(
        modifier = modifier,
        shape = shape,
        color = appColors.PanelWarm,
        border = androidx.compose.foundation.BorderStroke(1.dp, appColors.BorderSoft)
    ) {
        Column(
            modifier = Modifier
                .fillMaxHeight()
                .padding(vertical = 7.dp, horizontal = 5.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.SpaceBetween
        ) {
            AppIcon(
                source = message.source,
                registeredApps = registeredApps,
                modifier = Modifier.size(24.dp)
            )
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                if (message.hasApprovalAction()) {
                    TransientActionButton(
                        text = "允许",
                        onClick = { onResolveApproval?.invoke(message, "allow") }
                    )
                    TransientActionButton(
                        text = "拒绝",
                        onClick = { onResolveApproval?.invoke(message, "deny") }
                    )
                } else if (message.isWaitingTask()) {
                    TransientActionButton(
                        text = "处理",
                        onClick = { onProcessWaiting?.invoke(message) }
                    )
                    if (message.canSendMessage()) {
                        TransientActionButton(
                            text = "继续",
                            onClick = { onContinue?.invoke(message) }
                        )
                    }
                    TransientActionButton(text = "关闭", onClick = onDismiss)
                } else if (message.canSendMessage()) {
                    TransientActionButton(
                        text = "继续",
                        onClick = { onContinue?.invoke(message) }
                    )
                    TransientActionButton(text = "关闭", onClick = onDismiss)
                } else {
                    TransientActionButton(text = "关闭", onClick = onDismiss)
                }
            }
        }
    }
}

@Composable
private fun TransientActionButton(
    text: String,
    onClick: () -> Unit
) {
    val interactionSource = remember { MutableInteractionSource() }
    val shape = RoundedCornerShape(10.dp)
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(22.dp)
            .clip(shape)
            .background(appColors.PrimarySage, shape)
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = onClick
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = text,
            color = appColors.Surface,
            fontSize = 9.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1
        )
    }
}

@Composable
private fun TransientMessageSurface(
    message: NotifyMessage,
    modifier: Modifier = Modifier
) {
    val shape = RoundedCornerShape(14.dp)
    Surface(
        modifier = modifier,
        shape = shape,
        color = appColors.Surface,
        border = androidx.compose.foundation.BorderStroke(1.dp, appColors.BorderSoft)
    ) {
        Column(
            modifier = Modifier
                .fillMaxHeight()
                .padding(horizontal = 9.dp, vertical = 7.dp)
        ) {
            // Keep multi-line task logs visually centered in the fixed-height card.
            // A weighted Text alone pins its glyphs to the top and leaves an
            // unbalanced void above the footer for short logs.
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentAlignment = Alignment.CenterStart
            ) {
                Text(
                    text = message.displayContent(),
                    color = appColors.TextPrimary,
                    fontSize = 12.sp,
                    lineHeight = 18.sp,
                    maxLines = 4,
                    overflow = TextOverflow.Ellipsis
                )
            }
            Spacer(modifier = Modifier.height(3.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = MessageTimeFormatter.format(message.time),
                    modifier = Modifier.weight(1f),
                    color = appColors.TextTertiary,
                    fontSize = 10.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(modifier = Modifier.width(6.dp))
                Text(
                    text = TaskDurationFormatter.format(message.durationMs),
                    color = appColors.TextTertiary,
                    fontSize = 10.sp,
                    maxLines = 1
                )
                val tokenLabel = TaskTokenFormatter.formatUsage(message.credits, message.tokenCount)
                if (tokenLabel.isNotEmpty()) {
                    Text(
                        text = "  $tokenLabel",
                        color = appColors.TextTertiary,
                        fontSize = 10.sp,
                        maxLines = 1
                    )
                }
            }
        }
    }
}
