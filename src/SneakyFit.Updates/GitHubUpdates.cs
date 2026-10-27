using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SneakyFit.Updates;

public sealed record GitHubRelease(Version Version, Uri DownloadUrl, long Size, string? Digest);
public sealed record DownloadedUpdate(string Directory, string Executable, ExecutableRelease Release);

public static class GitHubUpdates
{
    public const string Repository = "SneakyCat-creator/SneakyFit";
    public const string AssetName = "SneakyFit.exe";
    private static readonly Uri Latest = new("https://api.github.com/repos/" + Repository + "/releases/latest");
    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static GitHubRelease? _cached;
    private static DateTimeOffset _validUntil;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SneakyFit/1.0 (Windows)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static async Task<GitHubRelease?> Find(CancellationToken cancellation)
    {
        await CheckGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _validUntil) return _cached;
            for (var attempt = 0; ; attempt++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(14));
                try
                {
                    using var response = await Get(Latest, deadline.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound) { Cache(null); return null; }
                    EnsureSuccess(response);
                    var bytes = await ReadBounded(response, 512 * 1024, deadline.Token).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(bytes);
                    var root = json.RootElement;
                    var tag = root.GetProperty("tag_name").GetString();
                    if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
                        !TryVersion(tag, out var version)) throw new InvalidDataException("Нет подходящего обновления.");
                    var assets = root.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == AssetName).ToArray();
                    if (assets.Length != 1) throw new InvalidDataException("Файл обновления ещё не опубликован.");
                    var asset = assets[0];
                    var size = asset.GetProperty("size").GetInt64();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    var expected = "https://github.com/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag!) + "/" + AssetName;
                    if (size is < 1024 or > ReleaseTrust.MaxPackageSize || url != expected || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        throw new InvalidDataException("Некорректный файл обновления.");
                    string? digest = null;
                    if (asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var raw = value.GetString();
                        if (raw?.StartsWith("sha256:", StringComparison.Ordinal) == true && ReleaseTrust.IsHash(raw[7..])) digest = raw[7..];
                    }
                    var found = new GitHubRelease(version, uri, size, digest); Cache(found); return found;
                }
                catch (Exception error) when (Retryable(error, cancellation) && attempt < 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1600 + Random.Shared.Next(600)), cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new IOException("Нет ответа от GitHub (504). Повторите позже."); }
                catch (HttpRequestException error)
                { throw new IOException(error.StatusCode is { } status ? $"GitHub недоступен ({(int)status}). Повторите позже." : "Нет соединения с GitHub. Повторите позже.", error); }
            }
        }
        finally { CheckGate.Release(); }
    }

    private static void Cache(GitHubRelease? release) { _cached = release; _validUntil = DateTimeOffset.UtcNow.AddMinutes(5); }
    public static bool TryVersion(string? tag, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (tag is null || tag.Length > 32 || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var parsed) || parsed.Major < 1) return false;
        version = new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)); return true;
    }

    public static async Task<DownloadedUpdate> Download(GitHubRelease release, IProgress<int>? progress, CancellationToken cancellation)
    {
        var directory = UpdateHandoff.NewDirectory(); var file = Path.Combine(directory, "download.exe");
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromMinutes(4));
                try
                {
                    using var response = await Get(release.DownloadUrl, deadline.Token).ConfigureAwait(false); EnsureSuccess(response);
                    if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException("Размер обновления изменился. Повторите позже.");
                    await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                    await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                    {
                        var buffer = new byte[128 * 1024]; long total = 0; int count, previous = -1;
                        while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                        {
                            total += count; if (total > release.Size) throw new InvalidDataException("Размер обновления не совпадает.");
                            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                            var percent = (int)(total * 100 / release.Size); if (percent != previous) { progress?.Report(percent); previous = percent; }
                        }
                        if (total != release.Size) throw new IOException("Загрузка прервалась. Повторите позже.");
                    }
                    cancellation.ThrowIfCancellationRequested();
                    var signed = await Task.Run(() => ReleaseTrust.VerifyExecutable(file), cancellation).ConfigureAwait(false);
                    if (Version.Parse(signed.Version) != release.Version) throw new InvalidDataException("Версия обновления не совпадает.");
                    if (release.Digest is { } digest && !ReleaseTrust.HashFile(file).Equals(digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Файл обновления повреждён.");
                    return new(directory, file, signed);
                }
                catch (Exception error) when (Retryable(error, cancellation) && attempt < 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new IOException("Загрузка не завершилась (504). Повторите позже."); }
                catch (HttpRequestException error)
                { throw new IOException(error.StatusCode is { } status ? $"Ошибка загрузки ({(int)status}). Повторите позже." : "Нет соединения с GitHub. Повторите позже.", error); }
            }
        }
        catch { UpdateHandoff.RemoveDirectory(directory); throw; }
    }

    private static bool Retryable(Exception error, CancellationToken cancellation) => !cancellation.IsCancellationRequested &&
        (error is OperationCanceledException or HttpIOException || error is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout });

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            throw new IOException("GitHub ограничил запросы. Попробуйте позже.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> Get(Uri uri, CancellationToken cancellation)
    {
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                throw new InvalidDataException("Неподдерживаемый адрес обновления.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null || redirect == 4) throw new InvalidDataException("GitHub не вернул файл обновления.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("Некорректный адрес обновления.");
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, int maximum, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Ответ GitHub слишком большой.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Ответ GitHub слишком большой.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
