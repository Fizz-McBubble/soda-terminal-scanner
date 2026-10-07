using System.Security.Cryptography;
using System.Text.Json;

namespace ZZZScannerHelper;

internal sealed record SodaOcrRuntimeIdentity(string WorkerPath, string ModelPath, string ConfigPath);

internal sealed record SodaRuntimeIdentity(
    string Version,
    string Root,
    string EntryPath,
    SodaOcrRuntimeIdentity PpOcrV6,
    string? CaptureVersion = null,
    string? OcrVersion = null);

internal static class SodaRuntimeLocator
{
    internal const string RelativeEntryPath = "native/ZZZ-Scanner.Next.Soda.exe";
    internal const string RelativePpOcrV6WorkerPath = "ocr/Soda.ScannerPpOcrV6.exe";
    internal const string RelativePpOcrV6ModelPath = "models/PP-OCRv6_small_rec_onnx/inference.onnx";
    internal const string RelativePpOcrV6ConfigPath = "models/PP-OCRv6_small_rec_onnx/inference.yml";

    public static string DefaultInstallRoot() => HelperStorageManager.DefaultDataRoot();

    public static SodaRuntimeIdentity Load(string? installRoot = null)
    {
        var root = Path.GetFullPath(installRoot ?? DefaultInstallRoot());
        var activePath = Path.Combine(root, "active.json");
        using var activeDocument = JsonDocument.Parse(File.ReadAllText(activePath));
        var active = activeDocument.RootElement;
        var version = RequiredString(active, "version");
        var versionRoot = Path.GetFullPath(RequiredString(active, "path"));
        EnsureWithin(Path.Combine(root, "versions"), versionRoot);

        var descriptorPath = Path.Combine(versionRoot, "scanner-runtime.json");
        using var descriptorDocument = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        var descriptor = descriptorDocument.RootElement;
        if (descriptor.GetProperty("schemaVersion").GetInt32() != 2
            || !string.Equals(RequiredString(descriptor, "version"), version, StringComparison.Ordinal)
            || descriptor.GetProperty("accountWriteEnabled").GetBoolean()
            || descriptor.GetProperty("importAccess").GetBoolean())
            throw new InvalidDataException("soda_runtime_descriptor_invalid");

        var entries = descriptor.GetProperty("entries").EnumerateArray().ToArray();
        var entryPath = VerifyEntry(versionRoot, entries, RelativeEntryPath, "entry");
        var workerPath = VerifyEntry(versionRoot, entries, RelativePpOcrV6WorkerPath, "ppocrv6_worker");
        var modelPath = VerifyEntry(versionRoot, entries, RelativePpOcrV6ModelPath, "ppocrv6_model");
        var configPath = VerifyEntry(versionRoot, entries, RelativePpOcrV6ConfigPath, "ppocrv6_config");
        return new SodaRuntimeIdentity(
            version,
            versionRoot,
            entryPath,
            new SodaOcrRuntimeIdentity(workerPath, modelPath, configPath),
            descriptor.TryGetProperty("capture", out var capture) && capture.ValueKind == JsonValueKind.String ? capture.GetString() : null,
            descriptor.TryGetProperty("ocr", out var ocr) && ocr.ValueKind == JsonValueKind.String ? ocr.GetString() : null);
    }

    private static string VerifyEntry(
        string versionRoot,
        JsonElement[] entries,
        string relativePath,
        string identityPart)
    {
        var entry = entries.SingleOrDefault(candidate => string.Equals(
            RequiredString(candidate, "path").Replace('\\', '/'),
            relativePath,
            StringComparison.OrdinalIgnoreCase));
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"soda_runtime_{identityPart}_missing");
        var path = Path.GetFullPath(Path.Combine(versionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(versionRoot, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.GetProperty("size").GetInt64())
            throw new InvalidDataException($"soda_runtime_{identityPart}_size_mismatch");
        using var stream = info.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, RequiredString(entry, "sha256"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"soda_runtime_{identityPart}_hash_mismatch");
        return path;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"soda_runtime_{name}_missing");
        return property.GetString()!;
    }

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("soda_runtime_path_escape");
    }
}
