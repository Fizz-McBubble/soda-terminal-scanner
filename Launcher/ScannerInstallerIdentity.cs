using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace ZZZScannerHelper;

internal static class ScannerInstallerIdentity
{
    internal static string? ReadVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SodaTerminalScanner", writable: false);
            return Validate(key?.GetValue("DisplayVersion") as string,
                key?.GetValue("InstallLocation") as string, HelperStorageManager.DefaultDataRoot());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static string? Validate(string? version, string? location, string managedRoot)
    {
        if (version is null || !Regex.IsMatch(version, @"\A(0|[1-9]\d{0,2})\.(0|[1-9]\d{0,2})\.(0|[1-9]\d{0,2})\z")) return null;
        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location)) return null;
        try
        {
            return Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? version : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
