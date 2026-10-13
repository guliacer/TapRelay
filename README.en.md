<div align="center">

<img src="./assets/TapRelay-logo.png" alt="TapRelay" width="160" />

# TapRelay

**Send AI task status from a Windows PC directly to an Android phone.**

[Chinese README](./README.md) | English (optional)

</div>

## Project Overview

<div align="center">
<table>
  <tr>
    <td align="center" valign="top">
      <img src="./assets/readme/pc-overview.png" alt="TapRelay PC main window with service status, notification apps, and runtime logs" width="560" />
      <br />
      <sub>PC: manage the relay service, app integrations, and task logs</sub>
    </td>
    <td align="center" valign="top">
      <img src="./assets/readme/android-device-management.jpg" alt="TapRelay Android device management screen with device name and connection status" width="280" />
      <br />
      <sub>Android: configure the device and receive PC task notifications</sub>
    </td>
  </tr>
</table>
</div>

TapRelay is a local-first task notification relay. Local agents, desktop tools, and scripts report task completion, failure, or input-required events to a Windows PC. The PC then relays them to an Android device over WebSocket on the same trusted network.

> The default route is local only: `Agent / Hook / local logs -> TapRelay PC -> Android`. There is no authentication or transport encryption by default. Never expose port `1122` directly to the public Internet.

## Screenshots

<div align="center">
  <img src="./assets/readme/android-main.png" alt="TapRelay Android message history" width="320" />
  <br />
  <sub>Android message history for pushed task status and task logs</sub>
</div>

The screenshots were captured from the current PC application and an Android emulator during a real connection test. The PC endpoint is masked; `10.0.2.2` is the emulator host address.

## Components

| Component | Purpose |
| --- | --- |
| `pc-app` | Windows WinForms application and the production HTTP/WebSocket service |
| `android-app` | Kotlin / Jetpack Compose Android client |
| `notify-server` | Standalone Node.js protocol implementation and test target; not required for everyday use |
| `tools` | Packaging, Android verification, and agent-integration scripts |

The production service listens on TCP port `1122`. Android requires API 26 or later, and the PC and phone must be able to reach one another on the same LAN.

## Quick Start

Build, publish, validate, and start the PC app:

```powershell
.\tools\package-pc.ps1
```

The script validates `http://127.0.0.1:1122/health` and starts `pc-app\publish\TapRelay.exe`. Then open the Android app, scan the PC QR code or enter the PC LAN address in Device Management. The port is always `1122`.

Build and install the Android debug application:

```powershell
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"
adb install -r .\android-app\app\build\outputs\apk\debug\app-debug.apk
```

Send a smoke-test message:

```powershell
$body = @{
  message = "LAN connection test completed."
  taskLog = "LAN connection test completed.`nThe PC relayed this task result to Android over WebSocket."
  source = "taprelay"
  status = "completed"
  taskId = "readme-smoke-test"
} | ConvertTo-Json -Compress

Invoke-RestMethod -Uri "http://127.0.0.1:1122/send" `
  -Method Post -ContentType "application/json; charset=utf-8" `
  -Body ([Text.Encoding]::UTF8.GetBytes($body))
```

An HTTP success response only confirms submission to the PC. A positive client count and a new Android message confirm delivery to an online device. The service does not queue messages for offline devices.

## Integration and Monitoring

All task sources eventually call `POST http://127.0.0.1:1122/send`. New integrations should provide the actual final result, error details, or the real question that needs input. Do not use a generic status string, a user prompt, or a conversation title as `taskLog`.

The PC application includes local monitoring channels for Claude Code, Claude Code Haha, Codex/ChatGPT, TraeCode CN, TraeWork CN, ZCode, Doubao, WorkBuddy, OpenCode, DeepSeek Harness, Freebuff, and selected workflow or transfer tools. A built-in channel does not guarantee validation against every third-party app version; validate completed, failed, and waiting events in your own environment before relying on it. The Android notification cards do not provide a session-continuation button.

Use the bundled scripts for Claude Code or Codex/ChatGPT configuration:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File ".\tools\install-ai-notify.ps1" -ClaudeOnly

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File ".\tools\install-ai-notify.ps1" -CodexOnly
```

See [tools/README-agent-integrations.md](./tools/README-agent-integrations.md) for the available local hooks, paths, and integration notes.

## HTTP API

Health check:

```http
GET http://127.0.0.1:1122/health
```

```json
{ "status": "ok", "port": 1122, "clients": 1 }
```

Send a task notification:

```http
POST http://127.0.0.1:1122/send
Content-Type: application/json
```

```json
{
  "message": "Authentication refactor completed and 12 checks passed",
  "taskLog": "Authentication refactor completed and 12 checks passed\nDetails: ...",
  "source": "example-agent",
  "status": "completed",
  "taskId": "native-task-id",
  "sessionId": "original-session-id",
  "cwd": "W:\\project",
  "durationMs": 125900,
  "tokenCount": 42850
}
```

`message` is required and limited to 500 characters. For new integrations, also send `source`, `status` (`completed`, `failed`, or `waiting`), a native `taskId`, and a real `taskLog` (up to 4,000 UTF-16 code units). Only send `durationMs` and `tokenCount` when the source provides trustworthy values.

`POST /events` supports lifecycle and approval events. `GET /apps`, `POST /apps/register`, and `GET /apps/icon/<appKey>` expose locally registered custom app metadata and icons.

## Privacy and Limits

- Android stores connection settings, the last 10 connection addresses (`host_history`) and the latest 100 messages locally. The PC only keeps connected clients in memory.
- Android remembers connection addresses that were used successfully; **Device management** lists the last 10 (most recent first) so you can switch back without retyping. **Hide address** masks the address in the app UI, the history list and the system notification.
- Notification, camera scanning, overlay, and background permissions are optional and should be granted only when required.
- Do not send passwords, tokens, private keys, or full sensitive command arguments in task logs or approval summaries.
- Do not use port forwarding, public firewall rules, or an unprotected reverse proxy for port `1122`.
- TapRelay does not provide user accounts, cloud storage, public push messaging, or offline relay storage.

## Development Checks

```powershell
dotnet build .\pc-app\TapRelay.csproj -c Debug
dotnet run --project .\tests\TapRelay.Protocol.Tests\TapRelay.Protocol.Tests.csproj -c Debug
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"

Push-Location .\notify-server
npm ci
npm test
Pop-Location
```

The screenshots for this README were produced on 2026-09-12 with the current `pc-app\publish\TapRelay.exe`, port `1122`, an Android emulator, and a real `POST /send` request. This verifies the PC health, WebSocket, and Android-receive path, not every third-party agent version.
