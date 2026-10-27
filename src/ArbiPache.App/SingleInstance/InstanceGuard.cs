namespace ArbiPache.App.SingleInstance;

/// <summary>Holds a named mutex for the lifetime of the process. Only the first holder acquires it.</summary>
public sealed class InstanceGuard(string mutexName = @"Local\ArbiPache.App") : IDisposable
{
    private Mutex? _mutex;

    public bool TryAcquire()
    {
        try
        {
            // Existence of the named object is the signal; ownership is not taken, so there is no thread affinity.
            var mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // ERROR_ACCESS_DENIED: another session or an elevated instance owns the name.
            return false;
        }
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }
}
