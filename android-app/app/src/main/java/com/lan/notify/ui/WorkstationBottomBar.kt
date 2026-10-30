package com.lan.notify.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.lan.notify.ui.theme.appColors

/** 底栏内容高度；手势区由 navigationBars inset 另行保留。 */
val CompactBottomBarHeight = 56.dp

/**
 * 类似微信的持久底栏。它只负责展示当前选中项并向上层发出切换事件，
 * 页面状态由工作台壳层持有。
 */
@Composable
fun WorkstationBottomBar(
    selected: AppDestination,
    onSelect: (AppDestination) -> Unit,
    modifier: Modifier = Modifier
) {
    val density = LocalDensity.current
    val navigationBarHeight = with(density) {
        WindowInsets.navigationBars.getBottom(this).toDp()
    }
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(appColors.Surface)
    ) {
        NavigationBar(
            modifier = Modifier.fillMaxWidth().height(CompactBottomBarHeight),
            windowInsets = WindowInsets(0, 0, 0, 0),
            containerColor = appColors.Surface,
            tonalElevation = 0.dp
        ) {
            AppDestination.entries.forEach { destination ->
                val isSelected = selected == destination
                NavigationBarItem(
                    selected = isSelected,
                    onClick = { onSelect(destination) },
                    icon = {
                        DestinationIcon(
                            destination = destination,
                            selected = isSelected
                        )
                    },
                    label = { Text(destination.label) },
                    alwaysShowLabel = true,
                    colors = NavigationBarItemDefaults.colors(
                        selectedIconColor = appColors.OnPrimary,
                        selectedTextColor = appColors.PrimarySageStrong,
                        indicatorColor = appColors.PrimarySage,
                        unselectedIconColor = appColors.TextSecondary,
                        unselectedTextColor = appColors.TextSecondary
                    )
                )
            }
        }
        Spacer(Modifier.fillMaxWidth().height(navigationBarHeight))
    }
}

@Composable
private fun DestinationIcon(
    destination: AppDestination,
    selected: Boolean
) {
    val tint = if (selected) appColors.OnPrimary else appColors.TextSecondary
    Canvas(
        modifier = Modifier
            .size(24.dp)
            .semantics { contentDescription = destination.contentDescription }
    ) {
        val stroke = 1.8.dp.toPx()
        when (destination) {
            AppDestination.TASK -> {
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(size.width * 0.16f, size.height * 0.18f),
                    size = Size(size.width * 0.68f, size.height * 0.64f),
                    cornerRadius = CornerRadius(4.dp.toPx()),
                    style = Stroke(width = stroke)
                )
                drawLine(
                    color = tint,
                    start = Offset(size.width * 0.32f, size.height * 0.42f),
                    end = Offset(size.width * 0.68f, size.height * 0.42f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round
                )
                drawLine(
                    color = tint,
                    start = Offset(size.width * 0.32f, size.height * 0.62f),
                    end = Offset(size.width * 0.58f, size.height * 0.62f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round
                )
            }

            AppDestination.COMFY_UI -> {
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(size.width * 0.16f, size.height * 0.16f),
                    size = Size(size.width * 0.68f, size.height * 0.68f),
                    cornerRadius = CornerRadius(5.dp.toPx()),
                    style = Stroke(width = stroke)
                )
                drawCircle(
                    color = tint,
                    radius = size.minDimension * 0.12f,
                    center = Offset(size.width * 0.5f, size.height * 0.5f),
                    style = Stroke(width = stroke)
                )
                drawLine(
                    color = tint,
                    start = Offset(size.width * 0.5f, size.height * 0.22f),
                    end = Offset(size.width * 0.5f, size.height * 0.36f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round
                )
                drawLine(
                    color = tint,
                    start = Offset(size.width * 0.5f, size.height * 0.64f),
                    end = Offset(size.width * 0.5f, size.height * 0.78f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round
                )
            }

            AppDestination.DEVICE -> {
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(size.width * 0.25f, size.height * 0.12f),
                    size = Size(size.width * 0.5f, size.height * 0.76f),
                    cornerRadius = CornerRadius(4.dp.toPx()),
                    style = Stroke(width = stroke)
                )
                drawLine(
                    color = tint,
                    start = Offset(size.width * 0.39f, size.height * 0.25f),
                    end = Offset(size.width * 0.61f, size.height * 0.25f),
                    strokeWidth = stroke,
                    cap = StrokeCap.Round
                )
                drawCircle(
                    color = tint,
                    radius = size.minDimension * 0.06f,
                    center = Offset(size.width * 0.5f, size.height * 0.73f)
                )
            }
        }
    }
}
