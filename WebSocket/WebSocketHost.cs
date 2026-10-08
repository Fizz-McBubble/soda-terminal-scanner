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
    private const int MaxMessageBytes = 256 * 1024;

    private readonly ScanController _controller;
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly int _port;
    private readonly string? _connectionToken;
    private readonly PpOcrV6RuntimeIdentity? _ppocrv6Runtime;
    private bool _disposed;

    public WebSocketHost(ScanProfileFile profiles, WikiData wikiData, int port, string? connectionToken = null)
        : this(profiles, wikiData, port, connectionToken, ppocrv6Runtime: null)
    {
    }

    internal WebSocketHost(
        ScanProfileFile profiles,
        WikiData wikiData,
        int port,
        string? connectionToken,
        PpOcrV6RuntimeIdentity? ppocrv6Runtime)
    {
        _controller = new ScanController(profiles, wikiData);
        _port = port > 0 ? port : throw new ArgumentOutOfRangeException(nameof(port));
        _connectionToken = string.IsNullOrWhiteSpace(connectionToken) ? null : connectionToken;
        _ppocrv6Runtime = ppocrv6Runtime;
    }

    public string? BrowserUrl { get; set; }

    public async Task RunAsync(CancellationToken token)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        if (!string.IsNullOrWhiteSpace(BrowserUrl))
        {
            TryOpenBrowser(BrowserUrl);
        }

        while (!token.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync().WaitAsync(token);
            _ = Task.Run(() => HandleContextAsync(context, token), token);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            var origin = context.Request.Headers["Origin"];
            AddCorsHeaders(context.Response, origin);
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = IsAllowedBrowserOrigin(origin) ? 204 : 403;
                context.Response.Close();
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                await SendJsonAsync(context.Response, 200, new
                {
                    service = "zzz-scanner",
                    version = AppInfo.Version,
                    scanner = AppInfo.DiagnosticPayload()
                }, token);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!IsAllowedWebSocketPath(path)
                || (_connectionToken is null && !IsAllowedBrowserOrigin(origin)))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await RunSessionAsync(wsContext.WebSocket, token);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private bool IsAllowedWebSocketPath(string path)
    {
        if (_connectionToken is null)
        {
            return path.Equals("/ws", StringComparison.OrdinalIgnoreCase);
        }

        return path.Equals($"/ws/{Uri.EscapeDataString(_connectionToken)}", StringComparison.OrdinalIgnoreCase)
            || path.Equals($"/ws/{_connectionToken}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunSessionAsync(System.Net.WebSockets.WebSocket socket, CancellationToken hostToken)
    {
        using var socketCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        CancellationTokenSource? scanCts = null;
        Task? scanTask = null;

        await SendAsync(socket, sendGate, "hello", new { service = "zzz-scanner", version = AppInfo.Version, scanner = AppInfo.DiagnosticPayload() }, socketCts.Token);

        while (socket.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, socketCts.Token);
            if (json is null)
            {
                break;
            }

            HelperEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<HelperEnvelope>(json, JsonDefaults.Read);
            }
            catch
            {
                continue;
            }

            if (envelope is null)
            {
                continue;
            }

            switch (envelope.Cmd)
            {
                case "ping":
                    await SendAsync(socket, sendGate, "pong", new { time = DateTimeOffset.Now, scanner = AppInfo.DiagnosticPayload() }, socketCts.Token);
                    break;

                case "child_probe":
                    await SendAsync(socket, sendGate, "child_probe", new
                    {
                        ready = true,
                        ppocrv6Identity = _ppocrv6Runtime is not null,
                        outputSessionCreated = false,
                        accountWriteEnabled = false,
                        importAccess = false
                    }, socketCts.Token);
                    break;

                case "scan_req":
                    if (scanTask is { IsCompleted: false })
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_busy",
                            phase = "scan",
                            title = "已有扫描任务",
                            message = "已有扫描任务正在进行。",
                            remedy = "请等待当前任务完成，或先停止当前扫描。",
                            retryable = true,
                            actions = Array.Empty<object>()
                        }, socketCts.Token);
                        break;
                    }

                    ScanRequestPayload payload;
                    try
                    {
                        payload = envelope.Data.Deserialize<ScanRequestPayload>(JsonDefaults.Read) ?? new ScanRequestPayload();
                    }
                    catch (JsonException)
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_request_invalid",
                            phase = "scan",
                            title = "扫描请求无效",
                            message = "扫描请求格式无效。",
                            remedy = "请刷新 calculator 页面后重试。",
                            retryable = true,
                            actions = new[] { new { kind = "retry_scan", label = "重新扫描" } }
                        }, socketCts.Token);
                        break;
                    }

                    if (!await _scanGate.WaitAsync(0, socketCts.Token))
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_busy",
                            phase = "scan",
                            title = "扫描器正被占用",
                            message = "另一个连接正在执行扫描任务。",
                            remedy = "请关闭其他 calculator 页面，或等待当前扫描完成。",
                            retryable = true,
                            actions = Array.Empty<object>()
                        }, socketCts.Token);
                        break;
                    }

                    scanCts?.Dispose();
                    scanCts = CancellationTokenSource.CreateLinkedTokenSource(socketCts.Token);
                    scanTask = RunScanWithGateAsync(socket, sendGate, payload, scanCts.Token);
                    break;

                case "scan_stop":
                    await SendAsync(socket, sendGate, "scan_stop_ack", new
                    {
                        accepted = scanTask is { IsCompleted: false },
                        time = DateTimeOffset.UtcNow,
                        scanner = AppInfo.DiagnosticPayload()
                    }, CancellationToken.None);
                    scanCts?.Cancel();
                    break;
            }
        }

        scanCts?.Cancel();
        if (scanTask is not null)
        {
            try { await scanTask; } catch { }
        }

        scanCts?.Dispose();
    }

    private async Task RunScanWithGateAsync(
        System.Net.WebSockets.WebSocket socket,
        SemaphoreSlim sendGate,
        ScanRequestPayload payload,
        CancellationToken token)
    {
        try
        {
            await RunScanAsync(socket, sendGate, payload, token);
        }
        finally
        {
            _scanGate.Release();
        }
    }
}
