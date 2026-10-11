using System.Diagnostics;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private sealed partial class DirectForkHttpHelperServer
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SodaRuntimeFileLease> _runtimeLeases = new();
        private static Exception? RequestForkTermination(Process process)
        {
            var failure = DirectForkTermination.TryRequest(() =>
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            });
            if (failure is not null) HelperLog.Write($"DIRECT_FORK_STOP_REQUEST_FAILED type={failure.GetType().Name}");
            return failure;
        }

        private async Task ReleaseForkAfterExitAsync(Process process, long job)
        {
            try
            {
                await _jobs.CompleteAfterExitAsync(job, () => process.WaitForExitAsync());
                if (_runtimeLeases.TryRemove(job, out var lease)) lease.Dispose();
                process.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No confirmed exit: keep ownership and never authorize another
                // child merely because waiting failed. Retain the handle too.
                HelperLog.Write($"DIRECT_FORK_EXIT_UNCONFIRMED type={ex.GetType().Name}");
            }
        }
    }
}
