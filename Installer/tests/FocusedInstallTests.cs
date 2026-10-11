using System.Text.Json;
using Soda.Scanner.Core;

namespace ZZZScannerHelper
{
    internal static class HelperStorageManager
    {
        // The linked production locator always receives an explicit synthetic root.
        internal static string DefaultDataRoot() => throw new InvalidOperationException("explicit_test_root_required");
    }
}

internal static class FocusedInstallTests
{
    internal static int RunVersionIdentity(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        void Pointer(string version) => PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"),
            new PointerInfo { Version = version, Path = Path.Combine(root, "versions", version) });
        try
        {
            Pointer(ScannerConstants.LockedRuntimeVersion);
            foreach (var version in new[] { "1.0.9", "1.0.10", "1.1.0" })
            {
                var registration = ProductIdentity(root, version);
                Check(RegistrationIdentity.UninstallOwned(registration, root), version + "_owned_uninstaller_accepted");
                var current = InstallerStartup.InstalledVersion(root, registration);
                Check(current == version, version + "_product_version_identity");
                Check((current != ScannerConstants.DisplayVersion) == (version != "1.1.0"), version + "_upgrade_or_repair_branch");
            }
            Check(InstallerStartup.InstalledVersion(root, null) == "1.0.10", "shared_runtime_without_product_identity_is_1010");
            Check(InstallerStartup.InstalledVersion(root, ProductIdentity(root + "-foreign", "1.1.0")) == "1.0.10", "foreign_root_cannot_claim_110");
            var extra = ProductIdentity(root, "1.1.0"); extra.Values.Add("Unknown", ("PRESERVE", Microsoft.Win32.RegistryValueKind.String));
            Check(InstallerStartup.InstalledVersion(root, extra) == "1.0.10", "foreign_registration_shape_cannot_claim_110");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.0.9", "legacy_109_pointer_remains_upgrade");
            File.WriteAllText(Path.Combine(root, "version-identity.json"), JsonSerializer.Serialize(new { ok = true, checks, current = ScannerConstants.DisplayVersion, registryWrites = 0, runtimeRebuilt = false }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "version-identity.json"), JsonSerializer.Serialize(new { ok = false, checks, error = error.Message }));
            return 1;
        }
    }
    private static RegistrySnapshot.KeySnapshot ProductIdentity(string root, string version)
    {
        var values = new Dictionary<string, object>
        {
            ["DisplayName"] = ScannerConstants.DisplayName, ["DisplayVersion"] = version, ["Publisher"] = ScannerConstants.Publisher,
            ["InstallLocation"] = Path.GetFullPath(root), ["UninstallString"] = "\"" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\"",
            ["QuietUninstallString"] = "\"" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\" --silent", ["NoModify"] = 1, ["NoRepair"] = 0
        };
        return new RegistrySnapshot.KeySnapshot { Exists = true, Values = values.ToDictionary(pair => pair.Key,
            pair => (pair.Value, pair.Value is int ? Microsoft.Win32.RegistryValueKind.DWord : Microsoft.Win32.RegistryValueKind.String), StringComparer.OrdinalIgnoreCase) };
    }
    internal static int Run(string root, string archive, string stub)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            Directory.CreateDirectory(root);
            InstallerUpgradeTests.Run(root, archive, stub, (name, test) =>
            {
                if (name is "shortcut creation repair rollback and exact owned removal"
                    or "start menu opens visible workspace and upgrades owned silent shortcut reversibly"
                    or "installer completion closes without opening a second webpage")
                { test(); checks.Add(name); }
            });
            var installed = Path.Combine(root, "descriptor-installed-target");
            InstallEngine.ExecuteInstall(installed, ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive),
                () => File.OpenRead(stub), testMode: true, noLaunch: true);
            var runtime = Path.Combine(installed, "versions", ScannerConstants.LockedRuntimeVersion);
            var descriptor = RuntimeCatalog.Files.Single(file => file.Path == "scanner-runtime.json");
            Check(RuntimeCatalog.Matches(Path.Combine(runtime, descriptor.Path), descriptor), "installed_descriptor_pin");
            var identity = ZZZScannerHelper.SodaRuntimeLocator.Load(installed);
            Check(identity.Version == ScannerConstants.LockedRuntimeVersion && identity.Root == runtime, "production_consumer_loads_installed_target");
            Check(ScannerShortcut.IsOwned(installed, ScannerConstants.DefaultPublicOrigin, true), "installed_shortcut_starts_helper_workspace");
            File.AppendAllText(Path.Combine(runtime, "scanner-runtime.json"), "ALTERED");
            try { RuntimeCatalog.Verify(runtime); throw new Exception("altered_descriptor_accepted"); }
            catch (InvalidOperationException error) when (error.Message == "runtime_file_verification_failed") { checks.Add("altered_descriptor_rejected"); }
            File.WriteAllText(Path.Combine(root, "focused-install.json"), JsonSerializer.Serialize(new { ok = true, checks, root, noActualInstallation = true, noHelperStarted = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "focused-install.json"), JsonSerializer.Serialize(new { ok = false, checks, error = error.ToString() }));
            return 1;
        }
    }
}
