namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-5";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.5";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-5-win-x64.zip";
    public const long LockedAssetSize = 133910367L;
    public const string LockedAssetSha256 = "eb31c9084ef15d19906aed0929b5772389f1d40190ef1a9adf6b3216b46294d7";

    public const string DisplayName = "Soda Terminal 扫描助手";
    public const string DisplayVersion = "1.0.5";
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
