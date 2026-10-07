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
    private static Rectangle ClampRectangle(Rectangle rect, System.Drawing.Size bounds)
    {
        var left = Math.Clamp(rect.Left, 0, Math.Max(0, bounds.Width - 1));
        var top = Math.Clamp(rect.Top, 0, Math.Max(0, bounds.Height - 1));
        var right = Math.Clamp(rect.Right, left + 1, bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, top + 1, bounds.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static AdaptivePanelTimingDecision ResolveDefaultPanelTiming(
        ScanProfile profile,
        string captureMode,
        int panelMinAcceptFloorMs = 120,
        PanelFloorMode panelFloorMode = PanelFloorMode.Static,
        int sameRowPanelFloorMs = 105,
        int postScrollPanelFloorMs = 110)
    {
        var captureModeMinimum = string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(panelMinAcceptFloorMs, 90, 120)
            : PanelCaptureTimingPolicy.GdiSafeMinimumAcceptMilliseconds;
        return new AdaptivePanelTimingDecision(
            PanelCaptureTimingPolicy.ResolveSafeMinimumAcceptMilliseconds(
                profile.PanelChangedMinimumAcceptMs,
                panelMinAcceptFloorMs,
                profile.LoadTimeoutMs,
                captureMode),
            PanelCaptureTimingPolicy.SafeRequiredStableFrames,
            0,
            WarmupComplete: false,
            AppliedAdaptiveMinimum: false,
            Reason: "disabled",
            PanelAcceptMode.Safe,
            PostScrollPanelAcceptMode.Safe,
            panelFloorMode,
            captureModeMinimum,
            Math.Clamp(sameRowPanelFloorMs, 100, 120),
            Math.Clamp(postScrollPanelFloorMs, 100, 120),
            "disabled");
    }

    private static AdaptivePanelTimingDecision ResolveEffectivePanelTiming(
        ScanProfile profile,
        GameWindow window,
        ScanRuntimeState runtimeState,
        bool postScrollFirstCell,
        bool sceneAdaptivePanelFloorEligible)
    {
        var panelAcceptMode = runtimeState.PanelProbeHealth.ForceSafe || runtimeState.ProfileHealth.ForceSafePanel
            ? PanelAcceptMode.Safe
            : runtimeState.PanelAcceptMode;
        var postScrollPanelAcceptMode = runtimeState.PanelProbeHealth.ForceSafe || runtimeState.ProfileHealth.ForceSafePanel
            ? PostScrollPanelAcceptMode.Safe
            : runtimeState.PostScrollPanelAcceptMode;
        var panelTiming = runtimeState.AdaptiveTiming?.ResolvePanelTiming(
                profile,
                window.ActiveCaptureMode,
                panelAcceptMode,
                postScrollFirstCell,
                postScrollPanelAcceptMode,
                runtimeState.PanelMinAcceptFloorMs,
                runtimeState.PanelFloorMode,
                sceneAdaptivePanelFloorEligible,
                runtimeState.SameRowPanelMinAcceptFloorMs,
                runtimeState.PostScrollPanelMinAcceptFloorMs)
            ?? ResolveDefaultPanelTiming(profile, window.ActiveCaptureMode, runtimeState.PanelMinAcceptFloorMs, runtimeState.PanelFloorMode, runtimeState.SameRowPanelMinAcceptFloorMs, runtimeState.PostScrollPanelMinAcceptFloorMs);
        if (postScrollFirstCell && panelTiming.EffectivePostScrollPanelAcceptMode == PostScrollPanelAcceptMode.Safe)
        {
            panelTiming = ResolveDefaultPanelTiming(profile, window.ActiveCaptureMode, runtimeState.PanelMinAcceptFloorMs, runtimeState.PanelFloorMode, runtimeState.SameRowPanelMinAcceptFloorMs, runtimeState.PostScrollPanelMinAcceptFloorMs);
        }
        return panelTiming;
    }

    private static void UpdateStabilityProbes(
        CapturedFrame image,
        Rectangle[] stableProbeRects,
        ref ImageSignature[]? previousStableProbeSignatures,
        ref int stableProbeFrames,
        ref double? stableMilliseconds,
        bool measureTextCoreStability,
        Rectangle[] textCoreStableProbeRects,
        ref ImageSignature[]? previousTextCoreStableProbeSignatures,
        ref int textCoreStableProbeFrames,
        ref double? textCoreStableMilliseconds,
        PanelStabilityDecision stabilityDecision,
        int requiredStableFrames,
        double elapsedMilliseconds)
    {
        if (stableProbeRects.Length > 0)
        {
            var stableProbeSignatures = CreateSignatures(image, stableProbeRects);
            stableProbeFrames = previousStableProbeSignatures is not null
                && AreProbesStable(previousStableProbeSignatures, stableProbeSignatures)
                    ? stableProbeFrames + 1
                    : 0;
            previousStableProbeSignatures = stableProbeSignatures;
            if (stableProbeFrames >= requiredStableFrames)
            {
                stableMilliseconds ??= elapsedMilliseconds;
            }
        }

        if (measureTextCoreStability)
        {
            var textCoreStableProbeSignatures = CreateSignatures(image, textCoreStableProbeRects);
            textCoreStableProbeFrames = previousTextCoreStableProbeSignatures is not null
                && AreProbesStable(previousTextCoreStableProbeSignatures, textCoreStableProbeSignatures)
                    ? textCoreStableProbeFrames + 1
                    : 0;
            previousTextCoreStableProbeSignatures = textCoreStableProbeSignatures;
            if (textCoreStableProbeFrames >= requiredStableFrames)
            {
                textCoreStableMilliseconds ??= elapsedMilliseconds;
                if (stabilityDecision.Source == PanelStabilitySource.TextCore)
                {
                    stableMilliseconds ??= elapsedMilliseconds;
                }
            }
        }
    }

    private static void LogAndThrowPanelCaptureTimeout(
        GameWindow window,
        int lastVisibleCount,
        int totalRois,
        VisibleRoiEvaluation lastRoiVisibility,
        string acceptGateReason,
        bool sawPanelChange,
        bool selectionChanged,
        int stableProbeFrames,
        int textCoreStableProbeFrames,
        int requiredStableFrames,
        int frameCount,
        ScanLog scanLog)
    {
        var timeoutException = new PanelCaptureTimeoutException(
            lastVisibleCount,
            totalRois,
            lastRoiVisibility.FirstMissingRoi,
            lastRoiVisibility.FirstMissingProbe,
            acceptGateReason,
            sawPanelChange,
            selectionChanged,
            Math.Max(stableProbeFrames, textCoreStableProbeFrames),
            requiredStableFrames,
            frameCount);
        scanLog.WriteEvent(
            "PANEL_CAPTURE_TIMEOUT",
            $"visibleRois={timeoutException.VisibleRois}/{timeoutException.TotalRois}, firstMissingRoi={timeoutException.FirstMissingRoi ?? "none"}, referenceLuma={FormatOptionalInt(timeoutException.ReferenceLuma)}, candidateLuma={FormatOptionalInt(timeoutException.CandidateLuma)}, lumaDelta={FormatOptionalInt(timeoutException.LumaDelta)}, allowedLumaDelta={FormatOptionalInt(timeoutException.AllowedLumaDelta)}, edgeDensityPermille={FormatOptionalInt(timeoutException.EdgeDensityPermille)}, minimumEdgeDensityPermille={FormatOptionalInt(timeoutException.MinimumEdgeDensityPermille)}, acceptGateReason={timeoutException.AcceptGateReason}, sawPanelChange={timeoutException.SawPanelChange}, selectionChanged={timeoutException.SelectionChanged}, stableFrames={timeoutException.StableFrames}/{timeoutException.RequiredStableFrames}, frameCount={timeoutException.FrameCount}, captureMode={window.ActiveCaptureMode}");
        throw timeoutException;
    }
}
