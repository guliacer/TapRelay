package com.lan.notify.ui

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import com.lan.notify.data.AppUiState
import com.lan.notify.data.HostHistoryEntry
import com.lan.notify.ui.theme.appColors

/** 设备一级页面；内容复用原设备管理面板，避免两套设置逻辑分叉。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceScreen(
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
    onClearHostHistory: () -> Unit = {}
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("设备") },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = appColors.TopBarSage,
                    titleContentColor = appColors.TopBarSageContent
                )
            )
        },
        containerColor = appColors.Background
    ) { padding ->
        DeviceControlContent(
            modifier = Modifier.padding(padding),
            uiState = uiState,
            deviceName = deviceName,
            onDeviceNameChange = onDeviceNameChange,
            onServerHostChange = onServerHostChange,
            dynamicEffectsEnabled = dynamicEffectsEnabled,
            onDynamicEffectsChange = onDynamicEffectsChange,
            onScanQr = onScanQr,
            onConnect = onConnect,
            onDisconnect = onDisconnect,
            onSleep = onSleep,
            onShutdown = onShutdown,
            onOpenPermissionCheck = onOpenPermissionCheck,
            hostHistory = hostHistory,
            hostMasked = hostMasked,
            onHostMaskedChange = onHostMaskedChange,
            onClearHostHistory = onClearHostHistory
        )
    }
}
