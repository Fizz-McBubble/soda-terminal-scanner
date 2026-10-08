using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public static partial class FastOcrEvaluator
{
    private static void WriteFeatureEvalReport(string outputFile, IReadOnlyList<FastOcrFeatureEvalRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("feature,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,reason,avg_score,avg_margin");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Feature),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason),
                row.AvgScore.ToString("F6", CultureInfo.InvariantCulture),
                row.AvgMargin.ToString("F6", CultureInfo.InvariantCulture)
            ]));
        }
    }

    private static void WriteCalibrationSummary(IReadOnlyList<FastOcrCalibrationRow> rows)
    {
        foreach (var row in rows)
        {
            var prefix = $"field.{SanitizeKey(row.FieldKey)}";
            Console.WriteLine($"{prefix}.calibrated_assist={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"{prefix}.calibrated_min_score={row.MinScore.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"{prefix}.calibrated_min_margin={row.MinMargin.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"{prefix}.calibrated_accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }
    }

    private static void WriteSummary(IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Console.WriteLine($"fast_eval.rows={rows.Count}");
        Console.WriteLine($"fast_eval.accepted={rows.Count(row => row.Accepted)}");
        Console.WriteLine($"fast_eval.false_accepts={rows.Count(row => row.FalseAccept)}");
        Console.WriteLine($"fast_eval.rejects={rows.Count(row => !row.Accepted)}");

        foreach (var group in rows.GroupBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.ToArray();
            var accepted = values.Count(row => row.Accepted);
            var matches = values.Count(row => row.MatchesClean);
            var falseAccepts = values.Count(row => row.FalseAccept);
            var prefix = $"field.{SanitizeKey(group.Key)}";
            Console.WriteLine($"{prefix}.rows={values.Length}");
            Console.WriteLine($"{prefix}.accepted={accepted}");
            Console.WriteLine($"{prefix}.false_accepts={falseAccepts}");
            Console.WriteLine($"{prefix}.rejects={values.Length - accepted}");
            Console.WriteLine($"{prefix}.accept_rate={Rate(accepted, values.Length)}");
            Console.WriteLine($"{prefix}.match_rate={Rate(matches, values.Length)}");
            Console.WriteLine($"{prefix}.avg_ms={Average(values.Select(row => row.ElapsedMs))}");
            Console.WriteLine($"{prefix}.p90_ms={Percentile(values.Select(row => row.ElapsedMs), 0.9)}");
        }
    }

    private static string ResolveOutputFile(string shadowPath)
    {
        if (File.Exists(shadowPath))
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shadowPath)) ?? ".", "ocr_fast_eval.csv");
        }

        var direct = Path.Combine(shadowPath, "ocr_shadow.csv");
        return File.Exists(direct)
            ? Path.Combine(Path.GetFullPath(shadowPath), "ocr_fast_eval.csv")
            : Path.Combine(Path.GetFullPath(shadowPath), "ocr_fast_eval.csv");
    }

    private static string ResolveSidecarOutputFile(string outputFile, string name)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? ".", name);
    }

    private static string ConfusionKey(FastOcrEvaluationRow row)
    {
        return $"{row.FieldKey}\0{row.CleanLabel}\0{row.FastLabel}";
    }

    private static string Rate(int numerator, int denominator)
    {
        return denominator == 0 ? "N/A" : (numerator / (double)denominator).ToString("F6", CultureInfo.InvariantCulture);
    }

    private static string Average(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? "N/A" : array.Average().ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string Percentile(IEnumerable<double> values, double percentile)
    {
        var array = values.OrderBy(value => value).ToArray();
        if (array.Length == 0)
        {
            return "N/A";
        }

        var index = Math.Clamp((int)Math.Ceiling(array.Length * percentile) - 1, 0, array.Length - 1);
        return array[index].ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string SanitizeKey(string key)
    {
        return string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

public sealed record FastOcrEvaluationRow(
    string Fold,
    string CsvFile,
    int ItemIndex,
    int RoiIndex,
    string VisualProfileId,
    string ProfileFamilyId,
    string FieldKey,
    string CleanLabel,
    string FastLabel,
    double Score,
    string Top2Label,
    double Top2Score,
    double Margin,
    bool AssistEnabled,
    bool Accepted,
    bool MatchesClean,
    bool FalseAccept,
    double ElapsedMs,
    string SourceFamilyId,
    bool CanonicalCropSucceeded,
    bool CanonicalCropFallback,
    double FeatureMs,
    string Reason);

public sealed record FastOcrProfileCalibrationRow(
    string VisualProfileId,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrFamilyCalibrationRow(
    string ProfileFamilyId,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrCalibrationRow(
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrFeatureEvalRow(
    string Feature,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    string Reason,
    double AvgScore,
    double AvgMargin);

public sealed record FastOcrThresholdChoice(
    double MinScore,
    double MinMargin,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate);

public sealed record FastOcrConfusionRow(
    string Kind,
    string FieldKey,
    string CleanLabel,
    string FastLabel,
    int Count,
    double AvgScore,
    double AvgMargin,
    string Folds)
{
    public static FastOcrConfusionRow From(
        string kind,
        IGrouping<string, FastOcrEvaluationRow> group)
    {
        var rows = group.ToArray();
        var first = rows[0];
        return new FastOcrConfusionRow(
            kind,
            first.FieldKey,
            first.CleanLabel,
            first.FastLabel,
            rows.Length,
            rows.Average(row => row.Score),
            rows.Average(row => row.Margin),
            string.Join("|", rows.Select(row => row.Fold).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
    }
}
