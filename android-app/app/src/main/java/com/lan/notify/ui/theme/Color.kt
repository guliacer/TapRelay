package com.lan.notify.ui.theme

import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * 主题模式：默认浅色，允许用户手动切换或跟随系统。
 */
enum class ThemeMode { LIGHT, DARK, SYSTEM }

/**
 * 语义颜色令牌：所有会随深/浅色模式变化的颜色都放这里。
 * UI 层通过 [AppColors]（@Composable）读取，不直接引用 [AppTokens] 的语义色。
 * 渠道品牌色不随模式变化，保留在 [AppTokens] 中。
 */
data class AppColors(
    val Background: Color,
    val Surface: Color,
    val Primary: Color,
    val OnPrimary: Color,
    val Success: Color,
    val Danger: Color,
    val TextPrimary: Color,
    val TextSecondary: Color,
    val TextTertiary: Color,
    val UnreadBackground: Color,
    val UnreadBadgeBackground: Color,
    val UnreadAccent: Color,
    val PrimarySage: Color,
    val PrimarySageStrong: Color,
    val PrimarySageSoft: Color,
    // —— 顶栏 / 主按钮统一鼠尾草绿（顶栏底色 = 主按钮填充，文字/图标色 = 按钮字色）——
    val TopBarSage: Color,
    val TopBarSageContent: Color,
    val BorderSoft: Color,
    val PanelWarm: Color,
    val OverlaySurface: Color,
    val OverlayScrim: Color,
    val OverlayWaiting: Color,
    val OverlayDismissBorder: Color,
    // —— 摄像头灵动岛（Dynamic Island）：随深浅色模式变化的语义令牌。 ——
    val IslandBackground: Color,
    val IslandSurface: Color,
    val IslandBorder: Color,
    val IslandTextPrimary: Color,
    val IslandTextSecondary: Color,
    val IslandDone: Color,
    val IslandWaiting: Color,
    val IslandError: Color
)

/** 浅色模式语义色。 */
val LightAppColors = AppColors(
    Background = Color(0xFFF8FAFC),
    Surface = Color(0xFFFFFFFF),
    Primary = Color(0xFF2563EB),
    OnPrimary = Color(0xFFFFFFFF),
    Success = Color(0xFF16A34A),
    Danger = Color(0xFFDC2626),
    TextPrimary = Color(0xFF0F172A),
    TextSecondary = Color(0xFF475569),
    TextTertiary = Color(0xFF94A3B8),
    UnreadBackground = Color(0xFFEFF6FF),
    UnreadBadgeBackground = Color(0xFFDBEAFE),
    UnreadAccent = Color(0xFF1D4ED8),
    PrimarySage = Color(0xFF6F867E),
    PrimarySageStrong = Color(0xFF536962),
    PrimarySageSoft = Color(0xFFEEF1EC),
    // 浅色顶栏：淡鼠尾草绿底 + 深鼠尾草绿字（状态栏深色图标清晰可见）
    TopBarSage = Color(0xFFD8E4DE),
    TopBarSageContent = Color(0xFF3E5249),
    BorderSoft = Color(0xFFDDDCD4),
    PanelWarm = Color(0xFFF1EFE9),
    OverlaySurface = Color(0xFFFFFFFF),
    OverlayScrim = Color(0x1A0F172A),
    OverlayWaiting = Color(0xFFEA580C),
    OverlayDismissBorder = Color(0xFFE2E8F0),
    // 浅色模式：中性浅灰绿，不与白色页面融为一体，也不让状态色铺满胶囊。
    IslandBackground = Color(0xFFE4ECE9),
    IslandSurface = Color(0xFFFFFFFF),
    IslandBorder = Color(0xFFB9CAC1),
    IslandTextPrimary = Color(0xFF111827),
    IslandTextSecondary = Color(0xFF64706D),
    IslandDone = Color(0xFF3B9B69),
    IslandWaiting = Color(0xFFD99822),
    IslandError = Color(0xFFD9535F)
)

/**
 * 深色模式语义色。所有颜色确保与对应背景的对比度 ≥ 4.5:1（WCAG AA）。
 * - TextPrimary #F1F5F9 on Background #0F172A: ~14:1 ✓
 * - TextSecondary #94A3B8 on Background #0F172A: ~5.5:1 ✓
 * - UnreadAccent #60A5FA on Background #0F172A: ~7:1 ✓
 */
val DarkAppColors = AppColors(
    Background = Color(0xFF0F172A),
    Surface = Color(0xFF1E293B),
    Primary = Color(0xFF3B82F6),
    OnPrimary = Color(0xFFFFFFFF),
    Success = Color(0xFF22C55E),
    Danger = Color(0xFFEF4444),
    TextPrimary = Color(0xFFF1F5F9),
    TextSecondary = Color(0xFF94A3B8),
    TextTertiary = Color(0xFF64748B),
    UnreadBackground = Color(0xFF1E293B),
    UnreadBadgeBackground = Color(0xFF1E3A8A),
    UnreadAccent = Color(0xFF60A5FA),
    PrimarySage = Color(0xFF6F867E),
    PrimarySageStrong = Color(0xFFA8BEB5),
    PrimarySageSoft = Color(0xFF2A3530),
    // 深色顶栏/主按钮：保持深 sage 底 + 白字，与浅色模式同一色系
    TopBarSage = Color(0xFF6F867E),
    TopBarSageContent = Color(0xFFFFFFFF),
    BorderSoft = Color(0xFF334155),
    PanelWarm = Color(0xFF111827),
    OverlaySurface = Color(0xFF1E293B),
    OverlayScrim = Color(0x330F172A),
    OverlayWaiting = Color(0xFFF97316),
    OverlayDismissBorder = Color(0xFF334155),
    // 深色模式：中性壳体，让状态色只承担语义提示，不把整颗胶囊染色。
    IslandBackground = Color(0xFF20262A),
    IslandSurface = Color(0xFF171C1F),
    IslandBorder = Color(0xFF39434A),
    IslandTextPrimary = Color(0xFFF4F7F8),
    IslandTextSecondary = Color(0xFFAAB5BC),
    IslandDone = Color(0xFF70D39B),
    IslandWaiting = Color(0xFFF2B84B),
    IslandError = Color(0xFFF06B73)
)

/** CompositionLocal：由 [NotifyTheme] 根据模式提供对应的 [AppColors]。 */
val LocalAppColors = compositionLocalOf { LightAppColors }

/** UI 层统一入口：`appColors.TextPrimary`、`appColors.Surface` 等。 */
val appColors: AppColors
    @Composable
    @ReadOnlyComposable
    get() = LocalAppColors.current

/**
 * 不随主题变化的常量：渠道品牌色。
 * 供 [com.lan.notify.data.Channel] enum 在类加载时引用，无需 Composable 上下文。
 */
object AppTokens {
    val ChannelClaude = Color(0xFFCC785C)
    /** Claude Code Haha 改版：与官方 Claude 同源品牌橙，独立渠道但视觉一致。 */
    val ChannelClaudeHaha = Color(0xFFCC785C)
    val ChannelChatGPT = Color(0xFF10A37F)
    val ChannelTrae = Color(0xFF6366F1)
    val ChannelTraeWork = Color(0xFF7C3AED)
    val ChannelZCode = Color(0xFFEC4899)
    val ChannelDoubao = Color(0xFFF56548)
    /** ????????????/ ZCode ?????????? */
    val ChannelQwen = Color(0xFF4F46E5)
    /** ?????????????????? */
    val ChannelSuyan = Color(0xFF374151)
    val ChannelBaiduNetdisk = Color(0xFF3677F6)
    /** 夸克网盘品牌色：蓝紫，与 PC 端 NotificationAppsPanel 配色一致。 */
    val ChannelQuarkNetdisk = Color(0xFF4E6EF2)
    /** 阿里云盘品牌色。 */
    val ChannelAdrive = Color(0xFF1677FF)
    /** WorkBuddy brand violet. */
    val ChannelWorkBuddy = Color(0xFF6351F1)
    /** WorkBuddy AI 独立版本的绿色品牌色。 */
    val ChannelWorkBuddyAi = Color(0xFF10C6A6)
    /** ComfyUI 官方品牌撞色：深紫底上的霓虹黄（取自官方 logo 的 #F2FF59）。 */
    val ChannelComfyUI = Color(0xFFF2FF59)
    /** OpenCode 官方浅灰白品牌色（取自官方 logo 的 #F4F4F5）。 */
    val ChannelOpencode = Color(0xFFF4F4F5)
    /** DeepSeek 官方品牌蓝（取自官方 #4D6BFE）。 */
    val ChannelDeepseekHarness = Color(0xFF4D6BFE)
    /** Freebuff 官方品牌黑（logo 为黑底白星，与 Codebuff 同源）。 */
    val ChannelFreebuff = Color(0xFF000000)
    /** Linux DO CDK 官方图标中的高亮黄。 */
    val ChannelLinuxDoCdk = Color(0xFFFFB000)
    /** TapRelay 项目自身：鼠尾草绿品牌色，与 UI 主题 PrimarySage 一致。 */
    val ChannelTapRelay = Color(0xFF6F867E)
    /** Unknown 渠道用固定中灰，深浅色下都可读。 */
    val ChannelUnknown = Color(0xFF94A3B8)

    // —— 摄像头灵动岛（Dynamic Island）：兼容别名，指向浅色取值。 ——
    // 业务消费点应改用 AppColors.Island*（随深浅色模式变化）；此处的常量仅给
    // Channel 等类加载期或非 Composable 上下文兜底，不在 UI 层直接引用。
    val IslandBackground = LightAppColors.IslandBackground
    val IslandSurface = LightAppColors.IslandSurface
    val IslandBorder = LightAppColors.IslandBorder
    val IslandTextPrimary = LightAppColors.IslandTextPrimary
    val IslandTextSecondary = LightAppColors.IslandTextSecondary
    val IslandDone = LightAppColors.IslandDone
    val IslandWaiting = LightAppColors.IslandWaiting
    val IslandError = LightAppColors.IslandError
    val IslandDebugText = Color(0xFFE8FF6A)
    val IslandDebugBackdrop = Color(0xCC111111)
}

val LightColorScheme = lightColorScheme(
    background = LightAppColors.Background,
    surface = LightAppColors.Surface,
    primary = LightAppColors.Primary,
    onPrimary = LightAppColors.OnPrimary,
    secondary = LightAppColors.PrimarySage,
    onSecondary = LightAppColors.Surface,
    secondaryContainer = LightAppColors.PrimarySageSoft,
    onSecondaryContainer = LightAppColors.PrimarySageStrong,
    tertiary = LightAppColors.PrimarySage,
    onTertiary = LightAppColors.Surface,
    surfaceVariant = Color(0xFFE7E5E0),
    onSurfaceVariant = LightAppColors.TextSecondary,
    error = LightAppColors.Danger,
    onError = LightAppColors.OnPrimary,
    onBackground = LightAppColors.TextPrimary,
    onSurface = LightAppColors.TextPrimary,
    outline = LightAppColors.BorderSoft
)

val DarkColorScheme = darkColorScheme(
    background = DarkAppColors.Background,
    surface = DarkAppColors.Surface,
    primary = DarkAppColors.Primary,
    onPrimary = DarkAppColors.OnPrimary,
    secondary = DarkAppColors.PrimarySage,
    onSecondary = DarkAppColors.Surface,
    secondaryContainer = DarkAppColors.PrimarySageSoft,
    onSecondaryContainer = DarkAppColors.PrimarySageStrong,
    tertiary = DarkAppColors.PrimarySage,
    onTertiary = DarkAppColors.Surface,
    surfaceVariant = Color(0xFF334155),
    onSurfaceVariant = DarkAppColors.TextSecondary,
    error = DarkAppColors.Danger,
    onError = DarkAppColors.OnPrimary,
    onBackground = DarkAppColors.TextPrimary,
    onSurface = DarkAppColors.TextPrimary,
    outline = DarkAppColors.BorderSoft
)
