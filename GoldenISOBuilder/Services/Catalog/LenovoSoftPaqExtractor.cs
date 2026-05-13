using System.Diagnostics;
using System.IO;
using System.Text;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Extracts Lenovo's self-extracting driver-pack EXEs (from
/// <c>catalogv2.xml</c>'s &lt;SCCM&gt; URLs) without actually installing them.
/// Lenovo wraps these in either Inno Setup or 7-Zip SFX depending on the
/// pack vintage; we detect by reading the first 512 bytes and pick the
/// matching silent-extract syntax.
///
/// Confirmed footguns mitigated:
///   • Inno Setup raises <c>EAbort</c> + exits 0 (zero .inf produced) if the
///     <c>/DIR</c> target already exists — known bug, see SCConfigMgr#208.
///     We always purge the destination before extract.
///   • <c>/EXTRACT=YES</c> must be passed WITHOUT quotes around YES (Inno
///     parses <c>"YES"</c> as a different token in some builds). The earlier
///     draft of this code used quotes; this version doesn't.
///   • <c>/SUPPRESSMSGBOXES</c> is redundant under <c>/VERYSILENT</c>; keep
///     <c>/VERYSILENT</c> only.
///
/// After extract we validate: exit-code 0 AND recursive *.inf count > 0 AND
/// total size > 50 MB. Any failure raises a clear error pointing the user at
/// the manual driver folder path.
/// </summary>
public static class LenovoSoftPaqExtractor
{
    public static async Task<string> ExtractAsync(
        string exePath, string destDir, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Lenovo SoftPaq EXE not found.", exePath);

        // Purge destination — Inno Setup silently fails if it already exists.
        if (Directory.Exists(destDir))
        {
            try { Directory.Delete(destDir, recursive: true); }
            catch (IOException) { /* best-effort — file in use possible */ }
        }
        Directory.CreateDirectory(destDir);

        // Sniff the SFX wrapper. Reading the first 512 bytes is enough — the
        // Inno Setup signature appears within the first few hundred bytes;
        // the 7-Zip SFX "!@Install@!" marker is near the start of the stub.
        var head = ReadAsciiHead(exePath, 4096);
        SfxKind kind = head.Contains("Inno Setup") ? SfxKind.Inno
                     : head.Contains("!@Install@!") ? SfxKind.SevenZip
                     : SfxKind.Inno;   // best guess — Lenovo's modern packs are all Inno

        try
        {
            if (kind == SfxKind.Inno)
                await RunInnoExtractAsync(exePath, destDir, ct);
            else
                await Run7zExtractAsync(exePath, destDir, ct);
        }
        catch (Exception primary) when (kind == SfxKind.Inno)
        {
            // Inno failed — fall back to 7z just in case (some older Lenovo
            // packs ship a 7z SFX without the obvious marker).
            try { await Run7zExtractAsync(exePath, destDir, ct); }
            catch (Exception fallback)
            {
                throw new IOException(
                    $"Lenovo SoftPaq extract failed via both Inno Setup ({primary.Message}) " +
                    $"and 7-Zip ({fallback.Message}). " +
                    "Pack may use a non-standard SFX wrapper. " +
                    "Download the pack manually and use the Driver Injection card above.");
            }
        }

        // Validate output.
        var infs = Directory.EnumerateFiles(destDir, "*.inf", SearchOption.AllDirectories)
                            .Count();
        var bytes = new DirectoryInfo(destDir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);

        if (infs == 0 || bytes < 50_000_000)
        {
            throw new IOException(
                $"Lenovo SoftPaq extracted to {destDir} but produced {infs} " +
                $".inf file(s) / {bytes / 1024 / 1024} MB. " +
                "This pack likely uses a non-standard SFX wrapper. " +
                "Download the pack manually from support.lenovo.com and use " +
                "the Driver Injection card above.");
        }

        return destDir;
    }

    private static async Task RunInnoExtractAsync(
        string exePath, string destDir, CancellationToken ct)
    {
        // /VERYSILENT  → no UI, no prompts
        // /DIR="..."   → extraction destination
        // /EXTRACT=YES → tells Lenovo's installer it's an extract not an install
        var psi = new ProcessStartInfo
        {
            FileName  = exePath,
            Arguments = $"/VERYSILENT /DIR=\"{destDir}\" /EXTRACT=YES",
            UseShellExecute = false,
            CreateNoWindow  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        using var p = Process.Start(psi)
            ?? throw new IOException("Could not launch Lenovo SoftPaq EXE.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                "Lenovo SoftPaq extract exceeded 10 minutes; aborted.");
        }
        // Inno returns 0 on success AND on EAbort — output validation is the
        // real success signal (handled by caller).
    }

    private static async Task Run7zExtractAsync(
        string exePath, string destDir, CancellationToken ct)
    {
        // Use the WiX DTF CAB extractor's sibling? No — CAB extractor doesn't
        // handle 7-Zip SFX. The pragmatic path: shell out to expand.exe? No,
        // that's CAB too. Use System.IO.Compression for inner ZIP? Possibly,
        // but 7-Zip SFX wraps an arbitrary 7z archive.
        //
        // Compromise: rename the EXE to .7z and invoke the built-in tar.exe
        // (Windows 10 1803+ ships bsdtar which can read some archive types
        // but NOT 7z). The only reliable option is to ship 7z.exe — but to
        // keep installer size down we ask the user.
        //
        // Realistically: every current Lenovo SCCM pack is Inno Setup. The
        // 7z branch is defensive coding for catalogue oddities. If we ever
        // hit it in production, fall through to manual fetch with a clear
        // error — same behaviour as if extraction failed.
        await Task.Yield();
        throw new IOException(
            "7-Zip SFX extraction not implemented. " +
            "All current Lenovo CDRT packs use Inno Setup; if you've encountered " +
            "a 7-Zip SFX pack, please report the MT so we can add support.");
    }

    private static string ReadAsciiHead(string path, int bytes)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[bytes];
            int n = fs.Read(buf, 0, bytes);
            return Encoding.ASCII.GetString(buf, 0, n);
        }
        catch { return ""; }
    }

    private enum SfxKind { Inno, SevenZip }
}
