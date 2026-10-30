package com.lan.notify.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lan.notify.data.AppUiState
import com.lan.notify.data.ConnectionStatus
import com.lan.notify.data.DEFAULT_PROJECT_VISIBLE_COUNT
import com.lan.notify.data.ListMode
import com.lan.notify.data.MessageTimeFormatter
import com.lan.notify.data.NotifyMessage
import com.lan.notify.R
import com.lan.notify.data.appGroupKey
import com.lan.notify.data.displayContent
import com.lan.notify.data.hasApprovalAction
import com.lan.notify.data.HostHistoryEntry
import com.lan.notify.data.isWaitingTask
import com.lan.notify.data.projectGroupSourceKey
import com.lan.notify.data.projectTitle
import com.lan.notify.data.taskUsageLabel
import com.lan.notify.ui.theme.ThemeMode
import com.lan.notify.ui.theme.appColors
import kotlinx.coroutines.launch
import kotlin.math.min

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    uiState: AppUiState,
    themeMode: ThemeMode,
    onThemeModeChange: (ThemeMode) -> Unit,
    listMode: ListMode,
    onListModeChange: (ListMode) -> Unit,
    projectExpandCounts: Map<String, Int>,
    onSetProjectExpandCount: (String, Int) -> Unit,
    onServerHostChange: (String) -> Unit,
    onScanQr: () -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onSleep: () -> Unit = {},
    onShutdown: () -> Unit = {},
    onOpenPermissionCheck: () -> Unit,
    onMarkMessageRead: (String) -> Unit,
    onMarkAllMessagesRead: () -> Unit,
    onClearHistory: () -> Unit,
    onResolveApproval: (NotifyMessage, String) -> Unit = { _, _ -> },
    onProcessWaiting: (NotifyMessage) -> Unit = {},
    dynamicEffectsEnabled: Boolean = true,
    onDynamicEffectsChange: (Boolean) -> Unit = {},
    deviceName: String = "",
    onDeviceNameChange: (String) -> Unit = {},
    hostHistory: List<HostHistoryEntry> = emptyList(),
    hostMasked: Boolean = false,
    onHostMaskedChange: (Boolean) -> Unit = {},
    onClearHostHistory: () -> Unit = {},
    onOpenDeviceManagement: () -> Unit = {},
    onOpenComfyUi: () -> Unit = {}
) {
    var showPersonalProjectsDialog by rememberSaveable { mutableStateOf(false) }

    Scaffold(
        // 工作台外层已经占位底栏与手势区，页面内不能再次消费系统 inset。
        contentWindowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
        topBar = {
            // 浅色主题顶栏用淡鼠尾草绿底 + 深鼠尾草绿字/图标（与品牌 PrimarySage 同系，
            // 色值取自 appColors.TopBarSage，与主按钮填充同源统一）：
            // 状态栏图标（时间/信号）以深色呈现，不再被白色透明顶栏吞没；深色主题保持深色顶栏。
            val isLightTopBar = themeMode != ThemeMode.DARK &&
                (themeMode != ThemeMode.SYSTEM || !isSystemInDarkTheme())
            val topBarContainer = if (isLightTopBar) appColors.TopBarSage else MaterialTheme.colorScheme.surface
            val topBarContent = if (isLightTopBar) appColors.TopBarSageContent else MaterialTheme.colorScheme.onSurface
            CompactTopAppBar(containerColor = topBarContainer) {
                TopAppBar(
                modifier = Modifier.fillMaxWidth().height(CompactTopAppBarHeight),
                windowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
                title = { Text("\u4efb\u52a1\u901a\u77e5") },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = topBarContainer,
                    titleContentColor = topBarContent,
                    actionIconContentColor = topBarContent
                ),
                actions = {
                    MoreActionsMenu(
                        themeMode = themeMode,
                        onToggleTheme = {
                            onThemeModeChange(
                                if (themeMode == ThemeMode.LIGHT) ThemeMode.DARK else ThemeMode.LIGHT
                            )
                        },
                        onOpenProjects = { showPersonalProjectsDialog = true },
                        onOpenPermissions = onOpenPermissionCheck,
                        tint = topBarContent,
                        modifier = Modifier.padding(end = 12.dp)
                    )
                }
                )
            }
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                // 底部由工作台 Scaffold 的底栏占位负责，页面本身不能再留一整段底部空白。
                .padding(horizontal = 16.dp)
                .padding(top = 16.dp)
        ) {
            CompactConnectionStatus(
                uiState = uiState,
                deviceName = deviceName,
                onOpenDeviceManagement = onOpenDeviceManagement,
                onConnect = onConnect,
                onScanQr = onScanQr
            )

            Spacer(modifier = Modifier.height(16.dp))
            HorizontalDivider()
            Spacer(modifier = Modifier.height(12.dp))

            val unreadCount = uiState.messages.count { !it.isRead }
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "\u6d88\u606f\u5386\u53f2",
                    style = MaterialTheme.typography.titleMedium
                )
                Spacer(modifier = Modifier.width(8.dp))
                // 鍙屾寮忔ā寮忓垏鎹細澶栧眰 PrimarySageSoft 娴呭簳锛屾縺娲绘 PrimarySage 娣卞簳鐧藉瓧銆?
                // 涓ょ娴呰壊鑳屾櫙鎸囩ず褰撳墠婵€娲绘ā寮忥紝涓?娑堟伅鍘嗗彶"鏍囬绱ц创渚夸簬鎿嶄綔銆?
                Row(
                    modifier = Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .background(appColors.PrimarySageSoft)
                        .padding(2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    ListMode.values().forEach { mode ->
                        val active = listMode == mode
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(6.dp))
                                .background(if (active) appColors.PrimarySage else Color.Transparent)
                                .clickable { onListModeChange(mode) }
                                .padding(horizontal = 10.dp, vertical = 4.dp)
                        ) {
                            Text(
                                text = mode.label,
                                fontSize = 11.sp,
                                fontWeight = if (active) FontWeight.Bold else FontWeight.Normal,
                                color = if (active) Color.White else appColors.TextSecondary
                            )
                        }
                    }
                }
                Spacer(modifier = Modifier.weight(1f))
                if (unreadCount > 0) {
                    Text(
                        text = "$unreadCount \u6761\u672a\u8bfb",
                        color = appColors.UnreadAccent,
                        fontWeight = FontWeight.Bold,
                        fontSize = 12.sp
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    PlainButton(
                        text = "\u5168\u90e8\u5df2\u8bfb",
                        onClick = onMarkAllMessagesRead,
                        variant = PlainButtonVariant.SMALL
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                }
                PlainButton(
                    text = "清空历史",
                    onClick = onClearHistory,
                    variant = PlainButtonVariant.SMALL,
                    enabled = uiState.messages.isNotEmpty()
                )
            }
            Spacer(modifier = Modifier.height(8.dp))

            if (uiState.messages.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    Card(
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(16.dp),
                        colors = CardDefaults.cardColors(
                            containerColor = appColors.Surface
                        ),
                        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
                    ) {
                        Column(
                            modifier = Modifier.padding(20.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            Text(
                                text = "暂无任务消息",
                                style = MaterialTheme.typography.titleMedium,
                                fontWeight = FontWeight.SemiBold,
                                color = appColors.TextPrimary
                            )
                            Text(
                                text = if (uiState.connectionStatus == ConnectionStatus.Connected) {
                                    "PC 已连接，新的任务完成后会自动出现在这里"
                                } else {
                                    "先连接 PC，任务完成后通知会自动出现在这里"
                                },
                                color = appColors.TextSecondary,
                                style = MaterialTheme.typography.bodySmall
                            )
                            if (uiState.connectionStatus != ConnectionStatus.Connected) {
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    PlainButton(
                                        text = "重新连接",
                                        onClick = onConnect,
                                        enabled = uiState.serverHost.isNotBlank(),
                                        variant = PlainButtonVariant.PRIMARY
                                    )
                                    PlainButton(
                                        text = "扫码连接",
                                        onClick = onScanQr,
                                        variant = PlainButtonVariant.SECONDARY
                                    )
                                }
                            }
                        }
                    }
                }
            } else {
                val listState = rememberLazyListState()
                val scope = rememberCoroutineScope()
                // 鍒楄〃鎸夋椂闂村€掑簭锛屾渶鏂版秷鎭湪 index 0銆傚彧鍦?鏈€鏂伴偅鏉℃崲浜?鏃跺洖鍒伴《閮紝
                // 杩欐牱鏂版秷鎭埌杈句竴瀹氳兘鐪嬪埌锛岃€岀敤鎴锋墜鍔ㄧ炕鏃ф秷鎭椂涓嶄細琚腑鏂€?
                val newestId = uiState.messages.first().id
                var lastNewestId by remember { mutableStateOf<String?>(null) }
                LaunchedEffect(newestId) {
                    if (lastNewestId != newestId) {
                        lastNewestId = newestId
                        listState.animateScrollToItem(0)
                    }
                }
                // 妯″紡鍒囨崲鏃朵袱绉嶅垪琛ㄥ唴瀹逛笉鍚岋紝鍥炲埌椤堕儴璁╂渶鏂版秷鎭珛鍗冲彲瑙併€?
                var lastMode by remember { mutableStateOf(listMode) }
                LaunchedEffect(listMode) {
                    if (lastMode != listMode) {
                        lastMode = listMode
                        listState.animateScrollToItem(0)
                    }
                }

                // 椤圭洰妯″紡涓嬪乏渚у渾鐐瑰鑸墍闇€锛氬垎缁勯『搴忥紙涓?LazyColumn items 涓€鑷达級銆?
                val groupOrder = if (listMode == ListMode.PROJECT) {
                    uiState.messages
                        .map { it.appGroupKey() }
                        .distinct()
                } else {
                    emptyList()
                }
                // 杩斿洖椤堕儴鎸夐挳锛氬垪琛ㄥ凡鍚戜笅婊氬姩鏃舵樉绀恒€俤erivedStateOf 閬垮厤姣忓抚閲嶇粍銆?
                val showBackToTop by remember {
                    derivedStateOf {
                        listState.firstVisibleItemIndex > 0 || listState.firstVisibleItemScrollOffset > 600
                    }
                }

                Box(modifier = Modifier.weight(1f)) {
                    if (listMode == ListMode.TIME) {
                        LazyColumn(
                            state = listState,
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(bottom = 8.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            items(
                                items = uiState.messages,
                                key = { it.id }
                            ) { message ->
                                MessageRow(
                                    message = message,
                                    registeredApps = uiState.registeredApps,
                                    onMarkMessageRead = onMarkMessageRead,
                                    onResolveApproval = onResolveApproval,
                                    onProcessWaiting = onProcessWaiting
                                )
                            }
                        }
                    } else {
                        // 椤圭洰妯″紡锛氭寜 source 鍒嗙粍銆俶essages 鏈韩鍊掑簭锛実roupBy 淇濇寔鎻掑叆椤哄簭锛?
                        // 鍥犳 distinct() 鍚庣涓€涓氨鏄叏灞€鏈€鏂版秷鎭墍灞炴笭閬擄紝缁勫唴 value 涔熶繚鎸佹渶鏂板湪鍓嶃€?
                        LazyColumn(
                            state = listState,
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(
                                start = 16.dp,
                                end = 4.dp,
                                bottom = 12.dp
                            ),
                            verticalArrangement = Arrangement.spacedBy(16.dp)
                        ) {
                            items(
                                items = groupOrder,
                                key = { it }
                            ) { groupKey ->
                                val groupMessages = uiState.messages.filter { msg ->
                                    msg.appGroupKey() == groupKey
                                }
                                ProjectGroupCard(
                                    groupKey = groupKey,
                                    messages = groupMessages,
                                    registeredApps = uiState.registeredApps,
                                    expandCount = projectExpandCounts[groupKey]
                                        ?: DEFAULT_PROJECT_VISIBLE_COUNT,
                                    onExpandCountChange = { count ->
                                        onSetProjectExpandCount(groupKey, count)
                                    },
                                      onMarkMessageRead = onMarkMessageRead,
                                      onResolveApproval = onResolveApproval,
                                     onProcessWaiting = onProcessWaiting
                                )
                            }
                        }

                        // 宸︿晶椤圭洰鍦嗙偣瀵艰埅锛氭瘡涓湁娑堟伅鐨勯」鐩竴涓渾鐐癸紝棰滆壊瀵瑰簲椤圭洰涓昏壊锛?
                        // 鐐瑰嚮璺宠浆鍒拌椤圭洰鍒嗙粍銆備粎椤圭洰妯″紡鏄剧ず锛屽瀭鐩村眳涓偓娴湪宸﹁竟缂樸€?
                        // 鏃犳秷鎭」鐩笉鏄剧ず锛岄伩鍏嶈瑙夊櫔闊炽€?
                        if (groupOrder.isNotEmpty()) {
                            ProjectDots(
                                items = groupOrder.mapIndexed { index, groupKey -> index to groupKey },
                                registeredApps = uiState.registeredApps,
                                modifier = Modifier.align(Alignment.CenterStart),
                                onClick = { index ->
                                    scope.launch { listState.animateScrollToItem(index) }
                                }
                            )
                        }
                    }

                    // 杩斿洖椤堕儴锛氬彸渚у瀭鐩村眳涓紙涓庡乏渚у渾鐐瑰鑸绉帮級锛岄伩寮€搴曢儴鎸夐挳鍖恒€?
                    if (showBackToTop) {
                        BackToTopButton(
                            onClick = {
                                scope.launch { listState.animateScrollToItem(0) }
                            },
                            modifier = Modifier.align(Alignment.CenterEnd)
                        )
                    }
                }
            }
        }
    }

    if (showPersonalProjectsDialog) {
        PersonalProjectsDialog(onDismiss = { showPersonalProjectsDialog = false })
    }

}

/** A visible action in a history card. */
private data class HistoryAction(
    val text: String,
    val onClick: () -> Unit
)

/**
 * Fixed-width history action row; every visible action shares the slot equally.
 * Keep at least two slots so a single action never becomes wider than the
 * two-action rows beside it.
 */
@Composable
private fun HistoryActionRow(
    actions: List<HistoryAction>,
    modifier: Modifier = Modifier.fillMaxWidth(),
    minSlots: Int = 2
) {
    if (actions.isEmpty()) return
    val slotCount = maxOf(actions.size, minSlots)
    Row(
        modifier = modifier,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        if (actions.size == 1) {
            Spacer(modifier = Modifier.weight(0.5f))
        }
        actions.forEach { action ->
            PlainButton(
                text = action.text,
                onClick = action.onClick,
                modifier = Modifier.weight(1f),
                variant = PlainButtonVariant.SMALL
            )
        }
        repeat(slotCount - actions.size - if (actions.size == 1) 1 else 0) {
            Spacer(
                modifier = Modifier
                    .weight(1f)
                    .height(28.dp)
            )
        }
        if (actions.size == 1) {
            Spacer(modifier = Modifier.weight(0.5f))
        }
    }
}

/**
 * Compact vertical action rail used inside history cards. Every action gets the
 * same width and height, so a card with one, two, or three actions keeps the
 * rail aligned with the cards around it.
 */
@Composable
private fun HistoryActionColumn(
    actions: List<HistoryAction>,
    modifier: Modifier = Modifier.fillMaxWidth()
) {
    if (actions.isEmpty()) return
    Column(
        modifier = modifier,
        verticalArrangement = Arrangement.spacedBy(5.dp)
    ) {
        actions.forEach { action ->
            PlainButton(
                text = action.text,
                onClick = action.onClick,
                modifier = Modifier.fillMaxWidth(),
                variant = PlainButtonVariant.SMALL
            )
        }
    }
}

/**
 * 鏃堕棿妯″紡涓嬬殑鍗曟潯娑堟伅琛岋細鍘?ListItem 琛屼负锛屾湭璇婚珮浜?+ 鏍囪鎸夐挳銆?
 */
@Composable
private fun MessageRow(
    message: NotifyMessage,
    registeredApps: Map<String, com.lan.notify.data.RegisteredApp>,
    onMarkMessageRead: (String) -> Unit,
    onResolveApproval: (NotifyMessage, String) -> Unit,
    onProcessWaiting: (NotifyMessage) -> Unit
) {
    val appLabel = appPresentation(message.source, registeredApps).label
    val approval = message.hasApprovalAction()
    val waiting = message.isWaitingTask() && !approval && !message.isRead
    val actions = when {
        approval -> listOf(
            HistoryAction("允许") { onResolveApproval(message, "allow") },
            HistoryAction("拒绝") { onResolveApproval(message, "deny") }
        )
        waiting -> buildList {
            add(HistoryAction("处理") { onProcessWaiting(message) })
            if (!message.isRead) add(HistoryAction("关闭") { onMarkMessageRead(message.id) })
        }
        else -> buildList {
            if (!message.isRead) add(HistoryAction("关闭") { onMarkMessageRead(message.id) })
        }
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(
                if (message.isRead) MaterialTheme.colorScheme.surface else appColors.UnreadBackground
            )
            .clickable { onMarkMessageRead(message.id) },
        verticalAlignment = Alignment.Top
    ) {
        Column(
            modifier = Modifier
                .width(96.dp)
                .padding(start = 10.dp, top = 12.dp, bottom = 12.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(
                modifier = Modifier.size(40.dp),
                contentAlignment = Alignment.Center
            ) {
                AppIcon(
                    source = message.source,
                    registeredApps = registeredApps,
                    modifier = Modifier.size(36.dp)
                )
                if (!message.isRead) {
                    Box(
                        modifier = Modifier
                            .align(Alignment.BottomEnd)
                            .size(9.dp)
                            .background(appColors.UnreadAccent, CircleShape)
                    )
                }
            }
            if (actions.isNotEmpty()) {
                Spacer(modifier = Modifier.height(8.dp))
                HistoryActionColumn(actions)
            }
        }
        Column(
            modifier = Modifier
                .weight(1f)
                .padding(start = 10.dp, end = 12.dp, top = 12.dp, bottom = 12.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            Text(
                text = message.projectTitle(appLabel),
                color = appColors.TextPrimary,
                fontSize = 16.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = message.displayContent(),
                color = appColors.TextSecondary,
                fontSize = 12.sp,
                fontWeight = FontWeight.Normal,
                lineHeight = 19.sp,
                maxLines = 4,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = message.taskUsageLabel(),
                color = appColors.TextTertiary,
                fontSize = 11.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = if (message.isRead) {
                    "\u5df2\u8bfb \u00b7 ${MessageTimeFormatter.format(message.time)}"
                } else {
                    "\u672a\u8bfb \u00b7 ${MessageTimeFormatter.format(message.time)}"
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (message.isRead) appColors.TextTertiary else appColors.UnreadAccent
            )
        }
    }
}

/**
 * 椤圭洰妯″紡涓嬬殑鍒嗙粍鍗＄墖锛氬甫闃村奖鐨?Card锛岄《閮ㄤ负娓犻亾 header锛堝瓧姣嶅窘鏍?+ 娓犻亾鍚?+ 娑堟伅鏁帮級锛?
 * 鍐呴儴鐢?Column 渚濇鍒楀嚭璇ユ笭閬撶殑娑堟伅锛堟渶鏂板湪涓婏紝鍥犱负 messages 鏈韩鍊掑簭锛夈€?
 * 榛樿浠呭睍绀烘渶鏂?[DEFAULT_PROJECT_VISIBLE_COUNT] 鏉★紝閫氳繃搴曢儴鎸夐挳鎸夐渶灞曞紑/鎶樺彔锛?
 * 灞曞紑鐘舵€佺敱 [expandCount] 璁板繂锛堟寔涔呭寲鍦?LocalStore锛夈€?
 * 缁勯棿鐢卞灞?LazyColumn 鐨?spacedBy(16.dp) 鎻愪緵鏄庢樉绌虹櫧闅旂銆?
 */
@Composable
private fun ProjectGroupCard(
    groupKey: String,
    messages: List<NotifyMessage>,
    registeredApps: Map<String, com.lan.notify.data.RegisteredApp>,
    expandCount: Int,
    onExpandCountChange: (Int) -> Unit,
    onMarkMessageRead: (String) -> Unit,
    onResolveApproval: (NotifyMessage, String) -> Unit = { _, _ -> },
    onProcessWaiting: (NotifyMessage) -> Unit = {}
) {
    if (messages.isEmpty()) return
    val sourceKey = projectGroupSourceKey(groupKey)
    val presentation = appPresentation(sourceKey, registeredApps)
    val appName = presentation.label
    val unreadCount = messages.count { !it.isRead }
    // 瀹為檯灞曠ず鏉℃暟锛歵ake min(expandCount, size)銆俥xpandCount 鍙兘鏄?Int.MAX_VALUE锛堟煡鐪嬪叏閮級銆?
    val visibleCount = min(expandCount.coerceAtLeast(1), messages.size)
    val visibleMessages = messages.take(visibleCount)
    val hasMore = messages.size > visibleCount
    val isExpanded = expandCount > DEFAULT_PROJECT_VISIBLE_COUNT && visibleCount > DEFAULT_PROJECT_VISIBLE_COUNT
    val haptic = LocalHapticFeedback.current

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .shadow(
                elevation = 2.dp,
                shape = RoundedCornerShape(12.dp),
                clip = false
            ),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp)
    ) {
        Column(modifier = Modifier.padding(14.dp)) {
            // 娓犻亾 header锛氬瓧姣嶅窘鏍?+ 娓犻亾鍚?+ 娑堟伅鏁?+ 鏈鏁?
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(24.dp)
                ) {
                    AppIcon(
                        source = sourceKey,
                        registeredApps = registeredApps,
                        modifier = Modifier.size(24.dp)
                    )
                }
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = appName,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    color = appColors.TextPrimary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "${messages.size} \u6761",
                    fontSize = 11.sp,
                    color = appColors.TextSecondary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (unreadCount > 0) {
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        text = "\u00b7 $unreadCount \u672a\u8bfb",
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        color = appColors.UnreadAccent
                    )
                }
            }
            Spacer(modifier = Modifier.height(10.dp))
            HorizontalDivider(color = appColors.BorderSoft)
            Spacer(modifier = Modifier.height(6.dp))

            // 缁勫唴娑堟伅锛氭渶鏂板湪涓娿€傛瘡鏉′箣闂寸暀 6dp 闂撮殧锛屾湭璇荤敤娴呭簳楂樹寒 + 琛屾湯"鏍囪"鎸夐挳銆?
            visibleMessages.forEachIndexed { index, message ->
                if (index > 0) Spacer(modifier = Modifier.height(6.dp))
                val hasApprovalAction = message.hasApprovalAction()
                val needsProcessing = message.isWaitingTask() && !hasApprovalAction && !message.isRead
                val actions = when {
                    hasApprovalAction -> listOf(
                        HistoryAction("允许") { onResolveApproval(message, "allow") },
                        HistoryAction("拒绝") { onResolveApproval(message, "deny") }
                    )
                    needsProcessing -> buildList {
                        add(HistoryAction("处理") { onProcessWaiting(message) })
                        if (!message.isRead) add(HistoryAction("关闭") { onMarkMessageRead(message.id) })
                    }
                    else -> buildList {
                        if (!message.isRead) add(HistoryAction("关闭") { onMarkMessageRead(message.id) })
                    }
                }
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp))
                        .background(
                            if (message.isRead) Color.Transparent else appColors.UnreadBackground
                        )
                        .clickable { onMarkMessageRead(message.id) }
                        .heightIn(min = 126.dp)
                        .padding(horizontal = 10.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.Top
                ) {
                    Box(
                        modifier = Modifier
                            .padding(top = 6.dp)
                            .size(8.dp)
                            .background(
                                color = if (message.isRead) Color.Transparent else appColors.UnreadAccent,
                                shape = CircleShape
                            )
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Column(
                        modifier = Modifier.weight(1f),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Text(
                            text = message.projectTitle(presentation.label),
                            color = appColors.TextPrimary,
                            fontSize = 16.sp,
                            fontWeight = FontWeight.Bold,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            text = message.displayContent(),
                            color = appColors.TextSecondary,
                            fontSize = 12.sp,
                            fontWeight = FontWeight.Normal,
                            lineHeight = 19.sp,
                            maxLines = 4,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            text = message.taskUsageLabel(),
                            color = appColors.TextTertiary,
                            fontSize = 11.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            text = if (message.isRead) {
                                "\u5df2\u8bfb \u00b7 ${MessageTimeFormatter.format(message.time)}"
                            } else {
                                "\u672a\u8bfb \u00b7 ${MessageTimeFormatter.format(message.time)}"
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = if (message.isRead) appColors.TextTertiary else appColors.UnreadAccent
                        )
                        if (actions.isNotEmpty()) {
                            Spacer(modifier = Modifier.height(2.dp))
                            HistoryActionRow(actions = actions)
                        }
                    }
                }
            }

            // 灞曞紑/鎶樺彔鎸夐挳鍖猴細榛樿鎶樺彔鏃?[鏌ョ湅6鏉? 鏌ョ湅鍏ㄩ儴]锛涘睍寮€鍚庤拷鍔?[鎶樺彔]銆?
            // 涓夋寜閽瑙夋潈閲嶉€掑噺锛氬～鍏?> 娴呭簳 > 鎻忚竟锛屾槑纭尯鍒嗗姛鑳藉樊寮傘€?
            if (hasMore || isExpanded) {
                Spacer(modifier = Modifier.height(10.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (hasMore) {
                        // "鏌ョ湅6鏉?锛氭祬搴?+ 鎻忚竟锛屾瑕佹搷浣滐紙澧為噺鍔犺浇6鏉★級
                        ProjectExpandButton(
                            text = "\u67e5\u770b6\u6761",
                            onClick = {
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                onExpandCountChange(visibleCount + 6)
                            },
                            modifier = Modifier.weight(1f),
                            style = ProjectExpandButtonStyle.SOFT
                        )
                        // "鏌ョ湅鍏ㄩ儴"锛氬～鍏?sage + 鐧藉瓧锛屼富瑕佹搷浣滐紙鍏ㄩ噺鍔犺浇锛?
                        ProjectExpandButton(
                            text = "\u67e5\u770b\u5168\u90e8",
                            onClick = {
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                onExpandCountChange(Int.MAX_VALUE)
                            },
                            modifier = Modifier.weight(1f),
                            style = ProjectExpandButtonStyle.FILLED
                        )
                    }
                    if (isExpanded) {
                        // "鎶樺彔"锛氭弿杈癸紝鏀惰捣鍔ㄤ綔锛屼粎灞曞紑鍚庡嚭鐜?
                        ProjectExpandButton(
                            text = "\u6298\u53e0",
                            onClick = {
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                onExpandCountChange(DEFAULT_PROJECT_VISIBLE_COUNT)
                            },
                            modifier = if (hasMore) Modifier.weight(1f) else Modifier,
                            style = ProjectExpandButtonStyle.OUTLINED
                        )
                    }
                }
            }
        }
    }
}

/** 椤圭洰鍗＄墖灞曞紑/鎶樺彔鎸夐挳鐨勮瑙夐鏍硷紝鏉冮噸閫掑噺浠ュ尯鍒嗗姛鑳姐€?*/
private enum class ProjectExpandButtonStyle { SOFT, FILLED, OUTLINED }

/**
 * 椤圭洰鍗＄墖涓撶敤灏忔寜閽細28dp 楂橈紝鍦嗚 8dp锛岀粺涓€杈规锛屼笁绉嶉鏍煎尯鍒嗗姛鑳姐€?
 * - SOFT锛氭祬 sage 搴?+ sageStrong 瀛楋紙娆¤锛氭煡鐪?鏉★級
 * - FILLED锛氶《鏍忓悓妗?TopBarSage 濉厖 + 娣?sage 瀛楋紙涓昏锛氭煡鐪嬪叏閮?涓庨《鏍忕粺涓?* - OUTLINED锛氶€忔槑搴?+ sage 瀛楋紙鏀惰捣锛氭姌鍙狅級
 */
@Composable
private fun ProjectExpandButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    style: ProjectExpandButtonStyle
) {
    val container = when (style) {
        ProjectExpandButtonStyle.SOFT -> appColors.PrimarySageSoft
        ProjectExpandButtonStyle.FILLED -> appColors.TopBarSage
        ProjectExpandButtonStyle.OUTLINED -> Color.Transparent
    }
    val content = when (style) {
        ProjectExpandButtonStyle.SOFT -> appColors.PrimarySageStrong
        ProjectExpandButtonStyle.FILLED -> appColors.TopBarSageContent
        ProjectExpandButtonStyle.OUTLINED -> appColors.PrimarySage
    }
    val borderColor = when (style) {
        ProjectExpandButtonStyle.SOFT -> appColors.PrimarySage
        ProjectExpandButtonStyle.FILLED -> appColors.TopBarSageContent.copy(alpha = 0.35f)
        ProjectExpandButtonStyle.OUTLINED -> appColors.PrimarySage
    }
    Box(
        modifier = modifier
            .height(28.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(container)
            .border(1.dp, borderColor, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = text,
            color = content,
            fontSize = 11.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1
        )
    }
}

/**
 * 宸︿晶椤圭洰鍦嗙偣瀵艰埅銆傛瘡涓湁娑堟伅鐨勯」鐩竴涓渾鐐癸紝棰滆壊瀵瑰簲 Channel 涓昏壊锛?
 * 鐐瑰嚮璺宠浆鍒拌椤圭洰鍒嗙粍銆?
 *
 * 瑙嗚涓?FullScreenAlertActivity / OverlayCards 鐨勬笭閬撳渾鐐逛竴鑷达細
 * 绾壊鍦嗙偣銆佹棤鑳屾櫙銆佹棤杈规銆佹棤闃村奖锛屼笉閬尅涓嬫柟鍗＄墖鍐呭銆?
 * 鍦嗙偣闂磋窛 12dp锛堚墺8px 瑙勮寖锛夛紝姣忓渾鐐?32dp 鐐瑰嚮鐑尯锛岃瑙夊昂瀵?12dp銆?
 * 鏃犺儗鏅鍣ㄤ繚璇佷笉閬尅涓嬫柟鍗＄墖鍐呭銆?
 */
@Composable
private fun ProjectDots(
    items: List<Pair<Int, String>>,
    registeredApps: Map<String, com.lan.notify.data.RegisteredApp>,
    onClick: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    if (items.isEmpty()) return
    val haptic = LocalHapticFeedback.current
    val listState = rememberLazyListState()
    val canScrollBackward by remember { derivedStateOf { listState.canScrollBackward } }
    val canScrollForward by remember { derivedStateOf { listState.canScrollForward } }

    Box(
        modifier = modifier
            .offset(x = (-12).dp)
            .padding(start = 8.dp)
            .width(40.dp)
            .heightIn(max = 260.dp),
        contentAlignment = Alignment.Center
    ) {
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(vertical = 4.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            items(items, key = { it.first }) { (index, groupKey) ->
                val sourceKey = projectGroupSourceKey(groupKey)
                val presentation = appPresentation(sourceKey, registeredApps)
                Box(
                    modifier = Modifier
                        .size(32.dp)
                        .clickable {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            onClick(index)
                        },
                    contentAlignment = Alignment.Center
                ) {
                    // The rail intentionally keeps its compact circular markers;
                    // full-size brand artwork is reserved for cards and alerts.
                    Box(
                        modifier = Modifier
                            .size(26.dp)
                            .background(presentation.color, CircleShape)
                            .padding(4.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        AppIcon(
                            source = sourceKey,
                            registeredApps = registeredApps,
                            modifier = Modifier.size(18.dp)
                        )
                    }
                }
            }
        }

        if (canScrollBackward) {
            Box(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .height(12.dp)
                    .background(appColors.Surface.copy(alpha = 0.72f))
            )
        }
        if (canScrollForward) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .height(12.dp)
                    .background(appColors.Surface.copy(alpha = 0.72f))
            )
        }
    }
}

/**
 * 每个已知项目使用正式 Logo；仅无法识别的来源使用 TapRelay 兜底图标。
 *
 * 注意：TapRelay 不能用 R.mipmap.ic_launcher，mipmap-anydpi-v26 下的
 * ic_launcher.xml 是 adaptive-icon XML，painterResource() 只支持
 * VectorDrawable 与 PNG/JPG/WEBP，加载 adaptive-icon 会抛
 * IllegalArgumentException 导致 App 闪退（详见 09-问题与优化记录-内容.md P029）。
 * 这里改用 PNG 资源 ic_taprelay_logo.png（从 pc-app\TapRelay.ico 提取的彩色应用图标）。
 */
/**
 * 杩斿洖椤堕儴鎮诞鎸夐挳锛氬彸渚у瀭鐩村眳涓紙涓庡乏渚у渾鐐瑰鑸绉帮級锛屼粎鍚戜笂绠ご鍥炬爣銆?
 * 鏃犺儗鏅€侀紶灏捐崏缁?+ 40% 閫忔槑搴︼紝瑙嗚涓嶆樉鐪间絾鍙偣鍑汇€?
 * 32dp 鐐瑰嚮鐑尯淇濊瘉鍙搷浣滄€э紝鍥炬爣 14dp 缂╁皬瑙嗚鍗犳瘮銆?
 */
@Composable
private fun BackToTopButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val haptic = LocalHapticFeedback.current
    // Use the theme surface background for the arrow so both theme variants keep contrast.
    val iconColor = appColors.Background
    Box(
        modifier = modifier
            .padding(end = 8.dp)
            .size(32.dp)
            .shadow(3.dp, CircleShape)
            .clip(CircleShape)
            .background(appColors.PrimarySageStrong)
            .clickable {
                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                onClick()
            },
        contentAlignment = Alignment.Center
    ) {
        Canvas(modifier = Modifier.size(14.dp)) {
            val stroke = 1.8.dp.toPx()
            val w = size.width
            val h = size.height
            // 鍚戜笂绠ご锛歏 瀛椾笂鍗?+ 绔栫嚎涓嬪崐
            drawLine(
                color = iconColor,
                start = Offset(w * 0.22f, h * 0.55f),
                end = Offset(w * 0.5f, h * 0.27f),
                strokeWidth = stroke,
                cap = StrokeCap.Round
            )
            drawLine(
                color = iconColor,
                start = Offset(w * 0.5f, h * 0.27f),
                end = Offset(w * 0.78f, h * 0.55f),
                strokeWidth = stroke,
                cap = StrokeCap.Round
            )
            drawLine(
                color = iconColor,
                start = Offset(w * 0.5f, h * 0.32f),
                end = Offset(w * 0.5f, h * 0.74f),
                strokeWidth = stroke,
                cap = StrokeCap.Round
            )
        }
    }
}
