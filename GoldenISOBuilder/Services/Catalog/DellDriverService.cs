using System.IO;
using System.Xml.Linq;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Reads Dell's <c>DriverPackCatalog.cab</c>, the canonical machine-readable
/// catalogue of every Dell enterprise driver pack. URL pattern unchanged since
/// 2014 — depended on by every SCCM/MDT/MEMCM admin's tooling
/// (Maurice Daly's DriverAutomationTool, garytown.com, Dell KB 000122176).
///
/// Schema (verified live 2026-05-13 against catalogue releaseID V9YVY):
///   &lt;DriverPackage path="…" hashMD5="…" releaseID="…" dateTime="…"&gt;
///     &lt;SupportedSystems&gt;
///       &lt;Brand prefix="LAT"&gt;
///         &lt;Model systemID="0CB9" name="Latitude 5550"/&gt;
///       &lt;/Brand&gt;
///     &lt;/SupportedSystems&gt;
///     &lt;SupportedOperatingSystems&gt;
///       &lt;OperatingSystem osCode="Windows11" osArch="x64"/&gt;
///     &lt;/SupportedOperatingSystems&gt;
///   &lt;/DriverPackage&gt;
///
/// IMPORTANT — Dell's osCode values are "Windows11" / "Windows10" / "Windows7"
/// / "Vista" / "XP" / "Windows8" / "Windows8.1" / "winpe11x" etc. The earlier
/// version of this file filtered on "W11" / "W10" which match zero entries in
/// the live catalogue — caller-side OS tokens MUST be mapped before lookup.
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

    /// <summary>SystemID → set of osCode values the catalogue publishes for it.
    /// Built once at EnsureCatalogAsync. Used to filter ListModelsAsync and to
    /// produce human-readable error messages.</summary>
    private Dictionary<string, HashSet<string>>? _modelOsSupport;

    /// <summary>Last failure detail for the UI to surface. Mirrors the pattern
    /// in LenovoDriverService.</summary>
    public string LastAttemptLog { get; private set; } = "";

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
        _modelOsSupport = BuildModelOsIndex(_manifest);
    }

    /// <summary>
    /// Maps a caller-side OS token ("W11", "win11", "Win11", "Windows11") to
    /// Dell's actual catalogue value. Returns null if the token is unrecognised.
    /// </summary>
    private static string? MapOsCode(string osVersion)
    {
        var v = osVersion.Trim().ToLowerInvariant();
        if (v == "windows11" || v == "win11" || v == "w11" || v.Contains("11"))
            return "Windows11";
        if (v == "windows10" || v == "win10" || v == "w10" || v.Contains("10"))
            return "Windows10";
        return null;
    }

    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default) => ListModelsAsync(includeNonWin11: false, ct);

    /// <summary>
    /// Optionally filter to models that have at least one Windows 11 driver
    /// pack — by default we do (492 SystemIDs as of 2026-05). Pass true to
    /// include legacy/retired models (1,114 total entries in the catalogue).
    /// </summary>
    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        bool includeNonWin11, CancellationToken ct = default)
    {
        EnsureLoaded();
        var seen = new Dictionary<string, DriverPackModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in _manifest!.Descendants(Ns + "DriverPackage"))
        {
            ct.ThrowIfCancellationRequested();
            var brand = pkg.Descendants(Ns + "Brand").FirstOrDefault();
            var model = pkg.Descendants(Ns + "Model").FirstOrDefault();
            if (model == null) continue;

            var sid = model.Attribute("systemID")?.Value?.Trim();
            if (string.IsNullOrEmpty(sid)) continue;

            // Skip models with no Win11 pack unless the caller asked for them.
            if (!includeNonWin11 &&
                (!_modelOsSupport!.TryGetValue(sid, out var supported) ||
                 !supported.Contains("Windows11")))
                continue;

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

        // Map the caller's canonical OS token to Dell's actual osCode.
        var dellOs = MapOsCode(osVersion);
        if (dellOs == null)
        {
            LastAttemptLog = $"unknown OS token '{osVersion}'.";
            return Task.FromResult<DriverPack?>(null);
        }

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
                             o.Attribute("osCode")?.Value, dellOs,
                             StringComparison.OrdinalIgnoreCase));
            if (!oss) continue;

            var pack = MaterialisePack(pkg, systemId, dellOs);
            if (pack != null && pack.ReleaseDate > bestDate)
            {
                best     = pack;
                bestDate = pack.ReleaseDate;
            }
        }

        if (best != null)
        {
            LastAttemptLog = $"matched Dell {dellOs} pack {best.Filename}";
        }
        else
        {
            // Produce an informative message naming the OS versions Dell DOES
            // publish for this model. Pulled from the pre-built index.
            if (_modelOsSupport!.TryGetValue(systemId, out var supported) &&
                supported.Count > 0)
            {
                var available = string.Join(", ",
                    supported.OrderByDescending(s => s)
                             .Take(6));
                LastAttemptLog =
                    $"Dell publishes no {dellOs} driver pack for SystemID {systemId}. " +
                    $"Available: {available}. " +
                    "This model likely predates Win11 system requirements.";
            }
            else
            {
                LastAttemptLog =
                    $"SystemID {systemId} not present in Dell DriverPackCatalog.";
            }
        }

        return Task.FromResult(best);
    }

    private DriverPack? MaterialisePack(XElement pkg, string systemId, string os)
    {
        var path = pkg.Attribute("path")?.Value;
        if (string.IsNullOrEmpty(path)) return null;

        long.TryParse(pkg.Attribute("size")?.Value, out var size);
        // Modern Dell entries use dateTime="..."; older ones use releaseDate.
        var dateStr = pkg.Attribute("dateTime")?.Value
                      ?? pkg.Attribute("releaseDate")?.Value;
        DateTime.TryParse(dateStr, out var date);

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
            Sha256:       null,                              // Dell publishes MD5 only
            Md5:          pkg.Attribute("hashMD5")?.Value,
            Filename:     Path.GetFileName(path));
    }

    private static Dictionary<string, HashSet<string>> BuildModelOsIndex(XDocument manifest)
    {
        var idx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in manifest.Descendants(Ns + "DriverPackage"))
        {
            var sids = pkg.Descendants(Ns + "Model")
                          .Select(m => m.Attribute("systemID")?.Value)
                          .Where(s => !string.IsNullOrEmpty(s))
                          .ToList();
            var oses = pkg.Descendants(Ns + "OperatingSystem")
                          .Select(o => o.Attribute("osCode")?.Value)
                          .Where(o => !string.IsNullOrEmpty(o))
                          .ToList();

            foreach (var sid in sids)
            {
                if (!idx.TryGetValue(sid!, out var set))
                    idx[sid!] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var os in oses) set.Add(os!);
            }
        }
        return idx;
    }

    private void EnsureLoaded()
    {
        if (_manifest == null || _modelOsSupport == null)
            throw new InvalidOperationException(
                "DellDriverService.EnsureCatalogAsync must be awaited before " +
                "ListModelsAsync / GetDriverPackAsync.");
    }
}
