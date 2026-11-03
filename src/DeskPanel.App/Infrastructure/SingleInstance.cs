using System;
using System.Threading;

namespace DeskPanel.App.Infrastructure;

internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DeskPanel.SingleInstance";
    private const string ShowEventName = @"Local\DeskPanel.ShowPanel";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread; subscribers must marshal to the dispatcher.</summary>
    public event Action? ShowRequested;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public static void SignalExistingInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
