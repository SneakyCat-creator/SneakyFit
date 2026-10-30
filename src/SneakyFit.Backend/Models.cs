using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SneakyFit.Backend;

public sealed record CatalogProduct(string Id, string? Barcode, string Name, string? Brand, string? Preparation,
    decimal? Calories, decimal? Protein, decimal? Fat, decimal? Carbs, string Basis, string Source, string? SourceUrl, string? Revision)
{
    public decimal? GramsPerMillilitre { get; init; }
}
public sealed record SearchResult(IReadOnlyList<CatalogProduct> Products, bool HasMore, string? Warning = null);
public sealed record CustomFoodRequest(string Name, string? Brand, string? Preparation, string? Barcode,
    [property: JsonRequired] decimal Calories, [property: JsonRequired] decimal Protein, [property: JsonRequired] decimal Fat, [property: JsonRequired] decimal Carbs,
    string Basis = "100g");
public sealed record CustomFoodResult(CatalogProduct Product, CatalogProduct Personal, bool Matched);
public sealed record EnrollmentRequest(string Invite, string DeviceName);
public sealed record EnrollmentResult(string Token, string UserId);
public sealed record Identity(string UserId, string DeviceId);
public sealed class ApiException(int status, string message, int? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
public static partial class FoodValidation
{
    public static string Text(string? value, string label, int max, bool required = false)
    {
        var clean = WhiteSpace().Replace((value ?? "").Normalize(NormalizationForm.FormKC).Trim(), " ");
        if (clean.Length > max || clean.Any(char.IsControl) || required && clean.Length == 0)
            throw new ApiException(400, $"Проверьте поле «{label}».");
        return clean;
    }
    public static string? Optional(string? value, string label, int max)
    {
        var clean = Text(value, label, max); return clean.Length == 0 ? null : clean;
    }
    public static string Normalize(string value) => Text(value, "Название", 400).ToLowerInvariant().Replace('ё', 'е');
    public static string IdentityKey(CatalogProduct product)
    {
        if (product.Barcode is { } barcode) return "barcode:" + CanonicalBarcode(barcode);
        var identity = new[] { Normalize(product.Name), Normalize(product.Brand ?? ""), Normalize(product.Preparation ?? "") };
        // Retain the original grams identity so existing records keep deduplicating after an upgrade.
        return JsonSerializer.Serialize(product.Basis == "100g" ? identity : [.. identity, product.Basis]);
    }
    public static string CanonicalBarcode(string value)
    {
        var barcode = value.Trim();
        if (barcode.Length is not (8 or 12 or 13 or 14) || barcode.Any(c => c is < '0' or > '9'))
            throw new ApiException(400, "Штрихкод: 8, 12, 13 или 14 цифр.");
        var sum = 0;
        for (var i = barcode.Length - 2; i >= 0; i--) sum += (barcode[i] - '0') * ((barcode.Length - 2 - i) % 2 == 0 ? 3 : 1);
        if ((10 - sum % 10) % 10 != barcode[^1] - '0') throw new ApiException(400, "Проверьте контрольную цифру штрихкода.");
        // UPC-A and its EAN-13 zero-prefixed representation identify the same product.
        return barcode.Length == 12 ? "0" + barcode : barcode;
    }
    public static CatalogProduct Custom(CustomFoodRequest request)
    {
        if (request.Basis is not ("100g" or "100ml")) throw new ApiException(400, "Выберите КБЖУ на 100 г или 100 мл.");
        var food = new CatalogProduct("", string.IsNullOrWhiteSpace(request.Barcode) ? null : CanonicalBarcode(request.Barcode),
            Text(request.Name, "Название", 200, true), Optional(request.Brand, "Марка", 120), Optional(request.Preparation, "Приготовление", 120),
            request.Calories, request.Protein, request.Fat, request.Carbs, request.Basis, "community", null, null);
        return Product(food, requireNutrition: true);
    }
    public static CatalogProduct Product(CatalogProduct food, bool requireNutrition = false)
    {
        if (food.GramsPerMillilitre is < .01m or > 25m) throw new ApiException(400, "Проверьте вес 100 мл продукта.");
        if (food.Basis is not ("100g" or "100ml" or "unknown")) throw new ApiException(400, "Неизвестная единица пищевой ценности.");
        static void Nutrient(decimal? value, decimal maximum, bool required)
        {
            if (required && value is null || value is < 0 || value > maximum)
                throw new ApiException(400, "Проверьте КБЖУ на 100 г или 100 мл.");
        }
        Nutrient(food.Calories, 1500, requireNutrition); Nutrient(food.Protein, 100, requireNutrition);
        Nutrient(food.Fat, 100, requireNutrition); Nutrient(food.Carbs, 100, requireNutrition);
        if (food.Protein + food.Fat + food.Carbs > 105) throw new ApiException(400, "Сумма БЖУ не может превышать 100 г с учётом округления.");
        return food with { Name = Text(food.Name, "Название", 200, true), Brand = Optional(food.Brand, "Марка", 120), Preparation = Optional(food.Preparation, "Приготовление", 120) };
    }
    public static bool ValidId(string? id) => id is not null && (id.StartsWith("custom:", StringComparison.Ordinal) && Guid.TryParseExact(id[7..], "N", out _) || id.StartsWith("off:", StringComparison.Ordinal) && id.Length is >= 12 and <= 18 && id[4..].All(char.IsAsciiDigit));
    [GeneratedRegex(@"\s+")] private static partial Regex WhiteSpace();
}
