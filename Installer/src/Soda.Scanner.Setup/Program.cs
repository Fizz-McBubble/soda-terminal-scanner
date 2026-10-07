using System.Reflection;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var silent = false;
        var testMode = false;
        var noLaunch = false;
        var uninstall = false;
        var verifyOnly = false;
        string? installRoot = null;
        string publicOrigin = ScannerConstants.DefaultPublicOrigin;
        string? overrideArchive = null;
        string? renderPreview = null;
        string faultInjection = "none";

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (arg.Equals("--test-mode", StringComparison.OrdinalIgnoreCase)) testMode = true;
            else if (arg.Equals("--no-launch", StringComparison.OrdinalIgnoreCase)) noLaunch = true;
            else if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            else if (arg.Equals("--verify-only", StringComparison.OrdinalIgnoreCase)) verifyOnly = true;
            else if (arg.Equals("--install-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                installRoot = args[++i];
            }
            else if (arg.Equals("--public-origin", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                publicOrigin = args[++i];
            }
            else if (arg.Equals("--offline-archive", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                overrideArchive = args[++i];
            }
            else if (arg == "--fault-injection" && i + 1 < args.Length) faultInjection = args[++i];
            else if (arg == "--render-preview" && i + 1 < args.Length) renderPreview = args[++i];
        }

        if (string.IsNullOrWhiteSpace(installRoot))
        {
            installRoot = ScannerConstants.GetDefaultManagedRoot();
        }
        installRoot = Path.GetFullPath(installRoot);
        try
        {
            installRoot = FileSystemSafety.ValidateRoot(installRoot, testMode);
            if (!testMode && overrideArchive != null) throw new InvalidOperationException("test_archive_override_forbidden");
            if (renderPreview != null)
            {
                if (!testMode || !Path.GetFullPath(renderPreview).EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("test_preview_required");
                FileSystemSafety.AssertSafePath(installRoot, renderPreview);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(renderPreview))!);
                using var preview = new InstallerForm(installRoot, publicOrigin, testMode, true, overrideArchive);
                preview.ShowInTaskbar = false;
                preview.StartPosition = FormStartPosition.Manual;
                preview.Location = new System.Drawing.Point(-32000, -32000);
                preview.Opacity = 0;
                preview.Show();
                preview.Update();
                using var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height);
                preview.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, preview.Size));
                bitmap.Save(renderPreview, System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (!silent && renderPreview == null) MessageBox.Show("安装目录不可用，请检查后重试。", "扫描助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        // Verification-only action
        if (verifyOnly)
        {
            try
            {
                Stream stream;
                if (!string.IsNullOrEmpty(overrideArchive) && File.Exists(overrideArchive))
                {
                    stream = File.OpenRead(overrideArchive);
                }
                else
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ScannerConstants.LockedAssetName, StringComparison.OrdinalIgnoreCase));
                    if (resName == null) throw new InvalidOperationException("Embedded locked asset not found.");
                    stream = asm.GetManifestResourceStream(resName) ?? throw new InvalidOperationException("Failed to open embedded stream.");
                }

                using (stream)
                {
                    var len = stream.Length;
                    var hash = Sha256Util.ComputeStreamSha256(stream);
                    Console.WriteLine($"Embedded Asset Size: {len} (Expected: {ScannerConstants.LockedAssetSize})");
                    Console.WriteLine($"Embedded Asset SHA256: {hash} (Expected: {ScannerConstants.LockedAssetSha256})");

                    if (len != ScannerConstants.LockedAssetSize || !string.Equals(hash, ScannerConstants.LockedAssetSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("Verification failed: Size or Hash mismatch.");
                        return 1;
                    }
                    Console.WriteLine("VERIFY_SUCCESS");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Verification failed: {ex.Message}");
                return 2;
            }
        }

        // Silent execution
        if (silent)
        {
            if (uninstall)
            {
                try
                {
                    var res = UninstallEngine.ExecuteUninstall(installRoot, testMode, msg => Console.WriteLine(msg), SetupResources.UninstallerHash());
                    Console.WriteLine(res.Message);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Uninstall error: {ex.Message}");
                    return 2;
                }
            }
            else
            {
                try
                {
                    var res = InstallEngine.ExecuteInstall(
                        installRoot,
                        publicOrigin,
                        offlineArchiveStreamProvider: () =>
                        {
                            if (!string.IsNullOrEmpty(overrideArchive) && File.Exists(overrideArchive))
                            {
                                return File.OpenRead(overrideArchive);
                            }
                            var asm = Assembly.GetExecutingAssembly();
                            var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ScannerConstants.LockedAssetName, StringComparison.OrdinalIgnoreCase));
                            if (resourceName == null) throw new InvalidOperationException("Embedded locked asset not found in setup executable.");
                            return asm.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Failed to open embedded asset stream.");
                        },
                        uninstallStubStreamProvider: () =>
                        {
                            var asm = Assembly.GetExecutingAssembly();
                            var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Soda-Scanner-Uninstall.exe", StringComparison.OrdinalIgnoreCase));
                            if (resourceName == null) throw new InvalidOperationException("Embedded uninstall stub not found in setup executable.");
                            return asm.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Failed to open embedded uninstall stub stream.");
                        },
                        testMode: testMode,
                        noLaunch: noLaunch,
                        progressCallback: (msg, percent) =>
                        {
                            Console.WriteLine($"[{percent}%] {msg}");
                        }, faultInjection: faultInjection
                    );
                    Console.WriteLine(res.Message);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Install error: {ex.Message}");
                    return 2;
                }
            }
        }

        // Interactive GUI execution
        var form = new InstallerForm(installRoot, publicOrigin, testMode, noLaunch, overrideArchive);
        Application.Run(form);
        return 0;
    }
}
