using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SneakyFit.Application;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop.Controls;

public sealed class FitCoach : Grid
{
    private enum Phase { Idle, Checking, Tasks, Completion, Insight, Chatter, Farewell }
    private const double CardWidth = 430, MessageHeight = 72;
    private static readonly FontFamily Paws = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Sneaky Paws");
    private readonly FitCat _cat = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid _card = new() { Width = CardWidth, MinHeight = 112, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 98, 22), Visibility = Visibility.Collapsed };
    private readonly Grid _tasks = new();
    private readonly Viewbox _checklist;
    private readonly StackPanel _body;
    private readonly Border _separator;
    private readonly TextBlock _message = T("", BodySize);
    private readonly Border _messageArea;
    private readonly ContentControl _status = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 98, 30), IsHitTestVisible = false };
    private readonly DispatcherTimer _transient = new();
    private readonly DispatcherTimer _tickle = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<(AdviceTask Task, bool Done)> _items = [];
    private Phase _phase;
    private bool _warned, _allClearShown, _visible;
    private int _cardVersion, _messageVersion;
    private string _layoutKey = "";
    private string _baseMessage = "";
    public event Action<AdviceTask>? TaskSelected;
    public event Action? Dismissed;
    public event Action? CelebrationFinished;
    public event Action? InsightFinished;
    public BitmapSource? CurrentCatIcon => _cat.CurrentIcon;
    public event Action<BitmapSource>? CatIconChanged
    {
        add => _cat.IconChanged += value;
        remove => _cat.IconChanged -= value;
    }

    public FitCoach()
    {
        Width = CardWidth + 98; Height = 390; ClipToBounds = false; UseLayoutRounding = true;
        _checklist = new Viewbox { Child = _tasks, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Stretch };
        _separator = new Border { Height = 1, Margin = new Thickness(0, 7, 12, 9), Background = B("#1B8B69B7"), Visibility = Visibility.Collapsed };
        _message.LineHeight = 24; _message.Margin = new Thickness(0); _message.VerticalAlignment = VerticalAlignment.Center;
        _message.TextAlignment = TextAlignment.Center;
        _messageArea = new Border { Height = MessageHeight, Child = _message };
        _body = S(_checklist, _separator, _messageArea);
        _body.VerticalAlignment = VerticalAlignment.Top;
        _body.HorizontalAlignment = HorizontalAlignment.Left; _body.Margin = CatDialogFrame.ContentInsets;
        // Shadow lives underneath; foreground text and artwork are never blurred.
        var shadow = new CatDialogFrame
        {
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(84, 60, 123), BlurRadius = 14, ShadowDepth = 3, Opacity = .14, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality },
            IsHitTestVisible = false
        };
        _card.Children.Add(shadow); _card.Children.Add(new CatDialogFrame()); _card.Children.Add(_body);
        var cross = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11"), Width = 12, Height = 12,
            Stroke = B("#E03651"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var close = QuietButton(cross, Close);
        close.Width = close.Height = 28; close.MinHeight = 0; close.Padding = new Thickness(0);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = CatDialogFrame.CloseInsets; AutomationProperties.SetName(close, "Закрыть сообщение фиткота");
        _card.Children.Add(close);
        Children.Add(_card); Children.Add(_cat); Children.Add(_status);
        _cat.Reacted += OnTickle;
        _transient.Tick += (_, _) => FinishTransient();
        _tickle.Tick += (_, _) =>
        {
            _tickle.Stop();
            if (_phase == Phase.Chatter) { _phase = Phase.Idle; HideCard(); }
            else if (_phase == Phase.Tasks) Say(FitCatPhrases.Welcome());
            else SetMessage(_baseMessage, _phase == Phase.Completion);
        };
        Unloaded += (_, _) => { _transient.Stop(); _tickle.Stop(); };
    }

    private static Button QuietButton(UIElement content, Action action)
    {
        var surface = new FrameworkElementFactory(typeof(Border)); surface.Name = "Surface";
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent); surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        surface.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        foreach (var (property, color) in new[] { (IsMouseOverProperty, "#168B69B7"), (Button.IsPressedProperty, "#258B69B7"), (IsKeyboardFocusedProperty, "#168B69B7") })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, B(color), "Surface")); template.Triggers.Add(trigger);
        }
        var button = new Button { Content = content, Template = template, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6), MinHeight = 38, Cursor = Cursors.Hand, FocusVisualStyle = null };
        button.Click += (_, _) => action(); return button;
    }

    public void ResetSession()
    {
        _transient.Stop(); _tickle.Stop(); _items.Clear(); _tasks.Children.Clear();
        _phase = Phase.Idle; _warned = _allClearShown = false; _baseMessage = ""; _layoutKey = "";
        _status.Content = null; _cardVersion++; _visible = false;
        _card.BeginAnimation(OpacityProperty, null);
        _card.Visibility = Visibility.Collapsed;
    }

    public void Checking()
    {
        ResetSession(); _phase = Phase.Checking;
        var spinner = new Ellipse { Width = 22, Height = 22, Stroke = B(Purple), StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection([2, 1, 2, 1, 1, 8]), RenderTransformOrigin = new Point(.5, .5) };
        var rotation = new RotateTransform(); spinner.RenderTransform = rotation;
        if (SystemParameters.ClientAreaAnimation) rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        _status.Content = spinner;
    }

    public void SetTasks(IReadOnlyList<(AdviceTask Task, bool Done)> items, bool reveal, bool allClear)
    {
        _status.Content = null;
        if (items.Count == 0)
        {
            if (_phase is Phase.Insight or Phase.Completion or Phase.Chatter or Phase.Farewell) return;
            _items.Clear();
            _phase = Phase.Idle; HideCard();
            if (allClear && !_allClearShown)
            {
                _allClearShown = true; _cat.SetMood(CatMood.Celebration); _cat.PlayCelebration();
                _transient.Interval = TimeSpan.FromSeconds(2.8); _transient.Start();
            }
            else if (!allClear) _cat.SetMood(CatMood.Idle);
            return;
        }
        var previous = _items.Where(i => i.Done).Select(i => i.Task.Id).ToHashSet();
        var changed = !_items.Select(i => (i.Task.Id, i.Task.Title, i.Done)).SequenceEqual(items.Select(i => (i.Task.Id, i.Task.Title, i.Done)));
        var entering = _phase != Phase.Tasks;
        _phase = Phase.Tasks; _transient.Stop(); _allClearShown = false;
        _items.Clear(); _items.AddRange(items);
        if (changed) RebuildTasks(previous);
        if (reveal || entering) { _warned = false; _tickle.Stop(); Say(FitCatPhrases.Welcome()); }
        else if (changed) { _warned = false; _tickle.Stop(); Say(FitCatPhrases.Welcome()); }
        _cat.SetMood(CatMood.Thinking); _separator.Visibility = Visibility.Visible; ShowCard();
    }

    private void RebuildTasks(HashSet<string> previouslyDone)
    {
        _tasks.Children.Clear();
        foreach (var item in _items)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new());
            var check = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(item.Done ? 0 : 1.2), BorderBrush = B("#9D8AB9"), Background = item.Done ? B("#D7EDE0") : B("#80FFFFFF"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            if (item.Done)
            {
                check.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 3,8 L 6.5,11.5 L 13,4"), Stroke = B("#3C8265"), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (!previouslyDone.Contains(item.Task.Id) && SystemParameters.ClientAreaAnimation) check.BeginAnimation(OpacityProperty, new DoubleAnimation(.1, 1, TimeSpan.FromMilliseconds(360)));
            }
            var label = T(item.Task.Title, 17, item.Done ? "#5A7566" : Ink);
            ApplyPaws(label); label.TextAlignment = TextAlignment.Left;
            row.Children.Add(check); Grid.SetColumn(label, 1); row.Children.Add(label);
            var link = QuietButton(row, () => { if (!item.Done) TaskSelected?.Invoke(item.Task); });
            link.Padding = new Thickness(3, 8, 7, 8); link.Margin = new Thickness(0, 1, 0, 1);
            AutomationProperties.SetName(link, (item.Done ? "Выполнено: " : "") + item.Task.Title);
            _tasks.Children.Add(link);
        }
        ArrangeChecklist();
    }

    // Opening a task or pressing Escape no longer changes the companion's geometry.
    public void Collapse() { }

    private void OnTickle(string reaction)
    {
        if (_phase == Phase.Checking) return;
        if (_phase is Phase.Idle or Phase.Chatter) { _transient.Stop(); _phase = Phase.Chatter; _items.Clear(); _tasks.Children.Clear(); _separator.Visibility = Visibility.Collapsed; }
        SetMessage(reaction); ShowCard(); _tickle.Stop(); _tickle.Start();
    }

    public void Celebrate()
    {
        if (_phase == Phase.Completion) return;
        _tickle.Stop(); _transient.Stop(); _phase = Phase.Completion; _warned = false;
        _allClearShown = true; _cat.SetMood(CatMood.Celebration, true); _cat.PlayCelebration();
        Say(FitCatPhrases.Completion(), praise: true); ShowCard();
        _transient.Interval = TimeSpan.FromSeconds(4.5); _transient.Start();
    }

    public void ShowInsight(FitInsight insight)
    {
        _transient.Stop(); _tickle.Stop(); _phase = Phase.Insight;
        _items.Clear(); _tasks.Children.Clear(); _separator.Visibility = Visibility.Collapsed;
        _warned = false; _status.Content = null;
        _allClearShown = true; _cat.SetMood(CatMood.Surprise, true); Say(FitCatPhrases.Insight(insight)); ShowCard();
        _transient.Interval = TimeSpan.FromSeconds(7); _transient.Start();
    }

    private void FinishTransient()
    {
        _transient.Stop(); _tickle.Stop(); var finished = _phase;
        _phase = Phase.Idle;
        HideCard(() =>
        {
            _items.Clear(); _tasks.Children.Clear();
            if (finished is Phase.Completion or Phase.Farewell) CelebrationFinished?.Invoke();
            else if (finished == Phase.Insight) InsightFinished?.Invoke();
            if (_phase == Phase.Idle) _cat.SetMood(CatMood.Idle);
        });
    }

    private void Say(string text, bool praise = false) { _baseMessage = text; SetMessage(text, praise); }

    private void SetMessage(string text, bool praise = false)
    {
        var version = ++_messageVersion;
        _message.BeginAnimation(OpacityProperty, null); _message.Opacity = 1;
        _message.Text = text;
        ApplyPaws(_message, praise);
        _message.Foreground = B(praise ? "#6B469F" : "#58416F");
        if (_visible && SystemParameters.ClientAreaAnimation)
        {
            var fade = new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => { if (version == _messageVersion) _message.BeginAnimation(OpacityProperty, null); };
            _message.BeginAnimation(OpacityProperty, fade);
        }
    }

    private static void ApplyPaws(TextBlock text, bool praise = false)
    {
        text.FontFamily = Paws; text.FontSize = praise ? 18 : 17;
        text.LineHeight = praise ? 25 : 24;
        text.FontWeight = praise ? FontWeights.SemiBold : FontWeights.Medium;
        Typography.SetContextualAlternates(text, true); Typography.SetStandardLigatures(text, true);
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(text, TextRenderingMode.ClearType);
    }

    private void Close()
    {
        if (_items.Any(i => !i.Done) && !_warned)
        {
            _warned = true; _tickle.Stop(); Say("Ещё есть незаполненные записи. Отложим? Нажми × ещё раз."); return;
        }
        if (_phase is Phase.Insight or Phase.Completion) { FinishTransient(); return; }
        if (_items.Count == 0) { _transient.Stop(); _tickle.Stop(); _phase = Phase.Idle; HideCard(); _cat.SetMood(CatMood.Idle); return; }
        _transient.Stop(); _tickle.Stop(); _phase = Phase.Farewell; _warned = false; Dismissed?.Invoke();
        _cat.SetMood(CatMood.Idle); Say("Договорились. Я рядом, если понадоблюсь."); ShowCard();
        _transient.Interval = TimeSpan.FromSeconds(1.8); _transient.Start();
    }

    private void ArrangeChecklist()
    {
        var columns = Math.Max(1, _tasks.ColumnDefinitions.Count);
        _tasks.RowDefinitions.Clear();
        var rows = (int)Math.Ceiling((double)_tasks.Children.Count / columns);
        for (var row = 0; row < rows; row++) _tasks.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < _tasks.Children.Count; index++)
        {
            var link = (FrameworkElement)_tasks.Children[index];
            Grid.SetRow(link, index % rows); Grid.SetColumn(link, index / rows);
            link.Margin = new Thickness(0, 1, index / rows < columns - 1 ? 14 : 0, 1);
        }
    }

    // Geometry depends on the checklist, never on a quip, font or tickle animation.
    private void LayoutCard()
    {
        var key = _items.Count > 0 ? string.Join("|", _items.Select(item => item.Task.Id)) : _phase == Phase.Insight ? "insight" : "chat";
        if (_visible && _layoutKey == key) return;
        _layoutKey = key;
        var window = Window.GetWindow(this);
        var availableHeight = Math.Max(320, (window?.ActualHeight ?? 800) - 90);
        var availableWidth = Math.Max(CardWidth, (window?.ActualWidth ?? 1120) - 140);
        var hasTasks = _items.Count > 0;
        var padding = hasTasks ? CatDialogFrame.ContentInsets : CatDialogFrame.CompactContentInsets;
        _body.Margin = padding;
        // The artwork needs asymmetric safe insets for its tail. Centre only
        // the speech within the frame; task links keep their left alignment.
        _messageArea.Margin = new Thickness(Math.Max(0, padding.Right - padding.Left), 0, 0, 0);
        _messageArea.Height = hasTasks ? MessageHeight : 48;
        if (!hasTasks && _phase == Phase.Insight)
        {
            _message.Measure(new Size(CardWidth - 2 * Math.Max(padding.Left, padding.Right), double.PositiveInfinity));
            _messageArea.Height = Math.Max(48, Math.Ceiling(_message.DesiredSize.Height));
        }
        var reserved = padding.Top + padding.Bottom + _messageArea.Height + 17;
        var columns = 1;
        double listHeight;
        do
        {
            _card.Width = Math.Min(availableWidth, columns == 1 ? CardWidth : CardWidth + (columns - 1) * 294);
            _body.Width = _card.Width - padding.Left - padding.Right;
            _tasks.Width = _body.Width;
            _tasks.ColumnDefinitions.Clear();
            for (var index = 0; index < columns; index++) _tasks.ColumnDefinitions.Add(new());
            ArrangeChecklist(); _tasks.Measure(new Size(_body.Width, double.PositiveInfinity));
            listHeight = _tasks.DesiredSize.Height;
            if (listHeight + reserved <= availableHeight || columns >= 3) break;
            columns++;
        } while (true);
        _checklist.Height = Math.Min(listHeight, Math.Max(80, availableHeight - reserved));
        _checklist.Visibility = _tasks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _separator.Visibility = _tasks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _card.Height = padding.Top + padding.Bottom + _messageArea.Height + (_tasks.Children.Count > 0 ? _checklist.Height + 17 : 0);
        Width = _card.Width + 98; Height = _card.Height + 22;
    }

    private void ShowCard()
    {
        LayoutCard(); if (_visible) return;
        var wasHidden = _card.Visibility == Visibility.Collapsed;
        _visible = true; var version = ++_cardVersion; _card.IsHitTestVisible = true; _card.Visibility = Visibility.Visible;
        var from = wasHidden ? 0 : _card.Opacity;
        _card.BeginAnimation(OpacityProperty, null); _card.Opacity = 1;
        if (!SystemParameters.ClientAreaAnimation) return;
        var fade = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { if (version == _cardVersion) _card.BeginAnimation(OpacityProperty, null); };
        _card.BeginAnimation(OpacityProperty, fade);
    }

    private void HideCard(Action? afterHide = null)
    {
        if (!_visible) { afterHide?.Invoke(); return; }
        _visible = false; _card.IsHitTestVisible = false; var version = ++_cardVersion;
        if (!SystemParameters.ClientAreaAnimation) { _card.Visibility = Visibility.Collapsed; afterHide?.Invoke(); return; }
        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (version == _cardVersion) { _card.Visibility = Visibility.Collapsed; afterHide?.Invoke(); } };
        _card.BeginAnimation(OpacityProperty, fade);
    }
}
