using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Services;

/// <summary>
/// Reads provisioned Appx packages and optional Windows features directly from
/// an install.wim file using DISM offline servicing commands — no WIM mount
/// is needed, so scanning is fast and non-destructive.
///
/// DISM supports:
///   /Get-ProvisionedAppxPackages /WimFile:"..." /Index:N
///   /Get-Features                /WimFile:"..." /Index:N
/// </summary>
public class ImageScanner
{
    // ── Known-bloat list ─────────────────────────────────────────────────────
    // Package name PREFIXES that are automatically flagged as bloat.
    // Users can uncheck any of them if they want to keep a particular app.
    // If Microsoft adds a new package later, it won't appear here — users will
    // still see it in the dynamic list; they just need to check it manually.
    private static readonly HashSet<string> KnownBloatPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Xbox
        "Microsoft.XboxApp",
        "Microsoft.GamingApp",
        "Microsoft.Xbox.TCUI",
        "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxSpeechToTextOverlay",
        // Teams (consumer)
        "MicrosoftTeams",
        "MSTeams",
        // Office / productivity bloat
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.Todos",
        "Microsoft.OutlookForWindows",
        // Clipchamp
        "Clipchamp.Clipchamp",
        // Bing widgets
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.BingFinance",
        "Microsoft.BingTravel",
        "Microsoft.BingSports",
        // Games
        "Microsoft.MicrosoftSolitaireCollection",
        "king.com",
        // Help / onboarding
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        // Phone / connectivity
        "Microsoft.YourPhone",
        "Microsoft.Link2Windows",
        // Maps
        "Microsoft.WindowsMaps",
        // Social
        "Microsoft.People",
        "Microsoft.SkypeApp",
        // Mixed Reality
        "Microsoft.MixedReality.Portal",
        // Feedback / telemetry UX
        "Microsoft.WindowsFeedbackHub",
        // 3D / legacy
        "Microsoft.Microsoft3DViewer",
        "Microsoft.Print3D",
        // Media
        "Microsoft.ZuneMusic",
        "Microsoft.ZuneVideo",
        // Cortana
        "Microsoft.549981C3F5F10",
        // Quick Assist (support tool — some orgs don't want it)
        "MicrosoftCorporationII.QuickAssist",
    };

    // ── Public API ────────────────────────────────────────────────────────────

    public record ScanResult(
        List<DiscoveredPackage> Packages,
        List<DiscoveredFeature> Features,
        string MountDir);

    /// <summary>
    /// Scans provisioned packages and Windows features from a WIM or ESD file.
    ///
    /// Uses PowerShell's Mount-WindowsImage cmdlet (not dism.exe /Mount-Image)
    /// because the PowerShell cmdlet natively supports both .wim AND .esd formats.
    /// dism.exe /Mount-Image returns exit 87 for ESD files; the PowerShell cmdlet
    /// does not have this limitation.
    ///
    /// Flow:
    ///   1. dism /Cleanup-Mountpoints  — clear any stuck mounts from earlier
    ///   2. Write a temp PowerShell script to %TEMP%
    ///   3. Run it:  Mount-WindowsImage → Get-AppxProvisionedPackage
    ///                                  → Get-WindowsOptionalFeature
    ///                                  → Dismount-WindowsImage -Discard
    ///   4. Parse structured PKG:/FEAT:/STATUS:/ERROR: output lines
    ///   5. Delete temp script
    /// </summary>
    /// <param name="keepMounted">
    /// When <c>true</c> the WIM is left mounted after packages and features are read
    /// so the caller can immediately read Windows\PolicyDefinitions from the live image.
    /// The caller MUST call <see cref="UnmountAsync"/> afterwards.
    /// When <c>false</c> (default) the WIM is unmounted inside the script and progress
    /// goes all the way to 100 — the old behaviour.
    /// </param>
    public static async Task<ScanResult> ScanAsync(
        string wimPath,
        int    wimIndex,
        Action<string>? status = null,
        CancellationToken ct = default,
        bool keepMounted = false)
    {
        if (!File.Exists(wimPath))
            throw new FileNotFoundException($"WIM file not found: {wimPath}");

        // Short fixed path at system drive root — e.g. C:\GIBMount.
        // Deep/long temp paths (like %TEMP%\GIBMount_GUID) cause DISM Error 3.
        // The PS script handles creation and cleanup of this directory.
        var mountDir = Path.Combine(
            Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "GIBMount");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"GIB_Scan_{Guid.NewGuid():N}.ps1");

        // 20-minute hard timeout: mount ~4 min + queries ~1 min + dismount ~7 min = ~12 min typical.
        // The previous 5-minute timeout was killing the process before mount even completed.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveCt = linkedCts.Token;

        await File.WriteAllTextAsync(scriptPath, BuildScanScript(wimPath, wimIndex, keepMounted),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), effectiveCt);

        try
        {
            status?.Invoke("Starting scan (mount takes 3-8 min for a full Win11 WIM)...");

            var packages = new List<DiscoveredPackage>();
            var features = new List<DiscoveredFeature>();
            var errorLines = new System.Text.StringBuilder();

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            using var proc = Process.Start(psi)
                ?? throw new Exception("Failed to start powershell.exe");
            using var killReg = effectiveCt.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            // ── Step 4: parse output lines as they arrive ─────────────────────
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                effectiveCt.ThrowIfCancellationRequested();

                if (line.StartsWith("PROGRESS:", StringComparison.Ordinal))
                {
                    // Forward the full "PROGRESS:N" token — the UI callback parses the number
                    status?.Invoke(line);
                }
                else if (line.StartsWith("STATUS:", StringComparison.Ordinal))
                {
                    status?.Invoke(line[7..]);
                }
                else if (line.StartsWith("PKG:", StringComparison.Ordinal))
                {
                    var parts = line[4..].Split('|');
                    if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        var pkgName = parts[0].Trim();
                        var display = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                            ? parts[1].Trim()
                            : FriendlyFromPackageName(pkgName);
                        packages.Add(new DiscoveredPackage
                        {
                            PackageName  = pkgName,
                            DisplayName  = display,
                            Version      = parts.Length > 2 ? parts[2].Trim() : "",
                            Architecture = parts.Length > 3 ? parts[3].Trim() : "",
                            IsKnownBloat = IsKnownBloat(pkgName),
                        });
                    }
                }
                else if (line.StartsWith("FEAT:", StringComparison.Ordinal))
                {
                    var parts = line[5..].Split('|');
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
                        features.Add(new DiscoveredFeature
                        {
                            FeatureName = parts[0].Trim(),
                            State       = parts[1].Trim(),
                        });
                }
                else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    errorLines.AppendLine(line[6..]);
                }
            }

            await proc.WaitForExitAsync(effectiveCt);

            if (proc.ExitCode != 0 && packages.Count == 0 && features.Count == 0)
                throw new Exception(
                    $"PowerShell scan script failed (exit {proc.ExitCode}).\n\n" +
                    errorLines.ToString().Trim());

            // Sort packages: known bloat first, then alphabetical
            packages = packages
                .OrderByDescending(p => p.IsKnownBloat)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            features = features
                .OrderBy(f => f.FeatureName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ScanResult(packages, features, mountDir);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            // When keepMounted=true the caller owns the mount dir (for ADMX reading).
            // When keepMounted=false the PS script already removed it; this is belt-and-suspenders.
            if (!keepMounted)
                try { Directory.Delete(mountDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Builds the PowerShell scan script.
    ///
    /// PROGRESS TRACKING STRATEGY:
    ///   DISM does not emit percentage output when stdout is redirected (only on a real TTY),
    ///   so file-polling for DISM progress doesn't work.  Instead we use two techniques:
    ///
    ///   a) For the MOUNT phase (WIM):  run dism.exe via Start-Process -PassThru so the PS
    ///      script gets a Process handle.  A while(!$proc.HasExited) loop polls every 800 ms
    ///      and emits PROGRESS:N using a time-based exponential approach curve:
    ///          frac = Min(0.93, 1 − exp(−3·elapsed/budget))
    ///      → fast start, decelerates near end, never gets stuck at 99%.
    ///
    ///   b) For the DISMOUNT phase:  same pattern using Start-Process -PassThru on dism /Unmount.
    ///
    ///   c) For the PACKAGE and FEATURE query phases:  progress is emitted per-item so the bar
    ///      advances as each package/feature is parsed.
    ///
    ///   d) For ESD files:  Mount-WindowsImage is a blocking PS cmdlet that cannot be
    ///      backgrounded without nested process complexity; the bar stays at 5% during the
    ///      mount wait and jumps to 63% when done.  (ESD is far less common than WIM.)
    ///
    /// PROGRESS BUDGET:
    ///   1– 4   Pre-flight cleanup
    ///   5–62   Mount   (~4 min typical, 8 min budget)
    ///  63–73   Package query (~11 s)
    ///  74–84   Feature query (~13 s)
    ///  84–99   Dismount (~7 min typical, 10 min budget)
    ///  100     Done
    ///
    /// Uses plain + concatenation (NOT C# $"..." interpolation) because the PS script
    /// contains $variable and $() subexpressions that clash with C# interpolation syntax.
    /// </summary>
    private static string BuildScanScript(string wimPath, int index, bool keepMounted = false)
    {
        string w = wimPath.Replace("'", "''");  // safe in PS single-quoted string

        return
            "#Requires -RunAsAdministrator\r\n"                                                                 +
            "$ErrorActionPreference = 'SilentlyContinue'\r\n"                                                   +
            "function wl([string]$line) { [Console]::Out.WriteLine($line) }\r\n"                                +
            "\r\n"                                                                                              +
            "$MountDir = \"$($env:SystemDrive)\\GIBMount\"\r\n"                                                 +
           $"$wimFile  = '{w}'\r\n"                                                                              +
           $"$idx      = {index}\r\n"                                                                            +
            "$isEsd    = $wimFile.ToLower().EndsWith('.esd')\r\n"                                                +
            "$dism     = \"$($env:SystemRoot)\\System32\\dism.exe\"\r\n"                                         +
            "$regKey   = 'HKLM:\\SOFTWARE\\Microsoft\\WIMMount\\Mounted Images'\r\n"                            +
            "\r\n"                                                                                              +
            "# ── Pre-flight: kill stuck DISM + delete registry entries (PROGRESS 1→4) ─────────────────────\r\n" +
            "wl 'PROGRESS:1'\r\n"                                                                               +
            "wl 'STATUS:Terminating any stuck DISM processes...'\r\n"                                           +
            "Get-Process -Name dism -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
            "Start-Sleep -Milliseconds 1500\r\n"                                                                +
            // dism /Cleanup-Mountpoints is the proper DISM command to release orphaned mount handles
            // left behind when a previous scan was cancelled mid-mount (e.g. user navigated away).
            "wl 'STATUS:Running DISM cleanup to release any orphaned mount sessions...'\r\n"                    +
            // Run Cleanup-Mountpoints with a 60-second timeout via Start-Process -PassThru.
            // The inline "& $dism /Cleanup-Mountpoints" call blocks indefinitely when a previous
            // scan was killed mid-mount (kernel-level WIM handles can't be released quickly).
            // If cleanup doesn't finish in 60 s we kill it and proceed — the mount phase will
            // create a fresh mount dir anyway, so a partially-stuck old mount won't block us.
            "$cpTmp  = [System.IO.Path]::GetTempFileName()\r\n"                                                 +
            "$cpProc = Start-Process $dism -ArgumentList '/Cleanup-Mountpoints' `\r\n"                          +
            "    -PassThru -NoNewWindow -RedirectStandardOutput $cpTmp -ErrorAction SilentlyContinue\r\n"       +
            "if ($cpProc) {\r\n"                                                                                +
            "    $cpProc.WaitForExit(60000) | Out-Null\r\n"                                                     +
            "    if (-not $cpProc.HasExited) {\r\n"                                                             +
            "        wl 'STATUS:DISM cleanup timed out — continuing anyway...'\r\n"                             +
            "        $cpProc.Kill()\r\n"                                                                        +
            "    }\r\n"                                                                                         +
            "    Remove-Item $cpTmp -ErrorAction SilentlyContinue\r\n"                                          +
            "}\r\n"                                                                                             +
            "wl 'PROGRESS:2'\r\n"                                                                               +
            "wl 'STATUS:Clearing stale DISM mount registry entries...'\r\n"                                     +
            "if (Test-Path $regKey) {\r\n"                                                                      +
            "    Get-ChildItem $regKey -ErrorAction SilentlyContinue |\r\n"                                      +
            "        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue\r\n"                              +
            "}\r\n"                                                                                             +
            "if (Test-Path $MountDir) {\r\n"                                                                    +
            "    Remove-Item $MountDir -Recurse -Force -ErrorAction SilentlyContinue\r\n"                        +
            "}\r\n"                                                                                             +
            "New-Item -ItemType Directory -Path $MountDir -Force | Out-Null\r\n"                                 +
            "wl 'PROGRESS:4'\r\n"                                                                               +
            "\r\n"                                                                                              +
            "$ErrorActionPreference = 'Stop'\r\n"                                                               +
            "try {\r\n"                                                                                         +
            "\r\n"                                                                                              +
            "    # ── MOUNT PHASE (PROGRESS 5→62) ─────────────────────────────────────────────────────────\r\n" +
            "    # WIM:  DISM runs as a background process; we poll HasExited every 800 ms and emit\r\n"         +
            "    #       PROGRESS:N using an exponential-approach curve so the bar always moves forward.\r\n"   +
            "    # ESD:  Mount-WindowsImage is blocking; bar stays at 5% until mount completes.\r\n"            +
            "    if ($isEsd) {\r\n"                                                                             +
            "        wl 'STATUS:Mounting ESD image (4-8 min) -- please wait...'\r\n"                            +
            "        wl 'PROGRESS:5'\r\n"                                                                       +
            "        Mount-WindowsImage -ImagePath $wimFile -Index $idx -Path $MountDir -ReadOnly | Out-Null\r\n" +
            "        wl 'PROGRESS:63'\r\n"                                                                      +
            "    } else {\r\n"                                                                                  +
            "        wl 'STATUS:Mounting WIM image (4-8 min) -- tracking progress live...'\r\n"                 +
            "        $tmpOut    = [System.IO.Path]::GetTempFileName()\r\n"                                      +
            // Run DISM in background; -ArgumentList as single string with embedded quotes
            "        $mountProc = Start-Process $dism `\r\n"                                                    +
            "            -ArgumentList \"/Mount-Image /ImageFile:`\"$wimFile`\" /Index:$idx /MountDir:`\"$MountDir`\" /ReadOnly\" `\r\n" +
            "            -PassThru -NoNewWindow -RedirectStandardOutput $tmpOut -ErrorAction Stop\r\n"           +
            "        wl 'PROGRESS:5'\r\n"                                                                       +
            "        $mountStart  = Get-Date\r\n"                                                               +
            "        $mountBudget = 480.0   # 8-minute budget\r\n"                                              +
            "        while (-not $mountProc.HasExited) {\r\n"                                                   +
            "            $elapsed = ((Get-Date) - $mountStart).TotalSeconds\r\n"                                 +
            "            $frac    = [Math]::Min(0.93, 1 - [Math]::Exp(-3.0 * $elapsed / $mountBudget))\r\n"     +
            "            $pct     = [int](5 + $frac * 57)   # 5 .. 58 (hard cap at 62 below)\r\n"               +
            "            wl \"PROGRESS:$([Math]::Min($pct, 62))\"\r\n"                                          +
            "            Start-Sleep -Milliseconds 800\r\n"                                                     +
            "        }\r\n"                                                                                     +
            "        Remove-Item $tmpOut -ErrorAction SilentlyContinue\r\n"                                      +
            // WaitForExit(ms) ensures ExitCode is populated; Start-Process -PassThru can return
            // a null ExitCode even after HasExited=true on some PS/Windows combinations.
            "        $null = $mountProc.WaitForExit(5000)\r\n"                                                  +
            "        $mountExit = if ($null -ne $mountProc.ExitCode) { [int]$mountProc.ExitCode } else { 0 }\r\n" +
            "        if ($mountExit -ne 0) {\r\n"                                                               +
            "            throw \"DISM mount failed (exit $mountExit)\"\r\n"                                     +
            "        }\r\n"                                                                                     +
            // Belt-and-suspenders: verify the image actually mounted (Windows dir must exist)
            "        if (-not (Test-Path (Join-Path $MountDir 'Windows'))) {\r\n"                               +
            "            throw 'Mount appeared to succeed but image is not accessible (Windows directory missing)'\r\n" +
            "        }\r\n"                                                                                     +
            "        wl 'PROGRESS:63'\r\n"                                                                      +
            "    }\r\n"                                                                                         +
            "\r\n"                                                                                              +
            "    # ── PACKAGE QUERY (PROGRESS 63→73) ─────────────────────────────────────────────────────\r\n" +
            "    wl 'STATUS:Reading provisioned app packages...'\r\n"                                            +
            "    try {\r\n"                                                                                     +
            "        $pkgOut   = & $dism /Image:\"$MountDir\" /Get-ProvisionedAppxPackages 2>&1\r\n"             +
            "        $pkgNames = @($pkgOut | Where-Object { $_ -match '^\\s*PackageName\\s*:' })\r\n"            +
            "        $pkgTotal = [Math]::Max(1, $pkgNames.Count)\r\n"                                           +
            "        $pkgIdx   = 0\r\n"                                                                         +
            "        foreach ($ln in $pkgOut) {\r\n"                                                            +
            "            if ($ln -match '^\\s*PackageName\\s*:\\s*(.+)$') {\r\n"                                 +
            "                $pn = $matches[1].Trim()\r\n"                                                      +
            "                if ($pn -and -not $pn.StartsWith('---')) {\r\n"                                    +
            "                    wl \"PKG:$pn||\"\r\n"                                                          +
            "                    $pkgIdx++\r\n"                                                                 +
            "                    $pct = [int](63 + ($pkgIdx / $pkgTotal) * 10)\r\n"                             +
            "                    wl \"PROGRESS:$([Math]::Min($pct, 73))\"\r\n"                                  +
            "                }\r\n"                                                                             +
            "            }\r\n"                                                                                 +
            "        }\r\n"                                                                                     +
            "    } catch { wl \"ERROR:Package query failed: $_\" }\r\n"                                         +
            "    wl 'PROGRESS:73'\r\n"                                                                          +
            "\r\n"                                                                                              +
            "    # ── FEATURE QUERY (PROGRESS 73→84) ─────────────────────────────────────────────────────\r\n" +
            "    wl 'STATUS:Reading Windows features...'\r\n"                                                    +
            "    try {\r\n"                                                                                     +
            "        $featOut   = & $dism /Image:\"$MountDir\" /Get-Features 2>&1\r\n"                           +
            "        $featTotal = [Math]::Max(1, @($featOut | Where-Object { $_ -match '^\\s*Feature Name' }).Count)\r\n" +
            "        $featName  = $null\r\n"                                                                    +
            "        $featIdx   = 0\r\n"                                                                        +
            "        foreach ($ln in $featOut) {\r\n"                                                           +
            "            if ($ln -match '^\\s*Feature Name\\s*:\\s*(.+)$') {\r\n"                                +
            "                $featName = $matches[1].Trim()\r\n"                                                 +
            "            } elseif ($featName -and $ln -match '^\\s*State\\s*:\\s*(.+)$') {\r\n"                  +
            "                $state = $matches[1].Trim()\r\n"                                                    +
            "                wl \"FEAT:$featName|$state\"\r\n"                                                   +
            "                $featIdx++\r\n"                                                                    +
            "                $pct = [int](73 + ($featIdx / $featTotal) * 11)\r\n"                               +
            "                wl \"PROGRESS:$([Math]::Min($pct, 84))\"\r\n"                                      +
            "                $featName = $null\r\n"                                                             +
            "            }\r\n"                                                                                 +
            "        }\r\n"                                                                                     +
            "    } catch { wl \"ERROR:Features query failed: $_\" }\r\n"                                        +
            "    wl 'PROGRESS:84'\r\n"                                                                          +
            "\r\n"                                                                                              +
            // When keepMounted=true we stop here — the C# caller reads ADMX from the live
            // mount then calls UnmountAsync which drives progress 90→100.
            // When keepMounted=false (legacy) we include the full dismount phase so progress
            // goes to 100 inside the script exactly as before.
            (keepMounted
                ? "    wl 'STATUS:Packages and features scanned.'\r\n"
                : "    # ── DISMOUNT PHASE (PROGRESS 84→99) ──────────────────────────────────────────────────────\r\n" +
                  "    # Same background-process + exponential-approach pattern as the mount phase.\r\n"               +
                  "    wl 'STATUS:Unmounting image (5-8 min) -- tracking progress live...'\r\n"                        +
                  "    $ErrorActionPreference = 'SilentlyContinue'\r\n"                                                +
                  "    $tmpOut2     = [System.IO.Path]::GetTempFileName()\r\n"                                         +
                  "    $unmountProc = Start-Process $dism `\r\n"                                                       +
                  "        -ArgumentList \"/Unmount-Image /MountDir:`\"$MountDir`\" /Discard\" `\r\n"                   +
                  "        -PassThru -NoNewWindow -RedirectStandardOutput $tmpOut2 -ErrorAction SilentlyContinue\r\n"  +
                  "    if ($unmountProc) {\r\n"                                                                        +
                  "        $umStart  = Get-Date\r\n"                                                                   +
                  "        $umBudget = 600.0   # 10-minute budget\r\n"                                                 +
                  "        while (-not $unmountProc.HasExited) {\r\n"                                                  +
                  "            $elapsed = ((Get-Date) - $umStart).TotalSeconds\r\n"                                    +
                  "            $frac    = [Math]::Min(0.94, 1 - [Math]::Exp(-3.0 * $elapsed / $umBudget))\r\n"        +
                  "            $pct     = [int](84 + $frac * 15)   # 84 .. 98\r\n"                                    +
                  "            wl \"PROGRESS:$([Math]::Min($pct, 99))\"\r\n"                                          +
                  "            Start-Sleep -Milliseconds 1000\r\n"                                                    +
                  "        }\r\n"                                                                                     +
                  "        Remove-Item $tmpOut2 -ErrorAction SilentlyContinue\r\n"                                     +
                  "        $null = $unmountProc.WaitForExit(5000)\r\n"                                                +
                  "        $unmountExit = if ($null -ne $unmountProc.ExitCode) { [int]$unmountProc.ExitCode } else { 0 }\r\n" +
                  "        if ($unmountExit -ne 0) {\r\n"                                                             +
                  "            wl \"STATUS:Dismount returned $unmountExit -- clearing registry entries...\"\r\n"      +
                  "            if (Test-Path $regKey) {\r\n"                                                          +
                  "                Get-ChildItem $regKey -ErrorAction SilentlyContinue |\r\n"                          +
                  "                    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue\r\n"                  +
                  "            }\r\n"                                                                                  +
                  "        }\r\n"                                                                                     +
                  "    }\r\n"                                                                                         +
                  "    Remove-Item $MountDir -Recurse -Force -ErrorAction SilentlyContinue\r\n"                        +
                  "    wl 'PROGRESS:100'\r\n"                                                                         +
                  "    wl 'STATUS:Scan complete.'\r\n"
            ) +
            "}\r\n"                                                                                             +
            "catch {\r\n"                                                                                       +
            "    wl \"ERROR:$_\"\r\n"                                                                          +
            "    $ErrorActionPreference = 'SilentlyContinue'\r\n"                                               +
            "    & $dism /Unmount-Image /MountDir:\"$MountDir\" /Discard 2>&1 | Out-Null\r\n"                    +
            "    if (Test-Path $regKey) {\r\n"                                                                  +
            "        Get-ChildItem $regKey -ErrorAction SilentlyContinue |\r\n"                                  +
            "            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue\r\n"                          +
            "    }\r\n"                                                                                         +
            "    Remove-Item $MountDir -Recurse -Force -ErrorAction SilentlyContinue\r\n"                        +
            "    exit 1\r\n"                                                                                    +
            "}\r\n";
    }

    // ── Unmount (called by Step3Page after ADMX read in single-mount flow) ──────

    /// <summary>
    /// Unmounts the WIM that was left mounted by <see cref="ScanAsync"/> when
    /// <c>keepMounted = true</c>.  Reports progress via the same STATUS:/PROGRESS:N
    /// format the caller already uses, in the range 90–100.
    /// </summary>
    public static async Task UnmountAsync(
        string mountDir,
        Action<string>? status,
        CancellationToken ct)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(), $"GIB_Unmount_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            BuildUnmountScript(mountDir),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            ct);

        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var proc = Process.Start(psi)
                ?? throw new Exception("Failed to start powershell.exe for unmount");
            using var _ = linked.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                status?.Invoke(line);

            await proc.WaitForExitAsync(linked.Token);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            // Belt-and-suspenders: delete mount dir if PS script left it behind
            try { Directory.Delete(mountDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Builds the small PowerShell script that unmounts the WIM and reports
    /// PROGRESS:N (90→100) + STATUS: messages to stdout.
    /// </summary>
    private static string BuildUnmountScript(string mountDir)
    {
        string m = mountDir.Replace("'", "''");   // safe in PS single-quoted string

        return
            "function wl([string]$line) { [Console]::Out.WriteLine($line) }\r\n"      +
            "$ErrorActionPreference = 'SilentlyContinue'\r\n"                          +
           $"$MountDir = '{m}'\r\n"                                                     +
            "$dism     = \"$($env:SystemRoot)\\System32\\dism.exe\"\r\n"                +
            "$regKey   = 'HKLM:\\SOFTWARE\\Microsoft\\WIMMount\\Mounted Images'\r\n"   +
            "wl 'PROGRESS:90'\r\n"                                                      +
            "wl 'STATUS:Unmounting image (5-8 min) — tracking progress live…'\r\n"    +
            "$tmpOut      = [System.IO.Path]::GetTempFileName()\r\n"                    +
            "$unmountProc = Start-Process $dism `\r\n"                                  +
            "    -ArgumentList \"/Unmount-Image /MountDir:`\"$MountDir`\" /Discard\" `\r\n" +
            "    -PassThru -NoNewWindow -RedirectStandardOutput $tmpOut -ErrorAction SilentlyContinue\r\n" +
            "if ($unmountProc) {\r\n"                                                   +
            "    $umStart  = Get-Date\r\n"                                              +
            "    $umBudget = 600.0\r\n"                                                 +
            "    while (-not $unmountProc.HasExited) {\r\n"                             +
            "        $elapsed = ((Get-Date) - $umStart).TotalSeconds\r\n"               +
            "        $frac    = [Math]::Min(0.94, 1 - [Math]::Exp(-3.0 * $elapsed / $umBudget))\r\n" +
            "        $pct     = [int](90 + $frac * 9)   # 90..98\r\n"                  +
            "        wl \"PROGRESS:$([Math]::Min($pct, 99))\"\r\n"                      +
            "        Start-Sleep -Milliseconds 1000\r\n"                                +
            "    }\r\n"                                                                 +
            "    Remove-Item $tmpOut -ErrorAction SilentlyContinue\r\n"                 +
            "    $null = $unmountProc.WaitForExit(5000)\r\n"                            +
            "    $unmountExit = if ($null -ne $unmountProc.ExitCode) { [int]$unmountProc.ExitCode } else { 0 }\r\n" +
            "    if ($unmountExit -ne 0) {\r\n"                                         +
            "        wl \"STATUS:Dismount returned $unmountExit — clearing registry entries…\"\r\n" +
            "        if (Test-Path $regKey) {\r\n"                                      +
            "            Get-ChildItem $regKey -ErrorAction SilentlyContinue |\r\n"     +
            "                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "        }\r\n"                                                             +
            "    }\r\n"                                                                 +
            "}\r\n"                                                                     +
            "Remove-Item $MountDir -Recurse -Force -ErrorAction SilentlyContinue\r\n"  +
            "wl 'PROGRESS:100'\r\n"                                                     +
            "wl 'STATUS:Unmount complete.'\r\n";
    }

    // ── Parsers ────────────────────────────────────────────────────────────────

    private static List<DiscoveredPackage> ParsePackages(string output)
    {
        var result = new List<DiscoveredPackage>();

        // Split output into per-package blocks separated by blank lines
        var blocks = Regex.Split(output, @"\r?\n(?:\r?\n)+")
            .Where(b => b.Contains("PackageName", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var block in blocks)
        {
            var pkgName = PickField(block, "PackageName");
            if (string.IsNullOrEmpty(pkgName)) continue;

            var displayName = PickField(block, "DisplayName");
            if (string.IsNullOrEmpty(displayName))
                displayName = FriendlyFromPackageName(pkgName);

            result.Add(new DiscoveredPackage
            {
                PackageName  = pkgName,
                DisplayName  = displayName,
                Version      = PickField(block, "Version"),
                Architecture = PickField(block, "Architecture"),
                IsKnownBloat = IsKnownBloat(pkgName),
            });
        }

        // Sort: known bloat first (so they appear pre-checked at top), then alpha by display name
        return result
            .OrderByDescending(p => p.IsKnownBloat)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DiscoveredFeature> ParseFeatures(string output)
    {
        var result = new List<DiscoveredFeature>();

        var nameMatches = Regex.Matches(output, @"Feature Name\s*:\s*(.+)", RegexOptions.IgnoreCase);
        foreach (Match m in nameMatches)
        {
            var featureName = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(featureName)) continue;

            // Find the "State" line in the block immediately after this feature name
            int start = m.Index + m.Length;
            int nextFeature = output.IndexOf("Feature Name", start, StringComparison.OrdinalIgnoreCase);
            string block = nextFeature > 0 ? output[start..nextFeature] : output[start..];

            var stateMatch = Regex.Match(block, @"State\s*:\s*(.+)", RegexOptions.IgnoreCase);
            string state = stateMatch.Success ? stateMatch.Groups[1].Value.Trim() : "Unknown";

            result.Add(new DiscoveredFeature { FeatureName = featureName, State = state });
        }

        return result.OrderBy(f => f.FeatureName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string PickField(string block, string fieldName)
    {
        var m = Regex.Match(block, $@"^{Regex.Escape(fieldName)}\s*:\s*(.+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string FriendlyFromPackageName(string packageName)
    {
        // "Microsoft.WindowsNotepad_11.0_neutral_~_8wekyb3d8bbwe" → "Windows Notepad"
        var bare = packageName.Split('_')[0];
        foreach (var prefix in new[] { "Microsoft.", "MicrosoftCorporationII.", "MSTeams" })
            if (bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                bare = bare[prefix.Length..];
                break;
            }
        // Insert space before capitals that follow lowercase: "WindowsNotepad" → "Windows Notepad"
        return Regex.Replace(bare, @"(?<=[a-z])(?=[A-Z])", " ");
    }

    public static bool IsKnownBloat(string packageName) =>
        KnownBloatPrefixes.Any(p =>
            packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the install.wim / install.esd path given the mounted ISO drive letter.
    /// </summary>
    public static string? FindWimInDrive(string driveLetter)
    {
        var sources = Path.Combine(driveLetter.TrimEnd('\\') + "\\", "sources");
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var p = Path.Combine(sources, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string DismPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    private static async Task<(string stdout, string stderr, int exit)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");
        using var _ = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } });
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        return (await outTask, await errTask, proc.ExitCode);
    }
}
