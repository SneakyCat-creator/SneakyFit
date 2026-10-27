using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;

namespace SneakyFit.Desktop.Views;

internal static class D
{
    public const double CaptionSize = 12, BodySize = 15, SubheadingSize = 17, HeadingSize = 20, PageHeadingSize = 24,
        MetricSize = 28, LargeMetricSize = 48, HeroSize = 56, ControlSize = 40;
    public static readonly Thickness ControlPadding = new(12, 8, 12, 8);
    public const string Ink = "#29223F", Muted = "#91889F", Purple = "#7B4AD8", Mint = "#2BA98B", Coral = "#F17D79", Gold = "#E6AA3C";
    public static Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock T(string value, double size = BodySize, string color = Ink, bool bold = false) => new() { Text = value, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    public static StackPanel S(params UIElement[] items) { var panel = new StackPanel(); foreach (var item in items) panel.Children.Add(item); return panel; }
    public static StackPanel H(params UIElement[] items) { var panel = S(items); panel.Orientation = Orientation.Horizontal; return panel; }
    public static Border M(UIElement child, double top = 0, double bottom = 0, double left = 0, double right = 0) => new() { Child = child, Margin = new Thickness(left, top, right, bottom) };
    public static Border Surface(UIElement child, string color = "#FFFFFF", double pad = 20, double radius = 25) => new() { Child = child, Background = B(color), CornerRadius = new CornerRadius(radius), Padding = new Thickness(pad) };
    public static void GiveDepth(Border surface, double blur = 12, double opacity = .12)
    {
        var original = surface.Background;
        if (original is SolidColorBrush solid)
            surface.Background = new LinearGradientBrush(solid.Color, Color.FromArgb(solid.Color.A, (byte)Math.Max(0, solid.Color.R - 5), (byte)Math.Max(0, solid.Color.G - 6), (byte)Math.Max(0, solid.Color.B - 2)), 90);
        // Keep text, vector illustrations and images out of the shadow's raster surface.
        var child = surface.Child; surface.Child = null;
        var layers = new Grid();
        layers.Children.Add(new Border { Background = surface.Background, CornerRadius = surface.CornerRadius, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(99, 69, 154), BlurRadius = blur, ShadowDepth = 4, Direction = 270, Opacity = opacity, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality } });
        layers.Children.Add(new Border { Background = surface.Background, CornerRadius = surface.CornerRadius, Padding = surface.Padding, Child = child,
            BorderThickness = new Thickness(1), BorderBrush = new LinearGradientBrush(Color.FromArgb(170, 255, 255, 255), Color.FromArgb(38, 115, 80, 168), 90) });
        surface.Background = Brushes.Transparent; surface.Padding = new Thickness(0); surface.Child = layers;
    }
    public static Border Tag(string label, string background = "#EEE6FC", string color = Purple) => new() { Child = T(label, CaptionSize, color, true), Background = B(background), CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 6, 11, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    public static Button Btn(string label, Action action, string background = "#EDE7FC", string foreground = Purple)
    {
        var color = (Color)ColorConverter.ConvertFromString(background);
        var button = new Button { Content = label, Foreground = B(foreground) };
        button.Background = new LinearGradientBrush(color, Color.FromArgb(color.A, (byte)Math.Max(0, color.R - 4), (byte)Math.Max(0, color.G - 5), (byte)Math.Max(0, color.B - 2)), 90);
        button.Click += (_, _) => action(); return button;
    }
    public static Button Primary(string label, Action action) => Btn(label, action, Purple, "#FFFFFF");
    public static Button Round(string label, Action action, string background = "#FFFFFF", string foreground = Purple) { var button = Btn(label, action, background, foreground); button.Width = ControlSize; button.Height = ControlSize; button.Padding = new Thickness(0); button.FontSize = 23; return button; }
    public static Grid Pair(UIElement left, UIElement right, double weight1 = 1, double weight2 = 1, double gap = 20) { var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(weight1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(gap) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(weight2, GridUnitType.Star) }); grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right); return grid; }
    public static Grid Between(UIElement left, UIElement right) { var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
    public static TextBox Input(string value = "", bool metric = false)
    {
        var input = new TextBox { Text = value };
        if (metric) input.SetResourceReference(FrameworkElement.StyleProperty, "MetricInput");
        return input;
    }
    public static StackPanel Field(string name, UIElement input) => S(M(T(name, CaptionSize, "#655F72"), bottom: 5), M(input, bottom: 12));
    public static ComboBox Select(IEnumerable<string> options, int selected = 0) { var box = new ComboBox(); foreach (var option in options) box.Items.Add(option); box.SelectedIndex = selected; return box; }
    public static Border Progress(decimal value, decimal total, string fill, string track = "#F2EBFA", double height = 8)
    {
        var fraction = total <= 0 ? 0 : Math.Clamp((double)(value / total), 0, 1); var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.00001, fraction), GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.00001, 1 - fraction), GridUnitType.Star) });
        if (fraction > 0) grid.Children.Add(new Border { Background = B(fill), CornerRadius = new CornerRadius(height / 2) });
        return new Border { Height = height, Background = B(track), Child = grid, CornerRadius = new CornerRadius(height / 2) };
    }
    public static TextBlock Link(string prefix, string linkText, Action action, string suffix = "", double size = BodySize)
    {
        var text = T("", size); text.Inlines.Add(new Run(prefix)); var link = new Hyperlink(new Run(linkText)) { Foreground = B(Purple), FontWeight = FontWeights.SemiBold }; link.Click += (_, _) => action(); text.Inlines.Add(link); text.Inlines.Add(new Run(suffix)); return text;
    }
    public static Grid Equal(params UIElement[] elements) { var grid = new Grid(); for (var i = 0; i < elements.Length; i++) { grid.ColumnDefinitions.Add(new ColumnDefinition()); var item = M(elements[i], right: i < elements.Length - 1 ? 16 : 0); Grid.SetColumn(item, i); grid.Children.Add(item); } return grid; }
    public static TextBlock Icon(string glyph, double size = 21, string color = Purple) { var text = T(glyph, size, color); text.FontFamily = new FontFamily("Segoe Fluent Icons"); return text; }
    public static UIElement WorkoutIcon() => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse("M 3,6 L 3,14 M 6,3 L 6,17 M 6,10 L 16,10 M 16,3 L 16,17 M 19,6 L 19,14"),
        Width = 22, Height = 22, Stroke = B("#79668F"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
    };

}
