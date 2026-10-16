using System.Text.Json;

namespace Soda.Scanner.Core;

public sealed record HistoricalRuntimePackage(
    string Version,
    string AssetName,
    long AssetSize,
    string AssetSha256,
    IReadOnlyList<RuntimeFile> Files);

public sealed record UninstallerStubIdentity(string Version, long Size, string Sha256);

public static class LegacyRuntimeCatalog
{
    // Published 1.0.8 catalog from e3590a3b; never trust a disk descriptor for deletion.
    public const string Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-8";
    public static IReadOnlyList<RuntimeFile> Files { get; } = Load("runtime-files-1.0.8.json");
    public static IReadOnlyList<RuntimeFile> Published100Files { get; } = Load("runtime-files-1.0.0.json");
    public static IReadOnlyList<RuntimeFile> Published102Files { get; } = Load("runtime-files-1.0.2.json");
    public static IReadOnlyList<RuntimeFile> Published105Files { get; } = Load("runtime-files-1.0.5.json");
    public static IReadOnlyList<RuntimeFile> Published106Files { get; } = Load("runtime-files-1.0.6.json");
    public static IReadOnlyList<RuntimeFile> Published107Files { get; } = Load("runtime-files-1.0.7.json");
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

    public static IReadOnlyList<HistoricalRuntimePackage> Packages { get; } = new List<HistoricalRuntimePackage>
    {
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-3", "soda-scanner-runtime-18-rc8-3-win-x64.zip", 142107129L, "722370a83ba93a9618d56c471e87c49d5a7ee8d75bd54e50e82f3f4f19efacc2", Published100Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-4", "soda-scanner-runtime-18-rc8-4-win-x64.zip", 128548618L, "de2735bb619a5a679a0b16e11637ade350fc2d4ab846aa48e81a61f474ce801b", Published102Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-5", "soda-scanner-runtime-18-rc8-5-win-x64.zip", 133910367L, "eb31c9084ef15d19906aed0929b5772389f1d40190ef1a9adf6b3216b46294d7", Published105Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-6", "soda-scanner-runtime-18-rc8-6-win-x64.zip", 133941203L, "47b9472664e01ce47ae9590bedd2ccced54165ed73af6941219320696d9f6b71", Published106Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-7", "soda-scanner-runtime-18-rc8-7-win-x64.zip", 133944562L, "474855cd91163828c02a992d263c7d9d9ac6169d857dfcc8b8c2250e6c949235", Published107Files),
        new(Version, "soda-scanner-runtime-18-rc8-8-win-x64.zip", 133950733L, "b6a7a178dff47ac14e8721f5c7343983a39b57222a47bd8846b94711a988d1b8", Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011", "soda-scanner-runtime-18-rc8-9-local-20261011-win-x64.zip", 131734513L, "23402875cd433adf95ea98296a5deb36c3af652e0452dd3025e2bdc3027a3642", Published109Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-10-local-20261011", "soda-scanner-runtime-18-rc8-10-local-20261011-win-x64.zip", 131735730L, "8da6370490286a778f49977c4a3162a3b2059fb10056afb687e6627eec48519d", Published110Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-11-local-20261011", "soda-scanner-runtime-18-rc8-11-local-20261011-win-x64.zip", 131737531L, "677505a03dfe2f696e897b241243c690285cb642a7e731b834b6f74428cb2352", Published111Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-12-local-20261011", "soda-scanner-runtime-18-rc8-12-local-20261011-win-x64.zip", 131758206L, "82f4c7c4e162c0330d6e37614696d53a55b5c0efbca3847e1b1649dd4c336292", Published112Files),
        new("soda-scanner-zzz-next-ppocrv6-18-rc8-13-local-20261011", "soda-scanner-runtime-18-rc8-13-local-20261011-win-x64.zip", 131758361L, "fc3eeb8ca7c8f8ef033038f1673c98ad8e1973c348b800b6f1ef3fae551d8842", Published113Files),
        new(Published116Version, "soda-scanner-runtime-18-rc8-14-local-20261011-win-x64.zip", 131761101L, "91c72d605303af04cad3ae07d13d6b31c6a85e46dd2b7989464e50325d7ee31c", Published116Files),
        new(Published117Version, "soda-scanner-runtime-18-rc8-15-local-20261015-win-x64.zip", 131762244L, "5cb44df256e3d36470f5ecc58b2ce634cc2e4cb1dec1af6c49f9eb814220e3a4", Published117Files),
    };

    public static IReadOnlyList<HistoricalRuntimePackage> AllPackages { get; } = Packages
        .Append(new HistoricalRuntimePackage(ScannerConstants.LockedRuntimeVersion, ScannerConstants.LockedAssetName, ScannerConstants.LockedAssetSize, ScannerConstants.LockedAssetSha256, RuntimeCatalog.Files))
        .ToList();

    public static IReadOnlyList<RuntimeFile> AllDistinctFiles { get; } = AllPackages
        .SelectMany(p => p.Files)
        .DistinctBy(f => (f.Path, f.Size, f.Sha256))
        .ToList();

    public static IReadOnlyList<RuntimeFile> AllHelperFiles { get; } = AllDistinctFiles
        .Where(f => f.Path.StartsWith("helper\\", StringComparison.OrdinalIgnoreCase))
        .ToList();

    public static IReadOnlyList<UninstallerStubIdentity> KnownStubs { get; } = LoadKnownStubs();

    private static IReadOnlyList<UninstallerStubIdentity> LoadKnownStubs()
    {
        var assembly = typeof(LegacyRuntimeCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("historical-stubs.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("historical_stubs_resource_missing");
        var entries = JsonSerializer.Deserialize<UninstallerStubIdentity[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("historical_stubs_deserialization_failed");
        return entries;
    }

    private static RuntimeFile[] Load(string fileName = "runtime-files-1.0.8.json")
    {
        var assembly = typeof(LegacyRuntimeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(fileName, StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<RuntimeFile[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
