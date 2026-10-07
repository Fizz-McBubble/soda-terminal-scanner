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
    private sealed class PanelSelectionTracker
    {
        public PanelTargetEvidenceGate EvidenceGate { get; } = new();
        public ImageSignature? PreviousSelectionSignature { get; private set; }
        public ImageSignature? PreviousPreselectedSelectionSignature { get; private set; }
        public int PreselectedSelectionStableFrames { get; private set; }
        public bool PreselectedEvidenceLogged { get; private set; }
        public int SelectionObservedFrame { get; private set; } = -1;
        public bool SelectionStableForObservedFrame { get; private set; }
        public bool TargetEvidenceLogged { get; private set; }
        public bool SelectionChanged { get; set; }
        public double? SelectionChangeMilliseconds { get; set; }
        public bool SelectionAttestedPanel { get; set; }

        public bool ObserveSelectionChange(
            Rectangle selectionProbeRect,
            ImageSignature beforeSelectionSignature,
            int frameCount,
            double elapsed)
        {
            if (EvidenceGate.Stable)
            {
                return true;
            }

            if (SelectionObservedFrame == frameCount)
            {
                return SelectionStableForObservedFrame;
            }

            var currentSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
            var changed = SignatureDistance(beforeSelectionSignature, currentSelectionSignature) > PanelChangeTolerance;
            var stableWithPrevious = PreviousSelectionSignature is not null
                && SignatureDistance(PreviousSelectionSignature.Value, currentSelectionSignature) <= ListStableTolerance;
            PreviousSelectionSignature = currentSelectionSignature;
            var stable = EvidenceGate.Observe(changed, stableWithPrevious, elapsed);
            SelectionObservedFrame = frameCount;
            SelectionStableForObservedFrame = stable;
            SelectionChanged = EvidenceGate.Changed;
            SelectionChangeMilliseconds = EvidenceGate.ChangeMilliseconds;
            return stable;
        }

        public bool ObservePreselectedSelectionPresence(
            bool preselectedTargetEvidence,
            Rectangle selectionProbeRect,
            ScanLog scanLog,
            bool postScrollFirstCell)
        {
            if (!preselectedTargetEvidence)
            {
                return false;
            }

            var currentSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
            var stableWithPrevious = PreviousPreselectedSelectionSignature is not null
                && SignatureDistance(PreviousPreselectedSelectionSignature.Value, currentSelectionSignature) <= ListStableTolerance;
            PreviousPreselectedSelectionSignature = currentSelectionSignature;
            PreselectedSelectionStableFrames = stableWithPrevious
                ? PreselectedSelectionStableFrames + 1
                : 0;
            if (PreselectedSelectionStableFrames >= PanelTargetEvidenceGate.RequiredStableFrames && !PreselectedEvidenceLogged)
            {
                PreselectedEvidenceLogged = true;
                scanLog.WriteEvent(
                    "PANEL_PRESELECTED_TARGET_EVIDENCE",
                    $"evidence=edge_target_list_move_and_stable_selection, stableFrames={PreselectedSelectionStableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, postScrollFirstCell={postScrollFirstCell}");
            }

            return PreselectedSelectionStableFrames >= PanelTargetEvidenceGate.RequiredStableFrames;
        }

        public bool PromoteSelectionChange(
            Rectangle selectionProbeRect,
            ImageSignature beforeSelectionSignature,
            int frameCount,
            double elapsed,
            bool allowSelectionOnlyFallback,
            bool postScrollFirstCell,
            bool selectionRoundTripReady,
            bool weakPanelChange,
            int weakPanelChangeDistance,
            ScanLog scanLog,
            ref bool sawPanelChange,
            ref bool panelChangedFromBaseline,
            ref string acceptReason,
            out string blockedReason)
        {
            blockedReason = "";
            if (!ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, frameCount, elapsed))
            {
                return false;
            }

            if (!EvidenceGate.CanPromote(
                    allowSelectionOnlyFallback,
                    postScrollFirstCell,
                    selectionRoundTripReady,
                    out blockedReason))
            {
                return false;
            }

            SelectionAttestedPanel = true;
            sawPanelChange = true;
            panelChangedFromBaseline = true;
            acceptReason = selectionRoundTripReady
                ? "neighbor_roundtrip_stable_full_roi"
                : "selection_changed_stable_full_roi";
            blockedReason = "";
            if (!TargetEvidenceLogged)
            {
                TargetEvidenceLogged = true;
                scanLog.WriteEvent(
                    "PANEL_TARGET_EVIDENCE",
                    $"evidence={(selectionRoundTripReady ? "neighbor_roundtrip" : "selection_change")}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(SelectionChangeMilliseconds)}, selectionStableFrames={EvidenceGate.StableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, weakPanelChange={weakPanelChange}, weakPanelChangeDistance={weakPanelChangeDistance}, postScrollFirstCell={postScrollFirstCell}");
                if (selectionRoundTripReady)
                {
                    scanLog.WriteEvent(
                        "PANEL_NEIGHBOR_ROUNDTRIP",
                        $"phase=target_ready, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(SelectionChangeMilliseconds)}, selectionStableFrames={EvidenceGate.StableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, postScrollFirstCell={postScrollFirstCell}");
                }
            }

            return true;
        }

        public void WriteSelectionOnlyBlocked(
            string reason,
            double elapsed,
            bool postScrollFirstCell,
            bool allowSelectionOnlyFallback,
            ScanLog scanLog)
        {
            scanLog.WriteEvent(
                "PANEL_SELECTION_ONLY_BLOCKED",
                $"selection_only_blocked_reason={reason}, post_scroll_panel_change_required={postScrollFirstCell}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(SelectionChangeMilliseconds)}, allowSelectionOnlyFallback={allowSelectionOnlyFallback}");
        }

        public void WriteWeakPanelChangeBlocked(
            string reason,
            double elapsed,
            double? weakPanelChangeMilliseconds,
            int weakPanelChangeDistance,
            ScanLog scanLog)
        {
            scanLog.WriteEvent(
                "PANEL_WEAK_CHANGE_BLOCKED",
                $"weak_panel_change_blocked_reason={reason}, weakPanelChangeMs={FormatOptionalMs(weakPanelChangeMilliseconds)}, weakPanelChangeDistance={weakPanelChangeDistance}, strongTolerance={PanelStrongChangeTolerance}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(SelectionChangeMilliseconds)}");
        }
    }

    private static PanelCapture CreateAcceptedPanelCapture(
        CapturedFrame image,
        Func<ImageSignature[]> ensureProbeSignatures,
        int visibleCount,
        DateTime start,
        int frameCount,
        double? changeMilliseconds,
        double? selectionChangeMilliseconds,
        double? fullRoiMilliseconds,
        double? stableMilliseconds,
        double? textCoreStableMilliseconds,
        PanelStabilityDecision stabilityDecision,
        double captureMilliseconds,
        ref double signatureMilliseconds,
        double visibleRoiMilliseconds,
        ref double frameLoopMilliseconds,
        ref double frameToBitmapMilliseconds,
        ref int bitmapCreatedCount,
        Stopwatch frameLoop,
        int changedMinimumAcceptMs,
        int requiredStableFrames,
        AdaptivePanelTimingDecision panelTiming,
        string panelFloorReason,
        Func<string> resolveAcceptReason,
        Func<TargetVerificationKind> resolveTargetVerificationKind,
        bool quickAccept,
        string quickRejectReason,
        int roiCompleteFrames,
        int selectedStableFrames,
        int selectionStableFrames,
        string acceptGateReason)
    {
        var acceptedProbeSignatures = ensureProbeSignatures();
        var bitmapWatch = Stopwatch.StartNew();
        var acceptedImage = image.ToBitmap();
        bitmapWatch.Stop();
        frameToBitmapMilliseconds += bitmapWatch.Elapsed.TotalMilliseconds;
        bitmapCreatedCount++;
        frameLoop.Stop();
        frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;

        var acceptReason = resolveAcceptReason();
        var waitMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;
        var targetVerificationKind = resolveTargetVerificationKind();
        return new PanelCapture(
            acceptedImage,
            visibleCount,
            waitMilliseconds,
            usedFallback: false,
            acceptedProbeSignatures,
            frameCount,
            changeMilliseconds,
            selectionChangeMilliseconds,
            fullRoiMilliseconds,
            stableMilliseconds,
            textCoreStableMilliseconds,
            stabilityDecision.SourceName,
            stabilityDecision.Reason,
            captureMilliseconds,
            signatureMilliseconds,
            visibleRoiMilliseconds,
            frameLoopMilliseconds,
            frameToBitmapMilliseconds,
            bitmapCreatedCount,
            changedMinimumAcceptMs,
            requiredStableFrames,
            panelTiming.SampleCount,
            panelTiming.Reason,
            acceptReason,
            quickAccept,
            quickRejectReason,
            panelTiming.EffectivePanelAcceptMode,
            panelTiming.EffectivePostScrollPanelAcceptMode,
            panelTiming.PanelFloorMode,
            panelTiming.PanelMinAcceptFloorMs,
            panelTiming.SameRowPanelFloorMs,
            panelTiming.PostScrollPanelFloorMs,
            panelFloorReason,
            CalculateFloorWaitLimitedMilliseconds(changedMinimumAcceptMs, changeMilliseconds, fullRoiMilliseconds, stableMilliseconds),
            waitMilliseconds - changedMinimumAcceptMs,
            roiCompleteFrames,
            selectedStableFrames,
            selectionStableFrames,
            targetVerificationKind,
            acceptGateReason);
    }
}
