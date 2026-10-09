using System.Drawing;

namespace ZZZScannerNext.Scanning;

internal static class PreselectedCardSelectionProbe
{
    // Coordinates in the existing 144x168 card probe. Exclude the rank strip,
    // slot badge, icon and adjacent card: they can be yellow without selection.
    private static readonly Rectangle[] BorderBands =
    [
        Rectangle.FromLTRB(10, 45, 22, 112),
        Rectangle.FromLTRB(128, 45, 140, 112),
        Rectangle.FromLTRB(40, 17, 114, 32)
    ];

    internal static bool IsSelected(CapturedFrame frame)
    {
        if (frame.Width < 48 || frame.Height < 56) return false;
        var stride = Math.Max(1, (int)Math.Round(frame.Height / 168d));
        foreach (var reference in BorderBands)
        {
            var band = Rectangle.FromLTRB(
                (int)Math.Round(reference.Left * frame.Width / 144d),
                (int)Math.Round(reference.Top * frame.Height / 168d),
                (int)Math.Round(reference.Right * frame.Width / 144d),
                (int)Math.Round(reference.Bottom * frame.Height / 168d));
            var selected = 0;
            var samples = 0;
            for (var y = band.Top; y < band.Bottom; y += stride)
            for (var x = band.Left; x < band.Right; x += stride)
            {
                var color = frame.GetPixel(x, y);
                samples++;
                // Relative color survives the outline's brightness pulse. A
                // dark/neutral border is not evidence, even when it is stable.
                if (color.R >= 32 && color.G >= 32
                    && color.B * 100 <= Math.Min(color.R, color.G) * 55
                    && color.R * 10 >= color.G * 4 && color.G * 2 >= color.R)
                    selected++;
            }
            if (samples == 0 || selected * 1000 < samples * 150) return false;
        }
        return true;
    }
}
