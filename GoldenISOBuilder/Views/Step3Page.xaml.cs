using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfPath = System.Windows.Shapes.Path;
using GoldenISOBuilder.Helpers;   // AppNotifier, ToastKind
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;

namespace GoldenISOBuilder.Views;

public partial class Step3Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    // Mirror of the session lists so we can rebuild the UI without losing state
    private readonly HashSet<string>     _packagesToRemove = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>     _featuresToEnable  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>     _featuresToDisable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GroupPolicyEntry>  _groupPolicies = [];
    private readonly List<CertificateEntry>  _certificates  = [];

    // ── ADMX source selector state ────────────────────────────────────────────
    private string      _admxSourceMode  = "machine"; // "machine" | "iso" | "custom"
    private string      _customAdmxPath  = "";
    private AdmxParser? _isoAdmxCache;                // cached ISO parser (avoids re-mounting)
    private string?     _isoAdmxCacheKey;             // SourceIsoPath the cache belongs to

    private CancellationTokenSource? _scanCts;

    // Scan in-progress state — persisted across page navigations so the progress panel
    // can be restored exactly when the user returns while a scan is still running.
    private bool   _scanRunning     = false;
    private string _lastScanStatus  = "Scanning…";
    private int    _lastScanPct     = 0;

    public Step3Page()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            // User navigated away — let the scan keep running in the background.
            // We DON'T cancel here; the Dispatcher callbacks remain valid because the
            // controls still exist in the visual tree even while the page is hidden.
            return;
        }

        RestoreFromSession();

        // Determine which panel to show when the page becomes visible again
        var s = BuildSession.Current;
        if (s.ScannedPackages.Count > 0 || s.ScannedFeatures.Count > 0)
        {
            // Scan finished (possibly while user was on another page) — show results
            ShowScanComplete(s.ScannedPackages.Count, s.ScannedFeatures.Count);
            RebuildPackageList(s.ScannedPackages, "");
            RebuildFeatureLists(s.ScannedFeatures, "");
        }
        else if (_scanRunning)
        {
            // Scan is still in progress — restore the progress panel to its last known state
            // so the user sees continuous movement rather than a blank prompt
            ScanPrompt.Visibility   = Visibility.Collapsed;
            ScanComplete.Visibility = Visibility.Collapsed;
            ScanProgress.Visibility = Visibility.Visible;
            ScanStatusText.Text     = _lastScanStatus;
            ScanProgressBar.Value   = _lastScanPct;
            ScanProgressPct.Text    = $"{_lastScanPct}%";
        }
        else
        {
            ShowScanPrompt();
        }
    }

    // ── Session restore ───────────────────────────────────────────────────────

    private void RestoreFromSession()
    {
        var s = BuildSession.Current;

        _packagesToRemove.Clear();
        foreach (var p in s.BloatwareToRemove) _packagesToRemove.Add(p);

        _featuresToEnable.Clear();
        foreach (var f in s.EnabledFeatures) _featuresToEnable.Add(f);

        _featuresToDisable.Clear();
        foreach (var f in s.DisabledFeatures) _featuresToDisable.Add(f);

        // Group policies
        _groupPolicies.Clear();
        _groupPolicies.AddRange(s.GroupPolicies);
        RefreshGroupPoliciesPanel();

        // ADMX source mode
        _admxSourceMode = s.AdmxSourceMode;
        _customAdmxPath = s.CustomAdmxPath;
        if (AdmxCustomFolderBox != null && !string.IsNullOrEmpty(_customAdmxPath))
            AdmxCustomFolderBox.Text = _customAdmxPath;
        UpdateAdmxSourcePills();

        // Security toggles
        SecDisableSmb1.IsChecked      = s.DisableSmbV1;
        SecDisableTelemetry.IsChecked = s.DisableTelemetry;
        SecDefenderUpdate.IsChecked   = s.EnableDefenderAtp;
        SecBitLocker.IsChecked        = s.EnableBitLocker;
        SecBitLockerSaveKey.IsChecked = s.BitLockerSaveRecoveryKey;
        TxtBitLockerKeyFolder.Text    = s.BitLockerKeyFolder ?? @"C:\";
        BitLockerOptions.Visibility        = s.EnableBitLocker          ? Visibility.Visible : Visibility.Collapsed;
        BitLockerKeyFolderPanel.Visibility = s.BitLockerSaveRecoveryKey ? Visibility.Visible : Visibility.Collapsed;

        // System defaults
        SysDarkMode.IsChecked   = s.DarkMode;
        SysShowExt.IsChecked    = s.ShowFileExtensions;
        SysShowHidden.IsChecked = s.ShowHiddenFiles;

        // Windows 11 UX & Privacy baseline
        UxDisableCopilot.IsChecked  = s.DisableCopilot;
        UxDisableRecall.IsChecked   = s.DisableRecall;
        UxDisableWidgets.IsChecked  = s.DisableWidgets;
        UxDisableChat.IsChecked     = s.DisableChatIcon;
        UxDisableConsumer.IsChecked = s.DisableConsumerFeatures;

        // OneDrive per-machine uninstall (sits inside Bloatware Removal card)
        BloatUninstallOneDrive.IsChecked = s.UninstallOneDrive;

        // Trusted certificates
        _certificates.Clear();
        _certificates.AddRange(s.Certificates);
        RefreshCertificateList();

        UpdateCounters();
    }

    // ── Scan orchestration ────────────────────────────────────────────────────

    private async void ScanIso_Click(object sender, RoutedEventArgs e)
    {
        var s = BuildSession.Current;
        if (string.IsNullOrEmpty(s.SourceIsoPath) || !File.Exists(s.SourceIsoPath))
        {
            ScanNoIsoHint.Visibility = Visibility.Visible;
            return;
        }
        ScanNoIsoHint.Visibility = Visibility.Collapsed;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _scanRunning    = true;
        _lastScanStatus = "Starting DISM scan…";
        _lastScanPct    = 0;

        // Clear any previously cached ISO ADMX data — re-scan means fresh everything
        _isoAdmxCache    = null;
        _isoAdmxCacheKey = null;

        ShowScanProgress("Starting DISM scan…");
        PackageListPanel.Children.Clear();
        EnabledFeaturesPanel.Children.Clear();
        DisabledFeaturesPanel.Children.Clear();
        BloatResultPanel.Visibility  = Visibility.Collapsed;
        FeatResultPanel.Visibility   = Visibility.Collapsed;
        BloatNoScanPanel.Visibility  = Visibility.Visible;
        FeatNoScanPanel.Visibility   = Visibility.Visible;

        // Progress callback shared across all three phases (packages, ADMX, unmount)
        void OnProgress(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (msg.StartsWith("PROGRESS:", StringComparison.Ordinal)
                    && int.TryParse(msg[9..], out int pct))
                {
                    _lastScanPct          = pct;
                    ScanProgressBar.Value = pct;
                    ScanProgressPct.Text  = $"{pct}%";
                }
                else if (msg.StartsWith("STATUS:", StringComparison.Ordinal))
                {
                    _lastScanStatus     = msg[7..];
                    ScanStatusText.Text = msg[7..];
                }
                else
                {
                    _lastScanStatus     = msg;
                    ScanStatusText.Text = msg;
                }
            });
        }

        // Track whether the WIM is still mounted so the finally block can force-clean up
        // if something goes wrong between ScanAsync returning and UnmountAsync completing.
        ImageScanner.ScanResult? pendingResult = null;

        try
        {
            // ── Phase 1: locate WIM ───────────────────────────────────────────────
            ShowScanProgress("Locating WIM file…");
            string wimPath = await FindOrMountWimAsync(s, ct);
            int    wimIndex = s.SelectedImage?.Index ?? 1;

            ShowScanProgress($"Scanning index {wimIndex} — packages, features and policy definitions…");

            // ── Phase 2: mount WIM, scan packages + features (progress 1–84%) ────
            // keepMounted:true leaves the WIM live so we can read ADMX next without
            // a second mount/unmount cycle.
            var result = await Task.Run(
                () => ImageScanner.ScanAsync(wimPath, wimIndex, OnProgress, ct,
                      keepMounted: true),
                ct);

            pendingResult     = result;   // WIM is now mounted at result.MountDir
            s.ScannedPackages = result.Packages;
            s.ScannedFeatures = result.Features;

            // ── Phase 3: read ADMX policy definitions (progress ~84–90%) ─────────
            // The WIM is still mounted — no second DISM mount needed.
            try
            {
                SetProgress(85, "Loading policy definitions from ISO…");
                await LoadAdmxFromMountedDirAsync(result.MountDir, s);

                if (_isoAdmxCache != null && _admxSourceMode != "custom")
                {
                    _admxSourceMode = "iso";
                    UpdateAdmxSourcePills();
                }
                SetProgress(90, "Policy definitions loaded — unmounting image…");
            }
            catch { /* non-fatal — user can still configure GP policies separately */ }

            // ── Phase 4: unmount WIM (progress 90–100%) ──────────────────────────
            await ImageScanner.UnmountAsync(result.MountDir, OnProgress, ct);
            pendingResult = null;   // successfully unmounted — nothing left to clean up

            // ── Done ──────────────────────────────────────────────────────────────
            ShowScanComplete(result.Packages.Count, result.Features.Count);
            RebuildPackageList(result.Packages, PkgSearchBox.Text);
            RebuildFeatureLists(result.Features, FeatSearchBox.Text);
        }
        catch (OperationCanceledException)
        {
            ShowScanPrompt();
        }
        catch (Exception ex)
        {
            ShowScanPrompt();
            AppDialog.Alert(this,
                $"ISO scan failed:\n\n{ex.Message}\n\n" +
                "Make sure the app is running as Administrator and the ISO path is still accessible.",
                "Scan Failed", AppDialogIcon.Warning);
        }
        finally
        {
            // If the WIM was left mounted (e.g. UnmountAsync was cancelled mid-way),
            // attempt a best-effort force-unmount so we don't leave a dangling DISM mount.
            if (pendingResult != null)
            {
                try { await ImageScanner.UnmountAsync(pendingResult.MountDir, null, CancellationToken.None); }
                catch { }
            }
            _scanRunning = false;
        }
    }

    // Helper: update progress bar + percentage text + status label in one call
    private void SetProgress(int pct, string? statusMsg = null)
    {
        _lastScanPct          = pct;
        ScanProgressBar.Value = pct;
        ScanProgressPct.Text  = $"{pct}%";
        if (statusMsg != null)
        {
            _lastScanStatus     = statusMsg;
            ScanStatusText.Text = statusMsg;
        }
    }

    private static async Task<string> FindOrMountWimAsync(BuildSession s, CancellationToken ct)
    {
        // Try using the already-mounted drive from Step1 analysis
        if (!string.IsNullOrEmpty(s.MountedIsoDrive))
        {
            var wim = ImageScanner.FindWimInDrive(s.MountedIsoDrive);
            if (wim != null) return wim;
        }

        // Re-mount the ISO (PowerShell Mount-DiskImage)
        var before = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name[0]).ToHashSet();
        await RunSilentAsync("powershell.exe",
            $"-NonInteractive -Command \"Mount-DiskImage -ImagePath '{s.SourceIsoPath!.Replace("'", "''")}' | Out-Null\"", ct);

        // Wait up to 20 seconds for a new drive letter
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            var after = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name[0]).ToHashSet();
            char newDrive = after.Except(before).FirstOrDefault();
            if (newDrive != default)
            {
                string drive = $"{newDrive}:";
                s.MountedIsoDrive = drive;
                var wim = ImageScanner.FindWimInDrive(drive);
                if (wim != null) return wim;
            }
        }

        throw new TimeoutException("Could not locate the install.wim after mounting the ISO.");
    }

    private static async Task<int> RunSilentAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        { CreateNoWindow = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    // ── Package list UI ───────────────────────────────────────────────────────

    private void RebuildPackageList(List<DiscoveredPackage> packages, string filter)
    {
        PackageListPanel.Children.Clear();

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? packages
            : packages.Where(p =>
                p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var pkg in filtered)
            PackageListPanel.Children.Add(BuildPackageRow(pkg));

        BloatNoScanPanel.Visibility = Visibility.Collapsed;
        BloatResultPanel.Visibility = Visibility.Visible;
        UpdateCounters();
    }

    private UIElement BuildPackageRow(DiscoveredPackage pkg)
    {
        // ── Determine initial checked state ──────────────────────────────────
        bool initChecked = _packagesToRemove.Contains(pkg.PackageName) ||
                           (!_packagesToRemove.Any() && pkg.IsKnownBloat);

        // On first load pre-seed the set with known bloat so counters are correct
        if (pkg.IsKnownBloat && initChecked && !_packagesToRemove.Contains(pkg.PackageName))
            _packagesToRemove.Add(pkg.PackageName);

        // ── Row container ─────────────────────────────────────────────────────
        bool rowChecked = initChecked;   // mutable capture for lambdas below

        var checkedBg   = (Brush)Application.Current.Resources["BG3Brush"];
        var uncheckedBg = (Brush)Application.Current.Resources["BG0Brush"];
        var hoverBg     = (Brush)Application.Current.Resources["RowHoverBgBrush"];

        var row = new Border
        {
            Background    = rowChecked ? checkedBg : uncheckedBg,
            CornerRadius  = new CornerRadius(6),
            Padding       = new Thickness(10, 8, 10, 8),
            Cursor        = Cursors.Hand,
            Tag           = pkg.PackageName
        };

        // ── Custom 22×22 rounded checkbox indicator ───────────────────────────
        var checkBorder = new Border
        {
            Width             = 22,
            Height            = 22,
            CornerRadius      = new CornerRadius(5),
            BorderThickness   = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 12, 0),
        };

        // Checkmark path — two-segment tick: M4,11 L9,16 L18,5
        var checkMark = new WpfPath
        {
            Data              = Geometry.Parse("M 4,11 L 9,16 L 18,5"),
            Stroke            = Brushes.White,
            StrokeThickness   = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap  = PenLineCap.Round,
            StrokeLineJoin    = PenLineJoin.Round,
            Stretch           = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBorder.Child = checkMark;

        // Helper that synchronises checkbox visual + row background to the current checked state
        void ApplyVisual(bool chk)
        {
            if (chk)
            {
                checkBorder.Background  = (Brush)Application.Current.Resources["Gold1Brush"];
                checkBorder.BorderBrush = (Brush)Application.Current.Resources["Gold1Brush"];
                checkMark.Visibility    = Visibility.Visible;
                row.Background          = checkedBg;
            }
            else
            {
                checkBorder.Background  = (Brush)Application.Current.Resources["BG1Brush"];
                checkBorder.BorderBrush = (Brush)Application.Current.Resources["LineBrush"];
                checkMark.Visibility    = Visibility.Collapsed;
                row.Background          = uncheckedBg;
            }
        }
        ApplyVisual(rowChecked);

        // ── Toggle handler — fires on click anywhere in the row ───────────────
        row.MouseLeftButtonUp += (_, ev) =>
        {
            rowChecked = !rowChecked;
            if (rowChecked) _packagesToRemove.Add(pkg.PackageName);
            else            _packagesToRemove.Remove(pkg.PackageName);
            ApplyVisual(rowChecked);
            UpdateCounters();
            ev.Handled = true;
        };

        // Subtle hover highlight (only when not already selected)
        row.MouseEnter += (_, _) => { if (!rowChecked) row.Background = hoverBg; };
        row.MouseLeave += (_, _) => { row.Background = rowChecked ? checkedBg : uncheckedBg; };

        // ── Grid layout: [checkbox] [text block] [badge] ─────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(checkBorder, 0);

        // Text block: friendly display name + package name in small monospace
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
        };
        info.Children.Add(new TextBlock
        {
            Text        = pkg.DisplayName,
            Foreground  = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize    = 13,
            FontWeight  = FontWeights.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text         = pkg.PackageName,
            Foreground   = (Brush)Application.Current.Resources["FG3Brush"],
            FontSize     = 10,
            FontFamily   = new FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip      = pkg.PackageName,
            Margin       = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(info, 1);

        // Known-bloat badge
        if (pkg.IsKnownBloat)
        {
            var errC = (Color)Application.Current.Resources["ErrColor"];
            var badge = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(0x30, errC.R, errC.G, errC.B)),
                BorderBrush       = new SolidColorBrush(Color.FromArgb(0x60, errC.R, errC.G, errC.B)),
                BorderThickness   = new Thickness(1),
                CornerRadius      = new CornerRadius(4),
                Padding           = new Thickness(6, 3, 6, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = new TextBlock
            {
                Text       = "BLOAT",
                Foreground = (Brush)Application.Current.Resources["ErrBrush"],
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        grid.Children.Add(checkBorder);
        grid.Children.Add(info);
        row.Child = grid;
        return row;
    }

    // ── Feature list UI ───────────────────────────────────────────────────────

    private void RebuildFeatureLists(List<DiscoveredFeature> features, string filter)
    {
        EnabledFeaturesPanel.Children.Clear();
        DisabledFeaturesPanel.Children.Clear();

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? features
            : features.Where(f => f.FeatureName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var f in filtered.Where(f => f.IsEnabled))
            EnabledFeaturesPanel.Children.Add(BuildFeatureRow(f, isEnableSection: false));

        foreach (var f in filtered.Where(f => !f.IsEnabled))
            DisabledFeaturesPanel.Children.Add(BuildFeatureRow(f, isEnableSection: true));

        FeatNoScanPanel.Visibility = Visibility.Collapsed;
        FeatResultPanel.Visibility = Visibility.Visible;
        UpdateCounters();
    }

    private UIElement BuildFeatureRow(DiscoveredFeature feature, bool isEnableSection)
    {
        bool isChecked = isEnableSection
            ? _featuresToEnable.Contains(feature.FeatureName)
            : _featuresToDisable.Contains(feature.FeatureName);

        var row = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(8, 4, 8, 4)
        };
        row.Background = isChecked
            ? (Brush)Application.Current.Resources["BG3Brush"]
            : (Brush)Application.Current.Resources["BG0Brush"];

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var cb = new CheckBox
        {
            IsChecked         = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0)
        };

        cb.Checked += (_, _) =>
        {
            if (isEnableSection) _featuresToEnable.Add(feature.FeatureName);
            else _featuresToDisable.Add(feature.FeatureName);
            row.Background = (Brush)Application.Current.Resources["BG3Brush"];
            UpdateCounters();
        };
        cb.Unchecked += (_, _) =>
        {
            if (isEnableSection) _featuresToEnable.Remove(feature.FeatureName);
            else _featuresToDisable.Remove(feature.FeatureName);
            row.Background = (Brush)Application.Current.Resources["BG0Brush"];
            UpdateCounters();
        };

        sp.Children.Add(cb);
        sp.Children.Add(new TextBlock
        {
            Text              = feature.FeatureName,
            Foreground        = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily        = new FontFamily("Consolas"),
            ToolTip           = $"{feature.FeatureName}  [State: {feature.State}]"
        });

        row.Child = sp;
        return row;
    }

    // ── Toolbar button handlers ───────────────────────────────────────────────

    private void PkgSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var s = BuildSession.Current;
        if (s.ScannedPackages.Count > 0)
            RebuildPackageList(s.ScannedPackages, PkgSearchBox.Text);
    }

    private void FeatSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var s = BuildSession.Current;
        if (s.ScannedFeatures.Count > 0)
            RebuildFeatureLists(s.ScannedFeatures, FeatSearchBox.Text);
    }

    private void SelectKnownBloat_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pkg in BuildSession.Current.ScannedPackages.Where(p => p.IsKnownBloat))
            _packagesToRemove.Add(pkg.PackageName);
        RebuildPackageList(BuildSession.Current.ScannedPackages, PkgSearchBox.Text);
    }

    private void SelectAllPkgs_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pkg in BuildSession.Current.ScannedPackages)
            _packagesToRemove.Add(pkg.PackageName);
        RebuildPackageList(BuildSession.Current.ScannedPackages, PkgSearchBox.Text);
    }

    private void DeselectAllPkgs_Click(object sender, RoutedEventArgs e)
    {
        _packagesToRemove.Clear();
        RebuildPackageList(BuildSession.Current.ScannedPackages, PkgSearchBox.Text);
    }

    // ── Counter pills ─────────────────────────────────────────────────────────

    private void UpdateCounters()
    {
        if (BloatCountPill != null)
            BloatCountPill.Text = $"{_packagesToRemove.Count} selected";
        if (FeatCountPill != null)
        {
            int total = _featuresToEnable.Count + _featuresToDisable.Count;
            FeatCountPill.Text = $"{total} change{(total != 1 ? "s" : "")}";
        }
        if (GPOCountPill != null)
            GPOCountPill.Text = $"{_groupPolicies.Count} polic{(_groupPolicies.Count == 1 ? "y" : "ies")}";
    }

    // ── Scan state panels ─────────────────────────────────────────────────────

    private void ShowScanPrompt()
    {
        ScanPrompt.Visibility   = Visibility.Visible;
        ScanProgress.Visibility = Visibility.Collapsed;
        ScanComplete.Visibility = Visibility.Collapsed;
    }

    private void ShowScanProgress(string status)
    {
        ScanStatusText.Text     = status;
        ScanProgressBar.Value   = 0;
        ScanProgressPct.Text    = "0%";
        ScanPrompt.Visibility   = Visibility.Collapsed;
        ScanProgress.Visibility = Visibility.Visible;
        ScanComplete.Visibility = Visibility.Collapsed;
    }

    private void ShowScanComplete(int packages, int features)
    {
        string policyPart = _isoAdmxCache != null
            ? $" · {_isoAdmxCache.AllPolicies.Count:N0} GP policies"
            : "";
        ScanSummaryText.Text    = $"✓ Scan complete — {packages} packages · {features} features{policyPart} found";
        ScanPrompt.Visibility   = Visibility.Collapsed;
        ScanProgress.Visibility = Visibility.Collapsed;
        ScanComplete.Visibility = Visibility.Visible;

        // Fire a Windows toast so the user knows even if they stepped away
        string toastBody = _isoAdmxCache != null
            ? $"Found {packages} packages, {features} features and {_isoAdmxCache.AllPolicies.Count:N0} GP policies."
            : $"Found {packages} packages and {features} features in your ISO.";
        AppNotifier.Show("ISO Scan Complete ✓", toastBody, ToastKind.Success);

        if (AppSettingsLoader.ReadSoundOnComplete())
            SystemSounds.Asterisk.Play();
    }

    // ── Session save ──────────────────────────────────────────────────────────

    private void SaveToSession()
    {
        var s = BuildSession.Current;

        s.BloatwareToRemove  = [.. _packagesToRemove];
        s.EnabledFeatures    = [.. _featuresToEnable];
        s.DisabledFeatures   = [.. _featuresToDisable];

        s.DisableSmbV1       = SecDisableSmb1.IsChecked      == true;
        s.DisableTelemetry   = SecDisableTelemetry.IsChecked == true;
        s.EnableDefenderAtp  = SecDefenderUpdate.IsChecked   == true;
        s.EnableBitLocker          = SecBitLocker.IsChecked        == true;
        s.BitLockerSaveRecoveryKey = SecBitLockerSaveKey.IsChecked == true;
        s.BitLockerKeyFolder       = TxtBitLockerKeyFolder.Text.Trim();

        s.DarkMode           = SysDarkMode.IsChecked    == true;
        s.ShowFileExtensions = SysShowExt.IsChecked     == true;
        s.ShowHiddenFiles    = SysShowHidden.IsChecked  == true;

        // Windows 11 UX & Privacy baseline (5 independent toggles)
        s.DisableCopilot          = UxDisableCopilot.IsChecked  == true;
        s.DisableRecall           = UxDisableRecall.IsChecked   == true;
        s.DisableWidgets          = UxDisableWidgets.IsChecked  == true;
        s.DisableChatIcon         = UxDisableChat.IsChecked     == true;
        s.DisableConsumerFeatures = UxDisableConsumer.IsChecked == true;

        // OneDrive per-machine uninstall
        s.UninstallOneDrive       = BloatUninstallOneDrive.IsChecked == true;

        // Trusted certificates (mirror of the working list back to the session)
        s.Certificates            = [.. _certificates];

        s.GroupPolicies      = [.. _groupPolicies];

        s.AdmxSourceMode     = _admxSourceMode;
        s.CustomAdmxPath     = _customAdmxPath;
    }

    // ── Group Policy ──────────────────────────────────────────────────────────

    private async void AddGroupPolicy_Click(object sender, RoutedEventArgs e)
    {
        // Disable the button while we may be doing async work (ISO mount)
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            AdmxParser? preloaded = null;
            string admxPath = AdmxParser.DefaultPath;

            switch (_admxSourceMode)
            {
                case "iso":
                    preloaded = await LoadAdmxFromIsoAsync();
                    if (preloaded == null) return;      // error already shown to user
                    break;

                case "custom":
                    if (string.IsNullOrEmpty(_customAdmxPath) || !Directory.Exists(_customAdmxPath))
                    {
                        AppDialog.Alert(this,
                            "Please select a valid ADMX folder using the 'Browse…' button.",
                            "ADMX Source", AppDialogIcon.Warning);
                        return;
                    }
                    admxPath = _customAdmxPath;
                    break;

                // "machine" — use DefaultPath, no preloaded parser (dialog loads it lazily)
            }

            var dialog = new GroupPolicyDialog(admxPath, preloaded)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true && dialog.Results.Count > 0)
            {
                _groupPolicies.AddRange(dialog.Results);
                RefreshGroupPoliciesPanel();
                UpdateCounters();
            }
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    // ── ADMX source selector ──────────────────────────────────────────────────

    private void AdmxSourcePill_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string mode)
        {
            _admxSourceMode = mode;
            UpdateAdmxSourcePills();
        }
    }

    private void UpdateAdmxSourcePills()
    {
        if (AdmxPillMachine == null) return;   // controls not yet initialised

        var activeBg     = (Brush)Application.Current.Resources["Gold1Brush"];
        var inactiveBg   = (Brush)Application.Current.Resources["BG1Brush"];
        var activeText   = (Brush)Application.Current.Resources["BG0Brush"];
        var inactiveText = (Brush)Application.Current.Resources["FG1Brush"];
        var goldBorder   = (Brush)Application.Current.Resources["Gold1Brush"];
        var quietBorder  = (Brush)Application.Current.Resources["LineBrush"];

        foreach (var (pill, txt, mode) in new[]
        {
            (AdmxPillMachine, AdmxPillMachineText, "machine"),
            (AdmxPillIso,     AdmxPillIsoText,     "iso"),
            (AdmxPillCustom,  AdmxPillCustomText,  "custom"),
        })
        {
            bool active           = _admxSourceMode == mode;
            pill.Background       = active ? activeBg   : inactiveBg;
            pill.BorderBrush      = active ? goldBorder  : quietBorder;
            pill.BorderThickness  = new Thickness(1);
            txt.Foreground        = active ? activeText  : inactiveText;
            txt.FontWeight        = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        AdmxCustomFolderRow.Visibility = _admxSourceMode == "custom"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── BitLocker sub-option event handlers ──────────────────────────────────

    private void SecBitLocker_Toggled(object sender, RoutedEventArgs e)
    {
        if (BitLockerOptions == null) return;
        BitLockerOptions.Visibility = SecBitLocker.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SecBitLockerSaveKey_Toggled(object sender, RoutedEventArgs e)
    {
        if (BitLockerKeyFolderPanel == null) return;
        BitLockerKeyFolderPanel.Visibility = SecBitLockerSaveKey.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseBitLockerFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to save BitLocker recovery key"
        };
        if (dlg.ShowDialog() != true) return;
        TxtBitLockerKeyFolder.Text = dlg.FolderName;
    }

    private void BrowseAdmxFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select ADMX Policy Definitions Folder"
        };
        if (dlg.ShowDialog() != true) return;

        _customAdmxPath          = dlg.FolderName;
        AdmxCustomFolderBox.Text = dlg.FolderName;
        // Note: custom-folder parsing uses AdmxParser's own static path-keyed cache —
        // no Step3Page-level cache entry to invalidate here.
    }

    /// <summary>
    /// Reads ADMX policy definitions from an <b>already-mounted</b> WIM directory.
    /// Called during the single-mount scan flow while the image is still live at
    /// <paramref name="mountDir"/> — no second DISM mount is needed.
    /// Progress is reported via <see cref="ScanStatusText"/> (status strings only;
    /// the progress bar is already at ~85% and will be advanced by the caller).
    /// </summary>
    private async Task LoadAdmxFromMountedDirAsync(string mountDir, BuildSession s)
    {
        string policyDefsPath = Path.Combine(mountDir, "Windows", "PolicyDefinitions");
        if (!Directory.Exists(policyDefsPath)) return;

        var progress = new Progress<string>(msg =>
            Dispatcher.Invoke(() => ScanStatusText.Text = msg));

        var parser = await AdmxParser.GetOrLoadAsync(policyDefsPath, progress);
        _isoAdmxCache    = parser;
        _isoAdmxCacheKey = s.SourceIsoPath;
    }

    /// <summary>
    /// Legacy ADMX loader used only by <see cref="LoadAdmxFromIsoAsync"/> (the manual
    /// "From ISO" flow triggered when the user adds a group policy without having run a
    /// full scan first).  Does its own mount → parse → unmount cycle.
    /// </summary>
    private async Task LoadAdmxFromWimAsync(string wimPath, BuildSession s)
    {
        string? tempMount = null;
        try
        {
            // Use a short, fixed base path to avoid DISM path-length errors (Error 3)
            Directory.CreateDirectory(@"C:\GIBMount");
            tempMount = Path.Combine(@"C:\GIBMount", "ADMX_" + Path.GetRandomFileName().Replace(".", ""));
            Directory.CreateDirectory(tempMount);

            int wimIndex = s.SelectedImage?.Index ?? 1;
            ScanStatusText.Text = "Mounting WIM for policy definitions (read-only)…";
            int mountExit = await RunSilentAsync("dism.exe",
                $"/Mount-Image /ImageFile:\"{wimPath}\" /Index:{wimIndex} /MountDir:\"{tempMount}\" /ReadOnly",
                CancellationToken.None);
            if (mountExit != 0) return;   // mount failed — non-fatal, user can still build

            string policyDefsPath = Path.Combine(tempMount, "Windows", "PolicyDefinitions");
            if (!Directory.Exists(policyDefsPath)) return;

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ScanStatusText.Text = msg));

            var parser = await AdmxParser.GetOrLoadAsync(policyDefsPath, progress);
            _isoAdmxCache    = parser;
            _isoAdmxCacheKey = s.SourceIsoPath;
        }
        finally
        {
            if (tempMount != null && Directory.Exists(tempMount))
            {
                ScanStatusText.Text = "Unmounting policy definitions WIM…";
                try { await RunSilentAsync("dism.exe", $"/Unmount-Image /MountDir:\"{tempMount}\" /Discard", CancellationToken.None); }
                catch { }
                try { Directory.Delete(tempMount, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Mounts the selected WIM read-only, parses ADMX files from
    /// Windows\PolicyDefinitions inside it, then unmounts immediately.
    /// The parsed <see cref="AdmxParser"/> is cached keyed by source ISO path
    /// so the WIM is only ever mounted once per session.
    /// </summary>
    private async Task<AdmxParser?> LoadAdmxFromIsoAsync()
    {
        var s = BuildSession.Current;

        if (string.IsNullOrEmpty(s.SourceIsoPath) || !File.Exists(s.SourceIsoPath))
        {
            AppDialog.Alert(this,
                "No Windows ISO is selected. Please select a source ISO in Step 1 first.",
                "ADMX Source", AppDialogIcon.Warning);
            return null;
        }

        // Block if the ISO scan is already running — two concurrent DISM WIM mounts
        // on the same image can conflict and cause errors.
        if (_scanRunning)
        {
            AppDialog.Alert(this,
                "The ISO scan is still in progress.\n\n" +
                "Please wait for it to finish before loading ADMX files from the ISO.",
                "ADMX Source", AppDialogIcon.Info);
            return null;
        }

        // Return the cached result if it matches the current ISO
        if (_isoAdmxCache != null && _isoAdmxCacheKey == s.SourceIsoPath)
            return _isoAdmxCache;

        AdmxIsoLoadingText.Visibility = Visibility.Visible;
        AdmxIsoLoadingText.Text       = "Locating WIM in ISO…";

        string? tempMount = null;
        try
        {
            var ct = CancellationToken.None;

            // Step 1: mount ISO (reuses already-mounted drive if available) → get WIM path
            AdmxIsoLoadingText.Text = "Mounting ISO…";
            string wimPath = await FindOrMountWimAsync(s, ct);

            // Step 2: mount the WIM read-only into a temp folder
            // Use a short, fixed base path to avoid DISM path-length errors (Error 3)
            Directory.CreateDirectory(@"C:\GIBMount");
            tempMount = Path.Combine(@"C:\GIBMount", "ADMX_" + Path.GetRandomFileName().Replace(".", ""));
            Directory.CreateDirectory(tempMount);

            int wimIndex  = s.SelectedImage?.Index ?? 1;
            string mArgs  = $"/Mount-Image /ImageFile:\"{wimPath}\" /Index:{wimIndex} /MountDir:\"{tempMount}\" /ReadOnly";

            AdmxIsoLoadingText.Text = "Mounting WIM (read-only) — this may take a minute…";
            int mountExit = await RunSilentAsync("dism.exe", mArgs, ct);
            if (mountExit != 0)
            {
                AppDialog.Alert(this,
                    "DISM failed to mount the WIM image (exit code: " + mountExit + ").\n" +
                    "Policy definitions from ISO are unavailable.",
                    "ADMX Source", AppDialogIcon.Warning);
                return null;
            }

            // Step 3: locate PolicyDefinitions inside the mounted WIM
            string policyDefsPath = Path.Combine(tempMount, "Windows", "PolicyDefinitions");
            if (!Directory.Exists(policyDefsPath))
            {
                AppDialog.Alert(this,
                    "The Windows\\PolicyDefinitions folder was not found inside the WIM.\n" +
                    "The ISO may not contain standard ADMX files.",
                    "ADMX Source", AppDialogIcon.Warning);
                return null;
            }

            // Step 4: parse ADMX files (progress shown in-line)
            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => AdmxIsoLoadingText.Text = msg));

            var parser = await AdmxParser.GetOrLoadAsync(policyDefsPath, progress);

            // Cache so subsequent clicks skip the mount entirely
            _isoAdmxCache    = parser;
            _isoAdmxCacheKey = s.SourceIsoPath;
            return parser;
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this,
                $"Failed to load ADMX files from ISO:\n\n{ex.Message}",
                "ADMX Source", AppDialogIcon.Error);
            return null;
        }
        finally
        {
            // Always unmount, even on failure — leave no dangling WIM mounts
            if (tempMount != null && Directory.Exists(tempMount))
            {
                AdmxIsoLoadingText.Text = "Unmounting WIM…";
                try
                {
                    await RunSilentAsync("dism.exe",
                        $"/Unmount-Image /MountDir:\"{tempMount}\" /Discard",
                        CancellationToken.None);
                }
                catch { }
                try { Directory.Delete(tempMount, true); } catch { }
            }
            AdmxIsoLoadingText.Visibility = Visibility.Collapsed;
        }
    }

    // ── Trusted Certificates ──────────────────────────────────────────────────

    private void AddCert_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Select certificate file",
            Filter      = "Certificate files|*.cer;*.crt;*.pem|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var path in dlg.FileNames)
        {
            // Skip duplicates (same source + same store)
            if (_certificates.Any(c => string.Equals(c.SourcePath, path,
                                                     StringComparison.OrdinalIgnoreCase)))
                continue;
            _certificates.Add(new CertificateEntry { SourcePath = path, Store = "Root" });
        }
        RefreshCertificateList();
    }

    private void RefreshCertificateList()
    {
        CertListPanel.Children.Clear();

        for (int i = 0; i < _certificates.Count; i++)
            CertListPanel.Children.Add(BuildCertRow(_certificates[i], i));

        if (CertCountPill != null)
            CertCountPill.Text = $"{_certificates.Count} cert(s)";
    }

    private UIElement BuildCertRow(CertificateEntry cert, int index)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(10, 8, 10, 8),
        };
        row.SetResourceReference(Border.BackgroundProperty, "BG3Brush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // "TrustedPublisher (code-sign)" needs ~210px of internal text width plus
        // the ComboBox chevron (~22px) — bump column to 240px so the label is
        // not clipped on long store names.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Filename + path (truncated)
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var fileNameTb = new TextBlock
        {
            Text         = System.IO.Path.GetFileName(cert.SourcePath),
            FontSize     = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        fileNameTb.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");
        info.Children.Add(fileNameTb);
        var pathTb = new TextBlock
        {
            Text         = cert.SourcePath,
            FontSize     = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 1, 0, 0),
        };
        pathTb.SetResourceReference(TextBlock.ForegroundProperty, "FG3Brush");
        info.Children.Add(pathTb);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Store dropdown
        var storeCombo = new ComboBox
        {
            Width             = 230,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
        };
        storeCombo.Items.Add(new ComboBoxItem { Content = "Root (Trusted Root CA)",        Tag = "Root" });
        storeCombo.Items.Add(new ComboBoxItem { Content = "CA (Intermediate)",              Tag = "CA" });
        storeCombo.Items.Add(new ComboBoxItem { Content = "TrustedPublisher (code-sign)",   Tag = "TrustedPublisher" });
        foreach (var obj in storeCombo.Items)
        {
            if (obj is ComboBoxItem item && (item.Tag as string) == cert.Store)
            {
                storeCombo.SelectedItem = item;
                break;
            }
        }
        if (storeCombo.SelectedItem == null) storeCombo.SelectedIndex = 0;
        int capturedStoreIdx = index;
        storeCombo.SelectionChanged += (_, _) =>
        {
            if (capturedStoreIdx >= _certificates.Count) return;
            var tag = (storeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Root";
            _certificates[capturedStoreIdx].Store = tag;
        };
        Grid.SetColumn(storeCombo, 1);
        grid.Children.Add(storeCombo);

        // Remove button
        int capturedIdx = index;
        var removeBtn = new Button
        {
            Content           = "✕",
            Style             = (Style)Application.Current.Resources["GhostButtonStyle"],
            FontSize          = 12,
            Padding           = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            FocusVisualStyle  = null,
        };
        removeBtn.Click += (_, _) =>
        {
            if (capturedIdx < _certificates.Count)
                _certificates.RemoveAt(capturedIdx);
            RefreshCertificateList();
        };
        Grid.SetColumn(removeBtn, 2);
        grid.Children.Add(removeBtn);

        row.Child = grid;
        return row;
    }

    private void RefreshGroupPoliciesPanel()
    {
        GPOListPanel.Children.Clear();

        if (_groupPolicies.Count == 0)
        {
            GPOEmptyHint.Visibility     = Visibility.Visible;
            GPOListContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            GPOEmptyHint.Visibility     = Visibility.Collapsed;
            GPOListContainer.Visibility = Visibility.Visible;

            foreach (var (entry, idx) in _groupPolicies.Select((e, i) => (e, i)))
                GPOListPanel.Children.Add(BuildGPORow(entry, idx));
        }

        UpdateCounters();
    }

    private UIElement BuildGPORow(GroupPolicyEntry entry, int index)
    {
        var row = new Border
        {
            Background   = (Brush)Application.Current.Resources["BG0Brush"],
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(10, 8, 10, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // state badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // class badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // remove

        // State badge
        bool isEnabled = entry.State == "Enabled";
        var stOkC  = (Color)Application.Current.Resources["OkColor"];
        var stErrC = (Color)Application.Current.Resources["ErrColor"];
        var stateBadge = new Border
        {
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin          = new Thickness(0, 0, 10, 0),
            Background      = isEnabled
                ? new SolidColorBrush(Color.FromArgb(0x28, stOkC.R,  stOkC.G,  stOkC.B))
                : new SolidColorBrush(Color.FromArgb(0x28, stErrC.R, stErrC.G, stErrC.B)),
        };
        stateBadge.Child = new TextBlock
        {
            Text       = entry.State,
            Foreground = isEnabled
                ? (Brush)Application.Current.Resources["OkBrush"]
                : (Brush)Application.Current.Resources["ErrBrush"],
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(stateBadge, 0);

        // Name + category
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text         = entry.DisplayName,
            Foreground   = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize     = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text         = string.IsNullOrEmpty(entry.CategoryPath) ? entry.RegistryKey : entry.CategoryPath,
            Foreground   = (Brush)Application.Current.Resources["FG3Brush"],
            FontSize     = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(info, 1);

        // Class badge
        bool isMachine = entry.PolicyClass != "User";
        var clGold1C = (Color)Application.Current.Resources["Gold1Color"];
        var clWarnC  = (Color)Application.Current.Resources["WarnColor"];
        var classBadge = new Border
        {
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(5, 2, 5, 2),
            Margin          = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background      = new SolidColorBrush(isMachine
                ? Color.FromArgb(0x28, clGold1C.R, clGold1C.G, clGold1C.B)
                : Color.FromArgb(0x28, clWarnC.R,  clWarnC.G,  clWarnC.B)),
        };
        classBadge.Child = new TextBlock
        {
            Text       = isMachine ? "Machine" : "User",
            Foreground = isMachine
                ? (Brush)Application.Current.Resources["Gold1Brush"]
                : (Brush)Application.Current.Resources["WarnBrush"],
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(classBadge, 2);

        // Remove button
        int capturedIdx = index;
        var removeBtn = new Button
        {
            Content             = "✕",
            Style               = (Style)Application.Current.Resources["GhostButtonStyle"],
            FontSize            = 12,
            Padding             = new Thickness(6, 2, 6, 2),
            VerticalAlignment   = VerticalAlignment.Center,
            FocusVisualStyle    = null,
        };
        removeBtn.Click += (_, _) =>
        {
            // FIX #3: bounds check guards against rapid double-click firing stale index
            if (capturedIdx < _groupPolicies.Count)
                _groupPolicies.RemoveAt(capturedIdx);
            RefreshGroupPoliciesPanel();
        };
        Grid.SetColumn(removeBtn, 3);

        grid.Children.Add(stateBadge);
        grid.Children.Add(info);
        grid.Children.Add(classBadge);
        grid.Children.Add(removeBtn);
        row.Child = grid;
        return row;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 1);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 3);
    }
}
