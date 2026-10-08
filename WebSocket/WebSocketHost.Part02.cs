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
    private static async Task SendProgressAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, ScanProgress progress, CancellationToken token)
    {
        await SendAsync(socket, sendGate, "scan_progress", new
        {
            message = progress.Message,
            visited = progress.Visited,
            queued = progress.Queued,
            completed = progress.Completed,
            failed = progress.Failed
        }, token);

        if (progress.Item is not null)
        {
            await SendAsync(socket, sendGate, "scan_item", progress.Item, token);
        }
    }

    internal static void ForwardProgressSafely(Func<Task> send, CancellationTokenSource linked)
    {
        try
        {
            send().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
            linked.Cancel();
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }

    private static async Task<string?> ReceiveTextAsync(System.Net.WebSockets.WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        await using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                }

                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Scanner WebSocket accepts text messages only.");
            }

            if (stream.Length + result.Count > MaxMessageBytes)
            {
                throw new InvalidDataException($"Scanner WebSocket message exceeds {MaxMessageBytes} bytes.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static async Task SendAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, string cmd, object? data, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), JsonDefaults.Wire);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendGate.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Wire));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    internal static bool IsAllowedBrowserOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        return origin.Equals("http://localhost:8787", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("http://127.0.0.1:8787", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zzzcaculator.top", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zzzcaculator.top:8443", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://www.zzzcaculator.top", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://www.zzzcaculator.top:8443", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zztisolation.github.io", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://jahooyoung.github.io", StringComparison.OrdinalIgnoreCase);
    }

    internal static object[] ScanFailureActions(string code)
    {
        if (string.Equals(code, "elevation_required", StringComparison.Ordinal))
        {
            return
            [
                new { kind = "restart_elevated", label = "以管理员权限重启" },
                new { kind = "open_logs", label = "打开日志目录" }
            ];
        }

        return
        [
            new { kind = "retry_scan", label = "重新扫描" },
            new { kind = "open_logs", label = "打开日志目录" }
        ];
    }

    private static void AddCorsHeaders(HttpListenerResponse response, string? origin)
    {
        if (IsAllowedBrowserOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
    }

    private sealed class HelperEnvelope
    {
        public string Cmd { get; set; } = "";
        public JsonElement Data { get; set; }

        public HelperEnvelope()
        {
        }

        public HelperEnvelope(string cmd, object? data)
        {
            Cmd = cmd;
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, JsonDefaults.Wire);
        }
    }

    internal sealed class ScanRequestPayload
    {
        public int MaxItems { get; set; }
        public string[] Rarities { get; set; } = ["S"];
        public string ResultDelivery { get; set; } = "";
        public bool StopAtNonLevel15 { get; set; } = true;
        public bool OcrShadowDataset { get; set; }
        public bool FastOcrShadow { get; set; }
        public bool FastOcrAssist { get; set; }
        public bool FastMode { get; set; }
        public bool? AdaptiveTiming { get; set; }
        public string FastOcrTemplateIndexFile { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string CaptureMode { get; set; } = "gdi";
        public string RowAdvanceMode { get; set; } = "";
        public string PanelStabilityMode { get; set; } = "";
        public string ScrollAcceptMode { get; set; } = "";
        public string PanelAcceptMode { get; set; } = "";
        public string PostScrollPanelAcceptMode { get; set; } = "";
        public string PanelFloorMode { get; set; } = "";
        public int PanelMinAcceptFloorMs { get; set; } = 120;
        public int SameRowPanelMinAcceptFloorMs { get; set; } = 105;
        public int PostScrollPanelMinAcceptFloorMs { get; set; } = 110;
        public int ScrollTickDelayMs { get; set; }
        public string OverlapConflictMode { get; set; } = "";
        public string VisualProfileId { get; set; } = "";
        public string VisualProfileQuality { get; set; } = "";
        public string VisualProfileClient { get; set; } = "";
        public string ProfileRouting { get; set; } = "";
        public string CollectVisualProfile { get; set; } = "";
    }
}
