package com.lan.notify.data

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

class TaskLogContractTest {
    @Test
    fun sharedWireCasesSurvivePersistenceAndDisplay() {
        val raw = checkNotNull(javaClass.classLoader!!.getResourceAsStream("task-log-cases.json"))
            .bufferedReader().use { it.readText() }
        val cases = JSONArray(raw)
        for (index in 0 until cases.length()) {
            val sample = cases.getJSONObject(index)
            val taskLog = sample.getString("taskLog")
            val wire = JSONObject().put("type", "message").put("source", "codex")
                .put("time", "2026-09-12T00:00:00Z").put("content", "[codex] 任务完成")
                .put("status", "completed").put("taskLog", taskLog)
            val decoded = LocalStore.decodeMessages(JSONArray().put(wire).toString()).single()
            val reloaded = LocalStore.decodeMessages(LocalStore.encodeMessages(listOf(decoded))).single()
            assertEquals(sample.getString("name"), taskLog, reloaded.displayContent())
        }
    }

    @Test
    fun legacyCleanupRecognizesSourceButPreservesLabelsAndLinks() {
        fun display(content: String, source: String = "codex") =
            NotifyMessage(type = "message", content = content, time = "now", source = source).displayContent()
        assertEquals("详情", display("[codex] 详情"))
        assertEquals("详情", display("[chatgpt] 详情"))
        assertEquals("详情", display("[custom-agent] 详情", "custom-agent"))
        assertEquals("[错误] 编译失败", display("[错误] 编译失败"))
        assertEquals("[README.md](./README.md)", display("[README.md](./README.md)"))
        assertEquals("[codex](./README.md)", display("[codex](./README.md)"))
    }
}
