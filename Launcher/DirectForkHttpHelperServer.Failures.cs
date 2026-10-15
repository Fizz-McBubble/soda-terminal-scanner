using System.Text.Json.Nodes;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private sealed partial class DirectForkHttpHelperServer
    {
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

    }
}
