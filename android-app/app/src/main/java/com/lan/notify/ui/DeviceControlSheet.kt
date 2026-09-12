package com.lan.notify.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.AppUiState
import com.lan.notify.data.ConnectionStatus
import com.lan.notify.data.LocalStore
import com.lan.notify.ui.theme.appColors

/** 首页首屏的连接结果摘要；配置和低频控制集中到设备管理面板。 */
@Composable
fun CompactConnectionStatus(
    uiState: AppUiState,
    deviceName: String,
    onOpenDeviceManagement: () -> Unit,
    onConnect: () -> Unit,
    onScanQr: () -> Unit
) {
    val statusColor = connectionStatusColor(uiState.connectionStatus)
    val showRecoveryActions = uiState.connectionStatus == ConnectionStatus.Disconnected ||
        uiState.connectionStatus == ConnectionStatus.Reconnecting
    val displayedName = deviceName.trim().ifBlank { LocalStore.DEFAULT_DEVICE_NAME }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            // 只有状态内容区域可点击，设备管理按钮是并列兄弟节点，避免点击冒泡。
            Box(
                modifier = Modifier
                    .weight(1f)
                    .clipWithBorder()
                    .clickable(onClick = onOpenDeviceManagement)
                    .padding(horizontal = 12.dp, vertical = 10.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        modifier = Modifier
                            .size(9.dp)
                            .background(statusColor, CircleShape)
                    )
                    Spacer(modifier = Modifier.size(10.dp))
                    Text(
                        text = displayedName,
                        modifier = Modifier.weight(1f),
                        color = appColors.TextPrimary,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Text(
                        text = uiState.connectionStatus.label,
                        color = statusColor,
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium
                    )
                }
            }
            Spacer(modifier = Modifier.size(8.dp))
            PlainButton(
                text = "设备管理",
                onClick = onOpenDeviceManagement,
                variant = PlainButtonVariant.SMALL
            )
        }

        if (showRecoveryActions) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                PlainButton(
                    text = "重新连接",
                    onClick = onConnect,
                    enabled = uiState.serverHost.isNotBlank(),
                    modifier = Modifier.weight(1f),
                    variant = PlainButtonVariant.PRIMARY
                )
                PlainButton(
                    text = "扫码连接",
                    onClick = onScanQr,
                    modifier = Modifier.weight(1f),
                    variant = PlainButtonVariant.SECONDARY
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceControlSheet(
    uiState: AppUiState,
    deviceName: String,
    onDeviceNameChange: (String) -> Unit,
    onServerHostChange: (String) -> Unit,
    dynamicEffectsEnabled: Boolean,
    onDynamicEffectsChange: (Boolean) -> Unit,
    onScanQr: () -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onSleep: () -> Unit,
    onShutdown: () -> Unit,
    onOpenPermissionCheck: () -> Unit,
    onDismiss: () -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(start = 20.dp, end = 20.dp, bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "设备管理",
                        color = appColors.TextPrimary,
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Text(
                        text = "连接保障和低频控制",
                        color = appColors.TextSecondary,
                        fontSize = 12.sp
                    )
                }
                TextButton(onClick = onDismiss) {
                    Text("完成", color = appColors.PrimarySage)
                }
            }

            DeviceSectionTitle("设备信息")
            OutlinedTextField(
                value = uiState.serverHost,
                onValueChange = onServerHostChange,
                modifier = Modifier.fillMaxWidth(),
                label = { Text("PC 地址（IP / 域名）") },
                supportingText = { Text("默认端口：1122") },
                singleLine = true,
            )
            OutlinedTextField(
                value = deviceName,
                onValueChange = onDeviceNameChange,
                modifier = Modifier.fillMaxWidth(),
                label = { Text("设备名称") },
                placeholder = { Text(LocalStore.DEFAULT_DEVICE_NAME) },
                supportingText = { Text("首页只显示此名称，最多 40 个字符") },
                singleLine = true
            )
            PlainButton(
                text = "扫码更新地址",
                onClick = onScanQr,
                modifier = Modifier.fillMaxWidth(),
                variant = PlainButtonVariant.SECONDARY
            )

            HorizontalDivider(color = appColors.BorderSoft)
            DeviceSectionTitle("连接管理")
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "消息通道",
                        color = appColors.TextPrimary,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium
                    )
                    Text(
                        text = connectionDetail(uiState.connectionStatus, uiState.serverHost),
                        color = connectionStatusColor(uiState.connectionStatus),
                        fontSize = 11.sp,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                ConnectionAction(
                    status = uiState.connectionStatus,
                    enabled = uiState.serverHost.isNotBlank(),
                    onConnect = onConnect,
                    onDisconnect = onDisconnect
                )
            }

            HorizontalDivider(color = appColors.BorderSoft)
            DeviceSectionTitle("系统设置")
            DeviceSettingRow(
                title = "权限状态",
                detail = "检查通知、悬浮窗和后台运行权限",
                action = {
                    PlainButton(
                        text = "检查权限",
                        onClick = onOpenPermissionCheck,
                        variant = PlainButtonVariant.SMALL
                    )
                }
            )
            DeviceSettingRow(
                title = "省电模式",
                detail = if (dynamicEffectsEnabled) "当前使用完整动效" else "已减少动效，降低耗电",
                action = {
                    Switch(
                        checked = !dynamicEffectsEnabled,
                        onCheckedChange = { powerSave ->
                            onDynamicEffectsChange(!powerSave)
                        }
                    )
                }
            )

            HorizontalDivider(color = appColors.BorderSoft)
            DeviceSectionTitle("PC 电源")
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PlainButton(
                    text = "让 PC 睡眠",
                    onClick = onSleep,
                    enabled = uiState.connectionStatus == ConnectionStatus.Connected,
                    modifier = Modifier.weight(1f),
                    variant = PlainButtonVariant.SECONDARY
                )
                PlainButton(
                    text = "关闭 PC",
                    onClick = onShutdown,
                    enabled = uiState.connectionStatus == ConnectionStatus.Connected,
                    modifier = Modifier.weight(1f),
                    variant = PlainButtonVariant.SECONDARY
                )
            }
        }
    }
}

@Composable
private fun DeviceSectionTitle(text: String) {
    Text(
        text = text,
        color = appColors.TextPrimary,
        fontSize = 13.sp,
        fontWeight = FontWeight.Bold
    )
}

@Composable
private fun DeviceSettingRow(
    title: String,
    detail: String,
    action: @Composable () -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                color = appColors.TextPrimary,
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium
            )
            Text(
                text = detail,
                color = appColors.TextSecondary,
                fontSize = 11.sp,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis
            )
        }
        Spacer(modifier = Modifier.size(8.dp))
        action()
    }
}

@Composable
private fun ConnectionAction(
    status: ConnectionStatus,
    enabled: Boolean,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit
) {
    when (status) {
        ConnectionStatus.Connected -> PlainButton(
            text = "断开连接",
            onClick = onDisconnect,
            variant = PlainButtonVariant.SECONDARY
        )
        ConnectionStatus.Connecting,
        ConnectionStatus.Reconnecting -> PlainButton(
            text = "重新连接",
            onClick = onConnect,
            enabled = enabled,
            variant = PlainButtonVariant.SECONDARY
        )
        ConnectionStatus.Disconnected -> PlainButton(
            text = "连接",
            onClick = onConnect,
            enabled = enabled,
            variant = PlainButtonVariant.PRIMARY
        )
    }
}

@Composable
private fun connectionStatusColor(status: ConnectionStatus): Color = when (status) {
    ConnectionStatus.Connected -> appColors.Success
    ConnectionStatus.Connecting,
    ConnectionStatus.Reconnecting -> appColors.OverlayWaiting
    ConnectionStatus.Disconnected -> appColors.Danger
}

private fun connectionDetail(status: ConnectionStatus, displayedHost: String): String {
    val host = displayedHost.trim().ifBlank { "未配置地址" }
    return when (status) {
        ConnectionStatus.Connected -> "$host · 消息通道正常"
        ConnectionStatus.Connecting -> "$host · 正在建立消息通道"
        ConnectionStatus.Reconnecting -> "$host · 正在尝试恢复消息通道"
        ConnectionStatus.Disconnected -> "$host · 连接后接收任务通知"
    }
}

@Composable
private fun Modifier.clipWithBorder(): Modifier =
    this
        .background(appColors.Surface, RoundedCornerShape(10.dp))
        .border(1.dp, appColors.BorderSoft, RoundedCornerShape(10.dp))
