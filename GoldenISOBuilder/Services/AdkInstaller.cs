using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace GoldenISOBuilder.Services;

public enum AdkInstallState { Idle, Downloading, Installing, Verifying, Done, Failed }

public class AdkInstallProgress
{
    public AdkInstallState State    { get; init; }
    public string          Message  { get; init; } = "";
    /// <summary>Download/install percent 0–100, or -1 if indeterminate.</summary>
    public int             Percent  { get; init; } = -1;
}

public class AdkInstallResult
{
    public bool   Success        { get; init; }
    public string? OscdimgPath   { get; init; }
    public string? AdkVersion    { get; init; }
    public string? Error         { get; init; }
}

/// <summary>
/// Downloads the Windows ADK bootstrapper from Microsoft and runs it silently
/// with only the Deployment Tools feature selected (~250 MB on disk, ~5 minute
/// install). Returns the path to oscdimg.exe on success.
/// </summary>
public class AdkInstaller
{
    // Microsoft's permanent FWLink for the latest Windows 11 ADK adksetup.exe.
    // adksetup.exe is a small bootstrapper (~2 MB) that downloads the rest at
    // install-time. /features OptionId.DeploymentTools restricts to just
    // oscdimg + DISM-related tools.
    private const string AdkSetupUrl =
        "https://go.microsoft.com/fwlink/?linkid=2289980";

    private static readonly string DownloadDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoldenISOBuilder", "Downloads");

    private static readonly string SetupPath = Path.Combine(DownloadDir, "adksetup.exe");

    public async Task<AdkInstallResult> InstallAsync(
        Action<AdkInstallProgress> onProgress,
        CancellationToken ct)
    {
        try
        {
            // ── 1) Download bootstrapper ─────────────────────────────────────
            Directory.CreateDirectory(DownloadDir);

            onProgress(new AdkInstallProgress {
                State = AdkInstallState.Downloading,
                Message = "Downloading ADK bootstrapper from Microsoft…",
                Percent = 0
            });

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using (var response = await http.GetAsync(AdkSetupUrl,
                       HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;

                using var src = await response.Content.ReadAsStreamAsync(ct);
                using var dst = File.Create(SetupPath);

                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct);
                    read += n;
                    int pct = total.HasValue ? (int)(read * 100 / total.Value) : -1;
                    onProgress(new AdkInstallProgress {
                        State = AdkInstallState.Downloading,
                        Message = total.HasValue
                            ? $"Downloading bootstrapper… {read / 1024} KB / {total / 1024} KB"
                            : $"Downloading bootstrapper… {read / 1024} KB",
                        Percent = pct
                    });
                }
            }

            if (!File.Exists(SetupPath) || new FileInfo(SetupPath).Length < 100_000)
                throw new Exception("Downloaded file is too small or missing — download may have failed.");

            // ── 2) Run silent install ────────────────────────────────────────
            onProgress(new AdkInstallProgress {
                State = AdkInstallState.Installing,
                Message = "Installing Deployment Tools (this can take 3–5 minutes)…",
                Percent = -1
            });

            var psi = new ProcessStartInfo(SetupPath,
                "/features OptionId.DeploymentTools /quiet /ceip off /norestart")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi)
                ?? throw new Exception("Could not start adksetup.exe");

            // Register cancellation: kill the installer if user cancels
            using var killOnCancel = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            // Read stderr concurrently with WaitForExitAsync to prevent pipe-buffer deadlock
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (proc.ExitCode != 0 && proc.ExitCode != 3010 /*reboot required*/)
            {
                throw new Exception(
                    $"adksetup.exe exited with code {proc.ExitCode}.\n{stderr.Trim()}");
            }

            // ── 3) Verify oscdimg.exe is now where we expect ──────────────────
            onProgress(new AdkInstallProgress {
                State = AdkInstallState.Verifying,
                Message = "Verifying installation…",
                Percent = -1
            });

            var oscdimg = BuildEngine.FindOscdimg();
            if (oscdimg == null || !File.Exists(oscdimg))
                throw new Exception(
                    "ADK install reported success but oscdimg.exe was not found. " +
                    "Install may need a system restart.");

            string version = "(installed)";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(oscdimg);
                version = info.ProductVersion ?? info.FileVersion ?? version;
            }
            catch { /* non-fatal */ }

            onProgress(new AdkInstallProgress {
                State = AdkInstallState.Done,
                Message = $"ADK installed successfully ({version})",
                Percent = 100
            });

            return new AdkInstallResult {
                Success = true,
                OscdimgPath = oscdimg,
                AdkVersion = version
            };
        }
        catch (OperationCanceledException)
        {
            return new AdkInstallResult { Success = false, Error = "Cancelled by user." };
        }
        catch (Exception ex)
        {
            onProgress(new AdkInstallProgress {
                State = AdkInstallState.Failed,
                Message = ex.Message,
                Percent = -1
            });
            return new AdkInstallResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>True if the ADK Deployment Tools (oscdimg specifically) is installed.</summary>
    public static bool IsInstalled() => BuildEngine.FindOscdimg() != null;

    public static string? GetVersion()
    {
        var p = BuildEngine.FindOscdimg();
        if (p == null || !File.Exists(p)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch { return null; }
    }
}
