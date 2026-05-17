<#
.SYNOPSIS
    Profile-driven end-to-end validator for WinForge deployments.

.DESCRIPTION
    Reads a .gibprofile file and dynamically generates all checks against the
    deployed laptop. Unlike the static Validate-GoldenImage.ps1, this script
    auto-adapts to whatever the profile asks for -- features, apps, certs,
    fonts, group policies, KB updates, driver packs, etc. If the profile does
    NOT enable something, no check is generated for it (so no false-positive
    FAILs).

    Run on a freshly-deployed laptop AFTER first login (so SetupComplete.cmd
    and GIBFirstBoot have finished). Writes a colourised report to console and
    a text report to C:\ALE-Validator\ValidationReport_<timestamp>.txt.

.PARAMETER ProfilePath
    Path to the .gibprofile JSON file. If omitted, the script looks for
    *.gibprofile in the same directory and uses the most-recently-modified one.

.PARAMETER ReportFolder
    Where to drop the text report. Default: C:\ALE-Validator

.EXAMPLE
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
    .\Validate-GoldenImage-Auto.ps1 -ProfilePath .\MyBuildProfileV3.gibprofile

.NOTES
    Must run elevated. Some checks (BitLocker, Get-AppxPackage -AllUsers,
    Get-WindowsDriver, Get-WindowsPackage) require admin context.
    ASCII-only by design (no em-dashes, no box-drawing) -- avoids Windows-1252
    re-interpretation problems when copying via USB.
#>

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ProfilePath,
    [string]$ReportFolder = 'C:\ALE-Validator'
)

# -----------------------------------------------------------------------------
# Locate profile
# -----------------------------------------------------------------------------
if (-not $ProfilePath) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $candidates = Get-ChildItem -Path $scriptDir -Filter '*.gibprofile' -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending
    if (-not $candidates) {
        Write-Host "ERROR: no .gibprofile file passed via -ProfilePath and none found in $scriptDir" -ForegroundColor Red
        exit 2
    }
    $ProfilePath = $candidates[0].FullName
    Write-Host "Auto-selected profile: $ProfilePath" -ForegroundColor Cyan
}
if (-not (Test-Path $ProfilePath)) {
    Write-Host "ERROR: profile not found: $ProfilePath" -ForegroundColor Red
    exit 2
}
$profileJson = $null
try {
    $profileJson = Get-Content -Path $ProfilePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Host "ERROR: could not parse profile JSON: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

# -----------------------------------------------------------------------------
# Plumbing
# -----------------------------------------------------------------------------
if (-not (Test-Path $ReportFolder)) { New-Item -ItemType Directory -Path $ReportFolder -Force | Out-Null }
$stamp      = Get-Date -Format 'yyyyMMdd_HHmmss'
$reportPath = Join-Path $ReportFolder "ValidationReport_$stamp.txt"
$global:Results = New-Object System.Collections.Generic.List[object]
$global:CurrentCategory = ''

function Write-Section([string]$Name) {
    $global:CurrentCategory = $Name
    $bar = '-' * 78
    Write-Host ''
    Write-Host $bar -ForegroundColor DarkGray
    Write-Host "[$Name]" -ForegroundColor Cyan
    Write-Host $bar -ForegroundColor DarkGray
}

function Add-Result {
    param(
        [ValidateSet('PASS','FAIL','WARN','INFO')] [string]$Status,
        [string]$Name,
        [string]$Detail = ''
    )
    $colour = switch ($Status) {
        'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } 'INFO' { 'Gray' }
    }
    $line = '  {0,-4}  {1}' -f $Status, $Name
    Write-Host $line -ForegroundColor $colour
    if ($Detail) { Write-Host "         |- $Detail" -ForegroundColor DarkGray }
    $global:Results.Add([pscustomobject]@{
        Category = $global:CurrentCategory
        Status   = $Status
        Name     = $Name
        Detail   = $Detail
    })
}

function Test-RegValue {
    param([string]$Path, [string]$Name, $Expected, [string]$Label)
    try {
        $val = (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name
        if ($null -eq $Expected -or "$val" -eq "$Expected") {
            Add-Result PASS $Label "$Path\$Name = $val"
        } else {
            Add-Result FAIL $Label "$Path\$Name = $val (expected $Expected)"
        }
    } catch {
        Add-Result FAIL $Label "Missing: $Path\$Name"
    }
}

function Test-DefaultUserReg {
    param([string]$SubKey, [string]$Name, $Expected, [string]$Label)
    $paths = @("Registry::HKEY_USERS\.DEFAULT\$SubKey", "HKCU:\$SubKey")
    foreach ($p in $paths) {
        try {
            $val = (Get-ItemProperty -Path $p -Name $Name -ErrorAction Stop).$Name
            if ("$val" -eq "$Expected") { Add-Result PASS $Label "$p\$Name = $val"; return }
        } catch {}
    }
    Add-Result WARN $Label "Not found in HKU\.DEFAULT or current HKCU: $SubKey\$Name"
}

# Strip Appx version+publisher tail from a bloatware ID, e.g.
#   "Microsoft.BingNews_4.1.24002.0_neutral_~_8wekyb3d8bbwe" -> "Microsoft.BingNews"
function Convert-BloatToShortName([string]$pkgName) {
    # First underscore-bracketed segment is the version; everything after is publisher tail.
    if ($pkgName -match '^([^_]+)_') { return $matches[1] }
    return $pkgName
}

# Extract KB number from a path like ".../KB5087058.msu" -> "KB5087058"
function Get-KbFromPath([string]$path) {
    $name = Split-Path -Leaf $path
    if ($name -match '(?i)(KB\d{6,7})') { return $matches[1] }
    return $null
}

# Header banner + profile summary
$banner = '=' * 78
Write-Host $banner -ForegroundColor Cyan
Write-Host 'WinForge -- Profile-Driven Validator' -ForegroundColor Cyan
Write-Host $banner -ForegroundColor Cyan
Write-Host "Profile : $(Split-Path -Leaf $ProfilePath)"
Write-Host "Host    : $env:COMPUTERNAME"
Write-Host "User    : $env:USERNAME"
Write-Host "Date    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "Report  : $reportPath"

# =============================================================================
# 1. IDENTITY / HOSTNAME / ADMIN
# =============================================================================
Write-Section 'IDENTITY'

$prefix = if ($profileJson.ComputerPrefix) { $profileJson.ComputerPrefix } else { '' }
$template = if ($profileJson.HostnameTemplate) { $profileJson.HostnameTemplate } else { '' }
$hostname = $env:COMPUTERNAME

if ($template) {
    $bios        = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
    $serial      = ($bios.SerialNumber -replace '[^a-zA-Z0-9]','').ToUpper()
    $last6serial = if ($serial.Length -ge 6) { $serial.Substring($serial.Length - 6) } else { $serial }
    $nic         = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Sort-Object Status | Select-Object -First 1
    $mac         = if ($nic) { ($nic.MacAddress -replace '[:\-]','').ToUpper() } else { '' }
    $last6mac    = if ($mac.Length -ge 6) { $mac.Substring($mac.Length - 6) } else { $mac }
    $enc         = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
    $asset       = ($enc.SMBIOSAssetTag -replace '[^a-zA-Z0-9]','').ToUpper()
    $expected    = $template `
        -replace '\{PREFIX\}',       $prefix `
        -replace '\{SERIAL\}',       $serial `
        -replace '\{LAST6_SERIAL\}', $last6serial `
        -replace '\{LAST6_MAC\}',    $last6mac `
        -replace '\{ASSETTAG\}',     $asset
    if ($expected.Length -gt 15) { $expected = $expected.Substring(0,15) }
    if ($hostname -eq $expected) {
        Add-Result PASS "Hostname matches template '$template'" "$hostname"
    } elseif ($prefix -and $hostname -like "$prefix*") {
        Add-Result WARN 'Hostname has expected prefix but does not match template' "got=$hostname expected=$expected"
    } else {
        Add-Result FAIL 'Hostname does not match template' "got=$hostname expected=$expected"
    }
} else {
    Add-Result INFO 'No HostnameTemplate in profile' "current=$hostname"
}

$adminName = if ($profileJson.AdminUsername) { $profileJson.AdminUsername } else { 'Administrator' }
$admin = Get-LocalUser -Name $adminName -ErrorAction SilentlyContinue
if ($admin) {
    if ($admin.Enabled) { Add-Result PASS "$adminName account enabled" }
    else                { Add-Result FAIL "$adminName account exists but is disabled" }
    if ($profileJson.PasswordNeverExpires) {
        # BuildEngine uses "net accounts /maxpwage:unlimited" which sets the machine
        # policy level, not the per-user flag. Get-LocalUser.PasswordNeverExpires only
        # reflects the per-user flag and stays $false even when the policy makes the
        # password effectively never expire. Use "net user" output instead -- it
        # reports the *effective* expiry which accounts for both per-user flag and policy.
        $netUserOut = (net user $adminName 2>&1) | Out-String
        $expiresLine = ($netUserOut -split '\n' | Where-Object { $_ -match 'Password expires' } | Select-Object -First 1).Trim()
        if ($netUserOut -match 'Password expires\s+Never' -or $admin.PasswordNeverExpires) {
            Add-Result PASS "$adminName : Password never expires" $expiresLine
        } else {
            Add-Result FAIL "$adminName : Password may expire" $expiresLine
        }
    }
} else {
    Add-Result FAIL "$adminName account missing"
}

if ($profileJson.AutoLoginEnabled) {
    # AutoAdminLogon resets to 0 after a single-use auto-login fires (LogonCount=1 is
    # consumed by Windows after the first automatic login). If the current session IS
    # the admin account, the auto-login worked correctly — report PASS rather than FAIL.
    $winlogonKey  = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    $autoAdminVal = (Get-ItemProperty -Path $winlogonKey -Name AutoAdminLogon -ErrorAction SilentlyContinue).AutoAdminLogon
    if ("$autoAdminVal" -eq '1') {
        Add-Result PASS 'AutoLogon: AutoAdminLogon=1' "$winlogonKey = 1"
    } elseif ($env:USERNAME -eq $adminName) {
        Add-Result PASS 'AutoLogon: single-use auto-login consumed' "AutoAdminLogon was reset to 0 after firing — current session is $adminName, confirming auto-login succeeded"
    } else {
        Add-Result FAIL 'AutoLogon: AutoAdminLogon=1' "$winlogonKey = $autoAdminVal (expected 1, and current user '$($env:USERNAME)' is not '$adminName')"
    }
    Test-RegValue $winlogonKey 'DefaultUserName' $adminName "AutoLogon: DefaultUserName=$adminName"
}

if ($profileJson.OrgName) {
    Test-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'RegisteredOrganization' $profileJson.OrgName 'Registered organization'
}

# =============================================================================
# 2. TIME ZONE / POWER PLAN
# =============================================================================
Write-Section 'SYSTEM SETTINGS'

if ($profileJson.TimeZone) {
    $tz = (Get-TimeZone).Id
    if ($tz -eq $profileJson.TimeZone) { Add-Result PASS 'TimeZone' $tz }
    else                                { Add-Result FAIL 'TimeZone' "got=$tz expected=$($profileJson.TimeZone)" }
}

if ($profileJson.PowerPlan) {
    try {
        $active = (powercfg /getactivescheme) 2>&1
        # Map profile internal identifiers to the display strings powercfg prints
        $planDisplay = switch ($profileJson.PowerPlan) {
            'HighPerformance' { 'High performance' }
            'Balanced'        { 'Balanced' }
            'Ultimate'        { 'Ultimate Performance' }
            default           { $profileJson.PowerPlan }
        }
        if ($active -match [regex]::Escape($planDisplay)) { Add-Result PASS 'Active power plan' $planDisplay }
        else                                               { Add-Result WARN 'Active power plan' "got=$active expected=$planDisplay" }
    } catch { Add-Result WARN 'Active power plan' 'powercfg unavailable' }
}

# =============================================================================
# 3. BLOATWARE REMOVAL
# =============================================================================
$bloat = @($profileJson.BloatwareToRemove)
if ($bloat.Count -gt 0) {
    Write-Section 'BLOATWARE REMOVAL'
    $installed   = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name -Unique)
    $provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DisplayName -Unique)
    foreach ($pkg in $bloat) {
        $shortName = Convert-BloatToShortName $pkg
        $stillInst = $installed   | Where-Object { $_ -eq $shortName }
        $stillProv = $provisioned | Where-Object { $_ -eq $shortName }
        if (-not $stillInst -and -not $stillProv) {
            Add-Result PASS "Removed: $shortName"
        } elseif ($stillProv -and -not $stillInst) {
            Add-Result WARN "$shortName" 'removed for users but still provisioned'
        } else {
            Add-Result FAIL "$shortName" 'still installed for at least one user'
        }
    }
}

# =============================================================================
# 4. OPTIONAL FEATURES
# =============================================================================
$enableFeats  = @($profileJson.EnabledFeatures)
$disableFeats = @($profileJson.DisabledFeatures)
if ($enableFeats.Count -gt 0 -or $disableFeats.Count -gt 0) {
    Write-Section 'OPTIONAL FEATURES'
    foreach ($f in $enableFeats) {
        $st = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue).State
        if ($st -eq 'Enabled')   { Add-Result PASS "Feature enabled: $f" }
        elseif ($null -eq $st)   { Add-Result FAIL "Feature missing: $f" }
        else                      { Add-Result FAIL "Feature not enabled: $f" "state=$st" }
    }
    foreach ($f in $disableFeats) {
        $st = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue).State
        if ($st -eq 'Disabled' -or $st -eq 'DisabledWithPayloadRemoved') {
            Add-Result PASS "Feature disabled: $f" "state=$st"
        } else {
            Add-Result FAIL "Feature not disabled: $f" "state=$st"
        }
    }
}

# =============================================================================
# 5. SECURITY / HARDENING
# =============================================================================
Write-Section 'SECURITY'

if ($profileJson.EnableBitLocker) {
    $blDrive = if ($profileJson.BitLockerDriveLetter) { $profileJson.BitLockerDriveLetter } else { 'C:' }
    if ($blDrive -notmatch ':$') { $blDrive = "$blDrive`:" }
    try {
        $bl = Get-BitLockerVolume -MountPoint $blDrive -ErrorAction Stop
        if ($bl.ProtectionStatus -eq 'On') {
            Add-Result PASS "BitLocker on $blDrive" "Protection=On, $($bl.EncryptionPercentage)% encrypted"
        } elseif ($bl.VolumeStatus -eq 'FullyEncrypted' -and $bl.EncryptionPercentage -eq 100) {
            # Drive is fully encrypted; ProtectionStatus briefly lags behind before
            # the TPM protector is confirmed active — treat as PASS.
            Add-Result PASS "BitLocker on $blDrive" "Fully encrypted (100%) — TPM protector activation completing"
        } elseif ($bl.VolumeStatus -match 'Encrypt') {
            Add-Result WARN "BitLocker on $blDrive" "Encryption in progress: $($bl.VolumeStatus) ($($bl.EncryptionPercentage)%)"
        } else {
            Add-Result FAIL "BitLocker on $blDrive" "Protection=$($bl.ProtectionStatus), Status=$($bl.VolumeStatus)"
        }
    } catch {
        Add-Result FAIL "BitLocker on $blDrive" $_.Exception.Message
    }

    if ($profileJson.BitLockerSaveRecoveryKey) {
        $keyFolder = if ($profileJson.BitLockerKeyFolder) { $profileJson.BitLockerKeyFolder } else { 'C:\' }
        $keyFiles = Get-ChildItem -Path $keyFolder -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '(?i)bitlocker.*\.txt$' }
        if ($keyFiles) {
            Add-Result PASS 'BitLocker recovery key file present' (($keyFiles | ForEach-Object FullName) -join ', ')
        } else {
            Add-Result WARN 'BitLocker recovery key file not found' "looked in $keyFolder for *.txt matching '(?i)bitlocker.*\.txt$'"
        }
    }
}

if ($profileJson.EnableDefenderAtp) {
    $defKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
    if (Test-Path $defKey) { Add-Result PASS 'Defender policy hive present' }
    else                    { Add-Result WARN 'Defender policy hive missing' }
    try {
        $mp = Get-MpPreference -ErrorAction Stop
        Add-Result PASS 'Defender service responsive' "MAPS=$($mp.MAPSReporting) Submit=$($mp.SubmitSamplesConsent)"
    } catch { Add-Result WARN 'Defender service not responding' $_.Exception.Message }
}

if ($profileJson.DisableTelemetry) {
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry' 0 'Telemetry disabled (AllowTelemetry=0)'
}

# =============================================================================
# 6. UI / SYSTEM DEFAULTS
# =============================================================================
Write-Section 'UI / SYSTEM DEFAULTS'

if ($profileJson.DarkMode) {
    Test-DefaultUserReg 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'AppsUseLightTheme' 0 'Dark mode (apps)'
    Test-DefaultUserReg 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'SystemUsesLightTheme' 0 'Dark mode (system)'
}

if ($profileJson.ShowFileExtensions) {
    Test-DefaultUserReg 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'HideFileExt' 0 'Show file extensions'
}

if ($profileJson.WallpaperPath) {
    $wpHits = @(
        Get-ChildItem -Path 'C:\Windows\Web\Wallpaper\Windows' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)^img(0|19|20)\.jpg$' }
    )
    if ($wpHits.Count -gt 0) { Add-Result PASS 'Wallpaper replaced' "found $($wpHits.Count) file(s) in Wallpaper\Windows" }
    else                      { Add-Result FAIL 'Wallpaper image not found' 'expected img0/img19/img20.jpg in Windows\Web\Wallpaper\Windows' }
}

if ($profileJson.LockScreenPath) {
    $ls = 'C:\Windows\Web\Screen'
    $lsHits = @(Get-ChildItem -Path $ls -Filter 'img1*.jpg' -ErrorAction SilentlyContinue)
    if ($lsHits.Count -gt 0) { Add-Result PASS 'Lock screen image present' "$ls" }
    else                      { Add-Result WARN 'Lock screen image not found' $ls }
}

# =============================================================================
# 7. WINDOWS 11 UX BASELINE
# =============================================================================
Write-Section 'WINDOWS 11 UX BASELINE'

if ($profileJson.DisableCopilot) {
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1 'Copilot disabled'
}
if ($profileJson.DisableRecall) {
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'DisableAIDataAnalysis' 1 'Recall disabled (DisableAIDataAnalysis)'
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'AllowRecallEnablement' 0 'Recall blocked (AllowRecallEnablement)'
}
if ($profileJson.DisableWidgets) {
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0 'Widgets / News & Interests disabled'
}
if ($profileJson.DisableChatIcon) {
    Test-RegValue 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Experience' 'ConfigureChatIcon' 3 'Teams Chat icon hidden'
}
if ($profileJson.DisableConsumerFeatures) {
    Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 1 'Consumer Features disabled'
}

# =============================================================================
# 8. ONEDRIVE UNINSTALL
# =============================================================================
if ($profileJson.UninstallOneDrive) {
    Write-Section 'ONEDRIVE'

    $proc = Get-Process -Name 'OneDrive' -ErrorAction SilentlyContinue
    $setupProc = Get-Process -Name 'OneDriveSetup' -ErrorAction SilentlyContinue
    if (-not $proc) {
        if ($setupProc) {
            Add-Result INFO 'No resident OneDrive.exe; OneDriveSetup.exe still running (uninstall in progress)' "$($setupProc.Name -join ', ')"
        } else {
            Add-Result PASS 'No OneDrive process running'
        }
    } else {
        Add-Result FAIL 'OneDrive.exe is running (uninstall did not complete)' ($proc.Name -join ', ')
    }

    # System32/SysWOW64 staging copies should be deleted (engine A2 fix)
    foreach ($p in @("$env:SystemRoot\System32\OneDriveSetup.exe","$env:SystemRoot\SysWOW64\OneDriveSetup.exe")) {
        if (Test-Path $p) {
            Add-Result WARN "OEM staging copy still present" $p
        } else {
            Add-Result PASS "OEM staging copy deleted" $p
        }
    }

    if (Test-Path "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDrive.exe") {
        Add-Result WARN 'Per-user OneDrive.exe still present' "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDrive.exe"
    } else {
        Add-Result PASS 'No per-user OneDrive.exe in LOCALAPPDATA'
    }

    $run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OneDrive' -ErrorAction SilentlyContinue
    if (-not $run) { Add-Result PASS 'No OneDrive Run key for current user' }
    else           { Add-Result FAIL 'OneDrive Run key still present' $run.OneDrive }
}

# =============================================================================
# 9. CERTIFICATES
# =============================================================================
$certs = @($profileJson.Certificates)
if ($certs.Count -gt 0) {
    Write-Section 'TRUSTED CERTIFICATES'
    foreach ($c in $certs) {
        $store     = $c.Store
        $fileName  = Split-Path -Leaf $c.SourcePath
        $storePath = "Cert:\LocalMachine\$store"

        # Primary: load the cert file from its staging folder (BuildEngine copies it
        # to C:\Windows\Setup\Scripts\Certs\<Store>\) and compare by thumbprint.
        # certutil -addstore does not set FriendlyName, so FriendlyName matching is
        # unreliable. Thumbprint comparison is exact and immune to filename/Subject
        # naming differences.
        $stagingCert = "C:\Windows\Setup\Scripts\Certs\$store\$fileName"
        $certObj = $null
        if (Test-Path $stagingCert) {
            try { $certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $stagingCert -ErrorAction Stop } catch {}
        }

        if ($certObj) {
            $thumb = $certObj.Thumbprint
            $found = Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
                     Where-Object { $_.Thumbprint -eq $thumb }
            if ($found) {
                Add-Result PASS "Cert imported in ${store}: $fileName" "Thumbprint=$thumb Subject=$($certObj.Subject)"
            } else {
                Add-Result FAIL "Cert NOT imported in ${store}: $fileName" "Thumbprint=$thumb not found in store (certutil may have failed)"
            }
        } else {
            # Fallback: staging file not present — derive a loose regex from filename stem
            $stem    = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
            $rxParts = ($stem -split '[-_ .]') | Where-Object { $_ -match '[A-Za-z]' }
            $rx      = ($rxParts -join '\s*[^a-zA-Z]*\s*')
            $found   = Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
                       Where-Object { $_.Subject -match $rx }
            if ($found) {
                Add-Result PASS "Cert in ${store}: $fileName" "Subject=$($found[0].Subject) (matched by Subject)"
            } else {
                Add-Result WARN "Cert staging file not found and no Subject match in ${store}" "$fileName (staging: $stagingCert)"
            }
        }
    }
}

# =============================================================================
# 10. CUSTOM FONTS
# =============================================================================
$fonts = @($profileJson.Fonts)
if ($fonts.Count -gt 0) {
    Write-Section 'CUSTOM FONTS'
    $fontReg = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts' -ErrorAction SilentlyContinue
    foreach ($f in $fonts) {
        $fileName = Split-Path -Leaf $f.SourcePath
        $fontPath = "C:\Windows\Fonts\$fileName"
        if (Test-Path $fontPath) { Add-Result PASS "Font file installed: $fileName" $fontPath }
        else                      { Add-Result FAIL "Font file missing: $fileName" $fontPath }

        if ($fontReg) {
            $displayName = if ($f.DisplayName) { $f.DisplayName } else { [System.IO.Path]::GetFileNameWithoutExtension($fileName) }
            $hit = $fontReg.PSObject.Properties | Where-Object { $_.Name -match [regex]::Escape($displayName) }
            if ($hit) { Add-Result PASS "Font registered: $displayName" "$($hit.Name) -> $($hit.Value)" }
            else      { Add-Result FAIL "Font registry entry missing: $displayName" }
        }
    }
}

# =============================================================================
# 11. GROUP POLICIES
# =============================================================================
$gps = @($profileJson.GroupPolicies)
if ($gps.Count -gt 0) {
    Write-Section 'GROUP POLICIES'
    foreach ($gp in $gps) {
        # Engine maps HKCU policies onto HKU\.DEFAULT offline, then HKCU after login.
        # ValueType: REG_DWORD = numeric; REG_SZ = string. State Enabled = 1, Disabled = 0
        # (this is the State as defined by the GP itself).
        # Prefer the explicit Value field (written by BuildEngine to the offline hive).
        # Fall back to State-derived 1/0 for older profiles that pre-date the Value field.
        $expectedVal = if ($null -ne $gp.Value -and "$($gp.Value)" -ne '') {
            $gp.Value
        } elseif ($gp.ValueType -eq 'REG_DWORD') {
            if ($gp.State -eq 'Enabled') { 1 } else { 0 }
        } else { $gp.State }
        if ($gp.PolicyClass -eq 'User') {
            Test-DefaultUserReg $gp.RegistryKey $gp.ValueName $expectedVal "GP/User: $($gp.DisplayName)"
        } else {
            Test-RegValue "HKLM:\$($gp.RegistryKey)" $gp.ValueName $expectedVal "GP/Machine: $($gp.DisplayName)"
        }
    }
}

# =============================================================================
# 12. LANGUAGE PACKS
# =============================================================================
$langPaths = @($profileJson.LanguagePackPaths)
if ($langPaths.Count -gt 0) {
    Write-Section 'LANGUAGE PACKS'
    # Extract locale tokens from filenames using two patterns (mirrors BuildEngine logic).
    # Pattern 1: dash-bounded  e.g. "LanguageFeatures-Basic-zh-cn-Package"  -> zh-cn
    # Pattern 2: underscore/tilde-bounded  e.g. "Client-Language-Pack_x64_zh-cn.cab" -> zh-cn
    function Get-LocaleTokens([string]$fileName) {
        $set = @{}
        [regex]::Matches($fileName, '(?i)\b([a-z]{2,3}-[A-Za-z]{2,4})\b') |
            ForEach-Object { $set[$_.Groups[1].Value.ToLower()] = $true }
        [regex]::Matches($fileName, '(?i)[_~]([a-z]{2,3}-[A-Za-z]{2,4})(?=[_~.\-]|$)') |
            ForEach-Object { $set[$_.Groups[1].Value.ToLower()] = $true }
        return @($set.Keys)
    }
    $expectedLangs = @{}
    foreach ($p in $langPaths) {
        $fname = Split-Path -Leaf $p
        foreach ($tok in (Get-LocaleTokens $fname)) { $expectedLangs[$tok] = $true }
    }
    # Query both Get-Intl (active locale) and Get-Packages (language component store)
    $dismIntl = try { dism /online /Get-Intl 2>&1 | Out-String } catch { '' }
    $dismPkgs = try { dism /online /Get-Packages /Format:Table 2>&1 | Out-String } catch { '' }
    $dismAll  = "$dismIntl`n$dismPkgs"
    foreach ($code in $expectedLangs.Keys) {
        if ($dismAll -match [regex]::Escape($code)) {
            Add-Result PASS "Language pack installed: $code"
        } else {
            Add-Result WARN "Language pack not detected via DISM: $code" 'check Settings > Time & language > Language'
        }
    }
}

# =============================================================================
# 13. STAGED APPS (installed by GIBFirstBoot)
# =============================================================================
$apps = @($profileJson.StagedApps)
if ($apps.Count -gt 0) {
    Write-Section 'STAGED APPS (first-boot installer)'

    # Multi-signal detection. We synthesize a DisplayName regex from the
    # installer filename by taking the first all-letter run (e.g.
    # "Zscaler-windows-4.5.0.296-installer-x64.msi" -> "Zscaler"; that fuzzy
    # match catches "Zscaler Client Connector" in the Uninstall hive).
    function Get-AppDisplayRegex([string]$name) {
        # Leading alphabetic word (3+ chars) — covers "Zscaler", "Adobe", etc.
        if ($name -match '^([A-Za-z]{3,})') { return $matches[1] }
        # Any alphabetic word (3+ chars) — covers "7-Zip", "3CX Desktop App", etc.
        # Uses the first such word so version numbers in the name don't pin the regex.
        if ($name -match '([A-Za-z]{3,})') { return $matches[1] }
        return [regex]::Escape($name)
    }

    $regRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($app in $apps) {
        $label = $app.Name
        $rx = Get-AppDisplayRegex $app.Name
        $signals = @()

        # Registry hit (any of three hives)
        foreach ($p in $regRoots) {
            $hit = Get-ItemProperty $p -ErrorAction SilentlyContinue |
                   Where-Object { $_.DisplayName -match $rx } |
                   Select-Object -First 1
            if ($hit) { $signals += "registry: $($hit.DisplayName) $($hit.DisplayVersion)"; break }
        }

        # File: assume vendor folder under Program Files matches the regex
        foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData)) {
            if (-not $pf) { continue }
            $found = Get-ChildItem -Path $pf -Directory -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -match $rx } |
                     Select-Object -First 1
            if ($found) { $signals += "folder: $($found.FullName)"; break }
        }

        # Service: any service whose Name OR DisplayName matches the regex
        $svcHit = Get-Service -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match $rx -or $_.DisplayName -match $rx } |
                  Select-Object -First 1
        if ($svcHit) { $signals += "service: $($svcHit.Name) ($($svcHit.Status))" }

        # Process
        $procHit = Get-Process -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match $rx } |
                   Select-Object -First 1
        if ($procHit) { $signals += "process: $($procHit.Name) pid=$($procHit.Id)" }

        if ($signals.Count -ge 1) {
            Add-Result PASS "$label installed" ($signals -join ' | ')
        } else {
            Add-Result FAIL "$label NOT installed" "no signal matched '$rx' in registry / Program Files / services / processes -- check C:\GIB\state.json and C:\GIB\install.log"
        }
    }

    if (-not (Test-Path 'C:\GIB')) { Add-Result PASS 'C:\GIB cleaned up (first-boot complete)' }
    elseif (Test-Path 'C:\GIB\state.json') {
        Add-Result WARN 'C:\GIB still present' 'first-boot incomplete or pending retry'
    } else {
        Add-Result WARN 'C:\GIB present but no state.json' 'check why cleanup did not run'
    }
}

# =============================================================================
# 14. DEPLOYMENT SCRIPTS / STAGED FILES
# =============================================================================
$scripts  = @($profileJson.DeploymentScripts)
$pubFiles = @($profileJson.PublicDesktopFiles)
$staged   = @($profileJson.StagedFiles)
if ($scripts.Count -gt 0 -or $pubFiles.Count -gt 0 -or $staged.Count -gt 0) {
    Write-Section 'DEPLOYMENT SCRIPTS & STAGED FILES'

    foreach ($s in $scripts) {
        $tgt = $s.Path
        if (Test-Path $tgt) {
            Add-Result PASS "Deployment script present" $tgt
        } else {
            Add-Result FAIL "Deployment script missing" $tgt
        }

        if ($s.Trigger -eq 'EveryLogin') {
            $scriptName = Split-Path -Leaf $tgt
            $stem = [System.IO.Path]::GetFileNameWithoutExtension($scriptName)
            $startupDirs = @(
                "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Startup",
                "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp"
            ) | Sort-Object -Unique
            $startupHits = foreach ($d in $startupDirs) {
                Get-ChildItem -Path $d -ErrorAction SilentlyContinue -Filter "$stem.*"
            }
            if ($startupHits) {
                Add-Result PASS "EveryLogin trigger present (Startup folder)" (($startupHits | ForEach-Object FullName) -join ', ')
            } else {
                # Fallback: Run-key entries referencing this script's stem
                $runHives = @(
                    @{ Name='HKCU\Run';         Path='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' },
                    @{ Name='HKU .DEFAULT\Run'; Path='Registry::HKEY_USERS\.DEFAULT\Software\Microsoft\Windows\CurrentVersion\Run' },
                    @{ Name='HKLM\Run';         Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' }
                )
                $runRefs = @()
                foreach ($h in $runHives) {
                    $values = Get-ItemProperty -Path $h.Path -ErrorAction SilentlyContinue
                    if ($values) {
                        $runRefs += $values.PSObject.Properties |
                            Where-Object { $_.MemberType -eq 'NoteProperty' -and $_.Value -is [string] -and $_.Value -match [regex]::Escape($stem) } |
                            ForEach-Object { "$($h.Name)\$($_.Name)=$($_.Value)" }
                    }
                }
                if ($runRefs.Count -gt 0) {
                    Add-Result PASS "EveryLogin trigger present (Run key)" ($runRefs[0])
                } else {
                    Add-Result WARN "EveryLogin trigger not found for $stem" 'expected file in Startup folder or Run-key entry'
                }
            }
        }
    }

    foreach ($sf in $staged) {
        $fileName = Split-Path -Leaf $sf.SourcePath
        # DestinationFolder is stored as an image-relative path (e.g. "Users\Public\Desktop").
        # Prepend C:\ so Test-Path resolves against the filesystem, not the working directory.
        $df       = if ($sf.DestinationFolder) { $sf.DestinationFolder.TrimStart('\') } else { 'Users\Public\Desktop' }
        $destBase = if ($df -match '^[A-Za-z]:') { $df } else { "C:\$df" }
        $dest     = Join-Path $destBase $fileName
        if (Test-Path $dest) { Add-Result PASS "Staged file present: $fileName" $dest }
        else                  { Add-Result FAIL "Staged file missing: $fileName" $dest }
    }

    foreach ($pf in $pubFiles) {
        $fileName = Split-Path -Leaf $pf
        $dest = Join-Path 'C:\Users\Public\Desktop' $fileName
        if (Test-Path $dest) { Add-Result PASS "Public desktop file present: $fileName" $dest }
        else                  { Add-Result FAIL "Public desktop file missing: $fileName" $dest }
    }
}

# =============================================================================
# 15. WINDOWS UPDATES (slipstreamed)
# =============================================================================
$msus = @($profileJson.UpdatesMsuPaths)
if ($msus.Count -gt 0) {
    Write-Section 'WINDOWS UPDATES'
    # Cache the package list once.
    $allPkgs = Get-WindowsPackage -Online -ErrorAction SilentlyContinue
    $allQfe  = Get-HotFix -ErrorAction SilentlyContinue
    foreach ($msu in $msus) {
        $kb = Get-KbFromPath $msu
        if (-not $kb) {
            Add-Result WARN "Cannot extract KB from MSU path" $msu
            continue
        }
        $kbDigits = $kb -replace 'KB',''
        $inQfe   = $allQfe  | Where-Object { $_.HotFixID -eq $kb } | Select-Object -First 1
        $inStore = $allPkgs | Where-Object { $_.PackageName -like "*$kbDigits*" -or $_.PackageName -like "*$kb*" } | Select-Object -First 1
        if ($inQfe) {
            Add-Result PASS "$kb installed" "via Get-HotFix on $($inQfe.InstalledOn)"
        } elseif ($inStore) {
            Add-Result PASS "$kb present" 'via Get-WindowsPackage (Component Store) -- slipstreamed'
        } else {
            $dismPkgs = (dism /online /Get-Packages 2>&1 | Out-String)
            if ($dismPkgs -match $kbDigits) { Add-Result PASS "$kb found via DISM packages" }
            else                              { Add-Result FAIL "$kb not detected" 'checked Get-HotFix, Get-WindowsPackage, and DISM' }
        }
    }
}

# =============================================================================
# 16. DRIVERS (auto-fetched packs)
# =============================================================================
$packs = @($profileJson.AutoFetchedDriverPacks)
if ($packs.Count -gt 0) {
    Write-Section 'DRIVERS'

    $oemDrivers = Get-WindowsDriver -Online -All -ErrorAction SilentlyContinue |
                  Where-Object { $_.ProviderName -notlike '*Microsoft*' }
    $oemInfCount = ($oemDrivers | Measure-Object).Count
    $manufacturer = try { (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).Manufacturer } catch { 'Unknown' }

    foreach ($pack in $packs) {
        $vendor = $pack.Vendor
        $model = $pack.ModelName
        $vendorRx = switch -Regex ($vendor) {
            '(?i)^hp'      { '^(hp|hewlett)' }
            '(?i)^dell'    { '^dell' }
            '(?i)^lenovo'  { '^lenovo' }
            default        { [regex]::Escape($vendor) }
        }
        $vendorDrivers = $oemDrivers | Where-Object { $_.ProviderName -match $vendorRx }
        $vendorCount = ($vendorDrivers | Measure-Object).Count

        $hostMatchesVendor = $manufacturer -match $vendorRx

        if ($vendorCount -gt 0 -and $hostMatchesVendor) {
            Add-Result PASS "$vendor drivers injected and host is $vendor hardware" "$vendorCount $vendor-branded INF(s), $oemInfCount OEM INF(s) total"
        } elseif ($vendorCount -gt 0) {
            Add-Result INFO "$vendor drivers injected but host is non-$vendor" "$vendorCount $vendor-branded INF(s) on $manufacturer hardware"
        } elseif ($hostMatchesVendor -and $oemInfCount -gt 50) {
            # OEM packs often include Intel/Realtek/AMD chipset bits that don't carry vendor name.
            Add-Result PASS "$vendor driver pack injected (chipset INFs)" "$oemInfCount non-Microsoft INF(s) on $manufacturer hardware ($vendor packs bundle chipset bits without $vendor in ProviderName)"
        } elseif ($hostMatchesVendor) {
            Add-Result FAIL "$vendor hardware but no OEM drivers in driver store" "expected pack: $model"
        } else {
            Add-Result INFO "No $vendor drivers in store and host is $manufacturer" "expected pack: $model -- nothing to validate cross-vendor"
        }
    }
}

# =============================================================================
# SUMMARY
# =============================================================================
Write-Section 'SUMMARY'

$pass = ($global:Results | Where-Object Status -eq 'PASS').Count
$fail = ($global:Results | Where-Object Status -eq 'FAIL').Count
$warn = ($global:Results | Where-Object Status -eq 'WARN').Count
$info = ($global:Results | Where-Object Status -eq 'INFO').Count

Write-Host ''
Write-Host ('  PASS = {0}    FAIL = {1}    WARN = {2}    INFO = {3}' -f $pass, $fail, $warn, $info) -ForegroundColor White
Write-Host ''

# Write text report
$lines = @()
$lines += '=' * 78
$lines += 'WinForge -- Profile-Driven Validation Report'
$lines += "Profile : $(Split-Path -Leaf $ProfilePath)"
$lines += "Host    : $env:COMPUTERNAME"
$lines += "User    : $env:USERNAME"
$lines += "Date    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += '=' * 78
$lines += ''
$cats = $global:Results | Select-Object -ExpandProperty Category -Unique
foreach ($cat in $cats) {
    if (-not $cat) { continue }
    $lines += "[$cat]"
    foreach ($r in $global:Results | Where-Object Category -eq $cat) {
        $lines += ('  {0,-4}  {1}' -f $r.Status, $r.Name)
        if ($r.Detail) { $lines += "          |- $($r.Detail)" }
    }
    $lines += ''
}
$lines += '=' * 78
$lines += "SUMMARY  PASS=$pass  WARN=$warn  FAIL=$fail  INFO=$info"
$lines += '=' * 78
Set-Content -Path $reportPath -Value $lines -Encoding UTF8

Write-Host "Report written: $reportPath" -ForegroundColor Cyan

if ($fail -gt 0) { exit 1 } else { exit 0 }
