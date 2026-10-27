using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private UIElement TodayPage()
    {
        var date = _session.SelectedDate; var day = _session.Day(date); var future = date > DateOnly.FromDateTime(DateTime.Now); var total = day.Total(future ? EntryKind.Planned : EntryKind.Eaten); var budget = day.Budget; var norm = day.Target.Split.AtCalories(budget); var remaining = budget - total.Calories;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var ringScale = _compactDiary ? .66 : .78;
        var numbers = H(T(N(total.Calories), _compactDiary ? LargeMetricSize : HeroSize, "#FFFFFF", true), M(T("/ " + N(budget), _compactDiary ? HeadingSize : PageHeadingSize, "#D5C1FA"), top: _compactDiary ? 16 : 24, left: 9));
        var ring = new Grid { Width = 178, Height = 178, LayoutTransform = new ScaleTransform(ringScale, ringScale) }; ring.Children.Add(new EnergyRing { Fraction = (double)(total.Calories / budget) });
        var ringText = S(T(N(total.Calories / budget * 100) + "%", MetricSize, "#FFFFFF", true), T(future ? "в плане" : "съедено", CaptionSize, "#E5D8FA")); ringText.HorizontalAlignment = HorizontalAlignment.Center; ringText.VerticalAlignment = VerticalAlignment.Center; foreach (TextBlock text in ringText.Children) text.TextAlignment = TextAlignment.Center; ring.Children.Add(ringText);
        var balance = Tag((remaining < 0 ? "Сверх нормы  " : "Осталось  ") + N(Math.Abs(remaining)) + " ккал", "#DFEFB8", "#426545");
        UIElement Macro(string name, decimal value, decimal target, string color) => S(T(name, CaptionSize, "#E2D6F9"), M(H(T(N(value), PageHeadingSize, "#FFFFFF", true), M(T("/ " + N(target) + " г", CaptionSize, "#D7C6F5"), left: 5, top: 7)), top: 5, bottom: 10), Progress(value, target, color, "#956BDB", 8));
        var macroPanel = Equal(Macro("Белки", total.Protein, norm.Protein, "#CEFFB3"), Macro("Жиры", total.Fat, norm.Fat, "#FFDBAB"), Macro("Углеводы", total.Carbs, norm.Carbs, "#D7D3FF")); macroPanel.VerticalAlignment = VerticalAlignment.Center;
        var index = future ? null : SneakyFit.Application.ActivityIndex.ForDay(_session.Data, day);
        var activity = new Grid { Width = 178, Height = 178, LayoutTransform = new ScaleTransform(ringScale, ringScale), VerticalAlignment = VerticalAlignment.Center };
        activity.Children.Add(new EnergyRing { Fraction = index?.Fraction ?? 0, FillColor = "#BDF4DD" });
        var activityText = S(T(index is null ? "—" : index.Percent + "%", MetricSize, "#FFFFFF", true), T("активность", CaptionSize, "#E5D8FA"));
        activityText.HorizontalAlignment = HorizontalAlignment.Center; activityText.VerticalAlignment = VerticalAlignment.Center;
        foreach (TextBlock text in activityText.Children) text.TextAlignment = TextAlignment.Center;
        activity.Children.Add(activityText);
        var indexButton = Btn("", ActivityDrawer, "#00FFFFFF"); indexButton.Padding = new Thickness(0); indexButton.Content = activity; indexButton.IsEnabled = !future;
        System.Windows.Automation.AutomationProperties.SetName(indexButton, "Индекс активности " + (index is null ? "нет данных" : index.Percent + "%"));
        var circles = H(indexButton, M(ring, left: 12)); circles.VerticalAlignment = VerticalAlignment.Center;
        var energy = new Grid(); energy.ColumnDefinitions.Add(new() { Width = new GridLength(1.05, GridUnitType.Star) }); energy.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); energy.ColumnDefinitions.Add(new() { Width = new GridLength(1.65, GridUnitType.Star) });
        energy.Children.Add(M(S(T(future ? "Запланировано" : "Калории", BodySize, "#E9DDFB"), numbers, M(balance, top: _compactDiary ? 6 : 14)), right: 16));
        Grid.SetColumn(circles, 1); energy.Children.Add(circles); var macros = M(macroPanel, left: 26); Grid.SetColumn(macros, 2); energy.Children.Add(macros);
        var energySurface = Surface(energy, pad: _compactDiary ? 16 : 24, radius: 29); energySurface.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#A065E9"), (Color)ColorConverter.ConvertFromString("#7050CE"), 18); energySurface.MinHeight = _compactDiary ? 152 : 188;
        GiveDepth(energySurface, 16, .2);
        root.Children.Add(M(energySurface, bottom: _compactDiary ? 10 : 18));
        var steps = day.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => a.Steps); var workout = day.Activities.Where(a => a.Kind == ActivityKind.Workout).Sum(a => a.Minutes);
        var ledger = BalanceLedger.Before(_session.Data.Days, date);
        UIElement ActivityPart(string icon, string label, string number)
        {
            return Between(H(M(icon == "workout" ? WorkoutIcon() : Icon(icon, 20, "#79668F"), right: 10), T(label, BodySize, Ink, true)), M(T(number, CaptionSize, "#65546F"), left: 10, right: 16));
        }
        if (!future)
        {
            var segments = new Grid();
            UIElement[] items = [ActivityPart("\uE805", steps.ToString("N0", Ru) + " шагов", "+" + N(day.ActivityCalories(ActivityKind.Steps)) + " ккал"), ActivityPart("workout", workout > 0 ? workout + " мин тренировки" : "Тренировка", workout > 0 ? "+" + N(day.ActivityCalories(ActivityKind.Workout)) + " ккал" : "—"), ActivityPart("\uE8D4", "Баланс недели", Sign(ledger.FoodReserve) + " ккал")];
            for (var i = 0; i < items.Length; i++)
            {
                segments.ColumnDefinitions.Add(new());
                var item = new Border { Child = items[i], Padding = new Thickness(i == 0 ? 0 : 16, 0, 0, 0), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), BorderBrush = B("#D9D0E6") };
                Grid.SetColumn(item, i); segments.Children.Add(item);
            }
            var pills = Btn("", ActivityDrawer, "#EDE8F7", Ink); pills.HorizontalContentAlignment = HorizontalAlignment.Stretch; pills.Height = ControlSize + 6;
            pills.Content = Between(segments, Icon("\uE76C", 14, "#79668F"));
            System.Windows.Automation.AutomationProperties.SetName(pills, "Шаги, тренировки и баланс недели");
            pills.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(pills, 1); root.Children.Add(pills);
        }
        var meals = new System.Windows.Controls.Primitives.UniformGrid { Columns = day.Meals.Count > 4 ? 3 : Math.Max(1, day.Meals.Count) };
        for (var i = 0; i < day.Meals.Count; i++) meals.Children.Add(M(MealTile(day, day.Meals[i], i), right: (i + 1) % meals.Columns == 0 ? 0 : 15, bottom: day.Meals.Count > 4 ? 15 : 0));
        UIElement mealArea = meals;
        if (day.Meals.Count > 4) { meals.MinHeight = Math.Ceiling(day.Meals.Count / 3d) * 280; mealArea = new ScrollViewer { Content = meals, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }; }
        var mealRow = M(mealArea, top: 16, bottom: 5); Grid.SetRow(mealRow, 2); root.Children.Add(mealRow);
        return root;
    }

    private UIElement MealTile(DiaryDay day, Meal meal, int index)
    {
        string[] colors = ["#FFF0CF", "#FFE3DA", "#DDF2E9", "#E7E0FC"];
        var total = meal.Total(day.Date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten);
        var content = new Grid();
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var text = S(Between(T(meal.Name, HeadingSize, Ink, true), Tag(meal.Time.ToString("HH:mm"), "#85FFFFFF", "#65546F")), M(H(T(N(total.Calories), MetricSize, Ink, true), M(T("ккал", CaptionSize, "#65546F"), left: 6, top: 14)), top: 12)); content.Children.Add(text);
        var entries = S(); var entryScroll = new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 12, 0, 0), PanningMode = PanningMode.VerticalOnly }; Grid.SetRow(entryScroll, 1); content.Children.Add(entryScroll);
        foreach (var entry in meal.Entries)
        {
            var row = S(T(entry.Food.Name, BodySize, Ink, true), M(T(N(entry.Grams) + " г · " + N(entry.Nutrition.Calories) + " ккал" + (entry.Kind == EntryKind.Planned ? " · план" : ""), CaptionSize, "#65546F"), top: 4));
            entries.Children.Add(M(row, top: 4, bottom: 9));
        }
        var bottom = S(); Grid.SetRow(bottom, 2); content.Children.Add(bottom);
        bottom.Children.Add(new FoodPicture { Kind = index, Width = _compactDiary ? 78 : 96, Height = _compactDiary ? 52 : 72, HorizontalAlignment = HorizontalAlignment.Right });
        var add = Btn("+  Добавить", () => FoodDrawer(meal.TemplateId, day.Date), "#CFFFFFFF", Ink); add.Height = ControlSize; add.Margin = new Thickness(0, 8, 0, 0); bottom.Children.Add(add);
        var surface = Surface(content, colors[index % colors.Length], 20, 24); surface.Cursor = System.Windows.Input.Cursors.Hand;
        GiveDepth(surface, 12, .13);
        AllowMealDrop(surface, day.Date, meal.TemplateId);
        surface.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button && !IsInsideButton(e.OriginalSource as DependencyObject)) MealDrawer(day.Date, meal.TemplateId); };
        return surface;
    }
    private static bool IsInsideButton(DependencyObject? source) { while (source is not null) { if (source is Button) return true; source = source is System.Windows.Media.Visual ? VisualTreeHelper.GetParent(source) : null; } return false; }
}
