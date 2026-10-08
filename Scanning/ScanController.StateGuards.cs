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
    private enum RowScanResult
    {
        Completed,
        Stop
    }


    private readonly record struct RarityProbe(
        string? Rarity,
        Color BestColor,
        string BestCandidate,
        int BestScore,
        int SecondScore,
        int Margin,
        bool FullScan);

    private sealed class ScanLog : IDisposable
    {
        private readonly object _sync = new();
        private readonly StreamWriter _writer;
        private int _eventId;

        public ScanLog(string path)
        {
            _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        }

        public void Write(string message)
        {
            lock (_sync)
            {
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                _writer.Flush();
            }
        }

        public void WriteEvent(string kind, string message)
        {
            lock (_sync)
            {
                _eventId++;
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] EVENT #{_eventId:000000} {kind}: {message}");
                if (_eventId % 64 == 0)
                {
                    _writer.Flush();
                }
            }
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private sealed class WarehouseContextGuard
    {
        private readonly object _sync = new();
        private readonly GameWindow _window;
        private readonly WarehouseMonitorPlan _plan;
        private readonly WarehousePreflightPolicy _policy;
        private readonly IOcrRecognizer _recognizer;
        private readonly ScanLog _scanLog;
        private readonly WarehouseInputGuardState _state;
        private WarehouseFastProbe _lastFastProbe;
        private WarehouseStrongProbe _lastStrongProbe;

        public WarehouseContextGuard(
            GameWindow window,
            WarehouseMonitorPlan plan,
            WarehousePreflightPolicy policy,
            IOcrRecognizer recognizer,
            ScanLog scanLog)
        {
            _window = window;
            _plan = plan;
            _policy = policy;
            _recognizer = recognizer;
            _scanLog = scanLog;
            PollMilliseconds = Math.Clamp(policy.MonitorPollMilliseconds, 100, 1000);
            RequiredFailureFrames = Math.Clamp(policy.MonitorFailureFrames, 2, 5);
            MinimumScore = Math.Clamp(policy.MonitorMinimumScore, 1, 100);
            _state = new WarehouseInputGuardState(
                Math.Clamp(policy.MonitorMaximumAgeMilliseconds, 100, 2000),
                RequiredFailureFrames,
                plan.FastBaseline);
            _lastFastProbe = new WarehouseFastProbe(true, 100, default, plan.FastBaseline);
        }

        public int PollMilliseconds { get; }
        public int RequiredFailureFrames { get; }
        public int MinimumScore { get; }

        public void EnsureHealthy()
        {
            lock (_sync)
            {
                if (_state.IsFresh())
                {
                    return;
                }

                _lastFastProbe = CaptureWarehouseFastProbe(_window, _plan, _state.FastBaseline);
                if (_state.AcceptFast(
                    _lastFastProbe.CaptureHealthy,
                    _lastFastProbe.Score,
                    MinimumScore))
                {
                    return;
                }

                _scanLog.WriteEvent(
                    "WAREHOUSE_INPUT_GUARD_FAST",
                    $"passed=False, score={_lastFastProbe.Score}, requiredScore={MinimumScore}, captureHealthy={_lastFastProbe.CaptureHealthy}, captureScore={_lastFastProbe.Health.Score}");
                _state.BeginStrongConfirmation();
                while (true)
                {
                    _lastStrongProbe = CaptureWarehouseStrongProbe(
                        _window,
                        _plan,
                        _recognizer,
                        _policy,
                        _scanLog);
                    if (_state.AcceptStrong(
                        _lastStrongProbe.Passed,
                        _lastStrongProbe.HeaderSignature))
                    {
                        _scanLog.WriteEvent(
                            "WAREHOUSE_INPUT_GUARD_CONFIRM",
                            $"passed=True, confirmationFailures=0/{RequiredFailureFrames}, captureHealthy={_lastStrongProbe.Health.Passed}, headerDetected={_lastStrongProbe.Header.HeaderDetected}, headerScore={_lastStrongProbe.Header.HeaderScore}, gridStructureScore={_lastStrongProbe.Structure.GridStructureScore}, layoutScore={_lastStrongProbe.Structure.LayoutScore}, baselineRebuilt=True");
                        return;
                    }

                    _scanLog.WriteEvent(
                        "WAREHOUSE_INPUT_GUARD_CONFIRM",
                        $"passed=False, confirmationFailures={_state.StrongFailures}/{RequiredFailureFrames}, captureHealthy={_lastStrongProbe.Health.Passed}, headerDetected={_lastStrongProbe.Header.HeaderDetected}, headerScore={_lastStrongProbe.Header.HeaderScore}, gridStructureScore={_lastStrongProbe.Structure.GridStructureScore}, layoutScore={_lastStrongProbe.Structure.LayoutScore}");
                    if (_state.ShouldBlock)
                    {
                        _scanLog.Write($"Warehouse input guard blocked input after {_state.StrongFailures} strong confirmation failures.");
                        throw CreateFailureCore();
                    }

                    Thread.Sleep(PollMilliseconds);
                }
            }
        }

        private ScannerFailureException CreateFailureCore()
        {
            var details = new Dictionary<string, object?>
            {
                ["preflightState"] = "warehouse_context_lost",
                ["fastScore"] = Math.Clamp(_lastFastProbe.Score, 0, 100),
                ["headerScore"] = Math.Clamp(_lastStrongProbe.Header.HeaderScore, 0, 100),
                ["gridStructureScore"] = Math.Clamp(_lastStrongProbe.Structure.GridStructureScore, 0, 100),
                ["layoutScore"] = Math.Clamp(_lastStrongProbe.Structure.LayoutScore, 0, 100),
                ["confirmationFailures"] = _state.StrongFailures,
                ["stableFrames"] = 0,
                ["requiredStableFrames"] = RequiredFailureFrames,
                ["captureMode"] = _window.ActiveCaptureMode,
                ["clientWidth"] = _window.ClientScreenRect.Width,
                ["clientHeight"] = _window.ClientScreenRect.Height,
                ["dpi"] = _window.Dpi
            };
            return new ScannerFailureException(
                "warehouse_context_lost",
                "无法确认驱动盘仓库界面",
                "扫描期间无法继续确认驱动盘仓库界面，已在下一次输入前停止。",
                "请确保游戏可见、未被遮挡并停留在背包中的驱动盘页面后重试。",
                details);
        }
    }

    internal sealed class WarehouseInputGuardState
    {
        private readonly long _maximumAgeTicks;
        private readonly int _requiredStrongFailures;
        private long _lastHealthyTimestamp;

        public WarehouseInputGuardState(
            int maximumAgeMilliseconds,
            int requiredStrongFailures,
            WarehouseMonitorSignature fastBaseline)
        {
            _maximumAgeTicks = (long)Math.Ceiling(
                Math.Max(1, maximumAgeMilliseconds) / 1000d * Stopwatch.Frequency);
            _requiredStrongFailures = Math.Max(1, requiredStrongFailures);
            _lastHealthyTimestamp = Stopwatch.GetTimestamp();
            FastBaseline = fastBaseline;
        }

        public WarehouseMonitorSignature FastBaseline { get; private set; }
        public int StrongFailures { get; private set; }
        public bool ShouldBlock => StrongFailures >= _requiredStrongFailures;

        public bool IsFresh() => Stopwatch.GetTimestamp() - _lastHealthyTimestamp <= _maximumAgeTicks;

        public bool AcceptFast(bool captureHealthy, int score, int minimumScore)
        {
            if (!captureHealthy || score < minimumScore)
            {
                return false;
            }

            MarkHealthy();
            return true;
        }

        public bool AcceptStrong(bool passed, WarehouseMonitorSignature headerSignature)
        {
            if (!passed)
            {
                StrongFailures++;
                return false;
            }

            FastBaseline = headerSignature;
            MarkHealthy();
            return true;
        }

        private void MarkHealthy()
        {
            StrongFailures = 0;
            _lastHealthyTimestamp = Stopwatch.GetTimestamp();
        }

        public void BeginStrongConfirmation()
        {
            StrongFailures = 0;
        }

    }

    private readonly record struct InventoryCountConsensusResult(
        int? InventoryCount,
        int? InventoryCapacity,
        int ConsensusFrames,
        int Attempts);

    private readonly record struct WarehouseFastProbe(
        bool CaptureHealthy,
        int Score,
        CaptureHealthResult Health,
        WarehouseMonitorSignature Signature);

    private readonly record struct WarehouseStrongProbe(
        bool Passed,
        CaptureHealthResult Health,
        WarehouseHeaderProbeResult Header,
        WarehouseStructureProbeResult Structure,
        WarehouseMonitorSignature HeaderSignature);

}
