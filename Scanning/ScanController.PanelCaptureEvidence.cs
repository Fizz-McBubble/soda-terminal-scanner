using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using CvRect = System.Drawing.Rectangle;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static void ObservePanelCaptureSignals(
        CapturedFrame image,
        IReadOnlyList<CvRect> fullPanelProbeRects,
        Rectangle[] stableProbeRects,
        Rectangle[] textCoreStableProbeRects,
        ImageSignature[]? previousPanelSignatures,
        Rectangle selectionProbeRect,
        ImageSignature beforeSelectionSignature,
        ImageSignature? selectionSignature,
        int frameCount,
        double elapsedMilliseconds,
        PanelStabilityDecision stabilityDecision,
        int requiredStableFrames,
        bool measureTextCoreStability,
        PanelSelectionTracker tracker,
        ref ImageSignature[]? changeProbeSignatures,
        ref bool sawPanelChange,
        ref bool panelChangedFromBaseline,
        ref bool weakPanelChange,
        ref int weakPanelChangeDistance,
        ref double? changeMilliseconds,
        ref double? weakPanelChangeMilliseconds,
        ref ImageSignature[]? previousStableProbeSignatures,
        ref int stableProbeFrames,
        ref double? stableMilliseconds,
        ref ImageSignature[]? previousTextCoreStableProbeSignatures,
        ref int textCoreStableProbeFrames,
        ref double? textCoreStableMilliseconds,
        ref double signatureMilliseconds)
    {
        var signatureWatch = Stopwatch.StartNew();
        if (!sawPanelChange && previousPanelSignatures is not null)
        {
            changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
            var fullPanelChangeDistance = ProbeChangeDistance(previousPanelSignatures, changeProbeSignatures);
            var strongPanelChange = PanelCaptureGate.LatchStrongChange(
                panelChangedFromBaseline,
                fullPanelChangeDistance,
                elapsedMilliseconds,
                PanelStrongChangeTolerance,
                MinReliablePanelChangeMs);
            if (strongPanelChange)
            {
                sawPanelChange = true;
                panelChangedFromBaseline = true;
                changeMilliseconds ??= elapsedMilliseconds;
            }
            else if (fullPanelChangeDistance > PanelChangeTolerance)
            {
                weakPanelChange = true;
                weakPanelChangeDistance = Math.Max(weakPanelChangeDistance, fullPanelChangeDistance);
                weakPanelChangeMilliseconds ??= elapsedMilliseconds;
                tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, frameCount, elapsedMilliseconds, selectionSignature);
            }
        }

        UpdateStabilityProbes(
            image,
            stableProbeRects,
            ref previousStableProbeSignatures,
            ref stableProbeFrames,
            ref stableMilliseconds,
            measureTextCoreStability,
            textCoreStableProbeRects,
            ref previousTextCoreStableProbeSignatures,
            ref textCoreStableProbeFrames,
            ref textCoreStableMilliseconds,
            stabilityDecision,
            requiredStableFrames,
            elapsedMilliseconds);

        tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, frameCount, elapsedMilliseconds, selectionSignature);
        signatureWatch.Stop();
        signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
    }

    private static ImageSignature[] EnsureCurrentChangeProbeSignatures(
        CapturedFrame image,
        IReadOnlyList<CvRect> fullPanelProbeRects,
        ref ImageSignature[]? changeProbeSignatures,
        ref double signatureMilliseconds)
    {
        if (changeProbeSignatures is not null)
        {
            return changeProbeSignatures;
        }

        var baselineWatch = Stopwatch.StartNew();
        changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
        baselineWatch.Stop();
        signatureMilliseconds += baselineWatch.Elapsed.TotalMilliseconds;
        return changeProbeSignatures;
    }

    private static ImageSignature[] EnsureAcceptedChangeProbeSignatures(
        CapturedFrame image,
        IReadOnlyList<CvRect> fullPanelProbeRects,
        ref ImageSignature[]? changeProbeSignatures,
        ref double signatureMilliseconds,
        ImageSignature[]? previousPanelSignatures,
        bool selectionRoundTripReady,
        bool preselectedTargetEvidence,
        bool preselectedSelectionPresent,
        int panelChangeTolerance,
        int lastVisibleCount,
        int totalRois,
        bool sawPanelChange,
        bool selectionChanged,
        int textCoreStableProbeFrames,
        int requiredStableFrames,
        int frameCount,
        double elapsedMilliseconds,
        ScanLog scanLog,
        PanelSelectionTracker tracker)
    {
        var signatures = EnsureCurrentChangeProbeSignatures(
            image,
            fullPanelProbeRects,
            ref changeProbeSignatures,
            ref signatureMilliseconds);
        var finalDistance = previousPanelSignatures is null
            ? int.MaxValue
            : ProbeChangeDistance(previousPanelSignatures, signatures);
        // A click animation can change and then return to the previous disc's
        // exact detail image. A latched transient is not final target evidence;
        // reuse the bounded neighbor retry in that case.
        if (PanelCaptureGate.RequiresFinalFrameRefresh(
            previousPanelSignatures is not null, finalDistance, panelChangeTolerance,
            selectionRoundTripReady && tracker.EvidenceGate.Stable,
            preselectedTargetEvidence && preselectedSelectionPresent))
        {
            scanLog.WriteEvent("PANEL_FINAL_UNCHANGED",
                $"finalChangeDistance={finalDistance}, tolerance={panelChangeTolerance}, elapsedMs={elapsedMilliseconds:F1}, action=neighbor_roundtrip");
            throw new StalePanelException(
                visibleRois: lastVisibleCount,
                totalRois: totalRois,
                firstMissingRoi: null,
                firstMissingProbe: null,
                acceptGateReason: "waiting_for_panel_change",
                sawPanelChange: sawPanelChange,
                selectionChanged: selectionChanged,
                stableFrames: textCoreStableProbeFrames,
                requiredStableFrames: requiredStableFrames,
                frameCount: frameCount,
                message: "最终详情与上一张相同，需要邻格往返确认目标。",
                selectionStableFrames: frameCount > 0
                    ? preselectedTargetEvidence
                        ? tracker.PreselectedSelectionStableFrames
                        : tracker.EvidenceGate.StableFrames
                    : null,
                preselectedTargetEvidence: preselectedTargetEvidence,
                preselectedSelectionPresent: preselectedTargetEvidence ? preselectedSelectionPresent : null,
                selectionRoundTripReady: selectionRoundTripReady);
        }
        return signatures;
    }

    [DoesNotReturn]
    private static void ThrowPanelCaptureTimeout(
        GameWindow window,
        int visibleRois,
        int totalRois,
        VisibleRoiEvaluation lastRoiVisibility,
        string acceptGateReason,
        bool sawPanelChange,
        bool selectionChanged,
        int stableFrames,
        int textCoreStableProbeFrames,
        int requiredStableFrames,
        int frameCount,
        ScanLog scanLog,
        int? selectionStableFrames,
        bool preselectedTargetEvidence,
        bool preselectedSelectionPresent,
        bool selectionRoundTripReady)
    {
        LogAndThrowPanelCaptureTimeout(
            window,
            visibleRois,
            totalRois,
            lastRoiVisibility,
            acceptGateReason,
            sawPanelChange,
            selectionChanged,
            stableFrames,
            textCoreStableProbeFrames,
            requiredStableFrames,
            frameCount,
            scanLog,
            selectionStableFrames,
            preselectedTargetEvidence,
            preselectedSelectionPresent,
            selectionRoundTripReady);
        throw new UnreachableException();
    }
}
