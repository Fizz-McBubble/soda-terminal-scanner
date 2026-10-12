using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

// Portable producer for the existing website contract. No paths, raw exceptions,
// registration/process identities or account information enter this DTO.
internal sealed record InstallerFeedbackReport(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("reportId")] string ReportId,
    [property: JsonPropertyName("installerVersion")] string InstallerVersion,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("attempts")] object[] Attempts);

internal static class InstallerFeedback
{
    internal const string FeedbackPage = "https://sodaterminal.com/installer-feedback#";
    internal static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    { "prepare", "payload_verify", "extract", "verify", "copy", "register", "commit", "start", "failed" };
    internal static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "installer_access_denied", "installer_local_operation_failed", "setup_asset_missing",
        "setup_asset_size_mismatch", "setup_asset_parent_unavailable", "setup_asset_parent_invalid",
        "setup_asset_parent_exited", "setup_asset_apphost_unavailable", "setup_asset_apphost_invalid",
        "setup_asset_prepare_timeout", "setup_asset_extract_failed", "setup_asset_ready_invalid",
        "runtime_asset_identity_mismatch", "runtime_file_identity_mismatch", "runtime_health_probe_failed",
        "runtime_health_probe_timeout", "uninstaller_invalid", "scanner_registration_conflict",
        "linked_path_rejected", "catalog_path_rejected", "another_installer_is_running", "scanner_still_running"
    };
    internal static string Classify(Exception error)
    {
        if (error is UnauthorizedAccessException) return "installer_access_denied";
        var code = error.Message switch
        {
            "runtime_file_verification_failed" => "runtime_file_identity_mismatch",
            "vc_runtime_asset_identity_mismatch" or "setup_uninstaller_identity_mismatch" or
                "setup_stub_identity_missing" or "setup_stub_identity_invalid" => "runtime_asset_identity_mismatch",
            "scanner_protocol_owned_by_another_path" or "scanner_uninstall_entry_owned_by_another_path" or
                "scanner_start_menu_conflict" => "scanner_registration_conflict",
            "managed_path_is_link" or "runtime_archive_link_forbidden" => "linked_path_rejected",
            "managed_path_invalid" or "managed_path_outside_root" or "managed_install_root_required" => "catalog_path_rejected",
            "scanner_setup_already_running" => "another_installer_is_running",
            _ => error.Message
        };
        // Exact finite matches only: a path-bearing or otherwise unknown error is generic.
        return Codes.Contains(code) ? code : "installer_local_operation_failed";
    }
    internal static InstallerFeedbackReport Create(string stage, string code, long elapsedMs) => new(
        1, "screen_scanner_installer", Guid.NewGuid().ToString("D"), ScannerConstants.DisplayVersion,
        Stages.Contains(stage) ? stage : "failed", Codes.Contains(code) ? code : "installer_local_operation_failed",
        Math.Clamp(elapsedMs, 0, 3600000), []);
    internal static string Serialize(InstallerFeedbackReport report) => JsonSerializer.Serialize(report);
    internal static string LaunchUrl(InstallerFeedbackReport report) => FeedbackPage + Uri.EscapeDataString(Serialize(report));
    internal static bool OpenBrowser(string url)
    {
        try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch { return false; }
    }
    internal static bool Copy(string text)
    {
        try { Clipboard.SetText(text); return true; } catch { return false; }
    }
}
