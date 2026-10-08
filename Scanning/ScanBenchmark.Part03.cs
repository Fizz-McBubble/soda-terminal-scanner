using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private static string ExportFingerprint(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .Where(property => !string.Equals(property.Name, "序号", StringComparison.Ordinal))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}:{ExportFingerprint(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(ExportFingerprint)) + "]",
            JsonValueKind.String => "s:" + (element.GetString() ?? ""),
            JsonValueKind.Number => "n:" + element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private static CounterSnapshot? ParseLastCounters(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var match = CounterRegex.Match(lines[i]);
            if (match.Success)
            {
                return new CounterSnapshot(
                    ParseInt(match.Groups["visited"].Value),
                    ParseInt(match.Groups["queued"].Value),
                    ParseInt(match.Groups["completed"].Value),
                    ParseInt(match.Groups["failed"].Value));
            }
        }

        return null;
    }

    private static ScanTerminalSnapshot? ParseScanTerminal(IReadOnlyList<ScanEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(events[i].Kind, "SCAN_TERMINAL", StringComparison.Ordinal))
            {
                continue;
            }

            var match = TerminalDetailRegex.Match(events[i].Detail);
            if (!match.Success)
            {
                continue;
            }

            return new ScanTerminalSnapshot(
                new CounterSnapshot(
                    ParseInt(match.Groups["visited"].Value),
                    ParseInt(match.Groups["queued"].Value),
                    ParseInt(match.Groups["completed"].Value),
                    ParseInt(match.Groups["failed"].Value)),
                bool.Parse(match.Groups["partial"].Value),
                match.Groups["termination"].Value,
                match.Groups["export"].Value.Trim());
        }

        return null;
    }

    private static CounterSnapshot? ParseScanOnceCounters(string resultFile)
    {
        if (!File.Exists(resultFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
            var root = document.RootElement;
            return new CounterSnapshot(
                ReadJsonInt(root, "Visited"),
                ReadJsonInt(root, "Queued"),
                ReadJsonInt(root, "Completed"),
                ReadJsonInt(root, "Failed"));
        }
        catch
        {
            return null;
        }
    }

    private static int ReadJsonInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static CounterSnapshot? ParseResourceCounters(IReadOnlyList<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var last = rows[^1];
        return new CounterSnapshot(
            ReadInt(last, "visited"),
            ReadInt(last, "queued"),
            ReadInt(last, "completed"),
            ReadInt(last, "failed"));
    }

    private static string ParseProfileRoute(IEnumerable<string> lines)
    {
        var routes = lines
            .Where(line => line.Contains("FAST_OCR_PROFILE_ROUTE", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, @"route=(?<route>[^,\s]+)"))
            .Where(match => match.Success)
            .Select(match => match.Groups["route"].Value)
            .GroupBy(route => route, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{NormalizeMetricName(group.Key)}:{group.Count()}");
        var summary = string.Join("+", routes);
        return string.IsNullOrWhiteSpace(summary) ? "none" : summary;
    }

    private static string ParseFastAcceptByProfileFamily(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var groups = rows
            .Where(row => row.TryGetValue("source", out var source)
                && source.Equals("fast", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.TryGetValue("source_family_id", out var family) && !string.IsNullOrWhiteSpace(family)
                ? family
                : "unknown")
            .GroupBy(family => family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{NormalizeMetricName(group.Key)}:{group.Count()}");
        var summary = string.Join("+", groups);
        return string.IsNullOrWhiteSpace(summary) ? "none" : summary;
    }

    private static int CountRowsWithColumn(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        return rows.Count(row => row.ContainsKey(column));
    }

    private static int CountFastExactProfileAccepts(IReadOnlyList<Dictionary<string, string>> rows)
    {
        return rows.Count(row => row.TryGetValue("source", out var source)
            && source.Equals("fast", StringComparison.OrdinalIgnoreCase)
            && row.TryGetValue("reason", out var reason)
            && reason.StartsWith("profile_exact:", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountBool(IReadOnlyList<Dictionary<string, string>> rows, string column, bool expected)
    {
        return rows.Count(row => row.TryGetValue(column, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed == expected);
    }

    private static void WriteReport(string prefix, ScanReport report)
    {
        Write(prefix, "scan_dir", report.ScanDirectory);
        Write(prefix, "scan_name", Path.GetFileName(report.ScanDirectory));
        Write(prefix, "duration_sec", report.DurationSeconds);
        Write(prefix, "capture_duration_sec", report.CaptureDurationSeconds);
        Write(prefix, "cell_timing_per_sec", report.CellTimingPerSecond);
        Write(prefix, "capture_cell_timing_per_sec", report.CaptureCellTimingPerSecond);
        Write(prefix, "queued_per_sec", report.QueuedPerSecond);
        Write(prefix, "capture_queued_per_sec", report.CaptureQueuedPerSecond);
        Write(prefix, "completed_per_sec", report.CompletedPerSecond);
        Write(prefix, "capture_limited", report.CaptureLimited.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "stop_reason", report.StopReason);
        Write(prefix, "partial", report.Partial);
        Write(prefix, "termination_code", report.TerminationCode);
        Write(prefix, "export_file", report.ExportFileName);
        Write(prefix, "traversal", report.Traversal);
        Write(prefix, "row_advance_mode", report.RowAdvanceMode);
        Write(prefix, "ocr_workers", report.OcrWorkers);
        Write(prefix, "ocr_batch_setting", report.OcrBatchSizeSetting);
        Write(prefix, "ocr_queue_capacity", report.OcrQueueCapacity);
        Write(prefix, "ocr_intra_op_threads", report.OcrIntraOpThreads);
        Write(prefix, "max_items_setting", report.MaxItemsSetting);
        Write(prefix, "profile_id", report.ProfileId);
        Write(prefix, "training_profile_id", report.TrainingProfileId);
        Write(prefix, "profile_family_id", report.ProfileFamilyId);
        Write(prefix, "profile_geometry_status", report.ProfileGeometryStatus);
        Write(prefix, "profile_requested_id", report.RequestedProfileId);
        Write(prefix, "profile_detected_id", report.DetectedProfileId);
        Write(prefix, "profile_detected_geometry", report.ProfileDetectedGeometry);
        Write(prefix, "profile_route", report.ProfileRoute);
        Write(prefix, "fast_accept_by_profile_family", report.FastAcceptByProfileFamily);
        Write(prefix, "fast_exact_profile_accept_count", report.FastExactProfileAcceptCount);
        Write(prefix, "health_fallback_count", report.HealthFallbackCount);
        Write(prefix, "canonical_crop_succeeded_count", report.CanonicalCropSucceededCount);
        Write(prefix, "canonical_crop_fallback_count", report.CanonicalCropFallbackCount);
        Write(prefix, "canonical_crop_fallback_rate", PercentValue(report.CanonicalCropFallbackCount, report.CanonicalCropDecisionCount));
        Write(prefix, "export_items", report.ExportItemCount);
        Write(prefix, "export_matches_completed", ExportMatchesCompleted(report));
        Write(prefix, "export_duplicate_groups", report.ExportDuplicateGroupCount);
        Write(prefix, "export_duplicate_items", report.ExportDuplicateItemCount);
        Write(prefix, "export_verified_adjacent_duplicates", report.ExportVerifiedAdjacentDuplicateCount);
        Write(prefix, "export_unverified_duplicates", report.ExportUnverifiedDuplicateCount);
        Write(prefix, "slot_out_of_range_count", report.SlotOutOfRangeCount);
        Write(prefix, "slot_mainstat_violation_count", report.SlotMainStatViolationCount);
        Write(prefix, "slot_fixed_value_violation_count", report.SlotFixedValueViolationCount);
        Write(prefix, "slot_safety_pass", report.SlotSafetyPass.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "error_files", report.ErrorFileCount);
        Write(prefix, "non15_files", report.Non15FileCount);
        Write(prefix, "last_visited", report.LastVisited);
        Write(prefix, "last_queued", report.LastQueued);
        Write(prefix, "last_completed", report.LastCompleted);
        Write(prefix, "last_failed", report.LastFailed);
        Write(prefix, "cell_timing_count", report.CellTimingCount);
        Write(prefix, "fallback_count", report.EffectiveFallbackCount);
        Write(prefix, "fallback_rate_percent", Percent(report.EffectiveFallbackCount, Math.Max(report.CellTimingCount, report.ClickAll.Count)));
        Write(prefix, "selection_only_accept_count", report.SelectionOnlyAcceptCount);
        Write(prefix, "post_scroll_selection_only_blocked_count", report.PostScrollSelectionOnlyBlockedCount);
        Write(prefix, "weak_panel_change_blocked_count", report.WeakPanelChangeBlockedCount);
        Write(prefix, "quick_accept_count", report.QuickAcceptCount);
        Write(prefix, "quick_reject_count", report.QuickRejectCount);
        Write(prefix, "quick_accept_rate_percent", Percent(report.QuickAcceptCount, Math.Max(1, report.QuickAcceptCount + report.QuickRejectCount)));
        Write(prefix, "panel_stable_source_panel_count", report.PanelStablePanelCount);
        Write(prefix, "panel_stable_source_text_core_count", report.PanelStableTextCoreCount);
        Write(prefix, "panel_stable_text_core_rate_percent", Percent(report.PanelStableTextCoreCount, Math.Max(1, report.PanelStablePanelCount + report.PanelStableTextCoreCount)));
        Write(prefix, "post_scroll_adaptive_accept_count", report.PostScrollAdaptiveAcceptCount);
        Write(prefix, "post_scroll_safe_accept_count", report.PostScrollSafeAcceptCount);
        Write(prefix, "before_min_accept_count", report.BeforeMinAcceptGateCount);
        Write(prefix, "floor_wait_limited_count", report.FloorWaitLimitedCount);
        Write(prefix, "panel_floor_mode", string.Join("+", report.PanelFloorModes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{NormalizeMetricName(pair.Key)}:{pair.Value}")));
        Write(prefix, "visual_row2_clicks", report.VisualRow2ClickCount);
        Write(prefix, "unsafe_visual_row2_clicks", report.UnsafeVisualRow2ClickCount);
        Write(prefix, "overlap_viewport_count", report.OverlapViewportCount);
        Write(prefix, "overlap_row_scanned_count", report.OverlapRowScannedCount);
        Write(prefix, "overlap_scroll_accepted_count", report.OverlapScrollAcceptedCount);
        Write(prefix, "overlap_conflict_count", report.OverlapConflictCount);
        Write(prefix, "overlap_conflict_recheck_count", report.OverlapConflictRecheckCount);
        Write(prefix, "overlap_conflict_recovered_count", report.OverlapConflictRecoveredCount);
        Write(prefix, "overlap_ambiguous_accept_count", report.OverlapAmbiguousAcceptCount);
        Write(prefix, "overlap_confirmed_two_row_accept_count", report.OverlapConfirmedTwoRowAcceptCount);
        Write(prefix, "overlap_hard_stop_count", report.OverlapHardStopCount);
        Write(prefix, "full_scan_expected", report.FullScanExpected.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "full_scan_complete", report.FullScanComplete.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "effective_full_scan_complete", report.EffectiveFullScanComplete.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "missing_logical_rows_count", report.OverlapMissingLogicalRowsCount);
        Write(prefix, "scanned_logical_rows_count", report.OverlapScannedLogicalRowsCount);
        Write(prefix, "total_rows", report.TotalLogicalRows);
        Write(prefix, "row_scroll_overshot_count", report.RowScrollOvershotCount);
        Write(prefix, "row_scroll_recovery_accepted_count", report.RowScrollRecoveryAcceptedCount);
        Write(prefix, "row_scroll_recovery_fail_count", report.RowScrollRecoveryFailCount);
        Write(prefix, "row_scroll_strict_stop_count", report.RowScrollStrictStopCount);
        Write(prefix, "row_scroll_no_move_count", report.RowScrollNoMoveCount);
        Write(prefix, "row_scroll_ambiguous_count", report.RowScrollAmbiguousCount);
        Write(prefix, "row_scroll_settle_start_count", report.RowScrollSettleStartCount);
        Write(prefix, "row_scroll_settle_accept_count", report.RowScrollSettleAcceptCount);
        Write(prefix, "row_scroll_settle_timeout_count", report.RowScrollSettleTimeoutCount);
        Write(prefix, "row_scroll_partial_move_continue_count", report.RowScrollPartialMoveContinueCount);
        Write(prefix, "row_scroll_false_advance_count", report.RowScrollFalseAdvanceCount);
        Write(prefix, "row_scroll_non_unit_done_count", report.NonUnitRowScrollDoneCount);
        Write(prefix, "panel_target_evidence_count", report.PanelTargetEvidenceCount);
        Write(prefix, "panel_neighbor_roundtrip_count", report.PanelNeighborRoundTripCount);
        Write(prefix, "target_verification_event_count", report.TargetVerificationEventCount);
        Write(prefix, "target_verification_unknown_count", report.TargetVerificationUnknownCount);
        Write(prefix, "target_selection_evidence_missing_count", report.TargetSelectionEvidenceMissingCount);
        Write(prefix, "target_selection_under_stable_count", report.TargetSelectionUnderStableCount);
        Write(prefix, "ocr_drain_start_count", report.OcrDrainStartCount);
        Write(prefix, "ocr_drain_done_count", report.OcrDrainDoneCount);
        Write(prefix, "ocr_drain_remaining_count", report.OcrDrainRemainingCount);
        Write(prefix, "ocr_drain_discarded_count", report.OcrDrainDiscardedCount);
        Write(prefix, "edge_click_blocked_count", report.EdgeClickBlockedCount);
        Write(prefix, "native_edge_click_start_count", report.NativeEdgeClickStartCount);
        Write(prefix, "native_edge_click_settled_count", report.NativeEdgeClickSettledCount);
        Write(prefix, "native_edge_click_changed_count", report.NativeEdgeClickChangedCount);
        Write(prefix, "native_edge_click_hash_fallback_count", report.NativeEdgeClickHashFallbackCount);
        Write(prefix, "native_edge_click_bottom_count", report.NativeEdgeClickBottomCount);
        Write(prefix, "native_edge_click_stop_count", report.NativeEdgeClickStopCount);
        Write(prefix, "native_edge_click_buffer_commit_count", report.NativeEdgeClickBufferCommitCount);
        Write(prefix, "native_edge_wheel_tick_conflict_count", report.NativeEdgeWheelTickConflictCount);
        Write(prefix, "min_visible_rois", report.MinVisibleRois);
        Write(prefix, "incomplete_roi_count", report.IncompleteRoiCount);

        WriteStats(prefix, "same_row_click_ms", report.ClickSameRow);
        WriteStats(prefix, "after_scroll_click_ms", report.ClickAfterScroll);
        WriteStats(prefix, "after_scroll_extra_ms", report.AfterScrollExtra);
        WriteStats(prefix, "all_click_ms", report.ClickAll);
        WriteStats(prefix, "scroll_ms", report.ScrollDuration);
        WriteStats(prefix, "panel_wait_ms", report.PanelWait);
        WriteStats(prefix, "same_row_panel_wait_ms", report.SameRowPanelWait);
        WriteStats(prefix, "post_scroll_first_panel_wait_ms", report.PostScrollFirstPanelWait);
        WriteStats(prefix, "post_scroll_first_cell_total_ms", report.PostScrollFirstCellTotal);
        WriteStats(prefix, "panel_frames", report.PanelFrames);
        WriteStats(prefix, "panel_frames_after_warmup", report.PanelFramesAfterWarmup);
        WriteStats(prefix, "panel_change_ms", report.PanelChange);
        WriteStats(prefix, "selection_change_ms", report.SelectionChange);
        WriteStats(prefix, "panel_roi_ms", report.PanelFullRoi);
        WriteStats(prefix, "roi_complete_frames", report.RoiCompleteFrames);
        WriteStats(prefix, "selected_stable_frames", report.SelectedStableFrames);
        WriteStats(prefix, "panel_stable_ms", report.PanelStable);
        WriteStats(prefix, "panel_text_stable_ms", report.PanelTextStable);
        WriteStats(prefix, "target_selection_stable_frames", report.TargetSelectionStableFrames);
        WriteStats(prefix, "rarity_probe_ms", report.RarityProbe);
        WriteStats(prefix, "selection_probe_ms", report.SelectionProbe);
        WriteStats(prefix, "capture_ms", report.PanelCapture);
        WriteStats(prefix, "frame_capture_ms", report.PanelCapture);
        WriteStats(prefix, "panel_signature_ms", report.PanelSignature);
        WriteStats(prefix, "visible_roi_ms", report.VisibleRoi);
        WriteStats(prefix, "frame_loop_ms", report.FrameLoop);
        WriteStats(prefix, "frame_to_bitmap_ms", report.FrameToBitmap);
        WriteStats(prefix, "bitmap_created_count", report.BitmapCreatedCount);
        WriteStats(prefix, "adaptive_throttle_ms", report.AdaptiveThrottle);
        WriteStats(prefix, "ocr_backlog_before_enqueue", report.OcrBacklogBeforeEnqueue);
        WriteStats(prefix, "adaptive_panel_min_ms", report.AdaptivePanelMin);
        WriteStats(prefix, "panel_min_floor_ms", report.PanelMinAcceptFloor);
        WriteStats(prefix, "same_row_panel_floor_ms", report.SameRowPanelFloor);
        WriteStats(prefix, "post_scroll_panel_floor_ms", report.PostScrollPanelFloor);
        WriteStats(prefix, "floor_wait_limited_ms", report.FloorWaitLimited);
        WriteStats(prefix, "panel_accept_elapsed_vs_floor_ms", report.PanelAcceptElapsedVsFloor);
        WriteStats(prefix, "scroll_tick_delay_ms", report.ScrollTickDelay);
        WriteStats(prefix, "scroll_tick_wait_ms", report.ScrollTickWait);
        WriteStats(prefix, "scroll_list_stable_ms", report.ScrollListStable);
        WriteStats(prefix, "row_scroll_settle_ms", report.RowScrollSettle);
        WriteStats(prefix, "row_signature_ms", report.RowSignature);
        WriteStats(prefix, "post_scroll_viewport_ms", report.PostScrollViewport);
        WriteStats(prefix, "cell_total_ms", report.CellTotal);
        WriteStats(prefix, "enqueue_wait_ms", report.EnqueueWait);
        WriteStats(prefix, "fallback_panel_wait_ms", report.FallbackPanelWait);
        WriteStats(prefix, "normal_panel_wait_ms", report.NormalPanelWait);
        WriteStats(prefix, "ocr_batch_size", report.OcrBatchSize);
        WriteStats(prefix, "ocr_bitmap_to_mat_ms", report.OcrBitmapToMat);
        WriteStats(prefix, "ocr_preprocess_ms", report.OcrPreprocess);
        WriteStats(prefix, "ocr_inference_ms", report.OcrInference);
        WriteStats(prefix, "ocr_decode_ms", report.OcrDecode);
        WriteStats(prefix, "ocr_total_ms", report.OcrTotal);
        WriteStats(prefix, "ocr_total_ms_per_item", report.OcrTotalPerItem);
        WriteStats(prefix, "ocr_clean_ms", report.OcrClean);
        WriteStats(prefix, "ocr_backlog", report.OcrBacklog);
        WriteStats(prefix, "fast_match_ms_per_item", report.FastMatchMsPerItem);
        WriteStats(prefix, "fast_accepted_per_item", report.FastAcceptedPerItem);
        WriteStats(prefix, "fast_rejected_per_item", report.FastRejectedPerItem);
        WriteStats(prefix, "ppocr_roi_per_item", report.PpOcrRoiPerItem);
        WriteStats(prefix, "v6_feature_ms", report.FastOcrFeatureMs);
        WriteStats(prefix, "scanner_cpu_percent", report.ScannerCpu);
        WriteStats(prefix, "resource_ocr_backlog", report.ResourceBacklog);
        foreach (var (reason, count) in report.AcceptGateReasons.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Write(prefix, $"accept_gate_reason_{NormalizeMetricName(reason)}_count", count);
        }
    }

    private static void WriteDeltaReport(ScanReport current, ScanReport baseline)
    {
        WriteDelta("same_row_click_delta_percent", current.ClickSameRow.Average, baseline.ClickSameRow.Average);
        WriteDelta("after_scroll_click_delta_percent", current.ClickAfterScroll.Average, baseline.ClickAfterScroll.Average);
        WriteDelta("after_scroll_extra_delta_percent", current.AfterScrollExtra.Average, baseline.AfterScrollExtra.Average);
        WriteDelta("all_click_delta_percent", current.ClickAll.Average, baseline.ClickAll.Average);
        WriteDelta("cell_timing_per_sec_delta_percent", current.CellTimingPerSecond, baseline.CellTimingPerSecond);
        WriteDelta("capture_cell_timing_per_sec_delta_percent", current.CaptureCellTimingPerSecond, baseline.CaptureCellTimingPerSecond);
        WriteDelta("queued_per_sec_delta_percent", current.QueuedPerSecond, baseline.QueuedPerSecond);
        WriteDelta("capture_queued_per_sec_delta_percent", current.CaptureQueuedPerSecond, baseline.CaptureQueuedPerSecond);
        WriteDelta("panel_wait_delta_percent", current.PanelWait.Average, baseline.PanelWait.Average);
        WriteDelta("same_row_panel_wait_delta_percent", current.SameRowPanelWait.Average, baseline.SameRowPanelWait.Average);
        WriteDelta("post_scroll_first_panel_wait_delta_percent", current.PostScrollFirstPanelWait.Average, baseline.PostScrollFirstPanelWait.Average);
        WriteDelta("post_scroll_first_cell_total_delta_percent", current.PostScrollFirstCellTotal.Average, baseline.PostScrollFirstCellTotal.Average);
        WriteDelta("panel_frames_delta_percent", current.PanelFrames.Average, baseline.PanelFrames.Average);
        WriteDelta("panel_frames_after_warmup_delta_percent", current.PanelFramesAfterWarmup.Average, baseline.PanelFramesAfterWarmup.Average);
        WriteDelta("panel_change_delta_percent", current.PanelChange.Average, baseline.PanelChange.Average);
        WriteDelta("selection_change_delta_percent", current.SelectionChange.Average, baseline.SelectionChange.Average);
        WriteDelta("panel_roi_delta_percent", current.PanelFullRoi.Average, baseline.PanelFullRoi.Average);
        WriteDelta("roi_complete_frames_delta_percent", current.RoiCompleteFrames.Average, baseline.RoiCompleteFrames.Average);
        WriteDelta("selected_stable_frames_delta_percent", current.SelectedStableFrames.Average, baseline.SelectedStableFrames.Average);
        WriteDelta("panel_stable_delta_percent", current.PanelStable.Average, baseline.PanelStable.Average);
        WriteDelta("panel_text_stable_delta_percent", current.PanelTextStable.Average, baseline.PanelTextStable.Average);
        WriteDelta("rarity_probe_delta_percent", current.RarityProbe.Average, baseline.RarityProbe.Average);
        WriteDelta("selection_probe_delta_percent", current.SelectionProbe.Average, baseline.SelectionProbe.Average);
        WriteDelta("capture_ms_delta_percent", current.PanelCapture.Average, baseline.PanelCapture.Average);
        WriteDelta("frame_to_bitmap_ms_delta_percent", current.FrameToBitmap.Average, baseline.FrameToBitmap.Average);
        WriteDelta("panel_signature_delta_percent", current.PanelSignature.Average, baseline.PanelSignature.Average);
        WriteDelta("visible_roi_delta_percent", current.VisibleRoi.Average, baseline.VisibleRoi.Average);
        WriteDelta("scroll_list_stable_delta_percent", current.ScrollListStable.Average, baseline.ScrollListStable.Average);
        WriteDelta("row_scroll_settle_delta_percent", current.RowScrollSettle.Average, baseline.RowScrollSettle.Average);
        WriteDelta("row_signature_delta_percent", current.RowSignature.Average, baseline.RowSignature.Average);
        WriteDelta("ocr_total_delta_percent", current.OcrTotal.Average, baseline.OcrTotal.Average);
        WriteDelta("ocr_total_per_item_delta_percent", current.OcrTotalPerItem.Average, baseline.OcrTotalPerItem.Average);
        WriteDelta("ppocr_roi_per_item_delta_percent", current.PpOcrRoiPerItem.Average, baseline.PpOcrRoiPerItem.Average);
        WriteDelta("fast_accepted_per_item_delta_percent", current.FastAcceptedPerItem.Average, baseline.FastAcceptedPerItem.Average);
        WriteDelta("fallback_rate_delta_percent",
            PercentValue(current.EffectiveFallbackCount, Math.Max(current.CellTimingCount, current.ClickAll.Count)),
            PercentValue(baseline.EffectiveFallbackCount, Math.Max(baseline.CellTimingCount, baseline.ClickAll.Count)));
    }
}
