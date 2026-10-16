using System.Net;

namespace Lakebase;

/// <summary>
/// Shared HTTP plumbing: one pooled <see cref="HttpClient"/> for the process and bounded retries
/// for throttling (429) and transient server/network failures.
/// </summary>
public static class LakebaseHttp
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Sends a request built by <paramref name="createRequest"/> (rebuilt per attempt). 429 is always
    /// retried because the server did not process the call. 5xx and network errors are retried only
    /// when <paramref name="idempotent"/> is true, so non-idempotent creates are never replayed.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpRequestMessage>> createRequest,
        bool idempotent,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Built outside the try: failures here (e.g. token fetch, which retries on its own)
            // must not be multiplied by this loop.
            using var request = await createRequest(ct);
            HttpResponseMessage? response = null;
            try
            {
                response = await Client.SendAsync(request, ct);
                if (attempt >= MaxAttempts || !ShouldRetry(response.StatusCode, idempotent))
                    return response;

                var delay = RetryDelay(response, attempt);
                CliLog.Progress($"==> HTTP {(int)response.StatusCode} from {request.RequestUri?.AbsolutePath}; retrying in {delay.TotalSeconds:0}s ({attempt}/{MaxAttempts})...");
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (idempotent
                && attempt < MaxAttempts
                && !ct.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException)
            {
                response?.Dispose();
                var delay = Backoff(attempt);
                CliLog.Progress($"==> Network error ({ex.GetType().Name}); retrying in {delay.TotalSeconds:0}s ({attempt}/{MaxAttempts})...");
                await Task.Delay(delay, ct);
            }
        }
    }

    public static bool ShouldRetry(HttpStatusCode status, bool idempotent)
        => status == HttpStatusCode.TooManyRequests
            || (idempotent && (int)status >= 500 && status != HttpStatusCode.NotImplemented);

    /// <summary>Truncates error bodies so logs stay readable and never echo large payloads.</summary>
    public static string SafeErrorSummary(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty body)";
        var trimmed = body.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta < MaxRetryAfter ? delta : MaxRetryAfter;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait < MaxRetryAfter ? wait : MaxRetryAfter;
        }

        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
}
