using System.Drawing;
using ZZZScannerNext.Ocr;
using CvRect = System.Drawing.Rectangle;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static WarehouseHeaderProbeResult ReadWarehouseHeader(
        Bitmap source,
        Rectangle headerRect,
        IOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog)
    {
        headerRect = ClampRectangle(headerRect, source.Size);
        if (headerRect.IsEmpty)
        {
            return new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        }

        try
        {
            using var header = source.Clone(headerRect, source.PixelFormat);
            var rawOcr = recognizer.Recognize(header, [new CvRect(0, 0, header.Width, header.Height)]);
            var raw = rawOcr.Count > 0
                ? WarehousePreflightEvaluator.EvaluateHeader(rawOcr[0].Text, rawOcr[0].Score, policy)
                : WarehousePreflightEvaluator.EvaluateHeader(string.Empty, 0, policy);
            if (raw.HeaderDetected && raw.InventoryCountDetected)
            {
                scanLog.Write($"Warehouse header OCR raw accepted. text='{SanitizeLogValue(raw.NormalizedText)}', confidence={raw.Confidence:F3}, headerScore={raw.HeaderScore}, inventoryCountDetected=True.");
                return raw;
            }

            using var normalizedImage = VisualProbeEvaluator.NormalizeLuminance(header);
            var normalizedOcr = recognizer.Recognize(normalizedImage, [new CvRect(0, 0, normalizedImage.Width, normalizedImage.Height)]);
            var normalized = normalizedOcr.Count > 0
                ? WarehousePreflightEvaluator.EvaluateHeader(normalizedOcr[0].Text, normalizedOcr[0].Score, policy, usedNormalizedImage: true)
                : WarehousePreflightEvaluator.EvaluateHeader(string.Empty, 0, policy, usedNormalizedImage: true);
            var selected = WarehousePreflightEvaluator.ChooseHeaderResult(raw, normalized);
            scanLog.Write($"Warehouse header OCR evaluated. rawText='{SanitizeLogValue(raw.NormalizedText)}', rawConfidence={raw.Confidence:F3}, normalizedText='{SanitizeLogValue(normalized.NormalizedText)}', normalizedConfidence={normalized.Confidence:F3}, selected={(selected.UsedNormalizedImage ? "normalized" : "raw")}, headerDetected={selected.HeaderDetected}, headerScore={selected.HeaderScore}, inventoryCountDetected={selected.InventoryCountDetected}.");
            return selected;
        }
        catch (Exception ex)
        {
            scanLog.Write($"Warehouse header OCR exception: {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
            return new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        }
    }

    private static async Task<InventoryCountConsensusResult> ReadInventoryCountConsensusAsync(
        GameWindow window,
        ScanProfile profile,
        IOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog,
        CancellationToken token)
    {
        var required = Math.Clamp(policy.CountConsensusFrames, 2, 3);
        var maximumAttempts = Math.Clamp(policy.CountMaximumAttempts, required, 5);
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(policy.PollMilliseconds, 100, 1000));
        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var votes = new Dictionary<(int Current, int Capacity), int>();
        var bestConsensus = 0;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var headerImage = window.Capture(headerRect);
            var result = ReadWarehouseHeader(
                headerImage,
                new Rectangle(Point.Empty, headerImage.Size),
                recognizer,
                policy,
                scanLog);
            if (result.HeaderDetected && result.InventoryCount is int current && result.InventoryCapacity is int capacity)
            {
                var key = (current, capacity);
                var count = votes.TryGetValue(key, out var existing) ? existing + 1 : 1;
                votes[key] = count;
                bestConsensus = Math.Max(bestConsensus, count);
                scanLog.WriteEvent("INVENTORY_COUNT_CONSENSUS", $"attempt={attempt}/{maximumAttempts}, accepted=True, countDetected=True, consensusFrames={count}/{required}, normalizedRetry={result.UsedNormalizedImage}, headerScore={result.HeaderScore}");
                if (count >= required)
                {
                    return new InventoryCountConsensusResult(current, capacity, count, attempt);
                }
            }
            else
            {
                scanLog.WriteEvent("INVENTORY_COUNT_CONSENSUS", $"attempt={attempt}/{maximumAttempts}, accepted=False, countDetected={result.InventoryCountDetected}, consensusFrames={bestConsensus}/{required}, normalizedRetry={result.UsedNormalizedImage}, headerScore={result.HeaderScore}");
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(poll, token);
            }
        }

        return new InventoryCountConsensusResult(null, null, bestConsensus, maximumAttempts);
    }

    private static WarehouseMonitorPlan CreateWarehouseMonitorPlan(GameWindow window, ScanProfile profile)
    {
        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, Math.Max(1, profile.VisibleRows), Math.Max(1, profile.VisibleColumns)));
        var detailRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var confirmationBounds = Rectangle.Intersect(
            window.ClientScreenRect,
            Rectangle.Union(Rectangle.Union(headerRect, listGridRect), detailRect));
        var driveDiscOffset = window.ToScreenPoint(profile.Point("driveDiscOffset"));
        var driveDiscStepNormalized = profile.Point("driveDiscStep");
        var driveDiscStep = window.ToClientSize(new SizeF(driveDiscStepNormalized.X, driveDiscStepNormalized.Y));
        using var headerImage = window.Capture(headerRect);
        var baseline = WarehousePreflightEvaluator.CreateMonitorSignature(
            headerImage,
            [new Rectangle(Point.Empty, headerImage.Size)]);
        return new WarehouseMonitorPlan(
            headerRect,
            baseline,
            confirmationBounds,
            ToLocalRectangle(headerRect, confirmationBounds, confirmationBounds.Size),
            ToLocalRectangle(listGridRect, confirmationBounds, confirmationBounds.Size),
            ToLocalRectangle(detailRect, confirmationBounds, confirmationBounds.Size),
            new Point(driveDiscOffset.X - confirmationBounds.Left, driveDiscOffset.Y - confirmationBounds.Top),
            driveDiscStep);
    }

    private static WarehouseFastProbe CaptureWarehouseFastProbe(
        GameWindow window,
        WarehouseMonitorPlan plan,
        WarehouseMonitorSignature baseline)
    {
        using var image = window.Capture(plan.FastScreenBounds);
        var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(image);
        var signature = health.Passed
            ? WarehousePreflightEvaluator.CreateMonitorSignature(
                image,
                [new Rectangle(Point.Empty, image.Size)])
            : new WarehouseMonitorSignature([]);
        var score = health.Passed
            ? WarehousePreflightEvaluator.CompareMonitorSignature(baseline, signature)
            : 0;
        return new WarehouseFastProbe(health.Passed, score, health, signature);
    }

    private static WarehouseStrongProbe CaptureWarehouseStrongProbe(
        GameWindow window,
        WarehouseMonitorPlan plan,
        IOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog)
    {
        using var image = window.Capture(plan.ConfirmationScreenBounds);
        var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(image);
        var header = health.Passed
            ? ReadWarehouseHeader(image, plan.ConfirmationHeaderRect, recognizer, policy, scanLog)
            : default;
        var structure = health.Passed
            ? WarehousePreflightEvaluator.EvaluateStructure(
                image,
                plan.ConfirmationListGridRect,
                plan.ConfirmationDetailPanelRect,
                plan.ConfirmationDriveDiscOffset,
                plan.DriveDiscStep)
            : default;
        var headerSignature = health.Passed
            ? WarehousePreflightEvaluator.CreateMonitorSignature(image, [plan.ConfirmationHeaderRect])
            : new WarehouseMonitorSignature([]);
        return new WarehouseStrongProbe(
            WarehousePreflightEvaluator.IsStrongConfirmationAccepted(health, header, structure, policy),
            health,
            header,
            structure,
            headerSignature);
    }

    private static Rectangle ToLocalRectangle(Rectangle screenRect, Rectangle captureBounds, Size imageSize) =>
        RelativeIntersection(screenRect, captureBounds, imageSize);

    private static RarityProbe DetectRarityAround(GameWindow window, ScanProfile profile, System.Drawing.Point center)
    {
        const int radius = 36;
        using var image = window.Capture(new Rectangle(center.X - radius, center.Y - radius, radius * 2 + 1, radius * 2 + 1));
        var candidates = new[]
        {
            new VisualRarityCandidate("S", profile.Color("rarityS")),
            new VisualRarityCandidate("A", profile.Color("rarityA")),
            new VisualRarityCandidate("B", profile.Color("rarityB")),
        };

        var rarityPolicy = (profile.VisualProbes ?? new VisualProbeOptions()).Rarity ?? new RarityProbePolicy();
        var best = ProbeBestRarity(image, candidates, FixedRaritySamplePoints(image.Width, image.Height), rarityPolicy);
        if (best.Rarity is not null)
        {
            return best with { FullScan = false };
        }

        best = ProbeBestRarity(image, candidates, CenterRaritySamplePoints(image.Width, image.Height), rarityPolicy);
        if (best.Rarity is not null)
        {
            return best with { FullScan = false };
        }

        return ProbeBestRarity(image, candidates, FullRaritySamplePoints(image.Width, image.Height), rarityPolicy) with { FullScan = true };
    }

    private static RarityProbe ProbeBestRarity(
        Bitmap image,
        IReadOnlyList<VisualRarityCandidate> candidates,
        IEnumerable<System.Drawing.Point> points,
        RarityProbePolicy policy)
    {
        var result = VisualProbeEvaluator.EvaluateRarity(image, candidates, points, policy);
        return new RarityProbe(result.Rarity, result.BestColor, result.BestCandidate, result.BestScore, result.SecondScore, result.Margin, FullScan: false);
    }

    private static IEnumerable<System.Drawing.Point> FixedRaritySamplePoints(int width, int height)
    {
        var cx = width / 2;
        var cy = height / 2;
        var inner = Math.Max(4, Math.Min(width, height) / 5);
        yield return new System.Drawing.Point(cx, cy);
        yield return new System.Drawing.Point(cx - inner, cy);
        yield return new System.Drawing.Point(cx + inner, cy);
        yield return new System.Drawing.Point(cx, cy - inner);
        yield return new System.Drawing.Point(cx, cy + inner);
        yield return new System.Drawing.Point(cx - inner, cy - inner);
        yield return new System.Drawing.Point(cx + inner, cy - inner);
        yield return new System.Drawing.Point(cx - inner, cy + inner);
        yield return new System.Drawing.Point(cx + inner, cy + inner);
    }

    private static IEnumerable<System.Drawing.Point> CenterRaritySamplePoints(int width, int height)
    {
        var cx = width / 2;
        var cy = height / 2;
        for (var y = cy - 12; y <= cy + 12; y += 4)
        {
            for (var x = cx - 12; x <= cx + 12; x += 4)
            {
                yield return new System.Drawing.Point(x, y);
            }
        }
    }

    private static IEnumerable<System.Drawing.Point> FullRaritySamplePoints(int width, int height)
    {
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                yield return new System.Drawing.Point(x, y);
            }
        }
    }

    private static string ColorText(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
