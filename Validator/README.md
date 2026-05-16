# ALE Image Forge — Post-Deployment Validator

Validates that a laptop deployed from an ALE Image Forge ISO actually matches what the build profile asked for.

Run it on the **deployed laptop** after first login to confirm every step the build engine applied — drivers, apps, language packs, wallpaper, certificates, Group Policies, BitLocker, fonts, registry entries, and more — actually landed correctly.

---

## Files in this folder

| File | Purpose | Where to run |
|---|---|---|
| `Validate-GoldenImage-Auto.ps1` | The validator. Reads the `.gibprofile`, generates all checks dynamically, prints a colour-coded report and saves it to `C:\ALE-Validator\`. | Deployed laptop |
| `Validator-Kit-Builder.ps1` | GUI tool that bundles the validator + a chosen `.gibprofile` into a portable **Validation Kit** folder you can copy to a USB stick. | Build workstation |
| `Build-ValidatorEXE.ps1` | Compiles the Kit Builder GUI + embedded validator into a single double-click `ALEValidatorBuilder.exe`. | Build workstation |

---

## Quick start — which flow to use?

### Option A — Run the validator directly (simplest)

Copy `Validate-GoldenImage-Auto.ps1` and your `.gibprofile` to the same folder on the deployed laptop, then:

```powershell
# Open PowerShell as Administrator on the deployed laptop
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Validate-GoldenImage-Auto.ps1 -ProfilePath .\MyBuild.gibprofile
```

The report is printed to the console (colour-coded) and saved to `C:\ALE-Validator\ValidationReport_<timestamp>.txt`.

---

### Option B — Generate a Validation Kit (recommended for repeated deployments)

The Kit Builder bundles the validator + a specific profile into one portable folder you can drop on any USB stick and use on any laptop built from that profile.

**On the build workstation:**

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Validator-Kit-Builder.ps1
```

1. Browse to your `.gibprofile`
2. Choose an output folder (default: Desktop)
3. Click **Generate Kit**

The kit folder contains:
```
ValidationKit-<profile>-<timestamp>/
  Validate-GoldenImage-Auto.ps1   (the validator)
  MyBuild.gibprofile               (your profile, copied)
  Run-Validator.bat                (double-click this on the laptop)
  README.txt                       (quick-start for the technician)
```

**On the deployed laptop:**

1. Copy the kit folder from USB / network share
2. Double-click `Run-Validator.bat` — it auto-elevates and runs the validator

---

### Option C — Compile to a single EXE (best for distribution)

If you want a single double-click file that contains both the Kit Builder GUI and the validator with no external dependencies:

**Requirements:** Windows PowerShell 5.1, internet access on first run (to install `ps2exe` from PSGallery).

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Build-ValidatorEXE.ps1
```

Output: `ALEValidatorBuilder.exe` in the same folder — double-click to launch the Kit Builder GUI. The validator is embedded inside the EXE so no extra files are needed.

---

## What the validator checks

All checks are **dynamic** — if a feature was not enabled in the profile, no check is generated for it (no false FAILs from unused features).

| Section | What is verified |
|---|---|
| **IDENTITY** | Hostname matches the profile template (`{PREFIX}{SERIAL}`, `{PREFIX}{LAST6_SERIAL}`, `{PREFIX}{LAST6_MAC}`, `{PREFIX}{ASSETTAG}`); admin account enabled; PasswordNeverExpires; AutoLogon registry |
| **SYSTEM SETTINGS** | Time zone; Active power plan (Balanced / High performance / Ultimate Performance) |
| **BLOATWARE REMOVAL** | Every requested AppX package is absent from provisioned + installed lists |
| **OPTIONAL FEATURES** | Every enabled feature is `Enabled`; every disabled feature is `Disabled` / `DisabledWithPayloadRemoved` |
| **SECURITY** | BitLocker protection status + recovery key file; Defender service responsive; Telemetry policy |
| **UI / SYSTEM DEFAULTS** | Dark mode registry; Show file extensions; Wallpaper files present |
| **WINDOWS 11 UX BASELINE** | Copilot, Recall, Widgets, Teams Chat icon, Consumer Features — each checked against its policy registry value |
| **ONEDRIVE** | No OneDrive.exe process; OEM staging copies deleted; no Run key |
| **TRUSTED CERTIFICATES** | Each cert verified by thumbprint in the correct store (Root / CA / TrustedPublisher) |
| **CUSTOM FONTS** | Font file present in `C:\Windows\Fonts\`; registry value registered under Fonts key |
| **GROUP POLICIES** | Every Machine and User policy entry present and set to the correct value |
| **LANGUAGE PACKS** | Each locale code detected in installed packages via DISM |
| **STAGED APPS** | Every first-boot app detectable via registry uninstall key, Program Files folder, service, or process |
| **DEPLOYMENT SCRIPTS** | Scripts present at destination; EveryLogin trigger in Startup folder or Run key |
| **WINDOWS UPDATES** | Each slipstreamed KB present via Get-HotFix, Get-WindowsPackage, or DISM packages |
| **DRIVERS** | OEM driver packs injected (matched by vendor in driver store); host hardware vendor check |

---

## Report output

Results are colour-coded in the console:

| Colour | Meaning |
|---|---|
| Green — `PASS` | Check confirmed |
| Yellow — `WARN` | Soft issue — something could not be fully verified but the build is likely fine (e.g. language feature pack not in `/Get-Intl` but present in package store) |
| Red — `FAIL` | Definitive failure — the expected configuration is missing or wrong |
| Grey — `INFO` | Informational, no action needed |

A text report is always saved to:
```
C:\ALE-Validator\ValidationReport_<yyyyMMdd_HHmmss>.txt
```

Exit codes: `0` = no FAILs, `1` = at least one FAIL.

---

## Parameters

```
Validate-GoldenImage-Auto.ps1
  -ProfilePath <path>    Path to the .gibprofile JSON file.
                         If omitted, auto-selects the most-recently-modified
                         .gibprofile in the same folder as the script.

  -ReportFolder <path>   Where to save the text report.
                         Default: C:\ALE-Validator
```

---

## Requirements

**On the deployed laptop (running the validator):**
- Windows 11
- PowerShell 5.1 (built-in)
- Run as **Administrator** — DISM, Get-WindowsDriver, Get-AppxPackage -AllUsers, and BitLocker queries require elevation
- The `.gibprofile` from the build that produced this ISO

**On the build workstation (Kit Builder / EXE compiler):**
- Windows 11, Windows PowerShell 5.1
- The `Build-ValidatorEXE.ps1` compiler needs internet access on first run to install `ps2exe` from PSGallery
- No admin required to build the kit or compile the EXE

---

## Created by

**Pavan G S** — BTQ Infra, Alcatel-Lucent Enterprise.

Part of [ALE Image Forge](../README.md) — enterprise Windows 11 image builder.
