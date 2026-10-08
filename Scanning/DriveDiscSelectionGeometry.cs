using System.Drawing;

namespace ZZZScannerNext.Scanning;

internal static class DriveDiscSelectionGeometry
{
    // The profile grid point samples the rarity strip near the lower right.
    // Selection input must target the card interior, without moving that probe.
    internal static Point Center(Point rarityAnchor, Rectangle client, ScanProfile profile)
    {
        if (!profile.Points.TryGetValue("driveDiscSelectionOffset", out var offset) || offset.Length != 2)
            return rarityAnchor;
        return new Point(
            rarityAnchor.X + (int)Math.Round(offset[0] * client.Width / (double)profile.StandardScreen[0]),
            rarityAnchor.Y + (int)Math.Round(offset[1] * client.Height / (double)profile.StandardScreen[1]));
    }

    internal static Rectangle Probe(Point center, Rectangle client, ScanProfile? profile)
    {
        var standardWidth = profile?.StandardScreen[0] ?? client.Width;
        var standardHeight = profile?.StandardScreen[1] ?? client.Height;
        var halfWidth = Math.Max(1, (int)Math.Round(72d * client.Width / standardWidth));
        var halfHeight = Math.Max(1, (int)Math.Round(84d * client.Height / standardHeight));
        var left = Math.Clamp(center.X - halfWidth, client.Left, Math.Max(client.Left, client.Right - 1));
        var top = Math.Clamp(center.Y - halfHeight, client.Top, Math.Max(client.Top, client.Bottom - 1));
        var right = Math.Clamp(center.X + halfWidth, left + 1, client.Right);
        var bottom = Math.Clamp(center.Y + halfHeight, top + 1, client.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal static int WitnessColumn(int target, int columns)
    {
        // Do not re-click the immediately preceding (possibly still selected)
        // card when recovering an ignored target click.
        if (target > 2) return target - 2;
        if (columns >= 3) return 3;
        return target == 1 && columns >= 2 ? 2 : target == 2 ? 1 : 0;
    }
}
