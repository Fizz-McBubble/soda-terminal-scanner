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
    private static async Task ProduceCapturesAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int? inventoryCount,
        ScanTraversalMode traversalMode,
        CancellationToken token)
    {
        if (traversalMode == ScanTraversalMode.OverlapSignaturePage && inventoryCount is > 0)
        {
            await ProduceCapturesOverlapSignaturePageAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.OverlapSignaturePage)
        {
            scanLog.Write("OverlapSignaturePage requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，重叠签名扫描无法计算总行数。");
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，重叠签名扫描无法计算总行数。请确认数量区域可见后重试。");
        }

        if (traversalMode == ScanTraversalMode.SafeBandViewport && inventoryCount is > 0)
        {
            await ProduceCapturesSafeBandViewportAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.SafeBandViewport)
        {
            scanLog.Write("SafeBandViewport requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，安全带扫描无法计算总行数。");
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，安全带扫描无法计算总行数。请确认数量区域可见后重试。");
        }

        if (traversalMode == ScanTraversalMode.CalibratedPage && inventoryCount is > 0)
        {
            await ProduceCapturesCalibratedPageAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.CalibratedPage)
        {
            scanLog.Write("CalibratedPage requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，校准翻页无法计算总行数。");
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，校准翻页无法计算总行数。请确认数量区域可见后重试。");
        }

        await ProduceCapturesLegacyThirdRowAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount, token);
    }

    private static async Task ProduceCapturesOverlapSignaturePageAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int inventoryCount,
        CancellationToken token)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var columns = Math.Max(1, profile.VisibleColumns);
        const int visibleRows = 4;
        var totalRows = Math.Max(1, (int)Math.Ceiling(inventoryCount / (double)columns));
        var lastRowColumns = inventoryCount % columns == 0 ? columns : inventoryCount % columns;
        var maxVisibleTop = Math.Max(1, totalRows - visibleRows + 1);
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, visibleRows, columns));
        var rowSignatureRects = BuildVisualRowSignatureRects(listGridRect, visibleRows);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var scannedLogicalRows = new HashSet<int>();
        var pendingRowStartColumns = new Dictionary<int, int>();
        var deferredRowCounts = new Dictionary<int, int>();
        var nativeEdgeClick = options.RowAdvanceMode == RowAdvanceMode.NativeEdgeClick;
        string? lastNativeMiddleRowHash = null;
        NativeEdgePostScrollSelection? pendingPreselectedPostScrollCell = null;

        scanLog.Write($"Traversal: overlap-signature-page. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, maxVisibleTop={maxVisibleTop}, listGrid={listGridRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var visibleTopLogicalRow = 1;
        var lastScrollChangedViewport = false;
        var guardIterations = Math.Max(totalRows * 4, 16);
        for (var iteration = 1; scannedLogicalRows.Count < totalRows && iteration <= guardIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            var currentRows = CaptureRowSignatures(window, rowSignatureRects);
            LogOverlapViewport(scanLog, iteration, visibleTopLogicalRow, maxVisibleTop, totalRows, currentRows);

            var scannedThisViewport = false;
            foreach (var candidate in BuildOverlapScanCandidates(visibleTopLogicalRow, maxVisibleTop, totalRows, visibleRows, scannedLogicalRows))
            {
                token.ThrowIfCancellationRequested();
                var logicalRow = candidate.LogicalRow;
                var visualRow = candidate.VisualRow;
                var viewportState = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
                var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
                var startColumn = pendingRowStartColumns.TryGetValue(logicalRow, out var pendingStartColumn)
                    ? Math.Clamp(pendingStartColumn, 1, maxColumns)
                    : 1;
                scanLog.WriteEvent("OVERLAP_ROW_CANDIDATE", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, state={viewportState}, rowHash={currentRows[Math.Clamp(visualRow - 1, 0, currentRows.Length - 1)].Hash:X16}, maxColumns={maxColumns}, startColumn={startColumn}");
                Report(progress, counters, $"重叠签名扫描：逻辑行 {logicalRow}/{totalRows}，视觉第 {visualRow} 行，{viewportState}。");

                var rowFingerprint = nativeEdgeClick && visualRow == 3
                    ? new RowDetailFingerprintBuilder()
                    : null;
                var preselectedPostScrollCell = pendingPreselectedPostScrollCell;
                if (preselectedPostScrollCell is not null
                    && !preselectedPostScrollCell.Value.Matches(logicalRow, visualRow, 1))
                {
                    throw NavigationFailure($"原生底行点击后的预选格未在预期位置出现：expectedLogicalRow={preselectedPostScrollCell.Value.LogicalRow}, expectedVisualRow={preselectedPostScrollCell.Value.VisualRow}, candidateLogicalRow={logicalRow}, candidateVisualRow={visualRow}。为避免漏扫，本次停止。");
                }
                RowScanResult rowResult;
                try
                {
                    rowResult = await ScanVisualRowAsync(
                        window,
                        profile,
                        queue,
                        options,
                        runtimeState,
                        counters,
                        progress,
                        scanLog,
                        offset,
                        step,
                        panelRect,
                        rois,
                        statOffset,
                        statRowBackground,
                        logicalRow,
                        visualRow,
                        isBottom: logicalRow == totalRows,
                        maxColumns,
                        logicalRow,
                        treatBlankAsEnd: false,
                        panelChangeProbeRect,
                        token,
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow,
                        maxVisibleTop: maxVisibleTop,
                        afterScroll: lastScrollChangedViewport,
                        postScrollFirstCell: lastScrollChangedViewport,
                        startColumn: startColumn,
                        rowFingerprint: rowFingerprint,
                        preselectedPostScrollCell: preselectedPostScrollCell);
                }
                catch (PanelCellCaptureException ex) when (
                    options.OverlapConflictMode == OverlapConflictMode.Recover
                    && visualRow > 2
                    && logicalRow < totalRows
                    && deferredRowCounts.GetValueOrDefault(logicalRow) < 2)
                {
                    var resumeColumn = Math.Clamp(ex.Column, 1, maxColumns);
                    pendingRowStartColumns[logicalRow] = resumeColumn;
                    deferredRowCounts[logicalRow] = deferredRowCounts.GetValueOrDefault(logicalRow) + 1;
                    scannedThisViewport = true;
                    lastScrollChangedViewport = false;
                    scanLog.WriteEvent("OVERLAP_ROW_DEFERRED_AFTER_PANEL_TIMEOUT", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, resumeColumn={resumeColumn}, maxColumns={maxColumns}, deferCount={deferredRowCounts[logicalRow]}, visibleTopLogicalRow={visibleTopLogicalRow}, state={viewportState}, reason={ex.InnerException?.Message ?? ex.Message}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    break;
                }

                lastScrollChangedViewport = false;
                if (rowResult == RowScanResult.Stop)
                {
                    return;
                }

                scannedLogicalRows.Add(logicalRow);
                if (preselectedPostScrollCell is not null)
                {
                    pendingPreselectedPostScrollCell = null;
                    scanLog.WriteEvent("EDGE_CLICK_PRESELECTED_CONSUMED", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, column=1, edgeTargetPoint={preselectedPostScrollCell.Value.EdgeTargetPoint}, reason=stable_roi_capture_accepted");
                }
                pendingRowStartColumns.Remove(logicalRow);
                deferredRowCounts.Remove(logicalRow);
                scannedThisViewport = true;
                scanLog.WriteEvent("OVERLAP_ROW_SCANNED", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                if (rowFingerprint is not null)
                {
                    if (rowFingerprint.Count != maxColumns)
                    {
                        throw NavigationFailure($"原生底行点击模式无法建立完整的视觉第 3 行指纹：logicalRow={logicalRow}, captured={rowFingerprint.Count}, expected={maxColumns}。");
                    }

                    lastNativeMiddleRowHash = rowFingerprint.Complete();
                    scanLog.WriteEvent("EDGE_CLICK_ROW_HASH", $"phase=baseline, iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, columns={rowFingerprint.Count}, rowHash={lastNativeMiddleRowHash}");
                }
            }

            if (scannedLogicalRows.Count >= totalRows)
            {
                break;
            }

            if (visibleTopLogicalRow >= maxVisibleTop)
            {
                var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
                throw NavigationFailure($"重叠签名扫描到达底部但仍有逻辑行未扫：{string.Join(",", missing)}。为避免漏扫，本次停止。");
            }

            var beforeTop = visibleTopLogicalRow;
            if (nativeEdgeClick)
            {
                var edgeClick = await AdvanceNativeEdgeClickAsync(
                    window,
                    profile,
                    listGridRect,
                    rowSignatureRects,
                    offset,
                    step,
                    visibleTopLogicalRow,
                    maxVisibleTop,
                    inventoryCount,
                    scannedLogicalRows.Count,
                    scanLog,
                    token);
                var edgeDecision = NativeEdgeClickPolicy.ResolveSettle(edgeClick);
                if (edgeDecision == NativeEdgeClickDecision.AdvanceAssumedOne)
                {
                    if (!NativeEdgePostScrollSelectionPolicy.TryBind(
                            edgeClick,
                            beforeTop,
                            maxVisibleTop,
                            columns,
                            out var preselected))
                    {
                        scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, reason=preselected_binding_insufficient");
                        throw NavigationFailure("底行点击后列表虽变化，但无法将已选中格安全绑定到新视口第 3 行第 1 列。为避免复用旧详情或漏扫，本次停止。");
                    }
                    visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + 1);
                    pendingPreselectedPostScrollCell = preselected;
                    lastScrollChangedViewport = true;
                    scanLog.WriteEvent("EDGE_CLICK_CHANGED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, assumedRows=1, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, samples={edgeClick.Samples}, elapsedMs={edgeClick.ElapsedMilliseconds:F1}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash ?? "unknown"}, currentRowHash=unknown, preselectedLogicalRow={preselected.LogicalRow}, preselectedVisualRow={preselected.VisualRow}, preselectedColumn={preselected.Column}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=native_edge_click_changed_assume_one");
                    scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced=1, decision=AssumedOne, reason=native_edge_click_changed_assume_one, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    continue;
                }

                if (edgeDecision == NativeEdgeClickDecision.Stop)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, samples={edgeClick.Samples}, elapsedMs={edgeClick.ElapsedMilliseconds:F1}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash ?? "unknown"}, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason={edgeClick.Reason}");
                    throw NavigationFailure($"底行点击后列表未在 {edgeClick.TimeoutMilliseconds}ms 内稳定：{edgeClick.Reason}。");
                }

                if (string.IsNullOrWhiteSpace(lastNativeMiddleRowHash))
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=missing_previous_row_hash");
                    throw NavigationFailure("底行点击未观察到列表变化，且缺少上一视觉第 3 行指纹，无法判断是否到底。");
                }

                using var bufferedRow = new BufferedRowCapture();
                var fallbackFingerprint = new RowDetailFingerprintBuilder();
                var fallbackLogicalRow = visibleTopLogicalRow + 3;
                RowScanResult fallbackResult;
                try
                {
                    fallbackResult = await ScanVisualRowAsync(
                        window,
                        profile,
                        queue,
                        options,
                        runtimeState,
                        counters,
                        progress,
                        scanLog,
                        offset,
                        step,
                        panelRect,
                        rois,
                        statOffset,
                        statRowBackground,
                        pass: fallbackLogicalRow,
                        row: 3,
                        isBottom: false,
                        maxColumns: columns,
                        logicalRow: fallbackLogicalRow,
                        treatBlankAsEnd: false,
                        panelChangeProbeRect,
                        token,
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow + 1,
                        maxVisibleTop: maxVisibleTop,
                        afterScroll: true,
                        postScrollFirstCell: true,
                        rowFingerprint: fallbackFingerprint,
                        bufferedRow: bufferedRow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failureCode = ex is IScannerFailureException scannerFailure
                        ? scannerFailure.Code
                        : ex.GetType().Name;
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash}, currentRowHash=incomplete, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=hash_fallback_capture_failed, failureCode={failureCode}");
                    throw;
                }
                if (fallbackResult != RowScanResult.Completed || fallbackFingerprint.Count != columns)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash}, currentRowHash=incomplete, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=incomplete_hash_fallback, captured={fallbackFingerprint.Count}, expected={columns}");
                    throw NavigationFailure("底行点击行哈希兜底未能捕获完整的视觉第 3 行。");
                }

                var fallbackHash = fallbackFingerprint.Complete();
                var previousMiddleRowHash = lastNativeMiddleRowHash;
                var fallbackDecision = NativeEdgeClickPolicy.ResolveFallback(previousMiddleRowHash, fallbackHash);
                scanLog.WriteEvent("EDGE_CLICK_HASH_FALLBACK", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, capturedColumns={fallbackFingerprint.Count}, decision={fallbackDecision}");
                if (fallbackDecision == NativeEdgeClickDecision.AdvanceAssumedOne)
                {
                    visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + 1);
                    var bufferedLogicalRow = visibleTopLogicalRow + 2;
                    var commitResult = CommitBufferedRow(
                        bufferedRow,
                        queue,
                        options,
                        counters,
                        progress,
                        scanLog,
                        bufferedLogicalRow,
                        token);
                    scannedLogicalRows.Add(bufferedLogicalRow);
                    lastNativeMiddleRowHash = fallbackHash;
                    lastScrollChangedViewport = false;
                    scanLog.WriteEvent("EDGE_CLICK_CHANGED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, assumedRows=1, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, reason=native_edge_click_hash_changed_assume_one, bufferedLogicalRow={bufferedLogicalRow}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced=1, decision=AssumedOne, reason=native_edge_click_hash_changed_assume_one, scannedThisViewport=True, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    if (commitResult == RowScanResult.Stop)
                    {
                        return;
                    }

                    continue;
                }

                if (fallbackDecision != NativeEdgeClickDecision.Bottom)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=invalid_hash_fallback");
                    throw NavigationFailure("底行点击行哈希兜底没有得到可提交结论。");
                }

                scanLog.WriteEvent("EDGE_CLICK_BOTTOM_DETECTED", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, visited={counters.Visited}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                var observedBottomLogicalRow = visibleTopLogicalRow + 3;
                RowScanResult bottomResult;
                try
                {
                    bottomResult = await ScanVisualRowAsync(
                        window,
                        profile,
                        queue,
                        options,
                        runtimeState,
                        counters,
                        progress,
                        scanLog,
                        offset,
                        step,
                        panelRect,
                        rois,
                        statOffset,
                        statRowBackground,
                        pass: observedBottomLogicalRow,
                        row: 4,
                        isBottom: true,
                        maxColumns: columns,
                        logicalRow: observedBottomLogicalRow,
                        treatBlankAsEnd: true,
                        panelChangeProbeRect,
                        token,
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow,
                        maxVisibleTop: visibleTopLogicalRow,
                        afterScroll: false,
                        postScrollFirstCell: false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failureCode = ex is IScannerFailureException scannerFailure
                        ? scannerFailure.Code
                        : ex.GetType().Name;
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=observed_bottom_capture_failed, failureCode={failureCode}");
                    throw;
                }
                if ((options.MaxItems <= 0 || counters.Queued < options.MaxItems)
                    && counters.Visited != inventoryCount)
                {
                    Interlocked.CompareExchange(
                        ref counters.StopReason,
                        "inventory_count_mismatch_at_observed_bottom",
                        null);
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, reason=inventory_count_mismatch_at_observed_bottom, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, visited={counters.Visited}, queued={counters.Queued}, bottomResult={bottomResult}");
                }

                return;
            }

            var scroll = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                options.ScrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                allowTwoRows: false,
                scanLog,
                options.ScrollTickDelayOverrideMs,
                token);
            if (!scroll.Success)
            {
                throw NavigationFailure($"重叠签名扫描滚动失败：{scroll.Message}", scroll.DiagnosticDetails);
            }

            var signatureEvidence = scroll.Evidence;
            var rowsAdvanced = scroll.RowsAdvanced;
            if (rowsAdvanced != 1)
            {
                throw NavigationFailure(
                    $"重叠签名扫描收到无效滚动结论：rowsAdvanced={rowsAdvanced}。",
                    scroll.DiagnosticDetails);
            }

            visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            lastScrollChangedViewport = true;
            scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, decision={signatureEvidence.Decision}, signatureBestRows={signatureEvidence.BestRows}, signatureBestScore={signatureEvidence.BestScore}, signatureSecondScore={signatureEvidence.SecondScore}, signatureMargin={signatureEvidence.Margin}, reason={signatureEvidence.Reason}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            runtimeState.ProfileHealth.ObserveOverlap(false, scanLog.Write);
        }

        if (scannedLogicalRows.Count < totalRows)
        {
            var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
            throw NavigationFailure($"重叠签名扫描达到保护上限仍未完成：scannedRows={scannedLogicalRows.Count}/{totalRows}, missing={string.Join(",", missing)}。");
        }

        scanLog.Write($"End: overlap-signature-page completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已完成重叠签名扫描：访问 {counters.Visited}/{inventoryCount}。");
    }

}
