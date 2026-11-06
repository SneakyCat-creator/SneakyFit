using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SneakyFit.Application;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private UIElement ProductInformation(CatalogProduct product, Food food, decimal amount)
    {
        var details = product.Details;
        var root = S(Between(T("О продукте", SubheadingSize, Ink, true), NutritionHelp(product, food, amount)));
        var basis = product.Basis == "100ml" ? "100 мл" : product.Basis == "100g" ? "100 г" : "100 г / мл";
        var reference = details is { Basis: "100g" or "100ml" } && details.Basis == product.Basis;
        var caption = new Grid { Margin = new Thickness(0, 14, 0, 10) };
        caption.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        caption.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        caption.Children.Add(T("Пищевая ценность", BodySize, Ink, true));
        var per = T(basis, CaptionSize, "#655F72"); per.TextAlignment = TextAlignment.Right;
        var portion = T(N(amount, "0.##") + " " + FoodUnits.Symbol(food.Unit), CaptionSize, "#655F72"); portion.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(per, 1); Grid.SetColumn(portion, 2); caption.Children.Add(per); caption.Children.Add(portion); root.Children.Add(caption);
        UIElement Row(string name, string value, string serving)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
            grid.Children.Add(T(name, BodySize));
            var a = T(value, BodySize, Ink, true); var b = T(serving, BodySize, Ink, true);
            a.TextAlignment = b.TextAlignment = TextAlignment.Right; Grid.SetColumn(a, 1); Grid.SetColumn(b, 2);
            grid.Children.Add(a); grid.Children.Add(b); return grid;
        }
        string Basic(decimal? value, decimal factor, string unit) => value is { } v ? N(v * factor, "0.##") + " " + unit : "—";
        var energyPrefix = details?.CaloriesEstimated == true ? "≈ " : "";
        root.Children.Add(Row("Калории", energyPrefix + Basic(product.Calories, 1, "ккал"), energyPrefix + Basic(product.Calories, amount / 100, "ккал")));
        root.Children.Add(Row("Белки", Basic(product.Protein, 1, "г"), Basic(product.Protein, amount / 100, "г")));
        root.Children.Add(Row("Жиры", Basic(product.Fat, 1, "г"), Basic(product.Fat, amount / 100, "г")));
        root.Children.Add(Row("Углеводы", Basic(product.Carbs, 1, "г"), Basic(product.Carbs, amount / 100, "г")));
        foreach (var group in NutrientCatalog.All.GroupBy(n => n.Group))
        {
            var rows = S();
            foreach (var definition in group)
            {
                var value = reference ? details!.Nutrients.GetValueOrDefault(definition.Key) : null;
                string Value(decimal factor) => value is null ? "—" :
                    (value.Modifier ?? (value.Derived ? "≈ " : "")) + N(value.Value * definition.DisplayFactor * factor, "0.###") + " " + definition.Unit;
                rows.Children.Add(Row(definition.Name, Value(1), Value(amount / 100)));
            }
            root.Children.Add(new Expander { Header = group.Key, IsExpanded = group.Key == "Основное", FontSize = BodySize,
                Content = M(rows, top: 6, bottom: 8), Margin = new Thickness(0, 14, 0, 0) });
        }
        root.Children.Add(M(GlycemicInformation(product, food, amount), top: 16));
        root.Children.Add(M(Pair(Surface(S(T("Nutri-Score", CaptionSize, "#655F72"), T(details?.NutriScore?.ToUpperInvariant() ?? "—", HeadingSize, Purple, true)), "#F1ECFA", 12, 15),
            Surface(S(T("Обработка · NOVA", CaptionSize, "#655F72"), T(details?.NovaGroup?.ToString() ?? "—", HeadingSize, Purple, true)), "#F1ECFA", 12, 15), gap: 12), top: 16));
        if (product.Source == "openfoodfacts") root.Children.Add(M(T(details?.FromSearchIndex == true
            ? "Open Food Facts · карточка и поисковый индекс" : "Оценки продукта из Open Food Facts", CaptionSize, "#655F72"), top: 7));
        if (details?.CaloriesEstimated == true) root.Children.Add(M(T("Калории рассчитаны приблизительно по БЖУ: 4 × белки + 9 × жиры + 4 × углеводы.", CaptionSize, "#655F72"), top: 7));
        void TextSection(string label, string? value)
        {
            root.Children.Add(M(T(label, BodySize, Ink, true), top: 18, bottom: 6));
            root.Children.Add(T(string.IsNullOrWhiteSpace(value) ? "Не указано" : value));
        }
        if (food.Recipe is { } recipe)
            TextSection("Ингредиенты рецепта", string.Join("\n", recipe.Ingredients.Select(i => i.Food.Name + " · " + N(i.Grams, "0.##") + " " + FoodUnits.Symbol(i.Food.Unit))));
        else TextSection("Состав", details?.Ingredients);
        TextSection("Аллергены", details?.Allergens.Count > 0 ? string.Join(", ", details.Allergens) : null);
        TextSection("Может содержать следы", details?.Traces.Count > 0 ? string.Join(", ", details.Traces) : null);
        TextSection("Добавки", details?.Additives.Count > 0 ? string.Join(", ", details.Additives) : null);
        if (product.Barcode is { } code) root.Children.Add(M(T("Штрихкод · " + code, CaptionSize, "#655F72"), top: 18));
        if (product.Source == "openfoodfacts" && product.SourceUrl is { } url)
            root.Children.Add(M(Link("", "Открыть в Open Food Facts · ODbL", () => OpenCatalogSource(url), size: CaptionSize), top: 12));
        return root;
    }

    private UIElement ProductEditForm(CatalogProduct product, Food original, Func<CatalogProduct, Food, Task> save, Action cancel)
    {
        var name = Input(product.Name); name.MaxLength = 200;
        var basis = Select(["На 100 г", "На 100 мл"], product.Basis == "100ml" ? 1 : 0);
        TextBox Number(decimal? value) => Input(value is { } v ? N(v, "0.######") : "");
        var calories = Number(product.Calories); var protein = Number(product.Protein); var fat = Number(product.Fat); var carbs = Number(product.Carbs);
        var brand = Input(product.Brand ?? ""); brand.MaxLength = 120;
        var preparation = Input(product.Preparation ?? ""); preparation.MaxLength = 120;
        var barcode = Input(product.Barcode ?? ""); barcode.MaxLength = 14;
        var ingredients = Input(product.Details?.Ingredients ?? ""); ingredients.AcceptsReturn = true; ingredients.TextWrapping = TextWrapping.Wrap;
        ingredients.Height = 90; ingredients.MaxLength = 12000;
        TextBox Labels(List<string>? list) => Input(string.Join(", ", list ?? []));
        var allergens = Labels(product.Details?.Allergens); var traces = Labels(product.Details?.Traces); var additives = Labels(product.Details?.Additives);
        var giMode = Select(["Автоматически", "Из исследования · выбрать аналог", "Моя оценка"],
            product.Details?.GlycemicReferenceId is not null ? 1 : product.Details?.GlycemicEstimate is not null || product.Details?.GlycemicIndexPersonal == true ? 2 : 0);
        var studies = Glycemic.Studies.ToArray();
        var studyChoice = Select(studies.Select(s => s.Food + " · ГИ " + N(s.Index)), Math.Max(0, Array.FindIndex(studies, s => s.Id == product.Details?.GlycemicReferenceId)));
        var studyDescription = new ContentControl();
        void DescribeStudy()
        {
            if (studyChoice.SelectedIndex < 0) return;
            var item = studies[studyChoice.SelectedIndex];
            studyDescription.Content = S(T(item.Preparation, CaptionSize, "#655F72"), M(Link("", item.Citation, () => OpenNutritionReference(item.Url), size: CaptionSize), top: 5));
        }
        studyChoice.SelectionChanged += (_, _) => DescribeStudy(); DescribeStudy();
        var giLow = Number(product.Details?.GlycemicEstimate?.Low ?? (product.Details?.GlycemicIndexPersonal == true ? product.Details.GlycemicIndex : null));
        var giHigh = Number(product.Details?.GlycemicEstimate?.High ?? (product.Details?.GlycemicIndexPersonal == true ? product.Details.GlycemicIndex : null));
        var studyFields = S(Field("Продукт в исследовании", studyChoice), studyDescription);
        var rangeFields = Pair(Field("ГИ от", giLow), Field("ГИ до", giHigh), gap: 12);
        void UpdateGi() { studyFields.Visibility = giMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; rangeFields.Visibility = giMode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
        giMode.SelectionChanged += (_, _) => UpdateGi(); UpdateGi();
        var nutrientInputs = new Dictionary<string, TextBox>();
        var body = S(Field("Название", name), Field("Пищевая ценность", basis),
            Pair(Field("Ккал", calories), MacroField("Белки, г", protein), gap: 12),
            Pair(MacroField("Жиры, г", fat), MacroField("Углеводы, г", carbs), gap: 12));
        foreach (var group in NutrientCatalog.All.GroupBy(n => n.Group))
        {
            var rows = S();
            foreach (var definition in group)
            {
                var amount = product.Details?.Nutrients.GetValueOrDefault(definition.Key);
                var input = Input(amount is null ? "" : (amount.Modifier ?? "") + N(amount.Value * definition.DisplayFactor, "0.######"));
                input.MaxLength = 20; nutrientInputs[definition.Key] = input;
                rows.Children.Add(Field(definition.Name + ", " + definition.Unit, input));
            }
            body.Children.Add(new Expander { Header = group.Key, Content = M(rows, top: 12), FontSize = BodySize, Margin = new Thickness(0, 4, 0, 12) });
        }
        body.Children.Add(new Expander { Header = "Состав и упаковка", FontSize = BodySize, Margin = new Thickness(0, 4, 0, 14),
            Content = M(S(Pair(Field("Марка", brand), Field("Приготовление", preparation), gap: 12), Field("Штрихкод", barcode),
                Field("Состав", ingredients), Field("Аллергены, через запятую", allergens), Field("Следы аллергенов", traces), Field("Добавки", additives)), top: 12) });
        body.Children.Add(new Expander { Header = "Гликемический индекс", FontSize = BodySize, Margin = new Thickness(0, 4, 0, 14),
            Content = M(S(Field("Источник", giMode), studyFields, rangeFields,
                M(Link("", "База исследований GI Search", () => OpenNutritionReference(Glycemic.DatabaseUrl), size: CaptionSize), top: 10)), top: 12) });
        var previous = basis.SelectedIndex;
        basis.SelectionChanged += (_, _) =>
        {
            if (previous == basis.SelectedIndex) return;
            if (original.GramsPerMillilitre is { } density)
            {
                var from = previous == 1 ? FoodUnit.Millilitres : FoodUnit.Grams; var to = basis.SelectedIndex == 1 ? FoodUnit.Millilitres : FoodUnit.Grams;
                var factor = FoodUnits.ConvertAmount(100, to, from, density) / 100;
                foreach (var input in new[] { calories, protein, fat, carbs }.Concat(nutrientInputs.Values))
                {
                    var text = input.Text.Trim(); var prefix = text.Length > 0 && "<>≤≥~".Contains(text[0]) ? text[..1] : "";
                    if (decimal.TryParse(text[prefix.Length..].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                        input.Text = prefix + N(value * factor, "0.######");
                }
            }
            previous = basis.SelectedIndex;
        };
        body.Children.Add(CatalogAction("Сохранить мою версию", async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Введите название.");
            var unit = basis.SelectedIndex == 1 ? FoodUnit.Millilitres : FoodUnit.Grams;
            var nutrients = new Dictionary<string, NutrientAmount>();
            foreach (var definition in NutrientCatalog.All)
            {
                var input = nutrientInputs[definition.Key]; var text = input.Text.Trim(); if (text.Length == 0) continue;
                var modifier = "<>≤≥~".Contains(text[0]) ? text[..1] : null;
                if (!decimal.TryParse(text[(modifier?.Length ?? 0)..].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100 * definition.DisplayFactor)
                    throw new ArgumentException("Проверьте: " + definition.Name.ToLowerInvariant() + ".");
                nutrients[definition.Key] = new(value / definition.DisplayFactor, modifier);
            }
            static List<string> ReadLabels(TextBox input) => input.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            var details = (product.Details ?? new()) with
            {
                Basis = FoodUnits.Basis(unit), Nutrients = nutrients, Ingredients = ingredients.Text.Trim(),
                Allergens = ReadLabels(allergens), Traces = ReadLabels(traces), Additives = ReadLabels(additives),
                GlycemicIndex = giMode.SelectedIndex == 0 && product.Details?.GlycemicIndexPersonal != true ? product.Details?.GlycemicIndex : null,
                GlycemicIndexPersonal = false,
                GlycemicReferenceId = giMode.SelectedIndex == 1 ? studies[studyChoice.SelectedIndex].Id : null,
                GlycemicEstimate = giMode.SelectedIndex == 2 ? new(Num(giLow, "ГИ от", 0, 200), Num(giHigh, "ГИ до", 0, 200)) : null,
                GlycemicEstimateOrigin = "manual", Personal = true,
                CaloriesEstimated = product.Details?.CaloriesEstimated == true && calories.Text.Trim() == N(product.Calories ?? 0, "0.######")
            };
            if (!FoodDetails.IsValid(details)) throw new ArgumentException("Проверьте состав и пищевую ценность.");
            var changed = product with { Name = name.Text.Trim(), Basis = FoodUnits.Basis(unit),
                Calories = Num(calories, "Ккал", 0, 1500), Protein = MacroValue(protein, "Белки"), Fat = MacroValue(fat, "Жиры"), Carbs = MacroValue(carbs, "Углеводы"),
                Brand = brand.Text.Trim(), Preparation = preparation.Text.Trim(), Barcode = string.IsNullOrWhiteSpace(barcode.Text) ? null : barcode.Text.Trim(), Details = details, PersonalEdited = true };
            if (changed.Protein + changed.Fat + changed.Carbs > 105) throw new ArgumentException("Проверьте сумму БЖУ на 100 г / мл.");
            var saved = original with { Name = changed.Name, Unit = unit, Per100 = new(changed.Calories.Value, changed.Protein.Value, changed.Fat.Value, changed.Carbs.Value),
                Details = details, CatalogModified = true, Brand = changed.Brand, Barcode = changed.Barcode, Preparation = changed.Preparation,
                Servings = unit == original.Unit ? original.Servings : [] };
            await save(changed, saved);
        }, true));
        body.Children.Add(M(Btn("Отмена", cancel), top: 8));
        return body;
    }
}
