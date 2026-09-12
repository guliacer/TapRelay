package com.lan.notify.ui

import android.os.SystemClock
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableLongState
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.isActive
import kotlinx.coroutines.delay
import kotlin.math.PI
import kotlin.math.sin
import kotlin.random.Random

/** Peek 胶囊专用状态符号。 */
enum class CapsuleStatusSymbol { Check, Cross, Equal }

data class CapsuleIndicatorStyle(
    val color: Color,
    val symbol: CapsuleStatusSymbol,
    val shortText: String
)

@Composable
private fun rememberCapsuleEffectClock(
    enabled: Boolean,
    resetKey: Any?,
    tickMs: Long,
    cycleMs: Long? = null,
    activeDurationMs: Long? = null
): MutableLongState {
    val elapsedMs = remember(resetKey) { mutableLongStateOf(0L) }
    LaunchedEffect(enabled, resetKey, tickMs, cycleMs, activeDurationMs) {
        elapsedMs.longValue = 0L
        if (!enabled) return@LaunchedEffect
        val startMs = SystemClock.elapsedRealtime()
        while (isActive) {
            val elapsed = (SystemClock.elapsedRealtime() - startMs).coerceAtLeast(0L)
            val cycleElapsed = cycleMs?.let { elapsed % it } ?: elapsed
            elapsedMs.longValue = activeDurationMs?.let { cycleElapsed.coerceAtMost(it) } ?: cycleElapsed
            if (cycleMs != null && activeDurationMs != null && cycleElapsed >= activeDurationMs) {
                // 静态阶段不需要重绘；休眠到下一个周期再唤醒。
                delay((cycleMs - cycleElapsed).coerceAtLeast(1L))
            } else {
                delay(tickMs)
            }
        }
    }
    return elapsedMs
}

private const val PULSE_COUNT = 3
private const val POWER_SAVE_PULSE_COUNT = 1
private const val SINGLE_PULSE_MS = 500L
private const val SYMBOL_ENTER_MS = 200L
private const val SYMBOL_HOLD_MS = 900L
private const val SYMBOL_EXIT_MS = 200L
private const val STATUS_ICON_ANIMATION_MS =
    PULSE_COUNT * SINGLE_PULSE_MS + SYMBOL_ENTER_MS + SYMBOL_HOLD_MS + SYMBOL_EXIT_MS
private const val POWER_SAVE_STATUS_ICON_ANIMATION_MS =
    POWER_SAVE_PULSE_COUNT * SINGLE_PULSE_MS + SYMBOL_ENTER_MS + SYMBOL_HOLD_MS + SYMBOL_EXIT_MS
private const val STATUS_ICON_NORMAL_CYCLE_MS = 15_000L
private const val STATUS_ICON_POWER_SAVE_CYCLE_MS = 60_000L

/**
 * 状态图标按周期播放一次并在剩余时间保持静态，避免悬浮层持续重绘。
 * 普通模式每 15 秒播放三次脉冲；省电模式每 60 秒只播放一次轻量脉冲。
 */
@Composable
fun AnimatedCapsuleStatusIndicator(
    style: CapsuleIndicatorStyle,
    dynamicEffectsEnabled: Boolean,
    modifier: Modifier = Modifier
) {
    val pulseCount = if (dynamicEffectsEnabled) PULSE_COUNT else POWER_SAVE_PULSE_COUNT
    val animationMs = if (dynamicEffectsEnabled) {
        STATUS_ICON_ANIMATION_MS
    } else {
        POWER_SAVE_STATUS_ICON_ANIMATION_MS
    }
    val cycleMs = if (dynamicEffectsEnabled) {
        STATUS_ICON_NORMAL_CYCLE_MS
    } else {
        STATUS_ICON_POWER_SAVE_CYCLE_MS
    }
    val clock = rememberCapsuleEffectClock(
        // 省电模式仍保留一次低频图标动画，但不再完全静态。
        enabled = true,
        resetKey = style,
        tickMs = if (dynamicEffectsEnabled) STATUS_TICK_MS else POWER_SAVE_STATUS_TICK_MS,
        cycleMs = cycleMs,
        activeDurationMs = animationMs
    )
    Canvas(modifier = modifier) {
        val cycle = clock.longValue
        if (cycle >= animationMs) {
            drawCapsuleSymbol(style.symbol, style.color, 1f, 1f)
            return@Canvas
        }
        val pulseDuration = pulseCount * SINGLE_PULSE_MS
        when {
            cycle < pulseDuration -> {
                val pulse = (cycle % SINGLE_PULSE_MS).toFloat() / SINGLE_PULSE_MS
                val wave = sin(pulse * PI).toFloat()
                drawStatusDot(
                    color = style.color,
                    dotScale = 1f + wave * if (dynamicEffectsEnabled) 0.48f else 0.18f,
                    glowScale = 1f + wave * if (dynamicEffectsEnabled) 0.72f else 0.22f,
                    dotAlpha = 1f,
                    glowAlpha = if (dynamicEffectsEnabled) 0.14f else 0.06f
                )
            }
            cycle < pulseDuration + SYMBOL_ENTER_MS -> {
                val progress = (cycle - pulseDuration).toFloat() / SYMBOL_ENTER_MS
                drawStatusDot(
                    style.color,
                    1f - progress * 0.2f,
                    1f,
                    1f - progress,
                    if (dynamicEffectsEnabled) 0.14f * (1f - progress) else 0.06f * (1f - progress)
                )
                drawCapsuleSymbol(style.symbol, style.color, progress, 0.72f + progress * 0.28f)
            }
            cycle < pulseDuration + SYMBOL_ENTER_MS + SYMBOL_HOLD_MS -> {
                drawCapsuleSymbol(style.symbol, style.color, 1f, 1f)
            }
            else -> {
                val exitStart = pulseDuration + SYMBOL_ENTER_MS + SYMBOL_HOLD_MS
                val progress = (cycle - exitStart).toFloat() / SYMBOL_EXIT_MS
                drawCapsuleSymbol(style.symbol, style.color, 1f - progress, 1f - progress * 0.18f)
                drawStatusDot(
                    style.color,
                    0.8f + progress * 0.2f,
                    1f,
                    progress,
                    if (dynamicEffectsEnabled) 0.14f * progress else 0.06f * progress
                )
            }
        }
    }
}

private const val STATUS_TICK_MS = 80L
private const val POWER_SAVE_STATUS_TICK_MS = 250L

private fun DrawScope.drawStatusDot(
    color: Color,
    dotScale: Float,
    glowScale: Float,
    dotAlpha: Float,
    glowAlpha: Float
) {
    // Canvas 尺寸单位是 px；按画布比例计算，避免在高密度设备上退化成 1dp 小点。
    // 状态圆点直径约为符号宽度的一半（符号宽度约为画布的 60%）。
    val minDimension = size.minDimension
    drawCircle(
        color.copy(alpha = glowAlpha),
        radius = minDimension * 0.28f * glowScale,
        center = center
    )
    drawCircle(
        color.copy(alpha = dotAlpha),
        radius = minDimension * 0.15f * dotScale,
        center = center
    )
}

private fun DrawScope.drawCapsuleSymbol(
    symbol: CapsuleStatusSymbol,
    color: Color,
    alpha: Float,
    scale: Float
) {
    val c = center
    val half = size.minDimension * 0.30f * scale
    val stroke = size.minDimension * 0.12f
    val symbolColor = color.copy(alpha = alpha.coerceIn(0f, 1f))
    when (symbol) {
        CapsuleStatusSymbol.Check -> {
            drawLine(symbolColor, Offset(c.x - half, c.y), Offset(c.x - half * 0.22f, c.y + half * 0.72f), stroke, StrokeCap.Round)
            drawLine(symbolColor, Offset(c.x - half * 0.22f, c.y + half * 0.72f), Offset(c.x + half, c.y - half * 0.80f), stroke, StrokeCap.Round)
        }
        CapsuleStatusSymbol.Cross -> {
            drawLine(symbolColor, Offset(c.x - half, c.y - half), Offset(c.x + half, c.y + half), stroke, StrokeCap.Round)
            drawLine(symbolColor, Offset(c.x + half, c.y - half), Offset(c.x - half, c.y + half), stroke, StrokeCap.Round)
        }
        CapsuleStatusSymbol.Equal -> {
            drawLine(symbolColor, Offset(c.x - half, c.y - half * 0.42f), Offset(c.x + half, c.y - half * 0.42f), stroke, StrokeCap.Round)
            drawLine(symbolColor, Offset(c.x - half, c.y + half * 0.42f), Offset(c.x + half, c.y + half * 0.42f), stroke, StrokeCap.Round)
        }
    }
}

private const val WATER_FILL_DURATION_MS = 60_000L
private const val WATER_RESET_DURATION_MS = 800L
private const val WATER_CYCLE_MS = WATER_FILL_DURATION_MS + WATER_RESET_DURATION_MS
private const val RAIN_DROP_COUNT = 14

private data class CapsuleRainDropSeed(
    val xFraction: Float,
    val startOffsetMs: Long,
    val fallDurationMs: Long,
    val lengthFraction: Float,
    val alpha: Float,
    val width: Float
)

/** 状态色雨滴和积水效果，裁剪在胶囊内部；省电模式完全停用。 */
@Composable
fun CapsuleRainEffect(
    color: Color,
    dynamicEffectsEnabled: Boolean,
    modifier: Modifier = Modifier
) {
    // 雨滴是装饰层，不需要跟随屏幕刷新率；约 4fps 已足够平滑且明显降低功耗。
    val clock = rememberCapsuleEffectClock(
        enabled = dynamicEffectsEnabled,
        resetKey = color,
        tickMs = WATER_TICK_MS
    )
    val drops = remember {
        val random = Random(System.nanoTime())
        List(RAIN_DROP_COUNT) {
            CapsuleRainDropSeed(
                xFraction = 0.08f + random.nextFloat() * 0.84f,
                startOffsetMs = random.nextLong(0L, 1_800L),
                fallDurationMs = random.nextLong(650L, 1_250L),
                lengthFraction = 0.10f + random.nextFloat() * 0.12f,
                alpha = 0.28f + random.nextFloat() * 0.25f,
                width = 0.8f + random.nextFloat() * 0.7f
            )
        }
    }
    Canvas(modifier = modifier.fillMaxSize()) {
        if (!dynamicEffectsEnabled) return@Canvas
        val cycle = clock.longValue % WATER_CYCLE_MS
        val fill = (cycle.toFloat() / WATER_FILL_DURATION_MS).coerceIn(0f, 1f)
        val effectAlpha = if (cycle < WATER_FILL_DURATION_MS) 1f
        else (1f - (cycle - WATER_FILL_DURATION_MS).toFloat() / WATER_RESET_DURATION_MS).coerceIn(0f, 1f)
        val waterTop = size.height * (1f - fill)
        drawWaterBody(color, waterTop, effectAlpha, cycle)
        if (cycle < WATER_FILL_DURATION_MS) {
            drops.forEach { drop ->
                val local = (cycle + drop.startOffsetMs) % drop.fallDurationMs
                val progress = local.toFloat() / drop.fallDurationMs
                val length = size.height * drop.lengthFraction
                val y = -length + progress * (size.height + length)
                if (y + length < waterTop) {
                    drawLine(color.copy(alpha = drop.alpha * effectAlpha), Offset(size.width * drop.xFraction, y), Offset(size.width * drop.xFraction, y + length), drop.width, StrokeCap.Round)
                }
            }
        }
    }
}

private const val WATER_TICK_MS = 240L

private fun DrawScope.drawWaterBody(color: Color, waterTop: Float, alpha: Float, elapsed: Long) {
    if (waterTop >= size.height || alpha <= 0f) return
    drawRect(color.copy(alpha = 0.10f * alpha), Offset(0f, waterTop), Size(size.width, size.height - waterTop))
    drawWaterWave(waterTop, color.copy(alpha = 0.28f * alpha), elapsed, 1.15f, size.width / 2.2f, 0.0022f)
    drawWaterWave(waterTop + 1.2f, color.copy(alpha = 0.14f * alpha), elapsed, 0.75f, size.width / 1.45f, -0.0015f)
}

private fun DrawScope.drawWaterWave(baseY: Float, color: Color, elapsed: Long, amplitude: Float, wavelength: Float, speed: Float) {
    if (wavelength <= 0f) return
    val path = Path()
    val phase = elapsed * speed
    var x = 0f
    while (x <= size.width) {
        val y = baseY + sin((x / wavelength * 2.0 * PI + phase).toFloat()) * amplitude
        if (x == 0f) path.moveTo(x, y) else path.lineTo(x, y)
        x += 2f
    }
    drawPath(path, color, style = Stroke(width = 1.1f, cap = StrokeCap.Round))
}
