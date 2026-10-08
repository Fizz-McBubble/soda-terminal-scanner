using System.Buffers;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ZZZScannerNext.Scanning;

internal sealed class BgraCapturedFrame : CapturedFrame
{
    private byte[]? _buffer;
    private readonly int _length;
    private readonly string _backendName;

    public BgraCapturedFrame(int width, int height, int stride, byte[] buffer, int length, string backendName)
    {
        Width = width;
        Height = height;
        Stride = stride;
        _buffer = buffer;
        _length = length;
        _backendName = backendName;
    }

    public override int Width { get; }
    public override int Height { get; }
    public int Stride { get; }
    public override string BackendName => _backendName;

    public override Color GetPixel(int x, int y)
    {
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BgraCapturedFrame));
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var offset = (y * Stride) + (x * 4);
        return Color.FromArgb(buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset]);
    }

    public override Bitmap ToBitmap()
    {
        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BgraCapturedFrame));
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, Width, Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = Width * 4;
            for (var y = 0; y < Height; y++)
            {
                Marshal.Copy(buffer, y * Stride, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    public override void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
