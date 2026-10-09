using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using ZZZScannerNext.Interop;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private const string ServiceName = "soda-terminal-scanner-helper";
    internal const string HelperVersion = "2.3.10";
    internal const int ProtocolVersion = 5;
    private const int HelperPort = 43127;
    private const string ProtocolName = SodaProtocolRegistration.Scheme;
    private const string ManifestPath = "/downloads/zzz-scanner/manifest.json";
    private const int PackageDownloadMaxAttempts = 5;
    private const int DownloadProgressIntervalMs = 300;
    internal const int MaxWebSocketMessageBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            args = EnableDiagnosticConsole(args);
            var lifecycle = await HelperInstallationManager.PrepareAsync(args);
            if (lifecycle.Exit) return 0;
            if (lifecycle.Arguments is ["--install-protocol"])
            {
                SodaProtocolRegistration.Register(new CurrentUserProtocolStore(), Environment.ProcessPath!);
                return 0;
            }
            if (lifecycle.Arguments is ["--uninstall-protocol"])
            {
                return SodaProtocolRegistration.Unregister(new CurrentUserProtocolStore(), Environment.ProcessPath!) ? 0 : 2;
            }
            return await RunAsync(lifecycle.Arguments);
        }
        catch (Exception ex)
        {
            if (HelperInstallationManager.TryRollbackPendingUpdate())
            {
                return 1;
            }
            var error = HelperErrors.FromException(ex, "startup");
            ShowStartupError(error);
            return 1;
        }
    }

    private static string[] EnableDiagnosticConsole(string[] args)
    {
        if (!args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            return args;
        }

        AllocConsole();
        return args.Where(argument => !argument.Equals("--console", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var launchOrigin = TryReadLaunchOrigin(args);
        using var mutex = new Mutex(initiallyOwned: true, "Local\\ZZZScannerHelper", out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        ScannerChildReaper.CleanupOrphans(SodaRuntimeLocator.DefaultInstallRoot(), Environment.ProcessId);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var server = new DirectForkHttpHelperServer(launchOrigin);
        await server.RunAsync(cts.Token);
        return 0;
    }

    private static void ShowStartupError(HelperErrorMessage error)
    {
        var text = $"{error.Message}\n\n处理方法：{error.Remedy}\n诊断编号：{error.DiagnosticId}\n日志：{HelperLog.DirectoryPath}";
        try
        {
            MessageBox(IntPtr.Zero, text, error.Title, 0x10);
        }
        catch
        {
            Console.Error.WriteLine(text);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private static string? TryReadLaunchOrigin(string[] args)
    {
        if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Scheme.Equals(ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private sealed partial class HelperServer : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly HttpListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _ensureGate = new(1, 1);
        private readonly HelperStorageManager _storage = new();
        private ScannerManifest? _manifest;
        private ScannerPackage? _selectedPackage;
        private HelperEnvironmentSnapshot? _environment;
        private string? _entryPath;
        private string? _activeVersion;
        private string? _activePackageId;
        private string? _activePackageMode;
        private string? _launchOrigin;
        private bool _disposed;
        private SodaRuntimeIdentity? _sodaRuntime;

        public HelperServer(string? launchOrigin)
        {
            TryRefreshSodaRuntime();
            if (IsAllowedOrigin(launchOrigin))
            {
                _launchOrigin = launchOrigin;
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{HelperPort}/");
            _listener.Start();
            HelperInstallationManager.CompletePendingUpdate();
            while (!token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(token);
                _ = Task.Run(() => HandleContextAsync(context, token), token);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            var origin = context.Request.Headers["Origin"];
            AddCorsHeaders(context.Response, origin);
            try
            {
                if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = IsAllowedOrigin(origin) ? 204 : 403;
                    context.Response.Close();
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (context.Request.IsWebSocketRequest && path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(context, token);
                    return;
                }

                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/")
                {
                    if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin))
                    {
                        await SendTextAsync(context.Response, 403, "Bad Origin", token);
                        return;
                    }
                    await SendJsonAsync(context.Response, 200, HelperInfo(), token);
                    return;
                }

                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/token")
                {
                    await HandleTokenAsync(context, origin, token);
                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            catch (Exception ex)
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    await SendTextAsync(context.Response, 500, ex.Message, token);
                }
            }
        }

        private async Task HandleTokenAsync(HttpListenerContext context, string? originHeader, CancellationToken token)
        {
            var origin = originHeader;
            if (string.IsNullOrWhiteSpace(origin))
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(token);
                try
                {
                    origin = JsonSerializer.Deserialize(body, HelperJsonContext.Default.TokenRequest)?.Origin;
                }
                catch
                {
                }
            }

            if (!IsAllowedOrigin(origin))
            {
                await SendTextAsync(context.Response, 403, "Bad Origin", token);
                return;
            }

            _launchOrigin = origin;
            var tokenValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var now = DateTimeOffset.Now;
            foreach (var expiredToken in _tokens.Where(pair => pair.Value < now).Select(pair => pair.Key))
            {
                _tokens.TryRemove(expiredToken, out _);
            }

            _tokens[tokenValue] = now.AddMinutes(5);
            await SendJsonAsync(context.Response, 200, new TokenResponse { Token = tokenValue }, token);
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken token)
        {
            var origin = context.Request.Headers["Origin"];
            if (!IsAllowedOrigin(origin)
                || !string.Equals(origin, _launchOrigin, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var tokenValue = context.Request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(tokenValue)
                || !_tokens.TryRemove(tokenValue, out var expires)
                || expires < DateTimeOffset.Now)
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            await using var session = new BrowserSession(this, wsContext.WebSocket);
            await session.RunAsync(token);
        }

        private HelperInfoResponse HelperInfo()
        {
            return new HelperInfoResponse
            {
                Service = ServiceName,
                Version = HelperVersion,
                ProtocolVersion = ProtocolVersion,
                Scanner = CurrentScannerState(),
                HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
            };
        }

        public ScannerState CurrentScannerState()
        {
            return new ScannerState
            {
                Version = _activeVersion ?? _manifest?.ScannerVersion,
                Installed = !string.IsNullOrWhiteSpace(_entryPath) && File.Exists(_entryPath),
                Entry = _entryPath,
                PackageId = _activePackageId ?? _selectedPackage?.Id,
                PackageMode = _activePackageMode ?? _selectedPackage?.Mode,
                DesktopRuntimeAvailable = _environment?.DesktopRuntimeAvailable
            };
        }

        public async Task EnsureSodaScannerAsync(
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token)
        {
            try
            {
                RefreshSodaRuntime();
                await report(new LauncherProgress
                {
                    Stage = "ready",
                    Message = "Soda 扫描运行时已完成完整性校验。",
                    Version = _sodaRuntime!.Version,
                    PackageId = "soda-runtime",
                    PackageMode = "self-contained"
                }, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new HelperFailureException(
                    "soda_runtime_not_ready",
                    "runtime",
                    "扫描组件尚未准备好",
                    ex.Message,
                    "请从 Soda Terminal 扫描页重新安装组件后再打开助手。",
                    retryable: true,
                    innerException: ex);
            }
        }

        private void RefreshSodaRuntime()
        {
            _sodaRuntime = SodaRuntimeLocator.Load();
            _activeVersion = _sodaRuntime.Version;
            _activePackageId = "soda-runtime";
            _activePackageMode = "self-contained";
            _entryPath = _sodaRuntime.EntryPath;
        }

        private bool TryRefreshSodaRuntime()
        {
            try
            {
                RefreshSodaRuntime();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _sodaRuntime = null;
                _activeVersion = null;
                _activePackageId = null;
                _activePackageMode = null;
                _entryPath = null;
                HelperLog.Write($"SODA_RUNTIME_UNAVAILABLE code={ex.Message}");
                return false;
            }
        }

        public HelperStorageSnapshot CurrentStorageInfo()
        {
            return _storage.Inspect(_activeVersion, _activePackageId);
        }

        public HelperStorageCleanupResult CleanupStorage()
        {
            return _storage.Cleanup(_activeVersion, _activePackageId);
        }

        public void MarkScannerActive()
        {
            if (_sodaRuntime is not null) return;
            if (_manifest is null || _selectedPackage is null)
            {
                throw new InvalidOperationException("Scanner manifest and package selection are unavailable.");
            }
            var active = _storage.SaveActiveRuntime(
                _manifest.ScannerVersion,
                _selectedPackage.Id,
                _selectedPackage.Mode,
                _selectedPackage.Entry);
            _activeVersion = active.Version;
            _activePackageId = active.PackageId;
            _activePackageMode = active.PackageMode;
            _entryPath = active.EntryPath;
        }

        public Uri ResolveHelperManifestUrl()
        {
            return new Uri(ResolveManifestUrl(), "helper-manifest.json");
        }
}
}
