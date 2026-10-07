using System.Reflection;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

internal static class SetupResources
{
    internal static Stream OpenArchive(string? overrideArchive = null)
        => overrideArchive == null ? Open(ScannerConstants.LockedAssetName) : File.OpenRead(overrideArchive);

    internal static Stream OpenUninstaller() => Open("Soda-Scanner-Uninstall.exe");

    internal static string UninstallerHash()
    {
        using var stream = OpenUninstaller();
        return Sha256Util.ComputeStreamSha256(stream);
    }

    private static Stream Open(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("setup_asset_missing");
    }
}
