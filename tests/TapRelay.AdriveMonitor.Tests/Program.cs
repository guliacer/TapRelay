using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TapRelay;

internal static class Program
{
    private static readonly List<string> Failures = new List<string>();
    private static int _passed;

    private static int Main()
    {
        Run("download status transitions emit one batch completion", TestDownloadTransitions);
        Run("upload batch waits for every task and summarizes failures", TestUploadFailureAndPause);
        Run("cancelled task removal emits no false success", TestCancelledRemoval);
        Run("current IndexedDB schema maps direction and taskStatus", TestCurrentSchema);
        Run("download log completion and failure emit once", TestDownloadLogEvents);
        Run("log appended during read is preserved for the next read", TestLogGrowthDuringRead);
        Run("aDrive status enum mapping is conservative", TestStatusMapping);
        Run("project name uses the transfer folder", TestProjectNameUsesFolder);
        Run("captured Chromium IndexedDB WAL records are readable", TestCapturedWal);
        Run("compacted LevelDB SSTable records are readable", TestCompactedTable);
        Run("transfer changes do not notify while aDrive is not running", TestNoNotificationWhenStopped);

        Console.WriteLine("aDrive monitor tests: " + _passed + " passed, " + Failures.Count + " failed");
        foreach (var failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static bool TestDownloadTransitions()
    {
        using var fixture = new Fixture("download");
        fixture.AppendPut(1, 1, "download-1", "PENDING", 1700000000000L, 0, "download.bin");
        using var monitor = fixture.StartMonitor();

        fixture.AppendPut(2, 1, "download-1", "IN_PROGRESS", 1700000000000L, 0, "download.bin");
        Thread.Sleep(1000);
        if (fixture.Events.Count != 0) return false;
        fixture.AppendPut(3, 1, "download-1", "SUCCESSED", 1700000000000L, 1700000000250L, "download.bin");
        fixture.WaitForEvents(1);
        Thread.Sleep(500);

        var events = fixture.Events.ToArray();
        return events.Length == 1 &&
               events[0].Source == "adrive" && events[0].Status == "done" &&
               events[0].Message.Contains("完成 1 个", StringComparison.Ordinal) &&
               events[0].DurationMs == 250;
    }

    private static bool TestUploadFailureAndPause()
    {
        using var fixture = new Fixture("upload");
        using var monitor = fixture.StartMonitor();

        fixture.AppendPut(1, 2, "upload-1", "PENDING", 1700000002000L, 0, "upload.bin");
        fixture.AppendPut(2, 2, "upload-2", "PENDING", 1700000002500L, 0, "second.bin");
        fixture.AppendPut(2, 2, "upload-1", "SUSPENDED", 1700000002000L, 0, "upload.bin");
        Thread.Sleep(1000);
        if (fixture.Events.Count != 0) return false;
        fixture.AppendPut(3, 2, "upload-1", "SUCCESSED", 1700000002000L, 1700000003400L, "upload.bin");
        fixture.AppendPut(4, 2, "upload-2", "FAILED", 1700000002500L, 1700000003900L, "failed.bin");
        fixture.WaitForEvents(1);

        var events = fixture.Events.ToArray();
        return events.Length == 1 &&
               events[0].Source == "adrive" && events[0].Status == "done" &&
               events[0].Message.Contains("完成 1 个", StringComparison.Ordinal) &&
               events[0].Message.Contains("失败 1 个", StringComparison.Ordinal);
    }

    private static bool TestCancelledRemoval()
    {
        using var fixture = new Fixture("cancelled");
        fixture.AppendPut(1, 1, "cancelled-1", "PENDING", 1700000004000L, 0, "cancelled.bin");
        using var monitor = fixture.StartMonitor();
        fixture.AppendDelete(2, 1, "cancelled-1");
        Thread.Sleep(4000);
        return fixture.Events.Count == 0;
    }

    private static bool TestCurrentSchema()
    {
        using var fixture = new Fixture("current-schema");
        fixture.AppendPutNew(1, 2, "key-upload", "IN_PROGRESS", 1700000005000L, 0, "current-upload.bin", "taskStatus", "value-upload", true, @"D:\Cloud\FolderA\current-upload.bin");
        var tasks = fixture.ReadTasks();
        var task = tasks.Values.SingleOrDefault();
        return tasks.Count == 1 && task != null &&
               task.Direction == AdriveTransferDirection.Upload &&
               task.TaskId == "value-upload" && task.Status == "IN_PROGRESS" &&
               task.Name == "current-upload.bin" &&
               task.Path == @"D:\Cloud\FolderA\current-upload.bin" &&
               !string.IsNullOrWhiteSpace(task.Identity);
    }

    private static bool TestDownloadLogEvents()
    {
        using var fixture = new Fixture("download-log");
        using var monitor = fixture.StartMonitor();

        fixture.AppendLog("[2026-08-04T00:22:24.427Z][transferPreload][info][Downloader] #1 create {\"path\":\"D:\\\\Downloads\\\\map.jpg.part\",\"fileSize\":22054149}");
        fixture.AppendLog("[2026-08-04T00:22:24.927Z][transferPreload][info][Downloader] #2 create {\"path\":\"D:\\\\Downloads\\\\map-2.jpg.part\",\"fileSize\":22054149}");
        fixture.AppendLog("[2026-08-04T00:22:26.427Z][transferPreload][info][Downloader] #1 complete {\"loaded\":22054149}");
        fixture.AppendLog("[2026-08-04T00:22:27.427Z][transferPreload][info][Downloader] #2 complete {\"loaded\":22054149}");
        fixture.AppendLog("[2026-08-04T00:22:28.427Z][transferWindow][info][RendererDownload] dump download {\"taskList\":0,\"groupList\":0,\"bufferList\":0}");
        monitor.PollNow();
        fixture.WaitForEvents(1);

        fixture.AppendLog("[2026-08-04T00:22:30.427Z][transferPreload][info][Downloader] #3 create {\"path\":\"D:\\\\Downloads\\\\failed.zip.part\"}");
        fixture.AppendLog("[2026-08-04T00:22:31.427Z][transferPreload][info][Downloader] #3 error {\"message\":\"network\"}");
        fixture.AppendLog("[2026-08-04T00:22:32.427Z][transferPreload][info][Downloader] #4 create {\"path\":\"D:\\\\Downloads\\\\cancelled.zip.part\"}");
        fixture.AppendLog("[2026-08-04T00:22:33.427Z][transferPreload][info][Downloader] #4 cancel {}");
        fixture.AppendLog("[2026-08-04T00:22:34.427Z][transferWindow][info][RendererDownload] dump download {\"taskList\":0,\"groupList\":0,\"bufferList\":0}");
        monitor.PollNow();
        fixture.WaitForEvents(2);

        fixture.AppendLog("[2026-08-04T00:22:26.427Z][transferPreload][info][Downloader] #1 complete {\"loaded\":22054149}");
        monitor.PollNow();

        var events = fixture.Events.ToArray();
        var completed = events.SingleOrDefault(item => item.Status == "done");
        var failed = events.SingleOrDefault(item => item.Status == "failed");
        return events.Length == 2 && completed != null && failed != null &&
               completed.Source == "adrive" && completed.Message.Contains("完成 2 个", StringComparison.Ordinal) &&
               failed.Message.Contains("完成 0 个", StringComparison.Ordinal) &&
               failed.Message.Contains("失败 1 个", StringComparison.Ordinal);
    }

    private static bool TestLogGrowthDuringRead()
    {
        var flags = System.Reflection.BindingFlags.NonPublic;
        var stateType = typeof(AdriveTransferMonitor).GetNestedType("LogState", flags)!;
        var state = Activator.CreateInstance(stateType, flags | System.Reflection.BindingFlags.Instance,
            null, new object[] { "memory-probe" }, null)!;
        var reader = typeof(AdriveTransferMonitor).GetMethods(flags | System.Reflection.BindingFlags.Static)
            .Single(method => method.Name == "TryReadAppendedLogLines" && method.GetParameters().Length == 3);
        using var stream = new GrowingLogStream();
        var firstArgs = new object[] { state, stream, null! };
        if (!(bool)reader.Invoke(null, firstArgs)!) return false;
        var secondArgs = new object[] { state, stream, null! };
        if (!(bool)reader.Invoke(null, secondArgs)!) return false;
        return ((List<string>)firstArgs[2]).SequenceEqual(new[] { "first" }) &&
               ((List<string>)secondArgs[2]).SequenceEqual(new[] { "second" });
    }

    private sealed class GrowingLogStream : MemoryStream
    {
        private bool _appended;
        internal GrowingLogStream()
        {
            var bytes = Encoding.UTF8.GetBytes("first\n");
            Write(bytes, 0, bytes.Length);
            Position = 0;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            if (!_appended)
            {
                _appended = true;
                var position = Position;
                Position = Length;
                var bytes = Encoding.UTF8.GetBytes("second\n");
                Write(bytes, 0, bytes.Length);
                Position = position;
            }
            return read;
        }
    }

    private static bool TestStatusMapping()
    {
        return AdriveStatus.Map("PREPARING") == "pending" &&
               AdriveStatus.Map("QUEUED") == "pending" &&
               AdriveStatus.Map("IN_PROGRESS") == "in_progress" &&
               AdriveStatus.Map("VERIFYING") == "in_progress" &&
               AdriveStatus.Map("RUNNING") == "in_progress" &&
               AdriveStatus.Map("SUSPENDED") == "paused" &&
               AdriveStatus.Map("PAUSED") == "paused" &&
               AdriveStatus.Map("SUCCESSED") == "done" &&
               AdriveStatus.Map("COMPLETED") == "done" &&
               AdriveStatus.Map("FAILED") == "failed" &&
               AdriveStatus.Map("ERROR") == "failed" &&
               AdriveStatus.Map("CANCELLED") == "cancelled" &&
               AdriveStatus.Map("CANCELED") == "cancelled";
    }

    private static bool TestProjectNameUsesFolder()
    {
        var task = new AdriveTaskRecord
        {
            Direction = AdriveTransferDirection.Download,
            TaskId = "folder-task",
            Name = "archive.zip",
            Path = @"D:\Cloud\FolderA\archive.zip"
        };
        var completion = AdriveTaskEvent.FromBatch(
            AdriveTransferDirection.Download,
            1,
            0,
            TimeSpan.FromSeconds(1),
            "batch-folder",
            new[] { task });
        return completion.ProjectName == "FolderA";
    }

    private static bool TestCapturedWal()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "real-indexeddb-wal-6.0.0.log");
        if (!File.Exists(source)) return false;
        var root = Path.Combine(Path.GetTempPath(), "taprelay-adrive-captured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(source, Path.Combine(root, "000003.log"));
            var tasks = AdriveTransferDatabaseReader.Read(root);
            var download = tasks.Values.SingleOrDefault(item => item.TaskId == "download-1");
            var upload = tasks.Values.SingleOrDefault(item => item.TaskId == "upload-1");
            return tasks.Count == 2 && download != null && upload != null &&
                   download.Direction == AdriveTransferDirection.Download && download.Status == "SUCCESSED" &&
                   upload.Direction == AdriveTransferDirection.Upload && upload.Status == "FAILED" &&
                   download.DurationMs == 5000 && upload.DurationMs == 5000;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool TestCompactedTable()
    {
        var root = Path.Combine(Path.GetTempPath(), "taprelay-adrive-ldb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var key = Fixture.BuildTaskKey(1, "compacted-download");
            var oldValue = Fixture.BuildTaskValue("compacted-download", "IN_PROGRESS", 1700000010000L, 0, "compacted.bin");
            var newValue = Fixture.BuildTaskValue("compacted-download", "SUCCESSED", 1700000010000L, 1700000011500L, "compacted.bin");
            File.WriteAllBytes(Path.Combine(root, "000005.ldb"), BuildLdbTable(key, oldValue, 10, false));
            File.WriteAllBytes(Path.Combine(root, "000006.ldb"), BuildLdbTable(key, newValue, 11, true));

            var tasks = AdriveTransferDatabaseReader.Read(root);
            var task = tasks.Values.SingleOrDefault(item => item.TaskId == "compacted-download");
            return tasks.Count == 1 && task != null && task.Status == "SUCCESSED" && task.DurationMs == 1500;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool TestNoNotificationWhenStopped()
    {
        using var fixture = new Fixture("stopped");
        fixture.ProcessRunning = false;
        using var monitor = fixture.StartMonitor();
        fixture.AppendRawLog(
            "[2026-08-05T01:00:00.000Z][Downloader] #1 create C:\\Cloud\\stopped.zip\n" +
            "[2026-08-05T01:00:01.000Z][Downloader] #1 complete C:\\Cloud\\stopped.zip\n" +
            "[2026-08-05T01:00:02.000Z][RendererDownload] complete {\"taskList\":0}\n");
        Thread.Sleep(2500);
        if (fixture.Events.Count != 0) return false;

        fixture.ProcessRunning = true;
        monitor.Refresh();
        Thread.Sleep(1000);
        return fixture.Events.Count == 0;
    }

    private static byte[] BuildLdbTable(byte[] userKey, byte[] value, ulong sequence, bool compressed)
    {
        var internalKey = new byte[userKey.Length + 8];
        Buffer.BlockCopy(userKey, 0, internalKey, 0, userKey.Length);
        var tag = (sequence << 8) | 1UL;
        for (var index = 0; index < 8; index++) internalKey[userKey.Length + index] = (byte)(tag >> (index * 8));

        var dataBlock = BuildBlock(internalKey, value);
        var storedData = compressed ? BuildSnappyLiteral(dataBlock) : dataBlock;
        var dataWithTrailer = AppendBlockTrailer(storedData, compressed ? (byte)1 : (byte)0);

        var handle = new MemoryStream();
        WriteVarint64(handle, 0);
        WriteVarint64(handle, (ulong)storedData.Length);
        var indexBlock = BuildBlock(Encoding.UTF8.GetBytes("index"), handle.ToArray());
        var storedIndex = compressed ? BuildSnappyLiteral(indexBlock) : indexBlock;
        var indexWithTrailer = AppendBlockTrailer(storedIndex, compressed ? (byte)1 : (byte)0);

        var footer = new byte[48];
        var footerOffset = 20;
        var footerHandle = new MemoryStream();
        WriteVarint64(footerHandle, (ulong)dataWithTrailer.Length);
        WriteVarint64(footerHandle, (ulong)storedIndex.Length);
        var encodedHandle = footerHandle.ToArray();
        Buffer.BlockCopy(encodedHandle, 0, footer, footerOffset, encodedHandle.Length);
        var magic = 0xdb4775248b80fb57UL;
        for (var index = 0; index < 8; index++) footer[40 + index] = (byte)(magic >> (index * 8));
        return Combine(dataWithTrailer, indexWithTrailer, footer);
    }

    private static byte[] BuildBlock(byte[] key, byte[] value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, 0);
        WriteVarint(stream, key.Length);
        WriteVarint(stream, value.Length);
        stream.Write(key, 0, key.Length);
        stream.Write(value, 0, value.Length);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 1);
        return stream.ToArray();
    }

    private static byte[] BuildSnappyLiteral(byte[] input)
    {
        using var stream = new MemoryStream();
        WriteVarint64(stream, (ulong)input.Length);
        if (input.Length < 60)
        {
            stream.WriteByte((byte)((input.Length - 1) << 2));
        }
        else
        {
            var encodedLength = input.Length - 1;
            var lengthBytes = encodedLength <= byte.MaxValue ? 1 : encodedLength <= ushort.MaxValue ? 2 : 4;
            stream.WriteByte((byte)((59 + lengthBytes) << 2));
            for (var index = 0; index < lengthBytes; index++) stream.WriteByte((byte)(encodedLength >> (index * 8)));
        }
        stream.Write(input, 0, input.Length);
        return stream.ToArray();
    }

    private static byte[] AppendBlockTrailer(byte[] data, byte compression)
    {
        return Combine(data, new[] { compression, (byte)0, (byte)0, (byte)0, (byte)0 });
    }

    private static byte[] Combine(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part, 0, part.Length);
        return stream.ToArray();
    }

    private static void WriteVarint64(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void WriteVarint(Stream stream, int value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void Run(string name, Func<bool> test)
    {
        try
        {
            if (!test()) throw new InvalidOperationException("assertion returned false");
            _passed++;
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception error)
        {
            Failures.Add(name + " - " + error.Message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _logPath;
        private readonly string _mainLogPath;
        private readonly object _gate = new object();
        internal List<AdriveTaskEvent> Events { get; } = new List<AdriveTaskEvent>();
        internal bool ProcessRunning { get; set; } = true;

        internal Fixture(string name)
        {
            _root = Path.Combine(Path.GetTempPath(), "taprelay-adrive-tests", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _logPath = Path.Combine(_root, "000003.log");
            File.WriteAllBytes(_logPath, Array.Empty<byte>());
            _mainLogPath = Path.Combine(_root, "main.log");
            File.WriteAllBytes(_mainLogPath, Array.Empty<byte>());
            Environment.SetEnvironmentVariable("TAPRELAY_ADRIVE_DB", _root);
            Environment.SetEnvironmentVariable("TAPRELAY_ADRIVE_LOG", _mainLogPath);
        }

        internal AdriveTransferMonitor StartMonitor()
        {
            var monitor = new AdriveTransferMonitor(
                item => { lock (_gate) Events.Add(item); },
                _ => { },
                () => ProcessRunning);
            monitor.Start();
            return monitor;
        }

        internal void AppendRawLog(string text)
        {
            File.AppendAllText(_mainLogPath, text, Encoding.UTF8);
        }

        internal void AppendPut(ulong sequence, int storeId, string taskId, string status, long create, long end, string name)
        {
            var key = BuildTaskKey(storeId, taskId);
            var value = BuildTaskValue(taskId, status, create, end, name);
            AppendBatch(sequence, new Operation(1, key, value));
        }

        internal void AppendPutNew(ulong sequence, int storeId, string keyTaskId, string status, long create, long end, string name, string statusField, string valueTaskId, bool utf16Name = false, string path = "")
        {
            var key = BuildTaskKeyNew(storeId, keyTaskId);
            var value = BuildTaskValue(valueTaskId, status, create, end, name, statusField, utf16Name, path);
            AppendBatch(sequence, new Operation(1, key, value));
        }

        internal Dictionary<string, AdriveTaskRecord> ReadTasks()
        {
            return AdriveTransferDatabaseReader.Read(_root);
        }

        internal void AppendLog(string line)
        {
            File.AppendAllText(_mainLogPath, line + Environment.NewLine, new UTF8Encoding(false));
        }

        internal void AppendDelete(ulong sequence, int storeId, string taskId)
        {
            AppendBatch(sequence, new Operation(0, BuildTaskKey(storeId, taskId), Array.Empty<byte>()));
        }

        private void AppendBatch(ulong sequence, params Operation[] operations)
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var batch = new MemoryStream();
            WriteUInt64(batch, sequence);
            WriteUInt32(batch, (uint)operations.Length);
            foreach (var operation in operations)
            {
                batch.WriteByte(operation.Type);
                WriteVarint(batch, operation.Key.Length);
                batch.Write(operation.Key, 0, operation.Key.Length);
                if (operation.Type == 1)
                {
                    WriteVarint(batch, operation.Value.Length);
                    batch.Write(operation.Value, 0, operation.Value.Length);
                }
            }
            var payload = batch.ToArray();
            stream.Write(new byte[4], 0, 4);
            stream.WriteByte((byte)(payload.Length & 0xff));
            stream.WriteByte((byte)(payload.Length >> 8));
            stream.WriteByte(1);
            stream.Write(payload, 0, payload.Length);
            stream.Flush(true);
        }

        internal static byte[] BuildTaskKey(int storeId, string taskId)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0);
            stream.WriteByte(1);
            stream.WriteByte((byte)storeId);
            stream.WriteByte(1);
            stream.WriteByte(0x0a);
            foreach (var character in taskId) { stream.WriteByte((byte)(character >> 8)); stream.WriteByte((byte)character); }
            return stream.ToArray();
        }

        internal static byte[] BuildTaskKeyNew(int storeId, string taskId)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0);
            stream.WriteByte((byte)storeId);
            stream.WriteByte(1);
            stream.WriteByte(1);
            stream.WriteByte(0x0a);
            foreach (var character in taskId) { stream.WriteByte((byte)(character >> 8)); stream.WriteByte((byte)character); }
            return stream.ToArray();
        }

        internal static byte[] BuildTaskValue(string taskId, string status, long create, long end, string name, string statusField = "status", bool utf16Name = false, string path = "")
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x6f);
            WriteStringProperty(stream, "taskId", taskId);
            WriteStringProperty(stream, statusField, status);
            WriteStringProperty(stream, "type", "file");
            if (utf16Name) WriteUtf16Property(stream, "fileName", name);
            else WriteStringProperty(stream, "fileName", name);
            if (!string.IsNullOrWhiteSpace(path)) WriteStringProperty(stream, "filePath", path);
            WriteDoubleProperty(stream, "createTime", create);
            WriteDoubleProperty(stream, "endTime", end);
            stream.WriteByte(0x7b);
            return stream.ToArray();
        }

        private static void WriteStringProperty(Stream stream, string key, string value)
        {
            stream.WriteByte(0x22); WriteVarint(stream, key.Length); WriteUtf8(stream, key);
            stream.WriteByte(0x22); WriteVarint(stream, value.Length); WriteUtf8(stream, value);
        }

        private static void WriteDoubleProperty(Stream stream, string key, double value)
        {
            stream.WriteByte(0x22); WriteVarint(stream, key.Length); WriteUtf8(stream, key);
            stream.WriteByte(0x4e); var bytes = BitConverter.GetBytes(value); stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUtf16Property(Stream stream, string key, string value)
        {
            stream.WriteByte(0x22); WriteVarint(stream, key.Length); WriteUtf8(stream, key);
            stream.WriteByte(0x63);
            var bytes = Encoding.Unicode.GetBytes(value);
            WriteVarint(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteVarint(Stream stream, int value)
        {
            while (value >= 0x80) { stream.WriteByte((byte)(value | 0x80)); value >>= 7; }
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 24));
        }

        private static void WriteUInt64(Stream stream, ulong value)
        {
            for (var i = 0; i < 8; i++) stream.WriteByte((byte)(value >> (i * 8)));
        }

        internal void WaitForEvents(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate) if (Events.Count >= count) return;
                Thread.Sleep(100);
            }
            throw new TimeoutException("aDrive status events were not emitted");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TAPRELAY_ADRIVE_DB", null);
            Environment.SetEnvironmentVariable("TAPRELAY_ADRIVE_LOG", null);
            try { Directory.Delete(_root, true); } catch { }
        }

        private readonly struct Operation
        {
            internal Operation(byte type, byte[] key, byte[] value) { Type = type; Key = key; Value = value; }
            internal byte Type { get; }
            internal byte[] Key { get; }
            internal byte[] Value { get; }
        }
    }
}
