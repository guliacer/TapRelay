# TapRelay

![TapRelay](./assets/TapRelay-logo.png)

**Push AI task status from Windows to Android and control ComfyUI from the phone.**

[中文 README](../README.md)

## Features

- Built-in HTTP / WebSocket service for completed, failed, and waiting task notifications.
- Android task history with approvals, stop, cleanup, address masking, and device management.
- Select, configure, run, and view ComfyUI workflows from Android.
- New-version dialogs on Windows and Android show the release summary and offer update, skip once, or skip forever.
- No accounts, cloud database, or third-party push service; the default port is `1122`.

## Recent updates

- Fixed Cloudflared edge disconnect recovery, undrained pipes in manual launches, and lost diagnostics across multiple public endpoints while waiting for real reconnection results.
- Improved native ComfyUI workflow conversion by fixing wildcard and Reroute parameter mapping, adding `.gg.json` controller-group sidecars, and loading that metadata for API-only workflows.
- Added regression coverage for Cloudflared, the workflow converter, and API-only group metadata, then verified the release build, health endpoint, and conversion of 28 workflows.

## Quick start

### Windows

```powershell
.\tools\package-pc.ps1
```

Start `TapRelay.exe` after packaging.

### Android

```powershell
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"
adb install -r .\android-app\app\build\outputs\apk\debug\app-debug.apk
```

Open Android **Device** and scan the PC QR code or enter its LAN address.

### Send a notification

```powershell
$body = @{ message = "Task completed"; source = "example-agent"; status = "completed"; taskId = "demo-1" } |
  ConvertTo-Json -Compress
Invoke-RestMethod "http://127.0.0.1:1122/send" -Method Post `
  -ContentType "application/json; charset=utf-8" -Body $body
```

### ComfyUI

Put workflows in [`pc-app/Assets/ComfyUIConsole/api_workflows`](./pc-app/Assets/ComfyUIConsole/api_workflows) and run ComfyUI on the PC.

## Repository layout

```text
pc-app/       Windows desktop app and built-in service
android-app/  Android client
notify-server Node.js protocol service and tests
tools/        build, packaging, and integration scripts
tests/        automated tests
docs/         reports and project notes
```

## Security

The default transport has no authentication or encryption and is intended for a trusted LAN. Do not expose port `1122` directly to the public Internet.

## Development checks

```powershell
dotnet build .\pc-app\TapRelay.csproj -c Debug
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"
Push-Location .\notify-server; npm ci; npm test; Pop-Location
```

## Related projects

- [SuYan](https://github.com/guliacer/SuYan)
- [ComfyUI-GuliNodes](https://github.com/guliacer/ComfyUI-GuliNodes)
