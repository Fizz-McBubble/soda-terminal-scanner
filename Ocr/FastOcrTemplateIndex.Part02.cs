using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ZZZScannerNext.Core;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public sealed class FastOcrImageFeature
{
    public const int Size = 16;
    public const int LegacyBitCount = Size * Size;
    public const int CurrentBitCount = LegacyBitCount * 2;
    public const int ExperimentalBitCount = LegacyBitCount * 3;
    public const int CanonicalBitCount = LegacyBitCount * 4;

    private FastOcrImageFeature(IReadOnlyList<ulong> words)
    {
        Words = words.ToArray();
    }

    private FastOcrImageFeature(IReadOnlyList<ulong> words, bool canonicalCropSucceeded, bool canonicalCropFallback, double featureElapsedMs)
    {
        Words = words.ToArray();
        CanonicalCropSucceeded = canonicalCropSucceeded;
        CanonicalCropFallback = canonicalCropFallback;
        FeatureElapsedMs = featureElapsedMs;
    }

    public IReadOnlyList<ulong> Words { get; }
    public bool CanonicalCropSucceeded { get; }
    public bool CanonicalCropFallback { get; }
    public double FeatureElapsedMs { get; }

    public int BitCount => Words.Count * 64;

    public static FastOcrImageFeature FromBitmap(Bitmap source)
    {
        return FromBitmap(source, new Rectangle(0, 0, source.Width, source.Height), FastOcrTemplateIndex.CurrentFeature);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, string featureName)
    {
        return FromBitmap(source, new Rectangle(0, 0, source.Width, source.Height), featureName);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, Rectangle sourceRect)
    {
        return FromBitmap(source, sourceRect, FastOcrTemplateIndex.CurrentFeature);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, Rectangle sourceRect, string featureName)
    {
        var sw = Stopwatch.StartNew();
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var rect = Rectangle.Intersect(bounds, sourceRect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            sw.Stop();
            return new FastOcrImageFeature(EmptyWordsForFeature(featureName), false, true, sw.Elapsed.TotalMilliseconds);
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.LegacyFeature, StringComparison.OrdinalIgnoreCase))
        {
            sw.Stop();
            return new FastOcrImageFeature(BuildAverageHash(source, rect), false, false, sw.Elapsed.TotalMilliseconds);
        }

        var useCanonical = string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase);
        var canonicalSucceeded = false;
        var canonicalFallback = false;
        Rectangle? canonical = null;
        if (useCanonical)
        {
            canonical = TryCanonicalize(source, rect, out _, out canonicalSucceeded, out canonicalFallback);
        }
        else
        {
            canonicalSucceeded = false;
            canonicalFallback = false;
        }

        var featureRect = canonical ?? rect;
        var words = new List<ulong>(16);
        words.AddRange(BuildAverageHash(source, featureRect));
        words.AddRange(BuildHorizontalDifferenceHash(source, featureRect));
        if (string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase)
            || useCanonical)
        {
            words.AddRange(BuildVerticalDifferenceHash(source, featureRect));
        }

        if (useCanonical)
        {
            words.AddRange(BuildEdgeDensityHash(source, featureRect));
        }

        sw.Stop();
        return new FastOcrImageFeature(words, canonicalSucceeded, canonicalFallback, sw.Elapsed.TotalMilliseconds);
    }

    public static FastOcrImageFeature FromHexWords(IReadOnlyList<string> words)
    {
        var parsed = words
            .Select(Parse)
            .ToArray();
        return new FastOcrImageFeature(parsed.Length == 0 ? [0, 0, 0, 0] : parsed);
    }

    public string[] ToHexWords()
    {
        return Words
            .Select(word => word.ToString("X16", CultureInfo.InvariantCulture))
            .ToArray();
    }

    public string ToKey()
    {
        return string.Join("", ToHexWords());
    }

    public int DistanceTo(FastOcrImageFeature other)
    {
        var count = Math.Max(Words.Count, other.Words.Count);
        var distance = 0;
        for (var i = 0; i < count; i++)
        {
            var left = i < Words.Count ? Words[i] : 0;
            var right = i < other.Words.Count ? other.Words[i] : 0;
            distance += BitOperations.PopCount(left ^ right);
        }

        return distance;
    }

    private static ulong[] BuildAverageHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size);
        var total = 0.0;
        foreach (var value in pixels)
        {
            total += value;
        }

        var average = total / LegacyBitCount;
        var words = new ulong[4];
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] < average)
            {
                continue;
            }

            words[i / 64] |= 1UL << (i % 64);
        }

        return words;
    }

    private static ulong[] BuildHorizontalDifferenceHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size + 1, Size);
        var words = new ulong[4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var bitIndex = y * Size + x;
                var left = pixels[y * (Size + 1) + x];
                var right = pixels[y * (Size + 1) + x + 1];
                if (left >= right)
                {
                    words[bitIndex / 64] |= 1UL << (bitIndex % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] BuildVerticalDifferenceHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size + 1);
        var words = new ulong[4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var bitIndex = y * Size + x;
                var top = pixels[y * Size + x];
                var bottom = pixels[(y + 1) * Size + x];
                if (top >= bottom)
                {
                    words[bitIndex / 64] |= 1UL << (bitIndex % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] BuildEdgeDensityHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size);
        var words = new ulong[4];
        for (var y = 1; y < Size - 1; y++)
        {
            for (var x = 1; x < Size - 1; x++)
            {
                var idx = y * Size + x;
                var left = pixels[idx - 1];
                var right = pixels[idx + 1];
                var up = pixels[idx - Size];
                var down = pixels[idx + Size];
                var gradient = Math.Abs(left - right) + Math.Abs(up - down);
                if (gradient >= 36)
                {
                    words[idx / 64] |= 1UL << (idx % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] EmptyWordsForFeature(string featureName)
    {
        if (string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[16];
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[12];
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.LegacyFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[4];
        }

        return new ulong[8];
    }

    private static double[] RenderLuma(Bitmap source, Rectangle rect, int width, int height)
    {
        using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), rect, GraphicsUnit.Pixel);
        }

        var pixels = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = resized.GetPixel(x, y);
                pixels[y * width + x] = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
            }
        }

        return pixels;
    }

    private static Rectangle? TryCanonicalize(Bitmap source, Rectangle rect, out Rectangle canonicalRect, out bool succeeded, out bool fallback)
    {
        canonicalRect = rect;
        succeeded = false;
        fallback = false;

        var insetX = Math.Max(1, (int)Math.Round(rect.Width * 0.08, MidpointRounding.AwayFromZero));
        var insetYTop = Math.Max(1, (int)Math.Round(rect.Height * 0.18, MidpointRounding.AwayFromZero));
        var insetYBottom = Math.Max(1, (int)Math.Round(rect.Height * 0.18, MidpointRounding.AwayFromZero));
        var cropped = Rectangle.FromLTRB(rect.Left + insetX, rect.Top + insetYTop, rect.Right - insetX, rect.Bottom - insetYBottom);
        if (cropped.Width < 4 || cropped.Height < 4)
        {
            fallback = true;
            return null;
        }

        const int sampleWidth = 48;
        const int sampleHeight = 24;
        var luminance = RenderLuma(source, cropped, sampleWidth, sampleHeight);
        var total = luminance.Length;
        if (total <= 0)
        {
            fallback = true;
            return null;
        }

        var darkPixels = luminance.Count(value => value < 210);
        var darkRate = darkPixels / (double)total;
        if (darkRate < 0.02)
        {
            fallback = true;
            return null;
        }

        var threshold = Math.Min(220, Math.Max(90, luminance.Average() - 12));
        var minX = sampleWidth;
        var minY = sampleHeight;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < sampleHeight; y++)
        {
            for (var x = 0; x < sampleWidth; x++)
            {
                var idx = y * sampleWidth + x;
                var center = luminance[idx];
                var left = x > 0 ? luminance[idx - 1] : center;
                var right = x + 1 < sampleWidth ? luminance[idx + 1] : center;
                var up = y > 0 ? luminance[idx - sampleWidth] : center;
                var down = y + 1 < sampleHeight ? luminance[idx + sampleWidth] : center;
                var gradient = Math.Abs(left - right) + Math.Abs(up - down);
                if (center <= threshold || gradient >= 42)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            fallback = true;
            return null;
        }

        var leftPx = cropped.Left + (int)Math.Floor(minX * cropped.Width / (double)sampleWidth);
        var topPx = cropped.Top + (int)Math.Floor(minY * cropped.Height / (double)sampleHeight);
        var rightPx = cropped.Left + (int)Math.Ceiling((maxX + 1) * cropped.Width / (double)sampleWidth);
        var bottomPx = cropped.Top + (int)Math.Ceiling((maxY + 1) * cropped.Height / (double)sampleHeight);
        var textBounds = Rectangle.FromLTRB(leftPx, topPx, rightPx, bottomPx);
        var padX = Math.Max(1, (int)Math.Round(textBounds.Width * 0.12, MidpointRounding.AwayFromZero));
        var padY = Math.Max(1, (int)Math.Round(textBounds.Height * 0.20, MidpointRounding.AwayFromZero));
        canonicalRect = Rectangle.Intersect(cropped, Rectangle.FromLTRB(
            textBounds.Left - padX,
            textBounds.Top - padY,
            textBounds.Right + padX,
            textBounds.Bottom + padY));
        if (canonicalRect.Width < 4 || canonicalRect.Height < 4)
        {
            fallback = true;
            canonicalRect = cropped;
            return null;
        }

        succeeded = true;
        return canonicalRect;
    }

    private static ulong Parse(string word)
    {
        return ulong.TryParse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
