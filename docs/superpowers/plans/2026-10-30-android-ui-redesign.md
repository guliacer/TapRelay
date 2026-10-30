# Android 移动工作台统一界面 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Android 主界面落地为类似微信的持久底栏工作台，用户点击“任务 / ComfyUI / 设备”后立即看到对应页面，并在切换时保留三个页面各自的编辑与滚动状态。

**Architecture:** 在 `MainActivity` 内增加三入口状态和根级 `Scaffold`，使用自定义 Compose 底栏与常驻页面容器渲染三个页面。三个页面同时保留在组合树中，仅切换可见性，避免现有 `remember` 状态在页面切换时被销毁；权限引导和图片预览仍作为临时全屏流程覆盖工作台。设备管理内容从 ModalBottomSheet 抽取成可复用内容，供设备一级页面使用。

**Tech Stack:** Kotlin 2.x、Jetpack Compose、Material 3、现有 `MessageRepository` / `ComfyUiRepository` / Local Store、Gradle Android plugin。

## Global Constraints

- 三个一级入口固定为“任务 / ComfyUI / 设备”，底栏始终显示在主页面底部。
- 底栏切换只改变内容区，不重启 Activity，不清空其它入口的页面状态。
- 现有 WebSocket、通知解析、ComfyUI API、图片缓存、PC 控制和权限逻辑不重写。
- 不新增 Android 导航依赖、账号体系、云服务或网络端口。
- 页面左右内边距 16dp，主要分组间距 20dp，普通卡片圆角 16dp，关键触控区域高度至少 48dp。
- 低频入口（主题、权限、个人项目）从任务页顶栏的“更多”入口进入；不占用底栏一级位置。
- MuMu 验证使用运行时发现的 ADB 序列号，不写死设备序列号。
- 工作流文件继续遵守 `AGENTS.md` 的 `pc-app/Assets/ComfyUIConsole/api_workflows/` 目录规则；本任务不新增工作流文件。

---

### Task 1: 新增一级页面模型和微信式底栏

**Files:**
- Create: `android-app/app/src/main/java/com/lan/notify/ui/AppDestination.kt`
- Create: `android-app/app/src/main/java/com/lan/notify/ui/WorkstationBottomBar.kt`
- Create: `android-app/app/src/test/java/com/lan/notify/ui/AppDestinationTest.kt`

**Interfaces:**
- Produces `AppDestination` with exactly `TASK`, `COMFY_UI`, `DEVICE` values and user-visible labels/content descriptions.
- Produces `WorkstationBottomBar(selected: AppDestination, onSelect: (AppDestination) -> Unit)`; it only emits selection events and does not own navigation state.

- [ ] **Step 1: Add the destination model**

Create `AppDestination.kt` with a stable enum order and presentation metadata:

```kotlin
package com.lan.notify.ui

enum class AppDestination(
    val label: String,
    val contentDescription: String
) {
    TASK("任务", "任务"),
    COMFY_UI("ComfyUI", "ComfyUI 工作流"),
    DEVICE("设备", "设备管理")
}
```

- [ ] **Step 2: Implement the bottom bar**

Create `WorkstationBottomBar.kt` with Material 3 `NavigationBar` and existing drawable/vector resources. Keep the bar state-driven and make the whole item clickable:

```kotlin
@Composable
fun WorkstationBottomBar(
    selected: AppDestination,
    onSelect: (AppDestination) -> Unit,
    modifier: Modifier = Modifier
) {
    NavigationBar(
        modifier = modifier,
        containerColor = appColors.Surface,
        tonalElevation = 0.dp
    ) {
        AppDestination.entries.forEach { destination ->
            NavigationBarItem(
                selected = selected == destination,
                onClick = { onSelect(destination) },
                icon = { DestinationIcon(destination, selected == destination) },
                label = { Text(destination.label) },
                alwaysShowLabel = true,
                colors = NavigationBarItemDefaults.colors(
                    selectedIconColor = appColors.OnPrimary,
                    selectedTextColor = appColors.PrimarySageStrong,
                    indicatorColor = appColors.PrimarySage,
                    unselectedIconColor = appColors.TextSecondary,
                    unselectedTextColor = appColors.TextSecondary
                )
            )
        }
    }
}
```

`DestinationIcon` 使用 `ic_stat_comfyui` 作为 ComfyUI 图标，任务和设备使用已有 Material 图标或本地线性 Vector；每个图标必须提供 `contentDescription`。

- [ ] **Step 3: Add a focused unit test**

Create `AppDestinationTest.kt` and verify the public model used by the shell:

```kotlin
class AppDestinationTest {
    @Test
    fun destinationsKeepStableUserOrder() {
        assertEquals(
            listOf(AppDestination.TASK, AppDestination.COMFY_UI, AppDestination.DEVICE),
            AppDestination.entries
        )
        assertEquals("任务", AppDestination.TASK.label)
        assertEquals("ComfyUI", AppDestination.COMFY_UI.label)
        assertEquals("设备", AppDestination.DEVICE.label)
    }
}
```

- [ ] **Step 4: Run the focused test**

Run from `W:\TapRelay\android-app`:

```powershell
.\gradlew.bat :app:testDebugUnitTest --tests com.lan.notify.ui.AppDestinationTest
```

Expected: the new test passes and no Android source is changed by the test task.

- [ ] **Step 5: Commit the isolated shell primitives**

```powershell
git add -- android-app/app/src/main/java/com/lan/notify/ui/AppDestination.kt android-app/app/src/main/java/com/lan/notify/ui/WorkstationBottomBar.kt android-app/app/src/test/java/com/lan/notify/ui/AppDestinationTest.kt
git commit -m "feat(android): add workstation destinations and bottom bar"
```

### Task 2: 抽取设备管理内容为可复用页面

**Files:**
- Create: `android-app/app/src/main/java/com/lan/notify/ui/DeviceScreen.kt`
- Modify: `android-app/app/src/main/java/com/lan/notify/ui/DeviceControlSheet.kt:137-end`

**Interfaces:**
- `DeviceControlContent(...)` owns the existing device form, host history subview, and confirmation dialogs without owning a `ModalBottomSheet`.
- `DeviceScreen(...)` provides the full-page `Scaffold` and calls `DeviceControlContent`.
- Existing `DeviceControlSheet(...)` remains available for any transient caller and wraps the same content, but the main shell uses `DeviceScreen`.

- [ ] **Step 1: Extract the sheet body without changing callbacks**

Move the current `ModalBottomSheet` body in `DeviceControlSheet.kt` into `DeviceControlContent` with the same parameters currently accepted by `DeviceControlSheet`, excluding `onDismiss`. Keep `showHostHistory`, `confirmClearHistory`, address masking, connection actions, power confirmations, and permission action unchanged. The content root must be:

```kotlin
Column(
    modifier = modifier
        .fillMaxWidth()
        .verticalScroll(rememberScrollState())
        .padding(horizontal = 20.dp, bottom = 28.dp),
    verticalArrangement = Arrangement.spacedBy(12.dp)
) { /* existing sections */ }
```

Do not pass the masked address back through `onServerHostChange`; preserve the existing read-only rule.

- [ ] **Step 2: Make the old sheet a thin wrapper**

Keep `DeviceControlSheet` as a compatibility wrapper:

```kotlin
ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
    DeviceControlContent(
        modifier = Modifier,
        uiState = uiState,
        deviceName = deviceName,
        onDeviceNameChange = onDeviceNameChange,
        onServerHostChange = onServerHostChange,
        dynamicEffectsEnabled = dynamicEffectsEnabled,
        onDynamicEffectsChange = onDynamicEffectsChange,
        onScanQr = onScanQr,
        onConnect = onConnect,
        onDisconnect = onDisconnect,
        onSleep = onSleep,
        onShutdown = onShutdown,
        onOpenPermissionCheck = onOpenPermissionCheck,
        hostHistory = hostHistory,
        hostMasked = hostMasked,
        onHostMaskedChange = onHostMaskedChange,
        onClearHostHistory = onClearHostHistory
    )
}
```

- [ ] **Step 3: Add the full device page**

Create `DeviceScreen.kt` with a top bar and the extracted content:

```kotlin
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceScreen(/* same device arguments, no onDismiss */) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("设备") },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = appColors.TopBarSage,
                    titleContentColor = appColors.TopBarSageContent
                )
            )
        }
    ) { padding ->
        DeviceControlContent(
            modifier = Modifier.padding(padding),
            /* forward the unchanged device arguments */
        )
    }
}
```

The full page must not show a “完成” action or a second modal scrim; system back is handled by the root shell and remains on the Device tab.

- [ ] **Step 4: Compile the device extraction**

```powershell
.\gradlew.bat :app:compileDebugKotlin
```

Expected: compilation succeeds with the old sheet and new full page both referencing the extracted content.

- [ ] **Step 5: Commit the reusable device page**

```powershell
git add -- android-app/app/src/main/java/com/lan/notify/ui/DeviceControlSheet.kt android-app/app/src/main/java/com/lan/notify/ui/DeviceScreen.kt
git commit -m "feat(android): expose device management as a page"
```

### Task 3: 建立常驻页面壳层并接入 MainActivity

**Files:**
- Create: `android-app/app/src/main/java/com/lan/notify/ui/WorkstationShell.kt`
- Modify: `android-app/app/src/main/java/com/lan/notify/MainActivity.kt:88-160`

**Interfaces:**
- `WorkstationShell(active: AppDestination, onSelect: (AppDestination) -> Unit, task: @Composable () -> Unit, comfyUi: @Composable () -> Unit, device: @Composable () -> Unit)` renders a root `Scaffold` and keeps all three page slots mounted.
- `MainActivity` owns `activeDestination`, permission overlay state, and the callbacks that switch tabs.

- [ ] **Step 1: Add a persistent page host**

Create `WorkstationShell.kt`. The hidden page slots must remain composed so existing `remember` values in `MainScreen` and the large `ComfyUiScreen` survive tab switching:

```kotlin
@Composable
fun WorkstationShell(
    active: AppDestination,
    onSelect: (AppDestination) -> Unit,
    task: @Composable () -> Unit,
    comfyUi: @Composable () -> Unit,
    device: @Composable () -> Unit
) {
    Scaffold(
        bottomBar = {
            WorkstationBottomBar(selected = active, onSelect = onSelect)
        },
        containerColor = appColors.Background
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            PersistentSlot(AppDestination.TASK, active) { task() }
            PersistentSlot(AppDestination.COMFY_UI, active) { comfyUi() }
            PersistentSlot(AppDestination.DEVICE, active) { device() }
        }
    }
}

@Composable
private fun PersistentSlot(
    destination: AppDestination,
    active: AppDestination,
    content: @Composable () -> Unit
) {
    val visible = destination == active
    Box(
        modifier = Modifier
            .fillMaxSize()
            .zIndex(if (visible) 1f else 0f)
            .alpha(if (visible) 1f else 0f)
            .then(if (visible) Modifier else Modifier.clearAndSetSemantics {})
    ) { content() }
}
```

Use `zIndex` so the active page receives input. Keep the hidden pages out of accessibility traversal with `clearAndSetSemantics`; do not dispose them with `if (active == ...)`.

- [ ] **Step 2: Replace the two boolean roots in MainActivity**

Inside `setContent`, replace `var showComfyUi` with:

```kotlin
var activeDestination by rememberSaveable { mutableStateOf(AppDestination.TASK) }
```

Keep `showPermissionCheck` as the only full-screen route. When onboarding or permission check is visible, render `PermissionCheckScreen` without the shell. Otherwise render `WorkstationShell`, passing the existing collected `uiState`, repository callbacks, and page content lambdas:

```kotlin
WorkstationShell(
    active = activeDestination,
    onSelect = { activeDestination = it },
    task = {
        MainScreen(
            /* existing arguments */
            onOpenDeviceManagement = { activeDestination = AppDestination.DEVICE },
            onOpenComfyUi = { activeDestination = AppDestination.COMFY_UI },
            onOpenPermissionCheck = { showPermissionCheck = true }
        )
    },
    comfyUi = {
        ComfyUiScreen(
            serverHost = uiState.serverHost,
            embedded = true,
            onBack = { activeDestination = AppDestination.TASK }
        )
    },
    device = {
        DeviceScreen(
            /* the existing device and power callback arguments */
        )
    }
)
```

Do not call `BackHandler` to leave the ComfyUI or Device tab from the root page. ComfyUI keeps its own internal back handling for selected workflow, result, gallery, and image dialogs; the root system back returns to Task only when a transient page state is closed.

- [ ] **Step 3: Remove the main Activity device sheet route**

Delete `showDeviceControlSheet` state and the branch that renders `DeviceControlSheet` from `MainScreen`. The connection summary callback now selects `AppDestination.DEVICE`. Leave `DeviceControlSheet` available only as a reusable wrapper for future transient callers.

- [ ] **Step 4: Run compilation and the focused unit test**

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
```

Expected: unit tests pass and `android-app/app/build/outputs/apk/debug/app-debug.apk` exists.

- [ ] **Step 5: Commit the shell integration**

```powershell
git add -- android-app/app/src/main/java/com/lan/notify/MainActivity.kt android-app/app/src/main/java/com/lan/notify/ui/WorkstationShell.kt
git commit -m "feat(android): add persistent workstation shell"
```

### Task 4: 统一任务页顶栏和低频入口

**Files:**
- Modify: `android-app/app/src/main/java/com/lan/notify/ui/MainScreen.kt:81-180`
- Create: `android-app/app/src/main/java/com/lan/notify/ui/MoreActionsMenu.kt`

**Interfaces:**
- `MoreActionsMenu` receives theme mode, theme callback, project callback, permission callback, and dismiss callback.
- `MainScreen` keeps message rendering and actions intact while replacing the ComfyUI top icon and separate low-frequency icons with one “更多” action.

- [ ] **Step 1: Add a compact more menu**

Create `MoreActionsMenu.kt` with a three-dot icon button and a `DropdownMenu` containing exactly:

```kotlin
DropdownMenuItem(
    text = { Text(if (themeMode == ThemeMode.DARK) "切换浅色模式" else "切换深色模式") },
    onClick = { onToggleTheme(); onDismiss() }
)
DropdownMenuItem(text = { Text("个人项目") }, onClick = { onOpenProjects(); onDismiss() })
DropdownMenuItem(text = { Text("权限检测") }, onClick = { onOpenPermissions(); onDismiss() })
```

The menu uses `appColors.Surface`, `appColors.TextPrimary`, and `appColors.BorderSoft`; the icon and every menu action provide content descriptions or visible text.

- [ ] **Step 2: Update the MainScreen top bar**

Remove the top-level ComfyUI icon and separate project/theme icon buttons. Add `MoreActionsMenu` as the final top bar action. Keep the title and light/dark top bar contrast logic. The existing `onOpenComfyUi` callback may remain as a compatibility parameter but the primary ComfyUI route is the bottom bar.

- [ ] **Step 3: Improve the task empty state and spacing**

Replace the bare “暂无消息” text with a centered surface card containing the title, connection hint, and the existing “扫码连接”/“重新连接” callback when applicable. Keep the existing time/project list modes, approval actions, message read behavior, and back-to-top button. Apply 16dp page padding and 20dp section spacing without changing message data or ordering.

- [ ] **Step 4: Compile the task page**

```powershell
.\gradlew.bat :app:compileDebugKotlin
```

Expected: `MainScreen` compiles with the new more menu and no removed callback is referenced.

- [ ] **Step 5: Commit task-page polish**

```powershell
git add -- android-app/app/src/main/java/com/lan/notify/ui/MainScreen.kt android-app/app/src/main/java/com/lan/notify/ui/MoreActionsMenu.kt
git commit -m "feat(android): simplify task page actions"
```

### Task 5: 让 ComfyUI 适配底栏嵌入模式

**Files:**
- Modify: `android-app/app/src/main/java/com/lan/notify/ui/ComfyUiScreen.kt:170-185,456-470,682-755`

**Interfaces:**
- Extend `ComfyUiScreen` with `embedded: Boolean = false`.
- Existing standalone callers keep the current close/back behavior; the shell calls `embedded = true` so the workflow list does not expose a “关闭” action that conflicts with the bottom bar.

- [ ] **Step 1: Add the embedded parameter**

Change the signature to:

```kotlin
fun ComfyUiScreen(
    serverHost: String,
    onBack: () -> Unit,
    embedded: Boolean = false
)
```

- [ ] **Step 2: Keep internal back states and suppress only the root close action**

Keep `navigateBack()` behavior for gallery, result, and selected workflow. In the top bar navigation icon, render the existing “返回” action for those internal states. When `selected == null`, `showResults == false`, and `showGallery == false`, render no navigation icon if `embedded` is true; render the existing “关闭” action when `embedded` is false.

- [ ] **Step 3: Verify ComfyUI state survives tab switching**

Do not replace `remember` with newly created screen instances or move repository state into a new network layer. The shell’s persistent slots keep this existing Composable mounted while the user visits Task or Device. While a workflow is running, switching tabs must leave polling and result collection active.

- [ ] **Step 4: Compile and run the ComfyUI-related unit tests**

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:compileDebugKotlin
```

Expected: all existing tests pass and no ComfyUI state or repository APIs are changed.

- [ ] **Step 5: Commit the embedded ComfyUI behavior**

```powershell
git add -- android-app/app/src/main/java/com/lan/notify/ui/ComfyUiScreen.kt
git commit -m "feat(android): embed ComfyUI in workstation tabs"
```

### Task 6: MuMu 实机安装、截图和回归验证

**Files:**
- Modify: none
- Test artifacts: screenshots stored outside the repository, for example `C:\Users\Admin\Desktop\TapRelay-mumu-ui\`

**Interfaces:**
- Uses the debug APK from Task 3 and the runtime-discovered ADB serial.
- Produces a verification table with build version, serial, resolution, and pass/fail evidence for the scenarios in the design spec.

- [ ] **Step 1: Build the final debug APK**

From `W:\TapRelay\android-app` run:

```powershell
.\gradlew.bat :app:assembleDebug
```

Expected: `app\build\outputs\apk\debug\app-debug.apk` is generated.

- [ ] **Step 2: Discover MuMu without assuming a serial**

```powershell
$adb = (Get-Command adb -ErrorAction Stop).Source
& $adb start-server | Out-Null
$device = (& $adb devices | Select-String "\tdevice$").ToString().Split("`t")[0]
if ([string]::IsNullOrWhiteSpace($device)) { throw "没有发现处于 device 状态的 MuMu ADB 设备" }
Write-Output "MuMu serial: $device"
```

If no device is found, stop verification and report that MuMu must be started or its ADB connection enabled; do not claim the UI passed on source inspection alone.

- [ ] **Step 3: Install and launch**

```powershell
& $adb -s $device install -r .\app\build\outputs\apk\debug\app-debug.apk
& $adb -s $device shell am force-stop com.lan.notify
& $adb -s $device shell monkey -p com.lan.notify 1
```

Capture the device size and a baseline screenshot:

```powershell
& $adb -s $device shell wm size
& $adb -s $device exec-out screencap -p > C:\Users\Admin\Desktop\TapRelay-mumu-ui\01-launch.png
```

- [ ] **Step 4: Exercise the three bottom tabs**

Use `adb shell input tap` coordinates based on the reported screen width and bottom navigation bounds. Confirm each tap immediately displays the matching page, the selected tab is highlighted, and the bottom bar remains visible. On ComfyUI, select a workflow and edit one parameter, switch to Device and back, and verify the selection/value remains. On Task, scroll the message list, switch tabs, and verify the scroll position remains.

- [ ] **Step 5: Exercise overlay and error paths**

Verify permission check, more menu, personal projects, dark/light theme, address masking, device history, back handling, workflow loading failure, run failure, result preview, and image editor. Confirm overlays hide the bottom bar only while they are visible, then restore the previous tab.

- [ ] **Step 6: Review screenshots and diagnostics**

Use the local image viewer on the captured PNGs. Check that no text is clipped, no page is covered by the bottom bar, 360dp-width content has no horizontal overflow, and hidden tabs are not exposed to TalkBack. Record any issue before claiming completion.

- [ ] **Step 7: Final source checks**

```powershell
git diff --check
git status --short
```

Only report completion after the build, focused tests, MuMu interaction matrix, screenshot review, and source checks all pass. Existing unrelated worktree changes must remain untouched.
