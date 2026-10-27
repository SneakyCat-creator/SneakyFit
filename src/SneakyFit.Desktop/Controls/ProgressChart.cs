using System.Globalization;
using System.Windows;
using System.Windows.Media;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop.Controls;

public sealed class ProgressChart : FrameworkElement
{
    public double[] Values { get; init; } = [];
    public string[] Labels { get; init; } = [];
    public bool Bars { get; init; }
    public string Color { get; init; } = "#8C59DC";
    public ProgressChart() { Height = 300; ClipToBounds = true; }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 150 || Values.Length == 0) return;
        double left = 48, top = 28, width = ActualWidth - 70, height = ActualHeight - 63;
        double min = Bars ? 0 : Values.Min() - Math.Max(.5, (Values.Max() - Values.Min()) * .3), max = Values.Max() + Math.Max(.5, (Values.Max() - Values.Min()) * .25);
        if (max <= min) max = min + 1;
        var brush = B(Color); var ru = CultureInfo.GetCultureInfo("ru-RU");
        void Label(string value, double x, double y, string color = "#A396B3", bool bold = false) => dc.DrawText(new FormattedText(value, ru, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), 12, B(color), VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        double Y(double value) => top + height - (value - min) / (max - min) * height;
        for (int i = 0; i <= 3; i++) { var value = min + (max - min) * i / 3; dc.DrawLine(new Pen(B("#EEE7F6"), 1) { DashStyle = DashStyles.Dash }, new(left, Y(value)), new(left + width, Y(value))); Label(value.ToString(max < 150 ? "0.0" : "0", ru), 0, Y(value) - 8); }
        var step = width / Values.Length; var points = Values.Select((v, i) => new Point(left + step * (i + .5), Y(v))).ToArray();
        if (Bars) { for (var i = 0; i < points.Length; i++) { var bar = B(Color).Clone(); bar.Opacity = i == points.Length - 1 ? 1 : .42; dc.DrawRoundedRectangle(bar, null, new(points[i].X - Math.Min(16, step * .3), points[i].Y, Math.Min(32, step * .6), top + height - points[i].Y), 7, 7); } }
        else
        {
            var area = new StreamGeometry(); using (var ctx = area.Open()) { ctx.BeginFigure(new(points[0].X, top + height), true, true); foreach (var p in points) ctx.LineTo(p, true, false); ctx.LineTo(new(points[^1].X, top + height), true, false); }
            var fill = new LinearGradientBrush(((SolidColorBrush)brush).Color, Colors.Transparent, 90) { Opacity = .18 }; dc.DrawGeometry(fill, null, area);
            for (int i = 1; i < points.Length; i++) dc.DrawLine(new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, points[i - 1], points[i]);
            foreach (var p in points) dc.DrawEllipse(Brushes.White, new Pen(brush, 2), p, 4, 4);
        }
        for (int i = 0; i < points.Length && i < Labels.Length; i++) if (i % Math.Max(1, Values.Length / 7) == 0 || i == points.Length - 1) Label(Labels[i], points[i].X - 15, top + height + 16);
        var last = points[^1]; Label(Values[^1].ToString(max < 150 ? "0.0" : "0", ru), last.X - 15, last.Y - 23, Color, true);
    }
}
