using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GoldenISOBuilder.Views;

// ── Dialog kinds ─────────────────────────────────────────────────────────────

public enum AppDialogIcon { Question, Warning, Error, Info, Success }
public enum AppDialogButtons { Ok, YesNo }

/// <summary>
/// Custom dark-themed dialog window — replaces every native MessageBox.Show in
/// the app so confirmations and alerts match the overall design language.
/// Use the static <see cref="AppDialog"/> helper rather than instantiating this
/// class directly.
/// </summary>
public partial class AppDialogWindow : Window
{
    /// <summary>True if the user clicked Yes / OK; false otherwise.</summary>
    public bool Confirmed { get; private set; }

    public AppDialogWindow(
        string          title,
        string          message,
        AppDialogButtons buttons = AppDialogButtons.Ok,
        AppDialogIcon   icon    = AppDialogIcon.Info)
    {
        InitializeComponent();

        TitleBlock.Text   = title;
        MessageBlock.Text = message;

        ApplyIcon(icon);
        BuildButtons(buttons);

        // Close on Escape
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            { Confirmed = false; Close(); }
        };
    }

    // ── Icon badge ────────────────────────────────────────────────────────────

    private void ApplyIcon(AppDialogIcon icon)
    {
        // Only the foreground glyph colour is theme-specific.
        // The badge background is a 14% opacity tint of the icon colour so it
        // looks correct on BOTH dark and light themes without any hardcoding.
        string fg = icon switch
        {
            AppDialogIcon.Question => "#4D8EF8",
            AppDialogIcon.Warning  => "#F5A623",
            AppDialogIcon.Error    => "#E05050",
            AppDialogIcon.Success  => "#3DD68C",
            _                      => "#8A9BB0",   // Info — neutral grey-blue
        };
        string glyph = icon switch
        {
            AppDialogIcon.Question => "?",
            AppDialogIcon.Warning  => "!",
            AppDialogIcon.Error    => "✕",
            AppDialogIcon.Success  => "✓",
            _                      => "i",
        };

        var conv    = new ColorConverter();
        var fgColor = (Color)conv.ConvertFrom(fg)!;

        IconGlyph.Text       = glyph;
        IconGlyph.Foreground = new SolidColorBrush(fgColor);

        // Semi-transparent tint: works on dark AND light backgrounds
        IconBadge.Background  = new SolidColorBrush(Color.FromArgb(0x22, fgColor.R, fgColor.G, fgColor.B));
        IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, fgColor.R, fgColor.G, fgColor.B));
        IconBadge.BorderThickness = new Thickness(1);
    }

    // ── Button row ────────────────────────────────────────────────────────────

    private void BuildButtons(AppDialogButtons mode)
    {
        if (mode == AppDialogButtons.YesNo)
        {
            // No first (left), then Yes (right, primary)
            AddButton("No",  isPrimary: false, result: false);
            AddButton("Yes", isPrimary: true,  result: true);
        }
        else
        {
            AddButton("OK", isPrimary: true, result: true);
        }
    }

    private void AddButton(string label, bool isPrimary, bool result)
    {
        var styleKey = isPrimary ? "DefaultButtonStyle" : "GhostButtonStyle";
        var btn = new Button
        {
            Content    = label,
            Style      = (Style)Application.Current.Resources[styleKey],
            MinWidth   = 80,
            Margin     = new Thickness(isPrimary ? 8 : 0, 0, 0, 0),
        };
        btn.Click += (_, _) => { Confirmed = result; Close(); };
        ButtonRow.Children.Add(btn);
    }
}

// ── Static helper — the only public API the rest of the app should use ───────

public static class AppDialog
{
    /// <summary>
    /// Shows a Yes / No confirmation dialog.  Returns true if the user clicked Yes.
    /// </summary>
    public static bool Confirm(
        DependencyObject caller,
        string message,
        string title = "Confirm",
        AppDialogIcon icon = AppDialogIcon.Question)
    {
        var dlg = Build(caller, title, message, AppDialogButtons.YesNo, icon);
        dlg.ShowDialog();
        return dlg.Confirmed;
    }

    /// <summary>
    /// Shows a single-button informational / warning / error alert.
    /// </summary>
    public static void Alert(
        DependencyObject caller,
        string message,
        string title = "Notice",
        AppDialogIcon icon = AppDialogIcon.Info)
    {
        var dlg = Build(caller, title, message, AppDialogButtons.Ok, icon);
        dlg.ShowDialog();
    }

    // ── Internal factory ──────────────────────────────────────────────────────

    private static AppDialogWindow Build(
        DependencyObject caller,
        string title, string message,
        AppDialogButtons buttons, AppDialogIcon icon)
    {
        var dlg = new AppDialogWindow(title, message, buttons, icon);

        // Try to find the owning Window so the dialog centres over it.
        var owner = caller is Window w ? w : Window.GetWindow(caller);
        if (owner != null) dlg.Owner = owner;

        return dlg;
    }
}
