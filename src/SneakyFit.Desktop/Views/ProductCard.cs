using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SneakyFit.Application;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private void ProductDrawer(Food food, DateOnly? date = null, Guid? mealId = null, bool planned = false) =>
        ProductDrawer(CatalogProduct.FromFood(food), food, date, mealId, planned);

    private void ProductDrawer(CatalogProduct product, Food? local = null, DateOnly? date = null, Guid? mealId = null,
        bool planned = false, FoodEntry? entry = null, bool create = false)
    {
        var day = date ?? _session.SelectedDate;
        Drawer(create ? "Своё блюдо" : product.Name, ProductCard(product, local, day, mealId, planned, entry, create), width: 600);
    }

    // All entry points share this view: catalog, personal list, recipe and diary snapshot.
    private UIElement ProductCard(CatalogProduct initial, Food? existing, DateOnly date, Guid? mealId, bool planned,
        FoodEntry? entry = null, bool create = false)
    {
        var profileId = _session.Data.Profile.Id; var createOperation = Guid.NewGuid();
        var product = initial;
        var food = existing ?? new Food(Guid.NewGuid(), product.Name, product.Source == "openfoodfacts" ? "Open Food Facts" : "Мои блюда",
            new(product.Calories ?? 0, product.Protein ?? 0, product.Fat ?? 0, product.Carbs ?? 0))
        {
            Unit = product.Basis == "100ml" ? FoodUnit.Millilitres : FoodUnit.Grams, Details = product.Details,
            GramsPerMillilitre = product.GramsPerMillilitre, CatalogId = string.IsNullOrEmpty(product.Id) ? null : product.Id,
            CatalogSource = product.Source, SourceUrl = product.SourceUrl, Barcode = product.Barcode, Brand = product.Brand,
            Preparation = product.Preparation, CatalogRevision = product.Revision
        };
        var selectedTab = "Обзор";
        var detailsRequested = false;
        var detailsLoading = false;
        string? detailsError = null;
        var tabHost = new ContentControl(); var content = new ContentControl(); var footer = new ContentControl();
        var root = S(tabHost, content, M(footer, top: 12));
        var meals = _session.Day(date).Meals;
        var selectedMeal = Math.Max(0, meals.FindIndex(m => m.TemplateId == mealId));
        decimal amount = entry?.Grams ?? _lastPortions.GetValueOrDefault((food.Id, food.Unit), food.Unit == FoodUnit.Millilitres ? 200 : 100);
        var recordKind = entry?.Kind ?? (planned || date > DateOnly.FromDateTime(DateTime.Now) ? EntryKind.Planned : EntryKind.Eaten);
        FoodQuantityEditor? quantity = null;
        UIElement? overview = null;
        var lifetime = new CancellationTokenSource();
        root.Unloaded += (_, _) => lifetime.Cancel();
        root.Loaded += async (_, _) =>
        {
            if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); }
            await LoadDetails();
        };
        async Task LoadDetails()
        {
            if (detailsRequested || product.Details is not null || string.IsNullOrEmpty(product.Id) || entry is not null) return;
            detailsRequested = true; detailsLoading = true; detailsError = null;
            try
            {
                var fresh = await new CatalogClient(profileId).Product(product.Id, lifetime.Token);
                EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
                var details = fresh.Details;
                if (details is null) { detailsError = "Подробных данных пока нет."; return; }
                if (details.Basis != product.Basis)
                    details = fresh.GramsPerMillilitre is { } density && fresh.Basis is "100g" or "100ml"
                        ? details.ConvertTo(product.Basis, FoodUnits.ConvertAmount(100, food.Unit, FoodUnits.FromBasis(fresh.Basis), density) / 100)
                        : details with { Basis = product.Basis, Nutrients = [] };
                product = product with { Details = details }; food = food with { Details = details };
            }
            catch (OperationCanceledException) { detailsRequested = false; }
            catch (Exception) { detailsRequested = false; detailsError = "Не удалось загрузить подробности."; }
            finally { detailsLoading = false; if (root.IsLoaded && selectedTab == "Подробнее") Show(selectedTab); }
        }

        void CaptureAmount()
        {
            if (quantity is null) return;
            try { amount = quantity.BaseAmount; } catch (ArgumentException) { }
        }
        void Show(string tab)
        {
            CaptureAmount(); selectedTab = tab; DrawerError.Text = "";
            root.Margin = new Thickness(0, 0, tab == "Обзор" ? 0 : 24, 0);
            tabHost.Content = M(Tabs(["Обзор", "Подробнее"], tab, Show), bottom: 8);
            footer.Content = null;
            if (tab == "Подробнее")
            {
                var information = S(ProductInformation(product, food, amount));
                if (detailsLoading) information.Children.Insert(0, M(T("Загружаю состав…", CaptionSize, "#655F72"), bottom: 8));
                else if (detailsError is not null)
                {
                    var status = T(detailsError, CaptionSize, "#655F72");
                    information.Children.Insert(0, M(detailsRequested ? status : Between(status, CatalogAction("Повторить", LoadDetails)), bottom: 8));
                }
                content.Content = information;
                footer.Content = Btn(entry is null ? "Изменить мою версию" : "Изменить эту запись", Edit);
            }
            else { overview ??= Overview(); content.Content = overview; }
            ResizeDialog();
        }
        void Apply(Food saved)
        {
            if (saved.Unit != food.Unit && saved.GramsPerMillilitre is { } density)
                amount = FoodUnits.ConvertAmount(amount, food.Unit, saved.Unit, density);
            food = saved; product = CatalogProduct.FromFood(saved); overview = null; quantity = null;
            DrawerTitle.Text = FoodText.Decode(saved.Name); create = false; Show("Обзор");
        }
        async Task Save(CatalogProduct changed, Food personal)
        {
            EnsureCatalogProfile(profileId);
            lifetime.Token.ThrowIfCancellationRequested();
            if (entry is not null)
            {
                // This edit belongs to the diary snapshot, never to every use of the product.
                var entryAmount = personal.Unit == food.Unit ? amount : personal.GramsPerMillilitre is { } d
                    ? FoodUnits.ConvertAmount(amount, food.Unit, personal.Unit, d) : amount;
                _session.EditEntry(date, entry.Id, changed.Name, personal.Per100, entryAmount, recordKind, personal.Unit,
                    personal.GramsPerMillilitre, personal.Details);
                Apply(personal); return;
            }
            var client = new CatalogClient(profileId);
            if (client.IsConnected && personal.Recipe is null)
            {
                if (string.IsNullOrEmpty(changed.Id))
                {
                    var request = new CustomFoodRequest(changed.Name, changed.Brand, changed.Preparation, changed.Barcode,
                        changed.Calories!.Value, changed.Protein!.Value, changed.Fat!.Value, changed.Carbs!.Value, changed.Basis) { Details = changed.Details };
                    var result = await client.Create(request, createOperation, lifetime.Token);
                    changed = result.Personal;
                }
                else changed = await client.SavePersonal(changed, lifetime.Token, product.Revision);
                EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
                personal = changed.ToFood() with { Id = food.Id, Favorite = personal.Favorite, Servings = personal.Servings };
            }
            _session.SaveFood(personal); Apply(personal); Notify("Моя версия сохранена ✓");
        }
        void Edit()
        {
            CaptureAmount(); selectedTab = "Редактирование";
            root.Margin = new Thickness(0, 0, 24, 0);
            tabHost.Content = M(Btn("←  К обзору", () => Show("Обзор")), bottom: 12);
            content.Content = food.Recipe is not null && entry is null
                ? RecipeEditor(food, updated => { _session.SaveFood(updated); Apply(_session.Data.Foods.Single(f => f.Id == updated.Id)); }, () => Show("Обзор"))
                : ProductEditForm(product, food, Save, () => Show("Обзор")); footer.Content = null;
            ResizeDialog();
        }
        void Servings()
        {
            CaptureAmount(); selectedTab = "Порции"; tabHost.Content = M(Btn("← К обзору", () => Show("Обзор")), bottom: 12); footer.Content = null;
            root.Margin = new Thickness(0, 0, 24, 0);
            content.Content = ServingEditor(food, updated =>
            {
                if (product.HasCompleteNutrition && entry is null) _session.SaveFood(updated);
                else if (entry is not null && _session.Data.Foods.FirstOrDefault(f => f.Id == food.Id && f.Unit == food.Unit) is { } source)
                    _session.SaveFood(source with { Servings = updated.Servings });
                food = updated; overview = null; quantity = null; Show("Обзор");
            });
            ResizeDialog();
        }
        async Task AddOrUpdate()
        {
            if (!product.HasCompleteNutrition) { Edit(); return; }
            EnsureCatalogProfile(profileId);
            if (quantity is null) return;
            var baseAmount = quantity.BaseAmount; var selected = quantity.SelectedFood with
            { Details = food.Details?.ConvertTo(FoodUnits.Basis(quantity.SelectedUnit), FoodUnits.ConvertAmount(100, quantity.SelectedUnit, food.Unit, quantity.GramsPerMillilitre) / 100) };
            if (entry is not null)
            {
                _session.EditEntry(date, entry.Id, selected.Name, selected.Per100, quantity.SelectedAmount, recordKind,
                    selected.Unit, selected.GramsPerMillilitre, selected.Details);
                CloseDrawer(); Notify("Порция обновлена ✓"); return;
            }
            if (selectedMeal < 0 || selectedMeal >= meals.Count) throw new ArgumentException("Добавьте приём пищи в профиле.");
            // Viewing never writes. Persist the private copy only on an explicit Add / Favourite / Save.
            var client = new CatalogClient(profileId);
            if (food.CatalogId is not null && product.Revision is null && client.IsConnected)
            {
                var saved = await client.SavePersonal(product, lifetime.Token);
                EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
                food = food with { CatalogRevision = saved.Revision }; product = saved;
            }
            var canonical = food with { GramsPerMillilitre = quantity.GramsPerMillilitre };
            _session.SaveFood(canonical);
            _session.AddFoodToMeal(date, meals[selectedMeal].TemplateId, selected, quantity.SelectedAmount, recordKind);
            _lastPortions[(food.Id, food.Unit)] = baseAmount;
            CloseDrawer(); Notify(food.Name + " добавлено ✓");
        }
        UIElement Overview()
        {
            var symbol = FoodUnits.Symbol(food.Unit);
            var title = food.Unit == FoodUnit.Millilitres ? "Выпито" : food.Recipe is not null ? "Готовое блюдо" : "Порция";
            Button? favourite = null;
            favourite = Round(food.Favorite ? "★" : "☆", () => Safe(() =>
            {
                if (!product.HasCompleteNutrition) { Edit(); return; }
                var source = entry is null ? food : _session.Data.Foods.FirstOrDefault(f => f.Id == food.Id);
                if (source is null) return;
                food = food with { Favorite = !source.Favorite };
                _session.SaveFood(source with { Favorite = food.Favorite });
                favourite!.Content = food.Favorite ? "★" : "☆";
            }), "#FFFFFF", Gold);
            var panel = S(Between(S(T(title, BodySize, Ink, true),
                T(string.Join(" · ", new[] { product.Brand, product.Preparation }.Where(v => !string.IsNullOrWhiteSpace(v))), CaptionSize, "#655F72")), favourite));
            quantity = new FoodQuantityEditor(food, amount, metric: true);
            panel.Children.Add(M(quantity, top: 10));
            panel.Children.Add(H(Btn("100 " + symbol, () => quantity.SetBaseAmount(100), "#FFFFFF"),
                M(Btn("200 " + symbol, () => quantity.SetBaseAmount(200), "#FFFFFF"), left: 8),
                M(Btn("250 " + symbol, () => quantity.SetBaseAmount(250), "#FFFFFF"), left: 8)));
            var calories = T("", MetricSize, Purple, true);
            var protein = T(""); var fat = T(""); var carbs = T("");
            void Preview()
            {
                decimal? factor;
                try { factor = quantity.BaseAmount / 100; } catch (ArgumentException) { factor = null; }
                string Value(decimal? value) => value is { } number && factor is { } f ? N(number * f, "0.#") : "—";
                calories.Text = (product.Details?.CaloriesEstimated == true && factor is not null ? "≈ " : "") + Value(product.Calories) + " ккал";
                protein.Text = Value(product.Protein) + " г"; fat.Text = Value(product.Fat) + " г"; carbs.Text = Value(product.Carbs) + " г";
            }
            quantity.Changed += Preview; Preview();
            panel.Children.Add(M(calories, top: 18, bottom: 12));
            panel.Children.Add(Equal(S(T("Белки", CaptionSize, "#655F72"), protein), S(T("Жиры", CaptionSize, "#655F72"), fat), S(T("Углеводы", CaptionSize, "#655F72"), carbs)));
            if (entry is null)
            {
                var meal = Select(meals.Select(m => m.Name), selectedMeal);
                meal.SelectionChanged += (_, _) => selectedMeal = meal.SelectedIndex;
                panel.Children.Add(M(Field("В приём пищи", meal), top: 16));
            }
            else
            {
                var kind = Select(["Съедено", "В плане"], recordKind == EntryKind.Eaten ? 0 : 1);
                kind.IsEnabled = date <= DateOnly.FromDateTime(DateTime.Now);
                kind.SelectionChanged += (_, _) => recordKind = kind.SelectedIndex == 0 ? EntryKind.Eaten : EntryKind.Planned;
                panel.Children.Add(M(Field("Запись", kind), top: 16));
            }
            var add = CatalogAction(!product.HasCompleteNutrition ? "Уточнить КБЖУ" : entry is not null ? "Сохранить порцию" : recordKind == EntryKind.Planned ? "Запланировать" : "Добавить", AddOrUpdate, true);
            quantity.AmountInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
            panel.Children.Add(add);
            panel.Children.Add(M(Pair(Btn("Мои порции", Servings, "#FFFFFF"), Btn(entry is null ? "Моя версия" : "Изменить запись", Edit, "#FFFFFF"), gap: 10), top: 10));
            if (entry is not null)
                panel.Children.Add(M(Btn("Удалить запись", () => Safe(() => { _session.RemoveEntry(date, entry.Id); CloseDrawer(); }), "#F5E7E8", "#B54F5B"), top: 10));
            if (product.Source == "openfoodfacts" && product.SourceUrl is { } url)
                panel.Children.Add(M(Link("", "Open Food Facts · ODbL", () => OpenCatalogSource(url), size: CaptionSize), top: 12));
            return Surface(panel, "#EEE7FB", 18, 22);
        }
        if (create) Edit(); else Show("Обзор");
        return root;
    }

    private UIElement ServingEditor(Food food, Action<Food> save)
    {
        var rows = S(); var editors = new List<(TextBox Name, TextBox Amount, UIElement Row)>();
        void Row(string name, decimal amount)
        {
            var label = Input(name); label.MaxLength = 80; var count = Input(N(amount, "0.###")); UIElement? row = null;
            row = Between(Pair(Field("Порция", label), Field(FoodUnits.Symbol(food.Unit), count), 2, 1, 10),
                M(Round("×", () => { rows.Children.Remove(row!); editors.RemoveAll(e => e.Row == row); }), left: 10, top: 16));
            editors.Add((label, count, row)); rows.Children.Add(row);
        }
        foreach (var serving in food.Servings) Row(serving.Name, serving.Amount);
        if (food.Servings.Count == 0) Row(food.Unit == FoodUnit.Millilitres ? "Чашка" : "Порция", food.Unit == FoodUnit.Millilitres ? 200 : 250);
        return S(rows, Btn("+ Порция", () => { if (editors.Count < 100) Row("", 250); }), M(Primary("Сохранить порции", () => Safe(() =>
        {
            var servings = editors.Select(e => new FoodServing(e.Name.Text.Trim(), Num(e.Amount, "Порция", .001m, 100000))).ToList();
            if (!FoodUnits.AreValidServings(servings)) throw new ArgumentException("Проверьте названия и размеры порций.");
            save(food with { Servings = servings });
        })), top: 14));
    }
}
