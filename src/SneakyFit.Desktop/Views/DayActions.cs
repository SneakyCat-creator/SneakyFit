using System.Windows;
using System.Windows.Controls;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private void MealDrawer(DateOnly date, Guid templateId)
    {
        var day = _session.Day(date); var meal = day.Meals.First(m => m.TemplateId == templateId);
        var body = S(Between(T(N(meal.Total(date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten).Calories) + " ккал", MetricSize, Purple, true), Btn(meal.Time.ToString("HH:mm"), () => EditMealTime(date, templateId))));
        foreach (var entry in meal.Entries)
        {
            var edit = Btn("", () => EditEntryDrawer(date, templateId, entry.Id), "#00FFFFFF", Ink); edit.HorizontalContentAlignment = HorizontalAlignment.Stretch; edit.Padding = new Thickness(0);
            edit.Content = Between(S(T(entry.Food.Name, BodySize, Ink, true), M(T(N(entry.Grams, "0.##") + " " + FoodUnits.Symbol(entry.Food.Unit) + " · " + N(entry.Nutrition.Calories) + " ккал" + (entry.Kind == EntryKind.Planned ? " · в плане" : ""), CaptionSize, Muted), top: 4)), Icon("\uE70F", 16));
            body.Children.Add(M(Surface(edit, "#F3EEFC", 14, 16), top: 10));
        }
        if (meal.Entries.Count == 0) body.Children.Add(M(T("Здесь будет что-то вкусное", BodySize, Muted), top: 28));
        if (meal.Entries.Any(e => e.Kind == EntryKind.Planned) && date <= DateOnly.FromDateTime(DateTime.Now)) body.Children.Add(M(Btn("✓  Съедено по плану", () => Safe(() => { _session.EatPlannedMeal(date, templateId); CloseDrawer(); Notify("Приятного аппетита! ✓"); }), "#DDF2E6", "#398568"), top: 18));
        Drawer(meal.Name, body, Primary("+  Добавить еду", () => FoodDrawer(templateId, date)));
    }

    private void EditEntryDrawer(DateOnly date, Guid templateId, Guid entryId)
    {
        var entry = _session.Day(date).Meals.SelectMany(m => m.Entries).FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return;
        var name = Input(entry.Food.Name);
        var product = new Food(entry.Food.SourceFoodId, entry.Food.Name, "", entry.Food.Per100)
            { Unit = entry.Food.Unit, GramsPerMillilitre = entry.Food.GramsPerMillilitre,
                Servings = _session.Data.Foods.FirstOrDefault(f => f.Id == entry.Food.SourceFoodId && f.Unit == entry.Food.Unit)?.Servings ?? [] };
        var quantity = new FoodQuantityEditor(product, entry.Grams);
        var kcal = Input(N(entry.Food.Per100.Calories, "0.##")); var protein = Input(N(entry.Food.Per100.Protein, "0.##"));
        var fat = Input(N(entry.Food.Per100.Fat, "0.##")); var carbs = Input(N(entry.Food.Per100.Carbs, "0.##"));
        var kind = Select(["Съедено", "В плане"], entry.Kind == EntryKind.Eaten ? 0 : 1); kind.IsEnabled = date <= DateOnly.FromDateTime(DateTime.Now);
        var total = T("", SubheadingSize, Purple, true);
        void Estimate() { try { total.Text = N(quantity.BaseAmount * Num(kcal, "Ккал") / 100) + " ккал в порции"; } catch (ArgumentException) { total.Text = "—"; } }
        quantity.Changed += Estimate; kcal.TextChanged += (_, _) => Estimate(); Estimate();
        void ReturnToMeal() { BackDrawer(); MealDrawer(date, templateId); }
        var symbol = FoodUnits.Symbol(entry.Food.Unit);
        var body = S(Field("Название", name), Field("Порция", quantity), Field("Запись", kind), M(total, bottom: 16), T("На 100 " + symbol, BodySize, Ink, true), M(Pair(Field("Ккал", kcal), MacroField("Белки, г", protein)), top: 12), Pair(MacroField("Жиры, г", fat), MacroField("Углеводы, г", carbs)));
        Drawer("Изменить порцию", body, Pair(Btn("Удалить", () => Safe(() => { _session.RemoveEntry(date, entryId); ReturnToMeal(); }), "#F5E7E8", "#B54F5B"), Primary("Сохранить", () => Safe(() =>
        {
            _ = quantity.BaseAmount;
            var nutrition = new Nutrition(Num(kcal, "Ккал", 0, 1000), MacroValue(protein, "Белки"), MacroValue(fat, "Жиры"), MacroValue(carbs, "Углеводы"));
            nutrition = FoodUnits.ConvertNutrition(nutrition, product.Unit, quantity.SelectedUnit, quantity.GramsPerMillilitre);
            _session.EditEntry(date, entryId, name.Text, nutrition, quantity.SelectedAmount, kind.SelectedIndex == 0 ? EntryKind.Eaten : EntryKind.Planned, quantity.SelectedUnit, quantity.GramsPerMillilitre);
            RememberFoodDensity(product, quantity.GramsPerMillilitre);
            ReturnToMeal();
        }))));
    }

    private void EditMealTime(DateOnly date, Guid templateId)
    {
        var day = _session.Day(date); var meal = day.Meals.First(m => m.TemplateId == templateId); var time = Input(meal.Time.ToString("HH:mm"));
        Drawer("Время приёма пищи", Field("Время", time), Primary("Сохранить", () => Safe(() => { if (!TimeOnly.TryParse(time.Text, out var value)) throw new ArgumentException("Например, 08:30"); var fresh = _session.Day(date); _session.UpdateDay(fresh with { Meals = fresh.Meals.Select(m => m.TemplateId == templateId ? m with { Time = value } : m).ToList() }); BackDrawer(); MealDrawer(date, templateId); })));
    }

    private void BalanceDrawer() => ActivityDrawer();
    private void ActivityDrawer()
    {
        if (_flushDrawer?.Invoke() == false) return;
        var date = _session.SelectedDate; var day = _session.Day(date);
        var weight = SneakyFit.Application.SmartTargets.WeightAt(_session.Data, date);
        var ledger = BalanceLedger.Before(_session.Data.Days, date);
        var steps = Input(day.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => a.Steps).ToString(), metric: true);
        var stepsEstimate = T("", PageHeadingSize, Mint, true);
        var duration = Input("30"); var exercise = Select(["Силовая тренировка", "Эллипс", "Домашняя тренировка", "Аэробика", "Плавание", "Велосипед"]);
        decimal[] mets = [3.5m, 5m, 3.8m, 7.3m, 5.8m, 4m];
        var workoutEstimate = T("", PageHeadingSize, Coral, true);
        void Estimate()
        {
            stepsEstimate.Text = int.TryParse(steps.Text.Replace(" ", "").Replace("\u00A0", ""), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count <= 200000
                ? "≈ " + N(SneakyFit.Application.SmartTargets.StepCalories(_session.Data, date, count)) + " ккал" : "—";
            try { workoutEstimate.Text = weight is { } kg ? "≈ " + N(Math.Max(0, mets[exercise.SelectedIndex] - 1) * kg * Num(duration, "Минуты", 1, 600) / 60) + " ккал" : ""; } catch { workoutEstimate.Text = "—"; }
        }
        steps.TextChanged += (_, _) => Estimate(); duration.TextChanged += (_, _) => Estimate(); exercise.SelectionChanged += (_, _) => Estimate(); Estimate();
        void CanRecord() { if (date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Выберите сегодняшний или прошедший день."); }
        var stepSettings = Round("⚙", StepRuleDrawer, "#FFFFFF");
        stepSettings.Width = stepSettings.Height = stepSettings.MinHeight = 32; stepSettings.FontSize = 20;
        var stepCard = Surface(S(Between(T("Шаги", HeadingSize, Ink, true), stepSettings), M(steps, top: 10, bottom: 8), stepsEstimate), "#E8F3EA", 14);
        UIElement workoutSummary = weight is null ? Link("", "Добавьте вес в профиле", OpenProfileWeight, size: SubheadingSize) : workoutEstimate;
        var training = S(T("Тренировка", HeadingSize, Ink, true), M(Pair(Field("Занятие", exercise), Field("Минуты", duration), 3, 1, 12), top: 12), M(workoutSummary, bottom: 8), Primary("Добавить тренировку", () => Safe(() =>
        {
            if (_flushDrawer?.Invoke() == false) return;
            CanRecord(); if (weight is null) { OpenProfileWeight(); return; }
            var minutes = (int)Num(duration, "Минуты", 1, 600); var fresh = _session.Day(date);
            var entry = new ActivityEntry(Guid.NewGuid(), ActivityKind.Workout, exercise.SelectedItem.ToString()!, 0, minutes, Math.Round((mets[exercise.SelectedIndex] - 1) * weight.Value * minutes / 60), TimeOnly.FromDateTime(DateTime.Now));
            _session.UpdateDay(fresh with { Activities = [.. fresh.Activities, entry] }); ActivityDrawer();
        })));
        var workouts = S();
        foreach (var activity in day.Activities.Where(a => a.Kind == ActivityKind.Workout)) workouts.Children.Add(M(Between(S(T(activity.Name, BodySize, Ink, true), M(T(activity.Minutes + " мин · " + N(activity.EstimatedCalories) + " ккал", CaptionSize, Muted), top: 3)), Round("×", () => Safe(() =>
        {
            if (_flushDrawer?.Invoke() == false) return;
            var fresh = _session.Day(date); _session.UpdateDay(fresh with { Activities = fresh.Activities.Where(a => a.Id != activity.Id).ToList() }); ActivityDrawer();
        }), "#FFFFFF")), bottom: 8));
        var workoutHistory = new ScrollViewer { Content = workouts, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (workouts.Children.Count > 0) training.Children.Add(M(workoutHistory, top: 8));

        var balance = S(
            T("Баланс недели", HeadingSize, Ink, true),
            M(T(Sign(ledger.FoodReserve) + " ккал", MetricSize, Purple, true), top: 8),
            M(T("Баланс еды за 7 дней", CaptionSize, Muted), top: 2, bottom: 16),
            Surface(S(
                Between(T("Шаги", BodySize, Ink), T("+" + N(ledger.StepsCredit), SubheadingSize, Mint, true)),
                M(Between(T("Тренировки", BodySize, Ink), T("+" + N(ledger.WorkoutsCredit), SubheadingSize, Mint, true)), top: 8)
            ), "#E8F3EA", 14, 14)
        );

        foreach (var past in _session.Data.Days.Where(d => d.Date < date && d.Date >= date.AddDays(-7)).OrderByDescending(d => d.Date))
        {
            var historyRow = Between(
                S(
                    T(past.Date.ToString("ddd, d MMM", Ru), BodySize, Ink, true),
                    M(T(Outcome(_session.Day(past.Date)), CaptionSize, Muted), top: 2)
                ),
                M(T(past.HasFoodRecord ? Sign(past.Remaining) + " ккал" : "—", BodySize, past.Remaining < 0 ? Coral : Mint), left: 12)
            );
            balance.Children.Add(M(Surface(historyRow, "#FFFFFF", 12, 12), top: 8));
        }

        var movement = S(stepCard, M(Surface(training, "#FFF0EB", 14), top: 12));
        var balanceCard = Surface(balance, "#F3EEFC", 14, 18);

        if (!Drawer(date.ToString("d MMMM", Ru), Pair(movement, balanceCard, 1, 1, 24), width: 920)) return;

        // Only an extended workout history scrolls; all seven balance rows have room.
        var movementWidth = (DrawerPanel.Width - DrawerPanel.Padding.Left - DrawerPanel.Padding.Right - 24) / 2;
        movement.Measure(new Size(movementWidth, double.PositiveInfinity));
        var formHeight = movement.DesiredSize.Height - workoutHistory.DesiredSize.Height;
        workoutHistory.MaxHeight = Math.Clamp(DrawerScroll.MaxHeight - formHeight, 48, 160);
        var dirty = false;
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        bool SaveSteps()
        {
            debounce.Stop(); if (!dirty) return true;
            try
            {
                CanRecord();
                var digits = steps.Text.Replace(" ", "").Replace("\u00A0", "");
                if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count > 200000) throw new ArgumentException("Шаги: целое число от 0 до 200 000.");
                _session.SaveSteps(date, count); dirty = false; DrawerError.Text = ""; return true;
            }
            catch (Exception error) { DrawerError.Text = error.Message; return false; }
        }
        debounce.Tick += (_, _) => SaveSteps();
        steps.TextChanged += (_, _) => { dirty = true; debounce.Stop(); debounce.Start(); DrawerError.Text = ""; };
        steps.LostKeyboardFocus += (_, _) => SaveSteps();
        steps.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveSteps(); e.Handled = true; } };
        _flushDrawer = SaveSteps; _releaseDrawer = debounce.Stop;
    }

    private void StepRuleDrawer()
    {
        var profile = _session.Data.Profile; var automatic = new CheckBox { Content = "Рассчитывать по весу и росту", IsChecked = profile.StepsCaloriesPer1000 is null, Margin = new Thickness(0, 0, 0, 20) };
        var rate = Input(N(SneakyFit.Application.SmartTargets.StepRate(_session.Data, _session.SelectedDate), "0.#")); rate.IsEnabled = automatic.IsChecked != true;
        automatic.Checked += (_, _) => rate.IsEnabled = false; automatic.Unchecked += (_, _) => rate.IsEnabled = true;
        Drawer("Расчёт шагов", S(automatic, Field("Ккал на 1000 дополнительных шагов", rate)), Primary("Сохранить", () => Safe(() =>
        {
            _session.SaveProfile(_session.Data.Profile with { StepsCaloriesPer1000 = automatic.IsChecked == true ? null : Num(rate, "Расход", 0, 300) });
            var day = _session.Day(_session.SelectedDate); if (day.Activities.Any(a => a.Kind == ActivityKind.Steps)) _session.SaveSteps(day.Date, day.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => a.Steps));
            BackDrawer(); ActivityDrawer();
        })));
    }
}