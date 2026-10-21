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
    private static async Task<bool> TryProbeOnlyBeforeChangeStepAsync(
        GameWindow window,
        Rectangle probeScreenRect,
        IReadOnlyList<Rectangle> probeCaptureRects,
        ImageSignature[] previousPanelSignatures,
        DateTime start,
        double elapsedMilliseconds,
        TimeSpan unchangedFallbackDelay,
        TimeSpan timeout,
        TimeSpan interval,
        Stopwatch frameLoop,
        CancellationToken token,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        Rectangle selectionProbeRect,
        ImageSignature beforeSelectionSignature,
        bool allowSelectionOnlyFallback,
        bool postScrollFirstCell,
        bool selectionRoundTripReady,
        ProbeOnlyState state,
        PanelSelectionTracker tracker,
        int totalRois,
        int requiredStableFrames)
    {
        state.FrameCount++;
        var probeCaptureWatch = Stopwatch.StartNew();
        using var probeImage = window.CaptureFrame(probeScreenRect);
        probeCaptureWatch.Stop();
        state.CaptureMilliseconds += probeCaptureWatch.Elapsed.TotalMilliseconds;

        var probeSignatureWatch = Stopwatch.StartNew();
        var probeSignatures = CreateSignatures(probeImage, probeCaptureRects);
        var probeChangeDistance = ProbeChangeDistance(previousPanelSignatures, probeSignatures);
        state.PanelChangedFromBaseline = PanelCaptureGate.IsStrongChangeCurrentFrame(
            probeChangeDistance,
            elapsedMilliseconds,
            PanelStrongChangeTolerance,
            MinReliablePanelChangeMs);
        if (state.PanelChangedFromBaseline)
        {
            state.SawPanelChange = true;
            state.ChangeMilliseconds ??= elapsedMilliseconds;
            state.QuickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_roi" : state.QuickRejectReason;
        }
        else if (probeChangeDistance > PanelChangeTolerance)
        {
            state.WeakPanelChange = true;
            state.WeakPanelChangeDistance = Math.Max(state.WeakPanelChangeDistance, probeChangeDistance);
            state.WeakPanelChangeMilliseconds ??= elapsedMilliseconds;
            tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, state.FrameCount, elapsedMilliseconds);
            state.QuickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_strong_panel_change" : state.QuickRejectReason;
        }
        probeSignatureWatch.Stop();
        state.SignatureMilliseconds += probeSignatureWatch.Elapsed.TotalMilliseconds;

        if (!state.SawPanelChange)
        {
            tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, state.FrameCount, elapsedMilliseconds);
            if (DateTime.UtcNow - start >= unchangedFallbackDelay)
            {
                if (!tracker.PromoteSelectionChange(
                        selectionProbeRect,
                        beforeSelectionSignature,
                        state.FrameCount,
                        elapsedMilliseconds,
                        allowSelectionOnlyFallback,
                        postScrollFirstCell,
                        selectionRoundTripReady,
                        state.WeakPanelChange,
                        state.WeakPanelChangeDistance,
                        scanLog,
                        ref state.SawPanelChange,
                        ref state.PanelChangedFromBaseline,
                        ref state.AcceptReason,
                        out var blockedReason))
                {
                    // The cloud return may first become visible in the capture that
                    // crosses the short unchanged fallback. Keep observing that
                    // pending target within the existing deadline; this is not
                    // permission to accept its old detail panel.
                    if (tracker.EvidenceGate.CanWaitForPendingCloudReturn(
                            window.IsCloud,
                            allowSelectionOnlyFallback,
                            selectionRoundTripReady,
                            (DateTime.UtcNow - start).TotalMilliseconds,
                            timeout.TotalMilliseconds))
                    {
                        if (!tracker.PendingReturnWaitLogged)
                        {
                            tracker.PendingReturnWaitLogged = true;
                            scanLog.WriteEvent("PANEL_PENDING_TARGET_RETURN",
                                $"reason={blockedReason}, elapsedMs={elapsedMilliseconds:F1}, selectionChangeMs={FormatOptionalMs(tracker.SelectionChangeMilliseconds)}, selectionStableFrames={tracker.EvidenceGate.StableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, timeoutMs={timeout.TotalMilliseconds:F0}");
                        }
                        frameLoop.Stop();
                        state.FrameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                        await Task.Delay(interval, token);
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(blockedReason))
                    {
                        tracker.WriteSelectionOnlyBlocked(blockedReason, elapsedMilliseconds, postScrollFirstCell, allowSelectionOnlyFallback, scanLog);
                    }

                    if (state.WeakPanelChange)
                    {
                        tracker.WriteWeakPanelChangeBlocked(blockedReason.Length == 0 ? "no_selection_change" : blockedReason, elapsedMilliseconds, state.WeakPanelChangeMilliseconds, state.WeakPanelChangeDistance, scanLog);
                    }

                    scanLog.Write("Panel probes stayed unchanged past fallback delay; refusing to capture stale detail panel.");
                    var staleException = new StalePanelException(
                        visibleRois: 0,
                        totalRois: totalRois,
                        firstMissingRoi: null,
                        firstMissingProbe: null,
                        acceptGateReason: "waiting_for_panel_change",
                        sawPanelChange: false,
                        selectionChanged: tracker.SelectionChanged,
                        stableFrames: tracker.EvidenceGate.StableFrames,
                        requiredStableFrames: requiredStableFrames,
                        frameCount: state.FrameCount,
                        message: "详情面板未检测到变化，已拒绝复用旧面板。");
                    scanLog.WriteEvent(
                        "PANEL_CAPTURE_TIMEOUT",
                        $"visibleRois={staleException.VisibleRois}/{staleException.TotalRois}, firstMissingRoi={staleException.FirstMissingRoi ?? "none"}, referenceLuma=none, candidateLuma=none, lumaDelta=none, allowedLumaDelta=none, edgeDensityPermille=none, minimumEdgeDensityPermille=none, acceptGateReason={staleException.AcceptGateReason}, sawPanelChange={staleException.SawPanelChange}, selectionChanged={staleException.SelectionChanged}, stableFrames={staleException.StableFrames}/{staleException.RequiredStableFrames}, frameCount={staleException.FrameCount}, captureMode={window.ActiveCaptureMode}");
                    throw staleException;
                }
            }

            if (!state.SawPanelChange)
            {
                frameLoop.Stop();
                state.FrameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                await Task.Delay(interval, token);
                return true;
            }
        }

        return false;
    }

    private sealed class ProbeOnlyState
    {
        public int FrameCount;
        public double CaptureMilliseconds;
        public double SignatureMilliseconds;
        public double FrameLoopMilliseconds;
        public bool SawPanelChange;
        public bool PanelChangedFromBaseline;
        public bool WeakPanelChange;
        public int WeakPanelChangeDistance;
        public double? ChangeMilliseconds;
        public double? WeakPanelChangeMilliseconds;
        public string QuickRejectReason = "";
        public string AcceptReason = "";
    }
}
