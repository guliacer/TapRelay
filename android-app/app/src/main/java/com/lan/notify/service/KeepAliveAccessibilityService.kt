package com.lan.notify.service

import android.accessibilityservice.AccessibilityService
import android.util.Log
import android.view.accessibility.AccessibilityEvent

/**
 * 进程保活用的无障碍服务。
 *
 * 仅用于让系统把本应用进程视为关键服务（绑定到 system_server），
 * 从而避免 ColorOS HANS 在后台冻结进程导致 socket 静默切断
 * （详见 09-问题与优化记录-内容.md P016/P028）。
 *
 * 不读取任何窗口内容、不监听任何业务事件、不执行任何业务逻辑。
 * 用户在系统设置中授权后即生效，无需在本服务内做任何动作。
 */
class KeepAliveAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        Log.i(TAG, "keep-alive accessibility service connected")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // 不处理任何事件，仅作保活
    }

    override fun onInterrupt() {
        Log.i(TAG, "keep-alive accessibility service interrupted")
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        Log.i(TAG, "keep-alive accessibility service unbound")
        return false
    }

    companion object {
        private const val TAG = "KeepAliveA11y"
    }
}
