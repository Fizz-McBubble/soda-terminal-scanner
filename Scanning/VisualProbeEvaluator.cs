using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public enum VisualTransformClass
{
    Neutral,
    HighlightClipped,
    WarmShifted,
    SaturationShifted,
    ContrastShifted,
    Unknown
}

public sealed record ChromaticProbeResult(
    bool Passed,
    int Score,
    double Coverage,
    float MedianHue,
    float MedianSaturation,
    float MedianValue,
    int HueDelta,
    int SaturationDeltaPercent,
    int ValueDeltaPercent,
    Color MedianColor,
    VisualTransformClass TransformClass,
    double MeanLuminance,
    double LuminanceStandardDeviation);

public sealed record VisualRarityCandidate(string Rarity, Color Color);

public sealed record RarityProbeResult(
    string? Rarity,
    Color BestColor,
    string BestCandidate,
    int BestScore,
    int SecondScore,
    int Margin);

public readonly record struct RowPresenceProbeResult(
    bool Present,
    int ReferenceLuma,
    int CandidateLuma,
    int LumaDelta,
    int AllowedLumaDelta,
    int EdgeDensityPermille,
    int MinimumEdgeDensityPermille);

public sealed class SelectionRefreshGate
{
    private readonly int _requiredStableFrames;

    public SelectionRefreshGate(int requiredStableFrames = 2)
    {
        _requiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public bool ChangedFromTarget { get; private set; }
    public int StableFrames { get; private set; }
    public bool Accepted { get; private set; }

    public bool Observe(bool changedFromTarget, bool stableWithPreviousFrame)
    {
        if (!changedFromTarget)
        {
            ChangedFromTarget = false;
            StableFrames = 0;
            Accepted = false;
            return false;
        }

        ChangedFromTarget = true;
        StableFrames = stableWithPreviousFrame ? StableFrames + 1 : 1;
        Accepted = StableFrames >= _requiredStableFrames;
        return Accepted;
    }
}

public static class SelectionRefreshTiming
{
    public const int MaximumWaitMilliseconds = 600;

    public static int ResolveMaximumWaitMilliseconds(int loadTimeoutMilliseconds) =>
        Math.Min(Math.Max(1, loadTimeoutMilliseconds), MaximumWaitMilliseconds);
}

internal static class SelectionVisualProbe
{
    private const int EdgeBandPixels = 24;
    private const int SampleStride = 2;

    public static int[] CreateSamples(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            return [];
        }

        var bandX = Math.Min(EdgeBandPixels, Math.Max(1, image.Width / 3));
        var bandY = Math.Min(EdgeBandPixels, Math.Max(1, image.Height / 3));
        var samples = new List<int>();
        for (var y = 0; y < image.Height; y += SampleStride)
        {
            for (var x = 0; x < image.Width; x += SampleStride)
            {
                if (x >= bandX && x < image.Width - bandX
                    && y >= bandY && y < image.Height - bandY)
                {
                    continue;
                }

                var color = image.GetPixel(x, y);
                samples.Add((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
                samples.Add(Math.Max(0, ((color.R + color.G) / 2) - color.B));
            }
        }

        return samples.ToArray();
    }

    public static int[] CreateSamples(CapturedFrame image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CreateSamples(image, new Rectangle(0, 0, image.Width, image.Height));
    }

    internal static int[] CreateSamples(CapturedFrame image, Rectangle region)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (region.Left < 0 || region.Top < 0 || region.Right > image.Width || region.Bottom > image.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        var width = region.Width;
        var height = region.Height;
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        // Keep the Bitmap sampling grid and order; only the pixel reader and
        // repeated dimension lookups change for an immutable captured frame.
        var bandX = Math.Min(EdgeBandPixels, Math.Max(1, width / 3));
        var bandY = Math.Min(EdgeBandPixels, Math.Max(1, height / 3));
        var samples = new List<int>();
        for (var y = 0; y < height; y += SampleStride)
        {
            for (var x = 0; x < width; x += SampleStride)
            {
                if (x >= bandX && x < width - bandX
                    && y >= bandY && y < height - bandY)
                {
                    continue;
                }

                var color = image.GetPixel(region.Left + x, region.Top + y);
                samples.Add((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
                samples.Add(Math.Max(0, ((color.R + color.G) / 2) - color.B));
            }
        }

        return samples.ToArray();
    }

    public static int MeasureLocalizedMovement(
        IReadOnlyList<int> before,
        IReadOnlyList<int> after)
    {
        if (before.Count == 0 || before.Count != after.Count)
        {
            return int.MaxValue;
        }

        var differences = new int[before.Count];
        for (var index = 0; index < differences.Length; index++)
        {
            differences[index] = Math.Abs(before[index] - after[index]);
        }

        Array.Sort(differences);
        var retained = Math.Max(1, differences.Length / 4);
        var start = differences.Length - retained;
        var sum = 0L;
        for (var index = start; index < differences.Length; index++)
        {
            sum += differences[index];
        }

        return (int)Math.Min(int.MaxValue, sum / retained);
    }
}

internal static class PanelCaptureTimingPolicy
{
    public const int GdiSafeMinimumAcceptMilliseconds = 180;
    public const int SafeRequiredStableFrames = 2;

    public static int ResolvePollDelayMilliseconds(int requestedMilliseconds, string frameBackend) =>
        string.Equals(frameBackend, "dxgi-raw", StringComparison.OrdinalIgnoreCase)
            ? 0
            : Math.Max(1, requestedMilliseconds);

    public static int ResolveSafeMinimumAcceptMilliseconds(
        int profileMinimumMilliseconds,
        int requestedFloorMilliseconds,
        int loadTimeoutMilliseconds,
        string captureMode)
    {
        var captureMinimum = string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(requestedFloorMilliseconds, 90, 120)
            : GdiSafeMinimumAcceptMilliseconds;
        return Math.Clamp(
            Math.Max(profileMinimumMilliseconds, captureMinimum),
            60,
            Math.Max(60, loadTimeoutMilliseconds));
    }
}

internal sealed class PanelTargetEvidenceGate
{
    public const int RequiredStableFrames = 2;
    public const double MinimumReliableChangeMilliseconds = 25.0;

    public bool Changed { get; private set; }
    public int StableFrames { get; private set; }
    public double? ChangeMilliseconds { get; private set; }
    public bool Stable => Changed && StableFrames >= RequiredStableFrames;

    public bool Observe(bool changedFromBaseline, bool stableWithPreviousFrame, double elapsedMilliseconds)
    {
        if (!changedFromBaseline || elapsedMilliseconds < MinimumReliableChangeMilliseconds)
        {
            Changed = false;
            StableFrames = 0;
            ChangeMilliseconds = null;
            return false;
        }

        Changed = true;
        ChangeMilliseconds ??= elapsedMilliseconds;
        StableFrames = stableWithPreviousFrame ? StableFrames + 1 : 1;
        return Stable;
    }

    public bool CanPromote(
        bool allowSelectionEvidence,
        bool postScrollFirstCell,
        bool selectionRoundTripReady,
        out string blockedReason)
    {
        if (!Stable)
        {
            blockedReason = Changed ? "selection_change_not_stable" : "no_selection_change";
            return false;
        }

        if (!selectionRoundTripReady)
        {
            blockedReason = postScrollFirstCell
                ? "post_scroll_first_cell"
                : "identical_neighbor_roundtrip_required";
            return false;
        }

        if (!allowSelectionEvidence)
        {
            blockedReason = "retry_or_recover_context";
            return false;
        }

        blockedReason = "";
        return true;
    }
}

public static class FirstPairBootstrapTiming
{
    public const int MaximumWaitMilliseconds = 1200;

    public static int ResolveMaximumWaitMilliseconds(int loadTimeoutMilliseconds) =>
        Math.Min(Math.Max(1, loadTimeoutMilliseconds), MaximumWaitMilliseconds);
}

internal enum ScrollTopResetTraceKind
{
    Probe,
    Wheel,
    Settle,
    Click,
    Confirmed,
    Failed
}

internal readonly record struct ScrollTopResetTrace(
    ScrollTopResetTraceKind Kind,
    string Phase,
    int Batch,
    int Tick,
    int Sample,
    int WheelTicks,
    int TopClicks,
    int StableMatches,
    Color ActualColor,
    Color ExpectedColor,
    int Tolerance,
    bool Matched,
    int DelayMilliseconds,
    long ElapsedMilliseconds,
    string MatchReason = "none");

internal readonly record struct ScrollTopResetResult(
    bool Confirmed,
    string Phase,
    int WheelTicks,
    int TopClicks,
    int ProbeSamples,
    Color LastActualColor,
    int StableMatches,
    long ElapsedMilliseconds);
