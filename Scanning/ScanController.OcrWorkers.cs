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
    private static int ResolveOcrWorkerCount(ScanOptions options)
    {
        if (options.OcrWorkerCount > 0)
        {
            return Math.Clamp(options.OcrWorkerCount, 1, 4);
        }

        return options.HighSpeedOcr ? Math.Min(2, Math.Max(1, Environment.ProcessorCount / 4)) : 1;
    }

    private static int ResolveOcrIntraOpThreads(ScanOptions options, int workerCount)
    {
        var requested = Math.Clamp(options.OcrIntraOpThreads, 1, 8);
        if (options.HighSpeedOcr && workerCount <= 1)
        {
            return Math.Clamp(Math.Max(requested, Environment.ProcessorCount / 2), 1, 8);
        }

        if (workerCount <= 1)
        {
            return requested;
        }

        return Math.Max(1, Math.Min(requested, Math.Max(2, Environment.ProcessorCount / Math.Max(1, workerCount * 2))));
    }


    private Task[] StartOcrWorkers(
        BlockingCollection<DiscCapture> queue,
        BlockingCollection<OcrWorkResult> ocrResults,
        string outputDir,
        ScanOptions options,
        ScanLog scanLog,
        int workerCount,
        int ocrIntraOpThreads,
        Counters counters,
        OcrDiagnosticsWriter diagnostics,
        OcrShadowDatasetWriter? shadowDataset,
        FastOcrShadowRecorder? fastOcrShadow,
        FastOcrAssistEngine? fastOcrAssist,
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        bool normalizedRetryEnabled,
        CancellationTokenSource linked)
    {
        return Enumerable.Range(1, workerCount)
            .Select(workerId => StartGuardedOcrTask(() => ConsumeOcrWorker(queue, ocrResults, outputDir, options, scanLog, workerId, ocrIntraOpThreads, counters, diagnostics, shadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, normalizedRetryEnabled, linked.Token), linked))
            .ToArray();
    }

    internal static Task StartGuardedOcrTask(Action work, CancellationTokenSource linked) => Task.Run(() =>
    {
        try { work(); }
        catch { linked.Cancel(); throw; }
    });

    private static IOcrRecognizer CreateOcrRecognizer(
        ScanOptions options,
        string outputDirectory,
        string instanceName,
        int intraOpThreads,
        CancellationToken cancellationToken = default)
    {
        if (options.OcrEngine == OcrEngine.PpOcrV6)
        {
            if (string.IsNullOrWhiteSpace(options.PpOcrV6WorkerPath)
                || string.IsNullOrWhiteSpace(options.PpOcrV6ModelPath)
                || string.IsNullOrWhiteSpace(options.PpOcrV6ConfigPath))
            {
                throw new InvalidDataException("ppocrv6_runtime_identity_missing");
            }

            return new PpOcrV6ProcessRecognizer(
                Path.GetFullPath(options.PpOcrV6WorkerPath),
                Path.GetFullPath(options.PpOcrV6ModelPath),
                Path.GetFullPath(options.PpOcrV6ConfigPath),
                Path.Combine(outputDirectory, ".ppocrv6", instanceName), cancellationToken);
        }

        // Explicit oracle-only mode. There is deliberately no automatic fallback from v6.
        return new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, intraOpThreads);
    }

    private static double CalculateFloorWaitLimitedMilliseconds(
        int minimumAcceptMilliseconds,
        double? changeMilliseconds,
        double? fullRoiMilliseconds,
        double? stableMilliseconds)
    {
        var readyMilliseconds = new[] { changeMilliseconds, fullRoiMilliseconds, stableMilliseconds }
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(0, minimumAcceptMilliseconds - readyMilliseconds);
    }

    private void ConsumeOcrWorker(
        BlockingCollection<DiscCapture> queue,
        BlockingCollection<OcrWorkResult> ocrResults,
        string outputDir,
        ScanOptions options,
        ScanLog scanLog,
        int workerId,
        int ocrIntraOpThreads,
        Counters counters,
        OcrDiagnosticsWriter diagnostics,
        OcrShadowDatasetWriter? shadowDataset,
        FastOcrShadowRecorder? fastOcrShadow,
        FastOcrAssistEngine? fastOcrAssist,
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        bool normalizedRetryEnabled,
        CancellationToken token)
    {
        using var recognizer = CreateOcrRecognizer(options, outputDir, $"worker-{workerId}", ocrIntraOpThreads, token);
        var cleaner = new DriveDiscCleaner(_wikiData);
        scanLog.Write($"OCR worker {workerId} started. IntraOpThreads={ocrIntraOpThreads}.");
        while (TryTakeBatch(queue, options.OcrBatchSize, out var batch))
        {
            try
            {
                if (Volatile.Read(ref counters.StopAfterIndex) > 0)
                {
                    scanLog.Write($"OCR worker {workerId} discarded batch after stop signal. BatchSize={batch.Count}.");
                    break;
                }

                var bitmapInputSw = Stopwatch.StartNew();

                FastOcrAssistPlan?[]? assistPlans = null;
                var fastMatchMs = 0.0;
                var fastAcceptedCount = 0;
                var fastRejectedCount = 0;
                if (fastOcrAssist is not null)
                {
                    assistPlans = new FastOcrAssistPlan?[batch.Count];
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var capture = batch[i];
                        var plan = fastOcrAssist.Plan(capture.Index, capture.Rarity, capture.Image, capture.Rois);
                        assistPlans[i] = plan;
                        fastMatchMs += plan.FastMatchMs;
                        fastAcceptedCount += plan.FastAcceptedCount;
                        fastRejectedCount += plan.FastRejectedCount;
                    }
                }

                var roisByCapture = batch
                    .Select((capture, index) => (IReadOnlyList<CvRect>)(
                        options.OcrEngine == OcrEngine.PpOcrV6
                            ? PpOcrV6DetailGeometry.LoadProductionRois()
                            : assistPlans?[index]?.PpOcrRois ?? capture.Rois))
                    .ToArray();
                var evidenceByCapture = options.OcrEngine == OcrEngine.PpOcrV6
                    ? batch.Select(capture => WriteR4CaptureEvidence(outputDir, capture)).ToArray()
                    : Array.Empty<(string DetailPath, string DetailHash, string CardPath)>();
                bitmapInputSw.Stop();
                IReadOnlyList<IReadOnlyList<OcrResult>> ocrBatch;
                PaddleOcrRecognizer.OcrBatchDiagnostics? v5Diagnostics = null;
                PpOcrV6ProcessRecognizer.PpOcrV6Diagnostics? v6Diagnostics = null;
                if (recognizer is PpOcrV6ProcessRecognizer v6)
                {
                    var recognition = v6.RecognizeBatchDetailed(batch
                        .Select((capture, index) => new PpOcrV6ProcessRecognizer.PpOcrV6BatchInput(capture.Image, roisByCapture[index]))
                        .ToArray());
                    ocrBatch = recognition.Results
                        .Select(PpOcrV6DetailGeometry.ExpandForUpstreamCleaner)
                        .ToArray();
                    v6Diagnostics = recognition.Diagnostics;
                }
                else if (recognizer is PaddleOcrRecognizer v5)
                {
                    var recognition = v5.RecognizeBatchDetailed(batch
                        .Select((capture, index) => new OcrBatchInput(capture.Image, roisByCapture[index]))
                        .ToArray());
                    ocrBatch = recognition.Results;
                    v5Diagnostics = recognition.Diagnostics;
                }
                else
                {
                    ocrBatch = batch.Select((capture, index) => recognizer.Recognize(capture.Image, roisByCapture[index])).ToArray();
                }
                if (Volatile.Read(ref counters.StopAfterIndex) > 0)
                {
                    scanLog.Write($"OCR worker {workerId} discarded recognized batch after stop signal. BatchSize={batch.Count}.");
                    break;
                }

                var cleanSw = Stopwatch.StartNew();
                for (var i = 0; i < batch.Count; i++)
                {
                    var capture = batch[i];
                    var assistPlan = assistPlans?[i];
                    var ocr = assistPlan?.Merge(ocrBatch[i]) ?? ocrBatch[i];
                    try
                    {
                        var export = cleaner.Clean(capture.Index, capture.Rarity, ocr);
                        var r4Record = options.OcrEngine == OcrEngine.PpOcrV6
                            ? new R4ScanRecord(capture.Index, export, ocr.ToArray(), evidenceByCapture[i].DetailPath, evidenceByCapture[i].DetailHash, evidenceByCapture[i].CardPath, capture.LockEvidence.Result)
                            : null;
                        TryWriteFastOcrAssist(fastOcrAssistRecorder, assistPlan, ocr, scanLog);
                        TryWriteOcrShadowDataset(shadowDataset, capture, ocr, export, scanLog);
                        TryWriteFastOcrShadow(fastOcrShadow, capture, ocr, export, scanLog);
                        ocrResults.Add(OcrWorkResult.Success(
                            capture.Index,
                            export,
                            capture.TargetVerificationKind,
                            r4Record,
                            BuildOcrDetail(capture, ocr)), token);
                    }
                    catch (Exception ex)
                    {
                        if (options.OcrEngine == OcrEngine.PpOcrV6)
                        {
                            if (normalizedRetryEnabled && NeedsNormalizedOcrRetry(ocr))
                            {
                                using var normalized = VisualProbeEvaluator.NormalizeLuminance(capture.Image);
                                var retry = TryCleanPpOcrV6NormalizedRetry(
                                    ocr,
                                    () => ((PpOcrV6ProcessRecognizer)recognizer).Recognize(normalized, PpOcrV6DetailGeometry.LoadProductionRois()),
                                    cleaner,
                                    capture.Index,
                                    capture.Rarity);
                                if (retry.Export is not null && retry.Ocr is not null)
                                {
                                    var retryR4Record = new R4ScanRecord(
                                        capture.Index,
                                        retry.Export,
                                        retry.Ocr.ToArray(),
                                        evidenceByCapture[i].DetailPath,
                                        evidenceByCapture[i].DetailHash,
                                        evidenceByCapture[i].CardPath,
                                        capture.LockEvidence.Result);
                                    scanLog.WriteEvent("OCR_LUMINANCE_RETRY_ACCEPTED", $"index={capture.Index}, engine=ppocrv6, source=production_geometry_fieldwise, sourceMask={retry.SourceMask}, candidateCount={retry.CandidateCount}, originalError={SanitizeLogValue(ex.Message)}, emptyOrLowConfidence=true");
                                    TryWriteOcrShadowDataset(shadowDataset, capture, retry.Ocr, retry.Export, scanLog);
                                    TryWriteFastOcrShadow(fastOcrShadow, capture, retry.Ocr, retry.Export, scanLog);
                                    ocrResults.Add(OcrWorkResult.Success(
                                        capture.Index,
                                        retry.Export,
                                        capture.TargetVerificationKind,
                                        retryR4Record,
                                        BuildOcrDetail(capture, retry.Ocr)), token);
                                    continue;
                                }

                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_REJECTED", $"index={capture.Index}, engine=ppocrv6, source=production_geometry_fieldwise, candidateCount={retry.CandidateCount}, originalError={SanitizeLogValue(ex.Message)}, retryError={SanitizeLogValue(retry.Error ?? "normalized_retry_incomplete")}");
                            }

                            var partial = ex as DriveDiscPartialParseException;
                            var reviewRecord = new R4ScanRecord(
                                capture.Index,
                                partial?.PartialExport,
                                ocr.ToArray(),
                                evidenceByCapture[i].DetailPath,
                                evidenceByCapture[i].DetailHash,
                                evidenceByCapture[i].CardPath,
                                capture.LockEvidence.Result,
                                partial?.Diagnostic ?? DriveDiscParseDiagnostic.From(ex));
                            scanLog.WriteEvent("OCR_NEEDS_REVIEW", $"index={capture.Index}, code=ppocrv6_parse_failed, reason={SanitizeLogValue(ex.Message)}");
                            ocrResults.Add(OcrWorkResult.Review(
                                capture.Index,
                                capture.TargetVerificationKind,
                                reviewRecord,
                                BuildErrorDetail(capture, ocr, ex),
                                ex.Message), token);
                            continue;
                        }

                        if (normalizedRetryEnabled && NeedsNormalizedOcrRetry(ocr))
                        {
                            try
                            {
                                using var normalized = VisualProbeEvaluator.NormalizeLuminance(capture.Image);
                                var retryOcr = recognizer.Recognize(normalized, capture.Rois);
                                var retryExport = cleaner.Clean(capture.Index, capture.Rarity, retryOcr);
                                var retryR4Record = options.OcrEngine == OcrEngine.PpOcrV6
                                    ? new R4ScanRecord(capture.Index, retryExport, retryOcr.ToArray(), evidenceByCapture[i].DetailPath, evidenceByCapture[i].DetailHash, evidenceByCapture[i].CardPath, capture.LockEvidence.Result)
                                    : null;
                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_ACCEPTED", $"index={capture.Index}, originalError={SanitizeLogValue(ex.Message)}, emptyOrLowConfidence={NeedsNormalizedOcrRetry(ocr)}");
                                TryWriteOcrShadowDataset(shadowDataset, capture, retryOcr, retryExport, scanLog);
                                TryWriteFastOcrShadow(fastOcrShadow, capture, retryOcr, retryExport, scanLog);
                                ocrResults.Add(OcrWorkResult.Success(
                                    capture.Index,
                                    retryExport,
                                    capture.TargetVerificationKind,
                                    retryR4Record,
                                    BuildOcrDetail(capture, retryOcr)), token);
                                continue;
                            }
                            catch (Exception retryException)
                            {
                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_REJECTED", $"index={capture.Index}, originalError={SanitizeLogValue(ex.Message)}, retryError={SanitizeLogValue(retryException.Message)}");
                            }
                        }

                        var detail = BuildErrorDetail(capture, ocr, ex);
                        ocrResults.Add(OcrWorkResult.Failure(
                            capture.Index,
                            capture.TargetVerificationKind,
                            detail,
                            ex.Message), token);
                    }
                }
                cleanSw.Stop();
                var backlog = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                if (v6Diagnostics is not null)
                {
                    diagnostics.Write(workerId, batch.Count, v6Diagnostics,
                        bitmapInputSw.Elapsed.TotalMilliseconds, cleanSw.Elapsed.TotalMilliseconds, backlog);
                }
                else if (v5Diagnostics is not null)
                {
                    diagnostics.Write(
                        workerId, batch.Count, v5Diagnostics,
                        bitmapInputSw.Elapsed.TotalMilliseconds, cleanSw.Elapsed.TotalMilliseconds,
                        fallbackCount: 0, backlog, fastMatchMs, fastAcceptedCount, fastRejectedCount,
                        v5Diagnostics.RoiCount);
                }
            }
            catch (Exception ex)
            {
                foreach (var capture in batch)
                {
                    ocrResults.Add(OcrWorkResult.Failure(
                        capture.Index,
                        capture.TargetVerificationKind,
                        BuildBatchErrorDetail(capture, ex),
                        ex.Message), token);
                }

                scanLog.Write($"OCR worker {workerId} batch failed: {ex}");
            }
            finally
            {
                foreach (var capture in batch)
                {
                    capture.Dispose();
                }
            }
        }

        scanLog.Write($"OCR worker {workerId} stopped.");
    }


    private sealed class DiscCapture : IDisposable
    {
        public int Index { get; }
        public string Rarity { get; }
        public Bitmap Image { get; }
        public CvRect[] Rois { get; }
        public TargetVerificationKind TargetVerificationKind { get; }
        public LockCaptureEvidence LockEvidence { get; }

        public DiscCapture(
            int index,
            string rarity,
            Bitmap image,
            CvRect[] rois,
            TargetVerificationKind targetVerificationKind,
            LockCaptureEvidence lockEvidence)
        {
            Index = index;
            Rarity = rarity;
            Image = image;
            Rois = rois;
            TargetVerificationKind = targetVerificationKind;
            LockEvidence = lockEvidence;
        }

        public void Dispose()
        {
            Image.Dispose();
            LockEvidence.Dispose();
        }
    }


    private sealed record OcrWorkResult(
        int Index,
        DriveDiscExport? Export,
        TargetVerificationKind TargetVerificationKind,
        R4ScanRecord? R4Record,
        string? ErrorDetail,
        string? ErrorMessage)
    {
        public static OcrWorkResult Success(
            int index,
            DriveDiscExport export,
            TargetVerificationKind targetVerificationKind,
            R4ScanRecord? r4Record = null,
            string? detail = null)
        {
            return new OcrWorkResult(index, export, targetVerificationKind, r4Record, detail, null);
        }

        public static OcrWorkResult Failure(
            int index,
            TargetVerificationKind targetVerificationKind,
            string errorDetail,
            string errorMessage)
        {
            return new OcrWorkResult(index, null, targetVerificationKind, null, errorDetail, errorMessage);
        }

        public static OcrWorkResult Review(
            int index,
            TargetVerificationKind targetVerificationKind,
            R4ScanRecord r4Record,
            string errorDetail,
            string errorMessage)
        {
            return new OcrWorkResult(index, null, targetVerificationKind, r4Record, errorDetail, errorMessage);
        }
    }

}
