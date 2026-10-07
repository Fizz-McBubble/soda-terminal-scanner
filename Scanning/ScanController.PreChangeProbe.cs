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
        PanelSelectionTracker tracker)
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
                    if (!string.IsNullOrWhiteSpace(blockedReason))
                    {
                        tracker.WriteSelectionOnlyBlocked(blockedReason, elapsedMilliseconds, postScrollFirstCell, allowSelectionOnlyFallback, scanLog);
                    }

                    if (state.WeakPanelChange)
                    {
                        tracker.WriteWeakPanelChangeBlocked(blockedReason.Length == 0 ? "no_selection_change" : blockedReason, elapsedMilliseconds, state.WeakPanelChangeMilliseconds, state.WeakPanelChangeDistance, scanLog);
                    }

                    scanLog.Write("Panel probes stayed unchanged past fallback delay; refusing to capture stale detail panel.");
                    throw new StalePanelException("详情面板未检测到变化，已拒绝复用旧面板。");
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
