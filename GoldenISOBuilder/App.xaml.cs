using System.IO;
using System.Windows;
using System.Windows.Threading;
using GoldenISOBuilder.Helpers;
using GoldenISOBuilder.Services;

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

        // ── Show splash on its own STA thread ────────────────────────────────
        // Keeps splash animated and responsive even while this thread is busy
        // constructing MainWindow (BAML parse of all wizard pages can block
        // this thread for several seconds on first launch).
        SplashHost.Start();

        // ── Synchronous initialisation ───────────────────────────────────────
        SplashHost.SetStatus("Registering notifications…");
        ToastHelper.Initialize();

        SplashHost.SetStatus("Loading settings…");
        AppSettingsLoader.Apply();   // applies theme + default paths

        // ── Bootstrap MainWindow ─────────────────────────────────────────────
        _ = BootstrapAsync();
    }

    private static async Task BootstrapAsync()
    {
        // Guarantee the splash is visible for at least 1.5 s even on fast
        // machines, otherwise it flashes by before the user registers it.
        await Task.Delay(1500);

        SplashHost.SetStatus("Loading wizard…");

        // Heavy BAML parse — blocks the main UI thread, but the splash is on
        // its own thread so it stays animated.
        var main = new MainWindow();

        SplashHost.SetStatus("Ready");
        await Task.Delay(200);

        main.Show();
        main.Activate();

        await SplashHost.FadeOutAndCloseAsync();

        // Deferred startup cleanup: reap orphan GIB-Test-* VMs and leftover
        // VHDX folders from previous crashed sessions. This can take 30+ s
        // when there's a multi-GB VHDX to delete, so we run it AFTER the
        // window is up so it never blocks launch.
        _ = Task.Run(() => HyperVService.Instance.ReapOrphanedVmsAsync());
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
