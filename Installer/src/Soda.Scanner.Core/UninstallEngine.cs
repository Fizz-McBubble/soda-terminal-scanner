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
        var versionRoot = Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion);
        foreach (var entry in RuntimeCatalog.Files) RemoveMatching(versionRoot, entry);
        foreach (var directory in new[] { "helper", "helper-bootstrap" })
        foreach (var entry in RuntimeCatalog.Files.Where(entry => entry.Path.StartsWith("helper\\", StringComparison.Ordinal)))
            RemoveMatching(root, entry with { Path = directory + "\\" + entry.Path["helper\\".Length..] });
        RemoveMatching(root, new RuntimeFile("downloads\\" + ScannerConstants.LockedAssetName,
            ScannerConstants.LockedAssetSize, ScannerConstants.LockedAssetSha256));
        foreach (var pointerName in new[] { "active.json", "previous.json" })
        {
            var path = SafeCandidate(root, pointerName);
            if (path == null || !File.Exists(path)) continue;
            if (PointerStore.ReadValidated(path, root) == null) result.PreservedItems.Add(path);
            else DeleteFile(path);
        }
        if (expectedUninstallerHash != null)
        {
            var path = SafeCandidate(root, ScannerConstants.UninstallExecutableName);
            if (path != null && File.Exists(path))
            {
                if (Sha256Util.ComputeFileSha256(path).Equals(expectedUninstallerHash, StringComparison.OrdinalIgnoreCase)) DeleteFile(path);
                else result.PreservedItems.Add(path);
            }
        }
        // Prune only directories used by known components, without recursively walking the tree.
        var knownDirectories = RuntimeCatalog.Files.SelectMany(entry =>
        {
            var parents = new List<string>();
            for (var parent = Path.GetDirectoryName(entry.Path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                parents.Add(Path.Combine(versionRoot, parent));
            return parents;
        }).Append(versionRoot).Append(Path.Combine(root, "versions"))
            .Concat(new[] { "helper", "helper-bootstrap", "downloads" }.Select(name => Path.Combine(root, name)));
        foreach (var directory in knownDirectories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
            PruneEmpty(directory);
        // Keep the Windows uninstall entry until file removal actually succeeds, so a failed uninstall remains retryable.
        RegistryHelper.CleanProtocolIfMatching(Path.Combine(root, ScannerConstants.HelperExecutableRelative), testMode);
        RegistryHelper.UnregisterUninstallEntry(root, testMode);
        PruneEmpty(root);
        result.Success = true;
        result.Message = "已卸载，扫描结果已保留。";
        return result;

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
