namespace Crowsnest.Host;

/// <summary>
/// One bridge per user session (spec §8). Two copies would race each other for the devices'
/// ports, and autostart plus a double-click is enough to start a second one. The tray and
/// DevConsole share the name, so neither runs beside the other.
///
/// The mutex is never owned: whether it already existed is the whole answer, and an owned
/// mutex would have to be released on the thread that took it, which an await does not keep.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>The guard, held until disposed; null if another copy holds it.</summary>
    public static SingleInstanceGuard? TryAcquire(string name = @"Local\Crowsnest.Bridge")
    {
        Mutex mutex = new(initiallyOwned: false, name, out bool createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
