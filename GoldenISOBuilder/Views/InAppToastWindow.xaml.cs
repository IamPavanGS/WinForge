using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoldenISOBuilder.Helpers;

namespace GoldenISOBuilder.Views;

/// <summary>
/// A lightweight in-app toast notification that works reliably from elevated
/// processes (bypasses WinRT AUMID / Focus-Assist restrictions entirely).
/// Shows bottom-right on screen, auto-dismisses after <see cref="DisplaySeconds"/> seconds.
/// </summary>
public partial class InAppToastWindow : Window
{
    // ── Static slot tracker so multiple toasts stack upward ──────────────────
    private static readonly List<InAppToastWindow> _active = [];
    private const int SlotHeight   = 84;   // window height + gap
    private const int MarginRight  = 20;
    private const int MarginBottom = 20;
    private const double DisplaySeconds = 4.0;

    // ─────────────────────────────────────────────────────────────────────────

    public InAppToastWindow(string title, string body, ToastKind kind)
    {
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text  = body;

        // Accent stripe colour
        Color stripe = kind switch
        {
            ToastKind.Success => Color.FromRgb(0x28, 0xA7, 0x45),  // green
            ToastKind.Failure => Color.FromRgb(0xDC, 0x35, 0x45),  // red
            _                 => Color.FromRgb(0x4D, 0x8E, 0xF8),  // blue (info)
        };
        Stripe.Background = new SolidColorBrush(stripe);

        // Position — stack above previous toasts
        lock (_active) _active.Add(this);
        RepositionAll();

        Closed += (_, _) =>
        {
            lock (_active) _active.Remove(this);
            RepositionAll();
        };
    }

    // ── Reposition all active toasts so they stack upward from bottom-right ──

    private static void RepositionAll()
    {
        var wa = SystemParameters.WorkArea;
        lock (_active)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                var w = _active[i];
                w.Left = wa.Right  - w.Width  - MarginRight;
                w.Top  = wa.Bottom - w.Height - MarginBottom - (i * SlotHeight);
            }
        }
    }

    // ── Show with slide-up + fade-in, auto-dismiss with fade-out ─────────────

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Slide up from 12px below + fade in
        var slideIn = new DoubleAnimation(12, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));

        var tt = new TranslateTransform();
        Card.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, slideIn);
        Card.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        // Auto-dismiss
        var dismiss = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(DisplaySeconds) };
        dismiss.Tick += (_, _) =>
        {
            dismiss.Stop();
            FadeOut();
        };
        dismiss.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(280)));
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
