using Microsoft.Win32;

namespace Soda.Scanner.Core;

public sealed class RegistrySnapshot
{
    public bool TestMode { get; init; }
    public KeySnapshot? ProtocolSnapshot { get; init; }
    public KeySnapshot? UninstallSnapshot { get; init; }

    public sealed class KeySnapshot
    {
        public bool Exists { get; init; }
        public Dictionary<string, (object Value, RegistryValueKind Kind)> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, KeySnapshot> SubKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Restore(string? installRoot = null)
    {
        if (TestMode) return;

        if (installRoot == null) throw new InvalidOperationException("registry_restore_identity_required");
        var current = RegistryHelper.Capture();
        if (RegistrationIdentity.ProtocolOwned(current.ProtocolSnapshot, Path.Combine(installRoot, ScannerConstants.HelperExecutableRelative)))
            RestoreKey(Registry.CurrentUser, ScannerConstants.ProtocolRegistryKeyPath, ProtocolSnapshot);
        if (RegistrationIdentity.UninstallOwned(current.UninstallSnapshot, installRoot))
            RestoreKey(Registry.CurrentUser, ScannerConstants.UninstallRegistryKeyPath, UninstallSnapshot);
    }

    private static void RestoreKey(RegistryKey rootKey, string subKeyPath, KeySnapshot? snapshot)
    {
        if (snapshot == null || !snapshot.Exists)
        {
            rootKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            return;
        }

        rootKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
        using var targetKey = rootKey.CreateSubKey(subKeyPath, writable: true);
        if (targetKey == null) throw new InvalidOperationException("registry_restore_failed");
        ApplySnapshot(targetKey, snapshot);
    }

    private static void ApplySnapshot(RegistryKey targetKey, KeySnapshot snapshot)
    {
        foreach (var (name, (value, kind)) in snapshot.Values)
        {
            targetKey.SetValue(name, value, kind);
        }
        foreach (var (subName, subSnapshot) in snapshot.SubKeys)
        {
            using var subKey = targetKey.CreateSubKey(subName, writable: true);
            if (subKey != null)
            {
                ApplySnapshot(subKey, subSnapshot);
            }
        }
    }
}

public static class RegistryHelper
{
    public static RegistrySnapshot.KeySnapshot? ReadUninstallIdentity()
    {
        try { return CaptureKey(Registry.CurrentUser, ScannerConstants.UninstallRegistryKeyPath); }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    public static bool HasInstallationRegistration(bool testMode = false)
    {
        if (testMode) return false;
        try
        {
            using var protocol = Registry.CurrentUser.OpenSubKey(ScannerConstants.ProtocolRegistryKeyPath);
            using var uninstall = Registry.CurrentUser.OpenSubKey(ScannerConstants.UninstallRegistryKeyPath);
            return protocol != null || uninstall != null;
        }
        catch (System.Security.SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    public static bool ProtocolCommandMatches(string? command, string managedHelperPath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(managedHelperPath)) return false;
        var fullHelper = Path.GetFullPath(managedHelperPath);
        var expected = $"\"{fullHelper}\" \"%1\"";
        return string.Equals(command.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool UninstallRegistrationMatches(string? location, string? command, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(installRoot)) return false;
        string expectedRoot;
        string normLocation;
        try
        {
            expectedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
            normLocation = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch { return false; }

        if (!string.Equals(normLocation, expectedRoot, StringComparison.OrdinalIgnoreCase)) return false;

        var expectedExe = Path.Combine(expectedRoot, ScannerConstants.UninstallExecutableName);
        return string.Equals(command.Trim(), $"\"{expectedExe}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static RegistrySnapshot Capture(bool testMode = false)
    {
        if (testMode)
        {
            return new RegistrySnapshot { TestMode = true };
        }

        return new RegistrySnapshot
        {
            TestMode = false,
            ProtocolSnapshot = CaptureKey(Registry.CurrentUser, ScannerConstants.ProtocolRegistryKeyPath),
            UninstallSnapshot = CaptureKey(Registry.CurrentUser, ScannerConstants.UninstallRegistryKeyPath)
        };
    }

    private static RegistrySnapshot.KeySnapshot CaptureKey(RegistryKey rootKey, string subKeyPath)
    {
        using var key = rootKey.OpenSubKey(subKeyPath);
        if (key == null)
        {
            return new RegistrySnapshot.KeySnapshot { Exists = false };
        }

        var snapshot = new RegistrySnapshot.KeySnapshot { Exists = true };
        foreach (var valueName in key.GetValueNames())
        {
            var val = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (val != null)
            {
                var kind = key.GetValueKind(valueName);
                snapshot.Values[valueName] = (val, kind);
            }
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            snapshot.SubKeys[subKeyName] = CaptureKey(key, subKeyName);
        }

        return snapshot;
    }

    public static void RegisterProtocol(string managedHelperPath, bool testMode = false)
    {
        if (testMode) return;

        var fullHelper = Path.GetFullPath(managedHelperPath);
        var protocolIdentity = CaptureKey(Registry.CurrentUser, ScannerConstants.ProtocolRegistryKeyPath);
        if (protocolIdentity.Exists && !RegistrationIdentity.ProtocolOwned(protocolIdentity, fullHelper))
            throw new InvalidOperationException("scanner_protocol_owned_by_another_path");
        using (var existing = Registry.CurrentUser.OpenSubKey(ScannerConstants.ProtocolRegistryKeyPath + @"\shell\open\command"))
        {
            var command = existing?.GetValue("") as string;
            if (!string.IsNullOrEmpty(command) && !ProtocolCommandMatches(command, managedHelperPath))
                throw new InvalidOperationException("scanner_protocol_owned_by_another_path");
        }
        if (!File.Exists(fullHelper))
            throw new FileNotFoundException("Managed helper executable missing for protocol registration", fullHelper);

        using var rootKey = Registry.CurrentUser.CreateSubKey(ScannerConstants.ProtocolRegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Failed to create protocol root key");
        rootKey.SetValue("", "URL:Soda Terminal Scanner Protocol", RegistryValueKind.String);
        rootKey.SetValue("URL Protocol", "", RegistryValueKind.String);

        using var shellKey = rootKey.CreateSubKey("shell", writable: true)
            ?? throw new InvalidOperationException("Failed to create protocol shell key");
        using var openKey = shellKey.CreateSubKey("open", writable: true)
            ?? throw new InvalidOperationException("Failed to create protocol open key");
        using var cmdKey = openKey.CreateSubKey("command", writable: true)
            ?? throw new InvalidOperationException("Failed to create protocol command key");

        cmdKey.SetValue("", $"\"{fullHelper}\" \"%1\"", RegistryValueKind.String);
    }

    public static bool RegisterStartMenuShortcut(string root, string origin, bool testMode = false) => ScannerShortcut.Create(root, origin, testMode);
    public static ScannerShortcutChange ApplyStartMenuShortcut(string root, string origin, bool testMode = false) => ScannerShortcut.Apply(root, origin, testMode);
    public static void RollbackStartMenuShortcut(string root, string origin, bool testMode, bool created) => ScannerShortcut.RollbackCreated(root, origin, testMode, created);
    public static void RollbackStartMenuShortcut(string root, string origin, bool testMode, ScannerShortcutChange? change) => ScannerShortcut.Rollback(root, origin, testMode, change);
    public static void RemoveStartMenuShortcut(string root, bool testMode = false) => ScannerShortcut.RemoveOwned(root, testMode);

    public static void RegisterUninstallEntry(string installRoot, string uninstallExePath, bool testMode = false)
    {
        if (testMode) return;

        var fullRoot = Path.GetFullPath(installRoot);
        var fullUninstall = Path.GetFullPath(uninstallExePath);
        var uninstallIdentity = CaptureKey(Registry.CurrentUser, ScannerConstants.UninstallRegistryKeyPath);
        if (uninstallIdentity.Exists && !RegistrationIdentity.UninstallOwned(uninstallIdentity, fullRoot))
            throw new InvalidOperationException("scanner_uninstall_entry_owned_by_another_path");
        using (var existing = Registry.CurrentUser.OpenSubKey(ScannerConstants.UninstallRegistryKeyPath))
        {
            if (existing != null && !UninstallRegistrationMatches(existing.GetValue("InstallLocation") as string, existing.GetValue("UninstallString") as string, fullRoot))
                throw new InvalidOperationException("scanner_uninstall_entry_owned_by_another_path");
        }

        using var key = Registry.CurrentUser.CreateSubKey(ScannerConstants.UninstallRegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Failed to create uninstall registry key");

        key.SetValue("DisplayName", ScannerConstants.DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", ScannerConstants.DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", ScannerConstants.Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", fullRoot, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{fullUninstall}\"", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{fullUninstall}\" --silent", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
    }

    public static void UnregisterUninstallEntry(string installRoot, bool testMode = false)
    {
        if (testMode) return;

        {
            using var key = Registry.CurrentUser.OpenSubKey(ScannerConstants.UninstallRegistryKeyPath);
            if (key == null) return;

            var location = key.GetValue("InstallLocation") as string;
            var uninstallString = key.GetValue("UninstallString") as string;
            if (UninstallRegistrationMatches(location, uninstallString, installRoot) && RegistrationIdentity.UninstallOwned(CaptureKey(Registry.CurrentUser, ScannerConstants.UninstallRegistryKeyPath), installRoot))
            {
                key.Close();
                Registry.CurrentUser.DeleteSubKeyTree(ScannerConstants.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
            }
        }
    }

    public static void CleanProtocolIfMatching(string managedHelperPath, bool testMode = false)
    {
        if (testMode) return;

        {
            using var cmdKey = Registry.CurrentUser.OpenSubKey($@"{ScannerConstants.ProtocolRegistryKeyPath}\shell\open\command");
            if (cmdKey != null)
            {
                var val = cmdKey.GetValue("") as string;
                if (ProtocolCommandMatches(val, managedHelperPath) && RegistrationIdentity.ProtocolOwned(CaptureKey(Registry.CurrentUser, ScannerConstants.ProtocolRegistryKeyPath), managedHelperPath))
                {
                    cmdKey.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(ScannerConstants.ProtocolRegistryKeyPath, throwOnMissingSubKey: false);
                }
            }
        }
    }

    public static bool IsUninstallEntryRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ScannerConstants.UninstallRegistryKeyPath);
        return key != null;
    }
}
