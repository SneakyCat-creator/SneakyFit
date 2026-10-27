using System.Windows;
using System.Windows.Media;

namespace SneakyFit.Desktop.Controls;

public sealed class ActivityGauge : FrameworkElement
{
    public double Fraction { get; init; }
    public bool OnDark { get; init; }
    protected override void OnRender(DrawingContext context)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 7);
        var track = new Pen(new SolidColorBrush(OnDark ? Color.FromArgb(65, 255, 255, 255) : Color.FromRgb(219, 206, 242)), 5);
        context.DrawEllipse(null, track, center, radius, radius);
        var fraction = Math.Clamp(Fraction, 0, 1); if (fraction <= 0 || radius == 0) return;
        var pen = new Pen(new LinearGradientBrush(OnDark ? Color.FromRgb(199, 249, 232) : Color.FromRgb(161, 103, 235), Color.FromRgb(66, 185, 154), 70), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 1) { context.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = fraction * Math.PI * 2 - Math.PI / 2;
        var end = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open()) { drawing.BeginFigure(new Point(center.X, center.Y - radius), false, false); drawing.ArcTo(end, new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false); }
        geometry.Freeze(); context.DrawGeometry(null, pen, geometry);
    }
}
