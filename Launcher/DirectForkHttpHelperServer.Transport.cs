using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private sealed partial class DirectForkHttpHelperServer
    {
        private sealed record CompletedResult(string Path, string Handle, string EvidenceRoot, string Status);

        private CompletedResult? CaptureResult()
        {
            lock (_gate)
                return _resultPath is null || _resultHandle is null || _evidenceRoot is null ? null :
                    new CompletedResult(_resultPath, _resultHandle, _evidenceRoot, SummaryString("resultStatus"));
        }

        private JsonObject BrowserScopedStaging(CompletedResult current)
        {
            var root = JsonNode.Parse(File.ReadAllText(current.Path))!.AsObject();
            foreach (var node in root["items"]!.AsArray())
            {
                var item = node!.AsObject();
                var evidence = item["evidence"]!.AsObject();
                var id = item["id"]!.GetValue<string>();
                evidence["detailPath"] = $"opaque:{current.Handle}:{id}:detail";
                evidence["cardPath"] = $"opaque:{current.Handle}:{id}:card";
            }
            var identity = new JsonArray(root["items"]!.AsArray().OrderBy(item => item!["sequence"]!.GetValue<int>()).Select(item =>
            {
                var source = item!.AsObject();
                var target = new JsonObject();
                foreach (var name in new[] { "id", "batchId", "sequence", "sourceIdentity", "duplicate", "fingerprint", "lockState", "candidate", "fields", "confirmations", "issues", "state", "evidence" }) target[name] = source[name]?.DeepClone();
                return (JsonNode)target;
            }).ToArray());
            var canonical = BrowserCanonicalJson(identity);
            root["batch"]!["manifest"]!["payloadHash"] = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return root;
        }

        private static JsonObject ResultItem(string path, string id) => JsonNode.Parse(File.ReadAllText(path))!["items"]!.AsArray().Select(node => node!.AsObject()).Single(item => item["id"]!.GetValue<string>() == id);

        private static async Task EvidenceAsync(HttpListenerResponse response, CompletedResult current, string itemId, string kind, CancellationToken token)
        {
            var source = ResultItem(current.Path, itemId)["evidence"]?[kind == "detail" ? "detailPath" : "cardPath"]?.GetValue<string>() ?? throw new InvalidDataException("result_evidence_unavailable");
            var path = Path.GetFullPath(source);
            var allowed = Path.GetFullPath(current.EvidenceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("result_evidence_out_of_scope");
            var bytes = await File.ReadAllBytesAsync(path, token);
            response.StatusCode = 200; response.ContentType = "image/png"; response.Headers["Cache-Control"] = "no-store"; response.Headers["X-Content-Type-Options"] = "nosniff"; response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, token); response.Close();
        }

        private void Publish()
        {
            HttpListenerResponse[] clients;
            JsonObject snapshot;
            lock (_gate) { clients = [.. _eventClients]; snapshot = Snapshot(); }
            foreach (var response in clients) _ = SendEventAsync(response, snapshot);
        }

        private async Task SendEventAsync(HttpListenerResponse response, JsonNode snapshot)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await EventAsync(response, snapshot, timeout.Token); }
            catch
            {
                lock (_gate) { _eventClients.Remove(response); _eventWriteGates.Remove(response); }
                try { response.Close(); } catch { }
            }
        }

        private async Task SendHeartbeatsAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    HttpListenerResponse[] clients;
                    lock (_gate) clients = [.. _eventClients];
                    foreach (var response in clients) _ = SendHeartbeatAsync(response, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task SendHeartbeatAsync(HttpListenerResponse response, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await WriteDirectForkFrameAsync(response.OutputStream, EventWriteGate(response), ": keepalive\n\n", timeout.Token); }
            catch
            {
                lock (_gate) { _eventClients.Remove(response); _eventWriteGates.Remove(response); }
                try { response.Close(); } catch { }
            }
        }

        private static JsonObject InitialSnapshot() => new()
        {
            ["state"] = "connecting",
            ["permission"] = "checking",
            ["readiness"] = new JsonObject { ["helperConnected"] = true, ["gameFrameReadable"] = false, ["accountWriteEnabled"] = false },
            ["config"] = new JsonObject { ["scopeLabel"] = "完整驱动盘仓库 · 当场读取数量", ["localOnly"] = true, ["reviewPolicyLabel"] = "领域证据不足时保留检查", ["safeStopAvailable"] = true }
        };

        private static void AddCors(HttpListenerResponse response, string? origin)
        {
            if (IsAllowedOrigin(origin)) response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin"; response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Soda-Scanner-Token"; response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"; response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
        private static async Task JsonAsync(HttpListenerResponse response, int status, JsonObject value, CancellationToken token) => await BytesAsync(response, status, Encoding.UTF8.GetBytes(value.ToJsonString()), token);
        private static async Task NodeAsync(HttpListenerResponse response, int status, JsonNode value, CancellationToken token) => await BytesAsync(response, status, Encoding.UTF8.GetBytes(value.ToJsonString()), token);
        private static async Task BytesAsync(HttpListenerResponse response, int status, byte[] bytes, CancellationToken token) { response.StatusCode = status; response.ContentType = "application/json; charset=utf-8"; response.Headers["Cache-Control"] = "no-store"; response.ContentLength64 = bytes.Length; await response.OutputStream.WriteAsync(bytes, token); response.Close(); }
        private Task EventAsync(HttpListenerResponse response, JsonNode value, CancellationToken token)
            => WriteDirectForkEventAsync(response.OutputStream, EventWriteGate(response), value, token);

        private SemaphoreSlim EventWriteGate(HttpListenerResponse response)
        {
            SemaphoreSlim gate;
            lock (_gate)
            {
                if (!_eventWriteGates.TryGetValue(response, out gate!)) _eventWriteGates[response] = gate = new SemaphoreSlim(1, 1);
            }
            return gate;
        }

    }
}
