using System.Diagnostics;

namespace Soda.Scanner.Core;

public class InstallResult
{
    public bool Success { get; set; }
    public bool RepeatedInstall { get; set; }
    public bool Started { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = ScannerConstants.LockedRuntimeVersion;
    public string InstallRoot { get; set; } = string.Empty;
}

public static class InstallEngine
{
    public static InstallResult ExecuteInstall(string installRoot, string publicOrigin,
        Func<Stream> offlineArchiveStreamProvider, Func<Stream> uninstallStubStreamProvider,
        bool testMode = false, bool noLaunch = false, Action<string, int>? progressCallback = null,
        string faultInjection = "none", bool firstRunOnly = false)
    {
        var root = FileSystemSafety.ValidateRoot(installRoot, testMode);
        if (!ScannerConstants.AllowedOrigins.Contains(publicOrigin, StringComparer.Ordinal))
            throw new InvalidOperationException("scanner_public_origin_invalid");
        if (faultInjection != "none" && (!testMode || !new[] { "components", "registration", "previous", "active" }.Contains(faultInjection)))
            throw new InvalidOperationException("runtime_fault_injection_forbidden");
        using var operationLock = FileSystemSafety.AcquireOperationLock(root);
        if (firstRunOnly && InstallerStartup.HasInstallationTraces(root, RegistryHelper.HasInstallationRegistration(testMode)))
            throw new InvalidOperationException("scanner_setup_existing_installation");
        Directory.CreateDirectory(root);
        var work = FileSystemSafety.SafePath(root, ".setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var activePath = FileSystemSafety.SafePath(root, "active.json");
        var previousPath = FileSystemSafety.SafePath(root, "previous.json");
        var versionRoot = FileSystemSafety.SafePath(root, "versions\\" + ScannerConstants.LockedRuntimeVersion);
        var oldActive = PointerStore.ReadValidated(activePath, root);
        var registrySnapshot = RegistryHelper.Capture(testMode);
        var wasRunning = ProcessHelper.IsManagedHelperRunning(root, testMode);
        var committed = false;
        var cleanupWork = false;
        using var transaction = new FileTransaction(root, work);
        try
        {
            progressCallback?.Invoke("正在准备安装…", 5);
            var archive = FileSystemSafety.SafePath(work, ScannerConstants.LockedAssetName);
            using (var input = offlineArchiveStreamProvider())
            using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            if (new FileInfo(archive).Length != ScannerConstants.LockedAssetSize ||
                Sha256Util.ComputeFileSha256(archive) != ScannerConstants.LockedAssetSha256)
                throw new InvalidOperationException("runtime_asset_identity_mismatch");
            var stagedRuntime = FileSystemSafety.SafePath(work, "runtime");
            ZipSecurity.ExtractSafe(archive, stagedRuntime, (done, total) => progressCallback?.Invoke("正在展开扫描组件…", 10 + (int)(done * 50 / total)));
            progressCallback?.Invoke("正在校验扫描组件…", 62);
            RuntimeCatalog.StageSupplement(work, stagedRuntime);
            RuntimeCatalog.Verify(stagedRuntime);
            if (!testMode) ProbeRuntime(stagedRuntime);
            var stub = FileSystemSafety.SafePath(work, ScannerConstants.UninstallExecutableName);
            using (var input = uninstallStubStreamProvider())
            using (var output = new FileStream(stub, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            using (var input = File.OpenRead(stub))
                if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidOperationException("uninstaller_invalid");

            ProcessHelper.TerminateManagedComponents(root, testMode);
            var repeated = oldActive?.Version == ScannerConstants.LockedRuntimeVersion;
            // Replace only known program files; leave every extra installed file alone.
            var totalBytes = Math.Max(1, RuntimeCatalog.Files.Sum(entry => entry.Size));
            long installedBytes = 0;
            foreach (var entry in RuntimeCatalog.Files)
            {
                transaction.Copy(FileSystemSafety.SafePath(stagedRuntime, entry.Path), FileSystemSafety.SafePath(versionRoot, entry.Path), preserveReplaced: true);
                installedBytes += entry.Size;
                progressCallback?.Invoke("正在安装扫描组件…", 68 + (int)(installedBytes * 18 / totalBytes));
            }
            foreach (var entry in RuntimeCatalog.Files.Where(entry => entry.Path.StartsWith("helper\\", StringComparison.Ordinal)))
                transaction.Copy(FileSystemSafety.SafePath(stagedRuntime, entry.Path), FileSystemSafety.SafePath(root, entry.Path), preserveReplaced: true);
            transaction.Copy(stub, FileSystemSafety.SafePath(root, ScannerConstants.UninstallExecutableName), preserveReplaced: true);
            Inject("components");
            RuntimeCatalog.Verify(versionRoot);
            RegistryHelper.RegisterProtocol(FileSystemSafety.SafePath(root, ScannerConstants.HelperExecutableRelative), testMode);
            RegistryHelper.RegisterUninstallEntry(root, FileSystemSafety.SafePath(root, ScannerConstants.UninstallExecutableName), testMode);
            Inject("registration");
            progressCallback?.Invoke("正在完成安装…", 90);
            if (oldActive != null && oldActive.Version != ScannerConstants.LockedRuntimeVersion)
            {
                var stagedPrevious = FileSystemSafety.SafePath(work, "previous.json");
                PointerStore.WritePointerAtomic(stagedPrevious, oldActive);
                transaction.Copy(stagedPrevious, previousPath);
            }
            Inject("previous");
            var stagedActive = FileSystemSafety.SafePath(work, "active.json");
            PointerStore.WritePointerAtomic(stagedActive, new PointerInfo
            {
                Version = ScannerConstants.LockedRuntimeVersion, Path = versionRoot, VerifiedAt = DateTime.UtcNow.ToString("O")
            });
            transaction.Copy(stagedActive, activePath, preserveReplaced: oldActive == null);
            Inject("active");
            transaction.Commit();
            committed = true;
            cleanupWork = true;
            var started = !noLaunch && !testMode && ProcessHelper.StartManagedHelper(
                FileSystemSafety.SafePath(root, ScannerConstants.HelperExecutableRelative), publicOrigin);
            progressCallback?.Invoke("安装完成", 100);
            return new InstallResult
            {
                Success = true, RepeatedInstall = repeated, Started = started, InstallRoot = root,
                Message = "安装完成，请返回网页连接助手。"
            };
        }
        catch
        {
            if (!committed)
            {
                transaction.Rollback();
                registrySnapshot.Restore();
                cleanupWork = true;
                if (wasRunning) ProcessHelper.StartManagedHelper(FileSystemSafety.SafePath(root, ScannerConstants.HelperExecutableRelative), publicOrigin, testMode);
            }
            throw;
        }
        finally { if (cleanupWork) FileSystemSafety.RemoveWorkDirectory(root, work); }

        void Inject(string point)
        {
            if (faultInjection == point) throw new InvalidOperationException("runtime_injected_" + point);
        }
    }

    public static bool VerifyRuntimeHealth(string runtimeRoot)
    {
        try { RuntimeCatalog.Verify(runtimeRoot); return true; }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static void ProbeRuntime(string root)
    {
        Run("native\\ZZZ-Scanner.Next.Soda.exe", "--probe-runtime", 0);
        Run("native\\ZZZ-Scanner.Next.Soda.exe", "--probe-r4-runtime", 0);
        Run("ocr\\Soda.ScannerPpOcrV6.exe", null, 2);
        void Run(string relative, string? argument, int expected)
        {
            var start = new ProcessStartInfo(FileSystemSafety.SafePath(root, relative))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (argument != null) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("runtime_health_probe_failed");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new InvalidOperationException("runtime_health_probe_timeout"); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != expected) throw new InvalidOperationException("runtime_health_probe_failed");
        }
    }
}
