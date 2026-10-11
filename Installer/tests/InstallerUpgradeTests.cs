using Microsoft.Win32;
using Soda.Scanner.Core;
using Soda.Scanner.Setup;
using System.Drawing;
using System.Windows.Forms;

internal static class InstallerUpgradeTests
{
    public static void Run(string basePath, string archive, string stub, Action<string, Action> test)
    {
        test("shortcut creation repair rollback and exact owned removal", () =>
        {
            var root = Path.Combine(basePath, "shortcut-contract"); Directory.CreateDirectory(root);
            var origin = ScannerConstants.DefaultPublicOrigin; var path = ScannerShortcut.PathFor(root, true);
            Check(RegistryHelper.RegisterStartMenuShortcut(root, origin, true));
            Check(ScannerShortcut.IsOwned(root, origin, true));
            Check(!RegistryHelper.RegisterStartMenuShortcut(root, origin, true));
            RegistryHelper.RollbackStartMenuShortcut(root, origin, true, false); Check(File.Exists(path));
            RegistryHelper.RollbackStartMenuShortcut(root, origin, true, true); Check(!File.Exists(path));
            RegistryHelper.RegisterStartMenuShortcut(root, origin, true);
            File.WriteAllText(path, "USER_EDIT");
            var edited = Sha256Util.ComputeFileSha256(path);
            RegistryHelper.RollbackStartMenuShortcut(root, origin, true, true); Check(Sha256Util.ComputeFileSha256(path) == edited);
            RegistryHelper.RemoveStartMenuShortcut(root, true); Check(File.Exists(path));
            try { RegistryHelper.RegisterStartMenuShortcut(root, origin, true); throw new Exception("shortcut_conflict_missing"); }
            catch (InvalidOperationException error) when (error.Message == "scanner_start_menu_conflict") { }
            File.Delete(path); RegistryHelper.RegisterStartMenuShortcut(root, origin, true);
            RegistryHelper.RemoveStartMenuShortcut(root, true); Check(!File.Exists(path));
        });
        test("start menu opens visible workspace and upgrades owned silent shortcut reversibly", () =>
        {
            var root = Path.Combine(basePath, "visible-shortcut-contract"); Directory.CreateDirectory(root);
            var origin = ScannerConstants.DefaultPublicOrigin; var path = ScannerShortcut.PathFor(root, true);
            var oldPath = ScannerShortcut.OldPathFor(root, true);
            var legacy = ScannerShortcut.LegacyContents(root, origin);
            File.WriteAllText(oldPath, legacy);
            var changed = RegistryHelper.ApplyStartMenuShortcut(root, origin, true);
            Check(changed.Created && changed.PreviousContents == legacy);
            Check(ScannerShortcut.IsOwned(root, origin, true) && !File.Exists(oldPath));
            RegistryHelper.RollbackStartMenuShortcut(root, origin, true, changed);
            Check(File.ReadAllText(oldPath) == legacy && !File.Exists(path));
            changed = RegistryHelper.ApplyStartMenuShortcut(root, origin, true);
            File.WriteAllText(path, "USER_EDIT"); var edited = Sha256Util.ComputeFileSha256(path);
            RegistryHelper.RollbackStartMenuShortcut(root, origin, true, changed);
            Check(Sha256Util.ComputeFileSha256(path) == edited);
            RegistryHelper.RemoveStartMenuShortcut(root, true); Check(File.Exists(path));
            File.Delete(path); File.WriteAllText(oldPath, legacy);
            RegistryHelper.RemoveStartMenuShortcut(root, true); Check(!File.Exists(oldPath));
        });
        test("failed repair restores previous silent shortcut after registration", () =>
        {
            var root = Path.Combine(basePath, "shortcut-repair-rollback");
            InstallEngine.ExecuteInstall(root, ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive),
                () => File.OpenRead(stub), testMode: true, noLaunch: true);
            var path = ScannerShortcut.OldPathFor(root, true);
            var legacy = ScannerShortcut.LegacyContents(root, ScannerConstants.DefaultPublicOrigin);
            File.WriteAllText(path, legacy);
            try
            {
                InstallEngine.ExecuteInstall(root, ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive),
                    () => File.OpenRead(stub), testMode: true, noLaunch: true, faultInjection: "registration");
                throw new Exception("registration_failure_missing");
            }
            catch (InvalidOperationException error) when (error.Message == "runtime_injected_registration") { }
            Check(File.ReadAllText(path) == legacy);
            Check(InstallerStartup.InstalledVersion(root) == ScannerConstants.DisplayVersion);
        });
        test("installer completion closes without opening a second webpage", () =>
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var root = Path.Combine(basePath, "visible-installer-contract"); Directory.CreateDirectory(root);
                    var opened = 0;
                    using var form = new InstallerForm(root, ScannerConstants.DefaultPublicOrigin, true, false, null, false,
                        _ => Task.FromResult(new InstallResult { Success = true, Started = false, InstallRoot = root, Message = "安装完成" }),
                        testOpenWorkspace: () => ++opened > 1);
                    Show(form); Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "启动助手"));
                    Button(form, "启动助手").PerformClick(); Check(Has(form, "完成") && opened == 0);
                    Button(form, "完成").PerformClick(); Check(opened == 0 && form.IsDisposed);
                    var start = ProcessHelper.WorkspaceStartInfo(ScannerConstants.DefaultPublicOrigin);
                    Check(start.UseShellExecute && start.FileName == ScannerConstants.DefaultPublicOrigin + "/system/scanner");
                    try { ProcessHelper.WorkspaceStartInfo("https://example.com"); throw new Exception("workspace_origin_gate_missing"); }
                    catch (InvalidOperationException error) when (error.Message == "scanner_public_origin_invalid") { }
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw failure;
        });
        test("protocol cleanup and upgrade use exact owned registry shape", () =>
        {
            var helper = Path.Combine(basePath, "registry-owner", ScannerConstants.HelperExecutableRelative);
            var command = Key(new() { [""] = "\"" + helper + "\" \"%1\"" });
            var open = Key(new()); open.SubKeys.Add("command", command);
            var shell = Key(new()); shell.SubKeys.Add("open", open);
            var protocol = Key(new() { [""] = "URL:Soda Terminal Scanner Protocol", ["URL Protocol"] = "" }); protocol.SubKeys.Add("shell", shell);
            Check(RegistrationIdentity.ProtocolOwned(protocol, helper));
            command.Values.Add("UserExtra", ("PRESERVE", RegistryValueKind.String));
            Check(!RegistrationIdentity.ProtocolOwned(protocol, helper));
            command.Values.Remove("UserExtra"); command.Values[""] = ("\"" + helper + "\" --foreign \"%1\"", RegistryValueKind.String);
            Check(!RegistrationIdentity.ProtocolOwned(protocol, helper));
        });
        test("uninstall cleanup preserves foreign extra registry values", () =>
        {
            var root = Path.Combine(basePath, "registry-owner"); var exe = Path.Combine(root, "uninstall.exe");
            var key = Key(new() { ["DisplayName"] = "Soda Terminal 扫描助手", ["DisplayVersion"] = "1.0.8", ["Publisher"] = "Soda Terminal",
                ["InstallLocation"] = root, ["UninstallString"] = "\"" + exe + "\"", ["QuietUninstallString"] = "\"" + exe + "\" --silent",
                ["NoModify"] = 1, ["NoRepair"] = 0 });
            Check(RegistrationIdentity.UninstallOwned(key, root));
            key.Values.Add("UserExtra", ("PRESERVE", RegistryValueKind.String)); Check(!RegistrationIdentity.UninstallOwned(key, root));
            key.Values.Remove("UserExtra"); key.Values["InstallLocation"] = (root + "-other", RegistryValueKind.String);
            Check(!RegistrationIdentity.UninstallOwned(key, root));
        });
        test("buttons execute isolated production retry repair uninstall and completion", () =>
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var root = Path.Combine(basePath, "button-production"); var calls = 0;
                    Func<Action<string, int>, Task<InstallResult>> install = progress => Task.Run(() =>
                        InstallEngine.ExecuteInstall(root, ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive),
                            () => File.OpenRead(stub), true, true, progressCallback: progress,
                            faultInjection: ++calls == 1 ? "registration" : "none"));
                    using (var form = New())
                    {
                        Show(form); Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "重试"));
                        Check(!File.Exists(Path.Combine(root, "active.json")));
                        Button(form, "重试").PerformClick(); Pump(() => Has(form, "完成"));
                        Check(InstallerStartup.InstalledVersion(root) == ScannerConstants.DisplayVersion);
                        Button(form, "完成").PerformClick(); Check(form.IsDisposed);
                    }
                    using (var form = New())
                    {
                        Show(form); Button(form, "修复安装").PerformClick(); Pump(() => Has(form, "完成"));
                        Check(calls == 3); Button(form, "完成").PerformClick(); Check(form.IsDisposed);
                    }
                    Directory.CreateDirectory(Path.Combine(root, "outputs")); File.WriteAllText(Path.Combine(root, "outputs", "keep.json"), "KEEP");
                    using (var form = New())
                    {
                        Show(form); Button(form, "卸载").PerformClick(); Pump(() => Has(form, "完成"));
                        Check(!File.Exists(Path.Combine(root, "active.json")) && !File.Exists(Path.Combine(root, "uninstall.exe")));
                        Check(File.ReadAllText(Path.Combine(root, "outputs", "keep.json")) == "KEEP");
                        Button(form, "完成").PerformClick(); Check(form.IsDisposed);
                    }
                    var helper = Path.Combine(root, ScannerConstants.HelperExecutableRelative);
                    var start = ProcessHelper.HelperStartInfo(helper, ScannerConstants.DefaultPublicOrigin);
                    Check(start.FileName == helper && start.WorkingDirectory == Path.GetDirectoryName(helper) && !start.UseShellExecute);
                    Check(start.ArgumentList.Single() == "soda-terminal-scanner://open?origin=" + Uri.EscapeDataString(ScannerConstants.DefaultPublicOrigin));
                    InstallerForm New() => new(root, ScannerConstants.DefaultPublicOrigin, true, true, null, false, install,
                        () => Task.Run(() => UninstallEngine.ExecuteUninstall(root, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub))), () => true);
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw failure;
        });
        var legacy = Environment.GetEnvironmentVariable("SODA_SCANNER_LEGACY_ARCHIVE");
        if (legacy is null) return;
        test("published 1.0.8 bytes upgrade, rollback and two-version uninstall preserve results", () =>
        {
            var root = Path.Combine(basePath, "published-108-upgrade"); var old = Path.Combine(root, "versions", LegacyRuntimeCatalog.Version);
            ZipSecurity.ExtractSafe(legacy, old);
            var supplement = Path.Combine(root, ".supplement"); Directory.CreateDirectory(supplement); RuntimeCatalog.StageSupplement(supplement, old);
            foreach (var pin in LegacyRuntimeCatalog.Files) Check(RuntimeCatalog.Matches(Path.Combine(old, pin.Path), pin));
            foreach (var pin in LegacyRuntimeCatalog.Files.Where(pin => pin.Path.StartsWith("helper\\", StringComparison.Ordinal)))
            { var path = Path.Combine(root, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(Path.Combine(old, pin.Path), path); }
            var helper = Path.Combine(root, ScannerConstants.HelperExecutableRelative);
            Check(ProcessHelper.IsKnownProgram(helper, ScannerConstants.HelperExecutableRelative));
            var foreign = Path.Combine(root, "foreign.exe"); File.WriteAllText(foreign, "MZ FOREIGN");
            Check(!ProcessHelper.IsKnownProgram(foreign, ScannerConstants.HelperExecutableRelative));
            Check(!ProcessHelper.StartManagedHelper(foreign));
            PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"), new PointerInfo { Version = LegacyRuntimeCatalog.Version, Path = old });
            Directory.CreateDirectory(Path.Combine(root, "outputs")); var result = Path.Combine(root, "outputs", "keep.json"); File.WriteAllText(result, "SYNTHETIC_SCAN_RESULT");
            File.WriteAllText(Path.Combine(old, "user.txt"), "USER_FILE");
            Check(InstallerStartup.InstalledVersion(root) == "1.0.8");
            foreach (var fault in new[] { "components", "registration", "previous", "active" })
            {
                var before = Snapshot(root);
                try { Install(fault); throw new Exception("fault_missing"); } catch (InvalidOperationException error) when (error.Message == "runtime_injected_" + fault) { }
                var after = Snapshot(root); Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value));
            }
            Check(Install("none").Success && InstallerStartup.InstalledVersion(root) == ScannerConstants.DisplayVersion);
            Check(PointerStore.ReadValidated(Path.Combine(root, "previous.json"), root)?.Version == LegacyRuntimeCatalog.Version);
            UninstallEngine.ExecuteUninstall(root, true, expectedUninstallerHash: Sha256Util.ComputeFileSha256(stub));
            Check(LegacyRuntimeCatalog.Files.All(pin => !File.Exists(Path.Combine(old, pin.Path))));
            Check(RuntimeCatalog.Files.All(pin => !File.Exists(Path.Combine(root, "versions", ScannerConstants.LockedRuntimeVersion, pin.Path))));
            Check(File.ReadAllText(result) == "SYNTHETIC_SCAN_RESULT" && File.ReadAllText(Path.Combine(old, "user.txt")) == "USER_FILE");
            Check(!File.Exists(Path.Combine(root, "uninstall.exe")) && !File.Exists(Path.Combine(root, "active.json")));
            Check(!File.Exists(ScannerShortcut.PathFor(root, true)));
            InstallResult Install(string fault) => InstallEngine.ExecuteInstall(root, ScannerConstants.DefaultPublicOrigin, () => File.OpenRead(archive), () => File.OpenRead(stub), true, true, faultInjection: fault);
        });
        test("old version shows upgrade action and two version labels", () =>
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var root = Path.Combine(basePath, "version-ui"); Directory.CreateDirectory(root);
                    PointerStore.WritePointerAtomic(Path.Combine(root, "active.json"), new PointerInfo { Version = LegacyRuntimeCatalog.Version, Path = Path.Combine(root, "versions", LegacyRuntimeCatalog.Version) });
                    using var form = new InstallerForm(root, ScannerConstants.DefaultPublicOrigin, true, true, null, false);
                    Check(form.Controls.OfType<Button>().Single(button => button.Text == "升级安装").Enabled);
                    var label = form.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single(label => label.Text.Contains("当前"));
                    Check(label.Text.Contains("1.0.8") && label.Text.Contains(ScannerConstants.DisplayVersion));
                    form.ShowInTaskbar = false; form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual; form.Opacity = 0; form.Show();
                    using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(basePath, "scanner-upgrade-100.png"));
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw failure;
        });
    }
    private static Button Button(Form form, string label) => form.Controls.OfType<Button>().Single(button => button.Text == label);
    private static bool Has(Form form, string label) => form.Controls.OfType<Button>().Any(button => button.Text == label && button.Enabled);
    private static void Show(Form form) { form.ShowInTaskbar = false; form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual; form.Opacity = 0; form.Show(); }
    private static void Pump(Func<bool> condition) { var until = DateTime.UtcNow.AddSeconds(90); while (!condition()) { Application.DoEvents(); if (DateTime.UtcNow > until) throw new Exception("scanner_button_timeout"); Thread.Sleep(5); } Application.DoEvents(); }
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), Sha256Util.ComputeFileSha256);
    private static RegistrySnapshot.KeySnapshot Key(Dictionary<string, object> values)
    {
        var result = new RegistrySnapshot.KeySnapshot { Exists = true };
        foreach (var pair in values) result.Values.Add(pair.Key, (pair.Value, pair.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String)); return result;
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("upgrade_uninstall_assertion"); }
}
