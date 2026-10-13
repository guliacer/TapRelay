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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.AppUiState
import com.lan.notify.data.ConnectionStatus
import com.lan.notify.data.HostHistoryEntry
import com.lan.notify.data.LocalStore
import com.lan.notify.data.MAX_HOST_HISTORY
import com.lan.notify.data.MessageTimeFormatter
import com.lan.notify.data.hostHistoryKey
import com.lan.notify.data.maskIp
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
    hostHistory: List<HostHistoryEntry> = emptyList(),
    hostMasked: Boolean = false,
    onHostMaskedChange: (Boolean) -> Unit = {},
    onClearHostHistory: () -> Unit = {},
    onDismiss: () -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    // 历史地址列表页与设备管理主页面共用同一个面板，用换页而不是嵌套第二个
    // ModalBottomSheet：双层 scrim 会叠压，返回键也会被外层 onDismissRequest 先吃掉。
    var showHostHistory by remember { mutableStateOf(false) }
    var confirmClearHistory by remember { mutableStateOf(false) }

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
            if (showHostHistory) {
                HostHistorySection(
                    entries = hostHistory,
                    currentHost = uiState.serverHost,
                    masked = hostMasked,
                    onBack = { showHostHistory = false },
                    onPick = { host ->
                        onServerHostChange(host)
                        onConnect()
                        onDismiss()
                    },
                    onRequestClear = { confirmClearHistory = true }
                )
                return@Column
            }

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
            // 脱敏开启时输入框必须只读：否则 onValueChange 会把 `1**.1**.*.3*` 这种
            // 掩码串当成真实地址写回 serverHost，直接污染连接配置。
            OutlinedTextField(
                value = if (hostMasked) maskIp(uiState.serverHost) else uiState.serverHost,
                onValueChange = onServerHostChange,
                modifier = Modifier.fillMaxWidth(),
                label = { Text("PC 地址（IP / 域名）") },
                supportingText = { Text("默认端口：1122") },
                singleLine = true,
                readOnly = hostMasked
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
            DeviceSettingRow(
                title = "历史地址",
                detail = if (hostHistory.isEmpty()) {
                    "连接成功后会自动记录"
                } else {
                    "最近使用的 ${hostHistory.size} 个地址"
                },
                action = {
                    PlainButton(
                        text = "查看",
                        onClick = { showHostHistory = true },
                        variant = PlainButtonVariant.SMALL
                    )
                }
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
                        // 同一屏上地址栏已脱敏，状态行不能还是明文，否则「隐藏地址」形同虚设。
                        // 这里只影响展示：下面的 enabled 判断仍用真实 serverHost，
                        // 掩码串 `1**.1**.*.3*` 不会被当成有效地址参与逻辑。
                        text = connectionDetail(
                            status = uiState.connectionStatus,
                            displayedHost = if (hostMasked) maskIp(uiState.serverHost) else uiState.serverHost
                        ),
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
            DeviceSettingRow(
                title = "隐藏地址",
                detail = if (hostMasked) {
                    "地址已脱敏，历史地址与系统通知同样脱敏"
                } else {
                    "地址明文显示，便于编辑与核对"
                },
                action = {
                    Switch(
                        checked = hostMasked,
                        onCheckedChange = onHostMaskedChange
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

    if (confirmClearHistory) {
        AlertDialog(
            onDismissRequest = { confirmClearHistory = false },
            title = { Text("清空历史地址") },
            text = { Text("将删除全部历史地址记录，当前正在使用的地址不受影响。") },
            confirmButton = {
                TextButton(
                    onClick = {
                        onClearHostHistory()
                        confirmClearHistory = false
                    }
                ) {
                    Text("清空", color = appColors.Danger)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmClearHistory = false }) {
                    Text("取消", color = appColors.TextSecondary)
                }
            }
        )
    }
}

/**
 * 历史地址列表页。
 *
 * 排版复用与设备管理主页面相同的标题、分隔线与行内卡片，不引入新的组件类型。
 * 这里用 Column 顺序渲染而不是 LazyColumn：外层已经有 verticalScroll，
 * 再套一个纵向可滚动组件会因无限高度约束直接崩溃，上限 10 条也用不上懒加载。
 */
@Composable
private fun HostHistorySection(
    entries: List<HostHistoryEntry>,
    currentHost: String,
    masked: Boolean,
    onBack: () -> Unit,
    onPick: (String) -> Unit,
    onRequestClear: () -> Unit
) {
    // 用去重键而不是字符串比较，否则 ws:// 前缀写法会让"当前"标记漏标。
    val currentKey = if (currentHost.isBlank()) "" else hostHistoryKey(currentHost)

    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        PlainButton(
            text = "返回",
            onClick = onBack,
            variant = PlainButtonVariant.SMALL
        )
        Spacer(modifier = Modifier.size(10.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = "历史地址",
                color = appColors.TextPrimary,
                fontSize = 20.sp,
                fontWeight = FontWeight.Bold
            )
            Text(
                text = "最近使用的排在最前，最多保留 $MAX_HOST_HISTORY 条",
                color = appColors.TextSecondary,
                fontSize = 12.sp
            )
        }
    }

    if (entries.isEmpty()) {
        // 写入时机是"连接成功"，所以新装用户首次进来必然是空的，文案必须说清原因。
        Text(
            text = "暂无历史地址，连接成功后会自动记录",
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 24.dp),
            color = appColors.TextSecondary,
            fontSize = 13.sp,
            textAlign = TextAlign.Center
        )
    } else {
        entries.forEach { entry ->
            HostHistoryRow(
                host = if (masked) maskIp(entry.host) else entry.host,
                detail = MessageTimeFormatter.formatHostUsedAt(entry.lastUsedAt),
                isCurrent = currentKey.isNotBlank() && hostHistoryKey(entry.host) == currentKey,
                onPick = { onPick(entry.host) }
            )
        }

        HorizontalDivider(color = appColors.BorderSoft)
        PlainButton(
            text = "清空历史",
            onClick = onRequestClear,
            modifier = Modifier.fillMaxWidth(),
            variant = PlainButtonVariant.SECONDARY
        )
    }
}

/** 单条历史地址：主行地址，副行最近使用时间；正在使用的地址右侧标「当前」。 */
@Composable
private fun HostHistoryRow(
    host: String,
    detail: String,
    isCurrent: Boolean,
    onPick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clipWithBorder()
            .clickable(onClick = onPick)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = host,
                color = appColors.TextPrimary,
                fontSize = 13.sp,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = detail,
                color = appColors.TextSecondary,
                fontSize = 11.sp,
                maxLines = 1
            )
        }
        if (isCurrent) {
            Spacer(modifier = Modifier.size(8.dp))
            Text(
                text = "当前",
                color = appColors.Success,
                fontSize = 11.sp,
                fontWeight = FontWeight.Medium
            )
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
