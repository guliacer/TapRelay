package com.lan.notify.util

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log

/**
 * 厂商自启动权限跳转工具。
 *
 * Android 原生没有标准的自启动权限 API，各厂商在定制系统中各自实现。
 *
 * 跳转策略（避免误跳到通用权限/应用详情页）：
 * 1. 同一厂商内 ComponentName 优先：精确指向自启动管理 Activity，
 *    不存在时直接抛 ActivityNotFoundException，不会跳错页面。
 * 2. action 方式必须 [setPackage] 限定目标包，否则系统 resolver
 *    可能把 action 派发给同包内的首页 Activity（手机管家主页、安全中心
 *    主页、应用权限聚合页），表现为「点击后只到一般权限页面」。
 * 3. 不用 resolveActivity 预检：部分厂商 Activity 受 OPLUS_COMPONENT_SAFE
 *    等系统权限保护，resolveActivity 返回 null 但应用内 startActivity
 *    仍可成功。
 */
object AutoStartHelper {

    private const val TAG = "AutoStartHelper"

    /**
     * 各厂商自启动权限页的 Intent 候选列表。
     * 排列顺序即尝试顺序，ComponentName 在前、action 在后。
     */
    private val autoStartIntents: List<Intent> by lazy {
        val list = mutableListOf<Intent>()

        // === ColorOS / realme / OnePlus (OPLUS) ===
        // ComponentName 优先：从新版本到旧版本逐个尝试
        componentIntents(
            list,
            // ColorOS 13+/14+/15+/16（含 realme UI 4.0+/5.0+/6.0）：
            // com.oplus.battery 是电池与启动管理主包
            "com.oplus.battery" to "com.oplus.startupapp.view.OptimizationAutoStartActivity",
            "com.oplus.battery" to "com.oplus.startupapp.view.AutoStartAppListActivity",
            "com.oplus.battery" to "com.oplus.startupapp.view.StartupAppListActivity",
            "com.oplus.battery" to "com.oplus.startupapp.view.PreventRecordActivity",
            // 手机管家入口（部分版本走 phonemanager）
            "com.coloros.phonemanager" to "com.coloros.phonemanager.module.startupapp.OptimizationAutoStartActivity",
            "com.coloros.phonemanager" to "com.coloros.phonemanager.module.startupapp.AutoStartAppListActivity",
            // 旧版 ColorOS（安全中心）
            "com.coloros.safecenter" to "com.coloros.safecenter.permission.startup.StartupAppListActivity",
            "com.coloros.safecenter" to "com.coloros.safecenter.startupapp.StartupAppListActivity",
            "com.oplus.safecenter" to "com.oplus.safecenter.startupapp.StartupAppListActivity"
        )

        // action 方式：必须 setPackage 限定，否则会被同包首页 Activity 接走
        actionIntents(
            list,
            "com.oplus.battery" to "safe.intent.action.OPT_AUTO_APP_ACTIVITY",
            "com.oplus.battery" to "com.oplus.battery.permission.startup.LaunchRecordActivity",
            "com.oplus.safecenter" to "com.oplus.safe.action.APP_PROTECT_LIST_ACTIVITY_FROM_SETTINGS"
            // 注意：不再加入 oplus.intent.action.PHONE_MANAGER_MAIN_ACTIVITY、
            // com.coloros.safecenter.SecureSafeMainActivity 等通用 action，
            // 它们在 ColorOS 16 上会落到手机管家/安全中心首页而非自启动页。
        )

        // === MIUI (Xiaomi / Redmi) ===
        componentIntents(
            list,
            "com.miui.securitycenter" to "com.miui.permcenter.autostart.AutoStartManagementActivity"
        )

        // === EMUI / HarmonyOS (Huawei / Honor) ===
        componentIntents(
            list,
            "com.huawei.systemmanager" to "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
            "com.huawei.systemmanager" to "com.huawei.systemmanager.optimize.process.ProtectActivity"
        )

        // === Samsung ===
        componentIntents(
            list,
            "com.samsung.android.sm" to "com.samsung.android.sm.ui.battery.BatteryActivity",
            "com.samsung.android.sm_cn" to "com.samsung.android.sm.ui.battery.BatteryActivity"
        )

        // === Vivo / iQOO ===
        componentIntents(
            list,
            "com.iqoo.secure" to "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity",
            "com.vivo.permissionmanager" to "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"
        )

        // === Meizu ===
        componentIntents(
            list,
            "com.meizu.safe" to "com.meizu.safe.security.SHOW_APPSEC"
        )

        list
    }

    private fun componentIntents(
        list: MutableList<Intent>,
        vararg targets: Pair<String, String>
    ) {
        targets.forEach { (pkg, cls) ->
            list.add(
                Intent().apply {
                    component = ComponentName(pkg, cls)
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
            )
        }
    }

    private fun actionIntents(
        list: MutableList<Intent>,
        vararg targets: Pair<String, String>
    ) {
        targets.forEach { (pkg, action) ->
            list.add(
                Intent(action).apply {
                    setPackage(pkg)
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
            )
        }
    }

    /**
     * 尝试打开厂商自启动权限管理页面。
     * 逐个尝试已知 Intent，第一个成功的即跳转。
     * 全部失败时回退到应用详情页。
     *
     * @return true 表示成功打开某个页面。
     */
    fun openAutoStartSettings(context: Context): Boolean {
        val brand = Build.BRAND?.lowercase() ?: ""
        Log.i(TAG, "openAutoStartSettings brand=$brand model=${Build.MODEL}")

        val tried = mutableListOf<String>()
        for (intent in autoStartIntents) {
            val label = intent.action ?: intent.component?.flattenToString() ?: "unknown"
            try {
                context.startActivity(intent)
                Log.i(TAG, "opened auto-start settings: $label")
                return true
            } catch (e: Exception) {
                tried.add("$label[${e.javaClass.simpleName}]")
                Log.d(TAG, "failed: $label - ${e.message}")
            }
        }
        Log.w(TAG, "all auto-start intents failed: $tried")

        // 全部失败，回退到应用详情页
        return try {
            val fallback = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:${context.packageName}")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(fallback)
            true
        } catch (_: Exception) {
            false
        }
    }

    /** 判断当前设备品牌是否支持自启动权限管理。 */
    fun isAutoStartSupported(): Boolean {
        val brand = Build.BRAND?.lowercase() ?: return false
        return brand in setOf(
            "oppo", "realme", "oneplus", "oplus",
            "xiaomi", "redmi", "meitu",
            "huawei", "honor",
            "samsung",
            "vivo", "iqoo",
            "meizu",
            "letv",
            "360"
        )
    }
}
