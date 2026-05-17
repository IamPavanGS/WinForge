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
    /// <summary>Windows 11 version detected from WIM metadata (e.g. "25H2"). Empty when unknown.</summary>
    public string  IsoOsVersion    { get; set; } = "";

    public List<WindowsImageInfo> AvailableImages { get; set; } = [];
    public WindowsImageInfo?      SelectedImage   { get; set; }

    public string SelectedEdition { get; set; } = "Pro";
    public string SelectedArch    { get; set; } = "x64";

    public string OutputPath      { get; set; } = "";
    public string WorkspacePath   { get; set; } = "";
    public string OutputFilename  { get; set; } = "WinForge_{edition}_{date}.iso";

    // ── Step 2: Assets ────────────────────────────────────────────────────────
    public string?         WallpaperPath           { get; set; }
    /// <summary>Optional image for the lock-screen / sign-in / OOBE backgrounds
    /// (<c>Windows\Web\Screen\img1*.jpg</c>). When null, the build engine leaves
    /// the Windows defaults in place — independent of WallpaperPath, so admins
    /// can brand desktop and lock screen separately.</summary>
    public string?         LockScreenPath          { get; set; }
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

    /// <summary>
    /// Per-driver-folder injection mode. Parallel array to <see cref="DriverFolderPaths"/>
    /// (key = folder path, value = true if only WinPE-critical PnP classes should
    /// be injected into boot.wim alongside install.wim). Folders not present in
    /// this map default to install-only / full injection — today's behaviour.
    /// </summary>
    public Dictionary<string, bool> DriverFolderWinPEOnly { get; set; } = [];

    // ── Auto-fetch features (Phase 4 — gated by EnableAutoFetchFeatures) ─────
    /// <summary>Windows Update MSU files staged for slipstream into install.wim.
    /// Populated either by the Phase 5 auto-fetch UI or by a manual folder drop.</summary>
    public List<string> UpdatesMsuPaths { get; set; } = [];

    /// <summary>OEM driver packs the auto-fetch flow downloaded for this build.
    /// Each entry's DownloadUrl is the local cache path after fetch; the pipeline
    /// step extracts and injects via DISM.</summary>
    public List<DriverPackSelection> AutoFetchedDriverPacks { get; set; } = [];

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
    /// <summary>Hostname template token. Default <c>"{PREFIX}{SERIAL}"</c> matches
    /// the original behaviour byte-for-byte. Other supported templates resolved
    /// at first boot: <c>{PREFIX}{LAST6_SERIAL}</c>, <c>{PREFIX}{LAST6_MAC}</c>,
    /// <c>{PREFIX}{ASSETTAG}</c>. The default branch emits the existing
    /// PowerShell line unchanged; non-default templates use a separate
    /// resolver so a bug in the new code can't break the default case.</summary>
    public string HostnameTemplate { get; set; } = "{PREFIX}{SERIAL}";

    // ── Windows 11 UX & privacy baseline (Phase 6) ────────────────────────────
    // Each toggle independently writes a fixed policy registry value to the
    // offline SOFTWARE hive. All default false so existing builds are
    // unchanged unless the admin opts in.
    /// <summary>Disable the Copilot taskbar icon and block invocation
    /// (<c>TurnOffWindowsCopilot=1</c>).</summary>
    public bool DisableCopilot          { get; set; } = false;
    /// <summary>Disable Windows 11 24H2+ Recall (AI screen-capture).
    /// Sets both <c>DisableAIDataAnalysis=1</c> and
    /// <c>AllowRecallEnablement=0</c>.</summary>
    public bool DisableRecall           { get; set; } = false;
    /// <summary>Remove the Widgets / News &amp; Interests pane
    /// (<c>AllowNewsAndInterests=0</c>).</summary>
    public bool DisableWidgets          { get; set; } = false;
    /// <summary>Hide the consumer Teams Chat icon
    /// (<c>ConfigureChatIcon=3</c>).</summary>
    public bool DisableChatIcon         { get; set; } = false;
    /// <summary>Block the "Welcome experience" tour, Spotlight promotions,
    /// and Microsoft Store consumer ads
    /// (<c>DisableWindowsConsumerFeatures=1</c>).</summary>
    public bool DisableConsumerFeatures { get; set; } = false;

    /// <summary>Run <c>OneDriveSetup.exe /uninstall</c> at first boot (both
    /// 64-bit and 32-bit copies). Catches the per-user installer that the
    /// provisioned-package match in <see cref="BloatwareToRemove"/> doesn't
    /// always reach.</summary>
    public bool UninstallOneDrive { get; set; } = false;

    /// <summary>Trusted certificates staged into the WIM and imported via
    /// <c>certutil -addstore</c> at first boot. Each entry carries its own
    /// target store (Root / Intermediate / TrustedPublisher).</summary>
    public List<CertificateEntry> Certificates { get; set; } = [];

    /// <summary>Custom fonts (.ttf / .otf / .ttc) staged into Windows\Fonts
    /// and registered under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts.</summary>
    public List<FontEntry> Fonts { get; set; } = [];

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

    // ── Auto-fetch feature flag (set via Settings page; off by default) ──────
    /// <summary>Master toggle for the new auto-fetch features (Updates +
    /// driver-pack auto-download + PnP-class boot.wim filter). When false, the
    /// pipeline behaves exactly as it did before Phase 4 was merged — the new
    /// soft step short-circuits and the new fields are ignored. Stored in
    /// AppSettings rather than the per-profile session, but copied onto the
    /// session at build start so the engine sees one source of truth.</summary>
    public bool EnableAutoFetchFeatures { get; set; } = false;
}
