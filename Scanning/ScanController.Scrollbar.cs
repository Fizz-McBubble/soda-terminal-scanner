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

    private static ScrollbarThumbProbe CaptureScrollbarThumbProbe(
        GameWindow window,
        ScanProfile profile,
        bool includeThumbEnds = false,
        int? maximumCandidateHeight = null)
    {
        var bounds = ScrollbarProbeBounds(window, profile, includeThumbEnds);
        using var image = window.CaptureFrame(bounds);
        return ExtractScrollbarThumbProbe(
            image,
            bounds,
            bounds,
            profile.Color("scrollBar"),
            Math.Max(0, profile.ColorTolerance),
            maximumCandidateHeight);
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
        CapturedFrame image,
        Rectangle frameBounds,
        Rectangle bounds,
        Color expected,
        int tolerance,
        int? maximumCandidateHeight = null)
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
        // Preserve the proven detector for ordinary (including narrow or
        // antialiased) thumbs. Only recover an oversized row union that reset
        // would otherwise reject. Never split an oversized connected region.
        if (bestStart >= 0 && maximumCandidateHeight is int maximumHeight
            && bestEnd - bestStart + 1 > maximumHeight)
        {
            return ExtractBoundedScrollbarThumbProbe(
                image, bounds, localLeft, localTop, expected, tolerance, maximumHeight);
        }

        return bestStart < 0
            ? default
            : new ScrollbarThumbProbe(true, bestStart, bestEnd, bestCenterX, (bestStart + bestEnd) / 2);
    }

    private static ScrollbarThumbProbe ExtractBoundedScrollbarThumbProbe(
        CapturedFrame image,
        Rectangle bounds,
        int localLeft,
        int localTop,
        Color expected,
        int tolerance,
        int maximumHeight)
    {
        if (maximumHeight <= 0)
        {
            return default;
        }

        var matched = new bool[bounds.Width * bounds.Height];
        for (var localY = 0; localY < bounds.Height; localY++)
        {
            for (var localX = 0; localX < bounds.Width; localX++)
            {
                matched[localY * bounds.Width + localX] =
                    image.GetPixel(localLeft + localX, localTop + localY)
                    .IsCloseTo(expected, tolerance);
            }
        }

        var visited = new bool[matched.Length];
        var legalCandidate = default(ScrollbarThumbProbe);
        var legalCandidates = 0;
        var pending = new Stack<(int X, int Y)>();

        for (var localY = 0; localY < bounds.Height; localY++)
        {
            for (var localX = 0; localX < bounds.Width; localX++)
            {
                var startIndex = localY * bounds.Width + localX;
                if (!matched[startIndex] || visited[startIndex])
                {
                    continue;
                }

                var minX = localX;
                var maxX = localX;
                var minY = localY;
                var maxY = localY;
                var pixelCount = 0;
                visited[startIndex] = true;
                pending.Push((localX, localY));

                while (pending.Count > 0)
                {
                    var (x, y) = pending.Pop();
                    pixelCount++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);

                    Visit(x - 1, y);
                    Visit(x + 1, y);
                    Visit(x, y - 1);
                    Visit(x, y + 1);
                }

                var height = maxY - minY + 1;
                var width = maxX - minX + 1;
                // Two columns of support, allowing a pixel per rounded corner;
                // a thin border with one sideways spur is not a thumb.
                if (height < 3 || height > maximumHeight || width < 2
                    || pixelCount < 2 * height - 4)
                {
                    continue;
                }

                legalCandidates++;
                if (legalCandidates > 1)
                {
                    return default;
                }

                var centerX = bounds.Left + (int)Math.Round(
                    (minX + maxX) / 2d, MidpointRounding.AwayFromZero);
                legalCandidate = new ScrollbarThumbProbe(
                    true,
                    bounds.Top + minY,
                    bounds.Top + maxY,
                    centerX,
                    bounds.Top + (minY + maxY) / 2);
            }
        }

        return legalCandidate;

        void Visit(int x, int y)
        {
            if (x < 0 || x >= bounds.Width || y < 0 || y >= bounds.Height)
            {
                return;
            }

            var index = y * bounds.Width + x;
            if (!matched[index] || visited[index])
            {
                return;
            }

            visited[index] = true;
            pending.Push((x, y));
        }
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
