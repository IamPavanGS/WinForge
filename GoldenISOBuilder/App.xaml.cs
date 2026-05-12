using System.IO;
using System.Windows;
using System.Windows.Threading;
using GoldenISOBuilder.Helpers;

namespace GoldenISOBuilder;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Surface any unhandled UI exception via a MessageBox + log file.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException     += OnUnobservedTaskException;

        base.OnStartup(e);   // no StartupUri — window creation is manual below

        // ── Show splash immediately ──────────────────────────────────────────
        var splash = new SplashWindow();
        splash.Show();

        // ── Synchronous initialisation (fast, but give splash time to paint) ─
        splash.SetStatus("Registering notifications…");
        ToastHelper.Initialize();

        splash.SetStatus("Loading settings…");
        AppSettingsLoader.Apply();   // applies theme + default paths

        // ── Hand off to async bootstrap so the splash stays responsive ───────
        _ = BootstrapAsync(splash);
    }

    /// <summary>
    /// Waits for the minimum splash display time, then shows MainWindow and
    /// fades the splash out. Runs on the UI thread via async/await so the
    /// WPF dispatcher keeps processing — the splash animation stays smooth.
    /// </summary>
    private static async Task BootstrapAsync(SplashWindow splash)
    {
        // Guarantee the splash is visible for at least 1.8 s even on fast machines.
        // This prevents an ugly flash where the splash barely appears before vanishing.
        await Task.Delay(1800);

        splash.SetStatus("Ready");

        // Brief pause so the "Ready" text is readable before the window appears.
        await Task.Delay(150);

        // Create and show the main window
        var main = new MainWindow();
        main.Show();
        main.Activate();

        // Fade out the splash (returns once the animation completes and the
        // window is closed — typically ~400 ms).
        await splash.FadeOutAndCloseAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe error has been logged. The app will continue to run.",
            "ALE Image Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogCrash("AppDomain", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Task", e.Exception);
        e.SetObserved();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "GoldenISOBuilder");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch { /* if we can't even log, give up silently */ }
    }
}
