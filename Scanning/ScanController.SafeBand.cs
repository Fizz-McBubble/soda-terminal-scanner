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
    private static RowScanResult CommitBufferedRow(
        BufferedRowCapture bufferedRow,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int logicalRow,
        CancellationToken token)
    {
        foreach (var cell in bufferedRow.Cells.OrderBy(cell => cell.Column))
        {
            token.ThrowIfCancellationRequested();
            if (options.MaxItems > 0 && counters.Queued >= options.MaxItems)
            {
                scanLog.Write($"End: max items reached while committing buffered row. MaxItems={options.MaxItems}, queued={counters.Queued}.");
                return RowScanResult.Stop;
            }

            Interlocked.Increment(ref counters.Visited);
            counters.Traversal.Visit(logicalRow, cell.Column, cell.Rarity, cell.HasCapture);
            if (!cell.HasCapture)
            {
                Report(progress, counters, $"跳过 {cell.Rarity} 级驱动盘。");
                continue;
            }

            var image = cell.TakeImage();
            var itemIndex = Interlocked.Increment(ref counters.Queued);
            var enqueued = false;
            try
            {
                queue.Add(new DiscCapture(
                    itemIndex,
                    cell.Rarity,
                    image,
                    cell.Rois,
                    cell.TargetVerificationKind,
                    cell.TakeLockEvidence()), token);
                enqueued = true;
            }
            finally
            {
                if (!enqueued)
                {
                    image.Dispose();
                }
            }

            scanLog.WriteEvent("EDGE_CLICK_BUFFER_COMMIT", $"logicalRow={logicalRow}, col={cell.Column}, index={itemIndex}, rarity={cell.Rarity}, queued={counters.Queued}, visited={counters.Visited}");
            Report(progress, counters, options.ShowDebugImages ? $"缓冲行入队 #{itemIndex}。" : "");
        }

        return RowScanResult.Completed;
    }

    private static ScanTraversalMode ResolveTraversalMode(ScanOptions options, ScanProfile profile)
    {
        if (options.TraversalMode != ScanTraversalMode.FromProfile)
        {
            return options.TraversalMode;
        }

        return Enum.TryParse<ScanTraversalMode>(profile.TraversalMode, ignoreCase: true, out var parsed) && parsed != ScanTraversalMode.FromProfile
            ? parsed
            : ScanTraversalMode.SafeBandViewport;
    }

    private static RowAdvanceMode ResolveRowAdvanceMode(ScanOptions options, ScanProfile profile)
    {
        if (options.RowAdvanceMode.HasValue)
        {
            return options.RowAdvanceMode.Value;
        }

        var normalized = (profile.RowAdvanceMode ?? "").Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<RowAdvanceMode>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : RowAdvanceMode.WheelVerified;
    }


    private static async Task<SafeBandMoveResult> MoveLogicalRowIntoSafeBandAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        int logicalRow,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        var desiredTop = ChooseSafeVisibleTop(logicalRow, totalRows, maxVisibleTop);
        var scrolled = false;
        while (visibleTopLogicalRow < desiredTop)
        {
            var result = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                scrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                allowTwoRows: false,
                scanLog,
                scrollTickDelayOverrideMs,
                token);
            if (!result.Success)
            {
                throw NavigationFailure($"安全带逐行滚动失败：{result.Message}", result.DiagnosticDetails);
            }

            var rowsAdvanced = result.RowsAdvanced;
            if (rowsAdvanced <= 0)
            {
                throw NavigationFailure("安全带逐行滚动没有得到可提交的正向位移。", result.DiagnosticDetails);
            }
            var nextVisibleTop = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            if (rowsAdvanced != 1)
            {
                scanLog.Write($"Safe-band viewport detected wheel overshoot: previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}.");
                if (nextVisibleTop > desiredTop)
                {
                    if (!profile.AllowScrollRecovery)
                    {
                        scanLog.WriteEvent("ROW_SCROLL_STRICT_STOP", $"previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recoveryAllowed={profile.AllowScrollRecovery}");
                        throw NavigationFailure($"安全带逐行滚动越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}。当前为单向严格模式，已禁止自动上翻恢复；请重试。");
                    }

                    var recovery = await ScrollSafeBandOneRowUpAsync(
                        window,
                        profile,
                        listGridRect,
                        rowSignatureRects,
                        nextVisibleTop,
                        scanLog,
                        scrollTickDelayOverrideMs,
                        token);
                    if (!recovery.Success)
                    {
                        throw NavigationFailure($"安全带逐行滚动越过目标行且回退失败：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recovery={recovery.Message}。为避免重复读取，本次停止。");
                    }

                    var rowsRecovered = recovery.RowsAdvanced;
                    if (rowsRecovered <= 0)
                    {
                        throw NavigationFailure("安全带回退没有得到可提交的位移。", recovery.DiagnosticDetails);
                    }
                    nextVisibleTop = Math.Max(1, nextVisibleTop - rowsRecovered);
                    if (nextVisibleTop > desiredTop)
                    {
                        throw NavigationFailure($"安全带逐行滚动越过目标行且回退后仍越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, rowsRecovered={rowsRecovered}, recoveredTop={nextVisibleTop}, desiredTop={desiredTop}。为避免重复读取，本次停止。");
                    }

                    scanLog.WriteEvent("ROW_SCROLL_RECOVERY_ACCEPTED", $"previousTop={visibleTopLogicalRow}, desiredTop={desiredTop}, recoveredTop={nextVisibleTop}, rowsAdvanced={rowsAdvanced}, rowsRecovered={rowsRecovered}");
                }
            }

            visibleTopLogicalRow = nextVisibleTop;
            scrolled = true;
        }

        var visualRow = logicalRow - visibleTopLogicalRow + 1;
        if (!IsSafeBandClick(visualRow, visibleTopLogicalRow, maxVisibleTop, logicalRow))
        {
            scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"logicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            throw NavigationFailure($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {visualRow} 行。");
        }

        return new SafeBandMoveResult(visibleTopLogicalRow, scrolled);
    }

    private static int ChooseSafeVisibleTop(int logicalRow, int totalRows, int maxVisibleTop)
    {
        if (logicalRow <= 3)
        {
            return 1;
        }

        if (logicalRow == totalRows)
        {
            return maxVisibleTop;
        }

        return Math.Clamp(logicalRow - 2, 1, maxVisibleTop);
    }

    private static bool IsSafeBandClick(int visualRow, int visibleTopLogicalRow, int maxVisibleTop, int logicalRow)
    {
        if (visualRow == 3)
        {
            return true;
        }

        var atTop = visibleTopLogicalRow == 1;
        var atBottom = visibleTopLogicalRow == maxVisibleTop;
        return (visualRow == 1 && atTop && logicalRow == 1)
            || (visualRow == 2 && atTop && logicalRow == 2)
            || (visualRow == 4 && atBottom);
    }

    private static IEnumerable<OverlapRowCandidate> BuildOverlapScanCandidates(
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        int visibleRows,
        ISet<int> scannedLogicalRows)
    {
        var atTop = visibleTopLogicalRow == 1;
        var atBottom = visibleTopLogicalRow == maxVisibleTop;
        var preferredVisualRows = atTop && atBottom
            ? new[] { 1, 2, 3, 4 }
            : atTop
            ? new[] { 1, 2, 3 }
            : atBottom
                ? new[] { 2, 3, 4 }
                : new[] { 2, 3 };

        foreach (var visualRow in preferredVisualRows)
        {
            if (visualRow < 1 || visualRow > visibleRows)
            {
                continue;
            }

            var logicalRow = visibleTopLogicalRow + visualRow - 1;
            if (logicalRow < 1 || logicalRow > totalRows || scannedLogicalRows.Contains(logicalRow))
            {
                continue;
            }

            yield return new OverlapRowCandidate(logicalRow, visualRow);
        }
    }

    private static OverlapGapCoverage VerifyOverlapGapCoverage(
        IReadOnlySet<int> scannedLogicalRows,
        int beforeTop,
        int rowsAdvanced,
        int visibleRows,
        int totalRows)
    {
        if (rowsAdvanced <= 1)
        {
            return new OverlapGapCoverage(true, [], []);
        }

        var leavingRows = Enumerable.Range(beforeTop, Math.Min(rowsAdvanced, visibleRows))
            .Where(row => row >= 1 && row <= totalRows)
            .ToArray();
        var missingRows = leavingRows
            .Where(row => !scannedLogicalRows.Contains(row))
            .ToArray();
        return new OverlapGapCoverage(missingRows.Length == 0, leavingRows, missingRows);
    }

    private static void LogOverlapViewport(
        ScanLog scanLog,
        int iteration,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        IReadOnlyList<RowVisualSignature> rowSignatures)
    {
        var state = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
        var rows = string.Join(",", rowSignatures.Select((signature, index) =>
        {
            var visualRow = index + 1;
            var logicalRow = visibleTopLogicalRow + index;
            var inRange = logicalRow <= totalRows;
            return $"v{visualRow}:l{(inRange ? logicalRow.ToString() : "NA")}:{signature.Hash:X16}";
        }));
        scanLog.WriteEvent("OVERLAP_VIEWPORT", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, state={state}, rows=[{rows}]");
    }

    private static string ViewportStateLabel(int visibleTopLogicalRow, int maxVisibleTop)
    {
        if (visibleTopLogicalRow == 1 && visibleTopLogicalRow == maxVisibleTop)
        {
            return "TopAndBottom";
        }

        if (visibleTopLogicalRow == 1)
        {
            return "Top";
        }

        return visibleTopLogicalRow == maxVisibleTop ? "Bottom" : "Middle";
    }


    private readonly record struct OverlapRowCandidate(int LogicalRow, int VisualRow);


    private readonly record struct SafeBandMoveResult(int VisibleTopLogicalRow, bool Scrolled);


    private readonly record struct OverlapGapCoverage(bool Safe, int[] CoveredRows, int[] MissingRows);

}
