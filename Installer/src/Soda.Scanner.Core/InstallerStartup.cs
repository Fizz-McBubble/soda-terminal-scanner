namespace Soda.Scanner.Core;

public static class InstallerStartup
{
    // Even incomplete or unfamiliar contents can belong to a previous installation.
    // Inspect only the root entries; never read user results or follow child links.
    public static bool HasInstallationTraces(string installRoot, bool registrationExists)
    {
        if (registrationExists) return true;
        try
        {
            if (File.Exists(installRoot)) return true;
            return Directory.Exists(installRoot) && Directory.EnumerateFileSystemEntries(installRoot).Any();
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
