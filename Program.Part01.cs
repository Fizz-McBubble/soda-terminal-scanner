using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.Ui;
using ZZZScannerNext.WebSocket;

static partial class Program
{
    private static string RunScanProcessAndWait(IReadOnlyList<string> scanArgs, TimeSpan timeout)
    {
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZZZ-Scanner.Next.exe");
        var scansRoot = Path.Combine(AppContext.BaseDirectory, "Scans");
        Directory.CreateDirectory(scansRoot);
        var existingDirectories = Directory.EnumerateDirectories(scansRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in scanArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start scan process.");
        var deadline = DateTime.UtcNow + timeout;
        string? scanDirectory = null;
        while (DateTime.UtcNow < deadline)
        {
            scanDirectory = Directory.EnumerateDirectories(scansRoot)
                .Where(directory => !existingDirectories.Contains(directory))
                .Select(directory => new DirectoryInfo(directory))
                .OrderByDescending(info => info.CreationTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(scanDirectory))
            {
                break;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"Scan process exited before creating a scan directory. ExitCode={process.ExitCode}.");
            }

            Thread.Sleep(500);
        }

        if (string.IsNullOrWhiteSpace(scanDirectory))
        {
            throw new TimeoutException("Timed out waiting for scan directory.");
        }

        var resultFile = Path.Combine(scanDirectory, "scan-once-result.json");
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultFile))
            {
                process.WaitForExit(5000);
                return scanDirectory;
            }

            Thread.Sleep(1000);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        throw new TimeoutException($"Timed out waiting for scan completion: {scanDirectory}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(targetDirectory, relativePath), overwrite: true);
        }
    }

    private static int RunScanOnce(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var scanMutexAcquired = false;
        using var scanMutex = new Mutex(false, @"Local\ZZZScannerNext.ScanOnce");
        try
        {
            scanMutexAcquired = scanMutex.WaitOne(0);
            if (!scanMutexAcquired)
            {
                Console.Error.WriteLine("Another scan is already running. Refusing to start a second scan against the same game window.");
                return 73;
            }

            var command = ParseScanRunCommand(args);
            var options = BuildScanOptions(command);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var progress = new Progress<ScanProgress>(progress =>
            {
                if (!string.IsNullOrWhiteSpace(progress.Message))
                {
                    Console.WriteLine($"progress={progress.Message}; visited={progress.Visited}; queued={progress.Queued}; completed={progress.Completed}; failed={progress.Failed}");
                }
            });

            var controller = new ScanController(ScanProfileFile.Load(), WikiData.Load());
            var result = controller.ScanAsync(options, progress, cts.Token).GetAwaiter().GetResult();
            var runResult = ScanRunResultFactory.FromSession(result);
            var resultFile = WriteScanRunResult(runResult);
            Console.WriteLine($"output_dir={result.OutputDirectory}");
            Console.WriteLine($"export_file={result.ExportFile}");
            Console.WriteLine($"items={result.Items.Count}");
            Console.WriteLine($"visited={result.Visited}");
            Console.WriteLine($"queued={result.Queued}");
            Console.WriteLine($"completed={result.Completed}");
            Console.WriteLine($"failed={result.Failed}");
            Console.WriteLine($"result_file={resultFile}");
            return runResult.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            var resultFile = WriteScanRunResult(new ScanRunResult
            {
                Success = false,
                Status = "canceled",
                OutputDirectory = FindLatestScanDirectory(),
                Error = "Scan canceled."
            });
            Console.Error.WriteLine("Scan canceled.");
            Console.Error.WriteLine($"result_file={resultFile}");
            return 130;
        }
        catch (Exception ex)
        {
            var resultFile = WriteScanRunResult(ScanRunResultFactory.FromFailure(ex, FindLatestScanDirectory()));
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine($"result_file={resultFile}");
            return 1;
        }
        finally
        {
            if (scanMutexAcquired)
            {
                scanMutex.ReleaseMutex();
            }
        }
    }

    private static int RunEdgeScrollProbe(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var scanMutexAcquired = false;
        using var scanMutex = new Mutex(false, @"Local\ZZZScannerNext.ScanOnce");
        try
        {
            scanMutexAcquired = scanMutex.WaitOne(0);
            if (!scanMutexAcquired)
            {
                Console.Error.WriteLine("Another scan is already running. Refusing to start the edge-scroll probe.");
                return 73;
            }

            var command = ParseScanRunCommand(args);
            var options = BuildScanOptions(command);
            options.RowAdvanceMode = RowAdvanceMode.NativeEdgeClick;
            var runs = TryReadIntOption(args, "--runs", out var configuredRuns)
                ? Math.Clamp(configuredRuns, 1, 100)
                : 30;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var controller = new ScanController(ScanProfileFile.Load(), WikiData.Load());
            var result = controller.ProbeNativeEdgeScrollAsync(options, runs, cts.Token).GetAwaiter().GetResult();
            var resultFile = Path.Combine(result.OutputDirectory, "edge-scroll-probe-result.json");
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result, JsonDefaults.Write));
            Console.WriteLine($"output_dir={result.OutputDirectory}");
            Console.WriteLine($"probe_runs={result.RequestedRuns}");
            Console.WriteLine($"changed_runs={result.ChangedRuns}");
            Console.WriteLine($"bottom_runs={result.BottomRuns}");
            Console.WriteLine($"failed_runs={result.FailedRuns}");
            Console.WriteLine($"result_file={resultFile}");
            return result.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Edge-scroll probe canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (scanMutexAcquired)
            {
                scanMutex.ReleaseMutex();
            }
        }
    }
}
