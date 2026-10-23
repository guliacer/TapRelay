package com.lan.notify.data

import android.content.Context
import org.json.JSONArray

/**
 * 手机端工作流展示偏好：置顶集合与自定义顺序，均按工作流 id 保存。
 * 只影响手机列表展示，不修改 PC 控制台或工作流文件本身。
 */
object ComfyWorkflowOrderStore {
    private const val PREFS_NAME = "notify_comfy_workflow_order"
    private const val KEY_ORDER = "order"
    private const val KEY_PINNED = "pinned"

    data class Order(
        val ids: List<String> = emptyList(),
        val pinned: Set<String> = emptySet()
    )

    fun load(context: Context): Order {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val ids = decodeStringArray(prefs.getString(KEY_ORDER, "[]"))
        val pinned = decodeStringArray(prefs.getString(KEY_PINNED, "[]")).toSet()
        return Order(ids, pinned)
    }

    fun save(context: Context, order: Order) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putString(KEY_ORDER, JSONArray(order.ids.distinct()).toString())
            .putString(KEY_PINNED, JSONArray(order.pinned.toList()).toString())
            .apply()
    }

    /**
     * 按本地偏好排列服务端列表：置顶项按保存顺序排在最前，其余保持保存顺序；
     * 新出现的工作流按服务端顺序追加到末尾，已删除的 id 自动忽略。
     */
    fun apply(
        workflows: List<ComfyWorkflowSummary>,
        order: Order
    ): List<ComfyWorkflowSummary> {
        if (workflows.size < 2) return workflows
        val rank = order.ids.withIndex().associate { (index, id) -> id to index }
        return workflows.sortedWith(
            compareBy<ComfyWorkflowSummary> { if (it.id in order.pinned) 0 else 1 }
                .thenBy { rank[it.id] ?: Int.MAX_VALUE }
        )
    }

    /** 把服务端最新列表合并进本地顺序：丢弃已删除项，新工作流追加到末尾。 */
    fun normalize(
        workflows: List<ComfyWorkflowSummary>,
        order: Order
    ): Order {
        val existing = workflows.map { it.id }.toSet()
        val kept = order.ids.filter { it in existing }.distinct()
        val appended = workflows.map { it.id }.filterNot { it in kept }
        return Order(kept + appended, order.pinned.intersect(existing))
    }

    /** 置顶时移到最上方；取消置顶后保留原相对位置，回到非置顶组。 */
    fun togglePin(order: Order, id: String): Order {
        val pinned = order.pinned.toMutableSet()
        val ids = order.ids.toMutableList()
        if (pinned.remove(id)) {
            return Order(ids, pinned)
        }
        pinned.add(id)
        ids.remove(id)
        ids.add(0, id)
        return Order(ids, pinned)
    }

    /** 拖动排序完成后写回完整可见顺序（置顶组在前，普通组在后）。 */
    fun reorder(order: Order, visibleIds: List<String>): Order {
        val known = order.ids.toSet()
        val merged = visibleIds.filter { it in known } + order.ids.filterNot { it in visibleIds }
        return Order(merged, order.pinned)
    }

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
