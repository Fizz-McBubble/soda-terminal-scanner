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
{    [STAThread]
    static int Main(string[] args)
    {
        var outputRoot = ReadOption(args, "--output-root");
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT", Path.GetFullPath(outputRoot));
        }

        if (TryRunCommandLine(args, out var exitCode))
        {
            return exitCode;
        }

        NativeMethods.TryEnablePerMonitorDpiAwareness();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static bool TryRunCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        if (string.Equals(args[0], "--scan-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]");
                exitCode = 2;
                return true;
            }

            exitCode = ScanBenchmark.Run(args[1], args.Length > 2 ? args[2] : null);
            return true;
        }

        if (string.Equals(args[0], "--scan-stability-suite", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --scan-stability-suite <scan-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = ScanBenchmark.RunStabilitySuite(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--capture-stability-suite", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --capture-stability-suite gdi|dxgi|both [--max-items 120] [--rounds 5] [--suite-profile speed-1.0.27]");
                exitCode = 2;
                return true;
            }

            exitCode = RunCaptureStabilitySuite(args);
            return true;
        }

        if (string.Equals(args[0], "--ocr-shadow-analyze", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-shadow-analyze <scan-dir-or-parent> [--build-fast-index <file>]");
                exitCode = 2;
                return true;
            }

            exitCode = OcrShadowDatasetAnalyzer.Run(args[1], ReadOption(args, "--build-fast-index"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-eval", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-eval <index.json> <shadow-dir-or-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunEval(args[1], args[2]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-runtime-smoke", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = false,
                    command = "ocr-runtime-smoke",
                    error = "Usage: ZZZ-Scanner.Next.exe --ocr-runtime-smoke <fixture>"
                }));
                exitCode = 2;
                return true;
            }

            exitCode = OcrRuntimeSmoke.Run(args[1], Console.Out);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-calibrate", StringComparison.OrdinalIgnoreCase))
        {
            var outputFile = ReadOption(args, "--output");
            if (args.Length < 2 || string.IsNullOrWhiteSpace(outputFile))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCalibrate(args[1], outputFile, ReadOption(args, "--feature"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-calibrate-visual-profiles", StringComparison.OrdinalIgnoreCase))
        {
            var outputFile = ReadOption(args, "--output");
            if (args.Length < 2 || string.IsNullOrWhiteSpace(outputFile))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCalibrateVisualProfiles(args[1], outputFile, ReadOption(args, "--feature"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-feature-eval", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-feature-eval <shadow-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunFeatureEval(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-cross-validate", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-cross-validate <shadow-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCrossValidate(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-merge-indexes", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]");
                exitCode = 2;
                return true;
            }

            exitCode = RunFastOcrMergeIndexes(args[1], args.Skip(2));
            return true;
        }

        if (string.Equals(args[0], "--scan-once", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunScanOnce(args);
            return true;
        }

        if (string.Equals(args[0], "--probe-runtime", StringComparison.OrdinalIgnoreCase))
        {
            var required = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Data", "scan_profiles.json"),
                Path.Combine(AppContext.BaseDirectory, "Data", "drive_discs.json"),
                Path.Combine(AppContext.BaseDirectory, "Data", "scanner-detail-geometry.v1.json"),
                Path.Combine(AppContext.BaseDirectory, "Data", "soda-drive-disc-data.v1.json")
            };
            var ok = required.All(File.Exists);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok,
                runtime = "ZZZ-Scanner.Next-1.0.49-soda-r25",
                upstreamCommit = "ff90891140016d3f1b738d73cb6cd9b291e17cec",
                accountWriteEnabled = false,
                importAccess = false
            }));
            exitCode = ok ? 0 : 2;
            return true;
        }

        if (string.Equals(args[0], "--probe-r4-runtime", StringComparison.OrdinalIgnoreCase))
        {
            var catalog = Path.Combine(AppContext.BaseDirectory, "Data", "soda-drive-disc-data.v1.json");
            var geometry = Path.Combine(AppContext.BaseDirectory, "Data", "scanner-detail-geometry.v1.json");
            var ok = File.Exists(catalog) && File.Exists(geometry);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok,
                ocr = "PP-OCRv6-small-ONNX",
                r4Schema = "soda-terminal-scan-staging@1",
                accountWriteEnabled = false,
                importAccess = false
            }));
            exitCode = ok ? 0 : 2;
            return true;
        }

        if (string.Equals(args[0], "--edge-scroll-probe", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunEdgeScrollProbe(args);
            return true;
        }

        if (string.Equals(args[0], "--selection-input-probe", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunSelectionInputProbe(args);
            return true;
        }

        if (string.Equals(args[0], "--ws", StringComparison.OrdinalIgnoreCase))
        {
            var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 22350;
            var openBrowser = !args.Any(arg => string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase));
            exitCode = RunWebSocketHost(port, connectionToken: null, openBrowser);
            return true;
        }

        if (string.Equals(args[0], "--ws-child", StringComparison.OrdinalIgnoreCase))
        {
            var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 0;
            var token = ReadOption(args, "--child-token");
            if (port <= 0 || string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ws-child <port> --child-token <token> [--no-browser]");
                exitCode = 2;
                return true;
            }

            var openBrowser = !args.Any(arg => string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase));
            PpOcrV6RuntimeIdentity.TryFromChildArguments(
                AppContext.BaseDirectory,
                ReadOption(args, "--ppocrv6-worker"),
                ReadOption(args, "--ppocrv6-model"),
                ReadOption(args, "--ppocrv6-config"),
                out var ppocrv6Runtime);
            try
            {
                exitCode = RunWebSocketHost(port, token, openBrowser, ppocrv6Runtime);
            }
            catch (Exception ex)
            {
                exitCode = 3;
                WriteChildEarlyDiagnostic(port, "child_websocket_start_failed", "launch", exitCode, ex.GetType().Name);
                Console.Error.WriteLine("child_websocket_start_failed");
            }
            return true;
        }

        return false;
    }

    private static int RunCaptureStabilitySuite(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var mode = args[1].Trim().ToLowerInvariant();
        var suiteProfile = ReadOption(args, "--suite-profile") ?? "";
        var cases = BuildCaptureSuiteCases(mode, suiteProfile);
        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"Unknown capture stability suite mode/profile: mode={args[1]}, suiteProfile={suiteProfile}. Expected gdi, dxgi, or both.");
            return 2;
        }

        var maxItems = TryReadIntOption(args, "--max-items", out var parsedMaxItems) ? Math.Max(1, parsedMaxItems) : 120;
        var rounds = TryReadIntOption(args, "--rounds", out var parsedRounds) ? Math.Clamp(parsedRounds, 1, 20) : 5;
        var suiteRoot = Path.Combine(AppContext.BaseDirectory, "StabilitySuites", $"capture-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(suiteRoot);

        Console.WriteLine($"capture_suite.root={suiteRoot}");
        Console.WriteLine($"capture_suite.mode={mode}");
        Console.WriteLine($"capture_suite.profile={(string.IsNullOrWhiteSpace(suiteProfile) ? "default" : suiteProfile)}");
        Console.WriteLine($"capture_suite.rounds={rounds}");
        Console.WriteLine($"capture_suite.max_items={maxItems}");

        foreach (var suiteCase in cases)
        {
            var caseRoot = Path.Combine(suiteRoot, suiteCase.Name);
            Directory.CreateDirectory(caseRoot);
            Console.WriteLine($"capture_suite.case={suiteCase.Name}");

            for (var round = 1; round <= rounds; round++)
            {
                var scanArgs = new List<string>
                {
                    "--scan-once",
                    "--fast-mode",
                    "--capture-mode",
                    suiteCase.CaptureMode,
                    "--max-items",
                    maxItems.ToString(CultureInfo.InvariantCulture)
                };
                scanArgs.AddRange(suiteCase.ExtraArgs);

                var scanDirectory = RunScanProcessAndWait(scanArgs, timeout: TimeSpan.FromMinutes(8));
                var targetDirectory = Path.Combine(caseRoot, Path.GetFileName(scanDirectory));
                CopyDirectory(scanDirectory, targetDirectory);
                Console.WriteLine($"capture_suite.{suiteCase.Name}.round_{round}_scan_dir={scanDirectory}");
            }

            ScanBenchmark.RunStabilitySuite(caseRoot);
        }

        return 0;
    }

    private static IReadOnlyList<CaptureSuiteCase> BuildCaptureSuiteCases(string mode, string suiteProfile)
    {
        if (string.Equals(suiteProfile, "speed-1.0.27", StringComparison.OrdinalIgnoreCase))
        {
            var speedCases = new[]
            {
                new CaptureSuiteCase("dxgi-default", "dxgi", []),
                new CaptureSuiteCase("dxgi-floor110-postscroll", "dxgi", ["--panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll"]),
                new CaptureSuiteCase("dxgi-scene105-post110-scroll60", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "105", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "60"]),
                new CaptureSuiteCase("dxgi-scene100-post110-scroll60", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "100", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "60"]),
                new CaptureSuiteCase("dxgi-scene105-post110-scroll50", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "105", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "50"])
            };

            return mode is "dxgi" or "both" ? speedCases : [];
        }

        var dxgiCases = new[]
        {
            new CaptureSuiteCase("dxgi-default", "dxgi", []),
            new CaptureSuiteCase("dxgi-floor110", "dxgi", ["--panel-min-accept-floor", "110"]),
            new CaptureSuiteCase("dxgi-floor110-postscroll", "dxgi", ["--panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll"])
        };
        var gdiCases = new[]
        {
            new CaptureSuiteCase("gdi-default", "gdi", []),
            new CaptureSuiteCase("gdi-postscroll", "gdi", ["--post-scroll-panel-accept-mode", "adaptive-after-scroll"])
        };

        return mode switch
        {
            "dxgi" => dxgiCases,
            "gdi" => gdiCases,
            "both" => dxgiCases.Concat(gdiCases).ToArray(),
            _ => []
        };
    }
}
