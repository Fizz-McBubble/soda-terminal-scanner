using System.Security.Cryptography;
using System.Text.Json;

namespace ZZZScannerNext.WebSocket;

/// <summary>
/// The elevated WebSocket child re-validates the three PP-OCRv6 assets supplied
/// by its non-elevated Helper parent.  <c>runas</c> does not inherit process
/// environment, so this is deliberately command-line/descriptor based.
/// </summary>
internal sealed record PpOcrV6RuntimeIdentity(string WorkerPath, string ModelPath, string ConfigPath)
{
    private const string WorkerRelativePath = "ocr/Soda.ScannerPpOcrV6.exe";
    private const string ModelRelativePath = "models/PP-OCRv6_small_rec_onnx/inference.onnx";
    private const string ConfigRelativePath = "models/PP-OCRv6_small_rec_onnx/inference.yml";

    internal static bool TryFromChildArguments(
        string scannerBaseDirectory,
        string? workerPath,
        string? modelPath,
        string? configPath,
        out PpOcrV6RuntimeIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(workerPath)
            || string.IsNullOrWhiteSpace(modelPath)
            || string.IsNullOrWhiteSpace(configPath))
        {
            return false;
        }

        var nativeDirectory = Path.GetFullPath(scannerBaseDirectory);
        var runtimeRoot = Directory.GetParent(nativeDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return false;
        }

        var expectedWorker = Path.Combine(runtimeRoot, WorkerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var expectedModel = Path.Combine(runtimeRoot, ModelRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var expectedConfig = Path.Combine(runtimeRoot, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!PathsEqual(workerPath, expectedWorker)
            || !PathsEqual(modelPath, expectedModel)
            || !PathsEqual(configPath, expectedConfig)
            || !VerifyDescriptorEntry(runtimeRoot, WorkerRelativePath, expectedWorker)
            || !VerifyDescriptorEntry(runtimeRoot, ModelRelativePath, expectedModel)
            || !VerifyDescriptorEntry(runtimeRoot, ConfigRelativePath, expectedConfig))
        {
            return false;
        }

        identity = new PpOcrV6RuntimeIdentity(expectedWorker, expectedModel, expectedConfig);
        return true;
    }

    private static bool VerifyDescriptorEntry(string runtimeRoot, string relativePath, string expectedPath)
    {
        var descriptorPath = Path.Combine(runtimeRoot, "scanner-runtime.json");
        if (!File.Exists(descriptorPath) || !File.Exists(expectedPath))
        {
            return false;
        }

        try
        {
            using var descriptor = JsonDocument.Parse(File.ReadAllBytes(descriptorPath));
            if (!descriptor.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("path", out var path)
                    || !string.Equals(NormalizeRelativePath(path.GetString()), relativePath, StringComparison.OrdinalIgnoreCase)
                    || !entry.TryGetProperty("size", out var size)
                    || !entry.TryGetProperty("sha256", out var sha))
                {
                    continue;
                }

                var info = new FileInfo(expectedPath);
                if (info.Length != size.GetInt64())
                {
                    return false;
                }

                using var stream = File.OpenRead(expectedPath);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return string.Equals(actualHash, sha.GetString(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool PathsEqual(string actual, string expected)
    {
        try
        {
            return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string? path) => (path ?? "").Replace('\\', '/');
}
