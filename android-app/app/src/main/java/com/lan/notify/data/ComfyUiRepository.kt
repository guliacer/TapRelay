package com.lan.notify.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.util.concurrent.TimeUnit

data class ComfyWorkflowSummary(
    val id: String,
    val name: String,
    val category: String
)

data class ComfyParam(
    val key: String,
    val label: String,
    val kind: String,
    val value: String,
    val choices: List<String> = emptyList(),
    val multiline: Boolean = false,
    val group: String = "",
    val node: String = "",
    val input: String = ""
)

data class ComfyWorkflowDetail(
    val id: String,
    val title: String,
    val primary: List<ComfyParam>,
    val advanced: List<ComfyParam>
)

data class ComfyOutputFile(
    val filename: String,
    val subfolder: String,
    val type: String,
    val promptId: String = "",
    val durationSeconds: Double? = null,
    val width: Int = 0,
    val height: Int = 0,
    val beforeFilename: String = "",
    val beforeSubfolder: String = "",
    val beforeType: String = "",
    /** 生成本图时提交的正向提示词，用于随图发送到素言。 */
    val promptText: String = ""
) {
    fun stableKey(): String = "$promptId:$type:$subfolder:$filename"

    fun hasComparison(): Boolean = beforeFilename.isNotBlank()

    /** 对比节点的 A 图（原图），通常保存在 ComfyUI 的 temp 目录。 */
    fun beforeOutput(): ComfyOutputFile? {
        if (!hasComparison()) return null
        return ComfyOutputFile(
            filename = beforeFilename,
            subfolder = beforeSubfolder,
            type = beforeType.ifBlank { "temp" },
            promptId = promptId
        )
    }
}

data class ComfyJobResult(
    val done: Boolean,
    val failed: Boolean,
    val outputs: List<ComfyOutputFile>,
    val durationSeconds: Double? = null,
    val comparisonBefore: ComfyOutputFile? = null
)

class ComfyUiRepository(baseUrl: String) {
    private val baseUrl = baseUrl.trimEnd('/')
    private val client = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(2, TimeUnit.MINUTES)
        .writeTimeout(2, TimeUnit.MINUTES)
        .build()

    suspend fun listWorkflows(): List<ComfyWorkflowSummary> = withContext(Dispatchers.IO) {
        val root = JSONObject(get("/comfy/api/workflows"))
        val result = mutableListOf<ComfyWorkflowSummary>()
        val items = root.optJSONArray("items") ?: JSONArray()
        for (index in 0 until items.length()) {
            val item = items.optJSONObject(index) ?: continue
            result += ComfyWorkflowSummary(
                id = item.optString("id"),
                name = item.optString("name", item.optString("id")),
                category = item.optString("category", "未分类")
            )
        }
        result
    }

    suspend fun importWorkflow(filename: String, data: String): String = withContext(Dispatchers.IO) {
        val body = JSONObject().apply {
            put("filename", filename)
            put("category", "")
            put("data", data)
        }
        postJson("/comfy/api/mobile/workflows/import", body.toString())
    }

    suspend fun exportWorkflow(id: String): ByteArray = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(url("/comfy/api/mobile/workflows/export?id=${encode(id)}"))
            .get()
            .build()
        executeBytes(request)
    }

    suspend fun deleteWorkflow(id: String): Unit = withContext(Dispatchers.IO) {
        val body = JSONObject().apply { put("id", id) }
        postJson("/comfy/api/mobile/workflows/delete", body.toString())
    }

    /** 只修改工作流显示名称，id 与文件名保持不变。 */
    suspend fun renameWorkflow(id: String, name: String): String = withContext(Dispatchers.IO) {
        val body = JSONObject().apply {
            put("id", id)
            put("name", name)
        }
        val root = JSONObject(postJson("/comfy/api/mobile/workflows/rename", body.toString()))
        root.optString("title", name)
    }

    suspend fun listLoraFiles(): List<String> = withContext(Dispatchers.IO) {
        val root = JSONObject(get("/comfy/api/mobile/loras"))
        val items = root.optJSONArray("items") ?: JSONArray()
        (0 until items.length()).mapNotNull { items.optString(it, null) }
    }

    suspend fun loadWorkflow(id: String): ComfyWorkflowDetail = withContext(Dispatchers.IO) {
        val root = JSONObject(get("/comfy/api/workflow/${encode(id)}"))
        ComfyWorkflowDetail(
            id = root.optString("id", id),
            title = root.optString("title", id),
            primary = parseParams(root.optJSONObject("params")?.optJSONArray("primary")),
            advanced = parseParams(root.optJSONObject("params")?.optJSONArray("advanced"))
        )
    }

    suspend fun runWorkflow(id: String, values: Map<String, Any?>): String = withContext(Dispatchers.IO) {
        val body = JSONObject().apply {
            put("id", id)
            put("values", JSONObject(values))
        }
        val root = JSONObject(postJson("/comfy/api/run", body.toString()))
        root.optString("prompt_id").ifBlank {
            val errors = root.optJSONObject("node_errors")?.toString().orEmpty()
            throw IllegalStateException(if (errors.isBlank()) "ComfyUI 没有返回任务编号" else "工作流校验失败：$errors")
        }
    }

    suspend fun pollJob(promptId: String): ComfyJobResult = withContext(Dispatchers.IO) {
        val root = JSONObject(get("/comfy/api/job?id=${encode(promptId)}"))
        val job = root.optJSONObject(promptId) ?: return@withContext ComfyJobResult(false, false, emptyList())
        val jobStatus = job.optJSONObject("status")
        val status = jobStatus?.optString("status_str").orEmpty()
        val failed = status.equals("error", ignoreCase = true)
        val outputsObject = job.optJSONObject("outputs")
        val outputs = parseOutputs(outputsObject)
        ComfyJobResult(
            done = outputs.isNotEmpty() || status.equals("success", true) || failed,
            failed = failed,
            outputs = outputs,
            durationSeconds = parseDurationSeconds(jobStatus),
            comparisonBefore = parseComparisonBefore(outputsObject)
        )
    }

    private fun parseDurationSeconds(status: JSONObject?): Double? {
        val messages = status?.optJSONArray("messages") ?: return null
        var startedAt = 0L
        var finishedAt = 0L
        for (index in 0 until messages.length()) {
            val message = messages.optJSONArray(index) ?: continue
            val name = message.optString(0)
            val payload = message.optJSONObject(1) ?: continue
            val timestamp = payload.optLong("timestamp", 0L)
            if (timestamp <= 0L) continue
            when (name) {
                "execution_start" -> if (startedAt == 0L) startedAt = timestamp
                "execution_success", "execution_error", "execution_interrupted" -> finishedAt = timestamp
            }
        }
        if (startedAt <= 0L || finishedAt < startedAt) return null
        return (finishedAt - startedAt) / 1000.0
    }

    suspend fun uploadImage(filename: String, bytes: ByteArray): String = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(url("/comfy/api/upload"))
            .header("X-Filename", URLEncoder.encode(filename, StandardCharsets.UTF_8.name()))
            .post(bytes.toRequestBody("application/octet-stream".toMediaType()))
            .build()
        val root = JSONObject(execute(request))
        root.optString("name").ifBlank { throw IllegalStateException("图片上传失败") }
    }

    suspend fun downloadInput(filename: String): ByteArray = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(url("/comfy/api/view?filename=${encode(filename)}&type=input"))
            .get()
            .build()
        executeBytes(request)
    }

    /** 把「原图 + 白/黑遮罩」交给 PC 端合成带 alpha 的 PNG，返回合成后的文件名。 */
    suspend fun composeMask(base: String, mask: String): String = withContext(Dispatchers.IO) {
        val body = JSONObject().apply {
            put("base", base)
            put("mask", mask)
        }
        val root = JSONObject(postJson("/comfy/api/mobile/mask", body.toString()))
        root.optString("name").ifBlank { throw IllegalStateException("遮罩合成失败") }
    }

    suspend fun downloadOutput(file: ComfyOutputFile): ByteArray = withContext(Dispatchers.IO) {
        val query = "filename=${encode(file.filename)}&subfolder=${encode(file.subfolder)}&type=${encode(file.type)}"
        val request = Request.Builder().url(url("/comfy/api/view?$query")).get().build()
        executeBytes(request)
    }

    /** 让 PC 端把 ComfyUI 结果图转发到素言本机收件服务。 */
    suspend fun sendToSuyan(file: ComfyOutputFile, prompt: String = ""): Int = withContext(Dispatchers.IO) {
        val body = JSONObject().apply {
            put("filename", file.filename)
            put("subfolder", file.subfolder)
            put("type", file.type)
            if (prompt.isNotBlank()) put("prompt", prompt)
        }
        val root = JSONObject(postJson("/comfy/api/mobile/suyan/send", body.toString()))
        root.optInt("sentImages", 1)
    }

    suspend fun downloadOutputTo(file: ComfyOutputFile, destination: java.io.File) = withContext(Dispatchers.IO) {
        val query = "filename=${encode(file.filename)}&subfolder=${encode(file.subfolder)}&type=${encode(file.type)}"
        val request = Request.Builder().url(url("/comfy/api/view?$query")).get().build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IllegalStateException("结果接收失败：${response.code}")
            response.body.byteStream().use { input ->
                destination.outputStream().buffered().use { output -> input.copyTo(output, 64 * 1024) }
            }
        }
        if (!destination.isFile || destination.length() == 0L) {
            destination.delete()
            throw IllegalStateException("结果图片内容为空")
        }
    }

    private fun parseParams(items: JSONArray?): List<ComfyParam> {
        if (items == null) return emptyList()
        return buildList {
            for (index in 0 until items.length()) {
                val item = items.optJSONObject(index) ?: continue
                val key = item.optString("key")
                val kind = item.optString("kind", "text")
                val choices = item.optJSONArray("choices")?.let { array ->
                    (0 until array.length()).mapNotNull { array.optString(it, null) }
                }.orEmpty()
                add(ComfyParam(
                    key = key,
                    label = item.optString("label", key),
                    kind = kind,
                    value = item.optString("value"),
                    choices = choices,
                    multiline = item.optBoolean("multiline"),
                    group = item.optString("group"),
                    node = item.optString("node"),
                    input = item.optString("input")
                ))
            }
        }
    }

    private fun parseOutputs(outputs: JSONObject?): List<ComfyOutputFile> {
        if (outputs == null) return emptyList()
        val result = mutableListOf<ComfyOutputFile>()
        val nodes = outputs.keys()
        while (nodes.hasNext()) {
            val node = outputs.optJSONObject(nodes.next()) ?: continue
            listOf("images", "gifs").forEach { key ->
                val files = node.optJSONArray(key) ?: return@forEach
                for (index in 0 until files.length()) {
                    val file = files.optJSONObject(index) ?: continue
                    val name = file.optString("filename")
                    if (name.isNotBlank()) result += ComfyOutputFile(
                        filename = name,
                        subfolder = file.optString("subfolder"),
                        type = file.optString("type", "output")
                    )
                }
            }
        }
        return result
    }

    /** 读取对比节点（rgthree / GG）保存的 A 图，作为“前后对比”的原图。 */
    private fun parseComparisonBefore(outputs: JSONObject?): ComfyOutputFile? {
        if (outputs == null) return null
        val nodes = outputs.keys()
        while (nodes.hasNext()) {
            val node = outputs.optJSONObject(nodes.next()) ?: continue
            val images = node.optJSONArray("a_images") ?: continue
            for (index in 0 until images.length()) {
                val file = images.optJSONObject(index) ?: continue
                val name = file.optString("filename")
                if (name.isNotBlank()) {
                    return ComfyOutputFile(
                        filename = name,
                        subfolder = file.optString("subfolder"),
                        type = file.optString("type", "temp")
                    )
                }
            }
        }
        return null
    }

    private fun get(path: String): String {
        val request = Request.Builder().url(url(path)).get().build()
        return execute(request)
    }

    private fun postJson(path: String, json: String): String {
        val request = Request.Builder()
            .url(url(path))
            .post(json.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .build()
        return execute(request)
    }

    private fun execute(request: Request): String {
        client.newCall(request).execute().use { response ->
            val body = response.body.string()
            if (!response.isSuccessful) throw IllegalStateException(body.ifBlank { "ComfyUI 请求失败：${response.code}" })
            return body
        }
    }

    private fun executeBytes(request: Request): ByteArray {
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IllegalStateException("结果读取失败：${response.code}")
            return response.body.bytes()
        }
    }

    private fun url(path: String): String = baseUrl + "/" + path.trimStart('/')

    private fun encode(value: String): String =
        URLEncoder.encode(value, StandardCharsets.UTF_8.name()).replace("+", "%20")
}
