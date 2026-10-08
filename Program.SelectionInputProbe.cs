using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;

static partial class Program
{
    private static int RunSelectionInputProbe(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(false, @"Local\ZZZScannerNext.ScanOnce");
        var acquired = mutex.WaitOne(0);
        if (!acquired) return 73;
        try
        {
            var options = BuildScanOptions(ParseScanRunCommand(args));
            var row = TryReadIntOption(args, "--visual-row", out var suppliedRow) ? suppliedRow : 3;
            var col = TryReadIntOption(args, "--column", out var suppliedColumn) ? suppliedColumn : 9;
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var controller = new ScanController(ScanProfileFile.Load(), WikiData.Load());
            var result = controller.ProbeSelectionInputAsync(options, row, col, cancellation.Token).GetAwaiter().GetResult();
            Console.WriteLine($"result_file={result}");
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { mutex.ReleaseMutex(); }
    }
}
