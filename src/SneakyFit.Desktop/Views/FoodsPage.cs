using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SneakyFit.Domain;
using SneakyFit.Application;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private IEnumerable<Food> VisibleFoods(string query, string filter)
    {
        var recent = _session.Data.Days.OrderByDescending(d => d.Date).SelectMany(d => d.Meals).SelectMany(m => m.Entries).Select(e => e.Food.SourceFoodId).Distinct().ToList();
        return _session.Data.Foods.Where(f => f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) && (filter != "Любимые" || f.Favorite) && (filter != "Недавние" || recent.Contains(f.Id))).OrderBy(f => filter == "Недавние" ? recent.IndexOf(f.Id) : 0).ThenBy(f => f.Name);
    }
    private UIElement FoodsPage()
    {
        var root = S(H(Btn("▥  Штрихкод", BarcodeDrawer), M(Btn("+  Рецепт", () => RecipeDrawer()), left: 10), M(Primary("+  Своё блюдо", () => NewFoodDrawer()), left: 10)));
        root.Children.Add(M(Tabs(["Любимые", "Недавние", "Все", "Каталог"], _foodFilter, filter => { _foodFilter = filter; _refreshFoods?.Invoke(); }), top: 20, bottom: 14));
        if (_foodFilter == "Каталог")
        {
            root.Children.Add(OnlineCatalogPanel(food => { _selectedFood = food; _foodFilter = "Все"; Navigate("foods"); }));
            return root;
        }
        var search = Input(_foodQuery);
        var list = new StackPanel(); var composer = new ContentControl { VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Draw()
        {
            list.Children.Clear(); var foods = VisibleFoods(_foodQuery, _foodFilter).ToArray();
            foreach (var food in foods)
            {
                var choose = Btn("", () => { _selectedFood = food; composer.Content = PortionPanel(food, _session.SelectedDate); }, _selectedFood?.Id == food.Id ? "#EEE5FF" : "#FFFFFF", Ink);
                choose.HorizontalContentAlignment = HorizontalAlignment.Stretch; choose.Padding = new Thickness(15, 10, 15, 10);
                choose.Content = Between(H(M(new FoodPicture { Kind = FoodKind(food), Width = 65, Height = 63 }, right: 17), S(T(food.Name, SubheadingSize, Ink, true), M(T(N(food.Per100.Calories) + " ккал · 100 г", CaptionSize, Muted), top: 5))), Round("+", () => { _selectedFood = food; composer.Content = PortionPanel(food, _session.SelectedDate); }, "#EEE5FB"));
                list.Children.Add(M(choose, bottom: 10));
            }
            if (foods.Length == 0) list.Children.Add(Surface(S(T("Не нашлось", HeadingSize, Ink, true), M(Btn("Добавить своё", () => NewFoodDrawer()), top: 15)), "#FFFFFF"));
        }
        search.TextChanged += (_, _) => { _foodQuery = search.Text; Draw(); };
        root.Children.Add(M(search, top: 23, bottom: 14));
        _selectedFood = _session.Data.Foods.FirstOrDefault(f => f.Id == _selectedFood?.Id) ?? VisibleFoods("", "Любимые").FirstOrDefault();
        composer.Content = _selectedFood is { } selected ? PortionPanel(selected, _session.SelectedDate) : Surface(T("Выберите блюдо", HeadingSize, Ink, true), "#EEE7FC");
        root.Children.Add(M(Pair(list, composer, 1.8, 1, 25), top: 13)); Draw();
        return root;
    }
    private static int FoodKind(Food food) => food.Category switch { "Завтраки" => 0, "Молочное" or "Фрукты" or "Орехи" => 2, "Овощи" => 3, _ => 1 };

    private UIElement PortionPanel(Food food, DateOnly date, Guid? mealId = null, bool planned = false, bool drawer = false)
    {
        var meals = _session.Day(date).Meals; var meal = Select(meals.Select(m => m.Name), Math.Max(0, meals.FindIndex(m => m.TemplateId == mealId)));
        var grams = Input(N(_lastPortions.GetValueOrDefault(food.Id, 100)), metric: true); grams.TextAlignment = TextAlignment.Center;
        var kcal = T("", MetricSize, Purple, true); var macros = T("", BodySize, Muted);
        void Preview() { if (decimal.TryParse(grams.Text.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var weight) && weight > 0) { var n = food.Per100.ForGrams(weight); kcal.Text = N(n.Calories) + " ккал"; macros.Text = $"Б {N(n.Protein, "0.#")}   Ж {N(n.Fat, "0.#")}   У {N(n.Carbs, "0.#")}"; } else { kcal.Text = "— ккал"; macros.Text = ""; } }
        grams.TextChanged += (_, _) => Preview(); Preview();
        void Add() => Safe(() => { if (meal.SelectedIndex < 0) throw new ArgumentException("Добавьте приём пищи в профиле."); var weight = Num(grams, "Порция", .1m); _session.AddFoodToMeal(date, meals[meal.SelectedIndex].TemplateId, food, weight, planned || date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten); _lastPortions[food.Id] = weight; if (drawer) CloseDrawer(); Notify(food.Name + " · " + N(weight) + " г ✓"); });
        grams.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        if (drawer) grams.Loaded += (_, _) => { grams.Focus(); grams.SelectAll(); };
        var favorite = Round(food.Favorite ? "★" : "☆", () => Safe(() => { _session.SaveFood(food with { Favorite = !food.Favorite }); if (drawer) CloseDrawer(); Notify(food.Favorite ? "Убрано из любимых" : "В любимых ✓"); }), "#FFFFFF", Gold);
        var panel = S(Between(T(food.Name, HeadingSize, Ink, true), favorite), M(T(food.Recipe is null ? "Порция, г" : "Съедено готового блюда, г", BodySize, Muted), top: 23, bottom: 7), grams,
            M(H(Btn("100 г", () => grams.Text = "100", "#FFFFFF"), M(Btn("200 г", () => grams.Text = "200", "#FFFFFF"), left: 6), M(Btn("250 г", () => grams.Text = "250", "#FFFFFF"), left: 6)), top: 10),
            M(kcal, top: 23, bottom: 7), macros, M(Field("В приём пищи", meal), top: 24), Primary(planned ? "Запланировать" : "Добавить", Add));
        if (food.Recipe is not null) panel.Children.Add(M(Btn("Состав и приготовление  ›", () => RecipeDrawer(food), "#FFFFFF"), top: 13));
        if (food.CatalogId is not null)
        {
            panel.Children.Add(M(Btn("Изменить мою версию", () => CatalogProductEditor(CatalogProduct.FromFood(food), updated =>
            {
                _selectedFood = updated;
                if (drawer) Drawer(updated.Name, PortionPanel(updated, date, mealId, planned, true)); else Navigate("foods");
            }, food.CatalogRevision), "#FFFFFF"), top: 13));
            if (food.CatalogSource == "openfoodfacts" && food.SourceUrl is { } source)
                panel.Children.Add(M(Link("", "Open Food Facts", () => OpenCatalogSource(source), size: CaptionSize), top: 10));
        }
        return Surface(panel, "#EEE7FB", 23);
    }

    private void FoodDrawer(Guid? mealId = null, DateOnly? date = null, bool planned = false)
    {
        var day = date ?? _session.SelectedDate; var query = Input();
        var results = new StackPanel(); var local = S(Field("Найти блюдо", query), results); var content = new ContentControl { Content = local };
        void SelectFood(Food food) => Drawer(food.Name, PortionPanel(food, day, mealId, planned, true));
        var tabHost = new ContentControl();
        UIElement? catalog = null;
        void SelectTab(string tab)
        {
            tabHost.Content = Tabs(["Мои блюда", "Каталог"], tab, SelectTab);
            content.Content = tab == "Каталог" ? catalog ??= OnlineCatalogPanel(SelectFood) : local;
            DrawerScroll.ScrollToTop();
        }
        SelectTab(_session.Data.Foods.Count == 0 ? "Каталог" : "Мои блюда");
        var body = S(M(tabHost, bottom: 16), content);
        void Draw()
        {
            results.Children.Clear();
            foreach (var food in VisibleFoods(query.Text, "Все").OrderByDescending(f => f.Favorite))
            {
                var select = Btn("", () => Drawer(food.Name, PortionPanel(food, day, mealId, planned, true)), "#F1ECFA", Ink); select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                select.Content = Between(S(T(food.Name, BodySize, Ink, true), M(T(N(food.Per100.Calories) + " ккал / 100 г", CaptionSize, Muted), top: 4)), T("+", PageHeadingSize, Purple)); results.Children.Add(M(select, bottom: 9));
            }
        }
        query.TextChanged += (_, _) => Draw(); Draw(); Drawer("Добавить еду", body, H(Btn("▥  Штрихкод", () => OnlineBarcodeDrawer(SelectFood)), M(Btn("+  Своё блюдо", () => NewSharedFoodDrawer(SelectFood)), left: 10)));
        if (_session.Data.Foods.Count > 0) query.Focus();
    }
    private void NewFoodDrawer() => NewSharedFoodDrawer();
    private void BarcodeDrawer() => OnlineBarcodeDrawer();

    private void RecipeDrawer(Food? existing = null)
    {
        var name = Input(existing?.Name ?? "");
        var ingredients = existing?.Recipe?.Ingredients.ToList() ?? [];
        var foods = _session.Data.Foods.Where(f => f.Id != existing?.Id).OrderBy(f => f.Name).ToArray();
        var choose = Select(foods.Select(f => f.Name)); choose.IsEditable = true; choose.IsTextSearchEnabled = true;
        var grams = Input("100"); var raw = Input(existing?.Recipe is { } recipe ? N(recipe.RawWeight) : "0");
        var cooked = Input(existing?.Recipe is { } r ? N(r.CookedWeight) : "");
        var rows = S(); var calories = T("—", MetricSize, Purple, true); var macros = T("", BodySize, Muted);
        void Preview()
        {
            try { var definition = new RecipeDefinition(ingredients, Num(raw, "До приготовления", .1m, 100000), Num(cooked, "Готовое блюдо", .1m, 100000)); var n = definition.Per100; calories.Text = N(n.Calories) + " ккал / 100 г"; macros.Text = $"Б {N(n.Protein, "0.#")}   Ж {N(n.Fat, "0.#")}   У {N(n.Carbs, "0.#")}"; }
            catch { calories.Text = "— ккал / 100 г"; macros.Text = ""; }
        }
        void Draw()
        {
            rows.Children.Clear();
            for (var i = 0; i < ingredients.Count; i++)
            {
                var index = i; var ingredient = ingredients[i]; var quantity = Input(N(ingredient.Grams, "0.#")); quantity.Width = 80;
                quantity.LostKeyboardFocus += (_, _) => Safe(() => { ingredients[index] = ingredients[index] with { Grams = Num(quantity, "Ингредиент", .1m, 100000) }; raw.Text = N(ingredients.Sum(x => x.Grams)); Preview(); });
                rows.Children.Add(M(Between(S(T(ingredient.Food.Name, BodySize, Ink, true), T(N(ingredient.Food.Per100.Calories) + " ккал / 100 г", CaptionSize, Muted)), H(quantity, M(Round("×", () => { ingredients.RemoveAt(index); raw.Text = N(ingredients.Sum(x => x.Grams)); Draw(); }, "#FFFFFF"), left: 8))), bottom: 12));
            }
            Preview();
        }
        cooked.TextChanged += (_, _) => Preview(); raw.TextChanged += (_, _) => Preview();
        var body = S(Field("Название", name), rows, Field("Ингредиент", choose), Pair(Field("Граммы", grams), M(Btn("+  Добавить", () => Safe(() =>
        {
            var food = foods.FirstOrDefault(f => string.Equals(f.Name, choose.Text, StringComparison.CurrentCultureIgnoreCase));
            if (food is null) throw new ArgumentException("Выберите ингредиент из списка");
            ingredients.Add(new(new(food.Id, food.Name, food.Per100), Num(grams, "Ингредиент", .1m, 100000))); raw.Text = N(ingredients.Sum(x => x.Grams)); Draw();
        })), top: 21)), M(Pair(Field("До приготовления, г", raw), Field("Готовое блюдо, г", cooked)), top: 20), M(Surface(S(calories, M(macros, top: 8)), "#EEE7FB"), top: 10));
        Draw();
        Drawer(existing is null ? "Новый рецепт" : "Рецепт", body, Primary("Сохранить рецепт", () => Safe(() =>
        {
            var recipe = new RecipeDefinition(ingredients.ToList(), Num(raw, "До приготовления", .1m, 100000), Num(cooked, "Готовое блюдо", .1m, 100000));
            var food = new Food(existing?.Id ?? Guid.NewGuid(), name.Text.Trim(), "Мои рецепты", recipe.Per100, existing?.Favorite ?? true) { Recipe = recipe };
            _session.SaveFood(food); _selectedFood = food; _foodFilter = "Все"; Navigate("foods"); Notify("Рецепт сохранён ✓");
        })));
    }
}
