using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SneakyFit.Desktop.Controls;

public sealed class CircularActionButton : Button
{
    private Ellipse? _halo;

    public CircularActionButton()
    {
        Style = null;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        var surface = new FrameworkElementFactory(typeof(Grid));
        var halo = new FrameworkElementFactory(typeof(Ellipse), "Halo");
        halo.SetValue(Shape.FillProperty, Brushes.White);
        halo.SetValue(OpacityProperty, 0d);
        surface.AppendChild(halo);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(IsHitTestVisibleProperty, false);
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _halo = GetTemplateChild("Halo") as Ellipse;
        UpdateHalo();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty ||
            e.Property == IsKeyboardFocusedProperty || e.Property == IsEnabledProperty) UpdateHalo();
    }

    private void UpdateHalo()
    {
        if (_halo is null) return;
        var opacity = !IsEnabled ? 0 : IsPressed ? .16 : IsMouseOver || IsKeyboardFocused ? .08 : 0;
        _halo.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity,
            TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 0))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
}
