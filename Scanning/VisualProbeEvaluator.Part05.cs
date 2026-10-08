using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class VisualProbeEvaluator
{
    private static HsvColor ToHsv(Color color)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = 0f;
        if (delta > 0.0001f)
        {
            if (Math.Abs(max - r) < 0.0001f)
            {
                hue = 60f * (((g - b) / delta) % 6f);
            }
            else if (Math.Abs(max - g) < 0.0001f)
            {
                hue = 60f * (((b - r) / delta) + 2f);
            }
            else
            {
                hue = 60f * (((r - g) / delta) + 4f);
            }

            if (hue < 0f)
            {
                hue += 360f;
            }
        }

        return new HsvColor(hue, max <= 0.0001f ? 0 : delta / max, max);
    }

    private readonly record struct HsvColor(float Hue, float Saturation, float Value);

    private sealed class RarityVote
    {
        public int Count { get; private set; }
        public int BestScore { get; private set; } = int.MaxValue;
        public int BestMargin { get; private set; }
        public Color BestColor { get; private set; } = Color.Empty;

        public void Observe(int score, int margin, Color color)
        {
            Count++;
            if (score < BestScore || (score == BestScore && margin > BestMargin))
            {
                BestScore = score;
                BestMargin = margin;
                BestColor = color;
            }
        }
    }
}
