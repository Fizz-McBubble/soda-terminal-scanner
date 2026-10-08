using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
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
    private const int PanelChangeTolerance = 8;
    private const int PanelStrongChangeTolerance = PanelChangeTolerance * 2;
    private const int PanelStableTolerance = 4;
    private const double MinReliablePanelChangeMs = 25.0;
    private const int ListMovementTolerance = 6;
    private const int ListStableTolerance = 4;
    private const int ScrollMaxSmallTicks = 6;
    private const int VerifiedScrollTransactionLimit = 2;
    private const int VerifiedScrollDetentBurstCount = 1;
    private const int VerifiedScrollDetentBurstIntervalMs = 20;
    private const int VerifiedScrollHoverSettleMilliseconds = 40;
    private const int VerifiedScrollReleaseSamples = 6;
    private const int VerifiedScrollReleaseStableFrames = 2;
    private const int ConsecutiveIdenticalDuplicateThreshold = 3;
    private const int SignatureColumns = 8;
    private const int SignatureRows = 4;

    private readonly ScanProfileFile _profiles;
    private readonly WikiData _wikiData;

    public ScanController(ScanProfileFile profiles, WikiData wikiData)
    {
        _profiles = profiles;
        _wikiData = wikiData;
    }

    public async Task<ScanSessionResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var requestedFastMode = options.FastMode;
        ScanModeDefaults.ApplyFastSafety(options);
        var fastModeActive = false;
        var fastModeMessage = "";
        var profileName = options.ProfileName;
        if (requestedFastMode)
        {
            if (string.IsNullOrWhiteSpace(profileName) || string.Equals(profileName, ScanOptions.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                profileName = ScanOptions.FastProfileName;
            }

            if (FastOcrTemplateIndex.TryValidateFastModeIndex(options.FastOcrTemplateIndexFile, out var resolvedIndexFile, out var validationMessage))
            {
                options.FastOcrAssist = true;
                options.FastOcrTemplateIndexFile = resolvedIndexFile;
                fastModeActive = true;
                fastModeMessage = validationMessage;
            }
            else
            {
                options.FastOcrAssist = false;
                if (string.Equals(profileName, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    profileName = ScanOptions.DefaultProfileName;
                }

                fastModeMessage = validationMessage;
            }
        }

        var profile = _profiles.Find(profileName);
        if (profile is null
            && requestedFastMode
            && fastModeActive
            && string.Equals(profileName, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase))
        {
            options.FastOcrAssist = false;
            fastModeActive = false;
            fastModeMessage = $"fast scan profile not found: {ScanOptions.FastProfileName}";
            profileName = ScanOptions.DefaultProfileName;
            profile = _profiles.ResolveRequired(profileName);
        }

        profile ??= _profiles.ResolveRequired(profileName);

        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        var results = new ConcurrentBag<DriveDiscExport>();
        var r4Records = new ConcurrentDictionary<int, R4ScanRecord>();
        int? observedTotal = null;
        string viewport = "unknown";
        var ocrWorkerCount = ResolveOcrWorkerCount(options);
        var ocrIntraOpThreads = ResolveOcrIntraOpThreads(options, ocrWorkerCount);
        var requestedQueueCapacity = Math.Max(1, options.OcrQueueCapacity);
        var queueCapacity = Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 4, requestedQueueCapacity);
        if (options.StopAtNonLevel15)
        {
            queueCapacity = Math.Min(queueCapacity, Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 2, requestedQueueCapacity));
        }
        var queue = new BlockingCollection<DiscCapture>(boundedCapacity: queueCapacity);
        var ocrResults = new BlockingCollection<OcrWorkResult>(boundedCapacity: queueCapacity);
        var counters = new Counters();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traversalMode = ResolveTraversalMode(options, profile);
        var rowAdvanceMode = ResolveRowAdvanceMode(options, profile);
        options.RowAdvanceMode = rowAdvanceMode;
        if (rowAdvanceMode == RowAdvanceMode.NativeEdgeClick
            && traversalMode != ScanTraversalMode.OverlapSignaturePage)
        {
            throw new ArgumentException("NativeEdgeClick row advance requires OverlapSignaturePage traversal.");
        }
        var duplicateGuard = new DuplicateGuard(Math.Max(1, profile.DuplicateRowThreshold));
        var adaptiveTimingActive = options.AdaptiveTiming ?? requestedFastMode;
        var panelStability = new PanelStabilitySelector(options.PanelStabilityMode);
        var runtimeState = new ScanRuntimeState(
            QuickPanelAcceptEnabled: false,
            adaptiveTimingActive,
            adaptiveTimingActive ? new AdaptiveTimingState() : null,
            adaptiveTimingActive ? new AdaptiveOcrThrottle(queueCapacity) : null,
            panelStability,
            new PanelProbeHealth(),
            new ProfileHealthGate(),
            options.PanelAcceptMode,
            options.PostScrollPanelAcceptMode,
            options.PanelFloorMode,
            Math.Clamp(options.PanelMinAcceptFloorMs, 90, 120),
            Math.Clamp(options.SameRowPanelMinAcceptFloorMs, 100, 120),
            Math.Clamp(options.PostScrollPanelMinAcceptFloorMs, 100, 120),
            EffectiveScrollTickDelay(profile, options.ScrollTickDelayOverrideMs));

        scanLog.Write($"AppVersion={AppInfo.Version}, AssemblyVersion={AppInfo.AssemblyVersion}, FileVersion={AppInfo.FileVersion}, ExecutablePath={AppInfo.ExecutablePath}, ExecutableLastWriteTime={AppInfo.ExecutableLastWriteTime?.ToString("O") ?? "unknown"}, RuntimeDirectory={AppInfo.BaseDirectory}");
        scanLog.Write($"Start scan. Profile={profile.Name}, Traversal={traversalMode}, RowAdvanceMode={rowAdvanceMode}, Process={options.ProcessName}, MaxItems={options.MaxItems}, Rarities={string.Join(",", options.Rarities)}, BringToFront={options.BringToFront}, StopAtNonLevel15={options.StopAtNonLevel15}, HighSpeedOcr={options.HighSpeedOcr}, OcrShadowDataset={options.OcrShadowDataset}, FastOcrShadow={options.FastOcrShadow}, FastOcrAssist={options.FastOcrAssist}, FastModeRequested={requestedFastMode}, FastModeActive={fastModeActive}, AdaptiveTimingRequested={options.AdaptiveTiming?.ToString() ?? "auto"}, AdaptiveTimingActive={adaptiveTimingActive}, PanelStabilityMode={options.PanelStabilityMode}, ScrollAcceptMode={options.ScrollAcceptMode}, PanelAcceptMode={options.PanelAcceptMode}, PostScrollPanelAcceptMode={options.PostScrollPanelAcceptMode}, PanelFloorMode={options.PanelFloorMode}, PanelMinAcceptFloorMs={Math.Clamp(options.PanelMinAcceptFloorMs, 90, 120)}, SameRowPanelMinAcceptFloorMs={Math.Clamp(options.SameRowPanelMinAcceptFloorMs, 100, 120)}, PostScrollPanelMinAcceptFloorMs={Math.Clamp(options.PostScrollPanelMinAcceptFloorMs, 100, 120)}, ScrollTickDelayOverrideMs={(options.ScrollTickDelayOverrideMs <= 0 ? 0 : Math.Clamp(options.ScrollTickDelayOverrideMs, 50, 80))}, EffectiveScrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, OverlapConflictMode={options.OverlapConflictMode}, CaptureModeRequested={options.CaptureMode}, CollectVisualProfile={options.CollectVisualProfile}, VisualProfileId={options.VisualProfileId}, VisualProfileClient={options.VisualProfileClient}, VisualQualityLabel={options.VisualQualityLabel}, ProfileRouting={options.ProfileRouting}, OcrWorkers={ocrWorkerCount}, OcrBatchSize={options.OcrBatchSize}, OcrQueueCapacity={queueCapacity}, OcrIntraOpThreads={ocrIntraOpThreads}");
        if (options.CollectVisualProfile)
        {
            scanLog.Write($"VISUAL_PROFILE_COLLECTION_PROTOCOL client={options.VisualProfileClient}, requestedProfile={options.VisualProfileId}, quality={options.VisualQualityLabel}, capture={options.CaptureMode}, fastOcrAssist={options.FastOcrAssist}, panelAccept={options.PanelAcceptMode}, scrollAccept={options.ScrollAcceptMode}, maxItems={options.MaxItems}");
        }
        if (options.PanelStabilityMode == PanelStabilityMode.Auto)
        {
            scanLog.Write($"Panel stability auto enabled. WarmupItems={PanelStabilitySelector.DefaultWarmupItems}, TextCoreGainMs={PanelStabilitySelector.MinimumTextCoreGainMilliseconds}.");
        }
        if (adaptiveTimingActive)
        {
            scanLog.Write($"Adaptive timing enabled for this scan only. WarmupItems={AdaptiveTimingState.DefaultWarmupItems}, OcrThrottleHigh={Math.Ceiling(queueCapacity * 0.60):F0}, OcrThrottleLow={Math.Floor(queueCapacity * 0.25):F0}.");
        }
        if (requestedFastMode)
        {
            scanLog.Write($"Fast mode {(fastModeActive ? "enabled" : "disabled")}. {fastModeMessage}");
        }
        using var ocrDiagnostics = new OcrDiagnosticsWriter(Path.Combine(outputDir, "ocr_diagnostics.csv"));
        using var ocrShadowDataset = options.OcrShadowDataset
            ? new OcrShadowDatasetWriter(outputDir, profile.OrderedRoiKeys())
            : null;
        if (ocrShadowDataset is not null)
        {
            scanLog.Write($"OCR shadow dataset enabled. Csv={Path.Combine(outputDir, "ocr_shadow.csv")}");
        }
        using var fastOcrShadow = options.FastOcrShadow
            ? FastOcrShadowRecorder.TryCreate(outputDir, options.FastOcrTemplateIndexFile, profile.OrderedRoiKeys(), scanLog.Write)
            : null;
        if (fastOcrShadow is not null)
        {
            scanLog.Write($"Fast OCR shadow enabled. Index={fastOcrShadow.IndexFile}, Templates={fastOcrShadow.TemplateCount}, Csv={Path.Combine(outputDir, "ocr_fast_shadow.csv")}");
        }
        FastOcrAssistEngine? fastOcrAssist = null;
        FastOcrAssistRecorder? fastOcrAssistRecorder = null;
        var resourceMonitor = ResourceMonitor.Start(
            Path.Combine(outputDir, "resource.csv"),
            options.ProcessName,
            () => new ResourceCounterSnapshot(
                Volatile.Read(ref counters.Visited),
                Volatile.Read(ref counters.Queued),
                Volatile.Read(ref counters.Completed),
                Volatile.Read(ref counters.Failed)),
            scanLog.Write);
        Exception? pendingException = null;
        Exception? ocrException = null;
        ScanSessionDiagnostics? sessionDiagnostics = null;
        var drainingQueuedOcrAfterFailure = false;

        try
        {
            Report(progress, counters, $"加载窗口：{options.ProcessName}");
            using var window = GameWindow.Find(options.ProcessName);
            var ppOcrGeometry = options.OcrEngine == OcrEngine.PpOcrV6
                ? PpOcrV6CaptureGeometryContract.Load()
                : null;
            ppOcrGeometry?.EnsureCompatible(profile, window.ClientScreenRect.Size);
            if (options.BringToFront)
            {
                window.BringToFront();
                // Activation refreshes client metrics; reject any changed size
                // before preflight can send navigation or selection input.
                ppOcrGeometry?.EnsureCompatible(profile, window.ClientScreenRect.Size);
            }

            window.BindCaptureContext();
            window.ConfigureCaptureMode(options.CaptureMode, scanLog.Write);

            Report(progress, counters, $"窗口客户区：{window.ClientScreenRect.Width} x {window.ClientScreenRect.Height}，DPI：{window.Dpi}，坐标倍率：{window.CoordinateScale:F2}");
            scanLog.Write($"Window client={window.ClientScreenRect}, dpi={window.Dpi}, scale={window.CoordinateScale:F2}, captureModeActive={window.ActiveCaptureMode}");
            viewport = $"{window.ClientScreenRect.Width}x{window.ClientScreenRect.Height}";
            var visualProfile = RuntimeVisualProfile.Create(
                options.ProcessName,
                options.VisualProfileId,
                options.VisualQualityLabel,
                options.VisualProfileClient,
                options.CaptureMode,
                window);
            visualProfile.ProfileRoutingDecision = options.ProfileRouting.ToString().ToLowerInvariant();
            runtimeState.VisualProfileId = visualProfile.ProfileId;
            visualProfile.Save(outputDir);
            scanLog.Write($"VISUAL_PROFILE_SELECTED id={visualProfile.ProfileId}, trainingProfile={visualProfile.TrainingProfileId}, profileFamily={visualProfile.ProfileFamilyId}, geometryStatus={visualProfile.ProfileGeometryStatus}, requested_label={visualProfile.RequestedProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}, clientKind={visualProfile.ClientKind}, quality={visualProfile.QualityLabel}, size={visualProfile.ClientWidth}x{visualProfile.ClientHeight}, dpi={visualProfile.Dpi}, captureRequested={visualProfile.CaptureModeRequested}, captureActive={visualProfile.CaptureModeActive}, frameBackend={visualProfile.CaptureFrameBackend}, profileRouting={options.ProfileRouting}");
            if (!visualProfile.ProfileId.Equals(visualProfile.DetectedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                scanLog.Write($"VISUAL_PROFILE_LABEL_GEOMETRY_MISMATCH requested_label={visualProfile.ProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}");
            }

            using var inventoryRecognizer = CreateOcrRecognizer(options, outputDir, "inventory", ocrIntraOpThreads, linked.Token);
            Task[] ocrWorkers = [];
            var resultConsumer = Task.CompletedTask;
            var captureCompleted = false;

            try
            {
                var preflight = await PrepareBackpackAsync(
                    window,
                    profile,
                    inventoryRecognizer,
                    visualProfile.ProfileId,
                    progress,
                    counters,
                    scanLog,
                    linked.Token);
                var transformClass = VisualProbeEvaluator.TransformClassName(preflight.Anchor.TransformClass);
                sessionDiagnostics = new ScanSessionDiagnostics
                {
                    ClientWidth = visualProfile.ClientWidth,
                    ClientHeight = visualProfile.ClientHeight,
                    Dpi = visualProfile.Dpi,
                    CaptureMode = visualProfile.CaptureModeActive,
                    VisualProfileId = visualProfile.ProfileId,
                    PreflightState = "accepted",
                    VisualTransformClass = transformClass,
                    AnchorScore = preflight.Anchor.Score,
                    GridScore = preflight.GridStructureScore,
                    WarehouseHeaderDetected = preflight.HeaderDetected,
                    HeaderScore = preflight.HeaderScore,
                    GridStructureScore = preflight.GridStructureScore,
                    LayoutScore = preflight.LayoutScore,
                    InventoryCountDetected = preflight.InventoryCount.HasValue,
                    CountConsensusFrames = preflight.CountConsensusFrames,
                    HueDelta = preflight.Anchor.HueDelta,
                    SaturationDeltaPct = preflight.Anchor.SaturationDeltaPercent,
                    ValueDeltaPct = preflight.Anchor.ValueDeltaPercent
                };

                var shiftedVisualEnvironment = preflight.Anchor.TransformClass != VisualTransformClass.Neutral;
                observedTotal = preflight.InventoryCount;
                if (shiftedVisualEnvironment && options.FastOcrAssist)
                {
                    options.FastOcrAssist = false;
                    scanLog.Write($"Fast OCR assist disabled for shifted visual environment. visualTransformClass={transformClass}; PP-OCR remains authoritative.");
                }

                if (options.FastOcrAssist)
                {
                    var requiredFastOcrLabels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mainStat"] = _wikiData.StatRules.SlotMainStats.Values
                            .SelectMany(stats => stats)
                            .Where(stat => !string.IsNullOrWhiteSpace(stat))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(stat => stat, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    };
                    fastOcrAssist = FastOcrAssistEngine.TryCreate(
                        options.FastOcrTemplateIndexFile,
                        profile.OrderedRoiKeys(),
                        visualProfile.ProfileId,
                        options.ProfileRouting,
                        requiredFastOcrLabels,
                        scanLog.Write);
                    fastOcrAssistRecorder = fastOcrAssist?.CreateRecorder(outputDir);
                    if (fastOcrAssist is not null)
                    {
                        scanLog.Write($"Fast OCR assist enabled. Index={fastOcrAssist.IndexFile}, Templates={fastOcrAssist.TemplateCount}, VisualProfile={fastOcrAssist.VisualProfileId}, ProfileRouting={fastOcrAssist.ProfileRoutingMode}, Csv={Path.Combine(outputDir, "ocr_fast_assist.csv")}");
                    }
                }

                var warehousePolicy = (profile.VisualProbes ?? new VisualProbeOptions()).WarehousePreflight ?? new WarehousePreflightPolicy();
                var contextGuard = new WarehouseContextGuard(
                    window,
                    preflight.MonitorPlan,
                    warehousePolicy,
                    inventoryRecognizer,
                    scanLog);
                window.ConfigureInputGuard(contextGuard.EnsureHealthy);
                window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
                await Task.Delay(profile.ClickDelayMs, linked.Token);
                await ResetListToTopAsync(window, profile, progress, counters, scanLog, preflight.InventoryCount, linked.Token);
                Report(progress, counters, "已定位到驱动盘列表顶部。");

                // v6's fixed production geometry uses the guarded field-wise
                // retry even in a neutral visual environment; legacy engines
                // keep the existing shifted-environment-only behavior.
                var normalizedRetryEnabled = shiftedVisualEnvironment || options.OcrEngine == OcrEngine.PpOcrV6;
                ocrWorkers = StartOcrWorkers(queue, ocrResults, outputDir, options, scanLog, ocrWorkerCount, ocrIntraOpThreads, counters, ocrDiagnostics, ocrShadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, normalizedRetryEnabled, linked);
                resultConsumer = StartGuardedOcrTask(() => ConsumeOcrResults(ocrResults, results, r4Records, counters, outputDir, progress, scanLog, duplicateGuard, options, linked), linked);
                await ProduceCapturesAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, preflight.InventoryCount, traversalMode, linked.Token);
                captureCompleted = true;
                Report(progress, counters, "截图采集完成，后台 OCR 正在收尾。");
            }
            catch (OperationCanceledException)
            {
                scanLog.Write("Scan canceled.");
                Report(progress, counters, "扫描已停止。");
            }
            catch (Exception ex)
            {
                pendingException = ex;
                if (ex is IScannerFailureException scannerFailure)
                {
                    Interlocked.CompareExchange(ref counters.StopReason, scannerFailure.Code, null);
                }
                scanLog.Write("Scan failed:");
                scanLog.Write(ex.ToString());
                Report(progress, counters, $"扫描失败：{ex.Message}");
            }
            finally
            {
                queue.CompleteAdding();
                var earlyStopDisposition = ScanEarlyStopPolicy.Resolve(
                    captureCompleted,
                    pendingException is not null,
                    cancellationToken.IsCancellationRequested,
                    !string.IsNullOrWhiteSpace(Volatile.Read(ref counters.StopReason)));
                if (earlyStopDisposition == ScanEarlyStopDisposition.DrainQueuedOcr)
                {
                    drainingQueuedOcrAfterFailure = true;
                    var backlog = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                    scanLog.WriteEvent(
                        "OCR_DRAIN_START",
                        $"reason=capture_failure, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, backlog={backlog}");
                }
                else if (earlyStopDisposition == ScanEarlyStopDisposition.DiscardQueuedOcr)
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, Math.Max(1, Volatile.Read(ref counters.Queued)), 0);
                    var discarded = DisposeQueuedCaptures(queue);
                    if (discarded > 0)
                    {
                        scanLog.Write($"Discarded {discarded} queued captures after early stop.");
                    }

                    linked.Cancel();
                }
            }

            try
            {
                await Task.WhenAll(ocrWorkers);
            }
            catch (Exception ex)
            {
                ocrException = ex;
                scanLog.Write("OCR worker failed:");
                scanLog.Write(ex.ToString());
            }
            finally
            {
                ocrResults.CompleteAdding();
            }

            await resultConsumer;
            if (drainingQueuedOcrAfterFailure)
            {
                var remaining = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                scanLog.WriteEvent(
                    "OCR_DRAIN_DONE",
                    $"queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, remaining={remaining}, discarded=0");
            }

            linked.Cancel();
        }
        catch (Exception ex)
        {
            pendingException ??= ex;
            scanLog.Write("Scan session failed before terminal export:");
            scanLog.Write(ex.ToString());
        }
        finally
        {
            fastOcrAssistRecorder?.Dispose();
            await resourceMonitor.StopAsync();
        }

        var ordered = results.OrderBy(x => x.Index).ToList();
        var terminationCode = Volatile.Read(ref counters.StopReason) ?? "";
        var partial = pendingException is not null
            || ocrException is not null
            || cancellationToken.IsCancellationRequested
            || counters.Failed > 0
            || !string.IsNullOrWhiteSpace(terminationCode);
        string? traversalError = partial ? "scan_partial" : options.MaxItems > 0 ? "bounded_scan" : null;
        if (options.OcrEngine == OcrEngine.PpOcrV6 && options.MaxItems == 0 && !partial)
        {
            try
            {
                var selected = counters.Traversal.Validate(observedTotal ?? -1, profile.VisibleColumns,
                    options.Rarities, counters.Visited, counters.Queued, counters.Completed,
                    counters.Failed, r4Records.Count, partial);
                ScanTraversalEvidence.EnsureImportable(selected);
                if (r4Records.Values.Any(record => record.Export is not null && !options.Rarities.Contains(record.Export.Rarity)))
                    throw new InvalidDataException("r4_selected_rarity_mismatch");
            }
            catch (Exception exception) when (exception is InvalidDataException or ScannerFailureException)
            {
                traversalError = exception is ScannerFailureException failure ? failure.Code : exception.Message;
                terminationCode = traversalError;
                partial = true;
                pendingException = exception;
            }
        }
        if (options.OcrEngine == OcrEngine.PpOcrV6)
            await counters.Traversal.WriteAsync(outputDir, observedTotal, profile.VisibleColumns,
                options.Rarities, counters.Visited, counters.Queued, counters.Completed,
                counters.Failed, r4Records.Count, partial, traversalError);
        var exportFile = Path.Combine(outputDir, partial ? "export.partial.json" : "export.json");
        await File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(ordered, JsonDefaults.Write), CancellationToken.None);
        if (options.OcrEngine == OcrEngine.PpOcrV6 && !partial)
        {
            if (!observedTotal.HasValue || observedTotal.Value <= 0)
                throw new InvalidDataException("r4_expected_total_missing");
            var r4ExpectedTotal = counters.Queued;
            var r4File = await R4StagingWriter.WriteAsync(
                outputDir, r4ExpectedTotal, viewport,
                r4Records.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
            scanLog.WriteEvent("R4_MANIFEST_READY", $"expectedTotal={r4ExpectedTotal}, warehouseTotal={observedTotal.Value}, records={r4Records.Count}, file={Path.GetFileName(r4File)}");
        }
        scanLog.WriteEvent(
            "SCAN_TERMINAL",
            $"visited={counters.Visited}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, partial={partial}, terminationCode={terminationCode}, exportFile={Path.GetFileName(exportFile)}");
        var terminalSnapshot = new ScanTerminalSnapshot(
            outputDir,
            exportFile,
            ordered.Count,
            counters.Visited,
            counters.Queued,
            counters.Completed,
            counters.Failed,
            partial,
            terminationCode);

        if (pendingException is not null)
        {
            pendingException = new ScanSessionDiagnosticException(pendingException, sessionDiagnostics, terminalSnapshot);

            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        if (ocrException is not null)
        {
            throw new ScanSessionDiagnosticException(new ScannerFailureException(
                "ocr_worker_failed",
                "OCR 识别进程失败",
                "后台 OCR 工作线程发生异常。",
                "请重新扫描；如果持续发生，请打开日志并提供诊断信息。",
                innerException: ocrException), sessionDiagnostics, terminalSnapshot);
        }

        Report(progress, counters, $"完成：输出 {ordered.Count} 条，失败 {counters.Failed} 条。");
        return new ScanSessionResult
        {
            OutputDirectory = outputDir,
            ExportFile = exportFile,
            Items = ordered,
            Visited = counters.Visited,
            Queued = counters.Queued,
            Completed = counters.Completed,
            Failed = counters.Failed,
            Partial = partial,
            TerminationCode = terminationCode,
            Diagnostics = sessionDiagnostics
        };
    }

}
