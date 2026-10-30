package com.lan.notify.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Scaffold
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.zIndex
import com.lan.notify.ui.theme.appColors

/**
 * 主工作台壳层。三个页面保持在组合树中，切换底栏时只改变可见页面，
 * 从而保留各页面已有的 remember 状态和滚动位置。
 */
@Composable
fun WorkstationShell(
    active: AppDestination,
    onSelect: (AppDestination) -> Unit,
    task: @Composable () -> Unit,
    comfyUi: @Composable () -> Unit,
    device: @Composable () -> Unit
) {
    Box(Modifier.fillMaxSize()) {
        Scaffold(
            // 页面顶栏由 CompactTopAppBar 统一绘制状态栏 inset，
            // 外层壳层不能再次消费顶部安全区，否则会产生重复空白。
            contentWindowInsets = WindowInsets(0, 0, 0, 0),
            bottomBar = {
                WorkstationBottomBar(selected = active, onSelect = onSelect)
            },
            containerColor = appColors.Background
        ) { padding ->
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding)
            ) {
                PersistentSlot(AppDestination.TASK, active, task)
                PersistentSlot(AppDestination.COMFY_UI, active, comfyUi)
                PersistentSlot(AppDestination.DEVICE, active, device)
            }
        }
    }
}

@Composable
private fun PersistentSlot(
    destination: AppDestination,
    active: AppDestination,
    content: @Composable () -> Unit
) {
    val visible = destination == active
    Box(
        modifier = Modifier
            .fillMaxSize()
            .zIndex(if (visible) 1f else 0f)
            .alpha(if (visible) 1f else 0f)
            .then(if (visible) Modifier else Modifier.clearAndSetSemantics {})
    ) {
        content()
    }
}
