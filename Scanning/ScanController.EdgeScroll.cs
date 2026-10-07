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
            await ResetListToTopAsync(window, profile, progress, counters, scanLog, token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            var armClientPoint = new PointF(
                offset.X + step.X * columns,
                offset.Y + step.Y * 3);
            var armPoint = window.ToScreenPoint(armClientPoint);
            window.MoveCursor(armPoint);
            window.LeftClickCurrent();
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
            if (decision == NativeEdgeClickDecision.AdvanceAssumedOne)
            {
                changedRuns++;
            }
            else if (decision == NativeEdgeClickDecision.HashFallback && totalRows is <= 4)
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
        var targetPoint = window.ToScreenPoint(targetClientPoint);
        window.MoveCursor(targetPoint);
        var rarity = DetectRarityAround(window, profile, targetPoint).Rarity;
        if (rarity is null)
        {
            await Task.Delay(25, token);
            rarity = DetectRarityAround(window, profile, targetPoint).Rarity;
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

        var beforeRows = CaptureRowSignatures(window, rowSignatureRects);
        var beforeListHash = FormatNativeEdgeListHash(beforeRows);
        var previousRows = beforeRows;
        var watch = Stopwatch.StartNew();
        var samples = 0;
        var stableFrames = 0;
        var movementDistance = 0;
        var sawMovement = false;
        var pollMilliseconds = Math.Max(10, profile.LoadPollMs);
        scanLog.WriteEvent(
            "EDGE_CLICK_START",
            $"visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, targetVisualRow=4, targetColumn=1, targetPoint={targetPoint}, targetRarity={rarity}, stableFrames=0, movementDistance=0, beforeListHash={beforeListHash}, afterListHash=pending, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedRows}, timeoutMs={NativeEdgeClickTimeoutMilliseconds}");
        window.LeftClickCurrent();

        while (watch.ElapsedMilliseconds < NativeEdgeClickTimeoutMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollMilliseconds, token);
            samples++;
            var currentRows = CaptureRowSignatures(window, rowSignatureRects);
            var currentMovementDistance = AverageSignatureDistance(
                beforeRows,
                currentRows,
                [(0, 0), (1, 1), (2, 2)]);
            movementDistance = Math.Max(movementDistance, currentMovementDistance);
            var frameDistance = AverageSignatureDistance(
                previousRows,
                currentRows,
                [(0, 0), (1, 1), (2, 2)]);
            previousRows = currentRows;
            sawMovement |= currentMovementDistance > ListMovementTolerance;
            stableFrames = frameDistance <= ListStableTolerance
                ? stableFrames + 1
                : 0;
            var mayDecideNoMove = watch.ElapsedMilliseconds >= NativeEdgeClickNoMoveDecisionMilliseconds;
            if (stableFrames >= NativeEdgeClickStableFrames && (sawMovement || mayDecideNoMove))
            {
                watch.Stop();
                var result = new NativeEdgeClickSettleResult(
                    true,
                    sawMovement,
                    movementDistance,
                    stableFrames,
                    samples,
                    watch.Elapsed.TotalMilliseconds,
                    sawMovement ? "native_edge_click_changed_assume_one" : "native_edge_click_stable_unchanged",
                    targetPoint,
                    beforeListHash,
                    FormatNativeEdgeListHash(currentRows));
                scanLog.WriteEvent(
                    "EDGE_CLICK_SETTLED",
                    $"visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={targetPoint}, settled=True, changed={result.Changed}, movementDistance={result.MovementDistance}, frameDistance={frameDistance}, stableFrames={result.StableFrames}, samples={result.Samples}, elapsedMs={result.ElapsedMilliseconds:F1}, beforeListHash={result.BeforeListHash}, afterListHash={result.AfterListHash}, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedRows}, reason={result.Reason}");
                return result;
            }
        }

        watch.Stop();
        return new NativeEdgeClickSettleResult(
            false,
            sawMovement,
            movementDistance,
            stableFrames,
            samples,
            watch.Elapsed.TotalMilliseconds,
            "native_edge_click_settle_timeout",
            targetPoint,
            beforeListHash,
            FormatNativeEdgeListHash(previousRows));
    }

    private static string FormatNativeEdgeListHash(IEnumerable<RowVisualSignature> signatures) =>
        string.Join("-", signatures.Take(3).Select(signature => signature.Hash.ToString("X16")));

}
