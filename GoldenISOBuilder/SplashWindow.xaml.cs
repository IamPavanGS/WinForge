using System.Windows;
using System.Windows.Media.Animation;

namespace GoldenISOBuilder;

/// <summary>
/// Branded splash screen shown while the application cold-starts.
/// Fades in on load, exposes SetStatus() for live progress text,
/// and FadeOutAndCloseAsync() for a smooth exit once MainWindow is ready.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FadeIn();
    }

    // ── Fade in ───────────────────────────────────────────────────────────────

    private void FadeIn()
    {
        var anim = new DoubleAnimation(0, 1,
            new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Live status text ──────────────────────────────────────────────────────

    /// <summary>Updates the status line shown above the progress bar.</summary>
    public void SetStatus(string text)
    {
        // Called from the UI thread (App.OnStartup), so no Dispatcher needed.
        StatusText.Text = text;

        // Force a render pass so the label visibly updates before the next
        // synchronous operation (e.g. loading settings) blocks the thread.
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
    }

    // ── Fade out and close ────────────────────────────────────────────────────

    /// <summary>
    /// Fades the splash to transparent then closes it.
    /// Awaitable — MainWindow is already shown before this is called.
    /// </summary>
    public Task FadeOutAndCloseAsync()
    {
        var tcs = new TaskCompletionSource();

        var anim = new DoubleAnimation(1, 0,
            new Duration(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        anim.Completed += (_, _) =>
        {
            Close();
            tcs.SetResult();
        };

        BeginAnimation(OpacityProperty, anim);
        return tcs.Task;
    }
}
