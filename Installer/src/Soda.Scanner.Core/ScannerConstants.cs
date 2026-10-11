namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-14-local-20261011";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.14-local.20261011";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-14-local-20261011-win-x64.zip";
    public const long LockedAssetSize = 131761101L;
    public const string LockedAssetSha256 = "91c72d605303af04cad3ae07d13d6b31c6a85e46dd2b7989464e50325d7ee31c";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.1.5";
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
