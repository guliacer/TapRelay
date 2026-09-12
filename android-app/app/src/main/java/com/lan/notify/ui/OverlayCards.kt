package com.lan.notify.ui

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
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
import com.lan.notify.overlay.OverlayMetrics
import com.lan.notify.ui.theme.appColors
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/** Every stacked card uses the same height so a long log cannot push into the card below. */
internal val OverlayCardHeight = 236.dp
internal val OverlayStackRevealStep = 18.dp
// The body has a fixed height so every stacked card lines up. Reserve almost all of
// that space for the log and use a relaxed leading, rather than leaving a blank block
// beneath tightly packed lines.
private val OverlayLogLineHeight = 22.sp
private const val StackScaleStep = 0.045f
private const val SwipeDismissThreshold = 0.28f

/**
 * 所有未读消息共享一个锚点：最新卡片完整展示在最前，旧卡在其后逐层下移、收窄，
 * 且以底部为基准对齐——旧卡只向下露出一条窄边，宽度逐层收窄，形成自然堆叠。
 * 卡片高度固定（R6：层叠卡片高度统一），避免短消息卡片高低不齐导致堆叠错乱。
 */
@Composable
fun OverlayCards(
    messages: List<NotifyMessage>,
    registeredApps: Map<String, RegisteredApp>,
    onCardDismiss: (NotifyMessage) -> Unit,
    onDismissAll: () -> Unit,
    onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
    onProcessWaiting: ((NotifyMessage) -> Unit)? = null,
    onContinue: ((NotifyMessage) -> Unit)? = null,
    playEntrance: Boolean = false,
    dynamicEffectsEnabled: Boolean = true
) {
    if (messages.isEmpty()) return
    val visibleMessages = messages.take(5)
    val stackHeight = OverlayCardHeight + OverlayStackRevealStep * (visibleMessages.size - 1)

    // 独立窗口入场时保留一次弹性展开；单一 Window 形变由 TapRelayOverlay 负责。
    val panelScale = remember { Animatable(if (playEntrance && dynamicEffectsEnabled) 0.32f else 1f) }
    val panelAlpha = remember { Animatable(if (playEntrance && dynamicEffectsEnabled) 0f else 1f) }
    LaunchedEffect(playEntrance, visibleMessages.firstOrNull()?.id) {
        if (!playEntrance || !dynamicEffectsEnabled) {
            panelScale.snapTo(1f)
            panelAlpha.snapTo(1f)
            return@LaunchedEffect
        }
        panelScale.snapTo(0.32f)
        panelAlpha.snapTo(0f)
        panelScale.animateTo(
            targetValue = 1f,
            animationSpec = spring(
                dampingRatio = Spring.DampingRatioMediumBouncy,
                stiffness = Spring.StiffnessMediumLow
            )
        )
        panelAlpha.animateTo(
            targetValue = 1f,
            animationSpec = tween(180, easing = FastOutSlowInEasing)
        )
    }

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(stackHeight + 16.dp)
            .graphicsLayer {
                scaleX = panelScale.value
                scaleY = panelScale.value
                alpha = panelAlpha.value
                transformOrigin = TransformOrigin(0.5f, 1f)
            }
            .padding(horizontal = OverlayMetrics.EXPANDED_HORIZONTAL_PADDING_DP.dp, vertical = 8.dp)
    ) {
        visibleMessages.forEachIndexed { depth, message ->
            key(message.id) {
                // 以底部为基准对齐：旧卡向下偏移 OverlayStackRevealStep，只露出底部窄边。
                val verticalOffset by animateDpAsState(
                    targetValue = OverlayStackRevealStep * depth,
                    animationSpec = spring(
                        dampingRatio = Spring.DampingRatioNoBouncy,
                        stiffness = Spring.StiffnessMedium
                    ),
                    label = "OverlayStackOffset"
                )
                val horizontalScale by animateFloatAsState(
                    targetValue = (1f - depth * StackScaleStep).coerceAtLeast(0.86f),
                    animationSpec = spring(
                        dampingRatio = Spring.DampingRatioNoBouncy,
                        stiffness = Spring.StiffnessMedium
                    ),
                    label = "OverlayStackScale"
                )

                OverlayTaskCard(
                    message = message,
                    registeredApps = registeredApps,
                    controlsVisible = depth == 0,
                    onDismiss = { onCardDismiss(message) },
                     onDismissAll = onDismissAll,
                     onResolveApproval = onResolveApproval,
                     onProcessWaiting = onProcessWaiting,
                     onContinue = onContinue,
                     dynamicEffectsEnabled = dynamicEffectsEnabled,
                    modifier = Modifier
                        .fillMaxWidth()
                        .offset(y = verticalOffset)
                        .graphicsLayer {
                            scaleX = horizontalScale
                            // 底部基准缩放：高度不变，宽度从底部对齐收窄
                            transformOrigin = TransformOrigin(0.5f, 1f)
                        }
                        .zIndex((visibleMessages.size - depth).toFloat())
                )
            }
        }
    }
}

@Composable
private fun OverlayTaskCard(
    message: NotifyMessage,
    registeredApps: Map<String, RegisteredApp>,
    controlsVisible: Boolean,
    onDismiss: () -> Unit,
    onDismissAll: () -> Unit,
    onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
    onProcessWaiting: ((NotifyMessage) -> Unit)? = null,
    onContinue: ((NotifyMessage) -> Unit)? = null,
    dynamicEffectsEnabled: Boolean,
    modifier: Modifier = Modifier
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
    val headerBackground = appColors.PanelWarm
    val footerBackground = appColors.PanelWarm
    val dividerColor = appColors.BorderSoft

    Surface(
        modifier = modifier
            .height(OverlayCardHeight)
            .smoothSwipeToDismiss(
                enabled = controlsVisible,
                onDismiss = onDismiss
            ),
        shape = RoundedCornerShape(14.dp),
        color = appColors.OverlaySurface,
        shadowElevation = if (controlsVisible) 8.dp else 4.dp
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .fillMaxHeight()
        ) {
            // 头部条：状态点 + 状态标题（左）‖ 应用图标 + 来源名（右）
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(headerBackground)
                    .padding(horizontal = 14.dp, vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(9.dp)
                        .background(accent, CircleShape)
                )
                Spacer(modifier = Modifier.width(7.dp))
                Text(
                    text = state.title,
                    color = accent,
                    fontWeight = FontWeight.Bold,
                    fontSize = 13.sp,
                    maxLines = 1
                )
                Spacer(modifier = Modifier.width(6.dp))
                Spacer(modifier = Modifier.weight(1f))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    AppIcon(
                        source = message.source,
                        registeredApps = registeredApps,
                        modifier = Modifier.size(18.dp)
                    )
                    Spacer(modifier = Modifier.width(5.dp))
                    Text(
                        text = presentation.label,
                        color = appColors.TextSecondary,
                        fontWeight = FontWeight.Medium,
                        fontSize = 11.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        text = MessageTimeFormatter.formatRecent(message.time),
                        color = appColors.TextTertiary,
                        fontSize = 10.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }

            // 1px 分隔线
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(dividerColor)
            )

            // 正文区：任务名称优先，使用量单独一行，真实日志用较小灰字承载。
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .padding(horizontal = 14.dp, vertical = 8.dp)
            ) {
                Text(
                    text = message.projectTitle(presentation.label),
                    color = appColors.TextPrimary,
                    fontWeight = FontWeight.Bold,
                    fontSize = 16.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    text = message.taskUsageLabel(),
                    color = appColors.TextTertiary,
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(modifier = Modifier.height(2.dp))
                Box(modifier = Modifier.weight(1f).fillMaxWidth().clip(RoundedCornerShape(4.dp))) {
                    val logScroll = rememberScrollState()
                    LaunchedEffect(message.id, message.taskLog) { logScroll.scrollTo(0) }
                    Text(
                        text = message.displayContent(),
                        color = appColors.TextSecondary,
                        fontSize = 12.sp,
                        lineHeight = OverlayLogLineHeight,
                        modifier = Modifier
                            .fillMaxWidth()
                            .verticalScroll(logScroll, enabled = controlsVisible)
                    )
                }
            }

            // 1px 分隔线
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(dividerColor)
            )

            // 页脚行：时间（左下角）‖ 操作按钮（右侧，仅最前卡）
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(footerBackground)
                    .padding(horizontal = 14.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = MessageTimeFormatter.format(message.time),
                    color = appColors.TextTertiary,
                    fontSize = 10.sp,
                    maxLines = 1
                )
                Spacer(modifier = Modifier.weight(1f))
                if (controlsVisible) {
                    val approvalAction = message.hasApprovalAction()
                    val waitingTask = message.isWaitingTask()
                    val actions = buildList {
                        if (approvalAction) {
                            add(
                                Triple("拒绝", { onResolveApproval?.invoke(message, "deny"); Unit }, false)
                            )
                            add(
                                Triple("允许", { onResolveApproval?.invoke(message, "allow"); Unit }, true)
                            )
                        } else {
                            if (waitingTask) {
                                add(
                                    Triple("处理", { onProcessWaiting?.invoke(message); Unit }, true)
                                )
                            }
                            if (message.canSendMessage()) {
                                add(
                                    Triple("继续", { onContinue?.invoke(message); Unit }, true)
                                )
                            }
                            if (message.canSendMessage() || waitingTask) {
                                add(Triple("关闭", onDismiss, false))
                            } else {
                                add(Triple("全部关闭", onDismissAll, false))
                                add(Triple("关闭", onDismiss, true))
                            }
                        }
                    }
                    // Every visible button owns the same slot. The three-action waiting
                    // state uses a little more width, but never squeezes labels unevenly.
                    Row(
                        modifier = Modifier.width(if (actions.size == 3) 204.dp else 148.dp),
                        horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(6.dp)
                    ) {
                        actions.forEach { (text, onClick, primary) ->
                            CardTextButton(
                                text = text,
                                onClick = onClick,
                                modifier = Modifier.weight(1f),
                                primary = primary
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * 卡片页脚长方形按钮：带文本，圆角小矩形，自绘 Box + Text（沿用 PlainButton 的 M3 白底规避策略，P066）。
 * [primary] 为 true 时实心 sage 填充（用于"关闭"主操作），否则 sage 描边。
 */
@Composable
private fun CardTextButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    primary: Boolean = false
) {
    val haptic = LocalHapticFeedback.current
    val shape = RoundedCornerShape(7.dp)
    val container = if (primary) appColors.PrimarySage else Color.Transparent
    val content = if (primary) appColors.Surface else appColors.PrimarySage
    val borderColor = if (primary) null else appColors.PrimarySage
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(24.dp)
            .clip(shape)
            .background(container, shape)
            .then(
                if (borderColor != null) {
                    Modifier.border(1.dp, borderColor, shape)
                } else {
                    Modifier
                }
            )
            .clickable(
                interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() },
                indication = null,
                onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    onClick()
                }
            )
            .padding(horizontal = 10.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = text,
            color = content,
            fontWeight = FontWeight.Medium,
            fontSize = 11.sp,
            maxLines = 1
        )
    }
}

/**
 * 关闭按钮：图标化（28dp 命中区），避免占用页脚右侧整列宽度。
 * 自绘 Box + Text（沿用 PlainButton 的 M3 白底规避策略，P066）。
 *
 * [content] 支持传入自定义图标字符（如 ⊕ 表示全部关闭），默认 ✕ 表示关闭。
 */
@Composable
private fun CardIconButton(
    contentDescription: String,
    onClick: () -> Unit,
    content: String = "\u2715"
) {
    val haptic = LocalHapticFeedback.current
    Box(
        modifier = Modifier
            .size(28.dp)
            .background(appColors.PrimarySageSoft, CircleShape)
            .border(1.dp, appColors.BorderSoft, CircleShape)
            .clickable(
                interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() },
                indication = null,
                onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    onClick()
                }
            ),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = content,
            color = appColors.TextSecondary,
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium
        )
    }
}

/**
 * 仅响应向左拖动。拖动阶段只更新图层位移与透明度，不触发布局重排；
 * 松手超过卡宽 28% 后快速滑出，否则用无回弹弹簧归位。
 */
@Composable
private fun Modifier.smoothSwipeToDismiss(
    enabled: Boolean,
    onDismiss: () -> Unit
): Modifier {
    var widthPx by remember { mutableFloatStateOf(1f) }
    var offsetPx by remember { mutableFloatStateOf(0f) }
    val scope = rememberCoroutineScope()
    val haptic = LocalHapticFeedback.current
    val animationJob = remember { arrayOfNulls<Job>(1) }

    if (!enabled) return this

    fun animateTo(target: Float, dismissAfter: Boolean) {
        animationJob[0]?.cancel()
        animationJob[0] = scope.launch {
            animate(
                initialValue = offsetPx,
                targetValue = target,
                animationSpec = if (dismissAfter) {
                    tween(durationMillis = 180, easing = FastOutSlowInEasing)
                } else {
                    spring(
                        dampingRatio = Spring.DampingRatioNoBouncy,
                        stiffness = Spring.StiffnessMediumLow
                    )
                }
            ) { value, _ ->
                offsetPx = value
            }
            if (dismissAfter) onDismiss()
        }
    }

    return this
        .onSizeChanged { widthPx = it.width.toFloat().coerceAtLeast(1f) }
        .graphicsLayer {
            translationX = offsetPx
            alpha = 1f - ((-offsetPx / widthPx).coerceIn(0f, 1f) * 0.35f)
        }
        .pointerInput(enabled) {
            detectHorizontalDragGestures(
                onDragStart = {
                    animationJob[0]?.cancel()
                },
                onHorizontalDrag = { change, dragAmount ->
                    if (dragAmount < 0f || offsetPx < 0f) {
                        change.consume()
                        offsetPx = (offsetPx + dragAmount).coerceIn(-widthPx, 0f)
                    }
                },
                onDragCancel = {
                    animateTo(target = 0f, dismissAfter = false)
                },
                onDragEnd = {
                    val shouldDismiss = offsetPx <= -widthPx * SwipeDismissThreshold
                    if (shouldDismiss) {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    }
                    animateTo(
                        target = if (shouldDismiss) -widthPx * 1.08f else 0f,
                        dismissAfter = shouldDismiss
                    )
                }
            )
        }
}
