using System.Diagnostics;

namespace Soda.Scanner.Core;

public static class ProcessHelper
{
    public static bool IsManagedHelperRunning(string root, bool testMode = false)
    {
        if (testMode) return false;
        var helper = FileSystemSafety.SafePath(root, ScannerConstants.HelperExecutableRelative);
        if (!IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative)) return false;
        var processes = MatchingProcesses(new[] { helper }).ToList();
        var found = processes.Count > 0;
        foreach (var process in processes) process.Dispose();
        return found;
    }

    public static void TerminateManagedComponents(string root, bool testMode = false)
    {
        if (testMode) return;
        var paths = new List<string>();
        AddSafe(root, ScannerConstants.HelperExecutableRelative);
        AddSafe(root, "helper-bootstrap\\ZZZ-Scanner-Helper.exe");
        foreach (var pkg in LegacyRuntimeCatalog.AllPackages)
        {
            var vRoot = Path.Combine(root, "versions", pkg.Version);
            AddSafe(vRoot, "native\\ZZZ-Scanner.Next.Soda.exe");
            AddSafe(vRoot, "ocr\\Soda.ScannerPpOcrV6.exe");
            AddSafe(vRoot, "helper\\ZZZ-Scanner-Helper.exe");
        }
        var pointer = PointerStore.ReadValidated(FileSystemSafety.SafePath(root, "active.json"), root);
        if (pointer != null)
        {
            AddSafe(pointer.Path, "native\\ZZZ-Scanner.Next.Soda.exe");
            AddSafe(pointer.Path, "ocr\\Soda.ScannerPpOcrV6.exe");
            AddSafe(pointer.Path, "helper\\ZZZ-Scanner-Helper.exe");
        }
        foreach (var process in MatchingProcesses(paths))
        {
            using (process)
            {
                try { process.Kill(); if (!process.WaitForExit(10000)) throw new InvalidOperationException("scanner_still_running"); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
        void AddSafe(string owner, string relative)
        {
            try
            {
                var path = FileSystemSafety.SafePath(owner, relative);
                if (IsKnownProgram(path, relative)) paths.Add(path);
            }
            catch (InvalidOperationException ex) when (ex.Message == "managed_path_is_link") { }
        }
    }

    public static bool IsKnownProgram(string path, string relative)
    {
        var catalogRelative = NormalizeCatalogRelative(relative);
        return LegacyRuntimeCatalog.AllDistinctFiles
            .Any(entry => entry.Path.Equals(catalogRelative, StringComparison.OrdinalIgnoreCase) && RuntimeCatalog.Matches(path, entry));
    }

    private static string NormalizeCatalogRelative(string relative)
    {
        if (relative.StartsWith("helper-bootstrap\\", StringComparison.OrdinalIgnoreCase))
            return "helper\\" + relative["helper-bootstrap\\".Length..];
        if (relative.StartsWith("helper-bootstrap/", StringComparison.OrdinalIgnoreCase))
            return "helper\\" + relative["helper-bootstrap/".Length..];
        return relative.Replace('/', '\\');
    }

    private static IEnumerable<Process> MatchingProcesses(IEnumerable<string> paths)
    {
        var expected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in expected.Select(Path.GetFileNameWithoutExtension).Distinct())
        foreach (var process in Process.GetProcessesByName(name!))
        {
            string? path;
            try { path = process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { process.Dispose(); continue; }
            catch (InvalidOperationException) { process.Dispose(); continue; }
            if (path != null && expected.Contains(Path.GetFullPath(path))) yield return process;
            else process.Dispose();
        }
    }

    public static bool StartManagedHelper(string helper, string origin = ScannerConstants.DefaultPublicOrigin, bool testMode = false)
    {
        if (testMode) return true;
        FileSystemSafety.AssertNoReparsePoints(helper);
        if (!IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative)) return false;
        var running = MatchingProcesses(new[] { helper }).ToList();
        var alreadyRunning = running.Count > 0;
        foreach (var process in running) process.Dispose();
        if (alreadyRunning) return true;
        var start = HelperStartInfo(helper, origin);
        try
        {
            using var process = Process.Start(start);
            return process != null && !process.WaitForExit(2000);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public static ProcessStartInfo HelperStartInfo(string helper, string origin)
    {
        var start = new ProcessStartInfo(helper)
        {
            WorkingDirectory = Path.GetDirectoryName(helper)!, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("soda-terminal-scanner://open?origin=" + Uri.EscapeDataString(origin));
        return start;
    }
    public static ProcessStartInfo WorkspaceStartInfo(string origin)
    {
        if (!ScannerConstants.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            throw new InvalidOperationException("scanner_public_origin_invalid");
        return new ProcessStartInfo(origin + "/system/scanner") { UseShellExecute = true };
    }
    public static bool OpenWorkspace(string origin, bool testMode = false)
    {
        var start = WorkspaceStartInfo(origin);
        if (testMode) return true;
        try { using var browser = Process.Start(start); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
