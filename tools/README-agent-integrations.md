# 智能体完成通知接入

所有接入方式最终调用：

```text
POST http://127.0.0.1:1122/send
Content-Type: application/json
```

```json
{
  "message": "任务完成",
  "source": "claude",
  "status": "completed",
  "taskId": "session-123"
}
```

## 一键安装

### Claude Code

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\install-ai-notify.ps1" -ClaudeOnly
```

### ChatGPT CLI

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\install-ai-notify.ps1" -ChatGPTOnly
```

## 其他工具

任务完成后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\ai-complete-notify.ps1" -Source trae -Message "任务完成"
```

也可以用包装器运行命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\run-agent-with-notify.ps1" -Source zcode -Command zcode -- "任务参数"
```

## PC 自动监听

TapRelay 会自动监听已适配的 Claude、ChatGPT、Trae、ZCode、豆包、OpenCode、Freebuff、SuYan 等工具，无需逐个修改安装目录。

手动启动进程监听：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "W:\TapRelay\tools\watch-adapted-processes.ps1"
```

新版本会在 PC 和 Android 弹窗显示一句话更新摘要，并提供更新、跳过本次和永久跳过选项。
