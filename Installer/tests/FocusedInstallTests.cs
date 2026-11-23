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
    internal static int RunVersionIdentityFix(string root)
    {
        root = FileSystemSafety.ValidateRoot(root, true);
        if (Directory.Exists(root)) throw new InvalidOperationException("fresh_version_fix_root_required");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            foreach (var version in new[] { "1.2.3", "1.2.4", "1.2.5", "1.2.6", "1.2.7", "1.2.8", "1.2.9", ScannerConstants.DisplayVersion })
            {
                var registered = ProductIdentity(root, version);
                Check(RegistrationIdentity.UninstallOwned(registered, root), version + "_registration_owned");
                Check(InstallerStartup.InstalledVersion(root, registered) == version, version + "_registration_preserves_exact_product");
            }
            foreach (var pair in new[] {
                (LegacyRuntimeCatalog.Published123Version, "1.2.3"),
                (LegacyRuntimeCatalog.Published124Version, "1.2.4"),
                (LegacyRuntimeCatalog.Published125Version, "1.2.5"),
                (LegacyRuntimeCatalog.Published126Version, "1.2.6"),
                (LegacyRuntimeCatalog.Delivered127Version, "1.2.7"),
                (LegacyRuntimeCatalog.Delivered128Version, "1.2.8"),
                (LegacyRuntimeCatalog.Delivered129Version, "1.2.9"),
                (ScannerConstants.LockedRuntimeVersion, ScannerConstants.DisplayVersion)
            })
            {
                PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"), new PointerInfo { Version = pair.Item1, Path = Path.Combine(root, "versions", pair.Item1) });
                Check(InstallerStartup.InstalledVersion(root, null) == pair.Item2, pair.Item2 + "_unregistered_pointer_identity");
                Check((pair.Item2 == ScannerConstants.DisplayVersion) == (pair.Item1 == ScannerConstants.LockedRuntimeVersion), pair.Item2 + "_upgrade_vs_current_repair");
            }
            Check(LegacyRuntimeCatalog.Published123Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.Published124Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative), "123_and_delivered124_helper_catalogs_embedded");
            Check(LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.3") && LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.4"), "123_and_delivered124_stubs_remain_known");
            Check(LegacyRuntimeCatalog.Published125Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.5"), "delivered125_catalog_and_stub_known");
            Check(LegacyRuntimeCatalog.Published126Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.6"), "delivered126_catalog_and_stub_known");
            Check(LegacyRuntimeCatalog.Delivered127Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.7"), "delivered127_catalog_and_stub_known");
            Check(LegacyRuntimeCatalog.Delivered128Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.8"), "delivered128_catalog_and_stub_known");
            Check(LegacyRuntimeCatalog.Delivered129Files.Any(f => f.Path == ScannerConstants.HelperExecutableRelative) &&
                  LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == "1.2.9"), "delivered129_catalog_and_stub_known");
            Report(0, null); return 0;
        }
        catch (Exception error) { Report(1, error.ToString()); return 1; }
        void Report(int exitCode, string? error) => File.WriteAllText(Path.Combine(root, "version-identity-fix.json"),
            JsonSerializer.Serialize(new { exitCode, checks, error, registryWrites = 0, helperStarted = false, actualInstall = false }));
    }

    internal static int RunVersionIdentity(string root, string? legacy112Helper = null)
    {
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        void Pointer(string version) => PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"),
            new PointerInfo { Version = version, Path = Path.Combine(root, "versions", version) });
        try
        {
            Pointer(ScannerConstants.LockedRuntimeVersion);
            foreach (var version in new[] { "1.0.9", "1.0.10", "1.1.0", "1.1.1", "1.1.2", "1.1.3", "1.1.4", "1.1.5", "1.1.6", "1.1.7", "1.1.8", "1.1.9", "1.2.0", "1.2.1", "1.2.2", "1.2.3", "1.2.4", "1.2.5", "1.2.6", "1.2.9", ScannerConstants.DisplayVersion })
            {
                var registration = ProductIdentity(root, version);
                Check(RegistrationIdentity.UninstallOwned(registration, root), version + "_owned_uninstaller_accepted");
                var current = InstallerStartup.InstalledVersion(root, registration);
                Check(current == version, version + "_product_version_identity");
                Check((current != ScannerConstants.DisplayVersion) == (version != ScannerConstants.DisplayVersion), version + "_upgrade_or_repair_branch");
            }
            Check(InstallerStartup.InstalledVersion(root, null) == ScannerConstants.DisplayVersion, "unique_runtime_without_product_identity_is_current");
            Check(InstallerStartup.InstalledVersion(root, ProductIdentity(root + "-foreign", "1.1.0")) == ScannerConstants.DisplayVersion, "foreign_root_cannot_claim_110");
            var extra = ProductIdentity(root, "1.1.0"); extra.Values.Add("Unknown", ("PRESERVE", Microsoft.Win32.RegistryValueKind.String));
            Check(InstallerStartup.InstalledVersion(root, extra) == ScannerConstants.DisplayVersion, "foreign_registration_shape_cannot_claim_110");
            Pointer(LegacyRuntimeCatalog.Published117Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.1.7", "legacy_117_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published117Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_117_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published120Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.0", "legacy_120_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published120Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_120_helper_catalog_embedded");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-17-local-20261017");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.1", "legacy_121_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published121Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_121_helper_catalog_embedded");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-18-local-20261019");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.2", "legacy_122_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published122Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_122_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published123Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.3", "legacy_123_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published123Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_123_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published124Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.4", "legacy_124_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published124Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_124_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published125Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.5", "legacy_125_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published125Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_125_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published126Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.6", "legacy_126_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published126Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_126_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Delivered129Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.2.9", "delivered_129_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Delivered129Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "delivered_129_helper_catalog_embedded");
            Pointer(LegacyRuntimeCatalog.Published116Version);
            Check(InstallerStartup.InstalledVersion(root, null) == "1.1.4", "legacy_116_shared_runtime_is_114_without_registration");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-13-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.1.3", "legacy_113_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published113Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_113_helper_catalog_embedded");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-12-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.1.2", "legacy_112_pointer_remains_upgrade");
            Check(LegacyRuntimeCatalog.Published112Files.Any(file => file.Path == ScannerConstants.HelperExecutableRelative), "legacy_112_helper_catalog_embedded");
            if (legacy112Helper != null)
            {
                Check(ProcessHelper.IsKnownProgram(legacy112Helper, ScannerConstants.HelperExecutableRelative), "legacy_112_helper_bytes_remain_known");
                var changedHelper = Path.Combine(root, "changed-helper.exe");
                File.WriteAllText(changedHelper, "unknown-program-bytes");
                Check(!ProcessHelper.IsKnownProgram(changedHelper, ScannerConstants.HelperExecutableRelative), "unknown_helper_bytes_remain_rejected");
            }
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-11-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.1.1", "legacy_111_pointer_remains_upgrade");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-10-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.0.10", "legacy_shared_runtime_without_product_identity_is_1010");
            Pointer("soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011");
            Check(InstallerStartup.InstalledVersion(root, null) == "1.0.9", "legacy_109_pointer_remains_upgrade");
            File.WriteAllText(Path.Combine(root, "version-identity.json"), JsonSerializer.Serialize(new { ok = true, checks, current = ScannerConstants.DisplayVersion, registryWrites = 0, actualInstall = false, helperStarted = false, runtimeRebuilt = false }));
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
            File.WriteAllText(Path.Combine(root, "focused-install.json"), JsonSerializer.Serialize(new { ok = true, checks, root, registryWrites = 0, actualInstall = false, helperStarted = false, noActualInstallation = true, noHelperStarted = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "focused-install.json"), JsonSerializer.Serialize(new { ok = false, checks, error = error.ToString() }));
            return 1;
        }
    }
}
