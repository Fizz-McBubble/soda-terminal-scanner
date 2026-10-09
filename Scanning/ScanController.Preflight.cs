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
    private static async Task<VisualPreflightResult> PrepareBackpackAsync(
        GameWindow window,
        ScanProfile profile,
        IOcrRecognizer inventoryRecognizer,
        string visualProfileId,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog,
        CancellationToken token)
    {
        Report(progress, counters, "等待背包驱动盘界面。");
        var visualOptions = profile.VisualProbes ?? new VisualProbeOptions();
        var warehousePolicy = visualOptions.WarehousePreflight ?? new WarehousePreflightPolicy();
        var aspectRatio = window.ClientScreenRect.Height <= 0
            ? 0
            : window.ClientScreenRect.Width / (double)window.ClientScreenRect.Height;
        if (Math.Abs(aspectRatio - (16d / 9d)) > 0.03)
        {
            throw VisualPreflightException.Create(
                "unsupported_display_layout",
                "游戏客户区不是受支持的 16:9 布局，本次没有继续点击或滚动。",
                default,
                headerDetected: false,
                headerScore: 0,
                gridStructureScore: 0,
                layoutScore: 0,
                inventoryCountDetected: false,
                countConsensusFrames: 0,
                stableFrames: 0,
                warehousePolicy.RequiredStableFrames,
                window,
                visualProfileId);
        }

        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, Math.Max(1, profile.VisibleRows), Math.Max(1, profile.VisibleColumns)));
        var detailPanelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var probeBounds = Rectangle.Union(Rectangle.Union(headerRect, listGridRect), detailPanelRect);
        probeBounds = Rectangle.Intersect(window.ClientScreenRect, probeBounds);
        var driveDiscOffset = window.ToScreenPoint(profile.Point("driveDiscOffset"));
        var driveDiscStepNormalized = profile.Point("driveDiscStep");
        var driveDiscStep = window.ToClientSize(new SizeF(driveDiscStepNormalized.X, driveDiscStepNormalized.Y));
        var requiredStableFrames = Math.Max(1, warehousePolicy.RequiredStableFrames);
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(warehousePolicy.PollMilliseconds, 100, 1000));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, profile.WaitForBackpackSeconds));
        var gate = new VisualPreflightGate(requiredStableFrames);
        var lastHealth = new CaptureHealthResult(false, 0, 0, 0, 100, 0, 0);
        var lastHeader = new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        var lastStructure = new WarehouseStructureProbeResult(0, 0, 0, 0, 0, 0, 0);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            using var frame = window.Capture(probeBounds);
            lastHealth = WarehousePreflightEvaluator.EvaluateCaptureHealth(frame);
            var localHeaderRect = ToLocalRectangle(headerRect, probeBounds, frame.Size);
            var localListGridRect = ToLocalRectangle(listGridRect, probeBounds, frame.Size);
            var localDetailPanelRect = ToLocalRectangle(detailPanelRect, probeBounds, frame.Size);
            lastHeader = lastHealth.Passed
                ? ReadWarehouseHeader(frame, localHeaderRect, inventoryRecognizer, warehousePolicy, scanLog)
                : new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
            lastStructure = lastHealth.Passed
                ? WarehousePreflightEvaluator.EvaluateStructure(
                    frame,
                    localListGridRect,
                    localDetailPanelRect,
                    new Point(driveDiscOffset.X - probeBounds.Left, driveDiscOffset.Y - probeBounds.Top),
                    driveDiscStep)
                : new WarehouseStructureProbeResult(0, 0, 0, 0, 0, 0, 0);
            var gridPassed = lastStructure.GridStructureScore >= warehousePolicy.GridMinimumScore;
            var layoutPassed = lastStructure.LayoutScore >= warehousePolicy.LayoutMinimumScore;
            var accepted = gate.Observe(lastHealth.Passed, lastHeader.HeaderDetected, gridPassed, layoutPassed);
            scanLog.WriteEvent(
                "VISUAL_PREFLIGHT",
                $"captureHealthy={lastHealth.Passed}, captureScore={lastHealth.Score}, meanLuma={lastHealth.MeanLuminance}, lumaStdDev={lastHealth.LuminanceStandardDeviation}, darkPct={lastHealth.DarkPixelPercent}, brightPct={lastHealth.BrightPixelPercent}, edgeDensityPermille={lastHealth.EdgeDensityPermille}, headerDetected={lastHeader.HeaderDetected}, headerScore={lastHeader.HeaderScore}, titleEditDistance={lastHeader.TitleEditDistance}, headerConfidence={lastHeader.Confidence:F3}, normalizedRetry={lastHeader.UsedNormalizedImage}, inventoryCountDetected={lastHeader.InventoryCountDetected}, gridCells={lastStructure.RecognizedGridCells}/6, gridStructureScore={lastStructure.GridStructureScore}, gridEdgeDensityPermille={lastStructure.GridEdgeDensityPermille}, layoutScore={lastStructure.LayoutScore}, layoutEdgeDensityPermille={lastStructure.LayoutEdgeDensityPermille}, verticalLineScore={lastStructure.VerticalLineScore}, horizontalLineScore={lastStructure.HorizontalLineScore}, stableFrames={gate.StableFrames}/{requiredStableFrames}, capture={window.ActiveCaptureMode}");

            // Empty warehouses have no grid. Confirm zero independently and stop
            // before game input, without weakening the navigation gate for any scan.
            if (lastHealth.Passed && lastHeader.HeaderDetected && lastHeader.InventoryCount == 0)
            {
                var emptyConsensus = await ReadInventoryCountConsensusAsync(window, profile,
                    inventoryRecognizer, warehousePolicy, scanLog, token);
                if (emptyConsensus.InventoryCount == 0) ScanTraversalEvidence.EnsureImportable(0);
            }

            if (accepted)
            {
                scanLog.Write($"Warehouse page confirmed without input. headerScore={lastHeader.HeaderScore}, gridStructureScore={lastStructure.GridStructureScore}, layoutScore={lastStructure.LayoutScore}.");
                var consensus = await ReadInventoryCountConsensusAsync(
                    window,
                    profile,
                    inventoryRecognizer,
                    warehousePolicy,
                    scanLog,
                    token);
                if (!consensus.InventoryCount.HasValue)
                {
                    throw InventoryCountOcrFailure(
                        "已确认驱动盘仓库，但仓库数量未能在独立画面中形成一致结果；本次没有继续点击或滚动。",
                        ScanDiagnosticDetails.Preflight(
                            "inventory_count_ocr_failed",
                            "unknown",
                            anchorScore: 0,
                            lastStructure.GridStructureScore,
                            lastHeader.HeaderDetected,
                            lastHeader.HeaderScore,
                            lastStructure.GridStructureScore,
                            lastStructure.LayoutScore,
                            inventoryCountDetected: false,
                            consensus.ConsensusFrames,
                            hueDelta: 0,
                            saturationDeltaPct: 0,
                            valueDeltaPct: 0,
                            gate.StableFrames,
                            requiredStableFrames,
                            window.ClientScreenRect.Width,
                            window.ClientScreenRect.Height,
                            window.Dpi,
                            window.ActiveCaptureMode,
                            visualProfileId));
                }

                var monitorPlan = CreateWarehouseMonitorPlan(window, profile);
                var backpackPolicy = visualOptions.BackpackReady ?? new ChromaticProbePolicy();
                var center = window.ToScreenPoint(profile.Point("dismantleButton"));
                var radiusX = Math.Max(4, (int)Math.Round(backpackPolicy.Radius * window.ClientScreenRect.Width / (double)profile.StandardScreen[0]));
                var radiusY = Math.Max(4, (int)Math.Round(backpackPolicy.Radius * window.ClientScreenRect.Height / (double)profile.StandardScreen[1]));
                var anchorRect = Rectangle.Intersect(
                    window.ClientScreenRect,
                    Rectangle.FromLTRB(center.X - radiusX, center.Y - radiusY, center.X + radiusX + 1, center.Y + radiusY + 1));
                using var anchorImage = window.Capture(anchorRect);
                var anchor = VisualProbeEvaluator.EvaluateChromaticAnchor(anchorImage, profile.Color("dismantleButton"), backpackPolicy);
                scanLog.Write($"Post-confirmation color diagnostic. passed={anchor.Passed}, score={anchor.Score}, transform={VisualProbeEvaluator.TransformClassName(anchor.TransformClass)}. This result does not affect warehouse readiness.");
                return new VisualPreflightResult(
                    anchor,
                    lastHeader.HeaderDetected,
                    lastHeader.HeaderScore,
                    lastStructure.GridStructureScore,
                    lastStructure.LayoutScore,
                    consensus.InventoryCount,
                    consensus.InventoryCapacity,
                    consensus.ConsensusFrames,
                    monitorPlan);
            }

            await Task.Delay(poll, token);
        }

        var structuralEvidence = lastStructure.GridStructureScore >= warehousePolicy.GridMinimumScore
            || lastStructure.LayoutScore >= warehousePolicy.LayoutMinimumScore;
        var partialWarehouseEvidence = lastHeader.HeaderDetected || structuralEvidence;
        var reason = !lastHealth.Passed
            ? "capture_unavailable"
            : partialWarehouseEvidence
                ? "inventory_screen_unreadable"
                : "inventory_screen_not_detected";
        scanLog.Write($"Visual preflight failed. reason={reason}, captureScore={lastHealth.Score}, headerScore={lastHeader.HeaderScore}, gridStructureScore={lastStructure.GridStructureScore}, layoutScore={lastStructure.LayoutScore}, stableFrames={gate.StableFrames}/{requiredStableFrames}.");
        throw VisualPreflightException.Create(
            reason,
            "未能安全确认驱动盘仓库界面。请保持游戏可见并确认仓库布局正常；本次没有继续点击或滚动。",
            default,
            lastHeader.HeaderDetected,
            lastHeader.HeaderScore,
            lastStructure.GridStructureScore,
            lastStructure.LayoutScore,
            lastHeader.InventoryCountDetected,
            countConsensusFrames: 0,
            gate.StableFrames,
            requiredStableFrames,
            window,
            visualProfileId);
    }

    private static async Task ResetListToTopAsync(
        GameWindow window,
        ScanProfile profile,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog,
        int? inventoryCount,
        CancellationToken token)
    {
        var visibleCapacity = Math.Max(1, profile.VisibleColumns) * Math.Max(1, profile.VisibleRows);
        if (inventoryCount is > 0 && inventoryCount <= visibleCapacity)
        {
            scanLog.WriteEvent("RESET_TOP_CONFIRMED",
                $"phase=all_items_visible, inventoryCount={inventoryCount}, visibleCapacity={visibleCapacity}, wheelTicks=0, clicks=0");
            return;
        }

        Report(progress, counters, "正在将驱动盘列表拉到最上方。");
        var scrollTop = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var scrollBottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var wheelPoint = window.ToScreenPoint(profile.Point("listWheelArea"));
        var expectedColor = profile.Color("scrollBar");
        var maximumWheelTicks = Math.Min(
            Math.Max(0, profile.ResetToTopWheelTicks),
            ScrollTopResetCoordinator.MaximumWheelTicks);
        window.MoveCursor(wheelPoint);
        var resetDelay = Math.Clamp(profile.ResetToTopWheelDelayMs, 20, 80);
        var fallbackResetAction = "not_run";
        var lastResetThumb = default(ScrollbarThumbProbe);
        var resetThumbHeight = 0;
        var resetClientHeight = window.ClientScreenRect.Height;
        var maximumThumbHeight = ScrollbarTopResetPlanner.MaximumScrollableThumbHeight(
            scrollTop.Y, scrollBottom.Y, inventoryCount, profile.VisibleColumns, profile.VisibleRows);
        const int resetWheelDelta = 120 * 16;

        bool IsValidResetThumb(ScrollbarThumbProbe thumb) => thumb.Found
            && ScrollbarTopResetPlanner.IsScrollableThumb(
                scrollTop.Y, scrollBottom.Y, thumb.StartY, thumb.EndY)
            && thumb.EndY - thumb.StartY + 1 <= maximumThumbHeight;

        void ObserveResetThumb(ScrollbarThumbProbe thumb)
        {
            if (resetThumbHeight == 0 && IsValidResetThumb(thumb))
                resetThumbHeight = thumb.EndY - thumb.StartY + 1;
        }

        void Trace(ScrollTopResetTrace trace)
        {
            switch (trace.Kind)
            {
                case ScrollTopResetTraceKind.Probe:
                    scanLog.WriteEvent(
                        "RESET_TOP_COLOR_PROBE",
                        $"phase={trace.Phase}, batch={trace.Batch}, sample={trace.Sample}/{ScrollTopResetCoordinator.ProbeSampleCount}, tick={trace.WheelTicks}/{maximumWheelTicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, delta=({Math.Abs(trace.ActualColor.R - trace.ExpectedColor.R)},{Math.Abs(trace.ActualColor.G - trace.ExpectedColor.G)},{Math.Abs(trace.ActualColor.B - trace.ExpectedColor.B)}), tolerance={trace.Tolerance}, matched={trace.Matched}, matchReason={trace.MatchReason}, stableMatches={trace.StableMatches}/{ScrollTopResetCoordinator.RequiredStableMatches}, thumbFound={lastResetThumb.Found}, thumbX={lastResetThumb.CenterX}, thumb={lastResetThumb.StartY}-{lastResetThumb.EndY}, thumbReferenceHeight={resetThumbHeight}, thumbHeightTolerance={ScrollbarTopResetPlanner.HeightTolerancePixels(resetClientHeight)}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Wheel:
                    scanLog.WriteEvent(
                        "RESET_WHEEL",
                        $"batch={trace.Batch}, event={trace.WheelTicks}/{maximumWheelTicks}, delta={resetWheelDelta}, cursor={wheelPoint}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Settle:
                    scanLog.WriteEvent(
                        "RESET_TOP_SETTLE",
                        $"phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, delayMs={trace.DelayMilliseconds}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Click:
                    scanLog.WriteEvent("RESET_TOP_FAST_WHEEL",
                        $"phase={trace.Phase}, delta={resetWheelDelta}, cursor={wheelPoint}, attempt={trace.TopClicks}, wheelEvents={trace.WheelTicks}/{maximumWheelTicks}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Confirmed:
                    scanLog.WriteEvent(
                        "RESET_TOP_CONFIRMED",
                        $"phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, clicks={trace.TopClicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, stableMatches={trace.StableMatches}/{ScrollTopResetCoordinator.RequiredStableMatches}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Failed:
                    scanLog.WriteEvent(
                        "RESET_TOP_FAILED",
                        $"reason=scroll_top_position_unconfirmed, phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, clicks={trace.TopClicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, tolerance={trace.Tolerance}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
            }
        }

        var result = await ScrollTopResetCoordinator.RunAsync(
            maximumWheelTicks,
            resetDelay,
            profile.ClickDelayMs,
            expectedColor,
            profile.ColorTolerance,
            () =>
            {
                lastResetThumb = CaptureScrollbarThumbProbe(window, profile);
                ObserveResetThumb(lastResetThumb);
                return window.GetPixel(scrollTop);
            },
            () => window.MouseWheel(resetWheelDelta),
            () =>
            {
                token.ThrowIfCancellationRequested();
                window.MoveCursor(wheelPoint);
                fallbackResetAction = "fast_wheel";
                window.MouseWheel(resetWheelDelta);
            },
            Trace,
            token,
            captureTopPosition: () =>
                IsValidResetThumb(lastResetThumb)
                && ScrollbarTopResetPlanner.HasConsistentHeight(
                    resetThumbHeight, lastResetThumb.StartY, lastResetThumb.EndY, resetClientHeight)
                && ScrollbarTopResetPlanner.IsAtTop(
                    scrollTop.Y,
                    scrollBottom.Y,
                    lastResetThumb.StartY,
                    lastResetThumb.EndY),
            resetBeforeProbe: true);
        if (!result.Confirmed)
        {
            throw NavigationFailure(
                "未能自动回到驱动盘列表顶部。请确认游戏窗口可操作后重试；本次已停止，扫描结果未导入。",
                new Dictionary<string, object?>
                {
                    ["phase"] = "reset_to_top",
                    ["reason"] = "scroll_top_position_unconfirmed",
                    ["acceptGateReason"] = "scroll_top_position_unconfirmed",
                    ["wheelTicks"] = result.WheelTicks,
                    ["topClicks"] = result.TopClicks,
                    ["fallbackResetAction"] = fallbackResetAction,
                    ["probeSamples"] = result.ProbeSamples,
                    ["actualColor"] = ColorText(result.LastActualColor),
                    ["expectedColor"] = ColorText(expectedColor),
                    ["colorTolerance"] = profile.ColorTolerance,
                    ["elapsedMs"] = result.ElapsedMilliseconds,
                    ["positionFound"] = lastResetThumb.Found,
                    ["actualThumbStart"] = lastResetThumb.Found ? lastResetThumb.StartY - window.ClientScreenRect.Top : null,
                    ["actualThumbEnd"] = lastResetThumb.Found ? lastResetThumb.EndY - window.ClientScreenRect.Top : null
                });
        }

        scanLog.Write($"Reset confirmed the scrollbar thumb at the top via {result.Phase}; wheelTicks={result.WheelTicks}, clicks={result.TopClicks}, elapsedMs={result.ElapsedMilliseconds}.");
    }

}
