using System;
using System.Threading;

namespace TapRelay
{
    /// <summary>
    /// Runs a monitor callback once at a time. The next tick is scheduled only
    /// after the previous callback returns, so a slow scan cannot pile up work
    /// on the thread pool.
    /// </summary>
    internal sealed class NonReentrantMonitorTimer : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action _callback;
        private readonly Func<TimeSpan> _intervalProvider;
        private readonly Action<Exception>? _onError;
        private readonly Timer _timer;
        private bool _disposed;
        private int _running;

        internal NonReentrantMonitorTimer(
            Action callback,
            TimeSpan dueTime,
            TimeSpan interval,
            Action<Exception>? onError = null)
            : this(callback, () => interval, dueTime, onError)
        {
        }

        internal NonReentrantMonitorTimer(
            Action callback,
            Func<TimeSpan> intervalProvider,
            TimeSpan dueTime,
            Action<Exception>? onError = null)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _intervalProvider = intervalProvider ?? throw new ArgumentNullException(nameof(intervalProvider));
            _onError = onError;
            _timer = new Timer(OnTick, null, dueTime, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Dispose();
            }
        }

        private void OnTick(object? state)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0)
            {
                ScheduleNext();
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                }

                _callback();
            }
            catch (Exception error)
            {
                try
                {
                    _onError?.Invoke(error);
                }
                catch
                {
                    // A diagnostics callback must never terminate the timer.
                }
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                ScheduleNext();
            }
        }

        private void ScheduleNext()
        {
            TimeSpan interval;
            try
            {
                interval = _intervalProvider();
            }
            catch
            {
                interval = TimeSpan.FromSeconds(30);
            }

            if (interval < TimeSpan.FromMilliseconds(100))
            {
                interval = TimeSpan.FromMilliseconds(100);
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _timer.Change(interval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }
}
