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

internal interface IWindowCaptureSource : IDisposable
{
    string Name { get; }
    string FrameBackendName { get; }
    CapturedFrame CaptureFrame(Rectangle screenRect);
    Bitmap Capture(Rectangle screenRect);
    Color GetPixel(Point point);
}

internal readonly record struct DxgiFrameBackendPolicy(bool UseRawFrames, string Name)
{
    public static DxgiFrameBackendPolicy Resolve(string? configuredValue)
    {
        if (string.Equals(configuredValue, "0", StringComparison.OrdinalIgnoreCase))
        {
            return new DxgiFrameBackendPolicy(false, "forced-bitmap");
        }

        if (string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase))
        {
            return new DxgiFrameBackendPolicy(true, "forced-raw");
        }

        return new DxgiFrameBackendPolicy(true, "default-raw");
    }

    public static DxgiFrameBackendPolicy Current =>
        Resolve(Environment.GetEnvironmentVariable("ZZZ_SCANNER_DXGI_RAW"));
}

internal sealed class CaptureSourceCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<IWindowCaptureSource> _fallbackFactory;
    private IWindowCaptureSource _source;
    private Action<string>? _log;
    private int _foregroundWaiters;
    private bool _disposed;

    public CaptureSourceCoordinator(
        IWindowCaptureSource source,
        Func<IWindowCaptureSource>? fallbackFactory = null)
    {
        _source = source;
        _fallbackFactory = fallbackFactory ?? (static () => new GdiCaptureSource());
    }

    public string Name
    {
        get
        {
            lock (_sync)
            {
                return _source.Name;
            }
        }
    }

    public string FrameBackendName
    {
        get
        {
            lock (_sync)
            {
                return _source.FrameBackendName;
            }
        }
    }

    public void ConfigureLog(Action<string>? log)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _log = log;
        }
    }

    public void Replace(IWindowCaptureSource source)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ReplaceCore(source);
        }
    }

    public Bitmap Capture(Rectangle screenRect) =>
        Execute("Capture", source => source.Capture(screenRect));

    public CapturedFrame CaptureFrame(Rectangle screenRect) =>
        Execute("CaptureFrame", source => source.CaptureFrame(screenRect));

    public Color GetPixel(Point point) =>
        Execute("GetPixel", source => source.GetPixel(point));

    public bool TryCapture(Rectangle screenRect, out Bitmap? image)
    {
        image = null;
        if (Volatile.Read(ref _foregroundWaiters) != 0)
        {
            return false;
        }

        if (!Monitor.TryEnter(_sync))
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref _foregroundWaiters) != 0)
            {
                return false;
            }

            image = ExecuteCore("Capture", source => source.Capture(screenRect));
            return true;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source.Dispose();
        }
    }

    private T Execute<T>(string operation, Func<IWindowCaptureSource, T> action)
    {
        Interlocked.Increment(ref _foregroundWaiters);
        Monitor.Enter(_sync);
        Interlocked.Decrement(ref _foregroundWaiters);
        try
        {
            return ExecuteCore(operation, action);
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private T ExecuteCore<T>(string operation, Func<IWindowCaptureSource, T> action)
    {
        ThrowIfDisposed();
        try
        {
            return action(_source);
        }
        catch (Exception ex) when (_source is not GdiCaptureSource)
        {
            var activeName = _source.Name;
            ReplaceCore(_fallbackFactory());
            var frameDetail = operation == "CaptureFrame"
                ? $", captureFrameBackend={_source.FrameBackendName}"
                : string.Empty;
            _log?.Invoke($"Capture backend fallback during {operation}: active={activeName}, fallback={_source.Name}{frameDetail}, reason={ex.GetType().Name}: {ex.Message}");
            return action(_source);
        }
    }

    private void ReplaceCore(IWindowCaptureSource source)
    {
        var previous = _source;
        _source = source;
        previous.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal abstract class CapturedFrame : IDisposable
{
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract string BackendName { get; }
    public Size Size => new(Width, Height);
    public abstract Color GetPixel(int x, int y);
    public abstract Bitmap ToBitmap();
    public abstract void Dispose();
}

internal sealed class GdiCaptureSource : IWindowCaptureSource
{
    public string Name => "gdi";
    public string FrameBackendName => "bitmap-fallback";

    public CapturedFrame CaptureFrame(Rectangle screenRect)
    {
        return new BitmapCapturedFrame(Capture(screenRect), FrameBackendName);
    }

    public Bitmap Capture(Rectangle screenRect)
    {
        var image = new Bitmap(screenRect.Width, screenRect.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(screenRect.Location, Point.Empty, screenRect.Size);
        return image;
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
    }
}
