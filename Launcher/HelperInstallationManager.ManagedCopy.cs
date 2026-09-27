using System.Diagnostics;

namespace ZZZScannerHelper;

internal static partial class HelperInstallationManager
{
    internal static bool IsSameManagedInstallation(string existingPath, string managedPath) =>
        Path.GetFullPath(existingPath).Equals(Path.GetFullPath(managedPath), StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> BackupManagedHelperAsync(string managedPath)
    {
        var backupPath = managedPath + ".previous-install";
        File.Copy(managedPath, backupPath, overwrite: true);
        await VerifySameFileAsync(managedPath, backupPath);
        return backupPath;
    }

    internal static async Task RestoreMissingManagedHelperAsync(string managedPath, string backupPath)
    {
        if (File.Exists(managedPath)) return;
        File.Copy(backupPath, managedPath);
        await VerifySameFileAsync(backupPath, managedPath);
    }

    internal static async Task InstallManagedCopyAsync(
        string processPath, string managedPath, string[] args,
        Func<ProcessStartInfo, bool>? launch = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        var stagingPath = managedPath + ".installing";
        var backupPath = managedPath + ".previous-install";
        File.Copy(processPath, stagingPath, overwrite: true);
        await VerifySameFileAsync(processPath, stagingPath);
        var hadPrevious = File.Exists(managedPath);
        if (hadPrevious)
        {
            await BackupManagedHelperAsync(managedPath);
        }
        try
        {
            File.Move(stagingPath, managedPath, overwrite: true);
            var childArguments = new List<string>
            {
                BootstrapArgument,
                Environment.ProcessId.ToString(),
                processPath,
            };
            childArguments.AddRange(args);
            var startInfo = new ProcessStartInfo { FileName = managedPath, UseShellExecute = false };
            foreach (var argument in childArguments) startInfo.ArgumentList.Add(argument);
            var started = launch is null ? Process.Start(startInfo) is not null : launch(startInfo);
            if (!started) throw new InvalidOperationException("Cannot start the managed Helper process.");
        }
        catch
        {
            if (hadPrevious && File.Exists(backupPath))
            {
                File.Copy(backupPath, managedPath, overwrite: true);
                await VerifySameFileAsync(backupPath, managedPath);
            }
            else if (!hadPrevious)
            {
                TryDelete(managedPath);
            }
            throw;
        }
    }
}
