using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GoldenISOBuilder;

// Hosts SplashWindow on a dedicated STA thread with its own dispatcher.
// The splash stays animated and responsive even while the main UI thread
// is blocked constructing MainWindow (BAML parse of ~10 wizard pages can
// freeze the main UI thread for several seconds on cold start).
internal static class SplashHost
{
    private static Thread?         _thread;
    private static SplashWindow?   _splash;
    private static Dispatcher?     _dispatcher;
    private static readonly ManualResetEventSlim _ready = new(false);

    public static void Start()
    {
        if (_thread != null) return;

        _thread = new Thread(() =>
        {
            _splash     = new SplashWindow();
            _dispatcher = Dispatcher.CurrentDispatcher;

            _splash.Show();
            _ready.Set();

            Dispatcher.Run();   // pumps messages until InvokeShutdown is called
        })
        {
            IsBackground = true,
            Name         = "SplashUI"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait();   // block caller until splash is on-screen
    }

    public static void SetStatus(string text)
    {
        if (_dispatcher == null || _splash == null) return;
        try { _dispatcher.Invoke(() => _splash.SetStatus(text)); }
        catch { /* dispatcher may have shut down */ }
    }

    public static Task FadeOutAndCloseAsync()
    {
        if (_dispatcher == null || _splash == null) return Task.CompletedTask;

        var tcs = new TaskCompletionSource();

        _dispatcher.BeginInvoke(async () =>
        {
            try { await _splash.FadeOutAndCloseAsync(); }
            catch { /* ignored */ }
            _dispatcher.InvokeShutdown();
            tcs.TrySetResult();
        });

        return tcs.Task;
    }
}
