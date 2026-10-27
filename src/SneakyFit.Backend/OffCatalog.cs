using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
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
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO responses(key,body,expires_at) VALUES($key,$body,$expiry) ON CONFLICT(key) DO UPDATE SET body=excluded.body,expires_at=excluded.expires_at";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(value, JsonDefaults.Options)); cmd.Parameters.AddWithValue("$expiry", DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds()); cmd.ExecuteNonQuery();
        using var prune = db.CreateCommand(); prune.Transaction = tx; prune.CommandText = "DELETE FROM responses WHERE expires_at<=$now OR key IN (SELECT key FROM responses ORDER BY expires_at DESC LIMIT -1 OFFSET 5000)";
        prune.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); prune.ExecuteNonQuery(); tx.Commit();
    }
    public void Backup(string target) { using var source = Open(); using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString()); destination.Open(); source.BackupDatabase(destination); }
}

public sealed class OffCatalog(HttpClient http, OffCache cache, string userAgent) : IExternalCatalog
{
    private const int PageSize = 20;
    private const string Fields = "code,product_name,product_name_ru,product_name_be,brands,nutriments,nutrition_data_per,nutrition,product_quantity_unit,serving_quantity_unit,last_modified_t";
    private readonly RequestBudget _searchBudget = new(10), _productBudget = new(15);
    private readonly ConcurrentDictionary<string, Lazy<Task<SearchResult>>> _searches = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ProductResult>>> _products = new();
    private sealed record ProductResult(CatalogProduct? Product);

    public Task<SearchResult> Search(string query, int page, CancellationToken cancellationToken)
    {
        var key = $"search:sal-v1:{query.ToLowerInvariant()}:{page}";
        var cached = cache.Read<SearchResult>(key); if (cached is not null) return Task.FromResult(cached);
        return Coalesce(_searches, key, async () =>
        {
            if (cache.Read<SearchResult>(key) is { } saved) return saved;
            _searchBudget.Take();
            using var document = await Read(new Uri($"https://search.openfoodfacts.org/search?q={Uri.EscapeDataString(query)}&page={page}&page_size={PageSize}&langs=ru,be,en&fields={Fields}"));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hits", out var products) || products.ValueKind != JsonValueKind.Array) throw new ApiException(502, "Open Food Facts вернул неполный ответ.");
            var parsed = products.EnumerateArray().Take(PageSize).Select(Parse).OfType<CatalogProduct>().DistinctBy(p => p.Id).ToArray();
            if (products.GetArrayLength() > 0 && parsed.Length == 0) throw new ApiException(502, "Open Food Facts вернул неполный ответ.");
            var count = NonNegativeInteger(root, "count");
            var pageCount = NonNegativeInteger(root, "page_count");
            var hasMore = pageCount is not null ? page < pageCount : count is not null ? (long)page * PageSize < count : products.GetArrayLength() == PageSize;
            // The search index omits the nutrition basis. The selected product is hydrated by barcode.
            var result = new SearchResult(parsed, hasMore, parsed.Any(p => p.Calories is null || p.Protein is null || p.Fat is null || p.Carbs is null) ? "У части продуктов не заполнены КБЖУ." : null);
            cache.Write(key, result, TimeSpan.FromSeconds(90)); return result;
        }, cancellationToken);
    }
    public async Task<CatalogProduct?> Barcode(string barcode, CancellationToken cancellationToken)
    {
        var key = "barcode:" + barcode;
        if (cache.Read<ProductResult>(key) is { } cached) return cached.Product;
        var result = await Coalesce(_products, key, async () =>
        {
            if (cache.Read<ProductResult>(key) is { } saved) return saved;
            _productBudget.Take();
            using var document = await Read(new Uri($"https://world.openfoodfacts.org/api/v3.6/product/{Uri.EscapeDataString(barcode)}?fields={Fields}&lc=ru&cc=by"), allowNotFound: true);
            CatalogProduct? product = null;
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ApiException(502, "Open Food Facts вернул неполный ответ.");
            if (document.RootElement.TryGetProperty("product", out var value)) product = Parse(value);
            var response = new ProductResult(product); cache.Write(key, response, TimeSpan.FromMinutes(product is null ? 2 : 15)); return response;
        }, cancellationToken);
        return result.Product;
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
            return new string(value.Select(c => char.IsControl(c) ? ' ' : c).Take(max).ToArray()).Trim();
        }
        static string? Brands(JsonElement product)
        {
            if (!product.TryGetProperty("brands", out var brands) || brands.ValueKind != JsonValueKind.Array) return Text(product, "brands");
            return string.Join(", ", brands.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Take(8).Select(item => Plain(item.GetString(), 120)).Where(item => item is not null).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        static decimal? Number(JsonElement obj, string key, decimal max)
        {
            var text = Text(obj, key); return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 && value <= max ? value : null;
        }
        var barcode = Text(product, "code");
        if (barcode is null || barcode.Length is < 8 or > 14 || barcode.Any(c => !char.IsAsciiDigit(c))) return null;
        var name = Plain(Text(product, "product_name_ru"), 200) ?? Plain(Text(product, "product_name_be"), 200) ?? Plain(Text(product, "product_name"), 200);
        if (string.IsNullOrWhiteSpace(name)) name = "Продукт " + barcode;
        var basis = "unknown"; decimal? calories = null, protein = null, fat = null, carbs = null;
        if (product.TryGetProperty("nutrition", out var nutrition) && nutrition.ValueKind == JsonValueKind.Object && nutrition.TryGetProperty("aggregated_set", out var aggregated)
            && Text(aggregated, "per") is "100g" or "100ml" && aggregated.TryGetProperty("nutrients", out var nutrients) && nutrients.ValueKind == JsonValueKind.Object)
        {
            basis = Text(aggregated, "per")!;
            decimal? Nutrient(string key, decimal max) => nutrients.TryGetProperty(key, out var nutrient) && Text(nutrient, "modifier") is null ? Number(nutrient, "value", max) : null;
            calories = Nutrient("energy-kcal", 1500) ?? (Nutrient("energy-kj", 6276) / 4.184m); protein = Nutrient("proteins", 100); fat = Nutrient("fat", 100); carbs = Nutrient("carbohydrates", 100);
        }
        else if (product.TryGetProperty("nutriments", out var legacy))
        {
            calories = Number(legacy, "energy-kcal_100g", 1500) ?? Number(legacy, "energy-kj_100g", 6276) / 4.184m;
            protein = Number(legacy, "proteins_100g", 100); fat = Number(legacy, "fat_100g", 100); carbs = Number(legacy, "carbohydrates_100g", 100);
            var per = Text(product, "nutrition_data_per");
            var liquid = Text(product, "product_quantity_unit") is "ml" or "l" || Text(product, "serving_quantity_unit") is "ml" or "l";
            // Legacy *_100g also represents 100 ml. Never infer density from packaging.
            basis = per == "100ml" ? "100ml" : per == "100g" && !liquid ? "100g" : "unknown";
        }
        return new("off:" + barcode, barcode, name.Trim(), Plain(Brands(product), 120), null, calories, protein, fat, carbs, basis, "openfoodfacts",
            "https://world.openfoodfacts.org/product/" + barcode, Text(product, "last_modified_t"));
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
