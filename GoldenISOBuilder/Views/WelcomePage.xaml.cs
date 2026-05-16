using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;
using GoldenISOBuilder.Views;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class WelcomePage : UserControl
{
    public event Action<string, int>? NavigateRequested;

    private bool _showAllHistory = false;

    public WelcomePage()
    {
        InitializeComponent();
        // Loaded fires once on startup — covers initial display.
        Loaded += (_, _) => { RefreshStats(); RefreshRecentBuilds(); };
        // IsVisibleChanged fires every time the user navigates back to the home page,
        // ensuring build history is always up-to-date after a build completes.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { RefreshStats(); RefreshRecentBuilds(); }
        };
    }

    /// <summary>
    /// Reads build history from disk and populates the Recent Builds list.
    /// Call this whenever history may have changed (page load, build completed).
    /// </summary>
    public void RefreshRecentBuilds()
    {
        var records = BuildHistoryStore.Load();

        if (records.Count == 0)
        {
            RecentBuildsEmpty.Visibility = Visibility.Visible;
            RecentBuildsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RecentBuildsEmpty.Visibility = Visibility.Collapsed;
        RecentBuildsPanel.Visibility = Visibility.Visible;

        RecentBuildsList.Children.Clear();

        // Show all or the 8 most-recent entries (newest first)
        var toShow = _showAllHistory
            ? records.AsEnumerable().Reverse()
            : records.AsEnumerable().Reverse().Take(8);

        foreach (var rec in toShow)
        {
            var row = BuildHistoryRow(rec);
            RecentBuildsList.Children.Add(row);
        }

        // Update "View all" / "Show less" label
        if (ViewAllBtnText != null)
        {
            bool hasMore = records.Count > 8;
            if (!hasMore)
            {
                // All records fit in the default view — hide the toggle
                ViewAllBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                ViewAllBtn.Visibility = Visibility.Visible;
                ViewAllBtnText.Text   = _showAllHistory ? "Show less" : $"View all ({records.Count})";
            }
        }
    }

    private Border BuildHistoryRow(BuildRecord rec)
    {
        var okBrush  = (Brush)Application.Current.Resources["OkBrush"];
        var errBrush = (Brush)Application.Current.Resources["ErrBrush"];
        var fg0      = (Brush)Application.Current.Resources["FG0Brush"];
        var fg2      = (Brush)Application.Current.Resources["FG2Brush"];
        var fg3      = (Brush)Application.Current.Resources["FG3Brush"];

        // Status dot
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width      = 8,
            Height     = 8,
            Fill       = rec.Success ? okBrush : errBrush,
            Margin     = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Edition / filename label
        var isoName = !string.IsNullOrEmpty(rec.IsoPath)
            ? System.IO.Path.GetFileName(rec.IsoPath)
            : rec.EditionName;
        var nameLabel = new TextBlock
        {
            Text              = string.IsNullOrEmpty(isoName) ? "(unnamed)" : isoName,
            FontSize          = 12.5,
            FontWeight        = FontWeights.Medium,
            Foreground        = fg0,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Duration
        var dur = TimeSpan.FromSeconds(rec.DurationSeconds);
        var durText = dur.TotalHours >= 1
            ? $"{(int)dur.TotalHours}h {dur.Minutes}m"
            : dur.TotalMinutes >= 1
                ? $"{(int)dur.TotalMinutes}m {dur.Seconds:D2}s"
                : $"{dur.Seconds}s";

        // Date + duration
        var metaLabel = new TextBlock
        {
            Text              = $"{rec.CompletedAt:dd MMM yyyy  HH:mm}  ·  {durText}",
            FontSize          = 11,
            Foreground        = fg2,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Status label
        var statusLabel = new TextBlock
        {
            Text              = rec.Success ? "✓ Success" : "✗ Failed",
            FontSize          = 11,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = rec.Success ? okBrush : errBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var mainSp = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        mainSp.Children.Add(nameLabel);
        mainSp.Children.Add(metaLabel);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(dot,         0);
        Grid.SetColumn(mainSp,      1);
        Grid.SetColumn(statusLabel, 2);

        inner.Children.Add(dot);
        inner.Children.Add(mainSp);
        inner.Children.Add(statusLabel);

        var row = new Border
        {
            Padding         = new Thickness(14, 10, 14, 10),
            BorderBrush     = (Brush)Application.Current.Resources["LineSoftBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = inner
        };

        // If ISO still exists, clicking the row opens it in Explorer
        if (!string.IsNullOrEmpty(rec.IsoPath) && System.IO.File.Exists(rec.IsoPath))
        {
            row.Cursor = System.Windows.Input.Cursors.Hand;
            row.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"/select,\"{rec.IsoPath}\"") { UseShellExecute = true });
                }
                catch { }
            };
        }

        return row;
    }

    /// <summary>
    /// Reads build history from disk and updates the three stat counters.
    /// Called on page load and can be called again after a build completes.
    /// </summary>
    public void RefreshStats()
    {
        var records = BuildHistoryStore.Load();

        if (records.Count == 0)
        {
            // Leave all three as "—"
            return;
        }

        // Builds completed
        StatBuildsCount.Text = records.Count.ToString();

        // Success rate
        int successCount = records.Count(r => r.Success);
        double rate = (double)successCount / records.Count * 100.0;
        StatSuccessRate.Text = $"{rate:F0}%";

        // Colour: green if ≥90 %, amber if ≥70 %, red below
        StatSuccessRate.Foreground = rate >= 90
            ? (Brush)Application.Current.Resources["OkBrush"]
            : rate >= 70
                ? (Brush)Application.Current.Resources["WarnBrush"]
                : (Brush)Application.Current.Resources["ErrBrush"];

        // Avg duration (successful builds only — failed ones skew the number)
        if (successCount > 0)
        {
            double avgSecs = records.Where(r => r.Success)
                                    .Average(r => r.DurationSeconds);
            var avg = TimeSpan.FromSeconds(avgSecs);
            StatAvgDuration.Text = avg.TotalHours >= 1
                ? $"{(int)avg.TotalHours}h {avg.Minutes}m"
                : avg.TotalMinutes >= 1
                    ? $"{(int)avg.TotalMinutes}m {avg.Seconds:D2}s"
                    : $"{avg.Seconds}s";
        }
    }

    private void NewBuildBtn_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("wizard", 0);

    private void QuickBuildBtn_Click(object sender, RoutedEventArgs e)
        => StartQuickBuild();

    private void OpenProfileBtn_Click(object sender, RoutedEventArgs e)
        => OpenProfile();

    private void NewBuildTile_Click(object sender, MouseButtonEventArgs e)
        => NavigateRequested?.Invoke("wizard", 0);

    private void QuickBuildTile_Click(object sender, MouseButtonEventArgs e)
        => StartQuickBuild();

    private void OpenProfileTile_Click(object sender, MouseButtonEventArgs e)
        => OpenProfile();

    private void RecentItem_Click(object sender, MouseButtonEventArgs e)
        => NavigateRequested?.Invoke("wizard", 4);

    private void OpenProfile()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Open Build Profile",
            Filter = "Golden ISO Profile|*.gibprofile;*.json|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json    = File.ReadAllText(dlg.FileName);
            var loaded  = JsonSerializer.Deserialize<BuildSession>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded == null)
            {
                AppDialog.Alert(this, "That file isn't a valid ALE Image Forge profile.",
                    "Open Profile", AppDialogIcon.Warning);
                return;
            }
            CopyToCurrent(loaded);

            // Validate every path in the loaded profile and warn the user about
            // anything that is missing before they reach the build step.
            var issues = CollectMissingPaths(BuildSession.Current);
            if (issues.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{issues.Count} path(s) referenced in this profile could not be found on this machine.");
                sb.AppendLine("Please validate and fix these paths in the wizard before building:");

                string? lastGroup = null;
                foreach (var (group, detail) in issues)
                {
                    if (group != lastGroup)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"  ▸ {group}");
                        lastGroup = group;
                    }
                    sb.AppendLine($"    {detail}");
                }

                AppDialog.Alert(this, sb.ToString().TrimEnd(),
                    "Profile Validation — Missing Paths", AppDialogIcon.Warning);
            }

            NavigateRequested?.Invoke("wizard", 0);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Couldn't load profile:\n\n{ex.Message}",
                "Open Profile", AppDialogIcon.Error);
        }
    }

    /// <summary>
    /// Copies every user-configurable field from a deserialised profile into the
    /// singleton BuildSession.  The wizard pages hold a direct reference to
    /// BuildSession.Current so we update it in-place rather than replacing the object.
    ///
    /// IMPORTANT: whenever a new field is added to BuildSession, add it here too.
    /// Fields omitted from this method silently revert to their default values
    /// after a profile import — the exact bug this method was audited to fix.
    /// </summary>
    private static void CopyToCurrent(BuildSession src)
    {
        var d = BuildSession.Current;

        // ── Step 1: Source & Output ───────────────────────────────────────────
        d.SourceIsoPath           = src.SourceIsoPath;
        d.SelectedEdition         = src.SelectedEdition;
        d.SelectedArch            = src.SelectedArch;
        d.SelectedImage           = src.SelectedImage;
        d.AvailableImages         = src.AvailableImages;
        d.OutputPath              = src.OutputPath;
        d.WorkspacePath           = src.WorkspacePath;
        d.OutputFilename          = src.OutputFilename;

        // ISO language fields (windowsPE boot language + deployed OS locale)
        d.IsoSourceLanguage       = src.IsoSourceLanguage;
        d.IsoAvailableLanguages   = src.IsoAvailableLanguages;
        d.TargetOsLocale          = src.TargetOsLocale;

        // ── Step 2: Assets ────────────────────────────────────────────────────
        d.WallpaperPath           = src.WallpaperPath;
        d.LockScreenPath          = src.LockScreenPath;
        d.StagedApps              = src.StagedApps;
        d.StagedFiles             = src.StagedFiles;
        d.PublicDesktopFiles      = src.PublicDesktopFiles;
        d.IncludeDeploymentScripts = src.IncludeDeploymentScripts;
        d.DeploymentScripts       = src.DeploymentScripts
                                        .Select(s => new DeploymentScript { Path = s.Path, Trigger = s.Trigger })
                                        .ToList();
        d.LanguagePackPaths       = src.LanguagePackPaths;
        d.DriverFolderPaths       = src.DriverFolderPaths;
        d.ScheduledTasks          = src.ScheduledTasks;

        // Auto-fetched Windows Updates (.msu) and OEM driver packs — cached
        // locally under %LOCALAPPDATA%\GoldenISOBuilder\Cache\. Stored paths
        // may be stale if the cache was cleared or the profile was loaded on
        // a different machine; CollectMissingPaths surfaces these to the user.
        d.HostnameTemplate        = string.IsNullOrWhiteSpace(src.HostnameTemplate)
                                      ? "{PREFIX}{SERIAL}"
                                      : src.HostnameTemplate;

        // Windows 11 UX & Privacy baseline
        d.DisableCopilot          = src.DisableCopilot;
        d.DisableRecall           = src.DisableRecall;
        d.DisableWidgets          = src.DisableWidgets;
        d.DisableChatIcon         = src.DisableChatIcon;
        d.DisableConsumerFeatures = src.DisableConsumerFeatures;

        // OneDrive per-machine uninstall
        d.UninstallOneDrive       = src.UninstallOneDrive;

        // Trusted certificates
        d.Certificates            = src.Certificates ?? [];

        // Custom fonts
        d.Fonts                   = src.Fonts ?? [];
        d.UpdatesMsuPaths         = src.UpdatesMsuPaths ?? [];
        d.AutoFetchedDriverPacks  = src.AutoFetchedDriverPacks ?? [];

        // ── Step 3: Customisations ────────────────────────────────────────────
        d.BloatwareToRemove       = src.BloatwareToRemove;

        // Security toggles
        d.EnableBitLocker         = src.EnableBitLocker;
        d.BitLockerDriveLetter    = src.BitLockerDriveLetter;
        d.BitLockerSaveRecoveryKey = src.BitLockerSaveRecoveryKey;
        d.BitLockerKeyFolder      = src.BitLockerKeyFolder;
        d.EnableDefenderAtp       = src.EnableDefenderAtp;
        d.DisableSmbV1            = src.DisableSmbV1;
        d.DisableTelemetry        = src.DisableTelemetry;

        // System defaults
        d.DarkMode                = src.DarkMode;
        d.ShowFileExtensions      = src.ShowFileExtensions;
        d.ShowHiddenFiles         = src.ShowHiddenFiles;
        d.EnableHyperV            = src.EnableHyperV;

        // Group policies
        d.GroupPolicies           = src.GroupPolicies
                                        .Select(g => new GroupPolicyEntry
                                        {
                                            DisplayName  = g.DisplayName,
                                            CategoryPath = g.CategoryPath,
                                            PolicyClass  = g.PolicyClass,
                                            RegistryKey  = g.RegistryKey,
                                            ValueName    = g.ValueName,
                                            State        = g.State,
                                            ValueType    = g.ValueType,
                                            Value        = g.Value,
                                        }).ToList();
        d.AdmxSourceMode          = src.AdmxSourceMode;
        d.CustomAdmxPath          = src.CustomAdmxPath;

        // ── Step 4: Admin Account ─────────────────────────────────────────────
        d.AdminUsername           = src.AdminUsername;
        d.AdminPassword           = src.AdminPassword;
        d.AutoLoginEnabled        = src.AutoLoginEnabled;
        d.PasswordNeverExpires    = src.PasswordNeverExpires;

        // ── Step 5: Registry ──────────────────────────────────────────────────
        d.CustomRegistryEntries   = src.CustomRegistryEntries;

        // ── Step 6: Advanced ──────────────────────────────────────────────────
        d.OrgName                 = src.OrgName;
        d.RegisteredOwner         = src.RegisteredOwner;
        d.ComputerPrefix          = src.ComputerPrefix;
        d.ProductKey              = src.ProductKey;
        d.SkipOobe                = src.SkipOobe;
        d.AutoLogonCount          = src.AutoLogonCount;
        d.PowerPlan               = src.PowerPlan;
        d.TimeZone                = src.TimeZone;
        d.OemManufacturer         = src.OemManufacturer;
        d.OemModel                = src.OemModel;
        d.OemSupportUrl           = src.OemSupportUrl;
        d.EnabledFeatures         = src.EnabledFeatures;
        d.DisabledFeatures        = src.DisabledFeatures;
    }

    /// <summary>
    /// Quick Build re-runs the last saved configuration. If there's no ISO source
    /// loaded yet (i.e. fresh app launch or never built before) we send the user
    /// through the wizard so they can configure properly.
    /// </summary>
    private void StartQuickBuild()
    {
        var s = BuildSession.Current;
        if (string.IsNullOrEmpty(s.SourceIsoPath) || s.SelectedImage == null)
        {
            AppDialog.Alert(this,
                "No saved configuration found. Please walk through the wizard first.",
                "Quick Build", AppDialogIcon.Info);
            NavigateRequested?.Invoke("wizard", 0);
            return;
        }
        if (!System.IO.File.Exists(s.SourceIsoPath))
        {
            AppDialog.Alert(this,
                $"The source ISO no longer exists:\n{s.SourceIsoPath}\n\nPlease select a new ISO in Step 1.",
                "Quick Build", AppDialogIcon.Warning);
            NavigateRequested?.Invoke("wizard", 0);
            return;
        }
        NavigateRequested?.Invoke("progress", 0);
    }

    private void ViewAllHistory_Click(object sender, RoutedEventArgs e)
    {
        // Toggle between showing the last 8 and showing all records in-app.
        _showAllHistory = !_showAllHistory;
        RefreshRecentBuilds();

        // Scroll down so the newly revealed rows are visible
        if (_showAllHistory)
        {
            if (this.Parent is System.Windows.Controls.ScrollViewer sv)
                sv.ScrollToEnd();
        }
    }

    /// <summary>Public refresh — called from MainWindow after a build completes.</summary>
    public void Refresh()
    {
        RefreshStats();
        RefreshRecentBuilds();
    }

    // ── Profile path validation ───────────────────────────────────────────────

    /// <summary>
    /// Checks every path-based field in <paramref name="s"/> and returns a list
    /// of (group, detail) pairs for anything that is missing on this machine.
    /// Called immediately after loading a .gibprofile so the user is told about
    /// stale paths before they reach the build step.
    /// </summary>
    private static List<(string Group, string Detail)> CollectMissingPaths(BuildSession s)
    {
        var issues = new List<(string, string)>();

        // ── Source ISO (critical — build cannot start without it) ─────────────
        if (!string.IsNullOrWhiteSpace(s.SourceIsoPath) && !System.IO.File.Exists(s.SourceIsoPath))
            issues.Add(("Source ISO", s.SourceIsoPath));

        // ── Wallpaper ─────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(s.WallpaperPath) && !System.IO.File.Exists(s.WallpaperPath))
            issues.Add(("Wallpaper", s.WallpaperPath));

        // ── Lock-screen image ─────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(s.LockScreenPath) && !System.IO.File.Exists(s.LockScreenPath))
            issues.Add(("Lock screen image", s.LockScreenPath));

        // ── Trusted certificates ──────────────────────────────────────────────
        foreach (var cert in s.Certificates)
            if (!string.IsNullOrWhiteSpace(cert.SourcePath) && !System.IO.File.Exists(cert.SourcePath))
                issues.Add(($"Certificate ({cert.Store})", cert.SourcePath));

        // ── Custom fonts ──────────────────────────────────────────────────────
        foreach (var font in s.Fonts)
            if (!string.IsNullOrWhiteSpace(font.SourcePath) && !System.IO.File.Exists(font.SourcePath))
                issues.Add(("Custom font", font.SourcePath));

        // ── Staged app installers ─────────────────────────────────────────────
        foreach (var app in s.StagedApps)
        {
            if (!string.IsNullOrWhiteSpace(app.FilePath) && !System.IO.File.Exists(app.FilePath))
                issues.Add(($"App installer — {app.Name}", app.FilePath));

            if (!string.IsNullOrWhiteSpace(app.MstPath) && !System.IO.File.Exists(app.MstPath))
                issues.Add(($"MST transform — {app.Name}", app.MstPath));
        }

        // ── Staged files (extra files copied into the image) ──────────────────
        foreach (var sf in s.StagedFiles)
            if (!string.IsNullOrWhiteSpace(sf.SourcePath) && !System.IO.File.Exists(sf.SourcePath))
                issues.Add(("Staged file", sf.SourcePath));

        // ── Public desktop files ──────────────────────────────────────────────
        foreach (var pf in s.PublicDesktopFiles)
            if (!string.IsNullOrWhiteSpace(pf) && !System.IO.File.Exists(pf))
                issues.Add(("Public desktop file", pf));

        // ── Language packs (.cab files) ───────────────────────────────────────
        foreach (var lp in s.LanguagePackPaths)
            if (!string.IsNullOrWhiteSpace(lp) && !System.IO.File.Exists(lp))
                issues.Add(("Language pack", lp));

        // ── Driver folders ────────────────────────────────────────────────────
        foreach (var df in s.DriverFolderPaths)
            if (!string.IsNullOrWhiteSpace(df) && !System.IO.Directory.Exists(df))
                issues.Add(("Driver folder", df));

        // ── Auto-fetched Windows Updates (.msu in the catalogue cache) ───────
        foreach (var msu in s.UpdatesMsuPaths)
            if (!string.IsNullOrWhiteSpace(msu) && !System.IO.File.Exists(msu))
                issues.Add(("Windows Update (auto-fetched) — re-fetch in Step 2",
                            System.IO.Path.GetFileName(msu)));

        // ── Auto-fetched OEM driver packs ─────────────────────────────────────
        // Lenovo and HP packs are extracted-folder paths; Dell packs are CAB
        // files. Accept either form so we don't false-positive on Dell entries.
        foreach (var dp in s.AutoFetchedDriverPacks)
        {
            if (string.IsNullOrWhiteSpace(dp.LocalCabPath)) continue;
            if (System.IO.File.Exists(dp.LocalCabPath) ||
                System.IO.Directory.Exists(dp.LocalCabPath)) continue;
            issues.Add(
                ($"Driver pack (auto-fetched) — {dp.Vendor} {dp.ModelName} ({dp.SystemId}). Re-fetch in Step 2",
                 dp.LocalCabPath));
        }

        // ── Deployment scripts ────────────────────────────────────────────────
        foreach (var ds in s.DeploymentScripts)
            if (!string.IsNullOrWhiteSpace(ds.Path) && !System.IO.File.Exists(ds.Path))
                issues.Add(("Deployment script", ds.Path));

        // ── Output / Workspace folders (warn — build will fail if missing) ────
        if (!string.IsNullOrWhiteSpace(s.OutputPath) && !System.IO.Directory.Exists(s.OutputPath))
            issues.Add(("Output folder", s.OutputPath));

        if (!string.IsNullOrWhiteSpace(s.WorkspacePath) && !System.IO.Directory.Exists(s.WorkspacePath))
            issues.Add(("Workspace folder", s.WorkspacePath));

        return issues;
    }
}
