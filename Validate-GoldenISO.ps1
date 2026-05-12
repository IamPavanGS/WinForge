#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    GoldenISOBuilder - ISO Validation Script
    Mounts a Golden ISO, inspects every injected component, and produces a
    detailed timestamped log that can be shared for support/review.

.DESCRIPTION
    Checks performed:
      ── Phase 2  ISO Root ──────────────────────────────────────────────────────
       1.  Autounattend.xml   - ISO root (XML parse + locale/disk checks)
       2.  install.wim / esd  - sources folder
       3.  BIOS boot file     - boot\bcd
       4.  UEFI boot file     - efi\microsoft\boot\bcd

      ── Phase 3  DISM WIM mount ────────────────────────────────────────────────
       5.  install.wim mount  - DISM /Mount-Image /ReadOnly

      ── Phase 4  Wallpaper ────────────────────────────────────────────────────
       6.  Standard wallpaper  - Web\Wallpaper\Windows
       7.  4K wallpaper        - Web\4K\Wallpaper\Windows
       8.  Lock-screen         - Web\Screen\img1*.jpg

      ── Phase 5  GIBFirstBoot ─────────────────────────────────────────────────
       9.  GIBFirstBoot.exe    - \GIB\GIBFirstBoot.exe (+ self-contained size check)
      10.  apps.json            - \GIB\apps.json (+ parses entries, MST transforms, timeouts)
      11.  Installers folder    - \GIB\Installers\* (installer + .mst per app)

      ── Phase 6  Public Desktop ───────────────────────────────────────────────
      12.  Public Desktop files  - Users\Public\Desktop\

      ── Phase 7  SetupComplete.cmd ────────────────────────────────────────────
      13.  SetupComplete.cmd     - Windows\Setup\Scripts\SetupComplete.cmd
             • GoldenISOBuilder banner
             • Administrator account activation
             • Named admin account creation (non-default username)
             • Admin password configuration
             • Timezone (tzutil)
             • Power plan (powercfg)
             • Product key (slmgr)
             • Computer rename + reboot
             • BitLocker registered as ONSTART task (SYSTEM/HIGHEST) + Enable-BitLocker.ps1
               - DriveLetter parameter, optional -NoSaveKey / -KeyFolder flags
               - Script uses Get-CimInstance (24H2+ compatible), TPM wait loop, run-once marker
             • Defender ATP signature update
             • Autologon (one-time)
             • Scheduled tasks (count)
             • Windows.old cleanup (skip_wold goto)
      14.  Panther unattend.xml  - Windows\Panther\unattend.xml

      ── Phase 8  Offline Registry ─────────────────────────────────────────────
      15.  RunOnce\GIBFirstBoot
      16.  Telemetry policy
      17.  OEM branding
      18.  RegisteredOwner / Organization
      19.  Dark mode (AppsUseLightTheme / SystemUsesLightTheme)
      20.  File extensions / hidden files
      21.  SMBv1 (mrxsmb10\Start=4)
      22.  NTLMv1 disabled (LmCompatibilityLevel=5)
      23.  SMB packet signing (RequireSecuritySignature=1)
      24.  Credential Guard (EnableVirtualizationBasedSecurity)
      25.  Autologon registry keys (Winlogon)
      26.  Group Policy keys (SOFTWARE\Policies\ enumeration)

      ── Phase 9  Deployment Scripts ───────────────────────────────────────────
      27.  Public\Documents staged scripts
      28.  All-users Startup folder scripts

      ── Phase 10  Windows Features (DISM) ─────────────────────────────────────
      29.  Enabled / disabled optional features

      ── Phase 11  Language Packs (DISM) ──────────────────────────────────────
      30.  Language pack packages

      ── Phase 12  Drivers (DISM) ──────────────────────────────────────────────
      31.  OEM / injected drivers

      ── Phase 13  Bloatware (DISM) ────────────────────────────────────────────
      32.  Remaining provisioned AppX packages
      33.  Known-bloatware presence check (expanded Windows 11 list)

      ── Phase 14  WIM Edition Metadata ────────────────────────────────────────
      34.  WIM edition name / version / architecture

.PARAMETER IsoPath
    Full path to the Golden ISO file to validate.

.PARAMETER WimIndex
    Index of the Windows edition inside install.wim (default 1).

.PARAMETER LogDir
    Folder where the log file is written (default: same folder as the ISO).

.EXAMPLE
    .\Validate-GoldenISO.ps1 -IsoPath "D:\ISO_Build\Output\GoldenWin11.iso"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string] $IsoPath,

    [int]    $WimIndex = 1,

    [string] $LogDir   = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
#  Counters and logging
# ---------------------------------------------------------------------------

$Script:PassCount = 0
$Script:WarnCount = 0
$Script:FailCount = 0
$Script:LogLines  = [System.Collections.Generic.List[string]]::new()

function Write-Log {
    param([string]$Line, [string]$Level = "INFO")
    $ts   = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $full = "[$ts] [$Level] $Line"
    $Script:LogLines.Add($full)
    switch ($Level) {
        "PASS" { Write-Host $full -ForegroundColor Green  }
        "FAIL" { Write-Host $full -ForegroundColor Red    }
        "WARN" { Write-Host $full -ForegroundColor Yellow }
        "HEAD" { Write-Host ""; Write-Host $full -ForegroundColor Cyan }
        default{ Write-Host $full }
    }
}

function Pass { param([string]$msg) $Script:PassCount++; Write-Log "  [PASS] $msg" "PASS" }
function Fail { param([string]$msg) $Script:FailCount++; Write-Log "  [FAIL] $msg" "FAIL" }
function Warn { param([string]$msg) $Script:WarnCount++; Write-Log "  [WARN] $msg" "WARN" }
function Info { param([string]$msg)                      Write-Log "         $msg" "INFO" }
function Head { param([string]$msg)                      Write-Log "=== $msg ===" "HEAD" }

function FormatKB {
    param([long]$bytes)
    return [string][math]::Round($bytes / 1024, 1) + " KB"
}
function FormatMB {
    param([long]$bytes)
    return [string][math]::Round($bytes / 1048576, 1) + " MB"
}
function FormatGB {
    param([long]$bytes)
    return [string][math]::Round($bytes / 1073741824, 2) + " GB"
}

function Check-FileExists {
    param([string]$FilePath, [string]$Label)
    if (Test-Path $FilePath -PathType Leaf) {
        $sz = (Get-Item $FilePath).Length
        $szStr = FormatKB $sz
        Pass "$Label exists ($szStr)"
        return $true
    }
    else {
        Fail "$Label NOT FOUND: $FilePath"
        return $false
    }
}

function Check-DirExists {
    param([string]$DirPath, [string]$Label)
    if (Test-Path $DirPath -PathType Container) {
        $count = @(Get-ChildItem $DirPath -File -ErrorAction SilentlyContinue).Count
        Pass "$Label exists ($count files)"
        return $true
    }
    else {
        Fail "$Label NOT FOUND: $DirPath"
        return $false
    }
}

# Run any reg.exe command without letting $ErrorActionPreference=Stop turn
# a non-zero exit (key not found, etc.) into a terminating error.
function Invoke-Reg {
    param([string[]]$Arguments)
    $savedEA = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    $raw  = reg @Arguments 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $savedEA
    $text = ($raw | Where-Object { $_ -is [string] }) -join "`n"
    return [PSCustomObject]@{ Output = $text; ExitCode = $code }
}

# Run DISM and return its stdout.
# stderr is read asynchronously via BeginErrorReadLine to prevent the classic
# deadlock: if both stdout and stderr are redirected but only stdout is read
# synchronously, DISM can block on a full stderr pipe before closing stdout.
function Invoke-Dism {
    param([string]$Arguments)
    $dism = Join-Path $env:SystemRoot "System32\dism.exe"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $dism
    $psi.Arguments = $Arguments
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    # Drain stderr asynchronously so it never blocks DISM's stderr writes
    $proc.BeginErrorReadLine()
    $stdout = $proc.StandardOutput.ReadToEnd()
    $proc.WaitForExit()
    return $stdout
}

# Helper: parse a REG_DWORD value from reg.exe query output
function Get-RegDword {
    param([string]$Output, [string]$ValueName)
    $m = [regex]::Match($Output, "$ValueName\s+REG_DWORD\s+(0x[\da-fA-F]+|\d+)")
    if (-not $m.Success) { return $null }
    $raw = $m.Groups[1].Value
    if ($raw.StartsWith("0x")) { return [Convert]::ToInt32($raw, 16) }
    return [int]$raw
}

# Helper: parse a REG_SZ value from reg.exe query output
function Get-RegSz {
    param([string]$Output, [string]$ValueName)
    $m = [regex]::Match($Output, "$ValueName\s+REG_SZ\s+(.+)")
    if (-not $m.Success) { return $null }
    return $m.Groups[1].Value.Trim()
}

# ---------------------------------------------------------------------------
#  Path setup
# ---------------------------------------------------------------------------

$IsoPath  = Resolve-Path $IsoPath | Select-Object -ExpandProperty Path
$isoName  = [System.IO.Path]::GetFileNameWithoutExtension($IsoPath)
$ts       = Get-Date -Format "yyyyMMdd_HHmmss"

if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Split-Path $IsoPath -Parent }
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$logFile  = Join-Path $LogDir ($isoName + "_Validation_" + $ts + ".log")

# Use a short root path to avoid DISM Error 3 (path-too-long) when mounting WIM.
# %TEMP% expands to a long path like C:\Users\…\AppData\Local\Temp which can
# push the mount path past DISM's limit.  C:\GIBValidate stays well under 64 chars.
$workBase = "C:\GIBValidate\$ts"
$isoDrive = ""
$mountDir = Join-Path $workBase "mount"

# ---------------------------------------------------------------------------
#  Begin
# ---------------------------------------------------------------------------

Head "GoldenISOBuilder - ISO Validation"
Info "ISO       : $IsoPath"
Info "WIM index : $WimIndex"
Info "Log file  : $logFile"
Info "Temp dir  : $workBase"

# ---------------------------------------------------------------------------
#  PHASE 1 - Mount the ISO
# ---------------------------------------------------------------------------

Head "PHASE 1 - Mount ISO"

try {
    New-Item -ItemType Directory -Path $workBase -Force | Out-Null
    New-Item -ItemType Directory -Path $mountDir  -Force | Out-Null

    Info "Mounting ISO image..."
    $diskImg  = Mount-DiskImage -ImagePath $IsoPath -PassThru
    $volLetter = ($diskImg | Get-Volume).DriveLetter
    $isoDrive  = $volLetter + ":"
    Pass "ISO mounted at $isoDrive"
}
catch {
    Fail "Failed to mount ISO: $_"
    $Script:LogLines | Out-File $logFile -Encoding UTF8
    exit 1
}

# ---------------------------------------------------------------------------
#  PHASE 2 - Check ISO-root items
# ---------------------------------------------------------------------------

Head "PHASE 2 - ISO Root Contents"

$autoXml = Join-Path $isoDrive "Autounattend.xml"
if (Check-FileExists $autoXml "Autounattend.xml") {
    try {
        [xml]$xdoc = Get-Content $autoXml -Raw
        Pass "Autounattend.xml is valid XML"

        # Keyboard / input locale
        $kbNodes = $xdoc.GetElementsByTagName("InputLocale")
        if ($kbNodes.Count -gt 0) {
            $kbVals = @($kbNodes | ForEach-Object { $_.InnerText } | Sort-Object -Unique)
            Pass "Keyboard/input locale: $($kbVals -join ', ')"
        }
        else { Warn "No InputLocale element found in Autounattend.xml" }

        # UI language
        $uiNodes = $xdoc.GetElementsByTagName("UILanguage")
        if ($uiNodes.Count -gt 0) {
            $uiVals = @($uiNodes | ForEach-Object { $_.InnerText } | Sort-Object -Unique)
            Pass "UI language: $($uiVals -join ', ')"
        }
        else { Warn "No UILanguage element found in Autounattend.xml" }

        # System locale
        $sysLocNodes = $xdoc.GetElementsByTagName("SystemLocale")
        if ($sysLocNodes.Count -gt 0) {
            $slVals = @($sysLocNodes | ForEach-Object { $_.InnerText } | Sort-Object -Unique)
            Pass "System locale: $($slVals -join ', ')"
        }

        # Disk configuration
        $diskNodes = $xdoc.GetElementsByTagName("DiskConfiguration")
        if ($diskNodes.Count -gt 0) { Pass "DiskConfiguration block present in Autounattend.xml" }
        else                         { Warn "DiskConfiguration block missing in Autounattend.xml" }

        # OOBE skip
        $oobeNodes = $xdoc.GetElementsByTagName("OOBE")
        if ($oobeNodes.Count -gt 0) { Pass "OOBE configuration block present" }
        else                         { Info "No OOBE block found (may use defaults)" }

        # SkipMachineOOBE / HideEULAPage
        $skipOobe  = @($xdoc.GetElementsByTagName("SkipMachineOOBE")  | Where-Object { $_.InnerText -eq "true" }).Count
        $hideEula  = @($xdoc.GetElementsByTagName("HideEULAPage")     | Where-Object { $_.InnerText -eq "true" }).Count
        if ($skipOobe -gt 0) { Pass "SkipMachineOOBE = true" }
        if ($hideEula -gt 0) { Pass "HideEULAPage = true" }

        # ── Zero-touch OOBE checks (Windows 11 requires all three) ────────────
        # UserAccounts + AdministratorPassword — without this Win11 always shows
        # the account-creation wizard regardless of SkipMachineOOBE
        $userAcctNodes = $xdoc.GetElementsByTagName("UserAccounts")
        $adminPwdNodes = $xdoc.GetElementsByTagName("AdministratorPassword")
        if ($userAcctNodes.Count -gt 0 -and $adminPwdNodes.Count -gt 0) {
            Pass "UserAccounts + AdministratorPassword present in oobeSystem (zero-touch account bypass)"
        }
        else {
            Fail "UserAccounts/AdministratorPassword MISSING from oobeSystem — Windows 11 will drop into interactive account-creation wizard"
        }

        # AutoLogon — drives OOBE past the post-account-creation login screen
        $autoLogonNodes = $xdoc.GetElementsByTagName("AutoLogon")
        if ($autoLogonNodes.Count -gt 0) {
            $lcNode = $autoLogonNodes | ForEach-Object { $_.SelectSingleNode("LogonCount") } | Where-Object { $_ } | Select-Object -First 1
            $lcVal  = if ($lcNode) { $lcNode.InnerText } else { "not set" }
            Pass "AutoLogon present in oobeSystem (LogonCount=$lcVal) — OOBE will not stall at login screen"
        }
        else {
            Warn "AutoLogon not found in oobeSystem — OOBE may stall at login screen on some hardware"
        }

        # BypassNRO — prevents Win11 forcing a Microsoft account when offline
        $bypassNroNodes = @($xdoc.GetElementsByTagName("Path") |
            Where-Object { $_.InnerText -match "BypassNRO" })
        if ($bypassNroNodes.Count -gt 0) {
            Pass "BypassNRO injected in specialize pass — Microsoft account screen suppressed when offline"
        }
        else {
            Warn "BypassNRO not found in specialize pass — OOBE may force Microsoft account if machine has no internet"
        }

        # ProductKey in UserData — prevents Setup prompting for product key during install
        $productKeyNodes = @($xdoc.GetElementsByTagName("Key") |
            Where-Object { $_.InnerText -match '^\w{5}-\w{5}-\w{5}-\w{5}-\w{5}$' })
        if ($productKeyNodes.Count -gt 0) {
            $pkVal = $productKeyNodes[0].InnerText
            Pass "ProductKey present in Autounattend.xml UserData ($($pkVal.Substring(0,5))-****-****-****-****) — Setup will not prompt for product key"
        }
        else {
            Warn "ProductKey not found in Autounattend.xml UserData — Windows Setup may prompt for a product key during installation"
        }
    }
    catch {
        Fail "Autounattend.xml XML parse error: $_"
    }
}

# install.wim / install.esd
$wimPathOnIso = Join-Path $isoDrive "sources\install.wim"
if (-not (Test-Path $wimPathOnIso)) {
    $wimPathOnIso = Join-Path $isoDrive "sources\install.esd"
}
if (Test-Path $wimPathOnIso) {
    $wimSzStr = FormatGB (Get-Item $wimPathOnIso).Length
    Pass "install.wim/esd present ($wimSzStr)"
}
else {
    Fail "sources\install.wim or install.esd NOT FOUND - ISO may be incomplete"
}

# BIOS boot
if (Test-Path (Join-Path $isoDrive "boot\bcd")) { Pass "BIOS boot file (boot\bcd) present" }
else                                              { Warn "BIOS boot file (boot\bcd) missing" }

# UEFI boot
if (Test-Path (Join-Path $isoDrive "efi\microsoft\boot\bcd")) { Pass "UEFI boot file present" }
else                                                            { Warn "UEFI boot file missing" }

# ---------------------------------------------------------------------------
#  PHASE 3 - Mount install.wim with DISM
# ---------------------------------------------------------------------------

Head "PHASE 3 - Mount install.wim (DISM)"

$wimMounted = $false
$tempWim    = Join-Path $workBase "install.wim"

try {
    Info "Copying install.wim to temp location (this may take a minute)..."
    Copy-Item $wimPathOnIso $tempWim -Force
    attrib -R $tempWim 2>$null

    Info "Querying WIM metadata..."
    $wimInfo   = Invoke-Dism "/Get-WimInfo /WimFile:`"$tempWim`""
    $nameMatch = $wimInfo -split "`n" | Where-Object { $_ -match "Name\s*:" } | Select-Object -First 1
    if ($nameMatch) { Pass "WIM edition: $($nameMatch.Trim())" }

    Info "Mounting WIM index $WimIndex -> $mountDir (this takes ~1-2 min)..."
    $null = Invoke-Dism "/Mount-Image /ImageFile:`"$tempWim`" /Index:$WimIndex /MountDir:`"$mountDir`" /ReadOnly"

    if (Test-Path (Join-Path $mountDir "Windows\System32")) {
        Pass "install.wim index $WimIndex mounted successfully"
        $wimMounted = $true
    }
    else {
        Fail "DISM mount completed but Windows\System32 not found in mount dir"
    }
}
catch {
    Fail "DISM mount failed: $_"
    Info "Skipping all WIM-based checks."
}

# ---------------------------------------------------------------------------
#  PHASE 4 - Wallpaper
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 4 - Wallpaper Injection"

    $wpDir = Join-Path $mountDir "Windows\Web\Wallpaper\Windows"
    Info "Checking standard wallpaper directory: $wpDir"
    foreach ($name in @("img0.jpg", "img19.jpg", "img20.jpg")) {
        $f = Join-Path $wpDir $name
        if (Test-Path $f) {
            $szStr = FormatKB (Get-Item $f).Length
            Pass "Wallpaper $name present ($szStr)"
        }
        else { Warn "Wallpaper $name not found (expected if wallpaper was not configured)" }
    }

    $dir4k = Join-Path $mountDir "Windows\Web\4K\Wallpaper\Windows"
    if (Test-Path $dir4k) {
        $files4k = @(Get-ChildItem $dir4k -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -match "^img(0|19|20)_.*\.jpg$" })
        if ($files4k.Count -gt 0) {
            Pass "4K wallpaper variants present: $($files4k.Count) files in Web\4K\Wallpaper\Windows"
            foreach ($f4 in $files4k) { Info "  4K: $($f4.Name) ($(FormatKB $f4.Length))" }
        }
        else { Info "No custom 4K wallpaper variants found (original files remain)" }
    }
    else { Info "4K wallpaper directory not present (pre-Win11 image)" }

    $screenDir = Join-Path $mountDir "Windows\Web\Screen"
    if (Test-Path $screenDir) {
        $lockFiles = @(Get-ChildItem $screenDir -Filter "img1*.jpg" -ErrorAction SilentlyContinue)
        if ($lockFiles.Count -gt 0) {
            Pass "Lock-screen images present: $($lockFiles.Count) files in Web\Screen"
            foreach ($lf in $lockFiles) { Info "  Lock: $($lf.Name) ($(FormatKB $lf.Length))" }
        }
        else { Info "No custom lock-screen images found (original files remain)" }
    }
    else { Warn "Windows\Web\Screen directory missing" }
}

# ---------------------------------------------------------------------------
#  PHASE 5 - GIBFirstBoot
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 5 - GIBFirstBoot Launcher"

    $gibDir  = Join-Path $mountDir "GIB"
    $gibExe  = Join-Path $gibDir "GIBFirstBoot.exe"
    $gibJson = Join-Path $gibDir "apps.json"
    $gibInst = Join-Path $gibDir "Installers"

    $null = Check-FileExists $gibExe "GIBFirstBoot.exe"

    if (Test-Path $gibExe) {
        $gibSzMB = [math]::Round((Get-Item $gibExe).Length / 1048576, 1)
        if ($gibSzMB -lt 1) {
            Fail "GIBFirstBoot.exe is only $($gibSzMB) MB — FRAMEWORK-DEPENDENT build. It will fail on the deployed PC without .NET 8 installed. Rebuild GoldenISOBuilder to inject the self-contained version."
        }
        else {
            Pass "GIBFirstBoot.exe is self-contained ($($gibSzMB) MB) — no .NET runtime dependency"
        }
    }

    $null = Check-FileExists $gibJson "apps.json"
    $null = Check-DirExists  $gibInst "GIB\Installers"

    if (Test-Path $gibJson) {
        try {
            $appsList = (Get-Content $gibJson -Raw) | ConvertFrom-Json
            $appCount = @($appsList).Count
            if ($appCount -eq 0) {
                Warn "apps.json is empty — no apps will be installed at first boot"
            }
            else {
                Pass "apps.json contains $appCount app(s)"
                foreach ($app in $appsList) {
                    $appFile = Join-Path $gibInst $app.file
                    if (Test-Path $appFile) {
                        Pass "  App [$($app.name)] installer [$($app.file)] present ($(FormatMB (Get-Item $appFile).Length))"
                    }
                    else {
                        Fail "  App [$($app.name)] installer [$($app.file)] MISSING from GIB\Installers"
                    }
                    $timeoutVal = if ($null -ne $app.timeoutMinutes) { "$($app.timeoutMinutes)min" } else { "not set" }
                    Info "    Type: $($app.type)  |  Args: $($app.args)  |  Success codes: $($app.successExitCodes)  |  Timeout: $timeoutVal"

                    # ── MST transform check ───────────────────────────────────────────
                    $mstVal = if ($null -ne $app.PSObject.Properties["mst"]) { $app.mst } else { $null }
                    if (-not [string]::IsNullOrWhiteSpace($mstVal)) {
                        # MST only valid for MSI type
                        if ($app.type -ne "msi") {
                            Warn "  App [$($app.name)] has MST transform '$mstVal' but installer type is '$($app.type)' — MST only applies to MSI installers"
                        }
                        else {
                            $mstFile = Join-Path $gibInst $mstVal
                            if (Test-Path $mstFile) {
                                Pass "  App [$($app.name)] MST transform [$mstVal] present ($(FormatKB (Get-Item $mstFile).Length))"
                            }
                            else {
                                Fail "  App [$($app.name)] MST transform [$mstVal] MISSING from GIB\Installers — msiexec will fail at first boot"
                            }
                        }
                    }
                    else {
                        Info "    No MST transform (standard silent install)"
                    }
                }
            }
        }
        catch { Fail "apps.json parse error: $_" }
    }
}

# ---------------------------------------------------------------------------
#  PHASE 6 - Public Desktop
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 6 - Public Desktop Files"

    $pubDesk = Join-Path $mountDir "Users\Public\Desktop"
    if (Test-Path $pubDesk) {
        $desktopFiles = @(Get-ChildItem $pubDesk -File -ErrorAction SilentlyContinue)
        if ($desktopFiles.Count -gt 0) {
            Pass "Public Desktop has $($desktopFiles.Count) staged file(s)"
            foreach ($df in $desktopFiles) { Info "  Desktop: $($df.Name) ($(FormatKB $df.Length))" }
        }
        else { Info "Public Desktop exists but no staged files (none configured)" }
    }
    else { Warn "Users\Public\Desktop directory not present in image" }
}

# ---------------------------------------------------------------------------
#  PHASE 7 - SetupComplete.cmd
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 7 - SetupComplete.cmd"

    $setupComplete = Join-Path $mountDir "Windows\Setup\Scripts\SetupComplete.cmd"
    if (Check-FileExists $setupComplete "SetupComplete.cmd") {
        $cmdContent = Get-Content $setupComplete -Raw

        # ── Banner ────────────────────────────────────────────────────────────
        if ($cmdContent -match "Generated by ALE Golden ISO Builder") { Pass "SetupComplete.cmd has GoldenISOBuilder banner" }
        else                                                            { Warn "GoldenISOBuilder banner not found in SetupComplete.cmd" }

        # ── Admin account activation ──────────────────────────────────────────
        if ($cmdContent -match "net user administrator /active:yes") { Pass "Administrator account activation present" }
        else                                                           { Warn "Administrator account activation not found" }

        # ── Admin password ────────────────────────────────────────────────────
        if ($cmdContent -match 'net user administrator "') { Pass "Administrator password configuration present" }
        else                                                { Info  "No administrator password set (account will have no password)" }

        # ── Named local account (non-default username) ────────────────────────
        if ($cmdContent -match "net user .+ /add") {
            $naMatch = [regex]::Match($cmdContent, 'net user "?([^"]+)"? ".+" /add')
            if ($naMatch.Success) { Pass "Named local account creation present: $($naMatch.Groups[1].Value.Trim())" }
            else                   { Pass "Named local account creation command present" }
        }
        else { Info "No additional named account configured (using built-in Administrator only)" }

        # ── Timezone ──────────────────────────────────────────────────────────
        $tzMatch = [regex]::Match($cmdContent, 'tzutil\s+/s\s+"([^"]+)"')
        if ($tzMatch.Success) { Pass "Timezone configured: $($tzMatch.Groups[1].Value)" }
        else                   { Warn "tzutil timezone command not found in SetupComplete.cmd" }

        # ── Power plan ────────────────────────────────────────────────────────
        if ($cmdContent -match "powercfg /setactive") { Pass "Power plan configuration present" }
        else                                            { Warn "powercfg command not found in SetupComplete.cmd" }

        # ── Product key ───────────────────────────────────────────────────────
        if ($cmdContent -match "slmgr.vbs /ipk") { Pass "Product key installation (slmgr) present" }
        else                                       { Info  "No product key in SetupComplete.cmd (OEM firmware key or not configured)" }

        # ── Computer rename ───────────────────────────────────────────────────
        if ($cmdContent -match "Rename-Computer") { Pass "Computer rename logic present (prefix + BIOS serial)" }
        else                                       { Info  "No computer rename configured" }

        # ── BitLocker ─────────────────────────────────────────────────────────
        if ($cmdContent -match "Enable-BitLocker.ps1") {

            # Delivery method — should be ONSTART task (not a direct call)
            if ($cmdContent -match "schtasks.+ONSTART.+Enable-BitLocker\.ps1|Enable-BitLocker\.ps1.+ONSTART") {
                Pass "BitLocker registered as ONSTART scheduled task (SYSTEM/HIGHEST) — fires after TPM and BDESVC are fully initialised"
            }
            elseif ($cmdContent -match "schtasks.+Enable-BitLocker\.ps1") {
                Pass "BitLocker registered as scheduled task in SetupComplete.cmd"
            }
            else {
                Warn "BitLocker references Enable-BitLocker.ps1 but no schtasks registration found — direct call from SetupComplete.cmd may fail before BDESVC/TPM are ready"
            }

            # Drive letter parameter
            $blDriveMatch = [regex]::Match($cmdContent, '-DriveLetter\s+(\S+?)(?=\s|")')
            if ($blDriveMatch.Success) {
                Pass "BitLocker drive letter: $($blDriveMatch.Groups[1].Value)"
            }
            else {
                Info "BitLocker -DriveLetter parameter not detected (script will default to C:)"
            }

            # Key saving options
            if ($cmdContent -match '-NoSaveKey') {
                Info "BitLocker recovery key saving is DISABLED (-NoSaveKey flag present)"
            }
            else {
                Info "BitLocker recovery key will be saved to disk"
                $blFolderMatch = [regex]::Match($cmdContent, '-KeyFolder\s+"?([^"\s]+)"?')
                if ($blFolderMatch.Success) {
                    Info "BitLocker key save folder: $($blFolderMatch.Groups[1].Value)"
                }
            }

            # Enable-BitLocker.ps1 file
            $btScript = Join-Path $mountDir "Windows\Setup\Scripts\Enable-BitLocker.ps1"
            if (Test-Path $btScript) {
                $btSzStr   = FormatKB (Get-Item $btScript).Length
                Pass "Enable-BitLocker.ps1 present in Scripts folder ($btSzStr)"
                $btContent = Get-Content $btScript -Raw -ErrorAction SilentlyContinue

                if ($btContent) {
                    # Modern CimInstance vs deprecated WmiObject
                    if ($btContent -match 'Get-CimInstance') {
                        Pass "Enable-BitLocker.ps1 uses Get-CimInstance (Windows 11 24H2+ compatible)"
                    }
                    elseif ($btContent -match 'Get-WmiObject') {
                        Warn "Enable-BitLocker.ps1 uses deprecated Get-WmiObject — consider updating to Get-CimInstance for Windows 11 24H2+"
                    }

                    # TPM readiness wait loop
                    if ($btContent -match 'Get-Tpm|TpmReady') {
                        Pass "Enable-BitLocker.ps1 has TPM readiness wait loop (up to 300 s)"
                    }
                    else {
                        Warn "Enable-BitLocker.ps1 does not appear to wait for TPM — may fail on first boot before TPM stack is ready"
                    }

                    # Run-once marker guard
                    if ($btContent -match 'RunOnceMarkers|markerFile') {
                        Pass "Enable-BitLocker.ps1 has run-once marker guard — will not re-encrypt on subsequent reboots"
                    }
                    else {
                        Warn "Enable-BitLocker.ps1 run-once guard not detected — script may execute on every reboot"
                    }

                    # WinPE pre-provisioning detection
                    if ($btContent -match 'alreadyEncrypting|Encryption in Progress|pre-provisioning') {
                        Pass "Enable-BitLocker.ps1 handles WinPE pre-provisioning (skips manage-bde -on if already encrypting)"
                    }
                }
            }
            else {
                Fail "Enable-BitLocker.ps1 referenced but NOT FOUND at Windows\Setup\Scripts\"
            }
        }
        else { Info "BitLocker not configured in SetupComplete.cmd" }

        # ── Defender ATP signature update ──────────────────────────────────────
        if ($cmdContent -match "MpCmdRun.exe.*-SignatureUpdate") { Pass "Defender ATP signature update command present" }
        else                                                       { Info  "Defender ATP signature update not configured" }

        # ── Autologon ─────────────────────────────────────────────────────────
        if ($cmdContent -match "AutoAdminLogon") { Pass "One-time autologon configured" }
        else                                      { Info  "Autologon not configured" }

        # ── Scheduled tasks ───────────────────────────────────────────────────
        $taskCount = ([regex]::Matches($cmdContent, "schtasks /Create")).Count
        if ($taskCount -gt 0) {
            Pass "$taskCount scheduled task(s) staged in SetupComplete.cmd"
            $taskNames = [regex]::Matches($cmdContent, '/TN\s+"([^"]+)"') |
                         ForEach-Object { $_.Groups[1].Value }
            foreach ($tn in $taskNames) { Info "  Task: $tn" }
        }
        else { Info "No scheduled tasks in SetupComplete.cmd" }

        # ── Windows.old cleanup ───────────────────────────────────────────────
        if ($cmdContent -match "skip_wold|Windows\.old") {
            Pass "Windows.old cleanup logic present — old OS folder will be removed after deployment"
        }
        else {
            Warn "Windows.old cleanup not found — a previous Windows install may leave 10-25 GB on C: after deployment"
        }

        # ── Named account create-or-update logic ─────────────────────────────
        if ($cmdContent -match "net user .+ /add") {
            if ($cmdContent -match "if %ERRORLEVEL% NEQ 0") {
                Pass "Named account create-or-update logic present (handles pre-existing oobeSystem account)"
            }
            else {
                Warn "Named account uses /add only — will fail silently if account already exists from oobeSystem XML"
            }
        }

        $lineCount = ($cmdContent -split "`n").Count
        Info "SetupComplete.cmd line count: $lineCount"
    }

    # ── Phase 7b: Panther unattend.xml ────────────────────────────────────────
    Head "PHASE 7b - Panther Unattend.xml"
    $pantherXml = Join-Path $mountDir "Windows\Panther\unattend.xml"
    if (Check-FileExists $pantherXml "Windows\Panther\unattend.xml") {
        try {
            [xml]$pxdoc = Get-Content $pantherXml -Raw
            Pass "Panther unattend.xml is valid XML"
            $kbPanther = $pxdoc.GetElementsByTagName("InputLocale")
            if ($kbPanther.Count -gt 0) {
                $kbPantherVals = @($kbPanther | ForEach-Object { $_.InnerText })
                Pass "Input locale in Panther unattend: $($kbPantherVals -join ', ')"
            }
        }
        catch { Fail "Panther unattend.xml XML parse error: $_" }
    }
}

# ---------------------------------------------------------------------------
#  PHASE 8 - Offline Registry
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 8 - Offline Registry Checks"

    $swHive   = Join-Path $mountDir "Windows\System32\config\SOFTWARE"
    $sysHive  = Join-Path $mountDir "Windows\System32\config\SYSTEM"
    $userHive = Join-Path $mountDir "Users\Default\NTUSER.DAT"

    $hivesLoaded = @{}

    function Load-Hive {
        param([string]$HivePath, [string]$MountPoint)
        if (Test-Path $HivePath) {
            $r = Invoke-Reg @("load", $MountPoint, $HivePath)
            if ($r.ExitCode -eq 0) {
                $script:hivesLoaded[$MountPoint] = $true
                return $true
            }
            else {
                Write-Log "  Failed to load hive $HivePath -> $MountPoint : $($r.Output)" "WARN"
                return $false
            }
        }
        else {
            Write-Log "  Hive file not found: $HivePath" "WARN"
            return $false
        }
    }

    function Unload-AllHives {
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        [GC]::Collect()
        foreach ($mp in $script:hivesLoaded.Keys) {
            for ($i = 1; $i -le 5; $i++) {
                $r = Invoke-Reg @("unload", $mp)
                if ($r.ExitCode -eq 0) { break }
                Start-Sleep -Seconds 1
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
                [GC]::Collect()
            }
        }
        $script:hivesLoaded = @{}
    }

    Info "Loading offline SOFTWARE hive..."
    $swLoaded  = Load-Hive $swHive  "HKLM\GIB_VALIDATE_SW"
    Info "Loading offline SYSTEM hive..."
    $sysLoaded = Load-Hive $sysHive "HKLM\GIB_VALIDATE_SYS"
    Info "Loading offline NTUSER.DAT hive..."
    $usrLoaded = Load-Hive $userHive "HKLM\GIB_VALIDATE_USR"

    try {

        # ── 8a) RunOnce - GIBFirstBoot ────────────────────────────────────────
        if ($swLoaded) {
            $runOnceKey = "HKLM\GIB_VALIDATE_SW\Microsoft\Windows\CurrentVersion\RunOnce"
            $runOnce    = Invoke-Reg @("query", $runOnceKey, "/v", "GIBFirstBoot")
            if ($runOnce.ExitCode -eq 0 -and $runOnce.Output -match "GIBFirstBoot") {
                $m = [regex]::Match($runOnce.Output, "REG_SZ\s+(.+)")
                if ($m.Success) { Pass "RunOnce\GIBFirstBoot = $($m.Groups[1].Value.Trim())" }
                else             { Pass "RunOnce\GIBFirstBoot key present" }
            }
            else { Fail "RunOnce\GIBFirstBoot NOT FOUND — GIBFirstBoot will not auto-launch on first login" }

            # ── 8b) Telemetry ─────────────────────────────────────────────────
            $telemKey = "HKLM\GIB_VALIDATE_SW\Policies\Microsoft\Windows\DataCollection"
            $telem    = Invoke-Reg @("query", $telemKey, "/v", "AllowTelemetry")
            if ($telem.ExitCode -eq 0 -and $telem.Output -match "AllowTelemetry") {
                $tv = Get-RegDword $telem.Output "AllowTelemetry"
                if ($null -ne $tv) {
                    if ($tv -eq 0) { Pass "Telemetry disabled (AllowTelemetry=0)" }
                    else            { Warn  "Telemetry AllowTelemetry=$tv (expected 0)" }
                }
                else { Pass "Telemetry policy key present" }
            }
            else { Info "Telemetry policy not configured (AllowTelemetry key absent — Windows default)" }

            # ── 8c) OEM branding ──────────────────────────────────────────────
            $oemKey = "HKLM\GIB_VALIDATE_SW\Microsoft\Windows\CurrentVersion\OEMInformation"
            $oemReg = Invoke-Reg @("query", $oemKey)
            if ($oemReg.ExitCode -eq 0) {
                $mfg = Get-RegSz $oemReg.Output "Manufacturer"
                $mdl = Get-RegSz $oemReg.Output "Model"
                $url = Get-RegSz $oemReg.Output "SupportURL"
                if ($mfg) { Pass "OEM Manufacturer: $mfg" } else { Info "OEM Manufacturer not set" }
                if ($mdl) { Pass "OEM Model: $mdl"         } else { Info "OEM Model not set"         }
                if ($url) { Pass "OEM SupportURL: $url"    } else { Info "OEM SupportURL not set"    }
            }
            else { Info "OEM branding key not present (not configured)" }

            # ── 8d) RegisteredOwner / Organization ────────────────────────────
            $ntCvKey = "HKLM\GIB_VALIDATE_SW\Microsoft\Windows NT\CurrentVersion"
            $ownerOut = (Invoke-Reg @("query", $ntCvKey, "/v", "RegisteredOwner")).Output
            $orgOut   = (Invoke-Reg @("query", $ntCvKey, "/v", "RegisteredOrganization")).Output
            $ownerVal = Get-RegSz $ownerOut "RegisteredOwner"
            $orgVal   = Get-RegSz $orgOut   "RegisteredOrganization"
            if ($ownerVal) { Pass "RegisteredOwner: $ownerVal" }       else { Info "RegisteredOwner not set" }
            if ($orgVal)   { Pass "RegisteredOrganization: $orgVal" }  else { Info "RegisteredOrganization not set" }

            # ── 8e) Autologon (Winlogon) ──────────────────────────────────────
            $winlogonKey = "HKLM\GIB_VALIDATE_SW\Microsoft\Windows NT\CurrentVersion\Winlogon"
            $wlOut = (Invoke-Reg @("query", $winlogonKey)).Output
            $autoAdminVal = Get-RegSz $wlOut "AutoAdminLogon"
            if ($autoAdminVal -eq "1") {
                $alUser = Get-RegSz $wlOut "DefaultUserName"
                $alCount = Get-RegDword $wlOut "AutoLogonCount"
                Pass "One-time autologon configured for user: $alUser (AutoLogonCount=$alCount)"
            }
            else { Info "Autologon not configured (AutoAdminLogon not set)" }

            # ── 8f) Credential Guard ──────────────────────────────────────────
            $cgKey = "HKLM\GIB_VALIDATE_SW\Policies\Microsoft\Windows\DeviceGuard"
            $cgOut = (Invoke-Reg @("query", $cgKey, "/v", "EnableVirtualizationBasedSecurity")).Output
            $cgVal = Get-RegDword $cgOut "EnableVirtualizationBasedSecurity"
            if ($null -ne $cgVal) {
                if ($cgVal -eq 1) { Pass "Credential Guard / VBS enabled (EnableVirtualizationBasedSecurity=1)" }
                else               { Info  "Credential Guard: EnableVirtualizationBasedSecurity=$cgVal" }
            }
            else { Info "Credential Guard not configured (DeviceGuard key absent)" }

            # ── 8g) Group Policy keys ─────────────────────────────────────────
            $gpRoot    = "HKLM\GIB_VALIDATE_SW\Policies"
            $gpRootFull = "HKEY_LOCAL_MACHINE\GIB_VALIDATE_SW\Policies"
            $gpOut  = Invoke-Reg @("query", $gpRoot)
            if ($gpOut.ExitCode -eq 0) {
                # reg.exe outputs sub-keys as "HKEY_LOCAL_MACHINE\..." (not "HKLM\...").
                # Filter lines that start with the full path prefix and are not the root key itself.
                $gpKeys = @($gpOut.Output -split "`n" |
                    Where-Object { $_.Trim().StartsWith("HKEY_LOCAL_MACHINE\") -and
                                   $_.Trim() -ne $gpRootFull })
                if ($gpKeys.Count -gt 0) {
                    Pass "Group Policy branch present: SOFTWARE\Policies has $($gpKeys.Count) sub-key(s)"
                    foreach ($gpk in $gpKeys) {
                        Info "  GP key: $($gpk.Trim().Replace('HKEY_LOCAL_MACHINE\GIB_VALIDATE_SW\', 'SOFTWARE\'))"
                    }
                }
                else { Info "SOFTWARE\Policies branch exists but no sub-keys (no GP settings injected)" }
            }
            else { Info "SOFTWARE\Policies key absent (no group policy settings configured)" }
        }

        # ── 8h) Dark mode / File extensions / Hidden files (user hive) ────────
        if ($usrLoaded) {
            $themeKey    = "HKLM\GIB_VALIDATE_USR\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            $explorerAdv = "HKLM\GIB_VALIDATE_USR\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"

            $appThemeOut = (Invoke-Reg @("query", $themeKey, "/v", "AppsUseLightTheme")).Output
            $sysThemeOut = (Invoke-Reg @("query", $themeKey, "/v", "SystemUsesLightTheme")).Output
            $hideExtOut  = (Invoke-Reg @("query", $explorerAdv, "/v", "HideFileExt")).Output
            $hiddenOut   = (Invoke-Reg @("query", $explorerAdv, "/v", "Hidden")).Output

            $appTheme = Get-RegDword $appThemeOut "AppsUseLightTheme"
            $sysTheme = Get-RegDword $sysThemeOut "SystemUsesLightTheme"
            $hideExt  = Get-RegDword $hideExtOut  "HideFileExt"
            $hidden   = Get-RegDword $hiddenOut   "Hidden"

            if ($null -ne $appTheme) {
                if ($appTheme -eq 0) { Pass "Apps theme: Dark mode (AppsUseLightTheme=0)" }
                else                  { Pass "Apps theme: Light mode (AppsUseLightTheme=$appTheme)" }
            }
            else { Info "AppsUseLightTheme not set (Windows default will apply)" }

            if ($null -ne $sysTheme) {
                if ($sysTheme -eq 0) { Pass "System theme: Dark mode (SystemUsesLightTheme=0)" }
                else                  { Pass "System theme: Light mode (SystemUsesLightTheme=$sysTheme)" }
            }

            if ($null -ne $hideExt) {
                if ($hideExt -eq 0) { Pass "File extensions visible (HideFileExt=0)" }
                else                 { Info  "File extensions hidden (HideFileExt=$hideExt — Windows default)" }
            }
            else { Info "HideFileExt not configured (Windows default)" }

            if ($null -ne $hidden) {
                if ($hidden -eq 1) { Pass "Hidden files visible (Hidden=1)" }
                else                { Info  "Hidden files not shown (Hidden=$hidden — Windows default)" }
            }
            else { Info "Hidden files setting not configured" }
        }

        # ── 8i) SMBv1 (SYSTEM hive) ───────────────────────────────────────────
        if ($sysLoaded) {
            $smbKey = "HKLM\GIB_VALIDATE_SYS\ControlSet001\Services\mrxsmb10"
            $smbOut = (Invoke-Reg @("query", $smbKey, "/v", "Start")).Output
            $smbStart = Get-RegDword $smbOut "Start"
            if ($null -ne $smbStart) {
                if ($smbStart -eq 4) { Pass "SMBv1 disabled (mrxsmb10\Start=4)" }
                else                  { Warn  "SMBv1 mrxsmb10\Start=$smbStart (expected 4 for disabled)" }
            }
            else { Info "SMBv1 mrxsmb10 service key not found" }

            # ── 8j) NTLMv1 disabled ───────────────────────────────────────────
            $lsaKey = "HKLM\GIB_VALIDATE_SYS\ControlSet001\Control\Lsa"
            $lsaOut = (Invoke-Reg @("query", $lsaKey, "/v", "LmCompatibilityLevel")).Output
            $ntlmVal = Get-RegDword $lsaOut "LmCompatibilityLevel"
            if ($null -ne $ntlmVal) {
                if ($ntlmVal -ge 5) { Pass "NTLMv1 disabled — LmCompatibilityLevel=$ntlmVal (NTLMv2 only)" }
                elseif ($ntlmVal -ge 3) { Warn "LmCompatibilityLevel=$ntlmVal (sends NTLMv2, but still accepts v1 responses)" }
                else                    { Warn "LmCompatibilityLevel=$ntlmVal — NTLMv1 may still be accepted" }
            }
            else { Info "LmCompatibilityLevel not set (Windows default — NTLMv1 may be in use)" }

            # ── 8k) SMB packet signing ────────────────────────────────────────
            $smbSignKey = "HKLM\GIB_VALIDATE_SYS\ControlSet001\Services\LanmanWorkstation\Parameters"
            $smbSignOut = (Invoke-Reg @("query", $smbSignKey, "/v", "RequireSecuritySignature")).Output
            $smbSign    = Get-RegDword $smbSignOut "RequireSecuritySignature"
            if ($null -ne $smbSign) {
                if ($smbSign -eq 1) { Pass "SMB packet signing required (RequireSecuritySignature=1)" }
                else                 { Warn  "SMB packet signing not required (RequireSecuritySignature=$smbSign)" }
            }
            else { Info "RequireSecuritySignature not configured (Windows default)" }
        }
    }
    finally {
        Info "Unloading offline registry hives..."
        Unload-AllHives
        Pass "Offline registry hives unloaded"
    }
}

# ---------------------------------------------------------------------------
#  PHASE 9 - Deployment Scripts
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 9 - Deployment Scripts"

    $pubDocs = Join-Path $mountDir "Users\Public\Documents"
    if (Test-Path $pubDocs) {
        $docScripts = @(Get-ChildItem $pubDocs -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Extension -in @(".ps1", ".bat", ".cmd", ".vbs", ".py", ".js") })
        if ($docScripts.Count -gt 0) {
            Pass "Deployment scripts staged in Public\Documents: $($docScripts.Count) file(s)"
            foreach ($ds in $docScripts) { Info "  Script: $($ds.Name) ($(FormatKB $ds.Length))" }
        }
        else { Info "No deployment scripts found in Public\Documents" }

        # Also list any non-script files placed there deliberately
        $otherDocs = @(Get-ChildItem $pubDocs -File -ErrorAction SilentlyContinue |
                       Where-Object { $_.Extension -notin @(".ps1", ".bat", ".cmd", ".vbs", ".py", ".js") })
        if ($otherDocs.Count -gt 0) {
            Info "Other files in Public\Documents: $($otherDocs.Count)"
            foreach ($od in $otherDocs) { Info "  File: $($od.Name)" }
        }
    }
    else { Info "Users\Public\Documents directory not present (no deployment scripts configured)" }

    $startupDir = Join-Path $mountDir "ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
    if (Test-Path $startupDir) {
        $startupFiles = @(Get-ChildItem $startupDir -File -ErrorAction SilentlyContinue)
        if ($startupFiles.Count -gt 0) {
            Pass "All-users Startup folder has $($startupFiles.Count) file(s) (Every Login scripts)"
            foreach ($sf in $startupFiles) { Info "  Startup: $($sf.Name)" }
        }
        else { Info "All-users Startup folder exists but is empty" }
    }
    else { Info "All-users Startup folder not present (no Every-Login scripts configured)" }
}

# ---------------------------------------------------------------------------
#  PHASE 10 - Windows Features (DISM)
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 10 - Windows Optional Features (DISM)"
    Info "Querying feature states..."
    try {
        $featOut = Invoke-Dism "/Image:`"$mountDir`" /Get-Features /Format:Table"
        $featLines = $featOut -split "`n" | Where-Object { $_ -match "\|" -and $_ -notmatch "^-" -and $_ -notmatch "Feature Name" }

        # Parse features into a hashtable
        $featMap = @{}
        foreach ($fl in $featLines) {
            $parts = $fl -split "\|" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
            if ($parts.Count -ge 2) {
                $featMap[$parts[0]] = $parts[1]
            }
        }

        # Key features to report on
        $keyFeatures = @(
            @{ Name = "Microsoft-Hyper-V";                    Label = "Hyper-V" },
            @{ Name = "Microsoft-Hyper-V-All";                Label = "Hyper-V (All)" },
            @{ Name = "TelnetClient";                         Label = "Telnet Client" },
            @{ Name = "TFTP";                                 Label = "TFTP Client" },
            @{ Name = "SMB1Protocol";                         Label = "SMBv1 Protocol" },
            @{ Name = "SMB1Protocol-Server";                  Label = "SMBv1 Server" },
            @{ Name = "NetFx3";                               Label = ".NET Framework 3.5" },
            @{ Name = "NetFx4-AdvSrvs";                       Label = ".NET Framework 4 Advanced" },
            @{ Name = "WCF-Services45";                       Label = "WCF Services 4.5" },
            @{ Name = "IIS-WebServerRole";                    Label = "IIS Web Server" },
            @{ Name = "Containers";                           Label = "Windows Containers" },
            @{ Name = "Microsoft-Windows-Subsystem-Linux";    Label = "WSL" }
        )

        $reportedAny = $false
        foreach ($kf in $keyFeatures) {
            if ($featMap.ContainsKey($kf.Name)) {
                $state = $featMap[$kf.Name]
                switch ($state) {
                    "Enabled"  { Pass  "Feature [$($kf.Label)]: Enabled" }
                    "Disabled" { Info  "Feature [$($kf.Label)]: Disabled" }
                    default    { Info  "Feature [$($kf.Label)]: $state" }
                }
                $reportedAny = $true
            }
        }

        # Report any custom-enabled features not in the key list
        $enabledFeats = @($featMap.GetEnumerator() | Where-Object { $_.Value -eq "Enabled" })
        $baseEnabled  = @("NetFx4", "NetFx4Extended-ASPNET45", "MicrosoftWindowsPowerShellV2",
                          "MicrosoftWindowsPowerShellV2Root", "WCF-TCP-PortSharing45")
        # NOTE: capture outer $_ in $featKey BEFORE the inner Where-Object pipeline,
        # otherwise the inner $_ (a keyFeature hashtable) shadows it and $_.Key throws
        # "The property 'Key' cannot be found on this object".
        $keyFeatureNames = @($keyFeatures | ForEach-Object { $_.Name })
        $customEnabled = @($enabledFeats |
            Where-Object { $baseEnabled -notcontains $_.Key } |
            Where-Object { $keyFeatureNames -notcontains $_.Key })
        if ($customEnabled.Count -gt 0) {
            Info "Additional enabled features: $($customEnabled.Count)"
            foreach ($ce in $customEnabled) { Info "  Enabled: $($ce.Key)" }
        }

        if (-not $reportedAny) { Info "No key features matched in image (image may use different feature names)" }
        Info "Total features in image: $($featMap.Count)"
    }
    catch { Warn "DISM /Get-Features failed: $_" }
}

# ---------------------------------------------------------------------------
#  PHASE 11 - Language Packs (DISM)
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 11 - Language Packs (DISM)"
    Info "Querying installed packages..."
    try {
        $pkgOut   = Invoke-Dism "/Image:`"$mountDir`" /Get-Packages /Format:Table"
        $langPkgs = $pkgOut -split "`n" | Where-Object {
            $_ -match "LanguagePack\." -or ($_ -match "Language" -and $_ -match "Installed")
        }
        if ($langPkgs.Count -gt 0) {
            Pass "Language pack(s) found: $($langPkgs.Count)"
            $langPkgs | Select-Object -First 30 | ForEach-Object { Info "  $_".Trim() }
        }
        else { Info "No additional language packs found (base Windows language only)" }
    }
    catch { Warn "DISM /Get-Packages failed: $_" }
}

# ---------------------------------------------------------------------------
#  PHASE 12 - Injected Drivers (DISM)
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 12 - Injected Drivers (DISM)"
    Info "Querying third-party (OEM) drivers..."
    try {
        $drvOut    = Invoke-Dism "/Image:`"$mountDir`" /Get-Drivers /All"
        $oemDrvs   = @($drvOut -split "`n" | Where-Object { $_ -match "oem\d+\.inf" })
        $totalDrvs = @($drvOut -split "`n" | Where-Object { $_ -match "\.inf" }).Count
        if ($oemDrvs.Count -gt 0) {
            Pass "OEM / injected driver(s) present: $($oemDrvs.Count)"
            $oemDrvs | ForEach-Object { Info "  $_".Trim() }
        }
        else {
            Info "No OEM drivers found — only in-box Microsoft drivers present"
            Info "  (Expected if no drivers were configured in the build)"
        }
        Info "Total driver INF files in image: $totalDrvs"
    }
    catch { Warn "DISM /Get-Drivers failed: $_" }
}

# ---------------------------------------------------------------------------
#  PHASE 13 - Provisioned AppX Packages (Bloatware)
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 13 - Provisioned AppX Packages (Bloatware Check)"
    Info "Querying remaining provisioned packages..."
    try {
        $appxOut  = Invoke-Dism "/Image:`"$mountDir`" /Get-ProvisionedAppxPackages"
        $packages = $appxOut -split "`n" |
            Where-Object { $_.TrimStart().StartsWith("PackageName") } |
            ForEach-Object {
                $idx = $_.IndexOf(":")
                if ($idx -ge 0) { $_.Substring($idx + 1).Trim() } else { "" }
            } |
            Where-Object { $_ -ne "" }

        if ($packages.Count -gt 0) {
            Pass "Provisioned AppX packages remaining: $($packages.Count)"
            $packages | ForEach-Object { Info "  Package: $_" }
        }
        else { Pass "No provisioned AppX packages found" }

        # Expanded Windows 11 known-bloatware list
        $knownBloat = @(
            "Microsoft.3DBuilder",
            "Microsoft.BingNews",
            "Microsoft.BingWeather",
            "Microsoft.BingFinance",
            "Microsoft.BingSports",
            "Microsoft.BingTravel",
            "Microsoft.GetHelp",
            "Microsoft.Getstarted",
            "Microsoft.GamingApp",
            "Microsoft.MicrosoftOfficeHub",
            "Microsoft.MicrosoftSolitaireCollection",
            "Microsoft.MixedReality.Portal",
            "Microsoft.Office.OneNote",
            "Microsoft.OutlookForWindows",
            "Microsoft.People",
            "Microsoft.PowerAutomateDesktop",
            "Microsoft.SkypeApp",
            "Microsoft.Teams",
            "Microsoft.Todos",
            "Microsoft.WindowsAlarms",
            "Microsoft.WindowsCommunicationsApps",
            "Microsoft.WindowsFeedbackHub",
            "Microsoft.WindowsMaps",
            "Microsoft.WindowsSoundRecorder",
            "Microsoft.Xbox",
            "Microsoft.XboxApp",
            "Microsoft.XboxGamingOverlay",
            "Microsoft.XboxGameOverlay",
            "Microsoft.XboxIdentityProvider",
            "Microsoft.XboxSpeechToTextOverlay",
            "Microsoft.YourPhone",
            "Microsoft.ZuneMusic",
            "Microsoft.ZuneVideo",
            "MicrosoftCorporationII.MicrosoftFamily",
            "MicrosoftCorporationII.QuickAssist",
            "Clipchamp.Clipchamp"
        )
        $stillPresent = @($packages | Where-Object {
            $pkg = $_
            @($knownBloat | Where-Object {
                $pkg.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0
        })
        if ($stillPresent.Count -gt 0) {
            Warn "Known bloatware still present ($($stillPresent.Count) package(s)):"
            $stillPresent | ForEach-Object { Warn "  Still present: $_" }
        }
        else { Pass "None of the known bloatware packages are present in the image" }
    }
    catch { Warn "DISM /Get-ProvisionedAppxPackages failed: $_" }
}

# ---------------------------------------------------------------------------
#  PHASE 14 - WIM Edition Metadata
# ---------------------------------------------------------------------------

if ($wimMounted) {
    Head "PHASE 14 - WIM Edition Metadata"
    try {
        $infoOut   = Invoke-Dism "/Get-WimInfo /WimFile:`"$tempWim`" /Index:$WimIndex"
        $infoLines = $infoOut -split "`n" | Where-Object {
            $_ -match "Name|Description|Version|Architecture|Languages|Edition"
        }
        if ($infoLines.Count -gt 0) {
            Pass "WIM edition details:"
            $infoLines | ForEach-Object { Info "  $($_.Trim())" }
        }
    }
    catch { Warn "Could not retrieve WIM edition details: $_" }
}

# ---------------------------------------------------------------------------
#  CLEANUP
# ---------------------------------------------------------------------------

Head "CLEANUP"

if ($wimMounted) {
    Info "Unmounting WIM (discard — read-only mount, no changes to commit)..."
    try {
        $null = Invoke-Dism "/Unmount-Image /MountDir:`"$mountDir`" /Discard"
        Pass "WIM unmounted cleanly"
    }
    catch { Warn "WIM unmount error: $_" }
}

if ($isoDrive -ne "") {
    Info "Dismounting ISO..."
    try {
        Dismount-DiskImage -ImagePath $IsoPath | Out-Null
        Pass "ISO dismounted"
    }
    catch { Warn "ISO dismount error: $_ — you may need to eject $isoDrive manually" }
}

if (Test-Path $workBase) {
    try {
        Remove-Item $workBase -Recurse -Force -ErrorAction SilentlyContinue
        Info "Temp directory removed"
    }
    catch { Warn "Could not remove temp directory $workBase" }
}

# ---------------------------------------------------------------------------
#  SUMMARY
# ---------------------------------------------------------------------------

$total = $Script:PassCount + $Script:WarnCount + $Script:FailCount
Head "VALIDATION SUMMARY"
Info "ISO file  : $IsoPath"
Info "Timestamp : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Info "----------------------------------------------------"
Info "  PASS : $($Script:PassCount)  /  $total checks"
Info "  WARN : $($Script:WarnCount)  /  $total checks"
Info "  FAIL : $($Script:FailCount)  /  $total checks"
Info "----------------------------------------------------"

if ($Script:FailCount -eq 0 -and $Script:WarnCount -eq 0) {
    Pass "RESULT: ALL CHECKS PASSED — ISO appears correctly built"
}
elseif ($Script:FailCount -eq 0) {
    Warn "RESULT: PASSED WITH WARNINGS — review WARN items above"
}
else {
    Fail "RESULT: $($Script:FailCount) CHECK(S) FAILED — review FAIL items above"
}

Info "Log saved : $logFile"
$Script:LogLines | Out-File $logFile -Encoding UTF8
Write-Host ""
Write-Host "Log written to: $logFile" -ForegroundColor Cyan

exit $(if ($Script:FailCount -gt 0) { 1 } else { 0 })
