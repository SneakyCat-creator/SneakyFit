using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SneakyFit.Desktop.Controls;

internal enum UiMotionKind { Dialog, Forward, Back, Calendar, Menu, Page, Shade }

internal static class UiMotion
{
    private sealed class MotionState
    {
        public int Version;
        public bool Running;
        public double Opacity;
        public Transform? Transform;
        public Geometry? Clip;
        public RoutedEventHandler? Loaded;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> States = new();

    public static void Enter(FrameworkElement element, UiMotionKind kind)
    {
        Stop(element);
        if (!SystemParameters.ClientAreaAnimation) return;
        var state = States.GetValue(element, _ => new MotionState());
        var version = ++state.Version;
        if (!element.IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                element.Loaded -= loaded;
                if (state.Loaded == loaded) state.Loaded = null;
                if (version == state.Version) Start(element, kind, state, version);
            };
            state.Loaded = loaded; element.Loaded += loaded;
        }
        else Start(element, kind, state, version);
    }

    public static void Stop(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state)) return;
        state.Version++;
        if (state.Loaded is { } loaded) { element.Loaded -= loaded; state.Loaded = null; }
        Restore(element, state);
    }

    private static void Restore(FrameworkElement element, MotionState state)
    {
        if (!state.Running) return;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = state.Opacity;
        element.RenderTransform = state.Transform ?? Transform.Identity;
        element.Clip = state.Clip;
        state.Running = false;
    }

    private static void Start(FrameworkElement element, UiMotionKind kind, MotionState state, int version)
    {
        state.Opacity = element.Opacity; state.Transform = element.RenderTransform; state.Clip = element.Clip; state.Running = true;
        var milliseconds = kind switch { UiMotionKind.Dialog => 220, UiMotionKind.Calendar => 180, UiMotionKind.Page => 190, UiMotionKind.Shade or UiMotionKind.Menu => 130, _ => 170 };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(kind == UiMotionKind.Shade ? 0 : .2, state.Opacity, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (version == state.Version) Restore(element, state); };
        if (kind != UiMotionKind.Shade)
        {
            var shift = new TranslateTransform();
            var transform = new TransformGroup();
            if (state.Transform is { } original && !original.Value.IsIdentity) transform.Children.Add(original);
            transform.Children.Add(shift); element.RenderTransform = transform;
            var x = kind switch { UiMotionKind.Forward => 10, UiMotionKind.Back => -10, _ => 0 };
            var y = kind switch { UiMotionKind.Dialog => 8, UiMotionKind.Calendar => -3, UiMotionKind.Menu => -2, UiMotionKind.Page => 5, _ => 0 };
            if (x != 0) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, 0, duration) { EasingFunction = ease });
            if (y != 0) shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, 0, duration) { EasingFunction = ease });
            if (kind == UiMotionKind.Calendar && state.Clip is null && element.ActualHeight > 0)
            {
                // Reveal the lower edge without scaling the calendar's type or hit targets.
                var end = new Rect(-24, -24, element.ActualWidth + 48, element.ActualHeight + 48);
                var start = new Rect(end.X, end.Y, end.Width, end.Height * .78);
                var clip = new RectangleGeometry(end); element.Clip = clip;
                clip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(start, end, duration) { EasingFunction = ease });
            }
        }
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
