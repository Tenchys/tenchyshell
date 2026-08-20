namespace TenchyShell.Core.Runtime;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        this.mutex = mutex;
        ownsMutex = true;
    }

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        if (!ownsMutex)
        {
            return;
        }

        mutex.ReleaseMutex();
        mutex.Dispose();
        ownsMutex = false;
        GC.SuppressFinalize(this);
    }
}
