# TapRelay PC 端非对称轻量工作台 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 TapRelay PC 端主窗口与主要弹窗重做为“非对称轻量工作台”，突出手机连接与服务状态，提供可持久化的简洁视图，同时保持通知、隧道、二维码和托盘行为不变。

**Architecture:** 复用现有 WinForms 控件、集中式 `MainForm.RecalculateLayout`、`VisualCardPanel`、`StatusStripPanel`、`UiButtonStyler` 和主题切换。通过新增 UI 状态字段控制简洁视图，标准视图和简洁视图只切换控件可见性与布局，不创建第二套窗口或通信链路。弹窗继续使用各自的布局，但统一消费 `UiTokens` 与按钮样式。

**Tech Stack:** .NET Core 3.1、WinForms、GDI+ 自绘面板、`System.Text.Json` UI 状态存储、现有 `UiButtonStyler`。

## Global Constraints

- 服务端 HTTP/WebSocket 端口固定为 `1122`。
- PC 正式入口固定为 `W:\TapRelay\pc-app\publish\TapRelay.exe`。
- 不改变现有通知、二维码、隧道检测/修复、ComfyUI、托盘退出和 Android 通信逻辑。
- 不新增公网服务、账号、鉴权、云链路或手机可见端口。
- 所有可见文案使用中文；源码标识符使用英文 ASCII。
- 不新增测试源码；验证使用 `dotnet build`、`git diff --check`、`tools\package-pc.ps1` 和 `http://127.0.0.1:1122/health`。

---

### Task 1: 统一 PC UI 令牌与简洁视图状态

**Files:**
- Modify: `W:\TapRelay\pc-app\UiTokens.cs`
- Modify: `W:\TapRelay\pc-app\UiStateStore.cs`
- Modify: `W:\TapRelay\pc-app\MainForm.cs`

**Interfaces:**
- `UiTokens` 继续提供 `PageMargin`、`CardPadding`、`SectionGap`、`CardRadius`、`ButtonRadius`、`FieldRadius` 等现有常量名称，布局代码只引用这些常量。
- `UiState` 新增 `bool CompactMode { get; set; }`，旧的 `ui-state.json` 缺少该字段时默认为 `false`。
- `MainForm` 新增 `_compactMode` 字段和 `ToggleCompactMode()`，切换后更新 `_uiState.CompactMode`、调用 `UiStateStore.Save` 和 `RecalculateLayout()`。

- [ ] **Step 1: 更新尺寸令牌**

  在 `UiTokens.cs` 将页面和模块令牌调整为：`PageMargin=24`、`CardPadding=20`、`SectionGap=16`、`RowGap=12`、`CardRadius=14`、`StatusStripRadius=14`、`ButtonRadius=10`、`FieldRadius=10`、`ChipRadius=10`；保留 `MainButtonHeight=40`、`DialogButtonHeight=32` 的既有语义。

- [ ] **Step 2: 增加简洁视图状态**

  在 `UiState.cs` 的 `UiState` 类型中加入：

  ```csharp
  public bool CompactMode { get; set; }
  ```

  在 `MainForm` 构造布局完成后读取 `_compactMode = _uiState.CompactMode;`，不得把缺失字段当成异常。

- [ ] **Step 3: 增加切换入口**

  在 `_serviceMoreMenu` 中增加 `ToolStripMenuItem`，文案随状态显示为“简洁视图”或“标准视图”，点击调用 `ToggleCompactMode()`。主题切换和窗口关闭前的 UI 状态保存要同时保存 `CompactMode`。

- [ ] **Step 4: 编译验证令牌与状态改动**

  Run: `dotnet build W:\TapRelay\pc-app\TapRelay.csproj -c Debug --no-restore`

  Expected: exit code 0，0 errors；此阶段不打包正式目录。

### Task 2: 重排主窗口为非对称轻量工作台

**Files:**
- Modify: `W:\TapRelay\pc-app\MainForm.cs`
- Modify: `W:\TapRelay\pc-app\NotificationAppsPanel.cs`（仅调整芯片密度和主题色，不改变应用识别）

**Interfaces:**
- 继续由 `RecalculateLayout()` 调用 `RecalculateLayoutCore()` 和 `LayoutNotificationAndLogCards()`，避免增加第二套布局入口。
- `_serviceCard` 继续承载连接地址和测试通知；`_notificationCard` 继续承载应用芯片；`_logCard` 继续承载运行记录。
- `CompactMode=true` 时，应用管理内容隐藏但不销毁，日志保持一行摘要可见。

- [ ] **Step 1: 调整顶部状态条**

  将状态条高度提高到令牌值，保留品牌、服务状态、端口、手机数量和二维码/主题/更多按钮；调整 `StatusStripPanel.OnPaint` 的字号、垂直居中和内容区右边界，确保 100% 与高 DPI 下文案不压住图标。

- [ ] **Step 2: 强化手机连接主模块**

  在 `RecalculateLayoutCore()` 中让地址输入框占服务卡第一主行，复制/脱敏按钮和启动/停止按钮保持同一基线；把卡片标题、地址标题和说明分成清晰的标题层级，并让二维码入口继续位于状态条右侧。

- [ ] **Step 3: 把测试通知变成次级工具区**

  保留 `_messageBox` 和 `_sendButton` 的现有事件，使用较轻的背景和次级标题；发送按钮继续使用 `UiButtonStyler.Primary`，停止按钮继续使用 `Danger`，不增加确认弹窗。

- [ ] **Step 4: 减少通知应用卡的视觉重量**

  调整 `NotificationAppsPanel` 的芯片内边距、圆角、间距和安装/未安装分组背景，让标题右侧“自动检测”和“应用选择”对齐；不修改 `RunAutoDetectAsync`、应用注册和启用状态逻辑。

- [ ] **Step 5: 保持日志抽屉轻量**

  折叠态继续显示级别点、最新日志和条数；增强标题行的 hover/点击反馈，展开态使用统一输入区域背景和圆角。日志内容顺序、滚动和空状态不变。

- [ ] **Step 6: 实现简洁视图布局分支**

  在 `LayoutNotificationAndLogCards()` 中根据 `_compactMode`：

  - 隐藏 `_notificationCard`、`_notificationAppsPanel`、`_notificationAppsLabel`、`_autoDetectAppsButton`、`_selectAppButton`；
  - 保留 `_logCard` 的折叠摘要，隐藏完整日志正文；
  - 重新计算 `_logCard` 紧跟 `_serviceCard`，不在底部留下无意义空白；
  - 切回标准视图时恢复控件可见性，并重新计算 `MinimumSize`。

- [ ] **Step 7: 编译验证主窗口布局**

  Run: `dotnet build W:\TapRelay\pc-app\TapRelay.csproj -c Debug --no-restore`

  Expected: exit code 0，0 errors，0 warnings。

### Task 3: 统一弹窗的视觉语言

**Files:**
- Modify: `W:\TapRelay\pc-app\ConnectionQrForm.cs`
- Modify: `W:\TapRelay\pc-app\NetworkSettingsForm.cs`
- Modify: `W:\TapRelay\pc-app\PersonalProjectsForm.cs`
- Modify: `W:\TapRelay\pc-app\UiButtonStyler.cs`（仅在现有令牌不足时调整公共圆角/字体映射）

**Interfaces:**
- 保留各窗体构造函数、`DialogResult`、事件和返回数据类型。
- 所有文字按钮继续通过 `UiButtonStyler.Create/Apply` 创建。
- 弹窗主题仍由调用方传入的 `darkMode` 决定。

- [ ] **Step 1: 统一标题区与内容区间距**

  将三个窗体的标题、说明、正文和底部按钮按 `UiTokens` 的 20–24px 内边距、16px 分组间距重排；不改变二维码内容、网络配置字段和项目列表数据。

- [ ] **Step 2: 统一输入框、按钮和卡片圆角**

  替换窗体内重复的字体、边框和圆角常量，使用 `UiTokens.UiFontFamily`、`UiTokens.BodyFontSize`、`UiTokens.FieldRadius` 与 `UiButtonStyler`；危险操作保持描边，主操作保持实心。

- [ ] **Step 3: 增强状态反馈**

  二维码公网检查/修复结果、网络设置校验错误和项目列表空状态使用统一的成功/警告/错误色和文本层级，不改现有业务判断。

- [ ] **Step 4: 编译验证弹窗改动**

  Run: `dotnet build W:\TapRelay\pc-app\TapRelay.csproj -c Debug --no-restore`

  Expected: exit code 0，0 errors，0 warnings。

### Task 4: 设计自检与正式发布验证

**Files:**
- Modify: only files listed in Tasks 1–3; do not touch Android, notify-server, workflow assets, or unrelated rule files.

- [ ] **Step 1: 检查差异范围和空白**

  Run: `git diff --check`

  Expected: no whitespace errors；`git status --short` 只显示当前 UI 改造相关文件和既有未提交文件，不清理其它工作。

- [ ] **Step 2: 执行正式打包**

  Run from PowerShell: `W:\TapRelay\tools\package-pc.ps1`

  Expected: Release build/publish succeeds, fixed output is `W:\TapRelay\pc-app\publish`, the old fixed-path process is restarted, and the script reports `/health` with `status=ok` and `port=1122`.

- [ ] **Step 3: 核对正式入口和新视图状态**

  Run: `Get-Process -Name TapRelay | Select-Object Id,Path,StartTime,MainWindowTitle,Responding`

  Expected: `Path` is `W:\TapRelay\pc-app\publish\TapRelay.exe`, process responds, and the main window opens with the standard view. The “更多” menu can switch to compact view and the setting persists after restart.

- [ ] **Step 4: Final verification**

  Run: `dotnet build W:\TapRelay\pc-app\TapRelay.csproj -c Debug --no-restore; git diff --check`

  Expected: build exit code 0 with 0 errors/warnings and no diff-check failures.

