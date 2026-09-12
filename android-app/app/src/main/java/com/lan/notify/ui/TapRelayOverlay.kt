package com.lan.notify.ui

import androidx.compose.animation.animateColor
import androidx.compose.animation.core.animateDp
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.updateTransition
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Popup
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.OverlayPhase
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TapRelayOverlayState
import com.lan.notify.data.events
import com.lan.notify.data.phase
import com.lan.notify.overlay.OverlayMetrics
import com.lan.notify.overlay.OverlayPositionCalculator
import com.lan.notify.overlay.ScreenGeometry
import com.lan.notify.ui.theme.AppTokens
import com.lan.notify.ui.theme.appColors

/**
 * 单一悬浮层 Compose Root：同一个 Window 内根据 [TapRelayOverlayState] 连续形变
 * 兼容的小岛（Compact）、状态栏下方的短提示胶囊（Peek）与下沉卡片（Expanded）。
 *
 * Compact 仅兼容保留；Peek 从状态栏底部以下横向展示，
 * 整颗可点；Expanded 卡片再下沉隔开。窗口宿主只负责承载 Root；尺寸/圆角/透明度在 Compose 内动画。
 */
@Composable
fun TapRelayOverlay(
    state: TapRelayOverlayState,
    registeredApps: Map<String, RegisteredApp>,
    capsuleHeight: Dp,
    onOpen: () -> Unit,
    onCardDismiss: (NotifyMessage) -> Unit,
    onDismissAll: () -> Unit,
    onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
    onProcessWaiting: ((NotifyMessage) -> Unit)? = null,
    onContinue: ((NotifyMessage) -> Unit)? = null,
    debugEnabled: Boolean = false,
    dynamicEffectsEnabled: Boolean = true,
    screenGeometry: ScreenGeometry? = null
) {
    val phase = state.phase()
    if (phase == OverlayPhase.Hidden) return

    val configuration = LocalConfiguration.current
    val density = LocalDensity.current
    val screenWidthPx = with(density) { configuration.screenWidthDp.dp.roundToPx() }
    val compactCore = screenGeometry
        ?.let { OverlayPositionCalculator.compactCoreSizeDp(it).dp }
        ?: OverlayMetrics.COMPACT_MIN_SIZE_DP.dp
    val compactTouchWidth = screenGeometry
        ?.let { OverlayPositionCalculator.compactWidthDp(it).dp }
        ?: OverlayMetrics.TOUCH_MIN_WIDTH_DP.dp
    val compactTouchHeight = capsuleHeight
    val peekVisualHeight = screenGeometry
        ?.let { OverlayPositionCalculator.peekVisualHeightDp(it).dp }
        ?: OverlayMetrics.PEEK_VISUAL_MIN_HEIGHT_DP.dp
    val peekTopInset = screenGeometry
        ?.let { OverlayPositionCalculator.peekTopInsetDp(it).dp }
        ?: OverlayMetrics.PEEK_BELOW_STATUS_GAP_DP.dp
    val peekTouchHeight = screenGeometry
        ?.let { OverlayPositionCalculator.peekTouchHeightDp(it).dp }
        ?: maxOf(
            peekVisualHeight + OverlayMetrics.TOUCH_OVERHANG_DP.dp,
            OverlayMetrics.TOUCH_CLEAR_BELOW_STATUS_DP.dp
        )
    // 独立悬浮提示：从状态栏底部以下开始，触控壳向下延伸。
    val peekShellHeight = screenGeometry
        ?.let { OverlayPositionCalculator.peekShellHeightDp(it).dp }
        ?: (peekTopInset + peekTouchHeight)
    val peekMaxWidth = OverlayPositionCalculator
        .peekMaxWidthDp(screenWidthPx, density.density)
        .dp
    val expandedTopInset = screenGeometry
        ?.let { OverlayPositionCalculator.expandedTopInsetDp(it).dp }
        ?: OverlayMetrics.EXPANDED_BELOW_STATUS_GAP_DP.dp
    val expandedWidth = configuration.screenWidthDp.dp
    val eventCount = state.events().size.coerceIn(1, 5)
    val expandedContentHeight =
        OverlayCardHeight + OverlayStackRevealStep * (eventCount - 1) + 16.dp
    val expandedHeight = expandedTopInset + expandedContentHeight

    // Peek 视觉 Surface 自适应内容；这里只保存视觉宽度，透明触控壳额外扩展 18dp。
    var measuredPeekWidth by remember {
        mutableStateOf(OverlayMetrics.PEEK_PREFERRED_WIDTH_DP.dp)
    }
    val peekVisualWidth = measuredPeekWidth.coerceIn(
        OverlayMetrics.PEEK_MIN_WIDTH_DP.dp,
        peekMaxWidth
    )
    val peekTouchWidth = OverlayPositionCalculator
        .peekTouchWidthDp(peekVisualWidth.value)
        .dp
    val transition = updateTransition(targetState = phase, label = "overlayTransition")
    val shellWidth by transition.animateDp(
        transitionSpec = { IslandAnimations.MorphDp },
        label = "overlayWidth"
    ) { target ->
        when (target) {
            OverlayPhase.Compact -> compactTouchWidth
            // 触控壳比视觉胶囊更宽，整颗展示区（含左右圆角）都可点
            OverlayPhase.Peek -> peekTouchWidth
            OverlayPhase.Expanded -> expandedWidth
            OverlayPhase.Hidden -> compactTouchWidth
        }
    }
    val shellHeight by transition.animateDp(
        transitionSpec = { IslandAnimations.MorphDp },
        label = "overlayHeight"
    ) { target ->
        when (target) {
            OverlayPhase.Compact -> compactTouchHeight
            OverlayPhase.Peek -> peekShellHeight
            OverlayPhase.Expanded -> expandedHeight
            OverlayPhase.Hidden -> compactTouchHeight
        }
    }
    val visualWidth by transition.animateDp(
        transitionSpec = { IslandAnimations.MorphDp },
        label = "visualWidth"
    ) { target ->
        when (target) {
            OverlayPhase.Compact -> compactCore
            OverlayPhase.Peek -> peekVisualWidth
            OverlayPhase.Expanded -> expandedWidth
            OverlayPhase.Hidden -> compactCore
        }
    }
    val visualHeight by transition.animateDp(
        transitionSpec = { IslandAnimations.MorphDp },
        label = "visualHeight"
    ) { target ->
        when (target) {
            OverlayPhase.Compact -> compactCore
            OverlayPhase.Peek -> peekVisualHeight
            OverlayPhase.Expanded -> expandedContentHeight
            OverlayPhase.Hidden -> compactCore
        }
    }
    val corner by transition.animateDp(
        transitionSpec = { IslandAnimations.MorphDp },
        label = "overlayCorner"
    ) { target ->
        when (target) {
            OverlayPhase.Compact -> compactCore / 2
            OverlayPhase.Peek -> peekVisualHeight / 2
            OverlayPhase.Expanded -> 14.dp
            OverlayPhase.Hidden -> compactCore / 2
        }
    }
    val compactAlpha by transition.animateFloat(
        transitionSpec = { IslandAnimations.MorphFloat },
        label = "compactAlpha"
    ) { target ->
        if (target == OverlayPhase.Compact) 1f else 0f
    }
    val peekAlpha by transition.animateFloat(
        transitionSpec = { IslandAnimations.MorphFloat },
        label = "peekAlpha"
    ) { target ->
        if (target == OverlayPhase.Peek) 1f else 0f
    }
    val expandedAlpha by transition.animateFloat(
        transitionSpec = { IslandAnimations.MorphFloat },
        label = "expandedAlpha"
    ) { target ->
        if (target == OverlayPhase.Expanded) 1f else 0f
    }
    val peekContentAlpha by transition.animateFloat(
        transitionSpec = {
            tween(
                durationMillis = IslandAnimations.ContentFadeMs,
                delayMillis = if (initialState == OverlayPhase.Peek && targetState == OverlayPhase.Expanded) {
                    0
                } else {
                    IslandAnimations.ContentInDelayMs
                }
            )
        },
        label = "peekContentAlpha"
    ) { target ->
        if (target == OverlayPhase.Peek) 1f else 0f
    }
    val peekContentOffset by transition.animateDp(
        transitionSpec = {
            tween(
                durationMillis = IslandAnimations.ContentFadeMs,
                delayMillis = IslandAnimations.ContentInDelayMs
            )
        },
        label = "peekContentOffset"
    ) { target ->
        if (target == OverlayPhase.Peek) 0.dp else 4.dp
    }
    val islandBackground = appColors.IslandBackground
    val expandedScrim = appColors.OverlaySurface.copy(alpha = 0f)
    val shellColor by transition.animateColor(
        transitionSpec = { IslandAnimations.MorphColor },
        label = "shellColor"
    ) { target ->
        if (target == OverlayPhase.Expanded) expandedScrim else islandBackground
    }

    Box(
        modifier = Modifier.wrapContentWidth(Alignment.CenterHorizontally),
        contentAlignment = Alignment.TopCenter
    ) {
        val shellClickable = phase == OverlayPhase.Compact || phase == OverlayPhase.Peek
        val shellInteraction = remember { MutableInteractionSource() }

        // 触控壳：Peek 位于状态栏下方，整颗左中右可点。
        Box(
            modifier = Modifier
                .width(shellWidth)
                .height(shellHeight)
                .then(
                    if (shellClickable) {
                        Modifier.clickable(
                            interactionSource = shellInteraction,
                            indication = null,
                            onClick = onOpen
                        )
                    } else {
                        Modifier
                    }
                ),
            contentAlignment = Alignment.TopCenter
        ) {
            // Compact 兼容旧锚点；Peek/Expanded 各自使用状态栏下方 padding。
            if (compactAlpha > 0.02f) {
                Box(
                    modifier = Modifier
                        .width(visualWidth)
                        .height(visualHeight)
                        .clip(RoundedCornerShape(corner))
                        .background(if (compactAlpha > 0.02f) shellColor else Color.Transparent)
                        .then(
                            if (shellClickable) {
                                Modifier.clickable(
                                    interactionSource = remember { MutableInteractionSource() },
                                    indication = null,
                                    onClick = onOpen
                                )
                            } else {
                                Modifier
                            }
                        )
                )
                Box(
                    modifier = Modifier
                        .size(compactCore)
                        .graphicsLayer {
                            alpha = compactAlpha
                            scaleX = 0.94f + 0.06f * compactAlpha
                            scaleY = 0.94f + 0.06f * compactAlpha
                        }
                ) {
                    TapRelayIsland(
                        unread = state.events(),
                        registeredApps = registeredApps,
                        onOpen = onOpen,
                        capsuleHeight = compactCore,
                        drawChrome = false,
                        compact = true,
                        enableClick = false,
                        dynamicEffectsEnabled = dynamicEffectsEnabled
                    )
                }
            }

            // Peek：状态栏下方的独立提示，不覆盖摄像头或系统状态栏。
            if (peekAlpha > 0.02f) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = peekTopInset),
                    contentAlignment = Alignment.TopCenter
                ) {
                    Box(
                        modifier = Modifier
                            .wrapContentWidth()
                            .height(peekVisualHeight)
                            .graphicsLayer {
                                alpha = peekAlpha * peekContentAlpha
                                scaleX = 0.72f + 0.28f * peekAlpha
                                scaleY = 0.94f + 0.06f * peekAlpha
                                translationX = with(density) { peekContentOffset.toPx() }
                            }
                            .clickable(
                                interactionSource = remember { MutableInteractionSource() },
                                indication = null,
                                onClick = onOpen
                            )
                    ) {
                        TapRelayIsland(
                            unread = state.events(),
                            registeredApps = registeredApps,
                            onOpen = onOpen,
                            capsuleHeight = peekVisualHeight,
                            drawChrome = false,
                            maxWidth = peekMaxWidth,
                            compact = false,
                            enableClick = false,
                            dynamicEffectsEnabled = dynamicEffectsEnabled,
                            onVisualSizeChanged = { size ->
                                val width = with(density) { size.width.toDp() }
                                if (width != measuredPeekWidth) measuredPeekWidth = width
                            }
                        )
                    }
                }
            }

            if (expandedAlpha > 0.02f) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = expandedTopInset)
                        .graphicsLayer {
                            alpha = expandedAlpha
                            translationY = (1f - expandedAlpha) * 12f
                            scaleX = 0.96f + 0.04f * expandedAlpha
                            scaleY = 0.96f + 0.04f * expandedAlpha
                        }
                ) {
                    OverlayCards(
                        messages = state.events(),
                        registeredApps = registeredApps,
                        onCardDismiss = onCardDismiss,
                        onDismissAll = onDismissAll,
                        onResolveApproval = onResolveApproval,
                        onProcessWaiting = onProcessWaiting,
                        onContinue = onContinue,
                        playEntrance = false,
                        dynamicEffectsEnabled = dynamicEffectsEnabled
                    )
                }
            }
        }

        // Popup 不参与 WRAP_CONTENT 测量，避免 debug HUD 把 overlay 撑成近全屏。
        if (debugEnabled && screenGeometry != null) {
            val hudOffsetY = with(density) { (shellHeight + 8.dp).roundToPx() }
            Popup(
                alignment = Alignment.TopCenter,
                offset = IntOffset(0, hudOffsetY)
            ) {
                OverlayDebugHud(
                    screen = screenGeometry,
                    phase = phase,
                    shellWidth = shellWidth,
                    shellHeight = shellHeight,
                    visualWidth = visualWidth,
                    visualHeight = visualHeight
                )
            }
        }
    }
}

@Composable
private fun OverlayDebugHud(
    screen: ScreenGeometry,
    phase: OverlayPhase,
    shellWidth: Dp,
    shellHeight: Dp,
    visualWidth: Dp,
    visualHeight: Dp,
    modifier: Modifier = Modifier
) {
    val cutout = screen.cutout
    val hole = OverlayPositionCalculator.estimatedHoleSizePx(screen)
    val top = OverlayPositionCalculator.holeTopPx(screen)
    val lines = buildList {
        add("SCREEN ${screen.screenWidthPx}x${screen.screenHeightPx}")
        add("DENSITY ${"%.1f".format(screen.density)}")
        add("STATUS ${screen.statusBarHeightPx}px")
        if (cutout == null) {
            add("CUTOUT none")
        } else {
            add("CUTOUT x=${cutout.left} y=${cutout.top} w=${cutout.width} h=${cutout.height}")
            add("CUTOUT centerX=${cutout.centerX}")
        }
        add("PHASE $phase")
        add("HOLE ${hole}px top=${top}px")
        add("VISUAL ${visualWidth.value.toInt()}x${visualHeight.value.toInt()}dp")
        add("TOUCH ${shellWidth.value.toInt()}x${shellHeight.value.toInt()}dp")
    }
    androidx.compose.material3.Text(
        text = lines.joinToString("\n"),
        color = AppTokens.IslandDebugText,
        modifier = modifier
            .background(AppTokens.IslandDebugBackdrop, RoundedCornerShape(6.dp))
            .padding(horizontal = 8.dp, vertical = 6.dp),
        style = androidx.compose.material3.MaterialTheme.typography.labelSmall
    )
}
