# ALE Image Forge

**Enterprise Windows 11 image builder for Alcatel-Lucent Enterprise field deployment.**

ALE Image Forge takes a vanilla Windows 11 ISO and produces a fully-customised, ready-to-deploy ISO that installs unattended, joins your domain, applies your group policies, lays down your drivers and applications, and hands the user a desktop with everything already installed — in one boot.

Internal codename: **GoldenISOBuilder**.

![Version](https://img.shields.io/badge/version-2.4.7-blue) ![.NET](https://img.shields.io/badge/.NET-8.0--windows10.0.17763-512BD4) ![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D6) ![License](https://img.shields.io/badge/license-Internal-lightgrey)

---

## What it does

A WPF wizard that walks an administrator through 8 steps, then writes a customised `.iso` that:

- Boots unattended (no language picker, no upgrade dialog, no OOBE prompts)
- Installs Windows 11 silently with the chosen edition, admin account, time zone, keyboard, and locale
- Auto-logs into the Administrator account on first boot, runs `SetupComplete.cmd`, renames the machine to your hostname template, and reboots into a clean login screen
- On the user's first login, silently installs every staged MSI / EXE application via the bundled **GIBFirstBoot** launcher
- Applies hundreds of registry tweaks and Group Policy entries through offline-hive injection
- Slipstreams Windows cumulative updates and OEM driver packs (Dell / HP / Lenovo) auto-fetched from vendor catalogues
- Replaces wallpapers, lock-screen backgrounds, OEM branding, fonts, and trusted certificates
- Encrypts the drive with BitLocker on first boot, optionally saving the recovery key
- Runs a **pre-commit validator** that inspects the mounted WIM and fails the build BEFORE commit if anything is missing — so a 2-hour build never silently ships a broken image

---

## Why this exists

Bare Windows ISOs require dozens of manual steps after every install: drivers, apps, policies, branding, security baseline, BitLocker, language packs, the lot. Image Forge collapses all of that into one wizard, runs the configuration **offline** against the mounted WIM (no template VM, no Sysprep dance), and ships an ISO that "just works" the first time it boots on real hardware.

Built specifically for the ALE BTQ infra team's deployment cadence — 25H2-aware, Hyper-V-validated, signed-installer distribution.

---

## Components

Two projects in one solution (`ALE ISO Creator.sln`):

| Project | Purpose | Output |
|---|---|---|
| **GoldenISOBuilder** | The main WPF GUI. Runs on the admin's build machine. | `GoldenISOBuilder.exe` (~75 MB self-contained) |
| **GIBFirstBoot** | First-boot installer that gets injected into the ISO and runs on the freshly-deployed laptop at first login. Reads `apps.json` and installs every staged app silently. | `GIBFirstBoot.exe` (~70 MB self-contained, lives at `C:\GIB\` inside the deployed image) |

Both are bundled into a single Inno Setup installer: `ALEImageForge-Setup-<version>.exe`.

---

## The 8-step wizard

| # | Step | What you configure |
|---|---|---|
| 1 | **Source & Output** | Pick the vanilla Win11 ISO, choose the edition (Pro / Enterprise / LTSC), pick the boot language, set the output folder. |
| 2 | **Assets** | Wallpaper + lock screen, staged applications (MSI/EXE/MST), language packs, drivers (manual folders **or** auto-fetched OEM packs from Dell / HP / Lenovo + Microsoft Update Catalog), deployment scripts, custom fonts. |
| 3 | **Customizations** | Bloatware removal (provisioned-Appx packages), Windows 11 UX baseline (dark mode, file extensions, telemetry, Copilot/Recall/Widgets/Chat/Consumer-Features toggles), security defaults (SMBv1 off, Defender ATP on, BitLocker), trusted certificates (Root / Intermediate / TrustedPublisher with first-boot `certutil` import), Group Policies. |
| 4 | **Admin Account** | Username, password (always base64-encoded in scripts, never plaintext), auto-logon, password-never-expires. |
| 5 | **Registry** | Custom registry entries (SET or DELETE) into HKLM\SOFTWARE, HKLM\SYSTEM, or the Default User hive — applied via offline-hive injection with `reg.exe`. |
| 6 | **Advanced** | Optional Windows features (WSL2, NFS Client, OpenSSH Server, RSAT, Hyper-V, ...), OEM branding (Manufacturer / Model / Support URL shown in Settings → About), product key, OOBE skip, power plan, time zone, hostname template (`{PREFIX}{SERIAL}`, `{PREFIX}{LAST6_SERIAL}`, `{PREFIX}{LAST6_MAC}`, `{PREFIX}{ASSETTAG}`), scheduled tasks. |
| 7 | **Review** | Summary of every choice; profile save/load as `.gibprofile` JSON. |
| 8 | **Build** | Runs the 19-stage pipeline with live progress (cinematic + pipeline views). |

A separate **Test in VM** page boots the produced ISO inside Hyper-V (Gen 2, Secure Boot, TPM 2.0) and shows a live preview while you watch it install.

---

## Build pipeline (the 19 stages)

1. **validate** — admin elevation, ADK presence, ISO readable, output path writable
2. **prepare** — workspace cleanup, orphan-mount sweep
3. **copyiso** — mount source ISO → robocopy contents → dismount
4. **exportedition** — `DISM /Export-Image` to single-edition install.wim
5. **mountwim** — `DISM /Mount-Image /Index:1`
6. **updates** *(soft)* — `DISM /Add-Package` per slipstreamed MSU/CAB
7. **langpacks** *(soft)* — `DISM /Add-Package` per language CAB
8. **drivers** *(soft)* — `DISM /Add-Driver /Recurse` per folder + WinPE-critical subset into boot.wim
9. **wallpaper** *(soft)* — takeown/icacls + overwrite img0/img19/img20.jpg, 4K variants, and lock-screen img1*.jpg
10. **firstboot** *(soft)* — copy `GIBFirstBoot.exe` + installers + `apps.json` to `C:\GIB\` inside the WIM
11. **publicdesktop** *(soft)* — files to `Users\Public\Desktop\`
12. **deploymentscripts** *(soft)* — scripts to `Public\Documents` + Startup-folder trigger
13. **bloatware** *(soft)* — `DISM /Remove-ProvisionedAppxPackage` per checked package
14. **features** *(soft)* — `DISM /Enable-Feature` / `/Disable-Feature`
15. **registry** *(soft)* — offline-hive load, apply registry tweaks + Group Policies, hive unload with retry
16. **unattend** *(soft)* — generate `Autounattend.xml`, `Unattend.xml`, `SetupComplete.cmd`
17. **precommit_validate** *(critical)* — inspect the mounted WIM end-to-end, write a `ValidationReport_<timestamp>.{txt,html}` to the output folder, FAIL the build if anything is missing
18. **unmount** *(critical)* — `DISM /Unmount-Image /Commit`
19. **buildiso** *(critical)* — `oscdimg.exe` UEFI + BIOS bootable ISO + SHA-256 verify

Soft steps log a warning and continue; critical steps halt the build on failure.

---

## Pre-commit Validator (the safety net)

The validator runs **after every mutation** is applied but **before** `DISM /Unmount-Image /Commit`. At that point the WIM is still mounted, nothing is permanent, and any failure can be fixed without a 2-hour rebuild.

Sections checked, dynamically based on what was configured:

- `[SESSION]` — username, password (encoded), language, edition
- `[AUTOUNATTEND.XML]` — encoding, UILanguage match, product key, AutoLogon
- `[SETUPCOMPLETE.CMD]` — every feature block (BitLocker schtask, auto-logon registry, password-never-expires, computer rename, OneDrive uninstall, certificate import, scheduled tasks, ...)
- `[STAGED FILES]` — `GIBFirstBoot.exe`, `apps.json`, every installer
- `[DRIVERS]` — manual folders + auto-fetched packs produced the expected `oem*.inf` count in the mounted store
- `[LANGUAGE PACKS]` — every requested locale present in `/Get-Packages` output
- `[WALLPAPER]` — desktop + 4K variants + lock screen replaced (byte-exact size match against source)
- `[BITLOCKER]` — `Enable-BitLocker.ps1` cmdlet/protector/error handling/run-once marker/recovery-key logic
- `[DEPLOYMENT SCRIPTS]` — staged in `Public\Documents` + Startup trigger present
- `[BLOATWARE]` — every requested package is absent from the provisioned-Appx list
- `[FEATURES]` — every Enabled/Disabled feature shows the correct state in `/Get-Features`
- `[AUTO-FETCH ASSETS]` — Windows Update MSUs + auto-fetched driver packs present
- `[TRUSTED CERTIFICATES]` — cert files staged + `certutil` import block in SetupComplete.cmd
- `[CUSTOM FONTS]` — font files in `\Windows\Fonts\` + matching `<Display Name> (TrueType|OpenType)` registry values in the SOFTWARE hive
- `[REGISTRY]` — every custom registry entry and Group Policy queryable from the offline hive

The report is written to `<OutputPath>\ValidationReport_<timestamp>.txt` (plain) and `.html` (themed). Any FAIL halts the build before commit; WARNs are logged but allow the build to continue.

---

## Runtime requirements

The application must run as **Administrator** (DISM and offline-hive registry edits require it; the build engine validates this at step 1).

Required on the build machine:
- Windows 11 (any edition, minimum build 17763 for the WinRT toast APIs)
- **Windows ADK > Deployment Tools** (for `oscdimg.exe`) — Image Forge can fetch and install the ADK on demand from the in-app installer dialog
- `dism.exe` (present in System32 on every Windows 11 install)
- ~30 GB free on the workspace drive (the WIM mount + ISO staging needs space)
- Hyper-V optional, used only by the in-app "Test in VM" feature

The deployed laptop only needs to be Win11-capable hardware; everything is baked into the image.

---

## Installation

Grab the latest signed installer from `Installer/Output/`:

```
ALEImageForge-Setup-2.4.7.exe
```

The installer requires admin privileges, registers a Start-menu shortcut, and installs to `%ProgramFiles%\ALE Image Forge\`. The app's mutex (`Global\ALEImageForge_RunningInstance`) prevents installing over a running copy.

Settings are kept under `%LOCALAPPDATA%\GoldenISOBuilder\settings.json` and survive uninstall + reinstall.

---

## Building from source

```powershell
# 1. Build GoldenISOBuilder + GIBFirstBoot
cd GoldenISOBuilder
dotnet build -c Release -p:Platform=x64 --nologo

# 2. Publish single-file self-contained
dotnet publish -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true --nologo

# 3. Compile the Inno installer (requires Inno Setup 6 installed locally)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" Installer\ALEImageForge.iss
```

Building `GoldenISOBuilder` automatically triggers `dotnet publish` on `GIBFirstBoot` (via an MSBuild target) and copies the resulting exe into the publish folder. If `GIBFirstBoot.exe` is missing at runtime, the "Inject first-boot launcher" pipeline step throws `FileNotFoundException`.

---

## Architecture overview

```
ALE-ISO-Creator/
├── GoldenISOBuilder/              ← main WPF GUI (.NET 8 windows10.0.17763.0)
│   ├── MainWindow.xaml            ← shell: sidebar nav, breadcrumb, build history
│   ├── App.xaml.cs                ← startup, theme apply, splash on STA thread
│   ├── SplashWindow.xaml          ← gold-on-navy boot splash
│   ├── Models/
│   │   ├── BuildSession.cs        ← single source of truth, every page reads/writes this
│   │   ├── StagedApp / StagedFile
│   │   ├── CertificateEntry / FontEntry / DriverPackSelection
│   │   ├── RegistryEntry / GroupPolicyEntry / ScheduledTaskConfig
│   │   └── ...
│   ├── Views/
│   │   ├── Step1Page.xaml         ← Source & Output
│   │   ├── Step2Page.xaml         ← Assets (wallpaper, apps, drivers, updates, fonts)
│   │   ├── Step3Page.xaml         ← Customizations (bloatware, security, GP, certs)
│   │   ├── Step4Page.xaml         ← Admin Account
│   │   ├── StepRegistryPage.xaml  ← Registry tweaks
│   │   ├── StepAdvancedPage.xaml  ← Features, OEM, scheduled tasks
│   │   ├── Step5Page.xaml         ← Review + profile save/load
│   │   ├── Step6Page.xaml         ← Build progress (cinematic + pipeline views)
│   │   ├── TestVmPage.xaml        ← Boot the produced ISO in Hyper-V
│   │   ├── WelcomePage.xaml
│   │   └── SettingsPage.xaml
│   ├── Services/
│   │   ├── BuildEngine.cs         ← the 19-stage pipeline + pre-commit validator
│   │   ├── HyperVService.cs       ← Hyper-V WMI orchestration for Test-in-VM
│   │   ├── IsoAnalyzer.cs         ← reads boot.wim metadata to detect OS version
│   │   ├── AdkInstaller.cs        ← downloads + installs Windows ADK on demand
│   │   └── Catalog/
│   │       ├── MsCatalogWebService.cs
│   │       ├── ResumeableDownloader.cs        ← Range-header resume, SHA-256 streaming
│   │       ├── CatalogCacheManager.cs         ← LRU cache under %LOCALAPPDATA%\GoldenISOBuilder\Cache
│   │       ├── DellSoftPaqExtractor.cs        ← Dell /S /E= silent SFX
│   │       ├── HpSoftPaqExtractor.cs          ← HP -s -e -f"…" silent SFX
│   │       └── LenovoSoftPaqExtractor.cs      ← Lenovo /VERYSILENT /DIR= /EXTRACT=YES
│   ├── Resources/
│   │   ├── AppColors.xaml         ← dark theme palette
│   │   ├── AppColorsLight.xaml    ← light theme palette
│   │   ├── AppStyles.xaml         ← buttons, inputs, ComboBox, ScrollBar, ...
│   │   └── Icons.xaml             ← SVG-style Path geometries
│   └── Helpers/
│       ├── AppSettingsLoader.cs   ← read/write %LOCALAPPDATA%\GoldenISOBuilder\settings.json
│       └── ...
│
├── GIBFirstBoot/                  ← first-boot installer (.NET 8 WPF, self-contained)
│   ├── MainWindow.xaml(.cs)       ← reads apps.json, installs every staged app, retries on next login
│   ├── State.cs                   ← resume state under C:\GIB\state.json
│   └── ...
│
├── Installer/
│   ├── ALEImageForge.iss          ← Inno Setup script
│   └── Output/                    ← compiled installer (gitignored)
│
├── workspace/                     ← BuildEngine staging (gitignored)
├── ALE ISO Creator.sln
├── README.md                      ← this file
└── CLAUDE.md                      ← engineering notes / debugging philosophy
```

### Key design rules

- **`BuildSession.Current` is the single source of truth.** Every wizard page reads/writes it directly; there is no ViewModel layer. Each page calls `SaveToSession()` before navigating away.
- **All engine work is `async`/`await`.** The UI thread is never blocked. Heavy I/O (driver-extract walks, DISM enumerations) runs on the thread pool via `Task.Run`.
- **Passwords are always base64-encoded** through `ToPsEncodedCommand()` before going into `.cmd` files — never plain text on disk.
- **Registry writes use `reg.exe`**, not PowerShell, for offline-hive compatibility. `MapOfflineKey()` maps `HKLM\SOFTWARE\…` → `HKLM\OFFLINE_SW\…`, `HKLM\SYSTEM\…` → `HKLM\OFFLINE_SYS\…`, `HKCU\…` → `HKLM\OFFLINE_USR\…`.
- **`Step()` = fatal, `StepSoft()` = warn-and-continue.** Critical operations (mount, validate, unmount, oscdimg) use `Step()`; everything else uses `StepSoft()` so a single bloatware-removal hiccup doesn't lose a 90-minute build.
- **Pre-commit validator runs BEFORE commit.** If a `FAIL` triggers, the WIM is still mounted and the user can intervene.

---

## Logging & diagnostics

| File | Purpose |
|---|---|
| `<OutputPath>\build-<timestamp>.log` | Full BuildEngine log, every step + DISM stdout/stderr |
| `<OutputPath>\ValidationReport_<timestamp>.txt` / `.html` | Pre-commit validator report |
| `%LOCALAPPDATA%\GoldenISOBuilder\crash.log` | Unhandled exceptions caught by the global handler |
| `%LOCALAPPDATA%\GoldenISOBuilder\settings.json` | Theme, default paths, build defaults |
| `%LOCALAPPDATA%\GoldenISOBuilder\Cache\` | Downloaded MSUs and extracted driver packs (LRU-trimmed) |

---

## Settings file (`%LOCALAPPDATA%\GoldenISOBuilder\settings.json`)

| Key | Default | Purpose |
|---|---|---|
| `Theme` | `dark` | `dark` or `light` |
| `DefaultOutputPath` | (empty) | Pre-fills Step 1 |
| `DefaultWorkspacePath` | (empty) | Pre-fills Step 1 |
| `SoundOnComplete` | `true` | Plays a chime when the build finishes |
| `VerifyIsoAfterBuild` | `true` | Run SHA-256 on the output ISO |
| `CleanWorkspaceAfterBuild` | `true` | Wipe `workspace/` post-build |
| `WimCompression` | `max` | `max`, `fast`, or `none` for the install.wim export |
| `EnableAutoFetchFeatures` | `true` | Master toggle for the Windows Update + driver auto-fetch |

---

## Version history

| Version | Highlights |
|---|---|
| **2.4.7** | Light-theme cosmetic fixes; driver-extract UI freeze fixed (Task.Run on thread pool); cert dropdown no longer clips "TrustedPublisher (code-sign)"; registry Op column resized; power-plan tile cast bug fixed; validator extended to cover drivers, language packs, wallpaper, bloatware removal, features, font registry registration. |
| **2.4.6** | AutoLogon regression fixed (removed `ForceLegacySetupClientAsync` — the legacy `setup.exe` path was breaking Win11 OOBE AutoLogon; `diskpart clean` in windowsPE was already suppressing the upgrade dialog on its own). |
| **2.4.5** | Auto-fetch on by default; OS-version-aware Windows-Update filter (24H2 / 25H2 detection from WIM metadata); UI overlap fixes in Settings + Custom Fonts; light-theme accent brushes. |
| **2.4.2 – 2.4.4** | Phase 5b hotfix series — separate Wallpaper / Lock Screen pickers, Dell extraction, HP CMSL install path, Lenovo catalogue handling, VM forensics, validator coverage. |
| **2.4.1** | Phase 5b — auto-fetch driver packs from Dell / HP / Lenovo catalogues; Windows Update slipstream from Microsoft Update Catalog; WinPE-critical driver filter. |
| **2.4.0** | Phase 5 — pre-commit validator + HTML report. |
| **earlier** | Phases 1–4: wizard, build engine, GIBFirstBoot, BitLocker, certificates, fonts, Hyper-V test, themes. |

---

## Created by

**Pavan G S** — BTQ Infra, Alcatel-Lucent Enterprise.

Internal use only.
