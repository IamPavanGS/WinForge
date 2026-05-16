using System.Diagnostics;
using System.IO;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Silent-extracts HP SoftPaq driver-pack EXEs (sp&lt;NNNN&gt;.exe) downloaded from
/// the HPClientDriverPackCatalog. HP wraps these in its own SoftPaq SFX
/// engine — NOT Inno Setup, NOT 7-Zip SFX — so the silent-extract flags
/// differ from the Lenovo/Dell pattern.
///
/// HP-documented switches (same flags used by HP Image Assistant + the
/// CMSL <c>Out-SoftpaqContent</c> cmdlet):
///   <c>-s</c>  silent
///   <c>-e</c>  extract only (no install attempt)
///   <c>-f"&lt;dest&gt;"</c> destination directory
///
/// Returns 0 on success. Extracted layout is a normal PnP tree
/// (<c>&lt;dest&gt;\&lt;vendor&gt;\&lt;device&gt;\*.inf,*.sys,*.cat</c>) that
/// DISM <c>/Add-Driver /Recurse</c> can consume directly — no further unwrap.
/// </summary>
public static class HpSoftPaqExtractor
{
    public static async Task<string> ExtractAsync(
        string exePath, string destDir, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("HP SoftPaq EXE not found.", exePath);

        // HP's SFX is happy to extract into a pre-existing directory but the
        // pre-commit validator counts .inf files post-extract, so guarantee a
        // clean slate — mirrors the Lenovo extractor's defensive behaviour.
        // Recursive delete of a previously-extracted pack is heavy synchronous
        // I/O; push it off the UI thread.
        await Task.Run(() =>
        {
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); }
                catch (IOException) { /* file in use — best-effort */ }
            }
            Directory.CreateDirectory(destDir);
        }, ct);

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            // -f takes the destination inline with NO space. Quoting the path
            // is required for spaces (e.g. cache under "Local AppData").
            Arguments              = $"-s -e -f\"{destDir}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var p = Process.Start(psi)
            ?? throw new IOException("Could not launch HP SoftPaq EXE.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                "HP SoftPaq extract exceeded 10 minutes; aborted.");
        }

        // HP SoftPaq exit codes are NOT a reliable success signal for -e
        // (extract-only) mode:
        //   0    = clean success
        //   1168 = ERROR_NOT_FOUND — the SFX wrapper extracted the payload
        //          then tried to invoke an inner installer that isn't there
        //          for driver-only packs (very common; not a real error)
        //   3010 = reboot requested (also benign for extract-only)
        //   <other> = could still be partial success
        //
        // The authoritative signal is post-extract file-system state. We
        // record the exit code for diagnostics but defer the success/fail
        // decision to the .inf + size check below — if the pack laid down
        // a valid driver tree, the extract worked regardless of exit code.
        var exitCode = p.ExitCode;

        // Walking a fully-extracted HP driver tree (10k-30k files) on the UI
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
                $"HP SoftPaq extract produced {infs} .inf file(s) / " +
                $"{bytes / 1024 / 1024} MB (exit code {exitCode}). " +
                "This pack may use a non-standard wrapper. " +
                "Download manually from support.hp.com and use the Driver " +
                "Injection card above.");

        return destDir;
    }
}
