using System.Drawing;

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
    string? TargetRarity = null,
    NativeEdgePositionDiagnostics? PositionDiagnostics = null);

// A slow game frame must not trigger a second click: it could advance another
// row. Keep observing the original click when movement has already begun.
internal sealed class NativeEdgeClickSettleTracker(int movementTolerance, int stabilityTolerance, bool isCloud = false)
{
    internal const int InitialTimeoutMilliseconds = 800;
    internal const int CloudInitialTimeoutMilliseconds = 1600;
    internal const int MovingTimeoutMilliseconds = 3000;
    internal const int NoMoveDecisionMilliseconds = 300;
    internal const int RequiredStableFrames = 2;

    public bool SawMovement { get; private set; }
    public int MovementDistance { get; private set; }
    public int StableFrames { get; private set; }
    public int Samples { get; private set; }
    public bool Settled { get; private set; }
    public int ReleaseStableFrames { get; private set; }
    // Cloud rendering can deliver its first post-click frame after 800ms.
    // Double the initial observation budget, leaving time for release proof
    // inside the existing 3000ms hard deadline. Proof gates stay unchanged.
    public int InitialObservationTimeoutMilliseconds => isCloud ? CloudInitialTimeoutMilliseconds : InitialTimeoutMilliseconds;
    public int TimeoutMilliseconds => SawMovement ? MovingTimeoutMilliseconds : InitialObservationTimeoutMilliseconds;

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

    public bool Observe(
        int movementDistance,
        int frameDistance,
        double elapsedMilliseconds,
        bool independentMovement = false,
        bool allowSettlement = true,
        bool independentlyStationary = false)
    {
        Samples++;
        // Check the existing deadline before accepting a new sample. A capture
        // can finish after its deadline; that frame cannot establish success.
        if (!CanObserve(elapsedMilliseconds)) return false;
        MovementDistance = Math.Max(MovementDistance, movementDistance);
        // A stationary witness can suppress this sample's decoration noise,
        // but cannot erase an earlier movement (including a return to baseline).
        SawMovement |= independentMovement
            || (!independentlyStationary && movementDistance > movementTolerance);
        StableFrames = frameDistance <= stabilityTolerance ? StableFrames + 1 : 0;
        Settled = allowSettlement && StableFrames >= RequiredStableFrames
            && (SawMovement || elapsedMilliseconds >= NoMoveDecisionMilliseconds);
        return Settled;
    }
}

internal static class NativeEdgeClickPolicy
{
    // This chooses an input mechanism only. It never proves a row advance.
    // The wider selection probe may cross the grid's horizontal padding; the
    // auto-scroll premise concerns the target card's vertical visibility.
    public static bool HasFullyVisibleTarget(Rectangle targetProbe, Rectangle viewport) =>
        targetProbe.Width > 0 && targetProbe.Height > 0 && viewport.Width > 0 && viewport.Height > 0
        && targetProbe.Top >= viewport.Top && targetProbe.Bottom <= viewport.Bottom
        && targetProbe.Left + targetProbe.Width / 2 >= viewport.Left
        && targetProbe.Left + targetProbe.Width / 2 < viewport.Right;

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
    public static RowAdvanceEvidence Resolve(
        RowAdvanceEvidence structural,
        ScanController.ScrollbarThumbProbe before,
        ScanController.ScrollbarThumbProbe after,
        double pixelsPerRow,
        bool leadingPhaseUnchangedFromBaseline = false)
    {
        var heightDelta = (before.EndY - before.StartY) - (after.EndY - after.StartY);
        var centerDelta = before.Found && after.Found ? after.CenterY - before.CenterY : (int?)null;
        if (leadingPhaseUnchangedFromBaseline
            && before.Found && after.Found
            && double.IsFinite(pixelsPerRow) && pixelsPerRow >= 1
            && heightDelta == 0
            && centerDelta == 0
            && structural.Decision == RowAdvanceDecision.NoMove
            && structural.Strong)
        {
            // Leading rows are outside the row-4 selection target. Alongside
            // strong structural NoMove and unchanged thumb geometry, their
            // baseline phase is a stationary witness for this sample. It must
            // not erase movement history or replace the final release gates.
            var secondScore = Math.Min(structural.OneRowScore, structural.TwoRowScore);
            return structural with
            {
                Decision = RowAdvanceDecision.NoMove,
                BestRows = 0,
                BestScore = structural.NoMoveScore,
                SecondScore = secondScore,
                Margin = structural.NoMoveScore == int.MaxValue || secondScore == int.MaxValue
                    ? 0
                    : Math.Max(0, secondScore - structural.NoMoveScore),
                Strong = true,
                Reason = "scrollbar_and_leading_phase_no_move"
            };
        }

        var strict = Resolve(structural, before.Found, after.Found, heightDelta, centerDelta, pixelsPerRow);
        if (strict.Reason != "scrollbar_thumb_geometry_changed" || Math.Abs((long)heightDelta) != 2
            || before.EndY < before.StartY || after.EndY < after.StartY)
            return strict;

        // Both live cloud failures had endpoint travel differing by two pixels,
        // making the measured thumb height differ by two pixels. A center-only
        // proof cannot distinguish that from a resized or clipped thumb. Keep
        // the original strict gate unless BOTH endpoints independently prove
        // the same single-row move using the unchanged alignment/structure gates.
        var start = RowAdvanceEvaluator.ReconcileScrollbarPosition(structural, after.StartY - before.StartY, pixelsPerRow);
        var end = RowAdvanceEvaluator.ReconcileScrollbarPosition(structural, after.EndY - before.EndY, pixelsPerRow);
        var center = RowAdvanceEvaluator.ReconcileScrollbarPosition(structural, centerDelta!.Value, pixelsPerRow);
        // At high density, endpoint quantization can disagree on one vs two
        // rows even though the center and the independent grid both prove one.
        // This is confined to the same exact two-pixel geometry change; neither
        // pixel thresholds nor release/bottom-of-list gates are widened.
        if (structural.Strong && structural.Decision == RowAdvanceDecision.OneRow
            && !structural.UsesScrollbarPosition && structural.Reason == "one_row_structurally_confirmed"
            && after.StartY >= before.StartY && after.EndY >= before.EndY
            && center.Strong && center.Decision == RowAdvanceDecision.OneRow)
            return center;
        return start.Strong && start.Decision == RowAdvanceDecision.OneRow
            && end.Strong && end.Decision == RowAdvanceDecision.OneRow
            && center.Strong && center.Decision == RowAdvanceDecision.OneRow
            ? center
            : strict;
    }

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
    // The configured mode is not evidence that an edge click occurred: a fully
    // visible edge card uses the ordinary verified wheel path without a click.
    public static bool RequiresNeighborRefresh(
        bool recoveredStationaryEdgeClick, bool firstCellAfterScroll, bool preselected) =>
        recoveredStationaryEdgeClick && firstCellAfterScroll && !preselected;

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
