namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-11-local-20261011";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.11-local.20261011";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-11-local-20261011-win-x64.zip";
    public const long LockedAssetSize = 131737531L;
    public const string LockedAssetSha256 = "677505a03dfe2f696e897b241243c690285cb642a7e731b834b6f74428cb2352";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.1.1";
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
