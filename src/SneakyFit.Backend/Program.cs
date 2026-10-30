using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using SneakyFit.Backend;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false; server.Limits.MaxRequestBodySize = 32 * 1024;
    server.Limits.MaxConcurrentConnections = 64;
    server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    server.ConfigureHttpsDefaults(https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
});
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
var dataDirectory = Path.GetFullPath(builder.Configuration["SneakyFit:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
Directory.CreateDirectory(dataDirectory);
builder.Services.AddSingleton(new Store(Path.Combine(dataDirectory, "app.sqlite")));
builder.Services.AddSingleton(new OffCache(Path.Combine(dataDirectory, "off-cache.sqlite")));
builder.Services.AddSingleton<IExternalCatalog>(services =>
{
    var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4), AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 4 }) { Timeout = Timeout.InfiniteTimeSpan };
    var userAgent = builder.Configuration["OpenFoodFacts:UserAgent"] ?? "SneakyFit/0.7 (personal nutrition tracker)";
    if (userAgent.Length > 300 || userAgent.Any(char.IsControl)) throw new InvalidOperationException("Invalid OpenFoodFacts:UserAgent");
    return new OffCatalog(http, services.GetRequiredService<OffCache>(), userAgent);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("catalog", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString() : "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Слишком много запросов. Повторите позже." }, token);
    };
});

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
if (args.FirstOrDefault()?.StartsWith("--", StringComparison.Ordinal) == true && args[0] is "--create-user" or "--create-invite" or "--revoke-device" or "--list-devices" or "--backup")
{
    try
    {
        if (args.Length != 2) throw new ApiException(400, "Команда требует один аргумент.");
        object result;
        switch (args[0])
        {
            case "--create-user": var created = store.CreateUser(args[1]); result = new { userId = created.UserId, invite = created.Invite, expiresInSeconds = 86400 }; break;
            case "--create-invite": result = new { userId = args[1], invite = store.CreateInvite(args[1]), expiresInSeconds = 86400 }; break;
            case "--revoke-device": result = new { revoked = store.RevokeDevice(args[1]) }; break;
            case "--list-devices": result = store.ListDevices(args[1]); break;
            default:
                var destination = Path.GetFullPath(args[1]); Directory.CreateDirectory(destination);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
                var personal = Path.Combine(destination, "app-" + stamp + ".sqlite"); store.Backup(personal);
                var off = Path.Combine(destination, "off-cache-" + stamp + ".sqlite"); app.Services.GetRequiredService<OffCache>().Backup(off);
                foreach (var old in Directory.EnumerateFiles(destination, "*.sqlite", SearchOption.TopDirectoryOnly))
                    if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(old), @"^(?:app|off-cache)-\d{8}-\d{6}-[0-9a-f]{6}\.sqlite$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                        && (File.GetAttributes(old) & FileAttributes.ReparsePoint) == 0 && File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) File.Delete(old);
                result = new { database = personal, openFoodFactsCache = off }; break;
        }
        Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
    }
    catch (ApiException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
    finally { await app.DisposeAsync(); }
    return;
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (ApiException error)
    {
        context.Response.StatusCode = error.Status;
        if (error.RetryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString();
        await context.Response.WriteAsJsonAsync(new { error = error.Message }, context.RequestAborted);
    }
    catch (BadHttpRequestException error)
    {
        context.Response.StatusCode = error.StatusCode; await context.Response.WriteAsJsonAsync(new { error = "Некорректный запрос." }, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error)
    {
        // Log types, not request bodies, credentials, food records or upstream payloads.
        app.Logger.LogError("Request failed ({ErrorType})", error.GetType().Name);
        context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "Не удалось выполнить запрос." }, context.RequestAborted);
    }
});
app.UseRouting(); app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var identity = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? store.Authenticate(authorization[7..]) : null;
        if (identity is null) { context.Response.Headers.WWWAuthenticate = "Bearer"; throw new ApiException(401, "Подключите профиль с помощью приглашения."); }
        context.Items["identity"] = identity;
    }
    await next();
});
static string User(HttpContext context) => ((Identity)context.Items["identity"]!).UserId;
static string? Operation(HttpContext context)
{
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (key.Length == 0) return null;
    if (key.Length > 80 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new ApiException(400, "Некорректный ключ операции.");
    return key;
}
app.MapGet("/healthz", () => new { status = "ok", customFoodBasis = true, foodUnits = new[] { "100g", "100ml" } });
app.MapPost("/api/devices/enroll", (EnrollmentRequest request, Store db) => db.Enroll(request)).AllowAnonymous().RequireRateLimiting("enrollment");
app.MapPost("/api/devices/invite", (HttpContext context, Store db) => new { invite = db.CreateDeviceInvite(User(context)), expiresInSeconds = 86400 });
app.MapPost("/api/users/invite", (HttpContext context, Store db) => new { invite = db.CreateFriendInvite(User(context)), expiresInSeconds = 86400 });
app.MapGet("/api/catalog/search", async (string? q, int? page, IExternalCatalog off, Store db, CancellationToken cancellation) =>
{
    var query = FoodValidation.Text(q, "Поиск", 120, true); var number = page ?? 1;
    if (query.Length < 2 || number is < 1 or > 10000) throw new ApiException(400, "Введите минимум два символа и корректную страницу.");
    var local = db.SearchCustom(query, number); var external = await off.Search(query, number, cancellation);
    return new SearchResult(local.Take(20).Concat(external.Products).DistinctBy(p => p.Id).ToArray(), local.Count > 20 || external.HasMore, external.Warning);
}).AllowAnonymous().RequireRateLimiting("catalog");
app.MapGet("/api/catalog/barcode/{code}", async (string code, IExternalCatalog off, Store db, CancellationToken cancellation) =>
{
    var barcode = FoodValidation.CanonicalBarcode(code);
    return db.CustomByBarcode(barcode) ?? await off.Barcode(barcode, cancellation) ?? throw new ApiException(404, "Продукт со штрихкодом не найден.");
}).AllowAnonymous().RequireRateLimiting("catalog");
app.MapPost("/api/foods/custom", async (CustomFoodRequest request, HttpContext context, IExternalCatalog off, Store db, CancellationToken cancellation) =>
{
    var product = FoodValidation.Custom(request); CatalogProduct? canonical = null;
    if (product.Barcode is { } barcode && db.CustomByBarcode(barcode) is null) canonical = await off.Barcode(barcode, cancellation);
    return db.CreateCustom(User(context), product, Operation(context), canonical);
});
app.MapGet("/api/foods/personal", (HttpContext context, Store db) => new { products = db.Personal(User(context)) });
app.MapPut("/api/foods/personal", async (CatalogProduct product, HttpContext context, IExternalCatalog off, Store db, CancellationToken cancellation) =>
{
    product = FoodValidation.Product(product);
    if (!FoodValidation.ValidId(product.Id)) throw new ApiException(400, "Неизвестный идентификатор продукта.");
    var canonical = product.Id.StartsWith("custom:", StringComparison.Ordinal) ? db.GetCustom(product.Id)
        : await off.Barcode(FoodValidation.CanonicalBarcode(product.Id[4..]), cancellation);
    if (canonical is null) throw new ApiException(404, "Продукт не найден в каталоге.");
    var revision = context.Request.Headers.IfMatch.ToString().Trim('"');
    var saved = db.SavePersonal(User(context), product with { Id = canonical.Id, Barcode = canonical.Barcode, Source = canonical.Source, SourceUrl = canonical.SourceUrl }, revision.Length == 0 ? null : revision);
    context.Response.Headers.ETag = '"' + saved.Revision + '"'; return saved;
});
await app.RunAsync();

public partial class Program { }
