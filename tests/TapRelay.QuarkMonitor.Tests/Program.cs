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
        Run("mixed download batch emits one aggregate completion", TestMixedDownloadBatch);
        Run("all failed download batch emits one aggregate failure", TestAllFailedDownloadBatch);
        Run("completed download batch counts once without re-emission", TestCompletedDownloadBatch);
        Run("upload batch emits completion with upload direction", TestUploadBatch);
        Run("all cancelled batch emits no notification", TestAllCancelledBatch);
        Run("paused task does not block completed sibling", TestPausedTaskDoesNotBlockBatch);
        Run("cancelled task is excluded from mixed batch counts", TestCancelledExcludedFromMixedBatch);
        Run("project name uses the transfer folder", TestProjectNameUsesFolder);
        Run("database changes do not notify while Quark is not running", TestNoNotificationWhenQuarkIsStopped);
        Run("pending callback is dropped after Quark exits", TestPendingCallbackDroppedAfterQuarkExit);
        Run("Quark restart establishes a fresh database baseline", TestRestartDoesNotReplayHistory);

        Console.WriteLine($"Quark monitor tests: {_passed} passed, {Failures.Count} failed");
        foreach (var failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static bool TestMixedDownloadBatch()
    {
        using var fixture = new Fixture("mixed", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((101, 1000), (102, 1001));
        Thread.Sleep(4300);
        fixture.CompleteAndFail(101, 102, 1060);
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        var checks = new[]
        {
            fixture.Events.Count == 1,
            item.Direction == QuarkDirection.Download,
            item.CompletedCount == 1,
            item.FailedCount == 1,
            item.Status == "completed",
            item.Duration == TimeSpan.FromMilliseconds(60),
            item.Source == "quarknetdisk",
            item.Message.Contains("\u5b8c\u6210 1 \u4e2a\uff0c\u5931\u8d25 1 \u4e2a", StringComparison.Ordinal),
            item.Message.Contains("\u4e0b\u8f7d", StringComparison.Ordinal)
        };
        return checks.All(value => value);
    }

    private static bool TestAllFailedDownloadBatch()
    {
        using var fixture = new Fixture("failed", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((201, 2000), (202, 2001));
        Thread.Sleep(4300);
        fixture.FailTasks((201, 2040), (202, 2042));
        fixture.WaitForEvents(1);

        // A later database write touching the same terminal task must not re-emit.
        fixture.TouchTask(202, 2043);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        var checks = new[]
        {
            fixture.Events.Count == 1,
            item.CompletedCount == 0,
            item.FailedCount == 2,
            item.Status == "failed",
            item.Duration == TimeSpan.FromMilliseconds(42)
        };
        return checks.All(value => value);
    }

    private static bool TestCompletedDownloadBatch()
    {
        using var fixture = new Fixture("completed", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((301, 3000), (302, 3002));
        Thread.Sleep(4300);
        fixture.CompleteTasks((301, 3000, 3030), (302, 3002, 3035));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.CompletedCount == 2 && item.FailedCount == 0 &&
               item.Status == "completed" && item.Duration == TimeSpan.FromMilliseconds(35);
    }

    private static bool TestUploadBatch()
    {
        using var fixture = new Fixture("upload", QuarkDirection.Upload);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((401, 5000));
        Thread.Sleep(4300);
        fixture.CompleteTasks((401, 5000, 5080));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        var checks = new[]
        {
            fixture.Events.Count == 1,
            item.Direction == QuarkDirection.Upload,
            item.CompletedCount == 1,
            item.FailedCount == 0,
            item.Status == "completed",
            item.Source == "quarknetdisk-upload",
            item.Message.Contains("\u4e0a\u4f20", StringComparison.Ordinal)
        };
        return checks.All(value => value);
    }

    private static bool TestAllCancelledBatch()
    {
        using var fixture = new Fixture("cancelled", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((501, 6000), (502, 6001));
        Thread.Sleep(4300);
        fixture.SetStatus((501, "CANCEL", 6040), (502, "USER_CANCELED", 6042));
        Thread.Sleep(7000);

        // User cancellation is not an alert: no event at all.
        return fixture.Events.Count == 0;
    }

    private static bool TestPausedTaskDoesNotBlockBatch()
    {
        using var fixture = new Fixture("paused", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((601, 7000), (602, 7001));
        Thread.Sleep(4300);
        fixture.SetStatus((601, "PAUSE", 0));
        fixture.CompleteTasks((602, 7001, 7050));
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.CompletedCount == 1 && item.FailedCount == 0 &&
               item.Status == "completed";
    }

    private static bool TestCancelledExcludedFromMixedBatch()
    {
        using var fixture = new Fixture("mixed-cancel", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((701, 8000), (702, 8001), (703, 8002));
        Thread.Sleep(4300);
        fixture.SetStatus((701, "CANCEL", 8030));
        fixture.CompleteAndFail(702, 703, 8060);
        fixture.WaitForEvents(1);
        Thread.Sleep(3700);

        var item = fixture.SingleEvent();
        return fixture.Events.Count == 1 &&
               item.CompletedCount == 1 && item.FailedCount == 1 &&
               item.Status == "completed";
    }

    private static bool TestProjectNameUsesFolder()
    {
        var projectName = QuarkBatchCompletion.BuildBatchProjectName(new[]
        {
            new QuarkTaskRow
            {
                Name = "archive.zip",
                Path = @"D:\Cloud\FolderA\archive.zip"
            }
        });
        return projectName == "FolderA";
    }

    private static bool TestNoNotificationWhenQuarkIsStopped()
    {
        using var fixture = new Fixture("stopped", QuarkDirection.Download);
        fixture.ProcessRunning = false;
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((801, 9000));
        fixture.CompleteTasks((801, 9040, 9040));
        Thread.Sleep(4200);

        return fixture.Events.Count == 0 &&
               !fixture.Logs.Any(message => message.Contains("轮询检测到夸克网盘数据库文件变化", StringComparison.Ordinal));
    }

    private static bool TestPendingCallbackDroppedAfterQuarkExit()
    {
        using var fixture = new Fixture("exit-pending", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((901, 10000));
        Thread.Sleep(4300);
        fixture.CompleteTasks((901, 10040, 10040));

        fixture.ProcessRunning = false;
        monitor.Refresh();
        Thread.Sleep(3700);

        return fixture.Events.Count == 0;
    }

    private static bool TestRestartDoesNotReplayHistory()
    {
        using var fixture = new Fixture("restart-baseline", QuarkDirection.Download);
        using var monitor = fixture.StartMonitor();
        fixture.InsertTasks((1001, 11000));

        fixture.ProcessRunning = false;
        monitor.Refresh();
        fixture.CompleteTasks((1001, 11040, 11040));

        fixture.ProcessRunning = true;
        monitor.Refresh();
        Thread.Sleep(3700);

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
        private readonly string _tableName;
        private readonly QuarkDirection _direction;
        private readonly object _gate = new object();
        internal List<QuarkBatchCompletion> Events { get; } = new List<QuarkBatchCompletion>();
        internal List<string> Logs { get; } = new List<string>();
        internal bool ProcessRunning { get; set; } = true;

        internal Fixture(string name, QuarkDirection direction)
        {
            _direction = direction;
            _root = Path.Combine(Path.GetTempPath(), "taprelay-quark-tests", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var dbFileName = direction == QuarkDirection.Download
                ? QuarkNetdiskBatchMonitor.DownloadDbName
                : QuarkNetdiskBatchMonitor.UploadDbName;
            _dbPath = Path.Combine(_root, dbFileName);
            _tableName = direction == QuarkDirection.Download
                ? QuarkNetdiskBatchMonitor.DownloadTableName
                : QuarkNetdiskBatchMonitor.UploadTableName;
            CreateSchema();
            Environment.SetEnvironmentVariable("TAPRELAY_QUARK_DB", _dbPath);
        }

        internal QuarkNetdiskBatchMonitor StartMonitor()
        {
            var monitor = new QuarkNetdiskBatchMonitor(
                item => { lock (_gate) Events.Add(item); },
                message =>
                {
                    lock (_gate) Logs.Add(message);
                    Console.WriteLine("LOG: " + message);
                },
                () => ProcessRunning);
            monitor.Start();
            return monitor;
        }

        internal void InsertTasks(params (long id, long create)[] tasks)
        {
            ExecuteTransaction(transaction =>
            {
                foreach (var task in tasks)
                {
                    Execute(transaction,
                        "INSERT INTO " + _tableName + "(id,status,createTime,finishTime,updateTime) VALUES($id,'RUNNING',$create,0,$create)",
                        ("$id", task.id.ToString()), ("$create", task.create));
                }
            });
        }

        internal void CompleteTasks(params (long id, long create, long finish)[] tasks)
        {
            ExecuteTransaction(transaction =>
            {
                foreach (var task in tasks)
                {
                    Execute(transaction,
                        "UPDATE " + _tableName + " SET status='FINISH',finishTime=$finish,updateTime=$finish WHERE id=$id",
                        ("$finish", task.finish), ("$id", task.id.ToString()));
                }
            });
        }

        internal void FailTasks(params (long id, long end)[] tasks)
        {
            ExecuteTransaction(transaction =>
            {
                foreach (var task in tasks)
                {
                    Execute(transaction,
                        "UPDATE " + _tableName + " SET status='DOWNLOAD_FAIL',finishTime=$end,updateTime=$end WHERE id=$id",
                        ("$end", task.end), ("$id", task.id.ToString()));
                }
            });
        }

        internal void CompleteAndFail(long completedId, long failedId, long end)
        {
            ExecuteTransaction(transaction =>
            {
                Execute(transaction,
                    "UPDATE " + _tableName + " SET status='FINISH',finishTime=$end,updateTime=$end WHERE id=$id",
                    ("$end", end), ("$id", completedId.ToString()));
                Execute(transaction,
                    "UPDATE " + _tableName + " SET status='DOWNLOAD_FAIL',finishTime=$end,updateTime=$end WHERE id=$id",
                    ("$end", end), ("$id", failedId.ToString()));
            });
        }

        internal void TouchTask(long id, long end)
        {
            ExecuteTransaction(transaction =>
                Execute(transaction,
                    "UPDATE " + _tableName + " SET updateTime=$end WHERE id=$id",
                    ("$end", end), ("$id", id.ToString())));
        }

        internal void SetStatus(params (long id, string status, long end)[] tasks)
        {
            ExecuteTransaction(transaction =>
            {
                foreach (var task in tasks)
                {
                    Execute(transaction,
                        "UPDATE " + _tableName + " SET status=$status,finishTime=$end,updateTime=$end WHERE id=$id",
                        ("$status", task.status), ("$end", task.end), ("$id", task.id.ToString()));
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

        internal QuarkBatchCompletion SingleEvent()
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
            command.CommandText = "CREATE TABLE " + _tableName + "(id TEXT PRIMARY KEY, status TEXT, createTime INTEGER, finishTime INTEGER, updateTime INTEGER);";
            command.ExecuteNonQuery();
        }

        private void ExecuteTransaction(Action<SqliteTransaction> action)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            action(transaction);
            transaction.Commit();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteTransaction transaction, string sql, params (string name, object value)[] parameters)
        {
            using var command = transaction.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.name, parameter.value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TAPRELAY_QUARK_DB", null);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
