using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;

namespace SneakyFit.Application;

public sealed class CatalogRequestException(string message, int? status = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// One bounded retry for reads. Mutations are never replayed automatically.
public sealed class CatalogHttp(HttpClient client, TimeSpan? attemptTimeout = null, TimeSpan? retryDelay = null)
{
    public async Task<byte[]> Send(Func<HttpRequestMessage> createRequest, bool retryRead, CancellationToken cancellation, IProgress<string>? progress = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var timeout = attemptTimeout ?? TimeSpan.FromSeconds(14); deadline.CancelAfter(timeout);
            try
            {
                var work = Read(createRequest, deadline.Token);
                // Also bounds handlers that ignore cancellation while connecting or reading.
                try { return await work.WaitAsync(timeout, cancellation).ConfigureAwait(false); }
                finally { deadline.Cancel(); _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested && (error is HttpRequestException or OperationCanceledException or TimeoutException or CatalogRequestException))
            {
                var failure = error switch
                {
                    CatalogRequestException known => known,
                    OperationCanceledException or TimeoutException => new CatalogRequestException("Нет ответа (504)", 504),
                    HttpRequestException network when network.InnerException is AuthenticationException => new CatalogRequestException("Не удалось проверить сервер"),
                    _ => new CatalogRequestException("Нет соединения", 503)
                };
                if (!retryRead || attempt >= 1 || failure.Status is not (408 or 502 or 503 or 504)) throw failure;
                progress?.Report("Повторяю подключение…");
                await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(1400 + Random.Shared.Next(600)), cancellation).ConfigureAwait(false);
            }
        }
    }
    private async Task<byte[]> Read(Func<HttpRequestMessage> factory, CancellationToken cancellation)
    {
        using var request = factory();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
            throw new CatalogRequestException("Каталог занят (429)", status, wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
        }
        if (status is 408 or 502 or 503 or 504) throw new CatalogRequestException(status == 504 || status == 408 ? $"Нет ответа ({status})" : $"Каталог недоступен ({status})", status);
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new CatalogRequestException("Ответ каталога слишком большой");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new CatalogRequestException("Ответ каталога слишком большой");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (!response.IsSuccessStatusCode)
        {
            var message = status switch { 401 => "Войдите в каталог заново", 404 => "Продукт не найден", 409 => "Продукт изменён. Обновите список", _ => $"Ошибка каталога ({status})" };
            if (status == 400)
                try { using var json = JsonDocument.Parse(bytes); if (json.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 and < 200 } text) message = text; } catch (JsonException) { }
            throw new CatalogRequestException(message, status);
        }
        return bytes;
    }
}
