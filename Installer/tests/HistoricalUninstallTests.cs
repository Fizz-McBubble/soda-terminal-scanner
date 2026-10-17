using System.Diagnostics;
using System.Text.Json;
using Soda.Scanner.Core;

namespace Soda.Scanner.Installer.Tests;

internal sealed class HistoricalFixtureConfig
{
    public required string TestExtractDir { get; init; }
    public required string EvidenceArchiveDir { get; init; }
    public required string AllStubPinsJsonPath { get; init; }

    public static HistoricalFixtureConfig Resolve(
        string? explicitExtractDir = null,
        string? explicitEvidenceDir = null,
        string? explicitStubPins = null)
    {
        static string RequiredPath(string? explicitValue, string variable)
        {
            var value = explicitValue ?? Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Required historical fixture input missing: {variable}");
            return Path.GetFullPath(value);
        }

        return new HistoricalFixtureConfig
        {
            TestExtractDir = RequiredPath(explicitExtractDir, "SODA_TEST_EXTRACT_DIR"),
            EvidenceArchiveDir = RequiredPath(explicitEvidenceDir, "SODA_EVIDENCE_ARCHIVE_DIR"),
            AllStubPinsJsonPath = RequiredPath(explicitStubPins, "SODA_STUB_PINS_PATH")
        };
    }
}

internal static class HistoricalUninstallTests
{
    internal static void Run(string safetyRoot, string archivePath, string stubPath, Action<string, Action> test, HistoricalFixtureConfig? fixtures = null)
    {
        fixtures ??= HistoricalFixtureConfig.Resolve();

        test("historical catalog and stub identities coverage", () =>
        {
            Check(LegacyRuntimeCatalog.AllPackages.Count == 15, "historical_packages_count_mismatch");
            Check(LegacyRuntimeCatalog.AllDistinctFiles.Count > 0, "all_distinct_files_empty");
            Check(LegacyRuntimeCatalog.AllHelperFiles.Count > 0, "all_helper_files_empty");
            Check(LegacyRuntimeCatalog.KnownStubs.Count >= 20, "historical_stubs_under_coverage");

            foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
            {
                Check(!string.IsNullOrWhiteSpace(pkg.Version), "package_version_empty");
                Check(!string.IsNullOrWhiteSpace(pkg.AssetName), $"package_{pkg.Version}_asset_empty");
                Check(pkg.AssetSize > 0, $"package_{pkg.Version}_asset_size_invalid");
                Check(pkg.AssetSha256.Length == 64, $"package_{pkg.Version}_asset_sha_invalid");
                Check(pkg.Files.Count > 0, $"package_{pkg.Version}_files_empty");
            }

            foreach (var stub in LegacyRuntimeCatalog.KnownStubs)
            {
                Check(stub.Size > 0, $"stub_{stub.Version}_invalid_size");
                Check(stub.Sha256.Length == 64, $"stub_{stub.Version}_invalid_sha256");
            }

            // Verify all 22 versions 1.0.0 - 1.2.0 have corresponding stub identities
            var expectedVersions = new[]
            {
                "1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6", "1.0.7", "1.0.8", "1.0.9",
                "1.0.10", "1.1.0", "1.1.1", "1.1.2", "1.1.3", "1.1.4", "1.1.5", "1.1.6", "1.1.7", "1.1.8", "1.1.9",
                "1.2.0"
            };
            foreach (var v in expectedVersions)
            {
                Check(LegacyRuntimeCatalog.KnownStubs.Any(s => s.Version == v), $"missing_stub_for_version_{v}");
            }

            // 1. Cross-check with the 21 pre-1.2.0 stub pins extracted from real release archives
            var allStubPinsPath = fixtures.AllStubPinsJsonPath;
            AssertFixtureFile(allStubPinsPath, "all_stub_pins_json");
            using (var doc = JsonDocument.Parse(File.ReadAllText(allStubPinsPath)))
            {
                var rows = doc.RootElement.GetProperty("rows");
                Check(rows.GetArrayLength() == 21, "all_stub_pins_rows_count_mismatch");
                foreach (var row in rows.EnumerateArray())
                {
                    var version = row.GetProperty("version").GetString()!;
                    var size = row.GetProperty("size").GetInt64();
                    var sha = row.GetProperty("sha256").GetString()!;
                    var stub = LegacyRuntimeCatalog.KnownStubs.FirstOrDefault(s => s.Version == version);
                    Check(stub != null, $"missing_stub_in_catalog_for_{version}");
                    Check(stub!.Size == size, $"stub_size_mismatch_for_{version}");
                    Check(string.Equals(stub.Sha256, sha, StringComparison.OrdinalIgnoreCase), $"stub_sha_mismatch_for_{version}");
                }
            }

            // 2. Cross-check 14 catalogs with original release manifests
            var manifest100Path = Path.Combine(fixtures.EvidenceArchiveDir, "scanner-installer-v1.0.0", "installer-build-manifest.json");
            AssertFixtureFile(manifest100Path, "manifest_1.0.0");
            using (var doc = JsonDocument.Parse(File.ReadAllText(manifest100Path)))
            {
                var rootElem = doc.RootElement;
                var asset = rootElem.GetProperty("embeddedRuntimeAsset");
                var assetName = asset.GetProperty("fileName").GetString()!;
                var assetSize = asset.GetProperty("size").GetInt64();
                var assetSha = asset.GetProperty("sha256").GetString()!;
                var pkg0 = LegacyRuntimeCatalog.AllPackages.First(p => p.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-3");
                Check(pkg0.AssetName == assetName, "v100_asset_name_mismatch");
                Check(pkg0.AssetSize == assetSize, "v100_asset_size_mismatch");
                Check(string.Equals(pkg0.AssetSha256, assetSha, StringComparison.OrdinalIgnoreCase), "v100_asset_sha_mismatch");

                var stubObj = rootElem.GetProperty("uninstallExecutable");
                var stubSize = stubObj.GetProperty("size").GetInt64();
                var stubSha = stubObj.GetProperty("sha256").GetString()!;
                var stub0 = LegacyRuntimeCatalog.KnownStubs.First(s => s.Version == "1.0.0");
                Check(stub0.Size == stubSize && string.Equals(stub0.Sha256, stubSha, StringComparison.OrdinalIgnoreCase), "v100_stub_mismatch");
            }

            var manifest101Path = Path.Combine(fixtures.EvidenceArchiveDir, "scanner-installer-v1.0.1", "installer-build-manifest.json");
            AssertFixtureFile(manifest101Path, "manifest_1.0.1");
            using (var doc = JsonDocument.Parse(File.ReadAllText(manifest101Path)))
            {
                var rootElem = doc.RootElement;
                var asset = rootElem.GetProperty("embeddedRuntimeAsset");
                var assetName = asset.GetProperty("fileName").GetString()!;
                var assetSize = asset.GetProperty("size").GetInt64();
                var assetSha = asset.GetProperty("sha256").GetString()!;
                var pkg0 = LegacyRuntimeCatalog.AllPackages.First(p => p.Version == "soda-scanner-zzz-next-ppocrv6-18-rc8-3");
                Check(pkg0.AssetName == assetName, "v101_asset_name_mismatch");
                Check(pkg0.AssetSize == assetSize, "v101_asset_size_mismatch");
                Check(string.Equals(pkg0.AssetSha256, assetSha, StringComparison.OrdinalIgnoreCase), "v101_asset_sha_mismatch");
            }
        });

        test("helper-bootstrap program identity mapping in ProcessHelper", () =>
        {
            var work = Path.Combine(safetyRoot, "bootstrap-ident-test");
            Directory.CreateDirectory(work);
            var helperDir = Path.Combine(work, "helper");
            var bootstrapDir = Path.Combine(work, "helper-bootstrap");
            Directory.CreateDirectory(helperDir);
            Directory.CreateDirectory(bootstrapDir);

            // Extract the real helper from archive
            var tempRuntime = Path.Combine(work, "temp-runtime");
            ZipSecurity.ExtractSafe(archivePath, tempRuntime);
            var realHelper = Path.Combine(tempRuntime, ScannerConstants.HelperExecutableRelative);
            Check(File.Exists(realHelper), "real_helper_missing_in_archive");
            var currentHelperSha = Sha256Util.ComputeFileSha256(realHelper);

            var helperTarget = Path.Combine(helperDir, "ZZZ-Scanner-Helper.exe");
            var bootstrapTarget = Path.Combine(bootstrapDir, "ZZZ-Scanner-Helper.exe");
            File.Copy(realHelper, helperTarget, true);
            File.Copy(realHelper, bootstrapTarget, true);

            // Verify normal helper relative path
            Check(ProcessHelper.IsKnownProgram(helperTarget, ScannerConstants.HelperExecutableRelative),
                "normal_helper_should_match");
            // Verify helper-bootstrap relative path is mapped to catalog helper
            Check(ProcessHelper.IsKnownProgram(bootstrapTarget, "helper-bootstrap\\ZZZ-Scanner-Helper.exe"),
                "bootstrap_helper_should_match_via_mapped_identity");

            // Verify with distinct historical helper binaries (1.0.8 & 1.1.6) from explicit fixtures
            var helper8Path = Path.Combine(fixtures.TestExtractDir, "helper-8", "ZZZ-Scanner-Helper.exe");
            AssertFixtureFile(helper8Path, "helper_1.0.8_fixture");
            var h8Expected = LegacyRuntimeCatalog.Files.Single(f => f.Path == ScannerConstants.HelperExecutableRelative);
            Check(RuntimeCatalog.Matches(helper8Path, h8Expected), "helper8_must_match_108_catalog");
            var h8Sha = Sha256Util.ComputeFileSha256(helper8Path);
            Check(!h8Sha.Equals(currentHelperSha, StringComparison.OrdinalIgnoreCase), "helper8_must_differ_from_current");

            var h8Dst = Path.Combine(helperDir, "ZZZ-Scanner-Helper-108.exe");
            var b8Dst = Path.Combine(bootstrapDir, "ZZZ-Scanner-Helper-108.exe");
            File.Copy(helper8Path, h8Dst, true);
            File.Copy(helper8Path, b8Dst, true);
            Check(ProcessHelper.IsKnownProgram(h8Dst, ScannerConstants.HelperExecutableRelative), "h8_normal_match");
            Check(ProcessHelper.IsKnownProgram(b8Dst, "helper-bootstrap\\ZZZ-Scanner-Helper.exe"), "h8_bootstrap_match");

            var helper14Path = Path.Combine(fixtures.TestExtractDir, "helper-14", "ZZZ-Scanner-Helper.exe");
            AssertFixtureFile(helper14Path, "helper_1.1.6_fixture");
            var h14Expected = LegacyRuntimeCatalog.Published116Files.Single(f => f.Path == ScannerConstants.HelperExecutableRelative);
            Check(RuntimeCatalog.Matches(helper14Path, h14Expected), "helper14_must_match_116_catalog");
            var h14Sha = Sha256Util.ComputeFileSha256(helper14Path);
            Check(!h14Sha.Equals(currentHelperSha, StringComparison.OrdinalIgnoreCase), "helper14_must_differ_from_current");

            var h14Dst = Path.Combine(helperDir, "ZZZ-Scanner-Helper-116.exe");
            var b14Dst = Path.Combine(bootstrapDir, "ZZZ-Scanner-Helper-116.exe");
            File.Copy(helper14Path, h14Dst, true);
            File.Copy(helper14Path, b14Dst, true);
            Check(ProcessHelper.IsKnownProgram(h14Dst, ScannerConstants.HelperExecutableRelative), "h14_normal_match");
            Check(ProcessHelper.IsKnownProgram(b14Dst, "helper-bootstrap\\ZZZ-Scanner-Helper.exe"), "h14_bootstrap_match");

            // Unknown / modified helper must be rejected
            var modifiedBootstrap = Path.Combine(bootstrapDir, "modified-helper.exe");
            File.WriteAllText(modifiedBootstrap, "TAMPERED_HELPER_BYTES");
            Check(!ProcessHelper.IsKnownProgram(modifiedBootstrap, "helper-bootstrap\\ZZZ-Scanner-Helper.exe"),
                "modified_bootstrap_helper_must_be_rejected");
        });

        test("multi-version coexistence single uninstall cleans all historical files and entries", () =>
        {
            var root = Path.Combine(safetyRoot, "multi-version-clean");
            Directory.CreateDirectory(root);

            // 1. Setup current version directory from locked runtime archive
            var currentVersionDir = Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
            ZipSecurity.ExtractSafe(archivePath, currentVersionDir);
            var currentHelper = Path.Combine(currentVersionDir, ScannerConstants.HelperExecutableRelative);
            Check(File.Exists(currentHelper), "current_helper_missing");
            var currentHelperSha = Sha256Util.ComputeFileSha256(currentHelper);
            var currentDiscs = Path.Combine(currentVersionDir, "native", "Data", "drive_discs.json");
            Check(File.Exists(currentDiscs), "current_discs_missing");
            var currentDiscsSha = Sha256Util.ComputeFileSha256(currentDiscs);

            // 2. Setup older versions with ACTUAL DISTINCT historical binaries from explicit fixtures
            // Version 1.0.8 (RC8.8)
            var helper8Src = Path.Combine(fixtures.TestExtractDir, "helper-8", "ZZZ-Scanner-Helper.exe");
            var rt8JsonSrc = Path.Combine(fixtures.TestExtractDir, "files-8", "scanner-runtime.json");
            var discs8Src = Path.Combine(fixtures.TestExtractDir, "files-8", "native", "Data", "drive_discs.json");
            var zip8Src = Path.Combine(fixtures.TestExtractDir, "soda-scanner-runtime-18-rc8-8-win-x64.zip");

            AssertFixtureFile(helper8Src, "helper-8 fixture");
            AssertFixtureFile(rt8JsonSrc, "files-8 scanner-runtime.json fixture");
            AssertFixtureFile(discs8Src, "files-8 drive_discs.json fixture");
            AssertFixtureFile(zip8Src, "rc8-8 zip fixture");

            // Verify they match 1.0.8 catalog
            var h8Expected = LegacyRuntimeCatalog.Files.Single(f => f.Path == ScannerConstants.HelperExecutableRelative);
            Check(RuntimeCatalog.Matches(helper8Src, h8Expected), "helper8_must_match_catalog");
            var discs8Expected = LegacyRuntimeCatalog.Files.Single(f => f.Path == @"native\Data\drive_discs.json");
            Check(RuntimeCatalog.Matches(discs8Src, discs8Expected), "discs8_must_match_catalog");
            var pkg8Expected = LegacyRuntimeCatalog.Packages.Single(p => p.Version == LegacyRuntimeCatalog.Version);
            Check(new FileInfo(zip8Src).Length == pkg8Expected.AssetSize && Sha256Util.ComputeFileSha256(zip8Src).Equals(pkg8Expected.AssetSha256, StringComparison.OrdinalIgnoreCase), "zip8_must_match_catalog");

            // Prove they differ from current version bytes
            var h8Sha = Sha256Util.ComputeFileSha256(helper8Src);
            Check(!h8Sha.Equals(currentHelperSha, StringComparison.OrdinalIgnoreCase), "helper8_must_differ_from_current");
            var discs8Sha = Sha256Util.ComputeFileSha256(discs8Src);
            Check(!discs8Sha.Equals(currentDiscsSha, StringComparison.OrdinalIgnoreCase), "discs8_must_differ_from_current");

            var oldVersion8 = Path.Combine(root, "versions", LegacyRuntimeCatalog.Version);
            Directory.CreateDirectory(oldVersion8);
            var h8Dst = Path.Combine(oldVersion8, "helper", "ZZZ-Scanner-Helper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(h8Dst)!);
            File.Copy(helper8Src, h8Dst, true);
            File.Copy(rt8JsonSrc, Path.Combine(oldVersion8, "scanner-runtime.json"), true);
            var d8Dst = Path.Combine(oldVersion8, "native", "Data", "drive_discs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(d8Dst)!);
            File.Copy(discs8Src, d8Dst, true);

            // Version 1.1.6 (RC8.14)
            var helper14Src = Path.Combine(fixtures.TestExtractDir, "helper-14", "ZZZ-Scanner-Helper.exe");
            var rt14JsonSrc = Path.Combine(fixtures.TestExtractDir, "files-14", "scanner-runtime.json");
            var discs14Src = Path.Combine(fixtures.TestExtractDir, "files-14", "native", "Data", "drive_discs.json");

            AssertFixtureFile(helper14Src, "helper-14 fixture");
            AssertFixtureFile(rt14JsonSrc, "files-14 scanner-runtime.json fixture");
            AssertFixtureFile(discs14Src, "files-14 drive_discs.json fixture");

            // Verify they match 1.1.6 catalog
            var h14Expected = LegacyRuntimeCatalog.Published116Files.Single(f => f.Path == ScannerConstants.HelperExecutableRelative);
            Check(RuntimeCatalog.Matches(helper14Src, h14Expected), "helper14_must_match_catalog");
            var discs14Expected = LegacyRuntimeCatalog.Published116Files.Single(f => f.Path == @"native\Data\drive_discs.json");
            Check(RuntimeCatalog.Matches(discs14Src, discs14Expected), "discs14_must_match_catalog");

            // Prove they differ from current version bytes
            var h14Sha = Sha256Util.ComputeFileSha256(helper14Src);
            Check(!h14Sha.Equals(currentHelperSha, StringComparison.OrdinalIgnoreCase), "helper14_must_differ_from_current");
            var discs14Sha = Sha256Util.ComputeFileSha256(discs14Src);
            Check(!discs14Sha.Equals(currentDiscsSha, StringComparison.OrdinalIgnoreCase), "discs14_must_differ_from_current");

            var oldVersion16 = Path.Combine(root, "versions", LegacyRuntimeCatalog.Published116Version);
            Directory.CreateDirectory(oldVersion16);
            var h14Dst = Path.Combine(oldVersion16, "helper", "ZZZ-Scanner-Helper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(h14Dst)!);
            File.Copy(helper14Src, h14Dst, true);
            File.Copy(rt14JsonSrc, Path.Combine(oldVersion16, "scanner-runtime.json"), true);
            var d14Dst = Path.Combine(oldVersion16, "native", "Data", "drive_discs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(d14Dst)!);
            File.Copy(discs14Src, d14Dst, true);

            // Populate shared identical runtime files to simulate realistic multi-package directories
            foreach (var pin in LegacyRuntimeCatalog.Files)
            {
                var src = Path.Combine(currentVersionDir, pin.Path);
                if (File.Exists(src) && RuntimeCatalog.Matches(src, pin))
                {
                    var dest = Path.Combine(oldVersion8, pin.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest)) File.Copy(src, dest, true);
                }
            }
            foreach (var pin in LegacyRuntimeCatalog.Published116Files)
            {
                var src = Path.Combine(currentVersionDir, pin.Path);
                if (File.Exists(src) && RuntimeCatalog.Matches(src, pin))
                {
                    var dest = Path.Combine(oldVersion16, pin.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest)) File.Copy(src, dest, true);
                }
            }

            // 3. Setup current helper and historical helper-bootstrap using distinct historical 1.0.8 helper
            var helperDst = Path.Combine(root, ScannerConstants.HelperExecutableRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(helperDst)!);
            File.Copy(currentHelper, helperDst, true);

            var bootstrapDst = Path.Combine(root, "helper-bootstrap", "ZZZ-Scanner-Helper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(bootstrapDst)!);
            File.Copy(helper8Src, bootstrapDst, true);

            // 4. Setup downloads cache matching historical asset (1.0.8 zip) and locked asset
            var downloadsDir = Path.Combine(root, "downloads");
            Directory.CreateDirectory(downloadsDir);
            var lockedZip = Path.Combine(downloadsDir, ScannerConstants.LockedAssetName);
            File.Copy(archivePath, lockedZip, true);
            var historicalZip8Dst = Path.Combine(downloadsDir, pkg8Expected.AssetName);
            File.Copy(zip8Src, historicalZip8Dst, true);

            // 5. Setup historical 1.0.0 stub uninstaller from explicit evidence archive
            var legacy100StubSrc = Path.Combine(fixtures.EvidenceArchiveDir, "scanner-installer-v1.0.0", "Soda-Scanner-Uninstall.exe");
            AssertFixtureFile(legacy100StubSrc, "legacy 1.0.0 stub uninstaller");
            var stub100Pin = LegacyRuntimeCatalog.KnownStubs.First(s => s.Version == "1.0.0");
            Check(new FileInfo(legacy100StubSrc).Length == stub100Pin.Size, "stub_100_size_mismatch");
            Check(Sha256Util.ComputeFileSha256(legacy100StubSrc).Equals(stub100Pin.Sha256, StringComparison.OrdinalIgnoreCase), "stub_100_sha_mismatch");
            var currentStubSha = Sha256Util.ComputeFileSha256(stubPath);
            Check(!stub100Pin.Sha256.Equals(currentStubSha, StringComparison.OrdinalIgnoreCase), "stub_100_must_differ_from_current_stub");

            var uninstallerDst = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.Copy(legacy100StubSrc, uninstallerDst, true);

            // 6. Setup preserved user data and custom files in the same mixed fixture
            var userScanFile = Path.Combine(root, "outputs", "scan-history.json");
            var userAccountFile = Path.Combine(root, "outputs", "account_token.json");
            var userJobLog = Path.Combine(root, "jobs", "scan-20261017.log");
            Directory.CreateDirectory(Path.GetDirectoryName(userScanFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(userJobLog)!);
            File.WriteAllText(userScanFile, "{\"scan\":\"results_preserved\",\"user\":\"test\"}");
            File.WriteAllText(userAccountFile, "{\"token\":\"secret_preserved\"}");
            File.WriteAllText(userJobLog, "JOB_EXECUTION_LOG_PRESERVED");

            var userCustomScript = Path.Combine(oldVersion8, "user-custom.py");
            File.WriteAllText(userCustomScript, "print('custom user file')");

            var modifiedExe = Path.Combine(oldVersion8, "helper", "modified-helper.exe");
            File.WriteAllText(modifiedExe, "TAMPERED_HELPER_BYTES_MUST_PRESERVE");

            var outsideProtectedDir = Path.Combine(safetyRoot, "outside-multi-protected");
            Directory.CreateDirectory(outsideProtectedDir);
            var outsideSentinel = Path.Combine(outsideProtectedDir, "outside-sentinel.dat");
            File.WriteAllText(outsideSentinel, "OUTSIDE_PRESERVED_DATA");

            var junctionLink = Path.Combine(downloadsDir, "outside-link");
            CreateJunction(junctionLink, outsideProtectedDir);

            // 7. Setup valid pointers
            PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"),
                new PointerInfo { Version = ScannerConstants.LockedRuntimeVersion, Path = currentVersionDir });
            PointerStore.WritePointerAtomic(Path.Combine(root, "previous.json"),
                new PointerInfo { Version = LegacyRuntimeCatalog.Published116Version, Path = oldVersion16 });

            // Explicit precheck: all files must exist before executing uninstall
            Check(File.Exists(uninstallerDst), "precheck_uninstaller_must_exist");
            Check(File.Exists(helperDst), "precheck_helper_must_exist");
            Check(File.Exists(bootstrapDst), "precheck_bootstrap_must_exist");
            Check(File.Exists(lockedZip), "precheck_lockedZip_must_exist");
            Check(File.Exists(historicalZip8Dst), "precheck_historicalZip8_must_exist");
            Check(File.Exists(h8Dst), "precheck_h8Dst_must_exist");
            Check(File.Exists(d8Dst), "precheck_d8Dst_must_exist");
            Check(File.Exists(h14Dst), "precheck_h14Dst_must_exist");
            Check(File.Exists(d14Dst), "precheck_d14Dst_must_exist");
            Check(File.Exists(userScanFile), "precheck_userScan_must_exist");
            Check(File.Exists(userAccountFile), "precheck_userAccount_must_exist");
            Check(File.Exists(userJobLog), "precheck_userJobLog_must_exist");
            Check(File.Exists(userCustomScript), "precheck_userCustom_must_exist");
            Check(File.Exists(modifiedExe), "precheck_modifiedExe_must_exist");
            Check(File.Exists(outsideSentinel), "precheck_outsideSentinel_must_exist");

            // 8. Execute single uninstall without expected hash to verify KnownStubs recognition
            var result = UninstallEngine.ExecuteUninstall(root, testMode: true);
            Check(result.Success, "uninstall_failed");

            // 9. Verify all known files and directories are completely cleaned up
            Check(!File.Exists(uninstallerDst), "uninstaller_stub_should_be_removed");
            Check(!File.Exists(Path.Combine(root, "active.json")), "active_pointer_should_be_removed");
            Check(!File.Exists(Path.Combine(root, "previous.json")), "previous_pointer_should_be_removed");
            Check(!File.Exists(helperDst), "helper_should_be_removed");
            Check(!File.Exists(bootstrapDst), "bootstrap_helper_should_be_removed");
            Check(!File.Exists(lockedZip), "downloads_cache_should_be_removed");
            Check(!File.Exists(historicalZip8Dst), "historical_zip8_cache_should_be_removed");
            Check(!Directory.Exists(currentVersionDir), "current_version_dir_should_be_pruned");

            // Distinct historical program files must be deleted
            Check(!File.Exists(h8Dst), "h8_helper_should_be_removed");
            Check(!File.Exists(Path.Combine(oldVersion8, "scanner-runtime.json")), "h8_rt_json_should_be_removed");
            Check(!File.Exists(d8Dst), "h8_discs_should_be_removed");
            Check(!File.Exists(h14Dst), "h14_helper_should_be_removed");
            Check(!File.Exists(Path.Combine(oldVersion16, "scanner-runtime.json")), "h14_rt_json_should_be_removed");
            Check(!File.Exists(d14Dst), "h14_discs_should_be_removed");

            // 10. Verify preserved user data, custom files, modified files, and outside targets
            Check(File.Exists(userScanFile) && File.ReadAllText(userScanFile) == "{\"scan\":\"results_preserved\",\"user\":\"test\"}", "user_scan_must_be_preserved");
            Check(File.Exists(userAccountFile) && File.ReadAllText(userAccountFile) == "{\"token\":\"secret_preserved\"}", "user_account_must_be_preserved");
            Check(File.Exists(userJobLog) && File.ReadAllText(userJobLog) == "JOB_EXECUTION_LOG_PRESERVED", "user_job_log_must_be_preserved");
            Check(File.Exists(userCustomScript) && File.ReadAllText(userCustomScript) == "print('custom user file')", "user_custom_script_must_be_preserved");
            Check(File.Exists(modifiedExe) && File.ReadAllText(modifiedExe) == "TAMPERED_HELPER_BYTES_MUST_PRESERVE", "modified_exe_must_be_preserved");
            Check(File.Exists(outsideSentinel) && File.ReadAllText(outsideSentinel) == "OUTSIDE_PRESERVED_DATA", "outside_sentinel_must_be_preserved");
            Check(Directory.Exists(outsideProtectedDir), "outside_dir_must_remain");
            Check(Directory.Exists(Path.Combine(root, "outputs")), "outputs_dir_must_remain");
            Check(Directory.Exists(Path.Combine(root, "jobs")), "jobs_dir_must_remain");
            Check(Directory.Exists(oldVersion8), "old_version8_with_preserved_items_must_remain");
        });

        test("early version without registration uninstalls cleanly", () =>
        {
            var root = Path.Combine(safetyRoot, "early-unregistered");
            Directory.CreateDirectory(root);

            var versionDir = Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
            ZipSecurity.ExtractSafe(archivePath, versionDir);
            var helperDst = Path.Combine(root, ScannerConstants.HelperExecutableRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(helperDst)!);
            File.Copy(Path.Combine(versionDir, ScannerConstants.HelperExecutableRelative), helperDst, true);
            var uninstallerDst = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.Copy(stubPath, uninstallerDst, true);

            var result = UninstallEngine.ExecuteUninstall(root, testMode: true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stubPath));
            Check(result.Success, "early_unregistered_uninstall_should_succeed");
            Check(!Directory.Exists(versionDir), "version_dir_should_be_pruned");
            Check(!File.Exists(helperDst), "helper_should_be_removed");
            Check(!File.Exists(uninstallerDst), "uninstaller_should_be_removed");
            Check(!Directory.Exists(root), "root_should_be_pruned");
        });

        test("failed installation residue in preserved-components is cleaned", () =>
        {
            var root = Path.Combine(safetyRoot, "preserved-residue-clean");
            Directory.CreateDirectory(root);

            var residueDir = Path.Combine(root, "preserved-components", ".setup-0123456789abcdef0123456789abcdef");
            Directory.CreateDirectory(residueDir);

            ZipSecurity.ExtractSafe(archivePath, residueDir);
            var nestedVersionDir = Path.Combine(residueDir, "versions", ScannerConstants.LockedRuntimeVersion);
            ZipSecurity.ExtractSafe(archivePath, nestedVersionDir);

            var uninstallerDst = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.Copy(stubPath, uninstallerDst, true);

            var result = UninstallEngine.ExecuteUninstall(root, testMode: true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stubPath));
            Check(result.Success, "uninstall_with_residue_should_succeed");
            Check(!Directory.Exists(residueDir), "residue_dir_should_be_pruned");
            Check(!Directory.Exists(Path.Combine(root, "preserved-components")), "preserved_components_should_be_pruned");
        });

        test("user scan results accounts jobs logs and modified unknown files are strictly preserved", () =>
        {
            var root = Path.Combine(safetyRoot, "user-data-preserve");
            Directory.CreateDirectory(root);

            var versionDir = Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
            ZipSecurity.ExtractSafe(archivePath, versionDir);

            var scanFile = Path.Combine(root, "outputs", "scan-history.json");
            var accountFile = Path.Combine(root, "outputs", "account_token.json");
            Directory.CreateDirectory(Path.GetDirectoryName(scanFile)!);
            File.WriteAllText(scanFile, "{\"scan\":\"results_preserved\"}");
            File.WriteAllText(accountFile, "{\"token\":\"secret_preserved\"}");

            var jobLog = Path.Combine(root, "jobs", "20261016.log");
            Directory.CreateDirectory(Path.GetDirectoryName(jobLog)!);
            File.WriteAllText(jobLog, "JOB_EXECUTION_LOG");

            var userScript = Path.Combine(versionDir, "custom-analysis.py");
            File.WriteAllText(userScript, "print('custom')");

            var modifiedExe = Path.Combine(versionDir, "native", "ZZZ-Scanner.Next.Soda.exe");
            File.WriteAllText(modifiedExe, "MODIFIED_BINARY_DATA");

            var unknownDownload = Path.Combine(root, "downloads", "user-manual.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(unknownDownload)!);
            File.WriteAllText(unknownDownload, "PDF_CONTENT");

            var unknownUninstaller = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.WriteAllText(unknownUninstaller, "UNKNOWN_UNINSTALLER_EXE");

            var corruptedActive = Path.Combine(root, "active.json");
            File.WriteAllText(corruptedActive, "MALFORMED_JSON_CONTENT");

            var result = UninstallEngine.ExecuteUninstall(root, testMode: true);
            Check(result.Success, "uninstall_should_succeed_with_preservations");

            Check(File.ReadAllText(scanFile) == "{\"scan\":\"results_preserved\"}", "scan_results_must_be_preserved");
            Check(File.ReadAllText(accountFile) == "{\"token\":\"secret_preserved\"}", "account_data_must_be_preserved");
            Check(File.ReadAllText(jobLog) == "JOB_EXECUTION_LOG", "job_log_must_be_preserved");
            Check(File.ReadAllText(userScript) == "print('custom')", "user_script_must_be_preserved");
            Check(File.ReadAllText(modifiedExe) == "MODIFIED_BINARY_DATA", "modified_exe_must_be_preserved");
            Check(File.ReadAllText(unknownDownload) == "PDF_CONTENT", "unknown_download_must_be_preserved");
            Check(File.ReadAllText(unknownUninstaller) == "UNKNOWN_UNINSTALLER_EXE", "unknown_uninstaller_must_be_preserved");
            Check(File.ReadAllText(corruptedActive) == "MALFORMED_JSON_CONTENT", "corrupted_pointer_must_be_preserved");

            Check(Directory.Exists(Path.Combine(root, "outputs")), "outputs_dir_must_remain");
            Check(Directory.Exists(Path.Combine(root, "jobs")), "jobs_dir_must_remain");
            Check(Directory.Exists(Path.Combine(root, "downloads")), "downloads_dir_must_remain");
            Check(Directory.Exists(versionDir), "version_dir_with_preserved_items_must_remain");
        });

        test("symlink junction protection does not delete outside target files", () =>
        {
            var root = Path.Combine(safetyRoot, "junction-protection");
            var outside = Path.Combine(safetyRoot, "outside-protected");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);

            var outsideSentinel = Path.Combine(outside, "important-user-archive.zip");
            File.WriteAllText(outsideSentinel, "OUTSIDE_DATA_SENTINEL");

            var downloadsJunction = Path.Combine(root, "downloads");
            CreateJunction(downloadsJunction, outside);

            var uninstallerDst = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.Copy(stubPath, uninstallerDst, true);

            var result = UninstallEngine.ExecuteUninstall(root, testMode: true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stubPath));
            Check(result.Success, "uninstall_with_junction_should_succeed");
            Check(File.ReadAllText(outsideSentinel) == "OUTSIDE_DATA_SENTINEL", "outside_sentinel_must_be_untouched");
            Check(Directory.Exists(outside), "outside_directory_must_remain");
        });

        test("historical uninstaller stub without expected hash is recognized and removed via KnownStubs", () =>
        {
            var root100 = Path.Combine(safetyRoot, "historical-100-stub-recognition");
            Directory.CreateDirectory(root100);

            var legacy100Stub = Path.Combine(fixtures.EvidenceArchiveDir, "scanner-installer-v1.0.0", "Soda-Scanner-Uninstall.exe");
            AssertFixtureFile(legacy100Stub, "legacy 1.0.0 stub uninstaller");

            var uninstaller100Dst = Path.Combine(root100, ScannerConstants.UninstallExecutableName);
            File.Copy(legacy100Stub, uninstaller100Dst, true);
            var stub100Pin = LegacyRuntimeCatalog.KnownStubs.First(s => s.Version == "1.0.0");
            Check(new FileInfo(uninstaller100Dst).Length == stub100Pin.Size, "stub_100_size_match");
            Check(Sha256Util.ComputeFileSha256(uninstaller100Dst).Equals(stub100Pin.Sha256, StringComparison.OrdinalIgnoreCase), "stub_100_sha_match");

            var currentStubSha = Sha256Util.ComputeFileSha256(stubPath);
            Check(!stub100Pin.Sha256.Equals(currentStubSha, StringComparison.OrdinalIgnoreCase), "stub_100_must_differ_from_current_stub");

            var result100 = UninstallEngine.ExecuteUninstall(root100, testMode: true);
            Check(result100.Success, "historical_100_stub_uninstall_should_succeed");
            Check(!File.Exists(uninstaller100Dst), "historical_100_stub_should_be_removed_by_KnownStubs");
        });

        test("busy locked file causes failure and preserves uninstaller entry for retry", () =>
        {
            var root = Path.Combine(safetyRoot, "locked-retry-test");
            Directory.CreateDirectory(root);

            var versionDir = Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
            ZipSecurity.ExtractSafe(archivePath, versionDir);

            var uninstallerDst = Path.Combine(root, ScannerConstants.UninstallExecutableName);
            File.Copy(stubPath, uninstallerDst, true);

            var activePtr = Path.Combine(root, "active.json");
            PointerStore.WritePointerAtomic(activePtr,
                new PointerInfo { Version = ScannerConstants.LockedRuntimeVersion, Path = versionDir });

            // Hold a shared read lock without delete on a runtime file
            var lockedTarget = Path.Combine(versionDir, "scanner-runtime.json");
            using (var lockStream = new FileStream(lockedTarget, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try
                {
                    UninstallEngine.ExecuteUninstall(root, testMode: true);
                }
                catch (IOException)
                {
                    failed = true;
                }
                Check(failed, "expected_ioexception_on_locked_file");
            }

            // Uninstaller entry and active pointer must remain intact for retry!
            Check(File.Exists(uninstallerDst), "uninstaller_must_remain_for_retry");
            Check(File.Exists(activePtr), "active_pointer_must_remain_for_retry");
        });
    }

    private static void AssertFixtureFile(string path, string fixtureName)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required historical fixture missing: {fixtureName} at {path}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(linkPath);
        psi.ArgumentList.Add(targetPath);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Check(process.ExitCode == 0 && FileSystemSafety.IsReparsePoint(linkPath), "junction_creation_failed");
    }
}
