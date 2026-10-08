using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.Ui;
using ZZZScannerNext.WebSocket;

static partial class Program
{
    private static ScanRunCommand ParseScanRunCommand(string[] args)
    {
        var configPath = ReadOption(args, "--config");
        var command = !string.IsNullOrWhiteSpace(configPath)
            ? JsonSerializer.Deserialize<ScanRunCommand>(File.ReadAllText(configPath), JsonDefaults.Read) ?? new ScanRunCommand()
            : new ScanRunCommand();

        if (TryReadIntOption(args, "--max-items", out var maxItems))
        {
            command.MaxItems = Math.Max(0, maxItems);
        }

        if (TryReadIntOption(args, "--ocr-workers", out var ocrWorkers))
        {
            command.OcrWorkerCount = Math.Clamp(ocrWorkers, 0, 4);
        }

        if (TryReadIntOption(args, "--ocr-batch", out var ocrBatch))
        {
            command.OcrBatchSize = Math.Clamp(ocrBatch, 1, 16);
        }

        if (TryReadIntOption(args, "--ocr-queue", out var ocrQueue))
        {
            command.OcrQueueCapacity = Math.Clamp(ocrQueue, 1, 256);
        }

        if (TryReadIntOption(args, "--ocr-intra-op", out var intraOp))
        {
            command.OcrIntraOpThreads = Math.Clamp(intraOp, 1, 8);
        }

        var ocrEngine = ReadOption(args, "--ocr-engine");
        if (!string.IsNullOrWhiteSpace(ocrEngine))
        {
            command.OcrEngine = ocrEngine.Equals("ppocrv6", StringComparison.OrdinalIgnoreCase)
                ? OcrEngine.PpOcrV6
                : ocrEngine.Equals("upstream-v5-oracle", StringComparison.OrdinalIgnoreCase)
                    ? OcrEngine.UpstreamPpOcrV5Oracle
                    : throw new ArgumentException("Unknown OCR engine; expected ppocrv6 or upstream-v5-oracle.");
        }
        command.PpOcrV6WorkerPath = ReadOption(args, "--ppocrv6-worker") ?? command.PpOcrV6WorkerPath;
        command.PpOcrV6ModelPath = ReadOption(args, "--ppocrv6-model") ?? command.PpOcrV6ModelPath;
        command.PpOcrV6ConfigPath = ReadOption(args, "--ppocrv6-config") ?? command.PpOcrV6ConfigPath;

        var processName = ReadOption(args, "--process");
        if (!string.IsNullOrWhiteSpace(processName))
        {
            command.ProcessName = processName;
        }

        var profileName = ReadOption(args, "--profile");
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            command.ProfileName = profileName;
        }

        var captureMode = ReadOption(args, "--capture-mode");
        if (!string.IsNullOrWhiteSpace(captureMode))
        {
            if (Enum.TryParse<CaptureMode>(captureMode, ignoreCase: true, out var parsedCaptureMode))
            {
                command.CaptureMode = parsedCaptureMode;
            }
            else
            {
                throw new ArgumentException($"Unknown capture mode: {captureMode}. Expected gdi or dxgi.");
            }
        }

        var rowAdvanceMode = ReadOption(args, "--row-advance-mode");
        if (!string.IsNullOrWhiteSpace(rowAdvanceMode))
        {
            if (TryParseRowAdvanceMode(rowAdvanceMode, out var parsedRowAdvanceMode))
            {
                command.RowAdvanceMode = parsedRowAdvanceMode;
            }
            else
            {
                throw new ArgumentException($"Unknown row advance mode: {rowAdvanceMode}. Expected wheel or native-edge-click.");
            }
        }

        var panelStabilityMode = ReadOption(args, "--panel-stability-mode");
        if (!string.IsNullOrWhiteSpace(panelStabilityMode))
        {
            if (TryParsePanelStabilityMode(panelStabilityMode, out var parsedPanelStabilityMode))
            {
                command.PanelStabilityMode = parsedPanelStabilityMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel stability mode: {panelStabilityMode}. Expected panel, text-core, or auto.");
            }
        }

        var scrollAcceptMode = ReadOption(args, "--scroll-accept-mode");
        if (!string.IsNullOrWhiteSpace(scrollAcceptMode))
        {
            if (TryParseScrollAcceptMode(scrollAcceptMode, out var parsedScrollAcceptMode))
            {
                command.ScrollAcceptMode = parsedScrollAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown scroll accept mode: {scrollAcceptMode}. Expected safe or early-one-row.");
            }
        }

        var panelAcceptMode = ReadOption(args, "--panel-accept-mode");
        if (!string.IsNullOrWhiteSpace(panelAcceptMode))
        {
            if (TryParsePanelAcceptMode(panelAcceptMode, out var parsedPanelAcceptMode))
            {
                command.PanelAcceptMode = parsedPanelAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel accept mode: {panelAcceptMode}. Expected safe or adaptive-early-full-roi.");
            }
        }

        var postScrollPanelAcceptMode = ReadOption(args, "--post-scroll-panel-accept-mode");
        if (!string.IsNullOrWhiteSpace(postScrollPanelAcceptMode))
        {
            if (TryParsePostScrollPanelAcceptMode(postScrollPanelAcceptMode, out var parsedPostScrollPanelAcceptMode))
            {
                command.PostScrollPanelAcceptMode = parsedPostScrollPanelAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown post-scroll panel accept mode: {postScrollPanelAcceptMode}. Expected safe or adaptive-after-scroll.");
            }
        }

        if (TryReadIntOption(args, "--panel-min-accept-floor", out var panelMinAcceptFloorMs))
        {
            command.PanelMinAcceptFloorMs = Math.Clamp(panelMinAcceptFloorMs, 90, 120);
        }

        var panelFloorMode = ReadOption(args, "--panel-floor-mode");
        if (!string.IsNullOrWhiteSpace(panelFloorMode))
        {
            if (TryParsePanelFloorMode(panelFloorMode, out var parsedPanelFloorMode))
            {
                command.PanelFloorMode = parsedPanelFloorMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel floor mode: {panelFloorMode}. Expected static or scene-adaptive.");
            }
        }

        if (TryReadIntOption(args, "--same-row-panel-min-accept-floor", out var sameRowPanelMinAcceptFloorMs))
        {
            command.SameRowPanelMinAcceptFloorMs = Math.Clamp(sameRowPanelMinAcceptFloorMs, 100, 120);
        }

        if (TryReadIntOption(args, "--post-scroll-panel-min-accept-floor", out var postScrollPanelMinAcceptFloorMs))
        {
            command.PostScrollPanelMinAcceptFloorMs = Math.Clamp(postScrollPanelMinAcceptFloorMs, 100, 120);
        }

        if (TryReadIntOption(args, "--scroll-tick-delay-ms", out var scrollTickDelayMs))
        {
            command.ScrollTickDelayOverrideMs = Math.Clamp(scrollTickDelayMs, 50, 80);
        }

        var overlapConflictMode = ReadOption(args, "--overlap-conflict-mode");
        if (!string.IsNullOrWhiteSpace(overlapConflictMode))
        {
            if (TryParseOverlapConflictMode(overlapConflictMode, out var parsedOverlapConflictMode))
            {
                command.OverlapConflictMode = parsedOverlapConflictMode;
            }
            else
            {
                throw new ArgumentException($"Unknown overlap conflict mode: {overlapConflictMode}. Expected strict, recheck, or recover.");
            }
        }

        var collectVisualProfile = ReadOption(args, "--collect-visual-profile");
        if (!string.IsNullOrWhiteSpace(collectVisualProfile))
        {
            command.CollectVisualProfile = true;
            command.VisualProfileId = collectVisualProfile;
            command.OcrShadowDataset = true;
            command.FastMode = false;
            command.FastOcrAssist = false;
            command.FastOcrShadow = false;
            command.AdaptiveTiming = false;
            command.PanelAcceptMode = PanelAcceptMode.Safe;
            command.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            command.ScrollAcceptMode = ScrollAcceptMode.Safe;
            command.PanelStabilityMode = PanelStabilityMode.Panel;
            command.PanelFloorMode = PanelFloorMode.Static;
            command.PanelMinAcceptFloorMs = 120;
            command.OverlapConflictMode = OverlapConflictMode.Recover;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                command.ProfileName = ScanOptions.FastProfileName;
            }
        }

        var rarities = ReadOption(args, "--rarities");
        if (!string.IsNullOrWhiteSpace(rarities))
        {
            command.Rarities = rarities
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }

        if (args.Any(arg => string.Equals(arg, "--include-non15", StringComparison.OrdinalIgnoreCase)))
        {
            command.StopAtNonLevel15 = false;
        }

        if (args.Any(arg => string.Equals(arg, "--no-bring-to-front", StringComparison.OrdinalIgnoreCase)))
        {
            command.BringToFront = false;
        }

        if (args.Any(arg => string.Equals(arg, "--low-speed-ocr", StringComparison.OrdinalIgnoreCase)))
        {
            command.HighSpeedOcr = false;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-shadow-dataset", StringComparison.OrdinalIgnoreCase)))
        {
            command.OcrShadowDataset = true;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-fast-shadow", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastOcrShadow = true;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-fast-assist", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastOcrAssist = true;
        }

        if (args.Any(arg => string.Equals(arg, "--fast-mode", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastMode = true;
            command.FastOcrAssist = true;
            if (string.IsNullOrWhiteSpace(panelStabilityMode))
            {
                command.PanelStabilityMode = PanelStabilityMode.TextCore;
            }

            if (string.IsNullOrWhiteSpace(profileName))
            {
                command.ProfileName = ScanOptions.FastProfileName;
            }

            if (string.IsNullOrWhiteSpace(scrollAcceptMode))
            {
                command.ScrollAcceptMode = ScanModeDefaults.ScrollAccept(true);
            }

            if (string.IsNullOrWhiteSpace(panelAcceptMode))
            {
                command.PanelAcceptMode = ScanModeDefaults.PanelAccept(true);
            }

            if (string.IsNullOrWhiteSpace(overlapConflictMode))
            {
                command.OverlapConflictMode = ScanModeDefaults.OverlapConflict(true);
            }
        }

        if (args.Any(arg => string.Equals(arg, "--adaptive-timing", StringComparison.OrdinalIgnoreCase)))
        {
            command.AdaptiveTiming = true;
        }

        if (args.Any(arg => string.Equals(arg, "--no-adaptive-timing", StringComparison.OrdinalIgnoreCase)))
        {
            command.AdaptiveTiming = false;
        }

        var fastOcrIndex = ReadOption(args, "--ocr-fast-index");
        if (!string.IsNullOrWhiteSpace(fastOcrIndex))
        {
            command.FastOcrTemplateIndexFile = fastOcrIndex;
        }

        var visualProfile = ReadOption(args, "--visual-profile");
        if (!string.IsNullOrWhiteSpace(visualProfile))
        {
            command.VisualProfileId = visualProfile;
        }

        var visualQuality = ReadOption(args, "--visual-quality");
        visualQuality ??= ReadOption(args, "--visual-profile-quality");
        if (!string.IsNullOrWhiteSpace(visualQuality))
        {
            command.VisualQualityLabel = visualQuality;
        }

        var visualClient = ReadOption(args, "--visual-profile-client");
        if (!string.IsNullOrWhiteSpace(visualClient))
        {
            if (TryParseVisualProfileClientKind(visualClient, out var parsedClientKind))
            {
                command.VisualProfileClient = parsedClientKind;
            }
            else
            {
                throw new ArgumentException($"Unknown visual profile client: {visualClient}. Expected auto, local, cloud, or unknown.");
            }
        }

        var profileRouting = ReadOption(args, "--profile-routing");
        if (!string.IsNullOrWhiteSpace(profileRouting))
        {
            if (TryParseProfileRoutingMode(profileRouting, out var parsedProfileRouting))
            {
                command.ProfileRouting = parsedProfileRouting;
            }
            else
            {
                throw new ArgumentException($"Unknown profile routing mode: {profileRouting}. Expected strict, family, compatible, or auto.");
            }
        }

        if (!string.IsNullOrWhiteSpace(collectVisualProfile))
        {
            command.CollectVisualProfile = true;
            command.VisualProfileId = collectVisualProfile;
            command.OcrShadowDataset = true;
            command.FastMode = false;
            command.FastOcrAssist = false;
            command.FastOcrShadow = false;
            command.AdaptiveTiming = false;
            command.PanelAcceptMode = PanelAcceptMode.Safe;
            command.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            command.ScrollAcceptMode = ScrollAcceptMode.Safe;
            command.PanelStabilityMode = PanelStabilityMode.Panel;
            command.PanelFloorMode = PanelFloorMode.Static;
            command.PanelMinAcceptFloorMs = 120;
            command.OverlapConflictMode = OverlapConflictMode.Recover;
        }

        return command;
    }

    private static ScanOptions BuildScanOptions(ScanRunCommand command)
    {
        var options = new ScanOptions
        {
            ProcessName = command.ProcessName,
            ProfileName = command.ProfileName,
            TraversalMode = command.TraversalMode,
            RowAdvanceMode = command.RowAdvanceMode,
            MaxItems = Math.Max(0, command.MaxItems),
            BringToFront = command.BringToFront,
            StopAtNonLevel15 = command.StopAtNonLevel15,
            HighSpeedOcr = command.HighSpeedOcr,
            OcrShadowDataset = command.OcrShadowDataset,
            FastOcrShadow = command.FastOcrShadow,
            FastOcrAssist = command.FastOcrAssist,
            FastMode = command.FastMode,
            AdaptiveTiming = command.AdaptiveTiming,
            CaptureMode = command.CaptureMode,
            PanelStabilityMode = command.PanelStabilityMode,
            ScrollAcceptMode = command.ScrollAcceptMode,
            PanelAcceptMode = command.PanelAcceptMode,
            PostScrollPanelAcceptMode = command.PostScrollPanelAcceptMode,
            PanelFloorMode = command.PanelFloorMode,
            PanelMinAcceptFloorMs = Math.Clamp(command.PanelMinAcceptFloorMs, 90, 120),
            SameRowPanelMinAcceptFloorMs = Math.Clamp(command.SameRowPanelMinAcceptFloorMs, 100, 120),
            PostScrollPanelMinAcceptFloorMs = Math.Clamp(command.PostScrollPanelMinAcceptFloorMs, 100, 120),
            ScrollTickDelayOverrideMs = command.ScrollTickDelayOverrideMs <= 0 ? 0 : Math.Clamp(command.ScrollTickDelayOverrideMs, 50, 80),
            OverlapConflictMode = command.OverlapConflictMode,
            FastOcrTemplateIndexFile = command.FastOcrTemplateIndexFile,
            VisualProfileId = command.VisualProfileId,
            VisualQualityLabel = command.VisualQualityLabel,
            VisualProfileClient = command.VisualProfileClient,
            CollectVisualProfile = command.CollectVisualProfile,
            ProfileRouting = command.ProfileRouting,
            OcrBatchSize = Math.Clamp(command.OcrBatchSize, 1, 16),
            OcrWorkerCount = Math.Clamp(command.OcrWorkerCount, 0, 4),
            OcrQueueCapacity = Math.Clamp(command.OcrQueueCapacity, 1, 256),
            OcrIntraOpThreads = Math.Clamp(command.OcrIntraOpThreads, 1, 8)
            ,
            OcrEngine = command.OcrEngine
            ,
            PpOcrV6WorkerPath = command.PpOcrV6WorkerPath
            ,
            PpOcrV6ModelPath = command.PpOcrV6ModelPath
            ,
            PpOcrV6ConfigPath = command.PpOcrV6ConfigPath
        };

        options.Rarities.Clear();
        foreach (var rarity in command.Rarities.Where(rarity => !string.IsNullOrWhiteSpace(rarity)))
        {
            options.Rarities.Add(rarity.Trim());
        }

        if (options.Rarities.Count == 0)
        {
            options.Rarities.Add("S");
        }

        return options;
    }
}
