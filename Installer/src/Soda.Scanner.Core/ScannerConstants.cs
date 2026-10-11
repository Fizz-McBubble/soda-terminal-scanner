namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-13-local-20261011";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.13-local.20261011";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-13-local-20261011-win-x64.zip";
    public const long LockedAssetSize = 131758361L;
    public const string LockedAssetSha256 = "fc3eeb8ca7c8f8ef033038f1673c98ad8e1973c348b800b6f1ef3fae551d8842";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.1.3";
    public const string Publisher = "Soda Terminal";
    public const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SodaTerminalScanner";
    public const string ProtocolRegistryKeyPath = @"Software\Classes\soda-terminal-scanner";

    public const string DefaultPublicOrigin = "https://sodaterminal.com";
    public static readonly string[] AllowedOrigins = new[]
    {
        "https://sodaterminal.com",
        "https://app.sodaterminal.workers.dev"
    };

    public const string HelperExecutableRelative = @"helper\ZZZ-Scanner-Helper.exe";
    public const string UninstallExecutableName = "uninstall.exe";

    public static string GetDefaultManagedRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SodaTerminal", "Scanner");
    }
}
