using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TapRelay;

internal static partial class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--child") return await RunChild(args[1]);
        // 无头布局快照：只渲染 MainForm 到 PNG，不跑测试（见 UiSnapshot.cs）。
        if (args.Length >= 2 && args[0] == "--ui-snapshot")
        {
            return UiSnapshot.Run(args[1], args.Length >= 3 ? args[2] : null);
        }
        // 托盘退出路径探针：在进程内真正调用 ExitFromTrayAsync（见 ExitProbe.cs）。
        if (args.Length >= 1 && args[0] == "--exit-probe")
        {
            return ExitProbe.Run();
        }

        await Test("CLI drains large stdout and stderr before exit", async () =>
        {
            var proxy = CreateProxy();
            try
            {
                var result = await proxy.SendAsync("large", "large", "", CancellationToken.None);
                Require(result.Accepted && result.Status == "已完成", "Large-output child failed to complete: " + result.Status);
            }
            finally { await proxy.StopAsync("large", CancellationToken.None); }
        });
        await Test("CLI retains bounded error tail and real exit code", async () =>
        {
            var proxy = CreateProxy();
            try
            {
                var result = await proxy.SendAsync("failure", "failure", "", CancellationToken.None);
                Require(!result.Accepted && result.Code == "COMMAND_FAILED", "Expected command failure");
                Require(result.Status.Contains("exit 7") && result.Detail.Contains("ERROR_END"), "Exit detail was lost");
                Require(result.Detail.Length <= 401, "Diagnostic was unbounded");
            }
            finally { await proxy.StopAsync("failure", CancellationToken.None); }
        });
        await Test("CLI keeps draining after the five-second startup response", async () =>
        {
            var proxy = CreateProxy();
            try
            {
                var result = await proxy.SendAsync("background", "background", "", CancellationToken.None);
                Require(result.Accepted && result.Status == "已启动", "Expected background startup");
                var pid = int.Parse(result.Detail.Substring(4));
                await Until(() => HasExited(pid), "Background output blocked the child", 5000);
            }
            finally { await proxy.StopAsync("background", CancellationToken.None); }
        });
        await Test("CLI replacement during startup preserves the latest stop handle", async () =>
        {
            var started = new ConcurrentQueue<int>();
            var proxy = CreateProxy(line =>
            {
                if (line.Contains("agent.send started")) started.Enqueue(int.Parse(line.Split("pid=").Last()));
            });
            try
            {
                var first = proxy.SendAsync("same", "wait", "", CancellationToken.None);
                await Until(() => started.Count == 1, "First child did not start");
                var second = proxy.SendAsync("SAME", "wait", "", CancellationToken.None);
                await Until(() => started.Count == 2, "Replacement did not start");
                var firstResult = await first;
                Require(!firstResult.Accepted && firstResult.Code == "INTERRUPTED", "Old task must be interrupted");
                Require(HasExited(started.ToArray()[0]), "Old child still running after replacement");
                var stop = await proxy.StopAsync("same", CancellationToken.None);
                Require(stop.Accepted && stop.Detail == "pid=" + started.ToArray()[1], "Lost latest process handle");
                Require((await second).Code == "INTERRUPTED", "Latest stopped child reported completion");
            }
            finally { await proxy.StopAsync("same", CancellationToken.None); }
        });
        await Test("CLI cancellation before start creates no process", async () =>
        {
            var started = false;
            var proxy = CreateProxy(line => { if (line.Contains("agent.send started")) started = true; });
            var result = await proxy.SendAsync("cancelled", "wait", "", new CancellationToken(true));
            Require(!result.Accepted && result.Code == "CANCELLED" && !started, "Cancelled request launched a child");
        });

        await TestWebSockets();
        await TestLogsAndMonitor();
        await TestFreebuffApiProxy();
        await TestPcOptimizations();
        Console.WriteLine($"Reliability: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<int> RunChild(string mode)
    {
        if (mode == "wait") { await Task.Delay(60000); return 0; }
        if (mode == "background") await Task.Delay(5600);
        var output = new string('x', 1024 * 1024);
        await Task.WhenAll(Console.Out.WriteAsync(output), Console.Error.WriteAsync(output));
        if (mode == "failure") { Console.Error.WriteLine("ERROR_END"); return 7; }
        return 0;
    }

    private static ShellAgentApiProxy CreateProxy(Action<string>? log = null)
    {
        var binary = Process.GetCurrentProcess().MainModule!.FileName;
        var prefix = string.Equals(Path.GetFileNameWithoutExtension(binary), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + typeof(Program).Assembly.Location + "\" " : "";
        return new ShellAgentApiProxy(new ShellAgentConfig
        {
            AgentKey = "reliability-probe", DisplayName = "测试子进程",
            BinaryCandidates = new[] { binary },
            BuildCommand = (_, mode, cwd) => prefix + "--child " + mode
        }, log ?? (_ => { }));
    }

    private static bool HasExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task Until(Func<bool> predicate, string failure, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds >= timeoutMs) throw new Exception(failure);
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Test(string name, Func<Task> assertion)
    {
        try { await assertion(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
}
