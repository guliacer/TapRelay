using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using TapRelay;

internal static class Program
{
    private static readonly List<string> Failures = new List<string>();
    private static int _passed;

    private static int Main()
    {
        Run("mixed batch emits one aggregate completion", TestMixedBatch);
        Run("all failed batch emits one aggregate failure", TestAllFailedBatch);
        Run("completed batch counts history rows once", TestCompletedBatch);
        Run("upload completed batch emits aggregate completion", TestUploadCompletedBatch);
        Run("upload all-failed batch emits aggregate failure", TestUploadFailedBatch);
        Run("database changes do not notify while Baidu is not running", TestNoNotificationWhenStopped);

        Console.WriteLine($"Baidu monitor tests: {_passed} passed, {Failures.Count} failed");
        foreach (var failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static bool TestMixedBatch()
    {
        using var fixture = new Fixture("mixed");
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((101, 1000), (102, 1001));
        Thread.Sleep(4300);
        fixture.CompleteAndFail(101, 102, 1000, 1060, 123);
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        var checks = new[] { fixture.Events.Count == 1, item.CompletedCount == 1, item.FailedCount == 1, item.Status == "completed", item.Duration == TimeSpan.FromSeconds(60), item.ProjectName == "FolderA", item.Message.Contains("\u5b8c\u6210 1 \u4e2a\uff0c\u5931\u8d25 1 \u4e2a", StringComparison.Ordinal) };
        return checks.All(value => value);
    }

    private static bool TestAllFailedBatch()
    {
        using var fixture = new Fixture("failed");
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((201, 2000), (202, 2001));
        Thread.Sleep(4300);
        fixture.FailTasks((201, 17, 2040), (202, 18, 2042));
        fixture.WaitForEvents(1);

        // A later database write affecting the same terminal task set must not emit again.
        fixture.TouchFailedTask(202, 2043);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        var checks = new[] { fixture.Events.Count == 1, item.CompletedCount == 0, item.FailedCount == 2, item.Status == "failed", item.Duration == TimeSpan.FromSeconds(42), item.Message.Contains("\u5b8c\u6210 0 \u4e2a\uff0c\u5931\u8d25 2 \u4e2a", StringComparison.Ordinal) };
        return checks.All(value => value);
    }

    private static bool TestCompletedBatch()
    {
        using var fixture = new Fixture("completed");
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((301, 3000), (302, 3002));
        Thread.Sleep(4300);
        fixture.CompleteTasks((301, 3000, 3030), (302, 3002, 3035));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.CompletedCount == 2 && item.FailedCount == 0 &&
               item.Status == "completed" && item.Duration == TimeSpan.FromSeconds(35) &&
               item.ProjectName == "FolderA";
    }

    private static bool TestUploadCompletedBatch()
    {
        using var fixture = new Fixture("upload-completed", "upload");
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((401, 4000), (402, 4002));
        Thread.Sleep(4300);
        fixture.CompleteTasks((401, 4000, 4030), (402, 4002, 4035));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.Kind == "upload" && item.Status == "completed" &&
               item.CompletedCount == 2 && item.FailedCount == 0 &&
               item.Message.Contains("上传", StringComparison.Ordinal);
    }

    private static bool TestUploadFailedBatch()
    {
        using var fixture = new Fixture("upload-failed", "upload");
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((501, 5000), (502, 5001));
        Thread.Sleep(4300);
        fixture.FailTasks((501, 17, 5040), (502, 18, 5042));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.Kind == "upload" && item.Status == "failed" &&
               item.CompletedCount == 0 && item.FailedCount == 2 &&
               item.Message.Contains("上传", StringComparison.Ordinal);
    }

    private static bool TestNoNotificationWhenStopped()
    {
        using var fixture = new Fixture("stopped");
        fixture.ProcessRunning = false;
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((601, 6000));
        fixture.CompleteTasks((601, 6000, 6040));
        Thread.Sleep(4200);
        if (fixture.Events.Count != 0) return false;

        fixture.ProcessRunning = true;
        monitor.Refresh();
        Thread.Sleep(1000);
        return fixture.Events.Count == 0;
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
        private readonly string _dbPath;
        private readonly string _kind;
        private readonly object _gate = new object();
        internal List<BaiduBatchCompletion> Events { get; } = new List<BaiduBatchCompletion>();
        internal bool ProcessRunning { get; set; } = true;

        internal Fixture(string name, string kind = "download")
        {
            _kind = kind;
            _root = Path.Combine(Path.GetTempPath(), "taprelay-baidu-tests", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "BaiduYunGuanjia.db");
            CreateSchema();
            Environment.SetEnvironmentVariable("TAPRELAY_BAIDU_DB", _dbPath);
        }

        private string TaskTable => _kind + "_file";
        private string HistoryTable => _kind + "_history_file";

        internal BaiduNetdiskBatchMonitor StartMonitor()
        {
            var monitor = new BaiduNetdiskBatchMonitor(
                item => { lock (_gate) Events.Add(item); },
                message => Console.WriteLine("LOG: " + message),
                () => ProcessRunning);
            monitor.Start();
            return monitor;
        }

        internal void InsertTasks(params (long id, long start)[] tasks)
        {
            ExecuteTransaction((connection, transaction) =>
            {
                foreach (var task in tasks)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO " + TaskTable + "(task_id,status,error_code,add_time,status_changetime) VALUES($id,1,0,$start,$start)";
                    command.Parameters.AddWithValue("$id", task.id);
                    command.Parameters.AddWithValue("$start", task.start);
                    command.ExecuteNonQuery();
                }
            });
        }

        internal void CompleteAndFail(long completedId, long failedId, long start, long end, long errorCode)
        {
            ExecuteTransaction((connection, transaction) =>
            {
                Execute(connection, transaction, "DELETE FROM " + TaskTable + " WHERE task_id=$id", ("$id", completedId));
                    Execute(connection, transaction,
                        "INSERT INTO " + HistoryTable + "(id,server_path,local_path,isdir,size,op_starttime,op_endtime) VALUES(1,'/remote/FolderA/completed.bin','D:/Cloud/FolderA/completed.bin',0,1,$start,$end)",
                    ("$start", start), ("$end", end));
                Execute(connection, transaction,
                    "UPDATE " + TaskTable + " SET error_code=$error,status_changetime=$end WHERE task_id=$id",
                    ("$error", errorCode), ("$end", end), ("$id", failedId));
            });
        }

        internal void FailTasks(params (long id, long error, long end)[] tasks)
        {
            ExecuteTransaction((connection, transaction) =>
            {
                foreach (var task in tasks)
                    Execute(connection, transaction,
                        "UPDATE " + TaskTable + " SET error_code=$error,status_changetime=$end WHERE task_id=$id",
                        ("$error", task.error), ("$end", task.end), ("$id", task.id));
            });
        }

        internal void TouchFailedTask(long id, long end)
        {
            ExecuteTransaction((connection, transaction) =>
                Execute(connection, transaction,
                    "UPDATE " + TaskTable + " SET status_changetime=$end WHERE task_id=$id",
                    ("$end", end), ("$id", id)));
        }

        internal void CompleteTasks(params (long id, long start, long end)[] tasks)
        {
            ExecuteTransaction((connection, transaction) =>
            {
                long historyId = 1;
                foreach (var task in tasks)
                {
                    Execute(connection, transaction, "DELETE FROM " + TaskTable + " WHERE task_id=$id", ("$id", task.id));
                    Execute(connection, transaction,
                        "INSERT INTO " + HistoryTable + "(id,server_path,local_path,isdir,size,op_starttime,op_endtime) VALUES($history,'/remote/FolderA/file.bin','D:/Cloud/FolderA/file.bin',0,1,$start,$end)",
                        ("$history", historyId++), ("$start", task.start), ("$end", task.end));
                }
            });
        }

        internal void WaitForEvents(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate) if (Events.Count >= count) return;
                Thread.Sleep(100);
            }
            throw new TimeoutException("aggregate callback was not received");
        }

        internal BaiduBatchCompletion SingleEvent()
        {
            lock (_gate)
            {
                if (Events.Count == 0) throw new InvalidOperationException("no event was emitted");
                return Events[0];
            }
        }

        private void CreateSchema()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE download_file(
  task_id INTEGER PRIMARY KEY,
  status INTEGER,
  error_code INTEGER,
  add_time INTEGER,
  status_changetime INTEGER
);
CREATE TABLE download_history_file(
  id INTEGER PRIMARY KEY,
  server_path TEXT,
  local_path TEXT,
  isdir INTEGER,
  size INTEGER,
  op_starttime INTEGER,
  op_endtime INTEGER,
  download_type INTEGER
);
CREATE TABLE upload_file(
  task_id INTEGER PRIMARY KEY,
  status INTEGER,
  error_code INTEGER,
  add_time INTEGER,
  status_changetime INTEGER
);
CREATE TABLE upload_history_file(
  id INTEGER PRIMARY KEY,
  server_path TEXT,
  local_path TEXT,
  isdir INTEGER,
  size INTEGER,
  op_starttime INTEGER,
  op_endtime INTEGER
);";
            command.ExecuteNonQuery();
        }

        private void ExecuteTransaction(Action<SqliteConnection, SqliteTransaction> action)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            action(connection, transaction);
            transaction.Commit();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string name, object value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TAPRELAY_BAIDU_DB", null);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
