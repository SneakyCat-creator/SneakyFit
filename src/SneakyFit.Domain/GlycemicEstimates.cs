using System.Text.RegularExpressions;

namespace SneakyFit.Domain;

// These broad, rounded intervals are SneakyFit's analogue model, not measured
// ranges or confidence intervals from a study. Reference means in Atkinson 2008
// guide the model. It deliberately abstains for mixed dishes and unknown preparation.
internal static class GlycemicEstimates
{
    private static readonly GlycemicSource Source = new("Atkinson et al., 2008 · данные для подбора аналогов", "https://doi.org/10.2337/dc08-1239");
    private sealed record Rule(string Label, string Pattern, string Category, decimal Low, decimal High,
        decimal MinCarbs, decimal MaxCarbs, decimal MaxFat, bool Cooked = false);
    private static readonly Rule[] Rules =
    [
        new("яблоко", @"^яблок[оиа]?\b|^apples?\b", "apples", 25, 50, 5, 22, 2),
        new("апельсин", @"^апельсин[ыа]?\b|^oranges?\b", "oranges", 30, 55, 5, 20, 2),
        new("банан", @"^банан[ыа]?\b|^bananas?\b", "bananas", 35, 70, 10, 35, 2),
        new("манго", @"^манго\b|^mango\b", "mangoes", 35, 65, 5, 25, 2),
        new("ананас", @"^ананас[ыа]?\b|^pineapples?\b", "pineapples", 40, 75, 5, 25, 2),
        new("арбуз", @"^арбуз[ыа]?\b|^watermelons?\b", "watermelons", 55, 85, 3, 15, 2),
        new("молоко без добавок", @"^(молоко|milk|whole milk|skimmed milk)\b", "", 20, 45, 3, 6, 6),
        new("белый пшеничный хлеб", @"хлеб.*(бел|пшенич)|бел.*хлеб|white.*bread", "white breads", 60, 90, 30, 65, 6),
        new("овсяная каша на воде", @"овсян.*каш|каш.*овсян|oat.*porridge|porridge.*oat", "", 40, 65, 5, 25, 5, true),
        new("варёная перловка", @"перлов|barley", "", 20, 40, 10, 40, 3, true),
        new("варёная чечевица", @"чечевиц|lentils?\b", "", 20, 45, 5, 35, 4, true),
        new("варёный нут", @"\bнут\b|chickpeas?\b", "", 15, 45, 5, 40, 6, true),
        new("варёная фасоль", @"фасоль|фасоли|kidney beans?", "", 15, 40, 5, 35, 4, true),
        new("варёная морковь", @"морков|carrots?\b", "", 25, 55, 3, 15, 3, true),
        new("варёный картофель", @"картоф|potatoes", "", 60, 95, 10, 30, 3, true),
        new("картофельные чипсы", @"чипсы.*картоф|картоф.*чипс|potato (crisps|chips)", "potato crisps", 40, 70, 30, 70, 50),
        new("сахар", @"^(сахар|sugar|sucrose)\b", "", 55, 75, 95, 100, 1)
    ];

    public static GlycemicResult? Resolve(string name, string? preparation, FoodDetails? details, decimal carbs, decimal fat)
    {
        var text = FoodText.Decode(name + " " + preparation).ToLowerInvariant().Replace('ё', 'е');
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
        if (Has(@"сок|нектар|смузи|juice|smoothie|суп|салат|соус|пирог|печен|батончик|десерт|шоколад|мюсли|начинк|сухо|сушен|порош|мука|быстр|instant|cake|cookie|sauce|salad|flour|dried|powder|canned|сироп|syrup|пюре|puree|молочн.*каш|каш.*молок")) return null;
        if (details?.Nutrients.GetValueOrDefault("added-sugars")?.Value > 0) return null;
        var cooked = Has(@"варен|отвар|варк|на воде|на пару|boiled|cooked|steamed");
        foreach (var rule in Rules)
        {
            if (carbs < rule.MinCarbs || carbs > rule.MaxCarbs || fat > rule.MaxFat || rule.Cooked && !cooked) continue;
            if (!Has(rule.Pattern) && !(rule.Category.Length > 0 && details?.Categories.Contains(rule.Category) == true)) continue;
            if (rule.Label == "молоко без добавок" && Has(@"кокос|миндал|овся|рисов|соев|сгущ|сахар|sugar|soy|almond|coconut|oat|condensed")) continue;
            return new(new(rule.Low, rule.High), "category-estimate", "Оценка по категории: " + rule.Label, []) { References = [Source] };
        }
        return null;
    }
}
