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
        var recent = _session.RecentFoodIds();
        return _session.Data.Foods.Where(f => f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) && (filter != "Любимые" || f.Favorite) && (filter != "Недавние" || recent.Contains(f.Id))).OrderBy(f => filter == "Недавние" ? recent.IndexOf(f.Id) : 0).ThenBy(f => f.Name);
    }
    private UIElement FoodsPage()
    {
        var root = S(H(Btn("▥  Штрихкод", BarcodeDrawer), M(Btn("+  Рецепт", () => RecipeDrawer()), left: 10), M(Primary("+  Своё блюдо", () => NewFoodDrawer()), left: 10)));
        root.Children.Add(M(Tabs(["Любимые", "Недавние", "Все", "Каталог"], _foodFilter, filter => { _foodFilter = filter; _refreshFoods?.Invoke(); }), top: 20, bottom: 14));
        if (_foodFilter == "Каталог")
        {
            root.Children.Add(OnlineCatalogPanel((product, food) => ProductDrawer(product, food)));
            return root;
        }
        var search = Input(_foodQuery); var list = S();
        void Draw()
        {
            list.Children.Clear();
            foreach (var food in VisibleFoods(_foodQuery, _foodFilter))
            {
                var choose = Btn("", () => ProductDrawer(food), "#FFFFFF", Ink);
                choose.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                choose.Content = Between(S(T(food.Name, SubheadingSize, Ink, true),
                    M(T(N(food.Per100.Calories) + " ккал · 100 " + FoodUnits.Symbol(food.Unit), CaptionSize, "#655F72"), top: 4)), Icon("\uE76C", 16));
                list.Children.Add(M(choose, bottom: 10));
            }
            if (list.Children.Count == 0) list.Children.Add(M(T("Здесь пока нет блюд", BodySize, "#655F72"), top: 18));
        }
        search.TextChanged += (_, _) => { _foodQuery = search.Text; Draw(); };
        root.Children.Add(Field("Найти блюдо", search)); root.Children.Add(list); Draw();
        return root;
    }
    private static int FoodKind(Food food) => food.Category switch { "Завтраки" => 0, "Молочное" or "Фрукты" or "Орехи" => 2, "Овощи" => 3, _ => 1 };

    private void FoodDrawer(Guid? mealId = null, DateOnly? date = null, bool planned = false)
    {
        var day = date ?? _session.SelectedDate; var query = Input();
        var results = new StackPanel(); var local = S(Field("Найти блюдо", query), results); var content = new ContentControl { Content = local };
        void SelectFood(CatalogProduct product, Food? food) => ProductDrawer(product, food, day, mealId, planned);
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
            var recent = _session.RecentFoodIds().ToHashSet();
            foreach (var food in VisibleFoods(query.Text, "Все").Where(f => f.Favorite || f.CatalogId is null || f.CatalogModified || recent.Contains(f.Id)).OrderByDescending(f => f.Favorite))
            {
                var select = Btn("", () => ProductDrawer(food, day, mealId, planned), "#F1ECFA", Ink); select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                select.Content = Between(S(T(food.Name, BodySize, Ink, true), M(T(N(food.Per100.Calories) + " ккал / 100 " + FoodUnits.Symbol(food.Unit), CaptionSize, Muted), top: 4)), T("+", PageHeadingSize, Purple)); results.Children.Add(M(select, bottom: 9));
            }
        }
        query.TextChanged += (_, _) => Draw(); Draw();
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Btn("▥  Штрихкод", () => OnlineBarcodeDrawer(day, mealId, planned)));
        actions.Children.Add(M(Btn("+  Своё блюдо", () => NewSharedFoodDrawer(day, mealId, planned)), left: 10));
        if (_session.Day(day).Meals.FirstOrDefault(m => m.TemplateId == mealId) is { } meal)
        {
            var toggle = Btn(meal.IsSkipped ? "Возобновить приём" : "Пропустить приём", () => Safe(() =>
            {
                _session.SetMealSkipped(day, meal.TemplateId, !meal.IsSkipped);
                CloseDrawer();
            }), "#F1EDF7", "#65546F");
            toggle.FontSize = BodySize; toggle.Padding = new Thickness(11, 5, 11, 5);
            actions.Children.Add(M(toggle, left: 10));
        }
        Drawer("Добавить еду", body, actions);
        if (_session.Data.Foods.Count > 0) query.Focus();
    }
    private void NewFoodDrawer() => NewSharedFoodDrawer();
    private void BarcodeDrawer() => OnlineBarcodeDrawer();

    private Button MealAction(DateOnly date, Meal meal, string background, string foreground = Purple) =>
        meal.IsSkipped ? Btn("Возобновить приём", () => Safe(() => _session.SetMealSkipped(date, meal.TemplateId, false)), background, foreground)
            : Btn("+  Добавить", () => FoodDrawer(meal.TemplateId, date, date > DateOnly.FromDateTime(DateTime.Now)), background, foreground);

    private void RecipeDrawer(Food? existing = null)
    {
        if (existing is not null) { ProductDrawer(existing); return; }
        Drawer("Новый рецепт", RecipeEditor(null, food =>
        {
            _session.SaveFood(food); ProductDrawer(_session.Data.Foods.Single(f => f.Id == food.Id));
        }, () => BackDrawer()), width: 600);
    }

    private UIElement RecipeEditor(Food? existing, Action<Food> save, Action cancel)
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
            ingredients.Add(new(new(selected.Id, selected.Name, selected.Per100) { Unit = selected.Unit, GramsPerMillilitre = selected.GramsPerMillilitre, Details = selected.Details }, ingredientQuantity.SelectedAmount)); RefreshRaw(); Draw();
        })), M(S(rawField, cookedField), top: 14), M(Surface(S(calories, M(macros, top: 8)), "#EEE7FB"), top: 10));
        Draw();
        body.Children.Add(M(Primary("Сохранить рецепт", () => Safe(() =>
        {
            var recipe = Definition();
            var food = new Food(existing?.Id ?? Guid.NewGuid(), name.Text.Trim(), "Мои рецепты", recipe.Per100, existing?.Favorite ?? true) { Recipe = recipe, Unit = recipe.Unit, Servings = existing?.Unit == recipe.Unit ? existing.Servings : [] };
            foreach (var ingredient in recipe.Ingredients)
                if (_session.Data.Foods.FirstOrDefault(f => f.Id == ingredient.Food.SourceFoodId) is { } source)
                    RememberFoodDensity(source, ingredient.Food.GramsPerMillilitre);
            save(food); Notify("Рецепт сохранён ✓");
        })), top: 14));
        body.Children.Add(M(Btn("Отмена", cancel), top: 8));
        return body;
    }
}
