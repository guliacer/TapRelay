package com.lan.notify.util

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import android.text.TextUtils
import androidx.core.content.ContextCompat

/**
 * 权限状态检测工具。
 *
 * 检测应用运行所需的关键权限状态，供权限检测界面展示。
 * 自启动权限是厂商定制功能，无标准 API 检测，标记为"需手动检查"。
 */
object PermissionChecker {

    /** 权限状态枚举。 */
    enum class PermissionStatus {
        GRANTED,      // 已授权
        DENIED,       // 未授权
        NOT_APPLICABLE // 当前系统版本不适用
    }

    /** 权限项数据。 */
    data class PermissionItem(
        val key: String,
        val title: String,
        val description: String,
        val status: PermissionStatus,
        /** 跳转到对应权限设置页面的 Intent factory，null 表示无法跳转。 */
        val settingsIntent: (() -> Intent)?
    )

    /**
     * 检测所有权限项，返回列表。
     * 顺序固定：通知权限 → 相机权限 → 电池优化 → 悬浮窗 → 无障碍保活 → 自启动。
     */
    fun checkAll(context: Context): List<PermissionItem> {
        return listOf(
            checkNotificationPermission(context),
            checkCameraPermission(context),
            checkBatteryOptimization(context),
            checkOverlayPermission(context),
            checkAccessibilityService(context),
            checkAutoStart(context)
        )
    }

    /** Android 13+ notification runtime permission; older versions grant it implicitly. */
    private fun checkNotificationPermission(context: Context): PermissionItem {
        val status = if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            PermissionStatus.NOT_APPLICABLE
        } else if (ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
        ) {
            PermissionStatus.GRANTED
        } else {
            PermissionStatus.DENIED
        }

        return PermissionItem(
            key = "notifications",
            title = "通知权限",
            description = "显示任务状态和处理按钮",
            status = status,
            settingsIntent = {
                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                    putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                }
            }
        )
    }

    /** 相机权限：仅用于用户主动点击扫码连接时读取 PC 二维码。 */
    private fun checkCameraPermission(context: Context): PermissionItem {
        val status = if (ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.CAMERA
            ) == PackageManager.PERMISSION_GRANTED
        ) {
            PermissionStatus.GRANTED
        } else {
            PermissionStatus.DENIED
        }

        return PermissionItem(
            key = "camera",
            title = "相机权限",
            description = "扫描 PC 端二维码快速连接",
            status = status,
            settingsIntent = {
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                    data = Uri.parse("package:${context.packageName}")
                }
            }
        )
    }

    /** 电池优化白名单（忽略电池优化）。 */
    private fun checkBatteryOptimization(context: Context): PermissionItem {
        val status = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val powerManager = context.getSystemService(Context.POWER_SERVICE) as PowerManager
            if (powerManager.isIgnoringBatteryOptimizations(context.packageName)) {
                PermissionStatus.GRANTED
            } else {
                PermissionStatus.DENIED
            }
        } else {
            PermissionStatus.NOT_APPLICABLE
        }

        return PermissionItem(
            key = "battery",
            title = "电池优化白名单",
            description = "防止后台冻结断连",
            status = status,
            settingsIntent = {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                        data = Uri.parse("package:${context.packageName}")
                    }
                } else {
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                        data = Uri.parse("package:${context.packageName}")
                    }
                }
            }
        )
    }

    /** 悬浮窗权限（SYSTEM_ALERT_WINDOW），用于悬浮卡片展示。 */
    private fun checkOverlayPermission(context: Context): PermissionItem {
        val status = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            if (Settings.canDrawOverlays(context)) {
                PermissionStatus.GRANTED
            } else {
                PermissionStatus.DENIED
            }
        } else {
            PermissionStatus.NOT_APPLICABLE
        }

        return PermissionItem(
            key = "overlay",
            title = "悬浮窗权限",
            description = "用悬浮卡片展示消息",
            status = status,
            settingsIntent = {
                Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION).apply {
                    data = Uri.parse("package:${context.packageName}")
                }
            }
        )
    }

    /**
     * 无障碍保活服务（AccessibilityService）。
     *
     * 用于提升进程优先级，避免 ColorOS HANS 后台冻结导致 socket 静默切断
     * （详见 09-问题与优化记录-内容.md P016/P028）。本应用只用作保活，不读取任何
     * 窗口内容。状态通过 Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES 检测，
     * 该字段自 API 1 起可用，但只有 API 24+ 起第三方应用可读取自身包名条目。
     */
    private fun checkAccessibilityService(context: Context): PermissionItem {
        val status = if (isAccessibilityServiceEnabled(context)) {
            PermissionStatus.GRANTED
        } else {
            PermissionStatus.DENIED
        }

        return PermissionItem(
            key = "accessibility",
            title = "无障碍保活",
            description = "防止后台冻结断连（ColorOS 推荐）",
            status = status,
            settingsIntent = {
                Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
            }
        )
    }

    /** 判断本应用的 KeepAliveAccessibilityService 是否被用户在系统设置中启用。 */
    private fun isAccessibilityServiceEnabled(context: Context): Boolean {
        val expected = ComponentName(
            context.packageName,
            "com.lan.notify.service.KeepAliveAccessibilityService"
        ).flattenToString()
        val enabled = Settings.Secure.getString(
            context.contentResolver,
            Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
        ) ?: return false
        val splitter = TextUtils.SimpleStringSplitter(':').apply { setString(enabled) }
        while (splitter.hasNext()) {
            if (splitter.next().equals(expected, ignoreCase = true)) return true
        }
        return false
    }

    /**
     * 自启动权限（厂商定制功能）。
     *
     * 没有标准 API 检测自启动权限状态，统一标记为"需手动检查"。
     * 用户点击跳转到厂商自启动设置页确认。
     */
    private fun checkAutoStart(context: Context): PermissionItem {
        val supported = AutoStartHelper.isAutoStartSupported()
        return PermissionItem(
            key = "auto_start",
            title = "自启动权限",
            description = if (supported) "允许开机/后台自启动" else "当前设备可能不支持",
            status = if (supported) PermissionStatus.DENIED else PermissionStatus.NOT_APPLICABLE,
            settingsIntent = {
                val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                intent.data = Uri.parse("package:${context.packageName}")
                intent
            }
        )
    }
}
