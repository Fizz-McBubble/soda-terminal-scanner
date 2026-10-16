using System.Drawing;
using System.Windows.Forms;
using Soda.Scanner.Core;
using Soda.Scanner.Setup;

internal static class InstallerFormTests
{
    public static void Run(string outputRoot, Action<string, Action> test)
    {
        var root = Path.Combine(outputRoot, "first-run-decisions");
        Directory.CreateDirectory(root);
        test("first run accepts missing and empty roots", () =>
        {
            Check(!InstallerStartup.HasInstallationTraces(Path.Combine(root, "missing"), false));
            var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
            Check(!InstallerStartup.HasInstallationTraces(empty, false));
        });
        test("existing and partial filesystem traces require manual repair without writes", () =>
        {
            foreach (var trace in new[] { "active.json", "previous.json", "helper", "versions", "uninstall.exe", "outputs", ".setup-interrupted", "unknown.txt" })
            {
                var candidate = Path.Combine(root, trace.Replace('.', '-'));
                Directory.CreateDirectory(candidate);
                var path = Path.Combine(candidate, trace);
                if (Path.HasExtension(trace)) File.WriteAllText(path, "PRESERVE");
                else Directory.CreateDirectory(path);
                Check(InstallerStartup.HasInstallationTraces(candidate, false));
                if (File.Exists(path)) Check(File.ReadAllText(path) == "PRESERVE");
            }
        });
        test("registration trace alone prevents automatic install", () =>
            Check(InstallerStartup.HasInstallationTraces(Path.Combine(root, "registry-only"), true)));

        RunSta(() =>
        {
            test("shown first run automatically installs once and reports progress", () =>
            {
                var calls = 0;
                var pending = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var form = NewForm(Path.Combine(root, "automatic"), progress =>
                {
                    calls++; progress("TEST_PREPARATION", 37); return pending.Task;
                });
                ShowHidden(form); PumpUntil(() => calls == 1);
                form.ShowAgain(); Application.DoEvents();
                Check(calls == 1);
                var bar = form.Controls.OfType<Panel>().Single().Controls.OfType<ProgressBar>().Single();
                Check(bar.Visible && bar.Value == 37);
                Check(Button(form, "安装中").Enabled == false);
                form.Close(); Check(!form.IsDisposed); // Closing is blocked until transaction completes.
                pending.SetResult(new InstallResult { Success = true, Message = "TEST_COMPLETED" });
                PumpUntil(() => ButtonOrNull(form, "完成")?.Enabled == true);
                Check(bar.Value == 100 && Button(form, "关闭").Enabled);
                form.ShowAgain(); Application.DoEvents(); Check(calls == 1);
                form.Close(); Check(form.IsDisposed);
            });
            test("failed automatic install stays manual and retry succeeds", () =>
            {
                var calls = 0;
                using var form = NewForm(Path.Combine(root, "failure"), _ =>
                    ++calls == 1 ? Task.FromException<InstallResult>(new IOException("test failure")) :
                    Task.FromResult(new InstallResult { Success = true, Message = "RETRIED" }));
                ShowHidden(form); PumpUntil(() => ButtonOrNull(form, "重试")?.Enabled == true);
                form.ShowAgain(); Application.DoEvents(); Check(calls == 1);
                Check(Button(form, "关闭").Enabled);
                Button(form, "重试").PerformClick();
                PumpUntil(() => ButtonOrNull(form, "完成")?.Enabled == true);
                Check(calls == 2);
            });
            test("partial install form preserves repair and uninstall without auto start", () =>
            {
                var partial = Path.Combine(root, "partial-form"); Directory.CreateDirectory(Path.Combine(partial, "versions"));
                var sentinel = Path.Combine(partial, "active.json"); File.WriteAllText(sentinel, "DAMAGED_POINTER");
                var calls = 0;
                using var form = NewForm(partial, _ => { calls++; return Task.FromResult(new InstallResult()); });
                ShowHidden(form); Application.DoEvents(); form.ShowAgain(); Application.DoEvents();
                Check(calls == 0 && Button(form, "修复安装").Enabled && Button(form, "卸载").Visible);
                Check(File.ReadAllText(sentinel) == "DAMAGED_POINTER");
            });
            test("uninstall failure uses existing feedback then completion closes without reinstall", () =>
            {
                var candidate = Path.Combine(root, "uninstall-feedback"); Directory.CreateDirectory(candidate);
                File.WriteAllText(Path.Combine(candidate, "active.json"), "PRESERVE");
                var removals = 0; var installs = 0; var feedbackOpens = 0;
                using var form = new InstallerForm(candidate, ScannerConstants.DefaultPublicOrigin, true, true, null, false,
                    testInstall: _ => { installs++; return Task.FromResult(new InstallResult()); },
                    testUninstall: () => ++removals == 1 ? Task.FromException<UninstallResult>(new IOException("scanner_still_running"))
                        : Task.FromResult(new UninstallResult { Success = true, Message = "扫描结果和账户数据已保留。" }),
                    testConfirmUninstall: () => true, testOpenFeedback: _ => { feedbackOpens++; return true; });
                ShowHidden(form); Button(form, "卸载").PerformClick();
                PumpUntil(() => ButtonOrNull(form, "反馈安装问题")?.Visible == true);
                Check(feedbackOpens == 0 && installs == 0 && Button(form, "卸载").Enabled);
                var report = File.ReadAllText(Path.Combine(candidate, "installer-diagnostics", "latest.json"));
                Check(report.Contains("scanner_still_running") && !report.Contains(candidate));
                Button(form, "反馈安装问题").PerformClick(); Check(feedbackOpens == 1);
                Button(form, "卸载").PerformClick(); PumpUntil(() => ButtonOrNull(form, "完成")?.Enabled == true);
                Check(ButtonOrNull(form, "反馈安装问题")?.Visible != true);
                Button(form, "完成").PerformClick(); Check(form.IsDisposed && installs == 0 && removals == 2);
            });
            test("installation traces appearing before shown prevent auto start", () =>
            {
                var late = Path.Combine(root, "late-trace"); var calls = 0;
                using var form = NewForm(late, _ => { calls++; return Task.FromResult(new InstallResult()); });
                Directory.CreateDirectory(Path.Combine(late, "helper"));
                ShowHidden(form); Application.DoEvents();
                Check(calls == 0 && Button(form, "修复安装").Enabled);
            });
            test("preview mode shows fresh UI without triggering install", () =>
            {
                var calls = 0;
                using var form = NewForm(Path.Combine(root, "preview"), _ => { calls++; return Task.FromResult(new InstallResult()); }, false);
                ShowHidden(form); Application.DoEvents(); Check(calls == 0);
            });
            test("all preview fixtures are guarded real controls with one completion action", () =>
            {
                foreach (var state in new[] { "initial", "preparing", "progress", "completion", "error", "repair" })
                {
                    var calls = 0;
                    using var form = NewForm(Path.Combine(root, "fixture-" + state), _ => { calls++; return Task.FromResult(new InstallResult()); }, false);
                    form.ApplyRenderFixture(state); ShowHidden(form); Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(root, "render-" + state + ".png"));
                    }
                    Check(calls == 0 && form.BackColor == Color.White);
                    var bar = form.Controls.OfType<Panel>().Single().Controls.OfType<ProgressBar>().Single();
                    Check(bar.AccessibleName == "安装进度");
                    if (state == "completion") Check(form.Controls.OfType<Button>().Count(b => b.Visible) == 1 && bar.Value == 100);
                    var scale = 1.5f / (form.DeviceDpi / 96f);
                    form.Scale(new SizeF(scale, scale));
                    var panel = form.Controls.OfType<Panel>().Single();
                    foreach (Control control in form.Controls)
                        Check(control.Right <= form.ClientSize.Width && control.Bottom <= form.ClientSize.Height);
                    foreach (Control control in panel.Controls)
                        Check(control.Right <= panel.Width && control.Bottom <= panel.Height);
                    Check(panel.Bottom <= form.Controls.OfType<Button>().Min(b => b.Top));
                    Check(panel.Controls.OfType<TextBox>().Single().ReadOnly);
                    Check(panel.Controls.OfType<TextBox>().Single().Visible);
                    Check(panel.Controls.OfType<TextBox>().Single().ScrollBars == ScrollBars.None);
                    Check(!panel.Controls.OfType<TextBox>().Single().TabStop && panel.Controls.OfType<TextBox>().Single().SelectionLength == 0);
                    Check(!panel.Controls.OfType<LinkLabel>().Any());
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(root, "render-" + state + "-150-details.png"));
                    }
                }
                using var productionLike = NewForm(Path.Combine(root, "preview-guard"), _ => Task.FromResult(new InstallResult()), true);
                var rejected = false;
                try { productionLike.ApplyRenderFixture("completion"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected);
            });
            test("visible details follow failure and completion", () =>
            {
                foreach (var failure in new[] { true, false })
                {
                    var pending = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var called = false;
                    using var form = NewForm(Path.Combine(root, "details-update-" + failure), _ => { called = true; return pending.Task; });
                    ShowHidden(form); PumpUntil(() => called);
                    var details = form.Controls.OfType<Panel>().Single().Controls.OfType<TextBox>().Single();
                    Check(details.Visible);
                    if (failure)
                    {
                        pending.SetException(new IOException("TEST_DETAILS_ERROR"));
                        PumpUntil(() => ButtonOrNull(form, "重试")?.Enabled == true);
                        Check(details.Text.Contains("安装未完成") && details.Text.Contains("尚未发送") && !details.Text.Contains("TEST_DETAILS_ERROR"));
                    }
                    else
                    {
                        pending.SetResult(new InstallResult { Success = true, Message = "COMPLETED" });
                        PumpUntil(() => ButtonOrNull(form, "完成")?.Enabled == true);
                        Check(details.Text.Contains("已安装"));
                    }
                }
            });
        });
    }

    private sealed class TestForm(string root, bool auto, Func<Action<string, int>, Task<InstallResult>> install)
        : InstallerForm(root, ScannerConstants.DefaultPublicOrigin, true, true, null, auto, install)
    {
        public void ShowAgain() => OnShown(EventArgs.Empty);
    }
    private static TestForm NewForm(string root, Func<Action<string, int>, Task<InstallResult>> install, bool auto = true) => new(root, auto, install);
    private static Button? ButtonOrNull(Form form, string text) => form.Controls.OfType<Button>().SingleOrDefault(b => b.Text == text);
    private static Button Button(Form form, string text) => ButtonOrNull(form, text) ?? throw new Exception("button missing: " + text);
    private static void Check(bool condition) { if (!condition) throw new Exception("first-run assertion failed"); }
    private static void ShowHidden(Form form)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { Application.DoEvents(); if (DateTime.UtcNow > deadline) throw new Exception("first-run UI timeout"); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new Exception("isolated WinForms test failed", error);
    }
}
