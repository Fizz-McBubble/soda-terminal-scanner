using Microsoft.Win32;

namespace ZZZScannerHelper;

internal interface IUserProtocolStore
{
    string? Read(string keyPath, string valueName);
    void Write(string keyPath, string valueName, string value);
    void DeleteTree(string keyPath);
}

internal sealed class CurrentUserProtocolStore : IUserProtocolStore
{
    public string? Read(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot create HKCU\\{keyPath}.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteTree(string keyPath) => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
}

internal static class SodaProtocolRegistration
{
    internal const string Scheme = "soda-terminal-scanner";
    internal const string RootPath = @"Software\Classes\soda-terminal-scanner";
    internal const string CommandPath = RootPath + @"\shell\open\command";

    public static string CommandValue(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" \"%1\"";

    public static void Register(IUserProtocolStore store, string executablePath)
    {
        var command = CommandValue(executablePath);
        store.Write(RootPath, "", "URL:Soda Terminal Scanner Protocol");
        store.Write(RootPath, "URL Protocol", "");
        store.Write(CommandPath, "", command);
    }

    public static bool Unregister(IUserProtocolStore store, string executablePath)
    {
        var expected = CommandValue(executablePath);
        if (!string.Equals(store.Read(CommandPath, ""), expected, StringComparison.OrdinalIgnoreCase))
            return false;
        store.DeleteTree(RootPath);
        return true;
    }
}
