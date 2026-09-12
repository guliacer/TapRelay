# 通用应用适配技能

## 目标

当用户希望把任意本地应用的任务状态通知到手机时，使用本技能完成调查、适配、注册、测试和交付说明。适配必须遵循 `W:\TapRelay\11-应用识别与适配规则.md`，不得把当前已适配应用的路径或日志关键字当作通用假设。

## 执行顺序

1. 读取 `W:\TapRelay\CLAUDE.md`、相关编号规则、`W:\TapRelay\11-应用识别与适配规则.md` 和 `W:\TapRelay\09-问题与优化记录.md` 中的相关主题。
2. 只读确认应用的安装目录、进程名、日志/数据库/API 来源。优先使用用户提供的路径或应用自身配置，不做全盘扫描。
3. 找到 `completed`、`failed`、`waiting`、取消/停止和重复事件的真实样本，记录原始证据、状态映射、`requiresAction` 语义和去重键。三种强制状态必须各有真实样本；用户取消必须保持静默或映射为明确的 `stopped` 状态，不能误报失败。waiting 若可能被自动模式快速完成，必须登记后经宽限期复核，不能在瞬时工具事件上直接广播。
4. 为每种强制状态找到真实任务日志来源：完成取 Agent 最终回复、应用结果或生成物摘要；失败取错误详情、失败步骤或退出码；待处理取实际提问、审批对象或阻塞详情。不得用用户原始提示词、会话标题、工具名列表或状态标题代替完成日志。
5. 只有同时满足“状态证据可复核”和“正文日志可复核”才算适配完成；任何状态缺少真实日志时标记为“待补充数据源”，不得用“智能体任务已完成”等模板补位。
6. 确定稳定的 ASCII `appKey`，检查它没有占用 TapRelay 内置键。
7. 从应用安装目录或官方随包资源提取 Logo，输出 `256x256 PNG`。Windows 图标可使用 PowerShell/.NET `System.Drawing.Icon` 或应用实际资源提取，不得手绘首字母代替正式图标。
8. 将注册信息发送到 PC：

```powershell
$icon = [Convert]::ToBase64String([IO.File]::ReadAllBytes('.\icon-256.png'))
$body = @{
  appKey = 'example-agent'
  displayName = 'Example Agent'
  brandColor = '#4E6EF2'
  iconBase64 = $icon
} | ConvertTo-Json -Compress
Invoke-RestMethod -Uri 'http://localhost:1122/apps/register' -Method Post `
  -ContentType 'application/json' -Body $body
```

9. 监控器只负责读取状态、去重和调用 `/send`：

```powershell
$body = @{
  # message 必须是该任务的真实结果/错误/等待详情，不能只写状态标题。
  message = '已生成 4 个文件并通过 12 项校验'
  taskLog = '已生成 4 个文件并通过 12 项校验'
  source = 'example-agent'
  status = 'completed'
  taskId = 'native-task-id'
  cwd = 'C:\work\project'
} | ConvertTo-Json -Compress
Invoke-RestMethod -Uri 'http://localhost:1122/send' -Method Post `
  -ContentType 'application/json' -Body $body
```

`/send` 支持可选 `taskLog` 字段（最多 4000 字符）；生产 PC 端 Android 卡片正文优先展示 `taskLog`、否则回退到 `message`。两者都必须是该任务的真实细节。`prompt` 仅用于 ComfyUI 工作流/执行图 JSON（上限 64KB），由 `ComfyUIPromptParser` 提取 KSampler/KSamplerAdvanced 正向提示词作为 `taskLog`；其它应用不得把用户 prompt 直接当作完成日志。示例状态的正文必须分别类似“已生成 4 个文件并通过 12 项校验”“编译失败：退出码 1，缺少 xxx”“等待确认：是否覆盖现有文件”，不得发送“任务已完成”“智能体任务已完成”“任务失败”或“需要确认”单独模板。

## 验证要求

验证顺序固定为：`GET /health`、`GET /apps`、`GET /apps/icon/<appKey>`、发送 ASCII/UTF-8 探针、完成/失败/待处理三类真实样本、取消/停止样本和重复样本。每类状态都必须核对 `status`、`taskId`、`requiresAction`（waiting）、`message`/`taskLog` 正文、项目名和耗时；必须确认正文不是用户 prompt、会话标题或通用状态模板。每次适配都要说明监控文件、安装路径覆盖变量、匹配规则、日志来源、去重规则和测试结果。

## 禁止事项

- 不修改目标应用配置，不注入目标进程，不模拟点击，不安装开机自启。
- 不使用第二端口、公网服务、云端数据库或第三方推送服务。
- 不注册 `claude`、`codex`、`trae`、`traework`、`zcode`、`doubao`、`qwen`、`suyan`、`baidunetdisk`、`quarknetdisk`、`quarknetdisk-upload`、`comfyui`、`opencode`、`taprelay` 等内置键；其中千问已列入适配黑名单，检测到时只提示暂不支持，不得重新开启适配。ComfyUI 使用官方 `comfyui` 键与官方图标，禁止以第三方改版（如 ComfyNexus）名称或图标注册。OpenCode 使用官方 `opencode` 键与官方图标，通过 `~/.local/share/opencode/opencode.db` 的 `message` 表监听任务终态。
- 不把“看见某个 UI 文案”当作任务完成证据，除非它能与应用自身的稳定事件或数据记录绑定。
- 不把“任务已完成”“智能体任务已完成”“任务失败”“需要确认”等状态模板作为新应用的 `message` 或 `taskLog`；状态写入 `status`，正文写入真实任务日志。
- 不将 `parsed_query`、用户提示词或会话标题冒充 Agent 最终总结；不将子代理完成、中间工具步骤、自动模式瞬时事件冒充外层任务终态。
