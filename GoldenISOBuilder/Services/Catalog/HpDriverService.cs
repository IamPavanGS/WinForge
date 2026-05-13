using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// HP driver-pack catalogue access via the HP Client Management Script Library
/// (HPCMSL) PowerShell module — HP's only supported programmatic catalogue path.
/// No public REST endpoint exists.
///
/// We spawn <c>powershell.exe -NoProfile</c> out-of-proc rather than embedding
/// <c>Microsoft.PowerShell.SDK</c> in-proc: the SDK is ~70 MB of dependencies
/// that conflicts with WPF in edge cases, and the out-of-proc path is what HP's
/// own samples and the community Driver Automation Tool use.
///
/// Key cmdlets:
///   Get-HPDeviceDetails -Like -Name "EliteBook*"  → enumerate platforms
///   Get-SoftpaqList -Platform 8723 -Os win11 -OsVer 24H2 -Category Driverpack
///                                                  → list driver packs
/// </summary>
public sealed class HpDriverService : IDriverPackService
{
    public DriverVendor Vendor => DriverVendor.HP;

    private readonly CatalogCacheManager _cache;
    private List<DriverPackModel>? _models;

    public HpDriverService(CatalogCacheManager cache,
                           ResumeableDownloader _ = null!)
    {
        // The downloader is accepted for API symmetry with Dell/Lenovo but HP
        // doesn't expose a downloadable catalogue — CMSL handles fetching.
        _cache = cache;
    }

    /// <summary>
    /// Verifies HPCMSL is installed and (if not) installs it. Population of
    /// the in-memory model list happens lazily on first ListModelsAsync.
    /// </summary>
    public async Task EnsureCatalogAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var probe = await RunPsAsync(
            "if (Get-Module -ListAvailable -Name HPCMSL) { 'present' } else { 'missing' }",
            ct);
        if (probe.StdOut.Trim().Equals("present", StringComparison.OrdinalIgnoreCase))
            return;

        // Install for current user — no admin elevation needed.
        var install = await RunPsAsync(
            "try { " +
            "Install-PackageProvider -Name NuGet -Force -Scope CurrentUser -ErrorAction Stop | Out-Null; " +
            "Install-Module -Name HPCMSL -Scope CurrentUser -Force -AcceptLicense -ErrorAction Stop; " +
            "'installed' } catch { 'failed: ' + $_.Exception.Message }",
            ct,
            longRunning: true);
        if (!install.StdOut.Trim().StartsWith("installed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "HP CMSL install failed: " + install.StdOut + "\n" + install.StdErr);
    }

    public async Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default)
    {
        if (_models != null) return _models;

        var probe = await RunPsAsync(
            "Import-Module HPCMSL -ErrorAction Stop; " +
            "Get-HPDeviceDetails -Like -Name '*' | " +
            "Select-Object SystemID,@{n='Name';e={$_.'System Name'}} | " +
            "ConvertTo-Json -Depth 3 -Compress",
            ct,
            longRunning: true);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException(
                "Get-HPDeviceDetails failed: " + probe.StdErr);

        var raw = JsonSerializer.Deserialize<List<HpRawModel>>(probe.StdOut.Trim())
                  ?? new List<HpRawModel>();
        _models = raw.Where(r => !string.IsNullOrEmpty(r.SystemID))
                     .Select(r => new DriverPackModel(
                         DriverVendor.HP, r.SystemID!, r.Name ?? r.SystemID!, null))
                     .OrderBy(m => m.Name)
                     .ToList();
        return _models;
    }

    public async Task<DriverPack?> GetDriverPackAsync(
        string systemId, string osVersion, CancellationToken ct = default)
    {
        // HP os tokens: win11 / win10. Map our canonical "W11" / "Win11"
        // inputs to "win11" so callers can pass the Dell-style code freely.
        var hpOs = osVersion.ToLowerInvariant().StartsWith("w11")
                   || osVersion.Contains("11") ? "win11" : "win10";

        var script =
            "Import-Module HPCMSL -ErrorAction Stop; " +
            $"$packs = Get-SoftpaqList -Platform '{systemId}' -Os {hpOs} " +
            "-Category Driverpack -ErrorAction SilentlyContinue; " +
            "if (-not $packs) { '[]'; exit } " +
            "$packs | Sort-Object -Property ReleaseDate -Descending | " +
            "Select-Object -First 1 -Property Id,Name,Version,ReleaseDate,Url,Size,SHA256 | " +
            "ConvertTo-Json -Depth 4 -Compress";

        var probe = await RunPsAsync(script, ct, longRunning: true);
        if (probe.ExitCode != 0)
            return null;

        var json = probe.StdOut.Trim();
        if (json is "[]" or "" or "null") return null;

        HpRawSoftpaq? p;
        try
        {
            p = JsonSerializer.Deserialize<HpRawSoftpaq>(json);
        }
        catch
        {
            // CMSL sometimes returns an array of one — try that shape.
            var arr = JsonSerializer.Deserialize<List<HpRawSoftpaq>>(json);
            p = arr?.FirstOrDefault();
        }
        if (p == null || string.IsNullOrEmpty(p.Url)) return null;

        DateTime.TryParse(p.ReleaseDate, out var date);

        return new DriverPack(
            Vendor:       DriverVendor.HP,
            Model:        p.Name ?? systemId,
            SystemId:     systemId,
            OsVersion:    osVersion,
            Version:      p.Version ?? "",
            ReleaseDate:  date,
            DownloadUrl:  p.Url,
            SizeBytes:    p.Size ?? 0,
            Sha256:       p.SHA256?.ToLowerInvariant(),
            Md5:          null,
            Filename:     Path.GetFileName(new Uri(p.Url).LocalPath));
    }

    // ── PowerShell host ───────────────────────────────────────────────────

    private static async Task<PsResult> RunPsAsync(
        string command, CancellationToken ct, bool longRunning = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start powershell.exe");

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError .ReadToEndAsync(ct);

        // CMSL Install-Module can take a minute or two on slow links; ordinary
        // queries finish in seconds.
        var timeout = TimeSpan.FromMinutes(longRunning ? 5 : 1);
        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        killCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(killCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new PsResult(proc.ExitCode, await outTask, await errTask);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);

    private sealed class HpRawModel
    {
        [JsonPropertyName("SystemID")] public string? SystemID { get; set; }
        [JsonPropertyName("Name")]     public string? Name     { get; set; }
    }

    private sealed class HpRawSoftpaq
    {
        [JsonPropertyName("Id")]          public string? Id          { get; set; }
        [JsonPropertyName("Name")]        public string? Name        { get; set; }
        [JsonPropertyName("Version")]     public string? Version     { get; set; }
        [JsonPropertyName("ReleaseDate")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("Url")]         public string? Url         { get; set; }
        [JsonPropertyName("Size")]        public long?   Size        { get; set; }
        [JsonPropertyName("SHA256")]      public string? SHA256      { get; set; }
    }
}
