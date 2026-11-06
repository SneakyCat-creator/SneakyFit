using SneakyFit.Domain;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SneakyFit.Backend;

public interface IExternalCatalog
{
    Task<SearchResult> Search(string query, int page, CancellationToken cancellationToken);
    Task<CatalogProduct?> Barcode(string barcode, CancellationToken cancellationToken);
}

public sealed class OffCache
{
    private readonly string _connectionString;
    public OffCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS responses(key TEXT PRIMARY KEY, body TEXT NOT NULL, expires_at INTEGER NOT NULL);"; cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public T? Read<T>(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM responses WHERE key=$key AND expires_at>$now";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return cmd.ExecuteScalar() is string body ? JsonSerializer.Deserialize<T>(body, JsonDefaults.Options) : default;
    }
    public void Write<T>(string key, T value, TimeSpan lifetime)
        => WriteMany(new[] { new KeyValuePair<string, T>(key, value) }, lifetime);
    public void WriteMany<T>(IEnumerable<KeyValuePair<string, T>> values, TimeSpan lifetime)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO responses(key,body,expires_at) VALUES($key,$body,$expiry) ON CONFLICT(key) DO UPDATE SET body=excluded.body,expires_at=excluded.expires_at";
        var key = cmd.Parameters.Add("$key", SqliteType.Text); var body = cmd.Parameters.Add("$body", SqliteType.Text);
        cmd.Parameters.AddWithValue("$expiry", DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds());
        foreach (var value in values) { key.Value = value.Key; body.Value = JsonSerializer.Serialize(value.Value, JsonDefaults.Options); cmd.ExecuteNonQuery(); }
        using var prune = db.CreateCommand(); prune.Transaction = tx; prune.CommandText = "DELETE FROM responses WHERE expires_at<=$now OR key IN (SELECT key FROM responses ORDER BY expires_at DESC LIMIT -1 OFFSET 5000)";
        prune.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); prune.ExecuteNonQuery(); tx.Commit();
    }
    public void Backup(string target) { using var source = Open(); using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString()); destination.Open(); source.BackupDatabase(destination); }
}

public sealed class OffCatalog(HttpClient http, OffCache cache, string userAgent) : IExternalCatalog
{
    private const int PageSize = 80;
    private const string Fields = "code,product_name,product_name_ru,product_name_be,product_name_en,generic_name,generic_name_ru,generic_name_en,brands,nutriments,nutrition_data_per,nutrition,product_quantity_unit,quantity,serving_quantity,serving_quantity_unit,serving_size,last_modified_t,ingredients_text,ingredients_text_ru,ingredients_text_be,ingredients_text_en,allergens_tags,traces_tags,additives_tags,categories_tags,nutriscore_grade,nutrition_grades,nova_group";
    private readonly RequestBudget _searchBudget = new(10), _productBudget = new(15);
    private readonly ConcurrentDictionary<string, Lazy<Task<SearchResult>>> _searches = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ProductResult>>> _products = new();
    private sealed record ProductResult(CatalogProduct? Product);

    public Task<SearchResult> Search(string query, int page, CancellationToken cancellationToken)
    {
        var key = $"search:details-v5:{query.ToLowerInvariant()}:{page}";
        SearchResult WithoutMissing(SearchResult result) => result with { Products = result.Products
            .Where(p => cache.Read<ProductResult>("barcode:details-v5:" + p.Id[4..]) is not { Product: null }).ToArray() };
        var cached = cache.Read<SearchResult>(key); if (cached is not null) return Task.FromResult(WithoutMissing(cached));
        return Coalesce(_searches, key, async () =>
        {
            if (cache.Read<SearchResult>(key) is { } saved) return WithoutMissing(saved);
            var primary = await SearchSource(query, page);
            var candidates = primary.Products.ToList();
            var hasMore = primary.HasMore;
            var variantKey = "spellings:details-v5:" + query.ToLowerInvariant();
            var variants = page == 1 ? (!primary.HasMore && candidates.Count < 8 ? CatalogSearch.Spellings(query).ToArray() : [])
                : cache.Read<string[]>(variantKey) ?? [];
            var usedVariants = new List<string>();
            // Search remains worldwide: a Russian label may belong to any country.
            // Sparse brand searches also try Latin spelling in the same OFF index.
            foreach (var alternative in variants)
                {
                    try
                    {
                        var extra = await SearchSource(alternative, page);
                        candidates.AddRange(extra.Products); hasMore |= extra.HasMore; usedVariants.Add(alternative);
                    }
                    catch (ApiException) { break; } // optional enrichment must not hide valid primary results
                    if (page == 1 && candidates.DistinctBy(p => p.Id).Count() >= 8) break;
                }
            if (page == 1) cache.Write(variantKey, usedVariants, TimeSpan.FromDays(1));
            var parsed = candidates
                .Where(p => cache.Read<ProductResult>("barcode:details-v5:" + p.Id[4..]) is not { Product: null })
                .GroupBy(p => p.Id).Select(g => g.OrderByDescending(CatalogQuality.Completeness).First())
                .OrderByDescending(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Brand?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                .ThenByDescending(CatalogQuality.Completeness).ToArray();
            // Filtering affects visibility only; personal products and canonical source data stay intact.
            var result = new SearchResult(parsed, hasMore);
            cache.WriteMany(parsed.Select(product => new KeyValuePair<string, CatalogProduct>("summary:details-v5:" + product.Id, product)), TimeSpan.FromDays(1));
            cache.Write(key, result, TimeSpan.FromSeconds(90)); return result;
        }, cancellationToken);
    }
    private async Task<SearchResult> SearchSource(string query, int page)
    {
        _searchBudget.Take();
        // Do not interpret a product name as a Lucene expression (quotes, brackets, etc.).
        var literal = Regex.Replace(FoodText.Decode(query), @"[^\p{L}\p{N}\s]", " ").Trim();
        if (literal.Length < 2) throw new ApiException(400, "Введите название продукта.");
        using var document = await Read(new Uri($"https://search.openfoodfacts.org/search?q={Uri.EscapeDataString(literal)}&page={page}&page_size={PageSize}&langs=ru,be,en&fields={Fields}"));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hits", out var products) || products.ValueKind != JsonValueKind.Array) throw new ApiException(502, "Open Food Facts вернул неполный ответ.");
        var count = NonNegativeInteger(root, "count"); var pages = NonNegativeInteger(root, "page_count");
        var more = pages is not null ? page < pages : count is not null ? (long)page * PageSize < count : products.GetArrayLength() == PageSize;
        return new(products.EnumerateArray().Take(PageSize).Select(Parse).OfType<CatalogProduct>().Where(CatalogQuality.Usable).ToArray(), more);
    }
    public async Task<CatalogProduct?> Barcode(string barcode, CancellationToken cancellationToken)
    {
        barcode = FoodValidation.NormalizeOffCode(barcode);
        var key = "barcode:details-v5:" + barcode;
        if (cache.Read<ProductResult>(key) is { } cached) return WithSearchSummary(cached.Product);
        var result = await Coalesce(_products, key, async () =>
        {
            if (cache.Read<ProductResult>(key) is { } saved) return saved;
            _productBudget.Take();
            using var document = await Read(new Uri($"https://world.openfoodfacts.org/api/v3.6/product/{Uri.EscapeDataString(barcode)}?fields={Fields}&lc=ru&cc=by"), allowNotFound: true);
            CatalogProduct? product = null;
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ApiException(502, "Open Food Facts вернул неполный ответ.");
            if (document.RootElement.TryGetProperty("product", out var value)) product = Parse(value);
            var response = new ProductResult(product); cache.Write(key, response, TimeSpan.FromMinutes(15)); return response;
        }, cancellationToken);
        return WithSearchSummary(result.Product);
    }

    private CatalogProduct? WithSearchSummary(CatalogProduct? product)
    {
        if (product is null || cache.Read<CatalogProduct>("summary:details-v5:" + product.Id) is not { } summary) return product;
        var sameBasis = product.Basis == summary.Basis || product.Basis == "unknown";
        var missingLabel = product.Calories is null || product.Protein is null || product.Fat is null || product.Carbs is null;
        var details = product.Details ?? new() { Basis = product.Basis };
        if (sameBasis && summary.Details is { } fallback)
            details = OffProductDetails.Merge(details with { Basis = summary.Basis }, fallback) with
            {
                FromSearchIndex = details.FromSearchIndex || missingLabel,
                CaloriesEstimated = product.Calories is null ? fallback.CaloriesEstimated : details.CaloriesEstimated,
                Ingredients = details.Ingredients ?? fallback.Ingredients,
                Allergens = details.Allergens.Count > 0 ? details.Allergens : fallback.Allergens,
                Traces = details.Traces.Count > 0 ? details.Traces : fallback.Traces,
                Additives = details.Additives.Count > 0 ? details.Additives : fallback.Additives,
                Categories = details.Categories.Count > 0 ? details.Categories : fallback.Categories,
                NutriScore = details.NutriScore ?? fallback.NutriScore, NovaGroup = details.NovaGroup ?? fallback.NovaGroup
            };
        var preferRussian = summary.Name.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') && !product.Name.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');
        return product with
        {
            Name = product.Name.StartsWith("Продукт ", StringComparison.Ordinal) || preferRussian ? summary.Name : product.Name,
            Brand = product.Brand ?? summary.Brand,
            Basis = product.Basis == "unknown" ? summary.Basis : product.Basis,
            Calories = product.Calories ?? (sameBasis ? summary.Calories : null), Protein = product.Protein ?? (sameBasis ? summary.Protein : null),
            Fat = product.Fat ?? (sameBasis ? summary.Fat : null), Carbs = product.Carbs ?? (sameBasis ? summary.Carbs : null), Details = details
        };
    }
    private static async Task<T> Coalesce<T>(ConcurrentDictionary<string, Lazy<Task<T>>> requests, string key, Func<Task<T>> action, CancellationToken callerCancellation)
    {
        // Cancellation by one search caller must not cancel the shared upstream request.
        var request = requests.GetOrAdd(key, _ => new(action, LazyThreadSafetyMode.ExecutionAndPublication));
        var task = request.Value;
        _ = task.ContinueWith(_ => requests.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, request)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(callerCancellation);
    }
    private static long? NonNegativeInteger(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;
    private async Task<JsonDocument> Read(Uri uri, bool allowNotFound = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent); request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) return JsonDocument.Parse("{}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new ApiException(429, "Open Food Facts временно ограничил запросы. Повторите чуть позже.", 60);
            if (!response.IsSuccessStatusCode) throw new ApiException(503, "Open Food Facts сейчас недоступен. Повторите позже.");
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new ApiException(502, "Ответ каталога слишком большой.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var data = new MemoryStream(); var buffer = new byte[16384];
            while (true) { var read = await stream.ReadAsync(buffer, timeout.Token); if (read == 0) break; if (data.Length + read > limit) throw new ApiException(502, "Ответ каталога слишком большой."); data.Write(buffer, 0, read); }
            return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (OperationCanceledException) { throw new ApiException(504, "Open Food Facts не ответил вовремя."); }
        catch (HttpRequestException) { throw new ApiException(503, "Не удалось связаться с Open Food Facts."); }
        catch (JsonException) { throw new ApiException(502, "Open Food Facts вернул некорректный ответ."); }
    }
    internal static CatalogProduct? Parse(JsonElement product)
    {
        if (product.ValueKind != JsonValueKind.Object) return null;
        static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null : null;
        static string? Plain(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var clean = new string(FoodText.Decode(value).Select(c => char.IsControl(c) ? ' ' : c).Take(max).ToArray()).Trim();
            return clean.Length > 0 ? clean : null;
        }
        static string? Brands(JsonElement product)
        {
            if (!product.TryGetProperty("brands", out var brands) || brands.ValueKind != JsonValueKind.Array) return Text(product, "brands");
            return string.Join(", ", brands.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Take(8).Select(item => Plain(item.GetString(), 120)).Where(item => item is not null).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        var barcode = Text(product, "code");
        if (!FoodValidation.ValidOffCode(barcode)) return null;
        barcode = FoodValidation.NormalizeOffCode(barcode!);
        var name = Plain(Text(product, "product_name_ru"), 200) ?? Plain(Text(product, "product_name_be"), 200) ?? Plain(Text(product, "product_name"), 200)
            ?? Plain(Text(product, "generic_name_ru"), 200) ?? Plain(Text(product, "product_name_en"), 200)
            ?? Plain(Text(product, "generic_name"), 200) ?? Plain(Text(product, "generic_name_en"), 200);
        if (string.IsNullOrWhiteSpace(name)) name = "Продукт " + barcode;
        var values = ReadNutrition(product);
        if (values.Calories is null && values is { Protein: { } protein, Fat: { } fat, Carbs: { } carbs } && protein + fat + carbs <= 105 &&
            !Regex.IsMatch(name, @"пив|вин[оа]|водк|виск|коньяк|лик[её]р|\bром\b|beer|wine|whisk|vodka|liqueur|\brum\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(30)))
            values = values with { Calories = decimal.Round(4 * protein + 9 * fat + 4 * carbs, 2),
                Details = (values.Details ?? new() { Basis = values.Basis }) with { CaloriesEstimated = true } };
        return new("off:" + barcode, FoodValidation.GtinOrNull(barcode!), name.Trim(), Plain(Brands(product), 120), null, values.Calories, values.Protein, values.Fat, values.Carbs, values.Basis, "openfoodfacts",
            "https://world.openfoodfacts.org/product/" + barcode, Text(product, "last_modified_t"))
        { GramsPerMillilitre = ServingDensity(Text(product, "serving_size")), Details = OffProductDetails.Metadata(product, values.Details ?? new() { Basis = values.Basis }) };
    }

    private sealed record NutritionValues(string Basis, decimal? Calories = null, decimal? Protein = null, decimal? Fat = null, decimal? Carbs = null)
    {
        public FoodDetails? Details { get; init; }
        public bool HasValues => Calories is not null || Protein is not null || Fat is not null || Carbs is not null;
    }
    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null : null;
    private static decimal? Number(JsonElement element, string key) => decimal.TryParse(Text(element, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static (string Basis, decimal Scale)? Unit(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "g" or "г" => ("100g", 1), "kg" or "кг" => ("100g", 1000),
        "ml" or "мл" => ("100ml", 1), "cl" => ("100ml", 10), "dl" => ("100ml", 100), "l" or "л" => ("100ml", 1000), _ => null
    };
    private static string? QuantityBasis(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300) return null;
        var matches = Regex.Matches(text, @"(?<![\p{L}\d])\d+(?:[.,]\d+)?\s*(kg|ml|cl|dl|g|l|кг|мл|г|л)(?!\p{L})", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        var bases = matches.Select(match => Unit(match.Groups[1].Value)?.Basis).Where(b => b is not null).Distinct().ToArray();
        return bases.Length == 1 ? bases[0] : null;
    }
    private static NutritionValues ReadNutrition(JsonElement product)
    {
        var packageBasis = Unit(Text(product, "product_quantity_unit"))?.Basis ?? QuantityBasis(Text(product, "quantity"));
        if (product.TryGetProperty("nutrition", out var nutrition) && nutrition.ValueKind == JsonValueKind.Object)
        {
            // OFF's aggregate prefers prepared food for Nutri-Score. A diary product is
            // the item as sold: dry coffee and its diluted drink must never be mixed.
            var sets = nutrition.TryGetProperty("input_sets", out var inputs) && inputs.ValueKind == JsonValueKind.Array
                ? inputs.EnumerateArray().Where(set => Text(set, "preparation") is "as_sold" or null)
                    .Select(set => (Source: SourcePriority(Text(set, "source")), Values: ReadSet(set, product)))
                    .Where(set => set.Values is { HasValues: true }).OrderBy(set => set.Source)
                    .ThenBy(set => set.Values!.Basis == packageBasis ? 0 : 1).ToArray() : [];
            if (sets.Length > 0)
            {
                var chosen = sets[0].Values!;
                foreach (var additional in sets.Skip(1).Select(set => set.Values!).Where(set => set.Basis == chosen.Basis))
                    chosen = chosen with { Calories = chosen.Calories ?? additional.Calories, Protein = chosen.Protein ?? additional.Protein, Fat = chosen.Fat ?? additional.Fat, Carbs = chosen.Carbs ?? additional.Carbs,
                        Details = OffProductDetails.Merge(chosen.Details!, additional.Details!) };
                return chosen;
            }
            if (nutrition.TryGetProperty("aggregated_set", out var aggregated) && Text(aggregated, "preparation") is "as_sold" or null && ReadSet(aggregated, product) is { HasValues: true } normalized)
                return normalized;
        }
        if (!product.TryGetProperty("nutriments", out var legacy) || legacy.ValueKind != JsonValueKind.Object) return new("unknown");
        var per = Text(product, "nutrition_data_per");
        var servingBasis = Unit(Text(product, "serving_quantity_unit"))?.Basis ?? QuantityBasis(Text(product, "serving_size"));
        // The legacy suffix *_100g covers both grams and millilitres. Resolve the
        // declared liquid unit, not a fictitious gram-to-millilitre density of 1.
        var basis = per == "100ml" ? "100ml" : packageBasis ?? (per == "serving" ? servingBasis : servingBasis ?? "100g");
        decimal? Value(string key, decimal max) => Number(legacy, key) is { } value && value <= max ? value : null;
        var available = Value("carbohydrates_100g", 100);
        if (available is null && Value("carbohydrates-total_100g", 100) is { } total && Value("fiber_100g", 100) is { } fiber && total >= fiber)
            available = total - fiber;
        return new(basis ?? "unknown", Value("energy-kcal_100g", 1500) ?? Value("energy-kj_100g", 6276) / 4.184m,
            Value("proteins_100g", 100), Value("fat_100g", 100), available)
        { Details = OffProductDetails.Legacy(legacy, basis ?? "unknown") };
    }
    private static int SourcePriority(string? source) => source switch { "manufacturer" => 0, "packaging" => 1, "usda" => 2, "estimate" => 3, _ => 4 };
    private static NutritionValues? ReadSet(JsonElement set, JsonElement product)
    {
        if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("nutrients", out var nutrients) || nutrients.ValueKind != JsonValueKind.Object) return null;
        var per = Text(set, "per");
        (string Basis, decimal Quantity) reference = per switch
        {
            "100g" => ("100g", 100m), "100ml" => ("100ml", 100m),
            "1kg" => ("100g", 1000m), "1l" => ("100ml", 1000m), _ => ("unknown", 0m)
        };
        if (per == "serving")
        {
            var unit = Unit(Text(set, "per_unit") ?? Text(product, "serving_quantity_unit"));
            var quantity = Number(set, "per_quantity") ?? Number(product, "serving_quantity");
            if (unit is { } u && quantity is > 0 and <= 100000) reference = (u.Basis, quantity.Value * u.Scale);
        }
        if (reference.Quantity <= 0) return null;
        decimal? Nutrient(string key, decimal maximum, bool energy = false)
        {
            if (!nutrients.TryGetProperty(key, out var nutrient) || nutrient.ValueKind != JsonValueKind.Object || Text(nutrient, "modifier") is not (null or "" or "~" or "≈")) return null;
            var raw = Number(nutrient, "value") ?? Number(nutrient, "value_computed");
            if (raw is null) return null;
            var unit = Text(nutrient, "unit")?.ToLowerInvariant();
            decimal? factor = energy ? unit switch { "kj" => 1m / 4.184m, "kcal" => 1, null or "" => key == "energy-kj" ? 1m / 4.184m : 1, _ => null }
                : unit switch { "g" or null or "" => 1, "mg" => .001m, "μg" or "µg" or "mcg" => .000001m, "kg" => 1000, _ => null };
            if (factor is null) return null;
            try { var value = raw.Value * factor.Value * 100 / reference.Quantity; return value <= maximum ? value : null; }
            catch (OverflowException) { return null; }
        }
        var available = Nutrient("carbohydrates", 100);
        if (available is null && Nutrient("carbohydrates-total", 100) is { } total && Nutrient("fiber", 100) is { } fiber && total >= fiber)
            available = total - fiber;
        return new(reference.Basis, Nutrient("energy-kcal", 1500, true) ?? Nutrient("energy-kj", 1500, true),
            Nutrient("proteins", 100), Nutrient("fat", 100), available)
        { Details = OffProductDetails.FromSet(nutrients, reference.Basis, reference.Quantity) };
    }
    private static decimal? ServingDensity(string? serving)
    {
        if (string.IsNullOrWhiteSpace(serving) || serving.Length > 300) return null;
        // Only an explicit equivalence for the same serving, e.g. "30 ml (27 g)".
        // Package volume and nutrient ratios alone do not establish a density.
        var pair = Regex.Match(serving, @"^\s*(?<a>\d+(?:[.,]\d+)?)\s*(?<au>kg|ml|cl|dl|g|l)\s*\(\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<bu>kg|ml|cl|dl|g|l)\s*\)\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        if (!pair.Success || Unit(pair.Groups["au"].Value) is not { } a || Unit(pair.Groups["bu"].Value) is not { } b || a.Basis == b.Basis) return null;
        if (!decimal.TryParse(pair.Groups["a"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var first) ||
            !decimal.TryParse(pair.Groups["b"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var second)) return null;
        if (first is <= 0 or > 100000 || second is <= 0 or > 100000) return null;
        var grams = a.Basis == "100g" ? first * a.Scale : second * b.Scale;
        var millilitres = a.Basis == "100ml" ? first * a.Scale : second * b.Scale;
        var density = grams / millilitres;
        return density is >= .01m and <= 25m ? density : null;
    }
    private sealed class RequestBudget(int limit)
    {
        private readonly Queue<DateTimeOffset> _requests = new();
        public void Take()
        {
            lock (_requests)
            {
                var now = DateTimeOffset.UtcNow; while (_requests.TryPeek(out var first) && first <= now.AddMinutes(-1)) _requests.Dequeue();
                if (_requests.Count >= limit) throw new ApiException(429, "Слишком много запросов к каталогу. Повторите чуть позже.", Math.Max(1, (int)Math.Ceiling((_requests.Peek().AddMinutes(1) - now).TotalSeconds)));
                _requests.Enqueue(now);
            }
        }
    }
}
