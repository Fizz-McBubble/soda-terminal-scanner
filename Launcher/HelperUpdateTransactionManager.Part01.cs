using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZScannerHelper;

internal static partial class HelperUpdateTransactionManager
{
    private static void CleanupOldConfirmationMarkers()
    {
        var root = UpdateRoot();
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(root, "confirmed-*.txt"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        await RetryIoAsync(() => File.Move(source, destination, overwrite: true));
    }

    private static async Task CopyWithRetryAsync(string source, string destination)
    {
        await RetryIoAsync(() => File.Copy(source, destination, overwrite: false));
    }

    private static async Task ReplaceWithRetryAsync(string source, string destination, string backup)
    {
        await RetryIoAsync(() => File.Replace(source, destination, backup, ignoreMetadataErrors: true));
    }

    private static async Task DeleteWithRetryAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        await RetryIoAsync(() => File.Delete(path));
    }

    private static async Task RetryIoAsync(Action action)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < 40)
                {
                    await Task.Delay(250);
                }
            }
        }
        throw new IOException("Helper update file operation did not complete within 10 seconds.", last);
    }

    private static async Task VerifySameFileAsync(string source, string destination)
    {
        var sourceHash = await FileSha256Async(source);
        var destinationHash = await FileSha256Async(destination);
        if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Managed Helper copy failed SHA-256 verification.");
        }
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Split(new[] { '+', '-' }, 2, StringSplitOptions.None)[0];
    }

    private static bool PathEquals(string left, string right)
    {
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [JsonSerializable(typeof(HelperUpdateTransactionReceipt))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
    private sealed partial class HelperUpdateTransactionJsonContext : JsonSerializerContext
    {
    }
}

internal sealed class HelperUpdateTransactionReceipt
{
    public string TransactionId { get; set; } = "";
    public string State { get; set; } = "pending";
    public string Stage { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string UpdaterPath { get; set; } = "";
    public string CandidateSha256 { get; set; } = "";
    public int? ManagedProcessId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class HelperUpdateTransactionInfo
{
    public string State { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
}

internal sealed class HelperUpdateCommitResult
{
    public string TransactionId { get; set; } = "";
    public bool Committed { get; set; }
    public string PreviousVersion { get; set; } = "";
}
