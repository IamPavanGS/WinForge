using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using GoldenISOBuilder.Models;
using Microsoft.Win32;
using Catalog = GoldenISOBuilder.Services.Catalog;

namespace GoldenISOBuilder.Views;

public partial class Step2Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    private readonly List<StagedApp>  _apps              = [];
    private readonly List<StagedFile> _stagedFiles       = [];
    private readonly List<string>    _languagePacks      = [];
    private readonly List<string>    _driverFolders      = [];
    private readonly List<FontEntry> _fonts              = [];
    private readonly List<DeploymentScript> _deploymentScripts = [];

    // Language pack folder-scan state
    private Dictionary<string, List<string>> _langGroupsScanned = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Threading.DispatcherTimer? _langPopupTimer;

    // Human-readable display names for BCP-47 locale codes
    private static readonly Dictionary<string, string> LangNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["af-za"]       = "Afrikaans (South Africa)",
        ["ar-sa"]       = "Arabic (Saudi Arabia)",
        ["bg-bg"]       = "Bulgarian (Bulgaria)",
        ["ca-es"]       = "Catalan (Spain)",
        ["cs-cz"]       = "Czech (Czech Republic)",
        ["cy-gb"]       = "Welsh (United Kingdom)",
        ["da-dk"]       = "Danish (Denmark)",
        ["de-de"]       = "German (Germany)",
        ["el-gr"]       = "Greek (Greece)",
        ["en-gb"]       = "English (United Kingdom)",
        ["en-us"]       = "English (United States)",
        ["es-es"]       = "Spanish (Spain)",
        ["es-mx"]       = "Spanish (Mexico)",
        ["et-ee"]       = "Estonian (Estonia)",
        ["eu-es"]       = "Basque (Spain)",
        ["fi-fi"]       = "Finnish (Finland)",
        ["fr-ca"]       = "French (Canada)",
        ["fr-fr"]       = "French (France)",
        ["gl-es"]       = "Galician (Spain)",
        ["he-il"]       = "Hebrew (Israel)",
        ["hr-hr"]       = "Croatian (Croatia)",
        ["hu-hu"]       = "Hungarian (Hungary)",
        ["id-id"]       = "Indonesian (Indonesia)",
        ["it-it"]       = "Italian (Italy)",
        ["ja-jp"]       = "Japanese (Japan)",
        ["ko-kr"]       = "Korean (Korea)",
        ["lt-lt"]       = "Lithuanian (Lithuania)",
        ["lv-lv"]       = "Latvian (Latvia)",
        ["ms-my"]       = "Malay (Malaysia)",
        ["nb-no"]       = "Norwegian Bokmål (Norway)",
        ["nl-nl"]       = "Dutch (Netherlands)",
        ["pl-pl"]       = "Polish (Poland)",
        ["pt-br"]       = "Portuguese (Brazil)",
        ["pt-pt"]       = "Portuguese (Portugal)",
        ["ro-ro"]       = "Romanian (Romania)",
        ["ru-ru"]       = "Russian (Russia)",
        ["sk-sk"]       = "Slovak (Slovakia)",
        ["sl-si"]       = "Slovenian (Slovenia)",
        ["sq-al"]       = "Albanian (Albania)",
        ["sr-latn-rs"]  = "Serbian Latin (Serbia)",
        ["sv-se"]       = "Swedish (Sweden)",
        ["th-th"]       = "Thai (Thailand)",
        ["tr-tr"]       = "Turkish (Turkey)",
        ["uk-ua"]       = "Ukrainian (Ukraine)",
        ["vi-vn"]       = "Vietnamese (Vietnam)",
        ["zh-cn"]       = "Chinese Simplified (China)",
        ["zh-tw"]       = "Chinese Traditional (Taiwan)",
    };

    public Step2Page()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore state from BuildSession
        var s = BuildSession.Current;

        if (!string.IsNullOrEmpty(s.WallpaperPath) && File.Exists(s.WallpaperPath))
            ApplyWallpaper(s.WallpaperPath);
        if (!string.IsNullOrEmpty(s.LockScreenPath) && File.Exists(s.LockScreenPath))
            ApplyLockScreen(s.LockScreenPath);

        foreach (var app in s.StagedApps)
            _apps.Add(app);

        // Backward-compat migration: old .gibprofile stored files in PublicDesktopFiles (List<string>).
        // If StagedFiles is empty but PublicDesktopFiles has entries, promote them to StagedFiles
        // with the default destination (Users\Public\Desktop) and clear the legacy list.
        if (s.StagedFiles.Count == 0 && s.PublicDesktopFiles.Count > 0)
        {
            s.StagedFiles = s.PublicDesktopFiles
                .Select(p => new StagedFile { SourcePath = p })
                .ToList();
            s.PublicDesktopFiles.Clear();
        }

        foreach (var sf in s.StagedFiles)
            _stagedFiles.Add(sf);

        foreach (var lp in s.LanguagePackPaths)
            _languagePacks.Add(lp);

        foreach (var d in s.DriverFolderPaths)
            _driverFolders.Add(d);

        foreach (var f in s.Fonts)
            _fonts.Add(f);

        IncludeDeploymentToggle.IsChecked = s.IncludeDeploymentScripts;

        foreach (var sc in s.DeploymentScripts)
            _deploymentScripts.Add(sc);

        RefreshAppsPanel();
        RefreshPublicFilesPanel();
        RefreshLangPacksPanel();
        RefreshDriversPanel();
        RefreshFontsPanel();
        RefreshDeploymentScriptsPanel();

        // Auto-fetch features card: visible only when the master toggle is on
        // (set on Settings page). Refreshed every time the page becomes visible
        // so flipping the toggle in Settings reflects without an app restart.
        IsVisibleChanged += (_, ev) =>
        {
            if ((bool)ev.NewValue) RefreshAutoFetchVisibility();
        };
        RefreshAutoFetchVisibility();
        RefreshUpdatesResolvedPanel();

        // Set vendor dropdown to Dell now that all controls referenced by
        // Vendor_Changed exist. Doing this in XAML via IsSelected="True" fires
        // SelectionChanged during InitializeComponent -- before ModelListPanel
        // is constructed -- and crashes the whole MainWindow ctor.
        if (VendorCombo != null && VendorCombo.SelectedIndex < 0)
            VendorCombo.SelectedIndex = 0;
    }

    // ── Auto-fetch (Windows Updates) ─────────────────────────────────────────
    //
    // Renders the new "Windows Updates" card above the Language Packs section.
    // Two modes: Auto (Microsoft Update Catalog via Phase 1-2 plumbing) and
    // Manual (folder of MSU files -- today's behaviour for power users).
    //
    // All state persists on BuildSession.UpdatesMsuPaths.

    private List<Catalog.CatalogItem> _availableUpdates = new();
    private readonly HashSet<string> _selectedUpdateIds = new(StringComparer.OrdinalIgnoreCase);
    private Catalog.CatalogCacheManager?  _cache;
    private Catalog.ResumeableDownloader? _downloader;
    private Catalog.MsCatalogWebService?  _msCatalog;

    private void RefreshAutoFetchVisibility()
    {
        bool on =
            GoldenISOBuilder.Helpers.AppSettingsLoader.ReadEnableAutoFetchFeatures();
        BuildSession.Current.EnableAutoFetchFeatures = on;
        UpdatesCard.Visibility     = on ? Visibility.Visible : Visibility.Collapsed;
        DriversAutoCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) RefreshFetchedPacksPanel();
        if (on)
        {
            // Update the detected-build label from the current session.
            var s = BuildSession.Current;
            if (s.SelectedImage != null && !string.IsNullOrEmpty(s.SelectedImage.Name))
            {
                UpdatesDetectedBuild.Text =
                    $"ISO: {s.SelectedImage.Name} ({s.SelectedArch})";
            }
            else
            {
                UpdatesDetectedBuild.Text =
                    "ISO not yet analysed -- pick a source ISO in Step 1 first.";
            }
        }
    }

    private void UpdatesMode_Changed(object sender, RoutedEventArgs e)
    {
        if (UpdatesAutoPanel == null || UpdatesManualPanel == null) return;
        bool auto = UpdatesAutoMode.IsChecked == true;
        UpdatesAutoPanel.Visibility   = auto ? Visibility.Visible   : Visibility.Collapsed;
        UpdatesManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UpdatesRefresh_Click(object sender, RoutedEventArgs e)
    {
        UpdatesRefreshBtn.IsEnabled = false;
        UpdatesStatusText.Text = "Querying Microsoft Update Catalog...";
        UpdatesProgress.Visibility = Visibility.Visible;
        UpdatesProgress.IsIndeterminate = true;

        try
        {
            EnsureCatalogServices();

            var win11Ver = BuildSession.Current.IsoOsVersion;
            var vTag     = win11Ver.Length > 0 ? " version " + win11Ver : "";
            var vLabel   = win11Ver.Length > 0 ? win11Ver : "25H2";

            // Two targeted searches against the public catalog web UI -- much
            // faster (and more reliable) than walking the full ~900 MB
            // wsusscn2.cab via WUA. Returns in 2-5 seconds typically.
            var combined = new List<Catalog.CatalogItem>();
            foreach (var q in new[]
            {
                "Windows 11" + vTag + " cumulative update x64",
                "Windows 11 " + vLabel + " enablement",
                "Windows 11" + vTag + " .NET cumulative"
            })
            {
                UpdatesStatusText.Text = "Querying \"" + q + "\"...";
                var hits = await _msCatalog!.SearchAsync(q);
                combined.AddRange(hits);
            }

            // De-dup by UpdateId. Prefer newer last-updated date.
            var deduped = combined
                .GroupBy(i => i.UpdateId)
                .Select(g => g.First())
                .OrderByDescending(i => i.LastUpdated)
                .ToList();

            // Drop results that explicitly target a different Windows 11 version.
            // Items without a "version X" tag in the title pass through unchanged.
            if (win11Ver.Length > 0)
            {
                var otherVersions = new[] { "22H2", "23H2", "24H2", "25H2", "26H2" }
                    .Where(v => v != win11Ver);
                deduped = deduped
                    .Where(u => !otherVersions.Any(v =>
                        u.Title.Contains("version " + v, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            _availableUpdates = deduped;

            RenderUpdatesList(UpdatesSearchBox?.Text?.Trim());
            UpdatesExportBtn.IsEnabled = _availableUpdates.Count > 0;
            var verSuffix = win11Ver.Length > 0 ? " for Windows 11 " + win11Ver : "";
            UpdatesStatusText.Text = _availableUpdates.Count == 0
                ? "Catalog returned no results. Try the manual folder mode below."
                : "Catalog ready -- " + _availableUpdates.Count + " update(s) available" + verSuffix + ". Tick any number to slipstream.";
        }
        catch (Exception ex)
        {
            UpdatesStatusText.Text = "Catalog query failed: " + ex.Message +
                "  Try the manual folder mode below.";
        }
        finally
        {
            UpdatesProgress.Visibility = Visibility.Collapsed;
            UpdatesProgress.IsIndeterminate = false;
            UpdatesRefreshBtn.IsEnabled = true;
        }
    }

    private void RenderUpdatesList(string? filter)
    {
        if (UpdatesListPanel == null) return;
        UpdatesListPanel.Children.Clear();

        IEnumerable<Catalog.CatalogItem> visible = _availableUpdates;
        if (!string.IsNullOrEmpty(filter))
            visible = visible.Where(u =>
                u.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                u.KbId .Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var u in visible.Take(120))
        {
            var cb = new CheckBox
            {
                Tag        = u,
                Margin     = new Thickness(0, 1, 0, 1),
                Foreground = (System.Windows.Media.Brush)FindResource("FG0Brush"),
                IsChecked  = _selectedUpdateIds.Contains(u.UpdateId),
                ToolTip    = u.Title
            };
            // Compose a short label so the checkbox row is readable.
            cb.Content =
                $"{(string.IsNullOrEmpty(u.KbId) ? "" : u.KbId + "  ")}" +
                $"{Trim(u.Title, 90)}" +
                $"{(string.IsNullOrEmpty(u.SizeText) ? "" : "   (" + u.SizeText + ")")}";
            cb.Checked   += UpdateRow_CheckChanged;
            cb.Unchecked += UpdateRow_CheckChanged;
            UpdatesListPanel.Children.Add(cb);
        }

        if (!UpdatesListPanel.Children.OfType<CheckBox>().Any())
        {
            UpdatesListPanel.Children.Add(new TextBlock
            {
                Text       = _availableUpdates.Count == 0
                             ? "Click 'Refresh catalog' above to query Microsoft Update Catalog."
                             : "(no matches for current filter)",
                Foreground = (System.Windows.Media.Brush)FindResource("FG3Brush"),
                FontStyle  = FontStyles.Italic,
                Margin     = new Thickness(4)
            });
        }

        UpdatesFetchBtn.IsEnabled = _selectedUpdateIds.Count > 0;
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0 || double.IsInfinity(seconds))
            return "?";
        if (seconds < 60)        return $"{seconds:F0}s";
        if (seconds < 60 * 60)   return $"{seconds / 60:F0}m {seconds % 60:F0}s";
        return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m";
    }

    private void UpdatesSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (UpdatesSearchBox == null || UpdatesListPanel == null) return;
        RenderUpdatesList(UpdatesSearchBox.Text.Trim());
    }

    private void UpdateRow_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not Catalog.CatalogItem u) return;
        if (cb.IsChecked == true) _selectedUpdateIds.Add(u.UpdateId);
        else                       _selectedUpdateIds.Remove(u.UpdateId);
        UpdatesFetchBtn.IsEnabled = _selectedUpdateIds.Count > 0;
    }

    private void UpdatesExport_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdates.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export update list",
            Filter     = "Text file (*.txt)|*.txt|CSV (*.csv)|*.csv",
            FileName   = $"WindowsUpdates_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog() != true) return;

        var isCsv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();

        if (isCsv)
        {
            sb.AppendLine("Selected,KB,Title,Classification,Size,LastUpdated,UpdateID");
            foreach (var u in _availableUpdates)
            {
                var ticked = _selectedUpdateIds.Contains(u.UpdateId) ? "Y" : "";
                sb.Append(ticked).Append(',')
                  .Append(CsvField(u.KbId)).Append(',')
                  .Append(CsvField(u.Title)).Append(',')
                  .Append(CsvField(u.Classification)).Append(',')
                  .Append(CsvField(u.SizeText)).Append(',')
                  .Append(CsvField(u.LastUpdated)).Append(',')
                  .AppendLine(u.UpdateId);
            }
        }
        else
        {
            sb.AppendLine($"ALE Image Forge -- Windows Update list ({DateTime.Now:yyyy-MM-dd HH:mm})");
            sb.AppendLine($"Total available: {_availableUpdates.Count}    Selected: {_selectedUpdateIds.Count}");
            sb.AppendLine(new string('─', 100));
            sb.AppendLine($"{"Sel",-4}{"KB",-13}{"Title",-70}{"Size",-12}{"Updated"}");
            sb.AppendLine(new string('─', 100));
            foreach (var u in _availableUpdates)
            {
                var ticked = _selectedUpdateIds.Contains(u.UpdateId) ? "[✓]" : "[ ]";
                sb.Append(ticked.PadRight(4));
                sb.Append((u.KbId ?? "").PadRight(13));
                sb.Append(Trim(u.Title ?? "", 68).PadRight(70));
                sb.Append((u.SizeText ?? "").PadRight(12));
                sb.AppendLine(u.LastUpdated ?? "");
            }
            sb.AppendLine();
            sb.AppendLine("Selected updates will be slipstreamed into install.wim during the build.");
            sb.AppendLine("UpdateID column omitted in text mode -- choose CSV for the GUIDs.");
        }

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            UpdatesStatusText.Text = $"Exported {_availableUpdates.Count} update(s) → {dlg.FileName}";
        }
        catch (Exception ex)
        {
            UpdatesStatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private static string CsvField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n');
        if (!needsQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private async void UpdatesFetch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUpdateIds.Count == 0) return;

        UpdatesFetchBtn.IsEnabled = false;
        UpdatesProgress.Visibility = Visibility.Visible;
        UpdatesProgress.IsIndeterminate = false;

        var selected = _availableUpdates
            .Where(u => _selectedUpdateIds.Contains(u.UpdateId))
            .ToList();

        int ok = 0;
        var failures = new System.Text.StringBuilder();

        try
        {
            EnsureCatalogServices();

            int idx = 0;
            foreach (var u in selected)
            {
                idx++;
                UpdatesStatusText.Text =
                    $"[{idx}/{selected.Count}] Resolving {u.KbId} download URL...";
                UpdatesProgress.IsIndeterminate = true;

                try
                {
                    var url = await _msCatalog!.ResolveDownloadUrlAsync(u.UpdateId);
                    if (string.IsNullOrEmpty(url))
                    {
                        failures.AppendLine(
                            $"  • {u.KbId}: catalog did not expose a direct URL.");
                        continue;
                    }

                    UpdatesStatusText.Text =
                        $"[{idx}/{selected.Count}] Starting download of {u.KbId}...";
                    UpdatesProgress.IsIndeterminate = false;
                    UpdatesProgress.Value = 0;

                    var key = (u.KbId.Length > 0 ? u.KbId : $"update-{u.UpdateId[..8]}") +
                              System.IO.Path.GetExtension(new Uri(url).LocalPath);
                    var dest = _cache!.GetEntryPath(
                        Catalog.CatalogCacheManager.Category.WindowsUpdates, key);

                    var progress = new Progress<Catalog.DownloadProgress>(p =>
                    {
                        // Percentage bar
                        if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
                            UpdatesProgress.Value =
                                (double)p.BytesDownloaded / p.TotalBytes.Value * 100;

                        // Rich status text: [i/n] KB12345 -- 425 MB / 871 MB (49%) -- 12.5 Mbps -- 35s left
                        string size = $"{p.BytesDownloaded / 1024 / 1024} MB";
                        string pct  = "";
                        string eta  = "";
                        if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
                        {
                            size += $" / {p.TotalBytes.Value / 1024 / 1024} MB";
                            pct   = $" ({(double)p.BytesDownloaded / p.TotalBytes.Value * 100:F0}%)";
                            if (p.Mbps.HasValue && p.Mbps.Value > 0.1)
                            {
                                var remainingBits = (p.TotalBytes.Value - p.BytesDownloaded) * 8.0;
                                var seconds       = remainingBits / (p.Mbps.Value * 1_000_000);
                                eta               = "  --  " + FormatEta(seconds) + " left";
                            }
                        }
                        string speed = p.Mbps.HasValue
                            ? $"  --  {p.Mbps.Value:F1} Mbps" : "";
                        UpdatesStatusText.Text =
                            $"[{idx}/{selected.Count}] {u.KbId}  --  {size}{pct}{speed}{eta}";
                    });
                    var result = await _downloader!.DownloadAsync(
                        url, dest, expectedSha256: null, progress);

                    _cache.WriteManifest(dest, new Catalog.CacheManifest
                    {
                        SourceUrl     = url,
                        Sha256        = result.Sha256,
                        SizeBytes     = result.SizeBytes,
                        DownloadedUtc = DateTime.UtcNow,
                        ExpiresUtc    = DateTime.UtcNow.AddDays(60),
                        Vendor        = "Microsoft",
                        Notes         = u.Title
                    });

                    if (!BuildSession.Current.UpdatesMsuPaths.Contains(dest))
                        BuildSession.Current.UpdatesMsuPaths.Add(dest);
                    ok++;
                }
                catch (Exception ex)
                {
                    failures.AppendLine($"  • {u.KbId}: {ex.Message}");
                }
            }

            RefreshUpdatesResolvedPanel();

            if (failures.Length == 0)
                UpdatesStatusText.Text =
                    $"Fetched {ok} update(s). They will be slipstreamed during the build.";
            else if (ok == 0)
                UpdatesStatusText.Text =
                    "No updates fetched. Reason(s):\n" + failures.ToString().TrimEnd();
            else
                UpdatesStatusText.Text =
                    $"Fetched {ok} update(s); {selected.Count - ok} failed:\n" +
                    failures.ToString().TrimEnd();
        }
        finally
        {
            UpdatesProgress.Visibility = Visibility.Collapsed;
            UpdatesProgress.IsIndeterminate = false;
            UpdatesFetchBtn.IsEnabled = _selectedUpdateIds.Count > 0;
        }
    }

    private void UpdatesBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder containing MSU/CAB update packages"
        };
        if (dlg.ShowDialog() != true) return;

        UpdatesFolderBox.Text = dlg.FolderName;
        var msus = Directory.EnumerateFiles(dlg.FolderName, "*.msu",
                       SearchOption.TopDirectoryOnly)
                   .Concat(Directory.EnumerateFiles(dlg.FolderName, "*.cab",
                       SearchOption.TopDirectoryOnly))
                   .ToList();

        BuildSession.Current.UpdatesMsuPaths.Clear();
        BuildSession.Current.UpdatesMsuPaths.AddRange(msus);
        RefreshUpdatesResolvedPanel();
        UpdatesManualHint.Text = msus.Count > 0
            ? $"Picked up {msus.Count} update file(s)."
            : "No .msu or .cab files found in the selected folder.";
    }

    private void RefreshUpdatesResolvedPanel()
    {
        if (UpdatesResolvedPanel == null) return;
        UpdatesResolvedPanel.Children.Clear();
        foreach (var msu in BuildSession.Current.UpdatesMsuPaths)
        {
            var row = new TextBlock
            {
                Text       = "  • " + System.IO.Path.GetFileName(msu),
                Foreground = (System.Windows.Media.Brush)
                                FindResource("FG1Brush"),
                FontSize   = 11.5,
                ToolTip    = msu
            };
            UpdatesResolvedPanel.Children.Add(row);
        }
    }

    private void EnsureCatalogServices()
    {
        _cache      ??= new Catalog.CatalogCacheManager();
        _downloader ??= new Catalog.ResumeableDownloader();
        _msCatalog  ??= new Catalog.MsCatalogWebService();
    }

    // ── Auto-fetch (Driver packs) ─────────────────────────────────────────────
    //
    // Adds a second card under Driver Injection. Vendor dropdown picks
    // Dell / HP / Lenovo, the model list is populated lazily on "Load models",
    // user multi-selects up to 3 SKUs, "Fetch selected" downloads each pack
    // via Phase 1 ResumeableDownloader and registers the resulting local path
    // in BuildSession.AutoFetchedDriverPacks for the Phase 4 pipeline step.

    private Catalog.IDriverPackService? _activeVendorService;
    private List<Catalog.DriverPackModel> _vendorModels = new();
    private readonly HashSet<string> _selectedSystemIds = new(StringComparer.OrdinalIgnoreCase);

    private void Vendor_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Vendor changed -- wipe the previous model list and selection.
        // Guard against the firing that happens during XAML parse when
        // ComboBox first picks its initial item: controls referenced below
        // appear later in the XAML and aren't constructed yet at that point.
        if (ModelListPanel == null || DriverFetchBtn == null) return;

        _vendorModels.Clear();
        _selectedSystemIds.Clear();
        ModelListPanel.Children.Clear();
        DriverFetchBtn.IsEnabled = false;
        if (DriverStatusText != null)
            DriverStatusText.Text = "Click 'Load models' to query the vendor catalogue.";
    }

    private async void DriverRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (VendorCombo.SelectedItem is not ComboBoxItem ci ||
            ci.Tag is not string vendorTag)
            return;

        DriverRefreshBtn.IsEnabled = false;
        DriverStatusText.Text = $"Loading {vendorTag} catalogue...";
        DriverProgress.Visibility = Visibility.Visible;
        DriverProgress.IsIndeterminate = true;

        try
        {
            EnsureCatalogServices();
            _activeVendorService = vendorTag switch
            {
                "Dell"   => new Catalog.DellDriverService  (_cache!, _downloader!),
                "HP"     => new Catalog.HpDriverService    (_cache!, _downloader!),
                "Lenovo" => new Catalog.LenovoDriverService(_cache!, _downloader!),
                _        => null
            };
            if (_activeVendorService == null)
            {
                DriverStatusText.Text = "Unknown vendor.";
                return;
            }

            await _activeVendorService.EnsureCatalogAsync();
            _vendorModels = (await _activeVendorService.ListModelsAsync()).ToList();
            ModelSearchBox.Text = "";
            RenderModelList(filter: null);

            DriverStatusText.Text =
                $"{_vendorModels.Count} {vendorTag} model(s) available -- type to search, tick up to 3.";
        }
        catch (Exception ex)
        {
            DriverStatusText.Text =
                "Catalogue load failed: " + ex.Message +
                "  (Manual driver folder picker above still works.)";
        }
        finally
        {
            DriverProgress.IsIndeterminate = false;
            DriverProgress.Visibility = Visibility.Collapsed;
            DriverRefreshBtn.IsEnabled = true;
        }
    }

    private void ModelSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ModelSearchBox == null || ModelListPanel == null) return;
        var text = ModelSearchBox.Text.Trim();

        // For Lenovo specifically: if the user typed exactly 4 chars and it's
        // not already in our seed list, treat it as a direct MT input. The
        // built-in seed is just for discovery -- real Lenovo MTs come off the
        // laptop's underside sticker and there are thousands of them.
        if (_activeVendorService is Catalog.LenovoDriverService &&
            text.Length == 4 &&
            !_vendorModels.Any(m =>
                m.SystemId.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            _vendorModels.Add(new Catalog.DriverPackModel(
                Catalog.DriverVendor.Lenovo, text.ToUpperInvariant(),
                $"Custom MT {text.ToUpperInvariant()}", "Custom"));
        }

        RenderModelList(text);
    }

    private void RenderModelList(string? filter)
    {
        ModelListPanel.Children.Clear();
        IEnumerable<Catalog.DriverPackModel> visible = _vendorModels;
        if (!string.IsNullOrEmpty(filter))
            visible = visible.Where(m =>
                m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                m.SystemId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (m.Brand?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        foreach (var m in visible.Take(200))
        {
            var cb = new CheckBox
            {
                Content    = $"{m.Name}   ({m.SystemId})",
                Tag        = m,
                Margin     = new Thickness(0, 1, 0, 1),
                Foreground = (System.Windows.Media.Brush)FindResource("FG0Brush"),
                IsChecked  = _selectedSystemIds.Contains(m.SystemId)
            };
            cb.Checked   += Model_CheckChanged;
            cb.Unchecked += Model_CheckChanged;
            ModelListPanel.Children.Add(cb);
        }
        if (!ModelListPanel.Children.OfType<CheckBox>().Any())
        {
            ModelListPanel.Children.Add(new TextBlock
            {
                Text       = "(no matches)",
                Foreground = (System.Windows.Media.Brush)FindResource("FG3Brush"),
                FontStyle  = FontStyles.Italic,
                Margin     = new Thickness(4)
            });
        }
    }

    private void Model_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not Catalog.DriverPackModel m) return;
        if (cb.IsChecked == true)
        {
            if (_selectedSystemIds.Count >= 3)
            {
                cb.IsChecked = false;
                DriverStatusText.Text =
                    "Maximum 3 SKUs per build. Untick another model first to add this one.";
                return;
            }
            _selectedSystemIds.Add(m.SystemId);
        }
        else
        {
            _selectedSystemIds.Remove(m.SystemId);
        }
        DriverFetchBtn.IsEnabled = _selectedSystemIds.Count > 0;
        DriverStatusText.Text = _selectedSystemIds.Count > 0
            ? $"{_selectedSystemIds.Count} model(s) selected."
            : "No models selected.";
    }

    private async void DriverFetch_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVendorService == null || _selectedSystemIds.Count == 0) return;

        DriverFetchBtn.IsEnabled = false;
        DriverProgress.Visibility = Visibility.Visible;
        DriverProgress.IsIndeterminate = false;
        DriverStatusText.Text =
            $"Resolving {_selectedSystemIds.Count} driver pack(s)...";

        var failures = new System.Text.StringBuilder();
        int okPacks = 0;

        try
        {
            var ids = _selectedSystemIds.ToArray();
            foreach (var sid in ids)
            {
                var model = _vendorModels.FirstOrDefault(
                    m => string.Equals(m.SystemId, sid, StringComparison.OrdinalIgnoreCase));
                if (model == null) continue;

                // Vendor OS token differs: Dell expects "W11", HP "win11",
                // Lenovo "Win11". Each service maps internally.
                string osCode = _activeVendorService.Vendor switch
                {
                    Catalog.DriverVendor.Dell   => "W11",
                    Catalog.DriverVendor.HP     => "win11",
                    Catalog.DriverVendor.Lenovo => "Win11",
                    _                            => "W11"
                };

                DriverStatusText.Text = $"Resolving driver pack for {model.Name}...";
                var pack = await _activeVendorService.GetDriverPackAsync(sid, osCode);
                if (pack == null)
                {
                    // Collect the reason -- final status message lists every
                    // failure rather than each one being overwritten by the
                    // final "Done" line. Each service exposes a LastAttemptLog
                    // explaining what went wrong.
                    var why = _activeVendorService switch
                    {
                        Catalog.LenovoDriverService l => l.LastAttemptLog,
                        Catalog.DellDriverService   d => d.LastAttemptLog,
                        Catalog.HpDriverService     h => h.LastAttemptLog,
                        _                             => null
                    };
                    if (string.IsNullOrEmpty(why))
                        why = $"no {osCode} driver pack published (vendor returned nothing).";
                    failures.AppendLine($"  • {model.Name} ({sid}): {why}");
                    continue;
                }

                DriverStatusText.Text =
                    $"Downloading {pack.Filename} for {model.Name} ({pack.SizeBytes / 1024 / 1024} MB)...";

                var key = $"{pack.Vendor}/{sid}/{pack.Filename}";
                var dest = _cache!.GetEntryPath(
                    pack.Vendor switch
                    {
                        Catalog.DriverVendor.Dell   => Catalog.CatalogCacheManager.Category.Dell,
                        Catalog.DriverVendor.HP     => Catalog.CatalogCacheManager.Category.HP,
                        _                            => Catalog.CatalogCacheManager.Category.Lenovo
                    },
                    key);

                var progress = new Progress<Catalog.DownloadProgress>(p =>
                {
                    if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
                        DriverProgress.Value =
                            (double)p.BytesDownloaded / p.TotalBytes.Value * 100;

                    // Rich status: model -- 425 MB / 871 MB (49%) -- 12.5 Mbps -- 35s left
                    string size = $"{p.BytesDownloaded / 1024 / 1024} MB";
                    string pct  = "";
                    string eta  = "";
                    string spd  = "";
                    if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
                    {
                        size += $" / {p.TotalBytes.Value / 1024 / 1024} MB";
                        pct   = $" ({(double)p.BytesDownloaded / p.TotalBytes.Value * 100:F0}%)";
                        if (p.Mbps.HasValue && p.Mbps.Value > 0.1)
                        {
                            var remainingBits = (p.TotalBytes.Value - p.BytesDownloaded) * 8.0;
                            var seconds       = remainingBits / (p.Mbps.Value * 1_000_000);
                            eta               = "  --  " + FormatEta(seconds) + " left";
                        }
                    }
                    if (p.Mbps.HasValue && p.Mbps.Value > 0.1)
                        spd = $"  --  {p.Mbps.Value:F1} Mbps";

                    DriverStatusText.Text =
                        $"Downloading {pack.Filename} for {model.Name} -- {size}{pct}{spd}{eta}";
                });
                var result = await _downloader!.DownloadAsync(
                    pack.DownloadUrl, dest, expectedSha256: pack.Sha256, progress);

                _cache.WriteManifest(dest, new Catalog.CacheManifest
                {
                    SourceUrl     = pack.DownloadUrl,
                    Sha256        = result.Sha256,
                    SizeBytes     = result.SizeBytes,
                    DownloadedUtc = DateTime.UtcNow,
                    ExpiresUtc    = DateTime.UtcNow.AddDays(60),
                    Vendor        = pack.Vendor.ToString(),
                    Notes         = $"{pack.Model} {pack.OsVersion} {pack.Version}"
                });

                // All three vendors ship driver packs as wrapped archives /
                // self-extracting EXEs. DISM /Add-Driver /Recurse needs an
                // unpacked .inf tree, so extract every pack here before
                // staging into BuildSession.
                var localPath = dest;
                {
                    DriverStatusText.Text =
                        $"Extracting {pack.Vendor} driver pack for {model.Name}...";
                    try
                    {
                        var extractDir = dest + ".extracted";
                        if (pack.Vendor == Catalog.DriverVendor.Lenovo)
                            await Catalog.LenovoSoftPaqExtractor.ExtractAsync(
                                dest, extractDir);
                        else if (pack.Vendor == Catalog.DriverVendor.HP)
                            await Catalog.HpSoftPaqExtractor.ExtractAsync(
                                dest, extractDir);
                        else  // Dell -- .exe self-extractor or .cab
                            await Catalog.DellSoftPaqExtractor.ExtractAsync(
                                dest, extractDir);
                        localPath = extractDir;
                    }
                    catch (Exception ex)
                    {
                        failures.AppendLine(
                            $"  • {model.Name} ({sid}): downloaded but extract failed -- {ex.Message}");
                        continue;
                    }
                }

                var sel = new DriverPackSelection
                {
                    Vendor              = pack.Vendor.ToString(),
                    SystemId            = pack.SystemId,
                    ModelName           = pack.Model,
                    OsVersion           = pack.OsVersion,
                    PackVersion         = pack.Version,
                    DownloadUrl         = pack.DownloadUrl,
                    LocalCabPath        = localPath,
                    Sha256              = result.Sha256,
                    SizeBytes           = result.SizeBytes,
                    InjectWinPECritical = true
                };

                var existing = BuildSession.Current.AutoFetchedDriverPacks
                    .FirstOrDefault(p => p.SystemId == sel.SystemId &&
                                         p.Vendor   == sel.Vendor);
                if (existing != null)
                    BuildSession.Current.AutoFetchedDriverPacks.Remove(existing);
                BuildSession.Current.AutoFetchedDriverPacks.Add(sel);
                okPacks++;
            }

            RefreshFetchedPacksPanel();

            // Compose a final status that doesn't mask per-model failures.
            int totalStaged = BuildSession.Current.AutoFetchedDriverPacks.Count;
            if (failures.Length == 0)
                DriverStatusText.Text = $"Done. {totalStaged} pack(s) staged for build.";
            else if (okPacks == 0)
                DriverStatusText.Text =
                    "No packs fetched. Reason(s):\n" + failures.ToString().TrimEnd() +
                    "\nFor Lenovo: check the 4-char Machine Type on the laptop's underside sticker and type it into the search box -- the seed list is reference only.";
            else
                DriverStatusText.Text =
                    $"Fetched {okPacks} pack(s); {failures.ToString().Split('\n').Count(l => l.Trim().Length > 0)} failed:\n" +
                    failures.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            DriverStatusText.Text = "Fetch failed: " + ex.Message;
        }
        finally
        {
            DriverProgress.Visibility = Visibility.Collapsed;
            DriverFetchBtn.IsEnabled = _selectedSystemIds.Count > 0;
        }
    }

    private void RefreshFetchedPacksPanel()
    {
        if (FetchedPacksPanel == null) return;
        FetchedPacksPanel.Children.Clear();
        foreach (var p in BuildSession.Current.AutoFetchedDriverPacks)
        {
            var row = new TextBlock
            {
                Text       = $"  • {p.Vendor}  {p.ModelName} ({p.SystemId}) -- v{p.PackVersion}, " +
                             $"{p.SizeBytes / 1024 / 1024} MB",
                Foreground = (System.Windows.Media.Brush)FindResource("FG1Brush"),
                FontSize   = 11.5,
                ToolTip    = p.LocalCabPath
            };
            FetchedPacksPanel.Children.Add(row);
        }
    }

    // ── Wallpaper ─────────────────────────────────────────────────────────────

    private void WallpaperDrop_Click(object sender, MouseButtonEventArgs e)
        => BrowseWallpaper();

    private void Wallpaper_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Wallpaper_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            TrySetWallpaper(files[0]);
    }

    private void WallpaperClear_Click(object sender, RoutedEventArgs e)
    {
        BuildSession.Current.WallpaperPath = null;
        WallpaperDropZone.Visibility  = Visibility.Visible;
        WallpaperFileRow.Visibility   = Visibility.Collapsed;
        WallpaperPreview.Visibility   = Visibility.Collapsed;
        WallpaperPlaceholder.Visibility = Visibility.Visible;
        WallpaperPreview.Source       = null;
    }

    private void BrowseWallpaper()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Wallpaper Image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
            TrySetWallpaper(dlg.FileName);
    }

    private void TrySetWallpaper(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp"))
        {
            AppDialog.Alert(this, "Please select a JPG, PNG, or BMP image.", "Unsupported Format",
                AppDialogIcon.Warning);
            return;
        }
        if (!File.Exists(path)) return;
        ApplyWallpaper(path);
    }

    private void ApplyWallpaper(string path)
    {
        BuildSession.Current.WallpaperPath = path;
        WallpaperFileLabel.Text = Path.GetFileName(path);
        WallpaperDropZone.Visibility    = Visibility.Collapsed;
        WallpaperFileRow.Visibility     = Visibility.Visible;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource      = new Uri(path);
            bmp.CacheOption    = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 192;
            bmp.EndInit();
            WallpaperPreview.Source = bmp;
            WallpaperPreview.Visibility     = Visibility.Visible;
            WallpaperPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            WallpaperPreview.Visibility     = Visibility.Collapsed;
            WallpaperPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // ── Lock screen image ─────────────────────────────────────────────────────
    // Independent of the desktop wallpaper picker above. Either picker can be
    // set / unset on its own; the build engine applies each only when its own
    // path is configured.

    private void LockScreenDrop_Click(object sender, MouseButtonEventArgs e)
        => BrowseLockScreen();

    private void LockScreen_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LockScreen_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            TrySetLockScreen(files[0]);
    }

    private void LockScreenClear_Click(object sender, RoutedEventArgs e)
    {
        BuildSession.Current.LockScreenPath = null;
        LockScreenDropZone.Visibility    = Visibility.Visible;
        LockScreenFileRow.Visibility     = Visibility.Collapsed;
        LockScreenPreview.Visibility     = Visibility.Collapsed;
        LockScreenPlaceholder.Visibility = Visibility.Visible;
        LockScreenPreview.Source         = null;
    }

    private void BrowseLockScreen()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Lock Screen Image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
            TrySetLockScreen(dlg.FileName);
    }

    private void TrySetLockScreen(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp"))
        {
            AppDialog.Alert(this, "Please select a JPG, PNG, or BMP image.", "Unsupported Format",
                AppDialogIcon.Warning);
            return;
        }
        if (!File.Exists(path)) return;
        ApplyLockScreen(path);
    }

    private void ApplyLockScreen(string path)
    {
        BuildSession.Current.LockScreenPath = path;
        LockScreenFileLabel.Text = Path.GetFileName(path);
        LockScreenDropZone.Visibility = Visibility.Collapsed;
        LockScreenFileRow.Visibility  = Visibility.Visible;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(path);
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 192;
            bmp.EndInit();
            LockScreenPreview.Source        = bmp;
            LockScreenPreview.Visibility    = Visibility.Visible;
            LockScreenPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            LockScreenPreview.Visibility     = Visibility.Collapsed;
            LockScreenPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // ── Apps ──────────────────────────────────────────────────────────────────

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Installer",
            Filter = "Installer files|*.exe;*.msi|EXE files|*.exe|MSI packages|*.msi|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var path = dlg.FileName;
        bool isMsi = Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        var app  = new StagedApp
        {
            Name     = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            Type     = isMsi ? "msi" : "exe",
            // MSI: leave Args empty -- GIBFirstBoot auto-adds /qn /norestart, user only adds extras.
            // EXE: default to /S (NSIS/Inno) which works for most installers.
            Args     = isMsi ? "" : "/S"
        };

        _apps.Add(app);
        RefreshAppsPanel();
        SaveAppsToSession();
    }

    private void RefreshAppsPanel()
    {
        AppsPanel.Children.Clear();
        bool hasApps = _apps.Count > 0;
        AppsEmptyHint.Visibility  = hasApps ? Visibility.Collapsed : Visibility.Visible;
        AppsHeader.Visibility     = hasApps ? Visibility.Visible   : Visibility.Collapsed;
        AppsArgsHint.Visibility   = hasApps ? Visibility.Visible   : Visibility.Collapsed;

        for (int i = 0; i < _apps.Count; i++)
        {
            var row = BuildAppRow(_apps[i], i);
            AppsPanel.Children.Add(row);
        }
    }

    private UIElement BuildAppRow(StagedApp app, int index)
    {
        var row = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 8, 10, 8)
        };
        row.SetResourceReference(Border.BackgroundProperty,   "BG2Brush");
        row.SetResourceReference(Border.BorderBrushProperty,  "LineBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        // Name
        var nameBox = new TextBox
        {
            Text            = app.Name,
            Style           = (Style?)Application.Current.Resources["TextInputStyle"],
            Margin          = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        nameBox.TextChanged += (_, _) => { app.Name = nameBox.Text; SaveAppsToSession(); };
        Grid.SetColumn(nameBox, 0);

        // File path + browse, with optional MST transform chip below (MSI only)
        var fileStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

        var fileGrid = new Grid();
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fileBox = new TextBox
        {
            Text     = string.IsNullOrEmpty(app.FilePath) ? "" : Path.GetFileName(app.FilePath),
            ToolTip  = app.FilePath,
            Style    = (Style?)Application.Current.Resources["TextInputStyle"],
            IsReadOnly = true,
            Margin   = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var browseBtn = new Button
        {
            Content = "...",
            Style   = (Style?)Application.Current.Resources["DefaultButtonStyle"],
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 32,
            VerticalAlignment = VerticalAlignment.Center
        };

        // ── MST transform line (only meaningful for MSI installers) ───────────────
        // Layout: [chip TextBlock -- click to add/change] [✕ clear button when set]
        var mstLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 4, 0, 0)
        };
        var mstChip = new TextBlock
        {
            FontSize          = 10.5,
            Cursor            = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        var mstClearBtn = new Button
        {
            Content    = "✕",
            FontSize   = 10,
            Padding    = new Thickness(4, 0, 4, 0),
            Margin     = new Thickness(6, 0, 0, 0),
            MinWidth   = 0,
            Style      = (Style?)Application.Current.Resources["GhostButtonStyle"],
            Foreground = (Brush)Application.Current.Resources["ErrBrush"],
            ToolTip    = "Remove MST transform",
            VerticalAlignment = VerticalAlignment.Center
        };

        void RefreshMstChip()
        {
            bool isMsi = app.Type.Equals("msi", StringComparison.OrdinalIgnoreCase);
            mstLine.Visibility = isMsi ? Visibility.Visible : Visibility.Collapsed;

            if (!isMsi) return;

            if (string.IsNullOrEmpty(app.MstPath))
            {
                mstChip.Text       = "+ Add MST transform (optional)";
                mstChip.Foreground = (Brush)Application.Current.Resources["FG3Brush"];
                mstChip.ToolTip    = "Click to attach a Windows Installer transform (.mst) file. " +
                                     "msiexec will be invoked with TRANSFORMS=\"<path>\".";
                mstClearBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                mstChip.Text       = $"📎 {Path.GetFileName(app.MstPath)}";
                mstChip.Foreground = (Brush)Application.Current.Resources["Gold1Brush"];
                mstChip.ToolTip    = app.MstPath + "  (click to change)";
                mstClearBtn.Visibility = Visibility.Visible;
            }
        }

        mstChip.MouseLeftButtonUp += (_, _) =>
        {
            if (!app.Type.Equals("msi", StringComparison.OrdinalIgnoreCase)) return;
            var dlg = new OpenFileDialog
            {
                Title    = "Select MST Transform",
                Filter   = "MSI Transforms (*.mst)|*.mst|All files|*.*",
                FileName = app.MstPath ?? ""
            };
            if (dlg.ShowDialog() != true) return;
            app.MstPath = dlg.FileName;
            RefreshMstChip();
            SaveAppsToSession();
        };
        mstClearBtn.Click += (_, _) =>
        {
            app.MstPath = null;
            RefreshMstChip();
            SaveAppsToSession();
        };
        mstLine.Children.Add(mstChip);
        mstLine.Children.Add(mstClearBtn);

        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Installer",
                Filter = "Installer files|*.exe;*.msi|All files|*.*",
                FileName = app.FilePath
            };
            if (dlg.ShowDialog() != true) return;
            app.FilePath  = dlg.FileName;
            fileBox.Text  = Path.GetFileName(dlg.FileName);
            fileBox.ToolTip = dlg.FileName;
            string newType = Path.GetExtension(dlg.FileName).Equals(".msi", StringComparison.OrdinalIgnoreCase) ? "msi" : "exe";
            // Switching away from MSI clears any previously-attached MST
            if (newType != "msi") app.MstPath = null;
            app.Type = newType;
            RefreshMstChip();
            SaveAppsToSession();
        };
        Grid.SetColumn(fileBox,   0);
        Grid.SetColumn(browseBtn, 1);
        fileGrid.Children.Add(fileBox);
        fileGrid.Children.Add(browseBtn);

        fileStack.Children.Add(fileGrid);
        fileStack.Children.Add(mstLine);
        RefreshMstChip();
        Grid.SetColumn(fileStack, 1);

        // Args
        var argsBox = new TextBox
        {
            Text       = app.Args,
            Style      = (Style?)Application.Current.Resources["MonoTextInputStyle"],
            Margin     = new Thickness(0, 0, 8, 0),
            ToolTip    = "Space-separated flags -- no commas, no quotes around the whole string.\n" +
                         "Just type them exactly as you would on a command line.\n\n" +
                         "── MSI installers ──\n" +
                         "GIBFirstBoot adds /qn /norestart automatically -- leave this BLANK for a\n" +
                         "standard silent install. Only add EXTRA flags here, e.g.:\n" +
                         "  REBOOT=ReallySuppress       suppress all reboot logic\n" +
                         "  ALLUSERS=1                  install for all users\n" +
                         "  ADDLOCAL=Feature1,Feature2  feature selection\n" +
                         "  /lv* C:\\install.log         verbose log\n\n" +
                         "── EXE installers ──\n" +
                         "  /S              silent (NSIS / Inno Setup)\n" +
                         "  /silent         silent (some installers)\n" +
                         "  /VERYSILENT     Inno Setup truly silent\n" +
                         "  /norestart      suppress reboot",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        argsBox.TextChanged += (_, _) => { app.Args = argsBox.Text; SaveAppsToSession(); };
        Grid.SetColumn(argsBox, 2);

        // Type pill
        var typeCombo = new ComboBox
        {
            Style         = (Style?)Application.Current.Resources["ComboBoxStyle"],
            SelectedIndex = app.Type.Equals("msi", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        typeCombo.Items.Add("exe");
        typeCombo.Items.Add("msi");
        typeCombo.SelectionChanged += (_, _) =>
        {
            app.Type = typeCombo.SelectedIndex == 1 ? "msi" : "exe";
            // Switching to EXE clears any previously-attached MST (it's MSI-only)
            if (app.Type != "msi") app.MstPath = null;
            RefreshMstChip();
            SaveAppsToSession();
        };
        Grid.SetColumn(typeCombo, 3);

        // Remove button
        var removeBtn = new Button
        {
            Content           = "✕",
            Style             = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize          = 12,
            Foreground        = (Brush)Application.Current.Resources["ErrBrush"],
            Padding           = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var capturedApp = app;
        removeBtn.Click += (_, _) =>
        {
            _apps.Remove(capturedApp);
            RefreshAppsPanel();
            SaveAppsToSession();
        };
        Grid.SetColumn(removeBtn, 4);

        grid.Children.Add(nameBox);
        grid.Children.Add(fileStack);
        grid.Children.Add(argsBox);
        grid.Children.Add(typeCombo);
        grid.Children.Add(removeBtn);
        row.Child = grid;
        return row;
    }

    private void SaveAppsToSession()
        => BuildSession.Current.StagedApps = [.. _apps];

    // ── Staged Image Files ────────────────────────────────────────────────────

    private void AddStagedFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Select File(s) to Stage into the Image",
            Filter      = "All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var f in dlg.FileNames)
            if (!_stagedFiles.Any(sf => sf.SourcePath.Equals(f, StringComparison.OrdinalIgnoreCase)))
                _stagedFiles.Add(new StagedFile { SourcePath = f });

        RefreshPublicFilesPanel();
        BuildSession.Current.StagedFiles = [.. _stagedFiles];
    }

    private void RefreshPublicFilesPanel()
    {
        PublicFilesPanel.Children.Clear();
        bool hasFiles = _stagedFiles.Count > 0;
        PublicFilesEmptyHint.Visibility  = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        StagedFilesHeader.Visibility     = hasFiles ? Visibility.Visible   : Visibility.Collapsed;

        foreach (var sf in _stagedFiles.ToList())
            PublicFilesPanel.Children.Add(BuildStagedFileRow(sf));
    }

    private UIElement BuildStagedFileRow(StagedFile sf)
    {
        var row = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(12, 8, 10, 8)
        };
        row.SetResourceReference(Border.BackgroundProperty,  "BG2Brush");
        row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // bullet
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // source filename
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });                    // destination TextBox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // remove button

        // Bullet
        var dot = new TextBlock
        {
            Text              = "•",
            FontSize          = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0)
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "Gold1Brush");
        Grid.SetColumn(dot, 0);

        // Source filename (display only; full path shown in tooltip)
        var nameLabel = new TextBlock
        {
            Text              = Path.GetFileName(sf.SourcePath),
            ToolTip           = sf.SourcePath,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(0, 0, 10, 0)
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");
        Grid.SetColumn(nameLabel, 1);

        // Destination folder TextBox -- editable, relative path inside the image
        var destBox = new TextBox
        {
            Text                     = sf.DestinationFolder,
            Style                    = (Style?)Application.Current.Resources["MonoTextInputStyle"],
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 8, 0),
            ToolTip                  = "Folder path INSIDE the image (relative, no leading backslash).\n" +
                                       "Examples:\n" +
                                       "  Users\\Public\\Desktop\n" +
                                       "  Windows\\System32\n" +
                                       "  ProgramData\\MyCompany\\Config"
        };
        destBox.TextChanged += (_, _) =>
        {
            sf.DestinationFolder = destBox.Text.Trim().TrimStart('\\', '/');
            BuildSession.Current.StagedFiles = [.. _stagedFiles];
        };
        Grid.SetColumn(destBox, 2);

        // Remove button
        var removeBtn = new Button
        {
            Content           = "✕",
            Style             = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize          = 11,
            Foreground        = (Brush)Application.Current.Resources["ErrBrush"],
            Padding           = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var captured = sf;
        removeBtn.Click += (_, _) =>
        {
            _stagedFiles.Remove(captured);
            RefreshPublicFilesPanel();
            BuildSession.Current.StagedFiles = [.. _stagedFiles];
        };
        Grid.SetColumn(removeBtn, 3);

        grid.Children.Add(dot);
        grid.Children.Add(nameLabel);
        grid.Children.Add(destBox);
        grid.Children.Add(removeBtn);
        row.Child = grid;
        return row;
    }

    // ── Language Pack Injection ───────────────────────────────────────────────

    private void AddLanguagePack_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Language Pack Folder (e.g. LanguagesAndOptionalFeatures\\)"
        };
        if (dlg.ShowDialog() != true) return;
        ScanLanguagePackFolder(dlg.FolderName);
    }

    // ── Whitelist patterns -- only real LP files match, all FoD noise is ignored ──
    //
    // Pattern A -- Core LP, underscore format (this ISO):
    //   Microsoft-Windows-Client-Language-Pack_x64_fr-fr.cab
    //
    // Pattern B -- Core LP, tilde format (other ISOs / older builds):
    //   Microsoft-Windows-Client-LanguagePack-Package~31bf...~amd64~fr-FR~10.0.x.cab
    //
    // Pattern C -- Language Features (Basic / Handwriting / OCR / Speech / TTS):
    //   Microsoft-Windows-LanguageFeatures-Basic-fr-fr-Package~31bf...~amd64~~.cab
    //
    private static readonly Regex[] _lpPatterns =
    [
        // A: underscore Core LP   -- locale is last segment before .cab
        new Regex(@"^Microsoft-Windows-Client-Language-Pack_[^_]+_([a-z]{2}[_\-][a-z]{2,}(?:[_\-][a-z]{2,})?)\.cab$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // B: tilde Core LP   -- locale is 4th tilde-segment
        new Regex(@"^Microsoft-Windows-Client-LanguagePack-Package~[^~]+~[^~]+~([a-z]{2}[_\-][a-z]{2,}(?:[_\-][a-z]{2,})?)~",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // C: LanguageFeatures (Basic|Handwriting|OCR|Speech|TextToSpeech)
        new Regex(@"^Microsoft-Windows-LanguageFeatures-(?:Basic|Handwriting|OCR|Speech|TextToSpeech)-([a-z]{2}[_\-][a-z]{2,}(?:[_\-][a-z]{2,})?)-Package~",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private void ScanLanguagePackFolder(string folder)
    {
        _langGroupsScanned.Clear();
        LangGroupsPanel.Children.Clear();

        string[] cabs;
        try
        {
            cabs = Directory.GetFiles(folder, "*.cab", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Could not read folder:\n{ex.Message}", "Folder Error",
                AppDialogIcon.Warning);
            return;
        }

        foreach (var cab in cabs)
        {
            var filename = Path.GetFileName(cab);
            string? code = null;

            // Try each whitelist pattern in order -- first match wins
            foreach (var pattern in _lpPatterns)
            {
                var m = pattern.Match(filename);
                if (!m.Success) continue;
                // Normalise: uppercase separators → lowercase hyphens (fr_FR → fr-fr)
                code = m.Groups[1].Value.Replace('_', '-').ToLowerInvariant();
                break;
            }

            if (code is null) continue;  // not a real LP file -- skip

            if (!_langGroupsScanned.TryGetValue(code, out var list))
            {
                list = [];
                _langGroupsScanned[code] = list;
            }
            list.Add(cab);
        }

        if (_langGroupsScanned.Count == 0)
        {
            AppDialog.Alert(this,
                "No language pack CAB files were detected in that folder.\n\n" +
                "Make sure you selected the LanguagesAndOptionalFeatures\\ folder " +
                "from the mounted LP ISO.",
                "No Language Packs Found", AppDialogIcon.Info);
            return;
        }

        // Reset the search filter for the new scan result
        LangSearchBox.Text = "";
        LangSearchPlaceholder.Visibility = Visibility.Visible;

        // Build one row per language, sorted alphabetically by display name
        foreach (var kvp in _langGroupsScanned
            .OrderBy(k => LangNames.TryGetValue(k.Key, out var n) ? n : k.Key))
        {
            LangGroupsPanel.Children.Add(BuildLangGroupRow(kvp.Key, kvp.Value));
        }

        var count = _langGroupsScanned.Count;
        LangScanSourceLabel.Text =
            $"Found {count} language{(count == 1 ? "" : "s")} in:  " +
            Path.GetFileName(folder.TrimEnd('\\', '/'));
        LangScanResultPanel.Visibility = Visibility.Visible;
    }

    private UIElement BuildLangGroupRow(string langCode, List<string> cabs)
    {
        // Row outer border -- doubles as the clickable hit-test area
        var outerBorder = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 8, 10, 8),
            Cursor          = Cursors.Hand,
            // Tag stores (langCode, cabs, isChecked)
            Tag = (langCode, cabs, true)
        };

        // Local helper -- updates all visuals atomically.
        // Unchecked state uses SetResourceReference so colours follow theme switches.
        // Checked state uses a semi-transparent accent tint (works in both themes).
        void ApplyVisual(bool chk)
        {
            if (chk)
            {
                var g1 = (Color)Application.Current.Resources["Gold1Color"];
                outerBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, g1.R, g1.G, g1.B));
                outerBorder.SetResourceReference(Border.BorderBrushProperty, "Gold1Brush");
            }
            else
            {
                outerBorder.SetResourceReference(Border.BackgroundProperty,  "BG2Brush");
                outerBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            }

            if (outerBorder.Child is Grid g && g.Children[0] is Border chkBorder)
            {
                if (chk)
                    chkBorder.SetResourceReference(Border.BackgroundProperty, "Gold1Brush");
                else
                    chkBorder.SetResourceReference(Border.BackgroundProperty, "BG3Brush");

                if (chkBorder.Child is System.Windows.Shapes.Path p)
                    p.Opacity = chk ? 1.0 : 0.0;
            }

            outerBorder.Tag = (langCode, cabs, chk);
        }

        // ── Layout: [checkbox]  [text] ──────────────────────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Rounded checkbox
        var chkMark = new System.Windows.Shapes.Path
        {
            Data              = Geometry.Parse("M 3,10 L 8,15 L 17,4"),
            Stroke            = Brushes.White,
            StrokeThickness   = 1.8,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap   = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin     = System.Windows.Media.PenLineJoin.Round,
            Stretch             = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Opacity             = 1.0
        };
        var chkBox = new Border
        {
            Width             = 20,
            Height            = 20,
            CornerRadius      = new CornerRadius(4),
            BorderThickness   = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 12, 0),
            Child             = chkMark
        };
        Grid.SetColumn(chkBox, 0);

        // Language name (line 1) + locale code + component summary (line 2)
        var displayName = LangNames.TryGetValue(langCode, out var humanName)
            ? humanName
            : langCode.ToUpperInvariant();

        var nameText = new TextBlock
        {
            Text       = displayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");

        var detailText = new TextBlock
        {
            Text       = $"{langCode.ToLowerInvariant()}  ·  " +
                         $"{cabs.Count} file{(cabs.Count == 1 ? "" : "s")}  ·  " +
                         SummariseComponents(cabs),
            FontSize   = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin     = new Thickness(0, 2, 0, 0)
        };
        detailText.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(nameText);
        textStack.Children.Add(detailText);
        Grid.SetColumn(textStack, 1);

        grid.Children.Add(chkBox);
        grid.Children.Add(textStack);
        outerBorder.Child = grid;

        // Apply initial (checked) visual
        ApplyVisual(true);

        // Toggle on click anywhere in the row
        outerBorder.MouseLeftButtonUp += (_, _) =>
        {
            var (_, _, currentlyChecked) = ((string, List<string>, bool))outerBorder.Tag;
            ApplyVisual(!currentlyChecked);
        };

        return outerBorder;
    }

    private static string SummariseComponents(List<string> cabs)
    {
        var parts = new List<string>();

        bool has(string kw) => cabs.Any(c =>
            Path.GetFileName(c).Contains(kw, StringComparison.OrdinalIgnoreCase));

        // Core LP -- matches both underscore format (Language-Pack_x64_) and tilde format (LanguagePack-Package~)
        if (cabs.Any(c =>
        {
            var n = Path.GetFileName(c);
            return n.StartsWith("Microsoft-Windows-Client-Language", StringComparison.OrdinalIgnoreCase);
        })) parts.Add("Core LP");

        if (has("LanguageFeatures-Basic"))        parts.Add("Basic");
        if (has("LanguageFeatures-Handwriting"))  parts.Add("Handwriting");
        if (has("LanguageFeatures-OCR"))          parts.Add("OCR");
        if (has("LanguageFeatures-Speech"))       parts.Add("Speech");
        if (has("LanguageFeatures-TextToSpeech")) parts.Add("TTS");

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : $"{cabs.Count} CAB files";
    }

    // ── Language search filter ────────────────────────────────────────────────

    private void LangSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = LangSearchBox.Text.Trim();

        // Show / hide placeholder
        LangSearchPlaceholder.Visibility =
            string.IsNullOrEmpty(LangSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;

        // If no query show everything; otherwise filter by display name or code
        foreach (var child in LangGroupsPanel.Children.OfType<Border>())
        {
            if (string.IsNullOrEmpty(query))
            {
                child.Visibility = Visibility.Visible;
                continue;
            }

            // The tag carries (langCode, cabs, isChecked)
            if (child.Tag is not ValueTuple<string, List<string>, bool> t)
                continue;

            var (code, _, _) = t;
            var displayName  = LangNames.TryGetValue(code, out var n) ? n : code;

            child.Visibility =
                displayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                code.Contains(query,        StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Scan result toolbar ───────────────────────────────────────────────────

    private void LangSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in LangGroupsPanel.Children.OfType<Border>())
            SetLangRowChecked(child, true);
    }

    private void LangSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in LangGroupsPanel.Children.OfType<Border>())
            SetLangRowChecked(child, false);
    }

    private static void SetLangRowChecked(Border row, bool chk)
    {
        if (row.Tag is not ValueTuple<string, List<string>, bool> t) return;
        var (code, cabs, _) = t;

        // Mirror the same theme-aware logic used in ApplyVisual inside BuildLangGroupRow
        if (chk)
        {
            var g1 = (Color)Application.Current.Resources["Gold1Color"];
            row.Background = new SolidColorBrush(Color.FromArgb(0x18, g1.R, g1.G, g1.B));
            row.SetResourceReference(Border.BorderBrushProperty, "Gold1Brush");
        }
        else
        {
            row.SetResourceReference(Border.BackgroundProperty,  "BG2Brush");
            row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        }

        if (row.Child is Grid g && g.Children[0] is Border chkBorder)
        {
            if (chk)
                chkBorder.SetResourceReference(Border.BackgroundProperty, "Gold1Brush");
            else
                chkBorder.SetResourceReference(Border.BackgroundProperty, "BG3Brush");

            if (chkBorder.Child is System.Windows.Shapes.Path p)
                p.Opacity = chk ? 1.0 : 0.0;
        }

        row.Tag = (code, cabs, chk);
    }

    private void LangAddSelected_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;

        foreach (var child in LangGroupsPanel.Children.OfType<Border>())
        {
            if (child.Tag is not ValueTuple<string, List<string>, bool> t) continue;
            var (_, cabs, chk) = t;
            if (!chk) continue;

            foreach (var cab in OrderLangPackCabs(cabs))
            {
                if (!_languagePacks.Contains(cab, StringComparer.OrdinalIgnoreCase))
                {
                    _languagePacks.Add(cab);
                    added++;
                }
            }
        }

        if (added == 0)
        {
            AppDialog.Alert(this,
                "No languages are currently selected.\n" +
                "Tick at least one language then click Add Selected.",
                "Nothing Selected", AppDialogIcon.Info);
            return;
        }

        // Collapse scan panel and clear transient state
        LangScanResultPanel.Visibility = Visibility.Collapsed;
        LangGroupsPanel.Children.Clear();
        _langGroupsScanned.Clear();

        RefreshLangPacksPanel();
        BuildSession.Current.LanguagePackPaths = [.. _languagePacks];
    }

    /// <summary>
    /// Returns CAB files sorted in DISM install order:
    ///   Core LP (0) → Basic (1) → Handwriting (2) → OCR (3) → Speech (4) → TextToSpeech (5)
    /// </summary>
    private static List<string> OrderLangPackCabs(List<string> cabs)
    {
        static int Priority(string path)
        {
            var n = Path.GetFileName(path).ToLowerInvariant();
            if (n.Contains("texttospeech"))  return 5;
            if (n.Contains("speech"))        return 4;
            if (n.Contains("ocr"))           return 3;
            if (n.Contains("handwriting"))   return 2;
            if (n.Contains("basic"))         return 1;
            return 0;  // Core LP
        }
        return [.. cabs.OrderBy(Priority)];
    }

    private void RefreshLangPacksPanel()
    {
        LangPacksPanel.Children.Clear();
        LangPacksEmptyHint.Visibility = _languagePacks.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var path in _languagePacks.ToList())
            LangPacksPanel.Children.Add(BuildSimpleFileRow(path,
                () =>
                {
                    _languagePacks.Remove(path);
                    RefreshLangPacksPanel();
                    BuildSession.Current.LanguagePackPaths = [.. _languagePacks];
                }));
    }

    // ── Info popup hover handlers ─────────────────────────────────────────────

    private void LangInfoTrigger_MouseEnter(object sender, MouseEventArgs e)
    {
        _langPopupTimer?.Stop();
        LangInfoPopup.IsOpen = true;
    }

    private void LangInfoTrigger_MouseLeave(object sender, MouseEventArgs e)
        => StartClosePopupTimer();

    private void LangInfoPopup_MouseEnter(object sender, MouseEventArgs e)
        => _langPopupTimer?.Stop();

    private void LangInfoPopup_MouseLeave(object sender, MouseEventArgs e)
        => StartClosePopupTimer();

    private void StartClosePopupTimer()
    {
        if (_langPopupTimer is null)
        {
            _langPopupTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _langPopupTimer.Tick += (_, _) =>
            {
                _langPopupTimer.Stop();
                LangInfoPopup.IsOpen = false;
            };
        }
        _langPopupTimer.Stop();
        _langPopupTimer.Start();
    }

    private void LangPackLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* ignore - browser may not be available */ }
        e.Handled = true;
    }

    // ── Driver Injection ──────────────────────────────────────────────────────

    private void AddDriverFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Driver Folder" };
        if (dlg.ShowDialog() != true) return;

        if (!_driverFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            _driverFolders.Add(dlg.FolderName);

        RefreshDriversPanel();
        BuildSession.Current.DriverFolderPaths = [.. _driverFolders];
    }

    // ── Custom fonts ──────────────────────────────────────────────────────────

    private void AddFont_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Select font file(s)",
            Filter      = "Font files|*.ttf;*.otf;*.ttc|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var path in dlg.FileNames)
        {
            // Skip duplicates by source path.
            if (_fonts.Any(f => string.Equals(f.SourcePath, path,
                                              StringComparison.OrdinalIgnoreCase)))
                continue;
            _fonts.Add(new FontEntry
            {
                SourcePath  = path,
                DisplayName = Path.GetFileNameWithoutExtension(path),
            });
        }
        RefreshFontsPanel();
        BuildSession.Current.Fonts = [.. _fonts];
    }

    private void RefreshFontsPanel()
    {
        FontsPanel.Children.Clear();
        FontsEmptyHint.Visibility = _fonts.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _fonts.Count; i++)
            FontsPanel.Children.Add(BuildFontRow(_fonts[i], i));
    }

    private UIElement BuildFontRow(FontEntry font, int index)
    {
        var row = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(12, 8, 10, 8)
        };
        row.SetResourceReference(Border.BackgroundProperty,  "BG2Brush");
        row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Filename + path (read-only)
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLabel = new TextBlock
        {
            Text         = Path.GetFileName(font.SourcePath),
            ToolTip      = font.SourcePath,
            FontSize     = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");
        var subLabel = new TextBlock
        {
            Text         = font.SourcePath,
            FontSize     = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        subLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");
        info.Children.Add(nameLabel);
        info.Children.Add(subLabel);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Display name -- editable, auto-filled from filename. The user can
        // override if the font's published display name differs from its
        // filename (common in pro fonts shipped as "FONT_REG.ttf" etc.).
        var displayBox = new TextBox
        {
            Text              = font.DisplayName,
            Margin            = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Friendly name shown in apps' font picker",
            Style             = (Style)Application.Current.Resources["TextInputStyle"],
        };
        int capturedDisplayIdx = index;
        displayBox.TextChanged += (_, _) =>
        {
            if (capturedDisplayIdx >= _fonts.Count) return;
            _fonts[capturedDisplayIdx].DisplayName = displayBox.Text.Trim();
            BuildSession.Current.Fonts = [.. _fonts];
        };
        Grid.SetColumn(displayBox, 1);
        grid.Children.Add(displayBox);

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
            if (capturedIdx < _fonts.Count)
                _fonts.RemoveAt(capturedIdx);
            RefreshFontsPanel();
            BuildSession.Current.Fonts = [.. _fonts];
        };
        Grid.SetColumn(removeBtn, 2);
        grid.Children.Add(removeBtn);

        row.Child = grid;
        return row;
    }

    private void RefreshDriversPanel()
    {
        DriversPanel.Children.Clear();
        DriversEmptyHint.Visibility = _driverFolders.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var path in _driverFolders.ToList())
            DriversPanel.Children.Add(BuildSimpleFileRow(path,
                () => { _driverFolders.Remove(path); RefreshDriversPanel(); BuildSession.Current.DriverFolderPaths = [.. _driverFolders]; }));
    }

    /// <summary>Shared row builder for LP and Driver lists (path + remove button).</summary>
    private UIElement BuildSimpleFileRow(string path, Action onRemove)
    {
        var row = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(12, 8, 10, 8)
        };
        row.SetResourceReference(Border.BackgroundProperty,  "BG2Brush");
        row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new TextBlock
        {
            Text              = "•",
            FontSize          = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0)
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "Gold1Brush");
        Grid.SetColumn(dot, 0);

        var nameLabel = new TextBlock
        {
            Text              = Path.GetFileName(path.TrimEnd('\\', '/')),
            ToolTip           = path,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");

        var subLabel = new TextBlock
        {
            Text              = path,
            FontSize          = 10,
            TextTrimming      = TextTrimming.CharacterEllipsis
        };
        subLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");
        var nameStack = new StackPanel();
        nameStack.Children.Add(nameLabel);
        nameStack.Children.Add(subLabel);
        Grid.SetColumn(nameStack, 1);

        var removeBtn = new Button
        {
            Content             = "✕",
            Style               = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize            = 11,
            Foreground          = (Brush)Application.Current.Resources["ErrBrush"],
            Padding             = new Thickness(4),
            VerticalAlignment   = VerticalAlignment.Center
        };
        removeBtn.Click += (_, _) => onRemove();
        Grid.SetColumn(removeBtn, 2);

        grid.Children.Add(dot);
        grid.Children.Add(nameStack);
        grid.Children.Add(removeBtn);
        row.Child = grid;
        return row;
    }

    // ── Deployment Scripts ────────────────────────────────────────────────────

    private void DeploymentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        BuildSession.Current.IncludeDeploymentScripts = IncludeDeploymentToggle.IsChecked == true;
        RefreshDeploymentScriptsPanel();
    }

    private void AddDeploymentScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Select Script(s) to Deploy",
            Filter      = "Scripts|*.ps1;*.bat;*.cmd;*.vbs;*.py|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var f in dlg.FileNames)
            if (!_deploymentScripts.Any(s => s.Path.Equals(f, StringComparison.OrdinalIgnoreCase)))
                _deploymentScripts.Add(new DeploymentScript { Path = f });

        RefreshDeploymentScriptsPanel();
        BuildSession.Current.DeploymentScripts = [.. _deploymentScripts];
    }

    private void RefreshDeploymentScriptsPanel()
    {
        bool enabled = IncludeDeploymentToggle.IsChecked == true;

        DeploymentScriptsPanel.Children.Clear();

        // All content below the header collapses when the section is disabled
        var contentVis = enabled ? Visibility.Visible : Visibility.Collapsed;
        DeploymentScriptsPanel.Visibility    = contentVis;
        DeploymentScriptsInfoNote.Visibility = contentVis;

        // Empty hint only shows when enabled AND list is empty
        DeploymentScriptsEmptyHint.Visibility =
            enabled && _deploymentScripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled) return;

        foreach (var script in _deploymentScripts.ToList())
            DeploymentScriptsPanel.Children.Add(BuildDeploymentScriptRow(script));
    }

    private Border BuildDeploymentScriptRow(DeploymentScript script)
    {
        var row = new Border
        {
            Background      = (Brush?)Application.Current.Resources["BG3Brush"],
            CornerRadius    = new CornerRadius(6),
            BorderBrush     = (Brush?)Application.Current.Resources["LineBrush"],
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 6, 10, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });            // bullet
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // filename
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });           // trigger
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // remove

        // ── Bullet ────────────────────────────────────────────────────────────
        var dot = new TextBlock
        {
            Text              = "•",
            FontSize          = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0)
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "Gold1Brush");
        Grid.SetColumn(dot, 0);

        // ── Filename ──────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text              = System.IO.Path.GetFileName(script.Path),
            ToolTip           = script.Path,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(0, 0, 10, 0)
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");
        Grid.SetColumn(nameLabel, 1);

        // ── Trigger ComboBox ──────────────────────────────────────────────────
        var combo = new ComboBox
        {
            Style             = (Style?)Application.Current.Resources["ComboBoxStyle"],
            Width             = 130,
            Height            = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0)
        };

        foreach (var (val, label) in DeploymentTrigger.All)
        {
            var item = new ComboBoxItem { Content = label, Tag = val };
            combo.Items.Add(item);
            if (val == script.Trigger) combo.SelectedItem = item;
        }
        // Default to first item if nothing matched (e.g. old profile with unknown trigger)
        if (combo.SelectedItem == null && combo.Items.Count > 0)
            combo.SelectedIndex = 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem selected)
            {
                script.Trigger = (string)selected.Tag!;
                BuildSession.Current.DeploymentScripts = [.. _deploymentScripts];
            }
        };
        Grid.SetColumn(combo, 2);

        // ── Remove button ─────────────────────────────────────────────────────
        var removeBtn = new Button
        {
            Content           = "✕",
            Style             = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize          = 11,
            Foreground        = (Brush)Application.Current.Resources["ErrBrush"],
            Padding           = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var captured = script;
        removeBtn.Click += (_, _) =>
        {
            _deploymentScripts.Remove(captured);
            RefreshDeploymentScriptsPanel();
            BuildSession.Current.DeploymentScripts = [.. _deploymentScripts];
        };
        Grid.SetColumn(removeBtn, 3);

        grid.Children.Add(dot);
        grid.Children.Add(nameLabel);
        grid.Children.Add(combo);
        grid.Children.Add(removeBtn);
        row.Child = grid;
        return row;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("wizard", 0);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // Persist final state
        var s = BuildSession.Current;
        s.StagedApps              = [.. _apps];
        s.StagedFiles             = [.. _stagedFiles];
        s.LanguagePackPaths       = [.. _languagePacks];
        s.DriverFolderPaths       = [.. _driverFolders];
        s.Fonts                   = [.. _fonts];
        s.DeploymentScripts        = [.. _deploymentScripts];
        s.IncludeDeploymentScripts = IncludeDeploymentToggle.IsChecked == true;

        NavigateRequested?.Invoke("wizard", 2);
    }
}
