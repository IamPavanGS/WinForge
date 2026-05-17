<#
.SYNOPSIS
    Compiles the ALE Validation Kit Builder + Validate-GoldenImage-Auto.ps1
    into a single self-contained EXE (ALEValidatorBuilder.exe).

.DESCRIPTION
    1. Installs ps2exe from PSGallery if not already present (CurrentUser scope).
    2. Reads Validate-GoldenImage-Auto.ps1 and base64-encodes it so it can be
       embedded verbatim inside the Kit Builder script.
    3. Transforms the Kit Builder to write the embedded validator instead of
       copying it from disk (removes the file-lookup dependency entirely).
    4. Compiles the combined PS1 to a WPF-capable EXE via ps2exe.

.NOTES
    - Run in Windows PowerShell 5.1 (not PowerShell 7) — WPF requires full .NET
      Framework and PresentationFramework.
    - Admin NOT required to build; admin IS required to install ps2exe system-wide
      but we use -Scope CurrentUser so it always works without elevation.
    - Output: ALEValidatorBuilder.exe in the same folder as this script.
#>

$ErrorActionPreference = 'Stop'

$validatorPath  = Join-Path $PSScriptRoot 'Validate-GoldenImage-Auto.ps1'
$kitBuilderPath = 'C:\Users\pgs6718\Downloads\ALE ISO Creator\Validator-Kit-Builder.ps1'
$iconPath       = 'C:\Users\pgs6718\Downloads\ALE ISO Creator\GoldenISOBuilder\Resources\app.ico'
$outputExe      = Join-Path $PSScriptRoot 'ALEValidatorBuilder.exe'

Write-Host ''
Write-Host '=== ALE Validator EXE Builder ===' -ForegroundColor Cyan
Write-Host ''

# ── 1. Ensure ps2exe is available ─────────────────────────────────────────────
if (-not (Get-Command Invoke-PS2EXE -ErrorAction SilentlyContinue)) {
    Write-Host '[1/4] Installing ps2exe from PSGallery (CurrentUser)...' -ForegroundColor Yellow
    Install-Module ps2exe -Scope CurrentUser -Force -AllowClobber
    Import-Module ps2exe -Force
    Write-Host '      ps2exe installed.' -ForegroundColor Green
} else {
    Write-Host '[1/4] ps2exe already installed.' -ForegroundColor Green
}

# ── 2. Read both source files ─────────────────────────────────────────────────
Write-Host '[2/4] Reading source files...'
foreach ($f in @($validatorPath, $kitBuilderPath)) {
    if (-not (Test-Path $f)) { throw "File not found: $f" }
}

# Normalise to LF so all pattern matches are consistent
$validatorContent = ([System.IO.File]::ReadAllText($validatorPath,  [System.Text.Encoding]::UTF8)) -replace "`r`n", "`n"
$kb               = ([System.IO.File]::ReadAllText($kitBuilderPath, [System.Text.Encoding]::UTF8)) -replace "`r`n", "`n"

Write-Host "      Validator  : $validatorPath  ($([int]($validatorContent.Length/1KB)) KB)"
Write-Host "      Kit Builder: $kitBuilderPath  ($([int]($kb.Length/1KB)) KB)"

# Base64-encode the validator (base64 is safe inside a PS1 single-quoted string)
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($validatorContent))
Write-Host "      Encoded validator: $($b64.Length) chars"

# ── 3. Transform the Kit Builder (3 targeted changes) ─────────────────────────
Write-Host '[3/4] Transforming Kit Builder...'

$lines  = $kb -split "`n"
$out    = [System.Collections.Generic.List[string]]::new()
$i      = 0
$changes = 0

while ($i -lt $lines.Count) {
    $line = $lines[$i]

    # ── Change A: Remove the $validatorSource file-lookup block ──────────────
    # Matches the comment line that begins the block:
    #   # Locate the validator script we will copy into each kit
    #   $validatorSource = Join-Path $scriptDir 'Validate-GoldenImage-Auto.ps1'
    #   if (-not (Test-Path $validatorSource)) {
    #       $alt = '...'
    #       if (Test-Path $alt) { $validatorSource = $alt }
    #   }          <- 6 lines total
    if ($line.Trim() -eq '# Locate the validator script we will copy into each kit') {
        $out.Add('# Validator content is embedded in this EXE (see $_ValidatorContent).')
        $i += 6   # skip the comment + 5 code lines
        $changes++
        continue
    }

    # ── Change B: Remove the "cannot find" early-exit guard ──────────────────
    # The block is exactly:
    #     if (-not (Test-Path $validatorSource)) {       <- line i
    #         Write-Log "ERROR: cannot find ..."         <- line i+1 (unique text)
    #         return                                     <- line i+2
    #     }                                              <- line i+3
    if ($line -match '\(-not \(Test-Path \$validatorSource\)\)' -and
        $i + 1 -lt $lines.Count -and
        $lines[$i + 1] -match 'cannot find Validate-GoldenImage-Auto') {
        $i += 4   # skip all 4 lines of this block
        $changes++
        continue
    }

    # ── Change C: Replace Copy-Item with embedded-content write ──────────────
    if ($line -match 'Copy-Item -Path \$validatorSource -Destination \$validatorTarget') {
        $out.Add("        [System.IO.File]::WriteAllText(`$validatorTarget, `$_ValidatorContent, [System.Text.Encoding]::new(65001))")
        $i++
        $changes++
        continue
    }

    $out.Add($line)
    $i++
}

if ($changes -ne 3) {
    Write-Warning "Expected 3 transformations but applied $changes. The Kit Builder source may have changed. Review the combined PS1 before distributing the EXE."
} else {
    Write-Host '      All 3 transformations applied.' -ForegroundColor Green
}

# Prepend preamble that defines the embedded validator
$preamble = @"
#Requires -Version 5.1
# ALE Validation Kit Builder -- self-contained EXE, validator embedded at build time.
# Generated by Build-ValidatorEXE.ps1 -- do not edit manually.
`$_ValidatorB64     = '$b64'
`$_ValidatorContent = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(`$_ValidatorB64))

"@

$combined = $preamble + ($out -join "`n")

# Write to a temp PS1
$tmpPs1 = Join-Path $env:TEMP 'ALEValidatorBuilder_combined.ps1'
[System.IO.File]::WriteAllText($tmpPs1, $combined, [System.Text.Encoding]::UTF8)
Write-Host "      Temp combined PS1: $tmpPs1 ($([int]($combined.Length/1KB)) KB)"

# ── 4. Compile to EXE via ps2exe ──────────────────────────────────────────────
Write-Host '[4/4] Compiling to EXE...' -ForegroundColor Cyan

$ps2Args = @{
    inputFile    = $tmpPs1
    outputFile   = $outputExe
    noConsole    = $true          # WPF GUI — no console window
    sta          = $true          # WPF requires single-threaded apartment
    requireAdmin = $false         # Building the kit needs no admin; the kit's Run-Validator.bat handles elevation
    title        = 'WinForge Validator Builder'
    description  = 'WinForge — Validation Kit Builder'
    company      = 'Pavan G S'
    product      = 'WinForge'
    version      = '2.4.8.0'
}
if (Test-Path $iconPath) {
    $ps2Args['iconFile'] = $iconPath
    Write-Host "      Using icon: $iconPath"
}

Invoke-PS2EXE @ps2Args

# Cleanup temp file
Remove-Item $tmpPs1 -ErrorAction SilentlyContinue

# ── Result ────────────────────────────────────────────────────────────────────
if (Test-Path $outputExe) {
    $sizeMB = '{0:N1} MB' -f ((Get-Item $outputExe).Length / 1MB)
    Write-Host ''
    Write-Host "SUCCESS  -->  $outputExe  ($sizeMB)" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Double-click ALEValidatorBuilder.exe to launch the Kit Builder GUI.' -ForegroundColor Cyan
    Write-Host 'The GUI will let you pick a .gibprofile and generate a kit folder.'
    Write-Host 'The kit folder contains the validator + Run-Validator.bat (copy to deployed laptop).'
    Write-Host ''
} else {
    Write-Host ''
    Write-Host 'FAILED: EXE was not produced. Check the errors above.' -ForegroundColor Red
    Write-Host 'Common causes:'
    Write-Host '  - ps2exe requires Windows PowerShell 5.1 (not PowerShell 7)'
    Write-Host '  - Antivirus blocking EXE creation in %TEMP%'
    Write-Host '  - Kit Builder source changed (transformation count was not 3)'
    Write-Host ''
    exit 1
}
