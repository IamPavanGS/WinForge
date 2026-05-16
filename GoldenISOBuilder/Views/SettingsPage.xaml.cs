using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;   // BuildHistoryStore, AdkInstaller
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class SettingsPage : UserControl
{
    /// <summary>Fired after build history is cleared — MainWindow uses this to refresh WelcomePage.</summary>
    public event Action? HistoryCleared;

    // Detect actual theme from loaded resource dictionaries instead of a hardcoded
    // default — this stays correct even when AppSettingsLoader.Apply() switched
    // to light mode before the main window opened.
    private static string _currentTheme =
        Application.Current?.Resources.MergedDictionaries
            .Any(d => d.Source?.OriginalString.Contains("AppColorsLight.xaml") == true) == true
        ? "light" : "dark";

    // Store in %LOCALAPPDATA%\GoldenISOBuilder\ so settings survive rebuilds,
    // version upgrades, and relocation of the exe.
    private static readonly string SettingsDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoldenISOBuilder");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private AppSettings _settings = new();

    // ADK install state
    private CancellationTokenSource? _adkCts;
    private Task<AdkInstallResult>?  _adkTask;

    /// <summary>Public hook so MainWindow's Help button can scroll to About.</summary>
    public void ScrollToAbout() => AboutCard?.BringIntoView();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded           += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        BindToUi();
        UpdateAboutInfo();
        UpdateAdkStatus();

        // Toggles save immediately — users expect a flip to stick right away.
        // Paths do NOT auto-save; the "Save Settings" button handles those.
        AutoSaveProfilesToggle.Checked   += (_, _) => { _settings.AutoSaveProfiles = true; Save(); };
        AutoSaveProfilesToggle.Unchecked += (_, _) => { _settings.AutoSaveProfiles = false; Save(); };
        VerifyIsoToggle.Checked   += (_, _) => { _settings.VerifyIsoAfterBuild = true; Save(); };
        VerifyIsoToggle.Unchecked += (_, _) => { _settings.VerifyIsoAfterBuild = false; Save(); };
        CleanWorkspaceToggle.Checked   += (_, _) => { _settings.CleanWorkspaceAfterBuild = true; Save(); };
        CleanWorkspaceToggle.Unchecked += (_, _) => { _settings.CleanWorkspaceAfterBuild = false; Save(); };
        BuildCompleteToastToggle.Checked   += (_, _) => { _settings.BuildCompleteToast = true; Save(); };
        BuildCompleteToastToggle.Unchecked += (_, _) => { _settings.BuildCompleteToast = false; Save(); };
        ErrorAlertsToggle.Checked   += (_, _) => { _settings.ErrorAlerts = true; Save(); };
        ErrorAlertsToggle.Unchecked += (_, _) => { _settings.ErrorAlerts = false; Save(); };
        SoundOnCompleteToggle.Checked   += (_, _) => { _settings.SoundOnComplete = true; Save(); };
        SoundOnCompleteToggle.Unchecked += (_, _) => { _settings.SoundOnComplete = false; Save(); };

        // Set initial nav highlight once layout is ready
        Dispatcher.BeginInvoke(UpdateActiveNavFromScroll,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Visible-change: reload fresh paths whenever the page is shown ─────────

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !IsLoaded) return;

        // Reload settings.json — this is the single source of truth.
        // Step 1 writes to it on Browse/Continue, so we always see the latest values.
        // No BuildSession overlaying — that was the source of circular overwrites.
        LoadSettings();
        DefaultOutputBox.Text    = _settings.DefaultOutputPath;
        DefaultWorkspaceBox.Text = _settings.DefaultWorkspacePath;
        ProfileStorageBox.Text   = _settings.ProfileStoragePath;
        _currentTheme            = _settings.Theme;
        UpdateThemeButtons();

        Dispatcher.BeginInvoke(UpdateActiveNavFromScroll,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Settings sidebar — scroll target into view ────────────────────────────

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        // Section key lives in CommandParameter; Tag is reserved for "active" visual state
        string section = btn.CommandParameter?.ToString() ?? "";
        FrameworkElement? target = section switch
        {
            "general"       => GeneralCard,
            "paths"         => PathsCard,
            "build"         => BuildCard,
            "notifications" => NotificationsCard,
            "adk"           => AdkCard,
            "about"         => AboutCard,
            _ => null
        };
        target?.BringIntoView();

        // Highlight immediately; scroll tracking will confirm/correct as scrolling settles
        SetActiveNav(btn);
    }

    // ── Save Settings button ──────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _saveStatusTimer;

    // Guard so initial IsChecked assignment during LoadIntoUi doesn't trigger
    // the on-change handler and re-write the setting.
    private bool _suppressAutoFetchPersist;

    private void AutoFetchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoFetchPersist) return;
        var value = AutoFetchToggle.IsChecked == true;

        // Follow the existing per-toggle pattern (cf. VerifyIsoToggle /
        // SoundOnCompleteToggle just above): update _settings then Save() so
        // the field round-trips correctly through the full-rewrite Save Settings
        // button as well. Without this the "Save Settings" click would
        // serialise _settings without our field and silently wipe it.
        _settings.EnableAutoFetchFeatures = value;
        Save();

        // Mirror onto the live session so Step 2 picks it up the next time
        // it becomes visible without needing an app restart.
        BuildSession.Current.EnableAutoFetchFeatures = value;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // Collect every UI control into _settings
        _settings.DefaultOutputPath        = DefaultOutputBox.Text.Trim();
        _settings.DefaultWorkspacePath     = DefaultWorkspaceBox.Text.Trim();
        _settings.ProfileStoragePath       = ProfileStorageBox.Text.Trim();
        _settings.Theme                    = _currentTheme;
        _settings.AutoSaveProfiles         = AutoSaveProfilesToggle.IsChecked == true;
        _settings.VerifyIsoAfterBuild      = VerifyIsoToggle.IsChecked == true;
        _settings.CleanWorkspaceAfterBuild = CleanWorkspaceToggle.IsChecked == true;
        _settings.BuildCompleteToast       = BuildCompleteToastToggle.IsChecked == true;
        _settings.ErrorAlerts              = ErrorAlertsToggle.IsChecked == true;
        _settings.SoundOnComplete          = SoundOnCompleteToggle.IsChecked == true;
        _settings.EnableAutoFetchFeatures  = AutoFetchToggle.IsChecked == true;

        if (CompressionCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
            _settings.WimCompression = tag;

        // Push paths into BuildSession so Step 1 reflects them immediately
        BuildSession.Current.OutputPath    = _settings.DefaultOutputPath;
        BuildSession.Current.WorkspacePath = _settings.DefaultWorkspacePath;

        // Write to disk — Save() now returns the error message (null = success)
        var saveError = Save();

        // Brief status feedback — keep the inline text short so it doesn't overlap
        // the hint text on the left. Full path goes into the tooltip for users who
        // want to know exactly where settings.json lives.
        if (saveError == null)
        {
            SaveStatusText.Text    = "✓ Settings saved";
            SaveStatusText.ToolTip = $"Saved to: {SettingsPath}";
        }
        else
        {
            SaveStatusText.Text    = "✗ Save failed";
            SaveStatusText.ToolTip = saveError;
        }

        _saveStatusTimer?.Stop();
        _saveStatusTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(saveError == null ? 4 : 10) };
        _saveStatusTimer.Tick += (_, _) =>
        {
            SaveStatusText.Text    = "";
            SaveStatusText.ToolTip = null;
            _saveStatusTimer?.Stop();
        };
        _saveStatusTimer.Start();
    }

    // ── Scroll tracking ───────────────────────────────────────────────────────

    private void SettingsScroll_Changed(object sender, ScrollChangedEventArgs e)
        => UpdateActiveNavFromScroll();

    private void UpdateActiveNavFromScroll()
    {
        if (SettingsScrollViewer == null) return;

        // Ordered list of (card, its nav button) — order matters: we want the LAST
        // card whose top is still at or above 45 % of the viewport height.
        var cards = new (FrameworkElement card, Button navBtn)[]
        {
            (GeneralCard,       NavGeneral),
            (PathsCard,         NavPaths),
            (BuildCard,         NavBuild),
            (NotificationsCard, NavNotifications),
            (AdkCard,           NavAdk),
            (AboutCard,         NavAbout),
        };

        double threshold = SettingsScrollViewer.ViewportHeight * 0.45;
        Button? active = NavGeneral;    // default to first section

        foreach (var (card, btn) in cards)
        {
            try
            {
                // TranslatePoint gives Y relative to the ScrollViewer viewport origin.
                // Negative → card is above the viewport; 0 → card top is at the top edge.
                var pt = card.TranslatePoint(new Point(0, 0), SettingsScrollViewer);
                if (pt.Y <= threshold)
                    active = btn;
            }
            catch { /* layout not yet ready — skip */ }
        }

        SetActiveNav(active);
    }

    private void SetActiveNav(Button? active)
    {
        foreach (var btn in new[] { NavGeneral, NavPaths, NavBuild, NavSecurity,
                                    NavNotifications, NavAdvanced, NavAdk, NavAbout })
            btn.Tag = btn == active ? "active" : null;
    }

    // ── Compression dropdown ──────────────────────────────────────────────────

    private void CompressionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompressionCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.WimCompression = tag;
            Save();
        }
    }

    // ── ADK install ───────────────────────────────────────────────────────────

    private void UpdateAdkStatus()
    {
        bool installed = AdkInstaller.IsInstalled();
        var ok = (Brush)Application.Current.Resources["OkBrush"];
        var warn = (Brush)Application.Current.Resources["WarnBrush"];

        if (installed)
        {
            var version = AdkInstaller.GetVersion() ?? "(installed)";
            AdkStatusDot.Foreground   = ok;
            AdkStatusLabel.Text       = $"Installed — {version}";
            AdkInstallBtn.Content     = "Reinstall";
            AdkInstallBtn.IsEnabled   = true;
        }
        else
        {
            AdkStatusDot.Foreground   = warn;
            AdkStatusLabel.Text       = "Not installed — required for the build step";
            AdkInstallBtn.Content     = "Download && Install";
            AdkInstallBtn.IsEnabled   = true;
        }
    }

    private async void InstallAdk_Click(object sender, RoutedEventArgs e)
    {
        if (_adkTask is { IsCompleted: false })
        {
            AppDialog.Alert(this, "ADK installation is already in progress.",
                "Already running", AppDialogIcon.Info);
            return;
        }

        // Confirm reinstall if already there
        if (AdkInstaller.IsInstalled())
        {
            if (!AppDialog.Confirm(this,
                "Windows ADK is already installed. Reinstall anyway?",
                "Reinstall ADK")) return;
        }

        _adkCts                 = new CancellationTokenSource();
        AdkInstallBtn.IsEnabled = false;
        AdkInstallPanel.Visibility = Visibility.Visible;
        AdkCancelBtn.Visibility    = Visibility.Visible;
        AdkInstallBar.Value        = 0;
        AdkInstallStatus.Text      = "Starting…";
        AdkInstallPct.Text         = "";

        var installer = new AdkInstaller();
        _adkTask = Task.Run(() => installer.InstallAsync(
            p => Dispatcher.Invoke(() => OnAdkProgress(p)),
            _adkCts.Token));

        try
        {
            var result = await _adkTask;
            if (result.Success)
            {
                AdkInstallStatus.Text = $"✓ {result.AdkVersion} installed";
                AdkInstallPct.Text    = "100%";
                AdkInstallBar.Value   = 100;
                AppDialog.Alert(this,
                    $"Windows ADK Deployment Tools installed successfully.\n\n" +
                    $"oscdimg.exe is now available at:\n{result.OscdimgPath}",
                    "ADK Installed", AppDialogIcon.Info);
            }
            else
            {
                AdkInstallStatus.Text = $"✗ {result.Error}";
                AppDialog.Alert(this,
                    $"ADK installation failed:\n\n{result.Error}",
                    "ADK Install Failed", AppDialogIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Install error:\n\n{ex.Message}",
                "ADK Install Error", AppDialogIcon.Error);
        }
        finally
        {
            AdkCancelBtn.Visibility = Visibility.Collapsed;
            AdkInstallBtn.IsEnabled = true;
            UpdateAdkStatus();
        }
    }

    private void OnAdkProgress(AdkInstallProgress p)
    {
        AdkInstallStatus.Text = p.Message;
        if (p.Percent >= 0)
        {
            AdkInstallBar.IsIndeterminate = false;
            AdkInstallBar.Value           = p.Percent;
            AdkInstallPct.Text            = $"{p.Percent}%";
        }
        else
        {
            AdkInstallBar.IsIndeterminate = true;
            AdkInstallPct.Text            = "";
        }
    }

    private void CancelAdkInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_adkTask is { IsCompleted: false } &&
            AppDialog.Confirm(this, "Cancel the ADK installation?", "Confirm Cancel"))
        {
            _adkCts?.Cancel();
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void ThemeDark_Click(object sender, MouseButtonEventArgs e)
        => ApplyTheme("dark");

    private void ThemeLight_Click(object sender, MouseButtonEventArgs e)
        => ApplyTheme("light");

    private void ApplyTheme(string theme)
    {
        if (theme == _currentTheme) return;
        _currentTheme = theme;
        _settings.Theme = theme;
        Save();

        var dicts = Application.Current.Resources.MergedDictionaries;
        var toRemove = dicts.FirstOrDefault(d =>
            d.Source != null && (
                d.Source.OriginalString.Contains("AppColors.xaml") ||
                d.Source.OriginalString.Contains("AppColorsLight.xaml")));
        if (toRemove != null) dicts.Remove(toRemove);

        var uri = theme == "light"
            ? new Uri("pack://application:,,,/Resources/AppColorsLight.xaml")
            : new Uri("pack://application:,,,/Resources/AppColors.xaml");
        dicts.Insert(0, new ResourceDictionary { Source = uri });

        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        var gold = (Brush)Application.Current.Resources["Gold1Brush"];
        var line = (Brush)Application.Current.Resources["LineBrush"];
        ThemeDarkOption.BorderBrush     = _currentTheme == "dark"  ? gold : line;
        ThemeDarkOption.BorderThickness = _currentTheme == "dark"  ? new Thickness(1.5) : new Thickness(1);
        ThemeLightOption.BorderBrush    = _currentTheme == "light" ? gold : line;
        ThemeLightOption.BorderThickness= _currentTheme == "light" ? new Thickness(1.5) : new Thickness(1);
    }

    // ── Browse buttons ────────────────────────────────────────────────────────

    private void BrowseDefaultOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Default Output Folder" };
        if (dlg.ShowDialog() == true) DefaultOutputBox.Text = dlg.FolderName;
    }

    private void BrowseDefaultWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Default Workspace Folder" };
        if (dlg.ShowDialog() == true) DefaultWorkspaceBox.Text = dlg.FolderName;
    }

    private void BrowseProfileStorage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Profile Storage Folder" };
        if (dlg.ShowDialog() == true)
        {
            ProfileStorageBox.Text = dlg.FolderName;
            _settings.ProfileStoragePath = dlg.FolderName;
            Save();
        }
    }

    // ── Build history ─────────────────────────────────────────────────────────

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!AppDialog.Confirm(this,
                "This will permanently delete all build history records.\n\nContinue?",
                "Clear Build History", AppDialogIcon.Warning))
            return;

        BuildHistoryStore.Clear();
        HistoryCleared?.Invoke();   // tell MainWindow to refresh WelcomePage immediately

        // Brief confirmation in the save-status bar
        SaveStatusText.Text = "✓ Build history cleared";
        _saveStatusTimer?.Stop();
        _saveStatusTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(3) };
        _saveStatusTimer.Tick += (_, _) => { SaveStatusText.Text = ""; _saveStatusTimer?.Stop(); };
        _saveStatusTimer.Start();
    }

    // ── About-card buttons ────────────────────────────────────────────────────

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            // SettingsDir now points to C:\ProgramData\GoldenISOBuilder
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsDir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppDialog.Alert(this,
            $"You are running ALE Image Forge {GetAppVersion()}.\n\n" +
            "Update checking is not yet implemented in this build.",
            "Check for Updates", AppDialogIcon.Info);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private class AppSettings
    {
        public string Theme                  { get; set; } = "dark";
        public string DefaultOutputPath      { get; set; } = "";
        public string DefaultWorkspacePath   { get; set; } = "";
        public string ProfileStoragePath     { get; set; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        public bool   AutoSaveProfiles       { get; set; } = true;
        public bool   VerifyIsoAfterBuild    { get; set; } = true;
        public bool   CleanWorkspaceAfterBuild{ get; set; } = true;
        public bool   BuildCompleteToast     { get; set; } = true;
        public bool   ErrorAlerts            { get; set; } = true;
        public bool   SoundOnComplete        { get; set; } = true;
        public string WimCompression         { get; set; } = "max";   // max | fast | none

        /// <summary>Master toggle for the auto-fetch features (Windows Updates
        /// + OEM driver packs). Must be on this full AppSettings model so the
        /// "Save Settings" button's full-rewrite preserves the value rather
        /// than wiping it.</summary>
        public bool   EnableAutoFetchFeatures { get; set; } = false;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { _settings = new AppSettings(); }
    }

    /// <summary>
    /// Writes _settings to disk.  Returns null on success, or the error message on failure.
    /// Never throws — callers use the return value to show feedback.
    /// </summary>
    private string? Save()
    {
        // IMPORTANT: never write before OnLoaded has run and LoadSettings() has
        // populated _settings from disk.  XAML-wired events (e.g. CompressionCombo
        // SelectionChanged from IsSelected="True") fire during InitializeComponent()
        // — before OnLoaded — and would overwrite the saved file with blank defaults.
        if (!IsLoaded) return null;

        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            return null;   // success
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void BindToUi()
    {
        // Sync _currentTheme from settings (in case AppSettingsLoader already switched it)
        _currentTheme = _settings.Theme;
        UpdateThemeButtons();

        DefaultOutputBox.Text         = _settings.DefaultOutputPath;
        DefaultWorkspaceBox.Text      = _settings.DefaultWorkspacePath;
        ProfileStorageBox.Text        = _settings.ProfileStoragePath;
        AutoSaveProfilesToggle.IsChecked   = _settings.AutoSaveProfiles;
        VerifyIsoToggle.IsChecked          = _settings.VerifyIsoAfterBuild;
        CleanWorkspaceToggle.IsChecked     = _settings.CleanWorkspaceAfterBuild;
        BuildCompleteToastToggle.IsChecked = _settings.BuildCompleteToast;
        ErrorAlertsToggle.IsChecked        = _settings.ErrorAlerts;
        SoundOnCompleteToggle.IsChecked    = _settings.SoundOnComplete;

        // Auto-fetch toggle — read from _settings so the value round-trips
        // through the same JSON the Save Settings button writes. Suppress
        // the on-change handler while we set the initial state.
        _suppressAutoFetchPersist          = true;
        AutoFetchToggle.IsChecked          = _settings.EnableAutoFetchFeatures;
        _suppressAutoFetchPersist          = false;
        // Mirror to BuildSession at startup so the wizard pages see the right
        // value on first render without waiting for the user to touch Settings.
        BuildSession.Current.EnableAutoFetchFeatures = _settings.EnableAutoFetchFeatures;

        // Compression dropdown — pick the matching item by Tag
        for (int i = 0; i < CompressionCombo.Items.Count; i++)
        {
            if (CompressionCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), _settings.WimCompression, StringComparison.OrdinalIgnoreCase))
            {
                CompressionCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void UpdateAboutInfo()
    {
        AppVersionLabel.Text = GetAppVersion();
        DotNetVersionLabel.Text = Environment.Version.ToString();

        // Detect ADK version from oscdimg path
        var oscdimg = BuildEngine.FindOscdimg();
        if (oscdimg != null && File.Exists(oscdimg))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(oscdimg);
                AdkVersionLabel.Text = info.ProductVersion ?? info.FileVersion ?? "(installed)";
            }
            catch { AdkVersionLabel.Text = "(installed)"; }
        }
        else
        {
            AdkVersionLabel.Text = "Not installed";
            AdkVersionLabel.Foreground = (Brush)Application.Current.Resources["WarnBrush"];
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "unknown"; }
    }
}
