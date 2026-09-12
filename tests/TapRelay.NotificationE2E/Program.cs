using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: TapRelay.NotificationE2E <zcode-cli-log-dir> <zcode-desktop-log-dir>");
            return 2;
        }

        var cliDirectory = Path.GetFullPath(args[0]);
        var desktopDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(cliDirectory);
        Directory.CreateDirectory(desktopDirectory);

        var runId = Guid.NewGuid().ToString("N");
        var sessionId = "sess_taprelay_e2e_" + runId;
        var completedTurnId = "turn_completed_" + runId;
        var secondCompletedTurnId = "turn_completed_second_" + runId;
        var failedTurnId = "turn_failed_" + runId;
        var cancelledTurnId = "turn_cancelled_" + runId;
        var falsePositiveTaskId = "repo_wiki_" + runId;
        var cliPath = Path.Combine(cliDirectory, "taprelay-e2e-" + runId + ".jsonl");
        var desktopPath = Path.Combine(desktopDirectory, "taprelay-e2e-" + runId + ".log");

        using var socket = new ClientWebSocket();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:1122"), connectTimeout.Token);
        Console.WriteLine("PASS: WebSocket connected");

        try
        {
            // Create empty files first so the monitor establishes position 0, then append events.
            File.WriteAllText(cliPath, string.Empty, new UTF8Encoding(false));
            File.WriteAllText(desktopPath, string.Empty, new UTF8Encoding(false));
            await Task.Delay(1200);

            var timestamp = DateTimeOffset.UtcNow;
            AppendLine(cliPath, CreateCliEvent("turn.completed", "completed", sessionId, completedTurnId, timestamp, "W:\\TapRelay"));
            AppendLine(cliPath, CreateCliEvent("turn.completed", "completed", sessionId, secondCompletedTurnId, timestamp.AddMilliseconds(1), "W:\\TapRelay"));
            AppendLine(cliPath, CreateCliEvent("turn.failed", "failed", sessionId, failedTurnId, timestamp.AddMilliseconds(2), "W:\\TapRelay"));
            AppendLine(cliPath, CreateCliEvent("turn.failed", "cancelled", sessionId, cancelledTurnId, timestamp.AddMilliseconds(3), "W:\\TapRelay"));
            // Duplicate the exact completed event. Monitor and server dedupe must keep one delivery.
            AppendLine(cliPath, CreateCliEvent("turn.completed", "completed", sessionId, completedTurnId, timestamp, "W:\\TapRelay"));
            AppendLine(desktopPath, "[host] [repoWiki] Wiki task_complete refresh skipped {\"workspaceKey\":\"W:\\\\TapRelay\",\"taskId\":\"" + falsePositiveTaskId + "\"}");

            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [completedTurnId] = "completed",
                [secondCompletedTurnId] = "completed",
                [failedTurnId] = "failed"
            };
            var received = new Dictionary<string, int>(StringComparer.Ordinal);
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));

            while (!receiveTimeout.IsCancellationRequested && received.Count < expected.Count)
            {
                var payload = await ReceiveMessageAsync(socket, receiveTimeout.Token);
                if (payload == null || payload.Source != "zcode")
                {
                    continue;
                }

                if (expected.TryGetValue(payload.TaskId, out var expectedStatus))
                {
                    if (!string.Equals(payload.Status, expectedStatus, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Task {payload.TaskId} status {payload.Status}, expected {expectedStatus}.");
                    }

                    received[payload.TaskId] = received.TryGetValue(payload.TaskId, out var count) ? count + 1 : 1;
                    Console.WriteLine($"PASS: received {payload.Status} taskId={payload.TaskId}");
                }
                else if (payload.TaskId == cancelledTurnId || payload.TaskId == falsePositiveTaskId)
                {
                    throw new InvalidOperationException("Unexpected notification for ignored event: " + payload.TaskId);
                }
            }

            foreach (var item in expected)
            {
                if (!received.TryGetValue(item.Key, out var count) || count != 1)
                {
                    throw new InvalidOperationException($"Task {item.Key} delivery count was {count}; expected 1.");
                }
            }

            // Observe a short quiet window to catch delayed duplicate/cancel/repoWiki false positives.
            using var quietTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!quietTimeout.IsCancellationRequested)
            {
                try
                {
                    var payload = await ReceiveMessageAsync(socket, quietTimeout.Token);
                    if (payload == null || payload.Source != "zcode")
                    {
                        continue;
                    }

                    if (payload.TaskId == cancelledTurnId || payload.TaskId == falsePositiveTaskId ||
                        (received.TryGetValue(payload.TaskId, out var count) && count >= 1))
                    {
                        throw new InvalidOperationException("Unexpected duplicate/ignored notification: " + payload.TaskId);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Console.WriteLine("PASS: no duplicate, cancelled, or repoWiki false-positive notification");
            Console.WriteLine("ZCode notification E2E: 3 expected deliveries, 0 unexpected deliveries.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("FAIL: timed out waiting for expected notification payloads.");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
        finally
        {
            TryDelete(cliPath);
            TryDelete(desktopPath);
            if (socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeTimeout.Token);
                }
                catch
                {
                }
            }
        }
    }

    private static string CreateCliEvent(
        string eventName,
        string status,
        string sessionId,
        string turnId,
        DateTimeOffset timestamp,
        string cwd)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            level = status == "failed" ? "error" : "info",
            @event = eventName,
            module = "taprelay.e2e",
            message = "TapRelay E2E",
            sessionId,
            turnId,
            status,
            context = new { workspacePath = cwd }
        });
    }

    private static void AppendLine(string path, string line)
    {
        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
    }

    private static async Task<Payload?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket closed during test.");
            }
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "message")
        {
            return null;
        }

        return new Payload
        {
            Source = ReadString(root, "source"),
            Status = ReadString(root, "status"),
            TaskId = ReadString(root, "taskId")
        };
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class Payload
    {
        internal string Source { get; set; } = string.Empty;
        internal string Status { get; set; } = string.Empty;
        internal string TaskId { get; set; } = string.Empty;
    }
}
