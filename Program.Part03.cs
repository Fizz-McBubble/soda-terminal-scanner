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
    private static int RunWebSocketHost(
        int port,
        string? connectionToken,
        bool openBrowser,
        PpOcrV6RuntimeIdentity? ppocrv6Runtime = null)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        var profiles = ScanProfileFile.Load();
        var wikiData = WikiData.Load();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var host = new WebSocketHost(profiles, wikiData, port, connectionToken, ppocrv6Runtime);
        if (openBrowser)
        {
            host.BrowserUrl = "http://localhost:8787/drive-discs.html";
        }

        try
        {
            host.RunAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static string? ReadOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        return null;
    }

    private static void WriteChildEarlyDiagnostic(int port, string code, string phase, int exitCode, string type)
    {
        try
        {
            var outputRoot = Environment.GetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT");
            if (string.IsNullOrWhiteSpace(outputRoot)) return;
            Directory.CreateDirectory(outputRoot);
            var path = Path.Combine(outputRoot, $"child-{port}.early.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { code, phase, exitCode, type }));
        }
        catch
        {
            // Early diagnostics must never alter child exit behavior.
        }
    }

    private static bool TryReadIntOption(string[] args, string optionName, out int value)
    {
        value = 0;
        var text = ReadOption(args, optionName);
        return int.TryParse(text, out value);
    }

    private static bool TryParsePanelStabilityMode(string value, out PanelStabilityMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelStabilityMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelStabilityMode.Panel;
        return false;
    }

    private static bool TryParseScrollAcceptMode(string value, out ScrollAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<ScrollAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = ScrollAcceptMode.Safe;
        return false;
    }

    private static bool TryParseRowAdvanceMode(string value, out RowAdvanceMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(normalized, "wheel", StringComparison.OrdinalIgnoreCase))
        {
            mode = RowAdvanceMode.WheelVerified;
            return true;
        }

        if (Enum.TryParse<RowAdvanceMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = RowAdvanceMode.WheelVerified;
        return false;
    }

    private static bool TryParsePanelAcceptMode(string value, out PanelAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelAcceptMode.Safe;
        return false;
    }

    private static bool TryParsePostScrollPanelAcceptMode(string value, out PostScrollPanelAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PostScrollPanelAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PostScrollPanelAcceptMode.Safe;
        return false;
    }

    private static bool TryParsePanelFloorMode(string value, out PanelFloorMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelFloorMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelFloorMode.Static;
        return false;
    }

    private static bool TryParseOverlapConflictMode(string value, out OverlapConflictMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<OverlapConflictMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = OverlapConflictMode.Recheck;
        return false;
    }

    private static bool TryParseVisualProfileClientKind(string value, out VisualProfileClientKind mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<VisualProfileClientKind>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = VisualProfileClientKind.Auto;
        return false;
    }

    private static bool TryParseProfileRoutingMode(string value, out ProfileRoutingMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<ProfileRoutingMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = ProfileRoutingMode.Strict;
        return false;
    }

    private static string WriteScanRunResult(ScanRunResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputDirectory))
        {
            return "";
        }

        var resultFile = Path.Combine(result.OutputDirectory, "scan-once-result.json");
        try
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result, JsonDefaults.Write));
            return resultFile;
        }
        catch
        {
            // The console output still reports the scan outcome if the sidecar cannot be written.
            return "";
        }
    }

    private static string FindLatestScanDirectory()
    {
        var scansDirectory = Path.Combine(AppContext.BaseDirectory, "Scans");
        if (!Directory.Exists(scansDirectory))
        {
            return "";
        }

        return Directory.EnumerateDirectories(scansDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault() ?? "";
    }

    private static int RunFastOcrMergeIndexes(string outputFile, IEnumerable<string> inputFiles)
    {
        var inputs = inputFiles
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(Path.GetFullPath)
            .ToArray();
        if (inputs.Length < 2)
        {
            Console.Error.WriteLine("At least two input indexes are required.");
            return 2;
        }

        var merged = new FastOcrTemplateIndex
        {
            Version = FastOcrTemplateIndex.CurrentVersion,
            Feature = FastOcrTemplateIndex.CanonicalFeature,
            CreatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
        };
        var templateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Input index not found: {input}");
                return 2;
            }

            var index = FastOcrTemplateIndex.Load(input);
            if (!string.Equals(index.Feature, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Only v6 canonical indexes can be merged. {input} uses feature={index.Feature}");
                return 2;
            }

            foreach (var template in index.Templates)
            {
                var key = string.Join(
                    '\u001f',
                    template.FieldKey,
                    template.Label,
                    template.VisualProfileId,
                    template.ProfileFamilyId,
                    string.Join("|", template.Bits));
                if (templateKeys.Add(key))
                {
                    merged.Templates.Add(new FastOcrTemplate
                    {
                        FieldKey = template.FieldKey,
                        Label = template.Label,
                        VisualProfileId = template.VisualProfileId,
                        ProfileFamilyId = template.ProfileFamilyId,
                        Bits = template.Bits.ToArray(),
                        SourceImage = template.SourceImage
                    });
                }
            }

            MergePolicies(merged.FieldPolicies, index.FieldPolicies);
            MergePolicies(merged.ProfileFieldPolicies, index.ProfileFieldPolicies);
            MergePolicies(merged.FamilyFieldPolicies, index.FamilyFieldPolicies);
        }

        merged.Save(outputFile);
        Console.WriteLine($"fast_merge.output={Path.GetFullPath(outputFile)}");
        Console.WriteLine($"fast_merge.inputs={inputs.Length}");
        Console.WriteLine($"fast_merge.templates={merged.Templates.Count}");
        Console.WriteLine($"fast_merge.field_policies={merged.FieldPolicies.Count}");
        Console.WriteLine($"fast_merge.profile_policies={merged.ProfileFieldPolicies.Count}");
        Console.WriteLine($"fast_merge.family_policies={merged.FamilyFieldPolicies.Count}");
        return 0;
    }

    private static void MergePolicies(
        IDictionary<string, FastOcrFieldPolicy> target,
        IReadOnlyDictionary<string, FastOcrFieldPolicy> source)
    {
        foreach (var (key, policy) in source)
        {
            if (target.TryGetValue(key, out var existing))
            {
                existing.AssistEnabled = existing.AssistEnabled && policy.AssistEnabled;
                existing.MinScore = Math.Max(existing.MinScore, policy.MinScore);
                existing.MinMargin = Math.Max(existing.MinMargin, policy.MinMargin);
                existing.TemplateCount += policy.TemplateCount;
                existing.LabelCount = Math.Max(existing.LabelCount, policy.LabelCount);
                target[key] = existing;
            }
            else
            {
                target[key] = new FastOcrFieldPolicy
                {
                    AssistEnabled = policy.AssistEnabled,
                    MinScore = policy.MinScore,
                    MinMargin = policy.MinMargin,
                    TemplateCount = policy.TemplateCount,
                    LabelCount = policy.LabelCount
                };
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record CaptureSuiteCase(string Name, string CaptureMode, string[] ExtraArgs);
}
