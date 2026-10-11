namespace ZZZScannerHelper;

internal static partial class Program
{
    // Only runtime validation occurs here. Normalize every descriptor/file/hash
    // failure before it reaches the public snapshot; never forward local paths.
    internal static SodaRuntimeIdentity LoadDirectForkRuntime(string? root = null)
    {
        try { return SodaRuntimeLocator.Load(root); }
        catch (Exception)
        {
            throw new DirectForkHttpHelperServer.ScannerRuntimeFailureException(
                "扫描组件文件缺失或校验失败，请修复安装。", "runtime_repair_required");
        }
    }
}
