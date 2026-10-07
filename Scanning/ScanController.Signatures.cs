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
    private static ImageSignature[] CaptureCurrentPanelSignatures(
        GameWindow window,
        Rectangle panelRect,
        Rectangle panelChangeProbeRect,
        IReadOnlyList<CvRect> rois)
    {
        var probeScreenRect = PanelProbeScreenRect(panelRect, panelChangeProbeRect);
        using var image = window.CaptureFrame(probeScreenRect);
        var probeRects = BuildPanelChangeProbeRectsForProbe(probeScreenRect, panelRect, rois);
        return CreateSignatures(image, probeRects);
    }

    private static ImageSignature CaptureSignature(GameWindow window, Rectangle rect)
    {
        using var image = window.CaptureFrame(rect);
        return CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
    }

    private static ulong CaptureGridCellFingerprint(GameWindow window, Point clickPoint, PointF step)
    {
        var bounds = window.ClientScreenRect;
        var halfWidth = Math.Clamp((int)Math.Round(Math.Abs(step.X) * 0.22), 16, 32);
        var halfHeight = Math.Clamp((int)Math.Round(Math.Abs(step.Y) * 0.22), 20, 40);
        var rect = Rectangle.Intersect(
            Rectangle.FromLTRB(
                clickPoint.X - halfWidth,
                clickPoint.Y - halfHeight,
                clickPoint.X + halfWidth,
                clickPoint.Y + halfHeight),
            bounds);
        using var image = window.CaptureFrame(rect);
        return RowVisualSignatureExtractor.Create(
            image,
            new Rectangle(Point.Empty, image.Size)).Hash;
    }

    private static ImageSignature CaptureScreenSignature(Rectangle rect)
    {
        using var image = new Bitmap(rect.Width, rect.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, image.Size);
        }

        return CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
    }

    private static ImageSignature CaptureSelectionSignature(Rectangle rect)
    {
        using var image = new Bitmap(rect.Width, rect.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, image.Size);
        }

        return new ImageSignature(0, SelectionVisualProbe.CreateSamples(image), EmphasizeLocalizedChanges: true);
    }

    private static Rectangle[] BuildPanelChangeProbeRects(Rectangle panelProbeRect, Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var rects = new List<Rectangle>(Math.Max(1, rois.Count));

        foreach (var roi in rois)
        {
            rects.Add(ClampRectangle(new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), imageSize));
        }

        if (rects.Count == 0)
        {
            rects.Add(panelProbeRect);
        }

        return rects.ToArray();
    }

    private static Rectangle[] BuildPanelChangeProbeRectsForProbe(Rectangle probeScreenRect, Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(probeScreenRect.Width, probeScreenRect.Height);
        var rects = new List<Rectangle>(Math.Max(1, rois.Count));

        foreach (var roi in rois)
        {
            var roiScreenRect = new Rectangle(panelRect.Left + roi.X, panelRect.Top + roi.Y, roi.Width, roi.Height);
            var intersection = Rectangle.Intersect(roiScreenRect, probeScreenRect);
            if (intersection.IsEmpty)
            {
                continue;
            }

            rects.Add(ClampRectangle(new Rectangle(
                intersection.Left - probeScreenRect.Left,
                intersection.Top - probeScreenRect.Top,
                intersection.Width,
                intersection.Height), imageSize));
        }

        if (rects.Count == 0)
        {
            rects.Add(new Rectangle(System.Drawing.Point.Empty, imageSize));
        }

        return rects.ToArray();
    }

    private static Rectangle PanelProbeScreenRect(Rectangle panelRect, Rectangle panelChangeProbeRect)
    {
        var probe = Rectangle.Intersect(panelRect, panelChangeProbeRect);
        return probe.IsEmpty ? panelRect : probe;
    }

    private static Rectangle[] TranslateProbeRectsToPanel(IReadOnlyList<Rectangle> probeRects, Rectangle probeScreenRect, Rectangle panelRect)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var dx = probeScreenRect.Left - panelRect.Left;
        var dy = probeScreenRect.Top - panelRect.Top;
        var translated = new Rectangle[probeRects.Count];
        for (var i = 0; i < translated.Length; i++)
        {
            var rect = probeRects[i];
            translated[i] = ClampRectangle(new Rectangle(rect.Left + dx, rect.Top + dy, rect.Width, rect.Height), imageSize);
        }

        return translated;
    }

    private static Rectangle[] BuildPanelStableProbeRects(Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var stableRoiCount = Math.Min(rois.Count, 4);
        var rects = new List<Rectangle>(stableRoiCount);

        for (var i = 0; i < stableRoiCount; i++)
        {
            var roi = rois[i];
            rects.Add(ClampRectangle(new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), imageSize));
        }

        return rects.ToArray();
    }

    private static Rectangle[] BuildTextCoreStableProbeRects(Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var rects = new List<Rectangle>(rois.Count);

        foreach (var roi in rois)
        {
            var cropX = (int)Math.Round(roi.Width * 0.06);
            var cropY = (int)Math.Round(roi.Height * 0.18);
            var left = roi.X + cropX;
            var top = roi.Y + cropY;
            var width = Math.Max(4, roi.Width - cropX * 2);
            var height = Math.Max(4, roi.Height - cropY * 2);

            rects.Add(ClampRectangle(new Rectangle(left, top, width, height), imageSize));
        }

        return rects.ToArray();
    }

    private static ImageSignature[] CreateSignatures(Bitmap image, IReadOnlyList<Rectangle> rects)
    {
        var signatures = new ImageSignature[rects.Count];
        for (var i = 0; i < signatures.Length; i++)
        {
            signatures[i] = CreateSignature(image, rects[i]);
        }

        return signatures;
    }

    private static ImageSignature[] CreateSignatures(CapturedFrame image, IReadOnlyList<Rectangle> rects)
    {
        var signatures = new ImageSignature[rects.Count];
        for (var i = 0; i < signatures.Length; i++)
        {
            signatures[i] = CreateSignature(image, rects[i]);
        }

        return signatures;
    }

    private static bool AreProbesStable(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures)
    {
        var count = Math.Min(previousSignatures.Count, currentSignatures.Count);
        for (var i = 0; i < count; i++)
        {
            if (SignatureDistance(previousSignatures[i], currentSignatures[i]) > PanelStableTolerance)
            {
                return false;
            }
        }

        return count > 0;
    }

    private static bool HasProbeChange(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures, int tolerance)
    {
        return ProbeChangeDistance(previousSignatures, currentSignatures) > tolerance;
    }

    private static int ProbeChangeDistance(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures)
    {
        var count = Math.Min(previousSignatures.Count, currentSignatures.Count);
        if (count == 0 || previousSignatures.Count != currentSignatures.Count)
        {
            return int.MaxValue;
        }

        var maxDistance = 0;
        for (var i = 0; i < count; i++)
        {
            maxDistance = Math.Max(maxDistance, SignatureDistance(previousSignatures[i], currentSignatures[i]));
        }

        return maxDistance;
    }

    private static ImageSignature CreateSignature(Bitmap image, Rectangle rect)
    {
        rect = ClampRectangle(rect, image.Size);
        var samples = new int[SignatureColumns * SignatureRows];
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var index = 0;

        for (var row = 0; row < SignatureRows; row++)
        {
            var y = rect.Top + Math.Min(rect.Height - 1, (int)Math.Round((row + 0.5) * rect.Height / SignatureRows));
            for (var col = 0; col < SignatureColumns; col++)
            {
                var x = rect.Left + Math.Min(rect.Width - 1, (int)Math.Round((col + 0.5) * rect.Width / SignatureColumns));
                var color = image.GetPixel(x, y);
                var luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                samples[index++] = luma;
                hash ^= color.R;
                hash *= prime;
                hash ^= color.G;
                hash *= prime;
                hash ^= color.B;
                hash *= prime;
            }
        }

        return new ImageSignature(hash, samples);
    }

    private static ImageSignature CreateSignature(CapturedFrame image, Rectangle rect)
    {
        rect = ClampRectangle(rect, image.Size);
        var samples = new int[SignatureColumns * SignatureRows];
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var index = 0;

        for (var row = 0; row < SignatureRows; row++)
        {
            var y = rect.Top + Math.Min(rect.Height - 1, (int)Math.Round((row + 0.5) * rect.Height / SignatureRows));
            for (var col = 0; col < SignatureColumns; col++)
            {
                var x = rect.Left + Math.Min(rect.Width - 1, (int)Math.Round((col + 0.5) * rect.Width / SignatureColumns));
                var color = image.GetPixel(x, y);
                var luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                samples[index++] = luma;
                hash ^= color.R;
                hash *= prime;
                hash ^= color.G;
                hash *= prime;
                hash ^= color.B;
                hash *= prime;
            }
        }

        return new ImageSignature(hash, samples);
    }

    private static int SignatureDistance(ImageSignature left, ImageSignature right)
    {
        if (left.Samples.Length == 0 || right.Samples.Length == 0)
        {
            return left.Hash == right.Hash ? 0 : int.MaxValue;
        }

        return left.EmphasizeLocalizedChanges || right.EmphasizeLocalizedChanges
            ? SelectionVisualProbe.MeasureLocalizedMovement(left.Samples, right.Samples)
            : VisualProbeEvaluator.MeasureLuminanceMovement(left.Samples, right.Samples);
    }

    private static Rectangle RelativeIntersection(Rectangle childScreenRect, Rectangle parentScreenRect, System.Drawing.Size imageSize)
    {
        var intersect = Rectangle.Intersect(childScreenRect, parentScreenRect);
        if (intersect.IsEmpty)
        {
            return new Rectangle(System.Drawing.Point.Empty, imageSize);
        }

        return ClampRectangle(new Rectangle(
            intersect.Left - parentScreenRect.Left,
            intersect.Top - parentScreenRect.Top,
            intersect.Width,
            intersect.Height), imageSize);
    }

    private static void ObserveAcceptedPanel(
        ScanRuntimeState runtimeState,
        PanelCapture panelCapture,
        ScanLog scanLog,
        bool postScrollFirstCell)
    {
        runtimeState.AdaptiveTiming?.ObservePanel(
            panelCapture.ChangeMilliseconds,
            panelCapture.FullRoiMilliseconds,
            panelCapture.StableMilliseconds,
            panelCapture.WaitMilliseconds,
            panelCapture.CaptureMilliseconds,
            panelCapture.FrameLoopMilliseconds,
            postScrollFirstCell);
        runtimeState.ProfileHealth.ObservePanel(panelCapture.WaitMilliseconds, panelCapture.CaptureMilliseconds, scanLog.Write);
        runtimeState.PanelStability.Observe(
            panelCapture.StableMilliseconds,
            panelCapture.PanelTextStableMilliseconds,
            panelCapture.PanelStableSource,
            panelCapture.PanelStabilityReason);
        runtimeState.PanelProbeHealth.Observe(
            panelCapture.ChangeMilliseconds,
            panelCapture.PanelAcceptMode,
            scanLog.Write);
    }


    private readonly record struct ImageSignature(
        ulong Hash,
        int[] Samples,
        bool EmphasizeLocalizedChanges = false);

}
