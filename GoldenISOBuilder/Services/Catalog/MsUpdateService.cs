using System.IO;
using System.Runtime.Versioning;

namespace GoldenISOBuilder.Services.Catalog;

public sealed record MsUpdate(
    string  KbArticleId,        // "KB5089549" — empty when WUA returns no KB
    string  Title,               // localised title from the catalog
    string  Classification,      // "Security Updates" / "Updates" / "Feature Packs" / etc.
    long    SizeBytes,           // sum of bundled file sizes, 0 if unknown
    string? DownloadUrl,         // direct CDN URL for the .msu / .cab, null if bundled
    string? Sha256);             // hex digest published in the catalog (lowercase)

/// <summary>
/// Reads the Microsoft offline-scan catalog (<c>wsusscn2.cab</c>) and exposes
/// the set of updates Microsoft has published. The catalog is the same file
/// Microsoft publishes for WSUS / Defender offline scanners; it contains
/// metadata for every Windows + Office update Microsoft has ever shipped
/// (KB IDs, titles, classifications, download URLs, SHA-256 hashes).
///
/// Strategy:
///   1. Cache wsusscn2.cab under %LOCALAPPDATA%\GoldenISOBuilder\Cache\
///      WindowsUpdates\wsusscn2.cab (refreshed every 7 days).
///   2. Use the Windows Update Agent COM API (<c>Microsoft.Update.Session</c> +
///      <c>AddScanPackageService</c>) to enumerate updates from the cab.
///      The COM API is dispatched via <c>dynamic</c> — this avoids depending
///      on a tlbimp'd WUApiLib interop assembly.
///   3. Project COM Update objects onto plain <see cref="MsUpdate"/> records
///      so the rest of the app sees only managed types.
///
/// This service does NOT yet filter by target build — Phase 2 deliverable is
/// "can we read the catalog at all". Build-specific filtering arrives in
/// Phase 4 when the pipeline step actually applies updates.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MsUpdateService
{
    // Canonical Microsoft URL for the offline-scan catalog. The fwlink form
    // 74689 has redirected to this for years; the s.download form is the one
    // current in 2025-2026 per Microsoft Learn.
    public const string Wsusscn2Url =
        "https://catalog.s.download.windowsupdate.com/microsoftupdate/v6/wsusscan/wsusscn2.cab";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly CatalogCacheManager _cache;
    private readonly ResumeableDownloader _downloader;

    public MsUpdateService(CatalogCacheManager cache, ResumeableDownloader downloader)
    {
        _cache      = cache;
        _downloader = downloader;
    }

    /// <summary>
    /// Ensures wsusscn2.cab is on disk and fresh, downloading if needed.
    /// Returns the absolute path to the cached cab.
    /// </summary>
    public async Task<string> EnsureCatalogAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        const string key = "wsusscn2.cab";
        if (_cache.TryGetValid(CatalogCacheManager.Category.WindowsUpdates, key,
                               out var path, out _))
            return path;

        var result = await _downloader.DownloadAsync(
            Wsusscn2Url, path, expectedSha256: null, progress, ct);

        _cache.WriteManifest(path, new CacheManifest
        {
            SourceUrl     = Wsusscn2Url,
            Sha256        = result.Sha256,
            SizeBytes     = result.SizeBytes,
            DownloadedUtc = DateTime.UtcNow,
            ExpiresUtc    = DateTime.UtcNow.Add(CacheLifetime),
            Vendor        = "Microsoft",
            Notes         = "Windows Update offline-scan catalog"
        });
        return path;
    }

    /// <summary>
    /// Enumerates updates known to the cached catalog. Filtering is intentionally
    /// minimal at this stage so callers (Phase 4 pipeline step + the temporary
    /// debug smoke test) can decide what to do with the result set.
    ///
    /// Performance note: the WUA Search("IsInstalled=0") call can take 30-90s on
    /// first run as the agent ingests ~900 MB of metadata. We run it on a
    /// background thread and return all results in one shot.
    /// </summary>
    public Task<IReadOnlyList<MsUpdate>> EnumerateAsync(
        string? titleContains = null,
        CancellationToken ct = default)
        => Task.Run<IReadOnlyList<MsUpdate>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Catalog must exist on disk before we call into WUA.
            const string key = "wsusscn2.cab";
            var catalogPath = _cache.GetEntryPath(
                CatalogCacheManager.Category.WindowsUpdates, key);
            if (!File.Exists(catalogPath))
                throw new InvalidOperationException(
                    "wsusscn2.cab not cached. Call EnsureCatalogAsync first.");

            return EnumerateInternal(catalogPath, titleContains, ct);
        }, ct);

    private static IReadOnlyList<MsUpdate> EnumerateInternal(
        string catalogPath, string? titleContains, CancellationToken ct)
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new PlatformNotSupportedException(
                "Microsoft.Update.Session COM class not available. The Windows " +
                "Update Agent must be installed and not disabled by policy.");

        dynamic session = Activator.CreateInstance(sessionType)!;
        try
        {
            // ssOthers (= 3) tells the searcher to use the service we register,
            // not Windows Update / WSUS / Microsoft Update online.
            const int ssOthers       = 3;
            // Searcher service flag: 1 = SearcherService (return updates that
            // the searcher could find / install).
            const int searcherFlag   = 1;

            dynamic mgr = session.CreateUpdateServiceManager();
            dynamic svc = mgr.AddScanPackageService(
                "Golden ISO Builder offline catalog", catalogPath, searcherFlag);

            dynamic searcher = session.CreateUpdateSearcher();
            searcher.ServerSelection = ssOthers;
            searcher.ServiceID       = svc.ServiceID;

            ct.ThrowIfCancellationRequested();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");

            int count = result.Updates.Count;
            var list  = new List<MsUpdate>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                dynamic upd = result.Updates.Item(i);

                string title = (string)(upd.Title ?? string.Empty);
                if (titleContains != null &&
                    title.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                list.Add(ProjectUpdate(upd, title));
            }

            try { mgr.RemoveService(svc.ServiceID); } catch { /* best-effort */ }
            return list;
        }
        finally
        {
            if (session is IDisposable d) d.Dispose();
        }
    }

    private static MsUpdate ProjectUpdate(dynamic upd, string title)
    {
        string kb = "";
        try
        {
            dynamic? kbs = upd.KBArticleIDs;
            if (kbs is not null && (int)kbs!.Count > 0)
                kb = "KB" + (string)kbs!.Item(0);
        }
        catch { }

        string classification = "";
        try
        {
            dynamic cats = upd.Categories;
            int catCount = (int)cats.Count;
            for (int i = 0; i < catCount; i++)
            {
                dynamic cat = cats.Item(i);
                if ((string)cat.Type == "UpdateClassification")
                {
                    classification = (string)cat.Name;
                    break;
                }
            }
        }
        catch { }

        long   size = 0;
        string? url = null;
        string? sha = null;
        try
        {
            dynamic contents = upd.DownloadContents;
            int n = (int)contents.Count;
            if (n > 0)
            {
                dynamic first = contents.Item(0);
                url = (string?)first.DownloadUrl;
            }
            // Per-file size + digest are exposed on the (deprecated but still
            // populated) BundledUpdates / Files collection. We try the
            // primary update first and fall back to bundled.
            try
            {
                dynamic files = upd.Files;
                int fileCount = (int)files.Count;
                for (int i = 0; i < fileCount; i++)
                {
                    dynamic f = files.Item(i);
                    size += (long)f.TotalBytes;
                    sha ??= TryDigest(f);
                }
            }
            catch { }
        }
        catch { }

        return new MsUpdate(kb, title, classification, size, url, sha);
    }

    private static string? TryDigest(dynamic file)
    {
        try
        {
            // IUpdateFile2.Sha256 is the modern field; older entries only have
            // .Digest (SHA-1 base64). Prefer Sha256.
            string? hex = (string?)file.Sha256;
            if (!string.IsNullOrEmpty(hex)) return hex.ToLowerInvariant();
        }
        catch { }
        return null;
    }
}
