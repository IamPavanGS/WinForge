using System;
using System.Diagnostics;
using SysPath = System.IO.Path;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class TestVmPage : UserControl
{
    public event Action<string, int>? NavigateRequested;

    // ── Hyper-V service ───────────────────────────────────────────────────────
    private readonly HyperVService _hv = HyperVService.Instance;

    // ── Telemetry ring buffers ────────────────────────────────────────────────
    private const int BufSize = 50;
    private readonly double[] _cpuBuf  = new double[BufSize];
    private readonly double[] _ramBuf  = new double[BufSize];
    private readonly double[] _diskBuf = new double[BufSize];
    private readonly double[] _netBuf  = new double[BufSize];
    private int _bufHead = 0;

    private double _cpuVal  = 0;
    private double _ramVal  = 0;
    private double _diskVal = 40.0;
    private double _netVal  = 120.0;

    private long _vmRamTotalMb = 8192;   // updated when VM starts

    private readonly Random _rng = new();

    // ── Timers ────────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _elapsedTimer;
    private DispatcherTimer?         _telemetryTimer;
    private int _elapsedSeconds = 0;

    // ── ISO path ──────────────────────────────────────────────────────────────
    private string _selectedIsoPath = string.Empty;

    // ── Screenshot timer for the in-page thumbnail preview ───────────────────
    // Interactive control happens via "Connect via RDP" / clicking the preview.
    private DispatcherTimer? _screenshotTimer;

    // Fixed capture size — Hyper-V re-rasterises whenever we change dimensions,
    // so a constant size avoids per-tick re-rasterisation jitter. The WPF Image
    // scales this to fit the available area via Stretch="Uniform".
    private const int CapW = 1024;
    private const int CapH = 768;

    // Reused frame buffer + WriteableBitmap so we don't allocate / replace
    // Image.Source every tick (previously caused a brief blink each frame).
    private System.Windows.Media.Imaging.WriteableBitmap? _previewBitmap;
    private byte[]? _previewBuffer;
    private bool _previewBusy;

    // ── Constructor ───────────────────────────────────────────────────────────
    public TestVmPage()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;

        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AddInitialLogEntries();
        CheckHyperVAvailability();
        TryAutoPopulateIso();
        SetVmUiState(VmState.Idle);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            _elapsedTimer.Start();
            // If the VM is already running (user navigated away and came back),
            // resume telemetry + preview.
            if (_hv.State == VmState.Running)
            {
                StartMetricsLoop();
                SetVmUiState(VmState.Running);
                if (_screenshotTimer == null && !string.IsNullOrEmpty(_hv.ActiveVmName))
                    TryAttachEmbed(_hv.ActiveVmName!);
            }
            else
            {
                // VM idle — re-check LastBuiltIsoPath so a freshly built ISO is
                // auto-populated when the user navigates to this page after a build.
                var last = BuildSession.Current?.LastBuiltIsoPath;
                if (!string.IsNullOrEmpty(last) && File.Exists(last) && _selectedIsoPath != last)
                {
                    SetIsoPath(last);
                    AppendLogRow("INFO", "#4D8EF8", $"Auto-selected freshly built ISO: {SysPath.GetFileName(last)}");
                }
            }
        }
        else
        {
            // Page hidden — pause timers and tear down embed (VM keeps running).
            _telemetryTimer?.Stop();
            _elapsedTimer.Stop();
            DisposeEmbed();
        }
    }

    // ── Hyper-V availability check ────────────────────────────────────────────
    private bool _hvAvailable = true;

    private void CheckHyperVAvailability()
    {
        _hvAvailable = _hv.IsHyperVAvailable();
        HvWarningBar.Visibility = _hvAvailable ? Visibility.Collapsed : Visibility.Visible;
        UpdateStartButtonEnabled();

        if (!_hvAvailable)
        {
            // Hide the specs bar entirely so the user isn't tempted to configure
            // controls that can't actually launch anything.
            VmSpecsBar.Visibility = Visibility.Collapsed;
            PlaceholderText.Text  = "Hyper-V is not enabled";
            PlaceholderSub.Text   = "Enable Hyper-V in Windows Features and restart your computer to use this feature.";
            AppendLogRow("WARN", "#F59E0B", "Hyper-V role is not enabled — VM features are unavailable");
        }
    }

    private void OpenWindowsFeatures_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "OptionalFeatures.exe",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLogRow("WARN", "#F05260", $"Could not open Windows Features: {ex.Message}");
        }
    }

    // ── UI state machine ──────────────────────────────────────────────────────
    private void SetVmUiState(VmState state)
    {
        bool idle     = state == VmState.Idle;
        bool creating = state == VmState.Creating;
        bool running  = state == VmState.Running;

        // Specs bar: visible only when idle AND Hyper-V is available
        VmSpecsBar.Visibility   = (idle && _hvAvailable) ? Visibility.Visible : Visibility.Collapsed;

        // Screen content
        VmEmbedHost.Visibility  = running ? Visibility.Visible   : Visibility.Collapsed;
        VmPlaceholder.Visibility= running ? Visibility.Collapsed : Visibility.Visible;
        VmInfoStrip.Visibility  = running ? Visibility.Visible   : Visibility.Collapsed;
        VmErrorOverlay.Visibility = Visibility.Collapsed;

        // Toolbar state pills
        VmRunningPill.Visibility  = running  ? Visibility.Visible : Visibility.Collapsed;
        VmCreatingPill.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        StopVmBtn.Visibility      = running  ? Visibility.Visible : Visibility.Collapsed;

        // Spec inputs
        RamCombo.IsEnabled       = idle;
        CpuCombo.IsEnabled       = idle;
        VtpmCheck.IsEnabled      = idle;
        SecureBootCheck.IsEnabled = idle;

        // Placeholder text
        if (idle)
        {
            PlaceholderText.Text = "Select an ISO and click  Start VM";
            PlaceholderSub.Text  = "Configure RAM, vCPU, and security options above, then click Start VM.";
            UpdateStartButtonEnabled();
        }
        else if (creating)
        {
            PlaceholderText.Text = "Creating VM…";
            PlaceholderSub.Text  = "This takes about 15–30 seconds. The screen will appear once the VM boots.";
        }
    }

    // ── Start VM ──────────────────────────────────────────────────────────────
    private async void StartVm_Click(object sender, RoutedEventArgs e)
    {
        // T1-F: guard against double-clicks / clicks while running
        if (_hv.State != VmState.Idle)
        {
            AppendLogRow("WARN", "#F59E0B", "A VM is already starting or running");
            return;
        }

        if (string.IsNullOrEmpty(_selectedIsoPath))
        {
            AppendLogRow("WARN", "#F59E0B", "No ISO selected — use Browse ISO first");
            return;
        }

        if (!File.Exists(_selectedIsoPath))
        {
            AppendLogRow("WARN", "#F59E0B", "ISO file not found — please select a valid path");
            return;
        }

        // T1-A: parse Tag strings safely (XAML stores Tag as string, direct cast crashes)
        long ramBytes;
        int  cpuCount;
        try
        {
            ramBytes = long.Parse(((ComboBoxItem)RamCombo.SelectedItem).Tag!.ToString()!);
            cpuCount = int.Parse(((ComboBoxItem)CpuCombo.SelectedItem).Tag!.ToString()!);
        }
        catch
        {
            AppendLogRow("WARN", "#F05260", "Invalid RAM/vCPU selection — please choose values from the dropdowns");
            return;
        }

        // T2-D: validate against host RAM (leave 2 GB for the host)
        long hostBytes = GetPhysicallyInstalledRamBytes();
        if (hostBytes > 0 && ramBytes > hostBytes - (2L * 1024 * 1024 * 1024))
        {
            AppendLogRow("WARN", "#F59E0B",
                $"Requested {ramBytes / 1073741824} GB exceeds usable host RAM " +
                $"({hostBytes / 1073741824} GB installed). Reduce RAM and try again.");
            return;
        }

        StartVmBtn.IsEnabled = false;
        SetVmUiState(VmState.Creating);
        _elapsedSeconds = 0;
        ElapsedText.Text = "00:00";
        _elapsedTimer.Start();

        _vmRamTotalMb = ramBytes / 1024 / 1024;

        var vmName = $"GIB-Test-{DateTime.Now:yyyyMMdd-HHmmss}";
        var config = new VmConfig(
            Name:             vmName,
            IsoPath:          _selectedIsoPath,
            CpuCount:         cpuCount,
            RamBytes:         ramBytes,
            EnableVtpm:       VtpmCheck.IsChecked == true,
            EnableSecureBoot: SecureBootCheck.IsChecked == true);

        AppendLogRow("INFO", "#4D8EF8", $"Creating VM: {vmName}  ({cpuCount} vCPU · {ramBytes / 1073741824} GB RAM)");
        AppendLogRow("INFO", "#4D8EF8", $"ISO: {SysPath.GetFileName(_selectedIsoPath)}");

        var result = await _hv.CreateAndStartAsync(config);

        if (!result.Success)
        {
            // T1-G: surface the actual PowerShell error to the user (one line per row)
            AppendLogRow("WARN", "#F05260", "VM creation failed");
            foreach (var line in (result.ErrorMessage ?? "(no details)")
                                  .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                AppendLogRow("WARN", "#F05260", line.TrimEnd('\r').Trim());
            }
            SetVmUiState(VmState.Idle);
            StartVmBtn.IsEnabled = true;
            return;
        }

        // Update info bar with real specs
        VmInfoBar.Text = $"{vmName}  ·  {cpuCount} vCPU  ·  {ramBytes / 1073741824} GB" +
                         (VtpmCheck.IsChecked == true ? "  ·  vTPM 2.0" : "") +
                         (SecureBootCheck.IsChecked == true ? "  ·  Secure Boot" : "");

        AppendLogRow("OK",   "#27C48A", "VM started — attaching console");
        AppendLogRow("INFO", "#4D8EF8", "Hyper-V integration services loading…");

        SetVmUiState(VmState.Running);
        StartMetricsLoop();
        TryAttachEmbed(vmName);
    }

    // Starts the WMI thumbnail polling loop and shows the embed area. The
    // preview is read-only by design — interactive keyboard/mouse happens via
    // "Connect via RDP" / clicking the preview, both of which launch the
    // dedicated vmconnect console window.
    private void TryAttachEmbed(string vmName)
    {
        VmEmbedHost.Visibility    = Visibility.Visible;
        VmErrorOverlay.Visibility = Visibility.Collapsed;
        StartScreenshotLoop();
        AppendLogRow("OK", "#27C48A", "VM preview attached — click the screen to open the console");
    }

    private void ShowEmbedError(string detail)
    {
        VmEmbedHost.Visibility    = Visibility.Collapsed;
        VmErrorDetail.Text        = string.IsNullOrEmpty(detail)
            ? "VM preview unavailable — the VM is still running."
            : detail;
        VmErrorOverlay.Visibility = Visibility.Visible;
    }

    private void DisposeEmbed()
    {
        _screenshotTimer?.Stop();
        _screenshotTimer = null;
        VmScreenImage.Source = null;
        _previewBitmap = null;
        _previewBuffer = null;
        _previewBusy   = false;
    }

    private void StartScreenshotLoop()
    {
        _screenshotTimer?.Stop();

        // Build the reused bitmap + buffer once. Bgr565 = 2 bytes per pixel —
        // matches the format Hyper-V's GetVirtualSystemThumbnailImage returns,
        // so the buffer can be copied straight into the bitmap.
        _previewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
            CapW, CapH, 96, 96, System.Windows.Media.PixelFormats.Bgr565, null);
        _previewBuffer = new byte[CapW * CapH * 2];
        _previewBusy   = false;
        VmScreenImage.Source = _previewBitmap;

        _screenshotTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _screenshotTimer.Tick += async (_, _) =>
        {
            if (_previewBusy || _previewBitmap == null || _previewBuffer == null) return;
            _previewBusy = true;
            try
            {
                var buf = _previewBuffer;
                bool ok = await System.Threading.Tasks.Task.Run(
                    () => _hv.TryCaptureBgr565(CapW, CapH, buf));
                if (ok && _previewBitmap != null)
                {
                    _previewBitmap.WritePixels(
                        new System.Windows.Int32Rect(0, 0, CapW, CapH),
                        buf, CapW * 2, 0);
                }
            }
            finally { _previewBusy = false; }
        };
        _screenshotTimer.Start();
    }

    // Click anywhere in the preview opens the dedicated vmconnect console
    // window for full interactive control (keyboard, mouse, clipboard).
    private void VmEmbedHost_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hv.State != VmState.Running) return;
        AppendLogRow("INFO", "#4D8EF8", "Opening VM console for interactive control…");
        _hv.LaunchVmConnect();
    }

    private void ReconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hv.State != VmState.Running || string.IsNullOrEmpty(_hv.ActiveVmName))
        {
            AppendLogRow("WARN", "#F59E0B", "No running VM to reconnect to");
            VmErrorOverlay.Visibility = Visibility.Collapsed;
            SetVmUiState(VmState.Idle);
            return;
        }
        AppendLogRow("INFO", "#4D8EF8", "Reconnecting to VM console…");
        TryAttachEmbed(_hv.ActiveVmName!);
    }

    // T2-D helper
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    private static long GetPhysicallyInstalledRamBytes()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out long kb))
                return kb * 1024L;
        }
        catch { /* ignored */ }
        return 0;
    }

    // ── Metrics loop (1 s) ────────────────────────────────────────────────────
    private void StartMetricsLoop()
    {
        _telemetryTimer?.Stop();
        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += async (_, _) =>
        {
            var metrics = await System.Threading.Tasks.Task.Run(() => _hv.GetMetrics());

            // All four metrics now read from Hyper-V WMI counters (HyperVService.GetMetrics).
            _cpuVal  = metrics.CpuPercent;
            _ramVal  = _vmRamTotalMb > 0
                ? Math.Clamp((double)metrics.RamUsedMb / _vmRamTotalMb * 100, 0, 100)
                : 0;
            _diskVal = Clamp(metrics.DiskBytesPerSec   / 1_048_576.0, 0, 150);    // bytes → MB/s
            _netVal  = Clamp(metrics.NetworkBitsPerSec / 1_000_000.0, 0, 1000);   // bits  → Mbps (decimal)

            _cpuBuf[_bufHead]  = _cpuVal;
            _ramBuf[_bufHead]  = _ramVal;
            _diskBuf[_bufHead] = _diskVal;
            _netBuf[_bufHead]  = _netVal;
            _bufHead = (_bufHead + 1) % BufSize;

            CpuValueText.Text  = $"{_cpuVal:F0}%";
            RamValueText.Text  = $"{_ramVal:F0}%";
            DiskValueText.Text = $"{_diskVal:F0} MB/s";
            NetValueText.Text  = $"{_netVal:F0} Mbps";

            UpdateSparkline(CpuCanvas,  CpuLine,  _cpuBuf,  _bufHead, 0, 100);
            UpdateSparkline(RamCanvas,  RamLine,  _ramBuf,  _bufHead, 0, 100);
            UpdateSparkline(DiskCanvas, DiskLine, _diskBuf, _bufHead, 0, 150);
            UpdateSparkline(NetCanvas,  NetLine,  _netBuf,  _bufHead, 0, 1000);
        };
        _telemetryTimer.Start();
    }

    // ── Sparkline rendering ───────────────────────────────────────────────────
    private static void UpdateSparkline(Canvas canvas, Polyline line,
                                        double[] buf, int head,
                                        double minVal, double maxVal)
    {
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 2 || h < 2) return;

        int    n     = buf.Length;
        double xStep = w / (n - 1);
        double range = maxVal - minVal;
        if (range <= 0) range = 1;

        var pts = new System.Windows.Media.PointCollection(n);
        for (int i = 0; i < n; i++)
        {
            int    idx        = (head + i) % n;
            double normalized = Math.Clamp((buf[idx] - minVal) / range, 0, 1);
            pts.Add(new System.Windows.Point(i * xStep, h - normalized * h));
        }
        line.Points = pts;
    }

    // ── Elapsed timer ─────────────────────────────────────────────────────────
    private void ElapsedTimer_Tick(object? sender, EventArgs e)
    {
        _elapsedSeconds++;
        ElapsedText.Text = TimeSpan.FromSeconds(_elapsedSeconds).ToString(@"mm\:ss");
    }

    // ── Event log ─────────────────────────────────────────────────────────────
    private void AddInitialLogEntries()
    {
        AppendLogRow("INFO", "#4D8EF8", "Test in VM ready — select an ISO and configure VM specs");
    }

    private void AppendLogRow(string level, string hexColor, string message)
    {
        var accentColor = (Color)ColorConverter.ConvertFromString(hexColor);

        var timeText = new TextBlock
        {
            Text              = DateTime.Now.ToString("HH:mm:ss"),
            FontFamily        = new FontFamily("Consolas, Cascadia Code, Courier New"),
            FontSize          = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth          = 55
        };
        timeText.SetResourceReference(TextBlock.ForegroundProperty, "FG3Brush");

        var levelBorder = new Border
        {
            CornerRadius      = new CornerRadius(3),
            Padding           = new Thickness(5, 1, 5, 1),
            Margin            = new Thickness(5, 0, 6, 0),
            Background        = new SolidColorBrush(Color.FromArgb(0x22, accentColor.R, accentColor.G, accentColor.B)),
            BorderBrush       = new SolidColorBrush(Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness   = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = level,
                FontSize   = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor)
            }
        };

        var msgText = new TextBlock
        {
            Text              = message,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping      = TextWrapping.Wrap
        };
        msgText.SetResourceReference(TextBlock.ForegroundProperty, "FG1Brush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(timeText);
        row.Children.Add(levelBorder);
        row.Children.Add(msgText);

        EventLogPanel.Children.Add(row);

        while (EventLogPanel.Children.Count > 50)
            EventLogPanel.Children.RemoveAt(0);

        EventLogScrollViewer.ScrollToBottom();
    }

    // ── ISO picker ────────────────────────────────────────────────────────────
    private void TryAutoPopulateIso()
    {
        var last = BuildSession.Current?.LastBuiltIsoPath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
            SetIsoPath(last);
    }

    private void SetIsoPath(string path)
    {
        _selectedIsoPath = path;
        IsoPathBox.Text  = path;
        IsoPathBox.ToolTip = path;
        UpdateStartButtonEnabled();
    }

    private void UpdateStartButtonEnabled()
    {
        StartVmBtn.IsEnabled =
            _hvAvailable
            && _hv.State == VmState.Idle
            && !string.IsNullOrEmpty(_selectedIsoPath)
            && File.Exists(_selectedIsoPath);

        StartVmBtn.ToolTip = StartVmBtn.IsEnabled
            ? null
            : (!_hvAvailable          ? "Hyper-V is not enabled"
            :  _hv.State != VmState.Idle ? "A VM is already running"
            :                             "Select an ISO first to enable this button");
    }


    private void BrowseIso_Click(object sender, RoutedEventArgs e) => OpenIsoPicker();
    private void IsoPathBox_Click(object sender, MouseButtonEventArgs e) => OpenIsoPicker();

    private void OpenIsoPicker()
    {
        var dlg = new OpenFileDialog
        {
            Title           = "Select ISO to test in Hyper-V",
            Filter          = "ISO Images (*.iso)|*.iso|All Files (*.*)|*.*",
            DefaultExt      = ".iso",
            CheckFileExists = true
        };

        var seed = _selectedIsoPath;
        if (string.IsNullOrEmpty(seed)) seed = BuildSession.Current?.OutputPath;
        if (!string.IsNullOrEmpty(seed))
        {
            var dir = SysPath.GetDirectoryName(seed);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true)
        {
            SetIsoPath(dlg.FileName);
            AppendLogRow("INFO", "#4D8EF8", $"ISO selected: {SysPath.GetFileName(dlg.FileName)}");
        }
    }

    // ── Cleanup (called from MainWindow on app close) ─────────────────────────
    public void CleanupVmOnClose()
    {
        _telemetryTimer?.Stop();
        _elapsedTimer.Stop();

        // Dispose embed before stopping the VM — killing the VM out from under
        // a live vmconnect can spawn a Watson error dialog.
        DisposeEmbed();

        // T1-D: block up to 8 seconds so the VM is actually torn down before the
        // process exits. Without this wait, the PowerShell child orphans the VM.
        try
        {
            var task = _hv.StopAndDeleteAsync("app closing — CleanupVmOnClose");
            task.Wait(TimeSpan.FromSeconds(8));
        }
        catch { /* best-effort */ }
    }

    // ── Button handlers ───────────────────────────────────────────────────────
    private void BackToBuild_Click(object sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("progress", 0);

    private void VmReset_Click(object sender, RoutedEventArgs e)
    {
        _elapsedSeconds = 0;
        ElapsedText.Text = "00:00";
        AppendLogRow("WARN", "#F59E0B", "VM reset requested");
        _ = _hv.ResetAsync();
    }

    private async void VmCtrlAltDel_Click(object sender, RoutedEventArgs e)
    {
        if (_hv.State != VmState.Running)
        {
            AppendLogRow("WARN", "#F59E0B", "VM is not running — Ctrl+Alt+Del ignored");
            return;
        }
        AppendLogRow("INFO", "#4D8EF8", "Sending Ctrl+Alt+Del to guest…");
        await _hv.SendCtrlAltDelAsync();
    }

    private async void VmSnapshot_Click(object sender, RoutedEventArgs e)
    {
        AppendLogRow("INFO", "#4D8EF8", "Creating snapshot…");
        var name = await _hv.CreateSnapshotAsync();
        AppendLogRow("OK", "#27C48A", $"Snapshot created: {name}");
    }

    private void VmFullscreen_Click(object sender, RoutedEventArgs e)
        => _hv.LaunchVmConnect();

    private async void StopVm_Click(object sender, RoutedEventArgs e)
    {
        if (_hv.State != VmState.Running) return;

        // Destructive action — the VM and its VHDX go away. Confirm so a
        // misclick on a long-running test doesn't wipe the work.
        if (!AppDialog.Confirm(this,
                "Stop and delete this test VM?\n\n" +
                "The VM and its virtual disk (.vhdx) will be removed. Any " +
                "changes made inside the VM will be lost.",
                "Confirm Stop & Delete"))
            return;

        AppendLogRow("INFO", "#4D8EF8", "Stopping VM…");
        _telemetryTimer?.Stop();
        DisposeEmbed();
        await _hv.StopAndDeleteAsync("user clicked Stop VM");
        AppendLogRow("OK", "#27C48A", "VM stopped and removed");
        SetVmUiState(VmState.Idle);
        _elapsedSeconds = 0;
        ElapsedText.Text = "00:00";
        _elapsedTimer.Stop();
        UpdateStartButtonEnabled();
    }

    private void VmRdp_Click(object sender, RoutedEventArgs e)
    {
        AppendLogRow("INFO", "#4D8EF8", "Opening VM console…");
        _hv.LaunchVmConnect();
    }

    // ── Utility ───────────────────────────────────────────────────────────────
    private static double Clamp(double v, double lo, double hi)
        => v < lo ? lo : v > hi ? hi : v;
}
