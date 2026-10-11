using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZZZScannerHelper;

internal static partial class Program
{
    // Production player transport: HTTP/SSE owns the verified fork CLI directly.
    // The historical BrowserSession/ManagedScannerProcess remains audit-only and is not called.
    private sealed partial class DirectForkHttpHelperServer : IDisposable
    {
        private const int Port = 43127;
        private readonly HttpListener _listener = new();
        private readonly object _gate = new();
        private readonly ScannerPairingStore _pairings = new();
        private readonly List<HttpListenerResponse> _eventClients = [];
        private readonly Dictionary<HttpListenerResponse, SemaphoreSlim> _eventWriteGates = [];
        private readonly DirectForkJobSlot<Process> _jobs;
        private readonly string? _launchOrigin;
        private JsonObject _snapshot = InitialSnapshot();
        private string? _resultPath;
        private string? _resultHandle;
        private string? _evidenceRoot;
        private Task _initialization = Task.CompletedTask;

        internal DirectForkHttpHelperServer(string? launchOrigin)
        {
            _jobs = new DirectForkJobSlot<Process>(_gate);
            _launchOrigin = IsAllowedOrigin(launchOrigin) ? launchOrigin : null;
        }

        internal async Task RunAsync(CancellationToken token)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            HelperInstallationManager.CompletePendingUpdate();
            _initialization = Task.Run(() =>
            {
                RefreshRuntime();
                RecoverCompletedResult();
            }, token);
            _ = SendHeartbeatsAsync(token);
            while (!token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(token);
                _ = Task.Run(() => HandleAsync(context, token), token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            var origin = context.Request.Headers["Origin"];
            AddCors(context.Response, origin);
            try
            {
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.StatusCode = IsAllowedOrigin(origin) ? 204 : 403;
                    context.Response.Close();
                    return;
                }
                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (context.Request.HttpMethod == "GET" && (path == "/" || path == "/identity"))
                {
                    await JsonAsync(context.Response, 200, new JsonObject { ["service"] = ServiceName, ["version"] = HelperVersion, ["installerVersion"] = ScannerInstallerIdentity.ReadVersion(), ["protocolVersion"] = ProtocolVersion, ["transport"] = "direct-fork-http", ["accountWriteEnabled"] = false, ["importAccess"] = false }, token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/token")
                {
                    var configuredHttps = ScannerOriginPolicy.IsTrustedHttpsOrigin(origin,
                        ScannerOriginPolicy.ReadAdditionalOrigin());
                    if (!IsAllowedOrigin(origin) || (!configuredHttps && _launchOrigin is not null &&
                        !string.Equals(_launchOrigin, origin, StringComparison.OrdinalIgnoreCase)))
                    {
                        await JsonAsync(context.Response, 403, new JsonObject { ["error"] = "origin_not_allowed" }, token);
                        return;
                    }
                    // The player already initiated pairing on the trusted site. Keep the
                    // exact-origin gate and short-lived token without a second Windows dialog.
                    var issued = _pairings.Issue(origin!, DateTimeOffset.UtcNow);
                    await JsonAsync(context.Response, 200, new JsonObject { ["token"] = issued }, token);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin))
                {
                    await JsonAsync(context.Response, 403, new JsonObject { ["error"] = "origin_not_allowed" }, token);
                    return;
                }
                if (!Authorized(context.Request))
                {
                    await JsonAsync(context.Response, 401, new JsonObject { ["error"] = "invalid_session_token" }, token);
                    return;
                }

                if (context.Request.HttpMethod == "POST" && path == "/api/revoke")
                {
                    _pairings.Revoke(context.Request.Headers["X-Soda-Scanner-Token"]);
                    lock (_gate)
                    {
                        foreach (var client in _eventClients) try { client.Close(); } catch { }
                        _eventClients.Clear();
                        _eventWriteGates.Clear();
                    }
                    await JsonAsync(context.Response, 200, new JsonObject { ["revoked"] = true }, token);
                    return;
                }

                if (context.Request.HttpMethod == "GET" && path == "/api/health")
                {
                    await JsonAsync(context.Response, 200, new JsonObject { ["ok"] = true, ["binding"] = $"127.0.0.1:{Port}", ["accountWriteEnabled"] = false, ["importAccess"] = false }, token);
                    return;
                }
                if (context.Request.HttpMethod == "GET" && path == "/api/snapshot")
                {
                    await NodeAsync(context.Response, 200, Snapshot(), token);
                    return;
                }
                if (context.Request.HttpMethod == "GET" && path == "/api/events")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.SendChunked = true;
                    lock (_gate) _eventClients.Add(context.Response);
                    await EventAsync(context.Response, Snapshot(), token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/api/retry")
                {
                    await _initialization.WaitAsync(token);
                    RefreshRuntime();
                    await NodeAsync(context.Response, 200, Snapshot(), token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/api/start")
                {
                    await _initialization.WaitAsync(token);
                    StartDirectFork();
                    await NodeAsync(context.Response, 202, Snapshot(), token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path is "/api/pause" or "/api/stop")
                {
                    await _initialization.WaitAsync(token);
                    StopDirectFork();
                    await NodeAsync(context.Response, 200, Snapshot(), token);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/api/result")
                {
                    var current = CaptureResult();
                    if (current is null)
                    {
                        await JsonAsync(context.Response, 409, new JsonObject { ["error"] = "result_not_ready" }, token);
                        return;
                    }
                    await JsonAsync(context.Response, 200, new JsonObject { ["resultFileHandle"] = current.Handle, ["resultStatus"] = current.Status, ["accountWriteEnabled"] = false, ["importAccess"] = false, ["preflight"] = "not_run", ["arm"] = "not_run", ["import"] = "not_run" }, token);
                    return;
                }
                var match = Regex.Match(path, "^/api/result/([^/]+)(?:/evidence/([^/]+)(?:/(detail|card))?)?$");
                if (context.Request.HttpMethod == "GET" && match.Success)
                {
                    var handle = Uri.UnescapeDataString(match.Groups[1].Value);
                    var current = CaptureResult();
                    if (current is null || !string.Equals(handle, current.Handle, StringComparison.Ordinal))
                    {
                        await JsonAsync(context.Response, 409, new JsonObject { ["error"] = "result_handle_invalid" }, token);
                        return;
                    }
                    if (!match.Groups[2].Success)
                    {
                        await NodeAsync(context.Response, 200, BrowserScopedStaging(current), token);
                        return;
                    }
                    var itemId = Uri.UnescapeDataString(match.Groups[2].Value);
                    if (!match.Groups[3].Success)
                    {
                        var item = ResultItem(current.Path, itemId);
                        var encodedHandle = Uri.EscapeDataString(handle);
                        var encodedItem = Uri.EscapeDataString(itemId);
                        await JsonAsync(context.Response, 200, new JsonObject { ["availability"] = "available", ["detailSrc"] = $"/api/result/{encodedHandle}/evidence/{encodedItem}/detail", ["cardSrc"] = $"/api/result/{encodedHandle}/evidence/{encodedItem}/card", ["visualDetailHash"] = item["evidence"]?["visualDetailHash"]?.GetValue<string>() ?? "" }, token);
                        return;
                    }
                    await EvidenceAsync(context.Response, current, itemId, match.Groups[3].Value, token);
                    return;
                }
                await JsonAsync(context.Response, 404, new JsonObject { ["error"] = "not_found" }, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HelperLog.RecordException("direct_fork_helper_request", "helper", ex);
                if (context.Response.OutputStream.CanWrite) await JsonAsync(context.Response, 500, new JsonObject { ["error"] = "helper_request_failed" }, token);
            }
        }

        private bool Authorized(HttpListenerRequest request)
        {
            return _pairings.Authorized(request.Headers["X-Soda-Scanner-Token"],
                request.Headers["Origin"], DateTimeOffset.UtcNow);
        }

        private void RefreshRuntime()
        {
            if (_jobs.IsBusy) return;
            try
            {
                // File verification can be slow. Never hold the task gate during it.
                var runtime = SodaRuntimeLocator.Load();
                lock (_gate)
                {
                    if (_jobs.IsBusy) return;
                    _snapshot["state"] = _resultPath is null ? "ready" : "completed";
                    _snapshot["permission"] = "checking";
                    _snapshot["distribution"] = new JsonObject { ["state"] = "ready", ["installedVersion"] = runtime.Version, ["targetVersion"] = runtime.Version, ["progressPercent"] = null, ["action"] = "open", ["message"] = "1.0.49 Soda fork、PP-OCRv6 与 R4 已完成文件校验。" };
                    _snapshot["connectedVersions"] = new JsonObject { ["installer"] = ScannerInstallerIdentity.ReadVersion(), ["runtime"] = runtime.Version, ["scanner"] = runtime.CaptureVersion };
                    _snapshot["error"] = null;
                }
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    if (_jobs.IsBusy) return;
                    _snapshot["state"] = "connection_failed";
                    _snapshot["permission"] = "denied";
                    _snapshot["distribution"] = new JsonObject { ["state"] = "repair_required", ["action"] = "repair", ["message"] = "组件文件校验失败；不会回退旧核心。" };
                    _snapshot["connectedVersions"] = new JsonObject { ["installer"] = ScannerInstallerIdentity.ReadVersion() };
                    _snapshot["error"] = new JsonObject { ["userMessage"] = "扫描组件文件缺失或校验失败，请修复安装。", ["recoveryAction"] = "retry", ["diagnosticCode"] = "runtime_repair_required" };
                    RuntimeDiagnosticFailure();
                }
            }
            Publish();
        }

        private void StartDirectFork()
        {
            var job = _jobs.Reserve();
            Process? process = null;
            try
            {
                if (!BeginDiagnosticAttempt(job)) return;
                var runtime = LoadDirectForkRuntime();
                try { _runtimeLeases[job] = SodaRuntimeFileLease.Acquire(runtime); }
                catch { throw new ScannerRuntimeFailureException("扫描组件文件缺失或校验失败，请修复安装。", "runtime_repair_required"); }
                var outputRoot = Path.Combine(SodaRuntimeLocator.DefaultInstallRoot(), "outputs", $"live-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{job}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(outputRoot);
                var start = new ProcessStartInfo { FileName = runtime.EntryPath, WorkingDirectory = runtime.Root, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in DirectForkScanArguments(runtime, outputRoot)) start.ArgumentList.Add(argument);
                if (!_jobs.Publish(job, () =>
                {
                    DirectForkDiagnostics.ApplyRuntime(_snapshot["diagnostics"]!.AsObject(), runtime);
                    _snapshot["diagnostics"]!["stage"] = "permission";
                    _resultPath = null;
                    _resultHandle = null;
                    _evidenceRoot = null;
                    _snapshot["state"] = "awaiting_elevation";
                    _snapshot["summary"] = null;
                    _snapshot["error"] = null;
                    _snapshot["progress"] = new JsonObject { ["processed"] = 0, ["total"] = null, ["stageLabel"] = "正在等待 Windows 权限确认并切换游戏", ["etaSeconds"] = null };
                    Publish();
                })) return;
                // UAC may block. Cancellation holds this reservation until the
                // prompt returns and any late elevated child actually exits.
                process = Process.Start(start) ?? throw new InvalidOperationException("direct_fork_start_failed");
                if (!_jobs.Attach(job, process))
                {
                    RequestForkTermination(process);
                    _ = Task.Run(() => ReleaseForkAfterExitAsync(process, job));
                    return;
                }
                HelperLog.Write($"DIRECT_FORK_STARTED pid={process.Id} runtime={runtime.Version}");
                _jobs.Publish(job, () =>
                {
                    _snapshot["state"] = "scanning";
                    _snapshot["permission"] = "granted";
                    _snapshot["diagnostics"]!["stage"] = "preflight";
                    _snapshot["progress"]!["stageLabel"] = "正在读取并识别驱动盘";
                    Publish();
                });
                _ = Task.Run(() => MonitorAsync(process, outputRoot, job));
            }
            catch (Exception ex)
            {
                PublishJobFailure(job, ex, complete: process is null, launching: process is null);
                if (process is not null)
                {
                    RequestForkTermination(process);
                    _ = Task.Run(() => ReleaseForkAfterExitAsync(process, job));
                }
                throw;
            }
            finally
            {
                // No process was launched (including UAC cancellation). It is
                // now safe to release a stopped pending reservation.
                if (process is null)
                {
                    if (_runtimeLeases.TryRemove(job, out var lease)) lease.Dispose();
                    _jobs.Complete(job, () => { });
                }
            }
        }

        private async Task MonitorAsync(Process process, string outputRoot, long job)
        {
            var terminationRequested = false;
            try
            {
                var clock = Stopwatch.StartNew();
                var watchdog = new DirectForkProgressWatchdog();
                var exited = process.WaitForExitAsync();
                while (!exited.IsCompleted)
                {
                    if (!_jobs.Publish(job, () => { })) return;
                    PublishScanProgress(outputRoot, job);
                    DirectForkProgressTimeout? timeout = null;
                    if (!exited.IsCompleted && !_jobs.Publish(job, () =>
                    {
                        timeout = watchdog.Observe(_snapshot, clock.ElapsedMilliseconds);
                        if (timeout is null) return;
                        if (_snapshot["diagnostics"] is JsonObject report)
                        {
                            var evidence = report["evidence"]!.AsObject();
                            DirectForkLifecycleDiagnostics.Record(report, "native_progress_unavailable", timeout.Phase);
                            evidence["elapsedMs"] = timeout.ElapsedMs;
                            evidence["timeoutMs"] = timeout.TimeoutMs;
                        }
                    })) return;
                    if (timeout is not null)
                    {
                        // Termination is only a request. Both successful and
                        // rejected requests retain ownership until actual exit.
                        var terminationFailure = RequestForkTermination(process);
                        terminationRequested = true;
                        throw new ScannerRuntimeFailureException(timeout.Code == "scan_start_timeout"
                            ? terminationFailure is not null ? "扫描未能开始，已停止等待。请先关闭画面扫描，再重新连接；也可反馈诊断。" : "扫描未能开始，已停止等待。请等待画面扫描结束后重试；若仍无反应，可反馈诊断。"
                            : terminationFailure is not null ? "扫描长时间没有推进，已停止等待。请先关闭画面扫描，再重新连接；也可反馈诊断。" : "扫描长时间没有推进，已停止等待。请等待画面扫描结束后重试；若再次中断，可反馈诊断。", timeout.Code);
                    }
                    await Task.WhenAny(exited, Task.Delay(500));
                }
                await exited;
                PublishScanProgress(outputRoot, job);
                if (!_jobs.Publish(job, () => { })) return;
                _jobs.Publish(job, () =>
                {
                    if (_snapshot["diagnostics"] is JsonObject report) report["evidence"]!["exitCode"] = unchecked((uint)process.ExitCode);
                });
                var result = DirectForkTaskOutput.FileFor(outputRoot, "scan-once-result.json");
                var terminalJson = DirectForkLifecycleDiagnostics.ReadTerminal(result, out var terminalFailure);
                if (terminalFailure is not null) _jobs.Publish(job, () =>
                {
                    DirectForkLifecycleDiagnostics.Record(_snapshot["diagnostics"]!.AsObject(), terminalFailure);
                });
                if (process.ExitCode != 0)
                {
                    if (terminalJson is not null)
                    {
                        using var failedRun = JsonDocument.Parse(terminalJson);
                        var scannerError = failedRun.RootElement.TryGetProperty("Error", out var errorNode) ? errorNode.GetString() : null;
                        if (scannerError?.Contains("未找到游戏窗口进程", StringComparison.Ordinal) == true)
                            throw new ScannerRuntimeFailureException("请先启动绝区零并保持游戏窗口可用，然后重新扫描。", "game_process_not_found");
                        if (scannerError?.Contains("未发现选中的 S 级驱动盘", StringComparison.Ordinal) == true)
                            throw new ScannerRuntimeFailureException("没有可导入的 S 级驱动盘；本次未生成导入结果。", "direct_fork_terminal_failed");
                    }
                    throw new ScannerRuntimeFailureException("扫描器已退出，结果未进入正式导入。", $"direct_fork_exit_{unchecked((uint)process.ExitCode):X8}");
                }
                var staging = DirectForkTaskOutput.FileFor(outputRoot, "scanner-r10c-r4-staging.json");
                if (staging is null || terminalJson is null) throw new InvalidDataException("direct_fork_result_missing");
                using var run = JsonDocument.Parse(terminalJson);
                if (!DirectForkTaskOutput.MatchesTerminal(outputRoot, run.RootElement)) throw new InvalidDataException("direct_fork_result_identity_mismatch");
                if (!run.RootElement.GetProperty("Success").GetBoolean() || run.RootElement.GetProperty("Failed").GetInt32() != 0) throw new InvalidDataException("direct_fork_partial");
                SetCompleted(staging, Path.Combine(Path.GetDirectoryName(staging)!, "r4-evidence"), job);
            }
            catch (Exception ex)
            {
                PublishJobFailure(job, ex, complete: false);
                if (!terminationRequested) RequestForkTermination(process);
            }
            finally { await ReleaseForkAfterExitAsync(process, job); }
        }

        private void PublishScanProgress(string outputRoot, long job)
        {
            try
            {
                var log = DirectForkTaskOutput.FileFor(outputRoot, "scan.log");
                var terminal = DirectForkTaskOutput.FileFor(outputRoot, "scan-once-result.json");
                int? total;
                lock (_gate) total = _snapshot["progress"]?["total"]?.GetValue<int>();
                // A partial/invalid sidecar must not hide readable log counters.
                // Classify a bad sidecar only after actual exit; writing is transient.
                var terminalJson = DirectForkLifecycleDiagnostics.ReadTerminal(terminal, out _);
                var update = DirectForkProgress.Read(log is null ? "" : DirectForkProgress.ReadLogFile(log), terminalJson, total);
                if (update is null) return;
                _jobs.Publish(job, () =>
                {
                    _snapshot["state"] = update["state"]!.DeepClone();
                    _snapshot["permission"] = "granted";
                    _snapshot["progress"] = update["progress"]!.DeepClone();
                    _snapshot["error"] = update["error"]?.DeepClone();
                    if (_snapshot["diagnostics"] is JsonObject report && update["diagnostics"] is JsonObject facts) DirectForkDiagnostics.Merge(report, facts);
                    Publish();
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // The fork can still be writing its current log or terminal sidecar.
            }
        }

        private void FailJob(long job, Exception ex) => PublishJobFailure(job, ex, complete: true);

        private void PublishJobFailure(long job, Exception ex, bool complete, bool launching = false)
        {
            void PublishFailure()
            {
                var reportedError = _snapshot["state"]?.GetValue<string>() == "connection_failed" ? _snapshot["error"]?.DeepClone() : null;
                _snapshot["state"] = "connection_failed";
                var failure = ex as ScannerRuntimeFailureException;
                var code = launching ? failure?.DiagnosticCode ?? DirectForkLifecycleDiagnostics.StartFailureCode(ex)
                    : ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "elevation_cancelled"
                    : ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException ? "permission_denied"
                    : failure?.DiagnosticCode ?? (ex.Message.StartsWith("soda_runtime_", StringComparison.Ordinal) ? "runtime_repair_required" : ex.Message);
                // A known terminal event in scan.log is useful even when a crash
                // prevented scan-once-result.json from being written.
                var observedCode = _snapshot["diagnostics"]?["code"]?.GetValue<string>();
                if (!launching && observedCode is not null and not "unknown" and not "none" and not "scanner_failure") code = observedCode;
                _snapshot["error"] = reportedError ?? new JsonObject
                {
                    ["userMessage"] = failure?.UserMessage ?? (code == "scanner_start_failed" ? "画面扫描未能启动，请修复安装后重试；仍失败时可反馈诊断。" : "扫描器运行失败，结果未进入正式导入。"),
                    ["recoveryAction"] = "retry",
                    ["diagnosticCode"] = DirectForkDiagnostics.Code(code)
                };
                if (_snapshot["diagnostics"] is JsonObject report)
                {
                    if (launching && code == "scanner_start_failed") DirectForkLifecycleDiagnostics.Record(report, "fork_start_failed", "launch");
                    DirectForkDiagnostics.Finish(report, "failed", reportedError?["diagnosticCode"]?.GetValue<string>() ?? code, DiagnosticElapsedMilliseconds());
                }
                Publish();
            }
            if (complete) _jobs.Complete(job, PublishFailure);
            else _jobs.Publish(job, PublishFailure);
        }

        internal sealed class ScannerRuntimeFailureException(string userMessage, string diagnosticCode) : Exception(diagnosticCode)
        {
            internal string UserMessage { get; } = userMessage;
            internal string DiagnosticCode { get; } = diagnosticCode;
        }

        private void StopDirectFork()
        {
            var running = _jobs.Stop(() =>
            {
                if (_snapshot["state"]?.GetValue<string>() is "checking" or "awaiting_elevation" or "scanning" && _snapshot["diagnostics"] is JsonObject report)
                    DirectForkDiagnostics.Finish(report, "cancelled", "none", DiagnosticElapsedMilliseconds());
                _resultPath = null;
                _resultHandle = null;
                _evidenceRoot = null;
                _snapshot["summary"] = null;
                _snapshot["state"] = _jobs.IsBusy ? "scanning" : "ready";
                if (_jobs.IsBusy)
                {
                    _snapshot["progress"] ??= new JsonObject { ["processed"] = 0, ["total"] = null };
                    _snapshot["progress"]!["stageLabel"] = "正在结束画面扫描，请稍候；若 Windows 权限提示仍在，请取消。";
                    _snapshot["progress"]!["etaSeconds"] = null;
                }
                else _snapshot["progress"] = null;
                _snapshot["error"] = null;
                Publish();
            }, () =>
            {
                _snapshot["state"] = "ready";
                _snapshot["progress"] = null;
                Publish();
            });
            if (running is not null) RequestForkTermination(running);
        }


        private string SummaryString(string name) => _snapshot["summary"]![name]!.GetValue<string>();
        private JsonObject Snapshot() { lock (_gate) return _snapshot.DeepClone().AsObject(); }

        public void Dispose()
        {
            try { StopDirectFork(); } catch { }
            lock (_gate) foreach (var response in _eventClients) try { response.Close(); } catch { }
            if (_listener.IsListening) _listener.Stop(); _listener.Close();
        }
    }

    internal static async Task WriteDirectForkEventAsync(Stream stream, SemaphoreSlim gate, JsonNode value, CancellationToken token)
        => await WriteDirectForkFrameAsync(stream, gate, $"data: {value.ToJsonString()}\n\n", token);

    internal static async Task WriteDirectForkFrameAsync(Stream stream, SemaphoreSlim gate, string frame, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await gate.WaitAsync(token);
        try { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
        finally { gate.Release(); }
    }

    internal static JsonObject RecoverDirectForkResultForTests(string root)
    {
        using var server = new DirectForkHttpHelperServer(null);
        return server.RecoverCompletedResultForTests(root);
    }

    internal static string[] DirectForkScanArguments(SodaRuntimeIdentity runtime, string outputRoot) =>
        ["--scan-once", "--output-root", outputRoot, "--scan-directory", DirectForkTaskOutput.DirectoryFor(outputRoot), "--max-items", "0", "--rarities", "S", "--include-non15",
         "--capture-mode", "gdi", "--ocr-engine", "ppocrv6", "--ppocrv6-worker", runtime.PpOcrV6.WorkerPath,
         "--ppocrv6-model", runtime.PpOcrV6.ModelPath, "--ppocrv6-config", runtime.PpOcrV6.ConfigPath];

}
