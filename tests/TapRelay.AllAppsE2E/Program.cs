using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: TapRelay.AllAppsE2E <user-profile> <application-data> <local-application-data>");
            return 2;
        }

        var home = Path.GetFullPath(args[0]);
        var appData = Path.GetFullPath(args[1]);
        var localAppData = Path.GetFullPath(args[2]);
        var run = Guid.NewGuid().ToString("N");
        var marker = "taprelay-e2e-" + run;
        var createdFiles = new List<string>();

        var claudePath = Prepare(Path.Combine(home, ".claude", "transcripts", marker + ".jsonl"), createdFiles);
        var codexPath = Prepare(Path.Combine(home, ".codex", "sessions", marker + ".jsonl"), createdFiles);
        var zcodePath = Prepare(Path.Combine(home, ".zcode", "cli", "log", marker + ".jsonl"), createdFiles);
        var zcodeDesktopPath = Prepare(Path.Combine(home, ".zcode", "v2", "logs", marker + ".log"), createdFiles);
        var traePath = Prepare(Path.Combine(appData, "Trae CN", "logs", marker, "renderer.log"), createdFiles);
        var traeSoloPath = Prepare(Path.Combine(appData, "TRAE SOLO CN", "logs", marker, "renderer.log"), createdFiles);
        var traeWorkPath = Prepare(Path.Combine(appData, "TraeWork CN", "logs", marker, "renderer.log"), createdFiles);
        var doubaoPath = Prepare(Path.Combine(localAppData, "Doubao", "User Data", "sdk_storage", "log", "saman_" + marker + ".log"), createdFiles);
        var workBuddyPath = Prepare(Path.Combine(home, ".workbuddy", "logs", marker + ".log"), createdFiles);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var ignoredTaskIds = new HashSet<string>(StringComparer.Ordinal);
        // stub 必须位于可信安装根（%LOCALAPPDATA%\Programs）之下，“进程正在运行”
        // 才能通过 IsRunningFromTrustedRoot 的路径校验，E2E 的事件才能通过通知门控。
        // 同时故意放深两层：即使清理失败，残留的 exe 也不会落在 HasExecutableInRoot
        // 的“根 + 一层子目录”扫描范围内，不会被误判为已安装。
        var stubRoot = Path.Combine(localAppData, "Programs", "taprelay-e2e-stubs", run);
        var stubProcesses = new List<Process>();
        string Key(string source, string status, string taskId) => source + "|" + status + "|" + taskId;

        var claudeCompletedMarker = marker + "-claude-completed";
        var claudeFailedMarker = marker + "-claude-failed";
        expected.Add(Key("claude", "completed", claudeCompletedMarker));
        expected.Add(Key("claude", "failed", claudeFailedMarker));

        var codexCompleted = marker + "-codex-completed";
        var codexFailed = marker + "-codex-failed";
        var codexWaiting = marker + "-codex-waiting";
        var codexCancelled = marker + "-codex-cancelled";
        expected.Add(Key("codex", "completed", codexCompleted));
        expected.Add(Key("codex", "failed", codexFailed));
        expected.Add(Key("codex", "waiting", marker + "-codex-session:" + codexWaiting));
        ignoredTaskIds.Add(codexCancelled);

        var traeSession = marker + "-trae-session";
        var traeTurn1 = marker + "-trae-turn-1";
        var traeTurn2 = marker + "-trae-turn-2";
        var traeFailed = marker + "-trae-failed";
        var traeWaiting = marker + "-trae-waiting";
        var traeIgnored = marker + "-trae-interrupted";
        expected.Add(Key("trae", "completed", traeSession + ":" + traeTurn1));
        expected.Add(Key("trae", "completed", traeSession + ":" + traeTurn2));
        expected.Add(Key("trae", "failed", traeSession + ":" + traeFailed));
        expected.Add(Key("trae", "waiting", traeSession + ":" + traeWaiting));
        ignoredTaskIds.Add(traeSession + ":" + traeIgnored);

        var soloSession = marker + "-solo-session";
        var soloCompleted = marker + "-solo-completed";
        var soloFailed = marker + "-solo-failed";
        var soloWaiting = marker + "-solo-waiting";
        var soloCancelled = marker + "-solo-cancelled";
        var workSession = marker + "-work-session";
        var workCompleted = marker + "-work-completed";
        expected.Add(Key("traework", "completed", soloSession + ":" + soloCompleted));
        expected.Add(Key("traework", "failed", soloSession + ":" + soloFailed));
        expected.Add(Key("traework", "waiting", soloSession + ":" + soloWaiting));
        expected.Add(Key("traework", "completed", workSession + ":" + workCompleted));
        ignoredTaskIds.Add(soloSession + ":" + soloCancelled);

        var zcodeSession = marker + "-zcode-session";
        var zcodeCompleted1 = marker + "-zcode-completed-1";
        var zcodeCompleted2 = marker + "-zcode-completed-2";
        var zcodeFailed = marker + "-zcode-failed";
        var zcodeCancelled = marker + "-zcode-cancelled";
        var repoWikiFalsePositive = marker + "-repo-wiki";
        expected.Add(Key("zcode", "completed", zcodeCompleted1));
        expected.Add(Key("zcode", "completed", zcodeCompleted2));
        expected.Add(Key("zcode", "failed", zcodeFailed));
        ignoredTaskIds.Add(zcodeCancelled);
        ignoredTaskIds.Add(repoWikiFalsePositive);

        var doubaoCompleted = marker + "-doubao-completed";
        var doubaoFailed = marker + "-doubao-failed";
        var doubaoCancelled = marker + "-doubao-cancelled";
        var doubaoList = marker + "-doubao-list";
        expected.Add(Key("doubao", "completed", doubaoCompleted));
        expected.Add(Key("doubao", "failed", doubaoFailed));
        ignoredTaskIds.Add(doubaoCancelled);
        ignoredTaskIds.Add(doubaoList);

        var workBuddySession = marker + "-workbuddy-session";
        var workBuddyCompleted = marker + "-workbuddy-completed";
        var workBuddyFailed = marker + "-workbuddy-failed";
        var workBuddyWaiting = marker + "-workbuddy-waiting";
        var workBuddyCancelled = marker + "-workbuddy-cancelled";
        // The PC server keeps the internal source key as "workbuddy" but
        // emits "workbuddy-remote" on the wire for older mobile clients.
        expected.Add(Key("workbuddy-remote", "completed", workBuddySession + ":" + workBuddyCompleted));
        expected.Add(Key("workbuddy-remote", "failed", workBuddySession + ":" + workBuddyFailed));
        expected.Add(Key("workbuddy-remote", "waiting", workBuddySession + ":" + workBuddyWaiting));
        ignoredTaskIds.Add(workBuddyCancelled);

        using var socket = new ClientWebSocket();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:1122"), connectTimeout.Token);
        Console.WriteLine("PASS: WebSocket connected");

        var autoDetectWindow = IntPtr.Zero;
        try
        {
            stubProcesses = StartAdaptedProcessStubs(stubRoot);
            // trae/zcode/workbuddy 本机未安装，来源默认处于禁用态，事件会被
            // “来源已禁用”门控丢弃。stub 位于可信安装根之下，触发生产实例重跑
            // 自动检测后，IsRunningFromTrustedRoot 会把这三个来源判为已安装并
            // 启用，事件才能完整送达（TraeWork/Claude/ChatGPT/豆包真实安装，
            // 启动时已启用，无需此步骤）。
            autoDetectWindow = await TriggerProductionAutoDetectAsync(localAppData);
            await Task.Delay(1400);
            var now = DateTimeOffset.UtcNow;

            Append(claudePath,
                JsonSerializer.Serialize(new { type = "assistant", session_id = marker + "-claude-session", cwd = "W:\\TapRelay", message = new { content = new[] { new { type = "text", text = claudeCompletedMarker } } } }) + "\n" +
                JsonSerializer.Serialize(new { type = "system", subtype = "turn_duration", session_id = marker + "-claude-session", timestamp = now.ToString("O") }) + "\n" +
                JsonSerializer.Serialize(new { type = "assistant", session_id = marker + "-claude-session", cwd = "W:\\TapRelay", isApiErrorMessage = 1, message = new { content = new[] { new { type = "text", text = claudeFailedMarker } } } }) + "\n" +
                JsonSerializer.Serialize(new { type = "system", subtype = "turn_duration", session_id = marker + "-claude-session", timestamp = now.AddMilliseconds(1).ToString("O") }) + "\n");

            var codexCompletedLine = JsonSerializer.Serialize(new { timestamp = now.ToString("O"), payload = new { type = "task_complete", task_id = codexCompleted, cwd = "W:\\TapRelay" } });
            Append(codexPath,
                codexCompletedLine + "\n" +
                codexCompletedLine + "\n" +
                JsonSerializer.Serialize(new { timestamp = now.AddMilliseconds(1).ToString("O"), payload = new { type = "task_failed", task_id = codexFailed, cwd = "W:\\TapRelay", error = "synthetic codex failure" } }) + "\n" +
                JsonSerializer.Serialize(new { timestamp = now.AddMilliseconds(2).ToString("O"), payload = new { type = "exec_approval_request", session_id = marker + "-codex-session", turn_id = codexWaiting, prompt = "Allow command?" } }) + "\n" +
                JsonSerializer.Serialize(new { timestamp = now.AddMilliseconds(3).ToString("O"), payload = new { type = "turn.failed", session_id = marker + "-codex-session", turn_id = codexCancelled, status = "cancelled" } }) + "\n");

            string Trae(string turn, int interrupted) =>
                "event: code_comp_complete_shown ; params:" + JsonSerializer.Serialize(new { session_id = traeSession, turn_id = turn, agent_name = marker, repo_working_dir = "W:\\TapRelay", is_interrupted = interrupted });
            string TraeFailure() =>
                "event: code_comp_fail ; params:" + JsonSerializer.Serialize(new { session_id = traeSession, message_id = traeFailed, agent_name = marker, error_message = "synthetic failure" });
            string TraeWaiting() =>
                "event: code_comp_selection_required ; params:" + JsonSerializer.Serialize(new { session_id = traeSession, message_id = traeWaiting, agent_name = marker });
            Append(traePath, Trae(traeTurn1, 0) + "\n" + Trae(traeTurn2, 0) + "\n" + Trae(traeTurn1, 0) + "\n" + Trae(traeIgnored, 1) + "\n" + TraeFailure() + "\n" + TraeWaiting() + "\n");

            Append(traeSoloPath,
                "[DoneHandler] Stream done event received " + JsonSerializer.Serialize(new { sessionId = soloSession, status = "completed", agentMessageId = soloCompleted }) + "\n" +
                "[DoneHandler] Stream done event received " + JsonSerializer.Serialize(new { sessionId = soloSession, status = "failed", agentMessageId = soloFailed }) + "\n" +
                "[NotificationPort] Sending waiting notification: " + JsonSerializer.Serialize(new { sessionId = soloSession, agentMessageId = marker + "-agent", planItemId = soloWaiting }) + "\n" +
                "[DoneHandler] Stream done event received " + JsonSerializer.Serialize(new { sessionId = soloSession, status = "canceled", agentMessageId = soloCancelled }) + "\n");
            // TraeWork CN 目录可能在本轮 TapRelay 启动后才创建（watcher 每 15 秒
            // 重新注册新目录，且注册时会跳过已写入的历史内容）。这里先空等一个
            // 恢复扫描周期，让 watcher 追上目录，再追加事件，模拟真实应用
            // 安装/迁移目录后持续写日志的场景。
            await Task.Delay(TimeSpan.FromSeconds(16));
            Append(traeWorkPath,
                "[DoneHandler] Stream done event received " + JsonSerializer.Serialize(new { sessionId = workSession, status = "completed", agentMessageId = workCompleted }) + "\n");

            string ZCode(string eventName, string status, string turnId, DateTimeOffset timestamp) => JsonSerializer.Serialize(new
            {
                timestamp = timestamp.ToString("O"),
                @event = eventName,
                sessionId = zcodeSession,
                turnId,
                status,
                context = new { workspacePath = "W:\\TapRelay" }
            });
            Append(zcodePath,
                ZCode("turn.completed", "completed", zcodeCompleted1, now) + "\n" +
                ZCode("turn.completed", "completed", zcodeCompleted2, now.AddMilliseconds(1)) + "\n" +
                ZCode("turn.failed", "failed", zcodeFailed, now.AddMilliseconds(2)) + "\n" +
                ZCode("turn.failed", "cancelled", zcodeCancelled, now.AddMilliseconds(3)) + "\n" +
                ZCode("turn.completed", "completed", zcodeCompleted1, now) + "\n");
            Append(zcodeDesktopPath, "[host] [repoWiki] Wiki task_complete refresh skipped {\"workspaceKey\":\"W:\\\\TapRelay\",\"taskId\":\"" + repoWikiFalsePositive + "\"}\n");

            string Doubao(string requestId, string ret, string baseStatus, int responseCode, string originUrl) =>
                "[AhaNetSDK LOG] [rlog] req_id=" + requestId + " ret=" + ret + " request_log=" +
                JsonSerializer.Serialize(new
                {
                    @base = new { origin_url = originUrl, status = baseStatus },
                    response = new { code = responseCode }
                });
            Append(doubaoPath,
                Doubao(doubaoCompleted, "Ok(200)", "SUCCESS", 200, "https://www.doubao.com/chat/completion") + "\n" +
                Doubao(doubaoFailed, "Err(Msg(\"server error\"))", "FAILED", 500, "https://www.doubao.com/chat/completion") + "\n" +
                Doubao(doubaoCancelled, "Err(Msg(\"request canceled\"))", "IO_PENDING", 200, "https://www.doubao.com/chat/completion") + "\n" +
                Doubao(doubaoList, "Ok(200)", "SUCCESS", 200, "https://mcs.doubao.com/list") + "\n" +
                Doubao(doubaoCompleted, "Ok(200)", "SUCCESS", 200, "https://www.doubao.com/chat/completion") + "\n");

            string WorkBuddy(string message) =>
                "[2026/8/4 12:00:00.000] [Info] [ACP StreamManager] outgoingWritable.write called, message=" + message;
            var workBuddyCompletedLine = WorkBuddy(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId = workBuddySession,
                    update = new
                    {
                        sessionUpdate = "session_end",
                        stopReason = "end_turn",
                        _meta = new Dictionary<string, string> { { "codebuddy.ai/requestId", workBuddyCompleted } }
                    }
                }
            }));
            var workBuddyFailedLine = WorkBuddy(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId = workBuddySession,
                    update = new
                    {
                        sessionUpdate = "session_end",
                        stopReason = "refusal",
                        _meta = new Dictionary<string, string> { { "codebuddy.ai/requestId", workBuddyFailed } }
                    }
                }
            }));
            var workBuddyWaitingLine = WorkBuddy(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = workBuddyWaiting,
                method = "session/request_permission",
                @params = new
                {
                    sessionId = workBuddySession,
                    toolCall = new { toolCallId = workBuddyWaiting, title = "Allow command?" },
                    options = new object[0]
                }
            }));
            var workBuddyCancelledLine = WorkBuddy(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId = workBuddySession,
                    update = new { sessionUpdate = "session_end", stopReason = "cancelled" }
                }
            }));
            Append(workBuddyPath,
                workBuddyCompletedLine + "\n" +
                workBuddyCompletedLine + "\n" +
                workBuddyFailedLine + "\n" +
                workBuddyWaitingLine + "\n" +
                workBuddyCancelledLine + "\n");

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            while (!receiveTimeout.IsCancellationRequested && counts.Count < expected.Count)
            {
                var payload = await ReceiveAsync(socket, receiveTimeout.Token);
                if (payload == null) continue;
                var taskIdentity = payload.TaskId;
                if (payload.Source == "claude")
                {
                    if (payload.Content.Contains(claudeCompletedMarker, StringComparison.Ordinal)) taskIdentity = claudeCompletedMarker;
                    else if (payload.Content.Contains(claudeFailedMarker, StringComparison.Ordinal)) taskIdentity = claudeFailedMarker;
                }
                var key = Key(payload.Source, payload.Status, taskIdentity);
                if (ignoredTaskIds.Contains(payload.TaskId)) throw new InvalidOperationException("Ignored event was delivered: " + payload.TaskId);
                if (!expected.Contains(key)) continue;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                if (counts[key] > 1) throw new InvalidOperationException("Duplicate delivery: " + key);
                Console.WriteLine("PASS: " + key);
            }

            foreach (var key in expected)
            {
                if (!counts.TryGetValue(key, out var count) || count != 1)
                    throw new InvalidOperationException("Missing delivery: " + key);
            }

            using var quietTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!quietTimeout.IsCancellationRequested)
            {
                try
                {
                    var payload = await ReceiveAsync(socket, quietTimeout.Token);
                    if (payload == null) continue;
                    var taskIdentity = payload.TaskId;
                    if (payload.Source == "claude")
                    {
                        if (payload.Content.Contains(claudeCompletedMarker, StringComparison.Ordinal)) taskIdentity = claudeCompletedMarker;
                        else if (payload.Content.Contains(claudeFailedMarker, StringComparison.Ordinal)) taskIdentity = claudeFailedMarker;
                    }
                    var key = Key(payload.Source, payload.Status, taskIdentity);
                    if (expected.Contains(key) || ignoredTaskIds.Contains(payload.TaskId))
                        throw new InvalidOperationException("Delayed duplicate or ignored event was delivered: " + key);
                }
                catch (OperationCanceledException) { break; }
            }

            Console.WriteLine($"All-app monitor E2E: {expected.Count} expected deliveries, 0 missing, 0 duplicate, 0 ignored-event deliveries.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("FAIL: timed out waiting for expected deliveries.");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
        finally
        {
            foreach (var file in createdFiles) TryDelete(file);
            foreach (var process in stubProcesses)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit(2000);
                }
                catch { }
                finally { process.Dispose(); }
            }
            TryDeleteDirectory(stubRoot);
            // 顺带清掉已空的 stub 基目录；目录非空（并发 E2E）时忽略。
            try { Directory.Delete(Path.Combine(localAppData, "Programs", "taprelay-e2e-stubs")); } catch { }
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeTimeout.Token);
                }
                catch { }
            }
            // stub 进程与目录已清理完毕，再触发一次自动检测，把 trae/zcode/workbuddy
            // 恢复为“未安装/未启用”，避免生产实例 UI 残留测试造成的假安装状态。
            if (autoDetectWindow != IntPtr.Zero)
            {
                await Task.Delay(500);
                NativeMethods.PostMessage(autoDetectWindow, WmAppTriggerAutoDetect, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private static string Prepare(string path, List<string> createdFiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
        createdFiles.Add(path);
        return path;
    }

    // MainForm 暴露的测试专用消息（WM_APP+4）：与托盘运行兼容，触发一次
    // RunNotificationAutoDetectAsync，等价于点击“自动检测”按钮。
    private const int WmAppTriggerAutoDetect = 0x8004;

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// 触发生产实例重跑自动检测，并等待其把 stub 代表的来源加入启用集合。
    /// 返回主窗口句柄供测试结束后恢复面板状态复用。
    /// </summary>
    private static async Task<IntPtr> TriggerProductionAutoDetectAsync(string localAppData)
    {
        var window = FindProductionMainWindow();
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("published TapRelay main window not found for auto-detect trigger");
        }

        var logPath = Path.Combine(localAppData, "LanNotify", "pc-server.log");
        long baselineLength = 0;
        try
        {
            var info = new FileInfo(logPath);
            if (info.Exists) baselineLength = info.Length;
        }
        catch { }

        if (!NativeMethods.PostMessage(window, WmAppTriggerAutoDetect, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("failed to post auto-detect trigger to TapRelay");
        }

        // OnEnabledAppsChanged 完成后会向 pc-server.log 写入“已启用通知应用：…”。
        // 轮询基线之后追加的内容，确认 trae/zcode/workbuddy 已进入启用列表。
        string? lastLine = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > baselineLength)
                {
                    stream.Seek(baselineLength, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var appended = reader.ReadToEnd();
                    var index = appended.IndexOf("已启用通知应用", StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        var end = appended.IndexOf('\n', index);
                        lastLine = appended.Substring(index, (end < 0 ? appended.Length : end) - index).TrimEnd('\r');
                    }
                }
            }
            catch
            {
                // 日志暂时被占用时下一轮重试。
            }

            if (lastLine != null)
            {
                // 列表以“、”连接；必须按键名精确匹配，否则 “traework” 会
                // 让 Contains("trae") 误判通过。
                var colonIndex = lastLine.IndexOf('：');
                var listPart = colonIndex >= 0 ? lastLine.Substring(colonIndex + 1) : string.Empty;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in listPart.Split('、'))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        keys.Add(trimmed);
                    }
                }

                if (keys.Contains("trae") && keys.Contains("zcode") && keys.Contains("workbuddy"))
                {
                    Console.WriteLine("PASS: auto-detect re-render enabled stub sources (trae/zcode/workbuddy)");
                    return window;
                }
            }
        }

        throw new InvalidOperationException(
            "timed out waiting for TapRelay auto-detect to enable stub sources" +
            (lastLine == null ? string.Empty : "; last enabled list: " + lastLine));
    }

    private static IntPtr FindProductionMainWindow()
    {
        foreach (var process in Process.GetProcessesByName("TapRelay"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName ?? string.Empty;
                    if (path.IndexOf("pc-app", StringComparison.OrdinalIgnoreCase) < 0 ||
                        path.IndexOf("publish", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var handle = FindMainWindowForProcess(process.Id);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
                catch
                {
                    // 无法读取路径的进程（权限不足等）跳过。
                }
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindMainWindowForProcess(int processId)
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != (uint)processId)
            {
                return true;
            }

            var className = new StringBuilder(128);
            NativeMethods.GetClassName(hWnd, className, className.Capacity);
            var title = new StringBuilder(128);
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);
            if (className.ToString().StartsWith("WindowsForms10.Window", StringComparison.Ordinal) &&
                title.ToString() == "TapRelay")
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void Append(string path, string text) => File.AppendAllText(path, text, new UTF8Encoding(false));

    private static async Task<Payload?> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("WebSocket closed.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        var root = json.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "message") return null;
        return new Payload
        {
            Source = Read(root, "source"),
            Status = Read(root, "status"),
            TaskId = Read(root, "taskId"),
            Content = Read(root, "content")
        };
    }

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static List<Process> StartAdaptedProcessStubs(string root)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(source))
        {
            throw new InvalidOperationException("powershell.exe was not found for process-gate E2E stubs");
        }

        var processNames = new[]
        {
            "Claude",
            "ChatGPT",
            "Trae CN",
            "TRAE SOLO CN",
            "TraeWork CN",
            "zcode",
            "Doubao",
            "WorkBuddy"
        };
        var processes = new List<Process>();
        try
        {
            foreach (var processName in processNames)
            {
                var executable = Path.Combine(root, processName + ".exe");
                File.Copy(source, executable, true);
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 90\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process == null)
                {
                    throw new InvalidOperationException("failed to start process-gate E2E stub: " + processName);
                }
                processes.Add(process);
            }
            return processes;
        }
        catch
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                process.Dispose();
            }
            TryDeleteDirectory(root);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    private sealed class Payload
    {
        internal string Source { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal string TaskId { get; set; } = string.Empty;
        internal string Content { get; set; } = string.Empty;
    }
}
