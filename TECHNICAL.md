# ALE Image Forge — Technical Reference

Internal project codename: **GoldenISOBuilder**  
Maintainer: Pavan G S, BTQ Infra, Alcatel-Lucent Enterprise  
Last updated: v2.4.8

---

## Table of Contents

1. [Solution Structure](#1-solution-structure)
2. [BuildSession — The Shared State Model](#2-buildsession--the-shared-state-model)
3. [MainWindow Navigation Model](#3-mainwindow-navigation-model)
4. [BuildEngine Pipeline — Stage-by-Stage Reference](#4-buildengine-pipeline--stage-by-stage-reference)
5. [Pre-Commit Validator](#5-pre-commit-validator)
6. [Registry Offline-Hive Mapping](#6-registry-offline-hive-mapping)
7. [Autounattend / Unattend / SetupComplete Generation](#7-autounattend--unattend--setupcomplete-generation)
8. [GIBFirstBoot — First-Boot Installer Protocol](#8-gibfirstboot--first-boot-installer-protocol)
9. [Auto-Fetch Subsystem (Windows Updates + OEM Drivers)](#9-auto-fetch-subsystem-windows-updates--oem-drivers)
10. [Hyper-V Test-in-VM](#10-hyper-v-test-in-vm)
11. [Theme System](#11-theme-system)
12. [Settings Persistence](#12-settings-persistence)
13. [Logging and Diagnostics](#13-logging-and-diagnostics)
14. [Security Model](#14-security-model)
15. [Error Handling Philosophy](#15-error-handling-philosophy)
16. [Known Design Decisions and Constraints](#16-known-design-decisions-and-constraints)
17. [Extension Points](#17-extension-points)
18. [Build and Distribution](#18-build-and-distribution)

---

## 1. Solution Structure

```
ALE ISO Creator.sln
├── GoldenISOBuilder/          net8.0-windows10.0.17763.0 — main WPF GUI
└── GIBFirstBoot/              net8.0-windows — first-boot installer (self-contained)
```

`GoldenISOBuilder` contains an MSBuild target (`PublishAndCopyGIBFirstBoot`) that automatically triggers `dotnet publish` on `GIBFirstBoot` and copies `GIBFirstBoot.exe` into GoldenISOBuilder's output/publish folder. This coupling is intentional — `GIBFirstBoot.exe` must always be present alongside `GoldenISOBuilder.exe`, and the build step that injects it into the WIM (`firstboot`) throws `FileNotFoundException` if it is absent.

### Target Framework Rationale

`net8.0-windows10.0.17763.0` is the minimum that exposes the WinRT toast notification API used by `Windows.UI.Notifications`. 17763 = Windows 10 1809 / Windows Server 2019. This also sets the minimum OS version for the **build machine** (not the deployed image).

---

## 2. BuildSession — The Shared State Model

`Models/BuildSession.cs` is a plain POCO with a `static Current` property. There is no ViewModel layer. All wizard pages read and write `BuildSession.Current` directly. Before navigating away from a page, every page calls its own `SaveToSession()` method to flush UI state into the model.

### Persistence

Profiles are `BuildSession` serialized to JSON with `System.Text.Json` and saved as `.gibprofile` files. Old profiles load safely because every property has a safe default initialiser (`= ""`, `= false`, `= 0`, `= []`). The serializer is configured with `JsonSerializerOptions { WriteIndented = true }`.

### Complete Property Reference

| Property | Type | Default | Purpose |
|---|---|---|---|
| `IsoSourcePath` | `string` | `""` | Source `.iso` path |
| `OutputPath` | `string` | `""` | Output folder for the finished ISO |
| `WorkspacePath` | `string` | `""` | Staging area (`workspace/`) |
| `IsoSourceLanguage` | `string` | `""` | BCP-47 tag e.g. `"en-GB"` — fed into `UILanguage` in windowsPE passes |
| `TargetEdition` | `string` | `""` | e.g. `"Pro"`, `"Enterprise"`, `"Education"` |
| `AdminUsername` | `string` | `"Administrator"` | Local admin account name |
| `AdminPassword` | `string` | `""` | Plaintext in the model; always encoded before writing to disk |
| `AutoLoginEnabled` | `bool` | `false` | Adds `<AutoLogon>` to `Autounattend.xml` and registry wires in `SetupComplete.cmd` |
| `AutoLogonCount` | `int` | `1` | Stored in session; BuildEngine currently hardcodes `LogonCount=1` in the generated XML |
| `PasswordNeverExpires` | `bool` | `false` | Adds `net accounts /maxpwage:unlimited` to `SetupComplete.cmd` |
| `SkipOobe` | `bool` | `true` | Controls whether `Autounattend.xml` is generated at all |
| `ComputerName` | `string` | `""` | Hostname template: `{PREFIX}{SERIAL}`, `{PREFIX}{LAST6_SERIAL}`, `{PREFIX}{LAST6_MAC}`, `{PREFIX}{ASSETTAG}` — expanded in `SetupComplete.cmd` at runtime |
| `TimeZone` | `string` | `""` | Injected into `<TimeZone>` in the specialize pass |
| `UILanguage` | `string` | `""` | Overrides the language in all unattend passes when set |
| `ProductKey` | `string` | `""` | Written into `<ProductKey>` if set; omit for KMS/MAK |
| `OrgName` | `string` | `""` | OEM branding: Manufacturer in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation` |
| `OemModel` | `string` | `""` | OEM branding: Model |
| `OemSupportUrl` | `string` | `""` | OEM branding: SupportURL |
| `PowerPlan` | `string` | `""` | `"Balanced"`, `"HighPerformance"`, or `"Ultimate"` — runs `powercfg /setactive` in `SetupComplete.cmd` |
| `BitLockerEnabled` | `bool` | `false` | Stages `Enable-BitLocker.ps1` and wires a schtask in `SetupComplete.cmd` |
| `BitLockerKeySavePath` | `string` | `""` | If non-empty, `Enable-BitLocker.ps1` writes the recovery key to this path |
| `WallpaperPath` | `string` | `""` | Source wallpaper file (copied to `img0/img19/img20.jpg` and 4K variants in the WIM) |
| `LockScreenPath` | `string` | `""` | Source lock-screen file (overwrites `img100+` variants in `Windows\Web\Screen\`) |
| `WimCompression` | `string` | `"max"` | Passed to `DISM /Export-Image /Compress:{max|fast|none}` |
| `VerifyIsoAfterBuild` | `bool` | `true` | Enables SHA-256 post-build verify |
| `CleanWorkspaceAfterBuild` | `bool` | `true` | Deletes `workspace/` on success |
| `StagedApps` | `List<StagedApp>` | `[]` | Applications for GIBFirstBoot to install |
| `StagedFiles` | `List<StagedFile>` | `[]` | Files to place on `Public\Desktop` |
| `LanguagePackPaths` | `List<string>` | `[]` | Language CAB paths (absolute, on the build machine) |
| `DriverFolders` | `List<string>` | `[]` | Driver source folders for manual injection |
| `AutoFetchedDriverPacks` | `List<DriverPackSelection>` | `[]` | OEM driver packs resolved by the auto-fetch subsystem |
| `AutoFetchedUpdates` | `List<string>` | `[]` | MSU/CAB paths downloaded from Windows Update Catalog |
| `CustomRegistryEntries` | `List<RegistryEntry>` | `[]` | Custom registry tweaks (SET / DELETE) |
| `GroupPolicies` | `List<GroupPolicyEntry>` | `[]` | Group Policy values written to the offline hive |
| `DeploymentScripts` | `List<StagedScript>` | `[]` | PowerShell/cmd scripts staged to `Public\Documents` with optional Startup trigger |
| `EnabledFeatures` | `List<string>` | `[]` | Windows optional features to enable (by feature name as reported by DISM) |
| `DisabledFeatures` | `List<string>` | `[]` | Windows optional features to disable |
| `BloatwareToRemove` | `List<string>` | `[]` | Provisioned AppX package name prefixes to remove |
| `TrustedCertificates` | `List<CertificateEntry>` | `[]` | Certificates to import at first-boot via `certutil` |
| `CustomFonts` | `List<FontEntry>` | `[]` | Font files to inject + registry entries to write |
| `ScheduledTasks` | `List<ScheduledTaskConfig>` | `[]` | Schtasks to register in `SetupComplete.cmd` |
| `SecurityToggles` | `Dictionary<string,bool>` | `{}` | Named security knobs: `DisableTelemetry`, `DisableCopilot`, `DisableWidgets`, `DisableRecall`, `DisableConsumerExperiences`, `DisableChat`, `DisableSMBv1`, `EnableDefenderATP`, `ShowFileExtensions`, `DarkModeDefault` |
| `LastBuiltIsoPath` | `string` | `""` | Set by BuildEngine on successful build; auto-populates the TestVmPage ISO field |

### Sub-model schemas

**`StagedApp`**
```json
{
  "DisplayName": "7-Zip 24.08",
  "InstallerPath": "C:\\Staging\\7z2408-x64.msi",
  "InstallerType": "MSI",
  "Arguments": "",
  "MstPath": "",
  "TimeoutMinutes": 60,
  "AcceptedExitCodes": [0, 1641, 3010],
  "Order": 1
}
```
`InstallerType` is `"MSI"` or `"EXE"`. For MSI, GIBFirstBoot appends `/qn /norestart` (plus optional `.mst` transform) automatically; the `Arguments` field is appended on top. For EXE, `Arguments` is used verbatim.

**`DriverPackSelection`**
```json
{
  "Vendor": "Dell",
  "Model": "OptiPlex 7090",
  "DownloadUrl": "https://...",
  "FileName": "...-A14.exe",
  "LocalCachePath": "%LOCALAPPDATA%\\GoldenISOBuilder\\Cache\\Drivers\\...",
  "ExtractedPath": "...",
  "SizeBytes": 412581888,
  "Checksum": "sha256:..."
}
```

**`RegistryEntry`**
```json
{
  "Hive": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\...",
  "ValueName": "DisableTelemetry",
  "ValueType": "DWORD",
  "ValueData": "1",
  "Operation": "SET"
}
```
`Operation` is `"SET"` or `"DELETE"`. `ValueType` mirrors `reg.exe` type names: `REG_SZ`, `REG_DWORD`, `REG_QWORD`, `REG_BINARY`, `REG_EXPAND_SZ`, `REG_MULTI_SZ`.

---

## 3. MainWindow Navigation Model

`MainWindow.xaml` owns all page instances, which are created once and toggled between `Visible` and `Collapsed`. Navigation is event-driven:

```csharp
// Each page raises this event with its target page index
event Action<object, int> NavigateRequested;

// MainWindow handles it:
void Navigate(BasePage page, int step)
```

The step index maps to a left-sidebar breadcrumb (integers 0–7 plus special pages for Registry, Advanced, Settings, and TestVm). The sidebar does not drive navigation; it is purely decorative feedback — the pages drive navigation themselves.

The wizard enforces forward-only navigation in the normal flow (Next button). Back is allowed freely. The Review page (Step5Page) can jump back to any prior step via its section links.

---

## 4. BuildEngine Pipeline — Stage-by-Stage Reference

`Services/BuildEngine.cs` runs `RunAsync(BuildSession session, IProgress<BuildProgress> progress, CancellationToken ct)` entirely on a background thread. The UI receives `BuildProgress` records via `IProgress<T>` (marshalled to UI thread automatically).

Internal helpers:
- `Step(id, Func<Task>)` — fatal step. Throws `BuildException` on failure, halting the pipeline and triggering abort/cleanup.
- `StepSoft(id, Func<Task>)` — soft step. Catches all exceptions, logs them as warnings (`!`), and continues.
- `DismAsync(args)` — runs `dism.exe` with `args`; throws on non-zero exit.
- `DismCapturedAsync(args)` — same but captures and returns stdout+stderr as a string.
- `RunCmdAsync(exe, args, workdir)` — general-purpose process runner.

### Stage Detail

#### 1. `validate`
Checks:
- Process is elevated (`IsUserAnAdministrator` via P/Invoke `shell32!ShellExecuteEx` or `WindowsPrincipal`)
- `IsoSourcePath` exists and is readable
- `OutputPath` is writable (writes a temp file)
- `WorkspacePath` is writable
- `dism.exe` exists in `System32`
- `oscdimg.exe` found in ADK install paths:
  - `%ProgramFiles(x86)%\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe`
  - `%ProgramFiles%\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe`
  - Path env scan

#### 2. `prepare`
1. Deletes `WorkspacePath\iso\`, `WorkspacePath\wim\`, and `WorkspacePath\mount\` if they exist.
2. Runs `DISM /Cleanup-Wim` to release any orphaned WIM mounts from a prior crashed run. Failure is swallowed.
3. Creates the three directories fresh.

#### 3. `copyiso`
1. Mounts the source `.iso` via `Powershell Mount-DiskImage` (parsed from stdout for drive letter).
2. `robocopy <mounted_drive>\ WorkspacePath\iso\ /E /COPYALL /R:2 /W:1 /NP` — copies the full ISO tree.
3. `Powershell Dismount-DiskImage`.

The `workspace\iso\` tree is the staging area that `oscdimg.exe` will later pack into the output ISO. All subsequent mutations write into `workspace\iso\sources\install.wim` (indirectly via the mounted WIM at `workspace\mount\`).

#### 4. `exportedition`
```
DISM /Export-Image
     /SourceImageFile:workspace\iso\sources\install.wim
     /SourceName:<TargetEdition>
     /DestinationImageFile:workspace\wim\install.wim
     /Compress:<WimCompression>
```
Outputs a single-edition WIM. Replaces `workspace\iso\sources\install.wim` with the new single-edition file at end of stage.

#### 5. `mountwim`
```
DISM /Mount-Image
     /ImageFile:workspace\iso\sources\install.wim
     /Index:1
     /MountDir:workspace\mount\
```
All subsequent stages operate on `workspace\mount\` (the mounted WIM tree).

#### 6. `updates` *(soft)*
Iterates `BuildSession.AutoFetchedUpdates` (paths to `.msu` / `.cab` files):
```
DISM /Add-Package /Image:workspace\mount\ /PackagePath:<path> /IgnoreCheck
```
`/IgnoreCheck` is required for cumulative updates that have applicability rules referencing a live system.

#### 7. `langpacks` *(soft)*
Iterates `BuildSession.LanguagePackPaths`:
```
DISM /Add-Package /Image:workspace\mount\ /PackagePath:<cab>
```
Each call is independent; failure of one CAB is logged as soft-warning `!` and does not stop other packs.

#### 8. `drivers` *(soft)*
**Main image drivers:** For each `DriverFolder` and each extracted `AutoFetchedDriverPack.ExtractedPath`:
```
DISM /Add-Driver /Image:workspace\mount\ /Driver:<path> /Recurse
```

**WinPE-critical drivers** (`InjectWinPECriticalDriversAsync`):
1. Mounts `workspace\iso\sources\boot.wim` at `workspace\boot_mount\` using `EnsureBootWimWritableAsync` (re-exports boot.wim with `/Compress:fast` first since the ADK-sourced boot.wim is read-only).
2. Filters the driver set to "WinPE-critical" INFs (network adapters, storage controllers — heuristic on `Class` in the INF header).
3. Injects them into boot.wim index 2 (the main PE image).
4. Commits and unmounts boot.wim.

#### 9. `wallpaper` *(soft)*
Target paths in the mounted WIM:
- `Windows\Web\Wallpaper\Windows\img0.jpg` (desktop default)
- `Windows\Web\4K\Wallpaper\Windows\img0_*.jpg` (4K variants, glob)
- `Windows\Web\Wallpaper\Windows\img19.jpg` / `img20.jpg` (alternate slots used by Win11)
- `Windows\Web\Screen\img100.jpg` (lock screen, plus `img101`–`img105` variants)

Each replacement:
1. `takeown.exe /F <path>` — claims ownership from `TrustedInstaller`.
2. `icacls.exe <path> /grant "Administrators:F"` — grants write access.
3. `File.Copy(sourcePath, targetPath, overwrite: true)`.

`TryReplaceFileWithOwnership()` wraps all three in a `try/catch`; failure produces a soft `!` in the log.

#### 10. `firstboot` *(soft)*
1. Creates `workspace\mount\GIB\` (the landing folder on the deployed machine at `C:\GIB\`).
2. Copies `GIBFirstBoot.exe` into it.
3. For each `StagedApp`, copies the installer file + any `.mst` transform into `workspace\mount\GIB\Installers\`.
4. Serializes an `apps.json` manifest (see §8).
5. Adds a `RunOnce` registry entry via the offline hive so GIBFirstBoot auto-runs on first user login. The key is `HKLM\OFFLINE_SW\Microsoft\Windows\CurrentVersion\RunOnce` (maps to `HKLM\SOFTWARE\...` in the live system).

#### 11. `publicdesktop` *(soft)*
Copies `StagedFile` entries whose `Destination == "PublicDesktop"` to `workspace\mount\Users\Public\Desktop\`.

#### 12. `deploymentscripts` *(soft)*
1. Copies each `StagedScript` to `workspace\mount\Users\Public\Documents\`.
2. If `StagedScript.RunAtStartup == true`, writes a `.cmd` relay into `workspace\mount\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\` that calls the script.

#### 13. `bloatware` *(soft)*
For each name prefix in `BloatwareToRemove`, queries the provisioned package list and removes each match:
```
DISM /Remove-ProvisionedAppxPackage /Image:workspace\mount\ /PackageName:<full_name>
```

#### 14. `features` *(soft)*
```
DISM /Enable-Feature /Image:workspace\mount\ /FeatureName:<name> /All /NoRestart
DISM /Disable-Feature /Image:workspace\mount\ /FeatureName:<name> /NoRestart
```

#### 15. `registry` *(soft)*
Three offline hives are loaded:

| Hive alias | File in WIM | Maps from |
|---|---|---|
| `HKLM\OFFLINE_SW` | `Windows\System32\config\SOFTWARE` | `HKLM\SOFTWARE\...` |
| `HKLM\OFFLINE_SYS` | `Windows\System32\config\SYSTEM` | `HKLM\SYSTEM\...` |
| `HKLM\OFFLINE_USR` | `Users\Default\NTUSER.DAT` | `HKCU\...` |

Load command: `reg.exe load HKLM\OFFLINE_SW <path>`.

All registry operations use `reg.exe add` / `reg.exe delete`, never `Set-ItemProperty`. This is required for offline-hive compatibility — PowerShell's registry provider cannot target arbitrary hive roots.

After all writes, each hive is unloaded with up to 5 retries (1-second delay between attempts, preceded by `GC.Collect()` to release any .NET handles that may be keeping the hive locked). Failure to unload is a soft warning — the DISM unmount in step 18 will fail if the hive is still loaded.

#### 16. `unattend` *(soft)*
Generates three files into `workspace\iso\`:
- **`Autounattend.xml`** — drives Windows Setup. Passes:
  - `windowsPE` — disk layout (DiskConfig + diskpart clean + primary partition create), language/locale/keyboard, edition selection, product key
  - `specialize` — computer name, timezone, OEM info, domain join (if configured)
  - `oobeSystem` — auto-logon, hide EULA/OOBE screens
- **`Unattend.xml`** — applied during first-login via `C:\Windows\Panther\`; mostly covers any remaining OOBE suppression
- **`SetupComplete.cmd`** — runs from `C:\Windows\Setup\Scripts\` at the end of Windows Setup (before first user login). Contains: BitLocker schtask registration, auto-logon registry write, password-never-expires, computer rename, power plan, OneDrive uninstall, certificate import, scheduled tasks, and finally relaunches as the current user to trigger GIBFirstBoot

See §7 for generation details.

#### 17. `precommit_validate` *(critical)*
Runs the full validator (see §5). Writes the report. Any FAIL throws `BuildException`.

#### 18. `unmount` *(critical)*
```
DISM /Unmount-Image /MountDir:workspace\mount\ /Commit
```
This is the point of no return — all WIM mutations are permanently baked in. If the validator in stage 17 passed, this will succeed under normal conditions.

On failure, the pipeline attempts `DISM /Unmount-Image /Discard` to release the mount without committing, preventing workspace corruption.

#### 19. `buildiso` *(critical)*
```
oscdimg.exe -m -o -u2 -udfver102
            -bootdata:2#p0,e,b"workspace\iso\boot\etfsboot.com"
                       #pEF,e,b"workspace\iso\efi\microsoft\boot\efisys.bin"
            workspace\iso\
            <OutputPath>\<IsoName>.iso
```
This produces a dual-mode (BIOS legacy MBR + UEFI GPT) bootable ISO.

After `oscdimg.exe`, if `VerifyIsoAfterBuild == true`, the engine computes a SHA-256 hash of the output file in 1 MB chunks and writes it to `<IsoName>.iso.sha256`.

---

## 5. Pre-Commit Validator

`ValidateBuildContentsAsync()` in `BuildEngine.cs` runs after all WIM mutations are applied but before `DISM /Unmount-Image /Commit`. The mounted WIM is still fully accessible at `workspace\mount\`.

All checks produce `ValCheck` records:
```csharp
record ValCheck(string Category, string Name, ValStatus Status, string Detail);
enum ValStatus { Pass, Warn, Fail }
```

A `Fail` on any record causes the build to halt before commit. `Warn` records are logged in the report but do not stop the build.

The validator uses **separate** offline hive aliases (`GIB_VAL_SW`, `GIB_VAL_SYS`, `GIB_VAL_USR`, `GIB_VAL_FONT_SW`) to avoid colliding with the build-phase hives (`OFFLINE_SW`, etc.), which are always unloaded by stage 15.

### Sections

#### `[SESSION]`
- Admin username is non-empty
- Admin password is non-empty
- `IsoSourceLanguage` is non-empty and matches a BCP-47 pattern
- `TargetEdition` is non-empty

#### `[AUTOUNATTEND.XML]`
If `SkipOobe == true`:
- File exists in `workspace\iso\` and is valid XML (UTF-8 with BOM)
- `UILanguage` in the windowsPE pass matches `IsoSourceLanguage`
- If `ProductKey` is set, it is present in the file
- If `AutoLoginEnabled`, the `<AutoLogon>` block is present and `<Enabled>true</Enabled>`

#### `[SETUPCOMPLETE.CMD]`
Each toggle that was enabled is checked for its corresponding block in the generated script:
- BitLocker: `schtasks /create`, `Enable-BitLocker.ps1`
- Auto-logon: registry write for `AutoAdminLogon`
- Password never expires: `net accounts /maxpwage:unlimited`
- Computer rename: `wmic computersystem` or `Rename-Computer` invocation
- Certificates: `certutil -addstore` for each cert
- Scheduled tasks: `schtasks /create` for each `ScheduledTaskConfig`

#### `[STAGED FILES]`
- `C:\GIB\GIBFirstBoot.exe` exists in the mounted WIM (`workspace\mount\GIB\GIBFirstBoot.exe`)
- `C:\GIB\apps.json` exists
- Each `StagedApp.InstallerPath` is present at `workspace\mount\GIB\Installers\<filename>`

#### `[DRIVERS]`
If any driver folders are configured (manual or auto-fetched):
1. Counts `.inf` files across all source folders (`infsExpected > 0` if any folder is readable).
2. Runs `DISM /Get-Drivers /Image:workspace\mount\ /Format:Table` and counts `oem<N>.inf` tokens.
3. **FAIL** if `infsExpected > 0 && oemCount == 0` — DISM injected nothing despite receiving driver folders.
4. **WARN** if `oemCount < infsExpected` — some INFs may have been rejected (unsigned, incompatible arch).
5. **PASS** if `oemCount >= infsExpected`.

#### `[LANGUAGE PACKS]`
For each CAB in `LanguagePackPaths`:
1. Extracts all locale tokens via `ExtractAllLocaleTokens(filename)` — two regex passes:
   - `\b([a-z]{2,3}-[A-Za-z]{2,4})\b` — dash-bounded (catches `zh-cn` in `LanguageFeatures-Basic-zh-cn-Package`)
   - `[_~]([a-z]{2,3}-[A-Za-z]{2,4})(?=[_~.\-]|$)` — underscore/tilde-bounded (catches `zh-cn` in `Client-Language-Pack_x64_zh-cn.cab`)
2. Runs `DISM /Get-Packages /Image:workspace\mount\ /Format:Table`.
3. Searches for any DISM output line containing `"Language"` (case-insensitive) that also contains any of the extracted tokens.
4. **PASS** if matched. **WARN** if not matched or no tokens could be extracted — the engine's per-CAB DISM log (in `build-<timestamp>.log`) is the authoritative failure record; the validator downgrades to WARN rather than FAIL to avoid halting a valid build over a filename-parsing miss.

> **Design note:** The decision to WARN rather than FAIL on no-match is deliberate. DISM failures are already logged at injection time; a validator FAIL over a filename heuristic miss would cost a full rebuild without providing diagnostic value beyond what the build log already contains.

#### `[WALLPAPER]`
If `WallpaperPath` is set:
1. Gets the source file length via `TryFileLength(WallpaperPath)`. If the source is unreadable at validation time (`< 0`), emits WARN and skips — the failure is on the build machine, not the image.
2. Checks `img0.jpg`, `img19.jpg`, `img20.jpg` in `workspace\mount\Windows\Web\Wallpaper\Windows\`. At least one must match the source file's byte length.
3. Same logic for 4K variants: any `img0_*.jpg` under `Windows\Web\4K\Wallpaper\Windows\`.
4. **FAIL** if `srcLen >= 0 && matched == 0` — takeown/icacls likely failed silently.

#### `[BITLOCKER]` (if `BitLockerEnabled`)
Reads `Enable-BitLocker.ps1` from the staged location and checks for:
- `Enable-BitLocker` cmdlet invocation
- A key protector (`-RecoveryPasswordProtector` or `-TpmProtector`)
- `try { ... } catch { ... }` error handling
- `exit 1` in the catch block
- A run-once marker write to registry (prevents double-encryption on re-run)
- If `BitLockerKeySavePath` is set: recovery key export logic is present

#### `[DEPLOYMENT SCRIPTS]` (if any scripts are staged)
- Each script file exists at `workspace\mount\Users\Public\Documents\<filename>`
- If `RunAtStartup == true`: a relay trigger exists in `workspace\mount\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\`

#### `[BLOATWARE]` (if `BloatwareToRemove` is non-empty)
Runs `DISM /Get-ProvisionedAppxPackages /Image:workspace\mount\` and checks that none of the requested prefixes appear in the output. Emits WARN (not FAIL) for each still-present package.

#### `[FEATURES]` (if either list is non-empty)
Runs `DISM /Get-Features /Image:workspace\mount\ /Format:Table`. For each requested feature:
- **PASS** if `EnabledFeatures` member's state starts with `"Enabled"` (covers `"Enabled"` and `"Enable Pending"` after offline inject, and trailing-whitespace variants)
- **PASS** if `DisabledFeatures` member's state starts with `"Disabled"` or `"Disable Pending"`
- **FAIL** if feature name is not found in the output at all (likely a typo in the feature name)
- **FAIL** if state does not match request (feature was not applied)

#### `[AUTO-FETCH ASSETS]`
- Each `.msu`/`.cab` in `AutoFetchedUpdates` exists on disk (the download may have resumed from a prior session)
- Each `AutoFetchedDriverPack` has a non-empty `ExtractedPath` that exists on disk

#### `[TRUSTED CERTIFICATES]` (if any certs are staged)
- Each cert file exists at `workspace\mount\GIB\Certs\<filename>`
- `SetupComplete.cmd` contains a `certutil -addstore <store>` invocation for each cert

#### `[CUSTOM FONTS]` (if any fonts are staged)
- Each font file exists at `workspace\mount\Windows\Fonts\<filename>`
- For each font's display name, the corresponding registry value `"<DisplayName> (TrueType|OpenType)"` is present under `HKLM\GIB_VAL_FONT_SW\Microsoft\Windows NT\CurrentVersion\Fonts`
- If the display name contains any of `" ' & %` (characters that confuse `reg.exe` quoting), the check is downgraded to WARN to avoid a false FAIL from a parser quoting bug

#### `[REGISTRY]`
Loads `GIB_VAL_SW`, `GIB_VAL_SYS`, `GIB_VAL_USR` and for each `CustomRegistryEntry` and `GroupPolicyEntry` with `Operation == "SET"`:
- Runs `reg query <mapped_key> /v <value_name>` and checks the output contains the expected value data
- HKCU Group Policy entries skip the `Software\` prefix strip (NTUSER.DAT stores them as `Software\Policies\...` — unlike the build-time mapper which handles this transparently)
- FAIL if a SET entry is absent or has wrong data; WARN if the reg query itself errors and the display name contains special chars

---

## 6. Registry Offline-Hive Mapping

`MapOfflineKey(string liveKey, string hiveAlias)` translates live registry paths to their offline-hive equivalents for use with `reg.exe`.

| Live key prefix | File in WIM | Hive alias (build) | Hive alias (validator) | Transformation |
|---|---|---|---|---|
| `HKLM\SOFTWARE\` | `Windows\System32\config\SOFTWARE` | `OFFLINE_SW` | `GIB_VAL_SW` | Strip `SOFTWARE\` prefix |
| `HKLM\SYSTEM\` | `Windows\System32\config\SYSTEM` | `OFFLINE_SYS` | `GIB_VAL_SYS` | Strip `SYSTEM\` prefix |
| `HKCU\` | `Users\Default\NTUSER.DAT` | `OFFLINE_USR` | `GIB_VAL_USR` | Keep full path after `HKCU\` (do NOT strip `Software\`) |

**HKCU note:** NTUSER.DAT stores paths as `Software\Policies\...`, not `Policies\...`. The mapper for HKCU does not strip any prefix — `HKCU\Software\Policies\X\Y` maps to `HKLM\OFFLINE_USR\Software\Policies\X\Y`. This differs from the SOFTWARE hive where `HKLM\SOFTWARE\Policies\X\Y` maps to `HKLM\OFFLINE_SW\Policies\X\Y` (the `SOFTWARE\` prefix is stripped).

Group Policy `User`-class entries are an exception — they always target `OFFLINE_USR` regardless of whether the GPE source path starts with `HKCU` or `HKLM\SOFTWARE`.

---

## 7. Autounattend / Unattend / SetupComplete Generation

`GenerateUnattendAsync()` produces three files. All XML output uses UTF-8 with BOM (`new UTF8Encoding(true)`). `--` is never used inside XML comments (XML spec prohibits it; the DISM XML parser rejects files containing it).

### `Autounattend.xml` structure

```
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="windowsPE">
    <!-- DiskConfiguration: diskpart clean, primary partition, format NTFS, boot flag -->
    <!-- ImageInstall: install.wim source, edition index, WillShowUI Never -->
    <!-- UserData: ProductKey (if set), AcceptEula, FullName, Organization -->
    <!-- SetupUILanguage: UILanguage, FallbackUILanguage -->
    <!-- InputLocale, SystemLocale, UILanguage, UserLocale -->
  </settings>
  <settings pass="specialize">
    <!-- ComputerName (template) -->
    <!-- TimeZone -->
    <!-- OEMInformation -->
    <!-- (Domain join if configured) -->
  </settings>
  <settings pass="oobeSystem">
    <!-- OOBE: HideEULAPage, HideLocalAccountScreen, HideOEMRegistrationScreen,
                HideOnlineAccountScreens, HideWirelessSetupInOOBE, ProtectYourPC=3 -->
    <!-- AutoLogon: Enabled, Username, Password (base64) -->
  </settings>
</unattend>
```

### Password encoding

All passwords in generated scripts and XML are passed through `ToPsEncodedCommand()`:
```csharp
private static string ToPsEncodedCommand(string password)
    => Convert.ToBase64String(Encoding.Unicode.GetBytes(password));
```

This produces a UTF-16 LE base64 string. In `SetupComplete.cmd`, the password is passed to PowerShell as `-EncodedCommand <base64>`, where the decoded script contains `ConvertTo-SecureString`. Plain text passwords never appear in any generated file.

### `SetupComplete.cmd` structure

`SetupComplete.cmd` runs from `C:\Windows\Setup\Scripts\` at the end of Windows Setup, before first user login, with SYSTEM privileges. Sequence:

1. Rename computer (if `ComputerName` template is set) — queries WMI BIOS serial/UUID/asset tag, applies template substitution
2. Set power plan (`powercfg /setactive`)
3. Register OEM branding registry keys
4. Uninstall OneDrive (if toggled)
5. Write auto-logon registry (`AutoAdminLogon`, `DefaultUserName`, `DefaultPassword` — base64 decoded at runtime via PowerShell)
6. Password never expires (`net accounts /maxpwage:unlimited`)
7. Import trusted certificates (`certutil -addstore <store> "<path>"` for each cert)
8. Register scheduled tasks (`schtasks /create ...`)
9. Register BitLocker schtask (runs `Enable-BitLocker.ps1` after first login, using a run-once marker to prevent double-encryption)
10. Log that Setup is complete, reboot if computer name changed

---

## 8. GIBFirstBoot — First-Boot Installer Protocol

`GIBFirstBoot.exe` is a self-contained .NET 8 WPF application injected into the image at `C:\GIB\`. It is registered in `RunOnce` (not `Run`) so it executes exactly once per user-session trigger — GIBFirstBoot itself re-adds itself to `RunOnce` if it exits with work remaining.

### `apps.json` schema

```json
[
  {
    "DisplayName": "7-Zip 24.08",
    "InstallerPath": "C:\\GIB\\Installers\\7z2408-x64.msi",
    "InstallerType": "MSI",
    "Arguments": "",
    "MstPath": "C:\\GIB\\Installers\\7zip-org.mst",
    "TimeoutMinutes": 60,
    "AcceptedExitCodes": [0, 1641, 3010],
    "Order": 1
  }
]
```

Apps are installed in `Order` ascending. For MSI: `msiexec.exe /i "<path>" /qn /norestart [TRANSFORMS="<mst>"] <Arguments>`. For EXE: `<path> <Arguments>`.

### `state.json` schema

```json
{
  "CompletedApps": ["7-Zip 24.08"],
  "FailedApps": [],
  "LastRunTime": "2026-04-01T09:15:00Z"
}
```

On each run, GIBFirstBoot reads `state.json` and skips apps in `CompletedApps`. This enables resumable installs across reboots — apps that require a reboot (exit code 3010 / 1641) leave the remaining apps for the next login trigger.

### Completion sequence

1. All apps completed → remove `HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce\GIBFirstBoot` key
2. Schedule self-deletion: `cmd.exe /c timeout /t 3 & rd /s /q C:\GIB\`

### Failure behaviour

Any app that exits with a code not in `AcceptedExitCodes`:
1. Added to `state.json.FailedApps`
2. GIBFirstBoot re-adds itself to `RunOnce` for the next login
3. `C:\GIB\` folder is preserved for diagnostics

---

## 9. Auto-Fetch Subsystem (Windows Updates + OEM Drivers)

### Windows Update Catalog (`MsCatalogWebService`)

1. Queries the Microsoft Update Catalog search endpoint with the OS build number detected from the source WIM (via `IsoAnalyzer.cs` which reads WIM metadata via DISM `/Get-WimInfo`).
2. Filters results to `x64` cumulative updates for the specific release version (e.g. `25H2`).
3. Returns `CatalogEntry[]` with title, KB number, size, download URL, and SHA-256 checksum.

Download is handled by `ResumableDownloader`:
- Uses `Range` HTTP header to resume partial downloads
- Streams to a temp file, computes SHA-256 incrementally
- Verifies hash at completion; deletes temp file and retries on mismatch
- Progress reported via `IProgress<DownloadProgress>` (bytes received, total bytes, speed MB/s)

Cache lives at `%LOCALAPPDATA%\GoldenISOBuilder\Cache\Updates\`. `CatalogCacheManager` maintains an LRU index; items older than 30 days or beyond the 10 GB cap are pruned on app startup.

### Dell Driver Packs (`DellSoftPaqExtractor`)

1. Fetches Dell's driver catalog XML from `https://downloads.dell.com/catalog/DriverPackCatalog.cab`.
2. Parses entries for the target model, selects the latest A-rev for Win11.
3. Downloads the self-extracting archive (`.exe`).
4. Extracts silently: `<sfx>.exe /S /E=<destDir>`.
5. Post-extraction: walks `<destDir>` for `.inf` files, reports count and total bytes.

Extraction runs on the thread pool (`Task.Run`) — file enumeration on large driver packs can take several seconds.

### HP Driver Packs (`HpSoftPaqExtractor`)

1. Downloads HP's SoftPaq catalog (`HPClientDriverPackCatalog.cab`).
2. Selects the WinPE + OS pack for the target model.
3. Downloads + extracts: `<sfx>.exe -s -e -f"<destDir>"`.

CMSL (HP Client Management Script Library) is queried if available, falling back to the static catalog.

### Lenovo Driver Packs (`LenovoSoftPaqExtractor`)

1. Parses Lenovo's PSREF catalog for the target machine type.
2. Downloads the driver pack installer.
3. Extracts: `<installer>.exe /VERYSILENT /DIR="<destDir>" /EXTRACT=YES`.

All three extractors follow the same pattern:
- Pre-extraction: delete and recreate `destDir` on the thread pool
- Post-extraction: count INFs and compute total bytes on the thread pool
- Report via `DriverPackExtractResult { InfCount, TotalBytes, ExtractedPath }`

---

## 10. Hyper-V Test-in-VM

`Services/HyperVService.cs` orchestrates a temporary Hyper-V VM used to verify the produced ISO boots correctly.

### VM Creation (`CreateAndStartAsync`)
1. Checks Hyper-V availability via WMI `Msvm_VirtualSystemManagementService`.
2. Creates a Gen 2 VM with Secure Boot (template: `MicrosoftUEFICertificateAuthority`), TPM 2.0, and 4 GB RAM.
3. Attaches the output ISO as a DVD drive and sets it as boot device.
4. Creates a fixed VHD at `workspace\TestVM\disk.vhdx` (60 GB).
5. Starts the VM.

### Thumbnail Preview

`TryCaptureBgr565(width, height, buffer)` calls `Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage` — a still-image WMI method, not a video stream. The UI holds a reused `WriteableBitmap` updated at 250 ms intervals via `DispatcherTimer`. Capture is guarded by `_previewBusy` (a non-reentrant flag) so slow WMI calls cannot overlap.

Capture size is fixed at `1024×768`. Hyper-V re-rasterises on dimension change; variable sizes produced visible jitter.

### Interactive Control

`LaunchVmConnect()` implements focus-or-launch deduplication:
1. If `_vmConnectProc` is still alive, restore/focus its window via `ShowWindow(SW_RESTORE)` + `SetForegroundWindow`.
2. Else, `EnumWindows` looking for a title containing `"<VMName> on localhost"` (covers vmconnect started from Hyper-V Manager directly).
3. Else, `Process.Start("vmconnect.exe", "localhost <VMName>")`.

### Cleanup (`StopAndDeleteAsync`)
1. `KillTrackedVmConnect()` — sends `WM_CLOSE` to the vmconnect process. Must precede VM deletion; otherwise vmconnect pops a Watson dialog when its VM disappears.
2. Powers off the VM via WMI `RequestStateChange(3)` (hard off).
3. Removes the VM.
4. Deletes `workspace\TestVM\`.

### Design constraints (do not attempt to change)
- **Do not embed vmconnect.exe in the WPF window** — HwndHost reparenting fails on GPU/DirectComposition; `MsRdpClient10NotSafeForScripting` ActiveX returns `E_NOINTERFACE` for `IMsRdpExtendedSettings` on modern Windows builds. Both approaches have been attempted and removed.
- **The thumbnail is not live video** — it is a polling-based still image, matching Hyper-V Manager's own behaviour (same WMI API, same ~4 FPS practical limit).

---

## 11. Theme System

Two resource dictionaries in `Resources/`:
- `AppColors.xaml` — dark theme (default)
- `AppColorsLight.xaml` — light theme

At runtime, the theme switcher removes one dictionary and inserts the other into `Application.Current.Resources.MergedDictionaries`. Any control that used `FindResource("FGxBrush")` or `Application.Current.Resources["FGxBrush"]` at construction time holds a static snapshot and will not update. All dynamic brushes must be applied via `SetResourceReference`:

```csharp
// Correct — updates when theme changes:
myTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");

// Incorrect — frozen at construction time:
myTextBlock.Foreground = (Brush)FindResource("FG0Brush");
```

The XAML key `FG0Brush` through `FG3Brush` are the four foreground shades (lightest to darkest). `AccentBrush` is the ALE gold (`#C9A42A`). Background grades are `BG0` through `BG4`.

---

## 12. Settings Persistence

`Helpers/AppSettingsLoader.cs` reads and writes `%LOCALAPPDATA%\GoldenISOBuilder\settings.json` using `System.Text.Json`.

Settings survive uninstall and reinstall because they are under `%LOCALAPPDATA%`, not `%ProgramFiles%`. The class is called once at `App.OnStartup` before `MainWindow` is constructed; the loaded values are applied to `AppSettings.Current` (a static singleton). Pages bind to `AppSettings.Current` directly.

Any property added to the settings class must have a `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` attribute or a safe default, so existing `settings.json` files missing the new key still load without error.

---

## 13. Logging and Diagnostics

### Build log (`build-<timestamp>.log`)

Written by `BuildEngine` to `<OutputPath>\build-<timestamp>.log` throughout the run. Every stage transition, every DISM invocation, every stdout/stderr line, and every exception with stack trace is logged. The log is append-only and is closed when the pipeline completes (success or failure).

Format: `[HH:mm:ss.fff] [LEVEL] [stage] message`

Levels: `INFO`, `WARN` (soft step failure — build continues), `ERR` (fatal failure), `DISM` (raw DISM output).

### Validation report (`ValidationReport_<timestamp>.txt` / `.html`)

Plain-text version: one line per check, `[PASS]` / `[WARN]` / `[FAIL]` prefix.  
HTML version: same data, styled with the ALE navy/gold palette for readability.

Both are written to `<OutputPath>` regardless of whether the build succeeds or fails, so the operator can review what passed/failed after a FAIL-halt.

### Crash log (`%LOCALAPPDATA%\GoldenISOBuilder\crash.log`)

`App.xaml.cs` registers `AppDomain.CurrentDomain.UnhandledException` and `Application.Current.DispatcherUnhandledException`. Both handlers write the exception type, message, and full stack trace to the crash log. The main window remains open (the exception is marked handled) so the user can save their profile before closing.

### Job Object (child-process lifecycle)

`HyperVService` creates a Win32 Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and assigns every spawned `powershell.exe` to it. When `GoldenISOBuilder.exe` exits (even via Task Manager), the Job Object's kernel handle is closed, and all child processes are terminated automatically. This prevents orphaned DISM-adjacent PowerShell processes from holding WIM mounts across crash scenarios.

---

## 14. Security Model

### Admin elevation

The installer sets `PrivilegesRequired=admin` in the Inno Setup script, so the installed shortcut auto-elevates via UAC. The engine validates elevation at stage 1 and throws if not elevated.

### Password handling

- `BuildSession.AdminPassword` stores the password in plaintext **in memory only**.
- When serialised to `.gibprofile`, the password is stored in plaintext — treat `.gibprofile` files as sensitive and restrict file ACLs accordingly.
- When written to any generated script (`Autounattend.xml`, `SetupComplete.cmd`), the password is always base64-encoded as a PowerShell `-EncodedCommand` blob. No generated file on disk contains a plaintext password.

### Offline-hive registry writes

All registry writes go through `reg.exe` with `reg.exe load` / `reg.exe unload`. The `reg.exe` process inherits the elevated token of `GoldenISOBuilder.exe`. No WMI registry provider, no `Microsoft.Win32.RegistryKey` calls against offline hives.

### Certificate trust chains

Certificates are staged into `C:\GIB\Certs\` inside the image and imported in `SetupComplete.cmd` via `certutil -addstore <store> "<path>"`. The store (`Root`, `Intermediate`, `TrustedPublisher`) is user-specified via the Step 3 UI. GoldenISOBuilder never adds certificates to the build machine's own trust stores.

---

## 15. Error Handling Philosophy

| Call type | Error handling |
|---|---|
| External process (DISM, PowerShell, reg.exe, robocopy) | `try/catch` in every caller; `Step()` callers rethrow; `StepSoft()` callers log and continue |
| File I/O (copy, read, delete) | `try/catch`; soft steps return `false`/`""` from catch |
| WMI (HyperVService) | `try/catch` per call; on error, `_thumbSvc`/`_thumbVm` cache is invalidated; caller receives `false` |
| HTTP (downloader) | `try/catch` with retry up to 3 times; resume via `Range` header on reconnect |
| XML parsing (validator) | `try/catch`; parse failure emits FAIL for that section |

`Step()` wraps the callback in a `try/catch` and, on exception, sets the build status to `Failed`, emits the exception to the log and progress stream, then rethrows to unwind the pipeline. Cleanup (`DISM /Unmount-Image /Discard` if mounted) is in a `finally` block in `RunAsync`.

`StepSoft()` wraps the callback in a `try/catch` and on exception logs the message as a `!`-prefixed warning, increments `_softFailCount`, and returns. The pipeline continues to the next stage.

---

## 16. Known Design Decisions and Constraints

### No ViewModel layer

Every wizard page reads and writes `BuildSession.Current` directly. This was a deliberate simplicity decision — the project is maintained by a single engineer and the wizard is strictly linear. A full MVVM layer would add indirection with no architectural benefit at this scale.

### `StepSoft()` vs `Step()`

Only three stages are hard-fatal: `precommit_validate`, `unmount`, and `buildiso`. All content-injection stages are soft. This means a 2-hour build is never aborted because a single bloatware package refused removal or a single optional feature failed to enable — those are logged and the build continues.

### DISM `/IgnoreCheck` on updates

Some MSU/CAB cumulative updates carry applicability rules referencing a live system's registry (`CurrentVersion`, `BuildLabEx`). When applied offline, the check fails even though the update itself is valid for the image. `/IgnoreCheck` bypasses applicability validation. This is the correct approach for offline servicing, but means DISM will attempt to apply updates even if they are not applicable — non-zero exit codes from such attempts are treated as soft warnings.

### Wallpaper replacement via file length check

The validator confirms wallpaper replacement by comparing the **byte length** of the replacement file against the target paths in the WIM. It does not do image content comparison. This is sufficient because the target slots (`img0/img19/img20.jpg`) have known stock lengths from the original Microsoft WIM; after replacement the lengths will differ. A false-pass is theoretically possible if the source and stock files happen to have the same byte count — judged an acceptable trade-off for implementation simplicity.

### Language pack validator: WARN not FAIL on no-match

The locale extractor uses filename heuristics (two regex patterns). Microsoft's package filenames are not formally specified and vary across components (e.g. `Client-Language-Pack_x64_zh-cn.cab` vs `LanguageFeatures-OCR-zh-cn-Package~...cab`). A FAIL on a filename-parsing miss would cost a full rebuild without confirming the injection actually failed. The build log's per-CAB DISM output is the authoritative failure record; the validator treats no-match as WARN.

### Auto-logon: no `ForceLegacySetupClientAsync`

An earlier version patched `boot.wim` to force `setup.exe` (legacy setup client) via `winpeshl.ini`. This was removed: `setup.exe` does not bridge `<AutoLogon>` from `Autounattend.xml` to the Windows 11 OOBE subsystem, so auto-logon silently failed. The modern setup client (`setupprep.exe`) handles `<AutoLogon>` correctly; disk wiping (preventing the upgrade dialog) is handled entirely by the `diskpart clean` + partition-create steps in the `windowsPE` Autounattend pass.

---

## 17. Extension Points

### Adding a new wizard step

1. Create `Views/StepNPage.xaml` + `.xaml.cs`.
2. Add a page instance to `MainWindow.xaml` in the `Grid` that contains all pages.
3. Wire `NavigateRequested` in `MainWindow.xaml.cs`.
4. Add a `SaveToSession()` implementation that writes from UI into `BuildSession.Current`.
5. Add any new `BuildSession` properties with safe defaults.

### Adding a new pipeline stage

1. Write `private async Task MyNewStepAsync()` in `BuildEngine.cs`.
2. Call `await Step("myStep", MyNewStepAsync)` (fatal) or `await StepSoft("myStep", MyNewStepAsync)` (soft) in `RunAsync()` at the appropriate position in the pipeline.
3. If the step mutates the WIM, consider whether a pre-commit validator check is needed. If so, add a `CheckMyNewStepAsync()` method and wire it into `ValidateBuildContentsAsync()`.
4. Add the step's `BuildStep` descriptor to the `_steps` list (used by the pipeline progress view).

### Adding a new validator section

1. Write `private async Task<List<ValCheck>> CheckMyFeatureAsync(string mountDir)` following the existing pattern — return `ValCheck` records with appropriate status and detail text.
2. Call it from `ValidateBuildContentsAsync()` and append the results to the `checks` list.
3. Ensure every FAIL condition has been desk-checked against all known good inputs.

### Adding a new OEM driver extractor

1. Implement the interface: `Task<DriverPackExtractResult> ExtractAsync(DriverPackSelection pack, string destDir, IProgress<...> progress, CancellationToken ct)`.
2. Add the new class to `Services/Catalog/`.
3. Register it in `BuildEngine.ResolveDriverExtractor(vendor)`.
4. Ensure both the pre-extraction purge and post-extraction file walk run on the thread pool (`Task.Run`), not the UI thread.

### Adding a new `BuildSession` property

1. Add the property with a safe initialiser (`= ""`, `= false`, `= 0`, `= []`).
2. Old `.gibprofile` files that lack the new key will deserialize the property to its safe default — no migration needed.
3. If the property affects the pre-commit validator, extend the relevant `Check*Async` method.
4. If the property affects the Autounattend/SetupComplete generation, extend `GenerateUnattendAsync()`.

---

## 18. Build and Distribution

### Developer build

```powershell
cd GoldenISOBuilder
dotnet build -c Release -p:Platform=x64 --nologo
```

`GIBFirstBoot.exe` is auto-published and copied by MSBuild targets. No manual steps.

### Distribution publish (single-file)

```powershell
cd GoldenISOBuilder
dotnet publish -c Release -p:Platform=x64 -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true --nologo
```

`IncludeNativeLibrariesForSelfExtract` is required to bundle the WPF native DLLs (`D3DCompiler_47_cor3.dll`, `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`) into the single exe. Without it, those DLLs appear alongside the exe as loose files.

Publish output: `GoldenISOBuilder\bin\x64\Release\net8.0-windows10.0.17763.0\win-x64\publish\`

### Inno Setup installer

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" Installer\ALEImageForge.iss
```

Output: `Installer\Output\ALEImageForge-Setup-<version>.exe`

The installer:
- Requires and prompts for admin elevation (`PrivilegesRequired=admin`)
- Uses `AppMutex=Global\ALEImageForge_RunningInstance` to block installation over a running copy
- Bundles both `GoldenISOBuilder.exe` and `GIBFirstBoot.exe`
- Creates a Start-menu shortcut and an optional desktop shortcut

### Version bump checklist

When incrementing the version:
1. Update `#define MyAppVersion` in `Installer\ALEImageForge.iss`
2. Update version text in `GoldenISOBuilder\SplashWindow.xaml` (`Text="v2.x.x"`)
3. Update the version badge in `README.md`
4. Add a line to the Version history table in `README.md`
5. Rebuild, publish, compile installer, commit, push
