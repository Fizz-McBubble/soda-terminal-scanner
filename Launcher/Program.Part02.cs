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

        private async Task DownloadFileAsync(
            Uri url,
            string destination,
            string version,
            ScannerPackage package,
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token)
        {
            var expectedSize = package.Size;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".download";

            var stopwatch = Stopwatch.StartNew();
            Exception? lastError = null;
            for (var attempt = 1; attempt <= PackageDownloadMaxAttempts; attempt++)
            {
                var existingBytes = File.Exists(temp) ? new FileInfo(temp).Length : 0L;
                if (expectedSize > 0 && existingBytes == expectedSize)
                {
                    await report(DownloadProgress(version, package, url, existingBytes, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                    MoveCompletedPackage(temp, destination);
                    return;
                }

                if (expectedSize > 0 && existingBytes > expectedSize)
                {
                    SafeDelete(temp);
                    existingBytes = 0;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existingBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                    }

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    HelperSecurity.EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? url, "package redirect");
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                        && expectedSize > 0
                        && existingBytes == expectedSize)
                    {
                        await report(DownloadProgress(version, package, url, existingBytes, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                        MoveCompletedPackage(temp, destination);
                        return;
                    }

                    if (existingBytes > 0 && response.StatusCode == HttpStatusCode.OK)
                    {
                        SafeDelete(temp);
                        existingBytes = 0;
                    }

                    response.EnsureSuccessStatusCode();
                    var responseLength = response.Content.Headers.ContentLength ?? 0;
                    var totalBytes = expectedSize > 0 ? expectedSize : existingBytes + responseLength;
                    await report(DownloadProgress(version, package, url, existingBytes, totalBytes, stopwatch, attempt, "正在下载 OCR 扫描器..."), token);

                    var downloaded = existingBytes;
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(
                        temp,
                        existingBytes > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 128 * 1024,
                        useAsync: true))
                    {
                        var buffer = new byte[128 * 1024];
                        var lastReport = Stopwatch.StartNew();
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                            if (read <= 0)
                            {
                                break;
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                            downloaded += read;
                            if (lastReport.ElapsedMilliseconds >= DownloadProgressIntervalMs
                                || (totalBytes > 0 && downloaded >= totalBytes))
                            {
                                await report(DownloadProgress(version, package, url, downloaded, totalBytes, stopwatch, attempt, "正在下载 OCR 扫描器..."), token);
                                lastReport.Restart();
                            }
                        }

                        await output.FlushAsync(token);
                    }

                    await report(DownloadProgress(version, package, url, downloaded, totalBytes, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                    MoveCompletedPackage(temp, destination);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    var downloaded = File.Exists(temp) ? new FileInfo(temp).Length : 0L;
                    if (expectedSize > 0 && downloaded == expectedSize)
                    {
                        await report(DownloadProgress(version, package, url, downloaded, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                        MoveCompletedPackage(temp, destination);
                        return;
                    }

                    if (attempt >= PackageDownloadMaxAttempts)
                    {
                        break;
                    }

                    await report(DownloadProgress(
                        version,
                        package,
                        url,
                        downloaded,
                        expectedSize,
                        stopwatch,
                        attempt + 1,
                        $"下载连接中断，正在第 {attempt + 1}/{PackageDownloadMaxAttempts} 次重试..."), token);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)), token);
                }
            }

            SafeDelete(temp);
            throw new IOException($"Scanner package download failed after {PackageDownloadMaxAttempts} attempts: {lastError?.Message}", lastError);
        }

        private static void MoveCompletedPackage(string temp, string destination)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temp, destination);
        }

        private static LauncherProgress DownloadProgress(
            string version,
            ScannerPackage package,
            Uri url,
            long downloaded,
            long total,
            Stopwatch stopwatch,
            int attempt,
            string message)
        {
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double? percent = total > 0 ? Math.Clamp(downloaded * 100d / total, 0d, 100d) : null;
            return new LauncherProgress
            {
                Stage = "download",
                Message = message,
                Version = version,
                PackageId = package.Id,
                PackageMode = package.Mode,
                Url = url.ToString(),
                BytesDownloaded = Math.Max(0, downloaded),
                TotalBytes = total > 0 ? total : null,
                Percent = percent,
                BytesPerSecond = Math.Max(0, downloaded / seconds),
                Attempt = attempt,
                MaxAttempts = PackageDownloadMaxAttempts
            };
        }

        private async Task DownloadAndVerifyPackageAsync(
            Uri manifestUrl,
            string scannerVersion,
            ScannerPackage package,
            string packagePath,
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token)
        {
            var urls = ResolvePackageUrls(manifestUrl, package).ToList();
            if (urls.Count == 0)
            {
                throw new InvalidOperationException("Scanner package URL is missing.");
            }

            var errors = new List<string>();
            foreach (var packageUrl in urls)
            {
                try
                {
                    await DownloadFileAsync(packageUrl, packagePath, scannerVersion, package, report, token);
                    await VerifyPackageSizeAsync(packagePath, package.Size);
                    await VerifySha256Async(packagePath, package.Sha256, token);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{packageUrl}: {ex.Message}");
                    SafeDelete(packagePath);
                    SafeDelete(packagePath + ".download");
                }
            }

            throw new HelperFailureException(
                "download_failed",
                "download",
                "扫描器下载失败",
                $"所有下载地址均失败：{string.Join(" | ", errors)}",
                "请检查网络后重试；Helper 会重新尝试所有备用地址。",
                retryable: true,
                new Dictionary<string, string> { ["packageId"] = package.Id });
        }

        private static IEnumerable<Uri> ResolvePackageUrls(Uri manifestUrl, ScannerPackage package)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawUrl in package.PackageUrls)
            {
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    continue;
                }

                var url = new Uri(manifestUrl, rawUrl.Trim());
                HelperSecurity.EnsureTrustedDownloadUri(url, "package");
                if (seen.Add(url.AbsoluteUri))
                {
                    yield return url;
                }
            }

        }

        private static Task VerifyPackageSizeAsync(string packagePath, long expectedSize)
        {
            if (expectedSize > 0)
            {
                var actualSize = new FileInfo(packagePath).Length;
                if (actualSize != expectedSize)
                {
                    throw new InvalidOperationException($"Scanner package size mismatch. Expected {expectedSize}, got {actualSize}.");
                }
            }

            return Task.CompletedTask;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static async Task VerifySha256Async(string path, string expectedHex, CancellationToken token)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (!actual.Equals(expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Scanner package checksum mismatch.");
            }
        }

        private static void ExtractZipSafe(string packagePath, string destination)
        {
            var rootFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                var full = HelperSecurity.ResolvePathWithinRoot(rootFull, entry.FullName);

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }
        }

        private static string RuntimeRootDirectory()
        {
            var path = Path.Combine(HelperStorageManager.DefaultDataRoot(), "runtime");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string PackageCacheDirectory()
        {
            var path = Path.Combine(HelperStorageManager.DefaultDataRoot(), "packages");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string LocalDataRoot()
        {
            var root = HelperStorageManager.DefaultDataRoot();
            Directory.CreateDirectory(root);
            return root;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
            _http.Dispose();
        }
}
}
