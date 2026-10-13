using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TapRelay;

internal static partial class Program
{
    private static async Task TestPcOptimizations()
    {
        await Test("Protocol echoes take the diagnostic channel instead of the UI log", () =>
        {
            // 这里不再按文案前缀过滤，而是在发出点就决定走哪条通道：
            // Log -> LogLine（首页摘要 + 诊断文件），LogDetail -> DetailLogLine（仅诊断文件）。
            // 因此测试直接驱动这两个出口，验证事件分流本身，而不是去匹配字符串。
            using var server = new NotifyServer(new NotifyServerConfig());
            var uiLines = new List<string>();
            var detailLines = new List<string>();
            server.LogLine += uiLines.Add;
            server.DetailLogLine += detailLines.Add;

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            var log = typeof(NotifyServer).GetMethod("Log", instance)!;
            var logDetail = typeof(NotifyServer).GetMethod("LogDetail", instance)!;

            log.Invoke(server, new object[] { "手机已连接：192.168.1.5 client=abc" });
            log.Invoke(server, new object[] { "broadcast source=codex status=completed sentClients=1" });
            logDetail.Invoke(server, new object[]
            {
                "WebSocket response sent client=abc type=heartbeat_ack bytes=24"
            });
            logDetail.Invoke(server, new object[]
            {
                "手机 agent.call 回执已发送：client=abc messageId=m1"
            });

            Require(uiLines.Count == 2, "User-visible log should receive exactly the two event lines");
            Require(detailLines.Count == 2, "Diagnostic channel should receive exactly the two echo lines");
            foreach (var line in detailLines)
            {
                Require(!uiLines.Contains(line), "Diagnostic line leaked into the UI log: " + line);
            }
            Require(uiLines[0].StartsWith("手机已连接", StringComparison.Ordinal),
                "Connection event should stay visible in the UI log");
            Require(uiLines[1].StartsWith("broadcast", StringComparison.Ordinal),
                "Task broadcast should stay visible in the UI log");
            return Task.CompletedTask;
        });

        await Test("Diagnostic log writer rotates oversized files", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "taprelay-log-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "pc-server.log");
            try
            {
                File.WriteAllText(path, new string('x', 4 * 1024 * 1024), Encoding.UTF8);
                var writerType = typeof(NotifyServer).Assembly.GetType("TapRelay.DiagnosticLogWriter")!;
                var writer = Activator.CreateInstance(
                    writerType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new object[] { path },
                    null)!;
                writerType.GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(writer, new object[] { "rotation-check\n" });
                ((IDisposable)writer).Dispose();

                Require(File.Exists(path + ".1"), "Oversized diagnostic log was not rotated");
                Require(File.ReadAllText(path).Contains("rotation-check", StringComparison.Ordinal),
                    "New diagnostic line was not written after rotation");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            return Task.CompletedTask;
        });

        await Test("HTTP body limits reject oversized send and event requests", async () =>
        {
            var port = GetFreePort();
            using var server = new NotifyServer(new NotifyServerConfig
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port
            });
            var broadcasts = 0;
            server.OutboundBroadcast += _ => broadcasts++;
            await server.StartAsync();
            try
            {
                // 必须显式禁用代理：本机（以及 CI）常设有 HTTP_PROXY/HTTPS_PROXY，
                // HttpClient 默认会用它，于是发往 127.0.0.1 的请求被代理拦下并回 502，
                // 测试就会拿到一个与被测逻辑完全无关的响应。回环请求永远不该走代理。
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var sendBody = new StringContent(
                    "{\"message\":\"" + new string('x', 512 * 1024) + "\"}",
                    Encoding.UTF8,
                    "application/json");
                using var sendResponse = await client.PostAsync(
                    "http://127.0.0.1:" + port + "/send",
                    sendBody);
                Require((int)sendResponse.StatusCode == 413,
                    "Oversized /send body was accepted; actual=" + (int)sendResponse.StatusCode +
                    " reason=" + sendResponse.ReasonPhrase + " port=" + port);

                using var eventBody = new StringContent(
                    "{\"eventType\":\"task.completed\",\"source\":\"codex\",\"eventId\":\"" +
                    new string('x', 70 * 1024) + "\"}",
                    Encoding.UTF8,
                    "application/json");
                using var eventResponse = await client.PostAsync(
                    "http://127.0.0.1:" + port + "/events",
                    eventBody);
                Require((int)eventResponse.StatusCode == 413, "Oversized /events body was accepted");
                Require(broadcasts == 0, "Oversized request was broadcast");
            }
            finally
            {
                await server.StopAsync();
            }
        });

        await Test("StopAsync releases the port and returns within the hard ceiling", async () =>
        {
            // 「退出就直接解除占用」的可测形式：停止之后端口必须立刻能重新绑定，
            // 且停止本身必须有上限。
            // 背景：实测通用主机的 host.StopAsync 会间歇性地永不返回 —— 诊断日志停在
            // "开始停止服务"、既无"超时"也无"服务已停止"，进程滞留 130MB+ 不退，
            // 用户看到的现象就是"软件退出了但端口还被占着"（占着端口的是那个残留进程）。
            // StopAsync 内部因此用 WhenAny 兜了硬上限，这里把这个契约钉住。
            var port = GetFreePort();
            var server = new NotifyServer(new NotifyServerConfig
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port
            });
            await server.StartAsync();
            var stopwatch = Stopwatch.StartNew();
            await server.StopAsync();
            stopwatch.Stop();
            server.Dispose();
            Require(stopwatch.ElapsedMilliseconds < 3000,
                "StopAsync took " + stopwatch.ElapsedMilliseconds + "ms; the hard ceiling is not applied");

            // 停止后端口必须能再次绑定。实测 TIME_WAIT 不影响绑定（Windows 允许），
            // 只有监听 socket 还活着才会抛 AddressAlreadyInUse。
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
            }
            catch (SocketException error)
            {
                throw new Exception(
                    "Port " + port + " is still bound after StopAsync: " + error.SocketErrorCode);
            }
            finally
            {
                probe.Stop();
            }
        });

        await Test("StopAsync stays bounded and frees the port with live WebSocket clients", async () =>
        {
            // 「手机连着的时候退出」才是用户的真实场景：StopAsync 会先 AbortConnectedClients
            // 强拆所有客户端，再走优雅停止。此前所有退出测试都是 0 客户端，
            // 等于把 AbortConnectedClients 的循环体整段跳过了。
            var port = GetFreePort();
            var server = new NotifyServer(new NotifyServerConfig
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port
            });
            await server.StartAsync();

            var clients = new List<ClientWebSocket>();
            try
            {
                for (var index = 0; index < 3; index++)
                {
                    var client = new ClientWebSocket();
                    // ClientWebSocket 默认走系统代理，和 HttpClient 是同一个坑：
                    // 本机设有 HTTP_PROXY 时，连 127.0.0.1 会被代理拦下。
                    client.Options.Proxy = null;
                    await client.ConnectAsync(
                        new Uri("ws://127.0.0.1:" + port + "/ws"),
                        CancellationToken.None);
                    clients.Add(client);
                }

                await Until(
                    () => server.GetSnapshot().ClientCount == 3,
                    "Server did not register all three WebSocket clients",
                    3000);

                var stopwatch = Stopwatch.StartNew();
                await server.StopAsync();
                stopwatch.Stop();
                Require(stopwatch.ElapsedMilliseconds < 3000,
                    "StopAsync took " + stopwatch.ElapsedMilliseconds + "ms with live clients");
                Require(server.GetSnapshot().ClientCount == 0,
                    "Clients were not aborted; still registered: " + server.GetSnapshot().ClientCount);
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }

                server.Dispose();
            }

            // 端口必须已可重新绑定 —— 这就是"退出即解除占用"。
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
            }
            catch (SocketException error)
            {
                throw new Exception(
                    "Port " + port + " is still bound after StopAsync: " + error.SocketErrorCode);
            }
            finally
            {
                probe.Stop();
            }
        });

        await Test("OpenCode install check ignores the npm CLI shim without the desktop app", async () =>
        {
            // 桌面版已卸载、仅剩 npm 全局 CLI 时,不应被判为已安装。
            // 仅在本机确实没有桌面版安装根时断言,避免在装有桌面版的机器上误失败。
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var desktopPresent =
                System.IO.File.Exists(System.IO.Path.Combine(programFiles, "OpenCode", "OpenCode.exe")) ||
                System.IO.Directory.Exists(System.IO.Path.Combine(localAppData, "Programs", "@opencode"));
            if (desktopPresent)
            {
                return;
            }

            var panelType = typeof(NotifyServer).Assembly.GetType("TapRelay.NotificationAppsPanel");
            Require(panelType != null, "NotificationAppsPanel type not found");
            var method = panelType!.GetMethod(
                "HasInstallEvidence",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Require(method != null, "HasInstallEvidence not found");
            var installed = (bool)method!.Invoke(null, new object[] { "opencode" })!;
            Require(!installed,
                "opencode reported installed from npm CLI shim after desktop app removal");
            await Task.CompletedTask;
        });

        await Test("Freebuff install check ignores leftover session databases without the desktop app", async () =>
        {
            // 桌面版已卸载、仅剩 ~/.config/freebuff-desktop 会话库时,不应被判为已安装。
            // 仅在本机确实没有桌面版 exe 且未设置环境变量覆盖时断言,避免在装有桌面版的机器上误失败。
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var desktopPresent = System.IO.File.Exists(System.IO.Path.Combine(
                localAppData, "Programs", "@codebufffreebuff-desktop", "Freebuff.exe"));
            var overridden =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAPRELAY_FREEBUFF_STATE_PATH")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FREEBUFF_DESKTOP_STATE_PATH"));
            if (desktopPresent || overridden)
            {
                return;
            }

            var panelType = typeof(NotifyServer).Assembly.GetType("TapRelay.NotificationAppsPanel");
            Require(panelType != null, "NotificationAppsPanel type not found");
            var method = panelType!.GetMethod(
                "HasInstallEvidence",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Require(method != null, "HasInstallEvidence not found");
            var installed = (bool)method!.Invoke(null, new object[] { "freebuff" })!;
            Require(!installed,
                "freebuff reported installed from leftover session databases after desktop app removal");
            await Task.CompletedTask;
        });

        await Test("WorkBuddy install evidence does not come from leftover log directories", async () =>
        {
            // ~/.workbuddy/logs 与 ~/.workbuddy-ai/logs 卸载后仍残留,不能作为安装证据。
            // 仅在本机确实没有强安装证据(可信根 exe/有效注册表)时断言,避免在
            // 真实装有 WorkBuddy 的机器上误失败。
            var panelType = typeof(NotifyServer).Assembly.GetType("TapRelay.NotificationAppsPanel");
            Require(panelType != null, "NotificationAppsPanel type not found");
            var method = panelType!.GetMethod(
                "HasDesktopInstallSignal",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Require(method != null, "HasDesktopInstallSignal not found");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            foreach (var (appKey, displayNames) in new[]
            {
                ("workbuddy", new[] { "WorkBuddy" }),
                ("workbuddy-ai", new[] { "WorkBuddyAI", "WorkBuddy AI" })
            })
            {
                var strongEvidence = (bool)method!.Invoke(null, new object[]
                {
                    appKey, userProfile, appData, localAppData, programFiles, displayNames
                })!;
                if (strongEvidence)
                {
                    continue;
                }

                var hasInstallEvidence = panelType!.GetMethod(
                    "HasInstallEvidence",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Require(hasInstallEvidence != null, "HasInstallEvidence not found");
                var installed = (bool)hasInstallEvidence!.Invoke(null, new object[] { appKey })!;
                Require(!installed,
                    appKey + " reported installed from leftover ~/.workbuddy logs directory after removal");
            }
            await Task.CompletedTask;
        });

        await Test("Install signature rejects an install root missing required files", async () =>
        {
            // 残缺安装:启动 exe 在但核心文件(如 resources\app.asar)被删,
            // 签名校验必须判为不完整,不得再显示"已安装"。
            var signatureType = typeof(NotifyServer).Assembly.GetType("TapRelay.AppInstallSignature");
            Require(signatureType != null, "AppInstallSignature type not found");
            var isComplete = signatureType!.GetMethod(
                "IsComplete",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Require(isComplete != null, "AppInstallSignature.IsComplete not found");

            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "taprelay-signature-" + Guid.NewGuid().ToString("N"));
            var root = System.IO.Path.Combine(directory, "WorkBuddy");
            var resources = System.IO.Path.Combine(root, "resources");
            Directory.CreateDirectory(resources);

            try
            {
                // 1) 只有启动 exe、缺 app.asar → 不完整。
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "WorkBuddy.exe"), "stub");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "icudtl.dat"), "stub");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "resources.pak"), "stub");
                var complete = (bool)isComplete!.Invoke(null, new object[] { "workbuddy", root })!;
                Require(!complete,
                    "workbuddy install root missing resources\\app.asar was accepted as complete");

                // 2) 必须文件补齐 → 完整。
                System.IO.File.WriteAllText(System.IO.Path.Combine(resources, "app.asar"), "stub");
                complete = (bool)isComplete!.Invoke(null, new object[] { "workbuddy", root })!;
                Require(complete,
                    "workbuddy install root with all required files was rejected");

                // 3) 无签名约束的应用（codex CLI 形态）不拦截。
                complete = (bool)isComplete!.Invoke(null, new object[] { "codex", root })!;
                Require(complete,
                    "codex (no signature constraint) should not be blocked by install signature");

                // 4) 夸克版本子目录谓词：空根 → 不完整；版本子目录含 icudtl.dat → 完整。
                var quarkRoot = System.IO.Path.Combine(directory, "QuarkCloudDrive");
                Directory.CreateDirectory(quarkRoot);
                complete = (bool)isComplete!.Invoke(null, new object[] { "quarknetdisk", quarkRoot })!;
                Require(!complete,
                    "quarknetdisk install root without versioned payload was accepted as complete");

                var versioned = System.IO.Path.Combine(quarkRoot, "9.9.9.999");
                Directory.CreateDirectory(versioned);
                System.IO.File.WriteAllText(System.IO.Path.Combine(versioned, "icudtl.dat"), "stub");
                complete = (bool)isComplete!.Invoke(null, new object[] { "quarknetdisk", quarkRoot })!;
                Require(complete,
                    "quarknetdisk install root with a versioned icudtl.dat payload was rejected");

                // 5) 可选组语义:素言中英文 exe 任一存在即满足;两组都缺 → 不完整。
                var suyanRoot = System.IO.Path.Combine(directory, "win-unpacked");
                Directory.CreateDirectory(suyanRoot);
                var suyanResources = System.IO.Path.Combine(suyanRoot, "resources");
                Directory.CreateDirectory(suyanResources);
                System.IO.File.WriteAllText(System.IO.Path.Combine(suyanResources, "app.asar"), "stub");
                System.IO.File.WriteAllText(System.IO.Path.Combine(suyanRoot, "icudtl.dat"), "stub");
                complete = (bool)isComplete!.Invoke(null, new object[] { "suyan", suyanRoot })!;
                Require(!complete,
                    "suyan install root without either launcher exe was accepted as complete");
                System.IO.File.WriteAllText(System.IO.Path.Combine(suyanRoot, "suyan.exe"), "stub");
                complete = (bool)isComplete!.Invoke(null, new object[] { "suyan", suyanRoot })!;
                Require(complete,
                    "suyan install root with the alternative launcher exe was rejected");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
            await Task.CompletedTask;
        });
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
