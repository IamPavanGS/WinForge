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
            NavigateRequested?.Invoke("wizard", 0);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Couldn't load profile:\n\n{ex.Message}",
                "Open Profile", AppDialogIcon.Error);
        }
    }

    /// <summary>Copies values from a deserialised profile into the singleton BuildSession
    /// (the rest of the app keeps a reference to BuildSession.Current).</summary>
    private static void CopyToCurrent(BuildSession src)
    {
        var d = BuildSession.Current;
        d.SourceIsoPath           = src.SourceIsoPath;
        d.SelectedEdition         = src.SelectedEdition;
        d.SelectedArch            = src.SelectedArch;
        d.SelectedImage           = src.SelectedImage;
        d.AvailableImages         = src.AvailableImages;
        d.OutputPath              = src.OutputPath;
        d.WorkspacePath           = src.WorkspacePath;
        d.OutputFilename          = src.OutputFilename;
        d.WallpaperPath           = src.WallpaperPath;
        d.StagedApps              = src.StagedApps;
        d.PublicDesktopFiles      = src.PublicDesktopFiles;
        d.IncludeDeploymentScripts = src.IncludeDeploymentScripts;
        d.DeploymentScripts        = src.DeploymentScripts
                                         .Select(s => new DeploymentScript { Path = s.Path, Trigger = s.Trigger })
                                         .ToList();
        d.BloatwareToRemove       = src.BloatwareToRemove;
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
        d.DisableSmbV1            = src.DisableSmbV1;
        d.DisableTelemetry        = src.DisableTelemetry;
        d.EnableDefenderAtp       = src.EnableDefenderAtp;
        d.DarkMode                = src.DarkMode;
        d.ShowFileExtensions      = src.ShowFileExtensions;
        d.ShowHiddenFiles         = src.ShowHiddenFiles;   // FIX #5: was missing
        d.AdminUsername           = src.AdminUsername;
        d.AdminPassword           = src.AdminPassword;
        d.AutoLoginEnabled        = src.AutoLoginEnabled;
        d.PasswordNeverExpires    = src.PasswordNeverExpires;
        d.CustomRegistryEntries   = src.CustomRegistryEntries;
        d.OrgName                 = src.OrgName;
        d.RegisteredOwner         = src.RegisteredOwner;
        d.ComputerPrefix          = src.ComputerPrefix;
        d.ProductKey              = src.ProductKey;
        d.SkipOobe                = src.SkipOobe;
        d.AutoLogonCount          = src.AutoLogonCount;
        d.PowerPlan               = src.PowerPlan;
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
}
