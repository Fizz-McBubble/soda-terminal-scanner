using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using CvRect = System.Drawing.Rectangle;
using OcrBatchInput = ZZZScannerNext.Ocr.PaddleOcrRecognizer.OcrBatchInput;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static async Task<ScrollRowsResult> ScrollVerifiedRowShiftAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        bool upward,
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        // The game consumes standard wheel detents reliably. Fractional -30 pulses can be
        // animated without being committed by the list after the 2026-07 game update.
        var configuredDelta = profile.ScrollWheelDelta != 0 ? profile.ScrollWheelDelta : profile.ScrollTickDelta;
        var delta = upward ? -configuredDelta : configuredDelta;
        var targetTop = upward ? visibleTopLogicalRow - 1 : visibleTopLogicalRow + 1;
        var direction = upward ? "up" : "down";
        var startEvent = upward ? "ROW_SCROLL_RECOVERY_START" : "ROW_SCROLL_START";
        var tickEvent = upward ? "ROW_SCROLL_RECOVERY_TICK" : "ROW_SCROLL_TICK";
        var doneEvent = upward ? "ROW_SCROLL_RECOVERED" : "ROW_SCROLL_DONE";
        var maxTicks = Math.Min(VerifiedScrollTransactionLimit, ScrollSmallTickLimit(profile));
        var wheelPoint = window.ToScreenPoint(profile.Point("listWheelArea"));
        var neutralPoint = ListNeutralPoint(window, listGridRect);
        var effectiveScrollTickDelayMs = EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs);
        var pollMilliseconds = Math.Max(10, profile.LoadPollMs);
        window.MoveCursor(neutralPoint);
        var beforeViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var beforeGrid = beforeViewport.Grid;
        var beforeRows = beforeViewport.Rows;
        var beforeScrollbar = CaptureScrollbarThumbProbe(window, profile);
        var scrollbarPixelsPerRow = ScrollbarPixelsPerLogicalRow(window, profile, beforeScrollbar, maxVisibleTop);
        var latestViewport = beforeViewport;
        var previousObservedGrid = beforeGrid;
        var totalWatch = Stopwatch.StartNew();
        var wheelWaitMilliseconds = 0.0;
        var signatureMilliseconds = 0.0;
        var settleStartedTicks = new HashSet<int>();
        scanLog.WriteEvent(startEvent, $"direction={direction}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, maxVisibleTop={maxVisibleTop}, delta={delta}, inputMode=standard_detent_hold, detentsPerTick={VerifiedScrollDetentBurstCount}, detentIntervalMs={VerifiedScrollDetentBurstIntervalMs}, hoverSettleMs={VerifiedScrollHoverSettleMilliseconds}, releaseSamples={VerifiedScrollReleaseSamples}, configuredScrollWheelDelta={profile.ScrollWheelDelta}, configuredScrollTickDelta={profile.ScrollTickDelta}, maxTicks={maxTicks}, samplesPerTick={RowScrollCoordinator.MaximumSamplesPerTick}, scrollTickDelayMs={effectiveScrollTickDelayMs}, wheelPoint={wheelPoint}, neutralPoint={neutralPoint}");

        var result = await RowScrollCoordinator.RunAsync(
            maxTicks,
            scrollAcceptMode,
            pollMilliseconds,
            async (tick, cancellationToken) =>
            {
                scanLog.WriteEvent(tickEvent, $"direction={direction}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, tick={tick}/{maxTicks}, delta={delta}, detents={VerifiedScrollDetentBurstCount}, intervalMs={VerifiedScrollDetentBurstIntervalMs}");
                window.MoveCursor(wheelPoint);
                await Task.Delay(VerifiedScrollHoverSettleMilliseconds, cancellationToken);
                for (var detent = 1; detent <= VerifiedScrollDetentBurstCount; detent++)
                {
                    window.MouseWheel(delta);
                    if (detent < VerifiedScrollDetentBurstCount)
                    {
                        await Task.Delay(VerifiedScrollDetentBurstIntervalMs, cancellationToken);
                    }
                }

                // Keep the cursor over the list while the game consumes the wheel gesture.
                // Moving it away immediately can leave the new smooth-scrolling list below
                // its snap threshold and make it spring back to the previous row.
                var wait = Stopwatch.StartNew();
                await Task.Delay(effectiveScrollTickDelayMs, cancellationToken);
                wait.Stop();
                wheelWaitMilliseconds += VerifiedScrollHoverSettleMilliseconds
                    + wait.Elapsed.TotalMilliseconds
                    + Math.Max(0, VerifiedScrollDetentBurstCount - 1) * VerifiedScrollDetentBurstIntervalMs;
            },
            async (tick, sample, phase, cancellationToken) =>
            {
                if (phase == RowScrollPhase.Settle || sample > 1)
                {
                    await Task.Delay(pollMilliseconds, cancellationToken);
                }

                var signatureWatch = Stopwatch.StartNew();
                latestViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
                signatureWatch.Stop();
                signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
                var movedDistance = SignatureDistance(beforeGrid, latestViewport.Grid);
                var frameDistance = SignatureDistance(previousObservedGrid, latestViewport.Grid);
                previousObservedGrid = latestViewport.Grid;
                var verification = upward
                    ? VerifyOneRowUp(beforeRows, latestViewport.Rows)
                    : VerifyOneRowDown(beforeRows, latestViewport.Rows);
                var structuralEvidence = RowAdvanceEvaluator.Evaluate(
                    verification.NoMoveScore,
                    verification.OneRowScore,
                    verification.TwoRowScore,
                    movedDistance);
                var structuralObservation = new RowScrollObservation(structuralEvidence, frameDistance);
                var scrollbar = structuralObservation.Stable || sample == 1
                    ? CaptureScrollbarThumbProbe(window, profile)
                    : default;
                var directionalScrollbarDelta = DirectionalScrollbarDelta(beforeScrollbar, scrollbar, upward);
                var evidence = directionalScrollbarDelta is int pixelDelta
                    ? RowAdvanceEvaluator.ReconcileScrollbarPosition(structuralEvidence, pixelDelta, scrollbarPixelsPerRow)
                    : structuralEvidence;
                var estimatedScrollbarRows = directionalScrollbarDelta is int overshotPixelDelta && scrollbarPixelsPerRow >= 1
                    ? Math.Max(0, overshotPixelDelta) / scrollbarPixelsPerRow
                    : (double?)null;
                if (estimatedScrollbarRows > 1.5)
                {
                    evidence = evidence with
                    {
                        Decision = RowAdvanceDecision.TwoRows,
                        BestRows = Math.Max(2, (int)Math.Round(estimatedScrollbarRows.Value, MidpointRounding.AwayFromZero)),
                        Strong = true,
                        Reason = "scrollbar_overshot_above_single_row_limit"
                    };
                }

                var observation = new RowScrollObservation(evidence, frameDistance, directionalScrollbarDelta);
                var sampleLimit = phase == RowScrollPhase.FastProbe
                    ? RowScrollCoordinator.MaximumSamplesPerTick.ToString()
                    : "settle";
                scanLog.WriteEvent("ROW_SCROLL_EVIDENCE", $"direction={direction}, phase={phase}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={tick}/{maxTicks}, sample={sample}/{sampleLimit}, decision={evidence.Decision}, bestRows={evidence.BestRows}, bestScore={evidence.BestScore}, secondScore={evidence.SecondScore}, margin={evidence.Margin}, strong={evidence.Strong}, movedDistance={evidence.MovedDistance}, frameDistance={frameDistance}, stable={observation.Stable}, scrollbarFound={scrollbar.Found}, scrollbarCenter={scrollbar.CenterY}, scrollbarDelta={ScrollbarDelta(beforeScrollbar, scrollbar)}, directionalScrollbarDelta={directionalScrollbarDelta}, scrollbarPixelsPerRow={scrollbarPixelsPerRow:F3}, scrollbarRun={scrollbar.StartY}-{scrollbar.EndY}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
                return observation;
            },
            trace =>
            {
                var traceEvidence = trace.Observation.Evidence;
                if (trace.Action.StartsWith("enter_settle", StringComparison.Ordinal) && settleStartedTicks.Add(trace.Tick))
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_START", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, triggerDecision={traceEvidence.Decision}, triggerAction={trace.Action}, frameDistance={trace.Observation.FrameDistance}, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, pollMs={pollMilliseconds}, reason={traceEvidence.Reason}");
                }

                if (trace.Action == "accept_settled")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_ACCEPT", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, reason={traceEvidence.Reason}");
                }
                else if (trace.Action == "stop_ambiguous")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_TIMEOUT", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, reason={traceEvidence.Reason}");
                }
                else if (trace.Action == "retry_after_full_no_move_window")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SECOND_TRANSACTION_ALLOWED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, scrollbarDelta={trace.Observation.ScrollbarPixelDelta}, stableNoMoveFrames=3, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, reason={traceEvidence.Reason}");
                }

                if (traceEvidence.Decision == RowAdvanceDecision.NoMove)
                {
                    scanLog.WriteEvent("ROW_SCROLL_NO_MOVE_CONFIRMED", $"direction={direction}, phase={trace.Phase}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, action={trace.Action}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, reason={traceEvidence.Reason}");
                }
            },
            token);

        var settleTimeoutProvisional = RowScrollReleasePolicy.TryCreateSettleTimeoutProvisional(
            result,
            out var provisionalEvidence);
        var provisionalAccepted = result.Accepted || settleTimeoutProvisional;
        if (settleTimeoutProvisional)
        {
            scanLog.WriteEvent("ROW_SCROLL_SETTLE_RELEASE_VERIFY", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, provisionalDecision={provisionalEvidence.Decision}, bestRows={provisionalEvidence.BestRows}, bestScore={provisionalEvidence.BestScore}, secondScore={provisionalEvidence.SecondScore}, margin={provisionalEvidence.Margin}, movedDistance={provisionalEvidence.MovedDistance}, frameDistance={result.Observation.FrameDistance}, reason={provisionalEvidence.Reason}");
        }

        var finalObservation = result.Observation;
        var releaseConfirmed = false;
        window.MoveCursor(neutralPoint);
        if (provisionalAccepted)
        {
            RowAdvanceDecision? releaseDecision = null;
            var releaseStableFrames = 0;
            var previousReleaseGrid = latestViewport.Grid;
            for (var releaseSample = 1; releaseSample <= VerifiedScrollReleaseSamples; releaseSample++)
            {
                await Task.Delay(pollMilliseconds, token);
                var signatureWatch = Stopwatch.StartNew();
                var releasedViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
                signatureWatch.Stop();
                signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
                var movedDistance = SignatureDistance(beforeGrid, releasedViewport.Grid);
                var frameDistance = SignatureDistance(previousReleaseGrid, releasedViewport.Grid);
                previousReleaseGrid = releasedViewport.Grid;
                latestViewport = releasedViewport;
                var verification = upward
                    ? VerifyOneRowUp(beforeRows, releasedViewport.Rows)
                    : VerifyOneRowDown(beforeRows, releasedViewport.Rows);
                var releaseStructuralEvidence = RowAdvanceEvaluator.Evaluate(
                    verification.NoMoveScore,
                    verification.OneRowScore,
                    verification.TwoRowScore,
                    movedDistance);
                var releaseScrollbar = CaptureScrollbarThumbProbe(window, profile);
                var releaseScrollbarDelta = DirectionalScrollbarDelta(beforeScrollbar, releaseScrollbar, upward);
                var releaseEvidence = releaseScrollbarDelta is int releasePixelDelta
                    ? RowAdvanceEvaluator.ReconcileScrollbarPosition(releaseStructuralEvidence, releasePixelDelta, scrollbarPixelsPerRow)
                    : releaseStructuralEvidence;
                finalObservation = new RowScrollObservation(releaseEvidence, frameDistance, releaseScrollbarDelta);

                if (finalObservation.Stable
                    && releaseEvidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    if (releaseDecision == releaseEvidence.Decision)
                    {
                        releaseStableFrames++;
                    }
                    else
                    {
                        releaseDecision = releaseEvidence.Decision;
                        releaseStableFrames = 1;
                    }
                }
                else
                {
                    releaseDecision = null;
                    releaseStableFrames = 0;
                }

                scanLog.WriteEvent("ROW_SCROLL_RELEASE_EVIDENCE", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, sample={releaseSample}/{VerifiedScrollReleaseSamples}, provisionalDecision={provisionalEvidence.Decision}, decision={releaseEvidence.Decision}, bestRows={releaseEvidence.BestRows}, bestScore={releaseEvidence.BestScore}, secondScore={releaseEvidence.SecondScore}, margin={releaseEvidence.Margin}, movedDistance={releaseEvidence.MovedDistance}, frameDistance={frameDistance}, stable={finalObservation.Stable}, stableMatches={releaseStableFrames}/{VerifiedScrollReleaseStableFrames}, scrollbarFound={releaseScrollbar.Found}, scrollbarCenter={releaseScrollbar.CenterY}, directionalScrollbarDelta={releaseScrollbarDelta}, scrollbarPixelsPerRow={scrollbarPixelsPerRow:F3}, scores=0:{releaseEvidence.NoMoveScore}|1:{releaseEvidence.OneRowScore}|2:{releaseEvidence.TwoRowScore}, reason={releaseEvidence.Reason}");
                if (releaseStableFrames >= VerifiedScrollReleaseStableFrames)
                {
                    releaseConfirmed = true;
                    break;
                }
            }
        }

        totalWatch.Stop();

        var evidence = result.Outcome == RowScrollCoordinatorOutcome.NoMovementStop
            ? result.Evidence with { Reason = "scroll_no_movement_after_max_ticks" }
            : finalObservation.Evidence;
        if (provisionalAccepted && !releaseConfirmed)
        {
            evidence = evidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Reason = "cursor_release_unconfirmed"
            };
        }
        else if (provisionalAccepted && evidence.Decision != provisionalEvidence.Decision)
        {
            scanLog.WriteEvent("ROW_SCROLL_RELEASE_RECLASSIFIED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, provisionalDecision={provisionalEvidence.Decision}, finalDecision={evidence.Decision}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
        }

        var observationPhase = provisionalAccepted ? "PostCursorRelease" : result.Phase.ToString();
        var details = ScanDiagnosticDetails.RowScroll(
            direction,
            result.Tick,
            maxTicks,
            result.Sample,
            evidence.Decision,
            evidence.NoMoveScore,
            evidence.OneRowScore,
            evidence.TwoRowScore,
            evidence.BestScore,
            evidence.SecondScore,
            evidence.Margin,
            evidence.MovedDistance,
            finalObservation.FrameDistance,
            observationPhase,
            result.SettleSamples,
            result.SettleElapsedMilliseconds,
            evidence.Reason);
        scanLog.WriteEvent("ROW_SCROLL_TIMING", $"direction={direction}, phase={observationPhase}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, tick={result.Tick}/{maxTicks}, scroll_tick_wait_ms={wheelWaitMilliseconds:F1}, list_stable_ms={Math.Max(0, totalWatch.Elapsed.TotalMilliseconds - wheelWaitMilliseconds - signatureMilliseconds):F1}, row_signature_ms={signatureMilliseconds:F1}, settle_samples={result.SettleSamples}, settle_elapsed_ms={result.SettleElapsedMilliseconds:F1}, post_scroll_viewport_ms={totalWatch.Elapsed.TotalMilliseconds:F1}");

        if (!provisionalAccepted)
        {
            if (result.Outcome == RowScrollCoordinatorOutcome.NoMovementStop)
            {
                scanLog.WriteEvent("ROW_SCROLL_FAIL", $"direction={direction}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, ticks={result.Tick}/{maxTicks}, decision={evidence.Decision}, movedDistance={evidence.MovedDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason=scroll_no_movement_after_max_ticks");
                return ScrollRowsResult.Fail(
                    $"scroll produced no confirmed movement after {result.Tick} tick(s)",
                    retryable: false,
                    evidence,
                    details);
            }

            scanLog.WriteEvent("ROW_SCROLL_AMBIGUOUS_STOP", $"direction={direction}, phase={result.Phase}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, settleSamples={result.SettleSamples}, settleElapsedMs={result.SettleElapsedMilliseconds:F1}, decision={evidence.Decision}, bestRows={evidence.BestRows}, bestScore={evidence.BestScore}, secondScore={evidence.SecondScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, frameDistance={result.Observation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
            return ScrollRowsResult.Fail(
                $"scroll evidence remained ambiguous after {result.Sample} sample(s): {evidence.Reason}",
                retryable: false,
                evidence,
                details);
        }

        if (!releaseConfirmed)
        {
            scanLog.WriteEvent("ROW_SCROLL_RELEASE_UNCONFIRMED", $"direction={direction}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, provisionalDecision={provisionalEvidence.Decision}, decision={evidence.Decision}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
            return ScrollRowsResult.Fail(
                "scroll movement did not remain confirmed after releasing the list hover",
                retryable: false,
                evidence,
                details);
        }

        var rowsAdvanced = evidence.AcceptedRows ?? 0;
        if (rowsAdvanced <= 0)
        {
            return ScrollRowsResult.Fail("row-scroll coordinator accepted a non-positive decision", retryable: false, evidence, details);
        }

        if (rowsAdvanced == 2 && !allowTwoRows)
        {
            scanLog.WriteEvent("ROW_SCROLL_OVERSHOT_BLOCKED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, rowsAdvanced=2, tick={result.Tick}/{maxTicks}, sample={result.Sample}, bestScore={evidence.BestScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, reason=strict_single_row_path");
            return ScrollRowsResult.Fail(
                "scroll advanced two rows while the current traversal requires exactly one row",
                retryable: false,
                evidence,
                details);
        }

        var estimatedTop = upward
            ? Math.Max(1, visibleTopLogicalRow - rowsAdvanced)
            : Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
        scanLog.WriteEvent(doneEvent, $"direction={direction}, phase={observationPhase}, fromTop={visibleTopLogicalRow}, estimatedToTop={estimatedTop}, requestedToTop={targetTop}, rowsAdvanced={rowsAdvanced}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, settleSamples={result.SettleSamples}, settleElapsedMs={result.SettleElapsedMilliseconds:F1}, decision={evidence.Decision}, bestScore={evidence.BestScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, reason={evidence.Reason}");
        return ScrollRowsResult.Ok(
            $"confirmed {direction} movement of {rowsAdvanced} row(s) from {visibleTopLogicalRow}",
            rowsAdvanced,
            latestViewport.Rows,
            evidence,
            details);
    }

    private static Rectangle[] BuildVisualRowSignatureRects(Rectangle listGridRect, int visibleRows)
    {
        var rects = new Rectangle[Math.Max(1, visibleRows)];
        var rowHeight = listGridRect.Height / (double)rects.Length;
        for (var i = 0; i < rects.Length; i++)
        {
            var top = listGridRect.Top + (int)Math.Round(rowHeight * i);
            var bottom = listGridRect.Top + (int)Math.Round(rowHeight * (i + 1));
            rects[i] = Rectangle.FromLTRB(
                listGridRect.Left + 8,
                top + 6,
                listGridRect.Right - 8,
                Math.Max(top + 7, bottom - 6));
        }

        return rects;
    }

    private static System.Drawing.Point ListNeutralPoint(GameWindow window, Rectangle listGridRect)
    {
        var bounds = window.ClientScreenRect;
        return new System.Drawing.Point(
            Math.Clamp(listGridRect.Right + 24, bounds.Left + 1, bounds.Right - 2),
            Math.Clamp(listGridRect.Top + (listGridRect.Height / 2), bounds.Top + 1, bounds.Bottom - 2));
    }

    private static Rectangle SelectionProbeRect(GameWindow window, System.Drawing.Point clickPoint, ScanProfile? profile = null)
    {
        return DriveDiscSelectionGeometry.Probe(clickPoint, window.ClientScreenRect, profile);
    }

    private static RowVisualSignature[] CaptureRowSignatures(GameWindow window, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        var signatures = new RowVisualSignature[rowSignatureRects.Count];
        if (rowSignatureRects.Count == 0)
        {
            return signatures;
        }

        var bounds = rowSignatureRects[0];
        for (var i = 1; i < rowSignatureRects.Count; i++)
        {
            bounds = Rectangle.Union(bounds, rowSignatureRects[i]);
        }

        using var image = window.CaptureFrame(bounds);
        for (var i = 0; i < signatures.Length; i++)
        {
            var rect = rowSignatureRects[i];
            signatures[i] = RowVisualSignatureExtractor.Create(image, new Rectangle(rect.Left - bounds.Left, rect.Top - bounds.Top, rect.Width, rect.Height));
        }

        return signatures;
    }

    private static ViewportSignatures CaptureViewportSignatures(GameWindow window, Rectangle listGridRect, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        using var image = window.CaptureFrame(listGridRect);
        var rows = new RowVisualSignature[rowSignatureRects.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var rect = rowSignatureRects[i];
            rows[i] = RowVisualSignatureExtractor.Create(image, new Rectangle(rect.Left - listGridRect.Left, rect.Top - listGridRect.Top, rect.Width, rect.Height));
        }

        return new ViewportSignatures(
            CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size)),
            rows);
    }


    private readonly record struct ViewportSignatures(ImageSignature Grid, RowVisualSignature[] Rows);

}
