using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Targeted reader for the Microsoft Update Catalog web UI
/// (<c>https://www.catalog.update.microsoft.com/</c>). This is what every
/// Windows-update automation tool (PSWindowsUpdate, Maurice Daly's tooling,
/// UUP Dump, etc.) actually uses in production — far faster than walking
/// the full ~900 MB <c>wsusscn2.cab</c> via WUA when all we want is a few
/// specific KBs.
///
/// Two operations:
///   1. SearchAsync(query)               → list of CatalogItem hits.
///   2. ResolveDownloadUrlAsync(updateId) → direct CDN .msu / .cab URL.
///
/// The catalog HTML structure is stable (unchanged since ~2018). The
/// DownloadDialog endpoint returns a small HTML page with the CDN URL
/// embedded in JavaScript; we extract via regex.
/// </summary>
public sealed class MsCatalogWebService : IDisposable
{
    private readonly HttpClient _http;

    public MsCatalogWebService()
    {
        var handler = new HttpClientHandler
        {
            UseProxy                = true,
            Proxy                   = WebRequest.GetSystemWebProxy(),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression  = DecompressionMethods.All,
            AllowAutoRedirect       = true,
            UseCookies              = true,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        // The catalog rejects requests with no User-Agent; mimic a browser.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GoldenISOBuilder/1.0");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<IReadOnlyList<CatalogItem>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var url = "https://www.catalog.update.microsoft.com/Search.aspx?q=" +
                  Uri.EscapeDataString(query);
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseSearchResults(html);
    }

    /// <summary>
    /// POSTs the GUID to DownloadDialog.aspx and extracts the direct CDN URL
    /// from the response. Returns null if the catalog doesn't expose one (rare).
    /// </summary>
    public async Task<string?> ResolveDownloadUrlAsync(
        string updateId, CancellationToken ct = default)
    {
        // The catalog's JS posts an updateIDs JSON-ish array. We hand-build it
        // with form-urlencoded encoding to avoid pulling in a JSON dependency.
        var payload =
            "updateIDs=" + Uri.EscapeDataString(
                $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\"," +
                $"\"updateID\":\"{updateId}\"}}]");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://www.catalog.update.microsoft.com/DownloadDialog.aspx");
        req.Content = new StringContent(payload, Encoding.UTF8,
            "application/x-www-form-urlencoded");
        req.Headers.Referrer = new Uri(
            "https://www.catalog.update.microsoft.com/Search.aspx");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);

        // The CDN URL appears inside a JS string literal like:
        //   downloadInformation[0].files[0].url = 'https://catalog.s.download...';
        // …or as a bare http(s) link ending in .msu / .cab.
        var m = Regex.Match(html,
            "(http[s]?://[^'\"\\s]+\\.(?:msu|cab))",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Parses the search-results page. The catalog renders one
    /// <c>tr id="...{guid}_R..."</c> per hit. Title lives in an
    /// <c>a id="...{guid}_link"</c> anchor. Size and date sit in fixed-position
    /// cells. The GUID inside the row id is the UpdateID we POST back to
    /// DownloadDialog.aspx.
    /// </summary>
    private static List<CatalogItem> ParseSearchResults(string html)
    {
        var results = new List<CatalogItem>();

        // Each row has the form  <tr id="GUID_R0" ...>...</tr>
        var rowRe = new Regex(
            "<tr[^>]*id=[\"']([0-9a-fA-F-]+)_R\\d+[\"'][^>]*>(.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var titleRe = new Regex(
            "<a[^>]*id=[\"'][0-9a-fA-F-]+_link[\"'][^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var cellRe = new Regex(
            "<td[^>]*>(.*?)</td>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match row in rowRe.Matches(html))
        {
            var guid    = row.Groups[1].Value;
            var inner   = row.Groups[2].Value;
            var titleM  = titleRe.Match(inner);
            if (!titleM.Success) continue;
            var title   = HtmlDecode(titleM.Groups[1].Value).Trim();

            // Fixed-position cells: 1=title, 2=products, 3=classification,
            // 4=last updated, 5=version, 6=size.
            var cells = cellRe.Matches(inner);
            string classification = "", lastUpdated = "", size = "";
            if (cells.Count >= 3) classification = StripHtml(cells[2].Groups[1].Value).Trim();
            if (cells.Count >= 4) lastUpdated    = StripHtml(cells[3].Groups[1].Value).Trim();
            if (cells.Count >= 6) size           = StripHtml(cells[5].Groups[1].Value).Trim();

            var kbMatch = Regex.Match(title, @"KB\d+", RegexOptions.IgnoreCase);
            var kbId    = kbMatch.Success ? kbMatch.Value.ToUpperInvariant() : "";

            results.Add(new CatalogItem(
                UpdateId:       guid,
                KbId:           kbId,
                Title:          title,
                Classification: classification,
                LastUpdated:    lastUpdated,
                SizeText:       size));
        }
        return results;
    }

    private static string StripHtml(string s) =>
        Regex.Replace(s, "<.*?>", "", RegexOptions.Singleline);

    private static string HtmlDecode(string s) =>
        System.Net.WebUtility.HtmlDecode(StripHtml(s));

    public void Dispose() => _http.Dispose();
}

/// <summary>A single search-result row from the Microsoft Update Catalog.</summary>
public sealed record CatalogItem(
    string UpdateId,         // GUID — POST this to DownloadDialog.aspx
    string KbId,             // "KB5089549" — extracted from title
    string Title,
    string Classification,   // "Security Updates" / "Updates" / "Feature Packs"
    string LastUpdated,
    string SizeText);        // free-form, e.g. "871.4 MB"
