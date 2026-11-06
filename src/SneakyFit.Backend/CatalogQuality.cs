namespace SneakyFit.Backend;

internal static class CatalogQuality
{
    public static bool Usable(CatalogProduct product) =>
        !string.IsNullOrWhiteSpace(product.Name) && !product.Name.StartsWith("Продукт ", StringComparison.Ordinal) &&
        (product.Calories is >= 0 and <= 1500 || new[] { product.Protein, product.Fat, product.Carbs }.Count(n => n is not null) >= 2) &&
        product.Calories is null or (>= 0 and <= 1500) &&
        new[] { product.Protein, product.Fat, product.Carbs }.All(n => n is null or (>= 0 and <= 100)) &&
        (product.Protein ?? 0) + (product.Fat ?? 0) + (product.Carbs ?? 0) <= 105;

    public static int Completeness(CatalogProduct product) =>
        new[] { product.Calories, product.Protein, product.Fat, product.Carbs }.Count(n => n is not null) * 3 +
        (product.Basis is "100g" or "100ml" ? 3 : 0) +
        Math.Min(product.Details?.Nutrients.Count ?? 0, 12) +
        (string.IsNullOrWhiteSpace(product.Details?.Ingredients) ? 0 : 4);
}
