using System.Text.RegularExpressions;

namespace SneakyFit.Domain;

public sealed record GlycemicRange(decimal Low, decimal High)
{
    public bool IsValid => Low >= 0 && High >= Low && High <= 200;
    public GlycemicRange Load(decimal availableCarbohydrates) => new(Low * availableCarbohydrates / 100, High * availableCarbohydrates / 100);
}

public sealed record GlycemicStudy(string Id, string Food, decimal Index, string Preparation, string Citation, string Url);
public sealed record GlycemicSource(string Title, string Url);
public sealed record GlycemicResult(GlycemicRange Index, string Kind, string Description, IReadOnlyList<GlycemicStudy> Studies)
{
    public IReadOnlyList<GlycemicSource> References { get; init; } = [];
}

// Glucose = 100. Values are study results, not measurements of an arbitrary OFF barcode.
// Only an explicitly selected study is shown as measured; automatic matches are estimates.
public static class Glycemic
{
    public const string DatabaseUrl = "https://glycemicindex.com/gi-search/";
    private const string Oats = "https://doi.org/10.1007/s00394-019-02083-5";
    private const string Rice = "https://doi.org/10.3390/nu8060308";
    public static IReadOnlyList<GlycemicStudy> Studies { get; } =
    [
        new("oats-whole-pressure", "Овёс цельный", 52, "Вода, скороварка 70 кПа, 20 минут", "Zhu et al., 2020 · 10 участников", Oats),
        new("oats-pearled-boiled", "Овёс шлифованный", 51, "Вода, обычная варка, 50 минут", "Zhu et al., 2020 · 10 участников", Oats),
        new("oats-pearled-pressure", "Овёс шлифованный", 58, "Вода, скороварка 70 кПа, 20 минут", "Zhu et al., 2020 · 10 участников", Oats),
        new("rice-japonica-steamed", "Рис белый Japonica", 84, "На пару, 50 минут", "Zhu et al., 2020 · 10 участников", Oats),
        new("rice-basmati-indiagate", "Рис басмати IndiaGate", 50, "Рисоварка, 26 минут, рис : вода 1 : 2", "Se et al., 2016 · 12 участников", Rice),
        new("rice-jasmine-rambutan", "Рис жасмин Rambutan AAA", 77, "Рисоварка, 32 минуты, рис : вода 1 : 2", "Se et al., 2016 · 12 участников", Rice),
        new("spaghetti-unico", "Спагетти Unico", 48, "Белые спагетти, приготовленные", "Wolever et al., 2003 · межлабораторное исследование", "https://doi.org/10.1038/sj.ejcn.1601551"),
        new("spaghetti-durum-simonato", "Спагетти из твёрдой пшеницы", 39, "Варка в воде", "Simonato et al., 2015 · 10 участников", "https://doi.org/10.1016/j.foodchem.2014.11.023"),
        new("spaghetti-durum-kim", "Спагетти из твёрдой пшеницы", 55, "Варка в воде", "Kim et al., 2019 · 11 участников", "https://doi.org/10.1017/S0007114518003446"),
        new("milk-full-ostman", "Молоко Skånemejerier 3%", 21, "Без добавленного сахара", "Östman et al., 2001 · 10 участников", "https://doi.org/10.1093/ajcn/74.1.96"),
        new("milk-full-hoyt", "Молоко цельное", 41, "Без добавленного сахара", "Hoyt et al., 2005 · 9 участников", "https://doi.org/10.1079/BJN20041304")
    ];

    public static GlycemicStudy? Study(string? id) => Studies.FirstOrDefault(s => s.Id == id);

    public static GlycemicResult? Resolve(string name, string? preparation, FoodDetails? details, decimal? carbs, decimal? fat,
        RecipeDefinition? recipe = null)
    {
        if (Study(details?.GlycemicReferenceId) is { } study)
            return new(new(study.Index, study.Index), "measured-analogue", "Измерено в исследовании · выбранный аналог", [study]);
        if (details?.GlycemicEstimate is { IsValid: true } range)
            return new(range, details.GlycemicEstimateOrigin == "recipe" ? "recipe" : "personal-estimate",
                details.GlycemicEstimateOrigin == "recipe" ? "Оценка по углеводам ингредиентов" : "Ваша оценка", []);
        if (details?.GlycemicIndex is { } declared)
            return new(new(declared, declared), details.GlycemicIndexPersonal ? "personal" : "catalog",
                details.GlycemicIndexPersonal ? "Указано вами" : "Указано в Open Food Facts · метод не подтверждён", []);
        if (recipe is not null) return ForRecipe(recipe);
        if (carbs is null or <= 0 || fat is null) return null;

        var categoryEstimate = GlycemicEstimates.Resolve(name, preparation, details, carbs.Value, fat.Value);

        var text = (name + " " + preparation).ToLowerInvariant().Replace('ё', 'е');
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
        // A name alone is insufficient for a mixed dish, a concentrate or a dessert.
        if (Has(@"суп|салат|пюре|соус|пирог|печен|батончик|мюсли|десерт|шоколад|быстр|instant|bar\b|cake|cookie|sauce|salad|puree|flour|мука|сухо|порош|powder")) return null;
        if (details?.Nutrients.GetValueOrDefault("added-sugars")?.Value > 0) return null;
        var cooked = Has(@"варен|отвар|варк|на воде|на пару|boiled|cooked|steamed");
        string[]? ids = null;
        if (Has(@"\bовес\b|овсяная крупа|зерн.*овс|oat.*kernel|pearled oat|whole.*oat") && cooked && carbs is > 5 and < 40 && fat < 5)
            ids = ["oats-pearled-boiled", "oats-whole-pressure", "oats-pearled-pressure"];
        else if (Has(@"\bрис\b|\brice\b") && Has(@"бел|white|басмати|basmati|жасмин|jasmine|japonica") && cooked && carbs is > 10 and < 45 && fat < 3)
            ids = ["rice-basmati-indiagate", "rice-jasmine-rambutan", "rice-japonica-steamed"];
        else if (Has(@"спагетти|spaghetti") && cooked && carbs is > 10 and < 45 && fat < 4)
            ids = ["spaghetti-durum-simonato", "spaghetti-unico", "spaghetti-durum-kim"];
        else if (Has(@"^(молоко|milk|whole milk)\b") && !Has(@"кокос|миндал|овся|рисов|соев|сгущ|сахар|sugar|soy|almond|coconut|oat") && carbs is >= 3 and <= 6 && fat is >= 2.5m and <= 6)
            ids = ["milk-full-ostman", "milk-full-hoyt"];
        if (ids is null) return categoryEstimate;
        var studies = ids.Select(id => Study(id)!).ToArray();
        return new(new(studies.Min(s => s.Index), studies.Max(s => s.Index)), "estimated", "Оценка по похожим продуктам и приготовлению", studies);
    }

    public static GlycemicResult? ForRecipe(RecipeDefinition recipe)
    {
        decimal low = 0, high = 0, carbohydrates = 0;
        var sources = new List<GlycemicStudy>();
        var references = new List<GlycemicSource>();
        foreach (var ingredient in recipe.Ingredients)
        {
            var carbs = ingredient.Food.Per100.Carbs * ingredient.Grams / 100;
            if (carbs == 0) continue;
            var result = Resolve(ingredient.Food.Name, null, ingredient.Food.Details, ingredient.Food.Per100.Carbs, ingredient.Food.Per100.Fat);
            if (result is null) return null;
            carbohydrates += carbs; low += result.Index.Low * carbs; high += result.Index.High * carbs;
            sources.AddRange(result.Studies);
            references.AddRange(result.References);
        }
        if (carbohydrates <= 0) return null;
        // Ingredient weighting is a model, not a clinical measurement of a cooked meal.
        return new(new(decimal.Floor(low / carbohydrates / 5) * 5, decimal.Ceiling(high / carbohydrates / 5) * 5),
            "recipe", "Оценка по углеводам ингредиентов", sources.DistinctBy(s => s.Id).ToArray())
            { References = references.DistinctBy(s => s.Url).ToArray() };
    }

    public static GlycemicRange? Load(GlycemicResult? result, decimal? availableCarbsPer100, decimal amount) =>
        availableCarbsPer100 is null || availableCarbsPer100 < 0 || amount <= 0 ? null :
        availableCarbsPer100 == 0 ? new(0, 0) : result?.Index.Load(availableCarbsPer100.Value * amount / 100);
}
