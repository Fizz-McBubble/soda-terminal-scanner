using System.Diagnostics;
using System.Runtime.InteropServices;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

// NSIS extracts the UI first and keeps its private directory until this child exits.
// Paths come only from our own apphost; no production payload-path option exists.
internal static class PayloadFiles
{
    internal const string ReadyContents = "SODA-OFFLINE-PAYLOAD-1";
    internal static void WaitUntilReady() => _ = WaitForParentPayload();

    internal static Stream Open(bool uninstaller = false)
        => OpenChecked(WaitForParentPayload(), uninstaller ? "Soda-Scanner-Uninstall.exe" : ScannerConstants.LockedAssetName,
            uninstaller ? SetupResources.UninstallerSize : ScannerConstants.LockedAssetSize);

    internal static Stream OpenChecked(string directory, string name, long expectedSize)
    {
        var path = Path.Combine(directory, name);
        FileSystemSafety.AssertNoReparsePoints(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException ex) { throw new InvalidOperationException("setup_asset_missing", ex); }
        catch (DirectoryNotFoundException ex) { throw new InvalidOperationException("setup_asset_missing", ex); }
        if (stream.Length == expectedSize) return stream;
        stream.Dispose();
        throw new InvalidOperationException("setup_asset_size_mismatch");
    }

    private static string WaitForParentPayload()
    {
        var info = new ProcessBasicInformation();
        using var current = Process.GetCurrentProcess();
        if (NtQueryInformationProcess(current.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            throw new InvalidOperationException("setup_asset_parent_unavailable");
        using var parent = Process.GetProcessById(checked((int)info.ParentId));
        if (parent.StartTime > current.StartTime || parent.HasExited)
            throw new InvalidOperationException("setup_asset_parent_unavailable");
        var parentPath = parent.MainModule?.FileName ?? throw new InvalidOperationException("setup_asset_parent_unavailable");
        if (FileVersionInfo.GetVersionInfo(parentPath).FileDescription != "扫描助手安装程序")
            throw new InvalidOperationException("setup_asset_parent_invalid");
        var ownPath = Environment.ProcessPath ?? throw new InvalidOperationException("setup_asset_apphost_unavailable");
        if (!Path.GetFileName(ownPath).Equals("Soda-Scanner-Setup-inner.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("setup_asset_apphost_invalid");
        var directory = Path.Combine(Path.GetDirectoryName(ownPath)!, "payload");
        Wait(directory, () => !parent.HasExited, TimeSpan.FromMinutes(2));
        return directory;
    }

    // Internal fixture seam. Not reachable through the command line.
    internal static void Wait(string directory, Func<bool> parentAlive, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (!parentAlive()) throw new InvalidOperationException("setup_asset_parent_exited");
            FileSystemSafety.AssertNoReparsePoints(directory);
            if (File.Exists(Path.Combine(directory, "failed"))) throw new InvalidOperationException("setup_asset_extract_failed");
            var ready = Path.Combine(directory, "ready");
            if (File.Exists(ready))
            {
                FileSystemSafety.AssertNoReparsePoints(ready);
                using var stream = new FileStream(ready, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != ReadyContents.Length) throw new InvalidOperationException("setup_asset_ready_invalid");
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
                if (reader.ReadToEnd() != ReadyContents) throw new InvalidOperationException("setup_asset_ready_invalid");
                return;
            }
            if (watch.Elapsed >= timeout) throw new InvalidOperationException("setup_asset_prepare_timeout");
            Thread.Sleep(50);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId;
    }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        ref ProcessBasicInformation information, int length, out int returnedLength);
}
