using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GoldenISOBuilder.Services.Catalog;

public sealed record DownloadProgress(long BytesDownloaded, long? TotalBytes,
                                      double? Mbps, TimeSpan Elapsed);

public sealed record DownloadResult(string FilePath, long SizeBytes,
                                    string Sha256, TimeSpan Duration);

/// <summary>
/// Streaming downloader with system-proxy autodetect, Range-header resume,
/// per-chunk SHA-256, and exponential-backoff retry.
///
/// Designed for files in the hundreds of MB to several GB range (wsusscn2.cab
/// is ~900 MB) so payloads are never buffered in memory.
/// </summary>
public sealed class ResumeableDownloader : IDisposable
{
    private readonly HttpClient _http;

    public int MaxAttempts { get; set; } = 3;
    public TimeSpan PerReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public ResumeableDownloader()
    {
        var handler = new HttpClientHandler
        {
            // Use the system proxy with current-user credentials so the tool
            // works on corporate networks behind WPAD/PAC without extra config.
            UseProxy                = true,
            Proxy                   = WebRequest.GetSystemWebProxy(),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression  = DecompressionMethods.All,
            AllowAutoRedirect       = true,
            UseCookies              = false,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            // No overall ceiling — large CAB files take a long time on slow
            // links. We enforce per-chunk timeouts via CancellationToken below.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GoldenISOBuilder", "1.0"));
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
    /// Writes to a sibling .part file while in flight; renames on success.
    /// Resumes from the .part file's existing length via Range header.
    /// Verifies SHA-256 against <paramref name="expectedSha256"/> if supplied.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var started = DateTime.UtcNow;

        Exception? lastError = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                    req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);

                // If the server doesn't honour resume (returns 200 not 206 for
                // a ranged request), start over rather than corrupt the file.
                if (existing > 0 && resp.StatusCode == HttpStatusCode.OK)
                {
                    existing = 0;
                    File.Delete(partPath);
                }
                resp.EnsureSuccessStatusCode();

                long? total = resp.Content.Headers.ContentLength.HasValue
                    ? existing + resp.Content.Headers.ContentLength.Value
                    : null;

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                // SHA-256 needs the whole file in order — if we resumed, we
                // must replay the existing prefix through the hasher.
                if (existing > 0)
                {
                    await using var existingFs = File.OpenRead(partPath);
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await existingFs.ReadAsync(buf, ct)) > 0)
                        hasher.AppendData(buf.AsSpan(0, n));
                }

                await using (var net = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs  = new FileStream(partPath,
                                FileMode.Append, FileAccess.Write, FileShare.None,
                                bufferSize: 1 << 20, useAsync: true))
                {
                    var buf = new byte[1 << 20];
                    long received = existing;
                    var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    while (true)
                    {
                        // Per-read timeout — guards against a stalled server
                        // holding the connection open without sending bytes.
                        chunkCts.CancelAfter(PerReadTimeout);
                        int n;
                        try
                        {
                            n = await net.ReadAsync(buf, chunkCts.Token);
                        }
                        finally
                        {
                            // Reset for next read.
                            chunkCts.Dispose();
                            chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        }
                        if (n == 0) break;

                        hasher.AppendData(buf.AsSpan(0, n));
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        received += n;

                        if (progress != null)
                        {
                            var elapsed = DateTime.UtcNow - started;
                            double? mbps = elapsed.TotalSeconds > 0
                                ? received * 8.0 / 1_000_000 / elapsed.TotalSeconds
                                : null;
                            progress.Report(new DownloadProgress(received, total, mbps, elapsed));
                        }
                    }
                    chunkCts.Dispose();
                }

                var digest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

                if (expectedSha256 != null &&
                    !string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partPath);
                    throw new InvalidDataException(
                        $"SHA-256 mismatch — expected {expectedSha256}, got {digest}.");
                }

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(partPath, destinationPath);

                return new DownloadResult(destinationPath,
                                          new FileInfo(destinationPath).Length,
                                          digest,
                                          DateTime.UtcNow - started);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-read timeout — treat as retryable network error.
                lastError = new TimeoutException(
                    $"Download stalled (>{PerReadTimeout.TotalSeconds:F0}s without data).");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                          or InvalidDataException)
            {
                lastError = ex;
            }

            if (attempt < MaxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        throw new IOException(
            $"Download failed after {MaxAttempts} attempts: {lastError?.Message}",
            lastError);
    }

    public void Dispose() => _http.Dispose();
}
