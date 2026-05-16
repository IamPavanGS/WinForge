using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// File-system cache for auto-fetched Windows Update and OEM driver-pack
/// payloads. Lives under %LOCALAPPDATA%\GoldenISOBuilder\Cache\&lt;category&gt;\.
///
/// Each cache entry is a directory containing the payload file plus a
/// manifest.json with source URL, SHA-256, size, fetched-at, expires-at.
/// LRU cleanup runs on app start to enforce a max total size + retention age.
///
/// Threading: instance methods are not thread-safe per cache entry — callers
/// should serialise downloads for the same entry. Different entries are
/// independent and safe to fetch in parallel.
/// </summary>
public sealed class CatalogCacheManager
{
    public enum Category
    {
        WindowsUpdates,
        Dell,
        HP,
        Lenovo
    }

    private static readonly string RootDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GoldenISOBuilder", "Cache");

    public long MaxCacheBytes      { get; set; } = 10L * 1024 * 1024 * 1024;   // 10 GB
    public int  RetentionDays      { get; set; } = 60;

    public string GetCategoryRoot(Category category)
    {
        var path = Path.Combine(RootDir, category.ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Returns the full path of a cache entry, creating its parent directory.
    /// The key is treated as a relative path inside the category root and is
    /// sanitised — callers can pass values like "26100/KB5089549.msu".
    /// </summary>
    public string GetEntryPath(Category category, string relativeKey)
    {
        var sanitised = SanitiseKey(relativeKey);
        var path = Path.Combine(GetCategoryRoot(category), sanitised);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Returns true if an entry is present, the manifest is well-formed,
    /// its expiry hasn't passed, and (if recordedSha256 is non-null) the
    /// on-disk SHA-256 matches.
    /// </summary>
    public bool TryGetValid(Category category, string relativeKey,
                            out string filePath, out CacheManifest? manifest)
    {
        filePath = GetEntryPath(category, relativeKey);
        manifest = ReadManifest(filePath);
        if (manifest == null) return false;
        if (!File.Exists(filePath)) return false;
        if (manifest.ExpiresUtc <= DateTime.UtcNow) return false;
        if (new FileInfo(filePath).Length != manifest.SizeBytes) return false;
        return true;
    }

    public CacheManifest? ReadManifest(string payloadPath)
    {
        var manifestPath = payloadPath + ".manifest.json";
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var fs = File.OpenRead(manifestPath);
            return JsonSerializer.Deserialize<CacheManifest>(fs);
        }
        catch { return null; }
    }

    public void WriteManifest(string payloadPath, CacheManifest manifest)
    {
        var manifestPath = payloadPath + ".manifest.json";
        using var fs = File.Create(manifestPath);
        JsonSerializer.Serialize(fs, manifest,
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Streams a file through SHA-256 and returns the lowercase hex digest.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string path,
        CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        using var hasher = SHA256.Create();
        var hash = await hasher.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Async LRU sweep. Removes entries older than RetentionDays first; if the
    /// cache is still over MaxCacheBytes, removes the oldest entries by
    /// DownloadedUtc until under the cap. Best-effort — never throws.
    /// Call once on app start.
    /// </summary>
    public Task SweepAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(RootDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var entries = EnumerateEntries().ToList();

            foreach (var e in entries.Where(e => e.Manifest.DownloadedUtc < cutoff))
                TryDeleteEntry(e);

            long total = entries.Sum(e => e.SizeBytes);
            if (total <= MaxCacheBytes) return;

            foreach (var e in entries.OrderBy(e => e.Manifest.DownloadedUtc))
            {
                if (total <= MaxCacheBytes) return;
                total -= e.SizeBytes;
                TryDeleteEntry(e);
            }
        }
        catch { /* swallow — cache cleanup must never crash the app */ }
    });

    private IEnumerable<CacheEntry> EnumerateEntries()
    {
        if (!Directory.Exists(RootDir)) yield break;
        foreach (var manifestPath in Directory.EnumerateFiles(
                     RootDir, "*.manifest.json", SearchOption.AllDirectories))
        {
            var payloadPath = manifestPath[..^".manifest.json".Length];
            if (!File.Exists(payloadPath)) continue;
            CacheManifest? m = null;
            try
            {
                using var fs = File.OpenRead(manifestPath);
                m = JsonSerializer.Deserialize<CacheManifest>(fs);
            }
            catch { /* skip corrupt manifest */ }
            if (m == null) continue;
            yield return new CacheEntry(payloadPath, manifestPath, m,
                new FileInfo(payloadPath).Length);
        }
    }

    private static void TryDeleteEntry(CacheEntry e)
    {
        try { File.Delete(e.PayloadPath); }   catch { }
        try { File.Delete(e.ManifestPath); }  catch { }
    }

    /// <summary>
    /// Strips path separators, drive letters, and dangerous chars from a
    /// caller-supplied key. Forward slashes are preserved as directory
    /// separators within the cache.
    /// </summary>
    private static string SanitiseKey(string key)
    {
        var bad = Path.GetInvalidFileNameChars()
                      .Where(c => c != '/' && c != '\\')
                      .ToArray();
        var parts = key.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            foreach (var c in bad) p = p.Replace(c, '_');
            parts[i] = p.Trim().TrimStart('.');
        }
        return Path.Combine(parts.Where(p => p.Length > 0).ToArray());
    }

    private sealed record CacheEntry(string PayloadPath, string ManifestPath,
                                     CacheManifest Manifest, long SizeBytes);
}

public sealed class CacheManifest
{
    [JsonPropertyName("sourceUrl")]    public string  SourceUrl     { get; set; } = "";
    [JsonPropertyName("sha256")]       public string  Sha256        { get; set; } = "";
    [JsonPropertyName("sizeBytes")]    public long    SizeBytes     { get; set; }
    [JsonPropertyName("downloadedUtc")] public DateTime DownloadedUtc { get; set; }
    [JsonPropertyName("expiresUtc")]   public DateTime ExpiresUtc    { get; set; }
    [JsonPropertyName("vendor")]       public string? Vendor        { get; set; }
    [JsonPropertyName("notes")]        public string? Notes         { get; set; }
}
