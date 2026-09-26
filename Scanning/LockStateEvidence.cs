using System.Drawing;

namespace ZZZScannerNext.Scanning;

/// <summary>
/// R10E's two independent visual lock-state witnesses. The card rule is the
/// proven R4/S4 bottom-right icon rule; the detail rule reads only the lock
/// button crop. Neither signal is allowed to decide the exported state alone.
/// </summary>
internal static class LockStateEvidence
{
    private const byte CardBrightThreshold = 180;
    private const double CardUnlockedMaximum = 0.15;
    private const double CardLockedMinimum = 0.30;
    private const byte DetailWhiteThreshold = 220;
    private const double DetailLockedMaximum = 0.02;
    private const double DetailUnlockedMinimum = 0.08;

    internal sealed record Sample(string Signal, bool? Value, double Ratio, string Gate);

    internal sealed record Combined(bool? Value, Sample Card, Sample Detail, string Gate)
    {
        public bool IsKnown => Value is not null;
    }

    public static Sample EvaluateCard(Bitmap card)
    {
        var crop = RelativeCrop(card, 0.76, 0.57, 0.22, 0.23);
        var ratio = PixelRatio(crop, color => Luminance(color) > CardBrightThreshold);
        return ratio < CardUnlockedMaximum
            ? new Sample("card-bottom-right", false, ratio, "unlocked")
            : ratio > CardLockedMinimum
                ? new Sample("card-bottom-right", true, ratio, "locked")
                : new Sample("card-bottom-right", null, ratio, "gray_zone");
    }

    public static Sample EvaluateDetailButton(Bitmap detailButton)
    {
        var ratio = PixelRatio(detailButton, color => color.R >= DetailWhiteThreshold
            && color.G >= DetailWhiteThreshold && color.B >= DetailWhiteThreshold);
        return ratio <= DetailLockedMaximum
            ? new Sample("detail-lock-button", true, ratio, "locked")
            : ratio >= DetailUnlockedMinimum
                ? new Sample("detail-lock-button", false, ratio, "unlocked")
                : new Sample("detail-lock-button", null, ratio, "gray_zone");
    }

    public static Combined Combine(Sample card, Sample detail)
    {
        if (card.Value is bool cardValue && detail.Value is bool detailValue && cardValue == detailValue)
        {
            return new Combined(cardValue, card, detail, "dual_evidence_agree");
        }

        return new Combined(null, card, detail,
            card.Value is null || detail.Value is null ? "evidence_unknown" : "evidence_conflict");
    }

    private static Bitmap RelativeCrop(Bitmap source, double left, double top, double width, double height)
    {
        var bounds = Rectangle.Intersect(new Rectangle(
            (int)Math.Floor(source.Width * left),
            (int)Math.Floor(source.Height * top),
            Math.Max(1, (int)Math.Ceiling(source.Width * width)),
            Math.Max(1, (int)Math.Ceiling(source.Height * height))),
            new Rectangle(Point.Empty, source.Size));
        if (bounds.Width == 0 || bounds.Height == 0)
        {
            throw new InvalidDataException("lock_card_roi_out_of_bounds");
        }

        return source.Clone(bounds, source.PixelFormat);
    }

    private static double PixelRatio(Bitmap source, Func<Color, bool> predicate)
    {
        var match = 0;
        var total = source.Width * source.Height;
        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    if (predicate(source.GetPixel(x, y)))
                    {
                        match++;
                    }
                }
            }
        }
        finally
        {
            source.Dispose();
        }

        return total == 0 ? 0 : match / (double)total;
    }

    private static double Luminance(Color color) => (color.R + color.G + color.B) / 3d;
}

internal sealed class LockCaptureEvidence : IDisposable
{
    public LockCaptureEvidence(Bitmap? cardImage, LockStateEvidence.Combined result)
    {
        CardImage = cardImage;
        Result = result;
    }

    public Bitmap? CardImage { get; private set; }
    public LockStateEvidence.Combined Result { get; }

    public Bitmap? TakeCardImage()
    {
        var result = CardImage;
        CardImage = null;
        return result;
    }

    public void Dispose()
    {
        CardImage?.Dispose();
        CardImage = null;
    }
}
