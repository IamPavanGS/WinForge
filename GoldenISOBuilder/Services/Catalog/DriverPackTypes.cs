namespace GoldenISOBuilder.Services.Catalog;

public enum DriverVendor { Dell, HP, Lenovo }

/// <summary>
/// A laptop / desktop / workstation model exposed by an OEM driver catalogue.
/// </summary>
/// <param name="Vendor">Originating OEM.</param>
/// <param name="SystemId">
///   Stable identifier used by the OEM. Dell: 4-char hex BIOS SystemID
///   (e.g. "0BA8"); HP: 4-char hex Platform ID (e.g. "8723"); Lenovo: the
///   4-char MachineType prefix (e.g. "21HD").
/// </param>
/// <param name="Name">Friendly model name (e.g. "Latitude 5550").</param>
/// <param name="Brand">Optional sub-brand grouping ("Latitude" / "EliteBook").</param>
public sealed record DriverPackModel(
    DriverVendor Vendor,
    string SystemId,
    string Name,
    string? Brand);

/// <summary>
/// A driver pack — a vendor-curated archive (usually a CAB or self-extracting
/// EXE) containing every PnP driver for a single model + OS combination.
/// </summary>
public sealed record DriverPack(
    DriverVendor Vendor,
    string  Model,           // friendly name
    string  SystemId,
    string  OsVersion,       // "Win11", "Win10", etc. — vendor-specific token
    string  Version,         // vendor's pack version string
    DateTime ReleaseDate,
    string  DownloadUrl,
    long    SizeBytes,
    string? Sha256,          // null if the vendor only publishes MD5
    string? Md5,             // populated when SHA-256 unavailable
    string  Filename);       // suggested local filename

/// <summary>
/// Uniform read-only API implemented by DellDriverService, HpDriverService
/// and LenovoDriverService so callers can swap vendors without branching.
/// </summary>
public interface IDriverPackService
{
    DriverVendor Vendor { get; }

    /// <summary>Refreshes the cached vendor catalogue if expired.</summary>
    Task EnsureCatalogAsync(IProgress<DownloadProgress>? progress = null,
                            CancellationToken ct = default);

    /// <summary>Enumerates every model the vendor publishes driver packs for.</summary>
    Task<IReadOnlyList<DriverPackModel>> ListModelsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns the latest driver pack for <paramref name="systemId"/> +
    /// <paramref name="osVersion"/>, or null if the vendor doesn't ship one
    /// for that combination.
    /// </summary>
    Task<DriverPack?> GetDriverPackAsync(string systemId, string osVersion,
        CancellationToken ct = default);
}
