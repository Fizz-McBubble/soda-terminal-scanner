using System.Diagnostics;
using System.Text.Json;
using Soda.Scanner.Core;

if (args.Length == 2 && args[1] == "--installer-feedback")
    return InstallerFeedbackTests.Run(Path.GetFullPath(args[0]));

if (args.Length == 4 && args[3] == "--installer-safety")
{
    var safetyRoot = FileSystemSafety.ValidateRoot(Path.GetFullPath(args[0]), true);
    Directory.CreateDirectory(safetyRoot);
    var checks = new List<string>();
    try
    {
        void SafetyTest(string name, Action action) { action(); checks.Add(name); Console.WriteLine("PASS " + name); }
        PayloadFilesTests.Run(safetyRoot, SafetyTest);
        InstallerUpgradeTests.Run(safetyRoot, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), SafetyTest);
        Legacy116RuntimeTests.Run(safetyRoot, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), SafetyTest);
        SafetyTest("successful install reports finite phases without changing installed identity", () =>
        {
            var phases = new List<string>();
            var target = Path.Combine(safetyRoot, "phase-success");
            var result = InstallEngine.ExecuteInstall(target, ScannerConstants.DefaultPublicOrigin,
                () => File.OpenRead(args[1]), () => File.OpenRead(args[2]), testMode: true, noLaunch: true,
                phaseCallback: phases.Add);
            if (!result.Success || !phases.SequenceEqual(new[] { "prepare", "payload_verify", "extract", "verify", "copy", "register", "commit", "start" }))
                throw new Exception("successful_phase_sequence_changed");
            RuntimeCatalog.Verify(Path.Combine(target, "versions", ScannerConstants.LockedRuntimeVersion));
            if (Sha256Util.ComputeFileSha256(Path.Combine(target, ScannerConstants.UninstallExecutableName)) != Sha256Util.ComputeFileSha256(args[2]))
                throw new Exception("new_uninstaller_identity_changed");
        });
        File.WriteAllText(Path.Combine(safetyRoot, "installer-safety.json"), JsonSerializer.Serialize(new { exitCode = 0, passed = checks.Count, checks, registryWrites = 0, actualInstall = false, uac = false, helperStarted = false }));
        return 0;
    }
    catch (Exception error)
    {
        File.WriteAllText(Path.Combine(safetyRoot, "installer-safety.json"), JsonSerializer.Serialize(new { exitCode = 1, passed = checks.Count, checks, error = error.ToString() }));
        Console.Error.WriteLine(error); return 1;
    }
}

if (args.Length == 4 && args[3] == "--focused-install")
    return FocusedInstallTests.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length == 2 && args[1] == "--version-identity")
    return FocusedInstallTests.RunVersionIdentity(Path.GetFullPath(args[0]));
if (args.Length == 3 && args[1] == "--version-identity")
    return FocusedInstallTests.RunVersionIdentity(Path.GetFullPath(args[0]), Path.GetFullPath(args[2]));

var basePath = FileSystemSafety.ValidateRoot(Path.GetFullPath(args[0]), true);
var archive = Path.GetFullPath(args[1]);
var stub = Path.GetFullPath(args[2]);
Directory.CreateDirectory(basePath);
var results = new List<object>();
var clean = Path.Combine(basePath, "clean");
var protectedRoot = Path.Combine(basePath, "protected");
var outside = Path.Combine(basePath, "outside");
Directory.CreateDirectory(outside);

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name);
}
void Refuses(Action action)
{
    var failed = false;
    try { action(); } catch (Exception) { failed = true; }
    Check(failed, "expected rejection");
}
InstallResult Install(string root, string fault = "none", bool firstRunOnly = false) => InstallEngine.ExecuteInstall(root,
    ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive), () => File.OpenRead(stub),
    testMode: true, noLaunch: true, faultInjection: fault, firstRunOnly: firstRunOnly);
string Version(string root) => Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
void Write(string path, string data) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, data); }
Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(root, path), Sha256Util.ComputeFileSha256);
void SameSnapshot(Dictionary<string, string> before, Dictionary<string, string> after)
    => Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), "file bytes changed");
void Junction(string link, string target)
{
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    Check(process.ExitCode == 0 && FileSystemSafety.IsReparsePoint(link), "junction could not be created: " + error);
}
try
{
    InstallerFormTests.Run(basePath, Test);
    PayloadFilesTests.Run(basePath, Test);
    InstallerUpgradeTests.Run(basePath, archive, stub, Test);
    Test("root guards and no actual installation", () =>
    {
        Refuses(() => FileSystemSafety.ValidateRoot(ScannerConstants.GetDefaultManagedRoot(), true));
        Refuses(() => FileSystemSafety.ValidateRoot(basePath, false));
        Refuses(() => FileSystemSafety.SafePath(basePath, "..\\escape.txt"));
        Refuses(() => FileSystemSafety.SafePath(basePath, "file.txt:stream"));
    });
    Test("clean install, pinned runtime and managed helper", () =>
    {
        Check(Install(clean, firstRunOnly: true).Success, "install failed"); RuntimeCatalog.Verify(Version(clean));
        Check(PointerStore.ReadValidated(Path.Combine(clean, "active.json"), clean)?.Path == Version(clean), "invalid active pointer");
        Check(File.Exists(Path.Combine(clean, ScannerConstants.HelperExecutableRelative)), "helper not deployed");
    });
    Test("repeat install and missing managed helper repair", () =>
    {
        Write(Path.Combine(clean, "outputs", "scan.json"), "SCAN_RESULT");
        Write(Path.Combine(Version(clean), "unknown.txt"), "UNKNOWN");
        Check(Install(clean).RepeatedInstall, "not repeat");
        var helper = Path.Combine(clean, ScannerConstants.HelperExecutableRelative);
        File.Delete(helper); Install(clean);
        Check(RuntimeCatalog.Matches(helper, RuntimeCatalog.Files.Single(file => file.Path == ScannerConstants.HelperExecutableRelative)), "helper not repaired");
    });
    Test("automatic engine recheck refuses existing install before transaction writes", () =>
    {
        var before = Snapshot(clean);
        Refuses(() => InstallEngine.ExecuteInstall(clean, ScannerConstants.DefaultPublicOrigin,
            () => File.OpenRead(archive), () => File.OpenRead(stub), testMode: true, noLaunch: true, firstRunOnly: true));
        SameSnapshot(before, Snapshot(clean));
    });
    Test("repair preserves substituted program bytes and unknown files", () =>
    {
        var replaced = Path.Combine(Version(clean), "native", "Data", "drive_discs.json");
        Write(replaced, "PRESERVE_REPLACED_BYTES"); Install(clean); RuntimeCatalog.Verify(Version(clean));
        Check(Directory.GetFiles(Path.Combine(clean, "preserved-components"), "drive_discs.json", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file) == "PRESERVE_REPLACED_BYTES"), "replacement not preserved");
        Check(File.ReadAllText(Path.Combine(Version(clean), "unknown.txt")) == "UNKNOWN", "unknown file changed");
    });
    var old = new PointerInfo { Version = "soda-scanner-zzz-next-ppocrv6-18-rc8-2", Path = Path.Combine(clean, "versions", "soda-scanner-zzz-next-ppocrv6-18-rc8-2"), VerifiedAt = "old" };
    Directory.CreateDirectory(old.Path);
    Write(Path.Combine(old.Path, "user-data.txt"), "LEGACY_USER_DATA");
    PointerStore.WritePointerAtomic(Path.Combine(clean, "active.json"), old);
    PointerStore.WritePointerAtomic(Path.Combine(clean, "previous.json"), old);
    Write(Path.Combine(clean, ScannerConstants.HelperExecutableRelative), "MZ_OLD_HELPER");
    foreach (var fault in new[] { "components", "registration", "previous", "active" })
    Test("actual failure rollback after " + fault, () =>
    {
        var before = Snapshot(clean); Refuses(() => Install(clean, fault)); SameSnapshot(before, Snapshot(clean));
        Check(PointerStore.ReadValidated(Path.Combine(clean, "active.json"), clean)?.Version == old.Version, "old pointer not restored");
    });
    Test("successful upgrade preserves previous pointer and scan results", () =>
    {
        Install(clean);
        Check(PointerStore.ReadValidated(Path.Combine(clean, "previous.json"), clean)?.Version == old.Version, "previous not retained");
        Check(File.ReadAllText(Path.Combine(clean, "outputs", "scan.json")) == "SCAN_RESULT", "scan changed");
        Check(File.ReadAllText(Path.Combine(old.Path, "user-data.txt")) == "LEGACY_USER_DATA", "legacy changed");
    });
    Test("locked-file uninstall fails and stays retryable", () =>
    {
        using var held = new FileStream(Path.Combine(Version(clean), "helper", "scanner-public-origin.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Refuses(() => UninstallEngine.ExecuteUninstall(clean, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub)));
        Check(File.Exists(Path.Combine(clean, "active.json")) && File.Exists(Path.Combine(clean, "uninstall.exe")), "retry entry files disappeared");
    });
    Test("regular uninstall and reinstall preserves data", () =>
    {
        Install(clean);
        UninstallEngine.ExecuteUninstall(clean, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub));
        Check(!File.Exists(Path.Combine(clean, ScannerConstants.HelperExecutableRelative)), "helper remains");
        Check(!File.Exists(Path.Combine(clean, "active.json")) && !File.Exists(Path.Combine(clean, "uninstall.exe")), "managed files remain");
        Check(File.ReadAllText(Path.Combine(clean, "outputs", "scan.json")) == "SCAN_RESULT", "scan deleted");
        Install(clean); RuntimeCatalog.Verify(Version(clean));
    });
    Test("nested junction, altered descriptor and substituted helper are preserved", () =>
    {
        Install(protectedRoot);
        Write(Path.Combine(Version(protectedRoot), "unknown.txt"), "DESCRIPTOR_TARGET");
        Write(Path.Combine(Version(protectedRoot), "scanner-runtime.json"), "{\"entries\":[{\"path\":\"unknown.txt\"}]}");
        Write(Path.Combine(protectedRoot, ScannerConstants.HelperExecutableRelative), "UNKNOWN_HELPER");
        var data = Path.Combine(Version(protectedRoot), "native", "Data");
        foreach (var entry in RuntimeCatalog.Files.Where(entry => entry.Path.StartsWith("native\\Data\\")))
        {
            var original = Path.Combine(Version(protectedRoot), entry.Path);
            File.Copy(original, Path.Combine(outside, Path.GetFileName(entry.Path))); File.Delete(original);
        }
        Directory.Delete(data, false); Junction(data, outside);
        Write(Path.Combine(outside, ScannerConstants.LockedAssetName), "OUTSIDE_ZIP_SENTINEL");
        Junction(Path.Combine(protectedRoot, "downloads"), outside);
        var before = Snapshot(outside);
        UninstallEngine.ExecuteUninstall(protectedRoot, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub));
        SameSnapshot(before, Snapshot(outside));
        Check(File.ReadAllText(Path.Combine(protectedRoot, ScannerConstants.HelperExecutableRelative)) == "UNKNOWN_HELPER", "unknown helper deleted");
        Check(File.Exists(Path.Combine(Version(protectedRoot), "scanner-runtime.json")) && File.Exists(Path.Combine(Version(protectedRoot), "unknown.txt")), "untrusted descriptor enabled deletion");
        UninstallEngine.ExecuteUninstall(protectedRoot, true);
        SameSnapshot(before, Snapshot(outside));
        Refuses(() => Install(protectedRoot)); SameSnapshot(before, Snapshot(outside));
    });
    Test("root and ancestor junction rejected without outside writes", () =>
    {
        var linked = Path.Combine(basePath, "linked-root"); Junction(linked, outside);
        var before = Snapshot(outside);
        Refuses(() => UninstallEngine.ExecuteUninstall(linked, true));
        Refuses(() => Install(linked)); Refuses(() => Install(Path.Combine(linked, "child")));
        SameSnapshot(before, Snapshot(outside));
    });
    Test("registry removal uses exact commands and root identity", () =>
    {
        var helper = Path.Combine(clean, ScannerConstants.HelperExecutableRelative);
        var command = $"\"{helper}\" \"%1\"";
        Check(RegistryHelper.ProtocolCommandMatches(command, helper), "valid protocol rejected");
        Check(!RegistryHelper.ProtocolCommandMatches("other.exe " + command, helper), "substring accepted");
        Check(!RegistryHelper.ProtocolCommandMatches(command + " --other", helper), "suffix accepted");
        Check(RegistryHelper.UninstallRegistrationMatches(clean, $"\"{Path.Combine(clean, "uninstall.exe")}\"", clean), "valid uninstall rejected");
        Check(!RegistryHelper.UninstallRegistrationMatches(clean + "-other", $"\"{Path.Combine(clean, "uninstall.exe")}\"", clean), "foreign root accepted");
    });
    Test("real packaged native health probes without game or service start", () => InstallEngine.ProbeRuntime(Version(clean)));
    File.WriteAllText(Path.Combine(basePath, "safety-report.json"), JsonSerializer.Serialize(new { passed = true, tests = results, actualUserInstall = false, actualRegistryWrites = false, gameScan = false }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    File.WriteAllText(Path.Combine(basePath, "safety-report.json"), JsonSerializer.Serialize(new { passed = false, tests = results, error = error.Message }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(error); return 1;
}
