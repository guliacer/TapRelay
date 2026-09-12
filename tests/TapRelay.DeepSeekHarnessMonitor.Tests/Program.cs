using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TapRelay;
using ZstdSharp;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        // --- status mapping ---
        Test("turn/end completed maps to completed", () =>
        {
            var file = BuildSessionFile(
                delegationDepth: 0,
                completed: true,
                reasonJson: @"{""kind"":""completed""}");
            return ScanOnce(file, out var items) &&
                   items.Count == 1 && items[0].Status == "completed" &&
                   items[0].Cwd == "W:\\TapRelay" &&
                   items[0].TaskId.StartsWith("deepseekharness-", StringComparison.Ordinal) &&
                   items[0].DurationMs >= 0;
        });

        Test("turn/end error maps to failed with message", () =>
        {
            var file = BuildSessionFile(
                delegationDepth: 0,
                completed: true,
                reasonJson: @"{""kind"":""error"",""error"":{""code"":""API_ERROR"",""message"":""quota exceeded""}}");
            return ScanOnce(file, out var items) &&
                   items.Count == 1 && items[0].Status == "failed" &&
                   items[0].Message.IndexOf("quota exceeded", StringComparison.OrdinalIgnoreCase) >= 0;
        });

        Test("turn/end blocked maps to waiting", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""blocked""}");
            return ScanOnce(file, out var items) &&
                   items.Count == 1 && items[0].Status == "waiting";
        });

        Test("turn/end aborted by user is cancelled, not failed", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""aborted"",""reason"":{""kind"":""user""}}");
            return ScanOnce(file, out var items) && items.Count == 0;
        });

        Test("turn/end aborted by parent is ignored", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""aborted"",""reason"":{""kind"":""parent""}}");
            return ScanOnce(file, out var items) && items.Count == 0;
        });

        Test("turn/end max-tokens is ignored", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""max-tokens""}");
            return ScanOnce(file, out var items) && items.Count == 0;
        });

        Test("turn/end interrupted is ignored", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""interrupted""}");
            return ScanOnce(file, out var items) && items.Count == 0;
        });

        // --- subagent exclusion ---
        Test("subagent (delegationDepth>0) completed is not reported", () =>
        {
            var file = BuildSessionFile(delegationDepth: 2, completed: true, @"{""kind"":""completed""}");
            return ScanOnce(file, out var items) && items.Count == 0;
        });

        // --- idempotency: prime then appended turn/end ---
        Test("prime does not emit; appended turn/end emits once", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                // Header + an already-completed turn/end (history).
                WriteFrames(path, HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(1, 1000L),
                    TurnEndFrame(1, 2000L, @"{""kind"":""completed""}"));
                var state = DeepSeekHarnessReader.Prime(path, out var primed);
                if (primed.Count != 0) return false;

                // Append a new turn/end.
                AppendFrame(path, TurnStartFrame(2, 3000L));
                AppendFrame(path, TurnEndFrame(2, 4000L, @"{""kind"":""completed""}"));
                if (!DeepSeekHarnessReader.TryScan(path, state, out var items)) return false;
                // The reader re-scans the full log and reports both the primed turn 1
                // and the new turn 2 (the monitor deduplicates by Seen/AppKey later).
                if (items.Count < 1 || !items.Any(i => i.Turn == 2)) return false;

                // Re-scan (file unchanged) must not re-emit (Emitted dedup in reader).
                if (DeepSeekHarnessReader.TryScan(path, state, out var again) && again.Count != 0) return false;
                return true;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        // --- duration from turn/start to turn/end ---
        Test("turn duration is derived from turn/start..turn/end", () =>
        {
            var file = BuildSessionFile(0, true, @"{""kind"":""completed""}", startTime: 100000L, endTime: 101500L);
            return ScanOnce(file, out var items) &&
                   items.Count == 1 && items[0].DurationMs == 1500L;
        });

        Test("adjacent frame events without newline retain the turn duration", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                // A Harness compression frame boundary is not a JSONL guarantee.
                // These objects intentionally have no newline separators.
                WriteFramesWithoutNewlines(path,
                    HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(7, 100000L),
                    TurnEndFrame(7, 101500L, @"{""kind"":""completed""}"));
                return ScanOnce(path, out var items) &&
                       items.Count == 1 && items[0].Turn == 7 &&
                       items[0].DurationMs == 1500L;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        Test("turn token usage is accumulated from assistant/chunk usage events", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                WriteFrames(path,
                    HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(5, 1000L),
                    UsageChunkFrame(5, 1, input: 100, output: 10, cacheRead: 5),
                    UsageChunkFrame(5, 2, input: 200, output: 20, cacheRead: 8),
                    TurnEndFrame(5, 2000L, @"{""kind"":""completed""}"));
                if (!ScanOnce(path, out var items)) return false;
                return items.Count == 1 && items[0].TokenCount == 100L + 10L + 5L + 200L + 20L + 8L;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        Test("blocked turn does not carry token usage", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                WriteFrames(path,
                    HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(6, 1000L),
                    UsageChunkFrame(6, 1, input: 300, output: 30, cacheRead: 9),
                    TurnEndFrame(6, 1500L, @"{""kind"":""blocked""}"));
                if (!ScanOnce(path, out var items)) return false;
                return items.Count == 1 && items[0].Status == "waiting" && items[0].TokenCount == 0L;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        Test("assistant final text is used as the project log", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                WriteFrames(path,
                    HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(3, 1000L),
                    UserMessageFrame("修复 DeepSeek Harness 通知"),
                    AssistantMessageFrame(3, "已修复通知字段并完成回归测试。"),
                    TurnEndFrame(3, 2500L, @"{""kind"":""completed""}"));
                if (!ScanOnce(path, out var items)) return false;
                return items.Count == 1 && items[0].DurationMs == 1500L &&
                       items[0].Message.IndexOf("已修复通知字段", StringComparison.Ordinal) >= 0 &&
                       items[0].ProjectLog == items[0].Message;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        Test("session title is the project-name fallback without cwd", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                WriteFrames(path,
                    HeaderFrame(0, string.Empty),
                    SessionTitleFrame("通知适配回归"),
                    TurnStartFrame(4, 1000L),
                    TurnEndFrame(4, 2000L, @"{""kind"":""completed""}"));
                if (!ScanOnce(path, out var items)) return false;
                return items.Count == 1 && items[0].ProjectName == "通知适配回归";
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        // --- torn (interrupted write) tail recovery ---
        Test("torn final frame keeps committed prefix", () =>
        {
            var root = NewTempDir();
            var path = Path.Combine(root, "session.jsonl.zstd");
            try
            {
                WriteFrames(path,
                    HeaderFrame(0, "W:\\TapRelay"),
                    TurnStartFrame(1, 1000L),
                    TurnEndFrame(1, 2000L, @"{""kind"":""completed""}"));
                // Append torn bytes — just the zstd magic (4 bytes) that start a new
                // frame but nowhere near complete. SplitFrames should recognize this
                // as a torn frame and stop before it.
                byte[] torn = { 0x28, 0xb5, 0x2f, 0xfd };
                using (var ap = new FileStream(path, FileMode.Append)) ap.Write(torn, 0, torn.Length);

                // Must not throw; committed prefix must contain the completed turn.
                var text = DeepSeekHarnessReader.DecompressCommittedPrefix(File.ReadAllBytes(path));
                return text.IndexOf("completed", StringComparison.Ordinal) >= 0 &&
                       text.IndexOf("\"type\":\"session\"", StringComparison.Ordinal) >= 0;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        // --- compressed round trip sanity ---
        Test("compress->decompress round trip preserves jsonl", () =>
        {
            var json = HeaderFrame(0, "W:\\TapRelay");
            var frame = Compress(json);
            var text = DeepSeekHarnessReader.DecompressCommittedPrefix(frame);
            return text.IndexOf("\"type\":\"session\"", StringComparison.Ordinal) >= 0;
        });

        Console.WriteLine();
        Console.WriteLine($"DeepSeek Harness monitor tests: {_passed} passed, {Failures.Count} failed");
        if (Failures.Count == 0)
        {
            return 0;
        }
        foreach (var failure in Failures) Console.WriteLine("  FAIL: " + failure);
        return 1;
    }

    private static bool ScanOnce(string path, out List<DeepSeekHarnessCompletion> items)
    {
        var state = new DeepSeekHarnessFileState();
        if (!DeepSeekHarnessReader.TryScan(path, state, out items))
        {
            items = new List<DeepSeekHarnessCompletion>();
            return false;
        }
        items = items.Where(i => i.Turn != 99).ToList();
        return true;
    }

    private static string BuildSessionFile(
        int delegationDepth,
        bool completed,
        string reasonJson,
        long? startTime = null,
        long? endTime = null,
        string turnId = "1")
    {
        var root = NewTempDir();
        var path = Path.Combine(root, "session.jsonl.zstd");
        var start = startTime ?? 10000L;
        var end = endTime ?? start + 5000L;
        try
        {
            var frames = new List<string> { HeaderFrame(delegationDepth, "W:\\TapRelay") };
            if (completed) frames.Add(TurnStartFrame(1, start));
            frames.Add(TurnStartFrame(int.Parse(turnId), start));
            if (completed) frames.Add(TurnEndFrame(int.Parse(turnId), end, reasonJson));
            WriteFrames(path, frames.ToArray());
            return path;
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static string HeaderFrame(int delegationDepth, string cwd)
    {
        // JSON-escape the cwd backslashes exactly like the real harness does.
        var escaped = cwd.Replace("\\", "\\\\");
        return "{\"type\":\"session\",\"version\":0,\"id\":\"session-test-0000000000-0000-0000-0000\",\"createdAt\":1000,\"cwd\":\"" + escaped + "\",\"delegationDepth\":" + delegationDepth + ",\"agentPreset\":\"standard\"}";
    }

    private static string TurnStartFrame(long turn, long time)
    {
        return $"{{\"type\":\"turn/start\",\"seq\":{turn * 100 + 1},\"time\":{time},\"data\":{{\"turn\":{turn}}}}}";
    }

    private static string TurnEndFrame(long turn, long time, string reasonJson)
    {
        return $"{{\"type\":\"turn/end\",\"seq\":{turn * 100 + 2},\"time\":{time},\"data\":{{\"turn\":{turn},\"reason\":{reasonJson}}}}}";
    }

    private static string UserMessageFrame(string text)
    {
        return "{\"type\":\"user/message\",\"seq\":303,\"time\":1200,\"data\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"source\":{\"kind\":\"user\"},\"role\":\"user\"}}";
    }

    private static string AssistantMessageFrame(long turn, string text)
    {
        return "{\"type\":\"assistant/message\",\"seq\":304,\"time\":2000,\"data\":{\"turn\":" + turn + ",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"reasoning\",\"text\":\"internal\"},{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";
    }

    private static string SessionTitleFrame(string title)
    {
        return "{\"type\":\"session/title\",\"seq\":20,\"time\":1001,\"data\":{\"title\":\"" + title + "\"}}";
    }

    private static string UsageChunkFrame(long turn, long step, long input, long output, long cacheRead)
    {
        return $"{{\"type\":\"assistant/chunk\",\"seq\":{step * 1000},\"time\":1000,\"data\":{{\"turn\":{turn},\"step\":{step},\"chunk\":{{\"type\":\"usage\",\"usage\":{{\"inputTokens\":{input},\"outputTokens\":{output},\"cacheReadTokens\":{cacheRead}}}}}}}}}";
    }

    private static void WriteFrames(string path, params string[] frames)
    {
        var list = new List<byte[]>();
        foreach (var frame in frames) list.Add(Compress(FrameLine(frame)));
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            foreach (var bytes in list) fs.Write(bytes, 0, bytes.Length);
        }
    }

    private static void AppendFrame(string path, string frame)
    {
        var bytes = Compress(FrameLine(frame));
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    private static void WriteFramesWithoutNewlines(string path, params string[] frames)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        foreach (var frame in frames)
        {
            var bytes = Compress(frame);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    // The real harness appends a trailing newline per event batch
    // (the JS writes `eventLines(events, packChunks) + "\n"`), so each frame
    // payload is newline-terminated exactly like production logs.
    private static string FrameLine(string frame) => frame + "\n";

    private static byte[] Compress(string value)
    {
        var input = Encoding.UTF8.GetBytes(value);
        using var ms = new MemoryStream();
        using (var cw = new CompressionStream(ms, leaveOpen: true))
        {
            cw.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-mon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Test(string name, Func<bool> assertion)
    {
        try
        {
            if (assertion())
            {
                _passed++;
                return;
            }
        }
        catch (Exception error)
        {
            Failures.Add(name + " -> " + error.ToString());
            return;
        }
        Failures.Add(name);
    }
}
