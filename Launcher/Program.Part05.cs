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

    private sealed class ManagedScannerProcess : IAsyncDisposable
    {
        private readonly ClientWebSocket _scannerWs;
        private readonly Process _process;
        private readonly CancellationTokenSource _lifetimeCts;
        private Task _supervisorTask = Task.CompletedTask;
        private int _expectedShutdown;
        private int _disposed;

        private ManagedScannerProcess(ClientWebSocket scannerWs, Process process, CancellationToken token)
        {
            _scannerWs = scannerWs;
            _process = process;
            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        public bool IsConnected => _scannerWs.State == WebSocketState.Open && !_process.HasExited;

        public int ExitCodeOrUnknown
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public static async Task<ManagedScannerProcess> StartAsync(
            SodaRuntimeIdentity runtime,
            string childToken,
            bool elevated,
            Func<string, CancellationToken, Task> forward,
            Func<int, CancellationToken, Task> onExited,
            Func<Exception, CancellationToken, Task> onPumpFailed,
            CancellationToken token)
        {
            var port = ReserveTcpPort();
            var outputRoot = Path.Combine(HelperStorageManager.DefaultDataRoot(), "outputs");
            var earlyDiagnosticPath = Path.Combine(outputRoot, $"child-{port}.early.json");
            Process process;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = runtime.EntryPath,
                    Arguments = $"--ws-child {port} --child-token {childToken} --no-browser --output-root \"{outputRoot}\" --ocr-engine ppocrv6 --ppocrv6-worker \"{runtime.PpOcrV6.WorkerPath}\" --ppocrv6-model \"{runtime.PpOcrV6.ModelPath}\" --ppocrv6-config \"{runtime.PpOcrV6.ConfigPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(runtime.EntryPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                if (elevated)
                {
                    startInfo.Verb = "runas";
                }

                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Cannot start scanner process.");
                HelperLog.Write($"SCANNER_PROCESS_STARTED pid={process.Id} entry={runtime.EntryPath} shell={startInfo.UseShellExecute} elevated={elevated} ppocrv6Identity=verified");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new HelperFailureException(
                    "uac_cancelled",
                    "launch",
                    "已取消管理员授权",
                    "你取消了 Windows 管理员权限确认，扫描器没有启动。",
                    "如游戏以管理员身份运行，请重新选择管理员启动并确认 UAC。",
                    retryable: true,
                    innerException: ex);
            }
            catch (Exception ex) when (ex is not HelperFailureException)
            {
                throw new HelperFailureException(
                    "child_start_failed",
                    "launch",
                    "无法启动 OCR 扫描器",
                    ex.Message,
                    "请选择重新下载并修复；如果问题持续，请打开日志。",
                    retryable: true,
                    innerException: ex);
            }

            var ws = new ClientWebSocket();
            try
            {
                var uri = new Uri($"ws://127.0.0.1:{port}/ws/{childToken}");
                Exception? last = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        throw ChildExited(process.ExitCode, earlyDiagnosticPath);
                    }

                    try
                    {
                        await ws.ConnectAsync(uri, token);
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        await Task.Delay(250, token);
                    }
                }

                if (ws.State != WebSocketState.Open)
                {
                    throw new HelperFailureException(
                        "child_handshake_timeout",
                        "launch",
                        "扫描器启动超时",
                        $"扫描器进程已启动，但 20 秒内没有完成本地连接：{last?.Message}",
                        "请重试；如果问题持续，请打开日志查看启动阶段错误。",
                        retryable: true);
                }

                var managed = new ManagedScannerProcess(ws, process, token);
                managed.StartSupervisor(forward, onExited, onPumpFailed);
                return managed;
            }
            catch
            {
                ws.Dispose();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process.Dispose();
                throw;
            }
        }

        public async Task<bool> TrySendRawAsync(string json, CancellationToken token)
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _scannerWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                return true;
            }
            catch (Exception ex) when (
                ex is WebSocketException or InvalidOperationException or ObjectDisposedException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                try { _scannerWs.Abort(); } catch { }
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _expectedShutdown, 1);
            _lifetimeCts.Cancel();
            try { _scannerWs.Abort(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try { await _supervisorTask; } catch { }
            _scannerWs.Dispose();
            _process.Dispose();
            _lifetimeCts.Dispose();
        }

        private void StartSupervisor(
            Func<string, CancellationToken, Task> forward,
            Func<int, CancellationToken, Task> onExited,
            Func<Exception, CancellationToken, Task> onPumpFailed)
        {
            _supervisorTask = RunScannerSupervisorAsync(
                PumpMessagesAsync,
                cancellation => WaitForExitCodeAsync(_process, cancellation),
                cancellation => TerminateAndWaitAsync(_process, cancellation),
                () => Volatile.Read(ref _expectedShutdown) != 0,
                () =>
                {
                    try { _scannerWs.Abort(); } catch { }
                },
                onExited,
                onPumpFailed,
                ex => HelperLog.RecordException("scanner_process_pump", "scan", ex),
                _lifetimeCts.Token);

            async Task PumpMessagesAsync(CancellationToken cancellation)
            {
                while (_scannerWs.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    var message = await ReceiveTextAsync(_scannerWs, cancellation);
                    if (message is null)
                    {
                        break;
                    }

                    await forward(message, cancellation);
                }
            }
        }

        private static async Task<int> WaitForExitCodeAsync(Process process, CancellationToken token)
        {
            var processId = process.Id;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsProcessAlive(processId))
                {
                    return -1;
                }

                await Task.Delay(100, token);
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using var probe = Process.GetProcessById(processId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task<int> TerminateAndWaitAsync(Process process, CancellationToken token)
        {
            if (!process.HasExited)
            {
                try
                {
                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    graceCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(graceCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            return process.ExitCode;
        }

        private static HelperFailureException ChildExited(int exitCode, string? earlyDiagnosticPath = null)
        {
            var unsignedCode = unchecked((uint)exitCode);
            var earlyCode = ReadEarlyDiagnosticCode(earlyDiagnosticPath);
            if (!string.IsNullOrWhiteSpace(earlyCode))
            {
                return new HelperFailureException(
                    earlyCode,
                    "launch",
                    "扫描器启动失败",
                    "扫描器在建立本机连接前终止。",
                    "请重新连接；如果问题持续，请打开日志并提供诊断编号。",
                    retryable: true,
                    new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}", ["phase"] = "launch" });
            }
            if (unsignedCode == 0xC0000135)
            {
                return new HelperFailureException(
                    "native_dependency_missing",
                    "launch",
                    "扫描器缺少运行组件",
                    $"扫描器启动时找不到原生 DLL（退出码 0x{unsignedCode:X8}）。",
                    "请选择重新下载并修复；VC 运行组件应已随包提供。",
                    retryable: true,
                    new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" });
            }

            return new HelperFailureException(
                "child_exited",
                "launch",
                "扫描器启动后立即退出",
                $"扫描器尚未连接就退出，退出码为 0x{unsignedCode:X8}。",
                "请选择重新下载并修复；如果问题持续，请打开日志并提供诊断编号。",
                retryable: true,
                new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" });
        }

        private static string? ReadEarlyDiagnosticCode(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var code = document.RootElement.TryGetProperty("code", out var value) ? value.GetString() : null;
                if (string.Equals(code, "child_websocket_start_failed", StringComparison.Ordinal)) return code;
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            return null;
        }

        private static int ReserveTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (origin.Equals("http://localhost:5173", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("http://127.0.0.1:5173", StringComparison.OrdinalIgnoreCase)) return true;
        return ScannerOriginPolicy.IsTrustedHttpsOrigin(origin,
            ScannerOriginPolicy.ReadAdditionalOrigin());
    }

    private static void AddCorsHeaders(HttpListenerResponse response, string? origin)
    {
        if (IsCorsReadableOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    internal static bool IsCorsReadableOrigin(string? origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https";
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ResolveJsonTypeInfo(payload.GetType())));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    private static async Task SendTextAsync(HttpListenerResponse response, int statusCode, string text, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }
}
