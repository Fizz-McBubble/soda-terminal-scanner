using System.ComponentModel;
using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ZZZScannerHelper;

internal static class HelperPlatform
{
    private const long DiskSafetyMargin = 100L * 1024 * 1024;

    public static HelperEnvironmentSnapshot Inspect(ScannerManifest manifest)
    {
        var support = manifest.Support ?? new ScannerSupport
        {
            Os = "windows",
            Architectures = ["x64"],
            MinWindowsBuild = 17763
        };
        var snapshot = new HelperEnvironmentSnapshot
        {
            WindowsBuild = Environment.OSVersion.Version.Build,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        if (!OperatingSystem.IsWindows() || snapshot.WindowsBuild < support.MinWindowsBuild)
        {
            throw new HelperFailureException(
                "unsupported_os",
                "preflight",
                "当前 Windows 版本不受支持",
                $"扫描器需要 Windows 10 1809（Build {support.MinWindowsBuild}）或更高版本；当前 Build 为 {snapshot.WindowsBuild}。",
                "请升级 Windows 后重试。",
                retryable: false,
                new Dictionary<string, string> { ["windowsBuild"] = snapshot.WindowsBuild.ToString() });
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new HelperFailureException(
                "unsupported_arch",
                "preflight",
                "当前系统架构不受支持",
                $"本版本仅支持 Windows x64；当前系统为 {snapshot.OsArchitecture}，Helper 为 {snapshot.ProcessArchitecture}。",
                "请使用 x64 Windows 10/11。ARM64 和 x86 暂不在支持范围内。",
                retryable: false);
        }

        return snapshot;
    }

    public static PackageSelection SelectPackage(ScannerManifest manifest, HelperEnvironmentSnapshot environment)
    {
        if (manifest.SchemaVersion == 1)
            return new PackageSelection(manifest.LegacyPackage(), "legacy-manifest");

        var frameworkPackage = manifest.Packages.Single(package =>
            package.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase));
        var selfContainedPackage = manifest.Packages.Single(package =>
            package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase));
        environment.DesktopRuntimeAvailable = frameworkPackage.Framework is not null
            && HasRequiredFramework(frameworkPackage.Framework);
        return environment.DesktopRuntimeAvailable
            ? new PackageSelection(frameworkPackage, "desktop-runtime-available")
            : new PackageSelection(selfContainedPackage, "desktop-runtime-missing");
    }

    internal static ScannerPackage SelectPackageForTesting(ScannerManifest manifest, bool desktopRuntimeAvailable)
    {
        if (manifest.SchemaVersion == 1)
            return manifest.LegacyPackage();

        var mode = desktopRuntimeAvailable
            ? ScannerPackageModes.FrameworkDependent
            : ScannerPackageModes.SelfContained;
        return manifest.Packages.Single(package => package.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
    }

    public static void EnsureWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HelperFailureException(
                "cache_unwritable",
                "preflight",
                "无法写入扫描器目录",
                $"Helper 无法写入运行目录：{directory}",
                "请检查磁盘权限、安全软件拦截或受控文件夹访问设置。",
                retryable: true,
                new Dictionary<string, string> { ["directory"] = directory },
                ex);
        }
    }

    public static void EnsureDiskSpace(ScannerPackage package, string directory)
    {
        var required = checked(package.Size + package.ExpandedSize + DiskSafetyMargin);
        var root = Path.GetPathRoot(Path.GetFullPath(directory))
            ?? throw new InvalidOperationException("Cannot resolve scanner cache drive.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < required)
        {
            throw new HelperFailureException(
                "disk_insufficient",
                "preflight",
                "磁盘空间不足",
                $"准备扫描器至少需要 {FormatBytes(required)} 可用空间，当前只有 {FormatBytes(available)}。",
                "请释放系统盘空间后重试。Helper 不会留下未完成的安装。",
                retryable: true,
                new Dictionary<string, string>
                {
                    ["requiredBytes"] = required.ToString(),
                    ["availableBytes"] = available.ToString()
                });
        }
    }

    public static bool HasRequiredFramework(ScannerFramework framework)
    {
        if (!Version.TryParse(framework.MinVersion, out var minimum))
            return false;

        var roots = DotnetRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots)
        {
            var sharedFramework = Path.Combine(root, "shared", framework.Name);
            if (!Directory.Exists(sharedFramework))
                continue;

            foreach (var directory in Directory.EnumerateDirectories(sharedFramework))
            {
                if (Version.TryParse(Path.GetFileName(directory), out var installed)
                    && installed.Major == framework.Major
                    && installed >= minimum)
                    return true;
            }
        }

        return roots.Any(root => DotnetListRuntimesContains(root, framework, minimum));
    }

    private static IEnumerable<string> DotnetRoots()
    {
        foreach (var variable in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "dotnet");
        }

        string? registered = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = key?.GetValue("InstallLocation") as string;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(registered))
        {
            yield return registered;
        }
    }

    private static bool DotnetListRuntimesContains(string root, ScannerFramework framework, Version minimum)
    {
        var executable = Path.Combine(root, "dotnet.exe");
        if (!File.Exists(executable))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--list-runtimes",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(3000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            foreach (var line in process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && parts[0].Equals(framework.Name, StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(parts[1], out var installed)
                    && installed.Major == framework.Major
                    && installed >= minimum)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string FormatBytes(long value)
    {
        return value >= 1024L * 1024 * 1024
            ? $"{value / (1024d * 1024 * 1024):F1} GB"
            : $"{value / (1024d * 1024):F0} MB";
    }
}

internal sealed class HelperEnvironmentSnapshot
{
    public int WindowsBuild { get; set; }
    public string OsArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public bool DesktopRuntimeAvailable { get; set; }
}

internal sealed record PackageSelection(ScannerPackage Package, string Reason);

internal sealed class HelperFailureException : Exception
{
    public HelperFailureException(
        string code,
        string phase,
        string title,
        string message,
        string remedy,
        bool retryable,
        Dictionary<string, string>? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Phase = phase;
        Title = title;
        Remedy = remedy;
        Retryable = retryable;
        Details = details ?? [];
    }

    public string Code { get; }
    public string Phase { get; }
    public string Title { get; }
    public string Remedy { get; }
    public bool Retryable { get; }
    public Dictionary<string, string> Details { get; }
}

internal sealed class HelperErrorMessage
{
    public string Code { get; set; } = "unknown_error";
    public string Phase { get; set; } = "unknown";
    public string Title { get; set; } = "扫描器发生错误";
    public string Message { get; set; } = "";
    public string Remedy { get; set; } = "请重试；如果问题持续，请打开日志。";
    public bool Retryable { get; set; }
    public List<HelperErrorAction> Actions { get; set; } = [];
    public string DiagnosticId { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = [];
}

internal sealed class HelperErrorAction
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
}

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
            if (!IsOwnedOrphan(candidate, allowedRoots, currentHelperProcessId, IsProcessAlive))
            {
                skipped++;
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
                terminated++;
                HelperLog.Write($"ORPHAN_SCANNER_TERMINATED pid={candidate.ProcessId}");
            }
            catch (Exception ex)
            {
                skipped++;
                HelperLog.Write($"ORPHAN_SCANNER_CLEANUP_SKIPPED pid={candidate.ProcessId} reason={ex.GetType().Name}");
            }
        }

        return new ScannerChildCleanupResult(examined, terminated, skipped);
    }

    internal static bool IsOwnedOrphan(
        ScannerChildProcessSnapshot candidate,
        IReadOnlyCollection<string> allowedRoots,
        int currentHelperProcessId,
        Func<int, bool> parentAlive)
    {
        if (candidate.ProcessId <= 0
            || candidate.ParentProcessId <= 0
            || candidate.ParentProcessId == currentHelperProcessId
            || parentAlive(candidate.ParentProcessId)
            || !Path.GetFileName(candidate.ExecutablePath).Equals(ScannerExecutableName, StringComparison.OrdinalIgnoreCase)
            || !candidate.CommandLine.Contains("--ws-child", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var executable = Path.GetFullPath(candidate.ExecutablePath);
        return allowedRoots.Any(root => IsWithin(root, executable));
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
