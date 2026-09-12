# 智能体完成通知接入

PC 端服务启动后，所有接入方式最终都调用：

```text
POST http://127.0.0.1:1122/send
Content-Type: application/json
```

请求示例：

```json
{
  "message": "任务完成: 修复登录问题",
  "source": "claude",
  "status": "completed",
  "taskId": "session-123",
  "cwd": "W:\\项目"
}
```

## Claude Code

在 PowerShell 中执行一次：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\install-ai-notify.ps1" -ClaudeOnly
```

安装程序只修改 `%USERPROFILE%\.claude\settings.json` 的 `Notification`、`Stop`、`StopFailure` 三个 Hook，并保留其他 Hook 与 `env`、`includeCoAuthoredBy` 等配置。

三个 Hook 的语义：

- `Notification`（matcher `permission_prompt|idle_prompt|agent_needs_input`）→ `status=waiting`，Claude 等待用户确认工具权限、等待下一条指令，或后台会话需要输入时推送。
- `Stop` → `status=completed`，一次回答结束时推送。
- `StopFailure` → `status=failed`，回答因 API 报错（限流、过载等）中断时推送。

Hook 采用 exec 形式（`command` = `powershell.exe`，脚本路径放在 `args` 里），因此不需要 `-ExecutionPolicy Bypass`，Windows 路径里的反斜杠也不会被任何一层 shell 重新解析。手工改完 `settings.json` 后一般会被文件监听自动加载；若几秒后仍未生效，重启 Claude Code 会话。

## ChatGPT CLI

执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\install-ai-notify.ps1" -ChatGPTOnly
```

ChatGPT 的 `notify` 配置会调用 `codex-notify.ps1`，该脚本同时支持从标准输入或参数读取事件。

## Trae、ZCode、豆包以及其他工具

如果工具支持“任务完成后执行命令”，配置：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\ai-complete-notify.ps1" -Source trae -Message "任务完成"
```

将 `trae` 换成 `zcode`、`doubao` 或自定义来源名即可。若工具是命令行程序，也可以让它通过统一包装器运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\run-agent-with-notify.ps1" -Source zcode -Command zcode -- "你的任务参数"
```

包装器会保留原程序退出码，并在退出码为 `0` 时发送完成通知，其他退出码发送失败通知。脚本发送失败时只写入 `%LOCALAPPDATA%\LanNotify\ai-notify.log`，不会阻断智能体本身。

## PC 端自动监听 Trae / ZCode / 豆包

PC 端 `TapRelay.exe` 启动后会自动监听本机日志，不需要为桌面版 Trae、ZCode 或豆包配置 bat 文件或修改安装目录：

- TraeCode CN（原 Trae CN）：`%APPDATA%\Trae CN\logs\**\renderer.log` 与 `%APPDATA%\TraeCode CN\logs\**\renderer.log`（官方更名后数据目录暂未迁移，新旧目录名都监听）
- TraeWork CN（原 TRAE SOLO）：`%APPDATA%\TRAE SOLO CN\logs\**\renderer.log` 与 `%APPDATA%\TraeWork CN\logs\**\renderer.log`（官方更名后数据目录暂未迁移，新旧目录名都监听）
- ChatGPT：`%USERPROFILE%\.codex\sessions\**\*.jsonl`（`codex` 仅为历史协议目录）
- ZCode CLI：`%USERPROFILE%\.zcode\cli\log\zcode-*.jsonl`
- ZCode 桌面端：`%USERPROFILE%\.zcode\v2\logs\*.log`
- 豆包桌面端：`%LOCALAPPDATA%\Doubao\User Data\sdk_storage\log\saman_*.log`

识别规则是：

- TraeCode（协议 source key 仍为 `trae`）的 `code_comp_complete_shown` 且 `is_interrupted` 不为 `1` 时发送 `status=completed`；`code_comp_fail` 发送 `status=failed`，消息会带上错误摘要；`code_comp_waiting`、`code_comp_paused`、`code_comp_selection_required` 等交互事件发送 `status=waiting`。
- TraeWork 的 `DoneHandler` completed/failed 和 `NotificationPort` waiting 分别发送 `status=completed`、`failed`、`waiting`；canceled/cancelled 明确忽略。协议 source key 统一为 `traework`（历史消息中的 `trae-solo` 在 Android 端归入同一渠道）。
- ChatGPT 的 `task_complete`、`turn.completed`、`response.completed` 等事件发送 `status=completed`；`task_failed`、`task_error`、`turn.failed`、`response.failed` 等发送 `status=failed` 并附带错误摘要；审批请求、输入请求、暂停和 `requires_action` 发送 `status=waiting`；canceled/cancelled/aborted/interrupted 明确忽略。
- ChatGPT 任务优先使用 `sessionId/session_id + turnId/turn_id/taskId/task_id` 组成唯一 `taskId`，缺失时回退到时间戳或事件指纹；`cwd` 从工作目录字段读取并随通知传递。
- ZCode CLI 记录 `turn.completed` 时发送 `source=zcode,status=completed`。
- ZCode 桌面端记录 `task_complete` 时发送 `source=zcode,status=completed`。
- ZCode CLI 记录 `turn.failed` 时发送 `source=zcode,status=failed`。
- ZCode 桌面端记录 `task_error` 时发送 `source=zcode,status=failed`。
- 豆包 `/chat/completion` 最终 `[rlog]` 为 `SUCCESS + 2xx` 时发送 `source=doubao,status=completed`；非取消错误发送 `failed`；`request canceled/cancelled` 与 `IO_PENDING` 忽略。
- 豆包使用网络请求 `req_id` 作为唯一 `taskId`。当前日志没有可靠工作目录，因此 `cwd` 为空。
- OpenCode 监听 `%USERPROFILE%\.local\share\opencode\opencode.db` 的 `message` 表，`finish=stop` 且无 `error` 发送 `source=opencode,status=completed`；完成正文取当前 assistant 消息在 `part` 表中的可见 `type=text` 内容，不能使用用户提示词、会话标题或 Windows toast；`error.name=MessageAbortedError` 判取消并静默忽略；其它 `error` 发送 `status=failed` 并附错误摘要；`projectName` 取所属 `session.directory` 叶目录，`taskId` 用 `opencode-<messageId>`。手机继续会话使用 `opencode run --session <sessionId> <message>`，不要使用仅支持 list/delete 的 `opencode session` 子命令。
- Freebuff 监听 `%USERPROFILE%\.config\freebuff-desktop\projects\*/desktop-v2.db` 的 `threads` 表，`turn_state=idle` 时 `last_turn_outcome` 为 `completed` 发送 `source=freebuff,status=completed`、`error` 发送 `failed`（正文取 `harness_state.output.message`）、`stopped`/`closed` 发送 `stopped`（不误报失败）；`attention_reason=merge-conflict` 发送 `status=waiting`；等待管理员命令确认（`request_elevation`）由 orchestrator 本地 SSE（`/api/events`，端口从 `%APPDATA%\Freebuff\logs\orchestrator-stderr.log` 解析）的 `elevation_request` 事件发送 `status=waiting`；`taskId` 用 `freebuff-<threadId>` / `freebuff-elev-<requestId>`。
- 监控器按任务和日志事件去重；启动时从现有日志/数据库末尾开始，不会重放历史完成记录。

因此，Claude、ChatGPT、Trae、TraeWork、ZCode 和豆包的任务状态都会汇聚到同一个 `POST /send` 和手机 WebSocket 通道。PC 端升级统一执行 `W:\TapRelay\tools\package-pc.ps1`，正式入口固定为 `W:\TapRelay\pc-app\publish\TapRelay.exe`。

## 千问桌面端（暂不支持）

千问已加入适配黑名单。当前问题暂时无法稳定解决，暂不考虑继续适配；检测到千问或收到 `source=qwen` 的接入请求时，TapRelay 只返回明确提示，不会修改千问配置、创建启动快捷方式、读取千问日志或发送任务通知。

## 已适配应用进程监听脚本

`W:\TapRelay\tools\watch-adapted-processes.ps1` 只对当前已适配的应用进程建立生命周期监听，未知进程不会加入监听集合。脚本使用 `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` 处理实时启动、退出事件，并每 5 秒按已适配进程名执行一次轻量兜底扫描，避免全量进程轮询。

持续运行并输出 JSON Lines 事件：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\watch-adapted-processes.ps1"
```

一次性扫描当前状态（适合接入其他脚本或健康检查）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\watch-adapted-processes.ps1" -Once
```

事件类型包括 `monitor_started`、`process_snapshot`、`process_started` 和 `process_stopped`；启动/退出事件的 `listenerState` 分别为 `listening` / `stopped`，并携带 `appKey`、`processName`、`processId` 与触发原因。

## 2026-07-31 ZCode 与全应用通知适配说明

- ZCode CLI 的 `%USERPROFILE%\.zcode\cli\log\zcode-*.jsonl` 已纳入受支持文件类型；此前该目录虽然被监听，但 `.jsonl` 被文件过滤器排除，解析逻辑不会执行。
- `turn.completed` 发送 `status=completed`，`turn.failed` 发送 `status=failed`；`status=cancelled/canceled` 明确忽略，避免取消任务误报为失败。
- ZCode 优先使用 `turnId` 作为 `taskId`；缺少 `turnId` 时使用 `sessionId:timestamp`，避免同一会话的连续任务落入服务端 15 秒去重窗口。
- ZCode Desktop 的 `[repoWiki] ... task_complete ...` 不代表用户任务完成，已排除该假阳性。
- Trae 使用 `sessionId:turn/request/message/timestamp/hash`，TraeWork 使用 `sessionId:agentMessageId`（等待确认使用 `sessionId:planItemId`），避免同一会话多条通知互相去重。
- Windows、macOS、Linux 风格路径会先统一为 `/` 再判断；Unix/macOS 路径当前只有代码级自动化测试，PC 图形界面仍是 Windows WinForms，不能据此宣称已有 macOS/Linux 可点击安装包。
- 可通过 `TAPRELAY_MONITOR_USER_HOME`、`TAPRELAY_MONITOR_APPDATA` 和 `TAPRELAY_MONITOR_LOCALAPPDATA` 覆盖当前监控用户目录，供测试隔离或便携部署使用。固定端口 `1122` 仍限制同一台电脑上的多用户并发实例。
- 服务端 payload 的 `taskId`、`cwd` 已由 Android 接收并持久化；Android 消息 ID 优先使用 `source|status|taskId`，同一秒、同文案的不同任务不再覆盖。

详细结果见 `W:\TapRelay\docs\ZCode-通知修复与全应用适配报告-20260731.md`。

## 2026-07-31 豆包通知适配说明

- 实际安装目录：`C:\Users\Admin\AppData\Local\Doubao\Application`；主程序：`Application\app\Doubao.exe`。
- 任务完成信号来自 `%LOCALAPPDATA%\Doubao\User Data\sdk_storage\log\saman_*.log` 的 `/chat/completion` 最终 `[rlog]`，不会把 `mcs.doubao.com/list` 等普通请求当成任务完成。
- 成功、失败使用 `req_id` 作为唯一 `taskId`；用户主动取消不会生成失败通知。
- Android 已新增 `source=doubao` → “豆包”渠道与独立橙红色标识。
- 解析/路径自动化测试为 `24 passed, 0 failed`；全应用真实 WebSocket E2E 为 `14 expected deliveries, 0 missing, 0 duplicate, 0 ignored-event deliveries`。
- 真机 `192.168.5.35:5555` 已确认收到并持久化 `source=doubao,status=completed,taskId=<唯一请求ID>,cwd=空`。该设备的 `task_alerts_v2` 系统通知渠道当前被用户设置为 `importance=0`，因此本次只确认消息链路和本地持久化，不把通知栏横幅计为通过。

详细结果见 `W:\TapRelay\docs\豆包通知适配报告-20260731.md`。
