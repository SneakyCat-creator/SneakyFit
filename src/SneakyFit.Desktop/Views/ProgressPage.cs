using System.Windows;
using System.Windows.Controls;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private UIElement ProgressSection(string section, Action redraw)
    {
        var add = Primary(section == "Параметры" ? "+  Замер" : "+  Вес", () => MeasurementDrawer(section == "Параметры")); add.Visibility = section is "Вес" or "Параметры" ? Visibility.Visible : Visibility.Collapsed;
        var root = S(M(Between(H(T(section, HeadingSize, Ink, true), M(ProgressShareButton(section), left: 16)), add), bottom: 18));
        var chartPeriod = _chartPeriods.GetValueOrDefault(section, 14);
        var today = DateOnly.FromDateTime(DateTime.Now); var start = chartPeriod == 0 ? DateOnly.MinValue : today.AddDays(-chartPeriod + 1);
        var period = Select(["Неделя", "2 недели", "Месяц", "За всё время"], chartPeriod == 7 ? 0 : chartPeriod == 14 ? 1 : chartPeriod == 30 ? 2 : 3);
        period.Width = 156; period.Height = period.MinHeight = ControlSize; period.VerticalAlignment = VerticalAlignment.Center;
        period.SelectionChanged += (_, _) => { _chartPeriods[section] = period.SelectedIndex switch { 0 => 7, 1 => 14, 2 => 30, _ => 0 }; redraw(); };
        double[] values; string[] labels; string unit; string color = Purple; string label = section; bool bars = false;
        var body = new StackPanel();
        if (section == "Вес")
        {
            var data = _session.Data.Weight.Where(w => DateOnly.FromDateTime(w.RecordedAt) >= start && DateOnly.FromDateTime(w.RecordedAt) <= today).OrderBy(w => w.RecordedAt).ToArray(); values = data.Select(w => (double)w.Kilograms).ToArray(); labels = data.Select(w => w.RecordedAt.ToString("dd.MM")).ToArray(); unit = "кг";
        }
        else if (section == "Параметры")
        {
            var definitions = MeasurementCatalog.All.ToList(); var definition = definitions.First(d => d.Key == _measurementKey);
            var choice = Select(definitions.Select(d => d.Name), definitions.FindIndex(d => d.Key == _measurementKey));
            choice.SelectionChanged += (_, _) => { _measurementKey = definitions[choice.SelectedIndex].Key; _measurementSide = definitions[choice.SelectedIndex].Bilateral ? BodySide.Left : BodySide.None; redraw(); };
            body.Children.Add(M(choice, bottom: 18));
            if (definition.Bilateral) { if (_measurementSide == BodySide.None) _measurementSide = BodySide.Left; body.Children.Add(Tabs(["Левая", "Правая"], _measurementSide == BodySide.Left ? "Левая" : "Правая", side => { _measurementSide = side == "Левая" ? BodySide.Left : BodySide.Right; redraw(); })); }
            var data = _session.Data.Measurements.Where(m => m.Key == _measurementKey && m.Side == _measurementSide && DateOnly.FromDateTime(m.RecordedAt) >= start && DateOnly.FromDateTime(m.RecordedAt) <= today).OrderBy(m => m.RecordedAt).ToArray(); values = data.Select(m => (double)m.Value).ToArray(); labels = data.Select(m => m.RecordedAt.ToString("dd.MM")).ToArray(); unit = "см"; label = definition.Name; color = Mint;
        }
        else
        {
            var data = _session.Data.Days.Where(d => d.Date >= start && d.Date <= today && d.Status != DayStatus.Planned).OrderBy(d => d.Date).ToArray();
            if (section == "КБЖУ") body.Children.Add(M(Tabs(["Ккал", "Белки", "Жиры", "Углеводы"], _macroTab, tab => { _macroTab = tab; redraw(); }), bottom: 15));
            values = data.Select(d => section == "Активность" ? d.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => (double)a.Steps) : _macroTab switch { "Белки" => (double)d.Total(EntryKind.Eaten).Protein, "Жиры" => (double)d.Total(EntryKind.Eaten).Fat, "Углеводы" => (double)d.Total(EntryKind.Eaten).Carbs, _ => (double)d.Total(EntryKind.Eaten).Calories }).ToArray(); labels = data.Select(d => d.Date.ToString("dd.MM")).ToArray(); unit = section == "Активность" ? "шагов" : _macroTab == "Ккал" ? "ккал" : "г"; label = section == "Активность" ? "Шаги" : _macroTab; bars = true; color = section == "Активность" ? Coral : Purple;
        }
        var last = values.Length > 0 ? values[^1].ToString(bars ? "N0" : "0.0", Ru) : "—";
        body.Children.Add(Between(S(T(label, BodySize, Muted), M(H(T(last, LargeMetricSize, color, true), M(T(unit, BodySize, Muted), left: 10, top: 21)), top: 4)), period));
        body.Children.Add(M(values.Length == 0 ? T("Здесь появятся ваши записи", SubheadingSize, Muted) : new ProgressChart { Values = values, Labels = labels, Bars = bars, Color = color }, top: 25));
        var goalPanel = S();
        if (section == "Вес")
        {
            var profile = _session.Data.Profile;
            if (profile.ShowWeightGoal && profile.GoalWeightKg is { } goal)
            {
                var weight = SneakyFit.Application.SmartTargets.WeightAt(_session.Data, today);
                goalPanel.Children.Add(T("Цель  " + N(goal, "0.#") + " кг", HeadingSize, Ink, true));
                goalPanel.Children.Add(M(T(weight is { } kg ? "Осталось " + N(Math.Max(0, kg - goal), "0.#") + " кг" : "—", MetricSize, Mint, true), top: 10));
                goalPanel.Children.Add(M(Btn("Скрыть цель", () => Safe(() => _session.SaveProfile(profile with { ShowWeightGoal = false })), "#F5FBEF"), top: 20));
            }
            else goalPanel.Children.Add(Btn(profile.GoalWeightKg is null ? "Указать цель" : "Показать цель", () => { if (profile.GoalWeightKg is null) GoalDrawer(); else Safe(() => _session.SaveProfile(profile with { ShowWeightGoal = true })); }, "#F5FBEF"));
        }
        root.Children.Add(Surface(body, "#FFFFFF", 28, 28));
        if (section == "Вес") root.Children.Add(M(Surface(goalPanel, "#E8F4DE", 24, 28), top: 20));
        var notes = new StackPanel();
        if (section == "Вес") foreach (var entry in _session.Data.Weight.Where(w => DateOnly.FromDateTime(w.RecordedAt) >= start && DateOnly.FromDateTime(w.RecordedAt) <= today).OrderByDescending(w => w.RecordedAt)) notes.Children.Add(M(Between(T(entry.RecordedAt.ToString("d MMMM yyyy · HH:mm", Ru), BodySize, Muted), Btn(N(entry.Kilograms, "0.0") + " кг   ✎", () => MeasurementDrawer(false, existingWeight: entry), "#F3EEFC", Ink)), bottom: 10));
        else if (section == "Параметры") foreach (var entry in _session.Data.Measurements.Where(m => m.Key == _measurementKey && m.Side == _measurementSide && DateOnly.FromDateTime(m.RecordedAt) >= start && DateOnly.FromDateTime(m.RecordedAt) <= today).OrderByDescending(m => m.RecordedAt)) notes.Children.Add(M(Between(T(entry.RecordedAt.ToString("d MMMM yyyy · HH:mm", Ru), BodySize, Muted), Btn(N(entry.Value, "0.#") + " см   ✎", () => MeasurementDrawer(true, entry), "#F3EEFC", Ink)), bottom: 10));
        else notes.Children.Add(T("Среднее: " + (values.Length == 0 ? "—" : values.Average().ToString("0", Ru)) + " " + unit, SubheadingSize, Ink, true));
        if (notes.Children.Count > 0) root.Children.Add(M(Surface(notes), top: 20));
        return root;
    }
    private void MeasurementDrawer(bool measure, BodyMeasurement? existingMeasure = null, WeightEntry? existingWeight = null)
    {
        var recorded = existingMeasure?.RecordedAt ?? existingWeight?.RecordedAt ?? DateTime.Now;
        var value = Input(existingMeasure is { } m ? N(m.Value, "0.##") : existingWeight is { } w ? N(w.Kilograms, "0.##") : "", metric: true); var date = new DatePicker { SelectedDate = recorded.Date, DisplayDateEnd = DateTime.Today }; var time = Input(recorded.ToString("HH:mm"));
        var definitions = MeasurementCatalog.All.ToList(); var kind = Select(definitions.Select(d => d.Name), definitions.FindIndex(d => d.Key == (existingMeasure?.Key ?? _measurementKey))); var side = Select(["Левая", "Правая"], (existingMeasure?.Side ?? _measurementSide) == BodySide.Right ? 1 : 0); var sideField = Field("Сторона", side);
        void Side() => sideField.Visibility = measure && definitions[kind.SelectedIndex].Bilateral ? Visibility.Visible : Visibility.Collapsed;
        kind.SelectionChanged += (_, _) => Side(); Side();
        var body = S(); if (measure) { body.Children.Add(Field("Параметр", kind)); body.Children.Add(sideField); } body.Children.Add(Field(measure ? "Сантиметры" : "Вес, кг", value)); body.Children.Add(Pair(Field("Дата", date), Field("Время", time)));
        if (existingMeasure is not null || existingWeight is not null) body.Children.Add(Btn("Удалить запись", () =>
        {
            Drawer("Удалить запись?", T(recorded.ToString("d MMMM yyyy · HH:mm", Ru), HeadingSize, Ink, true), Primary("Удалить", () => Safe(() => { if (existingMeasure is not null) _session.DeleteMeasurement(existingMeasure.Id); else _session.DeleteWeight(existingWeight!.Id); CloseDrawer(); Notify("Запись удалена"); })));
        }, "#FFE9E6", "#B75F65"));
        Drawer(existingMeasure is not null || existingWeight is not null ? "Изменить запись" : measure ? "Новый замер" : "Записать вес", body, Primary("Сохранить", () => Safe(() => { if (date.SelectedDate is not { } selected || !TimeOnly.TryParse(time.Text, out var clock)) throw new ArgumentException("Проверьте дату и время"); var at = DateOnly.FromDateTime(selected).ToDateTime(clock); if (measure) { var definition = definitions[kind.SelectedIndex]; _session.SaveMeasurement(new(existingMeasure?.Id ?? Guid.NewGuid(), at, definition.Key, definition.Bilateral ? side.SelectedIndex == 0 ? BodySide.Left : BodySide.Right : BodySide.None, Num(value, "Измерение", .1m, 500))); } else _session.SaveWeight(new(existingWeight?.Id ?? Guid.NewGuid(), at, Num(value, "Вес", 1, 500))); CloseDrawer(); Notify("Сохранено ✓"); })));
    }

    private void GoalDrawer()
    {
        var profile = _session.Data.Profile; var goal = Input(profile.GoalWeightKg is { } kg ? N(kg, "0.#") : "", metric: true);
        var show = new CheckBox { Content = "Показывать в прогрессе", IsChecked = profile.ShowWeightGoal };
        Drawer("Цель веса", S(Field("Вес, кг", goal), show), Primary("Сохранить", () => Safe(() => { _session.SaveProfile(profile with { GoalWeightKg = Num(goal, "Цель", 20, 500), ShowWeightGoal = show.IsChecked == true }); CloseDrawer(); })));
    }
}
