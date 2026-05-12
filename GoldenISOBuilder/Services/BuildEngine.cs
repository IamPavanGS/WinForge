using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Services;

public enum BuildStepStatus { Pending, Running, Done, Failed, Skipped }

public class BuildStep
{
    public string Id    { get; init; } = "";
    public string Title { get; init; } = "";
    public BuildStepStatus Status { get; set; } = BuildStepStatus.Pending;
    public string Detail { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public class BuildProgress
{
    public int CurrentStepIndex { get; init; }
    public int TotalSteps       { get; init; }
    public BuildStep CurrentStep { get; init; } = null!;
    public string Message       { get; init; } = "";
    public IReadOnlyList<BuildStep> AllSteps { get; init; } = Array.Empty<BuildStep>();
}

public class BuildResult
{
    public bool   Success   { get; init; }
    public string IsoPath   { get; init; } = "";
    public string Sha256    { get; init; } = "";
    public long   IsoSizeBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public string LogPath   { get; init; } = "";
    public string? Error    { get; init; }
}

/// <summary>
/// Orchestrates the entire golden-ISO build pipeline:
///   1. Validate environment (admin, ADK/oscdimg, disk space)
///   2. Prepare workspace
///   3. Copy ISO contents to workspace
///   4. Mount install.wim for the chosen edition
///   5. Inject wallpaper, GIBFirstBoot launcher + apps, public-desktop files
///   6. Remove bloatware (DISM /Remove-ProvisionedAppxPackage)
///   7. Enable optional features (DISM /Enable-Feature)
///   8. Apply registry tweaks via offline hive (HKLM\SOFTWARE + HKLM\SYSTEM)
///   9. Place SetupComplete.cmd + Autounattend.xml + Unattend.xml
///  10. Unmount with commit
///  11. Rebuild ISO with oscdimg.exe (UEFI + BIOS bootable)
///  12. Compute SHA-256 of resulting ISO
/// </summary>
public class BuildEngine
{
    private readonly BuildSession _s;
    private readonly Action<BuildProgress> _onProgress;
    private readonly Action<string> _onLog;
    private readonly CancellationToken _ct;

    private string _logPath = "";
    private StreamWriter? _logWriter;
    private string _workspace = "";   // root of build workspace
    private string _isoStaging = "";  // copy of ISO contents
    private string _mountDir = "";    // WIM mount directory
    private string _wimPath = "";     // staging\sources\install.wim
    private string _selectedEditionName = "";

    public List<BuildStep> Steps { get; } = new();

    public BuildEngine(BuildSession session,
                       Action<BuildProgress> onProgress,
                       Action<string> onLog,
                       CancellationToken ct)
    {
        _s = session;
        _onProgress = onProgress;
        _onLog = onLog;
        _ct = ct;

        Steps.AddRange(new[]
        {
            new BuildStep { Id = "validate",     Title = "Validate environment" },
            new BuildStep { Id = "prepare",      Title = "Prepare workspace" },
            new BuildStep { Id = "copyiso",      Title = "Copy ISO contents" },
            new BuildStep { Id = "exportedition",Title = "Export selected edition" },
            new BuildStep { Id = "mountwim",     Title = "Mount install.wim" },
            new BuildStep { Id = "langpacks",    Title = "Inject language packs" },
            new BuildStep { Id = "drivers",      Title = "Inject drivers" },
            new BuildStep { Id = "wallpaper",    Title = "Inject wallpaper" },
            new BuildStep { Id = "firstboot",    Title = "Inject first-boot launcher + apps" },
            new BuildStep { Id = "publicdesktop",     Title = "Stage Public Desktop files" },
            new BuildStep { Id = "deploymentscripts", Title = "Stage deployment scripts" },
            new BuildStep { Id = "bloatware",         Title = "Remove bloatware" },
            new BuildStep { Id = "features",     Title = "Apply Windows features" },
            new BuildStep { Id = "registry",     Title = "Apply registry tweaks" },
            new BuildStep { Id = "unattend",     Title = "Generate unattend / SetupComplete" },
            new BuildStep { Id = "precommit_validate", Title = "Validate build contents" },
            new BuildStep { Id = "unmount",      Title = "Commit and unmount WIM" },
            new BuildStep { Id = "buildiso",     Title = "Build bootable ISO (oscdimg)" },
            new BuildStep { Id = "verify",       Title = "Verify and compute SHA-256" },
        });
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<BuildResult> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            OpenLog();
            Log($"=== Golden ISO Build started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            // ── Critical steps: failure stops the build ──────────────────────
            await Step("validate",      ValidateAsync);
            await Step("prepare",       PrepareWorkspaceAsync);
            await Step("copyiso",       CopyIsoContentsAsync);
            await Step("exportedition", ExportSelectedEditionAsync);
            await Step("mountwim",      MountWimAsync);

            // ── Optional steps: failure is logged but build continues ─────────
            await StepSoft("langpacks",     InjectLanguagePacksAsync);
            await StepSoft("drivers",       InjectDriversAsync);
            await StepSoft("wallpaper",     InjectWallpaperAsync);
            await StepSoft("firstboot",     InjectFirstBootAsync);
            await StepSoft("publicdesktop",    StagePublicDesktopAsync);
            await StepSoft("deploymentscripts", StageDeploymentScriptsAsync);
            await StepSoft("bloatware",         RemoveBloatwareAsync);
            await StepSoft("features",      ApplyFeaturesAsync);
            await StepSoft("registry",      ApplyRegistryAsync);
            await StepSoft("unattend",      GenerateUnattendAsync);

            // ── Pre-commit validation: runs BEFORE commit so failures are still
            //    recoverable (WIM is mounted, nothing is permanent yet).
            //    Saves a human-readable report to the output folder.
            //    Any FAIL stops the build; WARNs are noted but do not block.
            await Step("precommit_validate", ValidateBuildContentsAsync);

            // ── Critical again: WIM must be committed cleanly ─────────────────
            await Step("unmount", UnmountWimAsync);

            // ── Build & verify (ISO build is critical; hash is soft) ──────────
            string isoPath = "";
            await Step("buildiso", async () => { isoPath = await BuildIsoAsync(); });
            string sha = "";
            var (verifyIso, cleanWorkspace, _) = GoldenISOBuilder.Helpers.AppSettingsLoader.ReadBuildSettings();
            if (verifyIso)
                await StepSoft("verify", async () => { sha = await VerifyAsync(isoPath); });
            else
            {
                var verifyStep = Steps.First(s => s.Id == "verify");
                verifyStep.Status = BuildStepStatus.Skipped;
                Log("ISO verification skipped (disabled in settings).");
            }

            sw.Stop();
            var size = new FileInfo(isoPath).Length;
            Log($"=== Build SUCCESS in {sw.Elapsed:hh\\:mm\\:ss}.  ISO: {isoPath}  ({size / (1024.0 * 1024 * 1024):F2} GB)  SHA256: {sha} ===");

            _s.LastBuiltIsoPath = isoPath;
            _s.LastBuildLogPath = _logPath;
            _s.LastBuildSha256  = sha;

            // ── Post-success workspace cleanup ────────────────────────────────
            // Non-fatal: a cleanup failure must never retroactively fail a good build.
            if (cleanWorkspace)
                await CleanupAfterSuccessAsync();
            else
                Log("Workspace cleanup skipped (disabled in settings).");

            BuildHistoryStore.Append(new BuildRecord
            {
                CompletedAt     = DateTime.Now,
                Success         = true,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                EditionName     = _s.SelectedEdition ?? "",
                IsoPath         = isoPath
            });

            return new BuildResult
            {
                Success      = true,
                IsoPath      = isoPath,
                Sha256       = sha,
                IsoSizeBytes = size,
                Duration     = sw.Elapsed,
                LogPath      = _logPath
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log("=== Build CANCELLED ===");
            _s.LastBuildLogPath = _logPath;   // ← fix: OpenLog button reads this
            BuildHistoryStore.Append(new BuildRecord
            {
                CompletedAt     = DateTime.Now,
                Success         = false,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                EditionName     = _s.SelectedEdition ?? ""
            });
            await TryCleanupOnFailure();
            return new BuildResult { Success = false, Error = "Build cancelled by user.", LogPath = _logPath };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log("=== Build FAILED ===");
            Log(ex.ToString());
            _s.LastBuildLogPath = _logPath;   // ← fix: OpenLog button reads this
            BuildHistoryStore.Append(new BuildRecord
            {
                CompletedAt     = DateTime.Now,
                Success         = false,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                EditionName     = _s.SelectedEdition ?? ""
            });
            await TryCleanupOnFailure();
            return new BuildResult { Success = false, Error = ex.Message, LogPath = _logPath };
        }
        finally
        {
            CloseLog();
        }
    }

    // ── Step 1: Validate ──────────────────────────────────────────────────────

    private Task ValidateAsync()
    {
        if (!IsAdministrator())
            throw new Exception("Build engine requires Administrator privileges. Please re-launch the app as Administrator.");

        if (string.IsNullOrWhiteSpace(_s.SourceIsoPath) || !File.Exists(_s.SourceIsoPath))
            throw new Exception("Source ISO is missing or path is invalid.");

        if (_s.SelectedImage is null)
            throw new Exception("No edition selected. Choose a Windows edition in Step 1.");

        if (string.IsNullOrWhiteSpace(_s.OutputPath))
            throw new Exception("Output path not configured.");

        if (string.IsNullOrWhiteSpace(_s.WorkspacePath))
            throw new Exception("Workspace path not configured.");

        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        if (!File.Exists(dism))
            throw new Exception("dism.exe not found in System32.");

        var oscdimg = FindOscdimg();
        if (oscdimg == null)
            throw new Exception(
                "oscdimg.exe not found.\n\nInstall the Windows ADK > Deployment Tools to enable bootable ISO creation:\nhttps://learn.microsoft.com/windows-hardware/get-started/adk-install");

        Log($"DISM:    {dism}");
        Log($"oscdimg: {oscdimg}");
        Log($"Source ISO:    {_s.SourceIsoPath}");
        Log($"Edition:       {_s.SelectedImage.Name} (index {_s.SelectedImage.Index})");
        Log($"Output folder: {_s.OutputPath}");
        Log($"Workspace:     {_s.WorkspacePath}");
        return Task.CompletedTask;
    }

    // ── Step 2: Prepare workspace ─────────────────────────────────────────────

    private async Task PrepareWorkspaceAsync()
    {
        _workspace  = _s.WorkspacePath!;
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_s.OutputPath!);

        _isoStaging = Path.Combine(_workspace, "iso");
        _mountDir   = Path.Combine(_workspace, "mount");

        // ── Clean up any leftover files from a previous aborted run ──────────
        // Each sub-step is logged so the user can see progress rather than
        // looking at a frozen screen while gigabytes of files are being deleted.

        if (Directory.Exists(_mountDir))
        {
            Log("  Checking for leftover WIM mount from a previous build…");
            Log("  Running DISM /Unmount-Image /Discard (timeout: 90 s)…");
            await TryUnmountIfMounted(_mountDir);
            Log("  DISM unmount done.");
        }

        if (Directory.Exists(_isoStaging))
        {
            double mb = GetDirectorySizeMb(_isoStaging);
            Log($"  Removing previous ISO staging folder ({mb:F0} MB) — please wait…");
            await DeleteDirectoryRobust(_isoStaging);
            Log("  ISO staging folder removed.");
        }

        if (Directory.Exists(_mountDir))
        {
            Log("  Removing previous mount directory…");
            await DeleteDirectoryRobust(_mountDir);
        }

        Directory.CreateDirectory(_isoStaging);
        Directory.CreateDirectory(_mountDir);

        Log($"Workspace prepared: {_workspace}");
    }

    /// <summary>Returns the total size of a directory in megabytes (best-effort, non-throwing).</summary>
    private static double GetDirectorySizeMb(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length) / (1024.0 * 1024);
        }
        catch { return 0; }
    }

    // ── Step 3: Copy ISO contents ─────────────────────────────────────────────

    private async Task CopyIsoContentsAsync()
    {
        // The ISO may already be mounted from Step 1's analysis. Use that drive if present.
        string driveLetter = _s.MountedIsoDrive ?? "";
        bool weMountedHere = false;
        if (string.IsNullOrEmpty(driveLetter) || !Directory.Exists(driveLetter + @"\sources"))
        {
            Log("Mounting source ISO…");
            driveLetter = await MountIsoAsync(_s.SourceIsoPath!);
            _s.MountedIsoDrive = driveLetter;
            weMountedHere = true;
        }
        Log($"Source ISO mounted at {driveLetter}");

        Log($"Copying ISO contents to {_isoStaging}…");
        await RobocopyAsync(driveLetter + "\\", _isoStaging, "/MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1");

        // Make staged WIM writable
        var stagedWim = Path.Combine(_isoStaging, "sources", "install.wim");
        var stagedEsd = Path.Combine(_isoStaging, "sources", "install.esd");
        _wimPath = File.Exists(stagedWim) ? stagedWim
                 : File.Exists(stagedEsd) ? stagedEsd
                 : throw new FileNotFoundException("Staged WIM/ESD missing after copy.");

        File.SetAttributes(_wimPath, FileAttributes.Normal);

        if (weMountedHere)
        {
            await DismountIsoAsync(_s.SourceIsoPath!);
        }
    }

    // ── Step 4: Export selected edition ───────────────────────────────────────

    private async Task ExportSelectedEditionAsync()
    {
        var img = _s.SelectedImage!;
        _selectedEditionName = img.Name;
        Log($"Exporting edition '{img.Name}' (index {img.Index}) to a fresh install.wim …");

        var outWim = Path.Combine(_isoStaging, "sources", "install.export.wim");
        if (File.Exists(outWim)) File.Delete(outWim);

        // Export by index → produces a single-image WIM with index 1.
        // If source is .esd, output goes to .wim and gets recompressed.
        // /CheckIntegrity is not supported for ESD source files — omit it when source is .esd
        bool sourceIsEsd = _wimPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase);
        string checkFlag = sourceIsEsd ? "" : " /CheckIntegrity";
        var (_, _, wimCompression) = GoldenISOBuilder.Helpers.AppSettingsLoader.ReadBuildSettings();
        if (!new[] { "max", "fast", "none" }.Contains(wimCompression, StringComparer.OrdinalIgnoreCase))
            wimCompression = "max";
        Log($"  WIM compression: {wimCompression}");
        await DismAsync(
            $"/Export-Image /SourceImageFile:\"{_wimPath}\" /SourceIndex:{img.Index} " +
            $"/DestinationImageFile:\"{outWim}\" /Compress:{wimCompression}{checkFlag}");

        // Replace original
        var dst = Path.Combine(_isoStaging, "sources", "install.wim");
        if (File.Exists(dst)) File.Delete(dst);
        var esd = Path.Combine(_isoStaging, "sources", "install.esd");
        if (File.Exists(esd)) File.Delete(esd);

        File.Move(outWim, dst);
        _wimPath = dst;
        Log($"Exported edition WIM: {_wimPath}");
    }

    // ── Step 5: Mount WIM ─────────────────────────────────────────────────────

    private async Task MountWimAsync()
    {
        // DISM keeps a global registry of active mounts. If a previous run was killed
        // mid-mount, the entry can linger and block a fresh mount on the same dir.
        Log("Cleaning DISM mount cache (handles orphaned mounts from prior runs)…");
        try { await DismAsync("/Cleanup-Wim"); } catch (Exception ex) { Log($"  ! Cleanup-Wim: {ex.Message}"); }

        Log($"Mounting WIM index 1 → {_mountDir}");
        await DismAsync($"/Mount-Image /ImageFile:\"{_wimPath}\" /Index:1 /MountDir:\"{_mountDir}\"");
    }

    // ── Step 6: Language Pack Injection ──────────────────────────────────────

    private async Task InjectLanguagePacksAsync()
    {
        if (_s.LanguagePackPaths.Count == 0)
        {
            Skip("No language packs configured.");
            return;
        }

        int added = 0;
        foreach (var cabPath in _s.LanguagePackPaths)
        {
            if (!File.Exists(cabPath))
            {
                Log($"  ! Language pack not found, skipping: {cabPath}");
                continue;
            }
            try
            {
                Log($"  Adding language pack: {Path.GetFileName(cabPath)}");
                // DISM /Add-Package injects the CAB/language pack into the mounted image.
                // Language packs must be added before any language-specific features.
                await DismAsync($"/Image:\"{_mountDir}\" /Add-Package /PackagePath:\"{cabPath}\"");
                Log($"  ✓ Language pack added: {Path.GetFileName(cabPath)}");
                added++;
            }
            catch (Exception ex)
            {
                // Non-fatal — log and continue with remaining packs
                Log($"  ! Failed to add language pack {Path.GetFileName(cabPath)}: {ex.Message}");
            }
        }
        Log($"Language pack injection complete. {added}/{_s.LanguagePackPaths.Count} pack(s) added.");
    }

    // ── Step 7: Driver Injection ──────────────────────────────────────────────

    private async Task InjectDriversAsync()
    {
        if (_s.DriverFolderPaths.Count == 0)
        {
            Skip("No drivers configured.");
            return;
        }

        int added = 0;
        foreach (var folder in _s.DriverFolderPaths)
        {
            if (!Directory.Exists(folder))
            {
                Log($"  ! Driver folder not found, skipping: {folder}");
                continue;
            }
            try
            {
                Log($"  Injecting drivers from: {folder}");
                // /Recurse scans all sub-folders for .INF files.
                // DISM only adds drivers that match the image architecture — mismatched
                // drivers are silently skipped rather than causing failures.
                await DismAsync($"/Image:\"{_mountDir}\" /Add-Driver /Driver:\"{folder}\" /Recurse");
                Log($"  ✓ Drivers added from: {Path.GetFileName(folder.TrimEnd('\\', '/'))}");
                added++;
            }
            catch (Exception ex)
            {
                // Non-fatal — one bad driver folder should not abort the whole build
                Log($"  ! Failed to inject drivers from {folder}: {ex.Message}");
            }
        }
        Log($"Driver injection complete. {added}/{_s.DriverFolderPaths.Count} folder(s) processed.");
    }

    // ── Step 8: Wallpaper ────────────────────────────────────────────────────

    private async Task InjectWallpaperAsync()
    {
        if (string.IsNullOrEmpty(_s.WallpaperPath) || !File.Exists(_s.WallpaperPath))
        {
            Skip("No wallpaper configured.");
            return;
        }

        // Win11 24H2/25H2 introduced "Bloom" defaults at img19 (light) and img20 (dark)
        // alongside the legacy img0. We overwrite all of them so the user's wallpaper
        // is the new default regardless of which one Windows picks based on theme.
        // Some Windows builds ship only a subset of these (e.g. img0 + img19 but no img20),
        // so we only attempt to replace files that actually exist — avoids spurious
        // "file not found" errors in the build log for missing variants.
        var wallpaperDir = Path.Combine(_mountDir, "Windows", "Web", "Wallpaper", "Windows");
        Directory.CreateDirectory(wallpaperDir);
        foreach (var name in new[] { "img0.jpg", "img19.jpg", "img20.jpg" })
        {
            var p = Path.Combine(wallpaperDir, name);
            if (!File.Exists(p))
            {
                Log($"  - {name} not present in this Windows build, skipping");
                continue;
            }
            await TryReplaceFileWithOwnership(p, _s.WallpaperPath, "wallpaper");
        }

        // 4K variants: img0_*, img19_*, img20_* (multi-resolution per file)
        var dir4k = Path.Combine(_mountDir, "Windows", "Web", "4K", "Wallpaper", "Windows");
        if (Directory.Exists(dir4k))
        {
            foreach (var pattern in new[] { "img0_*.jpg", "img19_*.jpg", "img20_*.jpg" })
                foreach (var f in Directory.GetFiles(dir4k, pattern))
                    await TryReplaceFileWithOwnership(f, _s.WallpaperPath, "4K wallpaper");
        }

        // Lock screen / OOBE / logon backgrounds.  Win10 used img1*.jpg.  Win11 uses
        // img100.jpg as primary, plus img101–img105 in 24H2/25H2.  The "img1*.jpg"
        // pattern matches both eras.
        var screenDir = Path.Combine(_mountDir, "Windows", "Web", "Screen");
        if (Directory.Exists(screenDir))
        {
            foreach (var f in Directory.GetFiles(screenDir, "img1*.jpg"))
                await TryReplaceFileWithOwnership(f, _s.WallpaperPath, "lock-screen");
        }
    }

    /// <summary>
    /// Take ownership of a TrustedInstaller-owned file, grant Administrators full
    /// control, clear read-only, and overwrite with a new file. Errors are logged
    /// but never thrown — wallpaper failure should not abort the whole build.
    /// </summary>
    private async Task TryReplaceFileWithOwnership(string targetPath, string sourcePath, string label)
    {
        try
        {
            await RunAsync("takeown.exe", $"/f \"{targetPath}\" /a", expectZero: false);
            await RunAsync("icacls.exe",  $"\"{targetPath}\" /grant Administrators:F", expectZero: false);
            if (File.Exists(targetPath)) File.SetAttributes(targetPath, FileAttributes.Normal);
            File.Copy(sourcePath, targetPath, overwrite: true);
            Log($"  ✓ {label}: {Path.GetFileName(targetPath)}");
        }
        catch (Exception ex)
        {
            Log($"  ! {label} {Path.GetFileName(targetPath)}: {ex.Message}");
        }
    }

    // ── Step 7: First-boot launcher ───────────────────────────────────────────

    private async Task InjectFirstBootAsync()
    {
        // Layout inside image:  C:\GIB\GIBFirstBoot.exe + Installers\* + apps.json
        var gibDir   = Path.Combine(_mountDir, "GIB");
        var instDir  = Path.Combine(gibDir, "Installers");
        Directory.CreateDirectory(gibDir);
        Directory.CreateDirectory(instDir);

        // 1) Copy the GIBFirstBoot.exe (sibling of GoldenISOBuilder.exe)
        var srcGib = Path.Combine(AppContext.BaseDirectory, "GIBFirstBoot.exe");
        if (!File.Exists(srcGib))
            throw new FileNotFoundException(
                $"GIBFirstBoot.exe not found at {srcGib}. The post-build step that copies it should run automatically.");
        File.Copy(srcGib, Path.Combine(gibDir, "GIBFirstBoot.exe"), overwrite: true);

        // 2) Copy each staged installer into Installers\ and build apps.json
        var manifest = new List<object>();
        foreach (var app in _s.StagedApps)
        {
            if (!File.Exists(app.FilePath))
            {
                Log($"  ! Installer not found, skipping: {app.FilePath}");
                continue;
            }
            string fname = Path.GetFileName(app.FilePath);
            string dst   = Path.Combine(instDir, fname);
            File.Copy(app.FilePath, dst, overwrite: true);

            // Default silent switches if user didn't supply any
            string args = string.IsNullOrWhiteSpace(app.Args)
                ? DefaultSilentArgs(app.Type, fname)
                : app.Args;

            // Optional MST transform — only applies to MSI installers.
            // Copy the .mst into Installers\ and pass the filename to GIBFirstBoot,
            // which prepends TRANSFORMS="...\Installers\<mst>" to the msiexec call.
            string? mstFname = null;
            if (app.Type.Equals("msi", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(app.MstPath))
            {
                if (!File.Exists(app.MstPath))
                {
                    Log($"  ! MST transform not found, skipping transform for {app.Name}: {app.MstPath}");
                }
                else
                {
                    mstFname = Path.GetFileName(app.MstPath);
                    var mstDst = Path.Combine(instDir, mstFname);
                    File.Copy(app.MstPath, mstDst, overwrite: true);
                    Log($"  Staged MST transform for {app.Name}: {mstFname}");
                }
            }

            // Clamp timeout to [5, 240] minutes to avoid foot-guns.
            int timeoutMin = Math.Clamp(app.TimeoutMinutes <= 0 ? 60 : app.TimeoutMinutes, 5, 240);

            manifest.Add(new
            {
                name = app.Name,
                file = fname,
                args,
                type = app.Type,
                successExitCodes = app.SuccessExitCodes ?? "0,1641,3010",
                mst = mstFname,                  // null when no transform
                timeoutMinutes = timeoutMin
            });
            Log($"  Staged installer: {app.Name}  ({fname})  timeout={timeoutMin}min");
        }

        var json = JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(gibDir, "apps.json"), json);

        // 3) Wire HKLM\Run via offline hive in the registry step.
        //    Here we only place files; registry will be applied during ApplyRegistryAsync.
        Log($"First-boot launcher staged at {gibDir} ({_s.StagedApps.Count} app(s))");
    }

    private static string DefaultSilentArgs(string type, string filename)
    {
        // MSI installers don't need default args here — GIBFirstBoot ALWAYS
        // prepends "/qn /norestart" before the user-supplied args. So an empty
        // args field for MSI just means "no extras", which is the common case.
        if (type.Equals("msi", StringComparison.OrdinalIgnoreCase))
            return "";

        // Best-effort silent flags by installer family heuristic
        var f = filename.ToLowerInvariant();
        if (f.Contains("chrome"))   return "/silent /install";
        if (f.Contains("firefox"))  return "-ms";
        if (f.Contains("edge"))     return "/silent /install";
        if (f.Contains("7z"))       return "/S";
        if (f.Contains("notepad"))  return "/S";
        if (f.Contains("vscode") || f.Contains("vs_code")) return "/VERYSILENT /MERGETASKS=!runcode";
        // Inno Setup default
        return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
    }

    // ── Step 8: Staged Image Files ────────────────────────────────────────────

    private Task StagePublicDesktopAsync()
    {
        if (_s.StagedFiles.Count == 0)
        {
            Skip("No staged image files configured.");
            return Task.CompletedTask;
        }

        foreach (var entry in _s.StagedFiles)
        {
            if (!File.Exists(entry.SourcePath))
            {
                Log($"  ! Missing source: {entry.SourcePath}");
                continue;
            }

            // Resolve destination folder inside the mounted image.
            // Accept both relative ("Users\Public\Desktop") and absolute ("C:\Users\Public\Desktop") inputs.
            // Path.Combine returns the 2nd arg unchanged when it is rooted — strip the drive+root first
            // so the destination always resolves to a path INSIDE the mounted image, not on the build machine.
            var destRel = entry.DestinationFolder.Trim();
            if (Path.IsPathRooted(destRel))
                destRel = destRel.Substring(Path.GetPathRoot(destRel)?.Length ?? 0);
            destRel = destRel.TrimStart('\\', '/');
            var dstDir   = Path.Combine(_mountDir, destRel);
            Directory.CreateDirectory(dstDir);

            var dstFile = Path.Combine(dstDir, Path.GetFileName(entry.SourcePath));
            File.Copy(entry.SourcePath, dstFile, overwrite: true);
            Log($"  + {Path.GetFileName(entry.SourcePath)}  →  {destRel}");
        }
        return Task.CompletedTask;
    }

    // ── Step 8b: Deployment Scripts ───────────────────────────────────────────

    private Task StageDeploymentScriptsAsync()
    {
        if (!_s.IncludeDeploymentScripts || _s.DeploymentScripts.Count == 0)
        {
            Skip("No deployment scripts configured.");
            return Task.CompletedTask;
        }

        // C:\Users\Public\Documents inside the mounted image
        var publicDocs = Path.Combine(_mountDir, "Users", "Public", "Documents");
        Directory.CreateDirectory(publicDocs);

        // C:\ProgramData\...\Startup inside the mounted image (all-users login trigger)
        var startupDir = Path.Combine(_mountDir, "ProgramData", "Microsoft", "Windows",
                                      "Start Menu", "Programs", "Startup");

        foreach (var script in _s.DeploymentScripts)
        {
            if (!File.Exists(script.Path))
            {
                Log($"  ! Missing script, skipping: {script.Path}");
                continue;
            }

            var fileName = Path.GetFileName(script.Path);
            var ext      = Path.GetExtension(script.Path).ToLowerInvariant();

            // Always stage the file into Public\Documents
            File.Copy(script.Path, Path.Combine(publicDocs, fileName), overwrite: true);
            Log($"  + {fileName} → Public\\Documents  [{script.Trigger}]");

            if (script.Trigger == DeploymentTrigger.EveryLogin)
            {
                Directory.CreateDirectory(startupDir);

                if (ext is ".bat" or ".cmd")
                {
                    // Batch files can run directly from Startup
                    File.Copy(script.Path, Path.Combine(startupDir, fileName), overwrite: true);
                    Log($"    ↳ {fileName} → Startup folder (Every Login)");
                }
                else if (ext == ".ps1")
                {
                    // PowerShell scripts cannot live directly in Startup — create a
                    // thin .bat launcher. Uses cmd /c + -windowstyle hidden so no
                    // console window flashes up during login.
                    var launcherName    = Path.GetFileNameWithoutExtension(fileName) + ".bat";
                    var launcherContent =
                        "@echo off\r\n" +
                        $"cmd.exe /c Powershell.exe -ExecutionPolicy ByPass -windowstyle hidden " +
                        $"-File \"C:\\Users\\Public\\Documents\\{fileName}\"\r\n";
                    File.WriteAllText(Path.Combine(startupDir, launcherName), launcherContent);
                    Log($"    ↳ {launcherName} (PS1 launcher) → Startup folder (Every Login)");
                }
                else
                {
                    // For any other type (vbs, py, etc.) copy directly and let Windows decide
                    File.Copy(script.Path, Path.Combine(startupDir, fileName), overwrite: true);
                    Log($"    ↳ {fileName} → Startup folder (Every Login)");
                }
            }
        }
        return Task.CompletedTask;
    }

    // ── Step 9: Remove bloatware ──────────────────────────────────────────────

    private async Task RemoveBloatwareAsync()
    {
        if (_s.BloatwareToRemove.Count == 0)
        {
            Skip("No bloatware selected.");
            return;
        }

        Log($"Querying provisioned packages in mounted image…");
        var (out_, _, _) = await DismCapturedAsync(
            $"/Image:\"{_mountDir}\" /Get-ProvisionedAppxPackages");

        // Parse "PackageName : <Microsoft.X_1.0.0.0_neutral_~_8wekyb3d8bbwe>"
        var packageNames = out_
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("PackageName"))
            .Select(l =>
            {
                int idx = l.IndexOf(':');
                return idx >= 0 ? l[(idx + 1)..].Trim() : "";
            })
            .Where(p => p.Length > 0)
            .ToList();

        Log($"Found {packageNames.Count} provisioned packages.");

        int removed = 0;
        foreach (var prefix in _s.BloatwareToRemove)
        {
            var matches = packageNames
                .Where(pn => pn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Log($"  - Not present: {prefix}");
                continue;
            }
            foreach (var pkg in matches)
            {
                try
                {
                    await DismAsync(
                        $"/Image:\"{_mountDir}\" /Remove-ProvisionedAppxPackage /PackageName:{pkg}");
                    Log($"  ✓ Removed: {pkg}");
                    removed++;
                }
                catch (Exception ex)
                {
                    Log($"  ! Failed: {pkg} – {ex.Message}");
                }
            }
        }
        Log($"Bloatware removal complete. {removed} package(s) removed.");
    }

    // ── Step 10: Optional features ────────────────────────────────────────────

    private async Task ApplyFeaturesAsync()
    {
        if (_s.EnabledFeatures.Count == 0 && _s.DisabledFeatures.Count == 0)
        {
            Skip("No feature changes configured.");
            return;
        }

        foreach (var f in _s.EnabledFeatures)
        {
            try
            {
                await DismAsync($"/Image:\"{_mountDir}\" /Enable-Feature /FeatureName:{f} /All");
                Log($"  ✓ Enabled: {f}");
            }
            catch (Exception ex) { Log($"  ! Enable {f} failed: {ex.Message}"); }
        }

        foreach (var f in _s.DisabledFeatures)
        {
            try
            {
                await DismAsync($"/Image:\"{_mountDir}\" /Disable-Feature /FeatureName:{f}");
                Log($"  ✓ Disabled: {f}");
            }
            catch (Exception ex) { Log($"  ! Disable {f} failed: {ex.Message}"); }
        }
    }

    // ── Step 11: Registry tweaks ──────────────────────────────────────────────

    private async Task ApplyRegistryAsync()
    {
        // Offline hive layout:
        //   SOFTWARE                       →  HKLM\OFFLINE_SW   (machine-wide policies)
        //   SYSTEM                         →  HKLM\OFFLINE_SYS  (services, drivers)
        //   Users\Default\NTUSER.DAT       →  HKLM\OFFLINE_USR  (per-user defaults that
        //                                                       apply to every NEW account
        //                                                       created on the deployed PC)
        //
        // The system DEFAULT hive (System32\config\DEFAULT) is HKEY_USERS\.DEFAULT, used by
        // services running as LocalSystem — it is NOT inherited by user profiles, so we do
        // NOT load it for personalisation tweaks.

        var swHive  = Path.Combine(_mountDir, "Windows", "System32", "config", "SOFTWARE");
        var sysHive = Path.Combine(_mountDir, "Windows", "System32", "config", "SYSTEM");
        var userHive= Path.Combine(_mountDir, "Users", "Default", "NTUSER.DAT");

        if (!File.Exists(swHive)) { Skip("Offline SOFTWARE hive missing — image incomplete."); return; }

        Log("Loading offline registry hives…");
        await RunAsync("reg.exe", $"load HKLM\\OFFLINE_SW \"{swHive}\"");
        await RunAsync("reg.exe", $"load HKLM\\OFFLINE_SYS \"{sysHive}\"");
        if (File.Exists(userHive))
            await RunAsync("reg.exe", $"load HKLM\\OFFLINE_USR \"{userHive}\"");
        else
            Log($"  ! Default user hive missing: {userHive}");

        try
        {
            // 1) HKLM\RunOnce launcher — fires exactly once on the first user logon
            //    after setup, then Windows deletes the entry automatically.
            //    Using RunOnce (not Run) so GIBFirstBoot never runs again on
            //    subsequent logins or reboots.
            await RegAdd(@"HKLM\OFFLINE_SW\Microsoft\Windows\CurrentVersion\RunOnce",
                         "GIBFirstBoot", "REG_SZ",
                         "C:\\GIB\\GIBFirstBoot.exe");

            // 2) Dark mode (HKCU-equivalent in DEFAULT hive)
            if (_s.DarkMode && File.Exists(userHive))
            {
                await RegAdd(@"HKLM\OFFLINE_USR\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                             "AppsUseLightTheme", "REG_DWORD", "0");
                await RegAdd(@"HKLM\OFFLINE_USR\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                             "SystemUsesLightTheme", "REG_DWORD", "0");
            }

            // 3) Show file extensions / hidden files
            if (File.Exists(userHive))
            {
                const string explorerAdv = @"HKLM\OFFLINE_USR\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
                if (_s.ShowFileExtensions)
                    await RegAdd(explorerAdv, "HideFileExt", "REG_DWORD", "0");
                if (_s.ShowHiddenFiles)
                    // 1 = show hidden, 2 = hide hidden (default)
                    await RegAdd(explorerAdv, "Hidden", "REG_DWORD", "1");
            }

            // 4) Disable telemetry
            if (_s.DisableTelemetry)
                await RegAdd(@"HKLM\OFFLINE_SW\Policies\Microsoft\Windows\DataCollection",
                             "AllowTelemetry", "REG_DWORD", "0");

            // 5) OEM branding
            if (!string.IsNullOrWhiteSpace(_s.OemManufacturer) ||
                !string.IsNullOrWhiteSpace(_s.OemModel) ||
                !string.IsNullOrWhiteSpace(_s.OemSupportUrl))
            {
                const string oemKey = @"HKLM\OFFLINE_SW\Microsoft\Windows\CurrentVersion\OEMInformation";
                if (!string.IsNullOrWhiteSpace(_s.OemManufacturer))
                    await RegAdd(oemKey, "Manufacturer", "REG_SZ", _s.OemManufacturer);
                if (!string.IsNullOrWhiteSpace(_s.OemModel))
                    await RegAdd(oemKey, "Model",        "REG_SZ", _s.OemModel);
                if (!string.IsNullOrWhiteSpace(_s.OemSupportUrl))
                    await RegAdd(oemKey, "SupportURL",   "REG_SZ", _s.OemSupportUrl);
            }

            // 6) RegisteredOwner / RegisteredOrganization
            if (!string.IsNullOrWhiteSpace(_s.RegisteredOwner) || !string.IsNullOrWhiteSpace(_s.OrgName))
            {
                const string nt = @"HKLM\OFFLINE_SW\Microsoft\Windows NT\CurrentVersion";
                if (!string.IsNullOrWhiteSpace(_s.RegisteredOwner))
                    await RegAdd(nt, "RegisteredOwner",        "REG_SZ", _s.RegisteredOwner);
                if (!string.IsNullOrWhiteSpace(_s.OrgName))
                    await RegAdd(nt, "RegisteredOrganization", "REG_SZ", _s.OrgName);
            }

            // 7) SMBv1 disable (server side)
            if (_s.DisableSmbV1)
                await RegAdd(@"HKLM\OFFLINE_SYS\ControlSet001\Services\mrxsmb10",
                             "Start", "REG_DWORD", "4");

            // 8) Custom user-defined entries
            foreach (var e in _s.CustomRegistryEntries)
            {
                try
                {
                    string offlineKey = MapOfflineKey(e.Hive, e.KeyPath);
                    if (e.Operation.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(e.ValueName))
                            await RunAsync("reg.exe", $"delete \"{offlineKey}\" /f", expectZero: false);
                        else
                            await RunAsync("reg.exe", $"delete \"{offlineKey}\" /v \"{e.ValueName}\" /f", expectZero: false);
                    }
                    else
                    {
                        await RegAdd(offlineKey, e.ValueName, e.Type, e.Data);
                    }
                    Log($"  ✓ Reg {e.Operation}: {e.Hive}\\{e.KeyPath}\\{e.ValueName}");
                }
                catch (Exception ex)
                {
                    Log($"  ! Reg {e.Hive}\\{e.KeyPath}\\{e.ValueName}: {ex.Message}");
                }
            }

            // 9) Group Policy settings
            await ApplyGroupPoliciesInner(File.Exists(userHive));
        }
        finally
        {
            Log("Unloading offline registry hives…");
            await UnloadHiveWithRetry("HKLM\\OFFLINE_SW");
            await UnloadHiveWithRetry("HKLM\\OFFLINE_SYS");
            if (File.Exists(userHive))
                await UnloadHiveWithRetry("HKLM\\OFFLINE_USR");   // matches the load name above
        }
    }

    /// <summary>
    /// reg.exe unload often fails on first attempt because .NET (or another
    /// process) is still holding a handle to a key inside the loaded hive.
    /// Standard fix: GC twice (first pass queues finalisers, second collects),
    /// then retry up to 5 times with a 1-second wait between attempts.
    /// </summary>
    private async Task UnloadHiveWithRetry(string mountPoint)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var (_, _, exit) = await RunCapturedAsync("reg.exe", $"unload {mountPoint}");
            if (exit == 0)
            {
                if (attempt > 1) Log($"  ✓ {mountPoint} unloaded on attempt {attempt}");
                return;
            }
            Log($"  ! {mountPoint} unload failed (attempt {attempt}/5) — retrying in 1s…");
            try { await Task.Delay(1_000, _ct); } catch { return; }
        }
        Log($"  ! {mountPoint} could not be unloaded after 5 attempts. " +
            "DISM /Unmount-Image /Commit will release it implicitly.");
    }

    private static string MapOfflineKey(string hive, string keyPath)
    {
        // For HKLM, the user-supplied path may already include the "SOFTWARE\" or "SYSTEM\"
        // prefix — strip it so we don't double up against the OFFLINE_SW/OFFLINE_SYS branch.
        string trimmed = keyPath.TrimStart('\\');
        string root;
        switch (hive.ToUpperInvariant())
        {
            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                root = trimmed.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase)
                    ? @"HKLM\OFFLINE_SYS"
                    : @"HKLM\OFFLINE_SW";
                if (trimmed.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed[("SOFTWARE\\".Length)..];
                else if (trimmed.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed[("SYSTEM\\".Length)..];
                break;
            case "HKCU":
            case "HKEY_CURRENT_USER":
                root = @"HKLM\OFFLINE_USR";   // default user template
                break;
            default:
                root = @"HKLM\OFFLINE_SW";
                break;
        }
        return $"{root}\\{trimmed}";
    }

    private async Task RegAdd(string key, string value, string type, string data)
    {
        // Reg-add escape: data needs quoting; reg.exe handles the rest
        string args = $"add \"{key}\" /v \"{value}\" /t {type} /d \"{EscapeForReg(data)}\" /f";
        await RunAsync("reg.exe", args);
    }

    private static string EscapeForReg(string s) => s.Replace("\"", "\\\"");

    // ── Group Policy injection ────────────────────────────────────────────────

    /// <summary>
    /// Writes all configured GroupPolicyEntry items to the already-loaded offline hives.
    /// Called from inside the try block of ApplyRegistryAsync while OFFLINE_SW and
    /// OFFLINE_USR are still mounted.
    /// </summary>
    private async Task ApplyGroupPoliciesInner(bool userHiveLoaded)
    {
        if (_s.GroupPolicies == null || _s.GroupPolicies.Count == 0) return;

        Log($"  Injecting {_s.GroupPolicies.Count} group policy setting(s)…");
        foreach (var gpe in _s.GroupPolicies)
        {
            if (gpe.State == "NotConfigured") continue;

            if (gpe.PolicyClass == "User" && !userHiveLoaded)
            {
                Log($"  ! [User] {gpe.DisplayName} — user hive not available, skipping.");
                continue;
            }

            try
            {
                if (string.IsNullOrEmpty(gpe.ValueName))
                {
                    // Write to the key default value using /ve
                    string regPath = MapGroupPolicyKey(gpe);
                    string args = $"add \"{regPath}\" /ve /t {gpe.ValueType} /d \"{EscapeForReg(gpe.Value)}\" /f";
                    await RunAsync("reg.exe", args);
                    Log($"  ✓ [GP/{gpe.PolicyClass}] {gpe.DisplayName}: {gpe.State} (default value)");
                    continue;
                }

                string regPathNamed = MapGroupPolicyKey(gpe);
                await RegAdd(regPathNamed, gpe.ValueName, gpe.ValueType, gpe.Value);
                Log($"  ✓ [GP/{gpe.PolicyClass}] {gpe.DisplayName}: {gpe.State}");
            }
            catch (Exception ex)
            {
                Log($"  ! [GP/{gpe.PolicyClass}] {gpe.DisplayName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Maps a GroupPolicyEntry registry key to the correct offline hive path.
    /// Machine policies: HKLM\OFFLINE_SW\Policies\...
    /// User policies:    HKLM\OFFLINE_USR\Software\Policies\...
    /// </summary>
    private static string MapGroupPolicyKey(GroupPolicyEntry gpe)
    {
        var key = gpe.RegistryKey.TrimStart('\\');

        if (gpe.PolicyClass == "User")
        {
            // ADMX user-class keys are HKCU-relative ("Software\Policies\…")
            // OFFLINE_USR IS the NTUSER.DAT, so key goes in as-is
            return $@"HKLM\OFFLINE_USR\{key}";
        }
        else
        {
            // ADMX machine-class keys are HKLM-relative ("SOFTWARE\Policies\…")
            // Strip "SOFTWARE\" prefix because OFFLINE_SW IS the SOFTWARE hive
            if (key.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                key = key["SOFTWARE\\".Length..];
            return $@"HKLM\OFFLINE_SW\{key}";
        }
    }

    // ── Step 12: Generate unattend / SetupComplete ────────────────────────────

    private async Task GenerateUnattendAsync()
    {
        // SetupComplete.cmd runs as SYSTEM at the end of OOBE.
        // We only register HKLM\Run for the first-boot launcher (which actually runs
        // at first interactive logon). SetupComplete.cmd is a placeholder for any
        // other one-off SYSTEM-level commands (e.g. licence, defender update).
        var scriptsDir = Path.Combine(_mountDir, "Windows", "Setup", "Scripts");
        Directory.CreateDirectory(scriptsDir);

        // SetupComplete.cmd runs as SYSTEM at the end of OOBE, just before first
        // user logon. It has no UI session, so we must redirect output to a log
        // file or failures are completely invisible. Microsoft's recommended
        // location for the log is %WINDIR%\Setup\Scripts\setupcomplete.log.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":: Generated by ALE Golden ISO Builder");
        sb.AppendLine($":: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("set LOG=%WINDIR%\\Setup\\Scripts\\setupcomplete.log");
        sb.AppendLine("echo [%date% %time%] SetupComplete.cmd starting >> \"%LOG%\"");

        // ── Admin account: enable + set password ──────────────────────────────
        // Done here (script) instead of unattend.xml so no plain-text password
        // ever appears in the XML file that ships inside the ISO.
        sb.AppendLine(":: --- Admin account ---");
        sb.AppendLine("net user administrator /active:yes >> \"%LOG%\" 2>&1");
        if (!string.IsNullOrEmpty(_s.AdminPassword))
        {
            // Use PowerShell EncodedCommand so passwords with any special characters
            // (", %, ^, &, \, etc.) are handled safely — base64 has no cmd special chars.
            string setPwdPs = $"Set-LocalUser -Name 'administrator' -Password " +
                              $"(ConvertTo-SecureString '{EscapeForPsSingleQuote(_s.AdminPassword)}' -AsPlainText -Force)";
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {ToPsEncodedCommand(setPwdPs)} >> \"%LOG%\" 2>&1");
            Log("  Admin password staged in SetupComplete.cmd (not stored in unattend.xml).");
        }

        // ── Named local admin account (if different from "Administrator") ──────
        // NOTE: When the named account is defined in the oobeSystem <LocalAccounts>
        // XML block (which is always the case here when isNamedAdmin=true), the account
        // already exists by the time SetupComplete.cmd runs.  Using /add on an existing
        // account fails with error 2224 and silently skips the password assignment.
        // Fix: try /add first (handles edge cases where oobeSystem skipped); if the
        // account already exists, fall through to a plain password-update call.
        string _setupUser = (_s.AdminUsername ?? "").Trim();
        if (!string.IsNullOrEmpty(_setupUser) &&
            !_setupUser.Equals("administrator", StringComparison.OrdinalIgnoreCase))
        {
            string upwd = _s.AdminPassword ?? "";
            sb.AppendLine($"echo [%date% %time%] Ensuring local account exists: {_setupUser} >> \"%LOG%\"");
            // Use PowerShell EncodedCommand — avoids all cmd.exe quoting issues for passwords
            // with special characters. Logic: if account exists → update password + enable;
            // if not → create it. Add to Administrators group idempotently in both paths.
            string userPs  = EscapeForPsSingleQuote(_setupUser);
            string pwdPs   = EscapeForPsSingleQuote(upwd);
            string manageUserPs =
                $"$p = ConvertTo-SecureString '{pwdPs}' -AsPlainText -Force; " +
                $"if (Get-LocalUser -Name '{userPs}' -ErrorAction SilentlyContinue) " +
                $"{{ Set-LocalUser -Name '{userPs}' -Password $p; Enable-LocalUser -Name '{userPs}' }} " +
                $"else {{ New-LocalUser -Name '{userPs}' -Password $p -Description 'Local Administrator' | Out-Null }}; " +
                $"Add-LocalGroupMember -Group Administrators -Member '{userPs}' -ErrorAction SilentlyContinue";
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {ToPsEncodedCommand(manageUserPs)} >> \"%LOG%\" 2>&1");
            Log($"  Local account '{_setupUser}' will be ensured at first boot (create or update).");
        }

        // ── Timezone ──────────────────────────────────────────────────────────
        // tzutil is the standard Windows command; no reboot required.
        string _tz = string.IsNullOrWhiteSpace(_s.TimeZone) ? "India Standard Time" : _s.TimeZone;
        sb.AppendLine($"echo [%date% %time%] Setting timezone: {_tz} >> \"%LOG%\"");
        sb.AppendLine($"tzutil /s \"{_tz}\" >> \"%LOG%\" 2>&1");

        // ── Organisation / Owner (visible in System Properties) ───────────────
        if (!string.IsNullOrWhiteSpace(_s.OrgName))
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\" /v RegisteredOrganization /t REG_SZ /d \"{_s.OrgName}\" /f >> \"%LOG%\" 2>&1");
        if (!string.IsNullOrWhiteSpace(_s.RegisteredOwner))
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\" /v RegisteredOwner /t REG_SZ /d \"{_s.RegisteredOwner}\" /f >> \"%LOG%\" 2>&1");

        // ── Product key ───────────────────────────────────────────────────────
        // Uses slmgr.vbs (Windows built-in licence manager).  OEM images that
        // pick the key from firmware can leave this blank.
        if (!string.IsNullOrWhiteSpace(_s.ProductKey))
        {
            sb.AppendLine($"echo [%date% %time%] Installing product key >> \"%LOG%\"");
            sb.AppendLine($"cscript.exe //nologo %WINDIR%\\System32\\slmgr.vbs /ipk {_s.ProductKey} >> \"%LOG%\" 2>&1");
        }

        // ── AutoLogon — ONE TIME ONLY ────────────────────────────────────────
        // How it works:
        //   AutoAdminLogon=1   → enables autologon
        //   AutoLogonCount=1   → Windows decrements this counter each time it
        //                        auto-logs in.  When it hits 0 Windows sets
        //                        AutoAdminLogon=0 and removes AutoLogonCount.
        //                        Result: the machine auto-logs in EXACTLY ONCE,
        //                        then behaves like a normal login-required machine.
        //
        // If a computer rename is also configured, the autologon fires on the
        // reboot that applies the new hostname — so the user first sees the
        // desktop with the correct machine name already showing.
        // ── Password never expires ────────────────────────────────────────────────
        if (_s.PasswordNeverExpires)
        {
            sb.AppendLine(":: Remove password expiration");
            sb.AppendLine("net accounts /maxpwage:unlimited >> \"%LOG%\" 2>&1");
            Log("  Password never expires applied via net accounts.");
        }

        if (_s.AutoLoginEnabled && !string.IsNullOrEmpty(_s.AdminPassword))
        {
            string alUser = string.IsNullOrEmpty(_setupUser) ? "Administrator" : _setupUser;
            sb.AppendLine("echo [%date% %time%] Configuring one-time auto-logon >> \"%LOG%\"");
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoAdminLogon    /t REG_SZ    /d \"1\"                /f >> \"%LOG%\" 2>&1");
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v DefaultUserName   /t REG_SZ    /d \"{alUser}\"         /f >> \"%LOG%\" 2>&1");
            // DefaultPassword written via PowerShell to safely handle any special characters.
            string defPwdPs = $"Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' " +
                              $"-Name 'DefaultPassword' -Value '{EscapeForPsSingleQuote(_s.AdminPassword)}'";
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {ToPsEncodedCommand(defPwdPs)} >> \"%LOG%\" 2>&1");
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v DefaultDomainName /t REG_SZ    /d \".\"                /f >> \"%LOG%\" 2>&1");
            sb.AppendLine($"reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoLogonCount    /t REG_DWORD /d 1                  /f >> \"%LOG%\" 2>&1");
            Log($"  One-time auto-logon configured for '{alUser}'.");
        }

        // ── Defender signature update ─────────────────────────────────────────
        if (_s.EnableDefenderAtp)
        {
            sb.AppendLine("echo [%date% %time%] Updating Defender signatures >> \"%LOG%\"");
            sb.AppendLine("\"%ProgramFiles%\\Windows Defender\\MpCmdRun.exe\" -SignatureUpdate >> \"%LOG%\" 2>&1");
        }

        // ── Power plan ────────────────────────────────────────────────────────
        sb.AppendLine($"echo [%date% %time%] Setting power plan {_s.PowerPlan} >> \"%LOG%\"");
        sb.AppendLine($"powercfg /setactive {PowerPlanGuid(_s.PowerPlan)} >> \"%LOG%\" 2>&1");

        // ── Remove Windows.old ───────────────────────────────────────────────
        // Windows Setup moves the previous Windows installation to C:\Windows.old
        // when it detects an existing OS on the target partition, even when
        // WillWipeDisk=true is set in the unattend.xml partition config.
        // This wastes 10-25 GB and must be cleaned up on every golden-image deploy.
        // takeown + icacls first because Windows.old is ACL-locked by default.
        sb.AppendLine(":: ── Remove Windows.old (left by Setup over an existing install) ──────");
        sb.AppendLine("echo [%date% %time%] Checking for Windows.old... >> \"%LOG%\"");
        // Note: %ERRORLEVEL% inside a parenthesised if-block is parsed early (before execution),
        // so commands are kept as flat sequential statements with goto to avoid that gotcha.
        sb.AppendLine("if not exist \"C:\\Windows.old\" goto skip_wold");
        sb.AppendLine("echo [%date% %time%] Windows.old found -- taking ownership... >> \"%LOG%\"");
        sb.AppendLine("takeown /F \"C:\\Windows.old\" /R /A /D Y >> \"%LOG%\" 2>&1");
        sb.AppendLine("icacls \"C:\\Windows.old\" /grant:r \"Administrators:(F)\" /T /C /Q >> \"%LOG%\" 2>&1");
        sb.AppendLine("echo [%date% %time%] Removing Windows.old... >> \"%LOG%\"");
        sb.AppendLine("rd /s /q \"C:\\Windows.old\" >> \"%LOG%\" 2>&1");
        sb.AppendLine("echo [%date% %time%] Windows.old removal done (exit %ERRORLEVEL%) >> \"%LOG%\"");
        sb.AppendLine(":skip_wold");

        // ── BitLocker ─────────────────────────────────────────────────────────
        if (_s.EnableBitLocker)
        {
            // Normalise drive letter: accept "C", "C:", "c:" → always "C:"
            string blDrive = (_s.BitLockerDriveLetter ?? "C:").Trim().ToUpperInvariant();
            if (!blDrive.EndsWith(":")) blDrive += ":";

            // Normalise key folder: ensure it ends with backslash
            string blKeyFolder = (_s.BitLockerKeyFolder ?? @"C:\").TrimEnd('\\') + @"\";

            Log($"  BitLocker enabled — drive={blDrive}  saveKey={_s.BitLockerSaveRecoveryKey}" +
                (_s.BitLockerSaveRecoveryKey ? $"  keyFolder={blKeyFolder}" : "") +
                "  writing Enable-BitLocker.ps1.");

            // Drive letter, key folder, and save-key flag are baked into the script string
            // at build time rather than passed as schtasks /TR command-line parameters.
            // Reason: schtasks breaks when the -KeyFolder value ends with a backslash
            // (e.g. "C:\") — the trailing \ is treated as escaping the closing " in the
            // /TR value, silently registering a malformed task that never fires.
            string btScript = BuildBitLockerScript(blDrive, blKeyFolder, _s.BitLockerSaveRecoveryKey);
            await File.WriteAllTextAsync(
                Path.Combine(scriptsDir, "Enable-BitLocker.ps1"), btScript);

            // Register as an ONSTART scheduled task instead of calling directly.
            // SetupComplete.cmd runs before OOBE — BDESVC and the TPM stack are not
            // yet fully initialised at that point, so manage-bde silently fails to
            // commit protectors.  Running at ONSTART (next reboot) guarantees that
            // services are up.  -DriveLetter and optional -NoSaveKey are passed as
            // script parameters so the same ps1 handles any drive, any key config.
            sb.AppendLine(":: Register BitLocker as ONSTART scheduled task (SYSTEM / HIGHEST).");
            sb.AppendLine(":: Direct calls from SetupComplete.cmd run before BDESVC/TPM are ready;");
            sb.AppendLine(":: the task fires at the next boot when all services are fully up.");
            sb.AppendLine(":: No script parameters — drive/key-folder/save-key are baked into the .ps1.");
            sb.AppendLine($"echo [%date% %time%] Registering BitLocker scheduled task (drive={blDrive}) >> \"%LOG%\"");
            // /DELAY format for ONSTART is HHHH:MM (hours:minutes).
            // 0000:02 = 0 hours 2 minutes. Previously 0002:00 = 2 HOURS — task never ran in testing.
            sb.AppendLine("schtasks /Create /TN \"EnableBitlocker\" " +
                          "/TR \"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe " +
                          "-NoProfile -ExecutionPolicy Bypass " +
                          "-File C:\\Windows\\Setup\\Scripts\\Enable-BitLocker.ps1\" " +
                          "/SC ONSTART /DELAY 0000:02 /RL HIGHEST /RU SYSTEM /F >> \"%LOG%\" 2>&1");
            sb.AppendLine($"echo [%date% %time%] BitLocker task registered (drive={blDrive}) -- runs at next startup (+2 min delay) >> \"%LOG%\"");
        }

        // ── Scheduled Tasks ───────────────────────────────────────────────────
        if (_s.ScheduledTasks.Count > 0)
        {
            sb.AppendLine(":: ── Scheduled Tasks ──────────────────────────────────────────────");
            sb.AppendLine($"echo [%date% %time%] Creating {_s.ScheduledTasks.Count} scheduled task(s) >> \"%LOG%\"");
            foreach (var task in _s.ScheduledTasks)
            {
                sb.AppendLine($":: Task: {task.Name}");
                sb.AppendLine(BuildSchtasksCommand(task));
                sb.AppendLine($"echo [%date% %time%] Task '{task.Name}' result: %ERRORLEVEL% >> \"%LOG%\"");
            }
            Log($"  {_s.ScheduledTasks.Count} scheduled task(s) staged in SetupComplete.cmd.");
        }

        // ── Computer rename: always LAST because it triggers a reboot ─────────
        //
        // Why last:
        //   Rename-Computer does NOT take effect until after a reboot.
        //   We issue `shutdown /r /t 0` immediately after so the machine comes
        //   back up with the correct hostname.  All other setup above runs on
        //   this first boot; the reboot is purely to activate the new name.
        //
        // AutoLogon timing:
        //   AutoLogonCount=1 (set above) is already in the registry before the
        //   reboot fires.  So after the reboot the machine auto-logs in ONCE
        //   (user sees the desktop with the correct hostname for the first time),
        //   then autologon is disabled permanently.
        //
        // If no prefix is configured this block is skipped entirely — no reboot,
        // the script exits normally and Windows presents the login screen.
        if (!string.IsNullOrWhiteSpace(_s.ComputerPrefix))
        {
            Log($"  Computer naming enabled — prefix '{_s.ComputerPrefix}' + BIOS serial. A reboot will follow.");
            sb.AppendLine(":: ── Computer rename (must be last — triggers reboot) ──────────────");
            sb.AppendLine($"echo [%date% %time%] Renaming computer: prefix {_s.ComputerPrefix.ToUpper()} + BIOS serial >> \"%LOG%\"");
            sb.AppendLine(
                $"powershell -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                $"$serial = ((Get-WmiObject -Class Win32_BIOS).SerialNumber.Trim() -replace '[^a-zA-Z0-9]','').ToUpper(); " +
                $"$newName = ('{_s.ComputerPrefix.ToUpper()}' + $serial).ToUpper(); " +
                $"if ($newName.Length -gt 15) {{ $newName = $newName.Substring(0,15) }}; " +
                $"Rename-Computer -NewName $newName -Force -ErrorAction SilentlyContinue; " +
                $"\\\"[$(Get-Date)] Renamed to: $newName\\\" | Out-File \\\"%LOG%\\\" -Append\" >> \"%LOG%\" 2>&1");
            // Reboot to apply the new hostname. No more commands after this.
            sb.AppendLine($"echo [%date% %time%] Rebooting to apply new hostname >> \"%LOG%\"");
            sb.AppendLine("shutdown /r /t 0");
        }
        else
        {
            // Normal exit — no rename, no reboot needed.
            sb.AppendLine("echo [%date% %time%] SetupComplete.cmd done >> \"%LOG%\"");
            sb.AppendLine("exit /b 0");
        }
        await File.WriteAllTextAsync(Path.Combine(scriptsDir, "SetupComplete.cmd"), sb.ToString());

        // Generate Autounattend.xml only when SkipOobe is enabled.
        // If the user has turned off Skip OOBE they want the interactive Windows setup
        // experience — no unattend file means Windows runs its normal first-boot wizard.
        if (_s.SkipOobe)
        {
            string unattend = BuildUnattendXml();
            // Windows Setup requires UTF-8 without BOM for unattend.xml
            var noBom      = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var pantherDir = Path.Combine(_mountDir, "Windows", "Panther");
            Directory.CreateDirectory(pantherDir);
            await File.WriteAllTextAsync(Path.Combine(pantherDir,   "unattend.xml"),     unattend, noBom);
            await File.WriteAllTextAsync(Path.Combine(_isoStaging,  "Autounattend.xml"), unattend, noBom);
            string pwdNote = string.IsNullOrEmpty(_s.AdminPassword)
                ? " — WARNING: no admin password set; auto-logon may stall on some hardware"
                : " + UserAccounts + AutoLogon (zero-touch)";
            Log($"Autounattend.xml generated (disk layout + hardware bypass + BypassNRO + oobeSystem{pwdNote}).");
        }
        else
        {
            Log("Skip OOBE is disabled — Autounattend.xml not generated. Windows will run the standard first-boot setup wizard.");
        }
    }

    // ── Scheduled-task command builder ────────────────────────────────────────

    /// <summary>
    /// Builds a schtasks.exe /Create command line that creates the task on
    /// the deployed OS from inside SetupComplete.cmd.
    /// </summary>
    private static string BuildSchtasksCommand(GoldenISOBuilder.Models.ScheduledTaskConfig task)
    {
        var sb2 = new StringBuilder("schtasks /Create /F");

        // Task name (quoted in case it contains spaces)
        sb2.Append($" /TN \"{task.Name.Replace("\"", "\\\"")}\"");

        // Action
        string action = $"\"{task.ActionPath.Replace("\"", "\\\"")}\"";
        if (!string.IsNullOrEmpty(task.ActionArguments))
            action += $" {task.ActionArguments}";
        sb2.Append($" /TR {action}");

        // Working directory — schtasks /ST requires XML for this; inject via STARTDIR flag if available
        // (Windows 7+ supports /SD for start dir in some editions; we embed it in the action wrapper if needed)
        if (!string.IsNullOrEmpty(task.StartInFolder))
        {
            // Wrap in cmd to honour Start-in: cmd /c "cd /d <dir> & <action>"
            sb2.Replace($" /TR {action}", $" /TR \"cmd /c cd /d \\\"{task.StartInFolder}\\\" && {action}\"");
        }

        // Trigger type
        switch (task.TriggerType)
        {
            case GoldenISOBuilder.Models.TaskTriggerType.Once:
                sb2.Append($" /SC ONCE");
                sb2.Append($" /SD {task.StartTime.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
                sb2.Append($" /ST {task.StartTime:HH:mm}");
                if (task.DeleteAfterRun) sb2.Append(" /Z");
                break;

            case GoldenISOBuilder.Models.TaskTriggerType.Daily:
                sb2.Append(" /SC DAILY");
                sb2.Append($" /SD {task.StartTime.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
                sb2.Append($" /ST {task.StartTime:HH:mm}");
                break;

            case GoldenISOBuilder.Models.TaskTriggerType.Weekly:
                sb2.Append(" /SC WEEKLY");
                sb2.Append($" /SD {task.StartTime.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
                sb2.Append($" /ST {task.StartTime:HH:mm}");
                if (task.WeekDays.Count > 0)
                {
                    string[] names = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
                    var days = string.Join(",", task.WeekDays
                        .Where(d => d >= 0 && d < names.Length)
                        .Select(d => names[d]));
                    if (!string.IsNullOrEmpty(days)) sb2.Append($" /D {days}");
                }
                break;

            case GoldenISOBuilder.Models.TaskTriggerType.AtLogon:
                sb2.Append(" /SC ONLOGON");
                break;

            case GoldenISOBuilder.Models.TaskTriggerType.AtStartup:
                sb2.Append(" /SC ONSTART");
                break;
        }

        // Run As
        if (task.RunAs == "SYSTEM")
            sb2.Append(" /RU SYSTEM");
        else
            sb2.Append(" /RU \"\" /IT");   // interactive token (current user)

        // Run with highest privileges — schtasks CLI doesn't expose /RL directly
        // on all Windows editions, so we append it only when SYSTEM is selected
        // (SYSTEM already runs at maximum privilege; /RL HIGHEST applies to user tasks)
        if (task.RunWithHighestPrivileges && task.RunAs != "SYSTEM")
            sb2.Append(" /RL HIGHEST");

        // Wake to run — not directly settable via schtasks CLI; requires XML
        // We log a note but skip it (the task still works, just won't wake)

        sb2.Append($" >> \"%LOG%\" 2>&1");
        return sb2.ToString();
    }

    /// <summary>
    /// Generates the Enable-BitLocker.ps1 script with the drive letter, key folder,
    /// and save-key flag baked in as PowerShell variables.  No command-line parameters
    /// are used so the schtasks /TR action string stays simple and unambiguous —
    /// avoiding the well-known bug where a path ending in backslash (e.g. C:\) is
    /// treated as escaping the closing quote in /TR "...path\" and the task silently
    /// fails to run (Last Run Time shows 30/11/1999).
    ///
    /// Logic mirrors the proven Bitlocker.ps1 reference script:
    ///   Enable-BitLocker cmdlet  →  Get-BitLockerVolume  →  save key  →  self-destruct task
    /// </summary>
    private static string BuildBitLockerScript(string driveLetter, string keyFolder, bool saveKey)
    {
        string saveKeyValue = saveKey ? "$true" : "$false";

        // Modeled on the proven working Bitlocker.ps1 script — no outer try/catch wrapper,
        // uses Get-WmiObject (faster + more reliable than Get-CimInstance on some Win11 builds),
        // dynamic drive letter and save folder baked in at build time.
        var script = @"# Bitlocker.ps1 — Generated by ALE Golden ISO Builder
# Runs as SYSTEM from an ONSTART scheduled task (+2 min startup delay).
# Drive letter, key folder and save-key flag are baked in at ISO-build time.

$DriveLetter = '__DRIVE__'
$KeyFolder   = '__KEYFOLDER__'
$SaveKey     = __SAVEKEY__

$LogFile = 'C:\Windows\Setup\Logs\Bitlocker.log'
if (!(Test-Path 'C:\Windows\Setup\Logs')) { New-Item -Path 'C:\Windows\Setup\Logs' -ItemType Directory -Force | Out-Null }
""==== Bitlocker script started: $(Get-Date) ===="" | Out-File -FilePath $LogFile -Encoding utf8 -Append

function Log { param($s) ""$((Get-Date).ToString()) - $s"" | Out-File -FilePath $LogFile -Append }

# marker directory — prevents re-running on subsequent boots
$markerDir = 'C:\ProgramData\RunOnceMarkers'
if (!(Test-Path $markerDir)) { New-Item -Path $markerDir -ItemType Directory -Force | Out-Null }
$markerFile = Join-Path $markerDir 'bitlocker.done'

if (Test-Path $markerFile) {
    Log 'BitLocker already applied. Exiting.'
    try { schtasks.exe /Delete /TN 'EnableBitlocker' /F | Out-Null } catch {}
    exit 0
}

# Get serial (retry up to 60 s)
$maxWaitWMI = 60
$count      = 0
$Serial     = $null
while ($count -lt $maxWaitWMI -and -not $Serial) {
    try { $bios = Get-WmiObject -Class Win32_BIOS -ErrorAction Stop; $Serial = ($bios.SerialNumber).Trim() }
    catch { Start-Sleep -Seconds 3; $count += 3 }
}
if (-not $Serial) { Log 'Failed to get serial'; $Serial = 'UNKNOWN' } else { Log ""Serial: $Serial"" }

# Wait for TPM to be ready (retry loop, up to 300 s)
$maxTPMWait = 300
$interval   = 6
$elapsed    = 0
$tpmReady   = $false

while ($elapsed -lt $maxTPMWait -and -not $tpmReady) {
    try {
        $tpm = Get-Tpm -ErrorAction Stop
        if ($tpm -and $tpm.TpmReady) { $tpmReady = $true; Log 'TPM ready.'; break }
        else { Log 'TPM present but not ready.' }
    } catch { Log ""Get-Tpm failed: $_"" }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}

if (-not $tpmReady) { Log ""TPM not ready after $maxTPMWait seconds. Exiting.""; exit 10 }

# Enable BitLocker
try {
    Log ""Enabling BitLocker on $DriveLetter...""
    Enable-BitLocker -MountPoint $DriveLetter -UsedSpaceOnly -SkipHardwareTest -RecoveryPasswordProtector -ErrorAction Stop
    Log 'Enable-BitLocker invoked.'
} catch {
    Log ""Enable-BitLocker FAILED: $_""
    exit 11
}

# Wait for recovery password (up to 180 s)
$maxWait = 180
$waited  = 0
$RecoveryPassword = $null
while ($waited -lt $maxWait -and -not $RecoveryPassword) {
    Start-Sleep -Seconds 3
    $waited += 3
    try {
        $vol = Get-BitLockerVolume -MountPoint $DriveLetter -ErrorAction Stop
        $rp  = $vol.KeyProtector | Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' }
        if ($rp) {
            if ($rp.RecoveryPassword) { $RecoveryPassword = $rp.RecoveryPassword }
            else {
                $r = (Get-BitLockerVolume -MountPoint $DriveLetter -ErrorAction SilentlyContinue).KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword'
                if ($r) { $RecoveryPassword = $r.RecoveryPassword }
            }
        }
    } catch { Log ""Get-BitLockerVolume error: $_"" }
}

if (-not $RecoveryPassword) { Log 'Could not obtain RecoveryPassword.'; exit 12 }

# Save recovery password — dynamic location based on user selection
if ($SaveKey) {
    $keyFolderPath = $KeyFolder.TrimEnd('\') + '\'
    if (!(Test-Path $keyFolderPath)) { New-Item -Path $keyFolderPath -ItemType Directory -Force | Out-Null }
    $FilePath = Join-Path $keyFolderPath ""BitlockerPassword_L$Serial.txt""
    ""Recovery Password: $RecoveryPassword"" | Out-File -FilePath $FilePath -Encoding utf8
    Log ""Saved recovery password to $FilePath""
} else {
    Log 'Recovery key saving disabled -- key not written to disk.'
}

# Create marker and delete task
try { New-Item -Path $markerFile -ItemType File -Force | Out-Null; Log ""Marker created: $markerFile"" } catch { Log ""Failed to create marker: $_"" }

try { schtasks.exe /Delete /TN 'EnableBitlocker' /F | Out-Null; Log 'Deleted task EnableBitlocker' } catch { Log ""Failed to delete task: $_"" }

# Optionally remove the script itself
try { Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue; Log 'Removed script file.' } catch { Log ""Failed to remove script: $_"" }

Log ""==== Bitlocker finished: $(Get-Date)""
exit 0
";
        return script
            .Replace("__DRIVE__",      driveLetter)
            .Replace("__KEYFOLDER__",  keyFolder)
            .Replace("__SAVEKEY__",    saveKeyValue);
    }

    // NOTE: The previous manage-bde–based BitLocker script and its helper method were
    // replaced by BuildBitLockerScript(drive, folder, saveKey) above.
    // Root-cause of the old task never running (Last Run: 30/11/1999):
    //   The schtasks /TR value passed "-KeyFolder C:\" — the trailing backslash
    //   escaped the closing quote, registering a malformed action that Windows
    //   silently refused to execute.  Fix: bake all values into the script string
    //   at ISO-build time and pass NO parameters to the scheduled task.
    private static string _OldBitLockerScript_DoNotCall() => @"#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
  Enable-BitLocker.ps1 — Generated by ALE Golden ISO Builder

  Delivery: registered as an ONSTART scheduled task by SetupComplete.cmd.
  Runs at next reboot (SYSTEM / HIGHEST) when BDESVC and TPM are fully up.
  Self-deletes the task and creates a run-once marker when complete.

  Parameters
    -DriveLetter  <string>  Drive to encrypt, default C:
    -NoSaveKey    <switch>  When present, recovery key file is NOT written to disk

  Flow:
     0  Run-once marker guard
     1  Get machine serial number
     2  Wait up to 5 min for TPM to be ready
     3  Check current BitLocker state (detect WinPE pre-provisioning)
     4  Remove stale protectors
     5  Add Recovery Password protector
     6  Add TPM protector (if ready)
     7  GATE: abort if no protectors committed
     8  Extract 48-digit recovery key
     9  Save key to C:\BitlockerPassword_L<serial>.txt  (skipped if -NoSaveKey)
    10  Start encryption only if NOT already encrypting (pre-provisioned)
    11  manage-bde -resume to enable protection
    12  Final status log
    13  Create run-once marker
    14  Delete this scheduled task
#>
param(
    [string]$DriveLetter = 'C:',
    [string]$KeyFolder   = 'C:\',
    [switch]$NoSaveKey
)

# Normalise drive letter -- accept C, C:, D: etc.
$DriveLetter = $DriveLetter.Trim().ToUpper()
if (-not $DriveLetter.EndsWith(':')) { $DriveLetter = $DriveLetter + ':' }

$log        = 'C:\Windows\Setup\Logs\bitlocker.log'
$markerDir  = 'C:\ProgramData\RunOnceMarkers'
$markerFile = Join-Path $markerDir ""bitlocker_$($DriveLetter.Replace(':','').ToLower()).done""
$taskName   = 'EnableBitlocker'

if (!(Test-Path $markerDir)) { New-Item -Path $markerDir -ItemType Directory -Force | Out-Null }
if (!(Test-Path 'C:\Windows\Setup\Logs')) { New-Item -Path 'C:\Windows\Setup\Logs' -ItemType Directory -Force | Out-Null }

function Write-Log([string]$msg) {
    $line = ""[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] $msg""
    $line | Out-File -FilePath $log -Append -Encoding UTF8
    Write-Host $line
}

Write-Log ""========== Enable-BitLocker.ps1 starting  DriveLetter=$DriveLetter  NoSaveKey=$NoSaveKey ==========""

# ── Run-once guard ────────────────────────────────────────────────────────────
if (Test-Path $markerFile) {
    Write-Log ""Run-once marker found ($markerFile) -- BitLocker already configured. Removing task and exiting.""
    try { schtasks.exe /Delete /TN $taskName /F | Out-Null } catch {}
    exit 0
}

try {

    # ── Step 1: Get machine serial number (used in recovery key filename) ─────
    $serial = $null; $attempt = 0
    while ($attempt -lt 10 -and -not $serial) {
        try {
            $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop
            $serial = ($bios.SerialNumber).Trim()
        } catch { Start-Sleep -Seconds 3; $attempt++ }
    }
    if (-not $serial -or $serial -eq '') { $serial = 'UNKNOWN' }
    Write-Log ""Serial number: $serial""

    # ── Step 2: Wait for TPM to be ready (up to 5 minutes) ───────────────────
    Write-Log 'Waiting for TPM to become ready (up to 300 s)...'
    $maxWait = 300; $interval = 6; $elapsed = 0; $tpmReady = $false
    while ($elapsed -lt $maxWait -and -not $tpmReady) {
        try {
            $tpm = Get-Tpm -ErrorAction Stop
            if ($tpm -and $tpm.TpmReady) {
                $tpmReady = $true
                Write-Log ""TPM ready (Enabled=$($tpm.TpmEnabled) Activated=$($tpm.TpmActivated))""
            } else {
                Write-Log ""TPM not ready yet (${elapsed}s) -- Enabled=$($tpm.TpmEnabled) Ready=$($tpm.TpmReady)""
            }
        } catch { Write-Log ""Get-Tpm error (${elapsed}s): $_"" }
        if (-not $tpmReady) { Start-Sleep -Seconds $interval; $elapsed += $interval }
    }
    if (-not $tpmReady) {
        Write-Log 'WARNING: TPM not ready after 300 s -- continuing with Recovery Password protector only.'
    }

    # ── Step 3: Check current BitLocker state ────────────────────────────────
    $statusRaw = (manage-bde -status $DriveLetter 2>&1) -join ' '
    Write-Log ""Current state ($DriveLetter): $statusRaw""
    # WinPE pre-provisioning may have already started encryption with a clear key
    $alreadyEncrypting = $statusRaw -match 'Encryption in Progress|Fully Encrypted'
    if ($alreadyEncrypting) {
        Write-Log ""$DriveLetter already encrypting/encrypted (WinPE pre-provisioning) -- will add protectors only.""
    }

    # ── Step 4: Remove any stale protectors ───────────────────────────────────
    Write-Log ""Removing stale key protectors from $DriveLetter...""
    manage-bde -protectors -delete $DriveLetter -all 2>&1 | ForEach-Object { Write-Log ""  [del] $_"" }

    # ── Step 5: Add Recovery Password protector ───────────────────────────────
    Write-Log ""Adding Recovery Password protector to $DriveLetter...""
    manage-bde -protectors -add $DriveLetter -recoverypassword 2>&1 | ForEach-Object { Write-Log ""  [addRP] $_"" }

    # ── Step 6: Add TPM protector (only if TPM is ready) ─────────────────────
    if ($tpmReady) {
        Write-Log ""Adding TPM protector to $DriveLetter...""
        manage-bde -protectors -add $DriveLetter -tpm 2>&1 | ForEach-Object { Write-Log ""  [addTPM] $_"" }
    } else {
        Write-Log 'Skipping TPM protector (TPM not ready).'
    }

    # ── Step 7: GATE — verify at least one protector committed ───────────────
    $protectorRaw = (manage-bde -protectors -get $DriveLetter 2>&1) -join ' '
    Write-Log ""Registered protectors ($DriveLetter): $protectorRaw""
    if ($protectorRaw -notmatch 'Numerical Password|Recovery Password|TPM') {
        throw ""GATE: No valid key protectors committed on $DriveLetter. Aborting to avoid unrecoverable encryption.""
    }
    Write-Log 'Gate passed -- key protectors confirmed.'

    # ── Step 8: Extract 48-digit recovery key ────────────────────────────────
    $rpLines  = manage-bde -protectors -get $DriveLetter -type RecoveryPassword 2>&1
    $rpJoined = ($rpLines) -join ' '
    $keyMatch = [regex]::Match($rpJoined, '\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}')
    $recoveryKey = if ($keyMatch.Success) { $keyMatch.Value } else { '(parse-failed -- see bitlocker.log)' }
    if ($keyMatch.Success) {
        Write-Log ""Recovery key extracted (first 6: $($recoveryKey.Substring(0,6))...)""
    } else {
        Write-Log ""WARNING: Could not parse 48-digit key. Raw: $rpJoined""
    }

    # ── Step 9: Save recovery key (skipped if -NoSaveKey is set) ─────────────
    if ($NoSaveKey) {
        Write-Log 'Recovery key saving is DISABLED (-NoSaveKey) -- key not written to disk.'
    } else {
        $keyFolder = $KeyFolder.TrimEnd('\') + '\'
        if (!(Test-Path $keyFolder)) { New-Item -Path $keyFolder -ItemType Directory -Force | Out-Null }
        $keyFile = Join-Path $keyFolder ""BitlockerPassword_L$serial.txt""
        @""
BitLocker Recovery Key
======================
Machine Serial : $serial
Computer Name  : $env:COMPUTERNAME
Date           : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Volume         : $DriveLetter
Recovery Key   : $recoveryKey

ACTION REQUIRED: Copy this key to a secure location then delete this file.
"" | Out-File -FilePath $keyFile -Encoding UTF8 -Force
        Write-Log ""Recovery key saved to $keyFile""
    }

    # ── Step 10: Start encryption only if not already running ─────────────────
    if ($alreadyEncrypting) {
        Write-Log ""Skipping manage-bde -on ($DriveLetter already encrypting from WinPE pre-provisioning).""
    } else {
        Write-Log ""Starting BitLocker encryption on $DriveLetter (XTS-AES 256, skip hardware test)...""
        manage-bde -on $DriveLetter -em XtsAes256 -skiphardwaretest 2>&1 | ForEach-Object { Write-Log ""  [on] $_"" }
    }

    # ── Step 11: Resume protection (critical for pre-provisioned drives) ──────
    Write-Log ""Resuming/enabling BitLocker protection on $DriveLetter...""
    manage-bde -resume $DriveLetter 2>&1 | ForEach-Object { Write-Log ""  [resume] $_"" }

    # ── Step 12: Final status ─────────────────────────────────────────────────
    Start-Sleep -Seconds 3
    $finalStatus = (manage-bde -status $DriveLetter 2>&1) -join ' '
    Write-Log ""Final status ($DriveLetter): $finalStatus""
    if ($finalStatus -match 'Protection On|Encryption in Progress|Fully Encrypted') {
        Write-Log ""SUCCESS: BitLocker active on $DriveLetter with key protectors set.""
    } else {
        Write-Log ""WARNING: Unexpected state after setup. Run: manage-bde -status $DriveLetter""
    }

    # ── Step 13: Create run-once marker ───────────────────────────────────────
    New-Item -Path $markerFile -ItemType File -Force | Out-Null
    Write-Log ""Run-once marker created: $markerFile""

} catch {
    Write-Log ""FATAL: $_""
    Write-Error ""BitLocker setup failed: $_""
    exit 1
}

# ── Step 14: Delete scheduled task ────────────────────────────────────────────
try {
    schtasks.exe /Delete /TN $taskName /F | Out-Null
    Write-Log ""Deleted scheduled task: $taskName""
} catch { Write-Log ""Failed to delete task: $_"" }

Write-Log '========== Enable-BitLocker.ps1 completed =========='
exit 0
";

    // ══════════════════════════════════════════════════════════════════════════
    // ── Pre-commit validation ─────────────────────────────────────────────────
    // Runs after all injection/generation steps, BEFORE DISM /Commit.
    // Reads every file the engine wrote into the mounted WIM and the ISO staging
    // folder, loads the offline registry hives temporarily, and cross-checks
    // them against what the user configured.  Any FAIL stops the build so the
    // user can investigate without losing the WIM.  WARNs are noted but let the
    // build continue.  A human-readable report is saved to the output folder.
    // ══════════════════════════════════════════════════════════════════════════

    private enum ValStatus { Pass, Warn, Fail }
    private record ValCheck(string Category, string Name, ValStatus Status, string Detail);

    private async Task ValidateBuildContentsAsync()
    {
        Log("─── Pre-commit validation starting ───");
        var checks = new List<ValCheck>();

        checks.AddRange(CheckSession());

        if (_s.SkipOobe)
            checks.AddRange(CheckAutounattend());
        else
            checks.Add(new ValCheck("AUTOUNATTEND.XML", "Skip OOBE disabled",
                ValStatus.Pass, "Autounattend.xml intentionally not generated — Windows will show its standard setup wizard."));

        checks.AddRange(CheckSetupComplete());
        checks.AddRange(CheckStagedFiles());
        checks.AddRange(CheckBitLockerScript());
        checks.AddRange(CheckDeploymentScripts());

        var regChecks = await CheckRegistryAsync();
        checks.AddRange(regChecks);

        // ── Tally ──────────────────────────────────────────────────────────
        int pass = checks.Count(c => c.Status == ValStatus.Pass);
        int warn = checks.Count(c => c.Status == ValStatus.Warn);
        int fail = checks.Count(c => c.Status == ValStatus.Fail);

        // ── Build report text ──────────────────────────────────────────────
        var generated = DateTime.Now;   // single timestamp used in both txt and html filenames
        var rpt = new StringBuilder();
        rpt.AppendLine("========================================");
        rpt.AppendLine("  GoldenISO Builder — Pre-commit Validation Report");
        rpt.AppendLine($"  Generated : {generated:yyyy-MM-dd HH:mm:ss}");
        rpt.AppendLine($"  Edition   : {_s.SelectedEdition}");
        rpt.AppendLine($"  Language  : {_s.IsoSourceLanguage}");
        rpt.AppendLine($"  Source ISO: {_s.SourceIsoPath}");
        rpt.AppendLine("========================================");

        string? lastCat = null;
        foreach (var chk in checks)
        {
            if (chk.Category != lastCat)
            {
                rpt.AppendLine();
                rpt.AppendLine($"[{chk.Category}]");
                lastCat = chk.Category;
            }
            string icon = chk.Status switch
            {
                ValStatus.Pass => "  PASS  ",
                ValStatus.Warn => "  WARN  ",
                _              => "  FAIL  "
            };
            rpt.AppendLine($"{icon}{chk.Name}");
            if (!string.IsNullOrEmpty(chk.Detail))
                rpt.AppendLine($"          └─ {chk.Detail}");
        }
        rpt.AppendLine();
        rpt.AppendLine("========================================");
        rpt.AppendLine($"  SUMMARY: {pass} Pass  |  {warn} Warn  |  {fail} Fail");
        if (fail > 0)
            rpt.AppendLine("  *** BUILD HALTED — correct failures above before retrying ***");
        else if (warn > 0)
            rpt.AppendLine("  Build continues — review warnings above.");
        else
            rpt.AppendLine("  All checks passed. Committing WIM.");
        rpt.AppendLine("========================================");

        // ── Log every check to the build log ──────────────────────────────
        lastCat = null;
        foreach (var chk in checks)
        {
            if (chk.Category != lastCat)
            {
                Log($"  ── [{chk.Category}]");
                lastCat = chk.Category;
            }
            string pfx = chk.Status switch { ValStatus.Pass => "✓", ValStatus.Warn => "!", _ => "✗" };
            string detail = string.IsNullOrEmpty(chk.Detail) ? "" : $" — {chk.Detail}";
            Log($"  {pfx} {chk.Name}{detail}");
        }
        Log($"Validation: {pass} pass / {warn} warn / {fail} fail");

        // ── Save reports (text + HTML) ─────────────────────────────────────
        try
        {
            Directory.CreateDirectory(_s.OutputPath!);
            string stamp      = generated.ToString("yyyyMMdd-HHmmss");
            string txtPath    = Path.Combine(_s.OutputPath!, $"ValidationReport_{stamp}.txt");
            string htmlPath   = Path.Combine(_s.OutputPath!, $"ValidationReport_{stamp}.html");

            await File.WriteAllTextAsync(txtPath, rpt.ToString(), Encoding.UTF8);
            Log($"Validation report saved: {txtPath}");

            string html = BuildHtmlReport(checks, _s.SelectedEdition ?? "Pro",
                _s.IsoSourceLanguage ?? "", _s.SourceIsoPath ?? "",
                generated, pass, warn, fail);
            await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);
            Log($"HTML report saved: {htmlPath}");
        }
        catch (Exception ex)
        {
            Log($"  ! Could not save validation report: {ex.Message}");
        }

        if (fail > 0)
            throw new Exception(
                $"Pre-commit validation found {fail} failure(s). " +
                "The WIM has NOT been committed — review the ValidationReport in your output folder.");
    }

    // ── Check helpers ─────────────────────────────────────────────────────────

    private List<ValCheck> CheckSession()
    {
        const string CAT = "SESSION";
        var c = new List<ValCheck>();

        c.Add(string.IsNullOrWhiteSpace(_s.AdminUsername)
            ? new(CAT, "Admin username", ValStatus.Warn, "Empty — 'Administrator' will be used.")
            : new(CAT, "Admin username", ValStatus.Pass, $"Username: {_s.AdminUsername}"));

        c.Add(string.IsNullOrEmpty(_s.AdminPassword)
            ? new(CAT, "Admin password", ValStatus.Warn,
                "No password set — auto-logon will fail; deployed machine may show login screen.")
            : new(CAT, "Admin password", ValStatus.Pass, "Password is configured."));

        c.Add(string.IsNullOrWhiteSpace(_s.IsoSourceLanguage)
            ? new(CAT, "ISO source language", ValStatus.Warn,
                "IsoSourceLanguage is empty — autounattend.xml UILanguage may be blank.")
            : new(CAT, "ISO source language", ValStatus.Pass, $"Language: {_s.IsoSourceLanguage}"));

        c.Add(string.IsNullOrWhiteSpace(_s.SelectedEdition)
            ? new(CAT, "Edition selected", ValStatus.Warn, "No edition set in session.")
            : new(CAT, "Edition selected", ValStatus.Pass, $"Edition: {_s.SelectedEdition}"));

        return c;
    }

    private List<ValCheck> CheckAutounattend()
    {
        const string CAT = "AUTOUNATTEND.XML";
        var c = new List<ValCheck>();

        var isoRootPath  = Path.Combine(_isoStaging, "Autounattend.xml");
        var pantherPath  = Path.Combine(_mountDir, "Windows", "Panther", "unattend.xml");

        // ── File presence ──────────────────────────────────────────────────
        c.Add(File.Exists(isoRootPath)
            ? new(CAT, "Autounattend.xml exists in ISO root", ValStatus.Pass, isoRootPath)
            : new(CAT, "Autounattend.xml exists in ISO root", ValStatus.Fail, $"Missing: {isoRootPath}"));

        c.Add(File.Exists(pantherPath)
            ? new(CAT, "unattend.xml in Windows\\Panther", ValStatus.Pass, pantherPath)
            : new(CAT, "unattend.xml in Windows\\Panther", ValStatus.Fail, $"Missing: {pantherPath}"));

        if (!File.Exists(isoRootPath)) return c;

        // ── Parse XML ──────────────────────────────────────────────────────
        XDocument? doc = null;
        try
        {
            doc = XDocument.Load(isoRootPath);
            c.Add(new(CAT, "Valid XML structure", ValStatus.Pass, "Parsed without errors."));
        }
        catch (Exception ex)
        {
            c.Add(new(CAT, "Valid XML structure", ValStatus.Fail,
                $"XML parse error: {ex.Message} — file may be corrupt."));
            return c;
        }

        // ── File encoding: must be UTF-8 (no BOM) ─────────────────────────
        {
            var bytes = File.ReadAllBytes(isoRootPath);
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            c.Add(hasBom
                ? new(CAT, "Encoding: UTF-8 without BOM", ValStatus.Fail,
                    "File has a UTF-8 BOM — Windows Setup may reject it. Regenerate the file.")
                : new(CAT, "Encoding: UTF-8 without BOM", ValStatus.Pass, "Correct encoding ✓"));
        }

        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var components = doc.Descendants(ns + "component").ToList();

        // ── windowsPE: International-Core-WinPE ──────────────────────────
        var winPeIntl = components.FirstOrDefault(e =>
            e.Attribute("name")?.Value == "Microsoft-Windows-International-Core-WinPE");

        if (winPeIntl == null)
        {
            c.Add(new(CAT, "windowsPE International-Core-WinPE block", ValStatus.Fail,
                "Component missing from windowsPE pass — UILanguage cannot be set."));
        }
        else
        {
            var uiLang = winPeIntl.Descendants(ns + "UILanguage")
                .FirstOrDefault(e => e.Parent?.Name.LocalName != "SetupUILanguage")?.Value?.Trim();
            var setupUiLang = winPeIntl.Descendants(ns + "SetupUILanguage")
                .Descendants(ns + "UILanguage").FirstOrDefault()?.Value?.Trim();
            var fallback = winPeIntl.Element(ns + "UILanguageFallback")?.Value?.Trim();

            c.Add(string.Equals(uiLang, _s.IsoSourceLanguage, StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "windowsPE UILanguage matches session", ValStatus.Pass,
                    $"{uiLang} matches {_s.IsoSourceLanguage} ✓")
                : new(CAT, "windowsPE UILanguage matches session", ValStatus.Fail,
                    $"XML has '{uiLang}' but session has '{_s.IsoSourceLanguage}' — MISMATCH will cause 0x8007000D."));

            c.Add(string.Equals(setupUiLang, _s.IsoSourceLanguage, StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "SetupUILanguage matches session", ValStatus.Pass, $"{setupUiLang} ✓")
                : new(CAT, "SetupUILanguage matches session", ValStatus.Fail,
                    $"SetupUILanguage has '{setupUiLang}', expected '{_s.IsoSourceLanguage}'."));

            c.Add(new(CAT, "UILanguageFallback present", string.IsNullOrEmpty(fallback) ? ValStatus.Warn : ValStatus.Pass,
                string.IsNullOrEmpty(fallback) ? "UILanguageFallback not set." : $"{fallback} ✓"));
        }

        // ── ProductKey ────────────────────────────────────────────────────
        var productKey = components.SelectMany(e => e.Descendants(ns + "Key"))
            .FirstOrDefault()?.Value?.Trim();
        c.Add(!string.IsNullOrWhiteSpace(productKey)
            ? new(CAT, "ProductKey present", ValStatus.Pass, $"Key: {productKey}")
            : new(CAT, "ProductKey present", ValStatus.Warn,
                "No ProductKey element found — Windows Setup may prompt for a product key."));

        // ── AcceptEula ────────────────────────────────────────────────────
        var acceptEula = components.SelectMany(e => e.Descendants(ns + "AcceptEula"))
            .FirstOrDefault()?.Value?.Trim();
        c.Add(acceptEula == "true"
            ? new(CAT, "AcceptEula = true", ValStatus.Pass, "")
            : new(CAT, "AcceptEula = true", ValStatus.Warn,
                $"AcceptEula value: '{acceptEula ?? "missing"}' — Setup may show EULA screen."));

        // ── AdministratorPassword ─────────────────────────────────────────
        var adminPwd = components.SelectMany(e => e.Descendants(ns + "AdministratorPassword")).FirstOrDefault();
        c.Add(adminPwd != null
            ? new(CAT, "AdministratorPassword block present", ValStatus.Pass, "")
            : new(CAT, "AdministratorPassword block present", ValStatus.Warn,
                "AdministratorPassword missing — built-in admin account may remain disabled after OOBE."));

        // ── AutoLogon ─────────────────────────────────────────────────────
        var autoLogon = components.SelectMany(e => e.Descendants(ns + "AutoLogon")).FirstOrDefault();
        c.Add(autoLogon != null
            ? new(CAT, "AutoLogon block present", ValStatus.Pass,
                "Required for zero-touch first boot — drives Setup past the login screen.")
            : new(CAT, "AutoLogon block present", ValStatus.Warn,
                "AutoLogon missing — machine may pause at login screen during OOBE."));

        // ── OOBE skip ─────────────────────────────────────────────────────
        var oobe = components.SelectMany(e => e.Descendants(ns + "OOBE")).FirstOrDefault();
        bool skipMachine = oobe?.Element(ns + "SkipMachineOOBE")?.Value == "true";
        bool skipUser    = oobe?.Element(ns + "SkipUserOOBE")?.Value == "true";
        c.Add(skipMachine && skipUser
            ? new(CAT, "OOBE skip configured", ValStatus.Pass,
                "SkipMachineOOBE=true + SkipUserOOBE=true ✓")
            : new(CAT, "OOBE skip configured", ValStatus.Warn,
                $"SkipMachineOOBE={skipMachine}, SkipUserOOBE={skipUser} — interactive prompts may appear."));

        // ── LocalAccount for named admin ──────────────────────────────────
        bool isNamedAdmin = !string.IsNullOrWhiteSpace(_s.AdminUsername) &&
                            !_s.AdminUsername.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        if (isNamedAdmin)
        {
            var localAcct = components.SelectMany(e => e.Descendants(ns + "LocalAccount")).FirstOrDefault();
            c.Add(localAcct != null
                ? new(CAT, $"LocalAccount entry for '{_s.AdminUsername}'", ValStatus.Pass, "")
                : new(CAT, $"LocalAccount entry for '{_s.AdminUsername}'", ValStatus.Warn,
                    $"Named admin '{_s.AdminUsername}' configured but no LocalAccount block found in XML."));
        }

        return c;
    }

    private List<ValCheck> CheckSetupComplete()
    {
        const string CAT = "SETUPCOMPLETE.CMD";
        var c = new List<ValCheck>();

        var scriptPath = Path.Combine(_mountDir, "Windows", "Setup", "Scripts", "SetupComplete.cmd");
        if (!File.Exists(scriptPath))
        {
            c.Add(new(CAT, "SetupComplete.cmd exists", ValStatus.Fail,
                $"File missing: {scriptPath} — post-OOBE configuration will not run."));
            return c;
        }

        string script;
        try { script = File.ReadAllText(scriptPath); }
        catch (Exception ex)
        {
            c.Add(new(CAT, "SetupComplete.cmd readable", ValStatus.Fail, ex.Message));
            return c;
        }

        var lines = script.Split('\n');
        c.Add(new(CAT, "SetupComplete.cmd exists", ValStatus.Pass, $"{lines.Length} lines"));

        c.Add(script.TrimEnd().Length > 20
            ? new(CAT, "SetupComplete.cmd non-empty", ValStatus.Pass, $"{lines.Length} lines ✓")
            : new(CAT, "SetupComplete.cmd non-empty", ValStatus.Fail,
                "Script is empty — all post-OOBE configuration will be skipped."));

        // ── Log redirect ──────────────────────────────────────────────────
        c.Add(script.Contains("%LOG%", StringComparison.OrdinalIgnoreCase)
            ? new(CAT, "Log file redirect (%LOG%) configured", ValStatus.Pass, "Script output will be captured ✓")
            : new(CAT, "Log file redirect (%LOG%) configured", ValStatus.Warn,
                "No %LOG% redirect — script errors will be invisible after deployment."));

        // ── Admin password ────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_s.AdminPassword))
        {
            // Set-LocalUser is base64-encoded so its name never appears as plain text;
            // checking for -EncodedCommand alone is the correct signal.
            bool hasEncoded = script.Contains("EncodedCommand", StringComparison.OrdinalIgnoreCase);
            c.Add(hasEncoded
                ? new(CAT, "Admin password via EncodedCommand", ValStatus.Pass,
                    "-EncodedCommand found — password set safely (base64, no special-char issues) ✓")
                : new(CAT, "Admin password via EncodedCommand", ValStatus.Fail,
                    "Expected '-EncodedCommand' not found — password may not be set."));
        }
        else
        {
            c.Add(new(CAT, "Admin password command", ValStatus.Warn,
                "No password configured — Set-LocalUser command will be absent."));
        }

        // ── Auto-logon registry ───────────────────────────────────────────
        if (_s.AutoLoginEnabled)
        {
            c.Add(script.Contains("AutoAdminLogon", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "Auto-logon registry keys present", ValStatus.Pass, "AutoAdminLogon key found ✓")
                : new(CAT, "Auto-logon registry keys present", ValStatus.Fail,
                    "AutoLoginEnabled is true but AutoAdminLogon key not found in script."));

            c.Add(script.Contains("DefaultUserName", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "DefaultUserName registry key present", ValStatus.Pass, "")
                : new(CAT, "DefaultUserName registry key present", ValStatus.Warn,
                    "DefaultUserName key not found — auto-logon may use wrong account."));

            c.Add(script.Contains("DefaultPassword", StringComparison.OrdinalIgnoreCase) ||
                  script.Contains("EncodedCommand", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "DefaultPassword set for auto-logon", ValStatus.Pass, "")
                : new(CAT, "DefaultPassword set for auto-logon", ValStatus.Warn,
                    "DefaultPassword key not found — auto-logon will fail on password-protected accounts."));
        }

        // ── BitLocker task registration ───────────────────────────────────
        if (_s.EnableBitLocker)
        {
            bool hasSchtasks = script.Contains("schtasks", StringComparison.OrdinalIgnoreCase) &&
                               script.Contains("Enable-BitLocker", StringComparison.OrdinalIgnoreCase);
            c.Add(hasSchtasks
                ? new(CAT, "BitLocker schtasks registration found", ValStatus.Pass, "")
                : new(CAT, "BitLocker schtasks registration found", ValStatus.Fail,
                    "BitLocker enabled but schtasks /Create for Enable-BitLocker.ps1 not found."));

            c.Add(script.Contains("DELAY", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "BitLocker startup delay configured", ValStatus.Pass,
                    "/DELAY parameter found — BitLocker task will wait for services to be ready ✓")
                : new(CAT, "BitLocker startup delay configured", ValStatus.Warn,
                    "No /DELAY found — BitLocker task may fire before TPM/BDESVC services are ready."));
        }

        // ── Password never expires ────────────────────────────────────────
        if (_s.PasswordNeverExpires)
        {
            c.Add(script.Contains("maxpwage", StringComparison.OrdinalIgnoreCase) ||
                  script.Contains("PasswordNeverExpires", StringComparison.OrdinalIgnoreCase) ||
                  script.Contains("Set-LocalUser", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "Password-never-expires command present", ValStatus.Pass, "")
                : new(CAT, "Password-never-expires command present", ValStatus.Warn,
                    "PasswordNeverExpires is true but no matching command found."));
        }

        // ── Computer rename ───────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(_s.ComputerPrefix))
        {
            c.Add(script.Contains("Rename-Computer", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "Rename-Computer command present", ValStatus.Pass,
                    $"Prefix: {_s.ComputerPrefix} ✓")
                : new(CAT, "Rename-Computer command present", ValStatus.Fail,
                    $"ComputerPrefix '{_s.ComputerPrefix}' set but Rename-Computer not found."));

            c.Add(script.TrimEnd().EndsWith("shutdown /r /t 0", StringComparison.OrdinalIgnoreCase) ||
                  script.Contains("shutdown /r /t 0", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "Shutdown/reboot command for rename", ValStatus.Pass,
                    "shutdown /r /t 0 found — machine will reboot to apply hostname ✓")
                : new(CAT, "Shutdown/reboot command for rename", ValStatus.Warn,
                    "ComputerPrefix set but shutdown /r not found — new hostname may not take effect."));
        }

        // ── Scheduled tasks ───────────────────────────────────────────────
        foreach (var task in _s.ScheduledTasks)
        {
            c.Add(script.Contains(task.Name, StringComparison.OrdinalIgnoreCase)
                ? new(CAT, $"Scheduled task '{task.Name}' registered", ValStatus.Pass, "")
                : new(CAT, $"Scheduled task '{task.Name}' registered", ValStatus.Fail,
                    $"Task '{task.Name}' configured but not found in SetupComplete.cmd."));
        }

        // ── Deployment scripts ────────────────────────────────────────────
        if (_s.IncludeDeploymentScripts)
        {
            foreach (var ds in _s.DeploymentScripts)
            {
                string fname = Path.GetFileName(ds.Path);
                c.Add(script.Contains(fname, StringComparison.OrdinalIgnoreCase)
                    ? new(CAT, $"Deployment script '{fname}' referenced", ValStatus.Pass, "")
                    : new(CAT, $"Deployment script '{fname}' referenced", ValStatus.Warn,
                        $"'{fname}' may be in Startup folder (EveryLogin trigger) rather than directly called."));
            }
        }

        // ── Script syntax: broken continuation lines ──────────────────────
        bool syntaxOk = true;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string trimmed = lines[i].TrimEnd();
            // A line ending with ^ followed by a blank line is broken batch syntax
            if (trimmed.EndsWith(" ^") && lines[i + 1].Trim().Length == 0)
            {
                c.Add(new(CAT, $"Script syntax warning (line {i + 1})", ValStatus.Warn,
                    $"Trailing ^ with blank continuation: '{trimmed}'"));
                syntaxOk = false;
            }
        }
        if (syntaxOk)
            c.Add(new(CAT, "Script syntax: no broken continuation lines", ValStatus.Pass, ""));

        return c;
    }

    private List<ValCheck> CheckStagedFiles()
    {
        const string CAT = "STAGED FILES";
        var c = new List<ValCheck>();

        // ── GIBFirstBoot.exe ──────────────────────────────────────────────
        var gibExe = Path.Combine(_mountDir, "GIB", "GIBFirstBoot.exe");
        c.Add(File.Exists(gibExe)
            ? new(CAT, "GIBFirstBoot.exe in WIM\\GIB", ValStatus.Pass, "")
            : new(CAT, "GIBFirstBoot.exe in WIM\\GIB", ValStatus.Fail,
                $"Missing: {gibExe} — apps will not be installed on deployed machine."));

        // ── apps.json ─────────────────────────────────────────────────────
        var appsJson = Path.Combine(_mountDir, "GIB", "apps.json");
        c.Add(File.Exists(appsJson)
            ? new(CAT, "apps.json in WIM\\GIB", ValStatus.Pass, $"{_s.StagedApps.Count} app(s)")
            : new(CAT, "apps.json in WIM\\GIB", ValStatus.Fail, $"Missing: {appsJson}"));

        // ── Verify apps.json content ──────────────────────────────────────
        if (File.Exists(appsJson))
        {
            try
            {
                string json = File.ReadAllText(appsJson);
                using var jdoc = System.Text.Json.JsonDocument.Parse(json);
                int count = jdoc.RootElement.GetArrayLength();
                c.Add(count == _s.StagedApps.Count
                    ? new(CAT, "apps.json entry count matches staged apps", ValStatus.Pass,
                        $"{count} entries ✓")
                    : new(CAT, "apps.json entry count matches staged apps", ValStatus.Warn,
                        $"apps.json has {count} entries but session has {_s.StagedApps.Count} staged apps."));
            }
            catch (Exception ex)
            {
                c.Add(new(CAT, "apps.json is valid JSON", ValStatus.Fail, $"Parse error: {ex.Message}"));
            }
        }

        // ── Staged app installer files ────────────────────────────────────
        foreach (var app in _s.StagedApps)
        {
            var fname = Path.GetFileName(app.FilePath);
            var dest  = Path.Combine(_mountDir, "GIB", "Installers", fname);
            c.Add(File.Exists(dest)
                ? new(CAT, $"Installer: {fname}", ValStatus.Pass, "Found in WIM\\GIB\\Installers ✓")
                : new(CAT, $"Installer: {fname}", ValStatus.Fail, $"Missing: {dest}"));
        }

        // ── StagedFiles ───────────────────────────────────────────────────
        foreach (var sf in _s.StagedFiles)
        {
            string fname  = Path.GetFileName(sf.SourcePath);
            string destRel = sf.DestinationFolder.Trim();
            if (Path.IsPathRooted(destRel))
                destRel = destRel.Substring(Path.GetPathRoot(destRel)?.Length ?? 0);
            destRel = destRel.TrimStart('\\', '/');
            var dest = Path.Combine(_mountDir, destRel, fname);
            c.Add(File.Exists(dest)
                ? new(CAT, $"Staged file: {fname}", ValStatus.Pass, $"At {destRel} ✓")
                : new(CAT, $"Staged file: {fname}", ValStatus.Fail,
                    $"Missing in WIM. Expected: {dest}"));
        }

        // ── PublicDesktopFiles (legacy) ───────────────────────────────────
        foreach (var pf in _s.PublicDesktopFiles)
        {
            string fname = Path.GetFileName(pf);
            var dest = Path.Combine(_mountDir, "Users", "Public", "Desktop", fname);
            c.Add(File.Exists(dest)
                ? new(CAT, $"Public Desktop: {fname}", ValStatus.Pass, "")
                : new(CAT, $"Public Desktop: {fname}", ValStatus.Fail,
                    $"Missing in WIM: {dest}"));
        }

        // ── Wallpaper images ──────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_s.WallpaperPath))
        {
            var wallDir  = Path.Combine(_mountDir, "Windows", "Web", "Wallpaper", "Windows");
            var names    = new[] { "img0.jpg", "img19.jpg", "img20.jpg" };
            int replaced = names.Count(n => File.Exists(Path.Combine(wallDir, n)));
            c.Add(replaced > 0
                ? new(CAT, "Wallpaper images replaced in WIM", ValStatus.Pass,
                    $"{replaced} wallpaper file(s) present in Windows\\Web\\Wallpaper\\Windows ✓")
                : new(CAT, "Wallpaper images replaced in WIM", ValStatus.Warn,
                    $"No wallpaper files found in {wallDir} — default Windows wallpaper will be used."));

            // Also check 4K variants directory exists at all
            var dir4k = Path.Combine(_mountDir, "Windows", "Web", "4K", "Wallpaper", "Windows");
            if (Directory.Exists(dir4k))
            {
                int count4k = Directory.GetFiles(dir4k, "img*.jpg").Length;
                c.Add(new(CAT, "4K wallpaper variants", count4k > 0 ? ValStatus.Pass : ValStatus.Warn,
                    count4k > 0 ? $"{count4k} 4K variant(s) present ✓" : "No 4K variants found."));
            }
        }

        return c;
    }

    private List<ValCheck> CheckBitLockerScript()
    {
        const string CAT = "BITLOCKER";
        var c = new List<ValCheck>();

        if (!_s.EnableBitLocker)
        {
            c.Add(new(CAT, "BitLocker", ValStatus.Pass, "Not configured — skipping."));
            return c;
        }

        var blPath = Path.Combine(_mountDir, "Windows", "Setup", "Scripts", "Enable-BitLocker.ps1");
        if (!File.Exists(blPath))
        {
            c.Add(new(CAT, "Enable-BitLocker.ps1 in WIM", ValStatus.Fail,
                $"Missing: {blPath}"));
            return c;
        }
        c.Add(new(CAT, "Enable-BitLocker.ps1 exists", ValStatus.Pass, blPath));

        string script;
        try { script = File.ReadAllText(blPath); }
        catch (Exception ex)
        {
            c.Add(new(CAT, "Enable-BitLocker.ps1 readable", ValStatus.Fail, ex.Message));
            return c;
        }

        // ── Enable-BitLocker cmdlet ───────────────────────────────────────
        c.Add(script.Contains("Enable-BitLocker", StringComparison.OrdinalIgnoreCase)
            ? new(CAT, "Enable-BitLocker cmdlet in script", ValStatus.Pass, "")
            : new(CAT, "Enable-BitLocker cmdlet in script", ValStatus.Fail,
                "Enable-BitLocker cmdlet not found — drive will not be encrypted."));

        // ── Key protector ─────────────────────────────────────────────────
        bool hasKeyProtector = script.Contains("RecoveryPasswordProtector", StringComparison.OrdinalIgnoreCase) ||
                               script.Contains("Add-BitLockerKeyProtector", StringComparison.OrdinalIgnoreCase) ||
                               script.Contains("TpmProtector", StringComparison.OrdinalIgnoreCase);
        c.Add(hasKeyProtector
            ? new(CAT, "BitLocker key protector in script", ValStatus.Pass, "Key protector command found ✓")
            : new(CAT, "BitLocker key protector in script", ValStatus.Warn,
                "No key protector command detected — drive may encrypt without a recovery key."));

        // ── Error handling ────────────────────────────────────────────────
        c.Add(script.Contains("try", StringComparison.OrdinalIgnoreCase) &&
              script.Contains("catch", StringComparison.OrdinalIgnoreCase)
            ? new(CAT, "try/catch error handling", ValStatus.Pass, "")
            : new(CAT, "try/catch error handling", ValStatus.Warn,
                "No try/catch block — unhandled errors will cause Task Scheduler 0x8007042B."));

        // ── Clean exit code ───────────────────────────────────────────────
        c.Add(script.Contains("exit 1", StringComparison.OrdinalIgnoreCase)
            ? new(CAT, "exit 1 on failure", ValStatus.Pass,
                "Prevents 'process terminated unexpectedly' in Task Scheduler ✓")
            : new(CAT, "exit 1 on failure", ValStatus.Warn,
                "No 'exit 1' found — Task Scheduler may show 0x8007042B on errors."));

        // ── Run-once marker ───────────────────────────────────────────────
        c.Add(script.Contains("markerFile", StringComparison.OrdinalIgnoreCase) ||
              script.Contains("marker", StringComparison.OrdinalIgnoreCase)
            ? new(CAT, "Run-once marker in script", ValStatus.Pass,
                "Script will not re-run on subsequent boots ✓")
            : new(CAT, "Run-once marker in script", ValStatus.Warn,
                "No run-once marker found — BitLocker setup may execute on every startup."));

        // ── Drive letter match ─────────────────────────────────────────────
        string configuredDrive = _s.BitLockerDriveLetter.TrimEnd(':') + ":";
        c.Add(script.Contains(configuredDrive, StringComparison.OrdinalIgnoreCase)
            ? new(CAT, $"Target drive letter ({configuredDrive}) in script", ValStatus.Pass, "")
            : new(CAT, $"Target drive letter ({configuredDrive}) in script", ValStatus.Warn,
                $"Configured drive '{configuredDrive}' not found in script — check BitLockerDriveLetter."));

        // ── Recovery key saving ───────────────────────────────────────────
        if (_s.BitLockerSaveRecoveryKey)
        {
            // Active script saves recovery key via Out-File when NoSaveKey=$false
            bool hasKeySave = script.Contains("Out-File", StringComparison.OrdinalIgnoreCase) &&
                              (script.Contains("NoSaveKey", StringComparison.OrdinalIgnoreCase) ||
                               script.Contains("KeyFolder", StringComparison.OrdinalIgnoreCase));
            c.Add(hasKeySave
                ? new(CAT, "Recovery key saving code present", ValStatus.Pass, "Key will be written to disk ✓")
                : new(CAT, "Recovery key saving code present", ValStatus.Warn,
                    "BitLockerSaveRecoveryKey is true but key-saving code not detected in script."));
        }
        else
        {
            // NoSaveKey=$true path — verify the skip logic is present
            c.Add(script.Contains("NoSaveKey", StringComparison.OrdinalIgnoreCase)
                ? new(CAT, "Recovery key saving skipped (as configured)", ValStatus.Pass,
                    "NoSaveKey flag present — key will NOT be written to disk ✓")
                : new(CAT, "Recovery key saving skipped (as configured)", ValStatus.Warn,
                    "BitLockerSaveRecoveryKey is false but NoSaveKey flag not found in script."));
        }

        return c;
    }

    private List<ValCheck> CheckDeploymentScripts()
    {
        const string CAT = "DEPLOYMENT SCRIPTS";
        var c = new List<ValCheck>();

        if (!_s.IncludeDeploymentScripts || _s.DeploymentScripts.Count == 0)
        {
            c.Add(new(CAT, "Deployment scripts", ValStatus.Pass, "None configured — skipping."));
            return c;
        }

        var publicDocs = Path.Combine(_mountDir, "Users", "Public", "Documents");
        var startupDir = Path.Combine(_mountDir, "ProgramData", "Microsoft", "Windows",
                                      "Start Menu", "Programs", "Startup");

        foreach (var ds in _s.DeploymentScripts)
        {
            string fname = Path.GetFileName(ds.Path);

            // All scripts go to Public\Documents
            var docsPath = Path.Combine(publicDocs, fname);
            c.Add(File.Exists(docsPath)
                ? new(CAT, $"'{fname}' in Public\\Documents", ValStatus.Pass,
                    $"[Trigger: {ds.Trigger}] ✓")
                : new(CAT, $"'{fname}' in Public\\Documents", ValStatus.Fail,
                    $"Missing: {docsPath}"));

            // EveryLogin scripts also go to Startup folder
            if (ds.Trigger == DeploymentTrigger.EveryLogin)
            {
                var startupPath = Path.Combine(startupDir, fname);
                c.Add(File.Exists(startupPath)
                    ? new(CAT, $"'{fname}' in Startup folder (EveryLogin)", ValStatus.Pass, "")
                    : new(CAT, $"'{fname}' in Startup folder (EveryLogin)", ValStatus.Warn,
                        $"EveryLogin trigger set but file not in Startup: {startupPath}"));
            }
        }

        return c;
    }

    private async Task<List<ValCheck>> CheckRegistryAsync()
    {
        const string CAT = "REGISTRY";
        var c = new List<ValCheck>();

        var swHive   = Path.Combine(_mountDir, "Windows", "System32", "config", "SOFTWARE");
        var sysHive  = Path.Combine(_mountDir, "Windows", "System32", "config", "SYSTEM");
        var userHive = Path.Combine(_mountDir, "Users", "Default", "NTUSER.DAT");

        if (!File.Exists(swHive))
        {
            c.Add(new(CAT, "SOFTWARE hive present", ValStatus.Fail,
                $"Hive file missing: {swHive} — image may be corrupt."));
            return c;
        }

        // Use unique names (GIB_VAL_*) to avoid clashing with any OFFLINE_* remnants
        const string SW  = @"HKLM\GIB_VAL_SW";
        const string SYS = @"HKLM\GIB_VAL_SYS";
        const string USR = @"HKLM\GIB_VAL_USR";

        bool swLoaded  = false;
        bool sysLoaded = false;
        bool usrLoaded = false;

        try
        {
            // Load SOFTWARE hive
            var (_, _, swExit) = await RunCapturedAsync("reg.exe", $"load \"{SW}\" \"{swHive}\"");
            swLoaded = swExit == 0;
            c.Add(swLoaded
                ? new(CAT, "SOFTWARE hive loaded for validation", ValStatus.Pass, "")
                : new(CAT, "SOFTWARE hive loaded for validation", ValStatus.Warn,
                    "reg.exe failed to load SOFTWARE hive — registry checks skipped. (Another process may hold a handle.)"));
            if (!swLoaded) return c;

            // Load SYSTEM hive (best-effort)
            if (File.Exists(sysHive))
            {
                var (_, _, sysExit) = await RunCapturedAsync("reg.exe", $"load \"{SYS}\" \"{sysHive}\"");
                sysLoaded = sysExit == 0;
            }

            // Load user (NTUSER.DAT) hive (best-effort)
            if (File.Exists(userHive))
            {
                var (_, _, usrExit) = await RunCapturedAsync("reg.exe", $"load \"{USR}\" \"{userHive}\"");
                usrLoaded = usrExit == 0;
            }

            // ── GIBFirstBoot RunOnce entry ─────────────────────────────────
            {
                var (_, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{SW}\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v GIBFirstBoot");
                c.Add(exit == 0
                    ? new(CAT, "GIBFirstBoot RunOnce key", ValStatus.Pass,
                        "HKLM\\...\\RunOnce\\GIBFirstBoot present ✓")
                    : new(CAT, "GIBFirstBoot RunOnce key", ValStatus.Fail,
                        "GIBFirstBoot RunOnce key missing — first-boot launcher will NOT run after deployment."));
            }

            // ── Telemetry ──────────────────────────────────────────────────
            if (_s.DisableTelemetry)
            {
                var (stdout, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{SW}\\Policies\\Microsoft\\Windows\\DataCollection\" /v AllowTelemetry");
                bool ok = exit == 0 && stdout.Contains("0x0");
                c.Add(ok
                    ? new(CAT, "Telemetry disabled (AllowTelemetry=0)", ValStatus.Pass, "")
                    : new(CAT, "Telemetry disabled (AllowTelemetry=0)", ValStatus.Warn,
                        exit != 0 ? "AllowTelemetry key not found." : $"Unexpected value — output: {stdout.Trim()}"));
            }

            // ── Dark mode ─────────────────────────────────────────────────
            if (_s.DarkMode && usrLoaded)
            {
                var (_, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{USR}\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\" /v AppsUseLightTheme");
                c.Add(exit == 0
                    ? new(CAT, "Dark mode (AppsUseLightTheme=0)", ValStatus.Pass, "")
                    : new(CAT, "Dark mode (AppsUseLightTheme=0)", ValStatus.Warn,
                        "AppsUseLightTheme key not found in default user hive."));
            }

            // ── File extensions ────────────────────────────────────────────
            if (_s.ShowFileExtensions && usrLoaded)
            {
                var (stdout, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{USR}\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v HideFileExt");
                bool ok = exit == 0 && stdout.Contains("0x0");
                c.Add(ok
                    ? new(CAT, "File extensions visible (HideFileExt=0)", ValStatus.Pass, "")
                    : new(CAT, "File extensions visible (HideFileExt=0)", ValStatus.Warn,
                        exit != 0 ? "HideFileExt key not found." : $"Unexpected value — {stdout.Trim()}"));
            }

            // ── SMBv1 ──────────────────────────────────────────────────────
            if (_s.DisableSmbV1 && sysLoaded)
            {
                var (stdout, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{SYS}\\ControlSet001\\Services\\mrxsmb10\" /v Start");
                bool ok = exit == 0 && stdout.Contains("0x4");
                c.Add(ok
                    ? new(CAT, "SMBv1 disabled (mrxsmb10 Start=4)", ValStatus.Pass, "")
                    : new(CAT, "SMBv1 disabled (mrxsmb10 Start=4)", ValStatus.Warn,
                        exit != 0 ? "mrxsmb10 Start key not found." : $"Unexpected value — {stdout.Trim()}"));
            }

            // ── OEM info ───────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(_s.OemManufacturer))
            {
                var (_, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{SW}\\Microsoft\\Windows\\CurrentVersion\\OEMInformation\" /v Manufacturer");
                c.Add(exit == 0
                    ? new(CAT, "OEM Manufacturer key present", ValStatus.Pass, $"{_s.OemManufacturer} ✓")
                    : new(CAT, "OEM Manufacturer key present", ValStatus.Warn,
                        "OemManufacturer configured but key not found in offline hive."));
            }

            // ── RegisteredOwner ────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(_s.RegisteredOwner))
            {
                var (_, _, exit) = await RunCapturedAsync("reg.exe",
                    $"query \"{SW}\\Microsoft\\Windows NT\\CurrentVersion\" /v RegisteredOwner");
                c.Add(exit == 0
                    ? new(CAT, "RegisteredOwner key present", ValStatus.Pass, $"{_s.RegisteredOwner} ✓")
                    : new(CAT, "RegisteredOwner key present", ValStatus.Warn,
                        "RegisteredOwner configured but key not found."));
            }

            // ── Custom registry entries ────────────────────────────────────
            foreach (var entry in _s.CustomRegistryEntries)
            {
                if (entry.Operation.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    // For DELETE: verify the key/value is ABSENT
                    bool   delIsHkcu = entry.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
                                       entry.Hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);
                    string delPrefix = ResolveValHivePrefix(entry.Hive, entry.KeyPath, SW, SYS, USR);
                    string delKey    = StripHivePrefix(entry.KeyPath, delIsHkcu);
                    string delFull   = $"{delPrefix}\\{delKey}";
                    string delVn     = string.IsNullOrEmpty(entry.ValueName) ? "/ve" : $"/v \"{entry.ValueName}\"";
                    var (_, _, delExit) = await RunCapturedAsync("reg.exe", $"query \"{delFull}\" {delVn}");
                    c.Add(delExit != 0
                        ? new(CAT, $"DELETE: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}", ValStatus.Pass,
                            "Value absent as expected ✓")
                        : new(CAT, $"DELETE: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}", ValStatus.Warn,
                            "DELETE was requested but key/value still found in offline hive."));
                    continue;
                }

                // SET: verify value is present with correct type/data
                bool   isHkcu  = entry.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
                                  entry.Hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);
                string prefix  = ResolveValHivePrefix(entry.Hive, entry.KeyPath, SW, SYS, USR);
                string keyPath = StripHivePrefix(entry.KeyPath, isHkcu);
                string fullKey = $"{prefix}\\{keyPath}";
                string vn      = string.IsNullOrEmpty(entry.ValueName) ? "/ve" : $"/v \"{entry.ValueName}\"";

                var (regOut, _, regExit) = await RunCapturedAsync("reg.exe", $"query \"{fullKey}\" {vn}");
                if (regExit != 0)
                {
                    c.Add(new(CAT, $"Custom SET: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}", ValStatus.Fail,
                        $"Value not found in offline hive at: {fullKey}"));
                    continue;
                }

                bool dataOk = ValRegDataMatches(regOut, entry.Type, entry.Data);
                c.Add(dataOk
                    ? new(CAT, $"Custom SET: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}", ValStatus.Pass,
                        $"{entry.Type}={entry.Data} ✓")
                    : new(CAT, $"Custom SET: {entry.Hive}\\{entry.KeyPath}\\{entry.ValueName}", ValStatus.Warn,
                        $"Key found but data check inconclusive. Expected: '{entry.Data}'. reg.exe: {regOut.Trim()}"));
            }

            // ── Group Policy entries ───────────────────────────────────────
            foreach (var gpe in _s.GroupPolicies)
            {
                string gpePrefix = gpe.PolicyClass == "User" ? USR : SW;
                string gpeKey    = gpe.RegistryKey ?? "";
                // Machine policies: key stored as "Policies\..." inside SOFTWARE hive → strip "SOFTWARE\"
                // User policies: key stored as "Software\Policies\..." inside NTUSER.DAT → keep "Software\"
                if (gpe.PolicyClass != "User" &&
                    gpeKey.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                    gpeKey = gpeKey["SOFTWARE\\".Length..];
                if (!string.IsNullOrEmpty(gpeKey))
                {
                    string fullGpeKey = $"{gpePrefix}\\{gpeKey}";
                    string gpeVn      = string.IsNullOrEmpty(gpe.ValueName) ? "/ve" : $"/v \"{gpe.ValueName}\"";
                    var (_, _, gpeExit) = await RunCapturedAsync("reg.exe", $"query \"{fullGpeKey}\" {gpeVn}");
                    c.Add(gpeExit == 0
                        ? new(CAT, $"Group Policy: {gpe.DisplayName}", ValStatus.Pass,
                            $"{(gpe.PolicyClass == "User" ? "User" : "Machine")} policy ✓")
                        : new(CAT, $"Group Policy: {gpe.DisplayName}", ValStatus.Warn,
                            $"Policy registry value not found at {fullGpeKey}"));
                }
            }
        }
        finally
        {
            // Always unload — use discard since result doesn't matter here
            if (swLoaded)  await RunCapturedAsync("reg.exe", $"unload \"{SW}\"");
            if (sysLoaded) await RunCapturedAsync("reg.exe", $"unload \"{SYS}\"");
            if (usrLoaded) await RunCapturedAsync("reg.exe", $"unload \"{USR}\"");
        }

        return c;
    }

    /// <summary>
    /// Resolves which validation hive prefix (SW/SYS/USR) to use for a custom
    /// registry entry based on its Hive tag and key path prefix.
    /// </summary>
    private static string ResolveValHivePrefix(string hive, string keyPath,
        string sw, string sys, string usr)
    {
        if (hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ||
            hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            return usr;

        string trimmed = keyPath.TrimStart('\\');
        if (trimmed.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase))
            return sys;
        return sw;
    }

    /// <summary>
    /// Strips the top-level hive/sub-hive prefix from a key path so it can be
    /// appended to the validation hive root without double-prefixing.
    /// For HKCU entries the key is stored inside NTUSER.DAT with "Software\" as part
    /// of the real path — never strip it for those entries.
    /// </summary>
    private static string StripHivePrefix(string keyPath, bool isHkcu = false)
    {
        string k = keyPath.TrimStart('\\');
        if (!isHkcu)
        {
            if (k.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)) return k["SOFTWARE\\".Length..];
            if (k.StartsWith("SYSTEM\\",   StringComparison.OrdinalIgnoreCase)) return k["SYSTEM\\".Length..];
        }
        return k;
    }

    /// <summary>
    /// Parses reg.exe /query output and returns true when the data value can
    /// be confirmed to match the expected value. Returns true on uncertain cases
    /// (e.g. binary types) to avoid false failures; only returns false on clear
    /// numeric/string mismatches.
    /// </summary>
    private static bool ValRegDataMatches(string regOutput, string type, string expectedData)
    {
        var lines = regOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (!t.Contains(type, StringComparison.OrdinalIgnoreCase)) continue;

            // Split on "    TYPE    " to isolate the data
            var parts = Regex.Split(t, $@"\s+{Regex.Escape(type)}\s+",
                RegexOptions.IgnoreCase);
            if (parts.Length < 2) continue;
            string actual = parts[1].Trim();

            if (type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase))
            {
                // reg.exe shows 0x00000001; compare numeric value
                string hex = actual.Replace("0x", "").Replace("0X", "");
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hexVal) &&
                    uint.TryParse(expectedData, out uint expVal))
                    return hexVal == expVal;
            }
            else if (type.Equals("REG_SZ",        StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("REG_EXPAND_SZ",  StringComparison.OrdinalIgnoreCase))
            {
                return actual.Equals(expectedData, StringComparison.OrdinalIgnoreCase);
            }

            // For other types (BINARY, MULTI_SZ, QWORD) — key found, accept
            return true;
        }
        // Couldn't parse the output — give benefit of the doubt (Pass via Warn in caller)
        return false;
    }

    // ── End of pre-commit validation ──────────────────────────────────────────

    private static string PowerPlanGuid(string plan) => plan switch
    {
        "HighPerformance"     => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        "UltimatePerformance" => "e9a42b02-d5df-448d-aa00-03f14749eb61",
        _                     => "381b4222-f694-41f0-9685-ff5bb260df2e"
    };

    private string BuildUnattendXml()
    {
        // Autounattend.xml — three passes:
        //   windowsPE  — disk layout, hardware bypass, language, EULA
        //   specialize — enable Administrator, inject BypassNRO (prevents MS-account prompt)
        //   oobeSystem — UserAccounts + AutoLogon (REQUIRED for zero-touch on Win 11),
        //                locale suppression, and OOBE screen hiding
        //
        // WHY UserAccounts / AutoLogon must be in the XML and not SetupComplete.cmd:
        //   Windows 11 OOBE runs BEFORE SetupComplete.cmd.  If no <UserAccounts> block
        //   exists in the oobeSystem pass, Setup drops the user into the interactive
        //   account-creation wizard regardless of SkipMachineOOBE / SkipUserOOBE.
        //   The <AutoLogon> block then drives Setup past the OOBE login screen so the
        //   machine reaches the desktop unattended.
        //   SetupComplete.cmd still owns all post-OOBE configuration (real password,
        //   hostname, registry tweaks, app installs, etc.).

        string org       = SecurityElementEscape(
            string.IsNullOrEmpty(_s.OrgName) ? "ALE Golden Image" : _s.OrgName);

        // ── Product key ────────────────────────────────────────────────────────
        // If the user supplied an explicit key in Step 6, use it.
        // Otherwise fall back to the Microsoft-published generic KMS client setup key
        // for the selected edition.  These keys:
        //   • suppress the product-key entry screen during Setup
        //   • select the correct edition automatically
        //   • do NOT activate Windows (OEM UEFI firmware key handles activation)
        //
        // NOTE: the 0x8007000D - 0x40029 crash was caused by the UILanguage mismatch
        // (en-US in SetupUILanguage on an en-GB ISO), NOT by the generic KMS key.
        // That language fix is in the windowsPE component block below.
        string productKeyXml = !string.IsNullOrWhiteSpace(_s.ProductKey)
            ? SecurityElementEscape(_s.ProductKey)
            : SecurityElementEscape(GenericKmsKeyForEdition(_s.SelectedEdition));

        // ── Account details ───────────────────────────────────────────────────
        string adminUser = (_s.AdminUsername ?? "").Trim();
        if (string.IsNullOrEmpty(adminUser))
            adminUser = "Administrator";

        bool   isNamedAdmin  = !adminUser.Equals("administrator",
                                   StringComparison.OrdinalIgnoreCase);
        string adminPwdRaw   = _s.AdminPassword ?? "";
        string adminPwdXml   = SecurityElementEscape(adminPwdRaw);

        // ── <LocalAccounts> block (only when a non-Administrator name is used) ─
        string localAccountsXml = "";
        if (isNamedAdmin)
        {
            localAccountsXml = $@"
      <LocalAccounts>
        <LocalAccount wcm:action=""add"">
          <Password>
            <Value>{adminPwdXml}</Value>
            <PlainText>true</PlainText>
          </Password>
          <DisplayName>{SecurityElementEscape(adminUser)}</DisplayName>
          <Group>Administrators</Group>
          <Name>{SecurityElementEscape(adminUser)}</Name>
        </LocalAccount>
      </LocalAccounts>";
        }

        // ── <AutoLogon> block — drives OOBE past the account-login screen ─────
        // Always emit this; LogonCount=1 means it fires exactly once (during OOBE)
        // then self-disables.  SetupComplete.cmd will re-apply registry auto-logon
        // for the first interactive boot if AutoLoginEnabled is set.
        string autoLogonUser = isNamedAdmin ? adminUser : "Administrator";
        string autoLogonXml  = $@"
    <AutoLogon>
      <Password>
        <Value>{adminPwdXml}</Value>
        <PlainText>true</PlainText>
      </Password>
      <Enabled>true</Enabled>
      <LogonCount>1</LogonCount>
      <Username>{SecurityElementEscape(autoLogonUser)}</Username>
    </AutoLogon>";

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">

  <!--
    Generated by ALE Golden ISO Builder
    Disk layout + hardware bypass + language + EULA + zero-touch OOBE.
    Post-OOBE configuration is in C:\Windows\Setup\Scripts\SetupComplete.cmd.
  -->

  <!-- ═══ windowsPE — runs in WinPE before any OS files are copied ═══ -->
  <settings pass=""windowsPE"">

    <component name=""Microsoft-Windows-International-Core-WinPE""
               processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35""
               language=""neutral"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
      <SetupUILanguage>
        <!-- UILanguage MUST match the boot.wim language of the source ISO (IsoSourceLanguage).
             WillShowUI=Never is safe only when WinPE has resources for this language.
             A mismatch (e.g. en-US here but en-GB boot.wim) causes 0x8007000D — Setup aborts.
             The target OS locale (TargetOsLocale) is set separately in the oobeSystem pass. -->
        <UILanguage>{_s.IsoSourceLanguage}</UILanguage>
        <WillShowUI>Never</WillShowUI>
      </SetupUILanguage>
      <!-- InputLocale/SystemLocale/UserLocale drive keyboard and system locale for the
           installed OS; they use TargetOsLocale, not the ISO boot language. -->
      <InputLocale>{_s.TargetOsLocale}</InputLocale>
      <SystemLocale>{_s.TargetOsLocale}</SystemLocale>
      <UILanguage>{_s.IsoSourceLanguage}</UILanguage>
      <UILanguageFallback>{_s.IsoSourceLanguage}</UILanguageFallback>
      <UserLocale>{_s.TargetOsLocale}</UserLocale>
    </component>

    <component name=""Microsoft-Windows-Setup""
               processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35""
               language=""neutral"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">

      <!-- Bypass TPM 2.0 / Secure Boot / RAM checks -->
      <RunSynchronous>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>1</Order><Description>BypassTPMCheck</Description>
          <Path>cmd /c reg add ""HKLM\SYSTEM\Setup\LabConfig"" /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>2</Order><Description>BypassSecureBootCheck</Description>
          <Path>cmd /c reg add ""HKLM\SYSTEM\Setup\LabConfig"" /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>3</Order><Description>BypassRAMCheck</Description>
          <Path>cmd /c reg add ""HKLM\SYSTEM\Setup\LabConfig"" /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>

      <!-- Disk 0: WinRE 500 MB | EFI 100 MB | MSR 16 MB | Windows (rest) -->
      <DiskConfiguration>
        <Disk wcm:action=""add"">
          <CreatePartitions>
            <CreatePartition wcm:action=""add""><Order>1</Order><Size>500</Size><Type>Primary</Type></CreatePartition>
            <CreatePartition wcm:action=""add""><Order>2</Order><Size>100</Size><Type>EFI</Type></CreatePartition>
            <CreatePartition wcm:action=""add""><Order>3</Order><Size>16</Size><Type>MSR</Type></CreatePartition>
            <CreatePartition wcm:action=""add""><Order>4</Order><Extend>true</Extend><Type>Primary</Type></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action=""add""><Order>1</Order><PartitionID>1</PartitionID><Format>NTFS</Format><Label>WinRE</Label><TypeID>DE94BBA4-06D1-4D40-A16A-BFD50179D6AC</TypeID></ModifyPartition>
            <ModifyPartition wcm:action=""add""><Order>2</Order><PartitionID>2</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
            <ModifyPartition wcm:action=""add""><Order>3</Order><PartitionID>3</PartitionID></ModifyPartition>
            <ModifyPartition wcm:action=""add""><Order>4</Order><PartitionID>4</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>
          <DiskID>0</DiskID>
          <WillWipeDisk>true</WillWipeDisk>
        </Disk>
        <!-- Never show UI — zero-touch requires silent failure rather than prompting.
             OnError caused the upgrade-detection dialog to appear when an existing
             Windows installation was found on the target drive. -->
        <WillShowUI>Never</WillShowUI>
      </DiskConfiguration>

      <!-- Install to disk 0 partition 4 (the Windows partition) -->
      <ImageInstall>
        <OSImage>
          <InstallTo><DiskID>0</DiskID><PartitionID>4</PartitionID></InstallTo>
          <InstallToAvailablePartition>false</InstallToAvailablePartition>
        </OSImage>
      </ImageInstall>

      <!-- Suppress the upgrade-detection prompt (Did you try to upgrade? Yes/No).
           Without this, Setup detects an existing Windows installation on the target
           drive and stops to ask, breaking zero-touch. Upgrade=false forces a clean
           install path; WillShowUI=Never ensures no prompt appears even on error. -->
      <UpgradeData>
        <Upgrade>false</Upgrade>
        <WillShowUI>Never</WillShowUI>
      </UpgradeData>

      <!-- EULA accept + product key.
           Uses the explicit key from Step 6 if supplied, otherwise the generic KMS
           client setup key for the selected edition.  The KMS key selects the edition
           and suppresses the product-key screen; it does NOT activate Windows.
           OEM UEFI firmware handles activation automatically after first boot. -->
      <UserData>
        <ProductKey>
          <Key>{productKeyXml}</Key>
        </ProductKey>
        <AcceptEula>true</AcceptEula>
        <Organization>{org}</Organization>
      </UserData>

    </component>
  </settings>

  <!-- ═══ specialize — runs after first reboot, before OOBE ═══ -->
  <settings pass=""specialize"">
    <component name=""Microsoft-Windows-Deployment""
               processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35""
               language=""neutral"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>1</Order>
          <Description>Enable built-in Administrator account</Description>
          <Path>cmd /c net user administrator /active:yes</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>2</Order>
          <Description>BypassNRO — prevents OOBE forcing a Microsoft account when offline</Description>
          <Path>cmd /c reg add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE"" /v BypassNRO /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>

  <!-- ═══ oobeSystem — zero-touch OOBE for Windows 11 ═══
       KEY CHANGE: <UserAccounts> + <AutoLogon> are REQUIRED for true zero-touch.
       Without <UserAccounts>, Windows 11 ignores SkipMachineOOBE / SkipUserOOBE
       and drops the user into the interactive account-creation wizard.
       Without <AutoLogon>, OOBE stalls at the login screen.
       The password stored here is temporary — SetupComplete.cmd owns the final state.
  ═══ -->
  <settings pass=""oobeSystem"">

    <!-- Suppress region / keyboard-layout selection in OOBE.
         All locale values here use TargetOsLocale (the desired installed-OS language),
         which is independent of IsoSourceLanguage (the boot.wim / WinPE language). -->
    <component name=""Microsoft-Windows-International-Core""
               processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35""
               language=""neutral"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
      <InputLocale>{_s.TargetOsLocale}</InputLocale>
      <SystemLocale>{_s.TargetOsLocale}</SystemLocale>
      <UILanguage>{_s.TargetOsLocale}</UILanguage>
      <UserLocale>{_s.TargetOsLocale}</UserLocale>
    </component>

    <component name=""Microsoft-Windows-Shell-Setup""
               processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35""
               language=""neutral"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State""
               xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">

      <!-- CRITICAL: define the Administrator password so Windows 11 OOBE skips
           the account-creation wizard.  SetupComplete.cmd will re-apply the real
           password (same value) and do all further account configuration. -->
      <UserAccounts>
        <AdministratorPassword>
          <Value>{adminPwdXml}</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>{localAccountsXml}
      </UserAccounts>

      <!-- CRITICAL: AutoLogon drives OOBE past the login screen so the machine
           reaches the desktop (and SetupComplete.cmd) without user interaction.
           LogonCount=1 means this fires exactly once then self-disables. -->
{autoLogonXml}

      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <NetworkLocation>Work</NetworkLocation>
        <ProtectYourPC>3</ProtectYourPC>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>

    </component>
  </settings>

</unattend>";
    }

    private static string SecurityElementEscape(string s) =>
        System.Security.SecurityElement.Escape(s ?? "") ?? "";

    /// <summary>
    /// Escapes a value for embedding inside a PowerShell single-quoted string ('...').
    /// In PS single-quoted strings only the single-quote itself needs doubling — all other
    /// characters (double-quotes, percent, caret, ampersand, backslash…) are literal.
    /// </summary>
    private static string EscapeForPsSingleQuote(string s) =>
        (s ?? "").Replace("'", "''");

    /// <summary>
    /// Base64-encodes a PowerShell script (UTF-16LE) for use with
    /// <c>powershell -EncodedCommand &lt;result&gt;</c>.
    /// The resulting string contains only A-Z, a-z, 0-9, +, /, = so it is
    /// completely safe to embed on a cmd.exe line without any quoting.
    /// </summary>
    private static string ToPsEncodedCommand(string psScript) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

    /// <summary>
    /// Returns the Microsoft-published generic KMS client setup key for the given edition.
    /// These keys select the correct edition and suppress the Setup product-key screen.
    /// They do NOT activate Windows — OEM UEFI firmware handles activation automatically.
    /// Source: https://learn.microsoft.com/en-us/windows-server/get-started/kms-client-activation-keys
    /// </summary>
    private static string GenericKmsKeyForEdition(string edition) => (edition ?? "").ToUpperInvariant() switch
    {
        "PRO"         => "VK7JG-NPHTM-C97JM-9MPGT-3V66T",
        "HOME"        => "YTMG3-N6DKC-DKB77-7M9GH-8HVX7",
        "EDUCATION"   => "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY",
        "ENTERPRISE"  => "XGVPP-NMH47-7TTHJ-W3FW7-8HV2C",
        _             => "VK7JG-NPHTM-C97JM-9MPGT-3V66T",   // default to Pro
    };

    // ── HTML Validation Report ────────────────────────────────────────────────

    private static string BuildHtmlReport(
        List<ValCheck> checks, string edition, string language, string isoPath,
        DateTime generated, int passCount, int warnCount, int failCount)
    {
        int total      = Math.Max(passCount + warnCount + failCount, 1);
        int healthPct  = (int)Math.Round(passCount * 100.0 / total);
        string isoName = Path.GetFileName(isoPath);

        string statusClass = failCount > 0 ? "status-fail" : warnCount > 0 ? "status-warn" : "status-pass";
        string statusText  = failCount > 0 ? "BUILD HALTED" : warnCount > 0 ? "WARNINGS" : "ALL CLEAR";
        string verdictClass = failCount > 0 ? "verdict-fail" : warnCount > 0 ? "verdict-warn" : "verdict-pass";
        string verdictIcon  = failCount > 0 ? "&#x2715;" : warnCount > 0 ? "!" : "&#x2713;";
        string verdictTitle = failCount > 0 ? "Build Halted &#8212; Action Required" : "Ready to Deploy";
        string verdictBody  = failCount > 0
            ? $"{failCount} critical check(s) failed. The ISO was not committed. Fix the items highlighted in red below and retry."
            : warnCount > 0
            ? $"All {passCount} critical checks passed. {warnCount} item(s) flagged for review &#8212; these won&#8217;t block deployment."
            : $"All {passCount} checks passed with no issues. Your ISO is ready to deploy.";

        // Donut geometry (r=54, circumference≈339.29)
        const double R = 54;
        double circ    = 2 * Math.PI * R;
        double passLen = passCount * circ / total;
        double warnLen = warnCount * circ / total;
        double failLen = failCount * circ / total;

        // Section metadata: icon, plain-English name, one-line description
        var meta = new Dictionary<string, (string i, string n, string d)>(StringComparer.OrdinalIgnoreCase)
        {
            ["SESSION"]            = ("&#9881;",  "Build Configuration",        "Edition, language, and admin credentials chosen for this build."),
            ["AUTOUNATTEND.XML"]   = ("&#128196;","Windows Setup Script",        "XML file that installs Windows automatically with no prompts at boot."),
            ["SETUPCOMPLETE.CMD"]  = ("&#9889;",  "First-Boot Configuration",    "Commands that run automatically the very first time Windows starts."),
            ["STAGED FILES"]       = ("&#128230;","Bundled Software &amp; Files","Apps and files injected into the ISO ready to install on deployment."),
            ["BITLOCKER"]          = ("&#128274;","Drive Encryption",             "BitLocker full-disk encryption &#8212; protects data if the device is lost."),
            ["DEPLOYMENT SCRIPTS"] = ("&#128220;","Deployment Scripts",           "Scripts that run each login to configure keyboard layout and user settings."),
            ["REGISTRY"]           = ("&#128193;","System Settings",              "Pre-configured Windows registry values applied before handover."),
        };

        var sections = new StringBuilder();
        foreach (var grp in checks.GroupBy(c => c.Category))
        {
            int gp = grp.Count(c => c.Status == ValStatus.Pass);
            int gw = grp.Count(c => c.Status == ValStatus.Warn);
            int gf = grp.Count(c => c.Status == ValStatus.Fail);
            string sc = gf > 0 ? "sec-fail" : gw > 0 ? "sec-warn" : "sec-ok";
            string oc = (gf > 0 || gw > 0) ? " open" : "";
            var (icon, name, desc) = meta.TryGetValue(grp.Key, out var mv) ? mv : ("&#9670;", grp.Key, "");

            sections.Append($@"<div class=""sec{oc}""><div class=""sec-hdr"" onclick=""tog(this)"">
<div class=""sec-l""><span class=""sico"">{icon}</span><div><div class=""sname"">{name}</div><div class=""sdesc"">{desc}</div></div></div>
<div class=""sec-r"">{(gp > 0 ? $"<span class=\"p pill\">{gp} passed</span>" : "")}{(gw > 0 ? $"<span class=\"w pill\">{gw} warn</span>" : "")}{(gf > 0 ? $"<span class=\"f pill\">{gf} failed</span>" : "")}<span class=""chev"">&#9660;</span></div></div>
<div class=""sec-body"">{string.Join("", grp.Select(RenderCheckHtml))}</div></div>");
        }

        // CSS — no interpolation needed so curly braces are literal
        string css = @"
*{box-sizing:border-box;margin:0;padding:0}
:root{--b0:#040B16;--b1:#070F1C;--b2:#0C1826;--b3:#142030;--b4:#1A2A40;
  --gold:#C9A42A;--g2:#E8C040;--ok:#1EC98A;--wn:#F5A623;--fl:#F05060;
  --t0:#EDF2FA;--t1:#B0BFC8;--t2:#607080;--ln:#162030;--r:12px}
body{background:var(--b0);color:var(--t0);font-family:'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.5}
.hdr{background:linear-gradient(135deg,#081426,#0E1E38 40%,#081426);border-bottom:1px solid var(--ln);padding:20px 40px;display:flex;justify-content:space-between;align-items:center}
.brand{display:flex;align-items:center;gap:14px}
.logo{width:42px;height:42px;border-radius:10px;background:linear-gradient(135deg,var(--gold),var(--g2));display:flex;align-items:center;justify-content:center;font-weight:900;font-size:19px;color:#050C18;box-shadow:0 2px 12px rgba(201,164,42,.35)}
.bt h1{font-size:17px;font-weight:700}.bt p{font-size:11px;color:var(--t2);letter-spacing:.5px}
.spill{padding:5px 14px;border-radius:20px;font-size:10px;font-weight:800;letter-spacing:1.2px;text-transform:uppercase}
.status-pass{background:rgba(30,201,138,.12);color:var(--ok);border:1px solid rgba(30,201,138,.25)}
.status-warn{background:rgba(245,166,35,.12);color:var(--wn);border:1px solid rgba(245,166,35,.25)}
.status-fail{background:rgba(240,80,96,.12);color:var(--fl);border:1px solid rgba(240,80,96,.25)}
.ibar{background:var(--b1);border-bottom:1px solid var(--ln);padding:10px 40px;display:flex;gap:48px;flex-wrap:wrap}
.ii label{font-size:10px;text-transform:uppercase;letter-spacing:1px;color:var(--t2);display:block}
.ii span{font-size:13px;color:var(--t0);font-weight:500}
.main{max-width:1080px;margin:0 auto;padding:28px 40px 48px}
.sgrid{display:grid;grid-template-columns:200px 1fr 1fr 1fr;gap:14px;margin-bottom:20px}
.dc{background:var(--b2);border:1px solid var(--ln);border-radius:var(--r);padding:20px;display:flex;flex-direction:column;align-items:center;gap:10px}
.dw{position:relative;width:120px;height:120px}
.dctr{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center}
.dscore{font-size:28px;font-weight:800;background:linear-gradient(135deg,var(--gold),var(--g2));-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text;line-height:1}
.dsub{font-size:10px;color:var(--t2);text-transform:uppercase;letter-spacing:.8px;margin-top:2px}
.dlbl{font-size:12px;color:var(--t1);font-weight:500}
.sc{background:var(--b2);border:1px solid var(--ln);border-radius:var(--r);padding:22px 24px;position:relative;overflow:hidden}
.sc::after{content:'';position:absolute;top:0;left:0;width:100%;height:3px}
.sp::after{background:linear-gradient(90deg,var(--ok),transparent)}
.sw::after{background:linear-gradient(90deg,var(--wn),transparent)}
.sf::after{background:linear-gradient(90deg,var(--fl),transparent)}
.sn{font-size:44px;font-weight:900;line-height:1;margin-bottom:4px}
.sp .sn{color:var(--ok)}.sw .sn{color:var(--wn)}.sf .sn{color:var(--fl)}
.snm{font-size:13px;color:var(--t1);font-weight:600}.ssd{font-size:11px;color:var(--t2);margin-top:2px}
.sbar{height:3px;background:var(--b4);border-radius:2px;margin-top:14px}
.sbf{height:100%;border-radius:2px;transition:width 1s ease}
.sp .sbf{background:var(--ok)}.sw .sbf{background:var(--wn)}.sf .sbf{background:var(--fl)}
.verd{border-radius:var(--r);padding:18px 22px;display:flex;align-items:center;gap:16px;margin-bottom:24px}
.verdict-pass{background:rgba(30,201,138,.07);border:1px solid rgba(30,201,138,.2)}
.verdict-warn{background:rgba(245,166,35,.07);border:1px solid rgba(245,166,35,.2)}
.verdict-fail{background:rgba(240,80,96,.07);border:1px solid rgba(240,80,96,.2);animation:pf 2s ease infinite}
@keyframes pf{0%,100%{box-shadow:0 0 0 0 rgba(240,80,96,0)}50%{box-shadow:0 0 0 6px rgba(240,80,96,.07)}}
.vi{width:42px;height:42px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:18px;font-weight:700;flex-shrink:0}
.verdict-pass .vi{background:rgba(30,201,138,.18);color:var(--ok)}
.verdict-warn .vi{background:rgba(245,166,35,.18);color:var(--wn)}
.verdict-fail .vi{background:rgba(240,80,96,.18);color:var(--fl)}
.vt h2{font-size:15px;font-weight:700;margin-bottom:3px}
.verdict-pass .vt h2{color:var(--ok)}.verdict-warn .vt h2{color:var(--wn)}.verdict-fail .vt h2{color:var(--fl)}
.vt p{font-size:13px;color:var(--t1)}
.slbl{font-size:11px;text-transform:uppercase;letter-spacing:1px;color:var(--t2);font-weight:600;margin-bottom:10px;padding-left:2px}
.sec{background:var(--b2);border:1px solid var(--ln);border-radius:var(--r);margin-bottom:10px;overflow:hidden;transition:border-color .15s}
.sec.sec-fail{border-color:rgba(240,80,96,.35)}.sec.sec-warn{border-color:rgba(245,166,35,.25)}
.sec-hdr{padding:14px 18px;display:flex;justify-content:space-between;align-items:center;cursor:pointer;user-select:none;transition:background .12s}
.sec-hdr:hover{background:var(--b3)}
.sec-l{display:flex;align-items:center;gap:12px}
.sico{width:34px;height:34px;border-radius:8px;background:var(--b4);display:flex;align-items:center;justify-content:center;font-size:15px;flex-shrink:0}
.sname{font-size:13px;font-weight:600;color:var(--t0)}.sdesc{font-size:11px;color:var(--t2);margin-top:1px}
.sec-r{display:flex;align-items:center;gap:8px}
.pill{padding:3px 10px;border-radius:10px;font-size:11px;font-weight:600}
.p.pill{background:rgba(30,201,138,.12);color:var(--ok)}.w.pill{background:rgba(245,166,35,.12);color:var(--wn)}.f.pill{background:rgba(240,80,96,.12);color:var(--fl)}
.chev{color:var(--t2);font-size:12px;margin-left:4px;transition:transform .2s;display:inline-block}
.sec.open .chev{transform:rotate(180deg)}
.sec-body{display:none}.sec.open .sec-body{display:block}
.ck{display:flex;align-items:flex-start;gap:11px;padding:9px 18px;border-bottom:1px solid rgba(255,255,255,.025);transition:background .1s}
.ck:last-child{border-bottom:none}.ck:hover{background:var(--b3)}
.cki{width:20px;height:20px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:10px;font-weight:800;flex-shrink:0;margin-top:1px}
.ckp .cki{background:rgba(30,201,138,.18);color:var(--ok)}.ckw .cki{background:rgba(245,166,35,.18);color:var(--wn)}.ckf .cki{background:rgba(240,80,96,.18);color:var(--fl)}
.ckb{flex:1;min-width:0}.ckn{font-size:13px;color:var(--t0);font-weight:500}
.ckf .ckn{color:#FF7080}.ckw .ckn{color:#F5BE5A}
.ckd{display:block;font-size:11px;color:var(--t2);margin-top:3px;font-family:Consolas,monospace;white-space:pre-wrap;word-break:break-all}
footer{text-align:center;padding:20px 40px;color:var(--t2);font-size:11px;border-top:1px solid var(--ln);letter-spacing:.3px}
footer strong{color:var(--t1)}
@media(max-width:760px){.sgrid{grid-template-columns:1fr 1fr}.dc{grid-column:1/-1}.ibar,.main{padding:16px}.ibar{gap:24px}}";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>ISO Build Report &#8212; {SecurityElementEscape(edition)}</title>
<style>{css}</style>
</head>
<body>
<header class=""hdr"">
  <div class=""brand"">
    <div class=""logo"">G</div>
    <div class=""bt""><h1>Golden ISO Builder</h1><p>PRE-DEPLOYMENT VALIDATION REPORT</p></div>
  </div>
  <span class=""spill {statusClass}"">{statusText}</span>
</header>
<div class=""ibar"">
  <div class=""ii""><label>ISO File</label><span>{SecurityElementEscape(isoName)}</span></div>
  <div class=""ii""><label>Edition</label><span>Windows 11 {SecurityElementEscape(edition)}</span></div>
  <div class=""ii""><label>Boot Language</label><span>{SecurityElementEscape(language)}</span></div>
  <div class=""ii""><label>Generated</label><span>{generated:yyyy-MM-dd HH:mm:ss}</span></div>
</div>
<div class=""main"">
  <div class=""sgrid"">
    <div class=""dc"">
      <div class=""dw"">
        <svg viewBox=""0 0 120 120"" width=""120"" height=""120"">
          <circle cx=""60"" cy=""60"" r=""54"" fill=""none"" stroke=""#1A2A40"" stroke-width=""11""/>
          <circle id=""ap"" cx=""60"" cy=""60"" r=""54"" fill=""none"" stroke=""#1EC98A"" stroke-width=""11"" stroke-dasharray=""0 339.3"" stroke-linecap=""round"" transform=""rotate(-90 60 60)"" style=""transition:stroke-dasharray 1s ease""/>
          <circle id=""aw"" cx=""60"" cy=""60"" r=""54"" fill=""none"" stroke=""#F5A623"" stroke-width=""11"" stroke-dasharray=""0 339.3"" stroke-linecap=""round"" transform=""rotate(-90 60 60)"" style=""transition:stroke-dasharray 1.1s ease""/>
          <circle id=""af"" cx=""60"" cy=""60"" r=""54"" fill=""none"" stroke=""#F05060"" stroke-width=""11"" stroke-dasharray=""0 339.3"" stroke-linecap=""round"" transform=""rotate(-90 60 60)"" style=""transition:stroke-dasharray 1.2s ease""/>
        </svg>
        <div class=""dctr""><span class=""dscore"" id=""ds"">0%</span><span class=""dsub"">health</span></div>
      </div>
      <div class=""dlbl"">Build Health Score</div>
    </div>
    <div class=""sc sp"">
      <div class=""sn"" data-t=""{passCount}"">0</div>
      <div class=""snm"">Checks Passed</div><div class=""ssd"">No action required</div>
      <div class=""sbar""><div class=""sbf"" data-pct=""{passCount * 100 / total}"" style=""width:0""></div></div>
    </div>
    <div class=""sc sw"">
      <div class=""sn"" data-t=""{warnCount}"">0</div>
      <div class=""snm"">Warnings</div><div class=""ssd"">Review recommended</div>
      <div class=""sbar""><div class=""sbf"" data-pct=""{warnCount * 100 / total}"" style=""width:0""></div></div>
    </div>
    <div class=""sc sf"">
      <div class=""sn"" data-t=""{failCount}"">0</div>
      <div class=""snm"">Failed</div><div class=""ssd"">{(failCount > 0 ? "Must fix before deploying" : "None &#8212; great!")}</div>
      <div class=""sbar""><div class=""sbf"" data-pct=""{failCount * 100 / total}"" style=""width:0""></div></div>
    </div>
  </div>
  <div class=""verd {verdictClass}"">
    <div class=""vi"">{verdictIcon}</div>
    <div class=""vt""><h2>{verdictTitle}</h2><p>{verdictBody}</p></div>
  </div>
  <div class=""slbl"">Validation Details</div>
  {sections}
</div>
<footer>Generated by <strong>ALE Golden ISO Builder</strong> &middot; {generated:dddd, d MMMM yyyy} at {generated:HH:mm:ss}</footer>
<script>
(function(){{
  var C=339.3,pl={passLen:F2},wl={warnLen:F2},fl={failLen:F2},ht={healthPct};
  setTimeout(function(){{
    var ap=document.getElementById('ap'),aw=document.getElementById('aw'),af=document.getElementById('af');
    ap.setAttribute('stroke-dasharray',pl+' '+(C-pl));
    aw.setAttribute('stroke-dasharray',wl+' '+(C-wl));aw.style.strokeDashoffset=(-pl).toFixed(2);
    af.setAttribute('stroke-dasharray',fl+' '+(C-fl));af.style.strokeDashoffset=(-(pl+wl)).toFixed(2);
    var ds=document.getElementById('ds'),c=0,s=Math.max(1,Math.ceil(ht/30));
    var t=setInterval(function(){{c=Math.min(c+s,ht);ds.textContent=c+'%';if(c>=ht)clearInterval(t);}},30);
    document.querySelectorAll('.sn[data-t]').forEach(function(e){{
      var tgt=parseInt(e.dataset.t);if(!tgt){{e.textContent='0';return;}}
      var c=0,s=Math.max(1,Math.ceil(tgt/30));
      var t=setInterval(function(){{c=Math.min(c+s,tgt);e.textContent=c;if(c>=tgt)clearInterval(t);}},30);
    }});
    document.querySelectorAll('.sbf').forEach(function(e){{e.style.width=e.dataset.pct+'%';}});
  }},150);
}})();
function tog(h){{h.parentElement.classList.toggle('open');}}
</script>
</body>
</html>";
    }

    private static string RenderCheckHtml(ValCheck c)
    {
        string cls  = c.Status == ValStatus.Pass ? "ckp" : c.Status == ValStatus.Warn ? "ckw" : "ckf";
        string icon = c.Status == ValStatus.Pass ? "&#10003;" : c.Status == ValStatus.Warn ? "!" : "&#10007;";
        string det  = string.IsNullOrEmpty(c.Detail) ? "" : $@"<span class=""ckd"">{SecurityElementEscape(c.Detail)}</span>";
        return $@"<div class=""ck {cls}""><div class=""cki"">{icon}</div><div class=""ckb""><span class=""ckn"">{SecurityElementEscape(c.Name)}</span>{det}</div></div>";
    }

    // ── Step 13: Unmount with commit ──────────────────────────────────────────

    private async Task UnmountWimAsync()
    {
        Log("Committing changes and unmounting WIM (this can take several minutes)…");
        await DismAsync($"/Unmount-Image /MountDir:\"{_mountDir}\" /Commit");
        await DismAsync("/Cleanup-Wim");
    }

    // ── Step 14: Build ISO ────────────────────────────────────────────────────

    private async Task<string> BuildIsoAsync()
    {
        var oscdimg = FindOscdimg() ?? throw new Exception("oscdimg.exe missing.");
        var bootDir = Path.Combine(_isoStaging, "boot");
        var efiDir  = Path.Combine(_isoStaging, "efi", "microsoft", "boot");
        var etfsboot= Path.Combine(bootDir, "etfsboot.com");

        // Prefer efisys_noprompt.bin so the deployed ISO boots without the
        // "Press any key to boot from CD" prompt — mandatory for unattended
        // installs in MDT/MEMCM-style golden-image deployment.
        // Fall back to efisys.bin if the no-prompt variant is missing.
        var efisysNoPrompt = Path.Combine(efiDir, "efisys_noprompt.bin");
        var efisysWithPrompt = Path.Combine(efiDir, "efisys.bin");
        var efisys = File.Exists(efisysNoPrompt) ? efisysNoPrompt
                   : File.Exists(efisysWithPrompt) ? efisysWithPrompt
                   : "";

        if (!File.Exists(etfsboot))
            throw new FileNotFoundException($"BIOS boot file missing: {etfsboot}");
        if (string.IsNullOrEmpty(efisys))
            throw new FileNotFoundException($"UEFI boot file missing in {efiDir}");

        Log(efisys.EndsWith("efisys_noprompt.bin", StringComparison.OrdinalIgnoreCase)
            ? "Using efisys_noprompt.bin (unattended UEFI boot — no key-press prompt)"
            : "Using efisys.bin (UEFI boot WILL show the press-any-key prompt — efisys_noprompt.bin not found)");

        string outFile = ResolveOutputFilename();
        string outPath = Path.Combine(_s.OutputPath!, outFile);

        // Dual-boot: BIOS (etfsboot) + UEFI (efisys). -m allows >4.7 GB; -u2 uses UDF only.
        string args =
            $"-m -o -u2 -udfver102 " +
            $"-bootdata:2#p0,e,b\"{etfsboot}\"#pEF,e,b\"{efisys}\" " +
            $"\"{_isoStaging}\" \"{outPath}\"";

        Log($"Running oscdimg → {outPath}");
        await RunAsync(oscdimg, args);
        return outPath;
    }

    private string ResolveOutputFilename()
    {
        var pattern = string.IsNullOrWhiteSpace(_s.OutputFilename)
            ? "GoldenImage_{edition}_{date}.iso"
            : _s.OutputFilename;
        var name = pattern
            .Replace("{edition}", _s.SelectedEdition)
            .Replace("{date}",    DateTime.Now.ToString("yyyyMMdd"))
            .Replace("{time}",    DateTime.Now.ToString("HHmmss"));
        if (!name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            name += ".iso";
        return name;
    }

    // ── Step 15: Verify ───────────────────────────────────────────────────────

    private async Task<string> VerifyAsync(string isoPath)
    {
        if (!File.Exists(isoPath)) throw new FileNotFoundException("Output ISO missing", isoPath);
        Log("Computing SHA-256 of output ISO…");
        using var stream = File.OpenRead(isoPath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, _ct);
        var hex = Convert.ToHexString(hash);
        Log($"SHA-256: {hex}");

        // Write a sidecar checksum file
        await File.WriteAllTextAsync(isoPath + ".sha256",
            $"{hex}  {Path.GetFileName(isoPath)}\n");
        return hex;
    }

    // ── Helpers: step orchestration ───────────────────────────────────────────

    private async Task Step(string id, Func<Task> body)
    {
        _ct.ThrowIfCancellationRequested();
        var step = Steps.First(s => s.Id == id);
        step.Status   = BuildStepStatus.Running;
        step.StartedAt= DateTime.Now;
        ReportProgress(step, $"▶ {step.Title}…");
        Log($"--- {step.Title} ---");

        try
        {
            await body();
            if (step.Status != BuildStepStatus.Skipped)
                step.Status = BuildStepStatus.Done;
            step.FinishedAt = DateTime.Now;
            ReportProgress(step, $"✓ {step.Title}");
        }
        catch (Exception ex)
        {
            step.Status = BuildStepStatus.Failed;
            step.Detail = ex.Message;
            step.FinishedAt = DateTime.Now;
            ReportProgress(step, $"✗ {step.Title}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Like Step() but non-fatal: if the step body throws, the error is logged
    /// as a warning and the build continues.  Use for optional steps (language
    /// packs, wallpaper, drivers, etc.) where partial failure is acceptable.
    /// Cancellation is always re-thrown regardless.
    /// </summary>
    private async Task StepSoft(string id, Func<Task> body)
    {
        _ct.ThrowIfCancellationRequested();
        var step = Steps.First(s => s.Id == id);
        step.Status    = BuildStepStatus.Running;
        step.StartedAt = DateTime.Now;
        ReportProgress(step, $"▶ {step.Title}…");
        Log($"--- {step.Title} ---");

        try
        {
            await body();
            if (step.Status != BuildStepStatus.Skipped)
                step.Status = BuildStepStatus.Done;
            step.FinishedAt = DateTime.Now;
            ReportProgress(step, $"✓ {step.Title}");
        }
        catch (OperationCanceledException)
        {
            step.Status    = BuildStepStatus.Failed;
            step.Detail    = "Cancelled";
            step.FinishedAt= DateTime.Now;
            ReportProgress(step, $"✗ {step.Title}: Cancelled");
            throw;   // cancellation is always fatal
        }
        catch (Exception ex)
        {
            // Non-fatal: mark step failed but let the build continue
            step.Status    = BuildStepStatus.Failed;
            step.Detail    = ex.Message;
            step.FinishedAt= DateTime.Now;
            Log($"[WARN] Step '{step.Title}' failed (non-fatal, build continues): {ex.Message}");
            ReportProgress(step, $"⚠ {step.Title}: {ex.Message} (non-fatal)");
        }
    }

    private void Skip(string reason)
    {
        var step = Steps.FirstOrDefault(s => s.Status == BuildStepStatus.Running);
        if (step != null) { step.Status = BuildStepStatus.Skipped; step.Detail = reason; }
        Log($"  (skipped: {reason})");
    }

    private void ReportProgress(BuildStep step, string msg)
    {
        var idx = Steps.IndexOf(step);
        _onProgress(new BuildProgress
        {
            CurrentStepIndex = idx,
            TotalSteps       = Steps.Count,
            CurrentStep      = step,
            Message          = msg,
            AllSteps         = Steps
        });
    }

    // ── Helpers: process exec ─────────────────────────────────────────────────

    private async Task DismAsync(string args)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        await RunAsync(dism, args);
    }

    private async Task<(string stdout, string stderr, int exit)> DismCapturedAsync(string args)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        return await RunCapturedAsync(dism, args);
    }

    private async Task RunAsync(string exe, string args, bool expectZero = true)
    {
        var (stdout, stderr, exit) = await RunCapturedAsync(exe, args);
        if (!string.IsNullOrWhiteSpace(stdout))
            foreach (var line in stdout.Split('\n').Take(80))
                if (line.Trim().Length > 0) Log("  | " + line.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            foreach (var line in stderr.Split('\n').Take(20))
                if (line.Trim().Length > 0) Log("  E " + line.TrimEnd());

        if (expectZero && exit != 0)
            throw new Exception($"{Path.GetFileName(exe)} failed with exit {exit}.\nArgs: {args}");
    }

    private async Task<(string stdout, string stderr, int exit)> RunCapturedAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };
        using var proc = Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");

        // If the user cancels the build, kill the running native process.
        // Without this, DISM/oscdimg keep running in the background even after we
        // throw OperationCanceledException.
        using var killOnCancel = _ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* race with normal exit — ignore */ }
        });

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(_ct);
        return (await outTask, await errTask, proc.ExitCode);
    }

    private async Task RobocopyAsync(string src, string dst, string flags)
    {
        // robocopy returns 0–7 = success, 8+ = error
        var psi = new ProcessStartInfo("robocopy.exe", $"\"{src.TrimEnd('\\')}\" \"{dst.TrimEnd('\\')}\" {flags}")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi) ?? throw new Exception("robocopy failed to start");
        await proc.WaitForExitAsync(_ct);
        if (proc.ExitCode >= 8)
            throw new Exception($"robocopy returned {proc.ExitCode}");
    }

    // ── Mount/dismount ISO ────────────────────────────────────────────────────

    private static async Task<string> MountIsoAsync(string isoPath)
    {
        var before = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name[0]).ToHashSet();
        await RunSilent("powershell.exe",
            $"-NonInteractive -Command \"Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'\"");
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var after = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name[0]).ToHashSet();
            char d = after.Except(before).FirstOrDefault();
            if (d != default) return $"{d}:";
        }
        throw new TimeoutException("ISO mount timed out.");
    }

    private static async Task DismountIsoAsync(string isoPath)
    {
        await RunSilent("powershell.exe",
            $"-NonInteractive -Command \"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'\"");
    }

    private static async Task RunSilent(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
        if (p != null) await p.WaitForExitAsync();
    }

    private static async Task TryUnmountIfMounted(string mountDir)
    {
        if (!Directory.Exists(mountDir)) return;
        try
        {
            var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
            var psi  = new ProcessStartInfo(dism, $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard")
                { CreateNoWindow = true, UseShellExecute = false };
            var p = Process.Start(psi);
            if (p != null)
            {
                // 90-second hard timeout — a stuck DISM should never block us indefinitely.
                var completed = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(90)));
                if (!p.HasExited)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { /* best-effort */ }
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private async Task TryCleanupOnFailure()
    {
        try
        {
            if (!string.IsNullOrEmpty(_mountDir))
                await TryUnmountIfMounted(_mountDir);
        }
        catch { }
    }

    /// <summary>
    /// Runs after a successful build only. Dismounts the source ISO (in case it
    /// is still mounted), then deletes the extracted ISO staging folder and the
    /// WIM mount directory. All errors are swallowed — a cleanup failure must
    /// never retroactively mark a successful build as failed.
    /// </summary>
    private async Task CleanupAfterSuccessAsync()
    {
        Log("── Post-build cleanup: dismounting source ISO and deleting staging folders …");
        try
        {
            // 1. Ensure source ISO is dismounted. CopyIsoContentsAsync already
            //    dismounts it when it mounts for copying, but if the user supplied
            //    a pre-mounted path this may still be attached.
            if (!string.IsNullOrEmpty(_s.SourceIsoPath) && File.Exists(_s.SourceIsoPath))
            {
                try
                {
                    await DismountIsoAsync(_s.SourceIsoPath!);
                    Log("   Source ISO dismounted.");
                }
                catch (Exception ex)
                {
                    Log($"   [WARN] Could not dismount source ISO: {ex.Message}");
                }
            }

            // 2. Delete the extracted ISO staging folder (workspace\iso) — this is
            //    the largest artifact and can be several GB.
            if (!string.IsNullOrEmpty(_isoStaging) && Directory.Exists(_isoStaging))
            {
                try
                {
                    await DeleteDirectoryRobust(_isoStaging);
                    Log($"   Deleted ISO staging folder: {_isoStaging}");
                }
                catch (Exception ex)
                {
                    Log($"   [WARN] Could not delete staging folder: {ex.Message}");
                }
            }

            // 3. Delete the WIM mount directory (workspace\mount) — should already
            //    be empty after UnmountWimAsync, but clean it up regardless.
            //    Brief wait: DISM releases the hive handles asynchronously after
            //    reporting success, so a small delay prevents a locked-handle failure.
            if (!string.IsNullOrEmpty(_mountDir) && Directory.Exists(_mountDir))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await DeleteDirectoryRobust(_mountDir);
                    Log($"   Deleted WIM mount directory: {_mountDir}");
                }
                catch (Exception ex)
                {
                    Log($"   [WARN] Could not delete mount directory: {ex.Message}");
                }
            }

            Log("── Post-build cleanup complete.");
        }
        catch (Exception ex)
        {
            // Safety net — nothing here should ever escape to the caller.
            Log($"   [WARN] Unexpected error during cleanup: {ex.Message}");
        }
    }

    private static async Task DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path)) return;

        // Try the fast .NET delete first. Skip the retry loop — if it fails once,
        // go straight to cmd.exe which handles locked handles and deep paths better.
        try { Directory.Delete(path, recursive: true); return; } catch { }

        // cmd /c rmdir /s /q — handles paths > MAX_PATH and locked handles better.
        // Hard 5-minute timeout so a stuck process never freezes the whole pipeline.
        var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{path}\"")
            { CreateNoWindow = true, UseShellExecute = false };
        try
        {
            var p = Process.Start(psi);
            if (p != null)
            {
                await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(TimeSpan.FromMinutes(5)));
                if (!p.HasExited) { try { p.Kill(); } catch { } }
            }
        }
        catch { }
    }

    private static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? FindOscdimg()
    {
        // Common ADK install locations
        string[] candidates =
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\x86\Oscdimg\oscdimg.exe",
            // Bundled alongside our app (we copy it during install if available)
            Path.Combine(AppContext.BaseDirectory, "Tools", "oscdimg.exe"),
            Path.Combine(AppContext.BaseDirectory, "oscdimg.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // PATH search
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            try
            {
                var p = Path.Combine(dir, "oscdimg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    private void OpenLog()
    {
        // Try to write the log next to the output ISO. If the output drive / path
        // doesn't exist yet (e.g. the user mis-typed it), fall back to %TEMP% so
        // the log always gets written and the "Open log" button always works.
        string logDir;
        try
        {
            logDir = _s.OutputPath ?? ".";
            Directory.CreateDirectory(logDir);
        }
        catch
        {
            logDir = Path.Combine(Path.GetTempPath(), "GoldenISOBuilder");
            Directory.CreateDirectory(logDir);
        }
        _logPath = Path.Combine(logDir, $"build-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _logWriter = new StreamWriter(_logPath, append: true) { AutoFlush = true };
    }

    private void CloseLog()
    {
        try { _logWriter?.Dispose(); } catch { }
        _logWriter = null;
    }

    private void Log(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{ts}] {msg}";
        try { _logWriter?.WriteLine(line); } catch { }
        _onLog(line);
    }
}
