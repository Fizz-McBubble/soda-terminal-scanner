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
        if (pointer?.Version == ScannerConstants.LockedRuntimeVersion) return ScannerConstants.DisplayVersion;
        if (pointer?.Version == LegacyRuntimeCatalog.Published117Version) return "1.1.7";
        // Runtime identity is shared by multiple product releases. Without a
        // trusted product registration it can prove only the first shipped version.
        if (pointer?.Version == LegacyRuntimeCatalog.Published120Version) return "1.2.0";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-17-local-20261017") return "1.2.1";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-18-local-20261019") return "1.2.2";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-14-local-20261011") return "1.1.4";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-13-local-20261011") return "1.1.3";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-12-local-20261011") return "1.1.2";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-11-local-20261011") return "1.1.1";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-10-local-20261011") return "1.0.10";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011") return "1.0.9";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-8") return "1.0.8";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-7") return "1.0.7";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-6") return "1.0.6";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-5") return "1.0.5";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-4") return "1.0.2";
        if (pointer?.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-3") return "1.0.0";
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
