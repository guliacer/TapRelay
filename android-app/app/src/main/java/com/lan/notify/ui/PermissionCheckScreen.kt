package com.lan.notify.ui

import android.content.Intent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.lan.notify.util.PermissionChecker
import com.lan.notify.util.PermissionChecker.PermissionStatus
import com.lan.notify.ui.theme.appColors

/**
 * 权限检测界面。
 *
 * 展示应用所需的所有权限项及其当前状态，为未授权权限提供跳转设置入口。
 * 每次 ON_RESUME 时自动刷新权限状态，确保从设置页返回后状态准确。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PermissionCheckScreen(
    onBack: () -> Unit,
    onOpenAutoStartSettings: () -> Unit,
    onboarding: Boolean = false
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current

    var permissionItems by remember {
        mutableStateOf(PermissionChecker.checkAll(context))
    }

    // ON_RESUME 时刷新权限状态（从设置页返回后状态会变化）
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                permissionItems = PermissionChecker.checkAll(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // 首次引导模式：未授权的项排在前面，提醒用户按顺序处理。
    val displayItems = remember(permissionItems, onboarding) {
        if (onboarding) {
            permissionItems.sortedBy { if (it.status == PermissionStatus.GRANTED) 1 else 0 }
        } else {
            permissionItems
        }
    }

    Scaffold(
        topBar = {
            CompactTopAppBar {
                TopAppBar(
                modifier = Modifier.fillMaxWidth().height(CompactTopAppBarHeight),
                windowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
                title = { Text(if (onboarding) "首次使用设置" else "权限检测") },
                navigationIcon = {
                    TextButton(onClick = onBack) {
                        Text(
                            if (onboarding) "开始使用" else "返回",
                            color = appColors.TextPrimary
                        )
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = appColors.TopBarSage,
                    titleContentColor = appColors.TopBarSageContent,
                    navigationIconContentColor = appColors.TopBarSageContent
                )
                )
            }
        }
    ) { padding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(
                horizontal = 16.dp,
                vertical = 12.dp
            ),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            if (onboarding) {
                item {
                    Text(
                        text = "按顺序完成授权，任务通知才能正常送达；可随时跳过，之后在主页\"权限\"中继续。",
                        style = MaterialTheme.typography.bodySmall,
                        color = appColors.TextSecondary,
                        modifier = Modifier.padding(bottom = 4.dp)
                    )
                }
            }
            items(displayItems, key = { it.key }) { item ->
                PermissionItemRow(
                    item = item,
                    onOpenSettings = {
                        if (item.key == "auto_start") {
                            onOpenAutoStartSettings()
                        } else {
                            item.settingsIntent?.let { factory ->
                                runCatching { context.startActivity(factory()) }
                            }
                        }
                    }
                )
            }

            item {
                Text(
                    text = "授权后返回本页会自动刷新",
                    modifier = Modifier.padding(top = 16.dp, start = 4.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = appColors.TextTertiary
                )
            }
        }
    }
}

@Composable
private fun PermissionItemRow(
    item: PermissionChecker.PermissionItem,
    onOpenSettings: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp),
        colors = CardDefaults.cardColors(containerColor = appColors.Surface),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // 状态图标
            StatusIcon(status = item.status)

            // 标题 + 描述
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = item.title,
                    style = MaterialTheme.typography.bodyLarge,
                    fontWeight = FontWeight.Medium,
                    color = appColors.TextPrimary
                )
                Text(
                    text = item.description,
                    style = MaterialTheme.typography.bodySmall,
                    color = appColors.TextSecondary
                )
            }

            // 状态文字 + 跳转按钮。已授权项也提供「管理」入口，方便用户到系统设置里
            // 关闭授权（如关闭悬浮窗权限后灵动岛/悬浮卡即降级为普通通知）。
            StatusText(status = item.status)
            if (item.settingsIntent != null && item.status != PermissionStatus.NOT_APPLICABLE) {
                PlainButton(
                    text = if (item.status == PermissionStatus.GRANTED) "管理" else "去设置",
                    onClick = onOpenSettings,
                    variant = PlainButtonVariant.SMALL
                )
            }
        }
    }
}

@Composable
private fun StatusIcon(status: PermissionStatus) {
    val (symbol, bgColor) = when (status) {
        PermissionStatus.GRANTED -> "\u2713" to appColors.Success
        PermissionStatus.DENIED -> "\u2715" to appColors.Danger
        PermissionStatus.NOT_APPLICABLE -> "\u2014" to appColors.TextTertiary.copy(alpha = 0.3f)
    }
    Box(
        modifier = Modifier
            .size(24.dp)
            .clip(CircleShape)
            .background(bgColor),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = symbol,
            color = Color.White,
            style = MaterialTheme.typography.bodySmall,
            fontWeight = FontWeight.Bold
        )
    }
}

@Composable
private fun StatusText(status: PermissionStatus) {
    val (text, color) = when (status) {
        PermissionStatus.GRANTED -> "已授权" to appColors.Success
        PermissionStatus.DENIED -> "未授权" to appColors.Danger
        PermissionStatus.NOT_APPLICABLE -> "不适用" to appColors.TextTertiary
    }
    Text(
        text = text,
        style = MaterialTheme.typography.bodySmall,
        color = color
    )
}
