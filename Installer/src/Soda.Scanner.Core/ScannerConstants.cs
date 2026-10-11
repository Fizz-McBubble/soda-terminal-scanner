namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-9-local-20261011";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.9-local.20261011";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-9-local-20261011-win-x64.zip";
    public const long LockedAssetSize = 131734513L;
    public const string LockedAssetSha256 = "23402875cd433adf95ea98296a5deb36c3af652e0452dd3025e2bdc3027a3642";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.0.9";
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
