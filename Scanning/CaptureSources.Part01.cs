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

internal sealed class DxgiDesktopCaptureSource : IWindowCaptureSource
{
    private readonly IDXGIFactory1 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput _output;
    private readonly IDXGIOutput1 _output1;
    private IDXGIOutputDuplication _duplication;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly Rectangle _desktopBounds;
    private ID3D11Texture2D? _stagingTexture;
    private Size _stagingSize;
    private bool _needsDuplicationReset;
    private bool _disposed;

    private DxgiDesktopCaptureSource(
        IDXGIFactory1 factory,
        IDXGIAdapter1 adapter,
        IDXGIOutput output,
        IDXGIOutput1 output1,
        IDXGIOutputDuplication duplication,
        ID3D11Device device,
        ID3D11DeviceContext context,
        Rectangle desktopBounds)
    {
        _factory = factory;
        _adapter = adapter;
        _output = output;
        _output1 = output1;
        _duplication = duplication;
        _device = device;
        _context = context;
        _desktopBounds = desktopBounds;
    }

    public string Name => "dxgi";
    public string FrameBackendName => FramePolicy.UseRawFrames ? "dxgi-raw" : "bitmap-fallback";

    private static DxgiFrameBackendPolicy FramePolicy => DxgiFrameBackendPolicy.Current;

    public static DxgiDesktopCaptureSource Create(Rectangle clientScreenRect)
    {
        var factory = CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? selectedAdapter = null;
        IDXGIOutput? selectedOutput = null;
        Rectangle selectedBounds = Rectangle.Empty;
        var selectedArea = 0;

        for (var adapterIndex = 0u; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            try
            {
                for (var outputIndex = 0u; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    var desc = output.Description;
                    var bounds = Rectangle.FromLTRB(
                        desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right,
                        desc.DesktopCoordinates.Bottom);
                    var area = IntersectionArea(bounds, clientScreenRect);
                    if (area <= selectedArea)
                    {
                        output.Dispose();
                        continue;
                    }

                    selectedOutput?.Dispose();
                    if (selectedAdapter is not null && !ReferenceEquals(selectedAdapter, adapter))
                    {
                        selectedAdapter.Dispose();
                    }

                    selectedAdapter = adapter;
                    selectedOutput = output;
                    selectedBounds = bounds;
                    selectedArea = area;
                }
            }
            finally
            {
                if (!ReferenceEquals(adapter, selectedAdapter))
                {
                    adapter?.Dispose();
                }
            }
        }

        if (selectedAdapter is null || selectedOutput is null || selectedArea <= 0)
        {
            selectedAdapter?.Dispose();
            selectedOutput?.Dispose();
            factory.Dispose();
            throw new InvalidOperationException($"DXGI 初始化失败：找不到覆盖游戏客户区 {clientScreenRect} 的显示器。");
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutput1? output1 = null;
        IDXGIOutputDuplication? duplication = null;
        try
        {
            var result = D3D11CreateDevice(
                selectedAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out device,
                out _,
                out context);
            result.CheckError();
            output1 = selectedOutput.QueryInterface<IDXGIOutput1>();
            duplication = output1.DuplicateOutput(device);
            return new DxgiDesktopCaptureSource(factory, selectedAdapter, selectedOutput, output1, duplication, device, context, selectedBounds);
        }
        catch
        {
            duplication?.Dispose();
            output1?.Dispose();
            context?.Dispose();
            device?.Dispose();
            selectedOutput.Dispose();
            selectedAdapter.Dispose();
            factory.Dispose();
            throw;
        }
    }

    public Bitmap Capture(Rectangle screenRect)
    {
        return CaptureBitmap(screenRect);
    }

    public CapturedFrame CaptureFrame(Rectangle screenRect)
    {
        return FramePolicy.UseRawFrames
            ? CaptureRawFrame(screenRect)
            : new BitmapCapturedFrame(CaptureBitmap(screenRect), FrameBackendName);
    }

    private Bitmap CaptureBitmap(Rectangle screenRect)
    {
        return CaptureFromDesktop(screenRect, CopyTextureToBitmap);
    }

    private CapturedFrame CaptureRawFrame(Rectangle screenRect)
    {
        return CaptureFromDesktop(screenRect, CopyTextureToFrame);
    }

    private T CaptureFromDesktop<T>(Rectangle screenRect, Func<ID3D11Texture2D, Size, T> copy)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DxgiDesktopCaptureSource));
        }

        var localRect = Rectangle.FromLTRB(
            screenRect.Left - _desktopBounds.Left,
            screenRect.Top - _desktopBounds.Top,
            screenRect.Right - _desktopBounds.Left,
            screenRect.Bottom - _desktopBounds.Top);
        if (localRect.Left < 0 || localRect.Top < 0 || localRect.Right > _desktopBounds.Width || localRect.Bottom > _desktopBounds.Height)
        {
            throw new InvalidOperationException($"DXGI 截图范围 {screenRect} 超出当前显示器范围 {_desktopBounds}。");
        }

        IDXGIResource? resource = null;
        var frameAcquired = false;
        try
        {
            if (_needsDuplicationReset)
            {
                ResetDuplication();
            }

            var result = _duplication.AcquireNextFrame(80, out _, out resource);
            if (result == Vortice.DXGI.ResultCode.InvalidCall)
            {
                ResetDuplication();
                result = _duplication.AcquireNextFrame(80, out _, out resource);
            }

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                throw new TimeoutException("DXGI AcquireNextFrame 超时。");
            }

            result.CheckError();
            if (resource is null)
            {
                throw new InvalidOperationException("DXGI AcquireNextFrame 未返回桌面资源。");
            }

            frameAcquired = true;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var staging = EnsureStagingTexture(screenRect.Size);
            var sourceBox = new Vortice.Mathematics.Box(localRect.Left, localRect.Top, 0, localRect.Right, localRect.Bottom, 1);
            _context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, sourceBox);
            return copy(staging, screenRect.Size);
        }
        catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            throw new TimeoutException("DXGI AcquireNextFrame 超时。", ex);
        }
        finally
        {
            if (frameAcquired)
            {
                try
                {
                    _duplication.ReleaseFrame().CheckError();
                }
                catch
                {
                    // A release failure should not invalidate an already copied frame.
                    // The next capture rebuilds the duplication session before acquiring.
                    _needsDuplicationReset = true;
                }
            }

            resource?.Dispose();
        }
    }

    public Color GetPixel(Point point)
    {
        using var image = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(point, Point.Empty, image.Size);
        return image.GetPixel(0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SafeDispose(_stagingTexture);
        SafeDispose(_duplication);
        SafeDispose(_output1);
        SafeDispose(_output);
        SafeDispose(_context);
        SafeDispose(_device);
        SafeDispose(_adapter);
        SafeDispose(_factory);
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
        }
    }

    private void ResetDuplication()
    {
        SafeDispose(_duplication);

        _duplication = _output1.DuplicateOutput(_device);
        _needsDuplicationReset = false;
    }

    private ID3D11Texture2D EnsureStagingTexture(Size size)
    {
        if (_stagingTexture is not null && _stagingSize == size)
        {
            return _stagingTexture;
        }

        _stagingTexture?.Dispose();
        _stagingSize = size;
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
        return _stagingTexture;
    }

    private CapturedFrame CopyTextureToFrame(ID3D11Texture2D texture, Size size)
    {
        var mapped = _context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = checked(size.Width * 4);
            var byteLength = checked(rowBytes * size.Height);
            var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
            for (var y = 0; y < size.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked((int)(y * mapped.RowPitch))), buffer, y * rowBytes, rowBytes);
            }

            return new BgraCapturedFrame(size.Width, size.Height, rowBytes, buffer, byteLength, FrameBackendName);
        }
        finally
        {
            try
            {
                _context.Unmap(texture, 0);
            }
            catch
            {
                // Cleanup failures are handled by the next capture attempt or GDI fallback.
            }
        }
    }

    private Bitmap CopyTextureToBitmap(ID3D11Texture2D texture, Size size)
    {
        var mapped = _context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            var bounds = new Rectangle(0, 0, size.Width, size.Height);
            var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = size.Width * 4;
                var buffer = new byte[rowBytes];
                for (var y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked((int)(y * mapped.RowPitch))), buffer, 0, rowBytes);
                    Marshal.Copy(buffer, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
        finally
        {
            try
            {
                _context.Unmap(texture, 0);
            }
            catch
            {
                // Cleanup failures are handled by the next capture attempt or GDI fallback.
            }
        }
    }

    private static int IntersectionArea(Rectangle a, Rectangle b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

}

internal sealed class BitmapCapturedFrame : CapturedFrame
{
    private readonly string _backendName;
    private Bitmap? _bitmap;
    private byte[]? _pixels;
    private readonly int _width;
    private readonly int _height;
    private bool _bitmapTaken;

    public BitmapCapturedFrame(Bitmap bitmap, string backendName)
    {
        _bitmap = bitmap;
        _backendName = backendName;
        _width = bitmap.Width;
        _height = bitmap.Height;

        // Captures use RGB/ARGB storage. Keep other formats on GDI+'s exact
        // GetPixel path, especially premultiplied alpha with rounding rules.
        if (bitmap.PixelFormat is not (PixelFormat.Format24bppRgb or
            PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb))
        {
            return;
        }

        var rowBytes = checked(_width * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * _height));
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, _width, _height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < _height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)),
                        pixels, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            _pixels = pixels;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }

    public override int Width => _bitmap?.Width ?? 0;
    public override int Height => _bitmap?.Height ?? 0;
    public override string BackendName => _backendName;

    public override Color GetPixel(int x, int y)
    {
        var bitmap = _bitmap ?? throw new ObjectDisposedException(nameof(BitmapCapturedFrame));
        // Once handed out, the original bitmap can be edited by its owner.
        // Invalid coordinates also use GDI+ to preserve its exception contract.
        if (_bitmapTaken || _pixels is null || (uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            return bitmap.GetPixel(x, y);
        }

        var offset = ((y * _width) + x) * 4;
        var pixels = _pixels;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    public override Bitmap ToBitmap()
    {
        var bitmap = _bitmap ?? throw new ObjectDisposedException(nameof(BitmapCapturedFrame));
        _bitmapTaken = true;
        return bitmap;
    }

    public override void Dispose()
    {
        if (!_bitmapTaken)
        {
            _bitmap?.Dispose();
        }

        _bitmap = null;
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
