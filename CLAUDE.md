# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Debugging Philosophy
- When fixing crashes or runtime errors, FIRST diagnose the root cause before applying fixes. Read the full stack trace, examine the relevant resources/theme/binding setup holistically, and explain the root cause before editing.
- Do NOT apply piecemeal one-error-at-a-time fixes. If you see a XAML/binding/theme error, audit the entire resource dictionary and related templates in one pass.
- After a fix attempt, verify the actual outcome (don't assume success from a build passing - confirm the GUI/dialog actually launches correctly).

## UI/UX Conventions

### Mobile/Touch UI Rules
- Never use hover-only styles (e.g., opacity on hover) for interactive elements - they are invisible on touch devices. Always provide a persistent visible state for buttons, delete icons, and controls.
- Test responsive layouts assume mobile viewport unless told otherwise.

## Workflow Rules

### Bulk/Destructive Actions
- Never run 'fix everything' or batch destructive operations blindly. Always present the list of planned changes and get explicit confirmation before executing more than 3 related fixes at once.
- When an external API returns an error (e.g., 401 from warranty APIs), propose a fallback strategy before retrying.

## What This Project Is

**ALE ISO Creator** (internal name: GoldenISOBuilder) is a Windows enterprise deployment tool: a WPF wizard that customizes Windows 11 ISO images and injects a first-boot launcher that silently installs apps on the first login after deployment.

Two projects, one solution (`ALE ISO Creator.sln`):
- **GoldenISOBuilder** — the main WPF GUI (net8.0-windows10.0.17763.0, minimum for WinRT toast API)
- **GIBFirstBoot** — a self-contained first-boot WPF exe (~120 MB, bundles .NET 8 runtime) that runs on the freshly deployed machine

Published output: `GoldenISOBuilder\bin\x64\Release\net8.0-windows10.0.17763.0\win-x64\publish\`

## Build Commands

Working directory: `C:\Users\pgs6718\Downloads\ALE ISO Creator\GoldenISOBuilder\`

```
# Build only
dotnet build -c Release -p:Platform=x64 --nologo

# Publish (self-contained single exe — use this for distribution)
dotnet publish -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true --nologo
```

**Important:** Building GoldenISOBuilder triggers a `PublishAndCopyGIBFirstBoot` MSBuild target that auto-runs `dotnet publish` on GIBFirstBoot and copies the resulting `GIBFirstBoot.exe` into GoldenISOBuilder's output folder. A separate `CopyGIBFirstBootToPublishDir` target (AfterTargets="Publish") copies it into the publish folder. If `GIBFirstBoot.exe` is missing at runtime, the "Inject first-boot launcher" build step throws `FileNotFoundException`.

## Runtime Requirements

GoldenISOBuilder must run as **Administrator** — the BuildEngine validates this at step 1. Requires:
- `dism.exe` (present in System32 on all Windows 11 installs)
- `oscdimg.exe` from the **Windows ADK > Deployment Tools** (searched in standard ADK install paths)

## Architecture

### BuildSession — the single shared state object

`Models\BuildSession.cs` is a plain model class with a `static Current` property. Every wizard page reads/writes `BuildSession.Current` directly — there is no ViewModel layer. Pages call `SaveToSession()` before navigating away. All pages are instantiated once in `MainWindow.xaml` and shown/hidden by toggling `Visibility`.

Key `BuildSession` properties:

| Property | Notes |
|---|---|
| `IsoSourcePath` | Path to source `.iso` |
| `IsoSourceLanguage` | BCP-47 e.g. `"en-GB"` — fed into windowsPE `UILanguage`; mismatch causes `0x8007000D` |
| `TargetEdition` | e.g. `"Pro"` |
| `AdminUsername` | Default `"Administrator"` |
| `AdminPassword` | Always base64-encoded via `ToPsEncodedCommand()` in scripts — never plain text |
| `AutoLoginEnabled` | Wires auto-logon registry in SetupComplete.cmd |
| `PasswordNeverExpires` | Applied in SetupComplete.cmd |
| `SkipOobe` | If `false`, no `Autounattend.xml` is generated |
| `BitLockerEnabled` | Triggers Enable-BitLocker.ps1 staging |
| `BitLockerKeySavePath` | Empty = don't save recovery key to disk |
| `WallpaperPath` | Source file, copied into WIM |
| `StagedApps` | GIBFirstBoot installs these at first boot |
| `CustomRegistryEntries` | Written via offline hive |
| `GroupPolicies` | Written via same hive session |
| `DeploymentScripts` | Staged to Public\Documents + Startup trigger |

Profiles are `BuildSession` serialized to JSON as `.gibprofile` files.

### Navigation model

`MainWindow` owns all page instances and a `Navigate(page, step)` method. Pages raise a `NavigateRequested` event. The wizard is 8 steps by integer index:

| Index | Page | Configures |
|---|---|---|
| 0 | Step1Page | Source ISO, language picker, Windows edition |
| 1 | Step2Page | Wallpaper, staged apps, staged files, language packs, drivers, deployment scripts |
| 2 | Step3Page | Bloatware removal, security toggles, system defaults, group policies |
| 3 | Step4Page | Admin account, auto-logon, wallpaper preview |
| 4 | StepRegistryPage | Custom registry entries |
| 5 | StepAdvancedPage | Org name, product key, OOBE skip, power plan, timezone, OEM branding, optional features |
| 6 | Step5Page | Review / summary |
| 7 | Step6Page | Build progress |

### BuildEngine pipeline

`Services\BuildEngine.cs` runs `RunAsync()` on a background thread. `Step()` = fatal (throws, halts pipeline). `StepSoft()` = logs warning and continues.

Pipeline order:
1. **validate** — admin, paths, dism.exe, oscdimg.exe
2. **prepare** — workspace cleanup, `DISM /Cleanup-Wim` for orphaned mounts
3. **copyiso** — mount ISO → robocopy to `workspace\iso\` → dismount
4. **exportedition** — `DISM /Export-Image` to single-edition WIM
5. **mountwim** — `DISM /Mount-Image /Index:1`
6. **langpacks** *(soft)* — `DISM /Add-Package` per CAB
7. **drivers** *(soft)* — `DISM /Add-Driver /Recurse` per folder
8. **wallpaper** *(soft)* — takeown/icacls + overwrite `img0/img19/img20.jpg` + 4K + lock screen
9. **firstboot** *(soft)* — creates `C:\GIB\` in WIM, copies `GIBFirstBoot.exe`, stages installers, writes `apps.json`
10. **publicdesktop** *(soft)* — files to `Users\Public\Desktop\`
11. **deploymentscripts** *(soft)* — staged to `Public\Documents` + Startup folder trigger
12. **bloatware** *(soft)* — `DISM /Remove-ProvisionedAppxPackage`
13. **features** *(soft)* — `DISM /Enable-Feature` / `Disable-Feature`
14. **registry** *(soft)* — loads offline hives (`OFFLINE_SW`, `OFFLINE_SYS`, `OFFLINE_USR`), applies all tweaks via `reg.exe`, applies Group Policies, then unloads with retry (GC + 5 attempts/1s)
15. **unattend** *(soft)* — generates `Autounattend.xml`, `Unattend.xml`, `SetupComplete.cmd`
16. **precommit_validate** *(critical)* — validates build contents; writes `ValidationReport_<timestamp>.txt` to output folder; any FAIL halts the build
17. **unmount** *(critical)* — `DISM /Unmount-Image /Commit`
18. **buildiso** *(critical)* — `oscdimg.exe` UEFI + BIOS bootable
19. **verify** *(soft, skippable)* — SHA-256 of output ISO

### Registry offline hive mapping

Registry writes use `reg.exe` (not PowerShell) for offline hive compatibility. `MapOfflineKey()` maps:
- `HKLM\SOFTWARE\...` → `HKLM\OFFLINE_SW\...` (SOFTWARE prefix stripped)
- `HKLM\SYSTEM\...` → `HKLM\OFFLINE_SYS\...` (SYSTEM prefix stripped)
- `HKCU\...` → `HKLM\OFFLINE_USR\...` (targets default user template; **HKCU paths do NOT strip the `Software\` prefix** — NTUSER.DAT stores them as `Software\Policies\...`)

Pre-commit validator uses **separate** hive names (`GIB_VAL_SW`, `GIB_VAL_SYS`, `GIB_VAL_USR`) to avoid clashing with build hives. Always unloaded in a `finally` block.

### Pre-commit validator (`ValidateBuildContentsAsync`)

Checks every section dynamically based on what the user selected. Saves a report to `<OutputFolder>\ValidationReport_<timestamp>.txt`. Any FAIL stops the build.

Sections checked:
- **[SESSION]** — username, password, language, edition
- **[AUTOUNATTEND.XML]** — encoding, UILanguage match, ProductKey, OOBE, AutoLogon
- **[SETUPCOMPLETE.CMD]** — all feature blocks: BitLocker schtask, auto-logon registry, password-never-expires, rename, etc.
- **[STAGED FILES]** — GIBFirstBoot.exe, apps.json, each installer .msi/.exe
- **[BITLOCKER]** — Enable-BitLocker.ps1: cmdlet, key protector, try/catch, exit 1, run-once marker, recovery key logic
- **[DEPLOYMENT SCRIPTS]** — Public\Documents presence + Startup folder trigger
- **[REGISTRY]** — loads `GIB_VAL_*` hives, queries every configured registry value and group policy

**Key validation rules to know:**
- Admin password check: looks for `-EncodedCommand` only (the cmdlet is base64-encoded — never check for `Set-LocalUser` in plain text)
- HKCU Group Policy validator: skips stripping `Software\` prefix; guards with `gpe.PolicyClass != "User"` check

### GIBFirstBoot — first-boot installer

Installed at `C:\GIB\` inside the image. On first user login:
1. Reads `apps.json` (manifest), `state.json` (resume state)
2. Installs each pending app: EXE with custom args, or MSI with `/qn /norestart` + optional `.mst` transform
3. Per-app `TimeoutMinutes` (default 60, clamped 5–240); accepted exit codes configurable (default `0,1641,3010`)
4. All done → removes Run key, schedules self-deletion of `C:\GIB\` via detached `cmd.exe`
5. Failures → re-adds to `RunOnce` for retry on next login; preserves `C:\GIB\` folder

### Settings persistence

`AppSettingsLoader` reads/writes `%LOCALAPPDATA%\GoldenISOBuilder\settings.json`. Applied at `App.OnStartup` before the main window opens. Contains: theme, default output/workspace paths, `VerifyIsoAfterBuild`, `CleanWorkspaceAfterBuild`, `WimCompression` (max/fast/none), `SoundOnComplete`. Crash log: `%LOCALAPPDATA%\GoldenISOBuilder\crash.log`.

### Coding conventions

- Never block UI thread — all engine work is `async`/`await`
- `BuildSession.Current` is the single source of truth; pages call `SaveToSession()` before navigating
- Passwords always base64-encoded via `ToPsEncodedCommand()` before going into `.cmd` files
- `Step()` = fatal; `StepSoft()` = logs and continues
- Registry writes use `reg.exe`, never PowerShell, for offline hive compatibility

### Known dummy controls (not yet wired to engine)

- `DisableGuestToggle` (Step 4)
- `HideEulaToggle` (StepAdvanced)
- `OemPhoneBox` (StepAdvanced)
- `AutoLogonCount` — saved to session but engine hardcodes `LogonCount=1`

## Key Files for Debugging

| Problem area | File |
|---|---|
| Full build pipeline | `Services\BuildEngine.cs` |
| Autounattend / SetupComplete generation | `BuildEngine.GenerateUnattendAsync()` |
| Registry / Group Policy writes | `BuildEngine.ApplyRegistryAsync()`, `ApplyGroupPoliciesInner()` |
| Pre-commit validation logic | `BuildEngine.ValidateBuildContentsAsync()` |
| First-boot install logic | `GIBFirstBoot\MainWindow.xaml.cs` |
| Settings | `%LOCALAPPDATA%\GoldenISOBuilder\settings.json` |
| Build log | `<OutputPath>\build-<timestamp>.log` |
| Validation report | `<OutputPath>\ValidationReport_<timestamp>.txt` |
| Crash log | `%LOCALAPPDATA%\GoldenISOBuilder\crash.log` |
