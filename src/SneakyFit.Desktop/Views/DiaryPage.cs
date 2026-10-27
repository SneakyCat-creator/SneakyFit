using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private bool _calendarOpen;
    private bool _animateCalendar;
    private DateOnly _calendarMonth;
    private static readonly DateOnly FirstDiaryDate = new(1900, 1, 1), LastDiaryDate = new(9999, 12, 25);
    private void SelectDiaryDate(DateOnly date)
    {
        if (!CloseDrawer()) return;
        _calendarOpen = false;
        _session.SelectedDate = date < FirstDiaryDate ? FirstDiaryDate : date > LastDiaryDate ? LastDiaryDate : date;
        Render(); PageScroll.ScrollToTop();
    }
    private UIElement DiaryPage()
    {
        var selected = _session.SelectedDate; var today = DateOnly.FromDateTime(DateTime.Now);
        var strip = new Grid();
        strip.ColumnDefinitions.Add(new() { Width = new GridLength(ControlSize) });
        for (var i = 0; i < 7; i++) strip.ColumnDefinitions.Add(new());
        strip.ColumnDefinitions.Add(new() { Width = new GridLength(ControlSize) });
        var previous = Round("‹", () => SelectDiaryDate(selected.AddDays(-1)), "#EDE7FA"); previous.IsEnabled = selected > FirstDiaryDate; strip.Children.Add(previous);
        for (var offset = -3; offset <= 3; offset++)
        {
            var date = selected.AddDays(offset); var active = offset == 0;
            var button = Btn("", () => { if (active) { _calendarMonth = new(selected.Year, selected.Month, 1); _calendarOpen = !_calendarOpen; _animateCalendar = _calendarOpen; Render(); } else SelectDiaryDate(date); }, active ? Purple : "#EFEBF8", active ? "#FFFFFF" : Ink);
            button.Padding = new Thickness(5, _compactDiary ? 6 : 12, 5, _compactDiary ? 6 : 12); button.Margin = new Thickness(5, 0, 5, 0);
            var number = T(date.ToString("d MMMM", Ru), BodySize, active ? "#FFFFFF" : "#655F72", active);
            var label = T(date == today ? "Сегодня" : date.ToString("dddd", Ru), CaptionSize, active ? "#E0D1F5" : "#746A82"); number.TextAlignment = label.TextAlignment = TextAlignment.Center;
            button.Content = S(number, M(label, top: 5));
            var cell = new Grid(); cell.Children.Add(button);
            if (date == today) cell.Children.Add(new Border { BorderBrush = B(active ? "#FFFFFF" : "#9274BC"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(15), Margin = new Thickness(5, 0, 5, 0), IsHitTestVisible = false });
            if (date.DayOfWeek == DayOfWeek.Monday && offset > -3) cell.Children.Add(new Border { Width = 2, Height = 42, Background = B("#B8A7CE"), CornerRadius = new CornerRadius(1), Margin = new Thickness(-1, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false });
            Grid.SetColumn(cell, offset + 4); strip.Children.Add(cell);
        }
        var next = Round("›", () => SelectDiaryDate(selected.AddDays(1)), "#EDE7FA"); next.IsEnabled = selected < LastDiaryDate; Grid.SetColumn(next, 8); strip.Children.Add(next);
        var modes = H();
        foreach (var count in new[] { 1, 3 })
        {
            var active = _threeDays == (count == 3);
            var button = Btn(count == 1 ? "1 день" : "3 дня", () => { _threeDays = count == 3; Render(); }, active ? "#E5DBF7" : "#F5F3FC", active ? Purple : Muted);
            button.Padding = ControlPadding; button.Height = ControlSize; button.Margin = new Thickness(0, 0, 6, 0); modes.Children.Add(button);
        }
        if (selected != today) modes.Children.Add(M(Btn("К сегодняшнему дню", () => SelectDiaryDate(today), "#FFFFFF"), left: 8));
        var copy = Btn("Взять меню из…", () => CopyMenuDrawer(selected), "#EDE7FA", Ink); copy.Padding = ControlPadding; copy.Height = ControlSize;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.Children.Add(strip);
        var actions = M(Between(modes, copy), top: _compactDiary ? 6 : 12, bottom: _compactDiary ? 8 : 16); Grid.SetRow(actions, 1); root.Children.Add(actions);
        var diary = _threeDays ? DiaryThreeDays() : TodayPage(); Grid.SetRow(diary, 2); root.Children.Add(diary);
        if (_calendarOpen)
        {
            var dismiss = new Border { Background = System.Windows.Media.Brushes.Transparent };
            Grid.SetRow(dismiss, 1); Grid.SetRowSpan(dismiss, 2); Panel.SetZIndex(dismiss, 3);
            dismiss.MouseLeftButtonDown += (_, e) => { _calendarOpen = false; Render(); e.Handled = true; }; root.Children.Add(dismiss);
            var calendar = InlineCalendar(selected); calendar.HorizontalAlignment = HorizontalAlignment.Center; calendar.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(calendar, 1); Grid.SetRowSpan(calendar, 2); Panel.SetZIndex(calendar, 4); root.Children.Add(calendar);
            if (_animateCalendar) { _animateCalendar = false; UiMotion.Enter(calendar, UiMotionKind.Calendar); }
        }
        return root;
    }

    private FrameworkElement InlineCalendar(DateOnly selected)
    {
        var month = _calendarMonth; var today = DateOnly.FromDateTime(DateTime.Now);
        var content = S();
        var prev = Round("‹", () => { _calendarMonth = month.AddMonths(-1); Render(); }); prev.IsEnabled = month.Year > 1900 || month.Month > 1;
        var next = Round("›", () => { _calendarMonth = month.AddMonths(1); Render(); }); next.IsEnabled = month.Year < 9999 || month.Month < 12;
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(ControlSize) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(ControlSize) });
        var caption = T(month.ToString("MMMM yyyy", Ru), SubheadingSize, Ink, true); caption.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(prev); Grid.SetColumn(caption, 1); header.Children.Add(caption); Grid.SetColumn(next, 2); header.Children.Add(next); content.Children.Add(header);
        var days = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 12, 0, 8) };
        foreach (var name in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" }) days.Children.Add(new TextBlock { Text = name, TextAlignment = TextAlignment.Center, Foreground = B(Muted), FontSize = CaptionSize, Margin = new Thickness(0, 0, 0, 8) });
        var offset = ((int)month.DayOfWeek + 6) % 7;
        for (var i = 0; i < 42; i++)
        {
            var number = month.DayNumber + i - offset;
            if (number < FirstDiaryDate.DayNumber || number > LastDiaryDate.DayNumber) { days.Children.Add(new Border()); continue; }
            var date = DateOnly.FromDayNumber(number); var active = date == selected;
            var button = Btn(date.Day.ToString(), () => SelectDiaryDate(date), active ? Purple : "#00FFFFFF", active ? "#FFFFFF" : date.Month == month.Month ? Ink : Muted);
            button.Height = 34; button.MinHeight = 0; button.Padding = new Thickness(0); button.Margin = new Thickness(2);
            var cell = new Grid(); cell.Children.Add(button);
            if (date == today) cell.Children.Add(new Border { BorderBrush = B(active ? "#FFFFFF" : Purple), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(11), Margin = new Thickness(2), IsHitTestVisible = false });
            days.Children.Add(cell);
        }
        content.Children.Add(days); content.Children.Add(Btn("Сегодня", () => SelectDiaryDate(today), "#F0EBF8"));
        var card = Surface(content, "#FEFDFF", 16, 20); card.Width = 324; card.Margin = new Thickness(0, 8, 0, 0); GiveDepth(card, 20, .18); return card;
    }
}
