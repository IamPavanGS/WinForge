namespace GoldenISOBuilder.Models;

/// <summary>
/// A trusted-certificate entry staged into the WIM and imported at first
/// boot via <c>certutil.exe -addstore</c>. The build engine groups entries
/// by <see cref="Store"/> into subfolders under
/// <c>\Windows\Setup\Scripts\Certs\</c> inside the mounted image; a single
/// <c>for</c> loop per store in SetupComplete.cmd then walks each folder
/// and imports every <c>.cer</c> / <c>.crt</c> file it finds.
///
/// Not reusing <see cref="StagedFile"/> because the <see cref="Store"/>
/// dropdown is semantically distinct — the destination folder is derived
/// from it, not freely chosen — and the validator needs to confirm
/// certutil-import success, not just file presence.
/// </summary>
public sealed class CertificateEntry
{
    /// <summary>Absolute path on the build host. Supports <c>.cer</c>, <c>.crt</c>
    /// (binary DER or base64-PEM — certutil accepts both formats).</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Target Windows certificate store. Must be one of:
    ///   <c>"Root"</c>              → Trusted Root Certification Authorities
    ///   <c>"CA"</c>                → Intermediate Certification Authorities
    ///   <c>"TrustedPublisher"</c>  → Trusted Publishers (code-signing)
    /// </summary>
    public string Store { get; set; } = "Root";

    /// <summary>Optional friendly name shown in the Step 5 review. Not used
    /// by the import — purely cosmetic.</summary>
    public string Description { get; set; } = "";
}
