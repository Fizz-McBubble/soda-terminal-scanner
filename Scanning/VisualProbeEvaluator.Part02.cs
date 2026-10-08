using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

internal static class RowAdvanceEvaluator
{
    public const int MatchTolerance = 48;
    public const int ClearMargin = 12;
    public const int StrongMargin = 18;
    public const int MovementTolerance = 6;

    public static RowAdvanceEvidence ReconcileScrollbarPosition(
        RowAdvanceEvidence structuralEvidence,
        int directionalPixelDelta,
        double pixelsPerRow)
    {
        if (pixelsPerRow < 1)
        {
            return structuralEvidence;
        }

        var alignmentTolerance = Math.Max(1.5, pixelsPerRow * 0.35);
        if (directionalPixelDelta < -alignmentTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_moved_opposite_direction"
            };
        }

        var estimatedRows = Math.Max(0, (int)Math.Round(
            Math.Max(0, directionalPixelDelta) / pixelsPerRow,
            MidpointRounding.AwayFromZero));
        var residual = Math.Abs(directionalPixelDelta - estimatedRows * pixelsPerRow);
        if (residual > alignmentTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_position_between_rows"
            };
        }

        if (estimatedRows > 2)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                BestRows = estimatedRows,
                Strong = false,
                Reason = "scrollbar_advanced_more_than_two_rows"
            };
        }

        var scrollbarDecision = estimatedRows switch
        {
            0 => RowAdvanceDecision.NoMove,
            1 => RowAdvanceDecision.OneRow,
            2 => RowAdvanceDecision.TwoRows,
            _ => RowAdvanceDecision.Ambiguous
        };
        if (estimatedRows == 0
            && structuralEvidence.MovedDistance > MovementTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_grid_movement_conflict"
            };
        }

        var hasPositiveStructureConflict = estimatedRows > 0
            && structuralEvidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows
            && structuralEvidence.Decision != scrollbarDecision;
        if (hasPositiveStructureConflict
            && structuralEvidence.Decision == RowAdvanceDecision.TwoRows
            && structuralEvidence.Strong)
        {
            return structuralEvidence;
        }

        if (hasPositiveStructureConflict && structuralEvidence.Strong)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_row_structure_conflict"
            };
        }

        var selectedScore = estimatedRows switch
        {
            0 => structuralEvidence.NoMoveScore,
            1 => structuralEvidence.OneRowScore,
            2 => structuralEvidence.TwoRowScore,
            _ => structuralEvidence.BestScore
        };
        var secondScore = estimatedRows switch
        {
            0 => Math.Min(structuralEvidence.OneRowScore, structuralEvidence.TwoRowScore),
            1 => Math.Min(structuralEvidence.NoMoveScore, structuralEvidence.TwoRowScore),
            2 => Math.Min(structuralEvidence.NoMoveScore, structuralEvidence.OneRowScore),
            _ => structuralEvidence.SecondScore
        };
        return structuralEvidence with
        {
            Decision = scrollbarDecision,
            BestRows = estimatedRows,
            BestScore = selectedScore,
            SecondScore = secondScore,
            Margin = secondScore - selectedScore,
            Strong = residual <= Math.Max(1, pixelsPerRow * 0.25),
            Reason = estimatedRows == 0
                ? "scrollbar_no_move_confirmed"
                : hasPositiveStructureConflict
                    ? $"scrollbar_{estimatedRows}_row_position_confirmed_weak_structure_override"
                    : $"scrollbar_{estimatedRows}_row_position_confirmed"
        };
    }

    public static RowAdvanceEvidence Evaluate(
        int noMoveScore,
        int oneRowScore,
        int twoRowScore,
        int movedDistance)
    {
        var ordered = new[]
        {
            (Rows: 0, Score: noMoveScore),
            (Rows: 1, Score: oneRowScore),
            (Rows: 2, Score: twoRowScore)
        }
        .OrderBy(candidate => candidate.Score)
        .ThenBy(candidate => candidate.Rows)
        .ToArray();
        var best = ordered[0];
        var secondScore = ordered[1].Score;
        var margin = secondScore == int.MaxValue || best.Score == int.MaxValue
            ? 0
            : Math.Max(0, secondScore - best.Score);
        var strong = best.Score <= MatchTolerance && margin >= StrongMargin;

        if (best.Score > MatchTolerance)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "no_structural_match");
        }

        if (margin < ClearMargin)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "insufficient_margin");
        }

        if (best.Rows == 0)
        {
            return Evidence(RowAdvanceDecision.NoMove, "no_move_structurally_confirmed");
        }

        if (movedDistance <= MovementTolerance)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "positive_shift_without_grid_movement");
        }

        return Evidence(
            best.Rows == 1 ? RowAdvanceDecision.OneRow : RowAdvanceDecision.TwoRows,
            best.Rows == 1 ? "one_row_structurally_confirmed" : "two_rows_structurally_confirmed");

        RowAdvanceEvidence Evidence(RowAdvanceDecision decision, string reason) => new(
            decision,
            best.Rows,
            best.Score,
            secondScore,
            margin,
            strong,
            noMoveScore,
            oneRowScore,
            twoRowScore,
            movedDistance,
            reason);
    }
}

internal enum RowScrollCoordinatorOutcome
{
    Accepted,
    AmbiguousStop,
    NoMovementStop
}

internal enum RowScrollPhase
{
    FastProbe,
    Settle
}

internal readonly record struct RowScrollObservation(
    RowAdvanceEvidence Evidence,
    int FrameDistance,
    int? ScrollbarPixelDelta = null)
{
    public bool Stable => FrameDistance <= RowScrollCoordinator.StableFrameTolerance;
    public bool ConfirmedZeroMovement => Stable
        && Evidence.Decision == RowAdvanceDecision.NoMove
        && Evidence.MovedDistance <= RowAdvanceEvaluator.MovementTolerance
        && ScrollbarPixelDelta == 0;
}

internal readonly record struct RowScrollCoordinatorTrace(
    int Tick,
    int Sample,
    RowScrollPhase Phase,
    RowScrollObservation Observation,
    string Action);

internal readonly record struct RowScrollCoordinatorResult(
    RowScrollCoordinatorOutcome Outcome,
    int Tick,
    int Sample,
    RowScrollPhase Phase,
    RowScrollObservation Observation,
    int SettleSamples,
    double SettleElapsedMilliseconds)
{
    public bool Accepted => Outcome == RowScrollCoordinatorOutcome.Accepted;
    public RowAdvanceEvidence Evidence => Observation.Evidence;
}

internal static class RowScrollReleasePolicy
{
    public static bool TryCreateSettleTimeoutProvisional(
        RowScrollCoordinatorResult result,
        out RowAdvanceEvidence provisionalEvidence)
    {
        provisionalEvidence = result.Evidence;
        if (result.Outcome != RowScrollCoordinatorOutcome.AmbiguousStop
            || !result.Evidence.Strong
            || !string.Equals(
                result.Evidence.Reason,
                "insufficient_stable_confirmation",
                StringComparison.Ordinal)
            || result.Evidence.BestRows is not (1 or 2))
        {
            return false;
        }

        provisionalEvidence = result.Evidence with
        {
            Decision = result.Evidence.BestRows == 1
                ? RowAdvanceDecision.OneRow
                : RowAdvanceDecision.TwoRows,
            Reason = "settle_timeout_strong_positive_release_pending"
        };
        return true;
    }
}
