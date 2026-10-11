using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZZZScannerHelper;

internal static class DirectForkLifecycleDiagnostics
{
    internal static string StartFailureCode(Exception exception) => exception switch
    {
        System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => "elevation_cancelled",
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException => "permission_denied",
        _ when exception.Message.StartsWith("soda_runtime_", StringComparison.Ordinal) => "runtime_repair_required",
        _ => "scanner_start_failed"
    };

    internal static string? ReadTerminal(string? path, out string? failureReason)
    {
        failureReason = path is null ? "fork_exit_without_terminal" : null;
        if (path is null) return null;
        try
        {
            var json = DirectForkProgress.ReadFile(path);
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("Success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !result.TryGetProperty("Completed", out var completed) || !completed.TryGetBoundedInteger(100000, out _) ||
                !result.TryGetProperty("Failed", out var failed) || !failed.TryGetBoundedInteger(100000, out _))
                throw new JsonException();
            foreach (var field in new[] { "Visited", "Queued" })
                if (result.TryGetProperty(field, out var counter) && !counter.TryGetBoundedInteger(100000, out _))
                    throw new JsonException();
            return json;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never copy terminal contents, exception text or filesystem paths.
            failureReason = "fork_terminal_unreadable";
            return null;
        }
    }

    internal static void Record(JsonObject report, string reason, string? phase = null)
    {
        var evidence = report["evidence"]!.AsObject();
        // A native stop owns its precise evidence. Add lifecycle classification
        // only if the native producer has not already explained that stop.
        if (evidence["reason"] is null) evidence["reason"] = reason;
        if (phase is not null) evidence["phase"] = phase;
        if (evidence["diagnosticSource"] is null || evidence["diagnosticSource"]!.GetValue<string>() == "unavailable")
            evidence["diagnosticSource"] = "helper_lifecycle";
    }
}
