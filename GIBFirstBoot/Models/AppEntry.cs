namespace GIBFirstBoot.Models;

public class AppEntry
{
    public string Name      { get; set; } = "";
    public string File      { get; set; } = "";   // filename under Installers\
    public string Args      { get; set; } = "";   // silent install switch(es)
    public string Type      { get; set; } = "exe"; // "exe" | "msi"
    public string? SuccessExitCodes { get; set; }  // comma-separated, default "0"

    /// <summary>
    /// Optional MST transform filename (under Installers\) — only used for MSI installers.
    /// When set, msiexec is invoked with TRANSFORMS="...\Installers\<Mst>".
    /// </summary>
    public string? Mst { get; set; }

    /// <summary>
    /// Installer timeout in minutes. Default 60 (covers O365 ProPlus etc.).
    /// Clamped to [5, 240] to avoid foot-guns.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 60;
}
