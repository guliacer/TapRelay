using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace TapRelay
{
    internal static class Program
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

[STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(arg => string.Equals(arg, "--codex-notify", StringComparison.OrdinalIgnoreCase)))
            {
                return HandleChatGPTNotify(args);
            }

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例规则：同一时刻只允许一个 TapRelay 桌面端运行。
            // 新实例启动时通知旧实例自动退出（释放 1122 端口），
            // 等待其完全退出后新实例接管，不会出现两个实例并存。
            const string mutexName = "TapRelay.SingleInstance";
            const string exitSignalName = "TapRelay.ExitSignal";
            using (var mutex = new Mutex(true, mutexName, out var isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    // 先给旧实例一个有序退出的机会；若其卡在关闭流程，则自动结束
                    // 同一用户会话里的残留 TapRelay 进程，避免把清理工作留给用户。
                    AskOldInstanceToExit(exitSignalName);
                    if (!TryAcquireMutex(mutex, TimeSpan.FromSeconds(3)))
                    {
                        TerminateStaleInstances();
                    }

                    if (!TryAcquireMutex(mutex, TimeSpan.FromSeconds(5)))
                    {
                        MessageBox.Show(
                            "TapRelay 未能释放旧实例占用的资源，请稍后重试。",
                            "TapRelay",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return 1;
                    }
                }

                using (var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, exitSignalName))
                {
                    Application.Run(new MainForm(exitSignal));
                }
            }
            return 0;
        }

        private static bool TryAcquireMutex(Mutex mutex, TimeSpan timeout)
        {
            try
            {
                return mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // 残留实例异常退出时，Windows 会把已获得的互斥锁交给当前实例。
                return true;
            }
        }

        /// <summary>
        /// 仅结束当前用户会话中的同名残留实例。先请求关闭窗口，再在短暂等待后
        /// 强制结束，保证新实例能接管 1122 端口与单实例互斥锁。
        /// </summary>
        private static void TerminateStaleInstances()
        {
            using var current = Process.GetCurrentProcess();
            var staleInstances = Process.GetProcessesByName("TapRelay")
                .Where(process => process.Id != current.Id && process.SessionId == current.SessionId)
                .ToArray();

            foreach (var process in staleInstances)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                    // 进程可能刚好退出，后续统一检查即可。
                }
            }

            var gracefulDeadline = DateTime.UtcNow.AddSeconds(1);
            foreach (var process in staleInstances)
            {
                try
                {
                    var remaining = gracefulDeadline - DateTime.UtcNow;
                    if (!process.HasExited && remaining > TimeSpan.Zero)
                    {
                        process.WaitForExit(Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                    }
                }
                catch
                {
                    // 无法等待的进程继续走强制清理。
                }
            }

            foreach (var process in staleInstances)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // 最终由 TryAcquireMutex 给出失败结果；不能在这里误杀其他进程。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// 通知旧实例退出。旧实例的事件句柄可能尚未创建（启动竞态），
        /// 短时间重试直到成功或超时。
        /// </summary>
        private static bool AskOldInstanceToExit(string exitSignalName)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (var signal = EventWaitHandle.OpenExisting(exitSignalName))
                    {
                        signal.Set();
                        return true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
            return false;
        }

        private static int HandleChatGPTNotify(string[] args)
        {
            try
            {
                var rawArgs = string.Join(" ", args);
                WriteChatGPTLog("收到回调参数：" + rawArgs);
                var eventJson = args
                    .Where(arg => !string.Equals(arg, "--codex-notify", StringComparison.OrdinalIgnoreCase))
                    .Reverse()
                    .FirstOrDefault(arg => TryParseJson(arg, out _));

                if (string.IsNullOrWhiteSpace(eventJson) && Console.IsInputRedirected)
                {
                    eventJson = Console.In.ReadToEnd();
                }

                using var json = TryParseJson(eventJson, out var document)
                    ? document
                    : null;
                if (json == null)
                {
                    // A bare `turn-ended` hook callback carries no authoritative
                    // task result. The session-file monitor will publish the
                    // later task_complete/task_failed record instead.
                    WriteChatGPTLog("忽略没有结构化任务终态的回调");
                    return 0;
                }
                var root = json?.RootElement ?? default;
                var payloadRoot = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("payload", out var nestedPayload) &&
                    nestedPayload.ValueKind == JsonValueKind.Object)
                {
                    payloadRoot = nestedPayload;
                }

                var type = FirstNonEmpty(
                    ReadString(payloadRoot, "type"),
                    ReadString(payloadRoot, "event"),
                    ReadString(payloadRoot, "status"),
                    ReadString(root, "type"),
                    ReadString(root, "event"),
                    ReadString(root, "status"));
                if (string.IsNullOrWhiteSpace(type))
                {
                    type = InferEventType(rawArgs);
                }

                if (string.Equals(type, "turn-ended", StringComparison.OrdinalIgnoreCase))
                {
                    // `turn-ended` is only a lifecycle hook marker. It is not a
                    // completion event and has no success/failure semantics.
                    WriteChatGPTLog("忽略 turn-ended 生命周期回调");
                    return 0;
                }

                if (IsChatGPTPerItemEvent(type))
                {
                    // Codex reports item_completed/item_started for individual
                    // tool or response items. They do not close the turn.
                    WriteChatGPTLog("忽略 Codex 单项生命周期事件：" + type);
                    return 0;
                }

                if (!IsCompletionEvent(type) && !IsFailureEvent(type))
                {
                    WriteChatGPTLog("忽略非完成事件：" + type);
                    return 0;
                }

                var failed = IsFailureEvent(type) ||
                             HasCodexTaskError(payloadRoot) ||
                             HasCodexTaskError(root);
                var taskId = FirstNonEmpty(
                    ReadString(payloadRoot, "turn-id"),
                    ReadString(payloadRoot, "turn_id"),
                    ReadString(payloadRoot, "taskId"),
                    ReadString(payloadRoot, "task_id"),
                    ReadString(payloadRoot, "thread-id"),
                    ReadString(payloadRoot, "thread_id"),
                    ReadString(root, "turn-id"),
                    ReadString(root, "turn_id"),
                    ReadString(root, "taskId"),
                    ReadString(root, "task_id"),
                    ReadString(root, "thread-id"),
                    ReadString(root, "thread_id"));
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    WriteChatGPTLog("忽略缺少任务 ID 的完成回调：" + type);
                    return 0;
                }
                var cwd = FirstNonEmpty(ReadString(payloadRoot, "cwd"), ReadString(root, "cwd"));
                var durationMs = FirstPositiveLong(
                    ReadLong(payloadRoot, "durationMs"),
                    ReadLong(payloadRoot, "duration_ms"),
                    ReadLong(root, "durationMs"),
                    ReadLong(root, "duration_ms"));
                var eventMessage = FirstNonEmpty(
                    ReadString(payloadRoot, "message"),
                    ReadString(payloadRoot, "last_agent_message"),
                    ReadString(payloadRoot, "last_assistant_message"),
                    ReadString(payloadRoot, "summary"),
                    ReadString(root, "message"),
                    ReadString(root, "last_agent_message"),
                    ReadString(root, "last_assistant_message"),
                    ReadObjectMessage(payloadRoot, "error"),
                    ReadObjectMessage(root, "error"));
                var message = failed ? "ChatGPT 任务失败" : "ChatGPT 任务已完成";
                var detail = SummarizeNotifyDetail(eventMessage);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    message += "：" + detail;
                }

                var payload = JsonSerializer.Serialize(new
                {
                    message,
                    source = "codex",
                    status = failed ? "failed" : "completed",
                    taskId,
                    cwd,
                    durationMs
                });

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = HttpClient.PostAsync(BuildLocalSendUrl(), content)
                    .GetAwaiter()
                    .GetResult();
                response.EnsureSuccessStatusCode();
                WriteChatGPTLog("已发送完成通知：" + message);
                return 0;
            }
            catch (Exception error)
            {
                WriteChatGPTLog(error.ToString());
                return 1;
            }
        }

        private static string InferEventType(string rawArgs)
        {
            if (rawArgs.IndexOf("agent-turn-complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawArgs.IndexOf("task_complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawArgs.IndexOf("task-complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawArgs.IndexOf("turn_complete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "task_complete";
            }

            if (rawArgs.IndexOf("turn.completed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "turn.completed";
            }

            if (rawArgs.IndexOf("turn-ended", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "turn-ended";
            }

            if (rawArgs.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawArgs.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "failed";
            }

            return string.Empty;
        }

        private static string BuildLocalSendUrl()
        {
            var config = NetworkSettingsStore.Load();
            var host = config.ListenAddress;
            if (host == "0.0.0.0" || host == "*")
            {
                host = "127.0.0.1";
            }
            else if (host.Contains(":", StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
            {
                host = "[" + host + "]";
            }

            return "http://" + host + ":" + config.ListenPort + "/send";
        }

        private static void WriteChatGPTLog(string message)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "NotifyPc-chatgpt-notify.log");
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // 回调失败时不能影响桌面端进程。
            }
        }

        private static bool TryParseJson(string? value, out JsonDocument? document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                document = JsonDocument.Parse(value);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsCompletionEvent(string type)
        {
            return type.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("finished", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(type, "turn-ended", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "task_complete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "task-complete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "turn_complete", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsChatGPTPerItemEvent(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            var normalized = type.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');
            return string.Equals(normalized, "response_item", StringComparison.Ordinal) ||
                   normalized.StartsWith("item_", StringComparison.Ordinal) ||
                   normalized.EndsWith("_item_completed", StringComparison.Ordinal) ||
                   normalized.EndsWith("_item_started", StringComparison.Ordinal) ||
                   normalized.EndsWith("_item_failed", StringComparison.Ordinal);
        }

        private static bool HasCodexTaskError(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    foreach (var _ in error.EnumerateObject())
                    {
                        return true;
                    }
                }
                else if (error.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    return true;
                }
            }

            if (root.TryGetProperty("codex_error_info", out var errorInfo))
            {
                return errorInfo.ValueKind == JsonValueKind.Object
                    ? errorInfo.EnumerateObject().Any()
                    : errorInfo.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(errorInfo.GetString());
            }

            return false;
        }

        private static bool IsFailureEvent(string type)
        {
            return type.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("abort", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string ReadObjectMessage(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            {
                return string.Empty;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return value.ValueKind == JsonValueKind.Object
                ? ReadString(value, "message")
                : string.Empty;
        }

        private static string SummarizeNotifyDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return string.Empty;
            }

            var normalized = string.Join(
                " ",
                detail.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 460 ? normalized : normalized.Substring(0, 460) + "...";
        }

        private static long ReadLong(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            {
                return 0L;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number > 0L && number <= 2592000000L ? number : 0L;
            }

            return value.ValueKind == JsonValueKind.String &&
                   long.TryParse(value.GetString(), out number) &&
                   number > 0L && number <= 2592000000L
                ? number
                : 0L;
        }

        private static long FirstPositiveLong(params long[] values)
        {
            return values.FirstOrDefault(value => value > 0L);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
        }
    }
}
