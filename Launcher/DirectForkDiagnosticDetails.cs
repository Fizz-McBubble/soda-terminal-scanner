using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZZZScannerHelper;

internal static partial class DirectForkDiagnostics
{
    private static JsonObject ReadFailureEvidence(string log, string code, JsonElement? terminalDetails, JsonObject environment)
    {
        if (terminalDetails is JsonElement details)
        {
            // The stopped operation owns this snapshot. Never fill gaps with a later
            // successful CELL_TIMING or with a different OCR worker's last record.
            var evidence = ProjectDetails(details);
            foreach (var (key, source) in new[] { ("width", "clientWidth"), ("height", "clientHeight"), ("dpi", "dpi") })
                if (details.TryGetProperty(source, out var value) && value.TryGetBoundedInteger(Contract["environmentLimits"]![key]!.GetValue<long>(), out var number) && number > 0)
                    environment[key] = number;
            if (details.TryGetProperty("captureMode", out var capture) && capture.ValueKind == JsonValueKind.String)
            {
                var mode = capture.GetString()!.ToLowerInvariant();
                if (mode is "gdi" or "dxgi") environment["captureMode"] = mode;
            }
            evidence["diagnosticSource"] = evidence.Count > 0 ? "terminal_details" : "unavailable";
            return evidence;
        }
        return ReadLegacyFailureEvidence(log, code);
    }

    private static JsonObject ProjectDetails(JsonElement source)
    {
        var result = new JsonObject();
        foreach (var limit in Contract["evidenceLimits"]!.AsObject())
            if (source.TryGetProperty(limit.Key, out var value) && value.TryGetBoundedInteger(limit.Value!.GetValue<long>(), out var number))
                result[limit.Key] = number;
        foreach (var item in Contract["evidenceBooleans"]!.AsArray())
        {
            var key = item!.GetValue<string>();
            if (source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result[key] = value.GetBoolean();
        }
        foreach (var entry in Contract["evidenceEnums"]!.AsObject())
            if (entry.Key != "diagnosticSource") CopyChoice(source, result, entry.Key, entry.Value!.AsArray());
        CopyChoice(source, result, "firstMissingRoi", Contract["missingRois"]!.AsArray());
        CopyChoice(source, result, "targetVerificationKind", Contract["targetVerificationKinds"]!.AsArray());
        return result;
    }

    private static void CopyChoice(JsonElement source, JsonObject target, string key, JsonArray allowed)
    {
        if (source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            allowed.Any(item => item!.GetValue<string>() == value.GetString())) target[key] = value.GetString();
    }

    private static JsonObject ReadLegacyFailureEvidence(string log, string code)
    {
        var evidence = new JsonObject();
        var eventNames = code switch
        {
            "panel_capture_timeout" => "PANEL_CAPTURE_TIMEOUT",
            "scan_navigation_failed" => "TRAVERSAL_POSITION_LOST|EDGE_CLICK_STOP|RESET_TOP_FAILED",
            "warehouse_context_lost" => "WAREHOUSE_INPUT_GUARD_CONFIRM",
            "duplicate_guard" => "DUPLICATE_GUARD_STOP",
            _ => "(?!)"
        };
        var stops = Regex.Matches(log, $@"^(?:\[[^\]\r\n]+\][ \t]*)?EVENT #\d+ (?<event>{eventNames}):[ \t]*(?<facts>[^\r\n]*)\r?$", RegexOptions.Multiline);
        if (stops.Count > 0)
        {
            var stop = stops[^1];
            var facts = stop.Groups["facts"].Value;
            // Duplicate/OCR records must never take row fields from the producer.
            if (code == "duplicate_guard") facts = RestrictLegacyDuplicate(facts);
            foreach (var limit in Contract["evidenceLimits"]!.AsObject())
                if (Number(facts, Regex.Escape(limit.Key), limit.Value!.GetValue<long>()) is long value)
                {
                    if (limit.Key == "itemIndex") evidence[limit.Key] = (int)value;
                    else evidence[limit.Key] = value;
                }
            foreach (var item in Contract["evidenceBooleans"]!.AsArray())
            {
                var key = item!.GetValue<string>();
                if (bool.TryParse(Token(facts, Regex.Escape(key)), out var value)) evidence[key] = value;
            }
            foreach (var entry in Contract["evidenceEnums"]!.AsObject())
                if (entry.Key != "diagnosticSource") CopyLegacyChoice(facts, evidence, entry.Key, entry.Value!.AsArray());
            CopyLegacyChoice(facts, evidence, "firstMissingRoi", Contract["missingRois"]!.AsArray());
            CopyLegacyChoice(facts, evidence, "targetVerificationKind", Contract["targetVerificationKinds"]!.AsArray());
            Pair(facts, "visibleRois", "visibleRois", "totalRois", evidence);
            Pair(facts, "stableFrames", "stableFrames", "requiredStableFrames", evidence);
            Pair(facts, "scannedRows", "scannedRows", "totalRows", evidence);
            Pair(facts, "tick", "tick", "maximumTicks", evidence);
            var phase = stop.Groups["event"].Value switch
            {
                "TRAVERSAL_POSITION_LOST" => "traversal_position", "EDGE_CLICK_STOP" => "edge_click",
                "RESET_TOP_FAILED" => "reset_to_top", "PANEL_CAPTURE_TIMEOUT" => "capture", _ => null
            };
            if (phase is not null) evidence["phase"] = phase;
            if (phase == "traversal_position") ReadLegacyPosition(facts, evidence);
            if (code == "panel_capture_timeout") ReadLegacyPanelPosition(log, stop.Index + stop.Length, evidence);
        }
        else if (code == "duplicate_guard")
        {
            var legacy = Regex.Matches(log, @"^(?:\[[^\]\r\n]+\][ \t]*)?Duplicate guard canceled scan at #(\d+):[^\r\n]*\r?$", RegexOptions.Multiline);
            if (legacy.Count > 0 && int.TryParse(legacy[^1].Groups[1].Value, out var index) && index is >= 0 and <= 100000)
                evidence["itemIndex"] = index;
        }
        evidence["diagnosticSource"] = evidence.Count > 0 ? "legacy_log" : "unavailable";
        return evidence;
    }

    private static void CopyLegacyChoice(string facts, JsonObject evidence, string key, JsonArray allowed)
    {
        // Require a full token. "known_reason.private" must not become a known reason.
        var value = Regex.Match(facts, $@"(?:^|[\s,]){Regex.Escape(key)}=([A-Za-z0-9_]+)(?=[,\s]|$)").Groups[1].Value;
        if (allowed.Any(item => item!.GetValue<string>() == value)) evidence[key] = value;
    }

    private static void ReadLegacyPosition(string facts, JsonObject evidence)
    {
        if (!bool.TryParse(Token(facts, "found"), out var found)) return;
        evidence["positionFound"] = found;
        foreach (var prefix in new[] { "expected", "actual" })
        {
            if (prefix == "actual" && !found) continue;
            var thumb = Regex.Match(facts, $@"\b{prefix}Thumb=(\d+)-(\d+)(?=[,\s]|$)");
            foreach (var (suffix, group) in new[] { ("Start", 1), ("End", 2) })
                if (thumb.Success && int.TryParse(thumb.Groups[group].Value, out var value) && value <= 20000)
                    evidence[$"{prefix}Thumb{suffix}"] = value;
        }
    }

    private static string RestrictLegacyDuplicate(string facts)
    {
        var index = Regex.Match(facts, @"(?:^|[\s,])itemIndex=(\d+)(?=[,\s]|$)").Groups[1].Value;
        var kind = Regex.Match(facts, @"(?:^|[\s,])targetVerificationKind=([A-Za-z0-9_]+)(?=[,\s]|$)").Groups[1].Value;
        return $"itemIndex={index}, targetVerificationKind={kind}";
    }

    private static void ReadLegacyPanelPosition(string log, int afterStop, JsonObject evidence)
    {
        // Older terminal exceptions recorded this fixed prefix immediately after the
        // timeout event. Do not borrow position fields from arbitrary later events.
        var tail = log[afterStop..Math.Min(log.Length, afterStop + 8192)];
        var location = Regex.Match(tail, @"(?:StalePanel:|详情面板截图等待超时：)[ \t]*logicalRow=(\d+|unknown),[ \t]*visualRow=(\d+),[ \t]*col=(\d+)/(\d+)");
        if (!location.Success) return;
        foreach (var (key, group) in new[] { ("logicalRow", 1), ("visualRow", 2), ("column", 3), ("maxColumns", 4) })
            if (int.TryParse(location.Groups[group].Value, out var value) && value >= 0 && value <= Contract["evidenceLimits"]![key]!.GetValue<int>()) evidence[key] = value;
    }
}

internal static class DiagnosticJsonNumber
{
    internal static bool TryGetBoundedInteger(this JsonElement value, long maximum, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number >= 0 && number <= maximum;
    }
}
