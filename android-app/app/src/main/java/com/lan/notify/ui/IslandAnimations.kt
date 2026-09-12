package com.lan.notify.ui

import androidx.compose.animation.core.SpringSpec
import androidx.compose.animation.core.spring
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp

/**
 * Compact ↔ Expanded 共用弹簧。WindowManager 不参与尺寸动画。
 */
object IslandAnimations {
    const val MorphDamping = 0.8f
    const val MorphStiffness = 450f
    const val ContentFadeMs = 180
    const val ContentInDelayMs = 80

    val MorphFloat: SpringSpec<Float> = spring(
        dampingRatio = MorphDamping,
        stiffness = MorphStiffness
    )

    val MorphDp: SpringSpec<Dp> = spring(
        dampingRatio = MorphDamping,
        stiffness = MorphStiffness
    )

    val MorphColor: SpringSpec<Color> = spring(
        dampingRatio = MorphDamping,
        stiffness = MorphStiffness
    )
}
