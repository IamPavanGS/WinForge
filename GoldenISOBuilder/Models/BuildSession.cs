namespace GoldenISOBuilder.Models;

public class BuildSession
{
    public static BuildSession Current { get; set; } = new();

    // ── Dynamic scan results (populated in Step 3 when user clicks "Scan ISO") ─
    public List<DiscoveredPackage> ScannedPackages { get; set; } = [];
    public List<DiscoveredFeature> ScannedFeatures { get; set; } = [];

    // ── Step 1: Source & Output ───────────────────────────────────────────────
    public string? SourceIsoPath   { get; set; }
    public string? MountedIsoDrive { get; set; }

    public List<WindowsImageInfo> AvailableImages { get; set; } = [];
    public WindowsImageInfo?      SelectedImage   { get; set; }

    public string SelectedEdition { get; set; } = "Pro";
    public string SelectedArch    { get; set; } = "x64";

    public string OutputPath      { get; set; } = "";
    public string WorkspacePath   { get; set; } = "";
    public string OutputFilename  { get; set; } = "GoldenImage_{edition}_{date}.iso";

    // ── Step 2: Assets ────────────────────────────────────────────────────────
    public string?         WallpaperPath           { get; set; }
    public List<StagedApp> StagedApps              { get; set; } = [];
    /// <summary>Kept for backward-compatible loading of old .gibprofile files only.
    /// New code should use <see cref="StagedFiles"/> instead.</summary>
    public List<string>    PublicDesktopFiles       { get; set; } = [];
    /// <summary>Files to copy into the image at arbitrary destinations.</summary>
    public List<StagedFile> StagedFiles             { get; set; } = [];
    public bool                    IncludeDeploymentScripts { get; set; } = true;
    public List<DeploymentScript>  DeploymentScripts        { get; set; } = [];

    // Language packs: full paths to .cab files to inject via DISM /Add-Package
    public List<string>    LanguagePackPaths       { get; set; } = [];

    // Drivers: folder paths to inject recursively via DISM /Add-Driver /Recurse
    public List<string>    DriverFolderPaths       { get; set; } = [];

    // ── Step 3: Customizations ────────────────────────────────────────────────
    public List<string> BloatwareToRemove { get; set; } = [];

    // Security toggles
    public bool   EnableBitLocker        { get; set; } = false;
    /// <summary>Drive letter to encrypt, e.g. "C:" or "D:". Defaults to "C:".</summary>
    public string BitLockerDriveLetter   { get; set; } = "C:";
    /// <summary>When false the recovery key file is NOT written to disk.</summary>
    public bool   BitLockerSaveRecoveryKey { get; set; } = true;
    /// <summary>Folder where the recovery key file is saved, e.g. "C:\" or "D:\Keys\".</summary>
    public string BitLockerKeyFolder       { get; set; } = @"C:\";
    public bool EnableDefenderAtp    { get; set; } = true;
    public bool DisableSmbV1         { get; set; } = true;

    // System defaults
    public bool DarkMode             { get; set; } = true;
    public bool ShowFileExtensions   { get; set; } = true;
    public bool ShowHiddenFiles      { get; set; } = false;
    public bool DisableTelemetry     { get; set; } = true;
    public bool EnableHyperV         { get; set; } = false;

    // ── Step 4: Admin Account ─────────────────────────────────────────────────
    public string AdminUsername      { get; set; } = "Administrator";
    public string AdminPassword      { get; set; } = "";
    public bool   AutoLoginEnabled   { get; set; } = false;
    public bool   PasswordNeverExpires { get; set; } = false;

    // ── Step 5: Registry ──────────────────────────────────────────────────────
    public List<RegistryEntry> CustomRegistryEntries { get; set; } = [];

    // ── ISO source language (windowsPE UILanguage — must match boot.wim locale) ─
    /// <summary>BCP-47 locale code matching the source ISO's boot.wim language pack, e.g. "en-GB".
    /// Written into the windowsPE SetupUILanguage / UILanguage / UILanguageFallback blocks.
    /// MUST match what is physically in the ISO's boot.wim — mismatches cause 0x8007000D.</summary>
    public string IsoSourceLanguage { get; set; } = "en-GB";

    /// <summary>All BCP-47 language codes available in the ISO's boot.wim (from lang.ini).
    /// Used to restrict the ISO Boot Language picker to only valid choices.
    /// Empty list = ISO not yet analysed, show all languages as fallback.</summary>
    public List<string> IsoAvailableLanguages { get; set; } = [];

    /// <summary>BCP-47 locale code for the installed Windows OS locale (oobeSystem pass),
    /// e.g. "en-US". This is independent of IsoSourceLanguage — you can deploy an en-US
    /// Windows from an en-GB ISO by setting IsoSourceLanguage=en-GB, TargetOsLocale=en-US.
    /// Drives InputLocale / SystemLocale / UILanguage / UserLocale in oobeSystem.</summary>
    public string TargetOsLocale { get; set; } = "en-US";

    // ── Step 6: Advanced ──────────────────────────────────────────────────────
    public string OrgName          { get; set; } = "";
    public string RegisteredOwner  { get; set; } = "";
    public string ComputerPrefix   { get; set; } = "";
    public string ProductKey       { get; set; } = "";
    public bool   SkipOobe         { get; set; } = true;
    public int    AutoLogonCount   { get; set; } = 0;
    public string PowerPlan        { get; set; } = "Balanced";
    public string TimeZone         { get; set; } = "India Standard Time";
    public string OemManufacturer  { get; set; } = "";
    public string OemModel         { get; set; } = "";
    public string OemSupportUrl    { get; set; } = "";
    public List<string> EnabledFeatures  { get; set; } = [];
    public List<string> DisabledFeatures { get; set; } = [];

    // ── Step 3: Group Policy settings ────────────────────────────────────────
    public List<GroupPolicyEntry> GroupPolicies  { get; set; } = [];
    /// <summary>"machine" | "iso" | "custom" — which ADMX source to use in the policy picker.</summary>
    public string AdmxSourceMode { get; set; } = "machine";
    /// <summary>Absolute path to the custom ADMX folder (only used when AdmxSourceMode == "custom").</summary>
    public string CustomAdmxPath { get; set; } = "";

    // ── Scheduled Tasks (injected via schtasks.exe in SetupComplete.cmd) ─────
    public List<ScheduledTaskConfig> ScheduledTasks { get; set; } = [];

    // ── Build outputs (set by engine) ─────────────────────────────────────────
    public string? LastBuiltIsoPath { get; set; }
    public string? LastBuildLogPath { get; set; }
    public string? LastBuildSha256  { get; set; }
}
