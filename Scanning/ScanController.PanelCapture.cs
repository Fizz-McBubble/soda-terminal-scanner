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
    private static async Task<PanelCapture> CaptureStablePanelAsync(
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
        CancellationToken token,
        bool postScrollFirstCell,
        bool sceneAdaptivePanelFloorEligible,
        bool allowSelectionOnlyFallback,
        bool selectionRoundTripReady,
        bool preselectedTargetEvidence = false,
        int? timeoutOverrideMs = null)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutOverrideMs ?? profile.LoadTimeoutMs, 1, profile.LoadTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(PanelCaptureTimingPolicy.ResolvePollDelayMilliseconds(
            Math.Max(5, profile.LoadPollMs),
            window.ActiveFrameBackend));
        var panelTiming = ResolveEffectivePanelTiming(profile, window, runtimeState, postScrollFirstCell, sceneAdaptivePanelFloorEligible);
        var stabilityDecision = runtimeState.PanelStability.Resolve();
        var settleDelay = Math.Clamp(profile.PanelSettleDelayMs, Math.Max(1, profile.MinPanelSettleDelayMs), 180);
        var changedMinimumAcceptMs = panelTiming.MinimumAcceptMilliseconds;
        var panelFloorReason = panelTiming.PanelFloorReason;
        var quickMinimumAcceptMs = string.Equals(window.ActiveCaptureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(changedMinimumAcceptMs, 90)
            : Math.Min(changedMinimumAcceptMs, 60);
        var requiredStableFrames = panelTiming.RequiredStableFrames;
        await Task.Delay(settleDelay, token);
        var start = DateTime.UtcNow;
        var roiCompleteFrames = 0;
        var probeScreenRect = PanelProbeScreenRect(panelRect, panelChangeProbeRect);
        var probeCaptureRects = BuildPanelChangeProbeRectsForProbe(probeScreenRect, panelRect, rois);
        var fullPanelProbeRects = TranslateProbeRectsToPanel(probeCaptureRects, probeScreenRect, panelRect);
        var stableProbeRects = stabilityDecision.Source == PanelStabilitySource.TextCore
            ? Array.Empty<Rectangle>()
            : BuildPanelStableProbeRects(panelRect, rois);
        const bool measureTextCoreStability = true;
        var textCoreStableProbeRects = measureTextCoreStability
            ? BuildTextCoreStableProbeRects(panelRect, rois)
            : Array.Empty<Rectangle>();
        var initialGate = PanelCaptureGate.Initialize(previousPanelSignatures is not null);
        var sawPanelChange = initialGate.SawPanelChange || preselectedTargetEvidence;
        var panelChangedFromBaseline = preselectedTargetEvidence;
        var tracker = new PanelSelectionTracker
        {
            SelectionChanged = initialGate.SelectionChanged || preselectedTargetEvidence
        };
        ImageSignature[]? previousStableProbeSignatures = null;
        ImageSignature[]? previousTextCoreStableProbeSignatures = null;
        var stableProbeFrames = 0;
        var textCoreStableProbeFrames = 0;
        var weakPanelChange = false;
        var weakPanelChangeDistance = 0;
        var frameCount = 0;
        var captureMilliseconds = 0.0;
        var signatureMilliseconds = 0.0;
        var visibleRoiMilliseconds = 0.0;
        var frameLoopMilliseconds = 0.0;
        var frameToBitmapMilliseconds = 0.0;
        var bitmapCreatedCount = 0;
        double? changeMilliseconds = initialGate.ChangeMilliseconds;
        double? weakPanelChangeMilliseconds = null;
        double? fullRoiMilliseconds = null;
        double? stableMilliseconds = null;
        double? textCoreStableMilliseconds = null;
        var acceptReason = "changed_stable_full_roi";
        var acceptGateReason = "waiting_for_panel_change";
        var lastVisibleCount = 0;
        var lastReadableRoiCount = -1;
        var roiKeys = profile.OrderedRoiKeys();
        var lastRoiVisibility = new VisibleRoiEvaluation(0, roiKeys.FirstOrDefault(), null, false, "not_sampled");
        var quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_panel_change" : "disabled";
        var unchangedFallbackDelay = TimeSpan.FromMilliseconds(Math.Clamp(
            profile.PanelUnchangedFallbackMs,
            Math.Max(1, profile.MinPanelUnchangedFallbackMs),
            profile.LoadTimeoutMs));
        var useProbeOnlyBeforeChange = true;

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            var frameLoop = Stopwatch.StartNew();
            var elapsedMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;

            if (useProbeOnlyBeforeChange && !sawPanelChange && previousPanelSignatures is not null)
            {
                var probeState = new ProbeOnlyState
                {
                    FrameCount = frameCount,
                    CaptureMilliseconds = captureMilliseconds,
                    SignatureMilliseconds = signatureMilliseconds,
                    FrameLoopMilliseconds = frameLoopMilliseconds,
                    SawPanelChange = sawPanelChange,
                    PanelChangedFromBaseline = panelChangedFromBaseline,
                    WeakPanelChange = weakPanelChange,
                    WeakPanelChangeDistance = weakPanelChangeDistance,
                    ChangeMilliseconds = changeMilliseconds,
                    WeakPanelChangeMilliseconds = weakPanelChangeMilliseconds,
                    QuickRejectReason = quickRejectReason,
                    AcceptReason = acceptReason
                };
                var shouldContinue = await TryProbeOnlyBeforeChangeStepAsync(
                    window,
                    probeScreenRect,
                    probeCaptureRects,
                    previousPanelSignatures,
                    start,
                    elapsedMilliseconds,
                    unchangedFallbackDelay,
                    interval,
                    frameLoop,
                    token,
                    runtimeState,
                    scanLog,
                    selectionProbeRect,
                    beforeSelectionSignature,
                    allowSelectionOnlyFallback,
                    postScrollFirstCell,
                    selectionRoundTripReady,
                    probeState,
                    tracker);
                frameCount = probeState.FrameCount;
                captureMilliseconds = probeState.CaptureMilliseconds;
                signatureMilliseconds = probeState.SignatureMilliseconds;
                frameLoopMilliseconds = probeState.FrameLoopMilliseconds;
                sawPanelChange = probeState.SawPanelChange;
                panelChangedFromBaseline = probeState.PanelChangedFromBaseline;
                weakPanelChange = probeState.WeakPanelChange;
                weakPanelChangeDistance = probeState.WeakPanelChangeDistance;
                changeMilliseconds = probeState.ChangeMilliseconds;
                weakPanelChangeMilliseconds = probeState.WeakPanelChangeMilliseconds;
                quickRejectReason = probeState.QuickRejectReason;
                acceptReason = probeState.AcceptReason;
                if (shouldContinue)
                {
                    continue;
                }
            }

            frameCount++;
            var preselectedSelectionPresent = tracker.ObservePreselectedSelectionPresence(
                preselectedTargetEvidence,
                selectionProbeRect,
                scanLog,
                postScrollFirstCell);
            var captureWatch = Stopwatch.StartNew();
            using var image = window.CaptureFrame(panelRect);
            captureWatch.Stop();
            captureMilliseconds += captureWatch.Elapsed.TotalMilliseconds;
            elapsedMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;

            var signatureWatch = Stopwatch.StartNew();
            ImageSignature[]? changeProbeSignatures = null;
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
                    tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, frameCount, elapsedMilliseconds);
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

            tracker.ObserveSelectionChange(selectionProbeRect, beforeSelectionSignature, frameCount, elapsedMilliseconds);
            signatureWatch.Stop();
            signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;

            ImageSignature[] EnsureCurrentChangeProbeSignatures()
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

            ImageSignature[] EnsureAcceptedChangeProbeSignatures()
            {
                var signatures = EnsureCurrentChangeProbeSignatures();
                var finalDistance = previousPanelSignatures is null
                    ? int.MaxValue
                    : ProbeChangeDistance(previousPanelSignatures, signatures);
                // A click animation can change and then return to the previous
                // disc's exact detail image. A latched transient is not final
                // target evidence; reuse the bounded neighbor retry in that case.
                if (PanelCaptureGate.RequiresFinalFrameRefresh(
                    previousPanelSignatures is not null, finalDistance, PanelChangeTolerance,
                    selectionRoundTripReady && tracker.EvidenceGate.Stable,
                    preselectedTargetEvidence && preselectedSelectionPresent))
                {
                    scanLog.WriteEvent("PANEL_FINAL_UNCHANGED",
                        $"finalChangeDistance={finalDistance}, tolerance={PanelChangeTolerance}, elapsedMs={elapsedMilliseconds:F1}, action=neighbor_roundtrip");
                    throw new StalePanelException("最终详情与上一张相同，需要邻格往返确认目标。");
                }
                return signatures;
            }

            var visibleWatch = Stopwatch.StartNew();
            var roiVisibility = EvaluateVisibleRois(
                image,
                rois,
                roiKeys,
                statOffset,
                (profile.VisualProbes ?? new VisualProbeOptions()).RowPresence ?? new RowPresenceProbePolicy());
            var visibleCount = roiVisibility.Count;
            lastVisibleCount = visibleCount;
            lastRoiVisibility = roiVisibility;
            visibleWatch.Stop();
            visibleRoiMilliseconds += visibleWatch.Elapsed.TotalMilliseconds;
            if (roiVisibility.ValidBoundary)
            {
                roiCompleteFrames = lastReadableRoiCount == visibleCount
                    ? roiCompleteFrames + 1
                    : 1;
                lastReadableRoiCount = visibleCount;
                var requiredLayoutFrames = RequiredRoiBoundaryFrames(visibleCount, rois.Count, requiredStableFrames);
                if (roiCompleteFrames >= requiredLayoutFrames)
                {
                    fullRoiMilliseconds ??= elapsedMilliseconds;
                }
            }
            else
            {
                roiCompleteFrames = 0;
                lastReadableRoiCount = -1;
            }

            if (visibleCount > 0)
            {
                var requiredRoiCompleteFrames = RequiredRoiBoundaryFrames(visibleCount, rois.Count, requiredStableFrames);
                var panelReadable = roiVisibility.ValidBoundary && roiCompleteFrames >= requiredRoiCompleteFrames;
                var fullPanelReadable = visibleCount == rois.Count && panelReadable;
                var panelSelectedStableFrames = textCoreStableProbeFrames;
                var adaptiveEarlyEvidence = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    && !tracker.SelectionAttestedPanel;
                var effectiveRequiredStableFrames = adaptiveEarlyEvidence
                    ? 1
                    : requiredStableFrames;
                var selectedStableFrames = adaptiveEarlyEvidence
                    ? roiCompleteFrames
                    : panelSelectedStableFrames;
                var stableEnough = selectedStableFrames >= effectiveRequiredStableFrames
                    && (tracker.EvidenceGate.Stable || preselectedSelectionPresent);
                var roiEnough = panelReadable;
                acceptGateReason = !panelChangedFromBaseline
                    ? "waiting_for_panel_change"
                    : preselectedTargetEvidence && !preselectedSelectionPresent
                        ? "waiting_for_preselected_selection_stability"
                    : !tracker.EvidenceGate.Stable
                        ? "waiting_for_target_selection_stability"
                        : !roiVisibility.ValidBoundary
                        ? roiVisibility.InvalidReason
                        : roiCompleteFrames < requiredRoiCompleteFrames
                            ? "waiting_for_variable_roi_stability"
                            : !stableEnough
                                ? "waiting_for_stable_frame"
                                : elapsedMilliseconds < changedMinimumAcceptMs
                                    ? "before_min_accept"
                                    : "ready";
                if (runtimeState.QuickPanelAcceptEnabled
                    && !tracker.SelectionAttestedPanel
                    && !preselectedTargetEvidence
                    && panelTiming.WarmupComplete
                    && previousPanelSignatures is not null
                    && fullPanelReadable
                    && panelChangedFromBaseline)
                {
                    if (selectedStableFrames >= 1 && elapsedMilliseconds >= quickMinimumAcceptMs)
                    {
                        window.VerifyTraversalPosition();
                        return CreateAcceptedPanelCapture(
                            image,
                            EnsureAcceptedChangeProbeSignatures,
                            visibleCount,
                            start,
                            frameCount,
                            changeMilliseconds,
                            tracker.SelectionChangeMilliseconds,
                            fullRoiMilliseconds,
                            stableMilliseconds,
                            textCoreStableMilliseconds,
                            stabilityDecision,
                            captureMilliseconds,
                            ref signatureMilliseconds,
                            visibleRoiMilliseconds,
                            ref frameLoopMilliseconds,
                            ref frameToBitmapMilliseconds,
                            ref bitmapCreatedCount,
                            frameLoop,
                            changedMinimumAcceptMs,
                            requiredStableFrames,
                            panelTiming,
                            panelFloorReason,
                            () => "quick_changed_stable_full_roi",
                            () => TargetVerificationPolicy.ResolveAccepted(selectionRoundTripReady, tracker.EvidenceGate.Stable),
                            quickAccept: true,
                            quickRejectReason: "accepted",
                            roiCompleteFrames,
                            selectedStableFrames,
                            tracker.EvidenceGate.StableFrames,
                            "quick_ready");
                    }

                    quickRejectReason = selectedStableFrames < 1 ? "waiting_for_stable_frame" : "before_quick_min_accept";
                }
                else if (runtimeState.QuickPanelAcceptEnabled)
                {
                    quickRejectReason = !panelTiming.WarmupComplete
                        ? "before_warmup_complete"
                        : panelChangedFromBaseline
                            ? visibleCount == rois.Count ? "waiting_for_full_roi" : "variable_roi_requires_stability"
                            : "waiting_for_panel_change";
                }

                if (roiEnough
                    && panelChangedFromBaseline
                    && elapsedMilliseconds >= changedMinimumAcceptMs
                    && stableEnough)
                {
                    window.VerifyTraversalPosition();
                    return CreateAcceptedPanelCapture(
                        image,
                        EnsureAcceptedChangeProbeSignatures,
                        visibleCount,
                        start,
                        frameCount,
                        changeMilliseconds,
                        tracker.SelectionChangeMilliseconds,
                        fullRoiMilliseconds,
                        stableMilliseconds,
                        textCoreStableMilliseconds,
                        stabilityDecision,
                        captureMilliseconds,
                        ref signatureMilliseconds,
                        visibleRoiMilliseconds,
                        ref frameLoopMilliseconds,
                        ref frameToBitmapMilliseconds,
                        ref bitmapCreatedCount,
                        frameLoop,
                        changedMinimumAcceptMs,
                        requiredStableFrames,
                        panelTiming,
                        panelFloorReason,
                        () =>
                        {
                            var finalAcceptReason = stabilityDecision.Source == PanelStabilitySource.TextCore
                                ? acceptReason.Replace("stable", "text_core_stable", StringComparison.Ordinal)
                                : acceptReason;
                            if (adaptiveEarlyEvidence)
                            {
                                finalAcceptReason = panelTiming.EffectivePostScrollPanelAcceptMode == PostScrollPanelAcceptMode.AdaptiveAfterScroll
                                    ? "adaptive_after_scroll"
                                    : "adaptive_early_full_roi";
                            }

                            return finalAcceptReason;
                        },
                        () => TargetVerificationPolicy.ResolveAccepted(selectionRoundTripReady, tracker.EvidenceGate.Stable),
                        quickAccept: false,
                        quickRejectReason,
                        roiCompleteFrames,
                        selectedStableFrames,
                        tracker.EvidenceGate.StableFrames,
                        acceptGateReason);
                }

                var unchangedStableFrames = stabilityDecision.Source == PanelStabilitySource.TextCore
                    ? textCoreStableProbeFrames
                    : stableProbeFrames;
                if (panelReadable && !panelChangedFromBaseline && unchangedStableFrames >= requiredStableFrames && DateTime.UtcNow - start >= unchangedFallbackDelay)
                {
                    if (!tracker.PromoteSelectionChange(
                            selectionProbeRect,
                            beforeSelectionSignature,
                            frameCount,
                            elapsedMilliseconds,
                            allowSelectionOnlyFallback,
                            postScrollFirstCell,
                            selectionRoundTripReady,
                            weakPanelChange,
                            weakPanelChangeDistance,
                            scanLog,
                            ref sawPanelChange,
                            ref panelChangedFromBaseline,
                            ref acceptReason,
                            out var blockedReason))
                    {
                        if (!string.IsNullOrWhiteSpace(blockedReason))
                        {
                            tracker.WriteSelectionOnlyBlocked(blockedReason, elapsedMilliseconds, postScrollFirstCell, allowSelectionOnlyFallback, scanLog);
                        }

                        if (weakPanelChange)
                        {
                            tracker.WriteWeakPanelChangeBlocked(blockedReason.Length == 0 ? "no_selection_change" : blockedReason, elapsedMilliseconds, weakPanelChangeMilliseconds, weakPanelChangeDistance, scanLog);
                        }

                        scanLog.Write("Panel probes stayed unchanged past fallback delay; refusing to capture stale detail panel.");
                        throw new StalePanelException("详情面板未检测到变化，已拒绝复用旧面板。");
                    }
                }
            }
            else
            {
                roiCompleteFrames = 0;
            }

            frameLoop.Stop();
            frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
            await Task.Delay(interval, token);
        }

        LogAndThrowPanelCaptureTimeout(
            window,
            lastVisibleCount,
            rois.Count,
            lastRoiVisibility,
            acceptGateReason,
            sawPanelChange,
            tracker.SelectionChanged,
            stableProbeFrames,
            textCoreStableProbeFrames,
            requiredStableFrames,
            frameCount,
            scanLog);
        throw new UnreachableException();
    }
}
