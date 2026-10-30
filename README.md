# TapRelay

![TapRelay](./assets/TapRelay-logo.png)

**把 Windows PC 上的 AI 任务状态推送到 Android 手机，并在手机上控制 ComfyUI 工作流。**

[English README](./README.en.md)

## 功能

- PC 内置 HTTP / WebSocket 服务，Android 通过局域网接收完成、失败和待处理通知。
- Android 保存消息历史，支持审批、停止、关闭、地址脱敏和权限管理。
- ComfyUI 工作流在 Android 上选择、填参、上传图片、运行、查看结果并保存。
- 不需要账号、云数据库或第三方推送服务；默认只使用 TapRelay 的 `1122` 端口。

## 界面

| PC 端 | Android 任务页 | Android 设备页 |
| --- | --- | --- |
| ![PC 主界面](./assets/readme/pc-overview.png) | ![Android 任务页：连接摘要、消息历史和底栏](./assets/readme/android-main.png) | ![Android 设备页：地址脱敏、连接管理和权限入口](./assets/readme/android-device-management.jpg) |

Android 截图已按当前真机布局更新，地址已脱敏。

### ComfyUI 手机控制

| 工作流 | 参数 | 结果 | 生成图 |
| --- | --- | --- | --- |
| ![工作流列表：置顶、排序、导入和导出](./assets/readme/comfyui-workflows.jpg) | ![参数页：提示词、图片和常用参数](./assets/readme/comfyui-parameters.jpg) | ![结果页：预览、对比和保存](./assets/readme/comfyui-results.jpg) | ![生成图汇总：按工作流查看缓存结果](./assets/readme/comfyui-gallery.jpg) |

ComfyUI 控制台只监听 PC 回环地址，手机仍通过 TapRelay 的 `1122` 端口访问；手机不会看到额外端口。需要 PC 本机已运行 ComfyUI（默认 `http://127.0.0.1:8188`）。

## 快速开始

### PC

```powershell
.\tools\package-pc.ps1
```

脚本会构建、发布并检查 `http://127.0.0.1:1122/health`，然后启动 `TapRelay.exe`。

### Android

```powershell
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"
adb install -r .\android-app\app\build\outputs\apk\debug\app-debug.apk
```

打开 Android 后，在“设备”页面扫码或填写 PC 的局域网地址。PC 和手机需要处于可互访的网络；不要填写手机自己的 `127.0.0.1`。

### 发送通知

```powershell
$body = @{ message = "任务已完成"; source = "example-agent"; status = "completed"; taskId = "demo-1" } |
  ConvertTo-Json -Compress
Invoke-RestMethod "http://127.0.0.1:1122/send" -Method Post `
  -ContentType "application/json; charset=utf-8" -Body $body
```

## 项目结构

```text
pc-app/       Windows WinForms 应用和正式 HTTP/WebSocket 服务
android-app/  Kotlin / Jetpack Compose 客户端
notify-server Node.js 协议实现和 Vitest 测试
tools/        构建、打包和 Agent 接入脚本
tests/        PC 协议、可靠性和端到端测试
assets/readme README 截图
docs/         适配报告和第三方声明
```

ComfyUI 工作流统一放在 [`pc-app/Assets/ComfyUIConsole/api_workflows`](./pc-app/Assets/ComfyUIConsole/api_workflows)。

## 安全边界

默认通信没有身份认证和传输加密，只适合可信局域网。不要把 `1122` 直接暴露到公网，也不要把密码、令牌或私钥放进任务日志。

## 开发验证

```powershell
dotnet build .\pc-app\TapRelay.csproj -c Debug
.\tools\test-android.ps1 -JavaHome "C:\path\to\jdk-21.0.4"
Push-Location .\notify-server; npm ci; npm test; Pop-Location
```

## 相关项目

- [SuYan](https://github.com/guliacer/SuYan)：归档和管理 ComfyUI 生成素材。
- [ComfyUI-GuliNodes](https://github.com/guliacer/ComfyUI-GuliNodes)：内置工作流使用的 ComfyUI 节点插件。

问题反馈请附版本、复现步骤和脱敏日志，不要上传凭据或完整敏感任务内容。
