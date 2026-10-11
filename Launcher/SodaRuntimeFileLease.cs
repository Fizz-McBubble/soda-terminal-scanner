namespace ZZZScannerHelper;

// Windows share modes keep verified bytes immutable while UAC is pending and
// while the child/worker can still load them. No ACL or elevation changes.
internal sealed class SodaRuntimeFileLease : IDisposable
{
    private readonly List<FileStream> _files = [];

    internal static SodaRuntimeFileLease Acquire(SodaRuntimeIdentity expected, string? installRoot = null)
    {
        var lease = new SodaRuntimeFileLease();
        try
        {
            foreach (var path in new[]
            {
                Path.Combine(installRoot ?? SodaRuntimeLocator.DefaultInstallRoot(), "active.json"),
                Path.Combine(expected.Root, "scanner-runtime.json"),
                expected.EntryPath, expected.PpOcrV6.WorkerPath,
                expected.PpOcrV6.ModelPath, expected.PpOcrV6.ConfigPath
            }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("soda_runtime_reparse_file");
                for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("soda_runtime_reparse_directory");
                    if (directory.Equals(Path.GetFullPath(installRoot ?? SodaRuntimeLocator.DefaultInstallRoot()), StringComparison.OrdinalIgnoreCase)) break;
                }
                lease._files.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            // Detect a change between the initial Load and acquiring all handles.
            if (SodaRuntimeLocator.Load(installRoot) != expected)
                throw new InvalidDataException("soda_runtime_identity_changed");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var file in _files) file.Dispose();
        _files.Clear();
    }
}
