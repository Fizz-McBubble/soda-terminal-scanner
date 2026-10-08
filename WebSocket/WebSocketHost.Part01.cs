using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.WebSocket;

public sealed partial class WebSocketHost : IDisposable
{
    private async Task RunScanAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, ScanRequestPayload payload, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lastProgressTicks = DateTime.UtcNow.Ticks;
        var timeoutTriggered = 0;
        var visited = 0;
        var queued = 0;
        var completed = 0;
        var failed = 0;
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
                    var inactiveFor = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks)));
                    await SendAsync(socket, sendGate, "scan_heartbeat", new
                    {
                        time = DateTimeOffset.UtcNow,
                        inactiveMs = (long)inactiveFor.TotalMilliseconds,
                        visited = Volatile.Read(ref visited),
                        queued = Volatile.Read(ref queued),
                        completed = Volatile.Read(ref completed),
                        failed = Volatile.Read(ref failed),
                        scanner = AppInfo.DiagnosticPayload()
                    }, CancellationToken.None);
                    if (inactiveFor < TimeSpan.FromSeconds(180))
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref timeoutTriggered, 1);
                    linked.Cancel();
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            var options = BuildOptions(payload);
            EnsurePpOcrV6RuntimeIdentity(options);
            GameWindow.EnsureCurrentProcessCanAccess(options.ProcessName);
            var progress = new InlineProgress<ScanProgress>(progress =>
            {
                Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref visited, progress.Visited);
                Volatile.Write(ref queued, progress.Queued);
                Volatile.Write(ref completed, progress.Completed);
                Volatile.Write(ref failed, progress.Failed);
                ForwardProgressSafely(
                    () => SendProgressAsync(socket, sendGate, progress, linked.Token),
                    linked);
            });

            var result = await _controller.ScanAsync(options, progress, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var streamedResult = string.Equals(payload.ResultDelivery, "stream-items-v1", StringComparison.OrdinalIgnoreCase);
            await SendAsync(socket, sendGate, "scan_complete", new
            {
                resultDelivery = streamedResult ? "stream-items-v1" : "legacy-items",
                items = streamedResult ? null : result.Items,
                itemCount = result.Items.Count,
                visited = result.Visited,
                queued = result.Queued,
                completed = result.Completed,
                failed = result.Failed,
                partial = result.Partial,
                terminationCode = string.IsNullOrWhiteSpace(result.TerminationCode) ? null : result.TerminationCode,
                outputDirectory = result.OutputDirectory,
                exportFile = result.ExportFile,
                scanner = AppInfo.DiagnosticPayload(),
                diagnostics = result.Diagnostics
            }, linked.Token);
        }
        catch (OperationCanceledException)
        {
            var noProgress = Volatile.Read(ref timeoutTriggered) == 1;
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = noProgress ? "scan_no_progress_timeout" : "scan_cancelled",
                phase = "scan",
                severity = noProgress ? "error" : "warning",
                title = noProgress ? "扫描长时间没有进展" : "扫描已停止",
                message = noProgress ? "Scanner 连续 180 秒没有产生新的扫描进展，任务已停止。" : "扫描已停止。",
                remedy = noProgress ? "请确认游戏仍在驱动盘仓库且未被遮挡，然后重新扫描。" : "可以调整选项后重新开始扫描。",
                retryable = true,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = new
                {
                    inactiveMs = noProgress ? 180_000 : 0,
                    visited = Volatile.Read(ref visited),
                    queued = Volatile.Read(ref queued),
                    completed = Volatile.Read(ref completed),
                    failed = Volatile.Read(ref failed)
                },
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (ScannerElevationRequiredException ex)
        {
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = ex.Code,
                phase = "scan",
                title = ex.Title,
                message = ex.Message,
                remedy = ex.Remedy,
                retryable = ex.Retryable,
                actions = ScanFailureActions(ex.Code),
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (VisualPreflightException ex)
        {
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = ex.Code,
                phase = "scan",
                title = ex.Title,
                message = ex.Message,
                remedy = ex.Remedy,
                retryable = ex.Retryable,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = ex.DiagnosticDetails,
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IScannerFailureException)
        {
            var ex = (IScannerFailureException)exception;
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = ex.Code,
                phase = "scan",
                title = ex.Title,
                message = exception.Message,
                remedy = ex.Remedy,
                retryable = ex.Retryable,
                actions = ScanFailureActions(ex.Code),
                details = ex.DiagnosticDetails,
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var gameNotFound = ex.Message.Contains("未找到游戏窗口进程", StringComparison.Ordinal);
            var foregroundFailed = ex.Message.Contains("game_foreground_handoff_failed", StringComparison.Ordinal);
            var ppocrIdentityMissing = ex.Message.Contains("ppocrv6_runtime_identity_missing", StringComparison.Ordinal);
            var code = gameNotFound
                ? "game_not_found"
                : foregroundFailed
                    ? "game_foreground_handoff_failed"
                    : ppocrIdentityMissing ? "ppocrv6_runtime_identity_missing" : "scan_failed";
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code,
                phase = "scan",
                title = gameNotFound
                    ? "未找到绝区零窗口"
                    : foregroundFailed
                        ? "无法切换到游戏"
                        : ppocrIdentityMissing ? "扫描组件识别身份不完整" : "扫描失败",
                message = ppocrIdentityMissing ? "扫描组件的识别文件校验信息不完整，扫描尚未开始。" : ex.Message,
                remedy = gameNotFound
                    ? "请启动游戏并进入驱动盘背包后重试。"
                    : foregroundFailed
                        ? "请取消其他置顶窗口，保持游戏未最小化，然后重试。"
                        : ppocrIdentityMissing
                            ? "请从扫描页重新修复本机组件后重试。"
                        : "请重试；如果问题持续，请打开 Helper 日志。",
                retryable = true,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = ScanDiagnosticDetails.FromException(ex),
                error = ex.ToString(),
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        finally
        {
            linked.Cancel();
            try { await heartbeatTask; } catch { }
        }
    }

    internal ScanOptions BuildOptions(ScanRequestPayload payload)
    {
        var options = new ScanOptions
        {
            MaxItems = Math.Max(0, payload.MaxItems),
            StopAtNonLevel15 = payload.StopAtNonLevel15,
            OcrShadowDataset = payload.OcrShadowDataset,
            FastOcrShadow = payload.FastOcrShadow,
            FastOcrAssist = payload.FastMode || payload.FastOcrAssist,
            FastMode = payload.FastMode,
            AdaptiveTiming = payload.AdaptiveTiming,
            FastOcrTemplateIndexFile = payload.FastOcrTemplateIndexFile,
            CaptureMode = ParseCaptureMode(payload.CaptureMode),
            RowAdvanceMode = ParseRowAdvanceMode(payload.RowAdvanceMode),
            PanelStabilityMode = ParsePanelStabilityMode(payload.PanelStabilityMode, payload.FastMode),
            ScrollAcceptMode = string.IsNullOrWhiteSpace(payload.ScrollAcceptMode) && payload.FastMode
                ? ScanModeDefaults.ScrollAccept(true)
                : ParseScrollAcceptMode(payload.ScrollAcceptMode),
            PanelAcceptMode = string.IsNullOrWhiteSpace(payload.PanelAcceptMode) && payload.FastMode
                ? ScanModeDefaults.PanelAccept(true)
                : ParsePanelAcceptMode(payload.PanelAcceptMode),
            PostScrollPanelAcceptMode = ParsePostScrollPanelAcceptMode(payload.PostScrollPanelAcceptMode),
            PanelFloorMode = ParsePanelFloorMode(payload.PanelFloorMode),
            PanelMinAcceptFloorMs = Math.Clamp(payload.PanelMinAcceptFloorMs <= 0 ? 120 : payload.PanelMinAcceptFloorMs, 90, 120),
            SameRowPanelMinAcceptFloorMs = Math.Clamp(payload.SameRowPanelMinAcceptFloorMs <= 0 ? 105 : payload.SameRowPanelMinAcceptFloorMs, 100, 120),
            PostScrollPanelMinAcceptFloorMs = Math.Clamp(payload.PostScrollPanelMinAcceptFloorMs <= 0 ? 110 : payload.PostScrollPanelMinAcceptFloorMs, 100, 120),
            ScrollTickDelayOverrideMs = payload.ScrollTickDelayMs <= 0 ? 0 : Math.Clamp(payload.ScrollTickDelayMs, 50, 80),
            OverlapConflictMode = string.IsNullOrWhiteSpace(payload.OverlapConflictMode) && payload.FastMode
                ? ScanModeDefaults.OverlapConflict(true)
                : ParseOverlapConflictMode(payload.OverlapConflictMode),
            VisualProfileId = string.IsNullOrWhiteSpace(payload.VisualProfileId) ? "auto" : payload.VisualProfileId,
            VisualQualityLabel = string.IsNullOrWhiteSpace(payload.VisualProfileQuality) ? "current" : payload.VisualProfileQuality,
            VisualProfileClient = ParseVisualProfileClient(payload.VisualProfileClient),
            ProfileRouting = ParseProfileRouting(payload.ProfileRouting),
            CollectVisualProfile = !string.IsNullOrWhiteSpace(payload.CollectVisualProfile)
        };

        if (!string.IsNullOrWhiteSpace(payload.ProcessName))
        {
            options.ProcessName = payload.ProcessName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.CollectVisualProfile))
        {
            options.VisualProfileId = payload.CollectVisualProfile;
            if (string.IsNullOrWhiteSpace(payload.ProfileName))
            {
                options.ProfileName = ScanOptions.FastProfileName;
            }

            options.OcrShadowDataset = true;
            options.FastMode = false;
            options.FastOcrAssist = false;
            options.FastOcrShadow = false;
            options.AdaptiveTiming = false;
            options.PanelAcceptMode = PanelAcceptMode.Safe;
            options.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            options.ScrollAcceptMode = ScrollAcceptMode.Safe;
            options.PanelStabilityMode = PanelStabilityMode.Panel;
            options.PanelFloorMode = PanelFloorMode.Static;
            options.PanelMinAcceptFloorMs = 120;
            options.OverlapConflictMode = OverlapConflictMode.Recover;
        }

        if (!string.IsNullOrWhiteSpace(payload.ProfileName))
        {
            options.ProfileName = payload.ProfileName;
        }
        else if (payload.FastMode)
        {
            options.ProfileName = ScanOptions.FastProfileName;
        }

        options.Rarities.Clear();
        options.Rarities.Add("S");
        if (_ppocrv6Runtime is not null)
        {
            options.OcrEngine = OcrEngine.PpOcrV6;
            options.PpOcrV6WorkerPath = _ppocrv6Runtime.WorkerPath;
            options.PpOcrV6ModelPath = _ppocrv6Runtime.ModelPath;
            options.PpOcrV6ConfigPath = _ppocrv6Runtime.ConfigPath;
        }

        return options;
    }

    private static void EnsurePpOcrV6RuntimeIdentity(ScanOptions options)
    {
        if (options.OcrEngine == OcrEngine.PpOcrV6
            && (string.IsNullOrWhiteSpace(options.PpOcrV6WorkerPath)
                || string.IsNullOrWhiteSpace(options.PpOcrV6ModelPath)
                || string.IsNullOrWhiteSpace(options.PpOcrV6ConfigPath)))
        {
            throw new InvalidDataException("ppocrv6_runtime_identity_missing");
        }
    }

    private static CaptureMode ParseCaptureMode(string? value)
    {
        return Enum.TryParse<CaptureMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : CaptureMode.Gdi;
    }

    private static PanelStabilityMode ParsePanelStabilityMode(string? value, bool fastMode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelStabilityMode.Panel;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelStabilityMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelStabilityMode.Panel;
    }

    private static ScrollAcceptMode ParseScrollAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ScrollAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<ScrollAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : ScrollAcceptMode.Safe;
    }

    private static RowAdvanceMode? ParseRowAdvanceMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(normalized, "wheel", StringComparison.OrdinalIgnoreCase))
        {
            return RowAdvanceMode.WheelVerified;
        }

        return Enum.TryParse<RowAdvanceMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : null;
    }

    private static PanelAcceptMode ParsePanelAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelAcceptMode.Safe;
    }

    private static PostScrollPanelAcceptMode ParsePostScrollPanelAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PostScrollPanelAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PostScrollPanelAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PostScrollPanelAcceptMode.Safe;
    }

    private static PanelFloorMode ParsePanelFloorMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelFloorMode.Static;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelFloorMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelFloorMode.Static;
    }

    private static OverlapConflictMode ParseOverlapConflictMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OverlapConflictMode.Recheck;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<OverlapConflictMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : OverlapConflictMode.Recheck;
    }

    private static VisualProfileClientKind ParseVisualProfileClient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VisualProfileClientKind.Auto;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<VisualProfileClientKind>(normalized, ignoreCase: true, out var mode)
            ? mode
            : VisualProfileClientKind.Auto;
    }

    private static ProfileRoutingMode ParseProfileRouting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ProfileRoutingMode.Strict;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<ProfileRoutingMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : ProfileRoutingMode.Strict;
    }
}
