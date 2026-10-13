package com.lan.notify.overlay

import android.content.Context
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.ComposeView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.lifecycle.setViewTreeViewModelStoreOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import com.lan.notify.data.MessageRepository
import com.lan.notify.data.NotifyMessage
import com.lan.notify.data.RegisteredApp
import com.lan.notify.ui.TransientTaskHeadsUp
import com.lan.notify.ui.theme.NotifyTheme

/**
 * A short-lived, app-owned heads-up card for an interactive unlocked screen.
 *
 * Native heads-up notifications always reserve a SystemUI app-icon slot which
 * RemoteViews cannot cover. This controller renders the approved source/action
 * card in an overlay instead, while the silent status-bar notification remains
 * available underneath for history and lock-screen delivery.
 */
class TransientTaskHeadsUpController(private val context: Context) :
    LifecycleOwner,
    ViewModelStoreOwner,
    SavedStateRegistryOwner {

    private val lifecycleRegistry = LifecycleRegistry(this)
    private val savedStateController = SavedStateRegistryController.create(this)
    private val store = ViewModelStore()
    private val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())

    override val lifecycle: Lifecycle get() = lifecycleRegistry
    override val viewModelStore: ViewModelStore get() = store
    override val savedStateRegistry: SavedStateRegistry get() = savedStateController.savedStateRegistry

    private val currentMessage = mutableStateOf<NotifyMessage?>(null)
    private val registeredApps = mutableStateOf<Map<String, RegisteredApp>>(emptyMap())
    private var composeView: ComposeView? = null
    private var onDismiss: ((NotifyMessage) -> Unit)? = null
    private var onResolveApproval: ((NotifyMessage, String) -> Unit)? = null
    private var onProcessWaiting: ((NotifyMessage) -> Unit)? = null
    private var onAutoDismiss: (() -> Unit)? = null
    private val autoDismissRunnable = Runnable {
        hide()
        onAutoDismiss?.invoke()
    }

    init {
        savedStateController.performAttach()
        savedStateController.performRestore(null)
        lifecycleRegistry.currentState = Lifecycle.State.CREATED
    }

    fun show(
        message: NotifyMessage,
        apps: Map<String, RegisteredApp>,
        onDismiss: (NotifyMessage) -> Unit,
        onAutoDismiss: () -> Unit,
        onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
        onProcessWaiting: ((NotifyMessage) -> Unit)? = null
    ): Boolean {
        checkMainThread("show")
        if (!Settings.canDrawOverlays(context)) return false

        currentMessage.value = message
        registeredApps.value = apps
        this.onDismiss = onDismiss
        this.onResolveApproval = onResolveApproval
        this.onProcessWaiting = onProcessWaiting
        this.onAutoDismiss = onAutoDismiss

        if (composeView == null) {
            attach()
        } else {
            windowManager.updateViewLayout(composeView, layoutParams())
        }
        handler.removeCallbacks(autoDismissRunnable)
        handler.postDelayed(autoDismissRunnable, AUTO_DISMISS_MS)
        return composeView != null
    }

    fun hide() {
        checkMainThread("hide")
        handler.removeCallbacks(autoDismissRunnable)
        composeView?.let { view ->
            runCatching { windowManager.removeView(view) }
        }
        composeView = null
        currentMessage.value = null
        lifecycleRegistry.currentState = Lifecycle.State.CREATED
    }

    fun destroy() {
        checkMainThread("destroy")
        hide()
        lifecycleRegistry.currentState = Lifecycle.State.DESTROYED
        store.clear()
    }

    private fun attach() {
        val view = ComposeView(context).apply {
            setViewTreeLifecycleOwner(this@TransientTaskHeadsUpController)
            setViewTreeViewModelStoreOwner(this@TransientTaskHeadsUpController)
            setViewTreeSavedStateRegistryOwner(this@TransientTaskHeadsUpController)
            setContent {
                val themeMode by MessageRepository.themeMode.collectAsState()
                NotifyTheme(themeMode = themeMode) {
                    val message = currentMessage.value ?: return@NotifyTheme
                    TransientTaskHeadsUp(
                        message = message,
                        registeredApps = registeredApps.value,
                        onDismiss = { dismissCurrent(message) },
                        onResolveApproval = onResolveApproval,
                        onProcessWaiting = onProcessWaiting
                    )
                }
            }
        }
        try {
            windowManager.addView(view, layoutParams())
            composeView = view
            lifecycleRegistry.currentState = Lifecycle.State.RESUMED
        } catch (_: WindowManager.BadTokenException) {
            lifecycleRegistry.currentState = Lifecycle.State.CREATED
        }
    }

    private fun dismissCurrent(message: NotifyMessage) {
        hide()
        onDismiss?.invoke(message)
    }

    private fun layoutParams(): WindowManager.LayoutParams {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }
        return WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            y = statusBarHeightPx() + dpToPx(8)
        }
    }

    private fun statusBarHeightPx(): Int {
        val resourceId = context.resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (resourceId == 0) 0 else context.resources.getDimensionPixelSize(resourceId)
    }

    private fun dpToPx(value: Int): Int =
        (value * context.resources.displayMetrics.density).toInt()

    private fun checkMainThread(action: String) {
        check(Looper.myLooper() == Looper.getMainLooper()) {
            "TransientTaskHeadsUpController.$action must run on the main thread"
        }
    }

    private companion object {
        const val AUTO_DISMISS_MS = 8_000L
    }
}
