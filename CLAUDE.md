# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Debugging Philosophy
- When fixing crashes or runtime errors, FIRST diagnose the root cause before applying fixes. Read the full stack trace, examine the relevant resources/theme/binding setup holistically, and explain the root cause before editing.
- Do NOT apply piecemeal one-error-at-a-time fixes. If you see a XAML/binding/theme error, audit the entire resource dictionary and related templates in one pass.
- After a fix attempt, verify the actual outcome (don't assume success from a build passing - confirm the GUI/dialog actually launches correctly).

## Zero Blast Radius Rule — MANDATORY for every edit

This project has a 2-hour ISO build cycle. A bug introduced in any edit can go undetected until a full build is attempted, costing hours and requiring another full rebuild to fix. Every other feature in the app is currently working. The cost of a regression is extremely high.

### Before making any edit

1. **Read every file you will touch in full** — not just the target function. Understand the surrounding code, existing patterns, and all callers before writing a single line.
2. **Search for all callers of any function you modify or remove.** Use Grep across the whole solution. Removing or renaming something that is called elsewhere silently breaks the build.
3. **Understand the data flow end-to-end** — trace how data enters the function, how it is transformed, and how the result is consumed downstream. A regex that matches the wrong line (e.g. DISM's own header vs the image version) is a logic bug that compiles cleanly but produces wrong results.
4. **Check for shared state** — `BuildSession.Current` is global. Writing a new property is safe; mutating an existing one mid-flow can corrupt session state for other pages.

### Rules for additive changes (new properties, new methods, new files)

- New `BuildSession` properties must have a safe default so old `.gibprofile` files deserialize without error. Always initialise with `= ""`, `= false`, `= 0`, or `= []`.
- New methods must be wrapped in `try/catch` if they call external processes (DISM, PowerShell, net I/O) — never let a new best-effort feature crash an existing flow. Return a safe default (`""`, `false`, `null`) from the `catch`.
- New DISM calls must be read-only queries (`/Get-*` only). Never add a mutating DISM call (`/Add-*`, `/Remove-*`, `/Commit`) outside the BuildEngine pipeline.

### Rules for modifying existing functions

- **Touch only the lines required.** Do not reformat, rename, or reorganise surrounding code "while you are there". Every additional line changed is a new surface for a bug.
- **Regex patterns against external process output** (DISM, PowerShell, reg.exe): the output always has a header section before the data section. A pattern that matches the first occurrence in the full output will often hit the header, not the data. Always anchor the regex to the correct section of the output (e.g. find the `Index : 1` line first, then search the substring after it).
- **Do not change method signatures** of existing public/internal methods — other pages call them by signature. Adding an overload is safe; changing a parameter type or count is not.
- **Do not add `status?.Invoke(...)` calls** inside loops or hot paths — they marshal to the UI thread and can cause visible lag on slow machines.

### Self-check before submitting any edit

Answer these questions mentally before finalising each edit:

| Question | Why it matters |
|---|---|
| Does every code path in my change return a safe value on failure? | External calls (DISM, HTTP) can fail; a thrown exception inside ISO analysis aborts the entire scan |
| If this property is missing from an old `.gibprofile`, does loading the profile still work? | Users load profiles from previous versions constantly |
| Did I read the full output format of any external tool I'm parsing? | Tools print headers before data; first-match regex hits the header |
| Is the method I removed referenced anywhere else in the solution? | Grep first — the compiler will catch it but only at build time, not at edit time |
| Does my change affect any code path outside the feature I was asked to fix? | If yes, stop and re-scope the edit |

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

## Test in VM page (`Views\TestVmPage.xaml[.cs]`, `Services\HyperVService.cs`)

The "Test in VM" feature lets the user boot the built ISO inside Hyper-V. **Important design constraints — don't fight these:**

- **The in-app preview is intentionally a read-only thumbnail.** It calls Hyper-V WMI `Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage`. That API is a still-image snapshot, NOT a video stream — Hyper-V Manager's own preview pane uses the same API at ~4 FPS for the same reason. Do not try to make this "live video". Real-time interactive video is only available through `vmconnect.exe` (RDP/Enhanced Session).
- **Interactive control is delegated to `vmconnect.exe` in a separate window.** Clicking the in-app preview, or the `Fullscreen` / `Connect via RDP` toolbar buttons, calls `HyperVService.LaunchVmConnect()`.
- **Do NOT re-attempt embedding `vmconnect.exe` or `MsTscAx` inside the WPF window.** This was tried and abandoned:
  - HwndHost reparenting of `vmconnect.exe`: vmconnect's GPU/DirectComposition rendering is tied to its original top-level window, so reparenting yields a black surface even though input works.
  - Hosting `MsRdpClient10NotSafeForScripting` ActiveX via WindowsFormsHost + AxHost: on this user's Windows build, `IMsRdpExtendedSettings` returns `E_NOINTERFACE`, breaking the PCB / pre-auth credential flow. White-flash → disconnect.
  - The deleted files `Services\EmbeddedVmConnectHost.cs`, `Services\EmbeddedRdpVmHost.cs`, `Services\EmbedRdpLogger.cs` exist in git history if you need the prior research.

### Thumbnail capture performance

`HyperVService.TryCaptureBgr565(w, h, buffer)` writes the thumbnail directly into a caller-provided Bgr565 byte buffer. The UI side keeps a reused `WriteableBitmap` + buffer and writes via `WritePixels` — this avoids per-tick allocation and `Image.Source` replacement (which caused visible flicker).

- The service caches `Msvm_VirtualSystemManagementService` and the per-VM `Msvm_ComputerSystem` (`_thumbSvc`, `_thumbVm`) so a tick costs only the thumbnail call itself, not the ~100 ms of WMI lookups. Cache is invalidated in `StopAndDeleteAsync` and on any capture error.
- Capture size is fixed at 1024×768. Hyper-V re-rasterises whenever requested dimensions change, so varying sizes (e.g. passing `ActualWidth`/`ActualHeight`) produced jitter. The WPF Image scales via `Stretch="Uniform"` + `RenderOptions.BitmapScalingMode="LowQuality"`.
- Tick interval is 250 ms; ticks are guarded by `_previewBusy` so async captures cannot overlap.

### vmconnect window management — focus-or-launch

`HyperVService.LaunchVmConnect()` is **not** "always spawn a new window". `vmconnect.exe` does no deduplication on its own — every launch creates a new top-level RDP window pointed at the same VM. The service therefore:

1. If `_vmConnectProc` is alive → restore (if minimised) and `SetForegroundWindow` its main window.
2. Else, fallback: `EnumWindows` for a window title containing `"<VMName> on localhost"` (covers the case where vmconnect was launched from Hyper-V Manager outside our app) → focus it.
3. Else → `Process.Start("vmconnect.exe localhost <VMName>")` and track the new process.

`KillTrackedVmConnect()` is called at the start of `StopAndDeleteAsync` — closing the console **before** the VM disappears prevents vmconnect from popping a "VM is no longer available" Watson dialog.

### TestVmPage ISO field

- `_selectedIsoPath` is auto-populated from `BuildSession.Current.LastBuiltIsoPath` (set by `BuildEngine.RunAsync` after a successful build) — both on first load and on every `IsVisibleChanged → visible`, so a freshly built ISO appears in the field when the user navigates here after a build.
- The `IsoPathBox` `TextBox` uses an inline `ControlTemplate` that strips the default WPF TextBox chrome down to just a `PART_ContentHost` ScrollViewer. Without this, the default template's background bleeds through as a colour difference inside the wrapping `Border`.

### `StartVmBtn` gating

`UpdateStartButtonEnabled()` is the **only** code path that should set `StartVmBtn.IsEnabled`. It enables only when (Hyper-V available) ∧ (VM state == Idle) ∧ (ISO selected and exists), and assigns a tooltip explaining the disabled reason. Call it from every state-changing handler (ISO picked, VM state change, Hyper-V availability check).

## App startup (`App.xaml.cs`, `SplashHost.cs`)

The splash screen runs on its **own STA thread** via `SplashHost.Start()`. The WPF BAML parse for `MainWindow.xaml` blocks the main UI thread for several seconds; without a separate dispatcher, the splash freezes at "Loading…". `SplashHost.SetStatus()` and `FadeOutAndCloseAsync()` marshal via the splash thread's dispatcher.

`HyperVService.ReapOrphanedVmsAsync()` is fired **after** `MainWindow.Show()` (not during startup) so a flaky PowerShell from a prior crash can't block the window from appearing. A Win32 Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) is created in `HyperVService` and every spawned `powershell.exe` is assigned to it via `AssignProcessToJobObject`, so child processes die with the app.

## Step 6 build progress page (`Views\Step6Page.xaml[.cs]`)

`CinematicView` is the default visualisation; `PipelineView` is one click away on the segmented toggle in the page header. Don't flip these without checking with the user — the default was deliberately set to cinematic so non-power-users aren't dropped straight into a dense pipeline log.

## Publishing & installer

**Single-file publish (WPF gotcha):** plain `PublishSingleFile=true` leaves native DLLs (`D3DCompiler_47_cor3.dll`, `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`) alongside the exe. Add `-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true` to embed them into the exe. The resulting `GoldenISOBuilder.exe` is ~75 MB; `GIBFirstBoot.exe` remains as a separate ~70 MB file next to it (the build engine copies it into ISOs at runtime).

**Inno Setup installer:** `Installer\ALEImageForge.iss` bundles both exes into a single setup. Compile with `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe Installer\ALEImageForge.iss`. The installer is `PrivilegesRequired=admin` (the app needs admin for DISM) and uses `AppMutex=Global\ALEImageForge_RunningInstance` to block upgrade while the app is running. Output: `Installer\Output\ALEImageForge-Setup-<version>.exe`.

When bumping `MyAppVersion` in the `.iss`, also bump the assembly/file version if you add one — the `AppMutex` does not protect against side-by-side installs of two different `MyAppVersion` values, only against installing over a running instance.
