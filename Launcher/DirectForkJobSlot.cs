namespace ZZZScannerHelper;

// Reserves a task before permission prompts or process creation. Callbacks only publish
// in-memory state; process launch, wait, kill and file reads stay outside this lock.
internal sealed class DirectForkJobSlot<T>(object gate) where T : class
{
    private long _generation;
    private bool _busy;
    private T? _process;
    private bool _stopping;
    private Action? _stopped;

    internal bool IsBusy { get { lock (gate) return _busy; } }

    internal long Reserve()
    {
        lock (gate)
        {
            if (_busy) throw new InvalidOperationException("scan_already_running");
            _busy = true;
            _stopping = false;
            _stopped = null;
            return ++_generation;
        }
    }

    internal bool Publish(long generation, Action publish)
    {
        lock (gate)
        {
            if (!_busy || _stopping || generation != _generation) return false;
            publish();
            return true;
        }
    }

    internal bool Attach(long generation, T process)
    {
        lock (gate)
        {
            if (!_busy || generation != _generation) return false;
            // A canceled UAC prompt can still return a live elevated process.
            // Own it until exit, while refusing publication for the canceled job.
            _process = process;
            return !_stopping;
        }
    }

    internal bool Complete(long generation, Action publish)
    {
        lock (gate)
        {
            if (!_busy || generation != _generation) return false;
            if (_stopping) _stopped?.Invoke();
            else publish();
            _busy = false;
            _process = null;
            _stopped = null;
            return true;
        }
    }

    internal T? Stop(Action publish, Action? stopped = null)
    {
        lock (gate)
        {
            if (_stopping && _busy) return null;
            _stopping = _busy;
            _stopped = stopped;
            publish();
            // Stop revokes publication, not physical process ownership. This
            // also holds the slot while Process.Start is blocked inside UAC.
            return _process;
        }
    }

    internal async Task CompleteAfterExitAsync(long generation, Func<Task> waitForExit)
    {
        // A successful Kill is asynchronous; denied/partial termination does
        // not prove exit either. An unsuccessful wait must keep the slot busy.
        await waitForExit();
        Complete(generation, () => { });
    }
}

internal static class DirectForkTermination
{
    internal static Exception? TryRequest(Action terminate)
    {
        try { terminate(); return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or AggregateException or InvalidOperationException or UnauthorizedAccessException)
        {
            return ex;
        }
    }
}
