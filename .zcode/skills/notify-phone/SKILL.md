---
name: notify-phone
description: 用户要求通知手机时，向本机 TapRelay 发送真实任务结果及可靠会话信息。
trigger:
  - keywords:
      - "通知手机"
      - "发送通知"
      - "任务完成通知"
      - "推送通知"
      - "手机提醒"
      - "notify phone"
---

当用户要求通知手机时，向 `POST http://localhost:1122/send` 发送 UTF-8 JSON。字段、真实日志和会话续接约束以根目录 `06-通信与数据获取规范.md`、`11-应用识别与适配规则.md` 为准。

先取得本轮的最终结果、错误详情或实际等待问题。`message` 写简短且真实的结果摘要，`taskLog` 保留原始正文的换行和缩进；不能默认使用“Agent 任务已完成”，也不能把用户提示词当作完成日志。未取得正文时继续查找可靠数据源，明确报告缺失，不能编造。

`source` 使用已确认的来源键，`status` 使用 `completed`、`failed` 或 `waiting`。仅填写从当前任务取得的 `taskId`、原始可续接 `sessionId` 和 `cwd`；示例中的占位符必须替换，拿不到可靠值时省略，不能随机编造。缺少会话 ID 的通知无法保证继续会话。`durationMs` 和 `tokenCount` 仅在有原生可信值时发送。

PowerShell 示例（先把占位符替换为本轮真实值）：

```powershell
$taskLog = Get-Content -LiteralPath '<本轮真实结果文件路径>' -Raw
if ([string]::IsNullOrWhiteSpace($taskLog)) { throw '尚未取得真实任务日志' }
$payload = @{
    message = '<从本轮结果提取的简短摘要>'
    taskLog = $taskLog
    source = '<实际来源键>'
    status = 'completed'
    taskId = '<实际任务ID>'
    sessionId = '<原始可续接会话ID>'
}
$json = $payload | ConvertTo-Json -Depth 6 -Compress
Invoke-RestMethod -Uri 'http://localhost:1122/send' -Method Post `
    -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($json))
```

Bash 可读取已按同一字段准备并校验的 JSON 文件，保留其中转义后的换行：

```bash
curl --fail-with-body --silent --show-error http://localhost:1122/send \
  -H 'Content-Type: application/json; charset=utf-8' \
  --data-binary @verified-notification.json
```

检查返回结果中的 `sentClients`；为 `0` 时只能报告当前未发送到在线手机，不能宣称已送达。服务端不补发离线消息。HTTP 请求成功也不等于用户已查看或 Agent 已接受继续指令。

若用户明确指定纯文本通知，可使用其原文作为 `message`；不把普通提醒包装成已验证的任务完成日志。不添加 token、签名、登录或外网依赖。
