using System.Net;

namespace VideoDownloaderConsole;

internal static class HttpRetry
{
    internal const int DefaultRetries = 2;
    internal const int MaximumRetries = 10;

    internal static Task<HttpResponseMessage> SendAsync(HttpClient client,
        Func<HttpRequestMessage> createRequest, HttpCompletionOption completion,
        int maxRetries, CancellationToken cancellationToken) =>
        ExecuteAsync(client, createRequest, completion, maxRetries,
            (response, _) => Task.FromResult(response), false, cancellationToken);

    internal static Task<T> TransferAsync<T>(HttpClient client, Func<HttpRequestMessage> createRequest,
        int maxRetries, Func<HttpResponseMessage, CancellationToken, Task<T>> consume, CancellationToken token) =>
        ExecuteAsync(client, createRequest, HttpCompletionOption.ResponseHeadersRead, maxRetries, consume, true, token);

    private static async Task<T> ExecuteAsync<T>(HttpClient client, Func<HttpRequestMessage> createRequest,
        HttpCompletionOption completion, int maxRetries, Func<HttpResponseMessage, CancellationToken, Task<T>> consume,
        bool disposeSuccess, CancellationToken token)
    {
        if (maxRetries is < 0 or > MaximumRetries) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var request = createRequest();
            HttpResponseMessage? response = null;
            var retained = false;
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
            string reason;
            try
            {
                response = await client.SendAsync(request, completion, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (!IsTransient(response.StatusCode)) response.EnsureSuccessStatusCode();
                    var retryAfter = response.Headers.RetryAfter;
                    delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : delay);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    if (delay > TimeSpan.FromSeconds(30))
                        throw new RetryLaterException($"HTTP {(int)response.StatusCode}. The server requested a retry after {Math.Ceiling(delay.TotalSeconds):0} seconds; please try again later.", response.StatusCode);
                    response.EnsureSuccessStatusCode();
                }
                var result = await consume(response, token);
                retained = !disposeSuccess;
                return result;
            }
            catch (HttpRequestException ex) when (ex is not RetryLaterException && attempt < maxRetries && !token.IsCancellationRequested && IsTransient(ex.StatusCode))
            { reason = ex.Message; }
            catch (OperationCanceledException) when (attempt < maxRetries && !token.IsCancellationRequested)
            { reason = "Request timed out"; }
            catch (TimeoutException ex) when (attempt < maxRetries && !token.IsCancellationRequested)
            { reason = ex.Message; }
            finally { if (!retained) response?.Dispose(); }
            Console.Error.WriteLine($"{reason}. Retrying in {delay.TotalSeconds:0.##}s (attempt {attempt + 2}/{maxRetries + 1})...");
            await Task.Delay(delay, token);
        }
    }

    internal static bool IsTransient(HttpStatusCode? status) => status == null ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status.Value >= 500;

    private sealed class RetryLaterException(string message, HttpStatusCode status) : HttpRequestException(message, null, status);
}
