namespace Soda.Scanner.Core;

public static class InstallerStartup
{
    public static string? InstalledVersion(string root)
        => InstalledVersion(root, RegistryHelper.ReadUninstallIdentity());

    public static string? InstalledVersion(string root, RegistrySnapshot.KeySnapshot? productIdentity)
    {
        if (RegistrationIdentity.UninstallOwned(productIdentity, root))
            return productIdentity!.Values["DisplayVersion"].Value as string;
        var pointer = PointerStore.ReadValidated(Path.Combine(root, "active.json"), root);
        // Runtime identity is shared by multiple product releases. Without a
        // trusted product registration it can prove only the first shipped version.
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-12-local-20261011") return "1.1.2";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-11-local-20261011") return "1.1.1";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-10-local-20261011") return "1.0.10";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011") return "1.0.9";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-8") return "1.0.8";
        return null;
    }
    // Even incomplete or unfamiliar contents can belong to a previous installation.
    // Inspect only the root entries; never read user results or follow child links.
    public static bool HasInstallationTraces(string installRoot, bool registrationExists)
    {
        if (registrationExists) return true;
        try
        {
            if (File.Exists(installRoot)) return true;
            return Directory.Exists(installRoot) && Directory.EnumerateFileSystemEntries(installRoot).Any();
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
