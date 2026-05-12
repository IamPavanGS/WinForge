namespace GoldenISOBuilder.Models;

/// <summary>
/// A serializable group policy setting chosen by the user.
/// Written to the offline registry hive during the build.
/// </summary>
public class GroupPolicyEntry
{
    /// <summary>Human-readable name shown in the Step 3 list (e.g. "Allow BitLocker without TPM").</summary>
    public string DisplayName  { get; set; } = "";

    /// <summary>Full category display path from the ADMX tree (e.g. "Windows Components\BitLocker Drive Encryption").</summary>
    public string CategoryPath { get; set; } = "";

    /// <summary>"Machine" or "User".</summary>
    public string PolicyClass  { get; set; } = "Machine";

    /// <summary>
    /// Registry key exactly as it appears in the ADMX (HKLM-relative for Machine,
    /// HKCU-relative for User). Example: "SOFTWARE\Policies\Microsoft\FVE".
    /// </summary>
    public string RegistryKey  { get; set; } = "";

    /// <summary>Registry value name to write.</summary>
    public string ValueName    { get; set; } = "";

    /// <summary>"Enabled" or "Disabled". "NotConfigured" entries are skipped at build time.</summary>
    public string State        { get; set; } = "Enabled";

    /// <summary>REG_DWORD, REG_SZ, REG_QWORD, REG_BINARY, REG_EXPAND_SZ, or REG_MULTI_SZ.</summary>
    public string ValueType    { get; set; } = "REG_DWORD";

    /// <summary>Value to write (string representation — reg.exe handles the conversion).</summary>
    public string Value        { get; set; } = "1";
}
