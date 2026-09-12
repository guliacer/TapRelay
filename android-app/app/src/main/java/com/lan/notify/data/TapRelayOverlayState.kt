package com.lan.notify.data

/**
 * 灵动岛悬浮层（overlay）的单一状态机。
 *
 * 同一个 Window 内通过 [TapRelayOverlayState] 描述可见档位：
 * 横向短提示胶囊（Peek）与展开卡片（Expanded）。
 * 挖孔正中的极小 Compact 会被摄像头盖住，**不再作为入场默认态**；
 * [OverlayExpansion.Compact] 仅保留兼容，reducer 会落到 Peek。
 * 只有 [Hidden] 才真正移除窗口。
 *
 * 纯 Kotlin、无 Android/Compose 依赖，便于单元测试覆盖状态转换规则。
 */
sealed interface TapRelayOverlayState {
    /** 无未读消息，窗口应被移除。 */
    data object Hidden : TapRelayOverlayState

    /**
     * 历史：贴合摄像头挖孔的极小岛。真机上会被挖孔/状态栏盖住，已不再入场。
     * 保留类型以免破坏旧测试/序列化路径；[deriveOverlayState] 不会产出此态。
     */
    data class Compact(val events: List<NotifyMessage>) : TapRelayOverlayState

    /** 默认入场：摄像头位置的短提示胶囊（状态点 + 来源图标 + 未读总数）。 */
    data class Peek(val events: List<NotifyMessage>) : TapRelayOverlayState

    /** 点击胶囊后的卡片列表，负责「信息 + 操作」。 */
    data class Expanded(val events: List<NotifyMessage>) : TapRelayOverlayState
}

/** 用户点击推进的展开档位，独立于消息列表。默认 [Peek]。 */
enum class OverlayExpansion { Compact, Peek, Expanded }

/** 状态机的离散阶段，便于宿主窗口做纯阶段判断（如只有 Hidden 才 removeView）。 */
enum class OverlayPhase { Hidden, Compact, Peek, Expanded }

fun TapRelayOverlayState.phase(): OverlayPhase = when (this) {
    TapRelayOverlayState.Hidden -> OverlayPhase.Hidden
    is TapRelayOverlayState.Compact -> OverlayPhase.Compact
    is TapRelayOverlayState.Peek -> OverlayPhase.Peek
    is TapRelayOverlayState.Expanded -> OverlayPhase.Expanded
}

/** 状态携带的未读事件（Hidden 为空）。 */
fun TapRelayOverlayState.events(): List<NotifyMessage> = when (this) {
    TapRelayOverlayState.Hidden -> emptyList()
    is TapRelayOverlayState.Compact -> events
    is TapRelayOverlayState.Peek -> events
    is TapRelayOverlayState.Expanded -> events
}

/** 默认 Peek；点击一次进入 Expanded。Compact 兼容值同样直接进卡片。 */
fun OverlayExpansion.advance(): OverlayExpansion = when (this) {
    OverlayExpansion.Compact, OverlayExpansion.Peek -> OverlayExpansion.Expanded
    OverlayExpansion.Expanded -> OverlayExpansion.Expanded
}

/**
 * 纯函数 reducer：根据「当前未读消息 + 用户展开档位」计算下一个 overlay 状态。
 *
 * - 无未读 → 永远 [TapRelayOverlayState.Hidden]（关闭最后一张卡后胶囊消失）。
 * - 有未读 + Compact/Peek → [TapRelayOverlayState.Peek]（默认横向胶囊，取消极小岛入场）。
 * - 有未读 + Expanded → [TapRelayOverlayState.Expanded]。
 *
 * [expansion] 是用户的点击意图，独立于消息列表；新消息到来只刷新事件列表，
 * 不会把已展开的 Expanded 抢回 Peek。消息不得自动标已读或自动 Hidden。
 */
fun deriveOverlayState(
    unread: List<NotifyMessage>,
    expansion: OverlayExpansion
): TapRelayOverlayState {
    if (unread.isEmpty()) return TapRelayOverlayState.Hidden
    return when (expansion) {
        OverlayExpansion.Compact, OverlayExpansion.Peek -> TapRelayOverlayState.Peek(unread)
        OverlayExpansion.Expanded -> TapRelayOverlayState.Expanded(unread)
    }
}

/**
 * 折叠胶囊的「前景」消息：待确认（waiting + requiresAction）优先级最高，
 * 否则取最新一条未读。返回 null 表示没有可展示的消息。
 */
fun overlayForeground(unread: List<NotifyMessage>): NotifyMessage? =
    unread.firstOrNull { it.requiresAction && it.status == NotifyMessage.STATUS_WAITING }
        ?: unread.firstOrNull()

/** 当前未读里是否存在需要在通知栏执行动作的等待消息。 */
fun hasPendingApproval(unread: List<NotifyMessage>): Boolean =
    unread.any { it.requiresAction && it.status == NotifyMessage.STATUS_WAITING }
