using SneakyFit.Domain;

namespace SneakyFit.Application;

public sealed record CatalogProduct
{
    public string Id { get; init; } = "";
    public string? Barcode { get; init; }
    public string Name { get; init; } = "";
    public string? Brand { get; init; }
    public string? Preparation { get; init; }
    public decimal? Calories { get; init; }
    public decimal? Protein { get; init; }
    public decimal? Fat { get; init; }
    public decimal? Carbs { get; init; }
    public string Basis { get; init; } = "unknown";
    public string Source { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string? Revision { get; init; }
    public decimal? GramsPerMillilitre { get; init; }
    public bool HasCompleteNutrition => Basis is "100g" or "100ml" && FoodUnits.IsValidDensity(GramsPerMillilitre) && Calories is >= 0 and <= 1000 && Protein is >= 0 and <= 100 && Fat is >= 0 and <= 100 && Carbs is >= 0 and <= 100;
    public Food ToFood() => HasCompleteNutrition
        ? new(Guid.NewGuid(), Name, Source == "openfoodfacts" ? "Open Food Facts" : "Общие продукты", new(Calories!.Value, Protein!.Value, Fat!.Value, Carbs!.Value))
        { Unit = FoodUnits.FromBasis(Basis), GramsPerMillilitre = GramsPerMillilitre, CatalogId = Id, CatalogSource = Source, Barcode = Barcode, Brand = Brand, Preparation = Preparation, SourceUrl = SourceUrl, CatalogRevision = Revision }
        : throw new InvalidOperationException("Укажите КБЖУ на 100 г или 100 мл.");
    public static CatalogProduct FromFood(Food food) => new()
    {
        Id = food.CatalogId ?? "", Name = food.Name, Barcode = food.Barcode, Brand = food.Brand, Preparation = food.Preparation,
        Calories = food.Per100.Calories, Protein = food.Per100.Protein, Fat = food.Per100.Fat, Carbs = food.Per100.Carbs,
        Basis = FoodUnits.Basis(food.Unit), GramsPerMillilitre = food.GramsPerMillilitre, Source = food.CatalogSource ?? "community", SourceUrl = food.SourceUrl, Revision = food.CatalogRevision
    };
}
public sealed record CatalogPage(List<CatalogProduct> Products, bool HasMore, string? Warning);
public sealed record PersonalProducts(List<CatalogProduct> Products);
public sealed record CustomFoodResult(CatalogProduct Product, CatalogProduct Personal, bool Matched);
public sealed record CustomFoodRequest(string Name, string? Brand, string? Preparation, string? Barcode, decimal Calories, decimal Protein, decimal Fat, decimal Carbs, string Basis = "100g");
public sealed record DeviceCredentials(string Token, string UserId);
public sealed record DeviceInvitation(string Invite, int ExpiresInSeconds);
