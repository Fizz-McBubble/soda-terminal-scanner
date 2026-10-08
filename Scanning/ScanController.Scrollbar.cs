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
    internal readonly record struct ScrollbarThumbProbe(
        bool Found,
        int StartY,
        int EndY,
        int CenterX,
        int CenterY);

    private static ScrollbarThumbProbe CaptureScrollbarThumbProbe(GameWindow window, ScanProfile profile, bool includeThumbEnds = false)
    {
        var bounds = ScrollbarProbeBounds(window, profile, includeThumbEnds);
        using var image = window.CaptureFrame(bounds);
        return ExtractScrollbarThumbProbe(image, bounds, bounds, profile.Color("scrollBar"), Math.Max(0, profile.ColorTolerance));
    }

    private static Rectangle ScrollbarProbeBounds(GameWindow window, ScanProfile profile, bool includeThumbEnds = false)
    {
        var top = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var bottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var minY = Math.Min(top.Y, bottom.Y);
        var maxY = Math.Max(top.Y, bottom.Y);
        if (includeThumbEnds)
        {
            // The frozen top/bottom points are colour-probe anchors inside the
            // thumb. Cropping there changes its measured height at the ends.
            var padding = Math.Max(4, (int)Math.Ceiling(window.ClientScreenRect.Height * 16 / 1080d));
            minY = Math.Max(window.ClientScreenRect.Top, minY - padding);
            maxY = Math.Min(window.ClientScreenRect.Bottom - 1, maxY + padding);
        }
        return new Rectangle(top.X - 2, minY, 5, Math.Max(1, maxY - minY + 1));
    }

    internal static ScrollbarThumbProbe ExtractScrollbarThumbProbe(
        CapturedFrame image, Rectangle frameBounds, Rectangle bounds, Color expected, int tolerance)
    {
        var minY = bounds.Top;
        var maxY = bounds.Bottom - 1;
        var localLeft = bounds.Left - frameBounds.Left;
        var localTop = bounds.Top - frameBounds.Top;
        if (localLeft < 0 || localTop < 0
            || localLeft + bounds.Width > image.Width || localTop + bounds.Height > image.Height)
        {
            return default;
        }
        var bestStart = -1;
        var bestEnd = -1;
        var bestCenterX = bounds.Left + bounds.Width / 2;
        var currentStart = -1;
        var currentXCounts = new int[bounds.Width];

        void CompleteRun(int endY)
        {
            if (currentStart < 0)
            {
                return;
            }

            if (bestStart < 0 || endY - currentStart > bestEnd - bestStart)
            {
                bestStart = currentStart;
                bestEnd = endY;
                long weightedX = 0;
                var matchedPixels = 0;
                for (var x = 0; x < currentXCounts.Length; x++)
                {
                    weightedX += (long)x * currentXCounts[x];
                    matchedPixels += currentXCounts[x];
                }

                bestCenterX = bounds.Left + (matchedPixels > 0
                    ? (int)Math.Round(weightedX / (double)matchedPixels, MidpointRounding.AwayFromZero)
                    : bounds.Width / 2);
            }

            currentStart = -1;
            Array.Clear(currentXCounts);
        }

        for (var localY = 0; localY < bounds.Height; localY++)
        {
            var matched = false;
            for (var localX = 0; localX < bounds.Width; localX++)
            {
                if (image.GetPixel(localLeft + localX, localTop + localY).IsCloseTo(expected, tolerance))
                {
                    matched = true;
                    currentXCounts[localX]++;
                }
            }

            if (matched)
            {
                currentStart = currentStart < 0 ? minY + localY : currentStart;
            }
            else
            {
                CompleteRun(minY + localY - 1);
            }
        }

        CompleteRun(maxY);
        return bestStart < 0
            ? default
            : new ScrollbarThumbProbe(true, bestStart, bestEnd, bestCenterX, (bestStart + bestEnd) / 2);
    }

    private static int? ScrollbarDelta(ScrollbarThumbProbe before, ScrollbarThumbProbe after) =>
        before.Found && after.Found ? after.CenterY - before.CenterY : null;

    private static int? DirectionalScrollbarDelta(
        ScrollbarThumbProbe before,
        ScrollbarThumbProbe after,
        bool upward)
    {
        var delta = ScrollbarDelta(before, after);
        return delta is int value ? (upward ? -value : value) : null;
    }

    private static double ScrollbarPixelsPerLogicalRow(
        GameWindow window,
        ScanProfile profile,
        ScrollbarThumbProbe thumb,
        int maxVisibleTop)
    {
        if (!thumb.Found || maxVisibleTop <= 1)
        {
            return 0;
        }

        var top = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var bottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var thumbHeight = Math.Max(1, thumb.EndY - thumb.StartY + 1);
        var halfThumb = (thumbHeight - 1) / 2.0;
        var minimumCenter = Math.Min(top.Y, bottom.Y) + halfThumb;
        var maximumCenter = Math.Max(top.Y, bottom.Y) - halfThumb;
        return Math.Max(0, maximumCenter - minimumCenter) / (maxVisibleTop - 1);
    }

}
