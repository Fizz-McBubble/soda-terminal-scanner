namespace Soda.Scanner.Core;

public sealed class UninstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> RemovedItems { get; } = new();
    public List<string> PreservedItems { get; } = new();
}

public static class UninstallEngine
{
    public static UninstallResult ExecuteUninstall(string installRoot, bool testMode = false,
        Action<string>? statusCallback = null, string? expectedUninstallerHash = null)
    {
        var root = FileSystemSafety.ValidateRoot(installRoot, testMode);
        using var operationLock = FileSystemSafety.AcquireOperationLock(root);
        var result = new UninstallResult();
        statusCallback?.Invoke("正在卸载…");
        ProcessHelper.TerminateManagedComponents(root, testMode);
        foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
        {
            var versionRoot = Path.Combine(root, "versions", pkg.Version);
            foreach (var entry in pkg.Files) RemoveMatching(versionRoot, entry);
        }
        foreach (var directory in new[] { "helper", "helper-bootstrap" })
        foreach (var entry in LegacyRuntimeCatalog.AllHelperFiles)
            RemoveMatching(root, entry with { Path = directory + "\\" + entry.Path["helper\\".Length..] });
        // Repair/upgrade recovery copies may include known executable bytes.
        // Only the installer's exact operation directories and catalog paths qualify.
        var preservedRoot = SafeCandidate(root, "preserved-components");
        var recoveryRoots = new List<string>();
        if (preservedRoot != null && Directory.Exists(preservedRoot))
        foreach (var directory in Directory.EnumerateDirectories(preservedRoot))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), "^\\.setup-[a-f0-9]{32}$")) continue;
            var safe = SafeCandidate(root, Path.GetRelativePath(root, directory));
            if (safe == null) continue;
            recoveryRoots.Add(safe);
            foreach (var entry in LegacyRuntimeCatalog.AllDistinctFiles)
            {
                RemoveMatching(safe, entry);
                foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
                {
                    RemoveMatching(safe, entry with { Path = "versions\\" + pkg.Version + "\\" + entry.Path });
                }
            }
        }
        foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
        {
            RemoveMatching(root, new RuntimeFile("downloads\\" + pkg.AssetName,
                pkg.AssetSize, pkg.AssetSha256));
        }
        foreach (var pointerName in new[] { "active.json", "previous.json" })
        {
            var path = SafeCandidate(root, pointerName);
            if (path == null || !File.Exists(path)) continue;
            if (PointerStore.ReadValidated(path, root) == null) result.PreservedItems.Add(path);
            else DeleteFile(path);
        }
        var uninstallerPath = SafeCandidate(root, ScannerConstants.UninstallExecutableName);
        if (uninstallerPath != null && File.Exists(uninstallerPath))
        {
            if (IsKnownUninstaller(uninstallerPath, expectedUninstallerHash)) DeleteFile(uninstallerPath);
            else result.PreservedItems.Add(uninstallerPath);
        }
        // Prune only directories used by known components, without recursively walking the tree.
        var knownDirectories = new List<string>();
        foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
        {
            var versionRoot = Path.Combine(root, "versions", pkg.Version);
            knownDirectories.Add(versionRoot);
            foreach (var entry in pkg.Files)
            {
                for (var parent = Path.GetDirectoryName(entry.Path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    knownDirectories.Add(Path.Combine(versionRoot, parent));
            }
        }
        knownDirectories.Add(Path.Combine(root, "versions"));
        knownDirectories.Add(Path.Combine(root, "helper"));
        knownDirectories.Add(Path.Combine(root, "helper-bootstrap"));
        knownDirectories.Add(Path.Combine(root, "downloads"));
        knownDirectories.Add(Path.Combine(root, "preserved-components"));

        foreach (var owner in recoveryRoots)
        {
            knownDirectories.Add(owner);
            foreach (var entry in LegacyRuntimeCatalog.AllDistinctFiles)
            {
                var candidateRelatives = new List<string> { entry.Path };
                foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
                    candidateRelatives.Add("versions\\" + pkg.Version + "\\" + entry.Path);
                foreach (var rel in candidateRelatives)
                {
                    for (var parent = Path.GetDirectoryName(rel); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                        knownDirectories.Add(Path.Combine(owner, parent));
                }
            }
        }
        foreach (var directory in knownDirectories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
            PruneEmpty(directory);
        // Keep the Windows uninstall entry until file removal actually succeeds, so a failed uninstall remains retryable.
        RegistryHelper.RemoveStartMenuShortcut(root, testMode);
        RegistryHelper.CleanProtocolIfMatching(Path.Combine(root, ScannerConstants.HelperExecutableRelative), testMode);
        RegistryHelper.CleanProtocolIfMatching(Path.Combine(root, "helper-bootstrap\\ZZZ-Scanner-Helper.exe"), testMode);
        RegistryHelper.UnregisterUninstallEntry(root, testMode);
        PruneEmpty(root);
        result.Success = true;
        result.Message = "已卸载，扫描结果已保留。";
        return result;

        bool IsKnownUninstaller(string path, string? expectedHash)
        {
            var hash = Sha256Util.ComputeFileSha256(path);
            if (expectedHash != null && hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return true;
            var len = new FileInfo(path).Length;
            return LegacyRuntimeCatalog.KnownStubs.Any(stub =>
                stub.Size == len && hash.Equals(stub.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        string? SafeCandidate(string ownerRoot, string relative)
        {
            try { return FileSystemSafety.SafePath(ownerRoot, relative); }
            catch (InvalidOperationException ex) when (ex.Message == "managed_path_is_link")
            {
                result.PreservedItems.Add(Path.Combine(ownerRoot, relative));
                return null;
            }
        }

        void RemoveMatching(string ownerRoot, RuntimeFile entry)
        {
            var path = SafeCandidate(ownerRoot, entry.Path);
            if (path == null || !File.Exists(path)) return;
            if (RuntimeCatalog.Matches(path, entry)) DeleteFile(path);
            else result.PreservedItems.Add(path);
        }

        void DeleteFile(string path)
        {
            FileSystemSafety.AssertSafePath(root, path);
            File.Delete(path); // File/permission/locking failures must reach the caller.
            result.PreservedItems.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
            result.RemovedItems.Add(path);
        }

        void PruneEmpty(string directory)
        {
            try { FileSystemSafety.AssertSafePath(root, directory); }
            catch (InvalidOperationException ex) when (ex.Message == "managed_path_is_link") { return; }
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
                result.RemovedItems.Add(directory);
            }
        }
    }
}
