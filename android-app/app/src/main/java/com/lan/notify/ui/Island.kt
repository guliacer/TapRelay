package com.lan.notify.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TaskState
import com.lan.notify.data.overlayForeground
import com.lan.notify.overlay.OverlayMetrics
import com.lan.notify.ui.theme.AppColors
import com.lan.notify.ui.theme.appColors

/** 状态栏下方的任务提示胶囊；展开卡片由 TapRelayOverlay 单独承载。 */
@Composable
fun TapRelayIsland(
    unread: List<NotifyMessage>,
    onOpen: () -> Unit,
    capsuleHeight: Dp,
    registeredApps: Map<String, RegisteredApp> = emptyMap(),
    drawChrome: Boolean = true,
    maxWidth: Dp = OverlayMetrics.PEEK_MAX_WIDTH_DP.dp,
    compact: Boolean = false,
    enableClick: Boolean = true,
    dynamicEffectsEnabled: Boolean = true,
    modifier: Modifier = Modifier,
    onVisualSizeChanged: ((IntSize) -> Unit)? = null
) {
    if (unread.isEmpty()) return
    val foreground = overlayForeground(unread)
    val state = TaskState.from(foreground?.status.orEmpty())
    val colors = appColors
    val latestMessage = foreground ?: unread.firstOrNull()
    val countLabel = unreadCountLabel(unread.size)
    val indicatorStyle = capsuleIndicatorStyle(state, colors)

    if (compact) {
        Box(
            modifier = modifier
                .fillMaxSize()
                .then(if (enableClick) Modifier.clickable(onClick = onOpen) else Modifier),
            contentAlignment = Alignment.Center
        ) {
            Box(
                modifier = Modifier
                    .size(7.dp)
                    .background(indicatorStyle.color, CircleShape)
            )
        }
        return
    }

    val capsuleShape = RoundedCornerShape(15.dp)
    Surface(
        modifier = modifier
            .wrapContentWidth()
            .widthIn(
                min = OverlayMetrics.PEEK_MIN_WIDTH_DP.dp,
                max = maxWidth
            )
            .height(30.dp)
            .onSizeChanged { onVisualSizeChanged?.invoke(it) }
            .then(if (enableClick) Modifier.clickable(onClick = onOpen) else Modifier),
        shape = capsuleShape,
        color = colors.IslandBackground,
        border = BorderStroke(1.dp, indicatorStyle.color.copy(alpha = 0.32f)),
        shadowElevation = 7.dp
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .clip(capsuleShape)
        ) {
            CapsuleRainEffect(
                color = indicatorStyle.color,
                dynamicEffectsEnabled = dynamicEffectsEnabled,
                modifier = Modifier.fillMaxSize()
            )
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(colors.IslandBackground.copy(alpha = 0.08f))
            )
            Row(
                modifier = Modifier
                    .align(Alignment.Center)
                    .wrapContentWidth()
                    .padding(horizontal = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                AnimatedCapsuleStatusIndicator(
                    style = indicatorStyle,
                    dynamicEffectsEnabled = dynamicEffectsEnabled,
                    modifier = Modifier.size(20.dp)
                )
                Spacer(modifier = Modifier.width(2.dp))
                Text(
                    text = indicatorStyle.shortText,
                    color = colors.IslandTextPrimary,
                    fontSize = 11.sp,
                    lineHeight = 12.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(modifier = Modifier.width(4.dp))
                AppIcon(
                    source = latestMessage?.source.orEmpty(),
                    registeredApps = registeredApps,
                    modifier = Modifier.size(16.dp),
                    contentDescription = "最新任务来源"
                )
                Spacer(modifier = Modifier.width(3.dp))
                CapsuleCountBadge(countLabel = countLabel, color = indicatorStyle.color)
            }
        }
    }
}

internal fun islandStatusColor(state: TaskState, colors: AppColors): Color = when (state) {
    TaskState.Waiting -> colors.IslandWaiting
    TaskState.Failed, TaskState.Stopped, TaskState.Rejected -> colors.IslandError
    TaskState.Approved, TaskState.Completed -> colors.IslandDone
}

private fun capsuleIndicatorStyle(state: TaskState, colors: AppColors): CapsuleIndicatorStyle = when (state) {
    TaskState.Waiting -> CapsuleIndicatorStyle(colors.IslandWaiting, CapsuleStatusSymbol.Equal, "待处理")
    TaskState.Failed, TaskState.Stopped, TaskState.Rejected -> CapsuleIndicatorStyle(colors.IslandError, CapsuleStatusSymbol.Cross, "失败")
    TaskState.Approved, TaskState.Completed -> CapsuleIndicatorStyle(colors.IslandDone, CapsuleStatusSymbol.Check, "完成")
}

@Composable
private fun CapsuleCountBadge(countLabel: String, color: Color) {
    Box(
        modifier = Modifier
            .height(18.dp)
            .widthIn(min = 18.dp)
            .background(color.copy(alpha = 0.14f), RoundedCornerShape(9.dp))
            .padding(horizontal = 4.dp),
        contentAlignment = Alignment.Center
    ) {
        AnimatedContent(
            targetState = countLabel,
            transitionSpec = {
                (slideInVertically { it / 2 } + fadeIn()) togetherWith
                    (slideOutVertically { -it / 2 } + fadeOut())
            },
            label = "capsule-count"
        ) { label ->
            Text(
                text = label,
                color = color,
                fontSize = 11.sp,
                lineHeight = 11.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1
            )
        }
    }
}

internal fun peekStatusTitle(state: TaskState, compact: Boolean): String = when (state) {
    TaskState.Waiting -> if (compact) "待处理" else "需要处理"
    TaskState.Failed -> if (compact) "失败" else "执行失败"
    TaskState.Stopped -> "已停止"
    TaskState.Approved -> "已批准"
    TaskState.Rejected -> "已拒绝"
    TaskState.Completed -> "已完成"
}

internal fun unreadCountLabel(count: Int): String = when {
    count <= 0 -> ""
    count > 99 -> "99+"
    else -> count.toString()
}
