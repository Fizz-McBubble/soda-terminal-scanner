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
    public async Task<EdgeScrollProbeResult> ProbeNativeEdgeScrollAsync(
        ScanOptions options,
        int runs,
        CancellationToken token)
    {
        runs = Math.Clamp(runs, 1, 100);
        var profile = _profiles.ResolveRequired(options.ProfileName);
        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        using var window = GameWindow.Find(options.ProcessName);
        if (options.BringToFront)
        {
            window.BringToFront();
        }

        window.ConfigureCaptureMode(options.CaptureMode, scanLog.Write);
        using var inventoryRecognizer = CreateOcrRecognizer(options, outputDir, "edge-inventory", 1);
        var progress = new Progress<ScanProgress>(_ => { });
        var counters = new Counters();
        var preflight = await PrepareBackpackAsync(
            window,
            profile,
            inventoryRecognizer,
            "edge-scroll-probe",
            progress,
            counters,
            scanLog,
            token);
        var warehousePolicy = (profile.VisualProbes ?? new VisualProbeOptions()).WarehousePreflight ?? new WarehousePreflightPolicy();
        var contextGuard = new WarehouseContextGuard(
            window,
            preflight.MonitorPlan,
            warehousePolicy,
            inventoryRecognizer,
            scanLog);
        window.ConfigureInputGuard(contextGuard.EnsureHealthy);
        window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
        await Task.Delay(profile.ClickDelayMs, token);

        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var columns = Math.Max(1, profile.VisibleColumns);
        var visibleRows = Math.Max(1, profile.VisibleRows);
        var totalRows = preflight.InventoryCount is > 0
            ? Math.Max(1, (int)Math.Ceiling(preflight.InventoryCount.Value / (double)columns))
            : (int?)null;
        var maxVisibleTop = totalRows.HasValue
            ? Math.Max(1, totalRows.Value - visibleRows + 1)
            : int.MaxValue;
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, visibleRows, columns));
        var rowSignatureRects = BuildVisualRowSignatureRects(listGridRect, visibleRows);
        var changedRuns = 0;
        var bottomRuns = 0;
        var failedRuns = 0;
        scanLog.WriteEvent(
            "EDGE_CLICK_PROBE_START",
            $"runs={runs}, inventoryCount={preflight.InventoryCount?.ToString() ?? "unknown"}, totalRows={totalRows?.ToString() ?? "unknown"}, visibleRows={visibleRows}, columns={columns}, captureMode={window.ActiveCaptureMode}, client={window.ClientScreenRect.Width}x{window.ClientScreenRect.Height}, dpi={window.Dpi}");

        for (var run = 1; run <= runs; run++)
        {
            token.ThrowIfCancellationRequested();
            await ResetListToTopAsync(window, profile, progress, counters, scanLog, preflight.InventoryCount, token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            var armClientPoint = new PointF(
                offset.X + step.X * columns,
                offset.Y + step.Y * 3);
            var armPoint = DriveDiscSelectionGeometry.Center(window.ToScreenPoint(armClientPoint), window.ClientScreenRect, profile);
            window.MoveCursor(armPoint);
            window.LeftClick(armPoint);
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            scanLog.WriteEvent(
                "EDGE_CLICK_PROBE_ARM",
                $"run={run}/{runs}, visualRow=3, column={columns}, point={armPoint}, reason=match_scan_pre_advance_selection");
            var result = await AdvanceNativeEdgeClickAsync(
                window,
                profile,
                listGridRect,
                rowSignatureRects,
                offset,
                step,
                visibleTopLogicalRow: 1,
                maxVisibleTop,
                preflight.InventoryCount ?? 0,
                scannedRows: 0,
                scanLog,
                token);
            var decision = NativeEdgeClickPolicy.ResolveSettle(result);
            if (decision == NativeEdgeClickDecision.AdvanceOne)
            {
                changedRuns++;
            }
            else if (decision == NativeEdgeClickDecision.NoMove && totalRows is <= 4)
            {
                bottomRuns++;
            }
            else
            {
                failedRuns++;
            }

            scanLog.WriteEvent(
                "EDGE_CLICK_PROBE_RESULT",
                $"run={run}/{runs}, decision={decision}, settled={result.Settled}, changed={result.Changed}, movementDistance={result.MovementDistance}, stableFrames={result.StableFrames}, samples={result.Samples}, elapsedMs={result.ElapsedMilliseconds:F1}, expectedMovement={totalRows is > 4}, reason={result.Reason}");
        }

        var probeResult = new EdgeScrollProbeResult
        {
            Success = failedRuns == 0 && changedRuns + bottomRuns == runs,
            OutputDirectory = outputDir,
            RequestedRuns = runs,
            ChangedRuns = changedRuns,
            BottomRuns = bottomRuns,
            FailedRuns = failedRuns,
            InventoryCount = preflight.InventoryCount,
            ClientWidth = window.ClientScreenRect.Width,
            ClientHeight = window.ClientScreenRect.Height,
            Dpi = window.Dpi,
            CaptureMode = window.ActiveCaptureMode.ToString()
        };
        scanLog.WriteEvent(
            "EDGE_CLICK_PROBE_DONE",
            $"success={probeResult.Success}, runs={runs}, changed={changedRuns}, bottom={bottomRuns}, failed={failedRuns}");
        return probeResult;
    }

    private static async Task<NativeEdgeClickSettleResult> AdvanceNativeEdgeClickAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        PointF offset,
        PointF step,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int inventoryCount,
        int scannedRows,
        ScanLog scanLog,
        CancellationToken token)
    {
        if (rowSignatureRects.Count < 4)
        {
            return new NativeEdgeClickSettleResult(
                false,
                false,
                0,
                0,
                0,
                0,
                "edge_row_geometry_missing",
                Point.Empty,
                "unknown",
                "unknown");
        }

        var targetClientPoint = new PointF(offset.X + step.X, offset.Y + step.Y * 4);
        var rarityAnchor = window.ToScreenPoint(targetClientPoint);
        var targetPoint = DriveDiscSelectionGeometry.Center(rarityAnchor, window.ClientScreenRect, profile);
        window.MoveCursor(targetPoint);
        var rarity = DetectRarityAround(window, profile, rarityAnchor).Rarity;
        if (rarity is null)
        {
            await Task.Delay(25, token);
            rarity = DetectRarityAround(window, profile, rarityAnchor).Rarity;
        }

        if (rarity is null)
        {
            return new NativeEdgeClickSettleResult(
                false,
                false,
                0,
                0,
                0,
                0,
                "edge_target_missing",
                targetPoint,
                "unknown",
                "unknown");
        }

        var before = CaptureNativeEdgeViewport(window, profile, listGridRect, rowSignatureRects);
        var beforeRows = before.Rows;
        var beforeListHash = FormatNativeEdgeListHash(beforeRows);
        var pixelsPerRow = ScrollbarPixelsPerLogicalRow(window, profile, before.Scrollbar, maxVisibleTop);
        var previous = before;
        var latest = before;
        var watch = Stopwatch.StartNew();
        var settle = new NativeEdgeClickSettleTracker(ListMovementTolerance, ListStableTolerance);
        var extended = false;
        var pollMilliseconds = Math.Max(10, profile.LoadPollMs);
        RowAdvanceEvidence latestEvidence = default;
        RowAdvanceDecision? provisional = null;
        RowAdvanceDecision? lastLoggedDecision = null;
        scanLog.WriteEvent(
            "EDGE_CLICK_START",
            $"visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, targetVisualRow=4, targetColumn=1, targetPoint={targetPoint}, targetRarity={rarity}, beforeListHash={beforeListHash}, inventoryCount={inventoryCount}, scannedRows={scannedRows}, scrollbarFound={before.Scrollbar.Found}, scrollbarCenter={before.Scrollbar.CenterY}, scrollbarPixelsPerRow={pixelsPerRow:F3}, timeoutMs={settle.TimeoutMilliseconds}, movingTimeoutMs={NativeEdgeClickSettleTracker.MovingTimeoutMilliseconds}");
        window.LeftClick(targetPoint);

        while (settle.CanObserve(watch.Elapsed.TotalMilliseconds))
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollMilliseconds, token);
            if (!settle.CanObserve(watch.Elapsed.TotalMilliseconds)) break;
            latest = CaptureNativeEdgeViewport(window, profile, listGridRect, rowSignatureRects);
            token.ThrowIfCancellationRequested();
            var currentMovementDistance = AverageSignatureDistance(
                beforeRows,
                latest.Rows,
                [(0, 0), (1, 1), (2, 2)]);
            latestEvidence = EvaluateNativeEdgePosition(before, latest, pixelsPerRow);
            var pixelDelta = ScrollbarDelta(before.Scrollbar, latest.Scrollbar);
            var frameStable = NativeEdgeFrameStable(previous, latest);
            var phaseDifference = NativeEdgePhaseProbe.Compare(previous.Phase, latest.Phase);
            if (settle.Samples == 0 || settle.Samples % 10 == 0)
                scanLog.WriteEvent("EDGE_PHASE_STABILITY",
                    $"visibleTopLogicalRow={visibleTopLogicalRow}, changedPermille={phaseDifference.ChangedPermille}, maxTileDelta={phaseDifference.MaximumDelta}, gridStable={phaseDifference.Stable}, beforeThumb={previous.Scrollbar.StartY}-{previous.Scrollbar.EndY}, afterThumb={latest.Scrollbar.StartY}-{latest.Scrollbar.EndY}, frameStable={frameStable}");
            previous = latest;
            settle.Observe(currentMovementDistance, frameStable ? 0 : ListStableTolerance + 1,
                watch.Elapsed.TotalMilliseconds, independentMovement: pixelDelta is not null and not 0,
                allowSettlement: false);
            if (watch.Elapsed.TotalMilliseconds >= settle.TimeoutMilliseconds) break;
            if (lastLoggedDecision != latestEvidence.Decision)
            {
                lastLoggedDecision = latestEvidence.Decision;
                scanLog.WriteEvent("EDGE_CLICK_POSITION", $"visibleTopLogicalRow={visibleTopLogicalRow}, decision={latestEvidence.Decision}, strong={latestEvidence.Strong}, scrollbarDelta={pixelDelta}, scrollbarPixelsPerRow={pixelsPerRow:F3}, frameStable={frameStable}, stableFrames={settle.StableFrames}, elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, reason={latestEvidence.Reason}");
            }
            if (!extended && settle.SawMovement
                && watch.ElapsedMilliseconds >= NativeEdgeClickSettleTracker.InitialTimeoutMilliseconds)
            {
                extended = true;
                scanLog.WriteEvent("EDGE_CLICK_SETTLE_EXTENDED",
                    $"visibleTopLogicalRow={visibleTopLogicalRow}, elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, movementDistance={settle.MovementDistance}, stableFrames={settle.StableFrames}, timeoutMs={settle.TimeoutMilliseconds}, reason=continue_original_click_observation");
            }
            if (settle.StableFrames >= NativeEdgeClickSettleTracker.RequiredStableFrames
                && latestEvidence.Strong && latestEvidence.Decision == RowAdvanceDecision.OneRow)
            {
                provisional = RowAdvanceDecision.OneRow;
                break;
            }
        }

        // A stationary click may be retried only after the full original window
        // and an independent zero-position proof. Identical cards are not bottom.
        if (provisional is null && !settle.SawMovement
            && watch.ElapsedMilliseconds >= NativeEdgeClickSettleTracker.InitialTimeoutMilliseconds
            && settle.StableFrames >= 3 && latestEvidence.Strong
            && latestEvidence.Decision == RowAdvanceDecision.NoMove
            && ScrollbarDelta(before.Scrollbar, latest.Scrollbar) == 0)
        {
            provisional = RowAdvanceDecision.NoMove;
        }
        if (provisional is null) return Finish(false, null, "native_edge_position_unverified");

        // Release hover and verify the final position against the ORIGINAL
        // baseline. A transient one-row position or a return to the baseline
        // must never authorize a preselected capture.
        window.MoveCursor(ListNeutralPoint(window, listGridRect));
        // Releasing hover can still animate. Keep observing the same click
        // until its original deadline instead of truncating after six samples.
        for (var sample = 1; settle.CanObserveRelease(watch.Elapsed.TotalMilliseconds); sample++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollMilliseconds, token);
            if (!settle.CanObserveRelease(watch.Elapsed.TotalMilliseconds)) break;
            var released = CaptureNativeEdgeViewport(window, profile, listGridRect, rowSignatureRects);
            token.ThrowIfCancellationRequested();
            if (!settle.CanObserveRelease(watch.Elapsed.TotalMilliseconds)) break;
            var evidence = EvaluateNativeEdgePosition(before, released, pixelsPerRow);
            var frameStable = NativeEdgeFrameStable(latest, released);
            var samePosition = evidence.Strong && evidence.Decision == provisional
                && frameStable
                && (provisional != RowAdvanceDecision.NoMove || ScrollbarDelta(before.Scrollbar, released.Scrollbar) == 0);
            var releaseConfirmed = settle.ObserveRelease(samePosition, watch.Elapsed.TotalMilliseconds);
            scanLog.WriteEvent("EDGE_CLICK_RELEASE_EVIDENCE", $"visibleTopLogicalRow={visibleTopLogicalRow}, sample={sample}, provisional={provisional}, decision={evidence.Decision}, strong={evidence.Strong}, scrollbarDelta={ScrollbarDelta(before.Scrollbar, released.Scrollbar)}, scrollbarPixelsPerRow={pixelsPerRow:F3}, beforeThumb={latest.Scrollbar.StartY}-{latest.Scrollbar.EndY}, afterThumb={released.Scrollbar.StartY}-{released.Scrollbar.EndY}, frameStable={frameStable}, stableMatches={settle.ReleaseStableFrames}/{NativeEdgeClickSettleTracker.RequiredStableFrames}, elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, deadlineMs={NativeEdgeClickSettleTracker.MovingTimeoutMilliseconds}, reason={evidence.Reason}");
            latest = released;
            latestEvidence = evidence;
            if (releaseConfirmed && settle.CanObserveRelease(watch.Elapsed.TotalMilliseconds))
                return Finish(true, provisional == RowAdvanceDecision.OneRow ? 1 : 0, "native_edge_position_release_confirmed");
        }
        return Finish(false, null, "native_edge_position_release_unverified");

        NativeEdgeClickSettleResult Finish(bool accepted, int? rowsAdvanced, string reason)
        {
            watch.Stop();
            var result = new NativeEdgeClickSettleResult(
                accepted, rowsAdvanced == 1, settle.MovementDistance, settle.StableFrames,
                settle.Samples, watch.Elapsed.TotalMilliseconds, reason, targetPoint,
                beforeListHash, FormatNativeEdgeListHash(latest.Rows), settle.TimeoutMilliseconds,
                rowsAdvanced, accepted);
            scanLog.WriteEvent("EDGE_CLICK_SETTLED", $"visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={targetPoint}, settled={accepted}, verifiedRowsAdvanced={rowsAdvanced}, changed={result.Changed}, movementDistance={result.MovementDistance}, stableFrames={result.StableFrames}, samples={result.Samples}, elapsedMs={result.ElapsedMilliseconds:F1}, beforeListHash={beforeListHash}, afterListHash={result.AfterListHash}, scrollbarDelta={ScrollbarDelta(before.Scrollbar, latest.Scrollbar)}, scrollbarPixelsPerRow={pixelsPerRow:F3}, beforeThumb={before.Scrollbar.StartY}-{before.Scrollbar.EndY}, afterThumb={latest.Scrollbar.StartY}-{latest.Scrollbar.EndY}, releaseConfirmed={accepted}, inventoryCount={inventoryCount}, scannedRows={scannedRows}, positionReason={latestEvidence.Reason}, reason={reason}");
            return result;
        }
    }

    private static NativeEdgeViewport CaptureNativeEdgeViewport(
        GameWindow window, ScanProfile profile, Rectangle listGridRect, IReadOnlyList<Rectangle> rowRects)
    {
        var scrollbarBounds = ScrollbarProbeBounds(window, profile, includeThumbEnds: true);
        var bounds = Rectangle.Union(listGridRect, scrollbarBounds);
        using var frame = window.CaptureFrame(bounds);
        var rows = rowRects.Select(rect => RowVisualSignatureExtractor.Create(frame,
            new Rectangle(rect.Left - bounds.Left, rect.Top - bounds.Top, rect.Width, rect.Height))).ToArray();
        var scrollbar = ExtractScrollbarThumbProbe(frame, bounds, scrollbarBounds,
            profile.Color("scrollBar"), Math.Max(0, profile.ColorTolerance));
        var phaseBounds = Rectangle.Union(rowRects[0], rowRects[1]);
        phaseBounds.Offset(-bounds.Left, -bounds.Top);
        using var bitmap = frame.ToBitmap();
        var phase = NativeEdgePhaseProbe.Capture(bitmap, phaseBounds);
        return new NativeEdgeViewport(rows, scrollbar, phase);
    }

    private static RowAdvanceEvidence EvaluateNativeEdgePosition(
        NativeEdgeViewport before, NativeEdgeViewport after, double pixelsPerRow)
    {
        var verification = VerifyOneRowDown(before.Rows, after.Rows);
        var distance = AverageSignatureDistance(before.Rows, after.Rows, [(0, 0), (1, 1), (2, 2)]);
        var structural = RowAdvanceEvaluator.Evaluate(verification.NoMoveScore,
            verification.OneRowScore, verification.TwoRowScore, distance);
        var delta = ScrollbarDelta(before.Scrollbar, after.Scrollbar);
        return NativeEdgePositionPolicy.Resolve(structural, before.Scrollbar.Found, after.Scrollbar.Found,
            (before.Scrollbar.EndY - before.Scrollbar.StartY) - (after.Scrollbar.EndY - after.Scrollbar.StartY),
            delta, pixelsPerRow);
    }

    private static bool NativeEdgeFrameStable(NativeEdgeViewport before, NativeEdgeViewport after) =>
        NativeEdgePhaseProbe.Compare(before.Phase, after.Phase).Stable
        && before.Scrollbar.Found && after.Scrollbar.Found
        && before.Scrollbar.StartY == after.Scrollbar.StartY
        && before.Scrollbar.EndY == after.Scrollbar.EndY;

    private readonly record struct NativeEdgeViewport(
        RowVisualSignature[] Rows, ScrollbarThumbProbe Scrollbar, NativeEdgePhase Phase);

    private static string FormatNativeEdgeListHash(IEnumerable<RowVisualSignature> signatures) =>
        string.Join("-", signatures.Take(3).Select(signature => signature.Hash.ToString("X16")));

}
