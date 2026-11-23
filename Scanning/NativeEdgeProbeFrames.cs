using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

// Used only by the explicit edge-scroll CLI diagnostic. Keep bounded copies of
// the exact frames used by the verifier; encode PNGs after the timed operation.
internal sealed class NativeEdgeProbeFrames(string outputDirectory, string prefix, Rectangle[] rowRects,
    string source = "same_capture_used_by_native_edge_verifier") : IDisposable
{
    private sealed record Snapshot(Bitmap Image, Rectangle Bounds, int Sample, double ElapsedMilliseconds);
    private readonly Dictionary<string, Snapshot> frames = new();
    private readonly Stopwatch watch = new();
    private int observedSamples;
    private int releaseSamples;

    public void CaptureBefore(Bitmap image, Rectangle bounds)
    {
        watch.Restart();
        Store("before", image, bounds, 0);
    }

    public void CaptureObserved(Bitmap image, Rectangle bounds)
    {
        observedSamples++;
        if (observedSamples <= 2) Store($"after-{observedSamples:000}", image, bounds, observedSamples);
        Store("after-last", image, bounds, observedSamples);
    }

    public void CaptureReleased(Bitmap image, Rectangle bounds)
    {
        releaseSamples++;
        if (releaseSamples == 1) Store("release-first", image, bounds, releaseSamples);
        Store("release-last", image, bounds, releaseSamples);
    }

    private void Store(string name, Bitmap image, Rectangle bounds, int sample)
    {
        var snapshot = new Snapshot((Bitmap)image.Clone(), bounds, sample, watch.Elapsed.TotalMilliseconds);
        if (frames.Remove(name, out var old)) old.Image.Dispose();
        frames[name] = snapshot;
    }

    public string Save()
    {
        var entries = new List<object>();
        foreach (var (name, snapshot) in frames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var filename = $"{prefix}-{name}.png";
            var path = Path.Combine(outputDirectory, filename);
            snapshot.Image.Save(path, ImageFormat.Png);
            using var input = File.OpenRead(path);
            entries.Add(new
            {
                filename, snapshot.Sample, elapsedMilliseconds = Math.Round(snapshot.ElapsedMilliseconds, 2),
                captureBounds = snapshot.Bounds, width = snapshot.Image.Width, height = snapshot.Image.Height,
                sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()
            });
        }
        var manifestPath = Path.Combine(outputDirectory, $"{prefix}-frames.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, source,
            coordinateSpace = "screen", rowSignatureRects = rowRects,
            observedSamples, releaseSamples, frames = entries
        }, JsonDefaults.Write));
        return manifestPath;
    }

    public void Dispose()
    {
        foreach (var snapshot in frames.Values) snapshot.Image.Dispose();
        frames.Clear();
    }
}
