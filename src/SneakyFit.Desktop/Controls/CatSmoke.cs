using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SneakyFit.Desktop.Controls;

// Small translucent wisps, not a filter on the miniature. Gradients fade to
// transparent edges so the fur remains sharp throughout the transition.
internal sealed class CatSmoke : FrameworkElement
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(CatSmoke),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush Pearl = Cloud(Color.FromArgb(218, 252, 249, 255), Color.FromArgb(112, 210, 196, 238));
    private static readonly Brush Lilac = Cloud(Color.FromArgb(178, 237, 227, 251), Color.FromArgb(92, 191, 173, 221));
    private static readonly Brush Mint = Cloud(Color.FromArgb(154, 243, 253, 249), Color.FromArgb(70, 193, 229, 213));
    private static readonly (double X, double Y, double Drift, double Delay, double Radius)[] Wisps =
    [
        (-24, 8, -12, .00, 15), (21, 9, 13, .05, 16),
        (-10, 17, -6, .11, 16), (9, 14, 7, .15, 14),
        (-30, -5, -8, .08, 12), (29, -5, 8, .12, 13),
        (-2, 0, 2, .20, 13)
    ];
    private int _version;
    private double _lean;
    private double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public CatSmoke()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
        Visibility = Visibility.Collapsed;
    }

    public void Play()
    {
        if (!SystemParameters.ClientAreaAnimation) { Stop(); return; }
        var version = ++_version;
        _lean = Random.Shared.NextDouble() * 4 - 2;
        Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(980)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { if (version == _version) Stop(); };
        BeginAnimation(ProgressProperty, animation);
    }

    public void Stop()
    {
        _version++; BeginAnimation(ProgressProperty, null); Progress = 1;
        Visibility = Visibility.Collapsed;
    }

    private static Brush Cloud(Color center, Color edge)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(.45, .40), Center = new Point(.5, .5), RadiusX = .5, RadiusY = .5,
            GradientStops = [new(center, 0), new(edge, .45), new(Color.FromArgb(0, edge.R, edge.G, edge.B), 1)]
        };
        brush.Freeze(); return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Progress <= 0 || Progress >= 1 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var origin = new Point(ActualWidth / 2, ActualHeight * .64);
        for (var i = 0; i < Wisps.Length; i++)
        {
            var wisp = Wisps[i];
            var t = Math.Clamp((Progress - wisp.Delay) / (1 - wisp.Delay), 0, 1);
            if (t <= 0 || t >= 1) continue;
            var envelope = Math.Pow(Math.Sin(Math.PI * t), 1.35) * .57;
            var lift = t * t;
            var x = origin.X + wisp.X + wisp.Drift * t + Math.Sin(t * Math.PI) * _lean;
            var y = origin.Y + wisp.Y - 22 * lift;
            var radius = wisp.Radius * (.55 + .80 * t);
            dc.PushOpacity(envelope);
            dc.DrawEllipse(i == 3 ? Mint : i % 2 == 0 ? Pearl : Lilac, null,
                new Point(x, y), radius, radius * (.78 + .12 * t));
            dc.Pop();
        }
    }
}
