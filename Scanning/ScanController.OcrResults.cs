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
    private static void ConsumeOcrResults(
        BlockingCollection<OcrWorkResult> ocrResults,
        ConcurrentBag<DriveDiscExport> results,
        ConcurrentDictionary<int, R4ScanRecord> r4Records,
        Counters counters,
        string outputDir,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        ScanOptions options,
        CancellationTokenSource linked)
    {
        var pending = new SortedDictionary<int, OcrWorkResult>();
        var nextIndex = 1;
        foreach (var result in ocrResults.GetConsumingEnumerable())
        {
            if (Volatile.Read(ref counters.StopAfterIndex) > 0)
            {
                continue;
            }

            pending[result.Index] = result;
            nextIndex = FlushOcrResults(pending, nextIndex, allowGaps: false, results, r4Records, counters, outputDir, progress, scanLog, options, linked);
        }

        _ = FlushOcrResults(pending, nextIndex, allowGaps: true, results, r4Records, counters, outputDir, progress, scanLog, options, linked);
    }

    private static int FlushOcrResults(
        SortedDictionary<int, OcrWorkResult> pending,
        int nextIndex,
        bool allowGaps,
        ConcurrentBag<DriveDiscExport> results,
        ConcurrentDictionary<int, R4ScanRecord> r4Records,
        Counters counters,
        string outputDir,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        ScanOptions options,
        CancellationTokenSource linked)
    {
        if (Volatile.Read(ref counters.StopAfterIndex) > 0)
        {
            pending.Clear();
            return nextIndex;
        }

        while (pending.Count > 0)
        {
            var index = nextIndex;
            if (!pending.TryGetValue(index, out var result))
            {
                if (!allowGaps)
                {
                    break;
                }

                index = pending.Keys.First();
                result = pending[index];
            }

            pending.Remove(index);
            nextIndex = Math.Max(nextIndex, index + 1);
            if (result.Export is not null)
            {
                if (options.StopAtNonLevel15 && result.Export.Level != 15)
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, result.Index, 0);
                    Interlocked.CompareExchange(ref counters.StopReason, "non_level_15_stop", null);
                    pending.Clear();
                    var detail = result.ErrorDetail ?? BuildExportDetail(result.Export);
                    File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.non15.txt"), detail);
                    scanLog.Write($"Stop at #{result.Index}: detected non-15 drive disc {result.Export.Name} {result.Export.Rarity} {result.Export.Level}/{result.Export.MaxLevel}.");
                    Report(progress, counters, $"检测到非15级驱动盘 #{result.Index}：{result.Export.Level}/{result.Export.MaxLevel}，扫描停止。");
                    linked.Cancel();
                    return nextIndex;
                }

                // Stats are not a disc identity. Capture and traversal verify
                // the physical target; distinct sequence numbers retain even
                // fully identical discs without a content-based stop limit.
                scanLog.WriteEvent(
                    "ITEM_TARGET_VERIFICATION",
                    $"index={result.Index}, kind={result.TargetVerificationKind}");

                results.Add(result.Export);
                if (result.R4Record is not null && !r4Records.TryAdd(result.Index, result.R4Record))
                    throw new InvalidDataException($"r4_sequence_duplicate:{result.Index}");
                Interlocked.Increment(ref counters.Completed);
                var message = result.Export.Index % 25 == 0
                    ? $"识别 #{result.Export.Index}：{result.Export.Name} {result.Export.Rarity} {result.Export.Level}/{result.Export.MaxLevel}"
                    : "";
                Report(progress, counters, message, result.Export);
            }
            else if (result.R4Record is not null)
            {
                if (!r4Records.TryAdd(result.Index, result.R4Record))
                    throw new InvalidDataException($"r4_sequence_duplicate:{result.Index}");
                Interlocked.Increment(ref counters.Completed);
                File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.review.txt"), result.ErrorDetail ?? result.ErrorMessage ?? "OCR review required");
                scanLog.Write($"OCR item needs review #{result.Index}: {result.ErrorMessage}");
                Report(progress, counters, $"待复核 #{result.Index}：{result.ErrorMessage}");
            }
            else
            {
                Interlocked.Increment(ref counters.Failed);
                File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.error.txt"), result.ErrorDetail ?? result.ErrorMessage ?? "Unknown OCR error");
                scanLog.Write($"OCR item failed #{result.Index}: {result.ErrorMessage}");
                Report(progress, counters, $"识别失败 #{result.Index}：{result.ErrorMessage}");
            }
        }

        return nextIndex;
    }

    private static bool TryTakeBatch(BlockingCollection<DiscCapture> queue, int maxBatchSize, out List<DiscCapture> batch)
    {
        batch = new List<DiscCapture>(Math.Max(1, maxBatchSize));
        try
        {
            batch.Add(queue.Take());
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        while (batch.Count < maxBatchSize && queue.TryTake(out var next, millisecondsTimeout: 3))
        {
            batch.Add(next);
        }

        return true;
    }

    private static int DisposeQueuedCaptures(BlockingCollection<DiscCapture> queue)
    {
        var count = 0;
        while (queue.TryTake(out var capture))
        {
            capture.Dispose();
            count++;
        }

        return count;
    }

    private static string BuildOcrDetail(DiscCapture capture, IReadOnlyList<OcrResult> ocr)
    {
        var lines = new List<string>
        {
            $"Index: {capture.Index}",
            $"Rarity: {capture.Rarity}",
            $"Rois: {capture.Rois.Length}",
            $"OcrResults: {ocr.Count}",
            "OCR:",
        };
        lines.AddRange(ocr.Select((item, index) => $"{index:D2}: {item.Score:P1} {item.Text}"));
        return string.Join(Environment.NewLine, lines);
    }


    private static string BuildErrorDetail(DiscCapture capture, IReadOnlyList<OcrResult> ocr, Exception ex)
    {
        var lines = new List<string>
        {
            BuildOcrDetail(capture, ocr),
            "Exception:",
            ex.ToString()
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildExportDetail(DriveDiscExport export)
    {
        return JsonSerializer.Serialize(export, JsonDefaults.Write);
    }

    private static string BuildBatchErrorDetail(DiscCapture capture, Exception ex)
    {
        return string.Join(Environment.NewLine, [
            $"Index: {capture.Index}",
            $"Rarity: {capture.Rarity}",
            $"Rois: {capture.Rois.Length}",
            "Batch OCR exception:",
            ex.ToString()
        ]);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan interval,
        int confirm,
        CancellationToken token)
    {
        var start = DateTime.UtcNow;
        var count = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token);
            if (condition())
            {
                count++;
                if (count >= confirm)
                {
                    return;
                }
            }
            else
            {
                count = 0;
            }
        }

        throw new TimeoutException("等待界面加载超时。");
    }

    private static int CurrentOcrBacklog(Counters counters)
    {
        return Math.Max(
            0,
            Volatile.Read(ref counters.Queued)
            - Volatile.Read(ref counters.Completed)
            - Volatile.Read(ref counters.Failed));
    }


    private static void Report(IProgress<ScanProgress> progress, Counters counters, string message, DriveDiscExport? item = null, Bitmap? debugImage = null)
    {
        progress.Report(new ScanProgress
        {
            Message = string.IsNullOrWhiteSpace(message) ? "" : $"[{DateTime.Now:HH:mm:ss}] {message}",
            Item = item,
            DebugImage = debugImage,
            Visited = counters.Visited,
            Queued = counters.Queued,
            Completed = counters.Completed,
            Failed = counters.Failed
        });
    }


    private sealed record ScanRuntimeState(
        bool QuickPanelAcceptEnabled,
        bool AdaptiveTimingActive,
        AdaptiveTimingState? AdaptiveTiming,
        AdaptiveOcrThrottle? OcrThrottle,
        PanelStabilitySelector PanelStability,
        PanelProbeHealth PanelProbeHealth,
        ProfileHealthGate ProfileHealth,
        PanelAcceptMode PanelAcceptMode,
        PostScrollPanelAcceptMode PostScrollPanelAcceptMode,
        PanelFloorMode PanelFloorMode,
        int PanelMinAcceptFloorMs,
        int SameRowPanelMinAcceptFloorMs,
        int PostScrollPanelMinAcceptFloorMs,
        int EffectiveScrollTickDelayMs)
    {
        public string VisualProfileId { get; set; } = "";
    }


    private sealed class Counters
    {
        public int Visited;
        public readonly ScanTraversalEvidence Traversal = new();
        public int Queued;
        public int Completed;
        public int Failed;
        public int StopAfterIndex;
        public string? StopReason;
    }

}
