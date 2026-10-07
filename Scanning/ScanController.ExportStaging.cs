using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using CvRect = System.Drawing.Rectangle;
using OcrBatchInput = ZZZScannerNext.Ocr.PaddleOcrRecognizer.OcrBatchInput;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static (string DetailPath, string DetailHash, string CardPath) WriteR4CaptureEvidence(
        string outputDirectory,
        DiscCapture capture)
    {
        var evidenceDirectory = Path.Combine(outputDirectory, "r4-evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var detailPath = Path.Combine(evidenceDirectory, $"{capture.Index:D4}-detail.png");
        capture.Image.Save(detailPath, ImageFormat.Png);
        var detailHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(detailPath))).ToLowerInvariant();
        var cardPath = detailPath;
        if (capture.LockEvidence.CardImage is { } cardImage)
        {
            cardPath = Path.Combine(evidenceDirectory, $"{capture.Index:D4}-card.png");
            cardImage.Save(cardPath, ImageFormat.Png);
        }

        return (detailPath, detailHash, cardPath);
    }

    private static void TryWriteOcrShadowDataset(
        OcrShadowDatasetWriter? shadowDataset,
        DiscCapture capture,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export,
        ScanLog scanLog)
    {
        if (shadowDataset is null)
        {
            return;
        }

        try
        {
            shadowDataset.Write(capture.Index, capture.Rarity, capture.Image, capture.Rois, ocr, export);
        }
        catch (Exception ex)
        {
            scanLog.Write($"OCR shadow dataset write failed for index={capture.Index}: {ex.Message}");
        }
    }

    private static void TryWriteFastOcrShadow(
        FastOcrShadowRecorder? fastOcrShadow,
        DiscCapture capture,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export,
        ScanLog scanLog)
    {
        if (fastOcrShadow is null)
        {
            return;
        }

        try
        {
            fastOcrShadow.Write(capture.Index, capture.Rarity, capture.Image, capture.Rois, ocr, export);
        }
        catch (Exception ex)
        {
            scanLog.Write($"Fast OCR shadow write failed for index={capture.Index}: {ex.Message}");
        }
    }

    private static void TryWriteFastOcrAssist(
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        FastOcrAssistPlan? assistPlan,
        IReadOnlyList<OcrResult> ocr,
        ScanLog scanLog)
    {
        if (fastOcrAssistRecorder is null || assistPlan is null)
        {
            return;
        }

        try
        {
            fastOcrAssistRecorder.Write(assistPlan.Decisions, ocr);
        }
        catch (Exception ex)
        {
            scanLog.Write($"Fast OCR assist write failed: {ex.Message}");
        }
    }


    private static ScannerFailureException InventoryCountOcrFailure(
        string message,
        IReadOnlyDictionary<string, object?>? details = null) => new(
        "inventory_count_ocr_failed",
        "仓库数量识别失败",
        message,
        "请确认仓库数量区域完整可见，并关闭遮挡或缩放后重试。",
        details);

    private static ScannerFailureException NavigationFailure(
        string message,
        IReadOnlyDictionary<string, object?>? details = null) => new(
        "scan_navigation_failed",
        "驱动盘列表滚动失败",
        message,
        "请保持游戏前台且不要操作鼠标滚轮，然后重新扫描。",
        details);


    private static bool NeedsNormalizedOcrRetry(IReadOnlyList<OcrResult> ocr)
    {
        return ocr.Any(result => string.IsNullOrWhiteSpace(result.Text) || result.Score < 0.45f);
    }

    internal sealed record PpOcrV6NormalizedRetryResult(
        bool Attempted,
        DriveDiscExport? Export,
        IReadOnlyList<OcrResult>? Ocr,
        string? Error,
        int CandidateCount = 0,
        string? SourceMask = null);

    // This seam is deliberately limited to a failed v6 clean with an original
    // empty/low-confidence row. The two reads may complement one another, but
    // we only accept them when every valid field-wise combination converges to
    // one canonical export; ambiguity remains review-only.
    internal static PpOcrV6NormalizedRetryResult TryCleanPpOcrV6NormalizedRetry(
        IReadOnlyList<OcrResult> originalOcr,
        Func<IReadOnlyList<OcrResult>> recognizeNormalized,
        DriveDiscCleaner cleaner,
        int index,
        string rarity)
    {
        ArgumentNullException.ThrowIfNull(originalOcr);
        ArgumentNullException.ThrowIfNull(recognizeNormalized);
        ArgumentNullException.ThrowIfNull(cleaner);
        if (!NeedsNormalizedOcrRetry(originalOcr))
        {
            return new PpOcrV6NormalizedRetryResult(false, null, null, "original_ocr_retry_gate_closed");
        }

        try
        {
            var normalized = PpOcrV6DetailGeometry.ExpandForUpstreamCleaner(recognizeNormalized());
            if (originalOcr.Count != 12 || normalized.Count != 12)
            {
                return new PpOcrV6NormalizedRetryResult(true, null, null, "ppocrv6_expanded_field_count_invalid");
            }

            var groups = new[]
            {
                new[] { 0 }, new[] { 1 }, new[] { 2, 3 }, new[] { 4, 5 },
                new[] { 6, 7 }, new[] { 8, 9 }, new[] { 10, 11 }
            };
            var valid = new Dictionary<string, (DriveDiscExport Export, IReadOnlyList<OcrResult> Ocr, string Mask)>();
            for (var mask = 0; mask < (1 << groups.Length); mask++)
            {
                var merged = originalOcr.ToArray();
                for (var group = 0; group < groups.Length; group++)
                {
                    if ((mask & (1 << group)) == 0) continue;
                    foreach (var field in groups[group]) merged[field] = normalized[field];
                }

                try
                {
                    var export = cleaner.Clean(index, rarity, merged);
                    var key = CanonicalDriveDiscExportKey(export);
                    valid.TryAdd(key, (export, merged, Convert.ToString(mask, 2).PadLeft(groups.Length, '0')));
                }
                catch (Exception candidateException) when (candidateException is InvalidDataException or DriveDiscPartialParseException)
                {
                    // A non-domain-valid candidate is intentionally ignored.
                }
            }

            if (valid.Count != 1)
            {
                return new PpOcrV6NormalizedRetryResult(true, null, null,
                    valid.Count == 0 ? "normalized_fieldwise_no_complete_candidate" : "normalized_fieldwise_ambiguous_candidates",
                    valid.Count);
            }

            var accepted = valid.Values.Single();
            return new PpOcrV6NormalizedRetryResult(true, accepted.Export, accepted.Ocr, null, valid.Count, accepted.Mask);
        }
        catch (Exception ex)
        {
            return new PpOcrV6NormalizedRetryResult(true, null, null, ex.Message);
        }
    }

    private static string CanonicalDriveDiscExportKey(DriveDiscExport export)
    {
        var main = string.Join(";", export.MainStat.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
        var sub = string.Join(";", export.SubStats.Select(stat => string.Join(",", stat.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"))));
        return $"{export.Name}|{export.Slot}|{export.Rarity}|{export.Level}|{export.MaxLevel}|{main}|{sub}";
    }

    private static string SanitizeLogValue(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace(',', ';');
    }

}
