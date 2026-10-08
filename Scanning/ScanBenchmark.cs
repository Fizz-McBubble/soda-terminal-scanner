using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private const double RecommendationBaselineCompletedPerSecond = 3.593;
    private const double RecommendationMinimumP10CompletedPerSecond = 3.65;
    private const double RecommendationMinimumAverageGainPercent = 5.0;

    private static readonly Regex EventRegex = new(
        @"^\[(?<timestamp>[^\]]+)\].*EVENT #\d+ (?<kind>[A-Z_]+): (?<detail>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex CellClickRegex = new(
        @"col=(?<col>\d+)/(?<max>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex SafeBandPositionRegex = new(
        @"logicalRow=(?<logical>\d+).*?visualRow=(?<visual>\d+).*?visibleTopLogicalRow=(?<top>\d+).*?state=(?<state>[A-Za-z]+)",
        RegexOptions.Compiled);

    private static readonly Regex CellTimingRegex = new(
        @"CELL_TIMING: index=(?<index>\d+).*?(?:afterScroll=(?<afterScroll>True|False), postScrollFirstCell=(?<postScrollFirst>True|False), )?panelWaitMs=(?<panel>[\d.]+), enqueueWaitMs=(?<enqueue>[\d.]+), fallback=(?<fallback>True|False), visibleRois=(?<visible>\d+)/(?<total>\d+), totalMs=(?<cellTotal>[\d.]+)(?:, panelFrames=(?<frames>\d+), changeMs=(?<change>[\d.]+|NA)(?:, selectionChangeMs=(?<selection>[\d.]+|NA))?, fullRoiMs=(?<fullRoi>[\d.]+|NA), stableMs=(?<stable>[\d.]+|NA)(?:, panelTextStableMs=(?<textStable>[\d.]+|NA), panelStableSource=(?<stableSource>[^,]+), panelStabilityReason=(?<stabilityReason>[^,]+)(?:, targetSelectionStableFrames=(?<targetSelectionStableFrames>\d+), targetVerificationKind=(?<targetVerificationKind>[^,]+))?, rarityProbeMs=(?<rarityProbe>[\d.]+), selectionProbeMs=(?<selectionProbe>[\d.]+))?(?:, captureMs=(?<capture>[\d.]+), signatureMs=(?<signature>[\d.]+), visibleRoiMs=(?<visibleRoi>[\d.]+), frameLoopMs=(?<frameLoop>[\d.]+)(?:, frameToBitmapMs=(?<frameToBitmap>[\d.]+), bitmapCreatedCount=(?<bitmapCreated>\d+))?(?:, quickAccept=(?<quickAccept>True|False), quickRejectReason=(?<quickReject>[^,]+))?(?:, adaptiveThrottleMs=(?<throttle>[\d.]+), ocrBacklogBeforeEnqueue=(?<backlog>\d+), adaptivePanelMinMs=(?<panelMin>\d+), adaptivePanelSamples=(?<panelSamples>\d+), adaptivePanelReason=(?<panelReason>[^,]+)(?:, panelAcceptMode=(?<panelAcceptMode>[^,]+)(?:, postScrollAcceptMode=(?<postScrollAcceptMode>[^,]+), panelMinFloorMs=(?<panelMinFloor>\d+))?, roiCompleteFrames=(?<roiCompleteFrames>\d+), selectedStableFrames=(?<selectedStableFrames>\d+), acceptGateReason=(?<acceptGateReason>[^,]+)(?:, panelFloorMode=(?<panelFloorMode>[^,]+), sameRowPanelFloorMs=(?<sameRowPanelFloor>\d+), postScrollPanelFloorMs=(?<postScrollPanelFloor>\d+), panelFloorReason=(?<panelFloorReason>[^,]+), floorWaitLimitedMs=(?<floorWaitLimited>[\d.]+), panelAcceptElapsedVsFloorMs=(?<acceptElapsedVsFloor>[-\d.]+), scrollTickDelayMs=(?<scrollTickDelay>\d+))?)?)?)?)?",
        RegexOptions.Compiled);

    private static readonly Regex ScrollTimingRegex = new(
        @"ROW_SCROLL_TIMING: .*?scroll_tick_wait_ms=(?<tick>[\d.]+), list_stable_ms=(?<stable>[\d.]+), row_signature_ms=(?<row>[\d.]+)(?:, settle_samples=(?<settleSamples>\d+), settle_elapsed_ms=(?<settle>[\d.]+))?, post_scroll_viewport_ms=(?<viewport>[\d.]+)",
        RegexOptions.Compiled);

    private static readonly Regex StartRegex = new(
        @"Start scan\..*?OcrWorkers=(?<workers>\d+), OcrBatchSize=(?<batch>\d+), OcrQueueCapacity=(?<queue>\d+), OcrIntraOpThreads=(?<intra>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex MaxItemsRegex = new(
        @"Start scan\..*?MaxItems=(?<max>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex RowAdvanceModeRegex = new(
        @"Start scan\..*?RowAdvanceMode=(?<mode>[a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TraversalRegex = new(
        @"Traversal:\s*(?<mode>[a-zA-Z0-9_-]+)",
        RegexOptions.Compiled);

    private static readonly Regex OverlapTraversalRegex = new(
        @"Traversal:\s*overlap-signature-page\..*?totalRows=(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverlapCompletedRegex = new(
        @"End:\s*overlap-signature-page completed\..*?visited=(?<visited>\d+).*?queued=(?<queued>\d+).*?completed=(?<completed>\d+).*?failed=(?<failed>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CounterRegex = new(
        @"visited=(?<visited>\d+), queued=(?<queued>\d+), completed=(?<completed>\d+), failed=(?<failed>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex TerminalDetailRegex = new(
        @"^visited=(?<visited>\d+), queued=(?<queued>\d+), completed=(?<completed>\d+), failed=(?<failed>\d+), partial=(?<partial>True|False), terminationCode=(?<termination>[^,]*), exportFile=(?<export>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CellTimingIndexRegex = new(
        @"CELL_TIMING: index=(?<index>\d+)",
        RegexOptions.Compiled);

    public static int Run(string scanDirectory, string? baselineDirectory)
    {
        if (!Directory.Exists(scanDirectory))
        {
            Console.Error.WriteLine($"Scan directory not found: {scanDirectory}");
            return 2;
        }

        if (baselineDirectory is not null && !Directory.Exists(baselineDirectory))
        {
            Console.Error.WriteLine($"Baseline scan directory not found: {baselineDirectory}");
            return 2;
        }

        var current = Analyze(scanDirectory);
        if (!current.Valid)
        {
            Console.Error.WriteLine(current.ErrorMessage);
            return 2;
        }

        ScanReport? baseline = null;
        if (baselineDirectory is not null)
        {
            baseline = Analyze(baselineDirectory);
            if (!baseline.Valid)
            {
                Console.Error.WriteLine(baseline.ErrorMessage);
                return 2;
            }
        }

        WriteReport("current", current);
        if (baseline is not null)
        {
            WriteReport("baseline", baseline);
            WriteDeltaReport(current, baseline);
        }

        WriteDiagnosis(current);
        return IsCorrectnessPass(current) ? 0 : 1;
    }

    public static int RunStabilitySuite(string scanParent)
    {
        if (!Directory.Exists(scanParent))
        {
            Console.Error.WriteLine($"Scan parent not found: {scanParent}");
            return 2;
        }

        var scanDirectories = EnumerateScanDirectories(scanParent).ToArray();
        if (scanDirectories.Length == 0)
        {
            Console.Error.WriteLine($"No scan.log files found under: {scanParent}");
            return 2;
        }

        var reports = new List<ScanReport>();
        foreach (var scanDirectory in scanDirectories)
        {
            var report = Analyze(scanDirectory);
            if (!report.Valid)
            {
                Write("suite", $"scan_{NormalizeMetricName(Path.GetFileName(scanDirectory))}_valid", "false");
                continue;
            }

            reports.Add(report);
            var scanName = NormalizeMetricName(Path.GetFileName(scanDirectory));
            Write("suite", $"scan_{scanName}_completed_per_sec", report.CompletedPerSecond);
            Write("suite", $"scan_{scanName}_correctness", IsCorrectnessPass(report) ? "pass" : "fail");
            Write("suite", $"scan_{scanName}_failed", report.LastFailed);
            Write("suite", $"scan_{scanName}_duplicates", report.ExportDuplicateItemCount);
            Write("suite", $"scan_{scanName}_incomplete_roi", report.IncompleteRoiCount);
            Write("suite", $"scan_{scanName}_overshot", report.RowScrollOvershotCount);
        }

        var correctnessFailCount = reports.Count(report => !IsCorrectnessPass(report));
        var completedPerSecondValues = reports.Select(report => report.CompletedPerSecond).ToArray();
        var completedPerSecondStats = Stats.From(completedPerSecondValues.Where(value => value is not null).Select(value => value!.Value));
        var completedPerSecondP10 = Percentile(completedPerSecondValues, 0.10);
        double? speedVsBaselinePercent = completedPerSecondStats.Average is not null
            ? (completedPerSecondStats.Average.Value - RecommendationBaselineCompletedPerSecond) * 100.0 / RecommendationBaselineCompletedPerSecond
            : null;
        var rejectReason = BuildSuiteRejectReason(reports.Count, correctnessFailCount, completedPerSecondP10, speedVsBaselinePercent);
        var recommendedCandidate = string.Equals(rejectReason, "none", StringComparison.OrdinalIgnoreCase);

        Write("suite", "scan_parent", scanParent);
        Write("suite", "scan_count", reports.Count);
        Write("suite", "correctness_pass_count", reports.Count(IsCorrectnessPass));
        Write("suite", "correctness_fail_count", correctnessFailCount);
        WriteSuiteStats("completed_per_sec", completedPerSecondValues);
        WriteSuiteStats("panel_wait_ms_avg", reports.Select(report => report.PanelWait.Average));
        WriteSuiteStats("post_scroll_first_panel_wait_ms_avg", reports.Select(report => report.PostScrollFirstPanelWait.Average));
        WriteSuiteStats("scroll_ms_avg", reports.Select(report => report.ScrollDuration.Average));
        Write("suite", "failed_sum", reports.Sum(report => report.LastFailed ?? 0));
        Write("suite", "export_duplicate_items_sum", reports.Sum(report => report.ExportDuplicateItemCount));
        Write("suite", "slot_out_of_range_sum", reports.Sum(report => report.SlotOutOfRangeCount));
        Write("suite", "slot_mainstat_violation_sum", reports.Sum(report => report.SlotMainStatViolationCount));
        Write("suite", "slot_fixed_value_violation_sum", reports.Sum(report => report.SlotFixedValueViolationCount));
        Write("suite", "incomplete_roi_sum", reports.Sum(report => report.IncompleteRoiCount));
        Write("suite", "row_scroll_overshot_sum", reports.Sum(report => report.RowScrollOvershotCount));
        Write("suite", "row_scroll_no_move_sum", reports.Sum(report => report.RowScrollNoMoveCount));
        Write("suite", "row_scroll_ambiguous_sum", reports.Sum(report => report.RowScrollAmbiguousCount));
        Write("suite", "row_scroll_settle_accept_sum", reports.Sum(report => report.RowScrollSettleAcceptCount));
        Write("suite", "row_scroll_settle_timeout_sum", reports.Sum(report => report.RowScrollSettleTimeoutCount));
        Write("suite", "row_scroll_partial_move_continue_sum", reports.Sum(report => report.RowScrollPartialMoveContinueCount));
        Write("suite", "row_scroll_false_advance_sum", reports.Sum(report => report.RowScrollFalseAdvanceCount));
        Write("suite", "panel_target_evidence_sum", reports.Sum(report => report.PanelTargetEvidenceCount));
        Write("suite", "panel_neighbor_roundtrip_sum", reports.Sum(report => report.PanelNeighborRoundTripCount));
        Write("suite", "ocr_drain_inconsistent_sum", reports.Count(report => !report.OcrDrainConsistent));
        Write("suite", "overlap_conflict_sum", reports.Sum(report => report.OverlapConflictCount));
        Write("suite", "overlap_conflict_recheck_sum", reports.Sum(report => report.OverlapConflictRecheckCount));
        Write("suite", "overlap_conflict_recovered_sum", reports.Sum(report => report.OverlapConflictRecoveredCount));
        Write("suite", "overlap_ambiguous_accept_sum", reports.Sum(report => report.OverlapAmbiguousAcceptCount));
        Write("suite", "overlap_confirmed_two_row_accept_sum", reports.Sum(report => report.OverlapConfirmedTwoRowAcceptCount));
        Write("suite", "overlap_hard_stop_sum", reports.Sum(report => report.OverlapHardStopCount));
        Write("suite", "missing_logical_rows_sum", reports.Sum(report => report.OverlapMissingLogicalRowsCount));
        Write("suite", "edge_click_changed_sum", reports.Sum(report => report.NativeEdgeClickChangedCount));
        Write("suite", "edge_click_hash_fallback_sum", reports.Sum(report => report.NativeEdgeClickHashFallbackCount));
        Write("suite", "edge_click_bottom_sum", reports.Sum(report => report.NativeEdgeClickBottomCount));
        Write("suite", "edge_click_stop_sum", reports.Sum(report => report.NativeEdgeClickStopCount));
        Write("suite", "native_edge_wheel_tick_conflict_sum", reports.Sum(report => report.NativeEdgeWheelTickConflictCount));
        Write("suite", "full_scan_complete_count", reports.Count(report => report.FullScanComplete));
        Write("suite", "quick_accept_sum", reports.Sum(report => report.QuickAcceptCount));
        Write("suite", "fallback_sum", reports.Sum(report => report.EffectiveFallbackCount));
        Write("suite", "recommendation_baseline_completed_per_sec", RecommendationBaselineCompletedPerSecond);
        Write("suite", "speed_vs_baseline_percent", speedVsBaselinePercent);
        Write("suite", "recommended_candidate", recommendedCandidate.ToString().ToLowerInvariant());
        Write("suite", "reject_reason", rejectReason);
        return reports.Count == 0 ? 2 : correctnessFailCount == 0 ? 0 : 1;
    }

    private static IEnumerable<string> EnumerateScanDirectories(string scanParent)
    {
        if (File.Exists(Path.Combine(scanParent, "scan.log")))
        {
            yield return scanParent;
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(scanParent)
                     .Where(directory => File.Exists(Path.Combine(directory, "scan.log")))
                     .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase))
        {
            yield return directory;
        }
    }
}
