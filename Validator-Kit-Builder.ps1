<#
.SYNOPSIS
    GUI tool that packages the universal validator + a chosen .gibprofile into
    a portable Validation Kit folder you can USB-stick to a deployed laptop.

.DESCRIPTION
    Workflow:
      1. Pick a .gibprofile file
      2. Pick an output folder (or accept the default)
      3. Click "Generate Kit"
      4. Copy the produced folder to the deployed laptop, double-click
         Run-Validator.bat (it auto-elevates and launches the validator
         against the bundled profile)

    The kit contains:
      - Validate-GoldenImage-Auto.ps1   (the universal validator)
      - <profile-name>.gibprofile        (the profile, copied)
      - Run-Validator.bat                (elevated launcher)
      - README.txt                       (how to use)

.NOTES
    Run on the workstation that has the build engine. No admin required to
    generate the kit (admin is only needed on the target laptop when running
    the validator).

    Requires:
      - Validate-GoldenImage-Auto.ps1 to exist in the same folder as this
        script (or in C:\Users\pgs6718\Downloads\ISO\).
#>

# Resolve script directory whether run as a file or pasted into a console
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $scriptDir) { $scriptDir = 'C:\Users\pgs6718\Downloads\ISO' }

# Locate the validator script we will copy into each kit
$validatorSource = Join-Path $scriptDir 'Validate-GoldenImage-Auto.ps1'
if (-not (Test-Path $validatorSource)) {
    $alt = 'C:\Users\pgs6718\Downloads\ISO\Validate-GoldenImage-Auto.ps1'
    if (Test-Path $alt) { $validatorSource = $alt }
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ALE Validation Kit Builder"
        Width="640" Height="430"
        WindowStartupLocation="CenterScreen"
        Background="#1B2330" Foreground="#E6EDF7"
        FontFamily="Segoe UI" FontSize="13"
        ResizeMode="CanMinimize">
  <Window.Resources>
    <Style TargetType="Button">
      <Setter Property="Background" Value="#2D6CDF"/>
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Padding" Value="14,6"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Cursor" Value="Hand"/>
    </Style>
    <Style TargetType="TextBox">
      <Setter Property="Background" Value="#0F1722"/>
      <Setter Property="Foreground" Value="#E6EDF7"/>
      <Setter Property="BorderBrush" Value="#2A3850"/>
      <Setter Property="Padding" Value="6,4"/>
      <Setter Property="CaretBrush" Value="#E6EDF7"/>
    </Style>
    <Style TargetType="TextBlock">
      <Setter Property="Foreground" Value="#E6EDF7"/>
    </Style>
  </Window.Resources>

  <Grid Margin="18">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="16"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="6"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="14"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="6"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="20"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>

    <StackPanel Grid.Row="0">
      <TextBlock Text="ALE Validation Kit Builder" FontSize="20" FontWeight="Bold"/>
      <TextBlock Text="Bundle the universal validator with a profile so it can run on a deployed laptop."
                 Opacity="0.7" Margin="0,4,0,0"/>
    </StackPanel>

    <TextBlock Grid.Row="2" Text="1. Build profile (.gibprofile)" FontWeight="SemiBold"/>
    <Grid Grid.Row="4">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="8"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>
      <TextBox x:Name="ProfileBox" Grid.Column="0"/>
      <Button  x:Name="BrowseProfileBtn" Grid.Column="2" Content="Browse..."/>
    </Grid>

    <TextBlock Grid.Row="6" Text="2. Output folder (kit will be created here)" FontWeight="SemiBold"/>
    <Grid Grid.Row="8">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="8"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>
      <TextBox x:Name="OutputBox" Grid.Column="0"/>
      <Button  x:Name="BrowseOutputBtn" Grid.Column="2" Content="Browse..."/>
    </Grid>

    <StackPanel Grid.Row="10" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button x:Name="GenerateBtn" Content="Generate Kit" Background="#2EA043" Padding="22,8" FontSize="14"/>
    </StackPanel>

    <Border Grid.Row="11" Margin="0,18,0,0" Background="#0F1722" BorderBrush="#2A3850" BorderThickness="1" CornerRadius="3" Padding="10">
      <ScrollViewer x:Name="LogScroll" VerticalScrollBarVisibility="Auto">
        <TextBlock x:Name="LogBlock" FontFamily="Consolas" FontSize="12" TextWrapping="Wrap" Foreground="#A8B5C8"
                   Text="Ready. Pick a .gibprofile and an output folder, then click Generate Kit."/>
      </ScrollViewer>
    </Border>
  </Grid>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)

$profileBox       = $window.FindName('ProfileBox')
$outputBox        = $window.FindName('OutputBox')
$browseProfileBtn = $window.FindName('BrowseProfileBtn')
$browseOutputBtn  = $window.FindName('BrowseOutputBtn')
$generateBtn      = $window.FindName('GenerateBtn')
$logBlock         = $window.FindName('LogBlock')
$logScroll        = $window.FindName('LogScroll')

# Default output folder
$defaultOut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ALE-Validation-Kit'
$outputBox.Text = $defaultOut

function Write-Log {
    param([string]$msg, [string]$colour = '#A8B5C8')
    $stamp = (Get-Date -Format 'HH:mm:ss')
    $logBlock.Text = "$($logBlock.Text)`r`n[$stamp] $msg"
    $logBlock.Foreground = [System.Windows.Media.Brushes]::LightGray
    $logScroll.ScrollToBottom()
    [System.Windows.Forms.Application]::DoEvents()
}

$browseProfileBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'GIB Profile (*.gibprofile)|*.gibprofile|All files (*.*)|*.*'
    $dlg.InitialDirectory = if ($profileBox.Text -and (Test-Path (Split-Path $profileBox.Text -Parent))) {
        Split-Path $profileBox.Text -Parent
    } else { 'C:\Users\pgs6718\Downloads\ISO' }
    if ($dlg.ShowDialog() -eq 'OK') {
        $profileBox.Text = $dlg.FileName
        Write-Log "Selected profile: $($dlg.FileName)"
    }
})

$browseOutputBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Choose a folder to create the validation kit in'
    $dlg.SelectedPath = if (Test-Path $outputBox.Text) { $outputBox.Text } else { [Environment]::GetFolderPath('Desktop') }
    if ($dlg.ShowDialog() -eq 'OK') {
        $outputBox.Text = $dlg.SelectedPath
        Write-Log "Selected output folder: $($dlg.SelectedPath)"
    }
})

$generateBtn.Add_Click({
    $profile = $profileBox.Text.Trim('"').Trim()
    $output  = $outputBox.Text.Trim('"').Trim()

    if (-not $profile -or -not (Test-Path $profile)) {
        Write-Log 'ERROR: pick a valid .gibprofile file first.'
        return
    }
    if (-not $output) {
        Write-Log 'ERROR: pick an output folder.'
        return
    }
    if (-not (Test-Path $validatorSource)) {
        Write-Log "ERROR: cannot find Validate-GoldenImage-Auto.ps1 (looked at $validatorSource). Place it next to this script."
        return
    }

    # Sanity-check the profile parses as JSON
    try {
        $parsed = Get-Content -Path $profile -Raw | ConvertFrom-Json
        $profileName = if ($parsed.OutputFilename) { $parsed.OutputFilename } else { [System.IO.Path]::GetFileNameWithoutExtension($profile) }
        Write-Log "Profile parsed OK: $profileName"
    } catch {
        Write-Log "ERROR: profile is not valid JSON: $($_.Exception.Message)"
        return
    }

    $generateBtn.IsEnabled = $false
    try {
        # Each kit goes in its own subfolder so multiple kits can live in one parent.
        $stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
        $kitName = "ValidationKit-$([System.IO.Path]::GetFileNameWithoutExtension($profile))-$stamp"
        $kitDir  = Join-Path $output $kitName

        if (-not (Test-Path $kitDir)) {
            New-Item -ItemType Directory -Path $kitDir -Force | Out-Null
        }
        Write-Log "Kit folder: $kitDir"

        # 1. Copy validator
        $validatorTarget = Join-Path $kitDir 'Validate-GoldenImage-Auto.ps1'
        Copy-Item -Path $validatorSource -Destination $validatorTarget -Force
        Write-Log '  + Validate-GoldenImage-Auto.ps1'

        # 2. Copy profile (preserve original filename)
        $profileFileName = Split-Path -Leaf $profile
        $profileTarget   = Join-Path $kitDir $profileFileName
        Copy-Item -Path $profile -Destination $profileTarget -Force
        Write-Log "  + $profileFileName"

        # 3. Write Run-Validator.bat (elevates + runs PS validator)
        $batPath = Join-Path $kitDir 'Run-Validator.bat'
        $bat = @"
@echo off
setlocal
cd /d "%~dp0"

REM Self-elevate if not already admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c','""%~f0""' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File ".\Validate-GoldenImage-Auto.ps1" -ProfilePath ".\$profileFileName"

echo.
echo ====================================================
echo  Validation complete. Report saved under C:\ALE-Validator\
echo ====================================================
pause
"@
        Set-Content -Path $batPath -Value $bat -Encoding ASCII
        Write-Log '  + Run-Validator.bat'

        # 4. Write README
        $readmePath = Join-Path $kitDir 'README.txt'
        $readme = @"
ALE Validation Kit
==================

Profile : $profileFileName
Built   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Host    : $env:COMPUTERNAME

How to use this kit on a deployed laptop:
-----------------------------------------

1. Copy this entire folder to the deployed laptop (USB stick, network share, etc.)

2. Double-click Run-Validator.bat
   - It will request administrator privileges (UAC prompt).
   - It will run the universal validator against $profileFileName.
   - All output is colour-coded:
       PASS = green       FAIL = red
       WARN = yellow      INFO = grey

3. A text report is saved to:
       C:\ALE-Validator\ValidationReport_<timestamp>.txt

4. Press any key in the console window to exit when done.

What this validator checks (auto-derived from the profile):
-----------------------------------------------------------
   - Hostname template
   - Administrator account flags (enabled, PasswordNeverExpires)
   - AutoLogon registry keys
   - Organization
   - TimeZone, Power plan
   - Bloatware removal
   - Optional features (enabled/disabled)
   - BitLocker + recovery key file
   - Defender, Telemetry
   - Dark mode, file extensions, wallpaper, lock screen
   - Win11 UX baseline (Copilot, Recall, Widgets, Chat, Consumer Features)
   - OneDrive uninstall + OEM staging copy deletion
   - Trusted certificates (Root / CA / TrustedPublisher)
   - Custom fonts (file + registry)
   - Group policies
   - Language packs
   - Staged apps installed by GIBFirstBoot
   - Deployment scripts + Startup triggers
   - Public desktop / staged files
   - Slipstreamed Windows updates (Get-WindowsPackage + Get-HotFix + DISM)
   - Auto-fetched OEM driver packs (HP / Dell / Lenovo)

The validator only checks what the profile asked for -- nothing else.

Re-run any time after each build to re-validate.
"@
        Set-Content -Path $readmePath -Value $readme -Encoding UTF8
        Write-Log '  + README.txt'

        $kitSize = '{0:N1} KB' -f (((Get-ChildItem $kitDir -Recurse | Measure-Object -Property Length -Sum).Sum) / 1KB)
        Write-Log ''
        Write-Log "SUCCESS: kit ready at $kitDir ($kitSize)" '#2EA043'
        Write-Log 'Next: copy that folder to the deployed laptop and double-click Run-Validator.bat.'

        # Open the folder in Explorer
        Start-Process explorer.exe -ArgumentList $kitDir | Out-Null
    } catch {
        Write-Log "ERROR: $($_.Exception.Message)"
    } finally {
        $generateBtn.IsEnabled = $true
    }
})

[void]$window.ShowDialog()
