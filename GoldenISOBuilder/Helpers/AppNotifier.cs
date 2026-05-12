using System.Windows;
using GoldenISOBuilder.Views;

namespace GoldenISOBuilder.Helpers;

/// <summary>
/// Application notification hub that always shows an in-app toast (reliable
/// from elevated processes) and also attempts a WinRT OS-level toast as a bonus.
///
/// Call <see cref="Show"/> from any thread; it marshals to the UI thread automatically.
/// </summary>
public static class AppNotifier
{
    /// <summary>
    /// Shows a notification.  Always shows an in-app toast; also attempts a
    /// Windows Action-Center toast (silently skipped if blocked by elevation,
    /// Focus Assist, or group policy).
    /// </summary>
    public static void Show(string title, string body, ToastKind kind = ToastKind.Info)
    {
        // ── In-app toast (primary — elevation-safe, always visible) ──────────
        var app = Application.Current;
        if (app != null)
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var win = new InAppToastWindow(title, body, kind);
                    win.Show();
                }
                catch { /* never crash on a notification */ }
            });
        }

        // ── WinRT OS-level toast (secondary — skipped silently on failure) ───
        // Runs on a thread-pool thread so the UI is never blocked by WinRT COM calls.
        Task.Run(() =>
        {
            try { ToastHelper.Show(title, body, kind); }
            catch { /* non-critical */ }
        });
    }
}
