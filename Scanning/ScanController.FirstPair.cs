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
    private static async Task<FirstPairBootstrapResult> CaptureFirstPairAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        PointF offset,
        PointF step,
        int pass,
        int visualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token)
    {
        var firstPoint = window.ToScreenPoint(new PointF(offset.X + step.X * firstColumn, offset.Y + step.Y * visualRow));
        var secondPoint = window.ToScreenPoint(new PointF(offset.X + step.X * secondColumn, offset.Y + step.Y * visualRow));
        var timeoutMs = FirstPairBootstrapTiming.ResolveMaximumWaitMilliseconds(profile.LoadTimeoutMs);
        scanLog.WriteEvent(
            "FIRST_PAIR_BOOTSTRAP_START",
            $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={visualRow}, firstCol={firstColumn}, secondCol={secondColumn}, timeoutMs={timeoutMs}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}");

        window.LeftClick(DriveDiscSelectionGeometry.Center(firstPoint, window.ClientScreenRect, profile));
        await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
        var provisionalFirstSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);

        PanelCapture? directSecond = null;
        PanelCapture? directFirst = null;
        try
        {
            directSecond = await CaptureFirstPairTargetAsync(
                "second",
                secondPoint,
                provisionalFirstSignatures,
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                runtimeState,
                scanLog,
                token,
                timeoutMs);
            directFirst = await CaptureFirstPairTargetAsync(
                "first_return",
                firstPoint,
                directSecond.ProbeSignatures,
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                runtimeState,
                scanLog,
                token,
                timeoutMs);

            var result = new FirstPairBootstrapResult(directFirst, directSecond, directFirst.ProbeSignatures, "first");
            directFirst = null;
            directSecond = null;
            return result;
        }
        catch (TimeoutException ex)
        {
            scanLog.WriteEvent("FIRST_PAIR_RECOVERED", $"phase=direct_pair, reason={SanitizeLogValue(ex.Message)}, action=search_witness");
        }
        finally
        {
            directFirst?.Dispose();
            directSecond?.Dispose();
        }

        var witnessAttempts = 0;
        foreach (var candidate in BuildFirstPairWitnessCandidates(window, profile, offset, step, visualRow, firstColumn, secondColumn, maxColumns))
        {
            token.ThrowIfCancellationRequested();
            var rarityProbe = DetectRarityAround(window, profile, candidate.Point);
            if (rarityProbe.Rarity is null)
            {
                await Task.Delay(25, token);
                rarityProbe = DetectRarityAround(window, profile, candidate.Point);
            }

            if (rarityProbe.Rarity is null)
            {
                continue;
            }

            witnessAttempts++;
            scanLog.WriteEvent("FIRST_PAIR_WITNESS_ATTEMPT", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, rarity={rarityProbe.Rarity}, point={candidate.Point}");
            var activeSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
            PanelCapture? witness = null;
            PanelCapture? confirmedFirst = null;
            PanelCapture? witnessReturn = null;
            PanelCapture? confirmedSecond = null;
            try
            {
                witness = await CaptureFirstPairTargetAsync(
                    $"witness_{candidate.VisualRow}_{candidate.Column}", candidate.Point, activeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                scanLog.WriteEvent("FIRST_PAIR_WITNESS_READY", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, waitMs={witness.WaitMilliseconds:F1}");

                confirmedFirst = await CaptureFirstPairTargetAsync(
                    "first_from_witness", firstPoint, witness.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                witnessReturn = await CaptureFirstPairTargetAsync(
                    $"witness_return_{candidate.VisualRow}_{candidate.Column}", candidate.Point, confirmedFirst.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                confirmedSecond = await CaptureFirstPairTargetAsync(
                    "second_from_witness", secondPoint, witnessReturn.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);

                scanLog.WriteEvent("FIRST_PAIR_RECOVERED", $"phase=witness_round_trip, attempt={witnessAttempts}, witnessRow={candidate.VisualRow}, witnessCol={candidate.Column}");
                var result = new FirstPairBootstrapResult(confirmedFirst, confirmedSecond, confirmedSecond.ProbeSignatures, "second");
                confirmedFirst = null;
                confirmedSecond = null;
                return result;
            }
            catch (TimeoutException ex)
            {
                scanLog.WriteEvent("FIRST_PAIR_WITNESS_SKIPPED", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, reason={SanitizeLogValue(ex.Message)}");
            }
            finally
            {
                witness?.Dispose();
                confirmedFirst?.Dispose();
                witnessReturn?.Dispose();
                confirmedSecond?.Dispose();
            }
        }

        scanLog.WriteEvent("FIRST_PAIR_FAILED", $"reason=first_pair_witness_unavailable, attempts={witnessAttempts}, firstCol={firstColumn}, secondCol={secondColumn}");
        var timeout = new PanelCaptureTimeoutException(
            0, rois.Count, null, null, "first_pair_witness_unavailable",
            false, false, 0, 2, 0);
        throw new PanelCellCaptureException(
            pass, visualRow, firstColumn, maxColumns, logicalRow, Math.Max(1, witnessAttempts),
            window, runtimeState.VisualProfileId, timeout);
    }

    private static async Task<PanelCapture> CaptureFirstPairTargetAsync(
        string label,
        System.Drawing.Point clickPoint,
        ImageSignature[] previousPanelSignatures,
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        CancellationToken token,
        int timeoutMs)
    {
        token.ThrowIfCancellationRequested();
        clickPoint = DriveDiscSelectionGeometry.Center(clickPoint, window.ClientScreenRect, profile);
        var selectionProbeRect = SelectionProbeRect(window, clickPoint, profile);
        var beforeSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
        window.MoveCursor(clickPoint);
        window.LeftClick(clickPoint);
        var capture = await CaptureStablePanelAsync(
            window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
            previousPanelSignatures, selectionProbeRect, beforeSelectionSignature, runtimeState, scanLog, token,
            postScrollFirstCell: false,
            sceneAdaptivePanelFloorEligible: false,
            allowSelectionOnlyFallback: false,
            selectionRoundTripReady: false,
            timeoutOverrideMs: timeoutMs);
        scanLog.WriteEvent(
            "FIRST_PAIR_CAPTURED",
            $"target={label}, point={clickPoint}, waitMs={capture.WaitMilliseconds:F1}, visibleRois={capture.VisibleRoiCount}/{rois.Count}, changeMs={FormatOptionalMs(capture.ChangeMilliseconds)}, stableFrames={capture.SelectedStableFrames}/{capture.RequiredStableFrames}, accept={capture.AcceptReason}");
        return capture;
    }

    private static IReadOnlyList<FirstPairWitnessCandidate> BuildFirstPairWitnessCandidates(
        GameWindow window,
        ScanProfile profile,
        PointF offset,
        PointF step,
        int firstVisualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns)
    {
        return FirstPairWitnessPlanner.Build(
                firstVisualRow,
                firstColumn,
                secondColumn,
                maxColumns,
                profile.VisibleRows,
                profile.VisibleColumns)
            .Select(candidate => new FirstPairWitnessCandidate(
                candidate.VisualRow,
                candidate.Column,
                window.ToScreenPoint(new PointF(
                    offset.X + step.X * candidate.Column,
                    offset.Y + step.Y * candidate.VisualRow))))
            .ToArray();
    }

    private static async Task<PanelCapture> CaptureStablePanelWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ImageSignature[]? previousPanelSignatures,
        Rectangle selectionProbeRect,
        ImageSignature beforeSelectionSignature,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        System.Drawing.Point clickPoint,
        System.Drawing.Point? selectionRefreshPoint,
        int pass,
        int row,
        int col,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token,
        bool postScrollFirstCell,
        bool sceneAdaptivePanelFloorEligible,
        bool firstQueuedItem,
        bool initialSelectionRoundTripReady,
        bool preselectedTargetEvidence = false)
    {
        const int maxAttempts = 2;
        var activePreviousPanelSignatures = previousPanelSignatures;
        var activeSelectionProbeRect = selectionProbeRect;
        var activeBeforeSelectionSignature = beforeSelectionSignature;
        var selectionRoundTripReady = initialSelectionRoundTripReady;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var allowSelectionEvidence = selectionRoundTripReady;
                return await CaptureStablePanelAsync(window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect, activePreviousPanelSignatures, activeSelectionProbeRect, activeBeforeSelectionSignature, runtimeState, scanLog, token, postScrollFirstCell, sceneAdaptivePanelFloorEligible && attempt == 1, allowSelectionEvidence, selectionRoundTripReady, preselectedTargetEvidence);
            }
            catch (StalePanelException) when (attempt < maxAttempts && !selectionRoundTripReady)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_STALE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}");
                if (firstQueuedItem)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt={attempt}/{maxAttempts - 1}, reason=stale_panel, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (firstQueuedItem && refresh.RefreshReady)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                activePreviousPanelSignatures = refresh.PanelSignatures;
                activeSelectionProbeRect = refresh.SelectionProbeRect;
                activeBeforeSelectionSignature = refresh.SelectionSignature;
            }
            catch (TimeoutException ex) when (attempt < maxAttempts && !selectionRoundTripReady)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_CAPTURE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, reason={ex.Message}");
                if (firstQueuedItem)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt={attempt}/{maxAttempts - 1}, reason=capture_timeout, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (firstQueuedItem && refresh.RefreshReady)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                activePreviousPanelSignatures = refresh.PanelSignatures;
                activeSelectionProbeRect = refresh.SelectionProbeRect;
                activeBeforeSelectionSignature = refresh.SelectionSignature;
            }
            catch (TimeoutException ex)
            {
                throw new PanelCellCaptureException(
                    pass,
                    row,
                    col,
                    maxColumns,
                    logicalRow,
                    attempt,
                    window,
                    runtimeState.VisualProfileId,
                    ex);
            }
        }

        throw new UnreachableException("Panel capture retry loop exhausted without returning or throwing.");
    }

    private static async Task<SelectionRefreshCapture> RefreshSelectionForPanelRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        Rectangle panelChangeProbeRect,
        ScanLog scanLog,
        System.Drawing.Point clickPoint,
        System.Drawing.Point? selectionRefreshPoint,
        int pass,
        int row,
        int col,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var targetPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        ImageSignature[] refreshedPanelSignatures = targetPanelSignatures;
        var refreshReady = false;
        var selectionRoundTripReady = false;
        if (selectionRefreshPoint is { } refreshPoint && refreshPoint != clickPoint)
        {
            scanLog.WriteEvent("PANEL_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            var targetSelectionProbeRect = SelectionProbeRect(window, clickPoint, profile);
            var witnessSelectionProbeRect = SelectionProbeRect(window, refreshPoint, profile);
            window.MoveCursor(refreshPoint);
            await Task.Delay(Math.Max((int)PanelTargetEvidenceGate.MinimumReliableChangeMilliseconds, profile.LoadPollMs), token);
            var beforeWitnessAndTarget = CaptureSelectionSignatures(window, witnessSelectionProbeRect, targetSelectionProbeRect);
            window.LeftClick(refreshPoint);

            var maximumWaitMs = SelectionRefreshTiming.ResolveMaximumWaitMilliseconds(profile.LoadTimeoutMs);
            var pollMs = Math.Max(5, profile.LoadPollMs);
            ImageSignature[]? previousWitnessAndTarget = null;
            var latestTargetSelectionDistance = 0;
            var latestWitnessSelectionDistance = 0;
            var result = await SelectionRefreshWaiter.WaitAsync(
                () =>
                {
                    var current = CaptureSelectionSignatures(window, witnessSelectionProbeRect, targetSelectionProbeRect);
                    latestWitnessSelectionDistance = SignatureDistance(beforeWitnessAndTarget[0], current[0]);
                    latestTargetSelectionDistance = SignatureDistance(beforeWitnessAndTarget[1], current[1]);
                    var witnessChanged = latestWitnessSelectionDistance > PanelChangeTolerance;
                    var stableWithPrevious = previousWitnessAndTarget is not null
                        && SignatureDistance(previousWitnessAndTarget[0], current[0]) <= ListStableTolerance
                        && SignatureDistance(previousWitnessAndTarget[1], current[1]) <= ListStableTolerance;
                    previousWitnessAndTarget = current;
                    return new SelectionRefreshObservation(witnessChanged, stableWithPrevious);
                },
                maximumWaitMs,
                pollMs,
                token);
            if (result.Ready)
            {
                refreshReady = true;
                selectionRoundTripReady = true;
                refreshedPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_READY",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, witnessDistance={latestWitnessSelectionDistance}, targetDistance={latestTargetSelectionDistance}, threshold={PanelChangeTolerance}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}, evidence=same_frame_witness_and_target");
                scanLog.WriteEvent(
                    "PANEL_NEIGHBOR_ROUNDTRIP",
                    $"phase=away_ready, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, stableFrames={result.StableFrames}/2, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            }
            else
            {
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_TIMEOUT",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, timeoutMs={maximumWaitMs}, witnessChanged={result.ChangedFromTarget}, witnessDistance={latestWitnessSelectionDistance}, targetDistance={latestTargetSelectionDistance}, threshold={PanelChangeTolerance}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}, baseline=witness_snapshot");
                scanLog.WriteEvent(
                    "PANEL_NEIGHBOR_ROUNDTRIP",
                    $"phase=away_failed, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, changedFromTarget={result.ChangedFromTarget}, stableFrames={result.StableFrames}/2, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            }
        }

        token.ThrowIfCancellationRequested();
        var refreshedSelectionProbeRect = SelectionProbeRect(window, clickPoint, profile);
        window.MoveCursor(clickPoint);
        await Task.Delay(Math.Max((int)PanelTargetEvidenceGate.MinimumReliableChangeMilliseconds, profile.LoadPollMs), token);
        var refreshedSelectionSignature = CaptureSelectionSignature(refreshedSelectionProbeRect);
        window.LeftClick(clickPoint);
        return new SelectionRefreshCapture(
            refreshedPanelSignatures,
            refreshedSelectionProbeRect,
            refreshedSelectionSignature,
            refreshReady,
            selectionRoundTripReady);
    }


    private sealed record FirstPairCommit(int Column, string Rarity, PanelCapture Capture);

    private sealed record FirstPairWitnessCandidate(
        int VisualRow,
        int Column,
        System.Drawing.Point Point);

    private sealed class FirstPairBootstrapResult : IDisposable
    {
        public FirstPairBootstrapResult(
            PanelCapture first,
            PanelCapture second,
            ImageSignature[] currentPanelSignatures,
            string currentPanelLabel)
        {
            First = first;
            Second = second;
            CurrentPanelSignatures = currentPanelSignatures;
            CurrentPanelLabel = currentPanelLabel;
        }

        public PanelCapture First { get; }
        public PanelCapture Second { get; }
        public ImageSignature[] CurrentPanelSignatures { get; }
        public string CurrentPanelLabel { get; }

        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
        }
    }


    private sealed record SelectionRefreshCapture(
        ImageSignature[] PanelSignatures,
        Rectangle SelectionProbeRect,
        ImageSignature SelectionSignature,
        bool RefreshReady,
        bool SelectionRoundTripReady);

}
