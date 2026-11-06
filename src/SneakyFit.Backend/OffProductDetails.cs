using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SneakyFit.Domain;

namespace SneakyFit.Backend;

internal static class OffProductDetails
{
    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null : null;
    private static decimal? Number(JsonElement obj, string key) => decimal.TryParse(Text(obj, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static string? Modifier(string? value) => value switch { "<" or ">" or "≤" or "≥" => value, "~" or "≈" => "~", _ => null };

    public static FoodDetails Legacy(JsonElement nutrients, string basis)
    {
        var values = new Dictionary<string, NutrientAmount>();
        foreach (var definition in NutrientCatalog.All)
            if (Number(nutrients, definition.Key + "_100g") is { } value && value <= 100)
                values[definition.Key] = new(value, Modifier(Text(nutrients, definition.Key + "_modifier")));
        // *_100g has already been normalized to grams by OFF, even if *_unit is mg.
        return WithSalt(new() { Basis = basis, Nutrients = values, GlycemicIndex = Index(Number(nutrients, "glycemic-index_100g")) });
    }

    public static FoodDetails FromSet(JsonElement nutrients, string basis, decimal reference)
    {
        var values = new Dictionary<string, NutrientAmount>();
        foreach (var definition in NutrientCatalog.All)
        {
            if (!nutrients.TryGetProperty(definition.Key, out var nutrient) || nutrient.ValueKind != JsonValueKind.Object) continue;
            var raw = Number(nutrient, "value") ?? Number(nutrient, "value_computed");
            var unit = Text(nutrient, "unit")?.ToLowerInvariant();
            decimal? scale = unit switch { "g" or null or "" => 1m, "mg" => .001m, "µg" or "μg" or "mcg" => .000001m, "kg" => 1000m, _ => null };
            if (raw is null || scale is null) continue;
            try
            {
                var value = raw.Value * scale.Value * 100 / reference;
                if (value <= 100) values[definition.Key] = new(value, Modifier(Text(nutrient, "modifier")));
            }
            catch (OverflowException) { }
        }
        var index = nutrients.TryGetProperty("glycemic-index", out var gi) ? Index(Number(gi, "value") ?? Number(gi, "value_computed")) : null;
        return WithSalt(new() { Basis = basis, Nutrients = values, GlycemicIndex = index });
    }

    public static FoodDetails Merge(FoodDetails first, FoodDetails second)
    {
        if (first.Basis != second.Basis) return first;
        var values = new Dictionary<string, NutrientAmount>(first.Nutrients);
        foreach (var item in second.Nutrients) values.TryAdd(item.Key, item.Value);
        return first with { Nutrients = values, GlycemicIndex = first.GlycemicIndex ?? second.GlycemicIndex };
    }

    public static FoodDetails Metadata(JsonElement product, FoodDetails nutrition)
    {
        var grade = Text(product, "nutriscore_grade")?.ToLowerInvariant() ?? Text(product, "nutrition_grades")?.ToLowerInvariant();
        var nova = Number(product, "nova_group");
        return nutrition with
        {
            Ingredients = Plain(Text(product, "ingredients_text_ru"), 12000) ?? Plain(Text(product, "ingredients_text_be"), 12000)
                ?? Plain(Text(product, "ingredients_text"), 12000) ?? Plain(Text(product, "ingredients_text_en"), 12000),
            Allergens = Labels(product, "allergens_tags", Allergens),
            Traces = Labels(product, "traces_tags", Allergens),
            Additives = Labels(product, "additives_tags", new Dictionary<string, string>()),
            Categories = Labels(product, "categories_tags", new Dictionary<string, string>()),
            NutriScore = grade is "a" or "b" or "c" or "d" or "e" ? grade : null,
            NovaGroup = nova is >= 1 and <= 4 && decimal.Truncate(nova.Value) == nova ? (int)nova : null,
            SourceRevision = Plain(Text(product, "last_modified_t"), 30)
        };
    }

    private static decimal? Index(decimal? value) => value is >= 0 and <= 200 ? value : null;
    private static FoodDetails WithSalt(FoodDetails details)
    {
        var values = details.Nutrients;
        if (!values.ContainsKey("salt") && values.TryGetValue("sodium", out var sodium) && sodium.Value <= 40)
            values["salt"] = sodium.Scale(2.5m) with { Derived = true };
        if (!values.ContainsKey("sodium") && values.TryGetValue("salt", out var salt))
            values["sodium"] = salt.Scale(1m / 2.5m) with { Derived = true };
        return details;
    }
    private static string? Plain(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = FoodText.Decode(value);
        value = Regex.Replace(value, "<[^>]{0,300}>", "", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        return new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(limit).ToArray()).Trim();
    }
    private static List<string> Labels(JsonElement product, string name, IReadOnlyDictionary<string, string> dictionary)
    {
        if (!product.TryGetProperty(name, out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        return tags.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Take(80)
            .Select(v => v.GetString()!).Select(v => v.Contains(':') ? v[(v.IndexOf(':') + 1)..] : v)
            .Select(v => dictionary.GetValueOrDefault(v) ?? (name == "additives_tags" ? v.ToUpperInvariant() : v.Replace('-', ' ')))
            .Select(v => Plain(v, 160)).OfType<string>().Where(v => v.Length > 0).Distinct().ToList();
    }
    private static readonly Dictionary<string, string> Allergens = new()
    {
        ["gluten"] = "Глютен", ["milk"] = "Молоко", ["eggs"] = "Яйца", ["fish"] = "Рыба",
        ["peanuts"] = "Арахис", ["nuts"] = "Орехи", ["soybeans"] = "Соя", ["celery"] = "Сельдерей",
        ["mustard"] = "Горчица", ["sesame-seeds"] = "Кунжут", ["sulphur-dioxide-and-sulphites"] = "Диоксид серы и сульфиты",
        ["lupin"] = "Люпин", ["molluscs"] = "Моллюски", ["crustaceans"] = "Ракообразные"
    };
}
