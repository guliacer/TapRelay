# WorkBuddy 通知适配报告

## 1. 适配范围

本次适配对应 Windows 安装路径：

```text
C:\Program Files\WorkBuddy\WorkBuddy.exe
```

WorkBuddy 实际 ACP 日志位于：

```text
%USERPROFILE%\.workbuddy\logs\**\*.log
```

适配仅处理包含 `outgoingWritable.write called, message=` 的 ACP 输出行，避免将普通运行日志误判为任务通知。

## 2. ACP 事件映射

| WorkBuddy ACP 事件 | TapRelay 状态 | 用户可见通知 |
| --- | --- | --- |
| `session/update` + `update.sessionUpdate=session_end` + `stopReason=end_turn` | `completed` | WorkBuddy 任务已完成 |
| `session/update` + `session_end` + 非 `end_turn` 终止原因，或同文件先出现 `ACP Agent RESULT ... hasError=true` | `failed` | WorkBuddy 任务失败，附带错误原因（如有） |
| `session/request_permission` | `waiting` | WorkBuddy 等待你的操作 |
| `_codebuddy.ai/question` / `elicitation/create` | `waiting` | WorkBuddy 等待你的操作 |
| `session/update` + `tool_call` + `title/name=AskUserQuestion` 等 | `waiting` | WorkBuddy 等待你的操作 |

任务 ID 按 `sessionId + requestId/userMessageId/toolCallId` 优先组合；缺失时回退到日志时间或消息哈希。这保证同一会话的多个任务可以分别通知。

`cancelled`、`canceled`、`abort`、`interrupt` 等用户主动停止原因会被忽略，不伪报为失败。普通 `Read`、`Bash` 等 `tool_call` 不会触发等待通知。

WorkBuddy 日志会对较长的 ACP `tool_call` 行进行截断，解析器同时支持完整 JSON 和包含 `sessionId` / `toolCallId` / `title=AskU` 前缀的截断行。

## 3. 项目集成

- PC 日志根、ACP 解析、文件监控和事件发送：`pc-app/AgentLogPaths.cs`、`pc-app/AgentCompletionEventParser.cs` 、`pc-app/AgentCompletionMonitor.cs`。
- PC 应用检测固定支持 `C:\Program Files\WorkBuddy\WorkBuddy.exe`，同时支持用户 ACP 日志目录。
- PC 通知开关、来源注册、品牌图标和颜色：`pc-app/NotificationAppsPanel.cs` 、`pc-app/NotificationAppRegistry.cs` 、`pc-app/NotifyServer.cs` 、`pc-app/Assets/ic_workbuddy_logo.png`。
- 启动时先完成本地应用自动检测，再加载远程应用，避免远程目录更新短暂隐藏 WorkBuddy 等内置来源。
- Android 渠道、来源归一化、图标和品牌色：`android-app/app/src/main/java/com/lan/notify/data/Channel.kt` 、`ChannelIcon.kt` 、`ui/theme/Color.kt` 、`drawable-nodpi/ic_workbuddy_logo.png`。
- Node 通知服务将 `workbuddy` 列为内置来源，防止被当成未知应用注册：`notify-server/src/apps.js` 。

## 4. 验证结果

- `TapRelay.AgentMonitor.Tests`：93 项通过，包含成功、失败、`hasError` 失败标记、权限请求、问题请求、`AskUserQuestion` 完整/截断日志、取消忽略和普通工具调用过滤。
- PC 主项目构建：0 警告，0 错误。
- `TapRelay.AllAppsE2E` 项目构建：0 警告，0 错误。
- Android `:app:testDebugUnitTest`：构建成功，21 项单测通过。
- `notify-server` Vitest：11 项通过。
- 本机实测已确认安装包存在于 `C:\Program Files\WorkBuddy` 且实际 ACP 日志包含 `session_end/end_turn` 和 `refusal` 结构。

正式 self-contained 包已更新至 `pc-app/publish/TapRelay.exe` 并启动。使用该正式包执行全应用 WebSocket E2E：20 项预期通知全部收到，0 缺失、0 重复、0 误报；其中 WorkBuddy 的 `completed`、`failed`、`waiting` 三类通知均通过，取消事件被忽略。

## 5. GitHub 远程规则与 Android 热更新验证

- 远程规则仓库：`guliacer/TapRelay-remote-adapter-test`，目录版本 `2026.08.04.3`。
- `catalog.json` 已发布 WorkBuddy 的 `workbuddy-acp` 白名单解析器、递归日志路径 `%USERPROFILE%\.workbuddy\logs\*.log` 和 `icons/workbuddy.png`。
- raw GitHub 地址实测返回 `200`；WorkBuddy 图标已校验为 `256x256 PNG`，PC 下载后内联并通过目录校验。
- Android 已安装包通过“适配”按钮检查到“发现 2 个适配规则更新（版本 2026.08.04.3）”，点击“立即更新”后 PC 日志记录 `remote adapter catalog applied: version=2026.08.04.3, adapters=2`。
- 更新后 PC 缓存和元数据均为 `2026.08.04.3`，WorkBuddy 规则已注册；Android `com.lan.notify` 的 `lastUpdateTime` 保持原值，验证无需重新覆盖安装 APK 即可更新适配规则。
