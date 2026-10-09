using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZZZScannerHelper;

internal static class DirectForkProgress
{
    internal static string ReadLogFile(string path)
    {
        // Long scans produce large logs. Keep startup metadata and the latest
        // counters, without repeatedly allocating/regex-scanning the whole file.
        const int window = 65536;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length <= window * 2)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        var head = new byte[window];
        var tail = new byte[window];
        var headLength = stream.Read(head);
        stream.Seek(-window, SeekOrigin.End);
        var tailLength = stream.Read(tail);
        return System.Text.Encoding.UTF8.GetString(head, 0, headLength) + "\n" +
            System.Text.Encoding.UTF8.GetString(tail, 0, tailLength);
    }

    internal static string ReadFile(string path)
    {
        // The fork keeps its StreamWriter open throughout capture. Readers must
        // share writing as well as reading; File.ReadAllText otherwise fails on Windows.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Reuses established native log counters; no observed total means unknown.
    internal static JsonObject? Read(string log, string? terminalJson, int? previousTotal)
    {
        var totals = Regex.Matches(log, @"\b(?:inventoryCount|warehouseTotal)=(\d+)\b");
        if (totals.Count == 0) totals = Regex.Matches(log, @"\bexpectedTotal=(\d+)\b");
        int? total = totals.Count > 0 && int.TryParse(totals[^1].Groups[1].Value, out var observed) && observed > 0 ? observed : previousTotal;
        var counters = Regex.Matches(log, @"\bcompleted=(\d+)\b", RegexOptions.IgnoreCase);
        var visited = Regex.Matches(log, @"\bvisited=(\d+)\b", RegexOptions.IgnoreCase);
        int? completed = counters.Count > 0 ? int.Parse(counters[^1].Groups[1].Value) : null;
        int? processed = visited.Count > 0 ? int.Parse(visited[^1].Groups[1].Value) : completed;
        var diagnostics = DirectForkDiagnostics.Read(log, terminalJson, total);
        JsonObject? error = null;
        if (terminalJson is not null)
        {
            using var terminal = JsonDocument.Parse(terminalJson);
            var result = terminal.RootElement;
            completed = result.GetProperty("Completed").GetInt32();
            processed = result.TryGetProperty("Visited", out var visitedNode) ? visitedNode.GetInt32() : completed;
            if (!result.GetProperty("Success").GetBoolean())
            {
                // A visited cell may still be waiting for OCR. At failure the UI
                // and feedback both report completed recognition, keeping visited
                // separately in diagnostic counts instead of overstating results.
                processed = completed;
                var detail = result.TryGetProperty("Error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String ? errorNode.GetString() ?? "" : "";
                var panelTimeout = diagnostics["code"]?.GetValue<string>() == "panel_capture_timeout"
                    || log.Contains("terminationCode=panel_capture_timeout", StringComparison.Ordinal) || detail.Contains("StalePanel", StringComparison.Ordinal);
                var gameMissing = detail.Contains("未找到游戏窗口进程", StringComparison.Ordinal);
                var noSelected = detail.Contains("scan_no_importable_s_discs", StringComparison.Ordinal)
                    || detail.Contains("未发现选中的 S 级驱动盘", StringComparison.Ordinal);
                var contextCode = diagnostics["code"]?.GetValue<string>();
                var contextMessage = contextCode switch
                {
                    "game_window_not_foreground" => "游戏已离开前台。请回到驱动仓库后重新扫描，期间保持游戏在前台。",
                    "game_window_not_visible" => "游戏窗口不可见。请恢复窗口，完整显示驱动仓库后重新扫描。",
                    "window_geometry_changed" => "游戏窗口位置、大小或显示缩放发生变化。请将窗口放好并保持大小不变，再重新扫描。",
                    "ppocrv6_detail_geometry_incompatible" => "当前游戏画面尺寸不兼容。在游戏中选择 1920 × 1080 的窗口模式后重新扫描。",
                    "warehouse_context_lost" => "暂时无法确认驱动仓库画面。请关闭遮挡并保持游戏在前台，再重新扫描。",
                    "duplicate_guard" => "连续读到相同驱动盘，但无法确认已切换到下一张，扫描已保护性停止。本次结果未进入正式导入；保持驱动仓库在前台重试，若再次停止请反馈此问题。",
                    _ => null
                };
                error = new JsonObject
                {
                    ["userMessage"] = contextMessage ?? (noSelected ? "没有可导入的 S 级驱动盘；本次未生成导入结果。" : gameMissing ? "请先启动绝区零并保持游戏窗口可用，然后重新扫描。" : panelTimeout
                        ? $"扫描在驱动盘详情切换时超时，已识别 {completed} 张；请检查游戏窗口与盘面后重试，本次结果未进入正式导入。"
                        : $"扫描中断，已识别 {completed} 张；结果未进入正式导入，请检查游戏窗口后重新扫描。"),
                    // The terminal projection already validates native codes against the shared
                    // whitelist. Preserve a specific failure through finalization; legacy text
                    // heuristics and the generic failure are only fallbacks.
                    ["recoveryAction"] = "retry", ["diagnosticCode"] = contextCode is not null and not "none" and not "unknown"
                        ? contextCode : gameMissing ? "game_process_not_found" : panelTimeout ? "panel_capture_timeout" : "direct_fork_terminal_failed"
                };
            }
        }
        if (processed is null && total is null) return null;
        return new JsonObject
        {
            ["diagnostics"] = diagnostics,
            ["state"] = error is null ? "scanning" : "connection_failed", ["error"] = error,
            ["progress"] = new JsonObject
            {
                ["processed"] = processed ?? 0, ["total"] = total,
                ["stageLabel"] = error is null ? total.HasValue && processed >= total ? "列表已遍历，正在完成识别" : "正在读取并识别驱动盘" : "扫描已中断，保留已读取进度", ["etaSeconds"] = null
            }
        };
    }
}

// Completed fork results are the terminal state of the same progress stream.
internal static partial class Program
{
    private sealed partial class DirectForkHttpHelperServer
    {
        internal JsonObject RecoverCompletedResultForTests(string root)
        {
            RecoverCompletedResult(root);
            return Snapshot();
        }

        private void RecoverCompletedResult(string? testRoot = null)
        {
            JsonObject? recoveryProgress = null;
            try
            {
                var roots = testRoot is null ? new List<string> { Path.Combine(SodaRuntimeLocator.DefaultInstallRoot(), "outputs") } : new List<string> { testRoot };
                var explicitRoot = Environment.GetEnvironmentVariable("SODA_SCANNER_RESULT_SESSION_ROOT");
                if (testRoot is null && Environment.GetEnvironmentVariable("SODA_SCANNER_TEST_MODE") == "1" && !string.IsNullOrWhiteSpace(explicitRoot)) roots.Insert(0, Path.GetFullPath(explicitRoot));
                // Select the latest task, not the latest successful artifact. A new task
                // can fail before creating staging, and must not reveal an older success.
                var scanDirectory = roots.Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
                    .Where(directory => Path.GetFileName(directory).StartsWith("live-", StringComparison.Ordinal) ||
                        File.Exists(Path.Combine(directory, "scan.log")) || File.Exists(Path.Combine(directory, "scan-once-result.json")) ||
                        File.Exists(Path.Combine(directory, "scanner-r10c-r4-staging.json")))
                    .OrderByDescending(Directory.GetCreationTimeUtc).ThenByDescending(directory => directory.Length).FirstOrDefault();
                if (scanDirectory is not null)
                {
                    var staging = Path.Combine(scanDirectory, "scanner-r10c-r4-staging.json");
                    var resultPath = Path.Combine(scanDirectory, "scan-once-result.json");
                    var logPath = Path.Combine(scanDirectory, "scan.log");
                    var log = File.Exists(logPath) ? DirectForkProgress.ReadLogFile(logPath) : "";
                    recoveryProgress = DirectForkProgress.Read(log, null, null);
                    if (!File.Exists(resultPath)) throw new InvalidDataException("recovered_scan_completion_missing");
                    var terminalJson = File.ReadAllText(resultPath);
                    using var result = JsonDocument.Parse(terminalJson);
                    var terminal = result.RootElement;
                    var recordedRoot = terminal.GetProperty("OutputDirectory").GetString();
                    if (string.IsNullOrWhiteSpace(recordedRoot) || !string.Equals(Path.GetFullPath(recordedRoot), Path.GetFullPath(scanDirectory), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("recovered_scan_identity_mismatch");
                    if (terminal.TryGetProperty("Completed", out _)) recoveryProgress = DirectForkProgress.Read(log, terminalJson, null);
                    lock (_gate)
                    {
                        var report = DirectForkDiagnostics.Create();
                        DirectForkDiagnostics.Merge(report, DirectForkDiagnostics.Read(log, terminalJson, null));
                        _snapshot["diagnostics"] = report;
                    }
                    if (!terminal.GetProperty("Success").GetBoolean() || terminal.GetProperty("Failed").GetInt32() != 0 ||
                        terminal.GetProperty("Status").GetString() != "completed")
                        throw new InvalidDataException("recovered_scan_not_completed");
                    if (!File.Exists(staging)) throw new InvalidDataException("recovered_scan_staging_missing");
                    var selected = staging;
                    var descriptor = Path.Combine(scanDirectory, "scanner-r19-derived-result.v1.json");
                    if (File.Exists(descriptor))
                    {
                        using var lineage = JsonDocument.Parse(File.ReadAllText(descriptor));
                        var relative = lineage.RootElement.GetProperty("derived").GetProperty("relativeStagingPath").GetString();
                        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("derived_result_path_missing");
                        var expected = lineage.RootElement.GetProperty("derived").GetProperty("fileSha256").GetString()!;
                        var appRoot = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(scanDirectory)!.FullName)!.FullName)!.FullName)!.FullName;
                        var candidate = Path.GetFullPath(Path.Combine(appRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))).ToLowerInvariant();
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("derived_result_identity_mismatch");
                        selected = candidate;
                    }
                    SetCompleted(selected, Path.Combine(scanDirectory, "r4-evidence"));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
            {
                // A corrupt or unfinished previous scan must not prevent the HTTP listener
                // from starting. Keep its files for diagnosis, but never publish a result handle.
                lock (_gate)
                {
                    _resultPath = null;
                    _resultHandle = null;
                    _evidenceRoot = null;
                    _snapshot["state"] = "connection_failed";
                    _snapshot["summary"] = null;
                    _snapshot["progress"] = recoveryProgress?["progress"]?.DeepClone();
                    _snapshot["error"] = recoveryProgress?["error"]?.DeepClone() ?? new JsonObject
                    {
                        ["userMessage"] = "上次扫描结果未完整完成或恢复校验失败；文件已保留，请重新扫描。",
                        ["recoveryAction"] = "retry",
                        ["diagnosticCode"] = "previous_scan_recovery_failed"
                    };
                    var report = _snapshot["diagnostics"] as JsonObject ?? DirectForkDiagnostics.Create();
                    if (recoveryProgress?["diagnostics"] is JsonObject facts) DirectForkDiagnostics.Merge(report, facts);
                    DirectForkDiagnostics.Finish(report, "failed", _snapshot["error"]!["diagnosticCode"]!.GetValue<string>(), null);
                    _snapshot["diagnostics"] = report;
                }
                if (testRoot is null) HelperLog.Write($"DIRECT_FORK_RECOVERY_REJECTED type={ex.GetType().Name}");
                Publish();
            }
        }

        private void SetCompleted(string staging, string evidenceRoot, long? job = null)
        {
            var root = JsonNode.Parse(File.ReadAllText(staging)) as JsonObject ?? throw new InvalidDataException("result_object_missing");
            var items = root["items"] as JsonArray ?? throw new InvalidDataException("result_items_missing");
            var ready = items.Count(item => item?["state"]?.GetValue<string>() == "ready");
            var review = items.Count(item => item?["state"]?.GetValue<string>() == "needs_review");
            var invalid = items.Count - ready - review;
            var batchId = root["batch"]?["id"]?.GetValue<string>() ?? throw new InvalidDataException("result_batch_id_missing");
            void Commit()
            {
                _resultPath = staging;
                _evidenceRoot = evidenceRoot;
                _resultHandle = $"r21:{batchId}";
                _snapshot["state"] = "completed";
                _snapshot["permission"] = "granted";
                if (_snapshot["diagnostics"] is JsonObject report) DirectForkDiagnostics.Finish(report, "completed", "none", job is not null ? DiagnosticElapsedMilliseconds() : null);
                var seconds = (_snapshot["diagnostics"]?["durationMs"]?.GetValue<long>() ?? 0) / 1000.0;
                _snapshot["summary"] = new JsonObject { ["reliable"] = ready, ["needsReview"] = review, ["unreadable"] = invalid, ["resultFileHandle"] = _resultHandle, ["resultStatus"] = invalid > 0 ? "blocked_import" : review > 0 ? "needs_review" : "ready_for_review", ["uniqueRecords"] = items.Count, ["totalSeconds"] = seconds };
                var warehouseTotal = _snapshot["diagnostics"]?["counts"]?["total"]?.GetValue<int>() ?? items.Count;
                var visited = _snapshot["diagnostics"]?["counts"]?["visited"]?.GetValue<int>() ?? warehouseTotal;
                _snapshot["progress"] = new JsonObject { ["processed"] = visited, ["total"] = warehouseTotal, ["stageLabel"] = "等待玩家检查", ["etaSeconds"] = null };
                Publish();
            }
            if (job is long generation) _jobs.Complete(generation, Commit);
            else { lock (_gate) Commit(); }
        }

    }
}
