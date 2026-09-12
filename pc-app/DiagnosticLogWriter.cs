using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace TapRelay
{
    /// <summary>
    /// Serializes diagnostic writes away from the WinForms thread and keeps the
    /// local log bounded through size-based rotation.
    /// </summary>
    internal sealed class DiagnosticLogWriter : IDisposable
    {
        private const int QueueCapacity = 4096;
        private const long MaxFileBytes = 4L * 1024L * 1024L;
        private const int MaxBackupFiles = 3;

        private readonly string _path;
        private readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), QueueCapacity);
        private readonly Thread _worker;
        private int _disposed;

        internal DiagnosticLogWriter(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _worker = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "TapRelay-DiagnosticLog"
            };
            _worker.Start();
        }

        internal void Enqueue(string line)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _queue.TryAdd(line ?? string.Empty);
            }
            catch (InvalidOperationException)
            {
                // Shutdown raced with a producer; the process is already closing.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.CompleteAdding();
            var stopped = false;
            if (Thread.CurrentThread != _worker)
            {
                stopped = _worker.Join(3000);
            }
            else
            {
                stopped = true;
            }
            if (stopped)
            {
                _queue.Dispose();
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        AppendLine(line);
                    }
                    catch
                    {
                        // Diagnostics must never terminate the application.
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Dispose can race with the final drain during process shutdown.
            }
        }

        private void AppendLine(string line)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = Encoding.UTF8.GetByteCount(line ?? string.Empty);
            if (File.Exists(_path) && new FileInfo(_path).Length + bytes > MaxFileBytes)
            {
                Rotate();
            }

            File.AppendAllText(_path, line ?? string.Empty, new UTF8Encoding(false));
        }

        private void Rotate()
        {
            for (var index = MaxBackupFiles - 1; index >= 1; index--)
            {
                var source = _path + "." + index;
                var destination = _path + "." + (index + 1);
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }

            var firstBackup = _path + ".1";
            if (File.Exists(firstBackup))
            {
                File.Delete(firstBackup);
            }
            if (File.Exists(_path))
            {
                File.Move(_path, firstBackup);
            }
        }
    }
}
