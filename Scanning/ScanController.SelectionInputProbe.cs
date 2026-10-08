using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    // Local diagnostics only: preserve the current scroll position and use the
    // same fork, warehouse guard and selection probe as the production scan.
    public async Task<string> ProbeSelectionInputAsync(ScanOptions options, int row, int column, CancellationToken token)
    {
        var profile = _profiles.ResolveRequired(options.ProfileName);
        if (row < 1 || row > profile.VisibleRows || column < 2 || column > profile.VisibleColumns)
            throw new ArgumentOutOfRangeException(nameof(column));
        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        using var window = GameWindow.Find(options.ProcessName);
        if (options.BringToFront) window.BringToFront();
        window.ConfigureCaptureMode(options.CaptureMode, scanLog.Write);
        using var recognizer = CreateOcrRecognizer(options, outputDir, "selection-inventory", 1);
        var progress = new Progress<ScanProgress>(_ => { });
        var preflight = await PrepareBackpackAsync(window, profile, recognizer, "selection-input-probe", progress, new Counters(), scanLog, token);
        var warehousePolicy = (profile.VisualProbes ?? new VisualProbeOptions()).WarehousePreflight ?? new WarehousePreflightPolicy();
        var guard = new WarehouseContextGuard(window, preflight.MonitorPlan, warehousePolicy, recognizer, scanLog);
        window.ConfigureInputGuard(guard.EnsureHealthy);
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        Point Cell(int col) => DriveDiscSelectionGeometry.Center(window.ToScreenPoint(new PointF(offset.X + step.X * col, offset.Y + step.Y * row)), window.ClientScreenRect, profile);
        var target = Cell(column);
        var neighbor = Cell(column - 1);
        var targetRect = SelectionProbeRect(window, target, profile);
        var neighborRect = SelectionProbeRect(window, neighbor, profile);
        using (var viewport = window.Capture(window.ClientScreenRect))
            viewport.Save(Path.Combine(outputDir, "initial-viewport.png"), ImageFormat.Png);
        var observations = new List<object>();
        foreach (var pulse in new[] { 0, 16, 32 })
        {
            token.ThrowIfCancellationRequested();
            window.LeftClick(neighbor, 32);
            await Task.Delay(180, token);
            window.MoveCursor(target);
            await Task.Delay(25, token);
            var beforeTarget = CaptureSelectionSignature(targetRect);
            var beforeNeighbor = CaptureSelectionSignature(neighborRect);
            using (var before = window.Capture(targetRect))
                before.Save(Path.Combine(outputDir, $"pulse-{pulse}-target-before.png"), ImageFormat.Png);
            using (var before = window.Capture(neighborRect))
                before.Save(Path.Combine(outputDir, $"pulse-{pulse}-neighbor-before.png"), ImageFormat.Png);
            var watch = Stopwatch.StartNew();
            window.LeftClick(target, pulse);
            ImageSignature? previousTarget = null;
            for (var sample = 1; sample <= 10; sample++)
            {
                await Task.Delay(25, token);
                var currentTarget = CaptureSelectionSignature(targetRect);
                var currentNeighbor = CaptureSelectionSignature(neighborRect);
                var targetDistance = SignatureDistance(beforeTarget, currentTarget);
                var neighborDistance = SignatureDistance(beforeNeighbor, currentNeighbor);
                var stableDistance = previousTarget is null ? (int?)null : SignatureDistance(previousTarget.Value, currentTarget);
                previousTarget = currentTarget;
                observations.Add(new { pulseMs = pulse, sample, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1), targetDistance, neighborDistance, stableDistance });
                if (sample is 1 or 3 or 10)
                {
                    using var targetImage = window.Capture(targetRect);
                    targetImage.Save(Path.Combine(outputDir, $"pulse-{pulse}-target-{sample}.png"), ImageFormat.Png);
                    using var neighborImage = window.Capture(neighborRect);
                    neighborImage.Save(Path.Combine(outputDir, $"pulse-{pulse}-neighbor-{sample}.png"), ImageFormat.Png);
                }
            }
        }
        var result = Path.Combine(outputDir, "selection-input-probe-result.json");
        await File.WriteAllTextAsync(result, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, inventoryCount = preflight.InventoryCount, row, column, client = window.ClientScreenRect,
            target, neighbor, targetRect, neighborRect, changeTolerance = PanelChangeTolerance,
            accountWriteEnabled = false, observations
        }, JsonDefaults.Write), token);
        return result;
    }
}
