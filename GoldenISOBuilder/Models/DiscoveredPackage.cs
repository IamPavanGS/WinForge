namespace GoldenISOBuilder.Models;

/// <summary>
/// A provisioned Appx package discovered by DISM /Get-ProvisionedAppxPackages
/// from the ISO's install.wim — used for dynamic bloatware selection in Step 3.
/// </summary>
public class DiscoveredPackage
{
    public string PackageName  { get; set; } = "";   // Full package name with version
    public string DisplayName  { get; set; } = "";   // Friendly name (from DISM or derived)
    public string Version      { get; set; } = "";
    public string Architecture { get; set; } = "";

    /// <summary>True if the package name prefix matches the curated known-bloat list.</summary>
    public bool IsKnownBloat   { get; set; }
}
