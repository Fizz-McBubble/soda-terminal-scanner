namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-16-local-20261016";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.16-local.20261016";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-16-local-20261016-win-x64.zip";
    public const long LockedAssetSize = 131763326L;
    public const string LockedAssetSha256 = "7bc5d50172d66760ff1d94b1ebd8982101122b756fb2932040d583a0139798e1";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.1.9";
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
