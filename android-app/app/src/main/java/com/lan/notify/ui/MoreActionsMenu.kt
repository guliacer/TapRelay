package com.lan.notify.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.lan.notify.ui.theme.ThemeMode
import com.lan.notify.ui.theme.appColors

/** 任务页低频操作入口，避免多个孤立图标挤占顶栏。 */
@Composable
fun MoreActionsMenu(
    themeMode: ThemeMode,
    onToggleTheme: () -> Unit,
    onOpenProjects: () -> Unit,
    onOpenPermissions: () -> Unit,
    tint: Color,
    modifier: Modifier = Modifier
) {
    var expanded by remember { mutableStateOf(false) }

    Box(modifier = modifier) {
        Box(
            modifier = Modifier
                .size(36.dp)
                .clickable { expanded = true }
                .semantics { contentDescription = "更多操作" },
            contentAlignment = Alignment.Center
        ) {
            Canvas(Modifier.size(24.dp)) {
                val x = size.width / 2f
                drawCircle(tint, radius = 1.7.dp.toPx(), center = Offset(x, size.height * 0.28f))
                drawCircle(tint, radius = 1.7.dp.toPx(), center = Offset(x, size.height * 0.5f))
                drawCircle(tint, radius = 1.7.dp.toPx(), center = Offset(x, size.height * 0.72f))
            }
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            containerColor = appColors.Surface
        ) {
            DropdownMenuItem(
                text = {
                    Text(if (themeMode == ThemeMode.DARK) "切换浅色模式" else "切换深色模式")
                },
                onClick = {
                    onToggleTheme()
                    expanded = false
                }
            )
            DropdownMenuItem(
                text = { Text("个人项目") },
                onClick = {
                    onOpenProjects()
                    expanded = false
                }
            )
            DropdownMenuItem(
                text = { Text("权限检测") },
                onClick = {
                    onOpenPermissions()
                    expanded = false
                }
            )
        }
    }
}
