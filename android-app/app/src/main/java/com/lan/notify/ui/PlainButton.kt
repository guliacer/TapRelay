package com.lan.notify.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.ui.theme.appColors

/**
 * TapRelay 统一按钮组件。
 *
 * 样式约束（用户反馈 P023 / P066）：
 * - 圆角 12dp（小圆角，不再是全圆角胶囊）
 * - PRIMARY/SMALL：顶栏同款鼠尾草绿（TopBarSage）填充 + 深 sage 字，与顶栏统一
 * - SECONDARY：透明底 + sage 描边 + sage 字（不得出现文字背后的白色小横条）
 * - 高度 36dp（SMALL 28dp），一行多按钮不错位
 * - 按压缩放 0.975 + 阴影动画 + 触觉反馈
 *
 * 实现注意：
 * 不使用 Material3 Button / OutlinedButton。ColorOS 上 M3 按钮会在
 * 标签文字背后额外铺一层 Surface/白底，看起来像「文字后面的白色小横条」。
 * 这里用 Box + background/border + Text 自绘，背景色完全由我们控制。
 */
enum class PlainButtonVariant { PRIMARY, SECONDARY, SMALL }

/** 统一圆角 12dp，比胶囊形更克制，适合紧凑布局。 */
private val ButtonShape = RoundedCornerShape(12.dp)

/**
 * 按压缩放 + 透明度动画，对应 PixGo 的 pixgoPressDepth。
 */
@Composable
private fun Modifier.pixgoPressDepth(
    interactionSource: MutableInteractionSource,
    enabled: Boolean,
    pressedScale: Float = 0.975f,
    disabledAlpha: Float = 0.56f
): Modifier {
    val isPressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (enabled && isPressed) pressedScale else 1f,
        animationSpec = spring(dampingRatio = 0.74f, stiffness = 520f),
        label = "PlainButtonPressDepth"
    )
    val alpha by animateFloatAsState(
        targetValue = if (enabled) 1f else disabledAlpha,
        animationSpec = spring(dampingRatio = 0.86f, stiffness = 420f),
        label = "PlainButtonAlpha"
    )
    return graphicsLayer {
        scaleX = scale
        scaleY = scale
        this.alpha = alpha
    }
}

/**
 * 按压阴影动画，对应 PixGo 的 pixgoLiftedSurface。
 */
@Composable
private fun Modifier.pixgoLiftedSurface(
    interactionSource: MutableInteractionSource,
    enabled: Boolean,
    shape: Shape,
    defaultElevation: Float,
    pressedElevation: Float
): Modifier {
    val isPressed by interactionSource.collectIsPressedAsState()
    val elevation by animateFloatAsState(
        targetValue = if (enabled && isPressed) pressedElevation else defaultElevation,
        animationSpec = spring(dampingRatio = 0.78f, stiffness = 500f),
        label = "PlainButtonElevation"
    )
    return shadow(elevation.dp, shape, clip = false)
}

@Composable
fun PlainButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    variant: PlainButtonVariant = PlainButtonVariant.PRIMARY,
    enabled: Boolean = true
) {
    val haptic = LocalHapticFeedback.current
    val interactionSource = remember { MutableInteractionSource() }
    val shape = ButtonShape

    val height: Dp
    val horizontalPadding: Dp
    val fontSize: TextUnit
    val container: Color
    val content: Color
    val borderColor: Color?
    val defaultElevation: Float
    val pressedElevation: Float
    val pressedScale: Float

    when (variant) {
        PlainButtonVariant.PRIMARY -> {
            height = 36.dp
            horizontalPadding = 14.dp
            fontSize = 13.sp
            container = appColors.TopBarSage
            content = appColors.TopBarSageContent
            borderColor = null
            defaultElevation = 3f
            pressedElevation = 1f
            pressedScale = 0.975f
        }
        PlainButtonVariant.SECONDARY -> {
            height = 36.dp
            horizontalPadding = 14.dp
            fontSize = 13.sp
            // 透明底：页面/卡片背景直接透出，避免 M3 OutlinedButton 的白底横条
            container = Color.Transparent
            content = appColors.PrimarySage
            borderColor = appColors.PrimarySage
            defaultElevation = 0f
            pressedElevation = 0f
            pressedScale = 0.975f
        }
        PlainButtonVariant.SMALL -> {
            height = 28.dp
            horizontalPadding = 10.dp
            fontSize = 11.sp
            container = appColors.TopBarSage
            content = appColors.TopBarSageContent
            borderColor = null
            defaultElevation = 0f
            pressedElevation = 0f
            pressedScale = 0.96f
        }
    }

    val clickWithHaptic = {
        if (enabled) {
            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
            onClick()
        }
    }

    Box(
        modifier = modifier
            .height(height)
            .then(
                if (defaultElevation > 0f || pressedElevation > 0f) {
                    Modifier.pixgoLiftedSurface(
                        interactionSource,
                        enabled,
                        shape,
                        defaultElevation,
                        pressedElevation
                    )
                } else {
                    Modifier
                }
            )
            .pixgoPressDepth(interactionSource, enabled, pressedScale = pressedScale)
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
                enabled = enabled,
                interactionSource = interactionSource,
                indication = null,
                onClick = clickWithHaptic
            )
            .padding(horizontal = horizontalPadding),
        contentAlignment = Alignment.Center
    ) {
        // 纯 Text，不附加任何 background / Surface，杜绝文字背后白条
        Text(
            text = text,
            color = content,
            fontWeight = FontWeight.Medium,
            fontSize = fontSize,
            maxLines = 1,
            textAlign = TextAlign.Center
        )
    }
}
