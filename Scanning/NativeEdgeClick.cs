using System.Drawing;
using System.Buffers;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ZZZScannerNext.Scanning;

internal enum NativeEdgeClickDecision
{
    AdvanceOne,
    NoMove,
    Stop
}

internal readonly record struct NativeEdgeClickSettleResult(
    bool Settled,
    bool Changed,
    int MovementDistance,
    int StableFrames,
    int Samples,
    double ElapsedMilliseconds,
    string Reason,
    Point TargetPoint,
    string BeforeListHash,
    string AfterListHash,
    int TimeoutMilliseconds = NativeEdgeClickSettleTracker.InitialTimeoutMilliseconds,
    int? VerifiedRowsAdvanced = null,
    bool ReleaseConfirmed = false,
    string? TargetRarity = null);

// A slow game frame must not trigger a second click: it could advance another
// row. Keep observing the original click when movement has already begun.
internal sealed class NativeEdgeClickSettleTracker(int movementTolerance, int stabilityTolerance)
{
    internal const int InitialTimeoutMilliseconds = 800;
    internal const int MovingTimeoutMilliseconds = 3000;
    internal const int NoMoveDecisionMilliseconds = 300;
    internal const int RequiredStableFrames = 2;

    public bool SawMovement { get; private set; }
    public int MovementDistance { get; private set; }
    public int StableFrames { get; private set; }
    public int Samples { get; private set; }
    public bool Settled { get; private set; }
    public int ReleaseStableFrames { get; private set; }
    public int TimeoutMilliseconds => SawMovement ? MovingTimeoutMilliseconds : InitialTimeoutMilliseconds;

    public bool CanObserve(double elapsedMilliseconds) => !Settled && elapsedMilliseconds < TimeoutMilliseconds;

    public bool CanObserveRelease(double elapsedMilliseconds) => elapsedMilliseconds < MovingTimeoutMilliseconds;

    public bool ObserveRelease(bool verifiedStablePosition, double elapsedMilliseconds)
    {
        if (!CanObserveRelease(elapsedMilliseconds))
        {
            ReleaseStableFrames = 0;
            return false;
        }
        ReleaseStableFrames = verifiedStablePosition ? ReleaseStableFrames + 1 : 0;
        return ReleaseStableFrames >= RequiredStableFrames;
    }

    public bool Observe(int movementDistance, int frameDistance, double elapsedMilliseconds,
        bool independentMovement = false, bool allowSettlement = true)
    {
        Samples++;
        // Check the existing deadline before accepting a new sample. A capture
        // can finish after its deadline; that frame cannot establish success.
        if (!CanObserve(elapsedMilliseconds)) return false;
        MovementDistance = Math.Max(MovementDistance, movementDistance);
        SawMovement |= movementDistance > movementTolerance || independentMovement;
        StableFrames = frameDistance <= stabilityTolerance ? StableFrames + 1 : 0;
        Settled = allowSettlement && StableFrames >= RequiredStableFrames
            && (SawMovement || elapsedMilliseconds >= NoMoveDecisionMilliseconds);
        return Settled;
    }
}

internal static class NativeEdgeClickPolicy
{
    public static NativeEdgeClickDecision ResolveSettle(NativeEdgeClickSettleResult result)
    {
        if (!result.Settled || !result.ReleaseConfirmed)
        {
            return NativeEdgeClickDecision.Stop;
        }

        return result.VerifiedRowsAdvanced switch
        {
            1 => NativeEdgeClickDecision.AdvanceOne,
            0 => NativeEdgeClickDecision.NoMove,
            _ => NativeEdgeClickDecision.Stop
        };
    }
}

internal static class NativeEdgePositionPolicy
{
    public static RowAdvanceEvidence Resolve(RowAdvanceEvidence structural,
        bool beforeThumbFound, bool afterThumbFound, int thumbHeightDelta,
        int? pixelDelta, double pixelsPerRow)
    {
        if (!beforeThumbFound || !afterThumbFound || pixelDelta is null
            || !double.IsFinite(pixelsPerRow) || pixelsPerRow < 1)
            return structural with { Decision = RowAdvanceDecision.Ambiguous, Strong = false, Reason = "native_scrollbar_position_evidence_missing" };
        if (Math.Abs((long)thumbHeightDelta) > 1)
            return structural with { Decision = RowAdvanceDecision.Ambiguous, Strong = false, Reason = "scrollbar_thumb_geometry_changed" };
        return RowAdvanceEvaluator.ReconcileScrollbarPosition(structural, pixelDelta.Value, pixelsPerRow);
    }
}

// The edge click selects visual row 4 / column 1 before the list moves.  Once a
// verified one-row move settles, that same selected card is visual row 3 /
// column 1.  Keep that relationship explicit so the scanner never "arms" a
// different card merely to force a panel transition.
internal readonly record struct NativeEdgePostScrollSelection(
    int LogicalRow,
    int VisualRow,
    int Column,
    Point EdgeTargetPoint,
    string? RarityBeforeSelection = null)
{
    public bool Matches(int? logicalRow, int visualRow, int column) =>
        logicalRow == LogicalRow && visualRow == VisualRow && column == Column;

    // The selected yellow outline can look like an S-rank stripe. Reuse only
    // the same physical card's pre-click rarity after its one-row move was verified.
    public string? ResolveRarity(int? logicalRow, int visualRow, int column, string? observedRarity) =>
        Matches(logicalRow, visualRow, column) && RarityBeforeSelection is "S" or "A" or "B"
            ? RarityBeforeSelection
            : observedRarity;
}

internal enum NativeEdgePostScrollActionKind
{
    EdgeClick,
    CapturePreselected,
    Click
}

internal readonly record struct NativeEdgePostScrollAction(
    NativeEdgePostScrollActionKind Kind,
    int VisualRow,
    int Column);

internal static class NativeEdgePostScrollSelectionPolicy
{
    public static bool CanReuseSettledViewport(
        NativeEdgePostScrollSelection? selection, int visibleTopLogicalRow, int maxVisibleTop) =>
        visibleTopLogicalRow >= 2 && visibleTopLogicalRow <= maxVisibleTop
        && selection is { } target && target.Matches(visibleTopLogicalRow + 2, 3, 1);

    public static bool TryBind(
        NativeEdgeClickSettleResult edge,
        int visibleTopBefore,
        int maxVisibleTop,
        int columns,
        out NativeEdgePostScrollSelection selection)
    {
        selection = default;
        if (NativeEdgeClickPolicy.ResolveSettle(edge) != NativeEdgeClickDecision.AdvanceOne
            || visibleTopBefore >= maxVisibleTop
            || columns < 1
            || edge.TargetPoint == Point.Empty
            || string.IsNullOrWhiteSpace(edge.BeforeListHash)
            || string.IsNullOrWhiteSpace(edge.AfterListHash))
        {
            return false;
        }

        // New visual row 3 is logical (old top + 3) after the one-row shift.
        selection = new NativeEdgePostScrollSelection(
            LogicalRow: visibleTopBefore + 3,
            VisualRow: 3,
            Column: 1,
            EdgeTargetPoint: edge.TargetPoint,
            RarityBeforeSelection: edge.TargetRarity);
        return true;
    }

    public static IReadOnlyList<NativeEdgePostScrollAction> BuildSequence(int columns)
    {
        columns = Math.Max(1, columns);
        var actions = new List<NativeEdgePostScrollAction>
        {
            new(NativeEdgePostScrollActionKind.EdgeClick, 4, 1),
            new(NativeEdgePostScrollActionKind.CapturePreselected, 3, 1)
        };
        for (var column = 2; column <= columns; column++)
        {
            actions.Add(new NativeEdgePostScrollAction(NativeEdgePostScrollActionKind.Click, 3, column));
        }

        return actions;
    }
}

internal readonly record struct NativeEdgePhase(int Width, int Height, byte[] Colors);
internal readonly record struct NativeEdgePhaseDifference(int ChangedPermille, int MaximumDelta, bool Stable);

// Dense stability evidence is independent of row/item identity. Every pixel
// contributes to a 4x4 RGB tile; small animated texture noise is not scrolling.
// Only three bounded tile arrays are live, never a history of captured images.
internal static class NativeEdgePhaseProbe
{
    private const int TileSize = 4;
    private const int NoiseDelta = 8;
    private const int MaximumChangedPermille = 5;

    public static NativeEdgePhase Capture(Bitmap image, Rectangle rectangle)
    {
        var width = (rectangle.Width + TileSize - 1) / TileSize;
        var height = (rectangle.Height + TileSize - 1) / TileSize;
        var colors = new byte[checked(width * height * 3)];
        var data = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var sums = ArrayPool<int>.Shared.Rent(colors.Length);
        Array.Clear(sums, 0, colors.Length);
        var rowBytes = checked(rectangle.Width * 3);
        var buffer = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (var y = 0; y < rectangle.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, 0, rowBytes);
                for (var x = 0; x < rectangle.Width; x++)
                {
                    var tile = ((y / TileSize) * width + x / TileSize) * 3;
                    var pixel = x * 3;
                    sums[tile] += buffer[pixel];
                    sums[tile + 1] += buffer[pixel + 1];
                    sums[tile + 2] += buffer[pixel + 2];
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var count = Math.Min(TileSize, rectangle.Width - x * TileSize)
                        * Math.Min(TileSize, rectangle.Height - y * TileSize);
                    var tile = (y * width + x) * 3;
                    for (var channel = 0; channel < 3; channel++)
                        colors[tile + channel] = (byte)(sums[tile + channel] / count);
                }
            }
            return new NativeEdgePhase(width, height, colors);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<int>.Shared.Return(sums);
            image.UnlockBits(data);
        }
    }

    public static NativeEdgePhaseDifference Compare(NativeEdgePhase left, NativeEdgePhase right)
    {
        if (left.Width != right.Width || left.Height != right.Height
            || left.Colors.Length == 0 || left.Colors.Length != right.Colors.Length)
            return new NativeEdgePhaseDifference(1000, 255, false);
        var changed = 0;
        var maximum = 0;
        for (var index = 0; index < left.Colors.Length; index++)
        {
            var delta = Math.Abs(left.Colors[index] - right.Colors[index]);
            maximum = Math.Max(maximum, delta);
            if (delta > NoiseDelta) changed++;
        }
        var permille = (int)((long)changed * 1000 / left.Colors.Length);
        return new NativeEdgePhaseDifference(permille, maximum,
            (long)changed * 1000 <= (long)MaximumChangedPermille * left.Colors.Length);
    }
}

internal sealed class RowDetailFingerprintBuilder
{
    private readonly SortedDictionary<int, string> tokens = new();

    public int Count => tokens.Count;

    public void AddGridCell(int column, string rarity, ulong gridHash)
    {
        tokens[column] = $"grid:{column}:{rarity}:{gridHash:X16}";
    }

    public void AddDetailPanel(int column, string rarity, Bitmap image, IReadOnlyList<Rectangle> rois)
    {
        tokens[column] = $"panel:{column}:{rarity}:{DetailPanelFingerprint.Compute(image, rois)}";
    }

    public string Complete()
    {
        if (tokens.Count == 0)
        {
            return "";
        }

        var payload = string.Join('|', tokens.Select(pair => pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

internal sealed class BufferedRowCapture : IDisposable
{
    private readonly List<BufferedCellCapture> cells = [];

    public IReadOnlyList<BufferedCellCapture> Cells => cells;

    public void AddSkipped(int column, string rarity)
    {
        cells.Add(new BufferedCellCapture(column, rarity, null, [], TargetVerificationKind.ChangedText, null));
    }

    public void AddCapture(
        int column,
        string rarity,
        Bitmap image,
        Rectangle[] rois,
        TargetVerificationKind targetVerificationKind,
        LockCaptureEvidence? lockEvidence = null)
    {
        cells.Add(new BufferedCellCapture(column, rarity, image, rois, targetVerificationKind, lockEvidence));
    }

    public void Dispose()
    {
        foreach (var cell in cells)
        {
            cell.Dispose();
        }
    }
}

internal sealed class BufferedCellCapture : IDisposable
{
    private Bitmap? image;
    private LockCaptureEvidence? lockEvidence;

    public BufferedCellCapture(
        int column,
        string rarity,
        Bitmap? image,
        Rectangle[] rois,
        TargetVerificationKind targetVerificationKind,
        LockCaptureEvidence? lockEvidence)
    {
        Column = column;
        Rarity = rarity;
        this.image = image;
        Rois = rois;
        TargetVerificationKind = targetVerificationKind;
        this.lockEvidence = lockEvidence;
    }

    public int Column { get; }
    public string Rarity { get; }
    public bool HasCapture => image is not null;
    public bool ImageTransferred { get; private set; }
    public bool Disposed { get; private set; }
    public Rectangle[] Rois { get; }
    public TargetVerificationKind TargetVerificationKind { get; }

    public LockCaptureEvidence TakeLockEvidence()
    {
        var result = lockEvidence ?? new LockCaptureEvidence(
            null,
            LockStateEvidence.Combine(
                new LockStateEvidence.Sample("card-bottom-right", null, 0, "not_captured"),
                new LockStateEvidence.Sample("detail-lock-button", null, 0, "not_captured")));
        lockEvidence = null;
        return result;
    }

    public Bitmap TakeImage()
    {
        var result = image ?? throw new InvalidOperationException("Buffered cell image has already been transferred.");
        image = null;
        ImageTransferred = true;
        return result;
    }

    public void Dispose()
    {
        image?.Dispose();
        image = null;
        lockEvidence?.Dispose();
        lockEvidence = null;
        Disposed = true;
    }
}

internal static class DetailPanelFingerprint
{
    private const int HorizontalSamples = 16;
    private const int VerticalSamples = 8;

    public static string Compute(Bitmap image, IReadOnlyList<Rectangle> rois)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(rois);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[4];
        Span<byte> samples = stackalloc byte[HorizontalSamples * VerticalSamples];
        for (var roiIndex = 0; roiIndex < rois.Count; roiIndex++)
        {
            var roi = Rectangle.Intersect(rois[roiIndex], new Rectangle(0, 0, image.Width, image.Height));
            header[0] = (byte)Math.Min(byte.MaxValue, roiIndex);
            header[1] = (byte)Math.Min(byte.MaxValue, roi.Width);
            header[2] = (byte)Math.Min(byte.MaxValue, roi.Height);
            header[3] = (byte)(roi.IsEmpty ? 0 : 1);
            hash.AppendData(header);
            if (roi.IsEmpty)
            {
                continue;
            }

            samples.Clear();
            var sampleIndex = 0;
            for (var row = 0; row < VerticalSamples; row++)
            {
                var y = roi.Top + Math.Min(
                    roi.Height - 1,
                    (int)Math.Round((row + 0.5) * roi.Height / VerticalSamples, MidpointRounding.AwayFromZero));
                for (var column = 0; column < HorizontalSamples; column++)
                {
                    var x = roi.Left + Math.Min(
                        roi.Width - 1,
                        (int)Math.Round((column + 0.5) * roi.Width / HorizontalSamples, MidpointRounding.AwayFromZero));
                    var color = image.GetPixel(x, y);
                    var luminance = (54 * color.R + 183 * color.G + 19 * color.B) >> 8;
                    samples[sampleIndex++] = (byte)(luminance & 0xF0);
                }
            }

            hash.AppendData(samples);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
