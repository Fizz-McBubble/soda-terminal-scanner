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
        var pointer = new PointerInfo { Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011", Path = Path.Combine(root, "versions", "soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011") };
        PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"), pointer);
        var passed = InstallerStartup.InstalledVersion(root) == "1.0.9";
        File.WriteAllText(Path.Combine(root, "version-identity.json"), JsonSerializer.Serialize(new { ok = passed, previous = InstallerStartup.InstalledVersion(root), current = ScannerConstants.DisplayVersion }));
        return passed ? 0 : 1;
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
