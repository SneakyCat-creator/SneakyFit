using System.Windows;
using System.Windows.Media;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop.Controls;

public sealed class EnergyRing : FrameworkElement
{
    public double Fraction { get; init; }
    public string FillColor { get; init; } = "#DEFFAE";
    public EnergyRing() { Width = Height = 178; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawEllipse(null, new Pen(B("#9972E3"), 13), new(89, 89), 72, 72);
        var fraction = Math.Clamp(Fraction, 0, .99999); if (fraction <= 0) return;
        var shape = new StreamGeometry(); using (var ctx = shape.Open()) { ctx.BeginFigure(new(89, 17), false, false); var angle = fraction * Math.PI * 2 - Math.PI / 2; ctx.ArcTo(new(89 + 72 * Math.Cos(angle), 89 + 72 * Math.Sin(angle)), new(72, 72), 0, fraction > .5, SweepDirection.Clockwise, true, false); }
        dc.DrawGeometry(null, new Pen(B(FillColor), 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, shape);
    }
}

public sealed class FoodPicture : FrameworkElement
{
    public int Kind { get; init; }
    public FoodPicture() { Width = 110; Height = 100; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth / 110, ActualHeight / 100); dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawEllipse(B("#18000000"), null, new(57, 86), 43, 7);
        if (Kind % 4 == 0)
        {
            dc.PushTransform(new RotateTransform(-13, 55, 50));
            dc.DrawRoundedRectangle(B("#D79D51"), null, new(20, 15, 70, 68), 17, 17); dc.DrawRoundedRectangle(B("#F5D79A"), null, new(26, 20, 58, 57), 13, 13);
            dc.DrawEllipse(B("#FFFCED"), null, new(53, 46), 23, 20); dc.DrawEllipse(B("#FFFCED"), null, new(60, 55), 24, 18); dc.DrawEllipse(B("#F9B72A"), null, new(55, 48), 15, 15); dc.DrawEllipse(B("#FFD65A"), null, new(52, 44), 8, 7); dc.Pop();
        }
        else if (Kind % 4 == 1)
        {
            dc.DrawEllipse(B("#B8DAD3"), null, new(55, 66), 48, 25); dc.DrawEllipse(B("#FAFAF2"), null, new(55, 58), 44, 22);
            for (int i = 0; i < 16; i++) dc.DrawEllipse(B(i % 2 == 0 ? "#B08E62" : "#D2AE77"), null, new(28 + i % 5 * 10, 43 + i / 5 * 7), 6, 4);
            dc.DrawRoundedRectangle(B("#DF975C"), null, new(51, 26, 39, 28), 12, 12); dc.DrawLine(new Pen(B("#B27648"), 3), new(61, 29), new(77, 49));
            dc.DrawEllipse(B("#6EAC61"), null, new(23, 49), 12, 8); dc.DrawEllipse(B("#82BF6E"), null, new(31, 39), 9, 12);
        }
        else if (Kind % 4 == 2)
        {
            var cup = Geometry.Parse("M 28,25 L 85,25 L 78,84 Q 56,92 35,84 Z"); dc.DrawGeometry(B("#B6DDDF"), null, cup); dc.DrawRoundedRectangle(B("#F9FCF7"), null, new(30, 40, 53, 26), 6, 6); dc.DrawEllipse(B("#EFF8F4"), null, new(56, 26), 30, 10);
            dc.DrawEllipse(B("#6B4B9E"), null, new(45, 23), 7, 7); dc.DrawEllipse(B("#8762B5"), null, new(58, 23), 7, 6); dc.DrawEllipse(B("#E9797C"), null, new(70, 20), 8, 9); dc.DrawLine(new Pen(B("#7CA565"), 3), new(70, 13), new(75, 9));
        }
        else
        {
            dc.DrawEllipse(B("#ACD9BE"), null, new(56, 65), 48, 24); dc.DrawEllipse(B("#F6F9EF"), null, new(56, 60), 43, 19);
            dc.PushTransform(new RotateTransform(-12, 55, 50)); dc.DrawRoundedRectangle(B("#F19578"), null, new(23, 36, 65, 30), 12, 12); for (int i = 0; i < 4; i++) dc.DrawLine(new Pen(B("#FFD4B5"), 3), new(35 + i * 12, 38), new(29 + i * 12, 63)); dc.Pop();
            dc.DrawEllipse(B("#69A56E"), null, new(83, 34), 9, 13); dc.DrawEllipse(B("#8BBF7E"), null, new(93, 44), 10, 7); dc.DrawEllipse(B("#E6C251"), null, new(24, 66), 12, 9);
        }
        dc.Pop();
    }
}
