using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using ZZZScannerNext.Interop;

namespace ZZZScannerHelper;

internal static partial class Program
{

    private sealed partial class BrowserSession : IAsyncDisposable
    {
        private readonly HelperServer _server;
        private readonly System.Net.WebSockets.WebSocket _browser;
        private readonly SemaphoreSlim _browserSendGate = new(1, 1);
        private readonly SemaphoreSlim _scanTransitionGate = new(1, 1);
        private ManagedScannerProcess? _scanner;
        private readonly ScanActivityGate _scanActivity = new();
        private string? _activeScanRequest;
        private CancellationTokenSource? _startupTimeoutCts;
        private IntPtr _browserWindowBeforeScan;

        public BrowserSession(HelperServer server, System.Net.WebSockets.WebSocket browser)
        {
            _server = server;
            _browser = browser;
        }

        public async Task RunAsync(CancellationToken token)
        {
            await SendAsync("hello", new HelperHello
            {
                Service = ServiceName,
                Version = HelperVersion,
                ProtocolVersion = ProtocolVersion,
                Scanner = _server.CurrentScannerState(),
                HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
            }, token);

            while (_browser.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(_browser, token);
                if (json is null)
                {
                    break;
                }

                HelperEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperEnvelope);
                }
                catch
                {
                    continue;
                }

                if (envelope is null)
                {
                    continue;
                }

                try
                {
                    await HandleEnvelopeAsync(envelope, token);
                }
                catch (Exception ex)
                {
                    FinishScanActivity();
                    await DisposeScannerProcessAsync();
                    TryRestoreBrowserForeground();
                    await SendAsync("scan_error", HelperErrors.FromException(ex, "prepare"), CancellationToken.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            CancelStartupTimeout();
            if (_scanner is not null)
            {
                await _scanner.DisposeAsync();
                _scanner = null;
            }

            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    await _browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
            }
            catch
            {
            }

            _browser.Dispose();
            _browserSendGate.Dispose();
            _scanTransitionGate.Dispose();
        }

        private async Task HandleEnvelopeAsync(HelperEnvelope envelope, CancellationToken token)
        {
            switch (envelope.Cmd)
            {
                case "ping":
                    await SendAsync("pong", new PongMessage { Time = DateTimeOffset.Now }, token);
                    break;

                case "ensure_scanner":
                    await EnsureScannerProcessAsync(token);
                    break;

                case "repair_scanner":
                    await EnsureScannerProcessAsync(token, forceRepair: true);
                    break;

                case "restart_scanner_elevated":
                    await EnsureScannerProcessAsync(token, elevated: true, forceRestart: true);
                    break;

                case "open_log_folder":
                    OpenLogFolder();
                    await SendAsync("launcher_progress", new LauncherProgress
                    {
                        Stage = "logs",
                        Message = "已打开 Helper 日志目录。"
                    }, token);
                    break;

                case "get_diagnostics":
                    await SendAsync("helper_diagnostics", new HelperDiagnosticsResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        HelperVersion = HelperVersion,
                        ProtocolVersion = ProtocolVersion,
                        LogDirectory = HelperLog.DirectoryPath,
                        Scanner = _server.CurrentScannerState(),
                        HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
                    }, token);
                    break;

                case "confirm_helper_update":
                    var confirmRequestId = RequestId(envelope.Data);
                    try
                    {
                        var transactionId = StringProperty(envelope.Data, "transactionId");
                        var commit = HelperInstallationManager.ConfirmPendingUpdate(transactionId);
                        await SendAsync("helper_update_commit_result", new HelperUpdateCommitResponse
                        {
                            RequestId = confirmRequestId,
                            TransactionId = commit.TransactionId,
                            Committed = commit.Committed,
                            PreviousVersion = commit.PreviousVersion,
                        }, token);
                    }
                    catch (Exception ex)
                    {
                        var error = HelperErrors.FromException(ex, "helper_update");
                        error.Code = "helper_update_confirmation_failed";
                        error.Phase = "helper";
                        error.Title = "扫描助手更新确认失败";
                        error.Remedy = "旧版 Helper 会自动恢复；请等待网页重新连接后重试。";
                        error.Retryable = true;
                        error.Details["requestId"] = confirmRequestId;
                        await SendAsync("helper_update_error", error, CancellationToken.None);
                    }
                    break;

                case "get_storage_info":
                    await SendAsync("storage_info", new StorageInfoResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        Storage = _server.CurrentStorageInfo()
                    }, token);
                    break;

                case "cleanup_storage":
                    var cleanup = _server.CleanupStorage();
                    await SendAsync("storage_cleanup_result", new StorageCleanupResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        Result = cleanup
                    }, token);
                    break;

                case "update_helper":
                    var updateRequestId = RequestId(envelope.Data);
                    try
                    {
                        var preparation = await HelperUpdateManager.PrepareAsync(
                            _server.ResolveHelperManifestUrl(),
                            HelperVersion,
                            async (progress, ct) =>
                            {
                                progress.RequestId = updateRequestId;
                                await SendAsync("helper_update_progress", progress, ct);
                            },
                            token);
                        await SendAsync("helper_update_result", new HelperUpdateResponse
                        {
                            RequestId = updateRequestId,
                            UpdateAvailable = preparation.UpdateAvailable,
                            CurrentVersion = preparation.CurrentVersion,
                            AvailableVersion = preparation.AvailableVersion,
                            Restarting = preparation.UpdateAvailable
                        }, token);
                        if (preparation.UpdateAvailable)
                        {
                            HelperInstallationManager.LaunchPreparedUpdate(preparation.ExecutablePath);
                            await Task.Delay(250, CancellationToken.None);
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var source = HelperErrors.FromException(ex, "helper_update");
                        var sourceCode = source.Code;
                        source.Code = "helper_update_failed";
                        source.Phase = "helper";
                        source.Title = "扫描助手更新失败";
                        source.Remedy = "请检查网络后重试；仍然失败时请手动下载最新版 Helper。";
                        source.Retryable = true;
                        source.Actions =
                        [
                            new HelperErrorAction { Kind = "update_helper", Label = "重试自动更新" },
                            new HelperErrorAction { Kind = "download_helper", Label = "手动下载" },
                            new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                        ];
                        source.Details["requestId"] = updateRequestId;
                        source.Details["sourceCode"] = sourceCode;
                        await SendAsync("helper_update_error", source, CancellationToken.None);
                    }
                    break;

                case "scan_req":
                    if (!_scanActivity.TryStart())
                    {
                        await SendScanStatusAsync("checking", "扫描请求正在处理中，请等待当前检查完成。", token);
                        break;
                    }

                    _activeScanRequest = JsonSerializer.Serialize(envelope, HelperJsonContext.Default.HelperEnvelope);
                    _browserWindowBeforeScan = NativeMethods.GetForegroundWindow();
                    var orphanCleanup = ScannerChildReaper.CleanupOrphans(
                        SodaRuntimeLocator.DefaultInstallRoot(),
                        Environment.ProcessId);
                    HelperLog.Write($"SCAN_REQUEST_ORPHAN_CHECK examined={orphanCleanup.Examined} terminated={orphanCleanup.Terminated} skipped={orphanCleanup.Skipped}");
                    var processName = StringProperty(envelope.Data, "processName");
                    if (string.IsNullOrWhiteSpace(processName)) processName = "ZenlessZoneZero";
                    var elevated = RequiresElevatedScannerChild(processName);
                    BeginStartupTimeout();
                    await SendScanStatusAsync(
                        elevated ? "awaiting_elevation" : "checking",
                        elevated
                            ? "游戏需要更高权限，正在等待 Windows 管理员确认。"
                            : "正在切换游戏并核验仓库页面。",
                        token);
                    try
                    {
                        await EnsureScannerProcessAsync(
                            token,
                            elevated: elevated,
                            forceRestart: true);
                        if (!_scanActivity.IsActive)
                        {
                            await DisposeScannerProcessAsync();
                            return;
                        }
                        await SendScanStatusAsync("checking", "扫描器已连接，正在切换游戏并核验仓库页面。", token);
                        await SendActiveScanRequestAsync(token);
                    }
                    catch
                    {
                        FinishScanActivity();
                        await DisposeScannerProcessAsync();
                        TryRestoreBrowserForeground();
                        throw;
                    }
                    break;

                case "scan_stop":
                    if (_scanner is not null)
                    {
                        var delivered = await _scanner.TrySendRawAsync(
                            JsonSerializer.Serialize(envelope, HelperJsonContext.Default.HelperEnvelope),
                            token);
                        if (!delivered)
                        {
                            await ScannerExitedAsync(_scanner.ExitCodeOrUnknown, CancellationToken.None);
                        }
                        else
                        {
                            BeginStopTimeout();
                        }
                    }
                    else
                    {
                        await ScannerExitedAsync(-1, CancellationToken.None);
                    }
                    break;
            }
        }

        private async Task EnsureScannerProcessAsync(
            CancellationToken token,
            bool forceRepair = false,
            bool elevated = false,
            bool forceRestart = false)
        {
            if (!forceRestart && !forceRepair && _scanner is { IsConnected: true })
            {
                await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
                return;
            }

            if (_scanner is not null)
            {
                await _scanner.DisposeAsync();
                _scanner = null;
            }

            await SendAsync("launcher_progress", new LauncherProgress { Stage = "queue", Message = "正在准备扫描器任务..." }, token);
            await _server.EnsureSodaScannerAsync(
                (progress, ct) => SendAsync("launcher_progress", progress, ct),
                token);

            var childToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _scanner = await ManagedScannerProcess.StartAsync(
                _server.CurrentSodaRuntime(),
                childToken,
                elevated,
                ForwardScannerMessageAsync,
                ScannerExitedAsync,
                ScannerTransportFailedAsync,
                token);
            _server.MarkScannerActive();
            await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
            if (HelperInstallationManager.ConsumePostUpdateStoragePreservation())
            {
                HelperLog.Write("AUTOMATIC_CLEANUP_SKIPPED reason=helper-update-preservation");
            }
            else
            {
                try
                {
                    _server.CleanupStorage();
                }
                catch (Exception ex)
                {
                    HelperLog.Write($"AUTOMATIC_CLEANUP_FAILED error={ex.Message}");
                }
            }
        }

        private static string RequestId(JsonElement data)
        {
            return data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("requestId", out var requestId)
                ? requestId.GetString() ?? ""
                : "";
        }

        private static string StringProperty(JsonElement data, string name)
        {
            return data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(name, out var value)
                ? value.GetString() ?? ""
                : "";
        }

        private static void OpenLogFolder()
        {
            Directory.CreateDirectory(HelperLog.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{HelperLog.DirectoryPath}\"",
                UseShellExecute = true
            });
        }
}
}
