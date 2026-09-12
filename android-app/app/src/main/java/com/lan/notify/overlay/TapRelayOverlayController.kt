package com.lan.notify.overlay

import android.content.Context
import android.graphics.PixelFormat
import android.os.Build
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.WindowManager
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.unit.dp
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
import com.lan.notify.data.OverlayPhase
import com.lan.notify.data.RegisteredApp
import com.lan.notify.data.TapRelayOverlayState
import com.lan.notify.data.events
import com.lan.notify.data.phase
import com.lan.notify.ui.TapRelayOverlay
import com.lan.notify.ui.theme.NotifyTheme

/**
 * 灵动岛 + 卡片共用的单一悬浮层宿主。
 *
 * 只维护一个 `TYPE_APPLICATION_OVERLAY` 窗口和一个 Compose Root。
 * Compact / Peek / Expanded 在同一窗口内形变，窗口参数固定为 WRAP_CONTENT + 挖孔对齐，
 * 只有 [OverlayPhase.Hidden] 才真正 `removeView`。
 */
class TapRelayOverlayController(private val context: Context) :
    LifecycleOwner, ViewModelStoreOwner, SavedStateRegistryOwner {

    private val lifecycleRegistry = LifecycleRegistry(this)
    private val savedStateController = SavedStateRegistryController.create(this)
    private val store = ViewModelStore()

    override val lifecycle: Lifecycle get() = lifecycleRegistry
    override val viewModelStore: ViewModelStore get() = store
    override val savedStateRegistry: SavedStateRegistry get() = savedStateController.savedStateRegistry

    private val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager

    private val overlayState = mutableStateOf<TapRelayOverlayState>(TapRelayOverlayState.Hidden)
    private val registeredApps = mutableStateOf<Map<String, RegisteredApp>>(emptyMap())
    private val debugEnabled = mutableStateOf(false)
    private val dynamicEffectsEnabled = mutableStateOf(true)
    private val screenGeometry = mutableStateOf(CutoutGeometryProvider.read(context))
    private var composeView: ComposeView? = null

    private var onOpen: (() -> Unit)? = null
    private var onCardDismiss: ((NotifyMessage) -> Unit)? = null
    private var onDismissAll: (() -> Unit)? = null
    private var onResolveApproval: ((NotifyMessage, String) -> Unit)? = null
    private var onProcessWaiting: ((NotifyMessage) -> Unit)? = null
    private var onContinue: ((NotifyMessage) -> Unit)? = null

    /** 上次展示的卡片数量，用于仅在新增卡片时响铃。 */
    private var lastShownCount = 0

    init {
        savedStateController.performAttach()
        savedStateController.performRestore(null)
        lifecycleRegistry.currentState = Lifecycle.State.CREATED
    }

    fun canShow(): Boolean = Settings.canDrawOverlays(context)

    /**
     * 用目标状态刷新单一悬浮层。返回 false 表示无悬浮窗权限，调用方应降级普通通知。
     * Compact/Peek/Expanded 之间不改 Window 参数；Hidden 才 removeView。
     */
    fun show(
        state: TapRelayOverlayState,
        registeredApps: Map<String, RegisteredApp>,
        onOpen: () -> Unit,
        onCardDismiss: (NotifyMessage) -> Unit,
        onDismissAll: () -> Unit,
        onResolveApproval: ((NotifyMessage, String) -> Unit)? = null,
        onProcessWaiting: ((NotifyMessage) -> Unit)? = null,
        onContinue: ((NotifyMessage) -> Unit)? = null,
        debugEnabled: Boolean = false,
        dynamicEffectsEnabled: Boolean = true
    ): Boolean {
        checkMainThread("show")
        if (!canShow()) return false

        this.onOpen = onOpen
        this.onCardDismiss = onCardDismiss
        this.onDismissAll = onDismissAll
        this.onResolveApproval = onResolveApproval
        this.onProcessWaiting = onProcessWaiting
        this.onContinue = onContinue
        this.registeredApps.value = registeredApps
        this.debugEnabled.value = debugEnabled
        this.dynamicEffectsEnabled.value = dynamicEffectsEnabled
        this.screenGeometry.value = CutoutGeometryProvider.read(context)

        val newCount = state.events().size.coerceAtMost(OVERLAY_CARD_LIMIT)
        val isNewCardArrived = newCount > lastShownCount
        lastShownCount = newCount

        val phase = state.phase()
        if (phase == OverlayPhase.Hidden) {
            overlayState.value = state
            hide()
            return true
        }

        overlayState.value = state
        if (composeView == null) {
            attach()
        } else {
            refreshWindowLayout()
        }
        if (isNewCardArrived && composeView != null) {
            playNotificationSound()
        }
        return composeView != null
    }

    fun hide() {
        checkMainThread("hide")
        composeView?.let { view ->
            runCatching { windowManager.removeView(view) }
                .onFailure { error ->
                    if (error !is IllegalArgumentException) throw error
                }
            lifecycleRegistry.currentState = Lifecycle.State.CREATED
        }
        composeView = null
        lastShownCount = 0
    }

    fun destroy() {
        checkMainThread("destroy")
        hide()
        lifecycleRegistry.currentState = Lifecycle.State.DESTROYED
        store.clear()
    }

    private fun attach() {
        val view = ComposeView(context).apply {
            setViewTreeLifecycleOwner(this@TapRelayOverlayController)
            setViewTreeViewModelStoreOwner(this@TapRelayOverlayController)
            setViewTreeSavedStateRegistryOwner(this@TapRelayOverlayController)
            setContent {
                val themeMode by MessageRepository.themeMode.collectAsState()
                NotifyTheme(themeMode = themeMode) {
                    val geometry = screenGeometry.value
                    TapRelayOverlay(
                        state = overlayState.value,
                        registeredApps = registeredApps.value,
                        capsuleHeight = OverlayPositionCalculator.compactHeightDp(geometry).dp,
                        onOpen = { onOpen?.invoke() },
                        onCardDismiss = { message -> onCardDismiss?.invoke(message) },
                        onDismissAll = { onDismissAll?.invoke() },
                        onResolveApproval = { message, decision -> onResolveApproval?.invoke(message, decision) },
                        onProcessWaiting = { message -> onProcessWaiting?.invoke(message) },
                        onContinue = { message -> onContinue?.invoke(message) },
                        debugEnabled = debugEnabled.value,
                        dynamicEffectsEnabled = dynamicEffectsEnabled.value,
                        screenGeometry = geometry
                    )
                }
            }
        }

        try {
            windowManager.addView(view, layoutParams())
            composeView = view
            lifecycleRegistry.currentState = Lifecycle.State.RESUMED
        } catch (error: WindowManager.BadTokenException) {
            Log.w(TAG, "overlay rejected by system, falling back to notification", error)
            lifecycleRegistry.currentState = Lifecycle.State.CREATED
        }
    }

    /** 旋转/挖孔变化时刷新 x/y，不在 Compact↔Peek↔Expanded 之间改窗口尺寸策略。 */
    private fun refreshWindowLayout() {
        val view = composeView ?: return
        runCatching { windowManager.updateViewLayout(view, layoutParams()) }
            .onFailure { error ->
                if (error !is IllegalArgumentException) throw error
            }
    }

    private fun playNotificationSound() {
        val uri = android.media.RingtoneManager.getActualDefaultRingtoneUri(
            context,
            android.media.RingtoneManager.TYPE_NOTIFICATION
        ) ?: return
        runCatching {
            android.media.RingtoneManager.getRingtone(context, uri)?.play()
        }.onFailure { error ->
            Log.w(TAG, "failed to play notification sound", error)
        }
    }

    private fun checkMainThread(action: String) {
        check(Looper.myLooper() == Looper.getMainLooper()) {
            "TapRelayOverlayController.$action must be called on the main thread"
        }
    }

    private fun layoutParams(): WindowManager.LayoutParams {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }
        val spec = OverlayPositionCalculator.windowSpec(
            screen = screenGeometry.value,
            wrapContent = WindowManager.LayoutParams.WRAP_CONTENT
        )
        return WindowManager.LayoutParams(
            spec.width,
            spec.height,
            type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            x = spec.x
            y = spec.y
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                layoutInDisplayCutoutMode =
                    WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
        }
    }

    companion object {
        private const val TAG = "NotifyOverlay"

        /** 同时展示的悬浮卡片上限（05-UI与组件规范 R6）。 */
        const val OVERLAY_CARD_LIMIT = 5
    }
}
