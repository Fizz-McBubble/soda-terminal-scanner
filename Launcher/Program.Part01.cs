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

    private sealed partial class HelperServer : IDisposable
    {

        public async Task EnsureScannerAsync(
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token,
            bool forceRepair = false,
            bool forceSelfContained = false)
        {
            await _ensureGate.WaitAsync(token);
            try
            {
                await report(new LauncherProgress { Stage = "manifest", Message = "正在获取扫描器版本清单..." }, token);
                var manifestUrl = ResolveManifestUrl();
                ScannerManifest manifest;
                try
                {
                    manifest = await DownloadManifestAsync(manifestUrl, token)
                        ?? throw new InvalidDataException("Scanner manifest is empty.");
                    HelperSecurity.ValidateManifest(manifest, manifestUrl, Version.Parse(HelperVersion));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not HelperFailureException)
                {
                    throw new HelperFailureException(
                        ex is HttpRequestException ? "manifest_unreachable" : "manifest_invalid",
                        "manifest",
                        ex is HttpRequestException ? "无法获取扫描器版本信息" : "扫描器版本信息无效",
                        ex.Message,
                        ex is HttpRequestException ? "请检查网络后重试。" : "请更新 Helper；如果问题持续，请打开日志。",
                        retryable: true,
                        innerException: ex);
                }

                _manifest = manifest;

                var environment = HelperPlatform.Inspect(manifest);
                _environment = environment;
                var runtimeRoot = RuntimeRootDirectory();
                var packageRoot = PackageCacheDirectory();
                HelperPlatform.EnsureWritableDirectory(runtimeRoot);
                HelperPlatform.EnsureWritableDirectory(packageRoot);
                var selection = forceSelfContained && manifest.SchemaVersion == 2
                    ? new PackageSelection(
                        manifest.Packages.Single(package => package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase)),
                        "desktop-runtime-disappeared")
                    : HelperPlatform.SelectPackage(manifest, environment);
                var package = selection.Package;
                _selectedPackage = package;
                var installRelative = Path.Combine(manifest.ScannerVersion, package.Id);
                var installDir = HelperSecurity.ResolvePathWithinRoot(runtimeRoot, installRelative);
                var entryPath = HelperSecurity.ResolvePathWithinRoot(installDir, package.Entry);
                var packagePath = HelperSecurity.ResolvePathWithinRoot(
                    packageRoot,
                    $"scanner-{manifest.ScannerVersion}-{package.Id}.zip");

                var selectionMessage = selection.Reason == "desktop-runtime-missing"
                    ? "未检测到 .NET 8 Desktop Runtime，正在使用兼容包，无需安装 .NET。"
                    : selection.Reason == "desktop-runtime-disappeared"
                        ? ".NET 8 在启动前不可用，正在自动切换兼容包。"
                    : selection.Reason == "desktop-runtime-available"
                        ? "已检测到 .NET 8 Desktop Runtime，使用精简包。"
                        : "正在使用兼容的旧版扫描器包。";
                await report(new LauncherProgress
                {
                    Stage = "select",
                    Message = selectionMessage,
                    Version = manifest.ScannerVersion,
                    PackageId = package.Id,
                    PackageMode = package.Mode,
                    SelectionReason = selection.Reason,
                    TotalBytes = package.Size,
                    RequiredBytes = checked(package.Size + package.ExpandedSize + 100L * 1024 * 1024)
                }, token);

                if (forceRepair)
                {
                    SafeDelete(packagePath);
                    SafeDelete(packagePath + ".download");
                    if (Directory.Exists(installDir))
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                }

                if (File.Exists(entryPath) && (manifest.SchemaVersion >= 3 || File.Exists(packagePath)))
                {
                    try
                    {
                        if (manifest.SchemaVersion >= 3)
                        {
                            await HelperSecurity.VerifyInstalledRuntimeAsync(package, installDir, token);
                        }
                        else
                        {
                            await VerifyPackageSizeAsync(packagePath, package.Size);
                            await VerifySha256Async(packagePath, package.Sha256, token);
                            await HelperSecurity.VerifyInstalledRuntimeAsync(packagePath, installDir, package.Entry, token);
                        }
                        _entryPath = entryPath;
                        await report(PackageProgress("ready", "扫描器已是最新版本。", manifest.ScannerVersion, package), token);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await report(new LauncherProgress
                        {
                            Stage = "repair",
                            Message = $"扫描器完整性校验失败，正在自动修复：{ex.Message}",
                            Version = manifest.ScannerVersion,
                            PackageId = package.Id,
                            PackageMode = package.Mode
                        }, token);
                    }
                }

                Directory.CreateDirectory(installDir);
                var packageReady = false;
                if (File.Exists(packagePath))
                {
                    try
                    {
                        await VerifyPackageSizeAsync(packagePath, package.Size);
                        await VerifySha256Async(packagePath, package.Sha256, token);
                        packageReady = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SafeDelete(packagePath);
                        await report(new LauncherProgress
                        {
                            Stage = "download",
                            Message = $"本地安装包校验失败，正在重新下载：{ex.Message}",
                            Version = manifest.ScannerVersion,
                            PackageId = package.Id,
                            PackageMode = package.Mode,
                            TotalBytes = package.Size
                        }, token);
                    }
                }

                if (!packageReady)
                {
                    HelperPlatform.EnsureDiskSpace(package, packageRoot);
                    await report(new LauncherProgress
                    {
                        Stage = "download",
                        Message = "正在下载 OCR 扫描器...",
                        Version = manifest.ScannerVersion,
                        PackageId = package.Id,
                        PackageMode = package.Mode,
                        TotalBytes = package.Size
                    }, token);
                    await DownloadAndVerifyPackageAsync(manifestUrl, manifest.ScannerVersion, package, packagePath, report, token);
                }

                await report(PackageProgress("checksum", "正在校验扫描器文件...", manifest.ScannerVersion, package), token);
                await VerifyPackageSizeAsync(packagePath, package.Size);
                await VerifySha256Async(packagePath, package.Sha256, token);

                var tempDir = HelperSecurity.ResolvePathWithinRoot(runtimeRoot, installRelative + ".tmp");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                Directory.CreateDirectory(tempDir);
                await report(PackageProgress("extract", "正在安装扫描器...", manifest.ScannerVersion, package), token);
                try
                {
                    ExtractZipSafe(packagePath, tempDir);
                    if (manifest.SchemaVersion >= 3)
                    {
                        await HelperSecurity.VerifyInstalledRuntimeAsync(package, tempDir, token);
                    }
                    else
                    {
                        await HelperSecurity.VerifyInstalledRuntimeAsync(packagePath, tempDir, package.Entry, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not HelperFailureException)
                {
                    throw new HelperFailureException(
                        ex is InvalidDataException ? "package_corrupt" : "install_failed",
                        "install",
                        ex is InvalidDataException ? "扫描器安装包内容损坏" : "扫描器安装失败",
                        ex.Message,
                        "请选择重新下载并修复；Helper 会清除临时文件后重新安装。",
                        retryable: true,
                        new Dictionary<string, string> { ["packageId"] = package.Id },
                        ex);
                }
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }

                Directory.Move(tempDir, installDir);
                if (manifest.SchemaVersion >= 3)
                {
                    SafeDelete(packagePath);
                }
                _entryPath = entryPath;
                await report(PackageProgress("ready", "扫描器准备完成。", manifest.ScannerVersion, package), token);
            }
            finally
            {
                _ensureGate.Release();
            }
        }

        public SodaRuntimeIdentity CurrentSodaRuntime()
        {
            if (_sodaRuntime is null || string.IsNullOrWhiteSpace(_entryPath) || !File.Exists(_entryPath))
            {
                throw new InvalidOperationException("Scanner runtime is not installed.");
            }

            return _sodaRuntime;
        }

        public bool ShouldFallbackToSelfContained()
        {
            return _manifest?.SchemaVersion == 2
                && _selectedPackage?.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase) == true
                && _selectedPackage.Framework is not null
                && !HelperPlatform.HasRequiredFramework(_selectedPackage.Framework);
        }

        private Uri ResolveManifestUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("ZZZ_SCANNER_DOWNLOAD_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = ResolveDownloadBaseFromLaunchOrigin();
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://localhost:8787";
            }

            var manifestUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ManifestPath.TrimStart('/'));
            HelperSecurity.EnsureTrustedDownloadUri(manifestUri, "manifest");
            return manifestUri;
        }

        private static LauncherProgress PackageProgress(string stage, string message, string version, ScannerPackage package)
        {
            return new LauncherProgress
            {
                Stage = stage,
                Message = message,
                Version = version,
                PackageId = package.Id,
                PackageMode = package.Mode,
                TotalBytes = package.Size
            };
        }

        private string? ResolveDownloadBaseFromLaunchOrigin()
        {
            if (!IsAllowedOrigin(_launchOrigin))
            {
                return null;
            }

            return _launchOrigin;
        }

        private async Task<ScannerManifest?> DownloadManifestAsync(Uri url, CancellationToken token)
        {
            using var response = await _http.GetAsync(url, token);
            HelperSecurity.EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? url, "manifest redirect");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync(stream, HelperJsonContext.Default.ScannerManifest, token);
        }
}
}
