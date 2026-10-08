using System.Diagnostics;

namespace ZZZScannerNext.Scanning;

internal static class TraversalPositionPolicy
{
    internal const int MissingPositionTimeoutMilliseconds = 3000;

    public static string? Failure(ScanController.ScrollbarThumbProbe expected, ScanController.ScrollbarThumbProbe actual)
    {
        if (!expected.Found || !actual.Found) return "scrollbar_position_missing";
        var startDelta = actual.StartY - expected.StartY;
        var endDelta = actual.EndY - expected.EndY;
        if (startDelta == 0 && endDelta == 0) return null;
        // An antialiased endpoint can alter height by one pixel. Translating
        // both ends, even by one pixel, can mean a row at 3000-item capacity.
        if (Math.Abs(startDelta) <= 1 && Math.Abs(endDelta) <= 1
            && (startDelta == 0 || endDelta == 0)) return null;
        return "unexpected_scroll_during_row";
    }

    // A missing probe is not evidence of movement. Observe without sending
    // input, but never adopt a different position as the new baseline.
    internal static (string? Failure, ScanController.ScrollbarThumbProbe Actual, int Samples)
        ConfirmAfterMissing(
            ScanController.ScrollbarThumbProbe expected,
            Func<ScanController.ScrollbarThumbProbe> capture,
            CancellationToken token,
            Func<long>? elapsedMilliseconds = null,
            Action<int, CancellationToken>? wait = null)
    {
        var watch = Stopwatch.StartNew();
        elapsedMilliseconds ??= () => watch.ElapsedMilliseconds;
        wait ??= (milliseconds, cancellation) => Task.Delay(milliseconds, cancellation).GetAwaiter().GetResult();
        var actual = default(ScanController.ScrollbarThumbProbe);
        var samples = 0;
        var stable = 0;
        token.ThrowIfCancellationRequested();
        if (!expected.Found) return ("scrollbar_position_missing", actual, samples);
        while (elapsedMilliseconds() < MissingPositionTimeoutMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            var remaining = MissingPositionTimeoutMilliseconds - elapsedMilliseconds();
            if (remaining <= 0) break;
            wait((int)Math.Min(25, remaining), token);
            token.ThrowIfCancellationRequested();
            if (elapsedMilliseconds() >= MissingPositionTimeoutMilliseconds) break;
            actual = capture();
            samples++;
            token.ThrowIfCancellationRequested();
            if (elapsedMilliseconds() >= MissingPositionTimeoutMilliseconds) break;
            if (!actual.Found)
            {
                stable = 0;
                continue;
            }
            var failure = Failure(expected, actual);
            if (failure is not null) return (failure, actual, samples);
            if (++stable >= 2) return (null, actual, samples);
        }
        return ("scrollbar_position_missing", actual, samples);
    }
}
