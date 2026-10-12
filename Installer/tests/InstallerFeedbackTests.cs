using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Soda.Scanner.Core;
using Soda.Scanner.Setup;

internal static class InstallerFeedbackTests
{
    internal static int Run(string outputRoot)
    {
        var root = FileSystemSafety.ValidateRoot(outputRoot, true);
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var uiRoot = Path.Combine(root, "ui-" + Guid.NewGuid().ToString("N"));
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            Check(InstallerFeedback.Classify(new IOException(@"private C:\account\unrecognized-error")) == "installer_local_operation_failed", "unknown_error_is_generic");
            Check(InstallerFeedback.Classify(new UnauthorizedAccessException("private")) == "installer_access_denied", "access_denial_classified_by_type");
            Check(InstallerFeedback.Classify(new IOException("runtime_file_verification_failed")) == "runtime_file_identity_mismatch", "runtime_identity_exact_match");
            Check(InstallerFeedback.Classify(new IOException("scanner_start_menu_conflict")) == "scanner_registration_conflict", "registration_exact_match");
            Check(InstallerFeedback.Classify(new IOException("setup_asset_prepare_timeout")) == "setup_asset_prepare_timeout", "preparation_timeout_exact_match");
            var report = InstallerFeedback.Create("private unknown stage", "private unknown code", long.MaxValue);
            Check(report.Stage == "failed" && report.Code == "installer_local_operation_failed" && report.DurationMs == 3600000, "finite_bounded_projection");
            Check(report.Tool == "screen_scanner_installer" && report.InstallerVersion == "1.1.6" && report.Attempts.Length == 0, "actual_product_identity_offline_attempts");
            Check(Guid.TryParse(report.ReportId, out _), "report_id_is_uuid");
            var url = InstallerFeedback.LaunchUrl(report);
            Check(url.StartsWith(InstallerFeedback.FeedbackPage, StringComparison.Ordinal), "fixed_website_fragment");
            Check(Uri.UnescapeDataString(url[(url.IndexOf('#') + 1)..]) == InstallerFeedback.Serialize(report), "fragment_roundtrip_no_query");
            Check(InstallerDiagnostics.Save(root, report), "safe_local_diagnostic_saved");
            using (var parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "installer-diagnostics", "latest.json"))))
            {
                var names = parsed.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
                Check(names.SequenceEqual(new[] { "schema", "tool", "reportId", "installerVersion", "stage", "code", "durationMs", "attempts" }.Order()) &&
                    parsed.RootElement.GetProperty("attempts").GetArrayLength() == 0, "diagnostic_contains_only_contract_fields");
            }
            var diagnostics = new InstallerDiagnostics(); diagnostics.Phase("copy"); diagnostics.Phase("private unknown stage");
            Check(diagnostics.Failure(new IOException("private unknown error")).Stage == "copy", "unknown_phase_does_not_replace_known_phase");
            foreach (var callbackThrows in new[] { false, true })
            {
                var phases = new List<string>();
                var rejected = false;
                try
                {
                    InstallEngine.ExecuteInstall(Path.Combine(uiRoot, "phase-" + callbackThrows), ScannerConstants.DefaultPublicOrigin,
                        () => new MemoryStream("synthetic invalid archive"u8.ToArray()), () => new MemoryStream(), testMode: true, noLaunch: true,
                        phaseCallback: phase => { phases.Add(phase); if (callbackThrows) throw new Exception("synthetic diagnostic callback failure"); });
                }
                catch (InvalidOperationException error) when (error.Message == "runtime_asset_identity_mismatch") { rejected = true; }
                Check(rejected && phases.SequenceEqual(new[] { "prepare", "payload_verify" }), "phase_callback_preserves_engine_rejection_" + callbackThrows);
            }

            Exception? threadError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    foreach (var (scale, priorInstallation) in new[] { (1f, false), (1.5f, false), (2f, false), (2f, true) })
                    {
                        var scenario = Path.Combine(uiRoot, "feedback-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + priorInstallation);
                        if (priorInstallation) Directory.CreateDirectory(Path.Combine(scenario, "versions"));
                        var installs = 0; var browserCalls = 0; var clipboardCalls = 0; var saveCalls = 0;
                        var recoverySucceeds = true;
                        string? openedUrl = null, copiedUrl = null, exportedJson = null;
                        using var form = new InstallerForm(scenario, ScannerConstants.DefaultPublicOrigin, true, true, null, true,
                            _ => ++installs == 1 ? Task.FromException<InstallResult>(new IOException(@"private C:\account\failure"))
                                : Task.FromResult(new InstallResult { Success = true, Message = "synthetic installed" }),
                            testOpenFeedback: target => { browserCalls++; openedUrl = target; return false; },
                            testCopyFeedback: target => { clipboardCalls++; copiedUrl = target; return recoverySucceeds; },
                            testSaveFeedback: text => { saveCalls++; exportedJson = text; return recoverySucceeds; });
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
                        if (priorInstallation) { Application.DoEvents(); Button(form, "修复安装")!.PerformClick(); }
                        Pump(() => Button(form, "重试")?.Enabled == true);
                        Check(browserCalls == 0 && clipboardCalls == 0 && saveCalls == 0, "no_automatic_feedback_" + scale);
                        var panel = form.Controls.OfType<Panel>().Single();
                        var detail = panel.Controls.OfType<TextBox>().Single();
                        Check(!detail.Text.Contains("private") && detail.Text.Contains("尚未发送"), "safe_failure_detail_" + scale);
                        var local = JsonSerializer.Deserialize<InstallerFeedbackReport>(File.ReadAllText(Path.Combine(scenario, "installer-diagnostics", "latest.json")))!;
                        Check(local.Code == "installer_local_operation_failed", "safe_failure_projection_" + scale);
                        form.Scale(new SizeF(scale / (form.DeviceDpi / 96f), scale / (form.DeviceDpi / 96f)));
                        var buttons = form.Controls.OfType<Button>().Where(button => button.Visible).OrderBy(button => button.Left).ToArray();
                        Check(buttons.Length == (priorInstallation ? 4 : 3) && buttons.Select(button => button.Top).Distinct().Count() == 1, "compact_failure_row_" + scale);
                        Check(buttons.All(button => Math.Abs(button.Height - 42 * scale) <= 1), "42px_buttons_" + scale);
                        Check(buttons.All(button => button.Left >= 0 && button.Right <= form.ClientSize.Width && button.Bottom <= form.ClientSize.Height), "buttons_not_clipped_" + scale);
                        Check(buttons.Zip(buttons.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left), "buttons_do_not_overlap_" + scale);
                        Check(detail.Right <= panel.Width && detail.Bottom <= panel.Height, "details_not_clipped_" + scale);
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(root, "feedback-error-" + scale + (priorInstallation ? "-repair" : "") + ".png")); }
                        Button(form, "反馈安装问题")!.PerformClick(); Application.DoEvents();
                        Check(browserCalls == 1 && Button(form, "复制 / 下载")?.Visible == true, "browser_failure_exposes_recovery_" + scale);
                        form.CopyFeedback(); form.ExportFeedback();
                        Check(clipboardCalls == 1 && saveCalls == 1 && copiedUrl == openedUrl, "explicit_copy_and_download_" + scale);
                        Check(exportedJson == InstallerFeedback.Serialize(local), "recovery_preserves_same_report_id_" + scale);
                        Check(detail.Text.Contains("尚未发送"), "download_does_not_claim_receipt_" + scale);
                        recoverySucceeds = false;
                        form.CopyFeedback(); Check(detail.Text.Contains("复制未完成"), "clipboard_failure_remains_recoverable_" + scale);
                        form.ExportFeedback(); Check(detail.Text.Contains("保存未完成") && exportedJson == InstallerFeedback.Serialize(local), "save_failure_preserves_report_" + scale);
                        Button(form, "重试")!.PerformClick(); Pump(() => Button(form, "完成")?.Enabled == true);
                        Check(!form.Controls.OfType<Button>().Any(button => button.Visible && (button.Text == "反馈安装问题" || button.Text == "复制 / 下载")), "retry_clears_stale_feedback_" + scale);
                    }
                    InstallerFormTests.Run(uiRoot, (name, action) => { action(); checks.Add(name); });
                }
                catch (Exception error) { threadError = error; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (threadError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threadError).Throw();
            File.WriteAllText(Path.Combine(root, "installer-feedback-tests.json"), JsonSerializer.Serialize(new { exitCode = 0, passed = checks.Count, checks, actualInstall = false, registryWrites = 0, browserLaunches = 0, uploads = 0 }));
            Console.WriteLine("PASS scanner installer feedback " + checks.Count); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "installer-feedback-tests.json"), JsonSerializer.Serialize(new { exitCode = 1, passed = checks.Count, checks, error = error.ToString() }));
            Console.Error.WriteLine(error); return 1;
        }
    }
    private static Button? Button(Form form, string text) => form.Controls.OfType<Button>().SingleOrDefault(button => button.Text == text);
    private static void Pump(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { Application.DoEvents(); if (DateTime.UtcNow > deadline) throw new Exception("feedback_fixture_timeout"); Thread.Sleep(5); }
        Application.DoEvents();
    }
}
