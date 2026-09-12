using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TapRelay;

internal static partial class Program
{
    private static async Task TestPcOptimizations()
    {
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
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var sendBody = new StringContent(
                    "{\"message\":\"" + new string('x', 512 * 1024) + "\"}",
                    Encoding.UTF8,
                    "application/json");
                using var sendResponse = await client.PostAsync(
                    "http://127.0.0.1:" + port + "/send",
                    sendBody);
                Require((int)sendResponse.StatusCode == 413, "Oversized /send body was accepted");

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
