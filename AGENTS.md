# 项目规则总入口

## 适用范围
本文件适用于 `W:\TapRelay` 项目根目录下的全部后续开发工作，覆盖 PC WinForms 通知软件、Node.js 协议测试服务、Android Kotlin 客户端、ZCode/ChatGPT 触发脚本与项目规则读取顺序。

## 规则条目

### R1: 规则读取顺序
**断言**：任何 AI Coder 在新增、修改或删除项目文件前，必须先读取 `W:\TapRelay\AGENTS.md`，再读取本文件列出的相关编号规则文件；若入口文件与编号规则文件冲突，以编号规则文件为准。
**示例**：
```text
必须读取:
W:\TapRelay\AGENTS.md
W:\TapRelay\01-人设与开发准则.md
W:\TapRelay\02-技术栈与核心库.md
W:\TapRelay\03-目录与架构规范.md
W:\TapRelay\04-核心铁律与避坑.md

涉及 Android UI 时追加:
W:\TapRelay\05-UI与组件规范.md

涉及 HTTP/WebSocket 时追加:
W:\TapRelay\06-通信与数据获取规范.md

涉及本地状态/消息历史时追加:
W:\TapRelay\08-数据库与状态管理规范.md

涉及通用应用适配机制时追加:
W:\TapRelay\11-应用识别与适配规则.md
W:\TapRelay\.zcode\skills\adapt-app\SKILL.md

排查缺陷或做性能/行为修正时追加（必须先读，用于复用已知根因）:
W:\TapRelay\09-问题与优化记录.md（目录）
W:\TapRelay\09-问题与优化记录-内容.md（按目录主题读取）
W:\TapRelay\11-应用识别与适配规则.md
```
**违规特征**：未读取相关编号规则文件就改动 `pc-app/`、`notify-server/`、`android-app/` 或 `.zcode/`；在规则冲突时按 `AGENTS.md` 覆盖编号文件；排障时跳过 `09-问题与优化记录.md` 目录和 `09-问题与优化记录-内容.md` 对应主题，直接重新定位已收录的问题。

### R2: 规则文件索引
**断言**：项目规则只允许维护在 `W:\TapRelay\AGENTS.md` 与 `W:\TapRelay\NN-*.md` 编号文件中；不得把长期架构规则散落到源码注释、README 或临时聊天记录。
**示例**：
```text
W:\TapRelay\01-人设与开发准则.md
W:\TapRelay\02-技术栈与核心库.md
W:\TapRelay\03-目录与架构规范.md
W:\TapRelay\04-核心铁律与避坑.md
W:\TapRelay\05-UI与组件规范.md
W:\TapRelay\06-通信与数据获取规范.md
W:\TapRelay\08-数据库与状态管理规范.md
W:\TapRelay\09-问题与优化记录.md
W:\TapRelay\09-问题与优化记录-内容.md
W:\TapRelay\11-应用识别与适配规则.md
```
**违规特征**：把“服务端端口固定为 1122”只写在 `notify-server/server.js` 注释中；新增 `docs/rules.md` 但不更新本入口文件；把已定位的缺陷根因只留在对话里而不写入 `09-问题与优化记录.md`。

### R3: 项目边界
**断言**：本项目的可交付代码边界固定为 `W:\TapRelay\pc-app`、`W:\TapRelay\notify-server`、`W:\TapRelay\android-app`、`W:\TapRelay\.zcode\skills\notify-phone` 和 `W:\TapRelay\.zcode\skills\adapt-app`；除规则文件外，不得在项目根目录新增业务源码文件。
**示例**：
```text
合规:
W:\TapRelay\pc-app\MainForm.cs
W:\TapRelay\notify-server\server.js
W:\TapRelay\android-app\app\src\main\java\com\lan\notify\MainActivity.kt
W:\TapRelay\.zcode\skills\notify-phone\SKILL.md
W:\TapRelay\.zcode\skills\adapt-app\SKILL.md

违规:
W:\TapRelay\server.js
W:\TapRelay\MainForm.cs
W:\TapRelay\MainActivity.kt
W:\TapRelay\notify-phone.md
```
**违规特征**：直接在根目录放置 `server.js`、`package.json`、`MainActivity.kt` 或临时脚本作为正式实现。

### R4: 业务目标不可变
**断言**：本项目的核心业务是“PC 上 Agent 任务完成后，在同一局域网内推送通知到 Android 手机”。经用户明确要求，另包含 ComfyUI 手机工作流控制功能；除此以外不得把产品范围扩展为公网消息平台、多人账号系统或云端推送服务。
**示例**：
```text
固定业务链路:
Agent/ChatGPT/ZCode -> http://localhost:1122/send -> ws://<PC-LAN-IP>:1122 -> Android 系统通知
Android ComfyUI 工作流操作 -> http://<PC-LAN-IP>:1122/comfy/* -> PC 本机回环控制台 -> ComfyUI
```
**用户明确的 ComfyUI 手机控制例外**：允许 Android 在现有 `1122` 页面内访问 `/comfy`，选择已配置的 API 工作流、填写参数、上传图片、提交运行并查看结果。`/comfy` 只代理到 PC 本机回环地址上的随 TapRelay 生命周期管理的控制台进程；不得新增手机可见端口、云服务或公网消息链路。控制台管理页 `/comfy/admin` 及 `/comfy/api/admin/*` 只允许 PC 本机回环请求。
**违规特征**：引入 Firebase Cloud Messaging、登录注册、云数据库、多租户控制台或公网反向代理作为默认路径；为了 ComfyUI 另开 LAN 端口；把管理接口暴露给手机。
