package com.lan.notify.data

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException

/** 某个工作流在本机索引里的全部已生成结果。 */
data class ComfyWorkflowResults(
    val workflowId: String,
    val updatedAt: Long,
    val outputs: List<ComfyOutputFile>
)

/** Remembers the latest received result files per workflow so the result list survives app restarts. */
object ComfyResultStore {
    private const val STORE_DIRECTORY = "ComfyUI"
    private const val STORE_FILE = "results.json"
    const val MAX_OUTPUTS = 50
    private val mutex = Mutex()

    suspend fun load(context: Context, workflowId: String): List<ComfyOutputFile> = mutex.withLock {
        withContext(Dispatchers.IO) {
            val entry = readRoot(context).optJSONObject("workflows")
                ?.optJSONObject(workflowId)
                ?: return@withContext emptyList()
            parseOutputs(entry.optJSONArray("outputs") ?: JSONArray())
        }
    }

    /** 读取所有工作流的结果并按最近更新时间从新到旧排列，用于跨工作流汇总查看。 */
    suspend fun loadAll(context: Context): List<ComfyWorkflowResults> = mutex.withLock {
        withContext(Dispatchers.IO) {
            val workflows = readRoot(context).optJSONObject("workflows")
                ?: return@withContext emptyList()
            val groups = mutableListOf<ComfyWorkflowResults>()
            val keys = workflows.keys()
            while (keys.hasNext()) {
                val workflowId = keys.next()
                val entry = workflows.optJSONObject(workflowId) ?: continue
                val outputs = parseOutputs(entry.optJSONArray("outputs") ?: JSONArray())
                if (outputs.isEmpty()) continue
                groups += ComfyWorkflowResults(
                    workflowId = workflowId,
                    updatedAt = entry.optLong("updatedAt"),
                    outputs = outputs
                )
            }
            groups.sortedByDescending { it.updatedAt }
        }
    }

    private fun parseOutputs(items: JSONArray): List<ComfyOutputFile> = buildList {
        for (index in 0 until items.length()) {
            val item = items.optJSONObject(index) ?: continue
            val filename = item.optString("filename")
            if (filename.isBlank()) continue
            add(
                ComfyOutputFile(
                    filename = filename,
                    subfolder = item.optString("subfolder"),
                    type = item.optString("type", "output"),
                    promptId = item.optString("promptId"),
                    durationSeconds = item.optDouble("durationSeconds")
                        .takeIf { item.has("durationSeconds") && it.isFinite() && it >= 0 },
                    width = item.optInt("width").coerceAtLeast(0),
                    height = item.optInt("height").coerceAtLeast(0),
                    beforeFilename = item.optString("beforeFilename"),
                    beforeSubfolder = item.optString("beforeSubfolder"),
                    beforeType = item.optString("beforeType"),
                    promptText = item.optString("promptText")
                )
            )
        }
    }.take(MAX_OUTPUTS)

    suspend fun save(
        context: Context,
        workflowId: String,
        outputs: List<ComfyOutputFile>
    ): Unit = mutex.withLock {
        withContext(Dispatchers.IO) {
            if (workflowId.isBlank()) return@withContext
            val root = readRoot(context)
            val workflows = root.optJSONObject("workflows")
                ?: JSONObject().also { root.put("workflows", it) }
            val items = JSONArray()
            outputs.take(MAX_OUTPUTS).forEach { output ->
                items.put(
                    JSONObject().apply {
                        put("filename", output.filename)
                        put("subfolder", output.subfolder)
                        put("type", output.type)
                        put("promptId", output.promptId)
                        output.durationSeconds?.let { put("durationSeconds", it) }
                        if (output.width > 0) put("width", output.width)
                        if (output.height > 0) put("height", output.height)
                        if (output.promptText.isNotBlank()) put("promptText", output.promptText)
                        if (output.hasComparison()) {
                            put("beforeFilename", output.beforeFilename)
                            put("beforeSubfolder", output.beforeSubfolder)
                            put("beforeType", output.beforeType)
                        }
                    }
                )
            }
            workflows.put(
                workflowId,
                JSONObject().apply {
                    put("updatedAt", System.currentTimeMillis())
                    put("outputs", items)
                }
            )
            writeRoot(context, root)
        }
    }

    suspend fun remove(context: Context, workflowId: String): Unit = mutex.withLock {
        withContext(Dispatchers.IO) {
            val root = readRoot(context)
            val workflows = root.optJSONObject("workflows") ?: return@withContext
            if (workflows.has(workflowId)) {
                workflows.remove(workflowId)
                writeRoot(context, root)
            }
        }
    }

    private fun readRoot(context: Context): JSONObject {
        val file = storeFile(context)
        if (!file.isFile || file.length() == 0L) return emptyRoot()
        return try {
            JSONObject(file.readText(Charsets.UTF_8))
        } catch (_: Exception) {
            emptyRoot()
        }
    }

    private fun writeRoot(context: Context, root: JSONObject) {
        val directory = File(context.filesDir, STORE_DIRECTORY)
        if (!directory.exists() && !directory.mkdirs()) {
            throw IOException("无法创建 ComfyUI 结果目录")
        }
        val target = File(directory, STORE_FILE)
        val temporary = File(directory, "$STORE_FILE.tmp")
        temporary.writeText(root.toString(), Charsets.UTF_8)
        if (target.exists() && !target.delete()) {
            throw IOException("无法更新 ComfyUI 结果索引")
        }
        if (!temporary.renameTo(target)) {
            temporary.copyTo(target, overwrite = true)
            if (!temporary.delete()) temporary.deleteOnExit()
        }
    }

    private fun storeFile(context: Context): File =
        File(File(context.filesDir, STORE_DIRECTORY), STORE_FILE)

    private fun emptyRoot(): JSONObject = JSONObject().put("version", 1)
}
