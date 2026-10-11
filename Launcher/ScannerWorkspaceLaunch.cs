namespace ZZZScannerHelper;

internal static class ScannerWorkspaceLaunch
{
    internal static string? ReadOrigin(string[] args)
    {
        if (args.Length == 0 || args[0] != "--open-workspace") return null;
        if (args.Length > 2) throw new ArgumentException("workspace_arguments_invalid");
        var origin = args.Length == 1 ? ScannerOriginPolicy.DomainPublicOrigin : args[1];
        if (!ScannerOriginPolicy.IsTrustedHttpsOrigin(origin,
                ScannerOriginPolicy.DomainPublicOrigin + "\n" + ScannerOriginPolicy.WorkersPublicOrigin))
            throw new ArgumentException("workspace_origin_not_allowed");
        return origin;
    }
}
