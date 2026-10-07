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

        private async Task ForwardScannerMessageAsync(string json, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            var terminal = false;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var cmd = root.TryGetProperty("cmd", out var lowerCmd)
                    ? lowerCmd.GetString()
                    : root.TryGetProperty("Cmd", out var upperCmd) ? upperCmd.GetString() : null;
                if (cmd == "scan_progress")
                {
                    CancelStartupTimeout();
                }
                if (cmd is "scan_complete" or "scan_error")
                {
                    terminal = true;
                    FinishScanActivity();
                }
            }
            catch
            {
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await SendBrowserBytesAsync(bytes, token);
            if (terminal)
            {
                TryRestoreBrowserForeground();
                _ = Task.Run(DisposeScannerProcessAsync);
            }
        }

        private async Task SendActiveScanRequestAsync(CancellationToken token)
        {
            if (_scanner is null || string.IsNullOrWhiteSpace(_activeScanRequest))
            {
                throw new HelperFailureException(
                    "scan_request_missing",
                    "scan",
                    "扫描请求未能启动",
                    "Helper 没有找到当前扫描请求。",
                    "请重新点击“检查并开始扫描”。",
                    retryable: true);
            }

            var delivered = await _scanner.TrySendRawAsync(_activeScanRequest, token);
            if (!delivered)
            {
                await ScannerExitedAsync(_scanner.ExitCodeOrUnknown, CancellationToken.None);
            }
        }

        private Task SendScanStatusAsync(string stage, string message, CancellationToken token)
        {
            return SendAsync("scan_status", new ScanStatusMessage { Stage = stage, Message = message }, token);
        }

        private void BeginStartupTimeout()
        {
            CancelStartupTimeout();
            var timeout = new CancellationTokenSource();
            _startupTimeoutCts = timeout;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), timeout.Token);
                    await _scanTransitionGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (!_scanActivity.IsActive) return;
                        FinishScanActivity();
                        await DisposeScannerProcessAsync();
                        TryRestoreBrowserForeground();
                        await SendAsync("scan_error", new HelperErrorMessage
                        {
                            Code = "scan_start_timeout",
                            Phase = "prepare",
                            Title = "扫描启动超时",
                            Message = "本机助手没有在 45 秒内完成权限交接和仓库检查。",
                            Remedy = "请确认 UAC 提示、游戏仓库页面与窗口状态，然后重试。",
                            Retryable = true,
                            Actions = { new HelperErrorAction { Kind = "retry_scan", Label = "重新开始" } }
                        }, CancellationToken.None);
                    }
                    finally
                    {
                        _scanTransitionGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void BeginStopTimeout()
        {
            CancelStartupTimeout();
            var timeout = new CancellationTokenSource();
            _startupTimeoutCts = timeout;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), timeout.Token);
                    if (!_scanActivity.IsActive) return;
                    FinishScanActivity();
                    await DisposeScannerProcessAsync();
                    TryRestoreBrowserForeground();
                    await SendAsync("scan_error", new HelperErrorMessage
                    {
                        Code = "scan_stop_timeout",
                        Phase = "scan",
                        Title = "扫描已强制停止",
                        Message = "扫描子进程没有及时结束，Helper 已安全终止本次任务。",
                        Remedy = "可以重新开始扫描。",
                        Retryable = true,
                        Actions = { new HelperErrorAction { Kind = "retry_scan", Label = "重新开始" } }
                    }, CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void CancelStartupTimeout()
        {
            var timeout = Interlocked.Exchange(ref _startupTimeoutCts, null);
            if (timeout is null) return;
            timeout.Cancel();
            timeout.Dispose();
        }

        private async Task DisposeScannerProcessAsync()
        {
            var scanner = _scanner;
            if (scanner is null) return;
            _scanner = null;
            await scanner.DisposeAsync();
        }

        private void TryRestoreBrowserForeground()
        {
            var handle = _browserWindowBeforeScan;
            _browserWindowBeforeScan = IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            try { NativeMethods.TryActivateForegroundWindow(handle); } catch { }
        }

        private void FinishScanActivity()
        {
            CancelStartupTimeout();
            _scanActivity.Finish();
            _activeScanRequest = null;
        }

        private async Task ScannerExitedAsync(int exitCode, CancellationToken token)
        {
            if (!_scanActivity.IsActive || _browser.State != WebSocketState.Open)
            {
                return;
            }

            FinishScanActivity();

            var unsignedCode = unchecked((uint)exitCode);
            var exception = new InvalidOperationException($"Scanner exited during an active scan with code 0x{unsignedCode:X8}.");
            var diagnosticId = HelperLog.RecordException("scanner_process_exited", "scan", exception);
            await SendAsync("scan_error", new HelperErrorMessage
            {
                Code = "scanner_process_exited",
                Phase = "scan",
                Title = "Scanner 进程意外退出",
                Message = $"扫描过程中 Scanner 子进程已退出（退出码 0x{unsignedCode:X8}）。",
                Remedy = "请重新连接并扫描；持续发生时请打开日志并提供退出码。",
                Retryable = true,
                Actions =
                {
                    new HelperErrorAction { Kind = "retry_connect", Label = "重新连接" },
                    new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                },
                DiagnosticId = diagnosticId,
                Details = new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" }
            }, token);
            TryRestoreBrowserForeground();
        }

        private async Task ScannerTransportFailedAsync(Exception exception, CancellationToken token)
        {
            if (!_scanActivity.IsActive || _browser.State != WebSocketState.Open)
            {
                return;
            }

            FinishScanActivity();

            var messageTooLarge = exception is ScannerMessageTooLargeException;
            var code = messageTooLarge ? "scanner_message_too_large" : "scanner_transport_failed";
            var diagnosticId = HelperLog.RecordException(code, "scan", exception);
            await SendAsync("scan_error", new HelperErrorMessage
            {
                Code = code,
                Phase = "scan",
                Title = messageTooLarge ? "扫描结果消息过大" : "Scanner 结果传输失败",
                Message = messageTooLarge
                    ? $"Scanner 返回的单条消息超过 {MaxWebSocketMessageBytes / 1024 / 1024} MiB 安全上限。"
                    : "Helper 读取或转发 Scanner 结果时发生异常。",
                Remedy = "已识别结果会由网页安全保留；请更新 Scanner 后重试，持续发生时请打开日志。",
                Retryable = true,
                Actions =
                {
                    new HelperErrorAction { Kind = "retry_scan", Label = "重新扫描" },
                    new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                },
                DiagnosticId = diagnosticId
            }, token);
            TryRestoreBrowserForeground();
            _ = Task.Run(DisposeScannerProcessAsync);
        }

        private async Task SendAsync(string cmd, object? data, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), HelperJsonContext.Default.HelperEnvelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            await SendBrowserBytesAsync(bytes, token);
        }

        private async Task SendBrowserBytesAsync(byte[] bytes, CancellationToken token)
        {
            await _browserSendGate.WaitAsync(token);
            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    await _browser.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                }
            }
            finally
            {
                _browserSendGate.Release();
            }
        }
}

    internal sealed class ScanActivityGate
    {
        private int _active;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref _active, 1, 0) == 0;
        }

        public bool Finish()
        {
            return Interlocked.Exchange(ref _active, 0) != 0;
        }
    }

    internal static async Task RunScannerSupervisorAsync(
        Func<CancellationToken, Task> pumpMessages,
        Func<CancellationToken, Task<int>> waitForExit,
        Func<CancellationToken, Task<int>> terminateAndWait,
        Func<bool> expectedShutdown,
        Action stopMessagePump,
        Func<int, CancellationToken, Task> onExited,
        Func<Exception, CancellationToken, Task> onPumpFailed,
        Action<Exception> onPumpFault,
        CancellationToken token)
    {
        using var supervisorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumpTask = pumpMessages(supervisorCts.Token);
        var exitTask = waitForExit(supervisorCts.Token);
        var completed = await Task.WhenAny(pumpTask, exitTask);

        if (token.IsCancellationRequested || expectedShutdown())
        {
            supervisorCts.Cancel();
            stopMessagePump();
            await ObserveAsync(pumpTask, null);
            await ObserveAsync(exitTask, null);
            return;
        }

        int exitCode;
        Exception? pumpException = null;
        if (completed == exitTask)
        {
            try
            {
                exitCode = await exitTask;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || expectedShutdown())
            {
                return;
            }
            catch (Exception ex)
            {
                onPumpFault(ex);
                exitCode = await TerminateSafelyAsync(terminateAndWait, onPumpFault);
            }

            supervisorCts.Cancel();
            stopMessagePump();
            if (!token.IsCancellationRequested && !expectedShutdown())
            {
                await onExited(exitCode, CancellationToken.None);
            }

            await ObserveAsync(pumpTask, onPumpFault);
            return;
        }
        else
        {
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                pumpException = ex;
                onPumpFault(ex);
            }
            if (token.IsCancellationRequested || expectedShutdown())
            {
                supervisorCts.Cancel();
                await ObserveAsync(exitTask, null);
                return;
            }

            supervisorCts.Cancel();
            stopMessagePump();
            await ObserveAsync(exitTask, null);
            exitCode = await TerminateSafelyAsync(terminateAndWait, onPumpFault);
        }

        if (!token.IsCancellationRequested && !expectedShutdown())
        {
            if (pumpException is not null)
            {
                await onPumpFailed(pumpException, CancellationToken.None);
            }
            else
            {
                await onExited(exitCode, CancellationToken.None);
            }
        }

        static async Task<int> TerminateSafelyAsync(
            Func<CancellationToken, Task<int>> terminate,
            Action<Exception> report)
        {
            try
            {
                return await terminate(CancellationToken.None);
            }
            catch (Exception ex)
            {
                report(ex);
                return -1;
            }
        }

        static async Task ObserveAsync(Task task, Action<Exception>? report)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                report?.Invoke(ex);
            }
        }
    }
}
