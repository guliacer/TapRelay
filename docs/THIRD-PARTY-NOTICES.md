# 致谢与第三方来源

本文记录 TapRelay 使用、参考或链接到的第三方项目、平台和资料。为避免误解，
“实际使用”表示代码或构建流程依赖；“研究参考”表示用于确认数据结构、事件语义或
适配可行性的资料，不表示复制了对方源代码；“仅跳转”表示应用内提供了项目链接，
TapRelay 不会加载或打包这些项目的代码。

## 实际使用的方法和配套仓库

### 远程适配规则配套仓库

- 项目：[guliacer/TapRelay-remote-adapter-test](https://github.com/guliacer/TapRelay-remote-adapter-test)
- 当前规则源版本：`main` 提交 `7525810613e78a455a5d2e2effc9580f2c8e5fb9`
- 使用范围：提供声明式 `catalog.json` 适配规则、`version.json` 远程版本信息和规则图标；
  PC 端通过 HTTPS/Raw GitHub 下载，校验后缓存和热加载。
- 该仓库是 TapRelay 的配套规则源，不是远程可执行代码来源；TapRelay 不执行其中的 DLL、EXE、
  PowerShell 或 JavaScript。当前扫描未发现该仓库提供 `LICENSE` 或 `NOTICE` 文件，因此本文不
  对它作许可证推断。

## 直接依赖

以下依赖由工程清单直接声明，版本以对应的构建文件为准。它们属于运行时、测试或构建工具，
不是本项目自行实现的代码。

| 子项目 | 依赖 | 用途与许可证 |
|---|---|---|
| Android | [AndroidX / Jetpack Compose](https://github.com/androidx/androidx)、Material 3 | UI、生命周期和系统集成；Apache-2.0 |
| Android | [OkHttp](https://github.com/square/okhttp) | WebSocket/HTTP 通信；Apache-2.0 |
| Android | [Kotlin Coroutines](https://github.com/Kotlin/kotlinx.coroutines) | 异步任务和连接管理；Apache-2.0 |
| Android 测试 | [JUnit 4](https://github.com/junit-team/junit4) | 单元测试；EPL-1.0 |
| Android 测试 | [JSON-java](https://github.com/stleary/JSON-java) | JSON 测试辅助；JSON License |
| PC | [.NET Runtime / ASP.NET Core](https://github.com/dotnet/runtime)、[ASP.NET Core](https://github.com/dotnet/aspnetcore) | WinForms、HTTP 服务和系统 API；MIT |
| PC | [Entity Framework Core](https://github.com/dotnet/efcore) 的 `Microsoft.Data.Sqlite` | SQLite 任务数据读取；MIT |
| PC | [.NET Runtime](https://github.com/dotnet/runtime) 的 `System.Management` | WMI/进程事件读取；MIT |
| Node.js 服务 | [ws](https://github.com/websockets/ws) | WebSocket 服务；MIT |
| Node.js 测试 | [Vitest](https://github.com/vitest-dev/vitest)（其锁定的传递依赖包含 [Vite](https://github.com/vitejs/vite) 等） | 协议服务测试；Vitest MIT，传递依赖以 `package-lock.json` 为准 |

构建工具项目还包括 [Gradle](https://github.com/gradle/gradle)（Apache-2.0）、
[Kotlin](https://github.com/JetBrains/kotlin)（Apache-2.0）和
[Node.js](https://github.com/nodejs/node)（MIT）；Android Gradle Plugin 和 npm
属于对应平台的构建工具。其版本分别记录在 `android-app` 和 `notify-server` 的
工程清单中。Node.js 的传递依赖由 `notify-server/package-lock.json` 锁定，发布时应同时保留
其包管理器生成的许可证信息。

## 研究参考

这些仓库只出现在百度网盘适配报告的“官方资料与开源证据”部分，用于核对任务状态、事件回调
和数据库路径；TapRelay 没有把它们作为运行时依赖，也没有将其源文件复制进应用：

- [peterq/pan-light](https://github.com/peterq/pan-light)，固定参考提交
  `867eee7a9251a0a5e6ac5367b1ec84c98b6cfb1d`；LGPL-3.0。
- [qjfoidnh/BaiduPCS-Go](https://github.com/qjfoidnh/BaiduPCS-Go)，固定参考提交
  `d79b2647d7c8e5bff9f0150555725df5f282969d`；Apache-2.0。
- [Yensan/BaiduNetdisk-viplimited](https://github.com/Yensan/BaiduNetdisk-viplimited)，固定参考提交
  `27a58d9d76b15a7025c796f77a1a2e4340f7cbce`；GitHub 仓库元数据未声明许可证，本文不作推断。

百度网盘报告还引用了百度网盘开放平台、开放平台文档目录和版本更新页面。这些是官方资料
链接，不是 TapRelay 的代码依赖：

- [百度网盘开放平台](https://pan.baidu.com/union)
- [百度网盘开放平台简介](https://pan.baidu.com/union/doc/nksg0sbfs)
- [开放平台文档目录](https://pan.baidu.com/union/doc/sitemap-0.xml)
- [百度网盘版本更新](https://pan.baidu.com/disk/version)

## 应用内仅跳转的项目

PC 和 Android 的“个人项目”入口会展示并打开以下 GitHub 项目。这些地址只是推荐和跳转，
不代表 TapRelay 使用了它们的源代码或运行时服务：

- [SuYan](https://github.com/guliacer/SuYan)
- [ComfyUI-GuliNodes](https://github.com/guliacer/ComfyUI-GuliNodes)
- [Prompt Polish](https://github.com/guliacer/prompt-polish)
- [GetPhoto](https://github.com/guliacer/GetPhoto)
- [PagePurifier](https://github.com/guliacer/PagePurifier)
- [preview](https://github.com/guliacer/preview)
- [VeilReader](https://github.com/guliacer/VeilReader)
- [cookclick](https://github.com/guliacer/cookclick)
- [PixGo](https://github.com/guliacer/PixGo)
- [news-html-digest](https://github.com/guliacer/news-html-digest)

其中，PC 端源码明确将 PixGo 作为个人项目入口的线性图标风格参考；这属于视觉风格参考，
没有引入 PixGo 的源文件或运行时依赖。PC 和 Android 还内嵌了一个用于项目入口的简化
GitHub Mark 路径；它只用于链接按钮，不属于 GitHub SDK。品牌标识使用应遵守
[GitHub Logos and Usage](https://github.com/logos) 的要求。

## 扫描范围说明

本清单基于项目源码、构建清单、适配报告、工具目录和应用内链接整理。截图、运行日志、
`bin`/`obj`、`build` 产物和 `node_modules` 不作为人工引用来源；其中的传递依赖仍以各自的
包管理锁文件和上游许可证为准。
