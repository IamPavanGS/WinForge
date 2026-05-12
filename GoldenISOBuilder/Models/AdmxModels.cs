namespace GoldenISOBuilder.Models;

// ── Transient (not serialized) models used only by AdmxParser and GroupPolicyDialog ──

/// <summary>
/// Category tree node for the GroupPolicyDialog TreeView,
/// populated at runtime from the local C:\Windows\PolicyDefinitions\ ADMX files.
/// </summary>
public class AdmxCategory
{
    public string Name         { get; set; } = "";
    public string DisplayName  { get; set; } = "";
    public string ParentName   { get; set; } = "";
    /// <summary>Full display path, e.g. "Windows Components\BitLocker Drive Encryption".</summary>
    public string FullPath     { get; set; } = "";

    public List<AdmxCategory> Children { get; set; } = [];
    public List<AdmxPolicy>   Policies { get; set; } = [];
}

/// <summary>Parsed policy from an ADMX file — used only for the picker dialog.</summary>
public class AdmxPolicy
{
    public string Name          { get; set; } = "";
    public string DisplayName   { get; set; } = "";
    public string ExplainText   { get; set; } = "";
    public string CategoryName  { get; set; } = "";   // ADMX internal ref
    public string CategoryPath  { get; set; } = "";   // Resolved display path

    /// <summary>"Machine", "User", or "Both".</summary>
    public string PolicyClass   { get; set; } = "Machine";

    /// <summary>Registry key as stated in ADMX (e.g. "SOFTWARE\Policies\Microsoft\…").</summary>
    public string RegistryKey   { get; set; } = "";
    public string ValueName     { get; set; } = "";

    /// <summary>REG_DWORD, REG_SZ, etc.</summary>
    public string ValueType     { get; set; } = "REG_DWORD";

    /// <summary>Value written when state = Enabled (typically "1").</summary>
    public string EnabledValue  { get; set; } = "1";

    /// <summary>Value written when state = Disabled (typically "0").</summary>
    public string DisabledValue { get; set; } = "0";

    /// <summary>Additional configurable elements (decimal, text, enum…).</summary>
    public List<AdmxElement> Elements { get; set; } = [];
}

/// <summary>A single configurable element inside an ADMX policy.</summary>
public class AdmxElement
{
    public string Id          { get; set; } = "";
    /// <summary>"decimal", "longDecimal", "text", "boolean", "enum", "list", "multiText".</summary>
    public string ElementType { get; set; } = "decimal";
    public string ValueName   { get; set; } = "";
    public string Label       { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public bool   Required    { get; set; } = false;

    /// <summary>Items for enum elements: (reg value, display label).</summary>
    public List<(string Value, string Label)> EnumItems { get; set; } = [];
}
