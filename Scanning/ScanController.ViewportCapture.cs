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
    private static async Task ProduceCapturesSafeBandViewportAsync(
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

        scanLog.Write($"Traversal: safe-band viewport. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, maxVisibleTop={maxVisibleTop}, listGrid={listGridRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var visibleTopLogicalRow = 1;
        for (var logicalRow = 1; logicalRow <= totalRows; logicalRow++)
        {
            token.ThrowIfCancellationRequested();
            var moveResult = await MoveLogicalRowIntoSafeBandAsync(
                window,
                profile,
                options.ScrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                totalRows,
                logicalRow,
                scanLog,
                options.ScrollTickDelayOverrideMs,
                token);
            visibleTopLogicalRow = moveResult.VisibleTopLogicalRow;

            var visualRow = logicalRow - visibleTopLogicalRow + 1;
            var viewportState = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
            var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
            scanLog.WriteEvent("VIEWPORT_STATE", $"targetLogicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={viewportState}, columns={maxColumns}, visited={counters.Visited}, queued={counters.Queued}");
            Report(progress, counters, $"安全带扫描：逻辑行 {logicalRow}/{totalRows}，视觉第 {visualRow} 行，{viewportState}。");

            var rowResult = await ScanVisualRowAsync(
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
                enforceSafeBand: true,
                visibleTopLogicalRow: visibleTopLogicalRow,
                maxVisibleTop: maxVisibleTop,
                afterScroll: moveResult.Scrolled,
                postScrollFirstCell: moveResult.Scrolled
                );
            if (rowResult == RowScanResult.Stop)
            {
                return;
            }
        }

        scanLog.Write($"End: safe-band viewport completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已到达安全带扫描末尾：访问 {counters.Visited}/{inventoryCount}。");
    }

    private static async Task ProduceCapturesCalibratedPageAsync(
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
        var visibleRows = Math.Max(1, profile.VisibleRows);
        var totalRows = Math.Max(1, (int)Math.Ceiling(inventoryCount / (double)columns));
        var lastRowColumns = inventoryCount % columns == 0 ? columns : inventoryCount % columns;
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, visibleRows, columns));
        var rowAlignProbeRect = ProfileRectangleOrFallback(window, profile, "rowAlignProbeRect", listGridRect);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var calibration = new ScrollCalibrationState(CaptureSignature(window, rowAlignProbeRect));

        scanLog.Write($"Traversal: calibrated-page. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, listGrid={listGridRect}, rowProbe={rowAlignProbeRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}, maxTicksPerRow={profile.ScrollMaxTicksPerRow}, calibrationRows={profile.CalibrationRows}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var scannedRows = 0;
        var startVisualRow = 1;
        var page = 1;
        while (scannedRows < totalRows)
        {
            token.ThrowIfCancellationRequested();
            var rowsThisPage = Math.Min(visibleRows - startVisualRow + 1, totalRows - scannedRows);
            var firstLogicalRow = scannedRows + 1;
            var lastLogicalRow = scannedRows + rowsThisPage;
            scanLog.Write($"Page {page}: scan visual rows {startVisualRow}-{startVisualRow + rowsThisPage - 1}, logical rows {firstLogicalRow}-{lastLogicalRow}, visited={counters.Visited}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
            Report(progress, counters, $"扫描第 {page} 页：逻辑行 {firstLogicalRow}-{lastLogicalRow}。");

            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

            for (var i = 0; i < rowsThisPage; i++)
            {
                var logicalRow = scannedRows + 1;
                var visualRow = startVisualRow + i;
                var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
                var rowResult = await ScanVisualRowAsync(
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
                    page,
                    visualRow,
                    isBottom: logicalRow == totalRows,
                    maxColumns,
                    logicalRow,
                    treatBlankAsEnd: false,
                    panelChangeProbeRect,
                    token
                    );
                if (rowResult == RowScanResult.Stop)
                {
                    return;
                }

                scannedRows++;
            }

            if (scannedRows >= totalRows)
            {
                break;
            }

            var remainingRows = totalRows - scannedRows;
            var scrollRows = Math.Min(visibleRows, remainingRows);
            startVisualRow = visibleRows - scrollRows + 1;
            scanLog.Write($"Scroll page {page}: rows={scrollRows}, nextStartVisualRow={startVisualRow}, remainingRows={remainingRows}.");
            Report(progress, counters, $"翻页 {scrollRows} 行，准备扫描下一页。");

            var scrollResult = await ScrollRowsWithRetryAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, scrollRows, scanLog, token);
            if (!scrollResult.Success)
            {
                throw NavigationFailure($"滚动失败：{scrollResult.Message}");
            }

            page++;
        }

        scanLog.Write($"End: calibrated-page completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已到达计算末尾：访问 {counters.Visited}/{inventoryCount}。");
    }

    private static async Task ProduceCapturesLegacyThirdRowAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int? inventoryCount,
        CancellationToken token)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, 4, Math.Max(1, profile.VisibleColumns)));
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var totalRows = inventoryCount is > 0 ? (int)Math.Ceiling(inventoryCount.Value / 9d) : (int?)null;
        var maxScrollRows = Math.Max(0, (totalRows ?? 69) - 4);
        scanLog.Write($"Traversal: legacy third-row mode. inventoryCount={inventoryCount?.ToString() ?? "unknown"}, totalRows={totalRows?.ToString() ?? "unknown"}, maxScrollRows={maxScrollRows}, listGrid={listGridRect}, wheelDelta={profile.ScrollWheelDelta}.");

        var pass = 0;
        var atBottom = false;
        var scrollRows = 0;
        for (var row = 1; row <= 4; row++)
        {
            token.ThrowIfCancellationRequested();
            pass++;
            var bottomBefore = atBottom;
            scanLog.Write($"Pass {pass}: scan visual row {row}, bottomBefore={bottomBefore}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}");
            Report(progress, counters, $"扫描第 {pass} 轮：可视第 {row} 行{(bottomBefore ? "，已到底" : "")}。");

            var rowResult = await ScanVisualRowAsync(
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
                pass,
                row,
                bottomBefore,
                Math.Max(1, profile.VisibleColumns),
                logicalRow: null,
                treatBlankAsEnd: true,
                panelChangeProbeRect,
                token
                );
            if (rowResult == RowScanResult.Stop)
            {
                return;
            }

            if (row == 3)
            {
                if (!atBottom && scrollRows < maxScrollRows)
                {
                    var before = CaptureSignature(window, listGridRect);
                    scanLog.Write($"Scroll: legacy third-row wheel after pass {pass}, delta={profile.ScrollWheelDelta}.");
                    scanLog.WriteEvent("LEGACY_WHEEL", $"afterPass={pass}, visualRow={row}, delta={profile.ScrollWheelDelta}");
                    window.MouseWheel(profile.ScrollWheelDelta);
                    await Task.Delay(profile.WheelDelayMs, token);
                    var after = CaptureSignature(window, listGridRect);
                    var distance = SignatureDistance(before, after);
                    atBottom = distance <= ListStableTolerance;
                    scanLog.WriteEvent("LEGACY_BOTTOM_MOTION", $"afterPass={pass}, signatureDistance={distance}, atBottom={atBottom}");
                    if (!atBottom)
                    {
                        scrollRows++;
                        row--;
                    }
                }
                else
                {
                    atBottom = true;
                    scanLog.Write("Bottom reached after visual row 3; scanning final visual row 4 next.");
                }
            }
        }

        scanLog.Write("End: legacy visual row loop completed.");
        Report(progress, counters, "滚动条已到底，扫描结束。");
    }

}
