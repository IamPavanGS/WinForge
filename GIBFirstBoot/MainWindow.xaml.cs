using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GIBFirstBoot.Models;
using Microsoft.Win32;

namespace GIBFirstBoot;

public partial class MainWindow : Window
{
    // ── Paths ────────────────────────────────────────────────────────────────
    private static readonly string AppDir       = AppContext.BaseDirectory;
    private static readonly string ManifestPath = Path.Combine(AppDir, "apps.json");
    private static readonly string StatePath    = Path.Combine(AppDir, "state.json");
    private static readonly string InstallersDir= Path.Combine(AppDir, "Installers");

    private const string RegRunKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RegRunName = "GIBFirstBoot";

    // ── State ────────────────────────────────────────────────────────────────
    private List<AppEntry>     _apps  = [];
    private InstallState       _state = new();
    private List<AppRowControl> _rows = [];

    // True once the task is fully done and it is safe to self-delete on close.
    // False while installing (or when failures exist and RunOnce must be kept).
    private bool _cleanupOnClose = false;

    public MainWindow()
    {
        InitializeComponent();
        Icon    =  BuildAppIcon();
        Loaded  += OnLoaded;
        Closing += OnWindowClosing;
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!LoadManifest()) { Application.Current.Shutdown(1); return; }
        LoadState();
        BuildAppRows();
        await RunInstallsAsync();
    }

    private bool LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            MessageBox.Show(
                $"apps.json not found at:\n{ManifestPath}",
                "Configuration Missing", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        try
        {
            var json = File.ReadAllText(ManifestPath);
            _apps = JsonSerializer.Deserialize<List<AppEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read apps.json:\n{ex.Message}",
                "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                _state = JsonSerializer.Deserialize<InstallState>(json) ?? new();
            }
        }
        catch { _state = new(); }
    }

    private void SaveState()
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(_state)); }
        catch { /* best-effort */ }
    }

    // ── UI rows ───────────────────────────────────────────────────────────────

    private void BuildAppRows()
    {
        AppListPanel.Children.Clear();
        _rows.Clear();

        foreach (var app in _apps)
        {
            var status = _state.Completed.Contains(app.Name) ? AppStatus.Done
                       : _state.Failed.Contains(app.Name)    ? AppStatus.Failed
                       : AppStatus.Pending;
            var row = new AppRowControl(app.Name, status);
            _rows.Add(row);
            AppListPanel.Children.Add(row);
        }

        UpdateCounters();
    }

    private void UpdateCounters()
    {
        int done  = _state.Completed.Count;
        int total = _apps.Count;
        int pct   = total > 0 ? (int)Math.Round((double)done / total * 100) : 0;

        CountLabel.Text  = $"{done} / {total}";
        OverallBar.Value = pct;
        OverallPct.Text  = $"{pct}%";
    }

    private AppRowControl? RowFor(string name) =>
        _rows.FirstOrDefault(r => r.AppName == name);

    // ── Install pipeline ─────────────────────────────────────────────────────

    private async Task RunInstallsAsync()
    {
        var pending = _apps.Where(a => !_state.Completed.Contains(a.Name)).ToList();

        if (pending.Count == 0)
        {
            await FinishAsync();
            return;
        }

        // Re-register in RunOnce BEFORE starting any installation.
        // Windows consumes the RunOnce entry the moment it launches this exe.
        // If the machine is rebooted or killed mid-install, this ensures the
        // app fires again on next login and resumes from where it left off.
        // RemoveRunKey() at the end (in FinishAsync) cancels this once all done.
        ReAddToRunOnce();

        SetStatus($"Starting installation of {pending.Count} application(s)…");

        foreach (var app in pending)
        {
            var row = RowFor(app.Name);
            row?.SetStatus(AppStatus.Installing);
            SetStatus($"Installing {app.Name}…");

            bool success = await InstallAppAsync(app);

            if (success)
            {
                _state.Completed.Add(app.Name);
                row?.SetStatus(AppStatus.Done);
            }
            else
            {
                _state.Failed.Add(app.Name);
                row?.SetStatus(AppStatus.Failed);
            }

            SaveState();
            UpdateCounters();
        }

        await FinishAsync();
    }

    private static async Task<bool> InstallAppAsync(AppEntry app)
    {
        var installer = Path.Combine(InstallersDir, app.File);
        if (!File.Exists(installer)) return false;

        try
        {
            string exe, args;
            if (app.Type.Equals("msi", StringComparison.OrdinalIgnoreCase))
            {
                exe  = "msiexec.exe";

                // Optional transform — TRANSFORMS must come BEFORE /qn per msiexec parsing rules.
                // Quote the path because Installers\ may contain spaces.
                string transforms = "";
                if (!string.IsNullOrWhiteSpace(app.Mst))
                {
                    var mstPath = Path.Combine(InstallersDir, app.Mst);
                    if (File.Exists(mstPath))
                        transforms = $"TRANSFORMS=\"{mstPath}\" ";
                }

                args = $"/i \"{installer}\" {transforms}/qn /norestart {app.Args}".Trim();
            }
            else
            {
                exe  = installer;
                args = app.Args;
            }

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow  = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // Per-app timeout — clamp to [5, 240] minutes. Default 60.
            int timeoutMin = app.TimeoutMinutes <= 0 ? 60 : Math.Clamp(app.TimeoutMinutes, 5, 240);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Installer hung past its timeout — kill the process tree and report failure.
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            // Accept any exit code in the success list (default: 0 and 3010=reboot-later)
            var acceptedCodes = ParseExitCodes(app.SuccessExitCodes ?? "0,3010");
            return acceptedCodes.Contains(proc.ExitCode);
        }
        catch { return false; }
    }

    private static HashSet<int> ParseExitCodes(string raw)
    {
        var result = new HashSet<int> { 0 };
        foreach (var part in raw.Split(','))
            if (int.TryParse(part.Trim(), out int code))
                result.Add(code);
        return result;
    }

    // ── Completion ───────────────────────────────────────────────────────────

    private async Task FinishAsync()
    {
        bool allDone   = _apps.All(a => _state.Completed.Contains(a.Name));
        bool hasFailed = _state.Failed.Count > 0;

        await Task.Delay(500); // brief pause so the last status update is visible

        if (allDone)
        {
            // All apps installed — remove RunOnce so it never fires again,
            // then wait for the technician to acknowledge and close manually.
            RemoveRunKey();
            _cleanupOnClose = true;

            Dispatcher.Invoke(() =>
            {
                HeaderTitle.Text = "Setup complete!";
                HeaderSub.Text   = "All applications have been installed successfully.";
                SetStatus("Installation finished. Please close this window when you are ready.");
                CloseBtn.Visibility = Visibility.Visible;
            });
        }
        else if (hasFailed)
        {
            // RunOnce entries are consumed by Windows on first run regardless.
            // Re-register so the app actually retries on next login.
            ReAddToRunOnce();
            // Still show Close button so the technician can dismiss the window.
            int failCount = _state.Failed.Count;
            Dispatcher.Invoke(() =>
            {
                HeaderTitle.Text = "Setup finished with errors";
                HeaderSub.Text   = $"{failCount} application(s) failed to install and will retry on next login.";
                SetStatus("Review the list above, then close this window.");
                CloseBtn.Visibility = Visibility.Visible;
            });
            // _cleanupOnClose stays false — self-delete is skipped so the
            // GIB folder (and apps.json) is preserved for the retry run.
        }
        else
        {
            // Edge case: re-launched after everything was already completed.
            RemoveRunKey();
            _cleanupOnClose = true;

            Dispatcher.Invoke(() =>
            {
                HeaderTitle.Text = "Already complete";
                HeaderSub.Text   = "All applications were installed on a previous run.";
                SetStatus("Nothing to do. Please close this window.");
                CloseBtn.Visibility = Visibility.Visible;
            });
        }
    }

    // ── Close button ─────────────────────────────────────────────────────────

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        // Shutdown() triggers OnWindowClosing which handles self-delete.
        Application.Current.Shutdown(0);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Self-delete only when the task is done and cleanup is safe.
        // If the user closes mid-install via Alt+F4 or Task Manager, skip cleanup
        // so the app can be re-launched to finish.
        if (_cleanupOnClose)
            ScheduleSelfDelete();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private static void ReAddToRunOnce()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
            key?.SetValue(RegRunName, @"C:\GIB\GIBFirstBoot.exe");
        }
        catch { /* best-effort */ }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegRunKey, writable: true);
            key?.DeleteValue(RegRunName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    private static void ScheduleSelfDelete()
    {
        // cmd.exe /c "timeout 4 && rmdir /s /q "<AppDir>""
        // Runs detached after this process exits.
        var dir = AppDir.TrimEnd('\\', '/');
        var bat = $"timeout /t 4 /nobreak > nul & rmdir /s /q \"{dir}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                CreateNoWindow  = true,
                UseShellExecute = false
            });
        }
        catch { /* if cleanup fails, files remain — harmless */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string msg) =>
        Dispatcher.Invoke(() => StatusLine.Text = msg);

    // ── App icon ──────────────────────────────────────────────────────────────

    private static BitmapSource BuildAppIcon()
    {
        const int    px  = 256;
        const double dpi = 96;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var gold = new LinearGradientBrush(
                Color.FromRgb(0xC9, 0xA4, 0x2A),
                Color.FromRgb(0xE8, 0xC0, 0x40),
                new Point(0, 0), new Point(1, 1));

            double r = px * 0.18;
            dc.DrawRoundedRectangle(gold, null, new Rect(0, 0, px, px), r, r);

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
        bmp.Freeze();
        return bmp;
    }
}

// ── Per-app row control ───────────────────────────────────────────────────────

public enum AppStatus { Pending, Installing, Done, Failed }

public class AppRowControl : Border
{
    public string AppName { get; }

    private readonly TextBlock _statusIcon;
    private readonly TextBlock _statusLabel;
    private readonly ProgressBar _bar;

    private static readonly SolidColorBrush Gold  = new(Color.FromRgb(0xC9, 0xA8, 0x4C));
    private static readonly SolidColorBrush Green  = new(Color.FromRgb(0x5D, 0xC8, 0x73));
    private static readonly SolidColorBrush Red    = new(Color.FromRgb(0xD4, 0x48, 0x40));
    private static readonly SolidColorBrush Grey   = new(Color.FromRgb(0x6A, 0x6C, 0x7A));
    private static readonly SolidColorBrush FG0    = new(Color.FromRgb(0xF4, 0xF5, 0xFC));
    private static readonly SolidColorBrush FG2    = new(Color.FromRgb(0x96, 0x98, 0xA8));
    private static readonly SolidColorBrush BG2    = new(Color.FromRgb(0x1E, 0x20, 0x28));
    private static readonly SolidColorBrush BG3    = new(Color.FromRgb(0x25, 0x28, 0x30));
    private static readonly SolidColorBrush Line   = new(Color.FromRgb(0x35, 0x37, 0x48));

    public AppRowControl(string appName, AppStatus initialStatus)
    {
        AppName         = appName;
        Background      = BG2;
        CornerRadius    = new CornerRadius(8);
        Padding         = new Thickness(16, 12, 16, 12);
        Margin          = new Thickness(0, 0, 0, 6);
        BorderThickness = new Thickness(1);
        BorderBrush     = Line;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Status icon circle
        var iconBorder = new Border
        {
            Width  = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = BG3, VerticalAlignment = VerticalAlignment.Center
        };
        _statusIcon = new TextBlock
        {
            FontSize            = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        iconBorder.Child = _statusIcon;
        Grid.SetColumn(iconBorder, 0);

        // Centre column: name + progress bar
        var centre = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameTb = new TextBlock
        {
            Text       = appName,
            Foreground = FG0,
            FontSize   = 13,
            FontWeight = FontWeights.Medium
        };
        _bar = new ProgressBar
        {
            Height          = 4,
            Margin          = new Thickness(0, 6, 0, 0),
            Background      = BG3,
            Foreground      = Gold,
            BorderThickness = new Thickness(0),
            Visibility      = Visibility.Collapsed,
            Style           = (Style?)Application.Current.Resources["GoldBar"]
        };
        centre.Children.Add(nameTb);
        centre.Children.Add(_bar);
        Grid.SetColumn(centre, 1);

        // Status label (right)
        _statusLabel = new TextBlock
        {
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_statusLabel, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(centre);
        grid.Children.Add(_statusLabel);
        Child = grid;

        SetStatus(initialStatus);
    }

    public void SetStatus(AppStatus status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            switch (status)
            {
                case AppStatus.Pending:
                    _statusIcon.Text       = "○";
                    _statusIcon.Foreground = Grey;
                    _statusLabel.Text       = "Pending";
                    _statusLabel.Foreground = Grey;
                    _bar.Visibility         = Visibility.Collapsed;
                    BorderBrush             = Line;
                    break;

                case AppStatus.Installing:
                    _statusIcon.Text        = "↓";
                    _statusIcon.Foreground  = Gold;
                    _statusLabel.Text       = "Installing…";
                    _statusLabel.Foreground = Gold;
                    _bar.IsIndeterminate    = true;
                    _bar.Visibility         = Visibility.Visible;
                    BorderBrush             = Gold;
                    Background              = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x18));
                    break;

                case AppStatus.Done:
                    _statusIcon.Text        = "✓";
                    _statusIcon.Foreground  = Green;
                    _statusLabel.Text       = "Installed";
                    _statusLabel.Foreground = Green;
                    _bar.Visibility         = Visibility.Collapsed;
                    BorderBrush             = Line;
                    Background              = new SolidColorBrush(Color.FromRgb(0x14, 0x22, 0x18));
                    break;

                case AppStatus.Failed:
                    _statusIcon.Text        = "✗";
                    _statusIcon.Foreground  = Red;
                    _statusLabel.Text       = "Failed – will retry";
                    _statusLabel.Foreground = Red;
                    _bar.Visibility         = Visibility.Collapsed;
                    BorderBrush             = Red;
                    Background              = new SolidColorBrush(Color.FromRgb(0x22, 0x14, 0x14));
                    break;
            }
        });
    }
}
