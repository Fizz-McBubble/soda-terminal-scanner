using System.Reflection;
using System.Text.Json;

namespace Soda.Scanner.Core;

public sealed record RuntimeFile(string Path, long Size, string Sha256);

public static class RuntimeCatalog
{
    // Compiled from the pinned ZIP, never from a descriptor found on the player's disk.
    public static IReadOnlyList<RuntimeFile> Files { get; } = Load();

    private static IReadOnlyList<RuntimeFile> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("runtime-files.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<RuntimeFile[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("runtime_catalog_missing");
    }

    public static bool Matches(string path, RuntimeFile entry) => File.Exists(path) &&
        new FileInfo(path).Length == entry.Size && Sha256Util.ComputeFileSha256(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase);

    public static void Verify(string runtimeRoot)
    {
        foreach (var entry in Files)
        {
            var path = FileSystemSafety.SafePath(runtimeRoot, entry.Path);
            if (!Matches(path, entry)) throw new InvalidOperationException("runtime_file_verification_failed");
        }
    }

    public static void StageSupplement(string workRoot, string runtimeRoot)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var metadataStream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("vc-runtime-input.json", StringComparison.Ordinal)))!;
        using var metadata = JsonDocument.Parse(metadataStream);
        var name = metadata.RootElement.GetProperty("fileName").GetString()!;
        var size = metadata.RootElement.GetProperty("size").GetInt64();
        var hash = metadata.RootElement.GetProperty("sha256").GetString();
        var archivePath = FileSystemSafety.SafePath(workRoot, name);
        using (var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)))!)
        using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.CopyTo(output);
        if (new FileInfo(archivePath).Length != size || Sha256Util.ComputeFileSha256(archivePath) != hash)
            throw new InvalidOperationException("vc_runtime_asset_identity_mismatch");
        ZipSecurity.ExtractSafe(archivePath, runtimeRoot);
    }
}
