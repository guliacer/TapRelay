# TapRelay（LAN Notify / 局域网通知）项目说明

> 面向优化智能体的完整背景文档。阅读后应能理解「项目做什么、怎么运转、UI 在哪里」。

## 1. 项目定位

**一句话**：把 PC 端 AI Agent（WorkBuddy、豆包 / Doubao 等）的任务状态与完成/待处理事件，实时推送到 Android 手机，并支持在手机上**远程处理**（允许/拒绝），实现「PC 干活、手机掌控」。

**核心价值**：用户离开电脑时，手机仍能收到 Agent 完成通知；当 Agent 需要用户确认（如批量删除、权限申请），手机直接弹出允许/拒绝按钮，点击后回传 PC 自动处置，无需回到电脑前。

**零配置/傻瓜式原则**（项目最高质量标准）：首次安装即开箱即用，自动防火墙放行、开机自启、端口冲突自动切换、首次启动权限向导、断线自动重连——用户不应做任何手动配置。

## 2. 技术栈与部署形态

| 端 | 技术 | 关键实现 |
|---|---|---|
| PC 端 | C# / WinForms | `TapRelay.exe`，内置 WebSocket 服务，默认端口 **1122** |
| Android 端 | Kotlin + Jetpack Compose | `com.lan.notify` App |
| 传输 | WebSocket | 局域网直连 + Cloudflare Tunnel 公网（`wss://win.guliacer.dpdns.org:443`） |
| 扫码建链 | zxing-android-embedded | PC 生成二维码 → 手机扫码 → WebSocket 建链 |
| 隧道检测 | 6 种工具 | Cloudflare Tunnel / frp / ngrok / cpolar / Tailscale / ZeroTier |
| 服务端 | Ubuntu 192.168.5.112（guliacer.dpdns.org） | 两套 Cloudflare Tunnel 命名空间分离：`guliacer`(Windows) / `ubuntu-home`(Ubuntu) |

## 3. 核心功能模块

1. **连接管理**
   - 局域网扫码直连、公网隧道连接
   - 端口冲突自动切换至可用端口
   - 首次启动自动防火墙放行、开机自启
   - 断线自动重连

2. **Agent 事件监控（PC 端）**
   - `AgentCompletionMonitor` 用 `FileSystemWatcher` 递归监听 WorkBuddy 日志目录（`.workbuddy/logs/**/*.log`）
   - 解析 Agent 完成事件，适配三态：**completed / failed / waiting**
   - `waiting` 信号：日志行 `[SessionManager] task state transition {"signal":"awaiting_input",...}`（每轮等待唯一）
   - 去重：`_workbuddyWaitingSessions`(session 级) + EventKey 去重，避免重复通知

3. **通知广播（PC → 手机）**
   - `NotifyServer` 经 WebSocket 推送 `MessagePayload`：`source / status / taskId / title / body / requestId`
   - `waiting` 事件携带 `requestId = "workbuddy:<sessionId>"` 用于回传路由

4. **手机通知面（四层渲染）**
   - 状态栏 heads-up（临时横幅）
   - 全屏提醒 `FullScreenAlertActivity`
   - 浮窗 Expanded 卡 `OverlayCards`
   - App 内列表卡 `MainScreen` / `ProjectGroupCard`
   - 四层在 `requiresAction && requestId 非空` 时统一显示**允许/拒绝**按钮

5. **远程审批（手机 → PC）**
   - 手机点允许/拒绝 → 发送 `ACTION_RESOLVE_APPROVAL` intent → `WebSocketService` 回传
   - PC `NotifyServer` 收到 `workbuddy:` 前缀 → `ResolveWorkBuddyWaiting`：遍历 WorkBuddy 进程置前窗口 + `SendKeys.SendWait(Enter/Esc)`
   - 去重保证只处置一次

## 4. 端到端链路（以 WorkBuddy 等待确认为例）

```
PC: WorkBuddy 弹出确认框
  → 日志写入 awaiting_input
  → AgentCompletionMonitor 解析 (status=waiting, requestId=workbuddy:<sid>)
  → NotifyServer 广播 (WebSocket)
  → 手机四层通知面显示「允许 / 拒绝」按钮
  → 用户点「允许」
  → WebSocketService 回传 approval resolve (decision=allow)
  → PC NotifyServer.ResolveWorkBuddyWaiting
  → 置前 WorkBuddy 窗口 + SendKeys Enter 完成处置
```

## 5. UI 布局（Android App 主屏 = `MainScreen.kt` Compose 单文件）

```
顶部状态栏：时间 | 5G | 电池
标题栏：任务通知 | 设置/主题图标
        └─ 待处理胶囊：🔔 待处理 9
连接状态卡片：
  ├─ 头部：连接状态 默认端口：1122 | 已连接（绿）
  ├─ PC 地址输入框（值 10.0.2.2）
  └─ 按钮矩阵（3 列）
     ├ 连接 | 断开 | 扫码连接
     ├ 隐藏 IP | 权限 | 省电
     └ 睡眠 | 关机
消息历史区域：
  ├─ 筛选栏：时间 | 应用 | 9条未读 | 全部已读 | 清空历史
  └─ 卡片列表（垂直滚动）
     └─ 单卡：应用图标+未读点 | 来源+状态摘要 | 时间 | 右侧操作按钮(继续/关闭，waiting 时允许/拒绝)
```

### 实际布局文件清单

**App 内主界面（Compose）**
- `ui/MainScreen.kt` — 主屏（截图对应页，承载连接卡片 + 消息历史）
- `ui/OverlayCards.kt` — 浮窗卡片（含允许/拒绝）
- `ui/TapRelayOverlay.kt` — 浮窗容器
- `ui/Island.kt` — 灵动岛样式
- `ui/TransientTaskHeadsUp.kt` — 临时 heads-up
- `ui/AppPresentation.kt` — 应用级路由
- `ui/PermissionCheckScreen.kt` — 首次权限向导
- `ui/PersonalProjectsDialog.kt` — 项目选择弹窗
- `ui/PlainButton.kt` `ThemeIconButton.kt` `ChannelIcon.kt` — 原子组件

**通知/弹窗（XML，`res/layout`）**
- `notification_summary_collapsed/expanded.xml` — 通知摘要
- `notification_group_collapsed/expanded.xml` — 分组
- `notification_task_collapsed/expanded.xml` — 任务卡片
- `scan_capture.xml` — 扫码界面

**系统层（Compose）**
- `notification/FullScreenAlertActivity.kt` — 全屏提醒（含允许/拒绝）
- `overlay/TapRelayOverlayController.kt` — 浮窗控制
- `MainActivity.kt` — 入口，挂载 MainScreen

## 6. 当前已知优化点（供智能体参考）

- 主屏连接卡片按钮 3 列网格，宽度利用不充分，可改为自适应/分组。
- 「待处理 9」胶囊与标题栏视觉权重可再平衡。
- 消息卡片右侧操作按钮需确保 waiting 状态正确显示允许/拒绝（已实装，待回归验证）。
- PC 端合成日志注入测试受文件锁限制，端到端自动化测试链路仍有改进空间。
