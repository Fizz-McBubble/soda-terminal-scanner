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
    private static Rectangle ProfileRectangleOrFallback(GameWindow window, ScanProfile profile, string key, Rectangle fallback)
    {
        return profile.HasRectangle(key) ? window.ToScreenRectangle(profile.Rectangle(key)) : fallback;
    }

    private static Rectangle BuildListGridFallback(GameWindow window, ScanProfile profile, int visibleRows, int columns)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var left = offset.X + step.X - step.X * 0.9f;
        var top = offset.Y + step.Y - step.Y * 0.72f;
        var width = step.X * columns + step.X * 0.55f;
        var height = step.Y * visibleRows;
        return window.ToScreenRectangle(new RectangleF(left, top, width, height));
    }


    private static CvRect[] BuildRois(GameWindow window, ScanProfile profile, Rectangle panelRect)
    {
        return profile.OrderedRoiKeys()
            .Select(key =>
            {
                var rect = window.ToScreenRectangle(profile.Rectangle(key));
                return new CvRect(rect.X - panelRect.X, rect.Y - panelRect.Y, rect.Width, rect.Height);
            })
            .ToArray();
    }

    private static readonly LockStateEvidence.Combined NotCollectedCombinedLockEvidence =
        LockStateEvidence.Combine(
            new LockStateEvidence.Sample("card-bottom-right", null, 0, "not_collected"),
            new LockStateEvidence.Sample("detail-lock-button", null, 0, "not_collected"));

    internal static LockCaptureEvidence UnknownLockEvidence(string gate = "not_collected")
    {
        if (gate == "not_collected")
        {
            return new LockCaptureEvidence(null, NotCollectedCombinedLockEvidence);
        }

        var card = new LockStateEvidence.Sample("card-bottom-right", null, 0, gate);
        var detail = new LockStateEvidence.Sample("detail-lock-button", null, 0, gate);
        return new LockCaptureEvidence(null, LockStateEvidence.Combine(card, detail));
    }

    internal sealed record VisibleRoiEvaluation(
        int Count,
        string? FirstMissingRoi,
        RowPresenceProbeResult? FirstMissingProbe,
        bool ValidBoundary,
        string InvalidReason);

    internal static VisibleRoiEvaluation EvaluateVariableRoiLayout(
        IReadOnlyList<bool> presence,
        IReadOnlyList<string> roiKeys,
        IReadOnlyList<RowPresenceProbeResult?>? probes = null)
    {
        const int requiredCoreRois = 4;
        if (presence.Count < requiredCoreRois || roiKeys.Count != presence.Count)
        {
            return new VisibleRoiEvaluation(0, roiKeys.FirstOrDefault(), null, false, "invalid_roi_layout");
        }

        for (var index = 0; index < requiredCoreRois; index++)
        {
            if (!presence[index])
            {
                return new VisibleRoiEvaluation(
                    index,
                    roiKeys[index],
                    probes?.ElementAtOrDefault(index),
                    false,
                    "required_core_missing");
            }
        }

        var readableCount = requiredCoreRois;
        var boundaryFound = false;
        string? firstMissing = null;
        RowPresenceProbeResult? firstMissingProbe = null;
        for (var index = requiredCoreRois; index + 1 < presence.Count; index += 2)
        {
            var namePresent = presence[index];
            var valuePresent = presence[index + 1];
            if (namePresent != valuePresent)
            {
                var valueProbe = probes?.ElementAtOrDefault(index + 1);
                var terminalNameFalsePositive = index + 2 == presence.Count
                    && namePresent
                    && !valuePresent
                    && valueProbe is
                    {
                        Present: false,
                        CandidateLuma: 0,
                        EdgeDensityPermille: 0
                    };
                if (terminalNameFalsePositive)
                {
                    boundaryFound = true;
                    firstMissing ??= roiKeys[index];
                    firstMissingProbe ??= valueProbe;
                    continue;
                }

                var missingIndex = namePresent ? index + 1 : index;
                return new VisibleRoiEvaluation(
                    readableCount,
                    roiKeys[missingIndex],
                    probes?.ElementAtOrDefault(missingIndex),
                    false,
                    "incomplete_substat_pair");
            }

            if (!namePresent)
            {
                boundaryFound = true;
                firstMissing ??= roiKeys[index];
                firstMissingProbe ??= probes?.ElementAtOrDefault(index);
                continue;
            }

            if (boundaryFound)
            {
                return new VisibleRoiEvaluation(
                    readableCount,
                    firstMissing,
                    firstMissingProbe,
                    false,
                    "substat_gap");
            }

            readableCount += 2;
        }

        return new VisibleRoiEvaluation(readableCount, firstMissing, firstMissingProbe, true, "");
    }

    internal static int RequiredRoiBoundaryFrames(int visibleCount, int totalCount, int configuredStableFrames) =>
        visibleCount == totalCount ? 1 : Math.Max(3, configuredStableFrames);

    private static VisibleRoiEvaluation EvaluateVisibleRois(
        Bitmap image,
        IReadOnlyList<CvRect> rois,
        IReadOnlyList<string> roiKeys,
        System.Drawing.Point statOffset,
        RowPresenceProbePolicy policy)
    {
        var referenceRoi = rois.Count > 2 ? rois[2] : rois[0];
        var presence = new bool[rois.Count];
        var probes = new RowPresenceProbeResult?[rois.Count];
        for (var index = 0; index < rois.Count; index++)
        {
            RowPresenceProbeResult? probe = index <= 3
                ? null
                : VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, referenceRoi, rois[index], statOffset, policy);
            probes[index] = probe;
            presence[index] = probe is null || probe.Value.Present;
        }

        return EvaluateVariableRoiLayout(presence, roiKeys, probes);
    }

    private static VisibleRoiEvaluation EvaluateVisibleRois(
        CapturedFrame image,
        IReadOnlyList<CvRect> rois,
        IReadOnlyList<string> roiKeys,
        System.Drawing.Point statOffset,
        RowPresenceProbePolicy policy)
    {
        var referenceRoi = rois.Count > 2 ? rois[2] : rois[0];
        var presence = new bool[rois.Count];
        var probes = new RowPresenceProbeResult?[rois.Count];
        for (var index = 0; index < rois.Count; index++)
        {
            RowPresenceProbeResult? probe = index <= 3
                ? null
                : VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, referenceRoi, rois[index], statOffset, policy);
            probes[index] = probe;
            presence[index] = probe is null || probe.Value.Present;
        }

        return EvaluateVariableRoiLayout(presence, roiKeys, probes);
    }

}
