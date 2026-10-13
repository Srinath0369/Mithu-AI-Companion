namespace Mithu.App.Services;

/// <summary>
/// Ensures only one Mithu process runs per user session.
/// A second launch signals the first instance (so it can show itself) and then exits.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // "Local\" scopes the names to the current user's logon session.
    private const string MutexName = @"Local\Mithu.SingleInstance.Mutex";
    private const string EventName = @"Local\Mithu.SingleInstance.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _registeredWait;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirstInstance { get; }

    /// <summary>Called by a second instance to wake up the first one.</summary>
    public void SignalFirstInstance() => _activateEvent.Set();

    /// <summary>
    /// Listens for activation requests from later launches without holding a dedicated thread.
    /// The callback runs on a thread-pool thread; marshal to the Dispatcher before touching UI.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => onActivate(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
