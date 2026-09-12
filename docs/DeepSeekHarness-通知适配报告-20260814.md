# DeepSeek Harness 通知适配报告

## 1. 适配范围

把本地 DeepSeek Harness（DeepSeek 出品的 Electron 桌面版 AI 智能体，安装于 `C:\Program Files\DeepSeek Harness Desktop`）的任务状态通知接入 TapRelay，使手机端弹窗正确显示官方名称与图标。

DeepSeek Harness 与 Claude/ChatGPT 等智能体不同，它把每个工作区的会话记录为 **Zstandard 压缩的 JSONL 日志**（`harness-home\sessions\<workspace>\<sessionId>\session.jsonl.zstd`），因此采用与 OpenCode 类似的“事件驱动文件监控”而非 Hook。

## 2. 信号来源与状态映射

- 数据来源：`%APPDATA%\deepseek-harness-desktop\harness-home\sessions\` 下的所有 `session.jsonl.zstd`。
- 完成信号：`type=turn/end` 事件，`data.reason.kind` 字段决定最终状态（见下）。`projectName` 取自 `session` 事件的 `cwd` 叶目录。
- 权限摘要与级别（TurnEndReasonMap）：
  | kind | 映射 | 说明 |
  |---|---|---|
  | `completed` | `completed` | 智能体完成本轮响应 |
  | `error` | `failed` | 发生 LLM 错误（取 `error.message` 或 `error.code`） |
  | `blocked` | `waiting` | 任务暂停，等待用户继续 |
  | `aborted` (reason.user/legacy) | **静默忽略** | 用户取消 / 旧版停止，不误报失败 |
  | `aborted` (parent/disposed/hook) | 忽略 | 内部拆解，无用户可见结果 |
  | `max-tokens` | 忽略 | 被截断的中间回合 |
  | `interrupted` | 忽略 | 外部中断，无明确用户结果 |

- 子代理隔离：只有 `session` 事件中 `delegationDepth == 0` 的主任务会话才通知；子代理（depth>0）位于单独会话文件，全部忽略——与 Claude Haha `isSidechain` 过滤同义。
- 任务标识：`taskId` = `deepseekharness-{sessionId前16位}-turn{N}`。
- 耗时时长：从同回合的 `turn/start.time` 到 `turn/end.time` 计算。
- token 统计：按回合累计 `assistant/chunk` 中 `chunk.type == "usage"` 的 `inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens`（与 Claude 的累计语义一致）；`blocked`（waiting）中途不展示 token，完成/失败才携带。
- 去重：以 `{sessionId}:{turn}:{status}` 为主键，同一轮次的同一终态只通知一次；监控器从启动时读取的基线开始扫描，不重放历史完成记录。
- 等待宽限：阻塞（`blocked`）立即通知，与 Haha 审批等待（P067）不同——`blocked` 是回合级别且低频，无需顺延。

## 3. 名称与图标来源

- 名称：**DeepSeek Harness**（官方产品名）。
- appKey：`deepseekharness`（ASCII 小写，唯一，不在内置保留键中冲突）。
- 品牌色：`#4D6BFE`（DeepSeek 官方品牌蓝，取自官方 app 与官网）。
- 图标：从已安装的可执行文件 `DeepSeek Harness Desktop.exe` 中提取关联图标（`System.Drawing.Icon.ExtractAssociatedIcon`），输出 `256x256 PNG`。图标为纯黑 DeepSeek 品牌标记（官方 EXE 资源，符合适配规则 R5 优先级 1）。

## 4. 项目集成

### PC 端（`pc-app/`）

- 核心监控器：`pc-app/DeepSeekHarnessMonitor.cs`（新增，Zstandard 帧分割 + 流式解压 + 事件解析 + 文件系统监听）。
- Zstandard 依赖：`pc-app/TapRelay.csproj` 添加 `ZstdSharp.Port 0.8.1`（纯托管，netcoreapp3.1 兼容）。
- 监控器集成：`pc-app/AgentCompletionMonitor.cs`（字段 `_deepseekHarnessMonitor`，构造/Start/Dispose/Refresh/DispatchDeepSeekHarnessTask 分发 `source=deepseekharness`）。
- 进程别名与启动门控：`pc-app/AdaptedProcessGate.cs`（`deepseekharness` → `DeepSeek Harness Desktop`）。
- 目录元数据：`pc-app/AppCatalogMetadata.cs`（`deepseekharness` 列入 `BuiltInKeys` 与 tooltip）。
- 通知面板与品牌色：`pc-app/NotificationAppsPanel.cs`、`pc-app/NotificationAppRegistry.cs`（内置保留键 + `GetDisplayName`/`IsInstalled`/`DetectInstalledApps`/图标映射/品牌色色值）。
- 官方 Logo：`pc-app/Assets/ic_deepseekharness_logo.png`。
- 安装路径覆盖：`TAPRELAY_DEEPSEEKHARNESS_ROOT` 环境变量可指定 sessions 根目录（含自动持久化发现记录）。

### Node 服务端（`notify-server/`）

- 保留键列表：`notify-server/src/apps.js` 添加 `"deepseekharness"` 到 `RESERVED_KEYS`，防止被意外动态注册覆盖内置元数据。

### Android 端（`android-app/`）

- 渠道枚举：`data/Channel.kt`（`DeepseekHarness("DeepSeek Harness", …, "N")` + `from` 映射 + `canonicalSourceKey`）。
- 品牌色：`ui/theme/Color.kt`（`ChannelDeepseekHarness = Color(0xFF4D6BFE)`）。
- 图标：`ui/ChannelIcon.kt`（`ic_deepseekharness_logo.png`）+ `notification/NotificationHelper.kt`（渠道标题、smallIcon `ic_stat_deepseekharness`、largeIcon）。
- 图标资源：`drawable-nodpi/ic_deepseekharness_logo.png` + `ic_stat_deepseekharness.png`（白色字形透明底，适合系统通知）。

### 测试与验证（`tests/`）

- 新增测试项目：`tests/TapRelay.DeepSeekHarnessMonitor.Tests/`（17 个单元测试全部通过，覆盖状态映射、子代理排除、首轮基线去重、新追加去重、耗时计算、压缩写入折断尾部恢复、Zstandard 往返回归、usage 累计与 blocked 不携带 token）。
- PC 主项目 Release 构建：0 错误（仅有既存 Doubao/NotificationAppsPanel 警告，与本次改动无关）。
- `notify-server` Vitest：8 项通过。
- Android `channelTest`（deepseekHarnessSourceUsesDedicatedChannel）：通过。

## 5. 验证结果

- 启动日志确认：`已接入 DeepSeek Harness 会话目录：...\sessions（17 个会话日志）`、`已启用 DeepSeek Harness 任务状态监听（Zstandard 会话日志事件驱动）`。
- 运行中自动探测并上报历史终态：`broadcast source=deepseekharness status=failed projectName=TapRelay`（本轮次第一个回合发生了 503 503 CPU overload，状态被正确映射为 `failed` 并携带了原始 API 错误消息）。
- 实测 `POST /send`（`source=deepseekharness status=completed taskId=adapt-test-dsh-001`）：日志 `status=sent`。
- 正式 self-contained 包已通过 `tools/package-pc.ps1` 重新构建至 `pc-app/publish/TapRelay.exe` 并启动，`/health` 返回 `ok`。

## 6. 说明

- `deepseekharness` 为内置保留键，与 `claude`/`codex`/`opencode` 一致，不通过 `/apps/register` 注册——内置保留键由 `NotificationPanel` 的嵌入式资源直接提供图标与品牌色，避免被重复注册或覆盖内置元数据。
- DeepSeek Harness 进程名 `DeepSeek Harness Desktop` 仅用于激活监控门控：监控器仅在 DeepSeek Harness 运行时启用任务通知，进程退出自动暂停、重启自动恢复。
- 路径覆盖：`TAPRELAY_DEEPSEEKHARNESS_ROOT` 环境变量可指定 `sessions` 根目录；非标准路径通过运行时自动发现 + `deepseek-harness-roots.json` 持久化。
- 委托深度过滤：子代理由独立的 `session.jsonl.zstd`（`delegationDepth>0`）记录，主监控器只读取深度为 0 的会话——因此子代理结束永远不会被误报为主任务完成。
- 用户主动停止（`aborted reason.kind=user`）映射为取消并静默——不产生通知。