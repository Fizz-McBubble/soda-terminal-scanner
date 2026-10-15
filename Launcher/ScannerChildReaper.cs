using System.Diagnostics;
using System.Management;
using System.Text.Json;

namespace ZZZScannerHelper;

internal sealed record ScannerChildProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string ExecutablePath,
    string CommandLine);

internal sealed record ScannerChildCleanupResult(int Examined, int Terminated, int Skipped);

internal static class ScannerChildReaper
{
    private const string ScannerExecutableName = "ZZZ-Scanner.Next.Soda.exe";

    public static ScannerChildCleanupResult CleanupOrphans(string installRoot, int currentHelperProcessId)
        => CleanupOrphans(installRoot, currentHelperProcessId, QueryCandidates, HasScannerProcess);

    internal static ScannerChildCleanupResult CleanupOrphans(
        string installRoot,
        int currentHelperProcessId,
        Func<IEnumerable<ScannerChildProcessSnapshot>> queryCandidates,
        Func<bool> scannerProcessPresent)
    {
        var allowedRoots = LoadAllowedRoots(installRoot);
        ScannerChildProcessSnapshot[] candidates;
        try
        {
            candidates = queryCandidates().ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Native AOT can omit constructors used internally by System.Management.
            // With no scanner process there is nothing to reap; if one exists, fail
            // closed instead of starting a second scanner without inspecting it.
            if (scannerProcessPresent())
                throw new HelperFailureException(
                    "legacy_scanner_inspection_unavailable",
                    "startup",
                    "旧扫描进程仍在运行",
                    "无法核对仍在运行的扫描进程。",
                    "请退出旧扫描进程后重试；无需重装或删除账户资料。",
                    retryable: true,
                    innerException: ex);
            HelperLog.Write($"ORPHAN_SCANNER_INSPECTION_SKIPPED reason={ex.GetType().Name}");
            return new ScannerChildCleanupResult(0, 0, 0);
        }
        var examined = 0;
        var terminated = 0;
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            examined++;
            if (!IsOwnedOrphan(candidate, allowedRoots, currentHelperProcessId, IsProcessAlive,
                    Path.Combine(installRoot, "outputs")))
            {
                skipped++;
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                process.Kill(entireProcessTree: true);
                if (process.WaitForExit(5_000))
                {
                    terminated++;
                    HelperLog.Write($"ORPHAN_SCANNER_TERMINATED pid={candidate.ProcessId}");
                }
                else
                {
                    skipped++;
                    HelperLog.Write($"ORPHAN_SCANNER_EXIT_UNCONFIRMED pid={candidate.ProcessId}");
                }
            }
            catch (Exception ex)
            {
                skipped++;
                HelperLog.Write($"ORPHAN_SCANNER_CLEANUP_SKIPPED pid={candidate.ProcessId} reason={ex.GetType().Name}");
            }
        }

        // Elevated children may deny WMI path inspection or Kill from the
        // unelevated Helper. Missing identity/confirmed exit never licenses a
        // second scanner: ask the player to close the remaining window.
        if (scannerProcessPresent())
            throw new HelperFailureException(
                "legacy_scanner_inspection_unavailable", "startup",
                "旧扫描进程仍在运行", "无法确认旧扫描进程已经结束。",
                "请先退出旧扫描进程后重试；无需重装或删除账户资料。", retryable: true);
        return new ScannerChildCleanupResult(examined, terminated, skipped);
    }

    internal static bool IsOwnedOrphan(
        ScannerChildProcessSnapshot candidate,
        IReadOnlyCollection<string> allowedRoots,
        int currentHelperProcessId,
        Func<int, bool> parentAlive,
        string? ownedOutputRoot = null)
    {
        if (candidate.ProcessId <= 0
            || candidate.ParentProcessId <= 0
            || candidate.ParentProcessId == currentHelperProcessId
            || parentAlive(candidate.ParentProcessId)
            || !Path.GetFileName(candidate.ExecutablePath).Equals(ScannerExecutableName, StringComparison.OrdinalIgnoreCase)
            || !IsOwnedCommandLine(candidate.CommandLine, ownedOutputRoot))
        {
            return false;
        }

        var executable = Path.GetFullPath(candidate.ExecutablePath);
        return allowedRoots.Any(root => IsWithin(root, executable));
    }

    private static bool IsOwnedCommandLine(string commandLine, string? ownedOutputRoot)
    {
        // Match argument tokens, never a substring embedded in a path or option.
        if (commandLine.Count(character => character == '"') % 2 != 0) return false;
        var arguments = System.Text.RegularExpressions.Regex.Matches(commandLine, "\"([^\"]*)\"|([^\\s\"]+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToArray();
        if (arguments.Length < 2) return false;
        // Old managed children can still be left behind by a retired Helper.
        if (arguments[1].Equals("--ws-child", StringComparison.OrdinalIgnoreCase)) return true;
        if (!arguments[1].Equals("--scan-once", StringComparison.OrdinalIgnoreCase)
            || ownedOutputRoot is null) return false;
        var positions = Enumerable.Range(2, arguments.Length - 2)
            .Where(index => arguments[index].Equals("--output-root", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (positions.Length != 1 || positions[0] + 1 >= arguments.Length) return false;
        try
        {
            var path = arguments[positions[0] + 1];
            return Path.IsPathFullyQualified(path) && IsWithin(ownedOutputRoot, path)
                && Path.GetFileName(path).StartsWith("live-", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static IReadOnlyList<string> LoadAllowedRoots(string installRoot)
    {
        var root = Path.GetFullPath(installRoot);
        var versionsRoot = Path.Combine(root, "versions");
        var result = new List<string>();
        foreach (var pointerName in new[] { "active.json", "previous.json" })
        {
            var pointerPath = Path.Combine(root, pointerName);
            if (!File.Exists(pointerPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(pointerPath));
                var versionRoot = Path.GetFullPath(document.RootElement.GetProperty("path").GetString() ?? "");
                if (IsWithin(versionsRoot, versionRoot)) result.Add(versionRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                HelperLog.Write($"ORPHAN_SCANNER_POINTER_SKIPPED pointer={pointerName} reason={ex.GetType().Name}");
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<ScannerChildProcessSnapshot> QueryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='ZZZ-Scanner.Next.Soda.exe'");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var executablePath = item["ExecutablePath"] as string;
            var commandLine = item["CommandLine"] as string;
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(commandLine)) continue;
            yield return new ScannerChildProcessSnapshot(
                Convert.ToInt32(item["ProcessId"]),
                Convert.ToInt32(item["ParentProcessId"]),
                executablePath,
                commandLine);
        }
    }

    private static bool HasScannerProcess()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ScannerExecutableName));
        try
        {
            return processes.Any(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
