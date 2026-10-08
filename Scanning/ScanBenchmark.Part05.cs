using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private sealed class ScanReport
    {
        public ScanReport(string scanDirectory)
        {
            ScanDirectory = scanDirectory;
        }

        public string ScanDirectory { get; }
        public bool Valid { get; init; }
        public string ErrorMessage { get; init; } = "";
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? CaptureEndTime { get; set; }
        public double? DurationSeconds { get; set; }
        public double? CaptureDurationSeconds { get; set; }
        public double? CellTimingPerSecond { get; set; }
        public double? CaptureCellTimingPerSecond { get; set; }
        public double? QueuedPerSecond { get; set; }
        public double? CaptureQueuedPerSecond { get; set; }
        public double? CompletedPerSecond { get; set; }
        public bool CaptureLimited { get; set; }
        public string StopReason { get; set; } = "";
        public bool? Partial { get; set; }
        public string TerminationCode { get; set; } = "";
        public string ExportFileName { get; set; } = "";
        public string Traversal { get; set; } = "unknown";
        public string RowAdvanceMode { get; set; } = "unknown";
        public int? OcrWorkers { get; set; }
        public int? OcrBatchSizeSetting { get; set; }
        public int? OcrQueueCapacity { get; set; }
        public int? OcrIntraOpThreads { get; set; }
        public int? MaxItemsSetting { get; set; }
        public string ProfileId { get; set; } = "";
        public string TrainingProfileId { get; set; } = "";
        public string ProfileFamilyId { get; set; } = "";
        public string ProfileGeometryStatus { get; set; } = "";
        public string RequestedProfileId { get; set; } = "";
        public string DetectedProfileId { get; set; } = "";
        public string ProfileDetectedGeometry { get; set; } = "";
        public string ProfileRoute { get; set; } = "";
        public string FastAcceptByProfileFamily { get; set; } = "";
        public int FastExactProfileAcceptCount { get; set; }
        public int HealthFallbackCount { get; set; }
        public int CanonicalCropSucceededCount { get; set; }
        public int CanonicalCropFallbackCount { get; set; }
        public int CanonicalCropDecisionCount { get; set; }
        public int? ExportItemCount { get; set; }
        public int ExportDuplicateGroupCount { get; set; }
        public int ExportDuplicateItemCount { get; set; }
        public int ExportVerifiedAdjacentDuplicateCount { get; set; }
        public int ExportUnverifiedDuplicateCount { get; set; }
        public int SlotOutOfRangeCount { get; set; }
        public int SlotMainStatViolationCount { get; set; }
        public int SlotFixedValueViolationCount { get; set; }
        public bool SlotSafetyPass => SlotOutOfRangeCount == 0
            && SlotMainStatViolationCount == 0
            && SlotFixedValueViolationCount == 0;
        public int ErrorFileCount { get; set; }
        public int Non15FileCount { get; set; }
        public int CellTimingCount { get; set; }
        public int CellTimingFallbackCount { get; set; }
        public int CellTimingFallbackLogCount { get; set; }
        public int EffectiveFallbackCount => CellTimingCount > 0 ? CellTimingFallbackCount : CellTimingFallbackLogCount;
        public int SelectionOnlyAcceptCount { get; set; }
        public int PostScrollSelectionOnlyBlockedCount { get; set; }
        public int WeakPanelChangeBlockedCount { get; set; }
        public int QuickAcceptCount { get; set; }
        public int QuickRejectCount { get; set; }
        public int PanelStablePanelCount { get; set; }
        public int PanelStableTextCoreCount { get; set; }
        public int PostScrollAdaptiveAcceptCount { get; set; }
        public int PostScrollSafeAcceptCount { get; set; }
        public int BeforeMinAcceptGateCount { get; set; }
        public int FloorWaitLimitedCount { get; set; }
        public int VisualRow2ClickCount { get; set; }
        public int UnsafeVisualRow2ClickCount { get; set; }
        public int OverlapViewportCount { get; set; }
        public int OverlapRowScannedCount { get; set; }
        public int OverlapScrollAcceptedCount { get; set; }
        public int OverlapConflictCount { get; set; }
        public int OverlapConflictRecheckCount { get; set; }
        public int OverlapConflictRecoveredCount { get; set; }
        public int OverlapAmbiguousAcceptCount { get; set; }
        public int OverlapConfirmedTwoRowAcceptCount { get; set; }
        public int OverlapHardStopCount { get; set; }
        public int? TotalLogicalRows { get; set; }
        public int OverlapScannedLogicalRowsCount { get; set; }
        public int OverlapMissingLogicalRowsCount { get; set; }
        public bool FullScanExpected { get; set; }
        public bool FullScanComplete { get; set; }
        public bool EffectiveFullScanComplete { get; set; }
        public int RowScrollOvershotCount { get; set; }
        public int RowScrollRecoveryAcceptedCount { get; set; }
        public int RowScrollRecoveryFailCount { get; set; }
        public int RowScrollStrictStopCount { get; set; }
        public int RowScrollNoMoveCount { get; set; }
        public int RowScrollAmbiguousCount { get; set; }
        public int RowScrollSettleStartCount { get; set; }
        public int RowScrollSettleAcceptCount { get; set; }
        public int RowScrollSettleTimeoutCount { get; set; }
        public int RowScrollPartialMoveContinueCount { get; set; }
        public int RowScrollFalseAdvanceCount { get; set; }
        public int NonUnitRowScrollDoneCount { get; set; }
        public int PanelTargetEvidenceCount { get; set; }
        public int PanelNeighborRoundTripCount { get; set; }
        public int TargetVerificationEventCount { get; set; }
        public int TargetVerificationUnknownCount { get; set; }
        public int TargetSelectionEvidenceMissingCount { get; set; }
        public int TargetSelectionUnderStableCount { get; set; }
        public bool TargetVerificationConsistent => TargetVerificationEventCount == 0
            || (TargetVerificationUnknownCount == 0
                && TargetSelectionEvidenceMissingCount == 0
                && TargetSelectionUnderStableCount == 0
                && LastCompleted == TargetVerificationEventCount);
        public int OcrDrainStartCount { get; set; }
        public int OcrDrainDoneCount { get; set; }
        public int OcrDrainRemainingCount { get; set; }
        public int OcrDrainDiscardedCount { get; set; }
        public bool OcrDrainConsistent => OcrDrainStartCount == 0
            || (OcrDrainDoneCount == OcrDrainStartCount
                && OcrDrainRemainingCount == 0
                && OcrDrainDiscardedCount == 0);
        public int EdgeClickBlockedCount { get; set; }
        public int NativeEdgeClickStartCount { get; set; }
        public int NativeEdgeClickSettledCount { get; set; }
        public int NativeEdgeClickChangedCount { get; set; }
        public int NativeEdgeClickHashFallbackCount { get; set; }
        public int NativeEdgeClickBottomCount { get; set; }
        public int NativeEdgeClickStopCount { get; set; }
        public int NativeEdgeClickBufferCommitCount { get; set; }
        public int NativeEdgeWheelTickConflictCount { get; set; }
        public int? MinVisibleRois { get; set; }
        public int IncompleteRoiCount { get; set; }
        public int? LastVisited { get; set; }
        public int? LastQueued { get; set; }
        public int? LastCompleted { get; set; }
        public int? LastFailed { get; set; }
        public Stats ClickSameRow { get; set; }
        public Stats ClickAfterScroll { get; set; }
        public Stats AfterScrollExtra { get; set; }
        public Stats ClickAll { get; set; }
        public Stats ScrollDuration { get; set; }
        public Stats PanelWait { get; set; }
        public Stats SameRowPanelWait { get; set; }
        public Stats PostScrollFirstPanelWait { get; set; }
        public Stats PostScrollFirstCellTotal { get; set; }
        public Stats PanelFrames { get; set; }
        public Stats PanelFramesAfterWarmup { get; set; }
        public Stats PanelChange { get; set; }
        public Stats SelectionChange { get; set; }
        public Stats PanelFullRoi { get; set; }
        public Stats RoiCompleteFrames { get; set; }
        public Stats SelectedStableFrames { get; set; }
        public Stats PanelStable { get; set; }
        public Stats PanelTextStable { get; set; }
        public Stats TargetSelectionStableFrames { get; set; }
        public Stats RarityProbe { get; set; }
        public Stats SelectionProbe { get; set; }
        public Stats PanelCapture { get; set; }
        public Stats PanelSignature { get; set; }
        public Stats VisibleRoi { get; set; }
        public Stats FrameLoop { get; set; }
        public Stats FrameToBitmap { get; set; }
        public Stats BitmapCreatedCount { get; set; }
        public Stats AdaptiveThrottle { get; set; }
        public Stats OcrBacklogBeforeEnqueue { get; set; }
        public Stats AdaptivePanelMin { get; set; }
        public bool PanelMinimumFloorPass => AdaptivePanelMin.Count == 0
            || AdaptivePanelMin.Minimum is >= 120;
        public Stats PanelMinAcceptFloor { get; set; }
        public Stats SameRowPanelFloor { get; set; }
        public Stats PostScrollPanelFloor { get; set; }
        public Stats FloorWaitLimited { get; set; }
        public Stats PanelAcceptElapsedVsFloor { get; set; }
        public Stats ScrollTickDelay { get; set; }
        public Stats ScrollTickWait { get; set; }
        public Stats ScrollListStable { get; set; }
        public Stats RowScrollSettle { get; set; }
        public Stats RowSignature { get; set; }
        public Stats PostScrollViewport { get; set; }
        public Stats CellTotal { get; set; }
        public Stats EnqueueWait { get; set; }
        public Stats FallbackPanelWait { get; set; }
        public Stats NormalPanelWait { get; set; }
        public Stats OcrBatchSize { get; set; }
        public Stats OcrBitmapToMat { get; set; }
        public Stats OcrPreprocess { get; set; }
        public Stats OcrInference { get; set; }
        public Stats OcrDecode { get; set; }
        public Stats OcrTotal { get; set; }
        public Stats OcrTotalPerItem { get; set; }
        public Stats OcrClean { get; set; }
        public Stats OcrBacklog { get; set; }
        public Stats FastMatchMsPerItem { get; set; }
        public Stats FastAcceptedPerItem { get; set; }
        public Stats FastRejectedPerItem { get; set; }
        public Stats PpOcrRoiPerItem { get; set; }
        public Stats FastOcrFeatureMs { get; set; }
        public Stats ScannerCpu { get; set; }
        public Stats ResourceBacklog { get; set; }
        public Dictionary<string, int> AcceptGateReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PanelFloorModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static ScanReport Invalid(string scanDirectory, string message)
        {
            return new ScanReport(scanDirectory)
            {
                Valid = false,
                ErrorMessage = message
            };
        }
    }

    private readonly record struct ScanEvent(DateTime Timestamp, string Kind, string Detail);

    private readonly record struct ClickInterval(double Milliseconds, bool AfterScroll);

    private readonly record struct ClickPosition(int LogicalRow, int VisualRow, int VisibleTopLogicalRow, string State);

    private readonly record struct CellTiming(int Index, double PanelWaitMs, double EnqueueWaitMs, bool Fallback, int VisibleRois, int TotalRois, double TotalMs, bool AfterScroll, bool PostScrollFirstCell, double? PanelFrames, double? ChangeMs, double? SelectionChangeMs, double? FullRoiMs, double? StableMs, double? TextStableMs, string? StableSource, string? StabilityReason, double? TargetSelectionStableFrames, string? TargetVerificationKind, double? RarityProbeMs, double? SelectionProbeMs, double? CaptureMs, double? SignatureMs, double? VisibleRoiMs, double? FrameLoopMs, double? FrameToBitmapMs, double? BitmapCreatedCount, bool? QuickAccept, string? QuickRejectReason, double? AdaptiveThrottleMs, double? OcrBacklogBeforeEnqueue, double? AdaptivePanelMinMs, string? PanelAcceptMode, string? PostScrollAcceptMode, double? PanelMinFloorMs, double? RoiCompleteFrames, double? SelectedStableFrames, string? AcceptGateReason, string? PanelFloorMode, double? SameRowPanelFloorMs, double? PostScrollPanelFloorMs, string? PanelFloorReason, double? FloorWaitLimitedMs, double? PanelAcceptElapsedVsFloorMs, double? ScrollTickDelayMs);

    private readonly record struct ScrollTiming(double ScrollTickWaitMs, double ListStableMs, double RowSignatureMs, double? SettleMs, double PostScrollViewportMs);

    private readonly record struct CounterSnapshot(int Visited, int Queued, int Completed, int Failed);

    private readonly record struct ScanTerminalSnapshot(
        CounterSnapshot Counters,
        bool Partial,
        string TerminationCode,
        string ExportFile);

    private readonly record struct StartSettings(int Workers, int BatchSize, int QueueCapacity, int IntraOpThreads);

    private readonly record struct ExportStats(
        int? ItemCount,
        int DuplicateGroupCount,
        int DuplicateItemCount,
        int VerifiedAdjacentDuplicateCount,
        int UnverifiedDuplicateCount,
        int SlotOutOfRangeCount,
        int SlotMainStatViolationCount,
        int SlotFixedValueViolationCount);

    private readonly record struct OverlapTraversalSummary(int? TotalRows, int ScannedRows, int MissingRows, bool FullScanComplete);

    private readonly record struct Stats(int Count, double? Average, double? P50, double? P90, double? Minimum, double? Maximum)
    {
        public bool HasData => Count > 0;

        public static Stats From(IEnumerable<double> values)
        {
            var sorted = values
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
            {
                return new Stats(0, null, null, null, null, null);
            }

            return new Stats(
                sorted.Length,
                sorted.Average(),
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.90),
                sorted[0],
                sorted[^1]);
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            var index = Math.Clamp((int)(sorted.Count * percentile), 0, sorted.Count - 1);
            return sorted[index];
        }
    }
}
