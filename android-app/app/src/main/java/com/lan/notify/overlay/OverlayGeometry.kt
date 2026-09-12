package com.lan.notify.overlay

import kotlin.math.abs
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * 顶部挖孔的几何信息。坐标相对当前屏幕窗口，单位像素。
 * DisplayCutout.boundingRect 常大于真实摄像头孔；小岛尺寸用 [OverlayPositionCalculator.estimatedHoleSizePx]。
 */
data class CutoutGeometry(
    val left: Int,
    val top: Int,
    val right: Int,
    val bottom: Int
) {
    val centerX: Int get() = (left + right) / 2
    val width: Int get() = (right - left).coerceAtLeast(0)
    val height: Int get() = (bottom - top).coerceAtLeast(0)
}

/** 计算窗口位置所需的屏幕几何，全部来自系统 API，禁止写死机型坐标。 */
data class ScreenGeometry(
    val screenWidthPx: Int,
    val screenHeightPx: Int,
    val density: Float,
    val statusBarHeightPx: Int,
    val cutout: CutoutGeometry?
)

/**
 * 单一 overlay 窗口的 WindowManager 参数。
 * Compact / Peek / Expanded 共用同一份 spec，禁止按阶段 remove/add 或改宽高策略。
 */
data class OverlayWindowSpec(
    val width: Int,
    val height: Int,
    val x: Int,
    val y: Int
)

object OverlayMetrics {
    /** 贴合摄像头的视觉核最小边长；ColorOS hole 约 68px@3.5≈19.4dp。 */
    const val COMPACT_MIN_SIZE_DP = 18f
    const val COMPACT_MAX_SIZE_DP = 36f
    /**
     * bounding 高度接近状态栏时，真实孔洞大约是状态栏高度的一半
     * （RMX3706: status 138、ColorOS holeRect 68）。
     */
    const val HOLE_FROM_STATUS_BAR_FRACTION = 0.5f
    /**
     * Compact 视觉核下方的最小伸出（dp）。真实可点高度还要保证整窗底边
     * 至少越过 StatusBar [TOUCH_CLEAR_BELOW_STATUS_DP]。
     */
    const val TOUCH_OVERHANG_DP = 14
    /**
     * 第三方 overlay 被 StatusBar（更高 z-order）盖住；手指可点区必须清楚落在
     * 状态栏底边以下。36dp ≈ 指尖宽度，并覆盖胶囊下半展示区。
     */
    const val TOUCH_CLEAR_BELOW_STATUS_DP = 36
    /** Compact 最小触控宽；视觉核仍按 hole，两侧透明扩大命中。 */
    const val TOUCH_MIN_WIDTH_DP = 44
    /** Peek 视觉高度：状态栏下方的紧凑横向胶囊。 */
    const val PEEK_VISUAL_MIN_HEIGHT_DP = 30f
    const val PEEK_VISUAL_MAX_HEIGHT_DP = 30f
    /**
     * Peek 是应用自身的悬浮提示，不伪装为系统状态栏组件。
     * 从状态栏底边再留出这段距离，避免贴住摄像头区域和系统图标。
     */
    const val PEEK_BELOW_STATUS_GAP_DP = 10
    /** 胶囊左右额外透明触控边，保证整颗展示区（含圆角外缘）都能点到。 */
    const val PEEK_TOUCH_HORIZONTAL_PAD_DP = 18
    const val PEEK_MIN_WIDTH_DP = 68
    /** 首次测量兜底宽度；稳定状态由视觉内容自适应决定。 */
    const val PEEK_PREFERRED_WIDTH_DP = 104
    /** 胶囊本体的视觉上限；左右触控扩展不计入此宽度。 */
    const val PEEK_MAX_WIDTH_DP = 112
    const val PEEK_MAX_WIDTH_FRACTION = 0.55f
    const val EXPANDED_HORIZONTAL_PADDING_DP = 12
    /** Expanded 卡片相对状态栏底边再下移，恢复早期「与顶部分隔」的悬浮感。 */
    const val EXPANDED_BELOW_STATUS_GAP_DP = 14
}

object OverlayPositionCalculator {

    /**
     * 从候选挖孔中选出主要顶部挖孔：优先 top 最小，其次最靠近屏幕水平中心。
     * 不写死 centerX / 状态栏高度。
     */
    fun selectPrimaryTopCutout(
        rects: List<CutoutGeometry>,
        screenWidthPx: Int
    ): CutoutGeometry? {
        if (rects.isEmpty() || screenWidthPx <= 0) return null
        val screenCenter = screenWidthPx / 2
        return rects.minWith(
            compareBy<CutoutGeometry> { it.top }
                .thenBy { abs(it.centerX - screenCenter) }
        )
    }

    /**
     * 估算真实摄像头孔边长（px）。
     * ColorOS 流体云用 holeRect（约 68×68），而 DisplayCutout.boundingRect 常是
     * 含安全区的大包围盒（本机 116×138）。当包围盒高度接近状态栏时，按状态栏一半估算孔洞。
     */
    fun estimatedHoleSizePx(screen: ScreenGeometry): Int {
        val density = screen.density.coerceAtLeast(0.1f)
        val minPx = dpToPx(OverlayMetrics.COMPACT_MIN_SIZE_DP, density)
        val maxPx = dpToPx(OverlayMetrics.COMPACT_MAX_SIZE_DP, density)
        val status = screen.statusBarHeightPx.coerceAtLeast(minPx)
        val cutout = screen.cutout
        if (cutout == null || (cutout.width <= 0 && cutout.height <= 0)) {
            return min(status / 2, maxPx).coerceAtLeast(minPx)
        }
        val raw = min(cutout.width.coerceAtLeast(1), cutout.height.coerceAtLeast(1))
        val estimated = if (cutout.height >= status * 0.85f) {
            min(
                cutout.width.coerceAtLeast(1),
                (status * OverlayMetrics.HOLE_FROM_STATUS_BAR_FRACTION).roundToInt()
            )
        } else {
            raw
        }
        return estimated.coerceIn(minPx, min(maxPx, status))
    }

    /**
     * 估算 hole 顶边 y（相对整屏）。孔洞在状态栏内垂直居中，对齐 ColorOS blankTop。
     */
    fun holeTopPx(screen: ScreenGeometry): Int {
        val hole = estimatedHoleSizePx(screen)
        val status = screen.statusBarHeightPx.coerceAtLeast(0)
        return if (screen.cutout != null && status > 0) {
            ((status - hole) / 2).coerceAtLeast(screen.cutout.top.coerceAtLeast(0))
        } else {
            screen.cutout?.top ?: 0
        }
    }

    /**
     * 单一窗口位置：水平与挖孔中心对齐。
     * 顶边对齐估算孔洞顶边（在状态栏内垂直居中），与 ColorOS seedling/hole 一致。
     * 窗口使用 FLAG_LAYOUT_IN_SCREEN，y 相对整屏。
     */
    fun windowSpec(screen: ScreenGeometry, wrapContent: Int): OverlayWindowSpec {
        val centerX = screen.cutout?.centerX ?: (screen.screenWidthPx / 2)
        return OverlayWindowSpec(
            width = wrapContent,
            height = wrapContent,
            x = centerX - screen.screenWidthPx / 2,
            y = holeTopPx(screen)
        )
    }

    fun peekMaxWidthDp(screenWidthPx: Int, density: Float): Float {
        if (density <= 0f || screenWidthPx <= 0) {
            return OverlayMetrics.PEEK_PREFERRED_WIDTH_DP.toFloat()
        }
        val screenDp = screenWidthPx / density
        return minOf(
            OverlayMetrics.PEEK_MAX_WIDTH_DP.toFloat(),
            screenDp * OverlayMetrics.PEEK_MAX_WIDTH_FRACTION
        )
    }

    fun peekWidthDp(contentWidthDp: Float, screenWidthPx: Int, density: Float): Float {
        val maxWidth = peekMaxWidthDp(screenWidthPx, density)
        return contentWidthDp.coerceIn(
            OverlayMetrics.PEEK_MIN_WIDTH_DP.toFloat(),
            maxWidth
        )
    }

    /** Compact 视觉核边长（贴摄像头），不含下方可点伸出。 */
    fun compactCoreSizeDp(screen: ScreenGeometry): Float {
        val density = screen.density.coerceAtLeast(0.1f)
        return estimatedHoleSizePx(screen) / density
    }

    /**
     * Compact 触控壳宽度：视觉核两侧透明扩大，至少 [TOUCH_MIN_WIDTH_DP]。
     * 壳背景仍按视觉核画圆角，点击落在透明区也算。
     */
    fun compactWidthDp(screen: ScreenGeometry): Float =
        maxOf(compactCoreSizeDp(screen), OverlayMetrics.TOUCH_MIN_WIDTH_DP.toFloat())

    /**
     * Compact 总高度：视觉核顶对齐 hole 顶边，底边必须越过 StatusBar 足够多，
     * 保证手指可点区不在 SystemUI 拦截带内。
     *
     * height = max(core + overhang, (statusBar - holeTop)/density + clearBelow)
     */
    fun compactHeightDp(screen: ScreenGeometry): Float {
        val density = screen.density.coerceAtLeast(0.1f)
        val core = compactCoreSizeDp(screen)
        val minByOverhang = core + OverlayMetrics.TOUCH_OVERHANG_DP
        val status = screen.statusBarHeightPx.coerceAtLeast(0)
        val top = holeTopPx(screen)
        val clearPx = dpToPx(OverlayMetrics.TOUCH_CLEAR_BELOW_STATUS_DP.toFloat(), density)
        val minByStatusClear = ((status - top).coerceAtLeast(0) + clearPx) / density
        return maxOf(minByOverhang, minByStatusClear)
    }

    /**
     * 窗口顶（holeTop）到状态栏底边的距离（dp）。
     * APPLICATION_OVERLAY 低于 StatusBar z-order，此带内触摸会被 SystemUI 吃掉
     * （挖孔中心偶发穿透，左右一定被拦）。Peek 必须从状态栏下方开始绘制。
     */
    fun statusBarClearTopInsetDp(screen: ScreenGeometry): Float {
        val density = screen.density.coerceAtLeast(0.1f)
        val status = screen.statusBarHeightPx.coerceAtLeast(0)
        val top = holeTopPx(screen)
        return ((status - top).coerceAtLeast(0)) / density
    }

    /**
     * Peek 视觉顶边相对窗口顶的下移。窗口仍以挖孔位置为几何锚点，
     * 但实际胶囊从状态栏底部以下开始，作为独立的应用悬浮提示显示。
     */
    fun peekTopInsetDp(screen: ScreenGeometry): Float =
        statusBarClearTopInsetDp(screen) + OverlayMetrics.PEEK_BELOW_STATUS_GAP_DP

    /**
     * Peek 视觉高度：横向短胶囊，不纵向拉成球。
     */
    fun peekVisualHeightDp(screen: ScreenGeometry): Float {
        val core = compactCoreSizeDp(screen)
        return maxOf(core + 6f, OverlayMetrics.PEEK_VISUAL_MIN_HEIGHT_DP).coerceIn(
            OverlayMetrics.PEEK_VISUAL_MIN_HEIGHT_DP,
            OverlayMetrics.PEEK_VISUAL_MAX_HEIGHT_DP
        )
    }

    /**
     * Peek 触控高度（相对视觉顶）：覆盖整颗胶囊 + 下方指尖余量。
     * 与视觉同顶边，全部在 StatusBar 下，左右与中间等价可点。
     */
    fun peekTouchHeightDp(screen: ScreenGeometry): Float {
        val visual = peekVisualHeightDp(screen)
        return maxOf(
            visual + OverlayMetrics.TOUCH_OVERHANG_DP,
            OverlayMetrics.TOUCH_CLEAR_BELOW_STATUS_DP.toFloat()
        )
    }

    /**
     * Peek 触控壳相对窗口顶的总高度 = 顶 inset + 触控高度。
     */
    fun peekShellHeightDp(screen: ScreenGeometry): Float =
        peekTopInsetDp(screen) + peekTouchHeightDp(screen)

    /**
     * Peek 触控宽 = 视觉胶囊宽 + 左右透明边。
     * 整颗展示区（含圆角外缘）都必须落在可点矩形内。
     */
    fun peekTouchWidthDp(visualWidthDp: Float): Float =
        visualWidthDp + OverlayMetrics.PEEK_TOUCH_HORIZONTAL_PAD_DP * 2f

    /** 兼容旧调用：Peek 默认指视觉高度（横向胶囊）。 */
    fun peekHeightDp(screen: ScreenGeometry): Float = peekVisualHeightDp(screen)

    /**
     * Expanded 内容相对窗口顶（holeTop）的下移量：先齐平状态栏底，再加分隔间距。
     * 窗口 y 仍固定 holeTop，不在档位间改 WindowManager 坐标。
     */
    fun expandedTopInsetDp(screen: ScreenGeometry): Float =
        statusBarClearTopInsetDp(screen) + OverlayMetrics.EXPANDED_BELOW_STATUS_GAP_DP

    fun pxToDp(px: Int, density: Float): Float =
        if (density <= 0f) 0f else px / density

    fun dpToPx(dp: Float, density: Float): Int =
        (dp * density).roundToInt()
}
