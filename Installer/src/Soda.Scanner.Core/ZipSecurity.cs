using System.IO.Compression;

namespace Soda.Scanner.Core;

public static class ZipSecurity
{
    public static void ExtractSafe(string zipFilePath, string destinationDirectory, Action<int, int>? progressCallback = null)
    {
        var destFullPath = Path.GetFullPath(destinationDirectory);
        if (!destFullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            destFullPath += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(destFullPath);

        using var archive = ZipFile.OpenRead(zipFilePath);
        var totalEntries = archive.Entries.Count;
        var processed = 0;

        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Contains(':'))
                throw new InvalidOperationException("runtime_archive_link_forbidden");
            var destinationPath = Path.GetFullPath(Path.Combine(destFullPath, entry.FullName));
            if (!destinationPath.StartsWith(destFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Zip slip attempt detected in entry: {entry.FullName}");
            }

            FileSystemSafety.AssertSafePath(destFullPath, destinationPath);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                var parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }

            processed++;
            progressCallback?.Invoke(processed, totalEntries);
        }
    }
}
