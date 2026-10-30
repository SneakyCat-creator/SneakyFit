using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SneakyFit.Application;

namespace SneakyFit.Desktop;

internal sealed class CatalogClient(Guid profileId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Uri Endpoint = new(CatalogConnection.DefaultEndpoint);
    private static readonly Lazy<HttpClient> Transport = new(CreateTransport);
    public bool IsConnected => CatalogConnection.LoadCredentials(profileId) is not null;
    private static HttpClient CreateTransport()
    {
        if (Endpoint.Scheme != Uri.UriSchemeHttps || CatalogConnection.CertificatePin.Length != 64) throw new InvalidOperationException("Сервер каталога ещё не настроен.");
        var pin = Convert.FromHexString(CatalogConnection.CertificatePin);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 4 };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 certificate &&
            DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin);
        var client = new HttpClient(handler) { BaseAddress = Endpoint, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SneakyFit/1.1.0 (Windows desktop)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
    public Task<CatalogPage> Search(string query, int page, CancellationToken ct, IProgress<string>? progress = null) => Send<CatalogPage>(HttpMethod.Get, "api/catalog/search?q=" + Uri.EscapeDataString(query.Trim()) + "&page=" + page, null, ct, authenticated: false, progress: progress);
    public Task<CatalogProduct> Barcode(string barcode, CancellationToken ct) => Send<CatalogProduct>(HttpMethod.Get, "api/catalog/barcode/" + Uri.EscapeDataString(barcode.Trim()), null, ct, authenticated: false);
    public Task<PersonalProducts> Personal(CancellationToken ct) => Send<PersonalProducts>(HttpMethod.Get, "api/foods/personal", null, ct);
    public async Task<DeviceInvitation> InviteFriend(CancellationToken ct)
    {
        try { return await Send<DeviceInvitation>(HttpMethod.Post, "api/users/invite", null, ct); }
        catch (CatalogRequestException error) when (error.Status == 404)
        { throw new InvalidOperationException("Приглашения друзей появятся после обновления сервера."); }
    }
    public async Task<bool> SupportsDrinkCreation(CancellationToken ct)
    {
        var capabilities = await Send<JsonElement>(HttpMethod.Get, "healthz", null, ct, authenticated: false);
        return capabilities.TryGetProperty("customFoodBasis", out var supported) && supported.ValueKind == JsonValueKind.True;
    }
    public Task<CatalogProduct> SavePersonal(CatalogProduct product, CancellationToken ct, string? expectedRevision = null) => Send<CatalogProduct>(HttpMethod.Put, "api/foods/personal", product, ct, expectedRevision: expectedRevision);
    public Task<CustomFoodResult> Create(CustomFoodRequest product, Guid operationId, CancellationToken ct) => Send<CustomFoodResult>(HttpMethod.Post, "api/foods/custom", product, ct, operationId: operationId);
    public async Task Enroll(string invite, CancellationToken ct)
    {
        var credentials = await Send<DeviceCredentials>(HttpMethod.Post, "api/devices/enroll", new { invite = invite.Trim(), deviceName = Environment.MachineName }, ct, authenticated: false);
        CatalogConnection.SaveCredentials(profileId, credentials.Token, credentials.UserId);
    }
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool authenticated = true, string? expectedRevision = null, Guid? operationId = null, IProgress<string>? progress = null)
    {
        var credentials = authenticated ? CatalogConnection.LoadCredentials(profileId) ?? throw new InvalidOperationException("Подключите устройство к каталогу.") : null;
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(method, path);
            if (expectedRevision is not null) request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"" + expectedRevision.Replace("\"", "") + "\""));
            if (operationId is not null) request.Headers.Add("Idempotency-Key", operationId.Value.ToString("D"));
            if (credentials is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            return request;
        }
        var bytes = await new CatalogHttp(Transport.Value).Send(Request, method == HttpMethod.Get, cancellationToken, progress);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidOperationException("Каталог вернул пустой ответ."); }
        catch (JsonException) { throw new InvalidOperationException("Не удалось прочитать ответ каталога."); }
    }
}
