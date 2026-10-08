using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

internal static class ScrollTopResetCoordinator
{
    public const int WheelBatchSize = 12;
    public const int MaximumWheelTicks = 1024;
    public const int MaximumResetMilliseconds = 60_000;
    public const int ProbeSampleCount = 3;
    public const int RequiredStableMatches = 2;
    public const int ProbeSampleDelayMilliseconds = 40;
    public const int BatchSettleDelayMilliseconds = 160;
    public const int FallbackSettleDelayMilliseconds = 160;

    public static bool IsTopColor(Color actual, Color expected, int tolerance) =>
        actual.IsCloseTo(expected, Math.Max(0, tolerance));

    public static async Task<ScrollTopResetResult> RunAsync(
        int configuredMaximumWheelTicks,
        int wheelDelayMilliseconds,
        int clickDelayMilliseconds,
        Color expectedColor,
        int tolerance,
        Func<Color> captureTopPixel,
        Action wheelUp,
        Action clickTop,
        Action<ScrollTopResetTrace>? trace,
        CancellationToken token,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Func<bool>? captureTopPosition = null,
        bool resetBeforeProbe = false,
        Func<long>? monotonicElapsedMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(captureTopPixel);
        ArgumentNullException.ThrowIfNull(wheelUp);
        ArgumentNullException.ThrowIfNull(clickTop);

        delayAsync ??= Task.Delay;
        var maximumWheelTicks = Math.Clamp(configuredMaximumWheelTicks, 0, MaximumWheelTicks);
        var wheelDelay = Math.Max(0, wheelDelayMilliseconds);
        var fallbackDelay = Math.Max(FallbackSettleDelayMilliseconds, clickDelayMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        monotonicElapsedMilliseconds ??= () => stopwatch.ElapsedMilliseconds;
        var deadlineReached = false;
        var wheelTicks = 0;
        var topClicks = 0;
        var probeSamples = 0;
        var lastActual = Color.Empty;
        var lastStableMatches = 0;

        bool HasTime()
        {
            token.ThrowIfCancellationRequested();
            deadlineReached |= monotonicElapsedMilliseconds() >= MaximumResetMilliseconds;
            return !deadlineReached;
        }

        async Task<bool> DelayWithinDeadlineAsync(int milliseconds)
        {
            if (!HasTime()) return false;
            var remaining = MaximumResetMilliseconds - monotonicElapsedMilliseconds();
            if (remaining <= 0) { deadlineReached = true; return false; }
            var boundedDelay = (int)Math.Min(milliseconds, remaining);
            await delayAsync(boundedDelay, token);
            // A shortened wait cannot support a successful probe even when a
            // test delay completes without advancing its clock.
            deadlineReached |= boundedDelay < milliseconds;
            return HasTime();
        }

        async Task<bool> ProbeAsync(string phase, int batch)
        {
            var stableMatches = 0;
            for (var sample = 1; sample <= ProbeSampleCount; sample++)
            {
                if (!HasTime()) return false;
                lastActual = captureTopPixel();
                probeSamples++;
                if (!HasTime()) return false;
                var colorMatched = IsTopColor(lastActual, expectedColor, tolerance);
                var positionMatched = captureTopPosition?.Invoke() == true;
                if (!HasTime()) return false;
                // A top-coloured pixel can belong to something other than the
                // scrollbar thumb. Geometry, when supplied, is authoritative.
                var matched = captureTopPosition is null ? colorMatched : positionMatched;
                var matchReason = positionMatched
                    ? "thumb_geometry"
                    : captureTopPosition is null && colorMatched
                        ? "top_color"
                        : "none";
                stableMatches = matched ? stableMatches + 1 : 0;
                lastStableMatches = stableMatches;
                trace?.Invoke(new ScrollTopResetTrace(
                    ScrollTopResetTraceKind.Probe,
                    phase,
                    batch,
                    wheelTicks,
                    sample,
                    wheelTicks,
                    topClicks,
                    stableMatches,
                    lastActual,
                    expectedColor,
                    tolerance,
                    matched,
                    0,
                    monotonicElapsedMilliseconds(),
                    matchReason));
                if (stableMatches >= RequiredStableMatches)
                {
                    return HasTime();
                }

                if (sample < ProbeSampleCount)
                {
                    if (!await DelayWithinDeadlineAsync(ProbeSampleDelayMilliseconds)) return false;
                }
            }

            return false;
        }

        ScrollTopResetResult Result(bool confirmed, string phase, int batch)
        {
            token.ThrowIfCancellationRequested();
            var elapsed = monotonicElapsedMilliseconds();
            // Trace callbacks are synchronous too. Recheck the timestamp used
            // by the returned result so a slow confirmation trace cannot make
            // the caller accept success after the deadline.
            if (confirmed && (deadlineReached || elapsed >= MaximumResetMilliseconds))
            {
                confirmed = false;
                phase = "deadline";
                lastStableMatches = 0;
                TraceOutcome(ScrollTopResetTraceKind.Failed, phase, batch);
            }
            return new ScrollTopResetResult(
                confirmed, phase, wheelTicks, topClicks, probeSamples,
                lastActual, lastStableMatches, elapsed);
        }

        void TraceOutcome(ScrollTopResetTraceKind kind, string phase, int batch)
        {
            trace?.Invoke(new ScrollTopResetTrace(
                kind,
                phase,
                batch,
                wheelTicks,
                0,
                wheelTicks,
                topClicks,
                lastStableMatches,
                lastActual,
                expectedColor,
                tolerance,
                lastStableMatches >= RequiredStableMatches,
                0,
                monotonicElapsedMilliseconds()));
        }

        ScrollTopResetResult DeadlineResult(int batch)
        {
            lastStableMatches = 0;
            TraceOutcome(ScrollTopResetTraceKind.Failed, "deadline", batch);
            return Result(false, "deadline", batch);
        }

        async Task<bool> ResetAndProbeAsync(string phase, int batch)
        {
            if (!HasTime()) return false;
            clickTop();
            topClicks++;
            if (!HasTime()) return false;
            trace?.Invoke(new ScrollTopResetTrace(
                ScrollTopResetTraceKind.Click, phase, batch, wheelTicks, 0,
                wheelTicks, topClicks, 0, Color.Empty, expectedColor, tolerance,
                false, fallbackDelay, monotonicElapsedMilliseconds()));
            trace?.Invoke(new ScrollTopResetTrace(
                ScrollTopResetTraceKind.Settle, phase, batch, wheelTicks, 0,
                wheelTicks, topClicks, 0, Color.Empty, expectedColor, tolerance,
                false, fallbackDelay, monotonicElapsedMilliseconds()));
            if (!await DelayWithinDeadlineAsync(fallbackDelay)) return false;
            return await ProbeAsync(phase, batch);
        }

        var initialPhase = resetBeforeProbe ? "direct" : "initial";
        var initiallyConfirmed = resetBeforeProbe
            ? await ResetAndProbeAsync(initialPhase, 0)
            : await ProbeAsync(initialPhase, 0);
        if (!HasTime()) return DeadlineResult(0);
        if (initiallyConfirmed)
        {
            TraceOutcome(ScrollTopResetTraceKind.Confirmed, initialPhase, 0);
            return Result(true, initialPhase, 0);
        }

        var batch = 0;
        while (wheelTicks < maximumWheelTicks && HasTime())
        {
            batch++;
            var ticksInBatch = Math.Min(WheelBatchSize, maximumWheelTicks - wheelTicks);
            for (var tick = 0; tick < ticksInBatch; tick++)
            {
                if (!HasTime()) return DeadlineResult(batch);
                wheelUp();
                wheelTicks++;
                if (!HasTime()) return DeadlineResult(batch);
                trace?.Invoke(new ScrollTopResetTrace(
                    ScrollTopResetTraceKind.Wheel,
                    "wheel",
                    batch,
                    wheelTicks,
                    0,
                    wheelTicks,
                    topClicks,
                    0,
                    Color.Empty,
                    expectedColor,
                    tolerance,
                    false,
                    wheelDelay,
                    monotonicElapsedMilliseconds()));
                if (wheelDelay > 0)
                {
                    if (!await DelayWithinDeadlineAsync(wheelDelay)) return DeadlineResult(batch);
                }
            }

            trace?.Invoke(new ScrollTopResetTrace(
                ScrollTopResetTraceKind.Settle,
                "wheel",
                batch,
                wheelTicks,
                0,
                wheelTicks,
                topClicks,
                0,
                Color.Empty,
                expectedColor,
                tolerance,
                false,
                BatchSettleDelayMilliseconds,
                monotonicElapsedMilliseconds()));
            if (!await DelayWithinDeadlineAsync(BatchSettleDelayMilliseconds)) return DeadlineResult(batch);
            var wheelConfirmed = await ProbeAsync("wheel", batch);
            if (!HasTime()) return DeadlineResult(batch);
            if (wheelConfirmed)
            {
                TraceOutcome(ScrollTopResetTraceKind.Confirmed, "wheel", batch);
                return Result(true, "wheel", batch);
            }
        }

        if (!HasTime()) return DeadlineResult(batch);
        var fallbackConfirmed = await ResetAndProbeAsync("fallback", batch);
        if (!HasTime()) return DeadlineResult(batch);
        if (fallbackConfirmed)
        {
            TraceOutcome(ScrollTopResetTraceKind.Confirmed, "fallback", batch);
            return Result(true, "fallback", batch);
        }

        TraceOutcome(ScrollTopResetTraceKind.Failed, "fallback", batch);
        return Result(false, "fallback", batch);
    }
}

internal static class ScrollbarTopResetPlanner
{
    // At 3000 discs a logical row occupies fewer than two scrollbar pixels.
    // A two-pixel tolerance can silently omit the first row.
    public const int TopEdgeTolerancePixels = 0;

    public static bool IsScrollableThumb(
        int trackEndpointAY, int trackEndpointBY, int thumbStartY, int thumbEndY)
    {
        var trackStart = Math.Min(trackEndpointAY, trackEndpointBY);
        var trackEnd = Math.Max(trackEndpointAY, trackEndpointBY);
        var thumbStart = Math.Min(thumbStartY, thumbEndY);
        var thumbEnd = Math.Max(thumbStartY, thumbEndY);
        var height = thumbEnd - thumbStart + 1;
        return thumbStart >= trackStart && thumbEnd <= trackEnd
            && height >= 3 && height < trackEnd - trackStart + 1;
    }

    public static int HeightTolerancePixels(int clientHeight) =>
        Math.Max(1, (int)Math.Ceiling(4d * clientHeight / 1080));

    public static bool HasConsistentHeight(int referenceHeight, int thumbStartY, int thumbEndY, int clientHeight) =>
        referenceHeight >= 3 && clientHeight > 0
        // The probe anchors clip the rounded thumb at the track ends. Scale
        // that clipping allowance with the game image, not desktop DPI. This
        // does not relax the exact top-position check below.
        && Math.Abs(referenceHeight - (Math.Abs(thumbEndY - thumbStartY) + 1)) <= HeightTolerancePixels(clientHeight);

    public static int MaximumScrollableThumbHeight(
        int trackEndpointAY, int trackEndpointBY, int? inventoryCount, int columns, int visibleRows)
    {
        var trackHeight = Math.Abs(trackEndpointBY - trackEndpointAY) + 1;
        if (inventoryCount is not > 0)
            return Math.Max(0, trackHeight - 1);
        var totalRows = (int)Math.Ceiling(inventoryCount.Value / (double)Math.Max(1, columns));
        // Allow the game's minimum thumb size and antialiased ends, but never
        // mistake almost the whole grey track for a large-inventory thumb.
        var proportionalHeight = (int)Math.Ceiling(trackHeight * Math.Max(1, visibleRows) / (double)totalRows);
        return Math.Min(Math.Max(0, trackHeight - 1), Math.Max(32, proportionalHeight + 4));
    }

    public static int GetTopCenterY(
        int trackEndpointAY,
        int trackEndpointBY,
        int thumbStartY,
        int thumbEndY)
    {
        var trackStart = Math.Min(trackEndpointAY, trackEndpointBY);
        var trackEnd = Math.Max(trackEndpointAY, trackEndpointBY);
        var trackHeight = Math.Max(1, trackEnd - trackStart + 1);
        var thumbHeight = Math.Clamp(Math.Abs(thumbEndY - thumbStartY) + 1, 1, trackHeight);
        return trackStart + (thumbHeight - 1) / 2;
    }

    public static bool IsAtTop(
        int trackEndpointAY,
        int trackEndpointBY,
        int thumbStartY,
        int thumbEndY) =>
        Math.Abs(
            Math.Min(thumbStartY, thumbEndY)
            - Math.Min(trackEndpointAY, trackEndpointBY)) <= TopEdgeTolerancePixels;
}

internal enum RowAdvanceDecision
{
    NoMove,
    OneRow,
    TwoRows,
    Ambiguous
}

internal readonly record struct RowVisualSignature(ulong Hash, short[] NormalizedLuminance);

internal static class RowVisualSignatureExtractor
{
    public const int Columns = 24;
    public const int Rows = 8;
    public const int TrimPercent = 10;

    public static RowVisualSignature Create(Bitmap image, Rectangle rect) =>
        Create(image.Width, image.Height, image.GetPixel, rect);

    public static RowVisualSignature Create(CapturedFrame image, Rectangle rect) =>
        Create(image.Width, image.Height, image.GetPixel, rect);

    public static int Distance(RowVisualSignature left, RowVisualSignature right)
    {
        var leftSamples = left.NormalizedLuminance;
        var rightSamples = right.NormalizedLuminance;
        if (leftSamples.Length == 0 || leftSamples.Length != rightSamples.Length)
        {
            return left.Hash == right.Hash ? 0 : int.MaxValue;
        }

        var differences = new int[leftSamples.Length];
        for (var index = 0; index < differences.Length; index++)
        {
            differences[index] = Math.Abs(leftSamples[index] - rightSamples[index]);
        }

        Array.Sort(differences);
        var dropped = differences.Length * TrimPercent / 100;
        var retained = Math.Max(1, differences.Length - dropped);
        var sum = 0L;
        for (var index = 0; index < retained; index++)
        {
            sum += differences[index];
        }

        return (int)Math.Min(int.MaxValue, sum / retained);
    }

    private static RowVisualSignature Create(
        int width,
        int height,
        Func<int, int, Color> getPixel,
        Rectangle rect)
    {
        if (width <= 0 || height <= 0)
        {
            return new RowVisualSignature(0, []);
        }

        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, width, height));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return new RowVisualSignature(0, []);
        }

        var luminance = new int[Columns * Rows];
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var index = 0;
        for (var row = 0; row < Rows; row++)
        {
            var y = rect.Top + Math.Min(rect.Height - 1, ((row * 2 + 1) * rect.Height) / (Rows * 2));
            for (var column = 0; column < Columns; column++)
            {
                var x = rect.Left + Math.Min(rect.Width - 1, ((column * 2 + 1) * rect.Width) / (Columns * 2));
                var color = getPixel(x, y);
                luminance[index++] = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                hash ^= color.R;
                hash *= prime;
                hash ^= color.G;
                hash *= prime;
                hash ^= color.B;
                hash *= prime;
            }
        }

        var ordered = (int[])luminance.Clone();
        Array.Sort(ordered);
        var median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
        var normalized = new short[luminance.Length];
        for (var sample = 0; sample < luminance.Length; sample++)
        {
            normalized[sample] = (short)(luminance[sample] - median);
        }

        return new RowVisualSignature(hash, normalized);
    }
}

internal readonly record struct RowAdvanceEvidence(
    RowAdvanceDecision Decision,
    int BestRows,
    int BestScore,
    int SecondScore,
    int Margin,
    bool Strong,
    int NoMoveScore,
    int OneRowScore,
    int TwoRowScore,
    int MovedDistance,
    string Reason)
{
    public bool UsesScrollbarPosition =>
        Reason.StartsWith("scrollbar_", StringComparison.Ordinal);

    public int? AcceptedRows => Decision switch
    {
        RowAdvanceDecision.NoMove => 0,
        RowAdvanceDecision.OneRow => 1,
        RowAdvanceDecision.TwoRows => 2,
        _ => null
    };
}
