using System.IO;
using System.Xml.Linq;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Reads Lenovo's <b>CDRT SCCM driver-pack catalogue</b> at
/// <c>https://download.lenovo.com/cdrt/td/catalogv2.xml</c>. This is the
/// canonical source for OS-deployment-ready bundled driver packs — the
/// Dell-equivalent of DriverPackCatalog.cab. Maintained by Lenovo's Customer
/// Deployment Readiness Toolkit team; consumed by Maurice Daly's Driver
/// Automation Tool, MSEndpointMgr DAT, and Lenovo's own LCSM PowerShell
/// module (<c>Get-LnvDriverPack</c>).
///
/// Earlier versions of this service hit the wrong catalogue
/// (<c>download.lenovo.com/catalog/&lt;MT&gt;_&lt;OS&gt;.xml</c>), which is the
/// Update Retriever / Thin Installer SoftPaq feed — a list of individual
/// driver components designed for in-OS update agents, not OSD slipstreaming.
/// Switching to catalogv2.xml gives Lenovo the same one-click UX as Dell.
///
/// Schema (verified live 2026-05-13):
///   &lt;ModelList version="1.0"&gt;
///     &lt;Model name="ThinkPad T495" arch="AMD"&gt;
///       &lt;Types&gt;&lt;Type&gt;20NJ&lt;/Type&gt;&lt;Type&gt;20NK&lt;/Type&gt;&lt;/Types&gt;
///       &lt;BIOS …/&gt;
///       &lt;SCCM os="win11" version="24H2" date="2023-03-30"
///             crc="060d6b…" md5="be8b55…"&gt;
///         https://download.lenovo.com/pccbbs/mobiles/tp_t495_…exe
///       &lt;/SCCM&gt;
///       &lt;HSA … /&gt;
///     &lt;/Model&gt;
///   &lt;/ModelList&gt;
///
/// Coverage limit: ThinkPad / ThinkCentre / ThinkStation only. IdeaPad,
/// Legion, ThinkBook, Yoga consumer SKUs are NOT in this catalogue — Lenovo
/// doesn't publish bundled OSD packs for those (the per-driver SoftPaq feed
/// + Lenovo Vantage at runtime is the supported path). The UI surfaces this
/// explicitly when a missing MT is requested.
/// </summary>
public sealed class LenovoDriverService : IDriverPackService
{
    public const string CatalogUrl =
        "https://download.lenovo.com/cdrt/td/catalogv2.xml";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    private readonly CatalogCacheManager _cache;
    private readonly ResumeableDownloader _downloader;

    private XDocument? _catalog;
    private DateTime _catalogDownloadedUtc;

    /// <summary>Per-call diagnostic trail for the UI to surface when the
    /// fetch returns null. The UI shows this verbatim in the Step 2 status
    /// text so the admin knows exactly what happened.</summary>
    public string LastAttemptLog { get; private set; } = "";

    public LenovoDriverService(CatalogCacheManager cache,
                               ResumeableDownloader downloader)
    {
        _cache      = cache;
        _downloader = downloader;
    }

    public DriverVendor Vendor => DriverVendor.Lenovo;

    public async Task EnsureCatalogAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        const string key = "catalogv2.xml";
        if (!_cache.TryGetValid(CatalogCacheManager.Category.Lenovo, key,
                                out var xmlPath, out var manifest))
        {
            var result = await _downloader.DownloadAsync(
                CatalogUrl, xmlPath, expectedSha256: null, progress, ct);
            _cache.WriteManifest(xmlPath, new CacheManifest
            {
                SourceUrl     = CatalogUrl,
                Sha256        = result.Sha256,
                SizeBytes     = result.SizeBytes,
                DownloadedUtc = DateTime.UtcNow,
                ExpiresUtc    = DateTime.UtcNow.Add(CacheLifetime),
                Vendor        = "Lenovo",
                Notes         = "Lenovo CDRT SCCM driver-pack catalogue"
            });
            _catalogDownloadedUtc = DateTime.UtcNow;
        }
        else
        {
            _catalogDownloadedUtc = manifest?.DownloadedUtc ?? DateTime.UtcNow;
        }

        _catalog = XDocument.Load(xmlPath);
    }

    public Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default)
    {
        EnsureLoaded();

        // Each <Model> may have multiple <Type> children — each MT becomes a
        // separate row in the picker so the user can filter by their actual
        // 4-char Machine Type. Brand grouping = first word of @name.
        var models = new List<DriverPackModel>();
        foreach (var modelEl in _catalog!.Descendants("Model"))
        {
            ct.ThrowIfCancellationRequested();
            var name  = modelEl.Attribute("name")?.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            var brand = name.Split(' ', 2)[0];   // ThinkPad / ThinkCentre / ThinkStation

            foreach (var typeEl in modelEl.Descendants("Type"))
            {
                var mt = typeEl.Value?.Trim();
                if (string.IsNullOrEmpty(mt)) continue;
                models.Add(new DriverPackModel(
                    DriverVendor.Lenovo, mt, name, brand));
            }
        }

        IReadOnlyList<DriverPackModel> result = models
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

        // Find every <Model> that lists this MT in its <Type>s, then pick the
        // most appropriate <SCCM> child. Preferences:
        //   1. Exact os ("win11") AND version match (the user's target build).
        //   2. Exact os ("win11"), any version (Lenovo recycles older packs).
        //   3. Anything else => no match, with a clear reason.
        var wantOs = MapOsCode(osVersion);

        var candidates = new List<(XElement Model, XElement Sccm, DateTime Date)>();

        foreach (var modelEl in _catalog!.Descendants("Model"))
        {
            ct.ThrowIfCancellationRequested();
            var matchesMt = modelEl.Descendants("Type")
                .Any(t => string.Equals(t.Value?.Trim(), systemId,
                                        StringComparison.OrdinalIgnoreCase));
            if (!matchesMt) continue;

            foreach (var sccm in modelEl.Descendants("SCCM"))
            {
                var os = sccm.Attribute("os")?.Value?.Trim();
                if (!string.Equals(os, wantOs, StringComparison.OrdinalIgnoreCase))
                    continue;
                DateTime.TryParse(sccm.Attribute("date")?.Value, out var date);
                candidates.Add((modelEl, sccm, date));
            }
        }

        if (candidates.Count == 0)
        {
            // Find out whether the MT was even in the catalogue.
            var anyMt = _catalog.Descendants("Type")
                                .Any(t => string.Equals(
                                    t.Value?.Trim(), systemId,
                                    StringComparison.OrdinalIgnoreCase));
            LastAttemptLog = anyMt
                ? $"MT {systemId} present in catalogue but no {wantOs} driver " +
                  "pack published. Lenovo's CDRT team usually adds Win11 packs " +
                  "within weeks of model release; try Refresh, or use the manual " +
                  "Driver Injection card above with a pack from support.lenovo.com."
                : $"MT {systemId} not in Lenovo CDRT catalogue " +
                  $"(downloaded {_catalogDownloadedUtc:yyyy-MM-dd}). " +
                  "The CDRT catalogue covers ThinkPad / ThinkCentre / ThinkStation only — " +
                  "IdeaPad / Legion / Yoga consumer SKUs are not included. " +
                  "Use the manual Driver Injection card above.";
            return Task.FromResult<DriverPack?>(null);
        }

        // Prefer exact-version match if the user passed something specific.
        // Step2Page passes "Win11" (build-less) so this lookup mostly takes
        // the newest pack for any version. Still, support the case where a
        // future caller passes "Win11 24H2" or similar.
        var requestedVersion = ExtractVersionToken(osVersion);
        var best = candidates
            .OrderByDescending(c =>
                requestedVersion != null &&
                string.Equals(c.Sccm.Attribute("version")?.Value, requestedVersion,
                              StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Date)
            .First();

        var url = best.Sccm.Value.Trim();
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            LastAttemptLog = $"catalogue listed an empty / malformed pack URL for MT {systemId}.";
            return Task.FromResult<DriverPack?>(null);
        }

        var name        = best.Model.Attribute("name")?.Value?.Trim() ?? systemId;
        var version     = best.Sccm.Attribute("version")?.Value?.Trim() ?? "";
        var md5         = best.Sccm.Attribute("md5")?.Value?.Trim();
        var crc         = best.Sccm.Attribute("crc")?.Value?.Trim();   // sha256 in CDRT schema
        var filename    = Path.GetFileName(parsed.LocalPath);

        // Pack size isn't published in catalogv2.xml — leave 0, the downloader
        // reports the actual Content-Length once it starts.
        LastAttemptLog = $"matched {name} ({systemId}) Win11 {version} → {filename}";
        return Task.FromResult<DriverPack?>(new DriverPack(
            Vendor:       DriverVendor.Lenovo,
            Model:        name,
            SystemId:     systemId,
            OsVersion:    osVersion,
            Version:      version,
            ReleaseDate:  best.Date,
            DownloadUrl:  url,
            SizeBytes:    0,
            Sha256:       crc,    // 'crc' attribute is actually SHA-256 per CDRT
            Md5:          md5,
            Filename:     filename));
    }

    private static string MapOsCode(string osVersion)
    {
        // Caller passes "Win11" / "win11" / "Windows11" / etc. CDRT uses
        // lowercase "win11" / "win10".
        var v = osVersion.Trim().ToLowerInvariant();
        if (v.Contains("11")) return "win11";
        if (v.Contains("10")) return "win10";
        return v;
    }

    private static string? ExtractVersionToken(string osVersion)
    {
        // Pull a version-style token from inputs like "Win11 24H2".
        var m = System.Text.RegularExpressions.Regex.Match(
            osVersion, @"\d{2}[Hh]\d");
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    private void EnsureLoaded()
    {
        if (_catalog == null)
            throw new InvalidOperationException(
                "LenovoDriverService.EnsureCatalogAsync must be awaited before " +
                "ListModelsAsync / GetDriverPackAsync.");
    }
}
