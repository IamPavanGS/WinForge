using System.Diagnostics;
using System.IO;
using WixToolset.Dtf.Compression.Cab;

namespace GoldenISOBuilder.Services.Catalog;

/// <summary>
/// Extracts .cab archives. System.IO.Compression on .NET 8 does not support
/// CAB, so we use WiX DTF (managed) as the primary path with expand.exe as a
/// fallback if loading the managed DLL ever fails (e.g. AppLocker / DLL block
/// on the admin's workstation).
/// </summary>
public static class CabExtractor
{
    /// <summary>
    /// Extracts every entry of <paramref name="cabPath"/> into
    /// <paramref name="destinationDir"/>, creating the destination directory.
    /// Returns the destination path.
    /// </summary>
    public static async Task<string> ExtractAsync(
        string cabPath, string destinationDir, CancellationToken ct = default)
    {
        if (!File.Exists(cabPath))
            throw new FileNotFoundException($"CAB not found: {cabPath}", cabPath);

        Directory.CreateDirectory(destinationDir);

        try
        {
            await Task.Run(() => new CabInfo(cabPath).Unpack(destinationDir), ct);
            return destinationDir;
        }
        catch (Exception managed)
        {
            // Fall through to the OS fallback. WiX DTF can occasionally throw
            // for cabs that use rare compression modes — expand.exe is the
            // canonical extractor and ships with every Windows install.
            try
            {
                await ExtractWithExpandAsync(cabPath, destinationDir, ct);
                return destinationDir;
            }
            catch (Exception fallback)
            {
                throw new IOException(
                    $"CAB extraction failed via both WiX DTF and expand.exe. " +
                    $"DTF: {managed.Message} | expand.exe: {fallback.Message}",
                    fallback);
            }
        }
    }

    private static async Task ExtractWithExpandAsync(
        string cabPath, string destinationDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "expand.exe",
            Arguments              = $"-F:* \"{cabPath}\" \"{destinationDir}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        using var proc = Process.Start(psi)
            ?? throw new IOException("expand.exe failed to start.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            throw new IOException(
                $"expand.exe exited with {proc.ExitCode}: {stderr.Trim()}");
        }
    }
}
