namespace GoldenISOBuilder.Models;

/// <summary>
/// Persistable record of a driver pack the auto-fetch flow selected. Lives in
/// Models\ (not Services\Catalog\) so it round-trips through .gibprofile JSON
/// without dragging the catalogue layer into the profile schema.
/// </summary>
public sealed class DriverPackSelection
{
    /// <summary>"Dell" / "HP" / "Lenovo".</summary>
    public string Vendor       { get; set; } = "";
    /// <summary>Vendor-specific identifier (Dell SystemID, HP Platform ID, Lenovo MT).</summary>
    public string SystemId     { get; set; } = "";
    public string ModelName    { get; set; } = "";
    public string OsVersion    { get; set; } = "";
    public string PackVersion  { get; set; } = "";
    public string DownloadUrl  { get; set; } = "";
    /// <summary>Absolute path to the locally cached pack file (set after fetch).</summary>
    public string LocalCabPath { get; set; } = "";
    public string? Sha256      { get; set; }
    public long   SizeBytes    { get; set; }

    /// <summary>True = inject only WinPE-critical PnP classes (Net/Storage/HID)
    /// into boot.wim and the full pack into install.wim. False = full pack into
    /// install.wim only.</summary>
    public bool   InjectWinPECritical { get; set; } = true;
}
