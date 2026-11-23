namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-26-local-20261027";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.26-local.20261027";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-26-local-20261027-win-x64.zip";
    public const long LockedAssetSize = 131775890L;
    public const string LockedAssetSha256 = "ddbca8ede47d8928f7c398ae31c00b0e1c1d42cf75243c7c9e5f2d46bd096a87";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.3.1";
    public const string Publisher = "Soda Terminal";
    public const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SodaTerminalScanner";
    public const string ProtocolRegistryKeyPath = @"Software\Classes\soda-terminal-scanner";

    public const string DefaultPublicOrigin = "https://sodaterminal.com";
    public static readonly string[] AllowedOrigins = new[]
    {
        "https://sodaterminal.com",
        "http://127.0.0.1:5173"
    };

    public const string HelperExecutableRelative = @"helper\ZZZ-Scanner-Helper.exe";
    public const string UninstallExecutableName = "uninstall.exe";

    public static string GetDefaultManagedRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SodaTerminal", "Scanner");
    }
}
