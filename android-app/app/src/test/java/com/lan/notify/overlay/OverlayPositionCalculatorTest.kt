package com.lan.notify.overlay

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OverlayPositionCalculatorTest {

    private companion object {
        const val WRAP_CONTENT = -2
    }

    @Test
    fun selectsTopCutoutClosestToScreenCenter() {
        val left = CutoutGeometry(left = 40, top = 0, right = 80, bottom = 40)
        val center = CutoutGeometry(left = 562, top = 0, right = 678, bottom = 138)
        val selected = OverlayPositionCalculator.selectPrimaryTopCutout(
            listOf(left, center),
            screenWidthPx = 1240
        )
        assertEquals(center, selected)
    }

    @Test
    fun emptyCutoutsReturnNull() {
        assertNull(
            OverlayPositionCalculator.selectPrimaryTopCutout(emptyList(), screenWidthPx = 1240)
        )
    }

    @Test
    fun windowSpecAlignsToEstimatedHoleTopNotFullBounding() {
        val screen = ScreenGeometry(
            screenWidthPx = 1240,
            screenHeightPx = 2772,
            density = 3.5f,
            statusBarHeightPx = 137,
            cutout = CutoutGeometry(left = 562, top = 0, right = 678, bottom = 138)
        )
        val hole = OverlayPositionCalculator.estimatedHoleSizePx(screen)
        // ColorOS holeRect on RMX3706 is ~68px; status*0.5 ≈ 68–69
        // status*0.5 rounded; clamp only by min/max dp
        assertTrue(hole in 63..72)
        val spec = OverlayPositionCalculator.windowSpec(
            screen,
            wrapContent = WRAP_CONTENT
        )
        assertEquals(WRAP_CONTENT, spec.width)
        assertEquals(WRAP_CONTENT, spec.height)
        assertEquals(0, spec.x)
        assertEquals((137 - hole) / 2, spec.y)
        assertEquals(OverlayPositionCalculator.holeTopPx(screen), spec.y)
        assertEquals(620, screen.cutout?.centerX)
    }

    @Test
    fun missingCutoutFallsBackToScreenCenter() {
        val screen = ScreenGeometry(
            screenWidthPx = 1080,
            screenHeightPx = 2400,
            density = 3f,
            statusBarHeightPx = 80,
            cutout = null
        )
        val spec = OverlayPositionCalculator.windowSpec(
            screen,
            wrapContent = WRAP_CONTENT
        )
        assertEquals(0, spec.x)
        assertEquals(0, spec.y)
    }

    @Test
    fun peekWidthClampsToMinAndMax() {
        val density = 3.5f
        val screenWidth = 1240
        assertEquals(
            OverlayMetrics.PEEK_MIN_WIDTH_DP.toFloat(),
            OverlayPositionCalculator.peekWidthDp(40f, screenWidth, density),
            0.01f
        )
        val max = OverlayPositionCalculator.peekMaxWidthDp(screenWidth, density)
        assertEquals(max, OverlayPositionCalculator.peekWidthDp(400f, screenWidth, density), 0.01f)
        assertEquals(
            OverlayMetrics.PEEK_PREFERRED_WIDTH_DP.toFloat(),
            OverlayPositionCalculator.peekWidthDp(
                OverlayMetrics.PEEK_PREFERRED_WIDTH_DP.toFloat(),
                screenWidth,
                density
            ),
            0.01f
        )
    }

    @Test
    fun compactUsesHoleCoreAndStatusBarClearTouchShell() {
        val screen = ScreenGeometry(
            screenWidthPx = 1240,
            screenHeightPx = 2772,
            density = 3.5f,
            statusBarHeightPx = 137,
            cutout = CutoutGeometry(left = 562, top = 0, right = 678, bottom = 138)
        )
        val hole = OverlayPositionCalculator.estimatedHoleSizePx(screen).toFloat()
        val core = hole / 3.5f
        val top = OverlayPositionCalculator.holeTopPx(screen)
        assertTrue(hole in 63f..72f)
        // 视觉核贴 hole；触控宽至少 44dp
        assertEquals(core, OverlayPositionCalculator.compactCoreSizeDp(screen), 0.01f)
        assertEquals(
            maxOf(core, OverlayMetrics.TOUCH_MIN_WIDTH_DP.toFloat()),
            OverlayPositionCalculator.compactWidthDp(screen),
            0.01f
        )
        val clearPx = OverlayPositionCalculator.dpToPx(
            OverlayMetrics.TOUCH_CLEAR_BELOW_STATUS_DP.toFloat(),
            3.5f
        )
        val expectedHeight = maxOf(
            core + OverlayMetrics.TOUCH_OVERHANG_DP,
            ((137 - top).coerceAtLeast(0) + clearPx) / 3.5f
        )
        assertEquals(expectedHeight, OverlayPositionCalculator.compactHeightDp(screen), 0.01f)
        // 底边必须清楚越过状态栏
        val bottomPx = top + OverlayPositionCalculator.dpToPx(
            OverlayPositionCalculator.compactHeightDp(screen),
            3.5f
        )
        assertTrue(bottomPx >= 137 + clearPx - 1)
        // Peek 从状态栏底部以下开始，不覆盖摄像头或系统状态栏。
        val peekInset = OverlayPositionCalculator.peekTopInsetDp(screen)
        assertEquals(
            ((137 - top).coerceAtLeast(0)) / 3.5f + OverlayMetrics.PEEK_BELOW_STATUS_GAP_DP,
            peekInset,
            0.01f
        )
        val peekVisual = OverlayPositionCalculator.peekVisualHeightDp(screen)
        assertTrue(peekVisual in OverlayMetrics.PEEK_VISUAL_MIN_HEIGHT_DP..OverlayMetrics.PEEK_VISUAL_MAX_HEIGHT_DP)
        assertEquals(peekVisual, OverlayPositionCalculator.peekHeightDp(screen), 0.01f)
        val peekTouch = OverlayPositionCalculator.peekTouchHeightDp(screen)
        assertTrue(peekTouch + 0.01f >= peekVisual + OverlayMetrics.TOUCH_OVERHANG_DP)
        assertEquals(
            peekInset + peekTouch,
            OverlayPositionCalculator.peekShellHeightDp(screen),
            0.01f
        )
        val peekTopPx = top + OverlayPositionCalculator.dpToPx(peekInset, 3.5f)
        assertEquals(
            137 + OverlayPositionCalculator.dpToPx(
                OverlayMetrics.PEEK_BELOW_STATUS_GAP_DP.toFloat(),
                3.5f
            ),
            peekTopPx
        )
        val peekBottomPx = top + OverlayPositionCalculator.dpToPx(
            OverlayPositionCalculator.peekShellHeightDp(screen),
            3.5f
        )
        assertTrue(peekBottomPx >= peekTopPx + clearPx - 1)
        assertEquals(
            peekVisual + OverlayMetrics.PEEK_TOUCH_HORIZONTAL_PAD_DP * 2f,
            OverlayPositionCalculator.peekTouchWidthDp(peekVisual),
            0.01f
        )
        // Expanded 卡片相对 holeTop 下沉到状态栏下并隔开
        val expandedInset = OverlayPositionCalculator.expandedTopInsetDp(screen)
        assertEquals(
            ((137 - top).coerceAtLeast(0)) / 3.5f + OverlayMetrics.EXPANDED_BELOW_STATUS_GAP_DP,
            expandedInset,
            0.01f
        )
    }

    @Test
    fun tightCutoutUsesMinSideWithoutStatusBarHeuristic() {
        val screen = ScreenGeometry(
            screenWidthPx = 1080,
            screenHeightPx = 2400,
            density = 3f,
            statusBarHeightPx = 80,
            cutout = CutoutGeometry(left = 500, top = 10, right = 560, bottom = 50)
        )
        // height 40 << status 80, use min(60,40)=40px; min dp@3=54 so clamp up to 54
        val hole = OverlayPositionCalculator.estimatedHoleSizePx(screen)
        assertEquals(OverlayPositionCalculator.dpToPx(OverlayMetrics.COMPACT_MIN_SIZE_DP, 3f), hole)
        assertEquals(hole / 3f, OverlayPositionCalculator.compactCoreSizeDp(screen), 0.01f)
    }

    @Test
    fun missingCutoutUsesHalfStatusBarClamped() {
        val screen = ScreenGeometry(
            screenWidthPx = 1080,
            screenHeightPx = 2400,
            density = 3f,
            statusBarHeightPx = 80,
            cutout = null
        )
        val hole = OverlayPositionCalculator.estimatedHoleSizePx(screen)
        // half status=40, min@3dp=54 → 54
        assertEquals(OverlayPositionCalculator.dpToPx(OverlayMetrics.COMPACT_MIN_SIZE_DP, 3f), hole)
        val core = hole / 3f
        assertEquals(core, OverlayPositionCalculator.compactCoreSizeDp(screen), 0.01f)
        assertEquals(
            maxOf(core, OverlayMetrics.TOUCH_MIN_WIDTH_DP.toFloat()),
            OverlayPositionCalculator.compactWidthDp(screen),
            0.01f
        )
        val top = OverlayPositionCalculator.holeTopPx(screen)
        val clearPx = OverlayPositionCalculator.dpToPx(
            OverlayMetrics.TOUCH_CLEAR_BELOW_STATUS_DP.toFloat(),
            3f
        )
        val expectedHeight = maxOf(
            core + OverlayMetrics.TOUCH_OVERHANG_DP,
            ((80 - top).coerceAtLeast(0) + clearPx) / 3f
        )
        assertEquals(expectedHeight, OverlayPositionCalculator.compactHeightDp(screen), 0.01f)
    }
}
