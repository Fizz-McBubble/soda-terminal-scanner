using System.Diagnostics;
using System.Text.Json;
using Soda.Scanner.Core;

internal static class RuntimeCompatibilityTests
{
    internal static int Run(string root, string archive, string stub, string legacyArchive, string legacyStub, string legacyProductVersion = "1.2.3")
    {
        root = FileSystemSafety.ValidateRoot(root, true);
        if (Directory.Exists(root)) throw new InvalidOperationException("fresh_compatibility_root_required");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool value, string name) { name = name.Replace("123", legacyProductVersion.Replace(".", "")); if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        static Dictionary<string, string> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(path, p), Sha256Util.ComputeFileSha256);
        static void Copy(string from, string to) { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to); }
        InstallResult Install(string path, string fault = "none") => InstallEngine.ExecuteInstall(path,
            ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive), () => File.OpenRead(stub),
            testMode: true, noLaunch: true, faultInjection: fault);
        try
        {
            var assembly = typeof(ScannerConstants).Assembly;
            Check(assembly.GetName().Version!.ToString() == ScannerConstants.DisplayVersion + ".0" &&
                FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion == ScannerConstants.DisplayVersion + ".0",
                "compiled_core_assembly_and_file_version_match_product");
            Check(FileVersionInfo.GetVersionInfo(stub).FileVersion == ScannerConstants.DisplayVersion + ".0", "current_stub_file_version_matches_product");
            var legacyRuntimeVersion = legacyProductVersion switch
            {
                "1.2.3" => LegacyRuntimeCatalog.Published123Version,
                "1.2.4" => LegacyRuntimeCatalog.Published124Version,
                "1.2.5" => LegacyRuntimeCatalog.Published125Version,
                "1.2.6" => LegacyRuntimeCatalog.Published126Version,
                _ => throw new InvalidOperationException("unsupported_legacy_fixture")
            };
            var legacy = LegacyRuntimeCatalog.Packages.Single(p => p.Version == legacyRuntimeVersion);
            Check(new FileInfo(legacyArchive).Length == legacy.AssetSize && Sha256Util.ComputeFileSha256(legacyArchive) == legacy.AssetSha256,
                "real_123_archive_matches_historical_pin");
            var legacyStubPin = LegacyRuntimeCatalog.KnownStubs.Single(s => s.Version == legacyProductVersion);
            Check(new FileInfo(legacyStub).Length == legacyStubPin.Size && Sha256Util.ComputeFileSha256(legacyStub) == legacyStubPin.Sha256 &&
                FileVersionInfo.GetVersionInfo(legacyStub).FileVersion == legacyProductVersion + ".0", "real_published_123_stub_matches_historical_pin");

            var installed = Path.Combine(root, "upgrade");
            var oldRuntime = Path.Combine(installed, "versions", legacy.Version);
            ZipSecurity.ExtractSafe(legacyArchive, oldRuntime);
            Check(legacy.Files.All(f => RuntimeCatalog.Matches(FileSystemSafety.SafePath(oldRuntime, f.Path), f)), "all_real_123_runtime_files_match_preserved_catalog");
            foreach (var file in legacy.Files.Where(f => f.Path.StartsWith("helper\\", StringComparison.Ordinal)))
                Copy(Path.Combine(oldRuntime, file.Path), Path.Combine(installed, file.Path));
            Copy(legacyStub, Path.Combine(installed, ScannerConstants.UninstallExecutableName));
            PointerStore.WritePointerAtomic(Path.Combine(installed, "active.json"), new PointerInfo { Version = legacy.Version, Path = oldRuntime, VerifiedAt = "fixture" });
            File.WriteAllText(Path.Combine(oldRuntime, "unknown.txt"), "SYNTHETIC_PRESERVED");
            Directory.CreateDirectory(Path.Combine(installed, "outputs"));
            File.WriteAllText(Path.Combine(installed, "outputs", "fixture.json"), "SYNTHETIC_RESULT");
            Check(InstallerStartup.InstalledVersion(installed, null) == legacyProductVersion, "registration_absent_123_recovers_upgrade_identity");
            var helper = Path.Combine(installed, ScannerConstants.HelperExecutableRelative);
            Check(ProcessHelper.IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative), "real_123_helper_remains_known");
            var before = Snapshot(installed);
            foreach (var phase in new[] { "components", "registration", "previous", "active" })
            {
                try { Install(installed, phase); throw new Exception("fault_not_reached"); }
                catch (InvalidOperationException error) when (error.Message == "runtime_injected_" + phase) { }
                var after = Snapshot(installed);
                Check(before.Count == after.Count && before.All(p => after.GetValueOrDefault(p.Key) == p.Value), "123_upgrade_rollback_preserves_every_fixture_byte_after_" + phase);
            }
            var result = Install(installed);
            Check(result.Success && !result.Started && !result.RepeatedInstall, "123_to_current_upgrade_without_helper_launch");
            var currentRuntime = Path.Combine(installed, "versions", ScannerConstants.LockedRuntimeVersion);
            RuntimeCatalog.Verify(currentRuntime);
            Check(PointerStore.ReadValidated(Path.Combine(installed, "active.json"), installed)?.Version == ScannerConstants.LockedRuntimeVersion &&
                PointerStore.ReadValidated(Path.Combine(installed, "previous.json"), installed)?.Version == legacy.Version, "new_active_and_123_previous_recovery_pointer");
            Check(legacy.Files.All(f => RuntimeCatalog.Matches(FileSystemSafety.SafePath(oldRuntime, f.Path), f)), "upgrade_preserves_real_123_runtime_for_recovery");
            Check(Sha256Util.ComputeFileSha256(Path.Combine(installed, ScannerConstants.UninstallExecutableName)) == Sha256Util.ComputeFileSha256(stub), "upgrade_installs_exact_current_stub");
            Check(Directory.GetFiles(Path.Combine(installed, "preserved-components"), "uninstall.exe", SearchOption.AllDirectories)
                .Any(p => Sha256Util.ComputeFileSha256(p) == legacyStubPin.Sha256), "upgrade_retains_original_123_stub_recovery_bytes");
            var data = RuntimeCatalog.Files.First(f => f.Path.StartsWith("native\\Data\\", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(currentRuntime, data.Path), "SYNTHETIC_REPLACED");
            File.Delete(helper);
            var repair = Install(installed);
            RuntimeCatalog.Verify(currentRuntime);
            Check(repair.Success && repair.RepeatedInstall && !repair.Started && ProcessHelper.IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative), "current_repair_restores_catalog_and_missing_helper");
            Check(Directory.GetFiles(Path.Combine(installed, "preserved-components"), Path.GetFileName(data.Path), SearchOption.AllDirectories)
                .Any(p => File.ReadAllText(p) == "SYNTHETIC_REPLACED"), "repair_preserves_replaced_file_bytes");

            // Exercise the production history-aware remover, with no supplied expected stub hash.
            var removal = Path.Combine(root, "historical-removal");
            var removedRuntime = Path.Combine(removal, "versions", legacy.Version);
            var core = legacy.Files.Single(f => f.Path == "native\\ZZZ-Scanner.Next.Soda.exe");
            Copy(Path.Combine(oldRuntime, core.Path), Path.Combine(removedRuntime, core.Path));
            Copy(legacyStub, Path.Combine(removal, ScannerConstants.UninstallExecutableName));
            Copy(Path.Combine(oldRuntime, ScannerConstants.HelperExecutableRelative), Path.Combine(removal, ScannerConstants.HelperExecutableRelative));
            var preserved = Path.Combine(removal, "preserved-components", ".setup-" + new string('a', 32), ScannerConstants.HelperExecutableRelative);
            Copy(Path.Combine(oldRuntime, ScannerConstants.HelperExecutableRelative), preserved);
            File.WriteAllText(Path.Combine(removal, "unknown.txt"), "SYNTHETIC_UNKNOWN");
            var altered = legacy.Files.First(f => f.Path.EndsWith(".txt", StringComparison.Ordinal));
            var alteredPath = Path.Combine(removedRuntime, altered.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(alteredPath)!);
            File.WriteAllText(alteredPath, "SYNTHETIC_USER_REPLACEMENT");
            var removed = UninstallEngine.ExecuteUninstall(removal, testMode: true);
            Check(removed.Success && !File.Exists(Path.Combine(removal, ScannerConstants.UninstallExecutableName)) &&
                !File.Exists(Path.Combine(removedRuntime, core.Path)) && !File.Exists(Path.Combine(removal, ScannerConstants.HelperExecutableRelative)) && !File.Exists(preserved),
                "real_123_known_stub_helper_core_and_recovery_copy_removed_by_pins");
            Check(File.ReadAllText(Path.Combine(removal, "unknown.txt")) == "SYNTHETIC_UNKNOWN" && File.ReadAllText(alteredPath) == "SYNTHETIC_USER_REPLACEMENT", "historical_removal_preserves_unknown_and_replaced_bytes");
            var unknownStub = Path.Combine(removal, ScannerConstants.UninstallExecutableName);
            File.WriteAllText(unknownStub, "MZ_UNKNOWN");
            UninstallEngine.ExecuteUninstall(removal, testMode: true);
            Check(File.ReadAllText(unknownStub) == "MZ_UNKNOWN", "unknown_stub_ownership_remains_protected");
            Check(File.ReadAllText(Path.Combine(installed, "outputs", "fixture.json")) == "SYNTHETIC_RESULT" &&
                File.ReadAllText(Path.Combine(oldRuntime, "unknown.txt")) == "SYNTHETIC_PRESERVED", "synthetic_results_and_legacy_unknown_survive_upgrade_repair");
            // The historical-caller guard must not turn an explicit uninstall
            // from the new installer into a refusal or leave known old versions.
            var whole = UninstallEngine.ExecuteUninstall(installed, testMode: true,
                expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub));
            Check(whole.Success && !File.Exists(helper) &&
                !File.Exists(Path.Combine(currentRuntime, "native", "ZZZ-Scanner.Next.Soda.exe")) &&
                !File.Exists(Path.Combine(oldRuntime, "native", "ZZZ-Scanner.Next.Soda.exe")) &&
                !File.Exists(Path.Combine(installed, ScannerConstants.UninstallExecutableName)),
                "explicit_current_installer_whole_product_uninstall_removes_current_and_123_programs");
            Check(legacy.Files.Where(f => f.Path.StartsWith("models\\", StringComparison.Ordinal)).All(f =>
                    !File.Exists(Path.Combine(oldRuntime, f.Path)) && !File.Exists(Path.Combine(currentRuntime, f.Path))),
                "explicit_whole_product_uninstall_removes_both_owned_shared_model_copies");
            Check(File.ReadAllText(Path.Combine(installed, "outputs", "fixture.json")) == "SYNTHETIC_RESULT" &&
                File.ReadAllText(Path.Combine(oldRuntime, "unknown.txt")) == "SYNTHETIC_PRESERVED",
                "explicit_whole_product_uninstall_preserves_synthetic_results_and_unknown_bytes");
            Report(0, null); return 0;
        }
        catch (Exception error) { Report(1, error.ToString()); Console.Error.WriteLine(error); return 1; }
        void Report(int exitCode, string? error) => File.WriteAllText(Path.Combine(root, "runtime-compatibility.json"),
            JsonSerializer.Serialize(new { exitCode, checks, error, legacyProductVersion, registryWrites = 0, helperStarted = false, actualInstall = false, uac = false, game = false }));
    }
}
