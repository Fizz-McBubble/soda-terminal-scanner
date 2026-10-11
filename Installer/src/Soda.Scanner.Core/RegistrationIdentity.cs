using Microsoft.Win32;
namespace Soda.Scanner.Core;

public static class RegistrationIdentity
{
    public static bool ProtocolOwned(RegistrySnapshot.KeySnapshot? key, string helper)
    {
        if (key is null || !key.Exists || !Values(key, new Dictionary<string, object> { [""] = "URL:Soda Terminal Scanner Protocol", ["URL Protocol"] = "" })) return false;
        var shell = Child(key, "shell"); var open = Child(shell, "open"); var command = Child(open, "command");
        return shell?.Values.Count == 0 && open?.Values.Count == 0 && command is not null && command.SubKeys.Count == 0
            && Values(command, new Dictionary<string, object> { [""] = "\"" + Path.GetFullPath(helper) + "\" \"%1\"" });
    }
    public static bool UninstallOwned(RegistrySnapshot.KeySnapshot? key, string root)
    {
        if (key is null || !key.Exists || key.SubKeys.Count != 0 || !RegistryHelper.UninstallRegistrationMatches(
            String(key, "InstallLocation"), String(key, "UninstallString"), root)) return false;
        var expected = new Dictionary<string, object>
        {
            ["DisplayName"] = String(key, "DisplayName")!, ["DisplayVersion"] = String(key, "DisplayVersion")!,
            ["Publisher"] = "Soda Terminal", ["InstallLocation"] = Path.GetFullPath(root),
            ["UninstallString"] = "\"" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\"",
            ["QuietUninstallString"] = "\"" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\" --silent",
            ["NoModify"] = 1, ["NoRepair"] = 0
        };
        return String(key, "DisplayName") is "Soda Terminal 扫描助手" or ScannerConstants.DisplayName
            && Version.TryParse(String(key, "DisplayVersion"), out _) && Values(key, expected);
    }
    private static string? String(RegistrySnapshot.KeySnapshot key, string name) => key.Values.TryGetValue(name, out var value) ? value.Value as string : null;
    private static RegistrySnapshot.KeySnapshot? Child(RegistrySnapshot.KeySnapshot? key, string name)
        => key is not null && key.SubKeys.Count == 1 ? key.SubKeys.GetValueOrDefault(name) : null;
    private static bool Values(RegistrySnapshot.KeySnapshot key, Dictionary<string, object> expected)
        => key.Values.Count == expected.Count && expected.All(pair => key.Values.TryGetValue(pair.Key, out var value)
            && Equals(value.Value, pair.Value) && value.Kind == (pair.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String));
}
