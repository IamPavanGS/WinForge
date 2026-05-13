using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text.Json;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;
using GoldenISOBuilder.Views;

namespace GoldenISOBuilder;

public partial class MainWindow : Window
{
    private string _currentPage = "welcome";
    private int _currentStep = 0;
    private DispatcherTimer? _footerTimer;

    private static readonly string[] WizardStepNames =
    {
        "Source & Output", "Assets", "Customizations",
        "Admin Account", "Registry Changes", "Advanced Options",
        "Review", "Build"
    };

    public MainWindow()
    {
        InitializeComponent();

        // Set the taskbar / Alt-Tab icon programmatically — the embedded ApplicationIcon
        // covers the Explorer file icon; this covers the running-instance taskbar button.
        Icon = BuildAppIcon();

        // Prevent Windows Aero Snap from maximising the window when dragged to the
        // top of the screen. The custom chrome has no title bar and the footer
        // buttons become unreachable when the window fills the screen.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        };

        PageWelcome.NavigateRequested        += Navigate;
        PageStep1.NavigateRequested          += Navigate;
        PageStep2.NavigateRequested          += Navigate;
        PageStep3.NavigateRequested          += Navigate;
        PageStep4.NavigateRequested          += Navigate;
        PageStepRegistry.NavigateRequested   += Navigate;
        PageStepAdvanced.NavigateRequested   += Navigate;
        PageStep5.NavigateRequested          += Navigate;
        PageStep6.NavigateRequested          += Navigate;
        PageTestVm.NavigateRequested         += Navigate;

        // When any build finishes (success or failure) refresh the Welcome page
        // stats and recent-builds list — BuildHistoryStore was already updated by
        // the engine before this event fires.
        PageStep6.BuildCompleted += () => Dispatcher.BeginInvoke(() =>
        {
            PageWelcome.Refresh();
            RefreshSidebarHistory();
        }, System.Windows.Threading.DispatcherPriority.Background);

        // When the user clears build history from Settings, refresh Welcome immediately.
        PageSettings.HistoryCleared += () => Dispatcher.BeginInvoke(() =>
        {
            PageWelcome.Refresh();
            RefreshSidebarHistory();
        }, System.Windows.Threading.DispatcherPriority.Background);

        // ── Global keyboard shortcuts ──────────────────────────────────────────
        // Ctrl+N  → New Build (wizard step 0)
        // Ctrl+Home → Welcome / Home
        PreviewKeyDown += (_, e) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                return;
            switch (e.Key)
            {
                case System.Windows.Input.Key.N:
                    Navigate("wizard", 0);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Home:
                    Navigate("welcome");
                    e.Handled = true;
                    break;
            }
        };

        Loaded += (_, _) =>
        {
            RefreshSidebarHistory();

            // Clamp window to the usable work area (handles HiDPI / small laptops).
            var area = SystemParameters.WorkArea;
            if (Width  > area.Width)  Width  = Math.Max(MinWidth,  area.Width  - 20);
            if (Height > area.Height) Height = Math.Max(MinHeight, area.Height - 20);
            // Re-centre after clamping
            Left = area.Left + (area.Width  - Width)  / 2;
            Top  = area.Top  + (area.Height - Height) / 2;

            // Refresh the footer once on load and every 5s thereafter (catches
            // changes when the user types in Step 1 or external drives mount/unmount).
            UpdateFooterDriveBar();
            _footerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _footerTimer.Tick += (_, _) => UpdateFooterDriveBar();
            _footerTimer.Start();
        };
        Closed += (_, _) =>
        {
            _footerTimer?.Stop();
            PageTestVm.CleanupVmOnClose();
        };
    }

    /// <summary>
    /// Footer always shows C:\ (system drive) label and free space.
    /// </summary>
    private void UpdateFooterDriveBar()
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var root = systemDrive + Path.DirectorySeparatorChar;   // "C:\"
            var di = new DriveInfo(root);
            if (!di.IsReady)
            {
                FooterDriveBar.Visibility = Visibility.Collapsed;
                return;
            }

            FooterDrivePath.Text      = systemDrive;                // "C:"
            FooterFreeSpace.Text      = $"{di.AvailableFreeSpace / 1_073_741_824.0:F0} GB";
            FooterDriveBar.Visibility = Visibility.Visible;
        }
        catch
        {
            FooterDriveBar.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Public navigation entry point ────────────────────────────────────────

    public void Navigate(string page, int step = 0)
    {
        _currentPage = page;
        _currentStep = step;

        HideAllPages();
        UpdateBreadcrumb();
        UpdateIconRailActive();
        UpdateNavPanel();

        switch (page)
        {
            case "welcome":  PageWelcome.Visibility  = Visibility.Visible; break;
            case "wizard":   ShowWizardStep(step);                         break;
            case "progress": PageStep6.Visibility    = Visibility.Visible; break;
            case "settings": PageSettings.Visibility = Visibility.Visible; break;
            case "testvm":   PageTestVm.Visibility   = Visibility.Visible; break;
        }
    }

    // ─── Internal helpers ──────────────────────────────────────────────────────

    private void HideAllPages()
    {
        PageWelcome.Visibility       = Visibility.Collapsed;
        PageStep1.Visibility         = Visibility.Collapsed;
        PageStep2.Visibility         = Visibility.Collapsed;
        PageStep3.Visibility         = Visibility.Collapsed;
        PageStep4.Visibility         = Visibility.Collapsed;
        PageStepRegistry.Visibility  = Visibility.Collapsed;
        PageStepAdvanced.Visibility  = Visibility.Collapsed;
        PageStep5.Visibility         = Visibility.Collapsed;
        PageStep6.Visibility         = Visibility.Collapsed;
        PageSettings.Visibility      = Visibility.Collapsed;
        PageTestVm.Visibility        = Visibility.Collapsed;
    }

    private void ShowWizardStep(int step)
    {
        switch (step)
        {
            case 0: PageStep1.Visibility         = Visibility.Visible; break;
            case 1: PageStep2.Visibility         = Visibility.Visible; break;
            case 2: PageStep3.Visibility         = Visibility.Visible; break;
            case 3: PageStep4.Visibility         = Visibility.Visible; break;
            case 4: PageStepRegistry.Visibility  = Visibility.Visible; break;
            case 5: PageStepAdvanced.Visibility  = Visibility.Visible; break;
            case 6: PageStep5.Visibility         = Visibility.Visible; break;
            case 7: PageStep6.Visibility         = Visibility.Visible; break;
        }
    }

    private void UpdateBreadcrumb()
    {
        BreadcrumbHome.Visibility        = Visibility.Collapsed;
        BreadcrumbWizardPanel.Visibility = Visibility.Collapsed;
        BreadcrumbProgress.Visibility    = Visibility.Collapsed;
        BreadcrumbSettings.Visibility    = Visibility.Collapsed;

        switch (_currentPage)
        {
            case "welcome":
                BreadcrumbHome.Visibility = Visibility.Visible;
                break;
            case "wizard":
                BreadcrumbWizardPanel.Visibility = Visibility.Visible;
                BreadcrumbStepText.Text = $"Step {_currentStep + 1} of 8";
                break;
            case "progress":
                BreadcrumbProgress.Visibility = Visibility.Visible;
                BreadcrumbProgress.Text = "Build Progress";
                break;
            case "testvm":
                BreadcrumbProgress.Visibility = Visibility.Visible;
                BreadcrumbProgress.Text = "Test in VM";
                break;
            case "settings":
                BreadcrumbSettings.Visibility = Visibility.Visible;
                break;
        }
    }

    private void UpdateIconRailActive()
    {
        NavHomeBtn.Tag     = null;
        NavNewBuildBtn.Tag = null;
        NavProgressBtn.Tag = null;
        NavSettingsBtn.Tag = null;
        NavTestVmBtn.Tag   = null;

        switch (_currentPage)
        {
            case "welcome":  NavHomeBtn.Tag     = "active"; break;
            case "wizard":   NavNewBuildBtn.Tag = "active"; break;
            case "progress": NavProgressBtn.Tag = "active"; break;
            case "settings": NavSettingsBtn.Tag = "active"; break;
            case "testvm":   NavTestVmBtn.Tag   = "active"; break;
        }
    }

    private void UpdateNavPanel()
    {
        bool isWizard = _currentPage == "wizard" || _currentPage == "progress";
        bool isTestVm = _currentPage == "testvm";

        NavPanelBorder.Visibility  = isTestVm ? Visibility.Collapsed : Visibility.Visible;
        NavPanelColumn.Width       = isTestVm ? new GridLength(0)    : new GridLength(240);

        NavWelcomePanel.Visibility = isWizard ? Visibility.Collapsed : Visibility.Visible;
        NavWizardPanel.Visibility  = isWizard ? Visibility.Visible   : Visibility.Collapsed;
        if (isWizard) UpdateWizardStepBadges();
    }

    private void UpdateWizardStepBadges()
    {
        var accent   = (Brush)FindResource("Gold1Brush");
        var gold1c   = (Color)FindResource("Gold1Color");
        var accentBg = new SolidColorBrush(Color.FromArgb(0x22, gold1c.R, gold1c.G, gold1c.B));
        var fg2      = (Brush)FindResource("FG2Brush");
        var bg3      = (Brush)FindResource("BG3Brush");
        var lineBr   = (Brush)FindResource("LineBrush");
        var okBg     = (Brush)FindResource("OkPillBgBrush");
        var okLine   = (Brush)FindResource("OkPillLineBrush");
        var okTxt    = (Brush)FindResource("OkBrush");

        var steps = new[]
        {
            (S1Badge,   S1Text,   WizardStep1Btn),
            (S2Badge,   S2Text,   WizardStep2Btn),
            (S3Badge,   S3Text,   WizardStep3Btn),
            (S4Badge,   S4Text,   WizardStep4Btn),
            (SRegBadge, SRegText, WizardStepRegistryBtn),
            (SAdvBadge, SAdvText, WizardStepAdvancedBtn),
            (S5Badge,   S5Text,   WizardStep5Btn),
            (S6Badge,   S6Text,   WizardStep6Btn),
        };

        int active = _currentPage == "progress" ? 7 : _currentStep;

        for (int i = 0; i < steps.Length; i++)
        {
            var (badge, text, _) = steps[i];
            bool isActive   = i == active;
            bool isComplete = i < active;

            if (isActive)
            {
                badge.BorderBrush = accent;
                badge.Background  = accentBg;
                text.Foreground   = accent;
                text.Text         = (i + 1).ToString();
            }
            else if (isComplete)
            {
                badge.BorderBrush = okLine;
                badge.Background  = okBg;
                text.Foreground   = okTxt;
                text.Text         = "✓";
            }
            else
            {
                badge.BorderBrush = lineBr;
                badge.Background  = bg3;
                text.Foreground   = fg2;
                text.Text         = (i + 1).ToString();
            }
        }
    }

    // ─── Quick Access sidebar handlers ────────────────────────────────────────

    private void NavQuickHomeBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("welcome");

    private void NavQuickProfilesBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Load Profile",
            Filter = "ISO Builder Profiles (*.gibprofile)|*.gibprofile|All Files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json    = File.ReadAllText(dlg.FileName);
            var session = JsonSerializer.Deserialize<BuildSession>(json);
            if (session == null) throw new InvalidDataException("Empty profile.");
            BuildSession.Current = session;
            Navigate("wizard", 0);
            AppDialog.Alert(this, $"Profile loaded from:\n{dlg.FileName}", "Profile Loaded",
                            AppDialogIcon.Success);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Could not load profile:\n{ex.Message}", "Load Failed",
                            AppDialogIcon.Error);
        }
    }

    private void NavQuickTemplatesBtn_Click(object sender, RoutedEventArgs e)
    {
        // Apply a blank-slate session and jump straight into the wizard.
        // The user can override everything step by step.
        if (!AppDialog.Confirm(this,
                "This will clear any unsaved settings and start a new build from a blank template.\n\nContinue?",
                "New from Template", AppDialogIcon.Question))
            return;

        BuildSession.Current = new BuildSession();
        Navigate("wizard", 0);
    }

    // ─── Icon rail handlers ────────────────────────────────────────────────────

    private void NavHomeBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("welcome");

    private void NavNewBuildBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("wizard", _currentPage == "wizard" ? _currentStep : 0);

    private void NavProgressBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("progress");

    private void NavHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        Navigate("welcome");
        // Scroll to the Recent Builds section after the page is rendered
        Dispatcher.BeginInvoke(
            () => PageWelcome.Refresh(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void NavTestVmBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("testvm");

    private void NavSettingsBtn_Click(object sender, RoutedEventArgs e)
        => Navigate("settings");

    private void NavHelpBtn_Click(object sender, RoutedEventArgs e)
    {
        // Help button takes the user to the Settings → About card so they can
        // see version info, ADK status, view logs, and check for updates.
        Navigate("settings");
        Dispatcher.BeginInvoke(new Action(() => PageSettings.ScrollToAbout()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ─── Wizard step sidebar buttons ──────────────────────────────────────────

    private void WizardStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int step))
        {
            if (step == 7)
                Navigate("progress");
            else
                Navigate("wizard", step);
        }
    }

    // ─── Sidebar build history ─────────────────────────────────────────────────

    /// <summary>
    /// Reads build history and populates the compact sidebar list.
    /// Shows the 5 most-recent entries (newest first).
    /// </summary>
    private void RefreshSidebarHistory()
    {
        var records = BuildHistoryStore.Load();

        if (records.Count == 0)
        {
            SidebarHistoryEmpty.Visibility = Visibility.Visible;
            SidebarHistoryList.Visibility  = Visibility.Collapsed;
            return;
        }

        SidebarHistoryEmpty.Visibility = Visibility.Collapsed;
        SidebarHistoryList.Visibility  = Visibility.Visible;
        SidebarHistoryList.Children.Clear();

        var okBrush  = (Brush)FindResource("OkBrush");
        var errBrush = (Brush)FindResource("ErrBrush");
        var fg0Brush = (Brush)FindResource("FG0Brush");
        var fg3Brush = (Brush)FindResource("FG3Brush");

        foreach (var rec in records.AsEnumerable().Reverse().Take(5))
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width  = 7, Height = 7,
                Fill   = rec.Success ? okBrush : errBrush,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var name = !string.IsNullOrEmpty(rec.IsoPath)
                ? System.IO.Path.GetFileNameWithoutExtension(rec.IsoPath)
                : (string.IsNullOrEmpty(rec.EditionName) ? "(unnamed)" : rec.EditionName);

            var nameBlock = new TextBlock
            {
                Text          = name,
                FontSize      = 11.5,
                Foreground    = fg0Brush,
                TextTrimming  = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dateBlock = new TextBlock
            {
                Text       = rec.CompletedAt.ToString("dd MMM  HH:mm"),
                FontSize   = 10.5,
                Foreground = fg3Brush,
                Margin     = new Thickness(14, 1, 0, 0)
            };

            var textStack = new StackPanel();
            textStack.Children.Add(nameBlock);
            textStack.Children.Add(dateBlock);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(16, 4, 16, 4)
            };
            row.Children.Add(dot);
            row.Children.Add(textStack);

            // Clicking a row opens the ISO in Explorer (if it still exists)
            if (!string.IsNullOrEmpty(rec.IsoPath) && System.IO.File.Exists(rec.IsoPath))
            {
                row.Cursor = System.Windows.Input.Cursors.Hand;
                var isoPath = rec.IsoPath;
                row.MouseLeftButtonDown += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(
                                "explorer.exe", $"/select,\"{isoPath}\"")
                            { UseShellExecute = true });
                    }
                    catch { }
                };
            }

            SidebarHistoryList.Children.Add(row);
        }
    }

    // ─── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── App icon (generated at runtime — no image file dependency) ────────────

    /// <summary>
    /// Renders a gold "A" badge using WPF drawing primitives and returns it as a
    /// frozen BitmapSource suitable for Window.Icon.  Mirrors the title-bar badge
    /// design so the taskbar button, Alt-Tab thumbnail, and running-instance icon
    /// are all consistent with the visible branding.
    /// </summary>
    private static BitmapSource BuildAppIcon()
    {
        const int px  = 256;
        const double dpi = 96;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Rounded gold badge background
            var gold = new LinearGradientBrush(
                Color.FromRgb(0xC9, 0xA4, 0x2A),
                Color.FromRgb(0xE8, 0xC0, 0x40),
                new Point(0, 0), new Point(1, 1));

            double r = px * 0.18;
            dc.DrawRoundedRectangle(gold, null, new Rect(0, 0, px, px), r, r);

            // Dark "A" centred on the badge
            var typeface = new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

            var ft = new FormattedText(
                "A",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                px * 0.60,
                new SolidColorBrush(Color.FromRgb(0x0A, 0x16, 0x28)),
                dpi);

            dc.DrawText(ft, new Point(
                (px - ft.Width)  / 2,
                (px - ft.Height) / 2 - px * 0.02));
        }

        var bmp = new RenderTargetBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();   // makes it thread-safe and prevents further mutation
        return bmp;
    }
}
