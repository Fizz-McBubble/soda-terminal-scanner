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
    private static async Task<RowScanResult> ScanVisualRowAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        PointF offset,
        PointF step,
        Rectangle panelRect,
        CvRect[] rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        int pass,
        int row,
        bool isBottom,
        int maxColumns,
        int? logicalRow,
        bool treatBlankAsEnd,
        Rectangle panelChangeProbeRect,
        CancellationToken token,
        bool enforceSafeBand = false,
        int visibleTopLogicalRow = 1,
        int maxVisibleTop = 1,
        bool afterScroll = false,
        bool postScrollFirstCell = false,
        int startColumn = 1,
        RowDetailFingerprintBuilder? rowFingerprint = null,
        BufferedRowCapture? bufferedRow = null,
        NativeEdgePostScrollSelection? preselectedPostScrollCell = null)
    {
        var currentY = offset.Y + step.Y * row;
        ImageSignature[]? previousPanelSignatures = CaptureCurrentPanelSignatures(
            window,
            panelRect,
            panelChangeProbeRect,
            rois);
        startColumn = Math.Clamp(startColumn, 1, maxColumns);
        if (startColumn > 1)
        {
            var refreshColumn = startColumn - 1;
            var refresh = new PointF(offset.X + step.X * refreshColumn, currentY);
            var refreshPoint = DriveDiscSelectionGeometry.Center(window.ToScreenPoint(refresh), window.ClientScreenRect, profile);
            scanLog.WriteEvent("ROW_RESUME_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, startColumn={startColumn}, refreshColumn={refreshColumn}, point={refreshPoint}, visibleTopLogicalRow={visibleTopLogicalRow}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            window.MoveCursor(refreshPoint);
            window.LeftClick(refreshPoint);
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            previousPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        }

        var postScrollFirstCellPending = postScrollFirstCell;
        var bootstrappedColumns = new HashSet<int>();
        for (var col = startColumn; col <= maxColumns; col++)
        {
            token.ThrowIfCancellationRequested();
            window.VerifyTraversalPosition();
            if (bootstrappedColumns.Remove(col))
            {
                scanLog.WriteEvent("FIRST_PAIR_CONSUMED_CELL_SKIPPED", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                continue;
            }

            if (bufferedRow is null && options.MaxItems > 0 && counters.Queued >= options.MaxItems)
            {
                scanLog.Write($"End: max items reached. MaxItems={options.MaxItems}, queued={counters.Queued}.");
                Report(progress, counters, $"已达到读取上限：{options.MaxItems}");
                return RowScanResult.Stop;
            }

            if (enforceSafeBand && !IsSafeBandClick(row, visibleTopLogicalRow, maxVisibleTop, logicalRow ?? -1))
            {
                scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
                throw NavigationFailure($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {row} 行。");
            }

            var ocrBacklogBeforeClick = CurrentOcrBacklog(counters);
            var throttleDecision = runtimeState.OcrThrottle?.Observe(ocrBacklogBeforeClick);
            var adaptiveThrottleMs = throttleDecision?.DelayMilliseconds ?? 0;
            if (throttleDecision?.Changed == true)
            {
                scanLog.WriteEvent("ADAPTIVE_OCR_THROTTLE", $"backlog={ocrBacklogBeforeClick}, delayMs={adaptiveThrottleMs}, highThreshold={throttleDecision.HighBacklogThreshold}, lowThreshold={throttleDecision.LowBacklogThreshold}");
            }

            if (adaptiveThrottleMs > 0)
            {
                await Task.Delay(adaptiveThrottleMs, token);
            }

            var sw = Stopwatch.StartNew();
            var currentPostScrollFirstCell = postScrollFirstCellPending;
            var isPreselectedPostScrollCell = preselectedPostScrollCell?.Matches(logicalRow, row, col) == true;
            var current = new PointF(offset.X + step.X * col, currentY);
            var rarityAnchor = window.ToScreenPoint(current);
            var clickPoint = DriveDiscSelectionGeometry.Center(rarityAnchor, window.ClientScreenRect, profile);
            var gridCellHash = rowFingerprint is null
                ? 0UL
                : CaptureGridCellFingerprint(window, rarityAnchor, step);
            var viewportKnown = logicalRow.HasValue && maxVisibleTop > 1;
            var visibleTopText = viewportKnown ? visibleTopLogicalRow.ToString() : "unknown";
            var viewportStateText = viewportKnown ? ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop) : "unknown";
            scanLog.WriteEvent("CELL_MOVE", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, normalized=({current.X:F5},{current.Y:F5}), queued={counters.Queued}, visited={counters.Visited}");
            window.MoveCursor(clickPoint);

            var rarityProbeWatch = Stopwatch.StartNew();
            var rarityProbe = DetectRarityAround(window, profile, rarityAnchor);
            if (rarityProbe.Rarity is null)
            {
                await Task.Delay(25, token);
                rarityProbe = DetectRarityAround(window, profile, rarityAnchor);
            }
            rarityProbeWatch.Stop();

            var rarity = rarityProbe.Rarity;
            if (rarity is null || options.ShowDebugImages || counters.Visited % 50 == 0)
            {
                scanLog.Write($"Probe pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, point={clickPoint}, rarity={rarity ?? "null"}, best={ColorText(rarityProbe.BestColor)}, bestMatch={rarityProbe.BestCandidate}, score={rarityProbe.BestScore}, secondScore={rarityProbe.SecondScore}, margin={rarityProbe.Margin}, fullScan={rarityProbe.FullScan}, bottom={isBottom}");
            }
            if (rarity is null)
            {
                if (treatBlankAsEnd)
                {
                    scanLog.Write($"End: blank cell at pass={pass}, visualRow={row}, col={col}. This is treated as list end after retry.");
                    Report(progress, counters, $"第 {pass} 轮第 {row} 行第 {col} 列未检测到驱动盘卡片，扫描结束。详情见本次输出目录的 scan.log。");
                    return RowScanResult.Stop;
                }

                throw NavigationFailure($"预期存在驱动盘，但第 {logicalRow} 行第 {col} 列未检测到品质颜色。可能发生漏扫或列表未稳定。");
            }

            if (bufferedRow is null)
            {
                counters.Visited++;
                counters.Traversal.Visit(logicalRow, col, rarity, options.Rarities.Contains(rarity));
            }

            var selectedForExport = options.Rarities.Contains(rarity);
            if (!selectedForExport)
            {
                rowFingerprint?.AddGridCell(col, rarity, gridCellHash);
                if (bufferedRow is null)
                {
                    Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                }
                else
                {
                    bufferedRow.AddSkipped(col, rarity);
                }

                continue;
            }

            var firstQueuedItem = bufferedRow is null && selectedForExport && counters.Queued == 0;
            var secondColumn = col + 1;
            if (!isPreselectedPostScrollCell && bufferedRow is null && firstQueuedItem && secondColumn <= maxColumns)
            {
                var secondClientPoint = new PointF(offset.X + step.X * secondColumn, currentY);
                var secondClickPoint = window.ToScreenPoint(secondClientPoint);
                var secondRarityProbe = DetectRarityAround(window, profile, secondClickPoint);
                if (secondRarityProbe.Rarity is null)
                {
                    await Task.Delay(25, token);
                    secondRarityProbe = DetectRarityAround(window, profile, secondClickPoint);
                }

                var secondRarity = secondRarityProbe.Rarity;
                if (secondRarity is not null && options.Rarities.Contains(secondRarity))
                {
                    using var bootstrap = await CaptureFirstPairAsync(
                        window,
                        profile,
                        panelRect,
                        rois,
                        statOffset,
                        statRowBackground,
                        panelChangeProbeRect,
                        runtimeState,
                        scanLog,
                        offset,
                        step,
                        pass,
                        row,
                        col,
                        secondColumn,
                        maxColumns,
                        logicalRow,
                        visibleTopText,
                        viewportStateText,
                        token);

                    var pair = new[]
                    {
                        new FirstPairCommit(col, rarity, bootstrap.First),
                        new FirstPairCommit(secondColumn, secondRarity, bootstrap.Second)
                    };
                    var commitCount = options.MaxItems > 0
                        ? Math.Min(pair.Length, options.MaxItems - counters.Queued)
                        : pair.Length;
                    for (var pairIndex = 0; pairIndex < commitCount; pairIndex++)
                    {
                        var commit = pair[pairIndex];
                        if (pairIndex > 0)
                        {
                            Interlocked.Increment(ref counters.Visited);
                            counters.Traversal.Visit(logicalRow, commit.Column, commit.Rarity, true);
                        }

                        ObserveAcceptedPanel(runtimeState, commit.Capture, scanLog, postScrollFirstCell: false);
                        var pairPanelImage = commit.Capture.TakeImage();
                        var pairCaptureRois = rois.Take(commit.Capture.VisibleRoiCount).ToArray();
                        rowFingerprint?.AddDetailPanel(commit.Column, commit.Rarity, pairPanelImage, pairCaptureRois);
                        var pairDebugImage = options.ShowDebugImages ? (Bitmap)pairPanelImage.Clone() : null;
                        var pairItemIndex = Interlocked.Increment(ref counters.Queued);
                        var pairEnqueued = false;
                        var pairPoint = window.ToScreenPoint(new PointF(offset.X + step.X * commit.Column, currentY));
                        var pairLockEvidence = CaptureLockEvidence(window, profile, pairPoint, scanLog);
                        try
                        {
                            queue.Add(new DiscCapture(
                                pairItemIndex,
                                commit.Rarity,
                                pairPanelImage,
                                pairCaptureRois,
                                commit.Capture.TargetVerificationKind,
                                pairLockEvidence), token);
                            pairEnqueued = true;
                        }
                        finally
                        {
                            if (!pairEnqueued)
                            {
                                pairPanelImage.Dispose();
                                pairLockEvidence.Dispose();
                            }
                        }

                        Report(progress, counters, options.ShowDebugImages ? $"首对识别入队 #{pairItemIndex}。" : "", debugImage: pairDebugImage);
                    }

                    previousPanelSignatures = bootstrap.CurrentPanelSignatures;
                    postScrollFirstCellPending = false;
                    if (commitCount > 1)
                    {
                        bootstrappedColumns.Add(secondColumn);
                    }

                    scanLog.WriteEvent(
                        "FIRST_PAIR_COMMIT",
                        $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, firstCol={col}, secondCol={secondColumn}, committed={commitCount}, queued={counters.Queued}, currentPanel={bootstrap.CurrentPanelLabel}");
                    continue;
                }
            }

            if (firstQueuedItem)
            {
                scanLog.WriteEvent(
                    "FIRST_CELL_BASELINE_CAPTURED",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, probes={previousPanelSignatures?.Length ?? 0}");
            }

            scanLog.WriteEvent(
                isPreselectedPostScrollCell ? "CELL_PRESELECTED" : "CELL_CLICK",
                $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, preselected={isPreselectedPostScrollCell}");
            var selectionProbeRect = SelectionProbeRect(window, clickPoint, profile);
            var selectionProbeWatch = Stopwatch.StartNew();
            var beforeSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
            selectionProbeWatch.Stop();
            if (!isPreselectedPostScrollCell)
            {
                window.LeftClick(clickPoint);
            }
            var selectionRoundTripReady = false;
            var sceneAdaptivePanelFloorEligible = !afterScroll && !currentPostScrollFirstCell && row != 2;
            System.Drawing.Point? selectionRefreshPoint = null;
            var refreshCol = DriveDiscSelectionGeometry.WitnessColumn(col, maxColumns);
            if (refreshCol > 0)
            {
                var refresh = new PointF(offset.X + step.X * refreshCol, currentY);
                selectionRefreshPoint = DriveDiscSelectionGeometry.Center(window.ToScreenPoint(refresh), window.ClientScreenRect, profile);
            }
            else
            {
                var refreshRow = row > 1
                    ? row - 1
                    : row < Math.Max(1, profile.VisibleRows)
                        ? row + 1
                        : 0;
                var refreshLogicalRow = logicalRow is null || refreshRow <= 0
                    ? -1
                    : logicalRow.Value + refreshRow - row;
                var refreshRowSafe = refreshRow > 0
                    && (!enforceSafeBand
                        || !viewportKnown
                        || IsSafeBandClick(refreshRow, visibleTopLogicalRow, maxVisibleTop, refreshLogicalRow));
                if (refreshRowSafe)
                {
                    var refresh = new PointF(offset.X + step.X, offset.Y + step.Y * refreshRow);
                    selectionRefreshPoint = DriveDiscSelectionGeometry.Center(window.ToScreenPoint(refresh), window.ClientScreenRect, profile);
                }
            }

            var nativeWheelRecovery = options.RowAdvanceMode == RowAdvanceMode.NativeEdgeClick
                && currentPostScrollFirstCell && !isPreselectedPostScrollCell;
            if (!isPreselectedPostScrollCell
                && (PanelCaptureGate.RequiresFirstCellNeighborRoundTrip(firstQueuedItem) || nativeWheelRecovery))
            {
                scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt=preflight, reason={(nativeWheelRecovery ? "native_zero_move_recovery_target_witness" : "deterministic_neighbor_round_trip")}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (!refresh.RefreshReady)
                {
                    throw new PanelCellCaptureException(
                        pass,
                        row,
                        col,
                        maxColumns,
                        logicalRow,
                        1,
                        window,
                        runtimeState.VisualProfileId,
                        new StalePanelException("首件邻格刷新无法证明详情面板变化。"));
                }

                scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt=preflight, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                previousPanelSignatures = refresh.PanelSignatures;
                selectionProbeRect = refresh.SelectionProbeRect;
                beforeSelectionSignature = refresh.SelectionSignature;
            }

            using var panelCapture = await CaptureStablePanelWithRetryAsync(
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                previousPanelSignatures,
                selectionProbeRect,
                beforeSelectionSignature,
                runtimeState,
                scanLog,
                clickPoint,
                selectionRefreshPoint,
                pass,
                row,
                col,
                maxColumns,
                logicalRow,
                visibleTopText,
                viewportStateText,
                token,
                currentPostScrollFirstCell,
                sceneAdaptivePanelFloorEligible,
                firstQueuedItem,
                selectionRoundTripReady,
                preselectedTargetEvidence: isPreselectedPostScrollCell);
            var panelImage = panelCapture.TakeImage();
            var captureRois = rois.Take(panelCapture.VisibleRoiCount).ToArray();
            previousPanelSignatures = panelCapture.ProbeSignatures;
            ObserveAcceptedPanel(runtimeState, panelCapture, scanLog, currentPostScrollFirstCell);
            rowFingerprint?.AddDetailPanel(col, rarity, panelImage, captureRois);
            if (!selectedForExport)
            {
                panelImage.Dispose();
                if (bufferedRow is null)
                {
                    Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                }
                else
                {
                    bufferedRow.AddSkipped(col, rarity);
                }

                postScrollFirstCellPending = false;
                continue;
            }

            var lockEvidence = CaptureLockEvidence(window, profile, rarityAnchor, scanLog);

            if (bufferedRow is not null)
            {
                bufferedRow.AddCapture(
                    col,
                    rarity,
                    panelImage,
                    captureRois,
                    panelCapture.TargetVerificationKind,
                    lockEvidence);
                postScrollFirstCellPending = false;
                scanLog.WriteEvent("EDGE_CLICK_BUFFERED_CELL", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, rarity={rarity}, rowHashColumns={rowFingerprint?.Count ?? 0}");
                continue;
            }

            var debugImage = options.ShowDebugImages ? (Bitmap)panelImage.Clone() : null;
            var ocrBacklogBeforeEnqueue = CurrentOcrBacklog(counters);
            var itemIndex = Interlocked.Increment(ref counters.Queued);
            var enqueueWait = Stopwatch.StartNew();
            var enqueued = false;
            try
            {
                queue.Add(new DiscCapture(
                    itemIndex,
                    rarity,
                    panelImage,
                    captureRois,
                    panelCapture.TargetVerificationKind,
                    lockEvidence), token);
                enqueued = true;
            }
            finally
            {
                enqueueWait.Stop();
                if (!enqueued)
                {
                    panelImage.Dispose();
                    lockEvidence.Dispose();
                }
            }

            if (enqueueWait.ElapsedMilliseconds >= 120)
            {
                scanLog.Write($"OCR queue backpressure: waited {enqueueWait.ElapsedMilliseconds}ms to enqueue #{itemIndex}, queueCount={queue.Count}.");
            }

            scanLog.WriteEvent("CELL_TIMING", $"index={itemIndex}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, afterScroll={afterScroll}, postScrollFirstCell={currentPostScrollFirstCell}, panelWaitMs={panelCapture.WaitMilliseconds:F1}, enqueueWaitMs={enqueueWait.Elapsed.TotalMilliseconds:F1}, fallback={panelCapture.UsedFallback}, visibleRois={panelCapture.VisibleRoiCount}/{rois.Length}, totalMs={sw.ElapsedMilliseconds}, panelFrames={panelCapture.FrameCount}, changeMs={FormatOptionalMs(panelCapture.ChangeMilliseconds)}, selectionChangeMs={FormatOptionalMs(panelCapture.SelectionChangeMilliseconds)}, fullRoiMs={FormatOptionalMs(panelCapture.FullRoiMilliseconds)}, stableMs={FormatOptionalMs(panelCapture.StableMilliseconds)}, panelTextStableMs={FormatOptionalMs(panelCapture.PanelTextStableMilliseconds)}, panelStableSource={panelCapture.PanelStableSource}, panelStabilityReason={panelCapture.PanelStabilityReason}, targetSelectionStableFrames={panelCapture.TargetSelectionStableFrames}, targetVerificationKind={panelCapture.TargetVerificationKind}, rarityProbeMs={rarityProbeWatch.Elapsed.TotalMilliseconds:F1}, selectionProbeMs={selectionProbeWatch.Elapsed.TotalMilliseconds:F1}, captureMs={panelCapture.CaptureMilliseconds:F1}, signatureMs={panelCapture.SignatureMilliseconds:F1}, visibleRoiMs={panelCapture.VisibleRoiMilliseconds:F1}, frameLoopMs={panelCapture.FrameLoopMilliseconds:F1}, frameToBitmapMs={panelCapture.FrameToBitmapMilliseconds:F1}, bitmapCreatedCount={panelCapture.BitmapCreatedCount}, quickAccept={panelCapture.QuickAccept}, quickRejectReason={panelCapture.QuickRejectReason}, adaptiveThrottleMs={adaptiveThrottleMs}, ocrBacklogBeforeEnqueue={ocrBacklogBeforeEnqueue}, adaptivePanelMinMs={panelCapture.MinimumAcceptMilliseconds}, adaptivePanelSamples={panelCapture.AdaptiveSampleCount}, adaptivePanelReason={panelCapture.AdaptiveReason}, panelAcceptMode={panelCapture.PanelAcceptMode}, postScrollAcceptMode={panelCapture.PostScrollPanelAcceptMode}, panelMinFloorMs={panelCapture.PanelMinAcceptFloorMs}, roiCompleteFrames={panelCapture.RoiCompleteFrames}, selectedStableFrames={panelCapture.SelectedStableFrames}, acceptGateReason={panelCapture.AcceptGateReason}, panelFloorMode={panelCapture.PanelFloorMode}, sameRowPanelFloorMs={panelCapture.SameRowPanelFloorMs}, postScrollPanelFloorMs={panelCapture.PostScrollPanelFloorMs}, panelFloorReason={panelCapture.PanelFloorReason}, floorWaitLimitedMs={panelCapture.FloorWaitLimitedMilliseconds:F1}, panelAcceptElapsedVsFloorMs={panelCapture.PanelAcceptElapsedVsFloorMilliseconds:F1}, scrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, accept={panelCapture.AcceptReason}");
            postScrollFirstCellPending = false;
            var queueMessage = options.ShowDebugImages || itemIndex % 25 == 0
                ? $"入队 #{itemIndex}：{rarity}，{captureRois.Length} 个文本区域，用时 {sw.ElapsedMilliseconds}ms。"
                : "";
            Report(progress, counters, queueMessage, debugImage: debugImage);
        }

        return RowScanResult.Completed;
    }

}
