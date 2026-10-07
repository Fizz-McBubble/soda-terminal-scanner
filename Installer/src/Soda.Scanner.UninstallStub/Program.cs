using System.Diagnostics;
using System.Runtime.InteropServices;
using Soda.Scanner.Core;

namespace Soda.Scanner.UninstallStub;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_ICONERROR = 0x00000010;
    private const int IDYES = 6;

    [STAThread]
    static int Main(string[] args)
    {
        var silent = false;
        var testMode = false;
        var relocated = false;
        var waitPid = 0;
        string? requestedRoot = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (arg.Equals("--test-mode", StringComparison.OrdinalIgnoreCase)) testMode = true;
            else if (arg.Equals("--relocated", StringComparison.OrdinalIgnoreCase)) relocated = true;
            else if (arg.Equals("--install-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                requestedRoot = args[++i];
            }
            else if (arg.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out waitPid);
            }
        }

        // Fixed managed root by default; only test mode in outputs may override root
        var installRoot = (testMode && !string.IsNullOrWhiteSpace(requestedRoot))
            ? requestedRoot
            : ScannerConstants.GetDefaultManagedRoot();

        string root;
        try
        {
            if (!testMode && requestedRoot != null) throw new InvalidOperationException("test_root_override_forbidden");
            root = FileSystemSafety.ValidateRoot(installRoot, testMode);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox(IntPtr.Zero, "安装目录不可用，请检查后重试。", "扫描助手", MB_OK | MB_ICONERROR);
            }
            Console.Error.WriteLine($"Root validation failed: {ex.Message}");
            return 2;
        }

        // GUI confirmation must happen BEFORE creating any temporary process or files
        if (!silent && !relocated)
        {
            var confirmResult = MessageBox(
                IntPtr.Zero,
                "卸载扫描助手？\n扫描结果会保留。",
                "卸载扫描助手",
                MB_YESNO | MB_ICONQUESTION);

            if (confirmResult != IDYES)
            {
                return 1;
            }
        }

        var currentExe = Environment.ProcessPath ?? "";
        var expectedExeInRoot = Path.Combine(root, ScannerConstants.UninstallExecutableName);

        // If not relocated and running exactly as root/uninstall.exe, relocate to unique temp directory
        if (!relocated && !string.IsNullOrEmpty(currentExe) &&
            string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(expectedExeInRoot), StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"SodaScannerUninstall_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var tempExe = Path.Combine(tempDir, ScannerConstants.UninstallExecutableName);
            File.Copy(currentExe, tempExe, true);

            var psi = new ProcessStartInfo
            {
                FileName = tempExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            psi.ArgumentList.Add("--relocated");
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (silent) psi.ArgumentList.Add("--silent");
            if (testMode)
            {
                psi.ArgumentList.Add("--test-mode");
                psi.ArgumentList.Add("--install-root");
                psi.ArgumentList.Add(root);
            }

            try
            {
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox(IntPtr.Zero, "卸载未完成，请关闭扫描助手后重试。", "扫描助手", MB_OK | MB_ICONERROR);
                }
                Console.Error.WriteLine($"Failed to start relocated uninstaller: {ex.Message}");
                return 2;
            }
        }

        // Relocated process waits for parent to exit to release file lock on root/uninstall.exe
        if (waitPid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(waitPid);
                proc.WaitForExit(10000);
            }
            catch { }
        }

        string? selfHash = null;
        if (File.Exists(currentExe))
        {
            try { selfHash = Sha256Util.ComputeFileSha256(currentExe); } catch { }
        }

        try
        {
            var result = UninstallEngine.ExecuteUninstall(
                root,
                testMode,
                statusCallback: null,
                expectedUninstallerHash: selfHash);

            if (!silent)
            {
                MessageBox(IntPtr.Zero, result.Message, "卸载完成", MB_OK | MB_ICONINFORMATION);
            }
            Console.WriteLine(result.Message);
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox(IntPtr.Zero, "卸载未完成，请关闭扫描助手后重试。", "扫描助手", MB_OK | MB_ICONERROR);
            }
            Console.Error.WriteLine($"Uninstall error: {ex.Message}");
            return 2;
        }
    }
}
