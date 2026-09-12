package com.lan.notify.overlay

import android.content.Context
import android.os.Build
import android.view.WindowManager

/**
 * 从系统 DisplayCutout 读取顶部挖孔。失败时返回 cutout=null，由计算器回退到屏幕水平中心。
 * 不针对单一机型写死坐标。
 */
object CutoutGeometryProvider {

    fun read(context: Context): ScreenGeometry {
        val metrics = context.resources.displayMetrics
        val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        return ScreenGeometry(
            screenWidthPx = metrics.widthPixels,
            screenHeightPx = metrics.heightPixels,
            density = metrics.density,
            statusBarHeightPx = statusBarHeightPx(context),
            cutout = readCutout(windowManager, metrics.widthPixels)
        )
    }

    fun readCutout(windowManager: WindowManager, screenWidthPx: Int): CutoutGeometry? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return null
        val cutout = windowManager.currentWindowMetrics.windowInsets.displayCutout ?: return null
        val rects = cutout.boundingRects.map { rect ->
            CutoutGeometry(rect.left, rect.top, rect.right, rect.bottom)
        }
        return OverlayPositionCalculator.selectPrimaryTopCutout(rects, screenWidthPx)
    }

    fun statusBarHeightPx(context: Context): Int {
        val resId = context.resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (resId > 0) context.resources.getDimensionPixelSize(resId) else 0
    }
}
