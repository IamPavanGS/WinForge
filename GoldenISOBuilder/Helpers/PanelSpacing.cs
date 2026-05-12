using System.Windows;
using System.Windows.Controls;

namespace GoldenISOBuilder.Helpers;

public static class PanelSpacing
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.RegisterAttached(
            "Spacing", typeof(double), typeof(PanelSpacing),
            new UIPropertyMetadata(0.0, OnSpacingChanged));

    public static void SetSpacing(DependencyObject o, double value) => o.SetValue(SpacingProperty, value);
    public static double GetSpacing(DependencyObject o) => (double)o.GetValue(SpacingProperty);

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackPanel sp)
            sp.Loaded += (s, _) => ApplyStackPanelSpacing((StackPanel)s!);
        else if (d is WrapPanel wp)
            wp.Loaded += (s, _) => ApplyWrapPanelSpacing((WrapPanel)s!);
    }

    private static void ApplyStackPanelSpacing(StackPanel panel)
    {
        double spacing = GetSpacing(panel);
        bool horizontal = panel.Orientation == Orientation.Horizontal;
        int count = panel.Children.Count;
        for (int i = 0; i < count - 1; i++)
        {
            if (panel.Children[i] is FrameworkElement fe)
            {
                var m = fe.Margin;
                fe.Margin = horizontal
                    ? new Thickness(m.Left, m.Top, spacing, m.Bottom)
                    : new Thickness(m.Left, m.Top, m.Right, spacing);
            }
        }
    }

    private static void ApplyWrapPanelSpacing(WrapPanel panel)
    {
        double spacing = GetSpacing(panel);
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement fe)
            {
                var m = fe.Margin;
                fe.Margin = new Thickness(m.Left, m.Top, spacing, m.Bottom);
            }
        }
    }
}
