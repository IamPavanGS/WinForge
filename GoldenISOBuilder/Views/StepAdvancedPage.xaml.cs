using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using System.Linq;
// GoldenISOBuilder.Helpers referenced from XAML (PanelSpacing attached property)

namespace GoldenISOBuilder.Views;

public partial class StepAdvancedPage : UserControl
{
    public event Action<string, int>? NavigateRequested;

    public StepAdvancedPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // Common enterprise / global time zones listed first; the rest alphabetically
    private static readonly string[] CommonTimeZones =
    [
        "India Standard Time",
        "UTC",
        "GMT Standard Time",
        "Romance Standard Time",
        "Central Europe Standard Time",
        "W. Europe Standard Time",
        "E. Europe Standard Time",
        "FLE Standard Time",
        "Arab Standard Time",
        "Arabian Standard Time",
        "Singapore Standard Time",
        "China Standard Time",
        "Tokyo Standard Time",
        "Korea Standard Time",
        "AUS Eastern Standard Time",
        "Eastern Standard Time",
        "Central Standard Time",
        "Mountain Standard Time",
        "Pacific Standard Time",
    ];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Populate time zone ComboBox (Windows TZ IDs)
        TimeZoneBox.Items.Clear();
        var allZones = TimeZoneInfo.GetSystemTimeZones()
                                   .Select(tz => tz.Id)
                                   .ToList();
        // Add common ones first, then the rest sorted
        var ordered = CommonTimeZones
            .Where(allZones.Contains)
            .Concat(allZones.Where(z => !CommonTimeZones.Contains(z)).OrderBy(z => z))
            .ToList();
        foreach (var id in ordered)
            TimeZoneBox.Items.Add(id);

        var s = BuildSession.Current;
        TimeZoneBox.SelectedItem = ordered.Contains(s.TimeZone) ? s.TimeZone : ordered.FirstOrDefault();

        OrgNameBox.Text    = s.OrgName;
        OwnerBox.Text      = s.RegisteredOwner;
        ProductKeyBox.Text = s.ProductKey;
        SkipOobeToggle.IsChecked  = s.SkipOobe;
        OemMfgBox.Text    = s.OemManufacturer;
        OemModelBox.Text  = s.OemModel;
        OemUrlBox.Text    = s.OemSupportUrl;

        // Restore computer renaming state
        bool hasPrefix = !string.IsNullOrWhiteSpace(s.ComputerPrefix);
        EnableRenameToggle.IsChecked = hasPrefix;
        PrefixBox.Text               = s.ComputerPrefix;
        PrefixPanel.Visibility       = hasPrefix ? System.Windows.Visibility.Visible
                                                  : System.Windows.Visibility.Collapsed;
        SelectHostnameTemplate(s.HostnameTemplate);
        UpdatePrefixPreview();

        switch (s.PowerPlan)
        {
            case "HighPerformance":  PlanHighPerf.IsChecked = true; break;
            case "UltimatePerformance": PlanUltimate.IsChecked = true; break;
            default: PlanBalanced.IsChecked = true; break;
        }

        FeatDotNet35.IsChecked  = s.EnabledFeatures.Contains("NetFx3");
        FeatHyperV.IsChecked    = s.EnabledFeatures.Contains("Microsoft-Hyper-V-All");
        FeatWsl.IsChecked       = s.EnabledFeatures.Contains("Microsoft-Windows-Subsystem-Linux");
        FeatSshServer.IsChecked = s.EnabledFeatures.Contains("OpenSSH.Server~~~~0.0.1.0");
        FeatIis.IsChecked       = s.EnabledFeatures.Contains("IIS-WebServerRole");
        FeatTelnet.IsChecked    = s.EnabledFeatures.Contains("TelnetClient");
        FeatNfs.IsChecked       = s.EnabledFeatures.Contains("ServicesForNFS-ClientOnly");
        FeatRsat.IsChecked      = s.EnabledFeatures.Contains("Rsat");

        // Load scheduled tasks list
        RefreshTaskList();
    }

    // ── Computer naming ───────────────────────────────────────────────────────

    private void EnableRename_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        bool on = EnableRenameToggle.IsChecked == true;
        PrefixPanel.Visibility = on ? System.Windows.Visibility.Visible
                                    : System.Windows.Visibility.Collapsed;
        if (!on) PrefixBox.Text = "";
        UpdatePrefixPreview();
    }

    private void PrefixBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdatePrefixPreview();

    private void UpdatePrefixPreview()
    {
        if (PrefixPreview == null) return;
        var prefix = PrefixBox?.Text?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrEmpty(prefix))
        {
            PrefixPreview.Text = "→ type a prefix above";
            return;
        }
        // Show a realistic example based on the chosen template so the user
        // sees how the hostname will look at first boot.
        var template = (HostnameTemplateCombo?.SelectedItem
                        as System.Windows.Controls.ComboBoxItem)?.Tag as string
                       ?? "{PREFIX}{SERIAL}";
        var sample = template switch
        {
            "{PREFIX}{LAST6_SERIAL}" => "B4586A",
            "{PREFIX}{LAST6_MAC}"    => "3A7F12",
            "{PREFIX}{ASSETTAG}"     => "AT00427",
            _                         => "PYB4586A"    // default: full serial
        };
        var sourceLabel = template switch
        {
            "{PREFIX}{LAST6_SERIAL}" => "last 6 of serial",
            "{PREFIX}{LAST6_MAC}"    => "last 6 of MAC",
            "{PREFIX}{ASSETTAG}"     => "asset tag",
            _                         => "BIOS serial"
        };
        PrefixPreview.Text = $"→ e.g.  {prefix}{sample}   (prefix + {sourceLabel})";
    }

    private void SelectHostnameTemplate(string template)
    {
        if (HostnameTemplateCombo == null) return;
        // Lookup the ComboBoxItem whose Tag matches; fall back to index 0
        // (the default {PREFIX}{SERIAL}) if the stored value is unrecognised.
        foreach (var obj in HostnameTemplateCombo.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item &&
                (item.Tag as string) == template)
            {
                HostnameTemplateCombo.SelectedItem = item;
                return;
            }
        }
        HostnameTemplateCombo.SelectedIndex = 0;
    }

    private void HostnameTemplate_Changed(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdatePrefixPreview();

    // ── Power plan tiles ──────────────────────────────────────────────────────

    private void PowerPlan_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        var plan = b.Tag?.ToString() ?? "Balanced";

        PlanBalanced.IsChecked  = plan == "Balanced";
        PlanHighPerf.IsChecked  = plan == "HighPerformance";
        PlanUltimate.IsChecked  = plan == "UltimatePerformance";

        // Update tile border highlights. The previous code cast each
        // RadioButton's logical Parent directly to Border — but Parent is the
        // StackPanel inside the tile, not the Border itself, so the cast
        // raised InvalidCastException on every click. Walk the visual tree
        // from the RadioButton instead; FindParentBorder finds the enclosing
        // tile Border safely.
        var radios = new DependencyObject[] { PlanBalanced, PlanHighPerf, PlanUltimate };
        var tags   = new[] { "Balanced", "HighPerformance", "UltimatePerformance" };
        for (int i = 0; i < radios.Length; i++)
        {
            var tile = FindParentBorder(radios[i]);
            if (tile != null)
            {
                tile.BorderBrush = tags[i] == plan
                    ? (Brush)Application.Current.Resources["Gold1Brush"]
                    : (Brush)Application.Current.Resources["LineBrush"];
                tile.Background  = tags[i] == plan
                    ? (Brush)Application.Current.Resources["NewBuildTileBrush"]
                    : (Brush)Application.Current.Resources["BG3Brush"];
            }
        }
    }

    private static Border? FindParentBorder(DependencyObject child)
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is Border b) return b;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("wizard", 4);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 6);
    }

    // ── Scheduled Tasks ───────────────────────────────────────────────────────

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ScheduledTaskDialog(Window.GetWindow(this));
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            BuildSession.Current.ScheduledTasks.Add(dlg.Result);
            RefreshTaskList();
        }
    }

    private void RefreshTaskList()
    {
        TaskListPanel.Children.Clear();
        var tasks = BuildSession.Current.ScheduledTasks;
        TaskEmptyHint.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var task in tasks)
            TaskListPanel.Children.Add(BuildTaskRow(task));
    }

    private Border BuildTaskRow(ScheduledTaskConfig task)
    {
        // Trigger label
        string trigLabel = task.TriggerType switch
        {
            TaskTriggerType.Once      => $"Once · {task.StartTime:dd MMM yyyy HH:mm}",
            TaskTriggerType.Daily     => $"Daily · {task.StartTime:HH:mm}",
            TaskTriggerType.Weekly    => $"Weekly · {WeekDayAbbrevs(task.WeekDays)} · {task.StartTime:HH:mm}",
            TaskTriggerType.AtLogon   => "At Logon",
            TaskTriggerType.AtStartup => "At Startup",
            _                         => task.TriggerType.ToString()
        };

        var row = new Border
        {
            Background      = (Brush)Application.Current.Resources["BG3Brush"],
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(14, 10, 14, 10),
            Margin          = new Thickness(0, 0, 0, 6),
            BorderBrush     = (Brush)Application.Current.Resources["LineBrush"],
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: info
        var info = new StackPanel { Orientation = Orientation.Vertical };
        var nameTb = new TextBlock
        {
            Text       = task.Name,
            FontSize   = 13, FontWeight = FontWeights.Medium
        };
        nameTb.SetResourceReference(TextBlock.ForegroundProperty, "FG0Brush");
        info.Children.Add(nameTb);
        var trigTb = new TextBlock
        {
            Text       = $"{trigLabel}  ·  {System.IO.Path.GetFileName(task.ActionPath)}",
            FontSize   = 11, Margin = new Thickness(0, 2, 0, 0)
        };
        trigTb.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");
        info.Children.Add(trigTb);
        if (task.DeleteAfterRun)
        {
            var deleteTb = new TextBlock
            {
                Text       = "✓ Run once then delete",
                FontSize   = 11, Margin = new Thickness(0, 2, 0, 0)
            };
            deleteTb.SetResourceReference(TextBlock.ForegroundProperty, "Gold1Brush");
            info.Children.Add(deleteTb);
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Right: Edit + Remove buttons
        var btns = new StackPanel
        {
            Orientation        = Orientation.Horizontal,
            VerticalAlignment  = VerticalAlignment.Center,
            Margin             = new Thickness(12, 0, 0, 0)
        };
        var editBtn = new Button
        {
            Content          = "Edit",
            Style            = (Style)Application.Current.Resources["GhostButtonStyle"],
            Padding          = new Thickness(10, 4, 10, 4),
            FontSize         = 12,
            FocusVisualStyle = null,
            Margin           = new Thickness(0, 0, 6, 0),
            Tag              = task
        };
        editBtn.Click += EditTask_Click;

        var removeBtn = new Button
        {
            Content          = "✕",
            Style            = (Style)Application.Current.Resources["GhostButtonStyle"],
            Padding          = new Thickness(8, 4, 8, 4),
            FontSize         = 12,
            FocusVisualStyle = null,
            Tag              = task
        };
        removeBtn.Click += RemoveTask_Click;

        btns.Children.Add(editBtn);
        btns.Children.Add(removeBtn);
        Grid.SetColumn(btns, 1);
        grid.Children.Add(btns);

        row.Child = grid;
        return row;
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ScheduledTaskConfig task) return;
        var dlg = new ScheduledTaskDialog(Window.GetWindow(this), task);
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            var idx = BuildSession.Current.ScheduledTasks.IndexOf(task);
            if (idx >= 0) BuildSession.Current.ScheduledTasks[idx] = dlg.Result;
            RefreshTaskList();
        }
    }

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ScheduledTaskConfig task) return;
        BuildSession.Current.ScheduledTasks.Remove(task);
        RefreshTaskList();
    }

    private static string WeekDayAbbrevs(List<int> days)
    {
        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        return string.Join(",", days.Select(d => d >= 0 && d < names.Length ? names[d] : "?"));
    }

    private void SaveToSession()
    {
        var s = BuildSession.Current;
        s.OrgName         = OrgNameBox.Text.Trim();
        s.RegisteredOwner = OwnerBox.Text.Trim();
        s.TimeZone        = TimeZoneBox.SelectedItem?.ToString() ?? "India Standard Time";
        // Only save prefix if renaming is enabled; clear it otherwise so BuildEngine skips the step
        s.ComputerPrefix    = EnableRenameToggle.IsChecked == true ? PrefixBox.Text.Trim() : "";
        s.HostnameTemplate  = (HostnameTemplateCombo?.SelectedItem
                                as System.Windows.Controls.ComboBoxItem)?.Tag as string
                              ?? "{PREFIX}{SERIAL}";
        s.ProductKey      = ProductKeyBox.Text.Trim();
        s.SkipOobe        = SkipOobeToggle.IsChecked == true;
        s.OemManufacturer = OemMfgBox.Text.Trim();
        s.OemModel        = OemModelBox.Text.Trim();
        s.OemSupportUrl   = OemUrlBox.Text.Trim();

        s.PowerPlan = PlanHighPerf.IsChecked == true    ? "HighPerformance"
                    : PlanUltimate.IsChecked == true     ? "UltimatePerformance"
                    : "Balanced";

        // Features managed by this page (fixed well-known list)
        var advancedManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NetFx3", "Microsoft-Hyper-V-All", "Microsoft-Windows-Subsystem-Linux",
            "OpenSSH.Server~~~~0.0.1.0", "IIS-WebServerRole", "TelnetClient",
            "ServicesForNFS-ClientOnly", "Rsat"
        };

        // Preserve any features added via Step 3 (discovered from ISO scan) that are not
        // in this page's managed set — merging instead of overwriting prevents data loss.
        var merged = s.EnabledFeatures
            .Where(f => !advancedManaged.Contains(f))
            .ToList();

        if (FeatDotNet35.IsChecked  == true) merged.Add("NetFx3");
        if (FeatHyperV.IsChecked    == true) merged.Add("Microsoft-Hyper-V-All");
        if (FeatWsl.IsChecked       == true) merged.Add("Microsoft-Windows-Subsystem-Linux");
        if (FeatSshServer.IsChecked == true) merged.Add("OpenSSH.Server~~~~0.0.1.0");
        if (FeatIis.IsChecked       == true) merged.Add("IIS-WebServerRole");
        if (FeatTelnet.IsChecked    == true) merged.Add("TelnetClient");
        if (FeatNfs.IsChecked       == true) merged.Add("ServicesForNFS-ClientOnly");
        if (FeatRsat.IsChecked      == true) merged.Add("Rsat");
        s.EnabledFeatures = merged;
    }
}
