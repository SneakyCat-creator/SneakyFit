using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private sealed record PlanDrag(DateOnly Date, Guid EntryId, Food Food, decimal Grams);
    private static void MakeDraggable(FrameworkElement element, object value)
    {
        Point start = default; var dragged = false;
        element.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(element); dragged = false; };
        element.PreviewMouseMove += (_, e) => { if (!dragged && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(element) - start).Length > 7) { dragged = true; DragDrop.DoDragDrop(element, new DataObject("SneakyFitFood", value), DragDropEffects.Copy | DragDropEffects.Move); e.Handled = true; } };
        element.PreviewMouseLeftButtonUp += (_, e) => { if (dragged) e.Handled = true; };
        element.Cursor = Cursors.Hand;
    }
    private UIElement DiaryThreeDays()
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); var start = _session.SelectedDate; var today = DateOnly.FromDateTime(DateTime.Now);
        var favorites = new WrapPanel();
        foreach (var food in _session.Data.Foods.Where(f => f.Favorite).Take(6))
        {
            var chip = Btn(food.Name + "  +", () => Drawer(food.Name, PortionPanel(food, start, planned: start > today, drawer: true)), "#FFFFFF", "#655F72"); chip.FontSize = BodySize; chip.Padding = ControlPadding; chip.Margin = new Thickness(0, 0, 8, 8); MakeDraggable(chip, food); favorites.Children.Add(chip);
        }
        root.Children.Add(M(favorites, bottom: 13));
        var columns = new List<UIElement>();
        for (int i = 0; i < 3; i++)
        {
            var date = start.AddDays(i); var day = _session.Day(date); var total = day.Total(EntryKind.Planned);
            var column = S(Between(S(T(DayName(date), HeadingSize, Ink, true), M(T(date.ToString("d MMMM", Ru), CaptionSize, Muted), top: 4)), Tag(N(total.Calories + day.Total(EntryKind.Eaten).Calories) + " ккал")), M(Tag(Outcome(day), "#EAE3F5", "#655F72"), top: 13, bottom: 18));
            for (int n = 0; n < day.Meals.Count; n++)
            {
                var meal = day.Meals[n]; string[] colors = ["#FFF0CF", "#FFE5DC", "#DEF3EB", "#E8E0FC"];
                var entries = S(Between(T(meal.Name, SubheadingSize, Ink, true), T(meal.Time.ToString("HH:mm"), CaptionSize, "#65546F")));
                foreach (var entry in meal.Entries)
                {
                    var food = _session.Data.Foods.FirstOrDefault(f => f.Id == entry.Food.SourceFoodId) ?? new Food(entry.Food.SourceFoodId, entry.Food.Name, "", entry.Food.Per100);
                    var row = S(T(entry.Food.Name, BodySize, Ink, true), M(T(N(entry.Grams) + " г · " + N(entry.Nutrition.Calories) + " ккал" + (entry.Kind == EntryKind.Planned ? " · план" : " · съедено"), CaptionSize, "#65546F"), top: 4));
                    var label = Surface(Between(row, Round("×", () => Safe(() => _session.RemoveEntry(date, entry.Id)), "#00FFFFFF", "#A08AA7")), "#A0FFFFFF", 11, 13);
                    if (entry.Kind == EntryKind.Planned) MakeDraggable(label, new PlanDrag(date, entry.Id, food, entry.Grams)); entries.Children.Add(M(label, top: 9));
                }
                if (date <= today && meal.Entries.Any(e => e.Kind == EntryKind.Planned)) entries.Children.Add(M(Btn("✓  Съедено", () => Safe(() => _session.EatPlannedMeal(date, meal.TemplateId)), "#D1ECD9", "#398568"), top: 10));
                var add = Btn("+  Добавить", () => FoodDrawer(meal.TemplateId, date, date > today), "#80FFFFFF"); add.Padding = ControlPadding; add.FontSize = BodySize; entries.Children.Add(M(add, top: 11));
                var tile = Surface(entries, colors[n % 4], 15, 20); AllowMealDrop(tile, date, meal.TemplateId);
                tile.Cursor = Cursors.Hand;
                tile.MouseLeftButtonUp += (_, e) => { if (!IsInsideButton(e.OriginalSource as DependencyObject)) MealDrawer(date, meal.TemplateId); };
                column.Children.Add(M(tile, bottom: 12));
            }
            var copyDate = date; column.Children.Add(Btn("Взять меню из…  ↙", () => CopyMenuDrawer(copyDate), "#FFFFFF"));
            columns.Add(Surface(new ScrollViewer { Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, "#F0EBF9", 18, 26));
        }
        var days = Equal(columns.ToArray()); Grid.SetRow(days, 1); root.Children.Add(days);
        return root;
    }

    private void AllowMealDrop(FrameworkElement tile, DateOnly date, Guid mealId)
    {
        tile.AllowDrop = true;
        tile.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("SneakyFitFood") ? e.Data.GetData("SneakyFitFood") is PlanDrag ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        tile.Drop += (_, e) => Safe(() =>
        {
            var data = e.Data.GetData("SneakyFitFood");
            if (data is Food food) _session.AddFoodToMeal(date, mealId, food, _lastPortions.GetValueOrDefault(food.Id, 100), date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten);
            if (data is PlanDrag item) _session.MovePlannedEntry(item.Date, item.EntryId, date, mealId);
            e.Handled = true;
        });
    }

    private void CopyMenuDrawer(DateOnly to)
    {
        var source = new DatePicker { SelectedDate = to.AddDays(-1).ToDateTime(TimeOnly.MinValue) };
        var replace = new CheckBox { Content = "Заменить запланированное", Margin = new Thickness(0, 18, 0, 18) };
        var preview = S();
        void Preview()
        {
            preview.Children.Clear();
            if (source.SelectedDate is not { } date) return;
            foreach (var meal in _session.Day(DateOnly.FromDateTime(date)).Meals.Where(m => m.Entries.Count > 0))
                preview.Children.Add(M(S(T(meal.Name, SubheadingSize, Ink, true), M(T(string.Join(" · ", meal.Entries.Select(e => e.Food.Name)), BodySize, Muted), top: 5)), bottom: 18));
        }
        source.SelectedDateChanged += (_, _) => Preview(); Preview();
        Drawer("Меню на " + to.ToString("d MMMM", Ru), S(Field("Из какого дня", source), replace, preview), Primary("Скопировать меню", () => Safe(() =>
        {
            if (source.SelectedDate is not { } date) throw new ArgumentException("Выберите день");
            _session.CopyMenu(DateOnly.FromDateTime(date), to, replace.IsChecked == true); CloseDrawer(); Notify("Меню скопировано ✓");
        })));
    }
}
