using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

internal static class RowScrollCoordinator
{
    public const int MaximumSamplesPerTick = 3;
    public const int MaximumSettleMilliseconds = 500;
    public const int StableFrameTolerance = 4;
    public const int MinimumPartialMovementDistance = 12;

    public static async Task<RowScrollCoordinatorResult> RunAsync(
        int maximumTicks,
        ScrollAcceptMode acceptMode,
        int settlePollMilliseconds,
        Func<int, CancellationToken, Task> issueWheelAsync,
        Func<int, int, RowScrollPhase, CancellationToken, Task<RowScrollObservation>> captureObservationAsync,
        Action<RowScrollCoordinatorTrace>? trace,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(issueWheelAsync);
        ArgumentNullException.ThrowIfNull(captureObservationAsync);
        maximumTicks = Math.Clamp(maximumTicks, 1, 2);
        settlePollMilliseconds = Math.Max(1, settlePollMilliseconds);
        var maximumSettleSamples = Math.Max(1, (int)Math.Ceiling(MaximumSettleMilliseconds / (double)settlePollMilliseconds));
        RowScrollObservation lastObservation = default;
        var totalSettleSamples = 0;
        var settleWatch = new System.Diagnostics.Stopwatch();

        for (var tick = 1; tick <= maximumTicks; tick++)
        {
            token.ThrowIfCancellationRequested();
            await issueWheelAsync(tick, token);
            RowAdvanceDecision? consecutiveDecision = null;
            var consecutiveFrames = 0;
            var enterSettle = false;

            for (var sample = 1; sample <= MaximumSamplesPerTick; sample++)
            {
                token.ThrowIfCancellationRequested();
                lastObservation = await captureObservationAsync(tick, sample, RowScrollPhase.FastProbe, token);
                var evidence = lastObservation.Evidence;

                if (IsConfirmedOvershot(lastObservation))
                {
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        sample,
                        RowScrollPhase.FastProbe,
                        lastObservation,
                        "accept_overshot_for_block"));
                    return Result(RowScrollCoordinatorOutcome.Accepted, tick, sample, RowScrollPhase.FastProbe, lastObservation);
                }

                if (evidence.Decision == RowAdvanceDecision.NoMove)
                {
                    enterSettle = true;
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        sample,
                        RowScrollPhase.FastProbe,
                        lastObservation,
                        lastObservation.Stable
                            ? "enter_settle_no_move"
                            : "enter_settle_unstable_no_move"));
                    break;
                }

                if (evidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    ObservePositive(lastObservation, ref consecutiveDecision, ref consecutiveFrames);
                    if (CanAccept(lastObservation, consecutiveFrames, acceptMode))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(tick, sample, RowScrollPhase.FastProbe, lastObservation, "accept"));
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, sample, RowScrollPhase.FastProbe, lastObservation);
                    }
                }
                else
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    enterSettle = true;
                }

                trace?.Invoke(new RowScrollCoordinatorTrace(
                    tick,
                    sample,
                    RowScrollPhase.FastProbe,
                    lastObservation,
                    enterSettle || sample == MaximumSamplesPerTick ? "enter_settle" : "resample"));
                if (enterSettle || sample == MaximumSamplesPerTick)
                {
                    enterSettle = true;
                    break;
                }
            }

            if (!enterSettle)
            {
                continue;
            }

            consecutiveDecision = null;
            consecutiveFrames = 0;
            var consecutiveStableNoMove = 0;
            settleWatch.Start();
            var settlePhaseWatch = System.Diagnostics.Stopwatch.StartNew();
            for (var settleSample = 1; settleSample <= maximumSettleSamples; settleSample++)
            {
                token.ThrowIfCancellationRequested();
                totalSettleSamples++;
                lastObservation = await captureObservationAsync(tick, settleSample, RowScrollPhase.Settle, token);
                var evidence = lastObservation.Evidence;

                if (evidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    consecutiveStableNoMove = 0;
                    if (IsConfirmedOvershot(lastObservation))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(
                            tick,
                            settleSample,
                            RowScrollPhase.Settle,
                            lastObservation,
                            "accept_overshot_for_block"));
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }

                    ObservePositive(lastObservation, ref consecutiveDecision, ref consecutiveFrames);
                    if (CanAccept(lastObservation, consecutiveFrames, acceptMode))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(tick, settleSample, RowScrollPhase.Settle, lastObservation, "accept_settled"));
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }
                }
                else if (lastObservation.ConfirmedZeroMovement)
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    consecutiveStableNoMove++;
                }
                else
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    consecutiveStableNoMove = 0;
                }

                var settleTimedOut = settleSample == maximumSettleSamples
                    || settlePhaseWatch.ElapsedMilliseconds >= MaximumSettleMilliseconds;
                if (!settleTimedOut)
                {
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        settleSample,
                        RowScrollPhase.Settle,
                        lastObservation,
                        "settle_resample"));
                    continue;
                }

                if (consecutiveStableNoMove >= 3 && lastObservation.ConfirmedZeroMovement)
                {
                    var action = tick < maximumTicks
                        ? "retry_after_full_no_move_window"
                        : "stop_no_movement";
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        settleSample,
                        RowScrollPhase.Settle,
                        lastObservation,
                        action));
                    if (tick == maximumTicks)
                    {
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.NoMovementStop, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }

                    break;
                }

                trace?.Invoke(new RowScrollCoordinatorTrace(
                    tick,
                    settleSample,
                    RowScrollPhase.Settle,
                    lastObservation,
                    "stop_ambiguous"));
                {
                    var ambiguous = evidence with
                    {
                        Decision = RowAdvanceDecision.Ambiguous,
                        Reason = evidence.Decision == RowAdvanceDecision.Ambiguous
                            ? evidence.Reason
                            : "insufficient_stable_confirmation"
                    };
                    lastObservation = lastObservation with { Evidence = ambiguous };
                    settleWatch.Stop();
                    return Result(RowScrollCoordinatorOutcome.AmbiguousStop, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                }
            }
            settleWatch.Stop();
        }

        return Result(RowScrollCoordinatorOutcome.NoMovementStop, maximumTicks, 1, RowScrollPhase.FastProbe, lastObservation);

        void ObservePositive(
            RowScrollObservation observation,
            ref RowAdvanceDecision? decision,
            ref int frames)
        {
            if (!observation.Stable)
            {
                decision = null;
                frames = 0;
                return;
            }

            if (decision == observation.Evidence.Decision)
            {
                frames++;
            }
            else
            {
                decision = observation.Evidence.Decision;
                frames = 1;
            }
        }

        static bool CanAccept(
            RowScrollObservation observation,
            int consecutiveFrames,
            ScrollAcceptMode mode)
        {
            if (!observation.Stable)
            {
                return false;
            }

            var evidence = observation.Evidence;
            var earlyStrongOneRow = mode == ScrollAcceptMode.EarlyOneRow
                && evidence.Decision == RowAdvanceDecision.OneRow
                && evidence.Strong
                && !evidence.UsesScrollbarPosition;
            return earlyStrongOneRow || consecutiveFrames >= 2;
        }

        static bool IsConfirmedOvershot(RowScrollObservation observation) =>
            observation.Evidence.Decision == RowAdvanceDecision.TwoRows
            && (observation.Stable
                || string.Equals(
                    observation.Evidence.Reason,
                    "scrollbar_overshot_above_single_row_limit",
                    StringComparison.Ordinal));

        RowScrollCoordinatorResult Result(
            RowScrollCoordinatorOutcome outcome,
            int tick,
            int sample,
            RowScrollPhase phase,
            RowScrollObservation observation) => new(
                outcome,
                tick,
                sample,
                phase,
                observation,
                totalSettleSamples,
                settleWatch.Elapsed.TotalMilliseconds);
    }
}

public readonly record struct FirstPairWitnessCoordinate(int VisualRow, int Column);

public static class FirstPairWitnessPlanner
{
    public static IReadOnlyList<FirstPairWitnessCoordinate> Build(
        int firstVisualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns,
        int visibleRows,
        int visibleColumns)
    {
        maxColumns = Math.Max(1, maxColumns);
        visibleRows = Math.Max(1, visibleRows);
        visibleColumns = Math.Max(1, visibleColumns);
        var candidates = new List<FirstPairWitnessCoordinate>();

        for (var column = secondColumn + 1; column <= maxColumns; column++)
        {
            candidates.Add(new FirstPairWitnessCoordinate(firstVisualRow, column));
        }

        if (firstVisualRow == 1)
        {
            for (var visualRow = 2; visualRow <= visibleRows; visualRow++)
            {
                for (var column = 1; column <= visibleColumns; column++)
                {
                    candidates.Add(new FirstPairWitnessCoordinate(visualRow, column));
                }
            }
        }

        return candidates
            .Where(candidate => candidate.VisualRow != firstVisualRow
                || (candidate.Column != firstColumn && candidate.Column != secondColumn))
            .Distinct()
            .ToArray();
    }
}

public readonly record struct SelectionRefreshObservation(
    bool ChangedFromTarget,
    bool StableWithPreviousFrame);

public sealed record SelectionRefreshWaitResult(
    bool Ready,
    bool ChangedFromTarget,
    int StableFrames,
    int FrameCount,
    double ElapsedMilliseconds);

public readonly record struct PanelCaptureInitialGate(
    bool SawPanelChange,
    bool SelectionChanged,
    double? ChangeMilliseconds);

public static class PanelCaptureGate
{
    public static PanelCaptureInitialGate Initialize(bool hasPanelBaseline) =>
        new(
            SawPanelChange: false,
            SelectionChanged: false,
            ChangeMilliseconds: null);

    public static bool RequiresFirstCellNeighborRoundTrip(bool firstQueuedItem) => firstQueuedItem;

    public static bool RequiresFinalFrameRefresh(
        bool hasPanelBaseline,
        int finalChangeDistance,
        int changeTolerance,
        bool roundTripVerified,
        bool preselectedTargetVerified) =>
        hasPanelBaseline && finalChangeDistance <= changeTolerance
        && !roundTripVerified && !preselectedTargetVerified;

    public static bool IsStrongChangeCurrentFrame(
        int changeDistance,
        double elapsedMilliseconds,
        int strongChangeTolerance,
        double minimumReliableChangeMilliseconds) =>
        changeDistance > strongChangeTolerance
        && elapsedMilliseconds >= minimumReliableChangeMilliseconds;

    public static bool LatchStrongChange(
        bool alreadyChanged,
        int changeDistance,
        double elapsedMilliseconds,
        int strongChangeTolerance,
        double minimumReliableChangeMilliseconds) =>
        alreadyChanged || IsStrongChangeCurrentFrame(
            changeDistance,
            elapsedMilliseconds,
            strongChangeTolerance,
            minimumReliableChangeMilliseconds);
}

public static class SelectionRefreshWaiter
{
    public static async Task<SelectionRefreshWaitResult> WaitAsync(
        Func<SelectionRefreshObservation> observe,
        int maximumWaitMilliseconds,
        int pollMilliseconds,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observe);
        maximumWaitMilliseconds = Math.Max(1, maximumWaitMilliseconds);
        pollMilliseconds = Math.Max(1, pollMilliseconds);
        var wait = Stopwatch.StartNew();
        var gate = new SelectionRefreshGate(requiredStableFrames: 2);
        var frameCount = 0;
        while (wait.ElapsedMilliseconds < maximumWaitMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            var observation = observe();
            frameCount++;
            if (gate.Observe(observation.ChangedFromTarget, observation.StableWithPreviousFrame))
            {
                return new SelectionRefreshWaitResult(
                    true,
                    gate.ChangedFromTarget,
                    gate.StableFrames,
                    frameCount,
                    wait.Elapsed.TotalMilliseconds);
            }

            var remainingMilliseconds = maximumWaitMilliseconds - (int)wait.ElapsedMilliseconds;
            if (remainingMilliseconds <= 0)
            {
                break;
            }

            await Task.Delay(Math.Min(pollMilliseconds, remainingMilliseconds), token);
        }

        return new SelectionRefreshWaitResult(
            false,
            gate.ChangedFromTarget,
            gate.StableFrames,
            frameCount,
            wait.Elapsed.TotalMilliseconds);
    }
}
