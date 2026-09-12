package com.lan.notify.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/**
 * 全局主题入口。
 *
 * @param themeMode 用户选择的主题模式，默认 [ThemeMode.LIGHT]（浅色）。
 * @param content 应用内容。
 */
@Composable
fun NotifyTheme(
    themeMode: ThemeMode = ThemeMode.LIGHT,
    content: @Composable () -> Unit
) {
    val isDark = when (themeMode) {
        ThemeMode.LIGHT -> false
        ThemeMode.DARK -> true
        ThemeMode.SYSTEM -> isSystemInDarkTheme()
    }

    val appColors = if (isDark) DarkAppColors else LightAppColors
    val colorScheme = if (isDark) DarkColorScheme else LightColorScheme

    // 状态栏图标颜色跟随主题：浅色（sage 顶栏/白底）用深色图标，
    // 深色主题用浅色图标，保证状态栏时间/信号图标始终可见。
    // 注意：Compose 也可能挂在 Service 上下文（如 WebSocketService 的悬浮窗），
    // 此时没有 window，必须安全转换，否则 ClassCastException 闪退。
    val view = LocalView.current
    if (!view.isInEditMode) {
        val activity = view.context as? Activity
        if (activity != null) {
            SideEffect {
                WindowCompat.getInsetsController(activity.window, view)
                    .isAppearanceLightStatusBars = !isDark
            }
        }
    }

    CompositionLocalProvider(LocalAppColors provides appColors) {
        MaterialTheme(
            colorScheme = colorScheme,
            typography = AppTypography,
            content = content
        )
    }
}
