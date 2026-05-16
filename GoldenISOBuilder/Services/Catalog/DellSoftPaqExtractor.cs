using System.Diagnostics;
using System.IO;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Extracts Dell enterprise driver packs into a folder DISM can consume via
/// <c>/Add-Driver /Recurse</c>. Handles both shapes Dell publishes today:
///
///   • <b>.exe</b> self-extractor (modern, ~2017+). Silent flags
///     <c>/S /E=&lt;dest&gt;</c> — same syntax Maurice Daly's Driver Automation
///     Tool, Microsoft Endpoint Manager's driver-pack importer and Dell's own
///     <c>Dell Command | Deploy</c> use.
///   • <b>.cab</b> archive (legacy, pre-2017). Extracted via the existing
///     WiX-DTF <see cref="CabExtractor"/>, falling back to <c>expand.exe</c>.
///
/// Post-extract validation mirrors the Lenovo/HP extractors: the produced
/// folder must contain at least one <c>.inf</c> file AND total &gt;50 MB.
/// Anything less and we raise a clear error pointing the user at the manual
/// driver-folder card.
///
/// Confirmed footgun: Dell's SFX silently fails (exit 0, zero output) if
/// <c>/E=</c> has a trailing backslash on some pack vintages. We strip
/// trailing separators from the destination path before invoking.
/// </summary>
public static class DellSoftPaqExtractor
{
    public static async Task<string> ExtractAsync(
        string packPath, string destDir, CancellationToken ct = default)
    {
        if (!File.Exists(packPath))
            throw new FileNotFoundException("Dell driver pack not found.", packPath);

        // Purge — the Lenovo extractor's defensive pattern. Dell's SFX is more
        // forgiving than Inno's EAbort bug but a clean destination guarantees
        // the post-extract .inf count reflects only this pack. Recursive delete
        // of a previously-extracted pack is heavy synchronous I/O; off the UI
        // thread it goes.
        await Task.Run(() =>
        {
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); }
                catch (IOException) { /* file in use — best-effort */ }
            }
            Directory.CreateDirectory(destDir);
        }, ct);

        var ext = Path.GetExtension(packPath).ToLowerInvariant();

        if (ext == ".cab")
        {
            // Pure CAB: hand straight to CabExtractor. No silent flags needed.
            await CabExtractor.ExtractAsync(packPath, destDir, ct);
        }
        else
        {
            // EXE self-extractor (the common case).
            await RunSfxExtractAsync(packPath, destDir, ct);
        }

        // Walking a fully-extracted Dell driver tree (10k-30k files) on the UI
        // thread freezes the wizard — run on the thread pool.
        var (infs, bytes) = await Task.Run(() =>
        {
            var infCount = Directory.EnumerateFiles(destDir, "*.inf", SearchOption.AllDirectories)
                                    .Count();
            var totalBytes = new DirectoryInfo(destDir)
                                 .EnumerateFiles("*", SearchOption.AllDirectories)
                                 .Sum(f => f.Length);
            return (infCount, totalBytes);
        }, ct);

        if (infs == 0 || bytes < 50_000_000)
            throw new IOException(
                $"Dell driver pack extracted to {destDir} but produced {infs} " +
                $".inf file(s) / {bytes / 1024 / 1024} MB. " +
                "This pack may use a non-standard wrapper. " +
                "Download manually from dell.com/support and use the Driver " +
                "Injection card above.");

        return destDir;
    }

    private static async Task RunSfxExtractAsync(
        string exePath, string destDir, CancellationToken ct)
    {
        // Trailing backslash on /E= confuses older Dell SFX builds. Strip it.
        var cleanDest = destDir.TrimEnd(Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar);
        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            // /S  — silent (no UI)
            // /E= — extract only, no install attempt
            Arguments              = $"/S /E=\"{cleanDest}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var p = Process.Start(psi)
            ?? throw new IOException("Could not launch Dell driver pack EXE.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                "Dell driver pack extract exceeded 10 minutes; aborted.");
        }

        // Dell's SFX returns 0 on clean success. Like HP's SoftPaq, in
        // extract-only mode it may also return non-zero codes for housekeeping
        // failures that don't affect the extracted output. Trust the
        // post-extract validation in the caller as the authoritative signal.
    }
}
