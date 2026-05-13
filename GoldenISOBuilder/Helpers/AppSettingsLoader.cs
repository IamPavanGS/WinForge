using System.IO;
using System.Text.Json;
using System.Windows;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Helpers;

/// <summary>
/// Loads persisted user settings (theme, default paths) at application startup,
/// BEFORE the main window is shown.  This ensures the correct colour scheme is
/// applied on first render and the Step 1 path boxes are pre-filled.
///
/// The settings file is shared with SettingsPage — both read/write the same JSON.
/// </summary>
public static class AppSettingsLoader
{
    // Store settings in %LOCALAPPDATA%\GoldenISOBuilder\ so they survive rebuilds,
    // version upgrades, and relocation of the exe.  The exe directory changes whenever
    // the project is built to a new output folder; LocalAppData is stable per user.
    private static readonly string SettingsDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoldenISOBuilder");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    // Minimal representation — only the fields we need at startup.
    // SettingsPage has its own full AppSettings class that covers everything.
    private sealed class StartupSettings
    {
        public string Theme                { get; set; } = "dark";
        public string DefaultOutputPath    { get; set; } = "";
        public string DefaultWorkspacePath { get; set; } = "";
        public bool   SoundOnComplete      { get; set; } = true;
        public bool   VerifyIsoAfterBuild      { get; set; } = true;
        public bool   CleanWorkspaceAfterBuild { get; set; } = true;
        public string WimCompression           { get; set; } = "max";

        /// <summary>Master toggle for the Windows-Update + driver-pack auto-fetch
        /// features. Off by default so existing builds are unaffected.</summary>
        public bool   EnableAutoFetchFeatures  { get; set; } = false;
    }

    /// <summary>
    /// Apply persisted settings at startup.  Called from App.OnStartup,
    /// before base.OnStartup() opens the main window.
    /// </summary>
    public static void Apply()
    {
        try
        {
            // One-time migration: if settings.json exists next to the old exe location
            // but the new LocalAppData path doesn't exist yet, copy it over so the user
            // doesn't lose their saved paths after a rebuild.
            if (!File.Exists(SettingsPath))
            {
                var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                if (File.Exists(legacyPath))
                {
                    Directory.CreateDirectory(SettingsDir);
                    File.Copy(legacyPath, SettingsPath);
                }
            }

            if (!File.Exists(SettingsPath)) return;

            var json     = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings == null) return;

            // ── 1. Apply theme ─────────────────────────────────────────────────
            if (string.Equals(settings.Theme, "light", StringComparison.OrdinalIgnoreCase))
            {
                var dicts    = Application.Current.Resources.MergedDictionaries;
                var toRemove = dicts.FirstOrDefault(d =>
                    d.Source?.OriginalString.Contains("AppColors.xaml") == true);
                if (toRemove != null) dicts.Remove(toRemove);

                dicts.Insert(0, new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Resources/AppColorsLight.xaml")
                });
            }

            // ── 2. Pre-populate default paths into the live BuildSession ────────
            // Step1Page reads BuildSession.Current.OutputPath / WorkspacePath on
            // Loaded, so setting them here means the boxes arrive pre-filled.
            if (!string.IsNullOrEmpty(settings.DefaultOutputPath))
                BuildSession.Current.OutputPath = settings.DefaultOutputPath;

            if (!string.IsNullOrEmpty(settings.DefaultWorkspacePath))
                BuildSession.Current.WorkspacePath = settings.DefaultWorkspacePath;
        }
        catch (Exception ex)
        {
            // Log to crash.log so startup failures are diagnosable — never throw.
            LogError("AppSettingsLoader.Apply", ex);
        }
    }

    /// <summary>
    /// Returns true if the user wants a sound played on build completion.
    /// Defaults to true if the file is missing or the field is absent.
    /// </summary>
    public static bool ReadSoundOnComplete()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            var json = File.ReadAllText(SettingsPath);
            var s    = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return s?.SoundOnComplete ?? true;
        }
        catch { return true; }
    }

    /// <summary>
    /// Reads ONLY the saved output and workspace paths from settings.json.
    /// Used by Step1Page as a direct fallback if BuildSession wasn't populated
    /// at startup (e.g. the file was written after Apply() ran).
    /// Returns ("", "") if the file is missing or unreadable.
    /// </summary>
    public static (string OutputPath, string WorkspacePath) ReadPaths()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ("", "");
            var json = File.ReadAllText(SettingsPath);
            var s    = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (s?.DefaultOutputPath ?? "", s?.DefaultWorkspacePath ?? "");
        }
        catch (Exception ex)
        {
            LogError("AppSettingsLoader.ReadPaths", ex);
            return ("", "");
        }
    }

    /// <summary>Reads the auto-fetch master toggle. Defaults to false.</summary>
    public static bool ReadEnableAutoFetchFeatures()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var json = File.ReadAllText(SettingsPath);
            var s    = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return s?.EnableAutoFetchFeatures ?? false;
        }
        catch { return false; }
    }

    /// <summary>Persists the auto-fetch master toggle.</summary>
    public static void SaveEnableAutoFetchFeatures(bool value)
    {
        try
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented               = true
            };
            Dictionary<string, object>? raw = null;
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                raw = JsonSerializer.Deserialize<Dictionary<string, object>>(json, opts);
            }
            raw ??= new Dictionary<string, object>();
            raw["EnableAutoFetchFeatures"] = value;

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(raw, opts));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Returns build-time settings. Defaults to the safe/default values if file is missing.</summary>
    public static (bool verify, bool clean, string compression) ReadBuildSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return (true, true, "max");
            var json = File.ReadAllText(SettingsPath);
            var s    = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (s?.VerifyIsoAfterBuild ?? true,
                    s?.CleanWorkspaceAfterBuild ?? true,
                    s?.WimCompression ?? "max");
        }
        catch { return (true, true, "max"); }
    }

    private static void LogError(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.AppendAllText(
                Path.Combine(SettingsDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch { }
    }

    /// <summary>
    /// Returns the theme stored in settings ("dark" or "light").
    /// Returns "dark" if the file is missing or unreadable.
    /// Used by SettingsPage to initialise its _currentTheme field correctly.
    /// </summary>
    public static string LoadTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return "dark";
            var json = File.ReadAllText(SettingsPath);
            var s    = JsonSerializer.Deserialize<StartupSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return s?.Theme ?? "dark";
        }
        catch { return "dark"; }
    }

    /// <summary>
    /// Persists the output and workspace paths into settings.json so they
    /// survive app restarts.  Called from Step1Page whenever the user changes
    /// the output or workspace path.
    /// </summary>
    public static void SavePaths(string outputPath, string workspacePath)
    {
        try
        {
            // Read the full JSON object (preserving all other fields), update only
            // the two path fields, then write it back.
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented               = true
            };
            Dictionary<string, object>? raw = null;
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                raw = JsonSerializer.Deserialize<Dictionary<string, object>>(json, opts);
            }
            raw ??= new Dictionary<string, object>();

            raw["DefaultOutputPath"]    = outputPath;
            raw["DefaultWorkspacePath"] = workspacePath;

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(raw, opts));
        }
        catch { /* non-fatal */ }
    }
}
