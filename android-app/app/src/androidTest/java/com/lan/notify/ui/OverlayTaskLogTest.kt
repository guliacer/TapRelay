package com.lan.notify.ui

import android.graphics.Bitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import com.lan.notify.data.NotifyMessage
import com.lan.notify.ui.theme.NotifyTheme
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.io.File

class OverlayTaskLogTest {
    @get:Rule val compose = createComposeRule()

    private fun show(log: String, status: String = "completed", dismiss: () -> Unit = {}) {
        compose.setContent {
            NotifyTheme {
                OverlayCards(
                    messages = listOf(NotifyMessage(
                        type = "message", content = "兼容正文", time = "2026-09-09T16:10:37+08:00",
                        source = "traework", status = status, projectName = "弹窗验证",
                        sessionId = "ui-test", taskLog = log, durationMs = 180792
                    )),
                    registeredApps = emptyMap(), onCardDismiss = { dismiss() },
                    onDismissAll = {}, dynamicEffectsEnabled = false
                )
            }
        }
    }

    @Test fun longLogScrollsWhileCloseRemainsUsable() {
        val log = (1..30).joinToString("\n") { "第 $it 行：已完成结果校验，保留真实任务日志。" }
        var closed = false
        show(log, dismiss = { closed = true })
        compose.onNodeWithText("兼容正文").assertDoesNotExist()
        val node = compose.onNodeWithText(log)
        node.performTouchInput { swipeUp() }
        compose.waitForIdle()
        val range = node.fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange]
        assertTrue(range.value() > 0f)
        compose.onNodeWithText("关闭").assertIsDisplayed()
        val file = File(InstrumentationRegistry.getInstrumentation().targetContext.getExternalFilesDir(null), "overlay-log-test.png")
        file.outputStream().use {
            compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it)
        }
        compose.onNodeWithText("关闭").performClick()
        compose.runOnIdle { assertTrue(closed) }
    }

    @Test fun shortLogIsDisplayedInsteadOfFallback() {
        show("已修复解析错误，全部校验通过。")
        compose.onNodeWithText("已修复解析错误，全部校验通过。").assertIsDisplayed()
        compose.onNodeWithText("兼容正文").assertDoesNotExist()
        compose.onNodeWithText("关闭").assertIsDisplayed()
    }

    @Test fun missingLogKeepsFallbackAndWaitingAction() {
        show("", status = "waiting")
        compose.onNodeWithText("兼容正文").assertIsDisplayed()
        compose.onNodeWithText("处理").assertIsDisplayed()
        compose.onNodeWithText("关闭").assertIsDisplayed()
    }
}
