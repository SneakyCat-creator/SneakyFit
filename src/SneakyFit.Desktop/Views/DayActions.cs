using System.Windows;
using System.Windows.Controls;
using SneakyFit.Application;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private void MealDrawer(DateOnly date, Guid templateId)
    {
        var day = _session.Day(date); var meal = day.Meals.First(m => m.TemplateId == templateId);
        if (meal.IsSkipped)
        {
            Drawer(meal.Name, T("Приём пропущен", HeadingSize, "#65546F"), Primary("Возобновить приём", () => Safe(() =>
            {
                _session.SetMealSkipped(date, templateId, false); CloseDrawer();
            })));
            return;
        }
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
        var saved = _session.Data.Foods.FirstOrDefault(f => f.Id == entry.Food.SourceFoodId);
        var food = new Food(entry.Food.SourceFoodId, entry.Food.Name, "", entry.Food.Per100, saved?.Favorite ?? false)
        {
            Unit = entry.Food.Unit, GramsPerMillilitre = entry.Food.GramsPerMillilitre, Details = entry.Food.Details,
            Servings = saved?.Unit == entry.Food.Unit ? saved.Servings : []
        };
        ProductDrawer(SneakyFit.Application.CatalogProduct.FromFood(food), food, date, templateId, entry: entry);
    }

    private void EditMealTime(DateOnly date, Guid templateId)
    {
        var day = _session.Day(date); var meal = day.Meals.First(m => m.TemplateId == templateId); var time = Input(meal.Time.ToString("HH:mm"));
        Drawer("Время приёма пищи", Field("Время", time), Primary("Сохранить", () => Safe(() => { if (!TimeOnly.TryParse(time.Text, out var value)) throw new ArgumentException("Например, 08:30"); var fresh = _session.Day(date); _session.UpdateDay(fresh with { Meals = fresh.Meals.Select(m => m.TemplateId == templateId ? m with { Time = value } : m).ToList() }); BackDrawer(); MealDrawer(date, templateId); })));
    }

    private void BalanceDrawer() => ActivityDrawer();
    private static string ActivityValue(decimal kcal, string prefix) => kcal is > 0 and < 1 ? "<1" : prefix + N(kcal);
    private static string ActivityEnergy(decimal kcal, string prefix) => ActivityValue(kcal, prefix) + " ккал";

    private void ActivityDrawer()
    {
        if (_flushDrawer?.Invoke() == false) return;
        var date = _session.SelectedDate; var day = _session.Day(date);
        var future = date > DateOnly.FromDateTime(DateTime.Now);
        var weight = SmartTargets.ActivityWeightAt(_session.Data, date);
        var ledger = BalanceLedger.Before(_session.Data.Days, date);
        var walks = day.Activities.Where(a => a.Kind == ActivityKind.Steps).ToList();
        var savedSteps = walks.Sum(a => a.Steps);
        decimal? savedExpense = walks.Any(a => a.EstimatedCalories is null) ? null : walks.Sum(a => a.EstimatedCalories ?? 0);
        var steps = Input(savedSteps.ToString(), metric: true);
        var stepsEstimate = T("", PageHeadingSize, Mint, true);
        var stepSetup = Link("", "Настроить расчёт шагов", StepRuleDrawer, size: SubheadingSize);
        var duration = Input("30"); var exercise = Select(["Силовая тренировка", "Эллипс", "Домашняя тренировка", "Аэробика", "Плавание", "Велосипед"]);
        decimal[] mets = [3.5m, 5m, 3.8m, 7.3m, 5.8m, 4m];
        var workoutEstimate = T("", PageHeadingSize, Coral, true);
        var stepsEdited = false;
        bool TryStepCount(out int count)
        {
            var digits = steps.Text.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
            return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count) && count <= 200000;
        }
        void Estimate()
        {
            var valid = TryStepCount(out var count);
            var kcal = stepsEdited ? SmartTargets.StepCalories(_session.Data, date, count) : savedExpense;
            var needsRate = valid && kcal is null;
            stepsEstimate.Text = valid && kcal is { } stepEnergy ? ActivityEnergy(stepEnergy, "≈ ") : "—";
            stepsEstimate.Visibility = needsRate ? Visibility.Collapsed : Visibility.Visible;
            stepSetup.Visibility = needsRate ? Visibility.Visible : Visibility.Collapsed;
            try { workoutEstimate.Text = SmartTargets.WorkoutCalories(weight, mets[exercise.SelectedIndex], (int)Num(duration, "Минуты", 1, 600)) is { } workoutEnergy ? ActivityEnergy(workoutEnergy, "≈ ") : ""; } catch { workoutEstimate.Text = "—"; }
        }
        steps.TextChanged += (_, _) => { stepsEdited = true; Estimate(); };
        duration.TextChanged += (_, _) => Estimate(); exercise.SelectionChanged += (_, _) => Estimate(); Estimate();
        var stepSettings = Round("⚙", StepRuleDrawer, "#FFFFFF");
        stepSettings.Width = stepSettings.Height = stepSettings.MinHeight = 32; stepSettings.FontSize = 20;
        var stepCard = Surface(S(Between(T("Шаги", HeadingSize, Ink, true), stepSettings), M(steps, top: 10, bottom: 8), stepsEstimate, stepSetup), "#E8F3EA", 14);
        var workoutSummary = weight is null ? Link("", "Добавьте вес в профиле", OpenProfileWeight, " для расчёта расхода", SubheadingSize) : workoutEstimate;
        Guid? editing = null;
        var save = Primary(future ? "Запланировать тренировку" : "Добавить тренировку", () => Safe(() =>
        {
            if (_flushDrawer?.Invoke() == false) return;
            var minutes = Num(duration, "Минуты", 1, 600);
            if (minutes != decimal.Truncate(minutes)) throw new ArgumentException("Укажите целое число минут.");
            _session.SaveWorkout(date, exercise.SelectedItem.ToString()!, mets[exercise.SelectedIndex], (int)minutes, editing);
            ActivityDrawer();
        }));
        var cancelEdit = Btn("Отмена", ActivityDrawer); cancelEdit.Visibility = Visibility.Collapsed;
        var training = S(T("Тренировка", HeadingSize, Ink, true), M(Pair(Field("Занятие", exercise), Field("Минуты", duration), 3, 1, 12), top: 12), M(workoutSummary, bottom: 8), save, cancelEdit);
        var workouts = S();
        foreach (var activity in day.Activities.Where(a => a.Kind == ActivityKind.Workout))
        {
            var edit = Link("", activity.Name, () =>
            {
                editing = activity.Id; exercise.SelectedIndex = Math.Max(0, exercise.Items.IndexOf(activity.Name));
                duration.Text = activity.Minutes.ToString(); save.Content = "Сохранить тренировку";
                cancelEdit.Visibility = Visibility.Visible; duration.Focus(); duration.SelectAll();
            });
            var estimate = activity.EstimatedCalories is { } kcal ? N(kcal) + " ккал" : "расход после взвешивания";
            var details = activity.Minutes + " мин · " + estimate + (activity.Planned ? " · план" : "");
            var remove = Round("×", () => Safe(() =>
            {
                if (_flushDrawer?.Invoke() == false) return;
                var fresh = _session.Day(date);
                _session.UpdateDay(fresh with { Activities = fresh.Activities.Where(a => a.Id != activity.Id).ToList() });
                ActivityDrawer();
            }), "#FFFFFF");
            workouts.Children.Add(M(Between(S(edit, M(T(details, CaptionSize, Muted), top: 3)), remove), bottom: 8));
        }
        var workoutHistory = new ScrollViewer { Content = workouts, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (workouts.Children.Count > 0) training.Children.Add(M(workoutHistory, top: 8));
        if (!future && day.Activities.Any(a => a.Planned))
            training.Children.Add(M(Btn("Отметить план активности выполненным", () => Safe(() =>
            {
                if (_flushDrawer?.Invoke() == false) return;
                _session.ConfirmActivityPlan(date); ActivityDrawer();
            })), top: 8));

        var balance = S(
            T("Баланс недели", HeadingSize, Ink, true),
            M(T(Sign(ledger.FoodReserve) + " ккал", MetricSize, Purple, true), top: 8),
            M(T("Баланс еды за 7 дней", CaptionSize, Muted), top: 2, bottom: 16),
            Surface(S(
                Between(T("Шаги", BodySize, Ink), T(ActivityValue(ledger.StepsCredit, "+"), SubheadingSize, Mint, true)),
                M(Between(T("Тренировки", BodySize, Ink), T(ActivityValue(ledger.WorkoutsCredit, "+"), SubheadingSize, Mint, true)), top: 8)
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
                if (!TryStepCount(out var count)) throw new ArgumentException("Шаги: целое число от 0 до 200 000.");
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
        var rate = Input(SmartTargets.StepRate(_session.Data, _session.SelectedDate) is { } currentRate ? N(currentRate, "0.#") : ""); rate.IsEnabled = automatic.IsChecked != true;
        automatic.Checked += (_, _) => rate.IsEnabled = false; automatic.Unchecked += (_, _) => rate.IsEnabled = true;
        var fields = S(automatic, Field("Ккал на 1 000 шагов", rate));
        var weightMissing = SmartTargets.ActivityWeightAt(_session.Data, _session.SelectedDate) is null;
        if (profile.HeightCm <= 0 || weightMissing)
        {
            var missing = profile.HeightCm > 0 ? "вес" : weightMissing ? "рост и вес" : "рост";
            fields.Children.Add(M(Link("Для авторасчёта ", "укажите " + missing + " в профиле", () => OpenProfileField(focusHeight: !weightMissing)), top: 10));
        }
        Drawer("Расчёт шагов", fields, Primary("Сохранить", () => Safe(() =>
        {
            _session.SaveProfile(_session.Data.Profile with { StepsCaloriesPer1000 = automatic.IsChecked == true ? null : Num(rate, "Расход", 0, 300) });
            var day = _session.Day(_session.SelectedDate); if (day.Activities.Any(a => a.Kind == ActivityKind.Steps && (!a.Planned || day.Date > DateOnly.FromDateTime(DateTime.Now)))) _session.SaveSteps(day.Date, day.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => a.Steps));
            BackDrawer(); ActivityDrawer();
        })));
    }
}
