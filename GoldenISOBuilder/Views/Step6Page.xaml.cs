using System.Diagnostics;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;  // ToggleButton
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoldenISOBuilder.Helpers;   // AppNotifier, ToastHelper, ToastKind, AppSettingsLoader
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;

namespace GoldenISOBuilder.Views;

public partial class Step6Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    /// <summary>Raised after any build attempt completes (success or failure).
    /// MainWindow listens to this to refresh the Welcome page stats.</summary>
    public event Action? BuildCompleted;

    private CancellationTokenSource? _cts;
    private Task<BuildResult>? _runTask;
    private DateTime _startedAt;
    private DispatcherTimerStub _timer;
    private readonly StringBuilder _logBuf = new();
    private bool _started = false;

    // We rebuild the pipeline list each time so the Step6 visualisation reflects
    // exactly what the engine plans to do for this run.
    private readonly Dictionary<string, Border> _stepRows = new();

    public Step6Page()
    {
        InitializeComponent();
        // Use IsVisibleChanged rather than Loaded — Loaded only fires the first
        // time the control is added to the visual tree, but our page is added
        // once and shown/hidden repeatedly via Visibility flags.
        IsVisibleChanged += OnIsVisibleChanged;
        _timer = new DispatcherTimerStub(Dispatcher, TimeSpan.FromSeconds(1), UpdateElapsed);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool nowVisible = (bool)e.NewValue;
        if (nowVisible)
        {
            if (!_started)
            {
                // Only auto-start if we have a valid build session (i.e., user clicked "Build ISO"
                // and has configured Step 1 at minimum — prevents accidental trigger from nav rail)
                if (BuildSession.Current.SelectedImage != null &&
                    !string.IsNullOrEmpty(BuildSession.Current.SourceIsoPath))
                {
                    _started = true;
                    StartBuild();
                }
            }
            else if (_runTask != null && !_runTask.IsCompleted)
            {
                // User navigated away and came back while the build is still running.
                // The build never paused — just restart the elapsed-time timer so the
                // counter resumes from the correct value (UpdateElapsed reads DateTime.Now
                // - _startedAt, so it will snap to the real elapsed time on the next tick).
                _timer.Start();
            }
            // If the build already finished, leave everything as-is (completion panel visible).
        }
        else
        {
            // Page hidden — pause the visual timer but keep the build running.
            _timer.Stop();
        }
    }

    // ── Start build ───────────────────────────────────────────────────────────

    private void StartBuild()
    {
        // Hide any leftover completion / failure panels from a previous run
        CompletionPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility    = Visibility.Collapsed;

        // Update header to reflect an active build
        PageHeaderSubtitle.Text = "Building golden image…";

        // Reset footer buttons: show Cancel, hide wizard/new-build button
        CancelBtn.Visibility   = Visibility.Visible;
        NewBuildBtn.Visibility = Visibility.Collapsed;

        // Reset all UI status fields
        OverallBar.Value = 0;
        OverallPct.Text  = "0%";
        StepBar.Value    = 0;
        ElapsedLabel.Text = "0:00";
        StepCountLabel.Text = "0 / 0";
        CenterTitle.Text    = "Starting build…";
        CenterSubtitle.Text = "Initialising pipeline…";
        CineTitle.Text      = "Starting build…";
        CineSubtitle.Text   = "Initialising pipeline…";
        CineBar.Value       = 0;
        CineElapsed.Text    = "0:00";
        CineStepCount.Text  = "0 / 0";
        CinePct.Text        = "0%";

        _cts = new CancellationTokenSource();
        _startedAt = DateTime.Now;
        _logBuf.Clear();
        _warnBuf.Clear();
        _errBuf.Clear();
        LogBoxWarn.Clear();
        LogBoxError.Clear();

        // Reset to "All" tab so the user sees live output from the start
        _activeLogTab = "all";
        LogTabAll.IsChecked   = true;
        LogTabWarn.IsChecked  = false;
        LogTabError.IsChecked = false;
        LogBoxAll.Visibility   = Visibility.Visible;
        LogBoxWarn.Visibility  = Visibility.Collapsed;
        LogBoxError.Visibility = Visibility.Collapsed;

        AppendLog("Starting build pipeline…");
        StartDiscSpin();

        var engine = new BuildEngine(
            BuildSession.Current,
            // Use Invoke (blocking) for progress so pipeline UI stays in sync with engine state.
            onProgress: p => Dispatcher.Invoke(() => OnProgress(p)),
            // Use BeginInvoke (fire-and-forget) for log lines so the background thread never
            // blocks waiting for the UI — this prevents potential Dispatcher queue buildup
            // during heavy operations like deleting gigabytes of staging files.
            onLog:      m => Dispatcher.BeginInvoke(() => AppendLog(m)),
            ct:         _cts.Token);

        BuildPipelineUi(engine.Steps);

        _timer.Start();
        _runTask = Task.Run(() => engine.RunAsync());

        // Successful completion (even if build reported failure internally)
        _ = _runTask.ContinueWith(t => Dispatcher.Invoke(() => OnBuildComplete(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);

        // Task faulted (unhandled exception escaped RunAsync — should be rare but handle it)
        _ = _runTask.ContinueWith(t => Dispatcher.Invoke(() => OnBuildFaulted(t.Exception)),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ── Pipeline list rendering ───────────────────────────────────────────────

    private void BuildPipelineUi(IReadOnlyList<BuildStep> steps)
    {
        // Find the StackPanel inside the existing pipeline ScrollViewer and replace its children
        var scroller = (ScrollViewer)((Border)PipelineView.Children[0]).Child;
        var panel    = (StackPanel)scroller.Content;
        panel.Children.Clear();
        _stepRows.Clear();

        var header = new TextBlock
        {
            Text       = "PIPELINE",
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 12)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");
        panel.Children.Add(header);

        foreach (var s in steps)
            _stepRows[s.Id] = AppendPipelineRow(panel, s);

        StepCountLabel.Text = $"0 / {steps.Count}";
    }

    private Border AppendPipelineRow(StackPanel panel, BuildStep step)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(12, 10, 12, 10),
            Margin       = new Thickness(0, 0, 0, 6),
            Opacity      = 0.6,
            Tag          = step.Id
        };
        // DynamicResource so rows re-render when the user switches theme mid-session
        row.SetResourceReference(Border.BackgroundProperty, "BG1Brush");

        var dot = new Border
        {
            Width           = 14, Height = 14,
            CornerRadius    = new CornerRadius(7),
            BorderThickness = new Thickness(1)
        };
        dot.SetResourceReference(Border.BackgroundProperty,  "BG3Brush");
        dot.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var label = new TextBlock
        {
            Text              = "  " + step.Title,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(dot);
        sp.Children.Add(label);
        row.Child = sp;
        panel.Children.Add(row);
        return row;
    }

    private void UpdatePipelineRow(BuildStep step)
    {
        if (!_stepRows.TryGetValue(step.Id, out var row)) return;

        var sp    = (StackPanel)row.Child;
        var dot   = (Border)sp.Children[0];
        var label = (TextBlock)sp.Children[1];

        // Track prior state so we only pulse on real transitions
        var prevStatus = (BuildStepStatus?)row.Tag2();

        switch (step.Status)
        {
            case BuildStepStatus.Running:
                row.Opacity         = 1.0;
                row.BorderThickness = new Thickness(1);
                row.SetResourceReference(Border.BackgroundProperty,  "BG3Brush");
                row.SetResourceReference(Border.BorderBrushProperty, "Gold1Brush");
                dot.SetResourceReference(Border.BackgroundProperty,  "Gold1Brush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Gold1Brush");
                if (prevStatus != BuildStepStatus.Running)
                {
                    var fadeIn = new DoubleAnimation(0.3, 1.0,
                        new Duration(TimeSpan.FromMilliseconds(180)));
                    row.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                break;

            case BuildStepStatus.Done:
                row.BorderThickness = new Thickness(0);
                row.SetResourceReference(Border.BackgroundProperty, "BG1Brush");
                dot.SetResourceReference(Border.BackgroundProperty,  "OkBrush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "OkBrush");
                if (prevStatus == BuildStepStatus.Running) PulseRow(row);
                else row.Opacity = 1.0;
                break;

            case BuildStepStatus.Skipped:
                row.Opacity = 0.55;
                dot.SetResourceReference(Border.BackgroundProperty,     "FG3Brush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "FG2Brush");
                break;

            case BuildStepStatus.Failed:
                row.BorderThickness = new Thickness(1);
                row.SetResourceReference(Border.BackgroundProperty,  "BG1Brush");
                row.SetResourceReference(Border.BorderBrushProperty, "ErrBrush");
                dot.SetResourceReference(Border.BackgroundProperty,  "ErrBrush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
                if (prevStatus == BuildStepStatus.Running) PulseRow(row);
                else row.Opacity = 1.0;
                break;
        }

        // Store status in the row Tag for next-transition comparison
        row.SetTag2(step.Status);
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    private void OnProgress(BuildProgress p)
    {
        UpdatePipelineRow(p.CurrentStep);
        // Also flip earlier completed/skipped steps
        foreach (var s in p.AllSteps)
            if (s.Status != BuildStepStatus.Pending)
                UpdatePipelineRow(s);

        // Pipeline-view labels
        CenterTitle.Text    = p.CurrentStep.Title;
        CenterSubtitle.Text = p.Message;

        int done = p.AllSteps.Count(s =>
            s.Status == BuildStepStatus.Done || s.Status == BuildStepStatus.Skipped);
        int pct  = (int)Math.Round((double)done / p.TotalSteps * 100);
        OverallBar.Value = pct;
        StepBar.Value    = (p.CurrentStep.Status == BuildStepStatus.Running ? 50 : 100);
        OverallPct.Text  = $"{pct}%";
        StepCountLabel.Text = $"{done} / {p.TotalSteps}";

        // Cinematic-view labels (mirror the pipeline so user sees same info)
        CineTitle.Text     = p.CurrentStep.Title;
        CineSubtitle.Text  = p.Message;
        CineBar.Value      = pct;
        CineStepCount.Text = $"{done} / {p.TotalSteps}";
        CinePct.Text       = $"{pct}%";
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    // ── Log tab switching ─────────────────────────────────────────────────────

    private string _activeLogTab = "all";   // "all" | "warn" | "error"
    private readonly StringBuilder _warnBuf = new();
    private readonly StringBuilder _errBuf  = new();

    private void LogTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        _activeLogTab = btn.Tag?.ToString() ?? "all";

        LogTabAll.IsChecked   = _activeLogTab == "all";
        LogTabWarn.IsChecked  = _activeLogTab == "warn";
        LogTabError.IsChecked = _activeLogTab == "error";

        LogBoxAll.Visibility   = _activeLogTab == "all"   ? Visibility.Visible : Visibility.Collapsed;
        LogBoxWarn.Visibility  = _activeLogTab == "warn"  ? Visibility.Visible : Visibility.Collapsed;
        LogBoxError.Visibility = _activeLogTab == "error" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsErrorLine(string line)
    {
        // Lines starting with ✓ are always success — never classify as error regardless of content.
        // e.g. "✓ Valid XML structure — Parsed without errors." contains " err" but is a pass.
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("✓")) return false;

        // DISM progress lines: "Successfully processed 1 files; Failed processing 0 files" — not errors.
        if (line.Contains("Failed processing 0 files", StringComparison.OrdinalIgnoreCase))
            return false;

        // " ERR" is too broad — it matches "errors", "error handling", etc. embedded in normal words.
        // Require it to be followed by a space, colon, digit, or end-of-string so it's a standalone token.
        bool hasErrToken = false;
        int idx = 0;
        while ((idx = line.IndexOf(" err", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = idx + 4; // position after " err"
            if (after >= line.Length || line[after] == ' ' || line[after] == ':' ||
                char.IsDigit(line[after]) || line[after] == ']' || line[after] == ')')
            {
                hasErrToken = true;
                break;
            }
            idx++;
        }

        return
            line.Contains("[FATAL]",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ERROR]",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("FAILED",     StringComparison.OrdinalIgnoreCase) ||
            (hasErrToken && !line.Contains("ERRORLEVEL")) ||
            line.Contains("✗",          StringComparison.Ordinal);
    }

    private static bool IsWarningLine(string line) =>
        line.Contains("[WARN]",   StringComparison.OrdinalIgnoreCase) ||
        line.Contains("WARN",     StringComparison.OrdinalIgnoreCase) ||
        line.Contains("! ",       StringComparison.Ordinal) ||
        line.Contains("Skipping", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Skipped",  StringComparison.OrdinalIgnoreCase);

    private void AppendLog(string line)
    {
        _logBuf.AppendLine(line);

        // AppendText() is O(1) — only inserts new chars, never re-renders existing.
        // Use \r\n (Windows line endings) so the TextBox always renders breaks correctly.
        const int maxBytes = 80_000;
        const string nl = "\r\n";

        if (_logBuf.Length > maxBytes)
        {
            TrimAndReset(_logBuf, LogBoxAll);
            TrimAndReset(_warnBuf, LogBoxWarn);
            TrimAndReset(_errBuf,  LogBoxError);
        }
        else
        {
            LogBoxAll.AppendText(line + nl);
        }
        LogBoxAll.ScrollToEnd();

        // Classify and mirror to secondary tabs
        if (IsErrorLine(line))
        {
            AppendToTab(_errBuf,  LogBoxError, line, nl, maxBytes);
            AppendToTab(_warnBuf, LogBoxWarn,  line, nl, maxBytes);   // errors show in Warn tab too
        }
        else if (IsWarningLine(line))
        {
            AppendToTab(_warnBuf, LogBoxWarn, line, nl, maxBytes);
        }
    }

    private static void AppendToTab(StringBuilder buf, TextBox box, string line, string nl, int maxBytes)
    {
        buf.AppendLine(line);
        if (buf.Length <= maxBytes) box.AppendText(line + nl);
        else { TrimAndReset(buf, box); }
        box.ScrollToEnd();
    }

    // StringBuilder is a class (reference type) — no 'ref' needed.
    // Calling Clear() / AppendLine() on the object mutates it in place.
    private static void TrimAndReset(StringBuilder buf, TextBox box)
    {
        var lines = buf.ToString().Split('\n');
        buf.Clear();
        foreach (var l in lines.Skip(Math.Max(0, lines.Length - 400)))
            buf.AppendLine(l);
        box.Text = buf.ToString();
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTime.Now - _startedAt;
        var text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
        ElapsedLabel.Text = text;
        CineElapsed.Text  = text;
    }

    // ── Completion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Swaps footer buttons: hides Cancel (no longer relevant) and shows
    /// "Start New Build →" so the technician can kick off another run.
    /// </summary>
    private void ShowBuildCompleteButtons()
    {
        CancelBtn.Visibility    = Visibility.Collapsed;
        NewBuildBtn.Visibility  = Visibility.Visible;
        NewBuildBtn.Content     = "Start New Build →";
        PageHeaderSubtitle.Text = "Build complete";
    }

    private void OnBuildComplete(BuildResult r)
    {
        _timer.Stop();
        StopDiscSpin();   // halt the spinning disc icon
        ShowBuildCompleteButtons();

        if (r.Success)
        {
            string shaPreview = !string.IsNullOrEmpty(r.Sha256) && r.Sha256.Length >= 16
                ? r.Sha256[..16] + "…"
                : r.Sha256 ?? "—";

            string subtitle =
                $"{System.IO.Path.GetFileName(r.IsoPath)}  ·  " +
                $"{r.IsoSizeBytes / (1024.0 * 1024 * 1024):F2} GB  ·  " +
                $"SHA-256: {shaPreview}";

            CenterTitle.Text    = "✓ Build complete";
            CenterSubtitle.Text = subtitle;
            CineTitle.Text      = "✓ Build complete";
            CineSubtitle.Text   = subtitle;
            OverallBar.Value    = 100;
            OverallPct.Text     = "100%";
            CineBar.Value       = 100;
            CinePct.Text        = "100%";

            FailurePanel.Visibility = Visibility.Collapsed;
            FinalIsoPath.Text       = r.IsoPath;
            AnimateIn(CompletionPanel);

            // Windows toast — shows even if the user has alt-tabbed away
            string sizeStr = $"{r.IsoSizeBytes / (1024.0 * 1024 * 1024):F2} GB";
            AppNotifier.Show(
                "ISO Build Complete ✓",
                $"{System.IO.Path.GetFileName(r.IsoPath)}  ·  {sizeStr}",
                ToastKind.Success);

            // Audible alert so the user knows it's done without watching the screen
            if (AppSettingsLoader.ReadSoundOnComplete())
                SystemSounds.Asterisk.Play();

            BuildCompleted?.Invoke();
        }
        else
        {
            string err = r.Error ?? "Unknown error";
            CenterTitle.Text    = "✗ Build failed";
            CenterSubtitle.Text = err;
            CineTitle.Text      = "✗ Build failed";
            CineSubtitle.Text   = err;

            CompletionPanel.Visibility = Visibility.Collapsed;
            FailureDetail.Text   = err;
            FailureLogPath.Text  = r.LogPath ?? "";
            AnimateIn(FailurePanel);

            // Toast for failure too — user may have stepped away during a long build
            string shortErr = err.Length > 120 ? err[..117] + "…" : err;
            AppNotifier.Show(
                "ISO Build Failed ✗",
                shortErr,
                ToastKind.Failure);

            if (AppSettingsLoader.ReadSoundOnComplete())
                SystemSounds.Exclamation.Play();

            BuildCompleted?.Invoke();   // failure also records to history
        }
    }

    /// <summary>
    /// Called when RunAsync() itself throws an unhandled exception (very rare —
    /// the engine wraps steps internally). Stops the timer and surfaces the error.
    /// </summary>
    private void OnBuildFaulted(AggregateException? ex)
    {
        _timer.Stop();
        StopDiscSpin();
        ShowBuildCompleteButtons();
        var msg = ex?.InnerException?.Message ?? ex?.Message ?? "An unexpected error ended the build.";
        CenterTitle.Text    = "✗ Build failed";
        CenterSubtitle.Text = msg;
        CineTitle.Text      = "✗ Build failed";
        CineSubtitle.Text   = msg;
        AppendLog($"[FATAL] {msg}");
        CompletionPanel.Visibility = Visibility.Collapsed;
        FailureDetail.Text         = msg;
        FailureLogPath.Text        = BuildSession.Current.LastBuildLogPath ?? "";
        AnimateIn(FailurePanel);
        AppNotifier.Show("ISO Build Failed ✗", msg, ToastKind.Failure);
        if (AppSettingsLoader.ReadSoundOnComplete())
            SystemSounds.Exclamation.Play();
        BuildCompleted?.Invoke();
    }

    // ── Animation helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Fades and slides a panel into view — gives a polished entrance instead of
    /// the element just snapping to Visible instantly.
    /// </summary>
    private static void AnimateIn(FrameworkElement el, double slideFromY = 16)
    {
        el.Opacity = 0;

        // Reuse the TranslateTransform already declared in XAML; create one if
        // the element has no transform (defensive).
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform(0, slideFromY);
            el.RenderTransform = tt;
        }
        else
        {
            tt.Y = slideFromY;
        }

        el.Visibility = Visibility.Visible;

        var dur  = new Duration(TimeSpan.FromMilliseconds(300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade  = new DoubleAnimation(0, 1, dur);
        var slide = new DoubleAnimation(slideFromY, 0, dur) { EasingFunction = ease };

        el.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Pulses a quick opacity flash on a pipeline row when it transitions to
    /// a new terminal state (Done / Failed) to draw the user's eye.
    /// </summary>
    private static void PulseRow(Border row)
    {
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        row.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    /// <summary>Starts both disc animations from code (reliable on every build start).</summary>
    private void StartDiscSpin()
    {
        // Pipeline-view disc — 3s per revolution
        var spin1 = new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(3)))
        { RepeatBehavior = RepeatBehavior.Forever };
        CenterDiscRotation?.BeginAnimation(RotateTransform.AngleProperty, spin1);

        // Cinematic-view disc — slightly slower for a cinematic feel
        var spin2 = new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(4)))
        { RepeatBehavior = RepeatBehavior.Forever };
        DiscRotation?.BeginAnimation(RotateTransform.AngleProperty, spin2);
    }

    /// <summary>Freezes both disc icons at their current angle when the build ends.</summary>
    private void StopDiscSpin()
    {
        var a1 = CenterDiscRotation?.Angle ?? 0;
        CenterDiscRotation?.BeginAnimation(RotateTransform.AngleProperty, null);
        if (CenterDiscRotation != null) CenterDiscRotation.Angle = a1;

        var a2 = DiscRotation?.Angle ?? 0;
        DiscRotation?.BeginAnimation(RotateTransform.AngleProperty, null);
        if (DiscRotation != null) DiscRotation.Angle = a2;
    }

    // ── Buttons ──────────────────────────────────────────────────────────────

    private void PipelineView_Click(object sender, RoutedEventArgs e)
    {
        PipelineView.Visibility   = Visibility.Visible;
        CinematicView.Visibility  = Visibility.Collapsed;
        PipelineViewBtn.IsChecked = true;
        CinematicViewBtn.IsChecked= false;
    }

    private void CinematicView_Click(object sender, RoutedEventArgs e)
    {
        PipelineView.Visibility   = Visibility.Collapsed;
        CinematicView.Visibility  = Visibility.Visible;
        PipelineViewBtn.IsChecked = false;
        CinematicViewBtn.IsChecked= true;
    }

    private void CancelBuild_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Confirm(this, "Cancel the running build?", "Confirm Cancel"))
            _cts?.Cancel();
    }

    private void NewBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_runTask is { IsCompleted: false })
        {
            if (!AppDialog.Confirm(this,
                    "A build is in progress. Cancel and start a new one?", "Confirm"))
                return;
            _cts?.Cancel();
        }
        _started = false;
        NavigateRequested?.Invoke("wizard", 0);
    }

    private void OpenIsoLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var iso = BuildSession.Current.LastBuiltIsoPath;
            if (!string.IsNullOrEmpty(iso) && System.IO.File.Exists(iso))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{iso}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 1. Prefer the real log file written by BuildEngine
            var p = BuildSession.Current.LastBuildLogPath;
            if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
            {
                OpenWithNotepad(p); return;
            }

            // 2. Scan the output folder for any build-*.log files (handles the case
            //    where LastBuildLogPath wasn't set — e.g. very fast failure)
            if (!string.IsNullOrEmpty(BuildSession.Current.OutputPath))
            {
                var outDir = BuildSession.Current.OutputPath;
                if (System.IO.Directory.Exists(outDir))
                {
                    var found = System.IO.Directory.GetFiles(outDir, "build-*.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault();
                    if (found != null)
                    {
                        BuildSession.Current.LastBuildLogPath = found;   // pin it
                        OpenWithNotepad(found); return;
                    }
                }
            }

            // 3. Fall back: write the in-memory buffer to a temp file and open that
            if (_logBuf.Length > 0)
            {
                var tmp = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"GIB-log-{DateTime.Now:HHmmss}.txt");
                System.IO.File.WriteAllText(tmp, _logBuf.ToString());
                OpenWithNotepad(tmp); return;
            }

            AppDialog.Alert(this,
                "No log file is available yet. Start a build first.",
                "Log Not Available", AppDialogIcon.Info);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Could not open log:\n{ex.Message}", "Error", AppDialogIcon.Error);
        }
    }

    private static void OpenWithNotepad(string path)
        => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        // Copy whichever tab is currently visible
        var text = _activeLogTab switch
        {
            "warn"  => _warnBuf.ToString(),
            "error" => _errBuf.ToString(),
            _       => _logBuf.ToString()
        };
        try { Clipboard.SetText(text.Length > 0 ? text : "No log content."); } catch { }
    }
}

// ── Small attached-property shim to store pipeline-row prior status ──────────
// Using Tag would overwrite the step ID that's already stored there, so we use
// the FrameworkElement's Tag for step.Id and a lightweight Dictionary instead.

internal static class RowStateStore
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, StepStatusBox>
        _table = new();

    public static BuildStepStatus? Tag2(this Border b)
        => _table.TryGetValue(b, out var box) ? box.Value : null;

    public static void SetTag2(this Border b, BuildStepStatus s)
        => _table.GetOrCreateValue(b).Value = s;

    private sealed class StepStatusBox { public BuildStepStatus Value; }
}

// Minimal DispatcherTimer wrapper (kept here so a single file change is enough)
internal class DispatcherTimerStub
{
    private readonly System.Windows.Threading.DispatcherTimer _t;
    public DispatcherTimerStub(System.Windows.Threading.Dispatcher d, TimeSpan interval, Action tick)
    {
        _t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, d)
        {
            Interval = interval
        };
        _t.Tick += (_, _) => tick();
    }
    public void Start() => _t.Start();
    public void Stop()  => _t.Stop();
}
