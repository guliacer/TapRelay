package com.lan.notify.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.dp
import com.lan.notify.ui.theme.ThemeMode
import com.lan.notify.ui.theme.appColors

private const val LIGHT_MODE_PATH =
    "M12,3 L12,5 M12,19 L12,21 M4.2,4.2 L5.6,5.6 " +
        "M18.4,18.4 L19.8,19.8 M3,12 L5,12 M19,12 L21,12 " +
        "M4.2,19.8 L5.6,18.4 M18.4,5.6 L19.8,4.2 " +
        "M12,16 C14.2,16 16,14.2 16,12 C16,9.8 14.2,8 12,8 " +
        "C9.8,8 8,9.8 8,12 C8,14.2 9.8,16 12,16 Z"

private const val DARK_MODE_PATH =
    "M18.5,15.5 C17.1,16.5 15.5,17 13.7,17 C9.8,17 6.7,13.9 6.7,10 " +
        "C6.7,8.2 7.3,6.5 8.3,5.2 C5.2,6 3,8.8 3,12 C3,17 7,21 12,21 " +
        "C15.2,21 18.1,19.3 19.7,16.7 C19.3,16.4 18.9,16 18.5,15.5 Z"

private val LightModeIcon = themeIcon("LightMode", LIGHT_MODE_PATH)
private val DarkModeIcon = themeIcon("DarkMode", DARK_MODE_PATH)

/**
 * Toggles between the light and dark themes.
 *
 * PC and Android share PixGo's 24 x 24 LightMode / DarkMode paths,
 * 1.5 stroke width, round caps, and round joins.
 */
@Composable
fun ThemeIconButton(
    themeMode: ThemeMode,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    tint: Color? = null
) {
    val haptic = LocalHapticFeedback.current
    val icon = if (themeMode == ThemeMode.LIGHT) LightModeIcon else DarkModeIcon

    Box(
        modifier = modifier
            .size(28.dp)
            .clickable {
                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                onClick()
            },
        contentAlignment = Alignment.Center
    ) {
        Icon(
            imageVector = icon,
            contentDescription = if (themeMode == ThemeMode.LIGHT) "\u5207\u6362\u5230\u6df1\u8272\u6a21\u5f0f" else "\u5207\u6362\u5230\u6d45\u8272\u6a21\u5f0f",
            modifier = Modifier.size(20.dp),
            tint = tint ?: appColors.PrimarySage
        )
    }
}

private fun themeIcon(name: String, pathData: String): ImageVector =
    ImageVector.Builder(
        name = name,
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f
    ).apply {
        addPath(
            pathData = PathParser().parsePathString(pathData).toNodes(),
            pathFillType = PathFillType.NonZero,
            name = name,
            fill = null,
            stroke = SolidColor(Color.Black),
            strokeLineWidth = 1.5f,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round
        )
    }.build()
