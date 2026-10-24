<div align="center">

<img src="./assets/TapRelay-logo.png" alt="TapRelay" width="160" />

# TapRelay

**Send AI task status from a Windows PC directly to an Android phone.**

[Chinese README](./README.md) | English (optional)

<p><strong>Acknowledgement</strong>: The ComfyUI mobile control feature is based on and improved from <a href="https://github.com/Zizzp0318/ComfyUI-MobileConsole">ComfyUI-MobileConsole</a>. Thanks to the original author.</p>

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

## ComfyUI Mobile Control

TapRelay also serves a ComfyUI control page inside the existing port `1122`. The Android app selects a configured API workflow, fills in parameters, and submits it; the PC relays the request to a bundled console process managed by TapRelay, which calls the local ComfyUI instance. The console listens on `127.0.0.1` only, so no extra LAN port is exposed. The management page `/comfy/admin` and `/comfy/api/admin/*` accept loopback requests from the PC only.

<div align="center">
<table>
  <tr>
    <td align="center" valign="top">
      <img src="./assets/readme/comfyui-workflows.jpg" alt="ComfyUI workflow list with starred pinning, import, and export" width="250" />
      <br />
      <sub>Workflow list: pin, reorder, import / export / delete</sub>
    </td>
    <td align="center" valign="top">
      <img src="./assets/readme/comfyui-parameters.jpg" alt="ComfyUI parameter screen with run panel, image upload, and prompt editor" width="250" />
      <br />
      <sub>Parameters: generated from `.ui.json`, prompts, and image fields</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top">
      <img src="./assets/readme/comfyui-results.jpg" alt="ComfyUI result screen with image, duration, resolution, and save action" width="250" />
      <br />
      <sub>Results: history, duration / resolution, comparison, and save</sub>
    </td>
    <td align="center" valign="top">
      <img src="./assets/readme/comfyui-gallery.jpg" alt="Cross-workflow generated image gallery grouped by workflow" width="250" />
      <br />
      <sub>Gallery: cached results across all workflows</sub>
    </td>
  </tr>
</table>
</div>

- **Workflow management**: built-in Qwen Image 2.1 regular image edit, Qwen Image 2.1 · Viggle Turbo (6 steps), and image decode & obfuscate; add others through import, with export, delete, star-to-pin, and long-press drag ordering.
- **Parameters**: Chinese parameter panels generated from each workflow's `.ui.json`; copy / paste / clear for prompts; upload, thumbnail, and clear for image fields; multi-slot LoRA with custom strengths; a full-screen mask brush for image fields.
- **Running and results**: the run button shows elapsed time and can be stopped; results accumulate per workflow (up to 50 entries) with multi-output paging, a before/after comparison slider, full-screen preview, save to `Download/ComfyUI`, and forwarding to SuYan through the PC.
- **Title interaction**: the workflow title scrolls automatically so long names stay readable; double-tap to rename, and the new name survives restarts and upgrades.
- **Gallery**: the **Generated images** entry groups cached images from every workflow for quick review.

ComfyUI must be running on the PC (default `http://127.0.0.1:8188`). The console lives in `pc-app/Assets/ComfyUIConsole`, and the built-in workflow files are published in the repository: [pc-app/Assets/ComfyUIConsole/api_workflows](./pc-app/Assets/ComfyUIConsole/api_workflows).

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

The ComfyUI mobile page uses `/comfy/*` to reach the PC console. Mobile-only helpers live under `/comfy/api/mobile/*` (workflow import, export, delete, rename, LoRA candidates, mask composition, and SuYan forwarding). The console is started by TapRelay, listens on loopback only, and `/comfy/admin` plus `/comfy/api/admin/*` are restricted to the PC.

## Privacy and Limits

- Android stores connection settings, the last 10 connection addresses (`host_history`) and the latest 100 messages locally. The PC only keeps connected clients in memory.
- Android remembers connection addresses that were used successfully; **Device management** lists the last 10 (most recent first) so you can switch back without retyping. **Hide address** masks the address in the app UI, the history list and the system notification.
- ComfyUI result indexes and images are stored in the Android private directory (`files/ComfyUI`, up to 50 entries per workflow). Writing to `Download/ComfyUI` or sharing to SuYan is always user-triggered. Workflows, parameters, masks, and forwarding requests all pass through the PC's `1122` loopback proxy; `/comfy/admin` and `/comfy/api/admin/*` are not exposed to the phone.
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

The notification screenshots were produced on 2026-09-12 with the current `pc-app\publish\TapRelay.exe`, port `1122`, an Android emulator, and a real `POST /send` request; the ComfyUI screenshots were captured on 2026-10-23 with the same emulator connected to a local ComfyUI instance. This verifies the PC health, WebSocket, Android-receive, and ComfyUI control paths, not every third-party agent version or workflow.

## Related Projects

- [SuYan](https://github.com/guliacer/SuYan) — local asset management. ComfyUI results can be sent from the PC to SuYan's local inbox for archiving, search, and reuse.
- [ComfyUI-GuliNodes](https://github.com/guliacer/ComfyUI-GuliNodes) — ComfyUI custom nodes. The built-in Qwen Image and image decode/obfuscate workflows use nodes such as `GGImageComparer2`, `GGImageCompressSave`, `GGLoRACustomLoader`, and `GGImageObfuscate`, so install this plugin in ComfyUI first.
