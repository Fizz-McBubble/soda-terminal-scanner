using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZZZScannerNext.Scanning;

internal readonly record struct NativeEdgePhase(int Width, int Height, byte[] Colors);
internal readonly record struct NativeEdgePhaseDifference(int ChangedPermille, int MaximumDelta, bool Stable);

// Dense stability evidence is independent of row/item identity. Every pixel
// contributes to a 4x4 RGB tile; small animated texture noise is not scrolling.
// Only three bounded tile arrays are live, never a history of captured images.
internal static class NativeEdgePhaseProbe
{
    private const int TileSize = 4;
    private const int NoiseDelta = 8;
    private const int MaximumChangedPermille = 5;

    public static NativeEdgePhase Capture(Bitmap image, Rectangle rectangle)
    {
        var width = (rectangle.Width + TileSize - 1) / TileSize;
        var height = (rectangle.Height + TileSize - 1) / TileSize;
        var colors = new byte[checked(width * height * 3)];
        var data = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var sums = ArrayPool<int>.Shared.Rent(colors.Length);
        Array.Clear(sums, 0, colors.Length);
        var rowBytes = checked(rectangle.Width * 3);
        var buffer = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (var y = 0; y < rectangle.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, 0, rowBytes);
                for (var x = 0; x < rectangle.Width; x++)
                {
                    var tile = ((y / TileSize) * width + x / TileSize) * 3;
                    var pixel = x * 3;
                    sums[tile] += buffer[pixel];
                    sums[tile + 1] += buffer[pixel + 1];
                    sums[tile + 2] += buffer[pixel + 2];
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var count = Math.Min(TileSize, rectangle.Width - x * TileSize)
                        * Math.Min(TileSize, rectangle.Height - y * TileSize);
                    var tile = (y * width + x) * 3;
                    for (var channel = 0; channel < 3; channel++)
                        colors[tile + channel] = (byte)(sums[tile + channel] / count);
                }
            }
            return new NativeEdgePhase(width, height, colors);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<int>.Shared.Return(sums);
            image.UnlockBits(data);
        }
    }

    public static NativeEdgePhaseDifference Compare(NativeEdgePhase left, NativeEdgePhase right)
    {
        if (left.Width != right.Width || left.Height != right.Height
            || left.Colors.Length == 0 || left.Colors.Length != right.Colors.Length)
            return new NativeEdgePhaseDifference(1000, 255, false);
        var changed = 0;
        var maximum = 0;
        for (var index = 0; index < left.Colors.Length; index++)
        {
            var delta = Math.Abs(left.Colors[index] - right.Colors[index]);
            maximum = Math.Max(maximum, delta);
            if (delta > NoiseDelta) changed++;
        }
        var permille = (int)((long)changed * 1000 / left.Colors.Length);
        return new NativeEdgePhaseDifference(permille, maximum,
            (long)changed * 1000 <= (long)MaximumChangedPermille * left.Colors.Length);
    }
}
