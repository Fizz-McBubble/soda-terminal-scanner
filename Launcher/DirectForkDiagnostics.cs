using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZZZScannerHelper;

// This projection is deliberately independent of raw exception messages and result contents.
// The same contract is embedded in Helper and consumed by the browser and feedback endpoint.
internal static class DirectForkDiagnostics
{
    private static readonly JsonObject Contract = JsonNode.Parse(typeof(DirectForkDiagnostics).Assembly
        .GetManifestResourceStream("ZZZScannerHelper.ScanFeedbackContract.json")!)!.AsObject();

    internal static JsonObject Create() => new()
    {
        ["reportId"] = Guid.NewGuid().ToString(), ["outcome"] = "unknown", ["stage"] = "connection", ["code"] = "none",
        ["versions"] = new JsonObject { ["helper"] = Program.HelperVersion, ["protocol"] = Program.ProtocolVersion.ToString(CultureInfo.InvariantCulture) },
        ["counts"] = new JsonObject { ["processed"] = null, ["total"] = null }, ["durationMs"] = null,
        ["environment"] = new JsonObject(), ["evidence"] = new JsonObject()
    };

    internal static void ApplyRuntime(JsonObject report, SodaRuntimeIdentity runtime)
    {
        var versions = report["versions"]!.AsObject();
        foreach (var (key, value) in new[] { ("runtime", runtime.Version), ("scanner", runtime.CaptureVersion), ("ocr", runtime.OcrVersion) })
            if (value is not null && Regex.IsMatch(value, Contract["versionPatterns"]![key]!.GetValue<string>())) versions[key] = value;
    }

    internal static string Code(string? code) => code switch
    {
        "capture_failed" or "inventory_screen_unreadable" or "unsupported_display_layout" or "inventory_screen_not_detected" => "visual_preflight_failed",
        "direct_fork_terminal_failed" => "scanner_failure",
        _ when code?.StartsWith("direct_fork_exit_", StringComparison.Ordinal) == true => "scanner_exit",
        _ => Contract["codes"]!.AsArray().Any(item => item!.GetValue<string>() == code) ? code! : "unknown"
    };

    internal static string Stage(string code) => code switch
    {
        "helper_unavailable" or "helper_incompatible" or "helper_pairing_denied" => "connection",
        "permission_denied" or "elevation_cancelled" => "permission",
        "game_process_not_found" or "visual_preflight_failed" => "preflight",
        "panel_capture_timeout" => "capture", "scan_navigation_failed" => "scroll", "ocr_worker_failed" => "ocr",
        "direct_fork_result_missing" or "direct_fork_partial" or "previous_scan_recovery_failed" or "scan_result_timeout" or "scan_result_read_failed" or "scan_file_invalid" => "result",
        "scan_import_handoff_failed" or "scan_import_failed" => "import",
        _ => "unknown"
    };

    internal static void Finish(JsonObject report, string outcome, string? code, double? elapsedMilliseconds)
    {
        var safeCode = Code(code);
        report["outcome"] = outcome;
        report["code"] = safeCode;
        var stage = Stage(safeCode);
        if (stage != "unknown") report["stage"] = stage;
        if (report["durationMs"] is null && elapsedMilliseconds is double elapsed && elapsed >= 0 && elapsed <= Contract["durationMaxMs"]!.GetValue<int>())
            report["durationMs"] = (long)Math.Round(elapsed);
    }

    internal static JsonObject Read(string log, string? terminalJson, int? previousTotal)
    {
        // Metadata is at the start and current failures/counters at the end. Bound every
        // extra diagnostic regex pass so instrumentation does not scale with the whole scan.
        if (log.Length > 131072) log = log[..65536] + "\n" + log[^65536..];
        var counts = new JsonObject { ["processed"] = Number(log, "completed", 100000), ["total"] = Number(log, "inventoryCount|expectedTotal", 100000) };
        if (counts["total"] is null && previousTotal is > 0 and <= 100000) counts["total"] = previousTotal;
        foreach (var key in new[] { "visited", "queued", "failed" })
            if (Number(log, key, 100000) is int value) counts[key] = value;
        var code = Code(Token(log, "terminationCode"));
        var outcome = "unknown";
        if (terminalJson is not null)
        {
            using var document = JsonDocument.Parse(terminalJson);
            var result = document.RootElement;
            foreach (var (field, key) in new[] { ("Completed", "processed"), ("Visited", "visited"), ("Queued", "queued"), ("Failed", "failed") })
                if (result.TryGetProperty(field, out var node) && node.TryGetInt32(out var value) && value is >= 0 and <= 100000) counts[key] = value;
            var status = result.TryGetProperty("Status", out var statusNode) ? statusNode.GetString() : null;
            outcome = status is "canceled" or "cancelled" ? "cancelled" : result.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.True ? "completed" : "failed";
            // Only classify known signals. None of this free text enters the projection.
            var detail = result.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : "";
            if (code == "unknown") code = detail.Contains("未找到游戏窗口进程", StringComparison.Ordinal) ? "game_process_not_found"
                : detail.Contains("StalePanel", StringComparison.Ordinal) ? "panel_capture_timeout"
                : log.Contains("OCR worker failed:", StringComparison.Ordinal) ? "ocr_worker_failed"
                : outcome == "completed" ? "none" : "scanner_failure";
        }
        var environment = new JsonObject();
        foreach (var (key, source) in new[] { ("width", "clientWidth"), ("height", "clientHeight"), ("dpi", "dpi") })
            if (Number(log, source, Contract["environmentLimits"]![key]!.GetValue<int>()) is > 0 and var value) environment[key] = value;
        var geometry = Regex.Match(log, @"Window client=\{[^\r\n]*?Width=(\d+),Height=(\d+)\}");
        if (geometry.Success)
            foreach (var (key, group) in new[] { ("width", 1), ("height", 2) })
                if (int.TryParse(geometry.Groups[group].Value, out var size) && size is > 0 and <= 20000) environment[key] = size;
        var capture = Token(log, "captureModeActive|captureMode").ToLowerInvariant();
        if (capture is "gdi" or "dxgi") environment["captureMode"] = capture;
        var evidence = new JsonObject();
        foreach (var limit in Contract["evidenceLimits"]!.AsObject())
            if (Number(log, Regex.Escape(limit.Key), limit.Value!.GetValue<long>()) is long value) evidence[limit.Key] = value;
        foreach (var item in Contract["evidenceBooleans"]!.AsArray())
        {
            var key = item!.GetValue<string>();
            if (bool.TryParse(Token(log, Regex.Escape(key)), out var value)) evidence[key] = value;
        }
        Pair(log, "visibleRois", "visibleRois", "totalRois", evidence);
        Pair(log, "stableFrames", "stableFrames", "requiredStableFrames", evidence);
        Pair(log, "col", "column", "maxColumns", evidence);
        var roi = Token(log, "firstMissingRoi");
        if (Contract["missingRois"]!.AsArray().Any(item => item!.GetValue<string>() == roi)) evidence["firstMissingRoi"] = roi;
        var stage = Stage(code);
        return new JsonObject { ["outcome"] = outcome, ["code"] = code, ["stage"] = stage == "unknown" ? counts["processed"]?.GetValue<int>() > 0 ? "capture" : "preflight" : stage,
            ["counts"] = counts, ["durationMs"] = Duration(log), ["environment"] = environment, ["evidence"] = evidence };
    }

    internal static void Merge(JsonObject report, JsonObject facts)
    {
        foreach (var field in new[] { "outcome", "code", "stage", "counts", "durationMs", "environment", "evidence" }) report[field] = facts[field]?.DeepClone();
    }

    private static int? Number(string text, string key, int maximum)
    {
        var value = Number(text, key, (long)maximum);
        return value is long number ? (int)number : null;
    }

    private static long? Number(string text, string key, long maximum)
    {
        var matches = Regex.Matches(text, $@"\b(?:{key})=(\d+)(?=[,/\s}}]|$)");
        return matches.Count > 0 && long.TryParse(matches[^1].Groups[1].Value, out var number) && number >= 0 && number <= maximum ? number : null;
    }

    private static string Token(string text, string key)
    {
        var matches = Regex.Matches(text, $@"\b(?:{key})=([A-Za-z0-9_]+)(?=[,/\s.]|$)");
        return matches.Count > 0 ? matches[^1].Groups[1].Value : "";
    }

    private static void Pair(string log, string source, string left, string right, JsonObject target)
    {
        var pairs = Regex.Matches(log, $@"\b{source}=(\d+)/(\d+)\b");
        if (pairs.Count == 0) return;
        foreach (var (key, group) in new[] { (left, 1), (right, 2) })
            if (int.TryParse(pairs[^1].Groups[group].Value, out var value) && value >= 0 && value <= Contract["evidenceLimits"]![key]!.GetValue<int>()) target[key] = value;
    }

    private static long? Duration(string log)
    {
        var start = Regex.Match(log, @"^\[([^\]\r\n]+)\] Start scan\.", RegexOptions.Multiline);
        var end = Regex.Match(log, @"^\[([^\]\r\n]+)\] EVENT #\d+ SCAN_TERMINAL:", RegexOptions.Multiline);
        if (!DateTime.TryParseExact(start.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) ||
            !DateTime.TryParseExact(end.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) return null;
        var elapsed = (last - first).TotalMilliseconds;
        return elapsed >= 0 && elapsed <= Contract["durationMaxMs"]!.GetValue<int>() ? (long)Math.Round(elapsed) : null;
    }
}

internal static partial class Program
{
    private sealed partial class DirectForkHttpHelperServer
    {
        private long? _diagnosticStartedTimestamp;

        private void RuntimeDiagnosticFailure()
        {
            var report = DirectForkDiagnostics.Create();
            DirectForkDiagnostics.Finish(report, "failed", "helper_incompatible", null);
            _snapshot["diagnostics"] = report;
        }

        private bool BeginDiagnosticAttempt(long job) => _jobs.Publish(job, () =>
        {
            _diagnosticStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _snapshot["diagnostics"] = DirectForkDiagnostics.Create();
            _snapshot["state"] = "checking";
            _snapshot["summary"] = null;
            _snapshot["progress"] = null;
            _snapshot["error"] = null;
            _resultPath = null;
            _resultHandle = null;
            _evidenceRoot = null;
            Publish();
        });

        private double? DiagnosticElapsedMilliseconds() => _diagnosticStartedTimestamp is long started
            ? System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds : null;
    }
}
