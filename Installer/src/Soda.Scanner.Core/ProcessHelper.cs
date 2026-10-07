using System.Diagnostics;

namespace Soda.Scanner.Core;

public static class ProcessHelper
{
    public static bool IsManagedHelperRunning(string root, bool testMode = false)
    {
        if (testMode) return false;
        var processes = MatchingProcesses(new[] { FileSystemSafety.SafePath(root, ScannerConstants.HelperExecutableRelative) }).ToList();
        var found = processes.Count > 0;
        foreach (var process in processes) process.Dispose();
        return found;
    }

    public static void TerminateManagedComponents(string root, bool testMode = false)
    {
        if (testMode) return;
        var paths = new List<string>();
        AddSafe(root, ScannerConstants.HelperExecutableRelative);
        var pointer = PointerStore.ReadValidated(FileSystemSafety.SafePath(root, "active.json"), root);
        if (pointer != null)
        {
            AddSafe(pointer.Path, "native\\ZZZ-Scanner.Next.Soda.exe");
            AddSafe(pointer.Path, "ocr\\Soda.ScannerPpOcrV6.exe");
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
            try { paths.Add(FileSystemSafety.SafePath(owner, relative)); }
            catch (InvalidOperationException ex) when (ex.Message == "managed_path_is_link") { }
        }
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
        var start = new ProcessStartInfo(helper)
        {
            WorkingDirectory = Path.GetDirectoryName(helper)!, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("soda-terminal-scanner://open?origin=" + Uri.EscapeDataString(origin));
        try
        {
            using var process = Process.Start(start);
            return process != null && !process.WaitForExit(2000);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
