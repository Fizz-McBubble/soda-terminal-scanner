using System.Text.Json.Nodes;

namespace ZZZScannerHelper;

internal sealed record DirectForkProgressTimeout(string Code, string Phase, long ElapsedMs, long TimeoutMs);

// Transport keepalives and repeated snapshots do not demonstrate native work.
// Each job owns its clock and counters; waiting for UAC occurs before this clock starts.
internal sealed class DirectForkProgressWatchdog
{
    internal const long StartTimeoutMs = 90_000;
    internal const long ProgressTimeoutMs = 120_000;
    private readonly long[] _highWater = new long[4];
    private long _lastProgressMs;
    private long _elapsedMs;
    private bool _started;

    internal DirectForkProgressTimeout? Observe(JsonObject snapshot, long elapsedMs)
    {
        _elapsedMs = Math.Max(_elapsedMs, elapsedMs);
        var counts = snapshot["diagnostics"]?["counts"];
        var counters = new[]
        {
            Number(snapshot["progress"]?["processed"]), Number(counts?["visited"]),
            Number(counts?["queued"]), Number(counts?["processed"])
        };
        var advanced = false;
        for (var index = 0; index < counters.Length; index++)
            if (counters[index] > _highWater[index])
            {
                _highWater[index] = counters[index];
                advanced = true;
            }
        if (advanced)
        {
            _started = true;
            _lastProgressMs = _elapsedMs;
        }
        var deadline = _started ? ProgressTimeoutMs : StartTimeoutMs;
        var idle = _elapsedMs - _lastProgressMs;
        return idle < deadline ? null : new(
            _started ? "scan_progress_timeout" : "scan_start_timeout",
            _started ? "capture" : "preflight", idle, deadline);
    }

    private static long Number(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<long>(out var wide) ? Math.Max(0, wide)
            : value.TryGetValue<int>(out var narrow) ? Math.Max(0, narrow) : 0
        : 0;
}
