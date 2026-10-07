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
    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        var delay = Math.Max(120, EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs) * 2);
        ScrollRowsResult last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await ScrollSafeBandOneRowAsync(window, profile, scrollAcceptMode, listGridRect, rowSignatureRects, visibleTopLogicalRow, maxVisibleTop, allowTwoRows, scanLog, scrollTickDelayOverrideMs, token);
            if (last.Success)
            {
                return last;
            }

            if (!last.Retryable)
            {
                scanLog.Write($"Safe-band scroll stopped without retry: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
                return last;
            }

            if (attempt < 3)
            {
                scanLog.Write($"Safe-band scroll retry {attempt + 1}/3: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
                await Task.Delay(delay, token);
                delay = Math.Min(360, delay + 120);
            }
        }

        scanLog.Write($"Safe-band scroll failed after retries: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
        return last;
    }

    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        if (visibleTopLogicalRow >= maxVisibleTop)
        {
            return ScrollRowsResult.Fail($"already at bottom visibleTopLogicalRow={visibleTopLogicalRow}", retryable: false);
        }

        return await ScrollVerifiedRowShiftAsync(
            window,
            profile,
            scrollAcceptMode,
            listGridRect,
            rowSignatureRects,
            visibleTopLogicalRow,
            maxVisibleTop,
            upward: false,
            allowTwoRows,
            scanLog,
            scrollTickDelayOverrideMs,
            token);
    }

    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowUpAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        if (visibleTopLogicalRow <= 1)
        {
            return ScrollRowsResult.Fail($"already at top visibleTopLogicalRow={visibleTopLogicalRow}", retryable: false);
        }

        return await ScrollVerifiedRowShiftAsync(
            window,
            profile,
            ScrollAcceptMode.Safe,
            listGridRect,
            rowSignatureRects,
            visibleTopLogicalRow,
            maxVisibleTop: visibleTopLogicalRow,
            upward: true,
            allowTwoRows: false,
            scanLog,
            scrollTickDelayOverrideMs,
            token);
    }


    private static RowScrollVerification VerifyOneRowDown(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(1, 0), (2, 1), (3, 2)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(2, 0), (3, 1)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static RowScrollVerification VerifyOneRowUp(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(0, 1), (1, 2), (2, 3)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(0, 2), (1, 3)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static int ScrollTickDelay(ScanProfile profile)
    {
        return Math.Max(Math.Max(1, profile.MinScrollTickDelayMs), profile.ScrollTickDelayMs);
    }

    private static int EffectiveScrollTickDelay(ScanProfile profile, int overrideMilliseconds)
    {
        return overrideMilliseconds > 0
            ? Math.Clamp(overrideMilliseconds, 50, 80)
            : ScrollTickDelay(profile);
    }

    private static int ScrollSmallTickLimit(ScanProfile profile)
    {
        return Math.Max(1, Math.Min(ScrollMaxSmallTicks, Math.Abs(-120 / Math.Max(1, Math.Abs(profile.ScrollTickDelta))) + 2));
    }

    private static int ScrollSingleTickDelay(ScanProfile profile)
    {
        return ScrollTickDelay(profile);
    }

    private static string FormatOptionalMs(double? value)
    {
        return value.HasValue ? value.Value.ToString("F1") : "NA";
    }

    private static string FormatOptionalInt(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "NA";
    }

    private static int AverageSignatureDistance(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows, IReadOnlyList<(int Before, int After)> pairs)
    {
        var sum = 0;
        var count = 0;
        foreach (var pair in pairs)
        {
            if (pair.Before < beforeRows.Count && pair.After < afterRows.Count)
            {
                sum += RowVisualSignatureExtractor.Distance(beforeRows[pair.Before], afterRows[pair.After]);
                count++;
            }
        }

        return count == 0 ? int.MaxValue : sum / count;
    }

    private static async Task<ScrollRowsResult> ScrollRowsWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        int rows,
        ScanLog scanLog,
        CancellationToken token)
    {
        var first = await ScrollRowsAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, rows, scanLog, token);
        if (first.Success)
        {
            return first;
        }

        scanLog.Write($"Scroll retry: rows={rows}, firstFailure={first.Message}");
        await Task.Delay(ScrollTickDelay(profile), token);
        var second = await ScrollRowsAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, rows, scanLog, token);
        if (!second.Success)
        {
            scanLog.Write($"Scroll failed after retry: rows={rows}, secondFailure={second.Message}");
        }

        return second;
    }

    private static async Task<ScrollRowsResult> ScrollRowsAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        int rows,
        ScanLog scanLog,
        CancellationToken token)
    {
        if (rows <= 0)
        {
            return ScrollRowsResult.Ok("no rows requested");
        }

        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        scanLog.WriteEvent("SCROLL_ROWS_START", $"rows={rows}, calibratedRows={calibration.CalibratedRows}, avgTicks={calibration.AverageTicksPerRow:F2}, listGrid={listGridRect}, rowProbe={rowAlignProbeRect}");
        if (calibration.CalibratedRows >= Math.Max(1, profile.CalibrationRows) && rows > 1 && calibration.AverageTicksPerRow > 0)
        {
            var ticks = Math.Max(1, (int)Math.Round(calibration.AverageTicksPerRow * rows));
            var before = CaptureSignature(window, listGridRect);
            for (var i = 0; i < ticks; i++)
            {
                token.ThrowIfCancellationRequested();
                scanLog.WriteEvent("WHEEL_TICK", $"mode=fast, requestedRows={rows}, tick={i + 1}/{ticks}, delta={profile.ScrollTickDelta}, avgTicks={calibration.AverageTicksPerRow:F2}");
                window.MouseWheel(profile.ScrollTickDelta);
                await Task.Delay(Math.Min(20, Math.Max(1, profile.ScrollTickDelayMs / 4)), token);
            }

            await Task.Delay(Math.Max(40, profile.ScrollTickDelayMs), token);
            var after = CaptureSignature(window, listGridRect);
            var movedDistance = SignatureDistance(before, after);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            scanLog.Write($"Scroll fast: rows={rows}, ticks={ticks}, avg={calibration.AverageTicksPerRow:F2}, movedDistance={movedDistance}.");
            return movedDistance > ListMovementTolerance
                ? ScrollRowsResult.Ok($"fast rows={rows}, ticks={ticks}")
                : ScrollRowsResult.Fail($"fast scroll did not move list enough, distance={movedDistance}");
        }

        for (var i = 0; i < rows; i++)
        {
            var result = await ScrollOneRowAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, scanLog, token);
            if (!result.Success)
            {
                return result;
            }
        }

        return ScrollRowsResult.Ok($"slow rows={rows}");
    }

    private static async Task<ScrollRowsResult> ScrollOneRowAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        ScanLog scanLog,
        CancellationToken token)
    {
        var beforeGrid = CaptureSignature(window, listGridRect);
        var beforeAlign = CaptureSignature(window, rowAlignProbeRect);
        var maxTicks = Math.Max(1, profile.ScrollMaxTicksPerRow);
        scanLog.WriteEvent("SCROLL_ONE_ROW_START", $"maxTicks={maxTicks}, delta={profile.ScrollTickDelta}, calibratedRows={calibration.CalibratedRows}, avgTicks={calibration.AverageTicksPerRow:F2}, beforeGrid={beforeGrid.Hash:X16}, beforeAlign={beforeAlign.Hash:X16}");
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            token.ThrowIfCancellationRequested();
            scanLog.WriteEvent("WHEEL_TICK", $"mode=calibrate-one-row, tick={tick}/{maxTicks}, delta={profile.ScrollTickDelta}");
            window.MouseWheel(profile.ScrollTickDelta);
            await Task.Delay(Math.Max(1, profile.ScrollTickDelayMs), token);

            var currentGrid = CaptureSignature(window, listGridRect);
            var currentAlign = CaptureSignature(window, rowAlignProbeRect);
            var gridDistance = SignatureDistance(beforeGrid, currentGrid);
            var alignDistance = SignatureDistance(beforeAlign, currentAlign);
            if (gridDistance > ListMovementTolerance || alignDistance > ListMovementTolerance)
            {
                await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
                calibration.Record(tick);
                scanLog.WriteEvent("SCROLL_ONE_ROW_DONE", $"ticks={tick}, gridDistance={gridDistance}, alignDistance={alignDistance}, afterGrid={currentGrid.Hash:X16}, afterAlign={currentAlign.Hash:X16}, avg={calibration.AverageTicksPerRow:F2}, calibratedRows={calibration.CalibratedRows}");
                scanLog.Write($"Scroll one row: ticks={tick}, gridDistance={gridDistance}, alignDistance={alignDistance}, avg={calibration.AverageTicksPerRow:F2}, calibratedRows={calibration.CalibratedRows}.");
                return ScrollRowsResult.Ok($"one row ticks={tick}");
            }
        }

        scanLog.WriteEvent("SCROLL_ONE_ROW_FAIL", $"maxTicks={maxTicks}, delta={profile.ScrollTickDelta}");
        return ScrollRowsResult.Fail($"list did not move after {maxTicks} wheel ticks");
    }

    private static async Task WaitForListStableAsync(GameWindow window, ScanProfile profile, Rectangle listGridRect, ScanLog scanLog, CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(
            Math.Max(1, profile.MinListStableTimeoutMs),
            Math.Min(profile.LoadTimeoutMs, Math.Max(1, profile.ListStableTimeoutMs))));
        var interval = TimeSpan.FromMilliseconds(Math.Max(15, profile.LoadPollMs));
        var start = DateTime.UtcNow;
        var stableFrames = 0;
        var requiredStableFrames = Math.Max(1, profile.ListStableConfirmFrames);
        var previous = CaptureSignature(window, listGridRect);

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token);
            var current = CaptureSignature(window, listGridRect);
            if (SignatureDistance(previous, current) <= ListStableTolerance)
            {
                stableFrames++;
                if (stableFrames >= requiredStableFrames)
                {
                    return;
                }
            }
            else
            {
                stableFrames = 0;
            }

            previous = current;
        }

        scanLog.Write($"List stable wait timed out after {timeout.TotalMilliseconds:F0}ms; continuing with best effort.");
    }



    private sealed class ScrollCalibrationState
    {
        public ScrollCalibrationState(ImageSignature initialSignature)
        {
            InitialSignature = initialSignature;
        }

        public ImageSignature InitialSignature { get; }
        public int CalibratedRows { get; private set; }
        public double AverageTicksPerRow { get; private set; }

        public void Record(int ticks)
        {
            CalibratedRows++;
            AverageTicksPerRow = ((AverageTicksPerRow * (CalibratedRows - 1)) + ticks) / CalibratedRows;
        }
    }


    private readonly record struct RowScrollVerification(int OneRowScore, int NoMoveScore, int TwoRowScore);


    private readonly record struct ScrollRowsResult(
        bool Success,
        string Message,
        int RowsAdvanced = 0,
        bool Retryable = true,
        RowVisualSignature[]? AfterRows = null,
        RowAdvanceEvidence Evidence = default,
        IReadOnlyDictionary<string, object?>? DiagnosticDetails = null)
    {
        public static ScrollRowsResult Ok(
            string message,
            int rowsAdvanced = 0,
            RowVisualSignature[]? afterRows = null,
            RowAdvanceEvidence evidence = default,
            IReadOnlyDictionary<string, object?>? diagnosticDetails = null) =>
            new(true, message, rowsAdvanced, AfterRows: afterRows, Evidence: evidence, DiagnosticDetails: diagnosticDetails);

        public static ScrollRowsResult Fail(
            string message,
            bool retryable = true,
            RowAdvanceEvidence evidence = default,
            IReadOnlyDictionary<string, object?>? diagnosticDetails = null) =>
            new(false, message, Retryable: retryable, Evidence: evidence, DiagnosticDetails: diagnosticDetails);
    }

}
