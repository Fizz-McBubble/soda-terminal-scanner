using System.Text.Json;
namespace Soda.Scanner.Core;

public static class LegacyRuntimeCatalog
{
    // Published 1.0.8 catalog from e3590a3b; never trust a disk descriptor for deletion.
    public const string Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-8";
    public static IReadOnlyList<RuntimeFile> Files { get; } = Load();
    public static IReadOnlyList<RuntimeFile> Published109Files { get; } = Load("runtime-files-1.0.9.json");
    public static IReadOnlyList<RuntimeFile> Published110Files { get; } = Load("runtime-files-1.1.0.json");
    public static IReadOnlyList<RuntimeFile> Published111Files { get; } = Load("runtime-files-1.1.1.json");
    public static IReadOnlyList<RuntimeFile> Published112Files { get; } = Load("runtime-files-1.1.2.json");
    public static IReadOnlyList<RuntimeFile> Published113Files { get; } = Load("runtime-files-1.1.3.json");
    // 1.1.4 through 1.1.6 share the exact published RC8.14 runtime.
    public const string Published116Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-14-local-20261011";
    public static IReadOnlyList<RuntimeFile> Published116Files { get; } = Load("runtime-files-1.1.6.json");
    public const string Published117Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-15-local-20261015";
    public static IReadOnlyList<RuntimeFile> Published117Files { get; } = Load("runtime-files-1.1.7.json");
    private static RuntimeFile[] Load(string fileName = "runtime-files-1.0.8.json")
    {
        var assembly = typeof(LegacyRuntimeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(fileName, StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<RuntimeFile[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
