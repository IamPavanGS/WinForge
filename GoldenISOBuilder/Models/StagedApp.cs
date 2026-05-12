namespace GoldenISOBuilder.Models;

public class StagedApp
{
    public string  Name             { get; set; } = "";
    public string  FilePath         { get; set; } = "";  // full host path to EXE/MSI
    public string  Args             { get; set; } = "";  // silent install args
    public string  Type             { get; set; } = "exe"; // "exe" or "msi"
    public string? SuccessExitCodes { get; set; }        // null = default "0,3010"

    /// <summary>
    /// Optional Windows Installer transform file (.mst) — only used when Type == "msi".
    /// Full host path; the build engine copies it into Installers\ alongside the .msi
    /// and GIBFirstBoot invokes msiexec with TRANSFORMS="filename.mst".
    /// </summary>
    public string? MstPath { get; set; }

    /// <summary>
    /// Per-app installer timeout in minutes. Default 60 — covers O365 ProPlus and other
    /// long-running enterprise installers. Range 5–240 enforced at runtime.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 60;
}
