using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private static ScanReport Analyze(string scanDirectory)
    {
        var logFile = Path.Combine(scanDirectory, "scan.log");
        if (!File.Exists(logFile))
        {
            return ScanReport.Invalid(scanDirectory, $"scan.log not found: {logFile}");
        }

        var lines = File.ReadAllLines(logFile);
        var events = ParseEvents(lines);
        var cellTimings = ParseCellTimings(lines);
        var scrollTimings = ParseScrollTimings(lines);
        var ocrRows = ReadCsv(Path.Combine(scanDirectory, "ocr_diagnostics.csv"));
        var fastAssistRows = ReadCsv(Path.Combine(scanDirectory, "ocr_fast_assist.csv"));
        var resourceRows = ReadCsv(Path.Combine(scanDirectory, "resource.csv"));
        var ocrPerItemTotal = ReadOcrMillisecondsPerItem(ocrRows);
        var fastMatchMsPerItem = ReadColumnPerItem(ocrRows, "fast_match_ms");
        var fastAcceptedPerItem = ReadColumnPerItem(ocrRows, "fast_accepted_count");
        var fastRejectedPerItem = ReadColumnPerItem(ocrRows, "fast_rejected_count");
        var ppOcrRoiPerItem = ReadColumnPerItem(ocrRows, "ppocr_roi_count");
        var intervals = BuildClickIntervals(events);
        var clickPositions = ParseClickPositions(events);
        var scrollDurations = BuildScrollDurations(events);
        var terminal = ParseScanTerminal(events);
        var lastCounters = ParseLastCounters(lines);
        var resourceCounters = ParseResourceCounters(resourceRows);
        var scanOnceCounters = ParseScanOnceCounters(Path.Combine(scanDirectory, "scan-once-result.json"));
        var visualProfile = RuntimeVisualProfile.LoadOrLegacy(scanDirectory);
        var startSettings = ParseStartSettings(lines);
        var traversal = ParseTraversal(lines);
        var overlapSummary = ParseOverlapTraversalSummary(lines, events);
        var exportFile = ResolveExportFile(scanDirectory, terminal?.ExportFile);
        var verifiedIdenticalNeighborIndices = events
            .Where(item => item.Kind == "ITEM_TARGET_VERIFICATION"
                && item.Detail.Contains("kind=IdenticalNeighborRoundTrip", StringComparison.OrdinalIgnoreCase)
                && TryReadEventInt(item, "index", out _))
            .Select(item =>
            {
                TryReadEventInt(item, "index", out var index);
                return index;
            })
            .ToHashSet();
        var exportStats = ReadExportStats(exportFile, verifiedIdenticalNeighborIndices);
        var invalidRoiTimeoutCount = events.Count(item => item.Kind == "PANEL_CAPTURE_TIMEOUT"
            && (item.Detail.Contains("acceptGateReason=required_core_missing", StringComparison.OrdinalIgnoreCase)
                || item.Detail.Contains("acceptGateReason=incomplete_substat_pair", StringComparison.OrdinalIgnoreCase)
                || item.Detail.Contains("acceptGateReason=substat_gap", StringComparison.OrdinalIgnoreCase)
                || item.Detail.Contains("acceptGateReason=invalid_roi_layout", StringComparison.OrdinalIgnoreCase)));
        var non15FileCount = Directory.EnumerateFiles(scanDirectory, "*.non15.txt").Count();
        var inferredPartial = terminal?.Partial
            ?? string.Equals(Path.GetFileName(exportFile), "export.partial.json", StringComparison.OrdinalIgnoreCase);
        var inferredTerminationCode = terminal?.TerminationCode
            ?? (non15FileCount > 0 ? "non_level_15_stop" : "");
        var captureEndTime = ParseCaptureEndTime(lines);
        var overlapMode = string.Equals(traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase);
        var sameRowClick = Stats.From(intervals.Where(x => !x.AfterScroll).Select(x => x.Milliseconds));
        var afterScrollClick = Stats.From(intervals.Where(x => x.AfterScroll).Select(x => x.Milliseconds));

        var report = new ScanReport(scanDirectory)
        {
            Valid = true,
            StartTime = ParseStartTime(lines),
            EndTime = ParseEndTime(lines),
            StopReason = ParseStopReason(lines),
            Partial = inferredPartial,
            TerminationCode = inferredTerminationCode,
            ExportFileName = Path.GetFileName(exportFile),
            CaptureEndTime = captureEndTime,
            OcrWorkers = startSettings?.Workers,
            OcrBatchSizeSetting = startSettings?.BatchSize,
            OcrQueueCapacity = startSettings?.QueueCapacity,
            OcrIntraOpThreads = startSettings?.IntraOpThreads,
            MaxItemsSetting = ParseMaxItems(lines),
            ProfileId = visualProfile.ProfileId,
            TrainingProfileId = visualProfile.TrainingProfileId,
            ProfileFamilyId = visualProfile.ProfileFamilyId,
            ProfileGeometryStatus = visualProfile.ProfileGeometryStatus,
            RequestedProfileId = visualProfile.RequestedProfileId,
            DetectedProfileId = visualProfile.DetectedProfileId,
            ProfileDetectedGeometry = visualProfile.GeometryKey,
            ProfileRoute = ParseProfileRoute(lines),
            FastAcceptByProfileFamily = ParseFastAcceptByProfileFamily(fastAssistRows),
            FastExactProfileAcceptCount = CountFastExactProfileAccepts(fastAssistRows),
            HealthFallbackCount = lines.Count(line => line.Contains("PROFILE_HEALTH_DEGRADED", StringComparison.Ordinal)),
            CanonicalCropSucceededCount = CountBool(fastAssistRows, "canonical_crop_succeeded", expected: true),
            CanonicalCropFallbackCount = CountBool(fastAssistRows, "canonical_crop_fallback", expected: true),
            CanonicalCropDecisionCount = CountRowsWithColumn(fastAssistRows, "canonical_crop_fallback"),
            Traversal = traversal,
            RowAdvanceMode = ParseRowAdvanceMode(lines),
            ExportItemCount = exportStats.ItemCount,
            ExportDuplicateGroupCount = exportStats.DuplicateGroupCount,
            ExportDuplicateItemCount = exportStats.DuplicateItemCount,
            ExportVerifiedAdjacentDuplicateCount = exportStats.VerifiedAdjacentDuplicateCount,
            ExportUnverifiedDuplicateCount = exportStats.UnverifiedDuplicateCount,
            SlotOutOfRangeCount = exportStats.SlotOutOfRangeCount,
            SlotMainStatViolationCount = exportStats.SlotMainStatViolationCount,
            SlotFixedValueViolationCount = exportStats.SlotFixedValueViolationCount,
            ErrorFileCount = Directory.EnumerateFiles(scanDirectory, "*.error.txt").Count(),
            Non15FileCount = non15FileCount,
            CellTimingCount = cellTimings.Count,
            CellTimingFallbackCount = cellTimings.Count(x => x.Fallback),
            CellTimingFallbackLogCount = lines.Count(line => line.Contains("Panel probes stayed unchanged", StringComparison.Ordinal)),
            SelectionOnlyAcceptCount = lines.Count(line => line.Contains("accept=selection_changed_stable_full_roi", StringComparison.Ordinal)),
            PostScrollSelectionOnlyBlockedCount = events.Count(x => x.Kind == "PANEL_SELECTION_ONLY_BLOCKED"
                && x.Detail.Contains("post_scroll_panel_change_required=True", StringComparison.OrdinalIgnoreCase)),
            WeakPanelChangeBlockedCount = events.Count(x => x.Kind == "PANEL_WEAK_CHANGE_BLOCKED"),
            PanelStablePanelCount = cellTimings.Count(x => string.Equals(x.StableSource, "panel", StringComparison.OrdinalIgnoreCase)),
            PanelStableTextCoreCount = cellTimings.Count(x => string.Equals(x.StableSource, "text-core", StringComparison.OrdinalIgnoreCase)),
            VisualRow2ClickCount = clickPositions.Count(x => x.VisualRow == 2),
            UnsafeVisualRow2ClickCount = clickPositions.Count(x => x.VisualRow == 2 && !IsAllowedVisualRow2Click(x, overlapMode)),
            OverlapViewportCount = events.Count(x => x.Kind == "OVERLAP_VIEWPORT"),
            OverlapRowScannedCount = events.Count(x => x.Kind == "OVERLAP_ROW_SCANNED"),
            OverlapScrollAcceptedCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_ACCEPTED"),
            OverlapConflictCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_MISMATCH"),
            OverlapConflictRecheckCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_RECHECK_FRAME"),
            OverlapConflictRecoveredCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_RECHECK_RECOVERED"),
            OverlapAmbiguousAcceptCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_AMBIGUOUS_ACCEPTED"),
            OverlapConfirmedTwoRowAcceptCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_TWO_ROW_COVERAGE_ACCEPTED"),
            OverlapHardStopCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_CONFLICT_HARD_STOP"),
            TotalLogicalRows = overlapSummary.TotalRows,
            OverlapScannedLogicalRowsCount = overlapSummary.ScannedRows,
            OverlapMissingLogicalRowsCount = overlapSummary.MissingRows,
            FullScanComplete = overlapSummary.FullScanComplete,
            RowScrollOvershotCount = events.Count(x => x.Kind is "ROW_SCROLL_OVERSHOT" or "ROW_SCROLL_OVERSHOT_BLOCKED"),
            RowScrollRecoveryAcceptedCount = events.Count(x => x.Kind == "ROW_SCROLL_RECOVERY_ACCEPTED"),
            RowScrollRecoveryFailCount = events.Count(x => x.Kind == "ROW_SCROLL_RECOVERY_FAIL"),
            RowScrollStrictStopCount = events.Count(x => x.Kind == "ROW_SCROLL_STRICT_STOP"),
            RowScrollNoMoveCount = events.Count(x => x.Kind == "ROW_SCROLL_NO_MOVE_CONFIRMED"),
            RowScrollAmbiguousCount = events.Count(x => x.Kind == "ROW_SCROLL_AMBIGUOUS_STOP"),
            RowScrollSettleStartCount = events.Count(x => x.Kind == "ROW_SCROLL_SETTLE_START"),
            RowScrollSettleAcceptCount = events.Count(x => x.Kind == "ROW_SCROLL_SETTLE_ACCEPT"),
            RowScrollSettleTimeoutCount = events.Count(x => x.Kind == "ROW_SCROLL_SETTLE_TIMEOUT"),
            RowScrollPartialMoveContinueCount = events.Count(x => x.Kind == "ROW_SCROLL_PARTIAL_MOVE_CONTINUE"),
            RowScrollFalseAdvanceCount = events.Count(x =>
                x.Kind is "ROW_SCROLL_DONE" or "ROW_SCROLL_RECOVERED" or "OVERLAP_SCROLL_ACCEPTED"
                && (x.Detail.Contains("decision=NoMove", StringComparison.OrdinalIgnoreCase)
                    || x.Detail.Contains("decision=Ambiguous", StringComparison.OrdinalIgnoreCase)
                    || x.Detail.Contains("rowsAdvanced=0", StringComparison.OrdinalIgnoreCase))),
            NonUnitRowScrollDoneCount = events.Count(x => x.Kind == "ROW_SCROLL_DONE"
                && (!TryReadEventInt(x, "rowsAdvanced", out var rowsAdvanced) || rowsAdvanced != 1)),
            PanelTargetEvidenceCount = events.Count(x => x.Kind == "PANEL_TARGET_EVIDENCE"),
            PanelNeighborRoundTripCount = events.Count(x => x.Kind == "PANEL_NEIGHBOR_ROUNDTRIP"
                && x.Detail.Contains("phase=target_ready", StringComparison.OrdinalIgnoreCase)),
            TargetVerificationEventCount = events.Count(x => x.Kind == "ITEM_TARGET_VERIFICATION"),
            TargetVerificationUnknownCount = events.Count(x => x.Kind == "ITEM_TARGET_VERIFICATION"
                && !x.Detail.Contains("kind=ChangedText", StringComparison.OrdinalIgnoreCase)
                && !x.Detail.Contains("kind=IdenticalNeighborRoundTrip", StringComparison.OrdinalIgnoreCase)),
            TargetSelectionEvidenceMissingCount = cellTimings.Count(x => x.TargetSelectionStableFrames is null),
            TargetSelectionUnderStableCount = cellTimings.Count(x => x.TargetSelectionStableFrames is < 2),
            OcrDrainStartCount = events.Count(x => x.Kind == "OCR_DRAIN_START"),
            OcrDrainDoneCount = events.Count(x => x.Kind == "OCR_DRAIN_DONE"),
            OcrDrainRemainingCount = SumEventInt(events, "OCR_DRAIN_DONE", "remaining"),
            OcrDrainDiscardedCount = SumEventInt(events, "OCR_DRAIN_DONE", "discarded"),
            EdgeClickBlockedCount = events.Count(x => x.Kind == "EDGE_CLICK_BLOCKED"),
            NativeEdgeClickStartCount = events.Count(x => x.Kind == "EDGE_CLICK_START"),
            NativeEdgeClickSettledCount = events.Count(x => x.Kind == "EDGE_CLICK_SETTLED"),
            NativeEdgeClickChangedCount = events.Count(x => x.Kind == "EDGE_CLICK_CHANGED"),
            NativeEdgeClickHashFallbackCount = events.Count(x => x.Kind == "EDGE_CLICK_HASH_FALLBACK"),
            NativeEdgeClickBottomCount = events.Count(x => x.Kind == "EDGE_CLICK_BOTTOM_DETECTED"),
            NativeEdgeClickStopCount = events.Count(x => x.Kind == "EDGE_CLICK_STOP"),
            NativeEdgeClickBufferCommitCount = events.Count(x => x.Kind == "EDGE_CLICK_BUFFER_COMMIT"),
            NativeEdgeWheelTickConflictCount = string.Equals(ParseRowAdvanceMode(lines), "NativeEdgeClick", StringComparison.OrdinalIgnoreCase)
                ? events.Count(x => x.Kind == "ROW_SCROLL_TICK")
                : 0,
            MinVisibleRois = cellTimings.Count > 0 ? cellTimings.Min(x => x.VisibleRois) : null,
            IncompleteRoiCount = cellTimings.Count(x => !IsCompleteVariableRoiLayout(x.VisibleRois, x.TotalRois))
                + invalidRoiTimeoutCount,
            LastVisited = terminal?.Counters.Visited ?? scanOnceCounters?.Visited ?? lastCounters?.Visited ?? resourceCounters?.Visited,
            LastQueued = terminal?.Counters.Queued ?? scanOnceCounters?.Queued ?? lastCounters?.Queued ?? resourceCounters?.Queued,
            LastCompleted = terminal?.Counters.Completed ?? scanOnceCounters?.Completed ?? lastCounters?.Completed ?? resourceCounters?.Completed,
            LastFailed = terminal?.Counters.Failed ?? scanOnceCounters?.Failed ?? lastCounters?.Failed ?? resourceCounters?.Failed,
            ClickSameRow = sameRowClick,
            ClickAfterScroll = afterScrollClick,
            AfterScrollExtra = Stats.From(sameRowClick.Average is null
                ? Enumerable.Empty<double>()
                : intervals.Where(x => x.AfterScroll).Select(x => Math.Max(0, x.Milliseconds - sameRowClick.Average.Value))),
            ClickAll = Stats.From(intervals.Select(x => x.Milliseconds)),
            ScrollDuration = Stats.From(scrollDurations),
            PanelWait = Stats.From(cellTimings.Select(x => x.PanelWaitMs)),
            SameRowPanelWait = Stats.From(cellTimings.Where(x => !x.AfterScroll).Select(x => x.PanelWaitMs)),
            PostScrollFirstPanelWait = Stats.From(cellTimings.Where(x => x.PostScrollFirstCell).Select(x => x.PanelWaitMs)),
            PostScrollFirstCellTotal = Stats.From(cellTimings.Where(x => x.PostScrollFirstCell).Select(x => x.TotalMs)),
            PanelFrames = Stats.From(cellTimings.Where(x => x.PanelFrames is not null).Select(x => x.PanelFrames!.Value)),
            PanelFramesAfterWarmup = Stats.From(cellTimings.Where(x => x.PanelFrames is not null && x.AdaptivePanelMinMs is not null).Skip(AdaptiveTimingState.DefaultWarmupItems).Select(x => x.PanelFrames!.Value)),
            PanelChange = Stats.From(cellTimings.Where(x => x.ChangeMs is not null).Select(x => x.ChangeMs!.Value)),
            SelectionChange = Stats.From(cellTimings.Where(x => x.SelectionChangeMs is not null).Select(x => x.SelectionChangeMs!.Value)),
            PanelFullRoi = Stats.From(cellTimings.Where(x => x.FullRoiMs is not null).Select(x => x.FullRoiMs!.Value)),
            RoiCompleteFrames = Stats.From(cellTimings.Where(x => x.RoiCompleteFrames is not null).Select(x => x.RoiCompleteFrames!.Value)),
            SelectedStableFrames = Stats.From(cellTimings.Where(x => x.SelectedStableFrames is not null).Select(x => x.SelectedStableFrames!.Value)),
            PanelStable = Stats.From(cellTimings.Where(x => x.StableMs is not null).Select(x => x.StableMs!.Value)),
            PanelTextStable = Stats.From(cellTimings.Where(x => x.TextStableMs is not null).Select(x => x.TextStableMs!.Value)),
            TargetSelectionStableFrames = Stats.From(cellTimings.Where(x => x.TargetSelectionStableFrames is not null).Select(x => x.TargetSelectionStableFrames!.Value)),
            RarityProbe = Stats.From(cellTimings.Where(x => x.RarityProbeMs is not null).Select(x => x.RarityProbeMs!.Value)),
            SelectionProbe = Stats.From(cellTimings.Where(x => x.SelectionProbeMs is not null).Select(x => x.SelectionProbeMs!.Value)),
            PanelCapture = Stats.From(cellTimings.Where(x => x.CaptureMs is not null).Select(x => x.CaptureMs!.Value)),
            PanelSignature = Stats.From(cellTimings.Where(x => x.SignatureMs is not null).Select(x => x.SignatureMs!.Value)),
            VisibleRoi = Stats.From(cellTimings.Where(x => x.VisibleRoiMs is not null).Select(x => x.VisibleRoiMs!.Value)),
            FrameLoop = Stats.From(cellTimings.Where(x => x.FrameLoopMs is not null).Select(x => x.FrameLoopMs!.Value)),
            FrameToBitmap = Stats.From(cellTimings.Where(x => x.FrameToBitmapMs is not null).Select(x => x.FrameToBitmapMs!.Value)),
            BitmapCreatedCount = Stats.From(cellTimings.Where(x => x.BitmapCreatedCount is not null).Select(x => x.BitmapCreatedCount!.Value)),
            AdaptiveThrottle = Stats.From(cellTimings.Where(x => x.AdaptiveThrottleMs is not null).Select(x => x.AdaptiveThrottleMs!.Value)),
            OcrBacklogBeforeEnqueue = Stats.From(cellTimings.Where(x => x.OcrBacklogBeforeEnqueue is not null).Select(x => x.OcrBacklogBeforeEnqueue!.Value)),
            AdaptivePanelMin = Stats.From(cellTimings.Where(x => x.AdaptivePanelMinMs is not null).Select(x => x.AdaptivePanelMinMs!.Value)),
            PanelMinAcceptFloor = Stats.From(cellTimings.Where(x => x.PanelMinFloorMs is not null).Select(x => x.PanelMinFloorMs!.Value)),
            SameRowPanelFloor = Stats.From(cellTimings.Where(x => x.SameRowPanelFloorMs is not null).Select(x => x.SameRowPanelFloorMs!.Value)),
            PostScrollPanelFloor = Stats.From(cellTimings.Where(x => x.PostScrollPanelFloorMs is not null).Select(x => x.PostScrollPanelFloorMs!.Value)),
            FloorWaitLimited = Stats.From(cellTimings.Where(x => x.FloorWaitLimitedMs is not null).Select(x => x.FloorWaitLimitedMs!.Value)),
            PanelAcceptElapsedVsFloor = Stats.From(cellTimings.Where(x => x.PanelAcceptElapsedVsFloorMs is not null).Select(x => x.PanelAcceptElapsedVsFloorMs!.Value)),
            ScrollTickDelay = Stats.From(cellTimings.Where(x => x.ScrollTickDelayMs is not null).Select(x => x.ScrollTickDelayMs!.Value)),
            ScrollTickWait = Stats.From(scrollTimings.Select(x => x.ScrollTickWaitMs)),
            ScrollListStable = Stats.From(scrollTimings.Select(x => x.ListStableMs)),
            RowScrollSettle = Stats.From(scrollTimings.Where(x => x.SettleMs is not null).Select(x => x.SettleMs!.Value)),
            RowSignature = Stats.From(scrollTimings.Select(x => x.RowSignatureMs)),
            PostScrollViewport = Stats.From(scrollTimings.Select(x => x.PostScrollViewportMs)),
            CellTotal = Stats.From(cellTimings.Select(x => x.TotalMs)),
            EnqueueWait = Stats.From(cellTimings.Select(x => x.EnqueueWaitMs)),
            FallbackPanelWait = Stats.From(cellTimings.Where(x => x.Fallback).Select(x => x.PanelWaitMs)),
            NormalPanelWait = Stats.From(cellTimings.Where(x => !x.Fallback).Select(x => x.PanelWaitMs)),
            OcrBatchSize = Stats.From(ReadColumn(ocrRows, "batch_size")),
            OcrBitmapToMat = Stats.From(ReadColumn(ocrRows, "bitmap_to_mat_ms")),
            OcrPreprocess = Stats.From(ReadColumn(ocrRows, "preprocess_ms")),
            OcrInference = Stats.From(ReadColumn(ocrRows, "inference_ms")),
            OcrDecode = Stats.From(ReadColumn(ocrRows, "decode_ms")),
            OcrTotal = Stats.From(ReadColumn(ocrRows, "total_ms")),
            OcrTotalPerItem = Stats.From(ocrPerItemTotal),
            OcrClean = Stats.From(ReadColumn(ocrRows, "clean_ms")),
            OcrBacklog = Stats.From(ReadColumn(ocrRows, "queued_completed_backlog")),
            FastMatchMsPerItem = Stats.From(fastMatchMsPerItem),
            FastAcceptedPerItem = Stats.From(fastAcceptedPerItem),
            FastRejectedPerItem = Stats.From(fastRejectedPerItem),
            PpOcrRoiPerItem = Stats.From(ppOcrRoiPerItem),
            FastOcrFeatureMs = Stats.From(ReadColumn(fastAssistRows, "feature_ms")),
            ScannerCpu = Stats.From(ReadColumn(resourceRows, "scanner_cpu_percent")),
            ResourceBacklog = Stats.From(ReadColumn(resourceRows, "ocr_backlog"))
        };
        report.FullScanExpected = report.MaxItemsSetting == 0
            && string.Equals(report.Traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase);
        report.EffectiveFullScanComplete = report.FullScanComplete
            || (report.FullScanExpected
                && report.Partial == true
                && string.Equals(report.TerminationCode, "non_level_15_stop", StringComparison.OrdinalIgnoreCase)
                && report.Non15FileCount > 0
                && report.LastFailed == 0
                && ExportMatchesCompleted(report) is not false
                && report.OverlapHardStopCount == 0
                && report.RowScrollFalseAdvanceCount == 0);
        report.QuickAcceptCount = cellTimings.Count(x => x.QuickAccept == true);
        report.QuickRejectCount = cellTimings.Count(x => x.QuickAccept == false);
        report.AcceptGateReasons = cellTimings
            .Where(x => !string.IsNullOrWhiteSpace(x.AcceptGateReason))
            .GroupBy(x => x.AcceptGateReason!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        report.PostScrollAdaptiveAcceptCount = cellTimings.Count(x => string.Equals(x.PostScrollAcceptMode, "AdaptiveAfterScroll", StringComparison.OrdinalIgnoreCase));
        report.PostScrollSafeAcceptCount = cellTimings.Count(x => string.Equals(x.PostScrollAcceptMode, "Safe", StringComparison.OrdinalIgnoreCase));
        report.BeforeMinAcceptGateCount = cellTimings.Count(x => string.Equals(x.AcceptGateReason, "before_min_accept", StringComparison.OrdinalIgnoreCase));
        report.FloorWaitLimitedCount = cellTimings.Count(x => x.FloorWaitLimitedMs is > 0.5);
        report.PanelFloorModes = cellTimings
            .Where(x => !string.IsNullOrWhiteSpace(x.PanelFloorMode))
            .GroupBy(x => x.PanelFloorMode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        report.DurationSeconds = report.StartTime is not null && report.EndTime is not null
            ? Math.Max(0, (report.EndTime.Value - report.StartTime.Value).TotalSeconds)
            : null;
        report.CaptureDurationSeconds = report.StartTime is not null && report.CaptureEndTime is not null
            ? Math.Max(0, (report.CaptureEndTime.Value - report.StartTime.Value).TotalSeconds)
            : null;
        report.CellTimingPerSecond = Rate(report.CellTimingCount, report.DurationSeconds);
        report.CaptureCellTimingPerSecond = Rate(report.CellTimingCount, report.CaptureDurationSeconds);
        report.CaptureQueuedPerSecond = Rate(report.LastQueued, report.CaptureDurationSeconds);
        report.CompletedPerSecond = Rate(report.LastCompleted, report.DurationSeconds);
        report.QueuedPerSecond = Rate(report.LastQueued, report.DurationSeconds);
        report.CaptureLimited = IsCaptureLimited(report);

        return report;
    }

    private static List<ScanEvent> ParseEvents(IEnumerable<string> lines)
    {
        var events = new List<ScanEvent>();
        foreach (var line in lines)
        {
            var match = EventRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
            {
                continue;
            }

            events.Add(new ScanEvent(timestamp, match.Groups["kind"].Value, match.Groups["detail"].Value));
        }

        return events;
    }

    private static int SumEventInt(IReadOnlyList<ScanEvent> events, string kind, string key)
    {
        var regex = new Regex($@"(?:^|,\s*){Regex.Escape(key)}=(?<value>\d+)(?:,|$)", RegexOptions.IgnoreCase);
        return events
            .Where(item => string.Equals(item.Kind, kind, StringComparison.Ordinal))
            .Select(item => regex.Match(item.Detail))
            .Where(match => match.Success)
            .Sum(match => ParseInt(match.Groups["value"].Value));
    }

    private static string ParseRowAdvanceMode(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = RowAdvanceModeRegex.Match(line);
            if (match.Success)
            {
                return match.Groups["mode"].Value;
            }
        }

        return "unknown";
    }

    private static bool TryReadEventInt(ScanEvent item, string key, out int value)
    {
        var match = Regex.Match(
            item.Detail,
            $@"(?:^|,\s*){Regex.Escape(key)}=(?<value>-?\d+)(?:,|$)",
            RegexOptions.IgnoreCase);
        value = match.Success ? ParseInt(match.Groups["value"].Value) : 0;
        return match.Success;
    }

    private static List<CellTiming> ParseCellTimings(IEnumerable<string> lines)
    {
        var timings = new List<CellTiming>();
        foreach (var line in lines)
        {
            var match = CellTimingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            timings.Add(new CellTiming(
                ParseInt(match.Groups["index"].Value),
                ParseDouble(match.Groups["panel"].Value),
                ParseDouble(match.Groups["enqueue"].Value),
                bool.Parse(match.Groups["fallback"].Value),
                ParseInt(match.Groups["visible"].Value),
                ParseInt(match.Groups["total"].Value),
                ParseDouble(match.Groups["cellTotal"].Value),
                ParseOptionalBool(match.Groups["afterScroll"].Value) == true,
                ParseOptionalBool(match.Groups["postScrollFirst"].Value) == true,
                ParseOptionalDouble(match.Groups["frames"].Value),
                ParseOptionalDouble(match.Groups["change"].Value),
                ParseOptionalDouble(match.Groups["selection"].Value),
                ParseOptionalDouble(match.Groups["fullRoi"].Value),
                ParseOptionalDouble(match.Groups["stable"].Value),
                ParseOptionalDouble(match.Groups["textStable"].Value),
                match.Groups["stableSource"].Success ? match.Groups["stableSource"].Value : null,
                match.Groups["stabilityReason"].Success ? match.Groups["stabilityReason"].Value : null,
                ParseOptionalDouble(match.Groups["targetSelectionStableFrames"].Value),
                match.Groups["targetVerificationKind"].Success ? match.Groups["targetVerificationKind"].Value : null,
                ParseOptionalDouble(match.Groups["rarityProbe"].Value),
                ParseOptionalDouble(match.Groups["selectionProbe"].Value),
                ParseOptionalDouble(match.Groups["capture"].Value),
                ParseOptionalDouble(match.Groups["signature"].Value),
                ParseOptionalDouble(match.Groups["visibleRoi"].Value),
                ParseOptionalDouble(match.Groups["frameLoop"].Value),
                ParseOptionalDouble(match.Groups["frameToBitmap"].Value),
                ParseOptionalDouble(match.Groups["bitmapCreated"].Value),
                ParseOptionalBool(match.Groups["quickAccept"].Value),
                match.Groups["quickReject"].Success ? match.Groups["quickReject"].Value : null,
                ParseOptionalDouble(match.Groups["throttle"].Value),
                ParseOptionalDouble(match.Groups["backlog"].Value),
                ParseOptionalDouble(match.Groups["panelMin"].Value),
                match.Groups["panelAcceptMode"].Success ? match.Groups["panelAcceptMode"].Value : null,
                match.Groups["postScrollAcceptMode"].Success ? match.Groups["postScrollAcceptMode"].Value : null,
                ParseOptionalDouble(match.Groups["panelMinFloor"].Value),
                ParseOptionalDouble(match.Groups["roiCompleteFrames"].Value),
                ParseOptionalDouble(match.Groups["selectedStableFrames"].Value),
                match.Groups["acceptGateReason"].Success ? match.Groups["acceptGateReason"].Value : null,
                match.Groups["panelFloorMode"].Success ? match.Groups["panelFloorMode"].Value : null,
                ParseOptionalDouble(match.Groups["sameRowPanelFloor"].Value),
                ParseOptionalDouble(match.Groups["postScrollPanelFloor"].Value),
                match.Groups["panelFloorReason"].Success ? match.Groups["panelFloorReason"].Value : null,
                ParseOptionalDouble(match.Groups["floorWaitLimited"].Value),
                ParseOptionalDouble(match.Groups["acceptElapsedVsFloor"].Value),
                ParseOptionalDouble(match.Groups["scrollTickDelay"].Value)));
        }

        return timings;
    }

    private static List<ScrollTiming> ParseScrollTimings(IEnumerable<string> lines)
    {
        var timings = new List<ScrollTiming>();
        foreach (var line in lines)
        {
            var match = ScrollTimingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            timings.Add(new ScrollTiming(
                ParseDouble(match.Groups["tick"].Value),
                ParseDouble(match.Groups["stable"].Value),
                ParseDouble(match.Groups["row"].Value),
                ParseOptionalDouble(match.Groups["settle"].Value),
                ParseDouble(match.Groups["viewport"].Value)));
        }

        return timings;
    }
}
