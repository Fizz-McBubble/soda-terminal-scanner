using System.Reflection;
using System.Text.Json;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

internal static class SetupResources
{
    internal static Stream OpenArchive(string? overrideArchive = null)
        => overrideArchive == null ? PayloadFiles.Open() : File.OpenRead(overrideArchive);

    private sealed record StubIdentity(long Size, string Sha256);
    private static readonly Lazy<StubIdentity> Stub = new(() =>
    {
        using var metadata = Open("setup-payload.json");
        var identity = JsonSerializer.Deserialize<StubIdentity>(metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("setup_stub_identity_missing");
        if (identity.Size <= 0 || identity.Size > 64 * 1024 * 1024 || identity.Sha256.Length != 64)
            throw new InvalidOperationException("setup_stub_identity_invalid");
        return identity;
    });

    internal static long UninstallerSize => Stub.Value.Size;

    internal static Stream OpenUninstaller()
    {
        var stream = PayloadFiles.Open(uninstaller: true);
        try
        {
            if (Sha256Util.ComputeStreamSha256(stream) != Stub.Value.Sha256)
                throw new InvalidOperationException("setup_uninstaller_identity_mismatch");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static string UninstallerHash()
    {
        return Stub.Value.Sha256;
    }

    private static Stream Open(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("setup_asset_missing");
    }
}
