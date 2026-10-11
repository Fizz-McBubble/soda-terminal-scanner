using System.Text.Json;
namespace Soda.Scanner.Core;

public static class LegacyRuntimeCatalog
{
    // Published 1.0.8 catalog from e3590a3b; never trust a disk descriptor for deletion.
    public const string Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-8";
    public static IReadOnlyList<RuntimeFile> Files { get; } = Load();
    public static IReadOnlyList<RuntimeFile> Published109Files { get; } = Load("runtime-files-1.0.9.json");
    private static RuntimeFile[] Load(string fileName = "runtime-files-1.0.8.json")
    {
        var assembly = typeof(LegacyRuntimeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(fileName, StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<RuntimeFile[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
