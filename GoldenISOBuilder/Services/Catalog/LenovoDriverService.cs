using System.IO;
using System.Xml.Linq;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Lenovo driver-pack catalogue access. Lenovo publishes one XML per
/// machine-type at:
///   https://download.lenovo.com/catalog/&lt;MachineType&gt;_Win11.xml
///   https://download.lenovo.com/catalog/&lt;MachineType&gt;_Win10.xml  (fallback;
///         many catalogues are still named Win10 but contain Win11-compatible
///         packages internally via &lt;Dependencies&gt;&lt;_OS&gt;WIN11&lt;/_OS&gt;).
///
/// There is no first-party master list of machine types. Lenovo discovery is
/// model-by-model — the user looks up their MT on the laptop's underside
/// label or in SMBIOS. We surface a free-text MT entry and validate by
/// attempting to fetch the catalogue.
///
/// Schema (simplified):
///   &lt;Packages&gt;
///     &lt;Package id="…"&gt;
///       &lt;Title&gt;&lt;Desc id="EN"&gt;Driver Pack for Win 11&lt;/Desc&gt;&lt;/Title&gt;
///       &lt;Version&gt;...&lt;/Version&gt;
///       &lt;ReleaseDate&gt;2025-03-10&lt;/ReleaseDate&gt;
///       &lt;Files&gt;
///         &lt;Installer&gt;&lt;Name&gt;...&lt;/Name&gt;&lt;CRC&gt;...&lt;/CRC&gt;&lt;/Installer&gt;
///       &lt;/Files&gt;
///       &lt;Dependencies&gt;&lt;_OS&gt;WIN11&lt;/_OS&gt;&lt;/Dependencies&gt;
///     &lt;/Package&gt;
///   &lt;/Packages&gt;
/// </summary>
public sealed class LenovoDriverService : IDriverPackService
{
    private const string CatalogUrlTemplate =
        "https://download.lenovo.com/catalog/{0}_{1}.xml";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly CatalogCacheManager _cache;
    private readonly ResumeableDownloader _downloader;

    /// <summary>
    /// Per-call diagnostic trail. Filled in by GetDriverPackAsync so the UI
    /// can show "Tried 21F8_Win11.xml → 404; Tried 21F8_Win10.xml → 404" when
    /// no pack is found, instead of just "0 packs staged".
    /// </summary>
    public string LastAttemptLog { get; private set; } = "";

    public LenovoDriverService(CatalogCacheManager cache,
                               ResumeableDownloader downloader)
    {
        _cache      = cache;
        _downloader = downloader;
    }

    public DriverVendor Vendor => DriverVendor.Lenovo;

    /// <summary>
    /// No-op for the global catalogue (there isn't one). Per-MT XML files are
    /// fetched on demand via <see cref="GetDriverPackAsync"/>.
    /// </summary>
    public Task EnsureCatalogAsync(IProgress<DownloadProgress>? progress = null,
                                   CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Returns the small built-in seed list of common Lenovo MTs. Real-world
    /// usage: admin types the MT off the laptop label / SMBIOS, we fetch the
    /// catalogue lazily. The seed list is just there so the UI has something
    /// to render in the picker before the user types.
    /// </summary>
    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<DriverPackModel> models = SeedMachineTypes
            .Select(m => new DriverPackModel(
                DriverVendor.Lenovo, m.Mt, m.Name, m.Family))
            .OrderBy(m => m.Brand ?? "")
            .ThenBy(m => m.Name)
            .ToList();
        return Task.FromResult(models);
    }

    public async Task<DriverPack?> GetDriverPackAsync(
        string systemId, string osVersion, CancellationToken ct = default)
    {
        var log = new System.Text.StringBuilder();
        // Probe Win11 first, fall back to Win10 (Lenovo's older naming
        // convention still publishes Win11-compatible packages).
        foreach (var os in new[] { "Win11", "Win10" })
        {
            var url = string.Format(CatalogUrlTemplate, systemId, os);
            var (doc, err) = await TryFetchAsync(url, systemId, os, ct);
            if (doc == null)
            {
                log.Append($"Tried {systemId}_{os}.xml → {err}; ");
                continue;
            }

            var pack = SelectDriverPack(doc, systemId, os, url);
            if (pack != null)
            {
                LastAttemptLog = $"OK from {systemId}_{os}.xml";
                return pack;
            }
            log.Append($"{systemId}_{os}.xml had no Driver Pack entry; ");
        }
        LastAttemptLog = log.ToString().TrimEnd(' ', ';');
        return null;
    }

    private async Task<(XDocument? doc, string err)> TryFetchAsync(
        string url, string mt, string os, CancellationToken ct)
    {
        var key = Path.Combine(mt, $"{mt}_{os}.xml");
        if (!_cache.TryGetValid(CatalogCacheManager.Category.Lenovo, key,
                                out var xmlPath, out _))
        {
            try
            {
                var r = await _downloader.DownloadAsync(url, xmlPath,
                            expectedSha256: null, progress: null, ct);
                _cache.WriteManifest(xmlPath, new CacheManifest
                {
                    SourceUrl     = url,
                    Sha256        = r.Sha256,
                    SizeBytes     = r.SizeBytes,
                    DownloadedUtc = DateTime.UtcNow,
                    ExpiresUtc    = DateTime.UtcNow.Add(CacheLifetime),
                    Vendor        = "Lenovo",
                    Notes         = $"Catalogue for {mt} ({os})"
                });
            }
            catch (IOException ex)
            {
                // 404 / network — try the other OS variant. Return reason
                // so the UI can show it.
                var msg = ex.InnerException?.Message ?? ex.Message;
                if (msg.Contains("404")) return (null, "404 (no catalogue published)");
                return (null, msg.Length > 60 ? msg[..60] + "…" : msg);
            }
        }

        try { return (XDocument.Load(xmlPath), ""); }
        catch (Exception ex) { return (null, "XML parse: " + ex.Message); }
    }

    private static DriverPack? SelectDriverPack(
        XDocument doc, string mt, string os, string sourceUrl)
    {
        // Prefer packages whose title contains "Driver Pack" (Lenovo's canonical
        // naming) and that target Win11 in <Dependencies>.
        DriverPack? best = null;
        DateTime bestDate = DateTime.MinValue;

        foreach (var pkg in doc.Descendants("Package"))
        {
            var title = pkg.Element("Title")?.Element("Desc")?.Value ?? "";
            if (!title.Contains("Driver Pack", StringComparison.OrdinalIgnoreCase))
                continue;

            var depOs = pkg.Descendants("_OS").FirstOrDefault()?.Value ?? "";
            if (os == "Win11" &&
                !depOs.Contains("WIN11", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("11", StringComparison.OrdinalIgnoreCase))
                continue;

            var installer = pkg.Descendants("Installer").FirstOrDefault();
            var fileName  = installer?.Element("Name")?.Value;
            if (string.IsNullOrEmpty(fileName)) continue;

            DateTime.TryParse(pkg.Element("ReleaseDate")?.Value, out var date);
            long.TryParse(installer?.Element("Size")?.Value, out var size);

            // Installer URLs are usually given in <LocalPath> (relative to
            // the catalogue host) or as a bare file name resolved against
            // the catalogue URL. Resolve against the source.
            var local = pkg.Element("LocalPath")?.Value
                        ?? pkg.Descendants("LocalPath").FirstOrDefault()?.Value
                        ?? Combine(sourceUrl, fileName);

            var sha = installer?.Element("SHA256")?.Value
                      ?? installer?.Element("Sha256")?.Value;
            var crc = installer?.Element("CRC")?.Value;

            var pack = new DriverPack(
                Vendor:       DriverVendor.Lenovo,
                Model:        $"Machine Type {mt}",
                SystemId:     mt,
                OsVersion:    os,
                Version:      pkg.Element("Version")?.Value ?? "",
                ReleaseDate:  date,
                DownloadUrl:  local,
                SizeBytes:    size,
                Sha256:       sha?.ToLowerInvariant(),
                Md5:          null,
                Filename:     fileName);

            if (pack.ReleaseDate > bestDate)
            {
                best     = pack;
                bestDate = pack.ReleaseDate;
            }
        }

        return best;
    }

    private static string Combine(string sourceUrl, string fileName)
    {
        try
        {
            var baseUri = new Uri(sourceUrl);
            return new Uri(baseUri, fileName).ToString();
        }
        catch
        {
            return fileName;
        }
    }

    // Small built-in seed list. The UI also exposes a free-text MT entry for
    // models not pre-populated here.
    private static readonly (string Mt, string Name, string Family)[] SeedMachineTypes =
    {
        ("21HD", "ThinkPad T14 Gen 4",      "ThinkPad"),
        ("21HE", "ThinkPad T16 Gen 2",      "ThinkPad"),
        ("21JJ", "ThinkPad X1 Carbon Gen12","ThinkPad"),
        ("21M9", "ThinkPad X1 Carbon Gen13","ThinkPad"),
        ("21F8", "ThinkPad L14 Gen 4",      "ThinkPad"),
        ("21F9", "ThinkPad L15 Gen 4",      "ThinkPad"),
        ("21KK", "ThinkPad P14s Gen 4",     "ThinkPad"),
        ("11GR", "ThinkCentre M75q",        "ThinkCentre"),
        ("12L8", "ThinkCentre M70q Gen 4",  "ThinkCentre"),
        ("30G9", "ThinkStation P3 Tower",   "ThinkStation"),
    };
}
