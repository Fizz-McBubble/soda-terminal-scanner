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
    private enum PanelStabilitySource
    {
        Panel,
        TextCore
    }

    private sealed record PanelStabilityDecision(PanelStabilitySource Source, string SourceName, string Reason);

    private sealed class PanelProbeHealth
    {
        private const int WarmupItems = 12;
        private const int ZeroChangeLimit = 3;
        private int _items;
        private int _zeroChangeItems;
        private bool _logged;

        public bool ForceSafe { get; private set; }

        public void Observe(double? changeMilliseconds, PanelAcceptMode effectivePanelAcceptMode, Action<string> log)
        {
            if (ForceSafe || _items >= WarmupItems)
            {
                return;
            }

            if (effectivePanelAcceptMode != PanelAcceptMode.AdaptiveEarlyFullRoi)
            {
                return;
            }

            _items++;
            if (changeMilliseconds is <= 0.1)
            {
                _zeroChangeItems++;
            }

            if (_zeroChangeItems < ZeroChangeLimit)
            {
                return;
            }

            ForceSafe = true;
            if (!_logged)
            {
                _logged = true;
                log($"PANEL_PROBE_DEGRADED_SAFE_MODE warmupItems={_items}, zeroChangeItems={_zeroChangeItems}, threshold={ZeroChangeLimit}, action=force_panel_accept_safe_for_scan");
                log($"PROFILE_HEALTH_DEGRADED source=panel_probe, warmupItems={_items}, zeroChangeItems={_zeroChangeItems}, threshold={ZeroChangeLimit}, action=force_panel_accept_safe_for_scan");
            }
        }
    }

    private sealed class ProfileHealthGate
    {
        private const int PanelWarmupItems = 12;
        private const int OverlapWarmupScrolls = 12;
        private const double PanelWaitAverageThresholdMs = 260;
        private const double CaptureAverageThresholdMs = 90;
        private const double AmbiguousOverlapThresholdRate = 0.50;

        private int _panelItems;
        private double _panelWaitTotalMs;
        private double _captureTotalMs;
        private int _overlapScrolls;
        private int _overlapAmbiguous;
        private bool _panelLogged;
        private bool _overlapLogged;

        public bool ForceSafePanel { get; private set; }

        public void ObservePanel(double panelWaitMilliseconds, double captureMilliseconds, Action<string> log)
        {
            if (_panelItems >= PanelWarmupItems)
            {
                return;
            }

            _panelItems++;
            _panelWaitTotalMs += panelWaitMilliseconds;
            _captureTotalMs += captureMilliseconds;
            if (_panelItems < PanelWarmupItems)
            {
                return;
            }

            var panelWaitAverage = _panelWaitTotalMs / _panelItems;
            var captureAverage = _captureTotalMs / _panelItems;
            if (panelWaitAverage > PanelWaitAverageThresholdMs || captureAverage > CaptureAverageThresholdMs)
            {
                ForceSafePanel = true;
                if (!_panelLogged)
                {
                    _panelLogged = true;
                    log($"PROFILE_HEALTH_DEGRADED source=panel_capture, warmupItems={_panelItems}, panelWaitAvgMs={panelWaitAverage:F1}, captureAvgMs={captureAverage:F1}, panelWaitThresholdMs={PanelWaitAverageThresholdMs:F1}, captureThresholdMs={CaptureAverageThresholdMs:F1}, action=force_panel_accept_safe_for_scan");
                }
            }
            else
            {
                log($"PROFILE_HEALTH_OK source=panel_capture, warmupItems={_panelItems}, panelWaitAvgMs={panelWaitAverage:F1}, captureAvgMs={captureAverage:F1}, panelWaitThresholdMs={PanelWaitAverageThresholdMs:F1}, captureThresholdMs={CaptureAverageThresholdMs:F1}");
            }
        }

        public void ObserveOverlap(bool ambiguous, Action<string> log)
        {
            if (_overlapScrolls >= OverlapWarmupScrolls)
            {
                return;
            }

            _overlapScrolls++;
            if (ambiguous)
            {
                _overlapAmbiguous++;
            }

            if (_overlapScrolls < OverlapWarmupScrolls)
            {
                return;
            }

            var rate = _overlapAmbiguous / (double)_overlapScrolls;
            if (rate > AmbiguousOverlapThresholdRate)
            {
                ForceSafePanel = true;
                if (!_overlapLogged)
                {
                    _overlapLogged = true;
                    log($"PROFILE_HEALTH_DEGRADED source=overlap_signature, warmupScrolls={_overlapScrolls}, ambiguous={_overlapAmbiguous}, ambiguousRate={rate:F3}, threshold={AmbiguousOverlapThresholdRate:F3}, action=force_panel_accept_safe_for_scan");
                }
            }
            else
            {
                log($"PROFILE_HEALTH_OK source=overlap_signature, warmupScrolls={_overlapScrolls}, ambiguous={_overlapAmbiguous}, ambiguousRate={rate:F3}, threshold={AmbiguousOverlapThresholdRate:F3}");
            }
        }
    }

    private sealed class PanelStabilitySelector
    {
        public const int DefaultWarmupItems = 12;
        public const int MinimumTextCoreGainMilliseconds = 15;

        private readonly PanelStabilityMode _requestedMode;
        private readonly int _warmupItems;
        private readonly List<PanelStabilitySample> _samples = new();
        private bool _safetyFallback;

        public PanelStabilitySelector(PanelStabilityMode requestedMode, int warmupItems = DefaultWarmupItems)
        {
            _requestedMode = requestedMode;
            _warmupItems = Math.Max(1, warmupItems);
        }

        public PanelStabilityDecision Resolve()
        {
            if (_requestedMode == PanelStabilityMode.Panel)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "configured_panel");
            }

            if (_requestedMode == PanelStabilityMode.TextCore)
            {
                return new PanelStabilityDecision(PanelStabilitySource.TextCore, "text-core", "configured_text_core");
            }

            if (_safetyFallback)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_safety");
            }

            if (_samples.Count < _warmupItems)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_warmup");
            }

            var panelAverage = _samples.Average(sample => sample.PanelStableMilliseconds);
            var textAverage = _samples.Average(sample => sample.TextCoreStableMilliseconds);
            var panelP90 = Percentile(_samples.Select(sample => sample.PanelStableMilliseconds), 0.90);
            var textP90 = Percentile(_samples.Select(sample => sample.TextCoreStableMilliseconds), 0.90);
            if (textAverage + MinimumTextCoreGainMilliseconds < panelAverage && textP90 <= panelP90)
            {
                return new PanelStabilityDecision(PanelStabilitySource.TextCore, "text-core", "text_core_selected");
            }

            return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_no_gain");
        }

        public void Observe(double? panelStableMilliseconds, double? textCoreStableMilliseconds, string sourceName, string reason)
        {
            if (_requestedMode != PanelStabilityMode.Auto)
            {
                return;
            }

            if (reason == "panel_fallback_safety")
            {
                _safetyFallback = true;
                return;
            }

            if (string.Equals(sourceName, "text-core", StringComparison.OrdinalIgnoreCase)
                && (panelStableMilliseconds is null || panelStableMilliseconds <= 0))
            {
                return;
            }

            if (panelStableMilliseconds is not > 0 || textCoreStableMilliseconds is not > 0)
            {
                return;
            }

            _samples.Add(new PanelStabilitySample(panelStableMilliseconds.Value, textCoreStableMilliseconds.Value));
            if (_samples.Count > 96)
            {
                _samples.RemoveAt(0);
            }
        }

        public void MarkSafetyFallback()
        {
            if (_requestedMode == PanelStabilityMode.Auto)
            {
                _safetyFallback = true;
            }
        }

        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }

        private readonly record struct PanelStabilitySample(double PanelStableMilliseconds, double TextCoreStableMilliseconds);
    }


    private sealed class StalePanelException : TimeoutException
    {
        public StalePanelException(string message)
            : base(message)
        {
        }
    }

    private sealed class PanelCaptureTimeoutException : TimeoutException
    {
        public PanelCaptureTimeoutException(
            int visibleRois,
            int totalRois,
            string? firstMissingRoi,
            RowPresenceProbeResult? firstMissingProbe,
            string acceptGateReason,
            bool sawPanelChange,
            bool selectionChanged,
            int stableFrames,
            int requiredStableFrames,
            int frameCount)
            : base("详情面板截图等待超时。")
        {
            VisibleRois = visibleRois;
            TotalRois = totalRois;
            FirstMissingRoi = firstMissingRoi;
            ReferenceLuma = firstMissingProbe?.ReferenceLuma;
            CandidateLuma = firstMissingProbe?.CandidateLuma;
            LumaDelta = firstMissingProbe?.LumaDelta;
            AllowedLumaDelta = firstMissingProbe?.AllowedLumaDelta;
            EdgeDensityPermille = firstMissingProbe?.EdgeDensityPermille;
            MinimumEdgeDensityPermille = firstMissingProbe?.MinimumEdgeDensityPermille;
            AcceptGateReason = acceptGateReason;
            SawPanelChange = sawPanelChange;
            SelectionChanged = selectionChanged;
            StableFrames = stableFrames;
            RequiredStableFrames = requiredStableFrames;
            FrameCount = frameCount;
        }

        public int VisibleRois { get; }
        public int TotalRois { get; }
        public string? FirstMissingRoi { get; }
        public int? ReferenceLuma { get; }
        public int? CandidateLuma { get; }
        public int? LumaDelta { get; }
        public int? AllowedLumaDelta { get; }
        public int? EdgeDensityPermille { get; }
        public int? MinimumEdgeDensityPermille { get; }
        public string AcceptGateReason { get; }
        public bool SawPanelChange { get; }
        public bool SelectionChanged { get; }
        public int StableFrames { get; }
        public int RequiredStableFrames { get; }
        public int FrameCount { get; }
    }

    private sealed class PanelCellCaptureException : TimeoutException, IScannerFailureException
    {
        public PanelCellCaptureException(
            int pass,
            int visualRow,
            int column,
            int maxColumns,
            int? logicalRow,
            int attempts,
            GameWindow window,
            string visualProfileId,
            Exception innerException)
            : base($"详情面板截图等待超时：logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={visualRow}, col={column}/{maxColumns}。", innerException)
        {
            Pass = pass;
            VisualRow = visualRow;
            Column = column;
            MaxColumns = maxColumns;
            LogicalRow = logicalRow;
            var timeout = innerException as PanelCaptureTimeoutException;
            DiagnosticDetails = ScanDiagnosticDetails.PanelCapture(
                logicalRow,
                visualRow,
                column,
                maxColumns,
                timeout?.VisibleRois ?? 0,
                timeout?.TotalRois ?? 0,
                timeout?.FirstMissingRoi,
                timeout?.ReferenceLuma,
                timeout?.CandidateLuma,
                timeout?.LumaDelta,
                timeout?.AllowedLumaDelta,
                timeout?.EdgeDensityPermille,
                timeout?.MinimumEdgeDensityPermille,
                timeout?.AcceptGateReason ?? "unknown",
                timeout?.SawPanelChange ?? false,
                timeout?.SelectionChanged ?? false,
                timeout?.StableFrames ?? 0,
                timeout?.RequiredStableFrames ?? 0,
                attempts,
                timeout?.FrameCount ?? 0,
                window.ClientScreenRect.Width,
                window.ClientScreenRect.Height,
                window.Dpi,
                window.ActiveCaptureMode,
                visualProfileId);
        }

        public int Pass { get; }
        public int VisualRow { get; }
        public int Column { get; }
        public int MaxColumns { get; }
        public int? LogicalRow { get; }
        public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
        public string Code => "panel_capture_timeout";
        public string Title => "驱动盘详情读取超时";
        public string Remedy => "请保持游戏前台且无遮挡后重试；持续发生时请打开日志。";
        public bool Retryable => true;
    }

}
