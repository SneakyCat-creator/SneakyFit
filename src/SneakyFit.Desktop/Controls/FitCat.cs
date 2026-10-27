using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SneakyFit.Desktop.Controls;

public enum CatMood { Thinking, Celebration, Surprise, Idle }

public sealed class FitCat : Grid
{
    private const double FaceWidth = 92, FaceHeight = 84;
    private readonly Image _face = Face();
    private readonly Image _previousFace = Face();
    private readonly Image _shadow = Face();
    private readonly Image _previousShadow = Face();
    private readonly Grid _surface = new();
    private readonly TranslateTransform _pointer = new();
    private readonly TranslateTransform _reaction = new();
    private readonly DispatcherTimer _variation = new();
    private readonly DispatcherTimer _deferredMood = new();
    private readonly CatSmoke _smoke = new() { Width = 122, Height = 112, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Dictionary<(int Sprite, double X, double Y), BitmapSource> _sized = [];
    private readonly Button _button;
    private int _sprite = -1, _crossfadeVersion, _iconSprite = -1;
    private long _changedAt;
    private bool _crossfading, _pendingVariation, _hasTransitioned, _celebrateAfterChange;
    private bool _clickPoseHeld;
    private CatReaction? _pendingClick, _activeClick;
    public event Action? Tickled;
    public event Action<string>? Reacted;
    public event Action<BitmapSource>? IconChanged;
    public BitmapSource? CurrentIcon { get; private set; }
    public CatMood Mood { get; private set; } = CatMood.Thinking;

    public FitCat()
    {
        Width = 102; Height = 94; UseLayoutRounding = true; ClipToBounds = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        foreach (var shadow in new[] { _shadow, _previousShadow })
            shadow.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(85, 57, 125), BlurRadius = 7, ShadowDepth = 2,
                Opacity = .24, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
        _surface.Children.Add(_previousShadow); _surface.Children.Add(_shadow);
        _surface.Children.Add(_previousFace); _surface.Children.Add(_face);
        var transforms = new TransformGroup(); transforms.Children.Add(_pointer); transforms.Children.Add(_reaction);
        _surface.RenderTransform = transforms;
        _button = new Button
        {
            Content = _surface, Width = FaceWidth, Height = FaceHeight,
            Style = (Style)FindResource("MascotButton"), Padding = new Thickness(0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetName(_button, "Фиткот: задачи и сообщения");
        _button.Click += (_, _) => RequestClickReaction();
        _button.MouseEnter += (_, _) => Pointer(-2);
        _button.MouseLeave += (_, _) => Pointer(0);
        _button.PreviewMouseLeftButtonDown += (_, _) => Pointer(1);
        _button.PreviewMouseLeftButtonUp += (_, _) => Pointer(_button.IsMouseOver ? -2 : 0);
        Children.Add(_button); Children.Add(_smoke);
        Loaded += (_, _) =>
        {
            if (_sprite >= 0) RefreshImage();
            ApplyRequestedMood();
            ScheduleVariation();
        };
        Unloaded += (_, _) =>
        {
            _variation.Stop(); _deferredMood.Stop(); _smoke.Stop();
            _pendingClick = _activeClick = null; _clickPoseHeld = false;
            _crossfadeVersion++; _crossfading = false;
        };
        _deferredMood.Tick += (_, _) => ApplyRequestedMood();
        _variation.Tick += (_, _) =>
        {
            if (IsVisible && !_button.IsMouseOver) SetMood(Mood, true);
            ScheduleVariation();
        };
    }

    private static Image Face() => new()
    {
        Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false
    };

    public void SetMood(CatMood mood, bool force = false)
    {
        var changed = Mood != mood;
        Mood = mood;
        if (changed || force)
        {
            _clickPoseHeld = false; _pendingClick = _activeClick = null;
        }
        if (changed) _pendingVariation = force;
        else _pendingVariation |= force;
        if (mood != CatMood.Celebration) _celebrateAfterChange = false;
        if (IsLoaded) ApplyRequestedMood();
    }

    // One latest request replaces any intermediate state. Loading/resetting the
    // diary must not replay Thinking -> Idle -> Thinking as visible animations.
    private void ApplyRequestedMood()
    {
        _deferredMood.Stop();
        if (!IsLoaded || _clickPoseHeld) return;
        if (_sprite >= 0)
        {
            if (CatSprites.All[_sprite].Mood == Mood && !_pendingVariation) return;
            var remaining = (_hasTransitioned ? 1200 : 2000) - (Environment.TickCount64 - _changedAt);
            if (_crossfading) remaining = Math.Max(remaining, 100);
            if (remaining > 0)
            {
                _deferredMood.Interval = TimeSpan.FromMilliseconds(remaining);
                _deferredMood.Start(); return;
            }
        }
        var candidates = CatSprites.All.Select((sprite, index) => (sprite, index))
            .Where(item => item.sprite.Mood == Mood && item.index != _sprite)
            .Select(item => item.index).ToArray();
        if (candidates.Length == 0) return;
        var selected = candidates[Random.Shared.Next(candidates.Length)];
        _pendingVariation = false;
        ChangeImage(selected);
        ScheduleVariation();
    }

    private void RequestClickReaction()
    {
        PlayTickle(); Tickled?.Invoke();
        _variation.Stop(); _deferredMood.Stop();
        _pendingVariation = _celebrateAfterChange = false;
        _clickPoseHeld = true;
        // A rapid series keeps only its newest choice; there is never a backlog
        // of old quips appearing against an unrelated image.
        _pendingClick = CatReactions.Choose(_sprite, _pendingClick?.Sprite ?? -1);
        if (!_crossfading) StartPendingClick();
    }

    private void StartPendingClick()
    {
        if (_pendingClick is not { } reaction) return;
        _pendingClick = null; _activeClick = reaction;
        ChangeImage(reaction.Sprite);
    }

    private void ScheduleVariation()
    {
        _variation.Stop();
        _variation.Interval = TimeSpan.FromSeconds(Random.Shared.Next(38, 56));
        _variation.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_sprite >= 0) { RefreshImage(); ApplyRequestedMood(); }
    }

    private BitmapSource Sized(int sprite)
    {
        var dpi = VisualTreeHelper.GetDpi(this); var key = (sprite, dpi.DpiScaleX, dpi.DpiScaleY);
        if (!_sized.TryGetValue(key, out var image))
        {
            var source = CatSprites.All[sprite];
            image = DisplayBitmap.Fit(source.Image, FaceWidth - 4, FaceHeight - 4, dpi, source.Mirror);
            _sized[key] = image;
        }
        return image;
    }

    private void RefreshImage()
    {
        _crossfadeVersion++; _crossfading = false;
        foreach (var image in new[] { _face, _shadow, _previousFace, _previousShadow }) image.BeginAnimation(OpacityProperty, null);
        _face.Opacity = _shadow.Opacity = 1; _previousFace.Opacity = _previousShadow.Opacity = 0;
        _face.Source = _shadow.Source = Sized(_sprite);
        _previousFace.Source = _previousShadow.Source = null;
        CompleteImageChange();
    }

    private void PublishIcon()
    {
        if (_sprite < 0 || _iconSprite == _sprite) return;
        CurrentIcon = CatTaskbarIcon.ForSprite(_sprite); _iconSprite = _sprite;
        IconChanged?.Invoke(CurrentIcon);
    }

    private void CompleteImageChange()
    {
        PublishIcon();
        var reaction = _activeClick; _activeClick = null;
        if (_pendingClick is not null) { StartPendingClick(); return; }
        if (_clickPoseHeld)
        {
            if (reaction is not null && reaction.Sprite == _sprite)
            {
                CatReactions.Committed(reaction);
                Reacted?.Invoke(reaction.Phrase);
            }
            ScheduleVariation(); return;
        }
        if (_celebrateAfterChange && Mood == CatMood.Celebration)
        {
            _celebrateAfterChange = false; PlayCelebration();
        }
        ApplyRequestedMood();
    }

    private void ChangeImage(int selected)
    {
        var first = _sprite < 0;
        _changedAt = Environment.TickCount64;
        if (!first) _hasTransitioned = true;
        if (_sprite < 0 || !IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            _sprite = selected; RefreshImage();
            return;
        }
        _crossfading = true; var version = ++_crossfadeVersion;
        _previousFace.Source = _previousShadow.Source = _face.Source;
        _sprite = selected; _face.Source = _shadow.Source = Sized(selected);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _smoke.Play();
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(440)) { EasingFunction = easing, BeginTime = TimeSpan.FromMilliseconds(70) };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(440)) { EasingFunction = easing, BeginTime = TimeSpan.FromMilliseconds(70) };
        // During the wisp's first breath only the previous pose is visible.
        foreach (var image in new[] { _face, _shadow, _previousFace, _previousShadow }) image.BeginAnimation(OpacityProperty, null);
        _face.Opacity = _shadow.Opacity = 0; _previousFace.Opacity = _previousShadow.Opacity = 1;
        fadeIn.Completed += (_, _) =>
        {
            if (version != _crossfadeVersion) return;
            // Retire the fade clocks once the new pose is committed. Resting
            // sprites render directly at their DPI size, with no animated opacity layer.
            foreach (var image in new[] { _face, _shadow, _previousFace, _previousShadow }) image.BeginAnimation(OpacityProperty, null);
            _face.Opacity = _shadow.Opacity = 1; _previousFace.Opacity = _previousShadow.Opacity = 0;
            _crossfading = false; _previousFace.Source = _previousShadow.Source = null;
            CompleteImageChange();
        };
        _face.BeginAnimation(OpacityProperty, fadeIn);
        _shadow.BeginAnimation(OpacityProperty, fadeIn.Clone());
        _previousFace.BeginAnimation(OpacityProperty, fadeOut);
        _previousShadow.BeginAnimation(OpacityProperty, fadeOut.Clone());
    }

    private void Pointer(double offset)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _pointer.Y = Math.Round(offset * dpi.DpiScaleY) / dpi.DpiScaleY;
    }

    private static DoubleAnimationUsingKeyFrames Motion(params (int Time, double Value)[] points)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var point in points)
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(point.Value,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(point.Time)), new KeySpline(.25, 0, .3, 1)));
        return animation;
    }

    private void PlayTickle()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        // Move the complete miniature, including its shadow, without scaling its pixels.
        _reaction.BeginAnimation(TranslateTransform.XProperty, Motion((0, _reaction.X), (65, -3), (130, 3), (195, -2), (270, 0)));
        _reaction.BeginAnimation(TranslateTransform.YProperty, Motion((0, _reaction.Y), (100, -2), (270, 0)));
    }

    public void PlayCelebration()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        if (Mood == CatMood.Celebration && (_sprite < 0 || CatSprites.All[_sprite].Mood != CatMood.Celebration || _crossfading))
        {
            _celebrateAfterChange = true; return;
        }
        _reaction.BeginAnimation(TranslateTransform.XProperty, Motion((0, _reaction.X), (260, 0)));
        _reaction.BeginAnimation(TranslateTransform.YProperty, Motion((0, _reaction.Y), (150, -9), (340, 0), (460, -3), (620, 0)));
    }
}
