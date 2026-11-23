using System.Diagnostics;
using System.Text.Json;
using Soda.Scanner.Core;

// Preparation checks intentionally do not need, generate or claim a new r40 binary.
internal static class Legacy125PreparationTests
{
    internal static int Run(string root, string archive, string stub)
    {
        root = FileSystemSafety.ValidateRoot(root, true);
        if (Directory.Exists(root)) throw new InvalidOperationException("fresh_legacy125_preparation_root_required");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
        static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(path, p), Sha256Util.ComputeFileSha256);
        static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
        static void Refuses(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException error) when (error.Message == message) { return; }
            throw new Exception("expected_refusal:" + message);
        }
        try
        {
            var legacy = LegacyRuntimeCatalog.Packages.Single(p => p.Version == LegacyRuntimeCatalog.Published125Version);
            var stubPin = LegacyRuntimeCatalog.KnownStubs.Single(s => s.Version == "1.2.5");
            Check(new FileInfo(archive).Length == legacy.AssetSize && Sha256Util.ComputeFileSha256(archive) == legacy.AssetSha256,
                "real_delivered125_zip_matches_historical_pin");
            Check(new FileInfo(stub).Length == stubPin.Size && Sha256Util.ComputeFileSha256(stub) == stubPin.Sha256 &&
                FileVersionInfo.GetVersionInfo(stub).FileVersion == "1.2.5.0", "real_delivered125_stub_matches_pin_and_file_version");
            var fixture = Path.Combine(root, "real125-files");
            ZipSecurity.ExtractSafe(archive, fixture);
            Check(legacy.Files.All(f => RuntimeCatalog.Matches(FileSystemSafety.SafePath(fixture, f.Path), f)),
                "all_delivered125_zip_files_match_byte_preserved_catalog");
            var shared = legacy.Files.Where(f => f.Path.StartsWith("helper\\", StringComparison.Ordinal) || f.Path.StartsWith("models\\", StringComparison.Ordinal)).ToArray();
            var helper = legacy.Files.Single(f => f.Path == ScannerConstants.HelperExecutableRelative);
            Check(helper.Sha256 == "4fa789b4060ee29df4989c4665c62b0120060a916e0d29cbe5b9721f400eb347" &&
                FileVersionInfo.GetVersionInfo(Path.Combine(fixture, helper.Path)).FileVersion == "2.4.0.0", "real_delivered125_helper240_legacy_identity");
            Check(LegacyRuntimeCatalog.Packages.Any(p => p.Version == LegacyRuntimeCatalog.Published123Version) &&
                LegacyRuntimeCatalog.Packages.Any(p => p.Version == LegacyRuntimeCatalog.Published124Version), "123_124_packages_preserved");

            var mixed = Path.Combine(root, "mixed-synthetic-current-pointer");
            Directory.CreateDirectory(mixed);
            var current = Path.Combine(mixed, "versions", ScannerConstants.LockedRuntimeVersion);
            foreach (var file in shared) Copy(Path.Combine(fixture, file.Path), Path.Combine(current, file.Path));
            Check(shared.Length > 1 && shared.All(f => RuntimeCatalog.Matches(Path.Combine(current, f.Path), f)),
                "independent_equal_bytes_fixture_does_not_claim_current126_helper_identity");
            Copy(Path.Combine(fixture, helper.Path), Path.Combine(mixed, helper.Path));
            Copy(stub, Path.Combine(mixed, ScannerConstants.UninstallExecutableName));
            Copy(Path.Combine(fixture, "scanner-runtime.json"), Path.Combine(mixed, "versions", legacy.Version, "scanner-runtime.json"));
            Directory.CreateDirectory(Path.Combine(mixed, "outputs"));
            File.WriteAllText(Path.Combine(mixed, "outputs", "synthetic-result.json"), "SYNTHETIC_PRESERVE_RESULT");
            File.WriteAllText(Path.Combine(current, "unknown.txt"), "SYNTHETIC_PRESERVE_UNKNOWN");
            PointerStore.WritePointerAtomic(Path.Combine(mixed, "active.json"), new PointerInfo { Version = ScannerConstants.LockedRuntimeVersion, Path = current });
            PointerStore.WritePointerAtomic(Path.Combine(mixed, "previous.json"), new PointerInfo { Version = legacy.Version, Path = Path.Combine(mixed, "versions", legacy.Version) });
            var before = Snapshot(mixed);
            var callbacks = 0;
            Refuses(() => UninstallEngine.ExecuteUninstall(mixed, testMode: true, expectedUninstallerHash: stubPin.Sha256,
                statusCallback: _ => callbacks++), "historical_uninstaller_cannot_remove_active_current_runtime");
            Check(callbacks == 0 && Equal(before, Snapshot(mixed)), "explicit_old125_caller_refused_before_status_or_mutation_with_equal_helper_and_models");
            Refuses(() => UninstallEngine.ExecuteUninstall(mixed, testMode: true), "historical_uninstaller_cannot_remove_active_current_runtime");
            Check(Equal(before, Snapshot(mixed)), "inferred_old125_installed_stub_cannot_claim_active_current_by_shared_hash");
            var unknownVersion = "soda-scanner-unrecognized-future-fixture";
            PointerStore.WritePointerAtomic(Path.Combine(mixed, "active.json"), new PointerInfo { Version = unknownVersion, Path = Path.Combine(mixed, "versions", unknownVersion) });
            before = Snapshot(mixed);
            Refuses(() => UninstallEngine.ExecuteUninstall(mixed, testMode: true), "uninstall_active_runtime_unknown");
            Check(Equal(before, Snapshot(mixed)), "unrecognized_active_runtime_preserves_every_fixture_byte");

            // A deliberate new-installer whole-product uninstall still recognizes
            // an actual legacy-only installation. No old stub process is executed.
            var oldOnly = Path.Combine(root, "legacy125-only");
            var oldRuntime = Path.Combine(oldOnly, "versions", legacy.Version);
            var core = legacy.Files.Single(f => f.Path == "native\\ZZZ-Scanner.Next.Soda.exe");
            foreach (var file in new[] { helper, core }) Copy(Path.Combine(fixture, file.Path), Path.Combine(oldRuntime, file.Path));
            Copy(Path.Combine(fixture, helper.Path), Path.Combine(oldOnly, helper.Path));
            Copy(stub, Path.Combine(oldOnly, ScannerConstants.UninstallExecutableName));
            PointerStore.WritePointerAtomic(Path.Combine(oldOnly, "active.json"), new PointerInfo { Version = legacy.Version, Path = oldRuntime });
            Check(InstallerStartup.InstalledVersion(oldOnly, null) == "1.2.5", "legacy125_pointer_recovers_exact_upgrade_identity");
            File.WriteAllText(Path.Combine(oldOnly, "unknown.txt"), "SYNTHETIC_PRESERVE");
            var removed = UninstallEngine.ExecuteUninstall(oldOnly, testMode: true);
            Check(removed.Success && !File.Exists(Path.Combine(oldOnly, helper.Path)) && !File.Exists(Path.Combine(oldRuntime, core.Path)) &&
                !File.Exists(Path.Combine(oldOnly, ScannerConstants.UninstallExecutableName)), "new_engine_removes_real_known125_only_components_without_launching_old_stub");
            Check(File.ReadAllText(Path.Combine(oldOnly, "unknown.txt")) == "SYNTHETIC_PRESERVE", "legacy_only_uninstall_preserves_unknown_bytes");
            Check(typeof(ScannerConstants).Assembly.GetName().Version!.ToString() == ScannerConstants.DisplayVersion + ".0",
                "preparation_assembly_matches_current_product_without_claiming_scanner_core");
            Report(0, null); return 0;
        }
        catch (Exception error) { Report(1, error.ToString()); Console.Error.WriteLine(error); return 1; }
        void Report(int exitCode, string? error) => File.WriteAllText(Path.Combine(root, "legacy125-preparation.json"),
            JsonSerializer.Serialize(new { exitCode, checks, error, registryWrites = 0, helperStarted = false, actualInstall = false,
                corePublishedByTest = false, finalSourceIdentityCreatedByTest = false, currentRuntimeIsSyntheticPointerFixture = true,
                oldStubExecuted = false }));
    }
}
