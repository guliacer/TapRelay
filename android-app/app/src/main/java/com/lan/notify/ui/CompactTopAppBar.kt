package com.lan.notify.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.statusBars
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.lan.notify.ui.theme.appColors

/** 顶栏仅保留一行标题和操作按钮的内容高度，系统状态栏由外层单独承载。 */
val CompactTopAppBarHeight = 48.dp

/**
 * 工作台统一的紧凑顶栏容器。
 *
 * 状态栏背景由外层容器绘制，Material3 顶栏只负责 48dp 的内容区，
 * 避免默认 TopAppBar 把状态栏 inset 叠加到标准高度上。
 */
@Composable
fun CompactTopAppBar(
    containerColor: Color = appColors.TopBarSage,
    content: @Composable () -> Unit
) {
    val density = LocalDensity.current
    val statusBarHeight = with(density) {
        WindowInsets.statusBars.getTop(this).toDp()
    }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(containerColor)
    ) {
        Box(Modifier.fillMaxWidth().height(statusBarHeight))
        Box(Modifier.fillMaxWidth().height(CompactTopAppBarHeight)) {
            content()
        }
    }
}
