using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private static void WriteDiagnosis(ScanReport report)
    {
        var panelHigh = report.PanelWait.HasData && report.PanelWait.Average >= 180;
        var scrollHigh = report.ClickAfterScroll.HasData
            && report.ClickSameRow.HasData
            && report.ClickAfterScroll.Average >= report.ClickSameRow.Average * 2.0;
        var backlogHigh = report.ResourceBacklog.HasData
            && report.OcrWorkers is > 0
            && report.ResourceBacklog.Maximum >= report.OcrWorkers.Value * 2;
        var ocrHigh = report.OcrTotal.HasData && report.OcrBacklog.HasData && report.OcrBacklog.Maximum >= 4;

        Write("diagnosis", "panel_wait", panelHigh ? "high: tune panel settle/poll/fallback next" : "ok");
        Write("diagnosis", "scroll_wait", scrollHigh ? "high: tune list stable wait carefully" : "ok");
        Write("diagnosis", "ocr", backlogHigh || ocrHigh ? "high: review ocr_diagnostics.csv and OCR settings" : "ok");
        Write("diagnosis", "roi_integrity", report.IncompleteRoiCount > 0 ? "risk: incomplete ROI captures present" : "ok");
        Write("diagnosis", "safe_band", report.UnsafeVisualRow2ClickCount > 0 ? "risk: middle visual row 2 clicks present" : "ok");
        Write("acceptance", "no_incomplete_roi", report.IncompleteRoiCount == 0 ? "pass" : "fail");
        Write("acceptance", "no_error_files", report.ErrorFileCount == 0 ? "pass" : "fail");
        Write("acceptance", "export_consistency", ExportMatchesCompleted(report) is false ? "risk" : "pass");
        Write("acceptance", "no_export_duplicates", report.ExportUnverifiedDuplicateCount == 0 ? "pass" : "fail");
        Write("acceptance", "slot_safety", report.SlotSafetyPass ? "pass" : "fail");
        Write("acceptance", "backlog_not_saturated", IsBacklogSaturated(report) ? "risk" : "pass");
        Write("acceptance", "no_unsafe_visual_row2", report.UnsafeVisualRow2ClickCount == 0 ? "pass" : "fail");
        Write("acceptance", "overlap_rows_complete", string.Equals(report.Traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase) && report.OverlapRowScannedCount > 0 && report.LastQueued == report.ExportItemCount ? "pass" : "skip");
        Write("acceptance", "overlap_no_hard_stop", report.OverlapHardStopCount == 0 ? "pass" : "fail");
        var nativeEdgeActive = string.Equals(report.RowAdvanceMode, "NativeEdgeClick", StringComparison.OrdinalIgnoreCase);
        Write("acceptance", "native_edge_no_stop", !nativeEdgeActive ? "skip" : report.NativeEdgeClickStopCount == 0 ? "pass" : "fail");
        Write("acceptance", "native_edge_no_wheel_tick", !nativeEdgeActive ? "skip" : report.NativeEdgeWheelTickConflictCount == 0 ? "pass" : "fail");
        var validNonLevel15Stop = report.EffectiveFullScanComplete
            && string.Equals(report.TerminationCode, "non_level_15_stop", StringComparison.OrdinalIgnoreCase);
        Write("acceptance", "overlap_no_missing_rows", !report.FullScanExpected || validNonLevel15Stop ? "skip" : report.OverlapMissingLogicalRowsCount == 0 ? "pass" : "fail");
        Write("acceptance", "full_scan_complete", !report.FullScanExpected ? "skip" : report.EffectiveFullScanComplete ? "pass" : "fail");
        Write("acceptance", "strict_one_way_scroll", report.RowScrollOvershotCount == 0
            && report.RowScrollRecoveryAcceptedCount == 0
            && report.RowScrollStrictStopCount == 0
            && report.NonUnitRowScrollDoneCount == 0
            && report.OverlapConfirmedTwoRowAcceptCount == 0
                ? "pass"
                : "fail");
        Write("acceptance", "scroll_overshot_recovered", report.RowScrollRecoveryFailCount == 0 && report.RowScrollOvershotCount <= report.RowScrollRecoveryAcceptedCount ? "pass" : "risk");
        Write("acceptance", "no_false_scroll_advance", report.RowScrollFalseAdvanceCount == 0 ? "pass" : "fail");
        Write("acceptance", "target_verification_consistency", report.TargetVerificationConsistent ? "pass" : "fail");
        Write("acceptance", "panel_minimum_floor", report.PanelMinimumFloorPass ? "pass" : "fail");
        Write("acceptance", "ocr_drain_consistency", report.OcrDrainStartCount == 0 ? "skip" : report.OcrDrainConsistent && ExportMatchesCompleted(report) != false ? "pass" : "fail");
    }

    private static void WriteStats(string prefix, string name, Stats stats)
    {
        Write(prefix, $"{name}_count", stats.Count);
        Write(prefix, $"{name}_avg", stats.Average);
        Write(prefix, $"{name}_p50", stats.P50);
        Write(prefix, $"{name}_p90", stats.P90);
        Write(prefix, $"{name}_min", stats.Minimum);
        Write(prefix, $"{name}_max", stats.Maximum);
    }

    private static string NormalizeMetricName(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static void Write(string prefix, string name, object? value)
    {
        var formatted = value switch
        {
            null => "N/A",
            double d => double.IsNaN(d) ? "N/A" : d.ToString("F3", CultureInfo.InvariantCulture),
            float f => float.IsNaN(f) ? "N/A" : f.ToString("F3", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "N/A"
        };

        Console.WriteLine($"{prefix}.{name}={formatted}");
    }

    private static void WriteDelta(string name, double? current, double? baseline)
    {
        if (current is null || baseline is null || Math.Abs(baseline.Value) < 0.000001)
        {
            Console.WriteLine($"delta.{name}=N/A");
            return;
        }

        var delta = (current.Value - baseline.Value) * 100.0 / baseline.Value;
        Console.WriteLine($"delta.{name}={delta.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    private static double? Percent(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static double? PercentValue(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double? ParseOptionalDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool? ParseOptionalBool(string value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> row, string column)
    {
        return row.TryGetValue(column, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static double? Rate(int? count, double? seconds)
    {
        return count is not null && seconds is > 0.000001 ? count.Value / seconds.Value : null;
    }

    private static bool? ExportMatchesCompleted(ScanReport report)
    {
        if (report.ExportItemCount is null || report.LastCompleted is null)
        {
            return null;
        }

        if (report.ExportItemCount.Value == report.LastCompleted.Value)
        {
            return true;
        }

        if (report.LastQueued is not null)
        {
            var expectedExportCount = Math.Max(0, report.LastQueued.Value - report.ErrorFileCount - report.Non15FileCount);
            return report.ExportItemCount.Value == expectedExportCount;
        }

        return false;
    }

    private static void WriteSuiteStats(string metric, IEnumerable<double?> values)
    {
        var stats = Stats.From(values.Where(value => value is not null).Select(value => value!.Value));
        Write("suite", $"{metric}_count", stats.Count);
        Write("suite", $"{metric}_min", stats.Minimum);
        Write("suite", $"{metric}_p10", Percentile(values, 0.10));
        Write("suite", $"{metric}_avg", stats.Average);
        Write("suite", $"{metric}_p90", stats.P90);
        Write("suite", $"{metric}_max", stats.Maximum);
    }

    private static string BuildSuiteRejectReason(
        int scanCount,
        int correctnessFailCount,
        double? completedPerSecondP10,
        double? speedVsBaselinePercent)
    {
        var reasons = new List<string>();
        if (scanCount == 0)
        {
            reasons.Add("no_valid_scans");
        }

        if (correctnessFailCount > 0)
        {
            reasons.Add("correctness_failed");
        }

        if (completedPerSecondP10 is null || completedPerSecondP10.Value < RecommendationMinimumP10CompletedPerSecond)
        {
            reasons.Add("p10_below_3_65");
        }

        if (speedVsBaselinePercent is null || speedVsBaselinePercent.Value < RecommendationMinimumAverageGainPercent)
        {
            reasons.Add("avg_gain_below_5_percent");
        }

        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }

    private static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        var sorted = values
            .Where(value => value is not null && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)(sorted.Length * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static bool IsCorrectnessPass(ScanReport report)
    {
        if (!report.Valid)
        {
            return false;
        }

        var exportMatchesCompleted = ExportMatchesCompleted(report);
        var validNonLevel15Stop = report.EffectiveFullScanComplete
            && string.Equals(report.TerminationCode, "non_level_15_stop", StringComparison.OrdinalIgnoreCase);
        return report.LastFailed == 0
            && report.ErrorFileCount == 0
            && report.ExportUnverifiedDuplicateCount == 0
            && report.SlotSafetyPass
            && report.IncompleteRoiCount == 0
            && report.QuickAcceptCount == 0
            && report.RowScrollOvershotCount == 0
            && report.RowScrollRecoveryAcceptedCount == 0
            && report.RowScrollRecoveryFailCount == 0
            && report.RowScrollStrictStopCount == 0
            && report.RowScrollFalseAdvanceCount == 0
            && report.NonUnitRowScrollDoneCount == 0
            && report.OverlapConfirmedTwoRowAcceptCount == 0
            && report.TargetVerificationConsistent
            && report.PanelMinimumFloorPass
            && report.OcrDrainConsistent
            && report.NativeEdgeClickStopCount == 0
            && report.NativeEdgeWheelTickConflictCount == 0
            && report.UnsafeVisualRow2ClickCount == 0
            && report.OverlapHardStopCount == 0
            && (report.Partial != true || validNonLevel15Stop)
            && (!report.FullScanExpected || report.EffectiveFullScanComplete)
            && exportMatchesCompleted != false;
    }

    private static bool IsBacklogSaturated(ScanReport report)
    {
        if (!report.OcrBacklog.HasData || report.OcrQueueCapacity is null || report.OcrBacklog.Maximum is null)
        {
            return false;
        }

        return report.OcrBacklog.Maximum.Value >= Math.Max(1, report.OcrQueueCapacity.Value - 1);
    }

    private static bool IsCaptureLimited(ScanReport report)
    {
        if (report.CaptureQueuedPerSecond is null || report.CompletedPerSecond is null)
        {
            return false;
        }

        var backlogMax = report.OcrBacklog.Maximum ?? report.ResourceBacklog.Maximum ?? 0;
        return backlogMax <= 4
            && report.CompletedPerSecond.Value >= report.CaptureQueuedPerSecond.Value * 0.90;
    }
}
