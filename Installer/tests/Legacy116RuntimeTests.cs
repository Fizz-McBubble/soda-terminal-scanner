using Soda.Scanner.Core;

internal static class Legacy116RuntimeTests
{
    internal static void Run(string basePath, string archive, string stub, Action<string, Action> test)
    {
        var oldArchive = Environment.GetEnvironmentVariable("SODA_SCANNER_LEGACY_116_ARCHIVE");
        if (oldArchive is not null)
            RunPublished(basePath, archive, stub, test, oldArchive, "116", LegacyRuntimeCatalog.Published116Version, "1.1.4", LegacyRuntimeCatalog.Published116Files);
        var published117 = Environment.GetEnvironmentVariable("SODA_SCANNER_LEGACY_117_ARCHIVE");
        if (published117 is not null)
            RunPublished(basePath, archive, stub, test, published117, "117", LegacyRuntimeCatalog.Published117Version, "1.1.7", LegacyRuntimeCatalog.Published117Files);
    }
    private static void RunPublished(string basePath, string archive, string stub, Action<string, Action> test,
        string oldArchive, string release, string runtimeVersion, string productVersion, IReadOnlyList<RuntimeFile> files)
    {
        test("published " + productVersion + " runtime upgrade rollback and exact uninstall preserve unknown bytes", () =>
        {
            var root = Path.Combine(basePath, "published-" + release + "-upgrade");
            var old = Path.Combine(root, "versions", runtimeVersion);
            ZipSecurity.ExtractSafe(oldArchive, old);
            foreach (var pin in files)
                Check(RuntimeCatalog.Matches(Path.Combine(old, pin.Path), pin));
            foreach (var pin in files.Where(pin => pin.Path.StartsWith("helper\\", StringComparison.Ordinal)))
            {
                var target = Path.Combine(root, pin.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(old, pin.Path), target);
            }
            var helper = Path.Combine(root, ScannerConstants.HelperExecutableRelative);
            Check(ProcessHelper.IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative));
            PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"), new PointerInfo { Version = runtimeVersion, Path = old });
            Check(InstallerStartup.InstalledVersion(root, null) == productVersion);
            var result = Path.Combine(root, "outputs", "keep.json");
            Directory.CreateDirectory(Path.GetDirectoryName(result)!);
            File.WriteAllText(result, "SYNTHETIC_RESULT");
            var unknown = Path.Combine(old, "user.txt"); File.WriteAllText(unknown, "USER_FILE");
            foreach (var fault in new[] { "components", "registration", "previous", "active" })
            {
                var before = Snapshot(root);
                try { Install(fault); throw new Exception("fault_missing"); }
                catch (InvalidOperationException error) when (error.Message == "runtime_injected_" + fault) { }
                var after = Snapshot(root);
                Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value));
            }
            Check(Install("none").Success);
            Check(InstallerStartup.InstalledVersion(root, null) == ScannerConstants.DisplayVersion);
            Check(PointerStore.ReadValidated(Path.Combine(root, "previous.json"), root)?.Version == runtimeVersion);
            // A known catalog path with replacement bytes must remain untouched.
            var replaced = files.First(pin => pin.Path == "native\\ZZZ-Scanner.Next.Soda.exe");
            File.WriteAllText(Path.Combine(old, replaced.Path), "USER_REPLACED_PROGRAM");
            UninstallEngine.ExecuteUninstall(root, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub));
            Check(File.ReadAllText(result) == "SYNTHETIC_RESULT" && File.ReadAllText(unknown) == "USER_FILE");
            Check(File.ReadAllText(Path.Combine(old, replaced.Path)) == "USER_REPLACED_PROGRAM");
            Check(files.Where(pin => pin.Path != replaced.Path).All(pin => !File.Exists(Path.Combine(old, pin.Path))));
            Check(RuntimeCatalog.Files.All(pin => !File.Exists(Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion, pin.Path))));
            Check(!File.Exists(helper));
            InstallResult Install(string fault) => InstallEngine.ExecuteInstall(root, ScannerConstants.DefaultPublicOrigin,
                () => File.OpenRead(archive), () => File.OpenRead(stub), true, true, faultInjection: fault);
        });
    }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), Sha256Util.ComputeFileSha256);
    private static void Check(bool value) { if (!value) throw new Exception("published_runtime_compatibility_failed"); }
}
