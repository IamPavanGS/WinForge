using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class Step1Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    // Required space for a build (~22 GB)
    private const long RequiredBytes = 22L * 1024 * 1024 * 1024;

    // Track which drive the output path is on so we highlight it
    private string _outputDriveLetter = "";

    // ── Language picker state (ISO boot language — must match boot.wim) ────────
    private string _selectedLangCode   = "en-GB";
    private bool   _langAutoDetected   = false;   // true when detected from ISO, false when manually chosen

    // ── Target OS locale (oobeSystem — the locale of the installed Windows) ───
    private string _targetOsLocale = "en-US";

    private static readonly LangItem[] AllTargetLocales =
    [
        new("en-US",      "English (United States)"),
        new("en-GB",      "English (United Kingdom)"),
        new("fr-FR",      "French (France)"),
        new("fr-CA",      "French (Canada)"),
        new("de-DE",      "German (Germany)"),
        new("es-ES",      "Spanish (Spain)"),
        new("es-MX",      "Spanish (Mexico)"),
        new("it-IT",      "Italian (Italy)"),
        new("nl-NL",      "Dutch (Netherlands)"),
        new("pt-PT",      "Portuguese (Portugal)"),
        new("pt-BR",      "Portuguese (Brazil)"),
        new("pl-PL",      "Polish (Poland)"),
        new("cs-CZ",      "Czech (Czech Republic)"),
        new("sk-SK",      "Slovak (Slovakia)"),
        new("hu-HU",      "Hungarian (Hungary)"),
        new("ro-RO",      "Romanian (Romania)"),
        new("bg-BG",      "Bulgarian (Bulgaria)"),
        new("hr-HR",      "Croatian (Croatia)"),
        new("sl-SI",      "Slovenian (Slovenia)"),
        new("sr-Latn-RS", "Serbian (Latin, Serbia)"),
        new("sv-SE",      "Swedish (Sweden)"),
        new("nb-NO",      "Norwegian Bokmål (Norway)"),
        new("da-DK",      "Danish (Denmark)"),
        new("fi-FI",      "Finnish (Finland)"),
        new("et-EE",      "Estonian (Estonia)"),
        new("lv-LV",      "Latvian (Latvia)"),
        new("lt-LT",      "Lithuanian (Lithuania)"),
        new("el-GR",      "Greek (Greece)"),
        new("tr-TR",      "Turkish (Turkey)"),
        new("ru-RU",      "Russian (Russia)"),
        new("uk-UA",      "Ukrainian (Ukraine)"),
        new("he-IL",      "Hebrew (Israel)"),
        new("ar-SA",      "Arabic (Saudi Arabia)"),
        new("th-TH",      "Thai (Thailand)"),
        new("vi-VN",      "Vietnamese (Vietnam)"),
        new("ja-JP",      "Japanese (Japan)"),
        new("ko-KR",      "Korean (Korea)"),
        new("zh-CN",      "Chinese Simplified (China)"),
        new("zh-TW",      "Chinese Traditional (Taiwan)"),
    ];

    private record LangItem(string Code, string DisplayName)
    {
        public override string ToString() => $"{Code}  {DisplayName}";
    }

    private static readonly LangItem[] AllLanguages =
    [
        new("ar-SA",      "Arabic (Saudi Arabia)"),
        new("bg-BG",      "Bulgarian (Bulgaria)"),
        new("cs-CZ",      "Czech (Czech Republic)"),
        new("da-DK",      "Danish (Denmark)"),
        new("de-DE",      "German (Germany)"),
        new("el-GR",      "Greek (Greece)"),
        new("en-GB",      "English (United Kingdom)"),
        new("en-US",      "English (United States)"),
        new("es-ES",      "Spanish (Spain)"),
        new("es-MX",      "Spanish (Mexico)"),
        new("et-EE",      "Estonian (Estonia)"),
        new("fi-FI",      "Finnish (Finland)"),
        new("fr-CA",      "French (Canada)"),
        new("fr-FR",      "French (France)"),
        new("he-IL",      "Hebrew (Israel)"),
        new("hr-HR",      "Croatian (Croatia)"),
        new("hu-HU",      "Hungarian (Hungary)"),
        new("it-IT",      "Italian (Italy)"),
        new("ja-JP",      "Japanese (Japan)"),
        new("ko-KR",      "Korean (Korea)"),
        new("lt-LT",      "Lithuanian (Lithuania)"),
        new("lv-LV",      "Latvian (Latvia)"),
        new("nb-NO",      "Norwegian Bokmål (Norway)"),
        new("nl-NL",      "Dutch (Netherlands)"),
        new("pl-PL",      "Polish (Poland)"),
        new("pt-BR",      "Portuguese (Brazil)"),
        new("pt-PT",      "Portuguese (Portugal)"),
        new("ro-RO",      "Romanian (Romania)"),
        new("ru-RU",      "Russian (Russia)"),
        new("sk-SK",      "Slovak (Slovakia)"),
        new("sl-SI",      "Slovenian (Slovenia)"),
        new("sr-Latn-RS", "Serbian (Latin, Serbia)"),
        new("sv-SE",      "Swedish (Sweden)"),
        new("th-TH",      "Thai (Thailand)"),
        new("tr-TR",      "Turkish (Turkey)"),
        new("uk-UA",      "Ukrainian (Ukraine)"),
        new("vi-VN",      "Vietnamese (Vietnam)"),
        new("zh-CN",      "Chinese Simplified (China)"),
        new("zh-TW",      "Chinese Traditional (Taiwan)"),
    ];

    // Timer to periodically refresh drive list (picks up USB/external drives)
    private System.Threading.Timer? _driveTimer;

    public Step1Page()
    {
        InitializeComponent();
        Loaded           += OnLoaded;
        Unloaded         += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Always read from settings file first — it's the source of truth.
        // This handles the case where AppSettingsLoader.Apply() ran before the
        // file had the correct paths (e.g. fresh install or migration from old location).
        var (savedOut, savedWs) = GoldenISOBuilder.Helpers.AppSettingsLoader.ReadPaths();
        if (!string.IsNullOrEmpty(savedOut)) BuildSession.Current.OutputPath    = savedOut;
        if (!string.IsNullOrEmpty(savedWs))  BuildSession.Current.WorkspacePath = savedWs;

        // Last-resort fallback: Windows system drive (C:\) — never picks D:\ or external drives.
        string defaultRoot = PickDefaultRoot();
        OutputPathBox.Text    = BuildSession.Current.OutputPath    is { Length: > 0 } op ? op : System.IO.Path.Combine(defaultRoot, "ISO_Build", "Output");
        WorkspacePathBox.Text = BuildSession.Current.WorkspacePath is { Length: > 0 } ws ? ws : System.IO.Path.Combine(defaultRoot, "ISO_Build", "Workspace");
        OutputFilenameBox.Text= BuildSession.Current.OutputFilename;

        UpdateOutputDriveLetter();
        RefreshDriveSpace();

        // Refresh every 5 s so external drives appear/disappear automatically
        _driveTimer = new System.Threading.Timer(_ =>
            Dispatcher.Invoke(RefreshDriveSpace), null, 5_000, 5_000);

        // If an ISO was already analysed in this session, restore UI
        if (BuildSession.Current.AvailableImages.Count > 0 &&
            BuildSession.Current.SourceIsoPath is not null)
        {
            RestoreIsoUi();
        }

        // Initialise ISO boot language picker — restore saved selection (default en-GB)
        _selectedLangCode = BuildSession.Current.IsoSourceLanguage is { Length: > 0 } lc ? lc : "en-GB";
        PopulateLanguageList("");

        // Initialise target OS locale ComboBox — restore saved selection (default en-US)
        _targetOsLocale = BuildSession.Current.TargetOsLocale is { Length: > 0 } tl ? tl : "en-US";
        PopulateTargetLocaleCombo();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _driveTimer?.Dispose();
        _driveTimer = null;
    }

    // Re-sync path boxes when user navigates back to this page.
    // OnLoaded only fires once; the settings file is the source of truth.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !IsLoaded) return;

        // Always re-read from settings file — BuildSession may contain a stale
        // fallback value (e.g. C:\ISO_Build\Output) if settings hadn't been saved
        // when OnLoaded first ran.  SavePaths() is called on every Browse + Continue,
        // so the file always reflects the user's latest explicit choice.
        var (savedOut, savedWs) = GoldenISOBuilder.Helpers.AppSettingsLoader.ReadPaths();

        if (!string.IsNullOrEmpty(savedOut))
        {
            BuildSession.Current.OutputPath = savedOut;
            OutputPathBox.Text = savedOut;
        }
        else if (!string.IsNullOrEmpty(BuildSession.Current.OutputPath))
        {
            OutputPathBox.Text = BuildSession.Current.OutputPath;
        }

        if (!string.IsNullOrEmpty(savedWs))
        {
            BuildSession.Current.WorkspacePath = savedWs;
            WorkspacePathBox.Text = savedWs;
        }
        else if (!string.IsNullOrEmpty(BuildSession.Current.WorkspacePath))
        {
            WorkspacePathBox.Text = BuildSession.Current.WorkspacePath;
        }

        UpdateOutputDriveLetter();
        RefreshDriveSpace();
    }

    // ── ISO selection ────────────────────────────────────────────────────────

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => OpenIsoDialog();
    private void ChangeISO_Click(object sender, RoutedEventArgs e)     => OpenIsoDialog();

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var iso   = files.FirstOrDefault(f =>
            f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
        if (iso is not null) _ = LoadIsoAsync(iso);
    }

    private void OpenIsoDialog()
    {
        // Remember last ISO folder so the dialog reopens in the right place
        string initDir = BuildSession.Current.SourceIsoPath is { } last && File.Exists(last)
            ? Path.GetDirectoryName(last) ?? ""
            : "";

        var dlg = new OpenFileDialog
        {
            Title            = "Select Windows ISO",
            Filter           = "ISO Images (*.iso)|*.iso",
            CheckFileExists  = true,
            InitialDirectory = initDir,
        };
        // Tip: user can type a full path (e.g. D:\ISOs\file.iso) in the filename box
        // even when the folder list doesn't show the right drive.
        if (dlg.ShowDialog() == true)
            _ = LoadIsoAsync(dlg.FileName);
    }

    // ── Direct-path input ────────────────────────────────────────────────────

    private void IsoPathBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            TryLoadFromPathBox();
    }

    private void LoadPathBox_Click(object sender, RoutedEventArgs e)
        => TryLoadFromPathBox();

    private void TryLoadFromPathBox()
    {
        var path = IsoPathBox.Text.Trim().Trim('"');   // strip accidental quotes
        if (string.IsNullOrEmpty(path)) return;

        if (!File.Exists(path))
        {
            AppDialog.Alert(this,
                $"File not found:\n{path}\n\nCheck the path and try again.",
                "File Not Found", AppDialogIcon.Warning);
            return;
        }
        if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.Alert(this,
                "The selected file doesn't have an .iso extension. Please select a valid Windows ISO.",
                "Invalid File", AppDialogIcon.Warning);
            return;
        }
        _ = LoadIsoAsync(path);
    }

    private async Task LoadIsoAsync(string isoPath)
    {
        // Show scanning state — hide entire picker panel (drop zone + path box)
        IsoPickerPanel.Visibility   = Visibility.Collapsed;
        SelectedISOPanel.Visibility = Visibility.Collapsed;
        ScanningPanel.Visibility    = Visibility.Visible;
        SetEditionsLocked(true);
        SetArchsLocked(true);

        var analyzer = new IsoAnalyzer();
        var result   = await analyzer.AnalyzeAsync(isoPath,
            msg => Dispatcher.Invoke(() => ScanningStatusText.Text = msg));

        ScanningPanel.Visibility = Visibility.Collapsed;

        if (!result.Success)
        {
            IsoPickerPanel.Visibility = Visibility.Visible;
            AppDialog.Alert(this,
                $"Could not analyse ISO:\n\n{result.Error}",
                "ISO Analysis Failed", AppDialogIcon.Warning);
            return;
        }

        // Persist to session
        BuildSession.Current.SourceIsoPath   = isoPath;
        BuildSession.Current.MountedIsoDrive = result.MountedDrive;
        BuildSession.Current.IsoOsVersion    = result.OsVersion;
        BuildSession.Current.AvailableImages = result.Images;

        // Update ISO info strip
        var fi = new FileInfo(isoPath);
        ISOFileName.Text = fi.Name;
        ISOFileSize.Text = $"{fi.Length / (1024.0 * 1024.0 * 1024.0):F1} GB  ·  {result.WindowsVersion}  ·  {result.Images.Count} edition(s) found";
        SelectedISOPanel.Visibility = Visibility.Visible;

        // Update edition and arch controls
        ApplyEditionAvailability(result.Images);
        ApplyArchAvailability(result.Architectures);

        // Store all available boot languages from lang.ini and apply the primary one.
        BuildSession.Current.IsoAvailableLanguages = result.AvailableBootLanguages;
        if (!string.IsNullOrEmpty(result.BootLanguage))
        {
            _selectedLangCode = result.BootLanguage;
            _langAutoDetected = true;
            BuildSession.Current.IsoSourceLanguage = result.BootLanguage;
            PopulateLanguageList("");
        }
    }

    private void RestoreIsoUi()
    {
        var fi = new FileInfo(BuildSession.Current.SourceIsoPath!);
        ISOFileName.Text = fi.Name;
        ISOFileSize.Text = $"{fi.Length / (1024.0 * 1024.0 * 1024.0):F1} GB  ·  {BuildSession.Current.AvailableImages.Count} edition(s)";
        IsoPickerPanel.Visibility   = Visibility.Collapsed;
        SelectedISOPanel.Visibility = Visibility.Visible;
        ApplyEditionAvailability(BuildSession.Current.AvailableImages);

        // Boot language was detected and saved to the session during the original ISO
        // analysis (IsoAnalyzer reads lang.ini while the drive is guaranteed mounted).
        // On restore, just apply the saved value — no mounting needed.
        if (!string.IsNullOrEmpty(BuildSession.Current.IsoSourceLanguage))
        {
            _selectedLangCode = BuildSession.Current.IsoSourceLanguage;
            _langAutoDetected = true;
            PopulateLanguageList("");
        }
    }

    // ── Edition controls ─────────────────────────────────────────────────────

    // Descriptions for well-known EditionKeys.  Any edition not in this dict gets
    // a generic "Index N" subtitle — so unknown/OEM editions still work correctly.
    private static readonly Dictionary<string, string> EditionHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Home"]            = "Core Windows experience — best for standard end-user deployments.",
        ["Home N"]          = "Home edition without Windows Media Player (EU/EEA variant).",
        ["Pro"]             = "Adds BitLocker, Hyper-V, and Remote Desktop. Recommended for enterprise.",
        ["Pro N"]           = "Pro without Windows Media Player (EU/EEA variant).",
        ["Pro Education"]   = "Pro features tailored for educational institutions.",
        ["Pro Workstation"] = "Pro plus ReFS, persistent memory, and NUMA support for high-end workstations.",
        ["Enterprise"]      = "Full enterprise feature set: DirectAccess, AppLocker, BranchCache, and more.",
        ["Enterprise N"]    = "Enterprise without Windows Media Player (EU/EEA variant).",
        ["Education"]       = "Enterprise features tailored for educational institutions and lab environments.",
        ["Education N"]     = "Education without Windows Media Player (EU/EEA variant).",
        ["SE"]              = "Windows SE — lightweight cloud-first edition for education devices.",
        ["IoT Enterprise"]  = "Long-Term Servicing Channel edition for industrial and kiosk deployments.",
    };

    // Priority order for auto-selecting the 'best' edition when no previous choice exists
    private static readonly string[] EditionPreferenceOrder =
        ["Pro", "Enterprise", "Education", "Pro Workstation", "Pro Education", "Home", "Home N"];

    /// <summary>
    /// Rebuilds the edition radio-button list from the actual images inside the ISO.
    /// Called after analysis completes AND when restoring a previously loaded session.
    /// </summary>
    private void ApplyEditionAvailability(List<WindowsImageInfo> images)
    {
        EditionPanel.Children.Clear();

        if (images.Count == 0)
        {
            EditionNoIsoText.Visibility  = Visibility.Visible;
            EditionListBorder.Visibility = Visibility.Collapsed;
            EditionInfoBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // Sort by WIM index so they appear in the natural order from the image file
        var sorted = images.OrderBy(i => i.Index).ToList();

        // Determine which edition to auto-select:
        //   1) Previously selected image (session restore)
        //   2) First match in preference order
        //   3) First in the list
        var previousKey  = BuildSession.Current.SelectedImage?.Name ?? "";
        RadioButton? firstBtn     = null;
        RadioButton? preferredBtn = null;
        RadioButton? restoredBtn  = null;

        foreach (var img in sorted)
        {
            var rb = BuildEditionRow(img);
            EditionPanel.Children.Add(rb);

            if (firstBtn == null) firstBtn = rb;

            // Restore previous selection
            if (!string.IsNullOrEmpty(previousKey) &&
                img.Name.Equals(previousKey, StringComparison.OrdinalIgnoreCase))
                restoredBtn = rb;

            // Track first match in preference order
            if (preferredBtn == null)
            {
                foreach (var pref in EditionPreferenceOrder)
                {
                    if (img.Name.Contains(pref, StringComparison.OrdinalIgnoreCase) ||
                        img.EditionKey.Equals(pref, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredBtn = rb;
                        break;
                    }
                }
            }
        }

        // Apply initial check — don't fire Edition_Checked yet (IsLoaded guard handles it)
        var toSelect = restoredBtn ?? preferredBtn ?? firstBtn;
        if (toSelect != null)
            toSelect.IsChecked = true;

        EditionNoIsoText.Visibility  = Visibility.Collapsed;
        EditionListBorder.Visibility = Visibility.Visible;
        EditionInfoBorder.Visibility = Visibility.Visible;

        SetEditionsLocked(false);
    }

    /// <summary>Builds a single edition radio button row.</summary>
    private RadioButton BuildEditionRow(WindowsImageInfo img)
    {
        // Sub-label: "Index 6  ·  17.4 GB uncompressed" or just "Index 6"
        string subLabel = img.SizeBytes > 0
            ? $"Index {img.Index}  ·  {img.SizeDisplay}"
            : $"Index {img.Index}";

        // Content is a two-line stack: edition name + index/size sub-text
        var nameText = new TextBlock
        {
            Text       = img.Name,
            Foreground = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
        };
        var subText = new TextBlock
        {
            Text       = subLabel,
            Foreground = (Brush)Application.Current.Resources["FG3Brush"],
            FontSize   = 10,
            FontFamily = new FontFamily("Consolas"),
            Margin     = new Thickness(0, 2, 0, 0),
        };
        var content = new StackPanel();
        content.Children.Add(nameText);
        content.Children.Add(subText);

        var rb = new RadioButton
        {
            GroupName         = "Edition",
            Tag               = img,
            Content           = content,
            Margin            = new Thickness(0, 2, 0, 2),
            Padding           = new Thickness(8, 6, 8, 6),
            Style             = (Style)Application.Current.Resources["CustomRadioStyle"],
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        rb.Checked += Edition_Checked;
        return rb;
    }

    private void SetEditionsLocked(bool locked)
    {
        EditionPanel.IsEnabled     = !locked;
        EditionListBorder.Opacity  = locked ? 0.4 : 1.0;
    }

    private void Edition_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is RadioButton { Tag: WindowsImageInfo img })
            ApplyEditionSelection(img);
    }

    private void ApplyEditionSelection(WindowsImageInfo img)
    {
        // Persist to session
        BuildSession.Current.SelectedEdition = img.EditionKey;
        BuildSession.Current.SelectedImage   = img;

        // Update hint strip
        if (EditionInfoText == null) return;
        if (EditionHints.TryGetValue(img.Name, out var hint) ||
            EditionHints.TryGetValue(img.EditionKey, out hint))
            EditionInfoText.Text = hint;
        else
            EditionInfoText.Text = $"{img.Name}  (Index {img.Index})";
    }

    // ── Architecture controls ────────────────────────────────────────────────

    private void ApplyArchAvailability(List<string> archs)
    {
        var buttons = new[]
        {
            (ArchX64,   "x64"),
            (ArchX86,   "x86"),
            (ArchARM64, "ARM64"),
        };

        RadioButton? firstEnabled = null;
        foreach (var (btn, arch) in buttons)
        {
            bool found = archs.Contains(arch, StringComparer.OrdinalIgnoreCase);
            btn.IsEnabled = found;
            btn.Opacity   = found ? 1.0 : 0.35;
            if (found && firstEnabled == null) firstEnabled = btn;
        }

        if (firstEnabled != null) firstEnabled.IsChecked = true;
        SetArchsLocked(false);
    }

    private void Arch_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        BuildSession.Current.SelectedArch =
              ArchX86.IsChecked   == true ? "x86"
            : ArchARM64.IsChecked == true ? "ARM64"
            : "x64";
    }

    private void SetArchsLocked(bool locked)
    {
        foreach (var btn in new[] { ArchX64, ArchX86, ArchARM64 })
        {
            btn.IsEnabled = !locked;
            btn.Opacity   = locked ? 0.35 : 1.0;
        }
    }

    // ── Output path ──────────────────────────────────────────────────────────

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Output Folder" };
        if (dlg.ShowDialog() == true)
        {
            OutputPathBox.Text = dlg.FolderName;
            BuildSession.Current.OutputPath = dlg.FolderName;
            // Persist immediately so theme changes in SettingsPage don't wipe paths
            GoldenISOBuilder.Helpers.AppSettingsLoader.SavePaths(
                dlg.FolderName, BuildSession.Current.WorkspacePath);
        }
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Workspace (Temp) Folder" };
        if (dlg.ShowDialog() == true)
        {
            WorkspacePathBox.Text = dlg.FolderName;
            BuildSession.Current.WorkspacePath = dlg.FolderName;
            GoldenISOBuilder.Helpers.AppSettingsLoader.SavePaths(
                BuildSession.Current.OutputPath, dlg.FolderName);
        }
    }

    private void OutputPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        BuildSession.Current.OutputPath = OutputPathBox.Text;
        UpdateOutputDriveLetter();
        RefreshDriveSpace();
    }

    private void UpdateOutputDriveLetter()
    {
        var path = OutputPathBox?.Text ?? "";
        _outputDriveLetter = path.Length >= 2 && path[1] == ':'
            ? path[..1].ToUpperInvariant()
            : "";
    }

    // ── Disk space ───────────────────────────────────────────────────────────

    private void RefreshDriveSpace()
    {
        if (DriveSpacePanel == null) return;
        DriveSpacePanel.Children.Clear();

        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady &&
                        (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .OrderBy(d => d.Name);

        foreach (var drive in drives)
            DriveSpacePanel.Children.Add(BuildDriveRow(drive));

        if (!DriveSpacePanel.Children.OfType<UIElement>().Any())
        {
            DriveSpacePanel.Children.Add(new TextBlock
            {
                Text       = "No drives detected.",
                Foreground = (Brush)Application.Current.Resources["FG2Brush"],
                FontSize   = 13
            });
        }
    }

    private UIElement BuildDriveRow(DriveInfo drive)
    {
        long free    = drive.AvailableFreeSpace;
        long total   = drive.TotalSize;
        long used    = total - free;
        double usedPct = total > 0 ? Math.Clamp((double)used / total, 0, 1) : 0;
        bool sufficient = free >= RequiredBytes;

        bool isOutputDrive = drive.Name[..1].Equals(_outputDriveLetter,
            StringComparison.OrdinalIgnoreCase);

        // Label: "C: (Windows)" or just "E:"
        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
            ? drive.Name.TrimEnd(Path.DirectorySeparatorChar)
            : $"{drive.Name.TrimEnd(Path.DirectorySeparatorChar)}  ({drive.VolumeLabel})";

        string driveType = drive.DriveType == DriveType.Removable ? "  USB" : "";

        var row = new Border
        {
            Background      = (Brush)Application.Current.Resources[isOutputDrive ? "BG4Brush" : "BG3Brush"],
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(14, 10, 14, 10),
            BorderThickness = isOutputDrive ? new Thickness(1) : new Thickness(0),
            BorderBrush     = isOutputDrive ? (Brush)Application.Current.Resources["Gold1Brush"] : null,
        };

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Drive letter + label
        var labelTb = new TextBlock
        {
            Text                = label + driveType,
            Foreground          = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize            = 13,
            FontFamily          = new FontFamily("Consolas, Cascadia Code"),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelTb, 0);

        // Progress track + fill
        var track = new Border
        {
            Height              = 6,
            CornerRadius        = new CornerRadius(3),
            Background          = (Brush)Application.Current.Resources["BG1Brush"],
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 0, 20, 0),
        };
        var fill = new Border
        {
            Height              = 6,
            CornerRadius        = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        // Color: gold for normal, red if insufficient
        fill.Background = sufficient
            ? (Brush)Application.Current.Resources["GoldProgressBrush"]
            : (Brush)Application.Current.Resources["ErrBrush"];

        // Set fill width once track has been measured
        track.SizeChanged += (_, e) =>
            fill.Width = Math.Max(0, e.NewSize.Width * usedPct);

        var barGrid = new Grid();
        barGrid.Children.Add(track);
        barGrid.Children.Add(fill);
        Grid.SetColumn(barGrid, 1);

        // "237 GB free / 500 GB"
        var sizeTb = new TextBlock
        {
            Text                = $"{FormatGB(free)} free of {FormatGB(total)}",
            Foreground          = (Brush)Application.Current.Resources["FG2Brush"],
            FontSize            = 12,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 0, 16, 0),
        };
        Grid.SetColumn(sizeTb, 2);

        // Status pill
        var statusTb = new TextBlock
        {
            Text              = sufficient ? "✓ Sufficient" : "✗ Need more space",
            Foreground        = sufficient
                ? (Brush)Application.Current.Resources["OkBrush"]
                : (Brush)Application.Current.Resources["ErrBrush"],
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(statusTb, 3);

        outer.Children.Add(labelTb);
        outer.Children.Add(barGrid);
        outer.Children.Add(sizeTb);
        outer.Children.Add(statusTb);
        row.Child = outer;
        return row;
    }

    /// <summary>
    /// Returns the Windows system drive root (e.g. "C:\").
    /// Always uses the drive Windows is installed on — never picks D:\ or
    /// an external drive just because it has more free space.
    /// This is the last-resort fallback; saved settings are always preferred.
    /// </summary>
    private static string PickDefaultRoot()
    {
        // Environment.SystemDrive is set by Windows to the drive containing %windir%
        // (always C:\ on a standard installation).
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (!string.IsNullOrEmpty(systemDrive))
            return systemDrive + Path.DirectorySeparatorChar;   // "C:\"

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string FormatGB(long bytes) =>
        bytes >= 1_073_741_824
            ? $"{bytes / 1_073_741_824.0:F0} GB"
            : $"{bytes / 1_048_576.0:F0} MB";

    // ── Language picker ──────────────────────────────────────────────────────

    private void PopulateLanguageList(string filter)
    {
        // Start from the ISO's available languages if known; fall back to full list.
        var available = BuildSession.Current.IsoAvailableLanguages;
        var source = available.Count > 0
            ? AllLanguages.Where(l => available.Contains(l.Code, StringComparer.OrdinalIgnoreCase)).ToArray()
            : AllLanguages;

        // Apply search filter on top
        var items = string.IsNullOrWhiteSpace(filter)
            ? source
            : source.Where(l =>
                l.Code.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                l.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        LangListBox.ItemsSource = items;

        // If only one language is in the list, it's the only valid choice — force-select it.
        if (items.Length == 1)
        {
            _selectedLangCode = items[0].Code;
            _langAutoDetected = true;
            BuildSession.Current.IsoSourceLanguage = _selectedLangCode;
        }

        // Re-select the current language and scroll it into view.
        // Use Dispatcher at Loaded priority so WPF has finished creating ListBoxItem
        // containers before we try to set SelectedItem — otherwise the gold highlight
        // never appears even though SelectedItem is technically set.
        var match = items.FirstOrDefault(l => l.Code == _selectedLangCode);
        if (match != null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                LangListBox.SelectedItem = match;
                LangListBox.ScrollIntoView(match);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        UpdateLangBanner();
    }

    private void LangSearch_TextChanged(object sender, TextChangedEventArgs e)
        => PopulateLanguageList(LangSearchBox.Text);

    private void LangList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangListBox.SelectedItem is LangItem item)
        {
            _selectedLangCode = item.Code;
            _langAutoDetected = false;   // user made a manual choice — clear auto-detect flag
            BuildSession.Current.IsoSourceLanguage = _selectedLangCode;
        }
        UpdateLangBanner();
    }

    private void UpdateLangBanner()
    {
        var match = AllLanguages.FirstOrDefault(l => l.Code == _selectedLangCode);
        if (match != null)
        {
            LangSelectedText.Text = _langAutoDetected
                ? $"Auto-detected from ISO: {match.Code}  —  {match.DisplayName}"
                : $"Selected: {match.Code}  —  {match.DisplayName}";
            LangSelectedBanner.Visibility = Visibility.Visible;
        }
        else
        {
            LangSelectedBanner.Visibility = Visibility.Collapsed;
        }
    }

    // ── Target OS locale ComboBox ─────────────────────────────────────────────

    private void PopulateTargetLocaleCombo()
    {
        // Suppress SelectionChanged while we rebuild items to avoid spurious saves
        TargetLocaleCombo.SelectionChanged -= TargetLocaleCombo_SelectionChanged;
        TargetLocaleCombo.Items.Clear();

        foreach (var lang in AllTargetLocales)
        {
            var item = new ComboBoxItem
            {
                Content = $"{lang.Code}  —  {lang.DisplayName}",
                Tag     = lang.Code,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13
            };
            TargetLocaleCombo.Items.Add(item);

            if (lang.Code.Equals(_targetOsLocale, StringComparison.OrdinalIgnoreCase))
                TargetLocaleCombo.SelectedItem = item;
        }

        // If nothing matched (e.g. custom code from a saved profile), fall back to en-US
        if (TargetLocaleCombo.SelectedItem == null && TargetLocaleCombo.Items.Count > 0)
            TargetLocaleCombo.SelectedIndex = 0;

        TargetLocaleCombo.SelectionChanged += TargetLocaleCombo_SelectionChanged;
    }

    private void TargetLocaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetLocaleCombo.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            _targetOsLocale = code;
            BuildSession.Current.TargetOsLocale = code;
        }
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("welcome", 0);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // Save output config before leaving
        BuildSession.Current.OutputPath         = OutputPathBox.Text;
        BuildSession.Current.WorkspacePath      = WorkspacePathBox.Text;
        BuildSession.Current.OutputFilename     = OutputFilenameBox.Text;
        BuildSession.Current.IsoSourceLanguage  = _selectedLangCode;
        BuildSession.Current.TargetOsLocale     = _targetOsLocale;

        // Persist paths so they survive app restart (pre-fills Step 1 next time)
        GoldenISOBuilder.Helpers.AppSettingsLoader.SavePaths(
            OutputPathBox.Text, WorkspacePathBox.Text);

        if (BuildSession.Current.SourceIsoPath is null)
        {
            AppDialog.Alert(this, "Please select a Windows ISO before continuing.",
                "ISO Required", AppDialogIcon.Info);
            return;
        }

        NavigateRequested?.Invoke("wizard", 1);
    }
}
