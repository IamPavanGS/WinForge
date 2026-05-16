using System.IO;
using System.Xml.Linq;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Reads HP's <c>HPClientDriverPackCatalog.cab</c> — HP's canonical, static,
/// machine-readable list of OS-deployment-ready driver packs. Same source HP
/// Image Assistant (HPIA), HP CMSL, Maurice Daly's Driver Automation Tool, and
/// Microsoft Endpoint Manager all consume for HP driver imports.
///
/// Earlier versions of this service called <c>Install-Module HPCMSL</c> +
/// <c>Get-SoftpaqList</c> via PowerShell. That path had four compounding
/// problems: PowerShellGet 1.0.0.1 on stock Win11 lacks <c>-AcceptLicense</c>;
/// CMSL's per-platform CAB cache constructs URLs from a fallback OsVer that's
/// empty for older platforms (producing malformed <c>1790_64_11.0..cab</c>
/// URLs that crash inside <c>HP.Private.psm1</c>); the EXE installer URL
/// rotates; the dependency on PowerShell itself was a 70-MB bootstrap. The
/// static-catalog approach drops every one of those.
///
/// Schema (verified live 2026-05-13 against ftp.hp.com; 187 KB CAB, 2.2 MB XML):
///   &lt;HPClientDriverPackCatalog SchemaVersion="1.0.0.0" DateReleased="2026-05-02"&gt;
///     &lt;OSList&gt;
///       &lt;OS&gt;&lt;Name&gt;Windows 11 64-bit, 24H2&lt;/Name&gt;&lt;OSId&gt;4323&lt;/OSId&gt;&lt;/OS&gt;
///       …20 entries Win10 1607 → Win11 25H2…
///     &lt;/OSList&gt;
///     &lt;SoftPaqList&gt;
///       &lt;SoftPaq&gt;
///         &lt;Id&gt;sp144691&lt;/Id&gt;
///         &lt;Url&gt;https://ftp.hp.com/pub/softpaq/sp144501-145000/sp144691.exe&lt;/Url&gt;
///         &lt;Size&gt;435140224&lt;/Size&gt;
///         &lt;MD5&gt;…&lt;/MD5&gt;&lt;SHA256&gt;…&lt;/SHA256&gt;
///         &lt;DateReleased&gt;1/24/2023 12:00:00 AM&lt;/DateReleased&gt;
///       &lt;/SoftPaq&gt;
///     &lt;/SoftPaqList&gt;
///     &lt;ProductOSDriverPackList&gt;
///       &lt;ProductOSDriverPack&gt;
///         &lt;SystemId&gt;81c3,8396&lt;/SystemId&gt;   &lt;!-- comma-separated --&gt;
///         &lt;SystemName&gt;HP Elite Slice&lt;/SystemName&gt;
///         &lt;OSName&gt;Windows 11 64-bit, 24H2&lt;/OSName&gt;
///         &lt;SoftPaqId&gt;sp80493&lt;/SoftPaqId&gt;   &lt;!-- join to SoftPaqList --&gt;
///       &lt;/ProductOSDriverPack&gt;
///     &lt;/ProductOSDriverPackList&gt;
///   &lt;/HPClientDriverPackCatalog&gt;
///
/// Coverage: 517 unique SystemIds across EliteBook (130) / ProBook (63) /
/// ZBook (61) / EliteDesk (62) / ProDesk (67) / Z-series workstations / Mini
/// (55) / Engage. <b>Zero consumer SKUs</b> — Pavilion, OMEN, ENVY, Stream,
/// Spectre are not in this catalogue. The UI surfaces this explicitly.
/// </summary>
public sealed class HpDriverService : IDriverPackService
{
    public const string CatalogUrl =
        "https://ftp.hp.com/pub/caps-softpaq/cmit/HPClientDriverPackCatalog.cab";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly CatalogCacheManager _cache;
    private readonly ResumeableDownloader _downloader;
    private XDocument? _catalog;

    /// <summary>SoftPaq Id ("sp123456") → cached SoftPaq element, built once.</summary>
    private Dictionary<string, XElement>? _softpaqById;

    /// <summary>SystemId (lower hex, no leading zeros stripped) → list of
    /// ProductOSDriverPack rows for that SystemId. SystemId fields in the
    /// catalogue are comma-separated; we explode them so callers can lookup
    /// by a single ID.</summary>
    private Dictionary<string, List<XElement>>? _pkgsBySystemId;

    /// <summary>Per-call diagnostic, mirrors the Dell / Lenovo pattern.</summary>
    public string LastAttemptLog { get; private set; } = "";

    public HpDriverService(CatalogCacheManager cache,
                           ResumeableDownloader downloader)
    {
        _cache      = cache;
        _downloader = downloader;
    }

    public DriverVendor Vendor => DriverVendor.HP;

    public async Task EnsureCatalogAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        const string key = "HPClientDriverPackCatalog.cab";
        if (!_cache.TryGetValid(CatalogCacheManager.Category.HP, key,
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
                Vendor        = "HP",
                Notes         = "HP Client Driver Pack catalogue"
            });
        }

        var extractDir = cabPath + ".extracted";
        var xmlPath    = Path.Combine(extractDir, "HPClientDriverPackCatalog.xml");
        if (!File.Exists(xmlPath))
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            await CabExtractor.ExtractAsync(cabPath, extractDir, ct);
        }
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException(
                "HPClientDriverPackCatalog.xml not produced by CAB extraction.", xmlPath);

        _catalog       = XDocument.Load(xmlPath);
        _softpaqById   = BuildSoftpaqIndex(_catalog);
        _pkgsBySystemId = BuildSystemIdIndex(_catalog);
        LastAttemptLog = $"HP catalogue loaded: {_pkgsBySystemId.Count} SystemIds " +
                         $"(release {_catalog.Root?.Attribute("DateReleased")?.Value}).";
    }

    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default) => ListModelsAsync(includeNonWin11: false, ct);

    /// <summary>
    /// By default only models with at least one Windows 11 driver pack appear.
    /// Pass <paramref name="includeNonWin11"/>=true to surface every model in
    /// the catalogue (useful for Win10 builds or legacy fleets).
    /// </summary>
    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        bool includeNonWin11, CancellationToken ct = default)
    {
        EnsureLoaded();

        // Each ProductOSDriverPack carries SystemId (possibly comma-separated)
        // + SystemName + OSName. Deduplicate by (SystemId, SystemName) so the
        // picker shows one row per real model.
        var seen = new Dictionary<(string SystemId, string Name), DriverPackModel>(
            new TupleCi());

        foreach (var pkg in _catalog!.Descendants("ProductOSDriverPack"))
        {
            ct.ThrowIfCancellationRequested();
            var osName = (pkg.Element("OSName")?.Value ?? "").Trim();
            if (!includeNonWin11 && !osName.Contains("Windows 11"))
                continue;

            var name      = (pkg.Element("SystemName")?.Value ?? "").Trim();
            var family    = (pkg.Element("ProductType")?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name)) continue;

            foreach (var sid in ExplodeSystemIds(pkg.Element("SystemId")?.Value))
            {
                var brand = DeriveBrand(name);
                var key   = (sid, name);
                if (!seen.ContainsKey(key))
                    seen[key] = new DriverPackModel(DriverVendor.HP, sid, name, brand);
            }
        }

        IReadOnlyList<DriverPackModel> result = seen.Values
            .OrderBy(m => m.Brand ?? "")
            .ThenBy(m => m.Name)
            .ThenBy(m => m.SystemId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<DriverPack?> GetDriverPackAsync(
        string systemId, string osVersion, CancellationToken ct = default)
    {
        EnsureLoaded();

        var wantWin11 = osVersion.ToLowerInvariant() switch
        {
            var v when v.Contains("11") => true,
            var v when v.Contains("10") => false,
            _ => true   // default to Win11 — caller-side tokens are Win11-biased
        };

        var sidKey = systemId.Trim().ToLowerInvariant();
        if (!_pkgsBySystemId!.TryGetValue(sidKey, out var rows) || rows.Count == 0)
        {
            LastAttemptLog =
                $"SystemId {systemId} not present in HP Client Driver Pack catalogue. " +
                "Catalogue covers HP commercial SKUs only (EliteBook / ProBook / " +
                "ZBook / EliteDesk / ProDesk / Z-series / Engage). " +
                "Consumer SKUs (Pavilion / OMEN / ENVY / Stream / Spectre) are not " +
                "published — use the manual Driver Injection card above.";
            return Task.FromResult<DriverPack?>(null);
        }

        // Preference order:
        //   1. Latest Windows 11 pack (newest DateReleased among Win11 rows).
        //   2. If wantWin11==false, latest Windows 10 pack.
        //   3. Otherwise, fall back to latest Windows 10 pack (most older HP
        //      models still have one) and surface a note in the log.
        var win11 = rows.Where(r => (r.Element("OSName")?.Value ?? "")
                                        .Contains("Windows 11")).ToList();
        var win10 = rows.Where(r => (r.Element("OSName")?.Value ?? "")
                                        .Contains("Windows 10")).ToList();

        XElement? chosenPkg = null;
        string chosenOs     = "";
        bool fellBack       = false;

        if (wantWin11 && win11.Count > 0)
        {
            chosenPkg = PickNewest(win11, out chosenOs);
        }
        else if (!wantWin11 && win10.Count > 0)
        {
            chosenPkg = PickNewest(win10, out chosenOs);
        }
        else if (wantWin11 && win10.Count > 0)
        {
            chosenPkg = PickNewest(win10, out chosenOs);
            fellBack  = true;
        }

        if (chosenPkg == null)
        {
            var have = rows.Select(r => r.Element("OSName")?.Value ?? "")
                           .Where(s => !string.IsNullOrEmpty(s))
                           .Distinct()
                           .ToList();
            LastAttemptLog =
                $"HP publishes no driver pack matching the requested OS for SystemId {systemId}. " +
                (have.Count > 0
                    ? $"Available: {string.Join("; ", have.Take(6))}. "
                    : "Catalogue lists no OS entries for this SystemId. ") +
                "Use the manual Driver Injection card above.";
            return Task.FromResult<DriverPack?>(null);
        }

        var softpaqId = chosenPkg.Element("SoftPaqId")?.Value?.Trim();
        if (string.IsNullOrEmpty(softpaqId) ||
            !_softpaqById!.TryGetValue(softpaqId, out var sp))
        {
            LastAttemptLog =
                $"HP catalogue lists SystemId {systemId} but the matching SoftPaq " +
                $"({softpaqId}) is missing from the SoftPaqList section.";
            return Task.FromResult<DriverPack?>(null);
        }

        var url    = sp.Element("Url")?.Value?.Trim() ?? "";
        var name   = chosenPkg.Element("SystemName")?.Value?.Trim() ?? systemId;
        var ver    = sp.Element("Version")?.Value?.Trim() ?? "";
        var sha256 = sp.Element("SHA256")?.Value?.Trim();
        var md5    = sp.Element("MD5")?.Value?.Trim();
        long.TryParse(sp.Element("Size")?.Value, out var size);
        DateTime.TryParse(sp.Element("DateReleased")?.Value, out var date);

        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            LastAttemptLog =
                $"HP catalogue listed SoftPaq {softpaqId} for {systemId} but with " +
                "no/empty download URL.";
            return Task.FromResult<DriverPack?>(null);
        }

        LastAttemptLog =
            $"matched HP {name} ({systemId}) {chosenOs} → {Path.GetFileName(parsed.LocalPath)}" +
            (fellBack ? " (Win10 fallback — Win11 pack not published)" : "");

        return Task.FromResult<DriverPack?>(new DriverPack(
            Vendor:       DriverVendor.HP,
            Model:        name,
            SystemId:     systemId,
            OsVersion:    osVersion,
            Version:      ver,
            ReleaseDate:  date,
            DownloadUrl:  url,
            SizeBytes:    size,
            Sha256:       sha256?.ToLowerInvariant(),
            Md5:          md5?.ToLowerInvariant(),
            Filename:     Path.GetFileName(parsed.LocalPath)));
    }

    private static XElement? PickNewest(List<XElement> rows, out string osName)
    {
        var best = rows
            .Select(r => new
            {
                Row  = r,
                Date = DateTime.TryParse(r.Element("DateReleased")?.Value, out var d)
                       ? d : DateTime.MinValue
            })
            .OrderByDescending(x => x.Date)
            .FirstOrDefault();
        osName = best?.Row.Element("OSName")?.Value ?? "";
        return best?.Row;
    }

    private static Dictionary<string, XElement> BuildSoftpaqIndex(XDocument doc)
    {
        var idx = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in doc.Descendants("SoftPaq"))
        {
            var id = sp.Element("Id")?.Value?.Trim();
            if (!string.IsNullOrEmpty(id)) idx[id] = sp;
        }
        return idx;
    }

    private static Dictionary<string, List<XElement>> BuildSystemIdIndex(XDocument doc)
    {
        // SystemId in HP's catalogue is comma-separated when a single model
        // has multiple BaseBoard product IDs (e.g. "81c3,8396"). Explode so
        // a single-ID lookup matches every variant.
        var idx = new Dictionary<string, List<XElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in doc.Descendants("ProductOSDriverPack"))
        {
            foreach (var sid in ExplodeSystemIds(pkg.Element("SystemId")?.Value))
            {
                if (!idx.TryGetValue(sid, out var list))
                    idx[sid] = list = new List<XElement>();
                list.Add(pkg);
            }
        }
        return idx;
    }

    private static IEnumerable<string> ExplodeSystemIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim().ToLowerInvariant();
            if (s.Length > 0) yield return s;
        }
    }

    private static string? DeriveBrand(string systemName)
    {
        // Brand grouping: first recognised commercial line in the SystemName,
        // else the first space-separated token. Keeps the picker readable.
        var families = new[]
        {
            "EliteBook", "ProBook", "ZBook", "EliteDesk", "ProDesk",
            "Z2 Mini", "Z2", "Z4", "Z6", "Z8",
            "Engage", "Pro Mini", "Elite Mini", "Mini", "Spectre"
        };
        foreach (var f in families)
            if (systemName.Contains(f, StringComparison.OrdinalIgnoreCase))
                return f;
        return systemName.Split(' ', 2).FirstOrDefault();
    }

    private void EnsureLoaded()
    {
        if (_catalog == null || _softpaqById == null || _pkgsBySystemId == null)
            throw new InvalidOperationException(
                "HpDriverService.EnsureCatalogAsync must be awaited before " +
                "ListModelsAsync / GetDriverPackAsync.");
    }

    private sealed class TupleCi : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) o) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(o.Item1 ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(o.Item2 ?? ""));
    }
}
