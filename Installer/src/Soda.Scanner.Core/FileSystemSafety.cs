using System.Security.Cryptography;
using System.Text;

namespace Soda.Scanner.Core;

public static class FileSystemSafety
{
    public static string ValidateRoot(string installRoot, bool testMode)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var managed = Path.GetFullPath(ScannerConstants.GetDefaultManagedRoot()).TrimEnd(Path.DirectorySeparatorChar);
        if (testMode)
        {
            if (!root.Split(Path.DirectorySeparatorChar).Contains("outputs", StringComparer.OrdinalIgnoreCase) ||
                root.Equals(managed, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(managed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                managed.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("test_install_root_must_be_isolated_in_outputs");
        }
        else if (!root.Equals(managed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("managed_install_root_required");
        AssertNoReparsePoints(root);
        return root;
    }

    public static string SafePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new InvalidOperationException("managed_path_invalid");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        AssertSafePath(root, path);
        return path;
    }

    public static void AssertSafePath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("managed_path_outside_root");
        AssertNoReparsePoints(fullPath);
    }

    public static void AssertNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("managed_path_is_link");
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static IDisposable AcquireOperationLock(string root)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, "Local\\SodaScannerSetup-" + identity);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new InvalidOperationException("scanner_setup_already_running"); }
        return new OperationLock(mutex);
    }

    // Used only for a unique working directory created by this operation. Never for an installed version.
    public static void RemoveWorkDirectory(string root, string path)
    {
        AssertSafePath(root, path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            AssertSafePath(root, entry);
            if (Directory.Exists(entry)) RemoveWorkDirectory(root, entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private sealed class OperationLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
