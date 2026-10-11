namespace Soda.Scanner.Core;

public static class ScannerConstants
{
    public const string LockedRuntimeVersion = "soda-scanner-zzz-next-ppocrv6-18-rc8-10-local-20261011";
    public const string LockedReleaseTag = "scanner-runtime-v18.0.0-rc.8.10-local.20261011";
    public const string LockedAssetName = "soda-scanner-runtime-18-rc8-10-local-20261011-win-x64.zip";
    public const long LockedAssetSize = 131735730L;
    public const string LockedAssetSha256 = "8da6370490286a778f49977c4a3162a3b2059fb10056afb687e6627eec48519d";

    public const string DisplayName = "Soda Terminal · 画面扫描";
    public const string DisplayVersion = "1.0.10";
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
