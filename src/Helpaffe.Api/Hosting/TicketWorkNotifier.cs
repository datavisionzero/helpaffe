namespace Helpaffe.Api.Hosting;

public sealed class TicketWorkNotifier
{
    private readonly Lock _gate = new();
    private long _version;
    private TaskCompletionSource _changed = NewSignal();

    public long Version
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    public void Signal()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _version++;
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
    }

    public async Task WaitForChangeAsync(long observedVersion, TimeSpan fallbackDelay, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_gate)
        {
            if (_version != observedVersion) return;
            changed = _changed.Task;
        }

        try
        {
            await changed.WaitAsync(fallbackDelay, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The fallback scan covers time-based eligibility and access changes.
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
