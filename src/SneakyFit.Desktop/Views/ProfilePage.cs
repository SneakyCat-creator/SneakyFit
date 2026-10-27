using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SneakyFit.Desktop.Controls;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private UIElement ProfilePage()
    {
        _refreshProfile = null; _refreshFoods = null;
        UIElement Section(string title, UIElement content) => M(S(M(T(title, HeadingSize, Ink, true), bottom: 18), content), bottom: 34);
        var meals = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = MealSettings() };
        _refreshProfile += () => meals.Content = MealSettings();
        var personal = S(Section("О себе", PersonalSettings()), Section("Питание", NutritionSettings()), Section("Фиткот", AdvisorSettings()));
        var technical = S(Section("Приёмы пищи", meals), Section("Каталог", CatalogSettings()), Section("Профиль в файле", DataSettings()), Section("Обновления", UpdateSettings()));
        var main = Pair(personal, technical, 1, 1, 44);
        main.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var type in new[] { typeof(TextBox), typeof(ComboBox), typeof(Button) })
        {
            var style = new Style(type, (Style)System.Windows.Application.Current.FindResource(type));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 10, 14, 10)));
            main.Resources.Add(type, style);
        }
        var refreshMain = _refreshProfile;
        var foods = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _refreshFoods = () => foods.Content = FoodsPage();
        var progress = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void DrawProgress()
        {
            progress.Content = S(M(Tabs(["Вес", "Параметры", "КБЖУ", "Активность"], _progressTab, tab =>
            {
                if (_progressTab == tab) return;
                _progressTab = tab; DrawProgress();
                UiMotion.Enter(progress, UiMotionKind.Page);
            }), bottom: 16), ProgressSection(_progressTab, DrawProgress));
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var back = Btn("←  Дневник", () => Navigate("diary"), "#00FFFFFF", Ink);
        var root = S(M(Between(header, back), bottom: 20), content);
        void SelectTab(string tab, bool animate = false)
        {
            var changed = _menuTab != tab;
            _menuTab = tab; header.Children.Clear();
            foreach (var name in new[] { "Прогресс", "Блюда", "Настройки" })
            {
                var selected = name == tab;
                var button = Btn(name, () => SelectTab(name, true), selected ? "#FFFFFF" : "#00FFFFFF", selected ? Ink : Muted);
                button.MinWidth = 120; button.Height = ControlSize; button.Padding = ControlPadding; button.FontSize = BodySize;
                button.Margin = new Thickness(0, 0, 10, 0);
                System.Windows.Automation.AutomationProperties.SetName(button, name);
                header.Children.Add(button);
            }
            if (tab == "Блюда") { _refreshFoods(); content.Content = foods; }
            else if (tab == "Прогресс") { DrawProgress(); content.Content = progress; }
            else content.Content = main;
            PageScroll.ScrollToTop();
            if (animate && changed) UiMotion.Enter(content, UiMotionKind.Page);
        }
        _refreshProfile = () => { refreshMain?.Invoke(); if (_menuTab == "Блюда") _refreshFoods(); else if (_menuTab == "Прогресс") DrawProgress(); };
        _selectMenuTab = tab => SelectTab(tab, true); SelectTab(_menuTab);
        return root;
    }
    private UIElement PersonalSettings()
    {
        var profile = _session.Data.Profile; var today = DateOnly.FromDateTime(DateTime.Now); int years = today.Year - profile.BirthDate.Year; if (profile.BirthDate > today.AddYears(-years)) years--;
        var name = Input(profile.Name); var height = Input(profile.HeightCm > 0 ? N(profile.HeightCm) : "");
        var age = Input(profile.BirthDate == DateOnly.MinValue ? "" : years.ToString()); var sex = Select(["Не указан", "Женский", "Мужской"], (int)profile.Sex);
        var lastWeight = _session.Data.Weight.MaxBy(w => w.RecordedAt)?.Kilograms; var weight = Input(lastWeight is { } kg ? N(kg, "0.#") : "");
        return S(Field("Имя", name), Pair(Field("Рост, см", height), Field("Возраст", age)), Pair(Field("Вес, кг", weight), Field("Пол", sex)), M(Primary("Сохранить", () => Safe(() =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Введите имя");
            var ageValue = Num(age, "Возраст", 1, 120); if (ageValue != decimal.Truncate(ageValue)) throw new ArgumentException("Возраст: целое число лет.");
            var newAge = (int)ageValue; var newWeight = Num(weight, "Вес", 1, 500);
            var current = _session.Data.Profile;
            _session.SaveProfile(current with { Name = name.Text.Trim(), HeightCm = Num(height, "Рост", 50, 260), BirthDate = newAge == years ? current.BirthDate : today.AddYears(-newAge), Sex = (BiologicalSex)sex.SelectedIndex },
                newWeight != lastWeight ? new WeightEntry(Guid.NewGuid(), DateTime.Now, newWeight) : null);
            lastWeight = newWeight; years = newAge; Notify("Профиль сохранён ✓");
        })), top: 10));
    }
    private UIElement NutritionSettings()
    {
        var profile = _session.Data.Profile; var target = profile.DefaultTarget;
        var norm = T("", MetricSize, Purple, true); var calculation = T("", CaptionSize, Muted);
        var goalButton = Btn("", GoalDrawer);
        void Refresh()
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var estimate = SneakyFit.Application.SmartTargets.Estimate(_session.Data, today);
            norm.Text = N(_session.Day(today).Budget) + " ккал";
            calculation.Text = estimate.Missing ?? "Авторасчёт";
            goalButton.Content = _session.Data.Profile.GoalWeightKg is { } goal ? "Цель веса  ·  " + N(goal, "0.#") + " кг  ›" : "Указать цель веса  ›";
        }
        _refreshProfile += Refresh; Refresh();
        var protein = Input(target.Split.Protein.ToString()); var fat = Input(target.Split.Fat.ToString()); var carbs = Input(target.Split.Carbs.ToString());
        var sum = T("", CaptionSize, Muted);
        void Sum() { if (int.TryParse(protein.Text, out var p) && int.TryParse(fat.Text, out var f) && int.TryParse(carbs.Text, out var c)) { sum.Text = (p + f + c) + "%"; sum.Foreground = B(p + f + c == 100 ? Mint : Coral); } else sum.Text = "—"; }
        protein.TextChanged += (_, _) => Sum(); fat.TextChanged += (_, _) => Sum(); carbs.TextChanged += (_, _) => Sum(); Sum();
        var body = S(T("Норма на сегодня", CaptionSize, Muted), M(norm, top: 6), M(calculation, top: 4),
            M(Equal(Field("Белки, %", protein), Field("Жиры, %", fat), Field("Углеводы, %", carbs)), top: 22), M(sum, bottom: 14), Primary("Сохранить", () => Safe(() =>
            {
                int Percent(TextBox field) { var number = Num(field, "Проценты", 0, 100); if (number != decimal.Truncate(number)) throw new ArgumentException("Введите целые проценты"); return (int)number; }
                var current = _session.Data.Profile;
                _session.SaveProfile(current with { DefaultTarget = current.DefaultTarget with { Mode = NutritionMode.Lose, Split = new(Percent(protein), Percent(fat), Percent(carbs)), ActivityCreditPercent = 100 } }); Notify("Сохранено ✓");
            })), M(goalButton, top: 15));
        return body;
    }
    private UIElement MealSettings()
    {
        var body = S(Btn("+  Приём пищи", () => MealTemplateDrawer()));
        foreach (var meal in _session.Data.Profile.MealTemplates)
        {
            var edit = Btn("", () => MealTemplateDrawer(meal), "#F1ECFA", Ink); edit.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            edit.Padding = ControlPadding;
            edit.Content = Between(T(meal.Name, BodySize, Ink, true), H(T(meal.Time.ToString("HH:mm"), CaptionSize, Muted), M(T("›", 20, Muted), left: 16)));
            body.Children.Add(M(edit, top: 10));
        }
        return body;
    }
    private void MealTemplateDrawer(MealTemplate? existing = null)
    {
        var name = Input(existing?.Name ?? ""); var time = Input(existing?.Time.ToString("HH:mm") ?? "12:00"); var body = S(Field("Название", name), Field("Время", time));
        if (existing is not null) body.Children.Add(Btn("Удалить приём", () => Safe(() => { if (_session.Data.Profile.MealTemplates.Count <= 1) throw new ArgumentException("Оставьте хотя бы один приём пищи"); _session.SaveProfile(_session.Data.Profile with { MealTemplates = _session.Data.Profile.MealTemplates.Where(m => m.Id != existing.Id).ToList() }); CloseDrawer(); Notify("Шаблон новых дней изменён"); }), "#FFEBE8", "#C76F70"));
        Drawer(existing is null ? "Новый приём" : "Приём пищи", body, Primary("Сохранить", () => Safe(() => { if (string.IsNullOrWhiteSpace(name.Text) || !TimeOnly.TryParse(time.Text, out var clock)) throw new ArgumentException("Введите название и время"); var item = new MealTemplate(existing?.Id ?? Guid.NewGuid(), name.Text.Trim(), clock); _session.SaveProfile(_session.Data.Profile with { MealTemplates = _session.Data.Profile.MealTemplates.Where(m => m.Id != item.Id).Append(item).OrderBy(m => m.Time).ToList() }); CloseDrawer(); Notify("Шаблон новых дней сохранён ✓"); })));
    }
    private UIElement DataSettings()
    {
        return S(Equal(Btn("Сохранить в файл", ExportProfile), Btn("Открыть из файла", ImportProfile)), M(Btn("Папка профиля", () => Safe(() => Process.Start(new ProcessStartInfo { FileName = _paths.DataDirectory, UseShellExecute = true })), "#00FFFFFF", Muted), top: 6));
    }
    private void ExportProfile()
    {
        var dialog = new SaveFileDialog { Filter = "Профиль SneakyFit|*.json", FileName = "SneakyFit-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json" };
        if (dialog.ShowDialog(this) == true) Safe(() => { _session.Export(dialog.FileName); Notify("Профиль сохранён ✓"); });
    }
    private void ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "Профиль SneakyFit|*.json;*.bak" }; if (dialog.ShowDialog(this) != true) return;
        Drawer("Открыть профиль?", S(T(System.IO.Path.GetFileName(dialog.FileName), HeadingSize, Ink, true), M(T("Текущий профиль будет заменён. Его предыдущая версия останется в резервной копии.", BodySize, Muted), top: 17)), Primary("Открыть", () => Safe(() => { _session.Import(dialog.FileName); _selectedFood = null; Navigate("profile"); Notify("Профиль открыт ✓"); })));
    }
}

