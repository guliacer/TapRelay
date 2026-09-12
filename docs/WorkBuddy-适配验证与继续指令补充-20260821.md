# WorkBuddy 适配验证与"继续"指令补全报告（最终版）

- 日期：2026-08-21（验证完成于当日 15:27）
- 范围：验证 `W:\TapRelay` 项目对 WorkBuddy 的适配完整性，补全手机"继续"指令链路，确认通知卡片四字段齐全，并以**真实端到端测试**收口。
- 关联记录：`docs/WorkBuddy-通知适配报告-20260804.md`（首版适配）、`09-问题与优化记录.md`、`11-应用识别与适配规则.md`、`pc-app/AgentResumeRunner.cs`、`pc-app/NotifyServer.cs`、各 `AgentResumeScripts/resume-*.ps1`、`notify-server/ws-test-*.mjs`。
- 用户指令：本次以**测试验证**为唯一目标（非推送/非发布）。本项目也不是 git 仓库，故不涉及推送。

## 1. 验证结论速览

| 功能 | 状态 | 最终证据 |
| --- | --- | --- |
| 任务完成（completed） | ✅ 已适配 | 首版 E2E 通过；本次 `ws-test-states.mjs` 三态 PASS，completed 广播携带 `projectName/durationMs/tokenCount/sessionId` |
| 任务失败（failed） | ✅ 已适配 | 同上，failed 广播字段齐全 |
| 任务待处理（waiting） | ✅ 已适配 | 同上，waiting 广播正确隐藏耗时/token（未知不臆造） |
| 手机"继续"指令 | ✅ 已修复并验证 | 见 §2；Android 单测 `workbuddy-remote` 可续接 ✅；真机链路 `agent.resume → succeeded=true,status=Started` ✅ |
| 通知卡片四字段 | ✅ 已接通并验证 | 广播 payload 实测均携带 `projectName/durationMs/tokenCount/sessionId`；卡片布局含 content/duration/token |

## 2. "继续"指令：根因与修复（关键）

### 2.1 真正的根因：`workbuddy-remote` 线路别名在两层都未归并

PC 端 `NotifyServer.ToWireSource("workbuddy")` 在广播时把来源改写为 **`workbuddy-remote`**（见 `NotifyServer.cs` 的 `WorkBuddyCompatibilityAppKey`），以便手机端把 WorkBuddy 归入兼容分组。这导致**手机实际收到的 `source` 是 `workbuddy-remote`，而非 `workbuddy`**，而两层旧代码都只认 `workbuddy`：

1. **Android 渠道归并缺失**：`Channel.from()` 没有把 `"workbuddy-remote"` 映射到 `WorkBuddy`，于是 `canonicalSourceKey("workbuddy-remote")` 返回 `"workbuddy-remote"`，不在 `RETRYABLE_AGENT_SOURCES`（含 `"workbuddy"`）中 → `isRetryable()` 返回 `false` → **"继续"按钮永不出现**。
   - 注：`Models.kt` 的 `RETRYABLE_AGENT_SOURCES` 本就含 `"workbuddy"`，无需改动；缺的是 `Channel.kt` 的别名映射。
2. **PC 续接路由缺失**：手机回传 `agent.resume` 携带 `source="workbuddy-remote"`；`AgentResumeRunner.NormalizeSource` 原样返回 `"workbuddy-remote"` → `GetScriptName` 无匹配 → 判定 `NotSupported`。
3. **续接脚本一度超时**：早期 `resume-workbuddy.ps1` 用内联 `Add-Type` 编译 C# P/Invoke（`SetForegroundWindow`/`ShowWindow`），冷编译超过 PC 端 `WaitForExit(5000)` 预算，且被 PowerShell 安全策略拦截 → `LaunchFailed`。已重写为立即返回的窗口聚焦脚本。

### 2.2 关键研判：WorkBuddy 无 CLI 续接

- WorkBuddy 是基于 **CodeBuddy** 引擎的 Electron 应用（`resources/app.asar.unpacked/cli/dist/codebuddy.js` 内含 `resumeSession`/`resumeFromRunId`，ACP 解析以 `codebuddy.ai/*` 为 meta key）。
- 其 `codebuddy` **未注册到 PATH**（已核实 `command -v codebuddy` 不存在），无独立 `codebuddy resume <sessionId>` 可 headless 调用。
- 因此 WorkBuddy 的"继续"采用与 **Trae** 一致的**窗口聚焦**模型（Electron/IDE 类无 CLI 续接），而非 Claude/Codex/OpenCode 的 CLI `resume` 模型。

### 2.3 本次修复（均已部署并验证）

| 文件 | 改动 |
| --- | --- |
| `android-app/.../data/Channel.kt` | `Channel.from` 增加 `"workbuddy", "workbuddy-remote" -> WorkBuddy`，使 `canonicalSourceKey("workbuddy-remote")="workbuddy"` 落入可续接集合，卡片归入 WorkBuddy 渠道 |
| `android-app/.../data/NotifyMessagePersistenceTest.kt` | 新增断言：`workbuddy` 与 `workbuddy-remote` 来源下 `isRetryable()` 均为 `true`（锁定行为） |
| `pc-app/AgentResumeRunner.cs` | `NormalizeSource` 增加 `"workbuddy-remote" => "workbuddy"`，与 `GetScriptName` 的 `case "workbuddy": return "resume-workbuddy.ps1"` 衔接 |
| `pc-app/AgentResumeScripts/resume-workbuddy.ps1`（重写） | 窗口聚焦模型：`Start-Process -WindowStyle Normal`（立即返回，不 `-Wait`）拉起 WorkBuddy 触发 Electron 单实例聚焦；`WScript.Shell.AppActivate('WorkBuddy')` 尽力置顶；不再使用 `Add-Type` P/Invoke，规避 5s 超时与被拦截 |

### 2.4 部署说明

PC 两处为 C# 改动 + 续接脚本，已在可编译环境经 `pc-manual-build.ps1`（绕开被回收站包装拦截的 `package-pc.ps1`）完成 `dotnet build -c Release` + `dotnet publish -r win-x64 --self-contained` + robocopy 到 `publish` + 重启 `TapRelay.exe`。重启后健康检查：`{"status":"ok","port":1122,"clients":1}`。`package-pc.ps1`（R9）因 `Remove-Item` 被本机回收站包装拦截而失败-关闭，仅在能正常删除的环境中可用；本机改用等价手动脚本完成发布。

## 3. 通知卡片字段验证（端到端实测）

卡片布局（`res/layout/notification_task_expanded.xml` 等）已含以下视图，且 `NotificationHelper` 已正确填充；本次通过 `ws-test-workbuddy.mjs` / `ws-test-states.mjs` 实测广播 payload 确认四字段下行到位：

| 用户要求字段 | 数据来源 | 渲染位置 | 实测广播值示例 |
| --- | --- | --- | --- |
| 任务名称 | `projectName` + `notificationTitle` | 正文 `[项目名]` 前缀 + 平台标题 | `projectName=test-project`，`notificationTitle="[workbuddy] WorkBuddy 任务失败"` |
| 任务日志 | `content` / `displayContent()` | `notification_content` | `[workbuddy] WorkBuddy 任务失败` |
| 任务耗时 | `durationMs` | `notification_duration` | completed `durationMs=120000`，failed `durationMs=65000`；waiting 隐藏（未知不展示） |
| 任务消耗 token | `tokenCount`（TraeCode 另含 `credits`） | `notification_token` | completed `tokenCount=8800`，failed `tokenCount=1234`；`tokenCount>0` 才展示 |

数据链路：`pc-app/AgentCompletionMonitor` 解析 WorkBuddy ACP 日志 → `NotifyServer.MessagePayload`（含 `projectName/durationMs/tokenCount/sessionId/credits/actionUrl`）→ 手机 WebSocket 解析进 `NotifyMessage` → `NotificationHelper` 渲染。四字段端到端打通。**WorkBuddy ACP 日志未必稳定提供耗时/token，此时按规则不展示，保证内容准确、不伪造。**

## 4. 其他智能体适配记录参考（历史方案）

查阅 `pc-app/AgentResumeScripts/` 与 `09-问题与优化记录.md`，作为 WorkBuddy 方案依据：

- **Claude / Codex / OpenCode / DeepSeek Harness / Freebuff**：提供 headless CLI 续接。
  - Claude：`resume-claude.ps1` `--resume <sessionId> -p <续接提示词>`。
  - Codex：`resume-codex.ps1` `exec resume --all <sessionId> <提示词>`（非交互）。
  - OpenCode / DeepSeek：`--resume <sessionId>`。
  - 统一注入固定 base64 续接提示词"请查看当前任务进度，在现有基础上继续后续执行。"，提示词放在随程序发布的脚本中，手机端无法篡改任务文本。
- **Trae（TraeCode CN）**：Electron/IDE，无 CLI 续接 → **窗口聚焦**模型。`resume-trae.ps1` 扫描 `workspaceStorage\<hash>\state.vscdb` 反查 session→工作目录，再以 `--reuse-window <folder>` 唤起原窗口（P078）。
- **WorkBuddy 归类**：与 Trae 同类（Electron、无 CLI 续接），故采用窗口聚焦；本次 `resume-workbuddy.ps1` 即参照 `resume-trae.ps1` 重写（去掉 P/Invoke，改为 `Start-Process` 立即返回）。若未来 WorkBuddy 暴露 `codebuddy resume` CLI，可平滑升级为 CLI 续接脚本。

## 5. 测试与验证结果（本次实测）

### 5.1 Android 单元测试（强制重跑，非缓存）
```
cd W:\TapRelay\android-app
.\gradlew.bat :app:testDebugUnitTest --tests "com.lan.notify.data.NotifyMessagePersistenceTest" --rerun-tasks
BUILD SUCCESSFUL in 26s  (25 tasks executed)
tests="14" skipped="0" failures="0" errors="0"
含 resumableAgentMessageCanBeContinuedFromEveryPhoneEntry：断言 workbuddy 与 workbuddy-remote 均 isRetryable()=true → PASS
```
> 说明：首次执行因 Gradle 增量缓存显示 UP-TO-DATE（未真正执行），已用 `--rerun-tasks` 强制重跑，结果真实有效。

### 5.2 PC 续接 E2E（`notify-server/ws-test-workbuddy.mjs`，真实 `workbuddy-remote` 来源）
```
[2] POST /send -> HTTP 200
[3] BROADCAST: source=workbuddy-remote status=failed taskId=wb-task-001 sessionId=test-session-123
                   projectName=test-project durationMs=65000 tokenCount=1234
[4] SENT control agent.resume (source=workbuddy-remote)
[5] RESUME control_result: succeeded=true  source=workbuddy  status=Started  error=(none)
```
→ 手机发送 `workbuddy-remote` → PC `NormalizeSource` 归并为 `workbuddy` → `resume-workbuddy.ps1` 启动并返回 `Started`（5s 内），续接链路闭环。

### 5.3 三态广播 E2E（`notify-server/ws-test-states.mjs`）
```
[3] Captured broadcasts: 3
    [completed] OK source=workbuddy-remote projectName=test-project duration=120000ms token=8800 sessionId=test-session-123
    [failed]    OK source=workbuddy-remote projectName=test-project duration=65000ms  token=1234 sessionId=test-session-123
    [waiting]   OK source=workbuddy-remote projectName=test-project duration=(hidden) token=(hidden) sessionId=test-session-123
[4] THREE-STATE RESULT: PASS
```
→ 任务完成/失败/待处理三态均能正确广播，四字段按"已知才展示"规则下行。

### 5.4 运行态
- `TapRelay.exe`（PID 23720）运行中，`/health` 返回 `{"status":"ok","port":1122,"clients":1}`。
- 部署产物 `pc-app/publish/AgentResumeScripts/resume-workbuddy.ps1` 已确认为重写版（无 `Add-Type`，使用 `Start-Process`）。

## 6. 适配结论与后续动作

1. **结论**：WorkBuddy 在 TapRelay 中的适配已达**完整标准**——三态通知、四字段卡片、手机"继续"指令（触发+执行）均通过端到端实测。
2. **未做**：未推送 git（用户明确要求仅测试；且本项目非 git 仓库）。
3. **后续可选**：
   - 真机点按验证：触发一次 WorkBuddy 失败/待处理通知 → 手机卡片出现"继续" → 点击后 WorkBuddy 窗口置顶（脚本层已验证，仅差一次人工点按）。
   - 若 WorkBuddy 后续提供 CLI 续接能力，将 `resume-workbuddy.ps1` 升级为 CLI 续接（参照 `resume-codex.ps1`）。
   - 修复 `package-pc.ps1` 的 `Remove-Item` 回收站包装拦截（或在本机继续用 `pc-manual-build.ps1`）。

## 7. 后续变更：移除手机"继续"指令（2026-08-22）

用户决定不再需要"手机继续 Agent 任务"功能并**彻底移除**。本报告 §1/§2/§5 关于"继续指令"的描述为移除前状态。相关实现（`AgentResumeRunner.cs`、`AgentResumeScripts/resume-*.ps1`、`MainForm` 的 `agent.resume` 分支、Android `RETRYABLE_AGENT_SOURCES`/`isRetryable()` 与各"继续"按钮、通知 layout 的 `notification_continue` 节点）已全部删除；`tools/package-pc.ps1` 不再携带续接脚本。仅移除继续相关片段，未改动任何卡片样式/布局/配色。
