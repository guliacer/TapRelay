package com.lan.notify.data

import android.content.Context
import org.json.JSONArray

/**
 * 手机端参数展示偏好：按工作流 id 保存“固定到常用”的参数 key。
 * 只影响手机端展示位置，不修改 PC 控制台或工作流文件；数据存放在 SharedPreferences，
 * 软件升级覆盖安装后仍然保留。
 */
object ComfyParamPinStore {
    private const val PREFS_NAME = "notify_comfy_pinned_params"

    fun load(context: Context, workflowId: String): List<String> {
        val raw = prefs(context).getString(prefKey(workflowId), null)
        return decodeStringArray(raw)
    }

    fun save(context: Context, workflowId: String, keys: List<String>) {
        prefs(context).edit()
            .putString(prefKey(workflowId), JSONArray(keys.distinct()).toString())
            .apply()
    }

    fun remove(context: Context, workflowId: String) {
        prefs(context).edit().remove(prefKey(workflowId)).apply()
    }

    /**
     * 只保留当前工作流仍存在的参数 key：工作流更新删除某参数后，
     * 对应偏好自动失效，不会把无效 key 长期写回。
     */
    fun normalize(saved: List<String>, availableKeys: Collection<String>): List<String> {
        val existing = availableKeys.toSet()
        return saved.filter { it in existing }.distinct()
    }

    private fun prefKey(workflowId: String): String = "wf:$workflowId"

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private fun decodeStringArray(raw: String?): List<String> {
        if (raw.isNullOrBlank()) return emptyList()
        return try {
            val array = JSONArray(raw)
            buildList {
                for (index in 0 until array.length()) {
                    array.optString(index, "").takeIf { it.isNotBlank() }?.let(::add)
                }
            }
        } catch (_: Exception) {
            emptyList()
        }
    }
}
