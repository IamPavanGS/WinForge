using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class ScheduledTaskDialog : Window
{
    // ── Result ────────────────────────────────────────────────────────────────
    public ScheduledTaskConfig? Result { get; private set; }

    // ── Trigger type ──────────────────────────────────────────────────────────
    private TaskTriggerType _triggerType = TaskTriggerType.Once;
    private Button[] _triggerBtns = null!;

    // ─────────────────────────────────────────────────────────────────────────

    public ScheduledTaskDialog(Window owner, ScheduledTaskConfig? existing = null)
    {
        Owner = owner;
        InitializeComponent();
        _triggerBtns = [TrigOnce, TrigDaily, TrigWeekly, TrigAtLogon, TrigAtStartup];

        if (existing != null)
        {
            // Editing an existing task
            DialogTitle.Text            = "Edit Scheduled Task";
            OkButton.Content            = "Save Changes";
            NameBox.Text                = existing.Name;
            DescriptionBox.Text         = existing.Description;
            ActionPathBox.Text          = existing.ActionPath;
            ActionArgsBox.Text          = existing.ActionArguments;
            StartInBox.Text             = existing.StartInFolder;
            StartDateBox.Text           = existing.StartTime.ToString("dd/MM/yyyy");
            StartTimeBox.Text           = existing.StartTime.ToString("HH:mm");
            HighPrivToggle.IsChecked    = existing.RunWithHighestPrivileges;
            DeleteAfterToggle.IsChecked = existing.DeleteAfterRun;
            WakeToggle.IsChecked        = existing.WakeToRun;
            BatteryToggle.IsChecked     = existing.RunOnBattery;

            // Run As
            foreach (ComboBoxItem item in RunAsBox.Items)
                if (item.Tag?.ToString() == existing.RunAs) { item.IsSelected = true; break; }

            // Week days
            if (existing.WeekDays.Contains(0)) DaySun.IsChecked = true;
            if (existing.WeekDays.Contains(1)) DayMon.IsChecked = true;
            if (existing.WeekDays.Contains(2)) DayTue.IsChecked = true;
            if (existing.WeekDays.Contains(3)) DayWed.IsChecked = true;
            if (existing.WeekDays.Contains(4)) DayThu.IsChecked = true;
            if (existing.WeekDays.Contains(5)) DayFri.IsChecked = true;
            if (existing.WeekDays.Contains(6)) DaySat.IsChecked = true;

            SelectTriggerType(existing.TriggerType);
        }
        else
        {
            StartDateBox.Text = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
            SelectTriggerType(TaskTriggerType.Once);
        }
    }

    // ── Trigger type selection ────────────────────────────────────────────────

    private void TriggerType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && Enum.TryParse<TaskTriggerType>(b.Tag?.ToString(), out var t))
            SelectTriggerType(t);
    }

    private void SelectTriggerType(TaskTriggerType type)
    {
        _triggerType = type;

        // Highlight active button; dim the rest
        var accent    = (Brush)Application.Current.Resources["Gold1Brush"];
        var neutral   = (Brush)Application.Current.Resources["FG2Brush"];
        var activeBg  = (Brush)Application.Current.Resources["BG1Brush"];

        foreach (var btn in _triggerBtns)
        {
            bool active    = btn.Tag?.ToString() == type.ToString();
            btn.Foreground = active ? accent   : neutral;
            btn.Background = active ? activeBg : Brushes.Transparent;
        }

        bool hasDateTime = type is TaskTriggerType.Once or TaskTriggerType.Daily or TaskTriggerType.Weekly;
        bool isWeekly    = type == TaskTriggerType.Weekly;
        bool hasInfoNote = type is TaskTriggerType.AtLogon or TaskTriggerType.AtStartup;

        DateTimePanel.Visibility    = hasDateTime ? Visibility.Visible   : Visibility.Collapsed;
        WeekDayPanel.Visibility     = isWeekly    ? Visibility.Visible   : Visibility.Collapsed;
        TriggerInfoPanel.Visibility = hasInfoNote ? Visibility.Visible   : Visibility.Collapsed;

        if (hasInfoNote)
            TriggerInfoText.Text = type == TaskTriggerType.AtLogon
                ? "ℹ  This task runs every time any user logs on to the machine."
                : "ℹ  This task runs each time the machine starts up (before user logon).";
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private void BrowseAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Script or Application",
            Filter = "All Executables|*.exe;*.bat;*.cmd;*.ps1;*.vbs|" +
                     "PowerShell Scripts (*.ps1)|*.ps1|" +
                     "Batch Files (*.bat;*.cmd)|*.bat;*.cmd|" +
                     "Executables (*.exe)|*.exe|" +
                     "All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            ActionPathBox.Text = dlg.FileName;
    }

    // ── OK ────────────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // ── Validate Name ────────────────────────────────────────────────────
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            AppDialog.Alert(this, "Task Name is required.", "Validation", AppDialogIcon.Warning);
            return;
        }
        if (name.Any(c => "<>:\"/\\|?*".Contains(c)))
        {
            AppDialog.Alert(this, "Task Name contains invalid characters.", "Validation", AppDialogIcon.Warning);
            return;
        }

        // ── Validate Action ──────────────────────────────────────────────────
        var path = ActionPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            AppDialog.Alert(this, "Program / Script Path is required.", "Validation", AppDialogIcon.Warning);
            return;
        }

        // ── Parse start date (DD/MM/YYYY) ────────────────────────────────────
        DateTime startDate = DateTime.Today.AddDays(1);
        if (!string.IsNullOrWhiteSpace(StartDateBox.Text))
        {
            if (!DateTime.TryParseExact(StartDateBox.Text.Trim(), "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out startDate))
            {
                AppDialog.Alert(this, "Invalid start date. Use DD/MM/YYYY format (e.g. 15/06/2025).",
                    "Validation", AppDialogIcon.Warning);
                return;
            }
        }

        // ── Parse start time (HH:MM) ─────────────────────────────────────────
        TimeSpan startTime = TimeSpan.FromHours(8);
        if (!string.IsNullOrWhiteSpace(StartTimeBox.Text))
        {
            if (!TimeSpan.TryParseExact(StartTimeBox.Text.Trim(), @"HH\:mm",
                    System.Globalization.CultureInfo.InvariantCulture, out startTime))
            {
                AppDialog.Alert(this, "Invalid time. Use HH:MM format (e.g. 08:30).",
                    "Validation", AppDialogIcon.Warning);
                return;
            }
        }

        // ── Validate week days ───────────────────────────────────────────────
        var weekDays = new List<int>();
        if (DaySun.IsChecked == true) weekDays.Add(0);
        if (DayMon.IsChecked == true) weekDays.Add(1);
        if (DayTue.IsChecked == true) weekDays.Add(2);
        if (DayWed.IsChecked == true) weekDays.Add(3);
        if (DayThu.IsChecked == true) weekDays.Add(4);
        if (DayFri.IsChecked == true) weekDays.Add(5);
        if (DaySat.IsChecked == true) weekDays.Add(6);

        if (_triggerType == TaskTriggerType.Weekly && weekDays.Count == 0)
        {
            AppDialog.Alert(this, "Select at least one day of the week for a Weekly trigger.",
                "Validation", AppDialogIcon.Warning);
            return;
        }

        var runAs = (RunAsBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "SYSTEM";

        Result = new ScheduledTaskConfig
        {
            Name                     = name,
            Description              = DescriptionBox.Text.Trim(),
            TriggerType              = _triggerType,
            StartTime                = startDate.Date + startTime,
            WeekDays                 = weekDays,
            ActionPath               = path,
            ActionArguments          = ActionArgsBox.Text.Trim(),
            StartInFolder            = StartInBox.Text.Trim(),
            RunAs                    = runAs,
            RunWithHighestPrivileges = HighPrivToggle.IsChecked == true,
            DeleteAfterRun           = DeleteAfterToggle.IsChecked == true,
            WakeToRun                = WakeToggle.IsChecked == true,
            RunOnBattery             = BatteryToggle.IsChecked == true,
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
