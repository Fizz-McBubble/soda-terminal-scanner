using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

/// <summary>
/// Soda's canonical seven-row detail geometry. The JSON is the single source
/// shared with the locked PP-OCRv6 benchmark and the production recognizer.
/// </summary>
public static partial class PpOcrV6DetailGeometry
{
    public static IReadOnlyList<Rectangle> LoadProductionRois()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.DataFile("scanner-detail-geometry.v1.json")));
        var production = document.RootElement.GetProperty("production");
        var crop = production.GetProperty("detailCrop");
        var width = crop.GetProperty("right").GetInt32() - crop.GetProperty("left").GetInt32();
        var height = crop.GetProperty("bottom").GetInt32() - crop.GetProperty("top").GetInt32();
        if (width != 450 || height != 517)
            throw new InvalidDataException($"ppocrv6_detail_geometry_size_mismatch:{width}x{height}");
        var fields = production.GetProperty("fields").EnumerateArray().ToArray();
        var expected = new[] { "selectedDiscName", "level", "mainStat", "subStat1", "subStat2", "subStat3", "subStat4" };
        if (!fields.Select(field => field.GetProperty("key").GetString()).SequenceEqual(expected))
            throw new InvalidDataException("ppocrv6_detail_geometry_field_order_invalid");
        var rois = fields.Select(field =>
        {
            var left = field.GetProperty("left").GetInt32();
            var top = field.GetProperty("top").GetInt32();
            var right = field.GetProperty("right").GetInt32();
            var bottom = field.GetProperty("bottom").GetInt32();
            if (left < 0 || top < 0 || right > width || bottom > height || left >= right || top >= bottom)
                throw new InvalidDataException("ppocrv6_detail_geometry_roi_out_of_bounds");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }).ToArray();
        ValidateProductionRois(rois, new Size(width, height));
        return rois;
    }

    /// <summary>
    /// Map canonical edges into the captured panel without resizing its pixels.
    /// The recognition worker already normalizes each individual text crop.
    /// Each capture, including a luminance retry, must use its own mapped ROIs.
    /// </summary>
    internal static IReadOnlyList<Rectangle> ResolveProductionRois(
        Size detailSize, IReadOnlyList<Rectangle> canonicalRois)
    {
        var canonicalSize = new Size(450, 517);
        ValidateProductionRois(canonicalRois, canonicalSize);
        // Crops come from a separately validated 16:9 client/profile. Independently
        // rounded panel dimensions can differ from the ideal ratio by <= 2px.
        if (detailSize.Width < 300 || detailSize.Width > 900
            || detailSize.Height < 344 || detailSize.Height > 1035
            || Math.Abs(detailSize.Height - detailSize.Width * 517.0 / 450) > 2)
            throw new InvalidDataException($"ppocrv6_detail_geometry_size_incompatible:{detailSize.Width}x{detailSize.Height}");

        // Preserve the exact established 1080/1081 path and crop bytes.
        if (detailSize == canonicalSize) return canonicalRois;

        var mapped = canonicalRois.Select(roi => Rectangle.FromLTRB(
            (int)Math.Round(roi.Left * detailSize.Width / 450.0),
            (int)Math.Round(roi.Top * detailSize.Height / 517.0),
            (int)Math.Round(roi.Right * detailSize.Width / 450.0),
            (int)Math.Round(roi.Bottom * detailSize.Height / 517.0))).ToArray();
        ValidateProductionRois(mapped, detailSize);
        return mapped;
    }

    private static void ValidateProductionRois(IReadOnlyList<Rectangle> rois, Size detailSize)
    {
        if (rois.Count != 7) throw new InvalidDataException($"ppocrv6_detail_field_count:{rois.Count}/7");
        for (var index = 0; index < rois.Count; index++)
        {
            var roi = rois[index];
            if (roi.Width <= 0 || roi.Height <= 0 || roi.Left < 0 || roi.Top < 0
                || roi.Right > detailSize.Width || roi.Bottom > detailSize.Height)
                throw new InvalidDataException("ppocrv6_detail_geometry_roi_out_of_bounds");
            for (var previous = 0; previous < index; previous++)
                if (roi.IntersectsWith(rois[previous]))
                    throw new InvalidDataException("ppocrv6_detail_geometry_roi_overlap");
        }
    }

    public static IReadOnlyList<OcrResult> ExpandForUpstreamCleaner(IReadOnlyList<OcrResult> rows)
    {
        if (rows.Count != 7) throw new InvalidDataException($"ppocrv6_detail_field_count:{rows.Count}/7");
        var expanded = new List<OcrResult>(12) { rows[0], rows[1] };
        expanded.AddRange(SplitStatRow(rows[2], allowUpgrade: false));
        for (var index = 3; index < rows.Count; index++)
            expanded.AddRange(SplitStatRow(rows[index], allowUpgrade: true));
        return expanded;
    }

    private static IReadOnlyList<OcrResult> SplitStatRow(OcrResult row, bool allowUpgrade)
    {
        var text = string.Concat(row.Text.Where(character => !char.IsWhiteSpace(character)));
        if (string.IsNullOrWhiteSpace(text)) return [new OcrResult(0, ""), new OcrResult(0, "")];
        var match = (allowUpgrade ? SubStatRowRegex() : MainStatRowRegex()).Match(text);
        if (!match.Success) return [row, new OcrResult(0, "")];
        var label = match.Groups["label"].Value;
        if (allowUpgrade && match.Groups["upgrade"].Success)
            label += $"+{match.Groups["upgrade"].Value}";
        return [new OcrResult(row.Score, label), new OcrResult(row.Score, match.Groups["value"].Value)];
    }

    [GeneratedRegex(@"^(?<label>.*?)(?<value>\d+(?:\.\d+)?%?)$", RegexOptions.CultureInvariant)]
    private static partial Regex MainStatRowRegex();

    [GeneratedRegex(@"^(?<label>.*?)(?:\+(?<upgrade>[0-5]))?(?<value>\d+(?:\.\d+)?%?)$", RegexOptions.CultureInvariant)]
    private static partial Regex SubStatRowRegex();
}
