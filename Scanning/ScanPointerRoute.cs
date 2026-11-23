using System.Drawing;

namespace ZZZScannerNext.Scanning;

internal static class ScanPointerRoute
{
    internal static Point ReleasePoint(Rectangle client, Rectangle grid, Point? lastPointer,
        params Rectangle[] observationRegions)
    {
        var padding = Math.Max(8, client.Width / 80);
        var y = Math.Clamp(lastPointer?.Y ?? grid.Top + grid.Height / 2,
            Math.Max(client.Top + 1, grid.Top + 1), Math.Min(client.Bottom - 2, grid.Bottom - 2));
        var right = new Point(Math.Min(grid.Right + padding, client.Right - 2), y);
        if (right.X > grid.Right && IsClear(right)) return right;
        var left = new Point(Math.Max(grid.Left - padding, client.Left + 1), y);
        if (left.X < grid.Left && IsClear(left)) return left;
        throw new InvalidDataException("list_hover_release_point_unavailable");

        // Releasing list hover must not create hover evidence in the detail/OCR panel.
        bool IsClear(Point point) => client.Contains(point)
            && !observationRegions.Any(region => region.Contains(point));
    }

    internal static Point WheelPoint(Rectangle grid, int columns, Point released, Point? current = null)
    {
        // Reuse the last selected card: a wheel gesture does not need a new target.
        if (current is Point existing && grid.Contains(existing)) return existing;
        // Stay over the adjacent outer card, then release just outside the grid.
        // Never cross the centre of the list merely to deliver a wheel detent.
        var inset = Math.Max(8, grid.Width / (2 * Math.Max(1, columns)));
        var x = released.X >= grid.Right ? grid.Right - inset : grid.Left + inset;
        return new Point(Math.Clamp(x, grid.Left + 1, grid.Right - 2),
            Math.Clamp(released.Y, grid.Top + 1, grid.Bottom - 2));
    }
}
