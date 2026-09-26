using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

/// <summary>
/// Soda's frozen seven-row detail geometry. The JSON is the single source
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
        return fields.Select(field =>
        {
            var left = field.GetProperty("left").GetInt32();
            var top = field.GetProperty("top").GetInt32();
            var right = field.GetProperty("right").GetInt32();
            var bottom = field.GetProperty("bottom").GetInt32();
            if (left < 0 || top < 0 || right > width || bottom > height || left >= right || top >= bottom)
                throw new InvalidDataException("ppocrv6_detail_geometry_roi_out_of_bounds");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }).ToArray();
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
