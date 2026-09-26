namespace ZZZScannerHelper;

// Reserves a task before permission prompts or process creation. Callbacks only publish
// in-memory state; process launch, wait, kill and file reads stay outside this lock.
internal sealed class DirectForkJobSlot<T>(object gate) where T : class
{
    private long _generation;
    private bool _busy;
    private T? _process;

    internal bool IsBusy { get { lock (gate) return _busy; } }

    internal long Reserve()
    {
        lock (gate)
        {
            if (_busy) throw new InvalidOperationException("scan_already_running");
            _busy = true;
            return ++_generation;
        }
    }

    internal bool Publish(long generation, Action publish)
    {
        lock (gate)
        {
            if (!_busy || generation != _generation) return false;
            publish();
            return true;
        }
    }

    internal bool Attach(long generation, T process) => Publish(generation, () => _process = process);

    internal bool Complete(long generation, Action publish)
    {
        lock (gate)
        {
            if (!_busy || generation != _generation) return false;
            publish();
            _busy = false;
            _process = null;
            return true;
        }
    }

    internal T? Stop(Action publish)
    {
        lock (gate)
        {
            ++_generation;
            _busy = false;
            var process = _process;
            _process = null;
            publish();
            return process;
        }
    }
}
