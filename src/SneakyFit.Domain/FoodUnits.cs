namespace SneakyFit.Domain;

public static class FoodUnits
{
    public static bool IsValidDensity(decimal? value) => value is null or (>= .01m and <= 25m);

    public static decimal ConvertAmount(decimal amount, FoodUnit from, FoodUnit to, decimal? gramsPerMillilitre)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to)) throw new ArgumentException("Неизвестная единица продукта.");
        if (from == to) return amount;
        if (gramsPerMillilitre is not { } density || !IsValidDensity(density))
            throw new ArgumentException("Укажите вес 100 мл этого продукта.");
        return from == FoodUnit.Grams ? amount / density : amount * density;
    }

    public static Nutrition ConvertNutrition(Nutrition per100, FoodUnit from, FoodUnit to, decimal? density) =>
        per100.ForAmount(ConvertAmount(100, to, from, density));

    public static string Symbol(FoodUnit unit) => unit switch
    {
        FoodUnit.Grams => "г",
        FoodUnit.Millilitres => "мл",
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static string Basis(FoodUnit unit) => unit switch
    {
        FoodUnit.Grams => "100g",
        FoodUnit.Millilitres => "100ml",
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static FoodUnit FromBasis(string? basis) => basis switch
    {
        "100g" => FoodUnit.Grams,
        "100ml" => FoodUnit.Millilitres,
        _ => throw new ArgumentException("Укажите основу КБЖУ: 100 г или 100 мл.", nameof(basis))
    };

    public static bool AreValidServings(IReadOnlyList<FoodServing>? servings) =>
        servings is not null && servings.Count <= 100 && servings.All(s => s is not null &&
            !string.IsNullOrWhiteSpace(s.Name) && s.Name.Trim().Length <= 80 && s.Amount is > 0 and <= 100000) &&
        servings.Select(s => s.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == servings.Count;
}
