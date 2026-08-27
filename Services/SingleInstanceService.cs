namespace CpuTempWidget.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\MugoByte.Pulse.SingleInstance";
    private Mutex? _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceService()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var created);
            IsFirstInstance = created;
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch
        {
            IsFirstInstance = true;
        }
    }

    public void Dispose() => _mutex?.Dispose();
}
