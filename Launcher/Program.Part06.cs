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
                throw new InvalidDataException("Helper WebSocket accepts text messages only.");
            }

            EnsureScannerMessageSize(stream.Length, result.Count);

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    internal static void EnsureScannerMessageSize(long currentLength, int incomingLength)
    {
        if (currentLength < 0 || incomingLength < 0 || currentLength + incomingLength > MaxWebSocketMessageBytes)
        {
            throw new ScannerMessageTooLargeException(MaxWebSocketMessageBytes);
        }
    }

    internal sealed class ScannerMessageTooLargeException(int maximumBytes)
        : IOException($"Helper WebSocket message exceeds {maximumBytes} bytes.");

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
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, ResolveJsonTypeInfo(data.GetType()));
        }

    }

    private sealed class TokenRequest
    {
        public string Origin { get; set; } = "";
    }

    private sealed class TokenResponse
    {
        public string Token { get; set; } = "";
    }

    private sealed class HelperInfoResponse
    {
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class HelperHello
    {
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class ScannerState
    {
        public string? Version { get; set; }
        public bool Installed { get; set; }
        public string? Entry { get; set; }
        public string? PackageId { get; set; }
        public string? PackageMode { get; set; }
        public bool? DesktopRuntimeAvailable { get; set; }
    }

    private sealed class LauncherProgress
    {
        public string Stage { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Version { get; set; }
        public string? Url { get; set; }
        public long? BytesDownloaded { get; set; }
        public long? TotalBytes { get; set; }
        public double? Percent { get; set; }
        public double? BytesPerSecond { get; set; }
        public int? Attempt { get; set; }
        public int? MaxAttempts { get; set; }
        public string? PackageId { get; set; }
        public string? PackageMode { get; set; }
        public string? SelectionReason { get; set; }
        public long? RequiredBytes { get; set; }
    }

    internal static bool RequiresElevatedScannerChild(string processName)
    {
        using var process = Process.GetProcessesByName(processName).FirstOrDefault(candidate =>
        {
            try { return candidate.MainWindowHandle != IntPtr.Zero; }
            catch { return false; }
        });
        return process is not null && NativeMethods.RequiresElevationForProcess(process.Id);
    }

    private sealed class ScanStatusMessage
    {
        public string Stage { get; set; } = "";
        public string Message { get; set; } = "";
    }

    private sealed class HelperDiagnosticsResponse
    {
        public string RequestId { get; set; } = "";
        public string HelperVersion { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public string LogDirectory { get; set; } = "";
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class HelperUpdateCommitResponse
    {
        public string RequestId { get; set; } = "";
        public string TransactionId { get; set; } = "";
        public bool Committed { get; set; }
        public string PreviousVersion { get; set; } = "";
    }

    private sealed class StorageInfoResponse
    {
        public string RequestId { get; set; } = "";
        public HelperStorageSnapshot Storage { get; set; } = new();
    }

    private sealed class StorageCleanupResponse
    {
        public string RequestId { get; set; } = "";
        public HelperStorageCleanupResult Result { get; set; } = new();
    }

    private sealed class PongMessage
    {
        public DateTimeOffset Time { get; set; }
    }
}
