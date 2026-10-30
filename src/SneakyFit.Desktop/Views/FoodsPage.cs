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
                choose.Content = Between(H(M(new FoodPicture { Kind = FoodKind(food), Width = 65, Height = 63 }, right: 17), S(T(food.Name, SubheadingSize, Ink, true), M(T(N(food.Per100.Calories) + " ккал · 100 " + FoodUnits.Symbol(food.Unit), CaptionSize, Muted), top: 5))), Round("+", () => { _selectedFood = food; composer.Content = PortionPanel(food, _session.SelectedDate); }, "#EEE5FB"));
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
        var symbol = FoodUnits.Symbol(food.Unit);
        var quantity = new FoodQuantityEditor(food, _lastPortions.GetValueOrDefault((food.Id, food.Unit), food.Unit == FoodUnit.Millilitres ? 200 : 100), metric: true);
        var amount = quantity.AmountInput;
        var kcal = T("", MetricSize, Purple, true); var macros = T("", BodySize, Ink);
        void Preview()
        {
            try { var n = food.Per100.ForAmount(quantity.BaseAmount); kcal.Text = N(n.Calories) + " ккал"; macros.Text = $"Б {N(n.Protein, "0.#")}   Ж {N(n.Fat, "0.#")}   У {N(n.Carbs, "0.#")}"; }
            catch (ArgumentException) { kcal.Text = "— ккал"; macros.Text = ""; }
        }
        quantity.Changed += Preview; Preview();
        void Add() => Safe(() =>
        {
            if (meal.SelectedIndex < 0) throw new ArgumentException("Добавьте приём пищи в профиле.");
            var value = quantity.BaseAmount; var selected = quantity.SelectedFood;
            RememberFoodDensity(food, quantity.GramsPerMillilitre);
            _session.AddFoodToMeal(date, meals[meal.SelectedIndex].TemplateId, selected, quantity.SelectedAmount, planned || date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten);
            _lastPortions[(food.Id, food.Unit)] = value;
            if (drawer) CloseDrawer();
            Notify(food.Name + " · " + N(quantity.SelectedAmount, "0.##") + " " + FoodUnits.Symbol(selected.Unit) + " ✓");
        });
        amount.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        if (drawer) amount.Loaded += (_, _) => { amount.Focus(); amount.SelectAll(); };
        void SetAmount(int value) => quantity.SetBaseAmount(value);
        void Redisplay(Food updated)
        {
            _selectedFood = updated;
            if (drawer) Drawer(updated.Name, PortionPanel(updated, date, mealId, planned, true)); else Navigate("foods");
        }
var favorite = Round(food.Favorite ? "★" : "☆", () => Safe(() =>
{
    var updated = food with { Favorite = !food.Favorite };
    _session.SaveFood(updated);
    Redisplay(updated);
    Notify(updated.Favorite ? "В любимых ✓" : "Убрано из любимых");
}), "#FFFFFF", Gold);

var portionLabel = T(
    food.Unit == FoodUnit.Millilitres
        ? "Выпито"
        : food.Recipe is null
            ? "Порция"
            : "Съедено готового блюда",
    BodySize,
    Ink);

var panel = S(
    Between(portionLabel, favorite),
    M(quantity, top: 7),M(H(Btn("100 " + symbol, () => SetAmount(100), "#FFFFFF"), M(Btn("200 " + symbol, () => SetAmount(200), "#FFFFFF"), left: 6), M(Btn("250 " + symbol, () => SetAmount(250), "#FFFFFF"), left: 6)), top: 10),
            M(kcal, top: 23, bottom: 7), macros, M(Field("В приём пищи", meal, Ink), top: 24), Primary(planned ? "Запланировать" : "Добавить", Add));
        panel.Children.Add(M(Btn("Мои порции", () => FoodServingsDrawer(food, Redisplay), "#FFFFFF"), top: 10));
        if (food.Recipe is not null) panel.Children.Add(M(Btn("Состав и приготовление  ›", () => RecipeDrawer(food), "#FFFFFF"), top: 13));
        else
        {
            panel.Children.Add(M(Btn("Изменить мою версию", () => CatalogProductEditor(CatalogProduct.FromFood(food), Redisplay, food.CatalogRevision, food.CatalogId is null ? food : null), "#FFFFFF"), top: 13));
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
                select.Content = Between(S(T(food.Name, BodySize, Ink, true), M(T(N(food.Per100.Calories) + " ккал / 100 " + FoodUnits.Symbol(food.Unit), CaptionSize, Muted), top: 4)), T("+", PageHeadingSize, Purple)); results.Children.Add(M(select, bottom: 9));
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
        var foodLabels = foods.Select(f => f.Name + " · " + FoodUnits.Symbol(f.Unit) + (string.IsNullOrWhiteSpace(f.Brand) ? "" : " · " + f.Brand) + (string.IsNullOrWhiteSpace(f.Preparation) ? "" : " · " + f.Preparation)).ToArray();
        var choose = Select(foodLabels); choose.IsEditable = true; choose.IsTextSearchEnabled = true;
        Food? SelectedIngredient()
        {
            if (choose.SelectedIndex >= 0 && string.Equals(choose.Text, foodLabels[choose.SelectedIndex], StringComparison.CurrentCultureIgnoreCase)) return foods[choose.SelectedIndex];
            var matches = foods.Where(f => string.Equals(f.Name, choose.Text, StringComparison.CurrentCultureIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        var outputUnit = Select(["Блюдо · граммы", "Напиток · миллилитры"], existing?.Unit == FoodUnit.Millilitres ? 1 : 0);
        FoodUnit OutputUnit() => outputUnit.SelectedIndex == 1 ? FoodUnit.Millilitres : FoodUnit.Grams;
        var ingredientQuantityHost = new ContentControl(); FoodQuantityEditor? ingredientQuantity = null;
        var raw = Input(existing?.Recipe is { } recipe ? N(recipe.RawWeight) : "");
        var cooked = Input(existing?.Recipe is { } r ? N(r.CookedWeight) : "");
        var rawField = Field("До приготовления, г", raw);
        var cookedField = Field("Готовое блюдо, г", cooked);
        var rows = S(); var quantities = new List<FoodQuantityEditor>(); var calories = T("—", MetricSize, Purple, true); var macros = T("", BodySize, Ink);
        RecipeDefinition Definition() => new(ingredients.Select((ingredient, index) => index < quantities.Count
            ? ingredient with { Grams = quantities[index].BaseAmount, Food = ingredient.Food with { GramsPerMillilitre = quantities[index].GramsPerMillilitre } }
            : ingredient).ToList(), OutputUnit() == FoodUnit.Grams ? Num(raw, "До приготовления", .1m, 100000) : 0, Num(cooked, "Готовое блюдо", .1m, 100000)) { Unit = OutputUnit() };
        void RefreshRaw()
        {
            // Millilitres are not mass: mixed recipes need the measured before-cooking weight.
            raw.Text = ingredients.Count > 0 && ingredients.All(i => i.Food.Unit == FoodUnit.Grams) ? N(ingredients.Sum(i => i.Grams), "0.##") : "";
        }
        void Preview()
        {
            var suffix = " ккал / 100 " + FoodUnits.Symbol(OutputUnit());
            try { var n = Definition().Per100; calories.Text = N(n.Calories) + suffix; macros.Text = $"Б {N(n.Protein, "0.#")}   Ж {N(n.Fat, "0.#")}   У {N(n.Carbs, "0.#")}"; }
            catch (ArgumentException) { calories.Text = "—" + suffix; macros.Text = ""; }
        }
        void Draw()
        {
            rows.Children.Clear(); quantities.Clear();
            for (var i = 0; i < ingredients.Count; i++)
            {
                var index = i; var ingredient = ingredients[i];
                var quantity = new FoodQuantityEditor(new Food(ingredient.Food.SourceFoodId, ingredient.Food.Name, "", ingredient.Food.Per100)
                    { Unit = ingredient.Food.Unit, GramsPerMillilitre = ingredient.Food.GramsPerMillilitre }, ingredient.Grams);
                quantities.Add(quantity);
                quantity.Changed += Preview;
                quantity.LostKeyboardFocus += (_, _) => Safe(() =>
                {
                    if (quantity.IsKeyboardFocusWithin) return;
                    var value = quantity.BaseAmount;
                    if (ingredients[index].Grams != value) { ingredients[index] = ingredients[index] with { Grams = value }; RefreshRaw(); }
                    Preview();
                });
                var symbol = FoodUnits.Symbol(ingredient.Food.Unit);
                rows.Children.Add(M(S(Between(S(T(ingredient.Food.Name, BodySize, Ink, true), T(N(ingredient.Food.Per100.Calories) + " ккал / 100 " + symbol, CaptionSize, Muted)), Round("×", () => { ingredients.RemoveAt(index); RefreshRaw(); Draw(); }, "#FFFFFF")), M(quantity, top: 7)), bottom: 14));
            }
            Preview();
        }
        cooked.TextChanged += (_, _) => Preview(); raw.TextChanged += (_, _) => Preview();
        void UpdateOutput()
        {
            rawField.Visibility = OutputUnit() == FoodUnit.Grams ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)((Border)cookedField.Children[0]).Child).Text = OutputUnit() == FoodUnit.Millilitres ? "Готовый напиток, мл" : "Готовое блюдо, г";
            Preview();
        }
        outputUnit.SelectionChanged += (_, _) => { cooked.Clear(); UpdateOutput(); };
        Guid? quantityFoodId = null;
        void IngredientSelected()
        {
            var f = SelectedIngredient();
            if (f?.Id == quantityFoodId && ingredientQuantity is not null) return;
            quantityFoodId = f?.Id;
            ingredientQuantity = f is null ? null : new FoodQuantityEditor(f, 100);
            ingredientQuantityHost.Content = ingredientQuantity;
        }
        choose.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => IngredientSelected()));
        choose.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(IngredientSelected);
        IngredientSelected(); UpdateOutput();
        var body = S(Field("Название", name), Field("Готовый продукт", outputUnit), rows, Field("Ингредиент", choose), Field("Количество", ingredientQuantityHost), Btn("+  Добавить", () => Safe(() =>
        {
            IngredientSelected();
            var food = SelectedIngredient();
            if (food is null || ingredientQuantity is null) throw new ArgumentException("Выберите ингредиент из списка");
            _ = ingredientQuantity.BaseAmount; var selected = ingredientQuantity.SelectedFood;
            ingredients.Add(new(new(selected.Id, selected.Name, selected.Per100) { Unit = selected.Unit, GramsPerMillilitre = selected.GramsPerMillilitre }, ingredientQuantity.SelectedAmount)); RefreshRaw(); Draw();
        })), M(S(rawField, cookedField), top: 14), M(Surface(S(calories, M(macros, top: 8)), "#EEE7FB"), top: 10));
        Draw();
        Drawer(existing is null ? "Новый рецепт" : "Рецепт", body, Primary("Сохранить рецепт", () => Safe(() =>
        {
            var recipe = Definition();
            var food = new Food(existing?.Id ?? Guid.NewGuid(), name.Text.Trim(), "Мои рецепты", recipe.Per100, existing?.Favorite ?? true) { Recipe = recipe, Unit = recipe.Unit, Servings = existing?.Unit == recipe.Unit ? existing.Servings : [] };
            foreach (var ingredient in recipe.Ingredients)
                if (_session.Data.Foods.FirstOrDefault(f => f.Id == ingredient.Food.SourceFoodId) is { } source)
                    RememberFoodDensity(source, ingredient.Food.GramsPerMillilitre);
            _session.SaveFood(food); _selectedFood = food; _foodFilter = "Все"; Navigate("foods"); Notify("Рецепт сохранён ✓");
        })));
    }
}
