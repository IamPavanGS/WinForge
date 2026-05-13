using System.IO;
using System.Xml.Linq;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Reads Dell's <c>DriverPackCatalog.cab</c>, the canonical machine-readable
/// catalogue of every Dell enterprise driver pack. URL pattern unchanged since
/// 2014 — depended on by every SCCM/MDT/MEMCM admin's tooling
/// (Maurice Daly's DriverAutomationTool, garytown.com, Dell KB 000122176).
///
/// Schema (root element <c>DriverPackManifest</c>):
///   &lt;DriverPackage releaseID="…" hashMD5="…" path="FOLDER/file.cab"
///                  size="…" releaseDate="…" type="win"&gt;
///     &lt;SupportedSystems&gt;
///       &lt;Brand prefix="LAT"&gt;
///         &lt;Model systemID="0BA8" name="Latitude 5550"/&gt;
///       &lt;/Brand&gt;
///     &lt;/SupportedSystems&gt;
///     &lt;SupportedOperatingSystems&gt;
///       &lt;OperatingSystem osCode="W11" osArch="x64"/&gt;
///     &lt;/SupportedOperatingSystems&gt;
///   &lt;/DriverPackage&gt;
///
/// Final download URL = "https://" + DriverPackManifest/@baseLocation + "/" + path.
/// </summary>
public sealed class DellDriverService : IDriverPackService
{
    public const string CatalogUrl =
        "https://downloads.dell.com/catalog/DriverPackCatalog.cab";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly XNamespace Ns =
        "openmanage/cm/dm";   // Dell's default namespace for the manifest

    private readonly CatalogCacheManager _cache;
    private readonly ResumeableDownloader _downloader;
    private XDocument? _manifest;
    private string?    _baseLocation;

    public DellDriverService(CatalogCacheManager cache,
                             ResumeableDownloader downloader)
    {
        _cache      = cache;
        _downloader = downloader;
    }

    public DriverVendor Vendor => DriverVendor.Dell;

    public async Task EnsureCatalogAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        const string key = "DriverPackCatalog.cab";
        if (!_cache.TryGetValid(CatalogCacheManager.Category.Dell, key,
                                out var cabPath, out _))
        {
            var result = await _downloader.DownloadAsync(
                CatalogUrl, cabPath, expectedSha256: null, progress, ct);
            _cache.WriteManifest(cabPath, new CacheManifest
            {
                SourceUrl     = CatalogUrl,
                Sha256        = result.Sha256,
                SizeBytes     = result.SizeBytes,
                DownloadedUtc = DateTime.UtcNow,
                ExpiresUtc    = DateTime.UtcNow.Add(CacheLifetime),
                Vendor        = "Dell",
                Notes         = "Dell DriverPack catalogue"
            });
        }

        var extractDir = cabPath + ".extracted";
        if (!Directory.Exists(extractDir) ||
            !File.Exists(Path.Combine(extractDir, "DriverPackCatalog.xml")))
        {
            await CabExtractor.ExtractAsync(cabPath, extractDir, ct);
        }

        var xmlPath = Path.Combine(extractDir, "DriverPackCatalog.xml");
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException(
                "DriverPackCatalog.xml not produced by CAB extraction.", xmlPath);

        _manifest     = XDocument.Load(xmlPath);
        _baseLocation = _manifest.Root?.Attribute("baseLocation")?.Value
                        ?? "downloads.dell.com";
    }

    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default)
    {
        EnsureLoaded();
        var seen   = new Dictionary<string, DriverPackModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in _manifest!.Descendants(Ns + "DriverPackage"))
        {
            ct.ThrowIfCancellationRequested();
            var brand = pkg.Descendants(Ns + "Brand").FirstOrDefault();
            var model = pkg.Descendants(Ns + "Model").FirstOrDefault();
            if (model == null) continue;

            var sid = model.Attribute("systemID")?.Value?.Trim();
            if (string.IsNullOrEmpty(sid)) continue;

            var name      = model.Attribute("name")?.Value?.Trim() ?? sid;
            var brandName = brand?.Attribute("prefix")?.Value?.Trim();

            if (!seen.ContainsKey(sid))
                seen[sid] = new DriverPackModel(
                    DriverVendor.Dell, sid, name, brandName);
        }

        IReadOnlyList<DriverPackModel> result = seen.Values
            .OrderBy(m => m.Brand ?? "")
            .ThenBy(m => m.Name)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<DriverPack?> GetDriverPackAsync(
        string systemId, string osVersion, CancellationToken ct = default)
    {
        EnsureLoaded();

        // Find every package supporting the given systemID + osCode and pick
        // the most recent by releaseDate. Some models have multiple packs
        // (e.g. one per major Win11 version).
        DriverPack? best = null;
        DateTime bestDate = DateTime.MinValue;

        foreach (var pkg in _manifest!.Descendants(Ns + "DriverPackage"))
        {
            ct.ThrowIfCancellationRequested();
            var systems = pkg.Descendants(Ns + "Model")
                             .Any(m => string.Equals(
                                 m.Attribute("systemID")?.Value, systemId,
                                 StringComparison.OrdinalIgnoreCase));
            if (!systems) continue;

            var oss = pkg.Descendants(Ns + "OperatingSystem")
                         .Any(o => string.Equals(
                             o.Attribute("osCode")?.Value, osVersion,
                             StringComparison.OrdinalIgnoreCase));
            if (!oss) continue;

            var pack = MaterialisePack(pkg, systemId, osVersion);
            if (pack != null && pack.ReleaseDate > bestDate)
            {
                best     = pack;
                bestDate = pack.ReleaseDate;
            }
        }

        return Task.FromResult(best);
    }

    private DriverPack? MaterialisePack(XElement pkg, string systemId, string os)
    {
        var path = pkg.Attribute("path")?.Value;
        if (string.IsNullOrEmpty(path)) return null;

        long.TryParse(pkg.Attribute("size")?.Value, out var size);
        DateTime.TryParse(pkg.Attribute("releaseDate")?.Value, out var date);

        var modelName = pkg.Descendants(Ns + "Model").FirstOrDefault()
                          ?.Attribute("name")?.Value ?? systemId;

        var url = $"https://{_baseLocation}/{path.TrimStart('/')}";

        return new DriverPack(
            Vendor:       DriverVendor.Dell,
            Model:        modelName,
            SystemId:     systemId,
            OsVersion:    os,
            Version:      pkg.Attribute("vendorVersion")?.Value
                          ?? pkg.Attribute("dellVersion")?.Value
                          ?? "",
            ReleaseDate:  date,
            DownloadUrl:  url,
            SizeBytes:    size,
            Sha256:       null,                              // Dell publishes MD5
            Md5:          pkg.Attribute("hashMD5")?.Value,
            Filename:     Path.GetFileName(path));
    }

    private void EnsureLoaded()
    {
        if (_manifest == null)
            throw new InvalidOperationException(
                "DellDriverService.EnsureCatalogAsync must be awaited before " +
                "ListModelsAsync / GetDriverPackAsync.");
    }
}
