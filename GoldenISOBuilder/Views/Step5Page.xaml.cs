using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class Step5Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    public Step5Page()
    {
        InitializeComponent();
        // IsVisibleChanged fires every time the page becomes visible — unlike Loaded,
        // which only fires once when the control is first added to the visual tree.
        // Without this the preflight panel would show stale (empty-session) results.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) Refresh();
    }

    private void Refresh()
    {
        var s = BuildSession.Current;

        V_IsoSource.Text  = string.IsNullOrEmpty(s.SourceIsoPath) ? "—" : Path.GetFileName(s.SourceIsoPath);
        V_Edition.Text    = s.SelectedImage?.Name ?? s.SelectedEdition;
        V_Arch.Text       = s.SelectedArch;
        V_Output.Text     = string.IsNullOrEmpty(s.OutputPath) ? "—" : s.OutputPath;

        V_Wallpaper.Text  = string.IsNullOrEmpty(s.WallpaperPath) ? "None" : Path.GetFileName(s.WallpaperPath);
        V_Apps.Text       = $"{s.StagedApps.Count} application(s)";
        // StagedFiles is the current field; PublicDesktopFiles is the legacy field kept
        // for backward-compatible profile loading only. Show the combined count so
        // both old and new profiles display correctly.
        int stagedFileCount = s.StagedFiles.Count + s.PublicDesktopFiles.Count;
        V_PublicFiles.Text = stagedFileCount == 0 ? "None" : $"{stagedFileCount} file(s)";
        V_Keyboard.Text   = s.IncludeDeploymentScripts && s.DeploymentScripts.Count > 0
                            ? $"{s.DeploymentScripts.Count} script(s)"
                            : "None";
        V_LangPacks.Text  = s.LanguagePackPaths.Count == 0 ? "None" : $"{s.LanguagePackPaths.Count} pack(s)";
        V_Drivers.Text    = s.DriverFolderPaths.Count == 0 ? "None" : $"{s.DriverFolderPaths.Count} folder(s)";

        V_Bloat.Text          = s.BloatwareToRemove.Count == 0 ? "None" : $"{s.BloatwareToRemove.Count} package(s)";
        V_GroupPolicies.Text  = s.GroupPolicies.Count == 0 ? "None" : $"{s.GroupPolicies.Count} polic{(s.GroupPolicies.Count == 1 ? "y" : "ies")}";
        V_Smb1.Text       = s.DisableSmbV1 ? "Yes" : "No";
        V_Telemetry.Text  = s.DisableTelemetry ? "Yes" : "No";
        V_DarkMode.Text   = s.DarkMode ? "Enabled" : "Disabled";
        V_BitLocker.Text  = s.EnableBitLocker ? "Enabled — key → C:\\BitLockerRecoveryKey.txt" : "Disabled";

        V_AdminUser.Text  = string.IsNullOrEmpty(s.AdminUsername) ? "Administrator" : s.AdminUsername;
        V_AdminPwd.Text   = string.IsNullOrEmpty(s.AdminPassword) ? "(not set)" : new string('●', Math.Min(12, s.AdminPassword.Length));
        V_AutoLogin.Text  = s.AutoLoginEnabled ? "Enabled" : "Disabled";
        V_Prefix.Text     = string.IsNullOrEmpty(s.ComputerPrefix) ? "(default)" : s.ComputerPrefix;

        V_RegEntries.Text = $"{s.CustomRegistryEntries.Count} entry(ies)";
        // Combine enabled (+) and disabled (−) into one wrapped line.
        // Using a stacked layout in XAML prevents long lists from overflowing left over the label.
        var featureParts = new List<string>();
        if (s.EnabledFeatures.Count  > 0) featureParts.Add("＋ " + string.Join(", ", s.EnabledFeatures.Select(ShortFeatureName)));
        if (s.DisabledFeatures.Count > 0) featureParts.Add("－ " + string.Join(", ", s.DisabledFeatures.Select(ShortFeatureName)));
        V_Features.Text = featureParts.Count == 0 ? "None" : string.Join("\n", featureParts);
        V_Power.Text      = s.PowerPlan;

        BuildPreflight();
    }

    private static string ShortFeatureName(string raw) => raw switch
    {
        "NetFx3"                            => ".NET 3.5",
        "Microsoft-Hyper-V-All"             => "Hyper-V",
        "Microsoft-Windows-Subsystem-Linux" => "WSL",
        "OpenSSH.Server~~~~0.0.1.0"         => "OpenSSH",
        "IIS-WebServerRole"                 => "IIS",
        "TelnetClient"                      => "Telnet",
        "ServicesForNFS-ClientOnly"         => "NFS",
        "Rsat"                              => "RSAT",
        _                                    => raw
    };

    // ── Pre-flight ────────────────────────────────────────────────────────────

    private void BuildPreflight()
    {
        PreflightPanel.Children.Clear();
        var s = BuildSession.Current;

        bool hasIso  = !string.IsNullOrEmpty(s.SourceIsoPath) && File.Exists(s.SourceIsoPath);
        bool hasEdt  = s.SelectedImage != null;
        bool hasOut  = !string.IsNullOrEmpty(s.OutputPath);
        bool hasOscd = BuildEngine.FindOscdimg() != null;
        bool isAdmin;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            isAdmin = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // disk space (best-effort) — check BOTH workspace and output drives
        long freeWorkspace = 0, freeOutput = 0;
        try
        {
            if (!string.IsNullOrEmpty(s.WorkspacePath) && Directory.Exists(Path.GetPathRoot(s.WorkspacePath) ?? ""))
                freeWorkspace = new DriveInfo(Path.GetPathRoot(s.WorkspacePath)!).AvailableFreeSpace;
        }
        catch { }
        try
        {
            if (!string.IsNullOrEmpty(s.OutputPath) && Directory.Exists(Path.GetPathRoot(s.OutputPath) ?? ""))
                freeOutput = new DriveInfo(Path.GetPathRoot(s.OutputPath)!).AvailableFreeSpace;
        }
        catch { }
        bool hasSpace = freeWorkspace >= 25L * 1024 * 1024 * 1024;
        // Output drive needs ~6 GB for the final ISO; only check if output path is on a different drive
        bool hasOutputSpace = string.IsNullOrEmpty(s.OutputPath) || freeOutput >= 6L * 1024 * 1024 * 1024;

        AddCheck(hasIso,  "Source ISO present",       string.IsNullOrEmpty(s.SourceIsoPath) ? "Select an ISO in Step 1" : Path.GetFileName(s.SourceIsoPath!));
        string editionLabel = s.SelectedImage?.Name ?? (s.AvailableImages.Count > 0 ? "Click a radio button to select an edition" : "Analyse an ISO in Step 1");
        AddCheck(hasEdt,  "Edition selected",         editionLabel);
        AddCheck(hasOut,  "Output folder configured", s.OutputPath);
        AddCheck(hasOscd, "oscdimg.exe available",    hasOscd ? "ADK detected" : "Install Windows ADK > Deployment Tools");
        AddCheck(hasSpace,"Sufficient workspace disk", hasSpace ? $"{freeWorkspace / 1_073_741_824} GB free" : "Need ≥25 GB on workspace drive");
        AddCheck(hasOutputSpace, "Sufficient output disk",    hasOutputSpace ? $"{freeOutput / 1_073_741_824} GB free on output drive" : "Need ≥6 GB on output drive");
        AddCheck(isAdmin, "Running as Administrator", isAdmin ? "Elevated" : "Re-launch as Admin (DISM requires it)");

        // ADK missing is handled with a popup in BuildISO_Click so the user gets
        // clear guidance — the Build button stays enabled for all other checks.
        bool ok = hasIso && hasEdt && hasOut && hasSpace && hasOutputSpace && isAdmin;
        BuildButton.IsEnabled = ok;
        BuildButton.Opacity   = ok ? 1.0 : 0.5;
    }

    private void AddCheck(bool ok, string title, string? detail)
    {
        var border = new Border
        {
            Background   = (Brush)Application.Current.Resources["BG3Brush"],
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(12, 8, 12, 8)
        };

        // Single-line layout: [icon]  [title]  ·  [detail]
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        sp.Children.Add(new TextBlock
        {
            Text              = ok ? "✓" : "!",
            FontSize          = 13,
            FontWeight        = FontWeights.Bold,
            Foreground        = (Brush)Application.Current.Resources[ok ? "OkBrush" : "WarnBrush"],
            Width             = 18,
            VerticalAlignment = VerticalAlignment.Center
        });

        sp.Children.Add(new TextBlock
        {
            Text              = title,
            Foreground        = (Brush)Application.Current.Resources["FG0Brush"],
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 0, 0)
        });

        if (!string.IsNullOrEmpty(detail))
        {
            sp.Children.Add(new TextBlock
            {
                Text              = $"  ·  {detail}",
                Foreground        = (Brush)Application.Current.Resources[ok ? "FG2Brush" : "WarnBrush"],
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        border.Child = sp;
        PreflightPanel.Children.Add(border);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void EditStep1_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("wizard", 0);
    private void EditStep2_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("wizard", 1);
    private void EditStep3_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("wizard", 2);
    private void EditStep4_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("wizard", 3);

    private void Back_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("wizard", 5);

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
        => SaveProfile();

    private void SaveProfile()
    {
        var dlg = new SaveFileDialog
        {
            Title      = "Save Build Profile",
            Filter     = "Golden ISO Profile|*.gibprofile|JSON|*.json|All files|*.*",
            DefaultExt = ".gibprofile",
            FileName   = "MyBuildProfile"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = JsonSerializer.Serialize(BuildSession.Current,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            AppDialog.Alert(this,
                $"Profile saved to:\n{dlg.FileName}\n\nUse \"Open Profile\" on the Home page to reload it later.",
                "Profile Saved", AppDialogIcon.Info);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Couldn't save profile:\n\n{ex.Message}",
                "Save Profile", AppDialogIcon.Error);
        }
    }

    private void BuildISO_Click(object sender, RoutedEventArgs e)
    {
        // ADK check — give a clear popup directing the user to Settings rather than
        // silently blocking the build button with no explanation.
        if (BuildEngine.FindOscdimg() == null)
        {
            AppDialog.Alert(this,
                "oscdimg.exe (Windows ADK — Deployment Tools) was not found on this machine.\n\n" +
                "Please go to Settings → Tools and click Install ADK to download and install it.\n\n" +
                "The build cannot proceed without the ADK.",
                "ADK Not Installed", AppDialogIcon.Warning);
            return;
        }
        NavigateRequested?.Invoke("progress", 0);
    }
}
