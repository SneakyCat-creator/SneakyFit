namespace SneakyFit.Domain;

// Nutrient masses use grams per 100 g / 100 ml, independently of their display unit.
// A missing key is unknown. A present zero is a declared zero.
public sealed record NutrientAmount(decimal Value, string? Modifier = null, bool Derived = false)
{
    public NutrientAmount Scale(decimal factor) => this with { Value = Value * factor };
}

public sealed record FoodDetails
{
    public int Version { get; init; } = 1;
    public string Basis { get; init; } = "unknown";
    public Dictionary<string, NutrientAmount> Nutrients { get; init; } = [];
    public decimal? GlycemicIndex { get; init; }
    public bool GlycemicIndexPersonal { get; init; }
    public string? GlycemicReferenceId { get; init; }
    public GlycemicRange? GlycemicEstimate { get; init; }
    public string GlycemicEstimateOrigin { get; init; } = "manual";
    public string? Ingredients { get; init; }
    public List<string> Allergens { get; init; } = [];
    public List<string> Traces { get; init; } = [];
    public List<string> Additives { get; init; } = [];
    public List<string> Categories { get; init; } = [];
    public string? NutriScore { get; init; }
    public int? NovaGroup { get; init; }
    public string? SourceRevision { get; init; }
    public bool Personal { get; init; }
    public bool CaloriesEstimated { get; init; }
    public bool FromSearchIndex { get; init; }

    public static bool HasRichInformation(FoodDetails? details) => details is not null &&
        (details.Nutrients.Count >= 6 || details.Nutrients.Count >= 3 && !string.IsNullOrWhiteSpace(details.Ingredients));

    public FoodDetails ConvertTo(string basis, decimal factor) => this with
    {
        Basis = basis,
        Nutrients = Nutrients.ToDictionary(p => p.Key, p => p.Value.Scale(factor))
    };

    public static bool IsValid(FoodDetails? details) => details is null ||
        details.Version == 1 && details.Basis is "100g" or "100ml" or "unknown" &&
        details.Nutrients is { Count: <= 80 } && details.Nutrients.All(p =>
            NutrientCatalog.All.Any(n => n.Key == p.Key) && p.Value is { Value: >= 0 and <= 100 } &&
            p.Value.Modifier is null or "" or "<" or ">" or "≤" or "≥" or "~") &&
        details.GlycemicIndex is null or (>= 0 and <= 200) &&
        (details.GlycemicReferenceId is null || Glycemic.Study(details.GlycemicReferenceId) is not null) &&
        (details.GlycemicEstimate is null or { IsValid: true }) && details.GlycemicEstimateOrigin is "manual" or "recipe" &&
        details.NutriScore is null or "a" or "b" or "c" or "d" or "e" &&
        details.NovaGroup is null or (>= 1 and <= 4) &&
        (details.Ingredients?.Length ?? 0) <= 12000 &&
        ValidLabels(details.Allergens) && ValidLabels(details.Traces) && ValidLabels(details.Additives) && ValidLabels(details.Categories);

    private static bool ValidLabels(List<string>? values) => values is { Count: <= 80 } &&
        values.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 160);

    public static FoodDetails ForRecipe(RecipeDefinition recipe)
    {
        var nutrients = new Dictionary<string, NutrientAmount>();
        foreach (var definition in NutrientCatalog.All)
        {
            var items = recipe.Ingredients.Select(i => (Ingredient: i,
                Amount: i.Food.Details?.Nutrients.GetValueOrDefault(definition.Key))).ToArray();
            // Do not turn a partially known recipe into a deceptively complete label.
            if (items.Length == 0 || items.Any(i => i.Amount is null || !string.IsNullOrEmpty(i.Amount.Modifier))) continue;
            var total = items.Sum(i => i.Amount!.Value * i.Ingredient.Grams / 100);
            nutrients[definition.Key] = new(total * 100 / recipe.CookedWeight, Derived: true);
        }
        return new() { Basis = FoodUnits.Basis(recipe.Unit), Nutrients = nutrients, Personal = true,
            GlycemicEstimate = Glycemic.ForRecipe(recipe)?.Index, GlycemicEstimateOrigin = "recipe" };
    }
}

public sealed record NutrientDefinition(string Key, string Name, string Group, string Unit = "г", decimal DisplayFactor = 1);

public static class NutrientCatalog
{
    public static IReadOnlyList<NutrientDefinition> All { get; } =
    [
        new("sugars", "Сахара, всего", "Основное"),
        new("added-sugars", "Добавленный сахар", "Основное"),
        new("fiber", "Клетчатка", "Основное"),
        new("carbohydrates-total", "Углеводы с клетчаткой", "Основное"),
        new("salt", "Соль", "Основное"),
        new("sodium", "Натрий", "Основное", "мг", 1000),
        new("saturated-fat", "Насыщенные жиры", "Жиры"),
        new("monounsaturated-fat", "Мононенасыщенные жиры", "Жиры"),
        new("polyunsaturated-fat", "Полиненасыщенные жиры", "Жиры"),
        new("trans-fat", "Трансжиры", "Жиры"),
        new("cholesterol", "Холестерин", "Жиры", "мг", 1000),
        new("omega-3-fat", "Омега-3", "Жиры"),
        new("omega-6-fat", "Омега-6", "Жиры"),
        new("omega-9-fat", "Омега-9", "Жиры"),
        new("vitamin-a", "Витамин A", "Витамины", "мкг", 1000000),
        new("beta-carotene", "Бета-каротин", "Витамины", "мкг", 1000000),
        new("vitamin-b1", "Витамин B1", "Витамины", "мг", 1000),
        new("vitamin-b2", "Витамин B2", "Витамины", "мг", 1000),
        new("vitamin-pp", "Витамин B3 · ниацин", "Витамины", "мг", 1000),
        new("pantothenic-acid", "Витамин B5", "Витамины", "мг", 1000),
        new("vitamin-b6", "Витамин B6", "Витамины", "мг", 1000),
        new("biotin", "Витамин B7 · биотин", "Витамины", "мкг", 1000000),
        new("vitamin-b9", "Витамин B9 · фолаты", "Витамины", "мкг", 1000000),
        new("vitamin-b12", "Витамин B12", "Витамины", "мкг", 1000000),
        new("vitamin-c", "Витамин C", "Витамины", "мг", 1000),
        new("vitamin-d", "Витамин D", "Витамины", "мкг", 1000000),
        new("vitamin-e", "Витамин E", "Витамины", "мг", 1000),
        new("vitamin-k", "Витамин K", "Витамины", "мкг", 1000000),
        new("calcium", "Кальций", "Минералы", "мг", 1000),
        new("iron", "Железо", "Минералы", "мг", 1000),
        new("magnesium", "Магний", "Минералы", "мг", 1000),
        new("zinc", "Цинк", "Минералы", "мг", 1000),
        new("potassium", "Калий", "Минералы", "мг", 1000),
        new("phosphorus", "Фосфор", "Минералы", "мг", 1000),
        new("copper", "Медь", "Минералы", "мг", 1000),
        new("manganese", "Марганец", "Минералы", "мг", 1000),
        new("selenium", "Селен", "Минералы", "мкг", 1000000),
        new("iodine", "Йод", "Минералы", "мкг", 1000000),
        new("chloride", "Хлориды", "Минералы", "мг", 1000),
        new("fluoride", "Фториды", "Минералы", "мг", 1000),
        new("chromium", "Хром", "Минералы", "мкг", 1000000),
        new("molybdenum", "Молибден", "Минералы", "мкг", 1000000)
    ];
}
