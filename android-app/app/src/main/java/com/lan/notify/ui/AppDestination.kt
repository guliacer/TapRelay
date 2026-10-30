package com.lan.notify.ui

/** 一级工作台页面。顺序与底部导航的视觉顺序保持一致。 */
enum class AppDestination(
    val label: String,
    val contentDescription: String
) {
    TASK("任务", "任务"),
    COMFY_UI("ComfyUI", "ComfyUI 工作流"),
    DEVICE("设备", "设备管理")
}
