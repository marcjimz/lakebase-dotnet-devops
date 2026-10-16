using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lakebase;

/// <summary>
/// Lakebase REST client on the shared pooled <see cref="LakebaseHttp.Client"/>. GET/DELETE retry on
/// throttling and transient failures; POST retries only on 429 so creates are never replayed.
/// </summary>
public sealed class LakebaseApiClient(IWorkspaceCredentials settings, DatabricksWorkspaceAuth auth)
{
    private readonly Uri _baseAddress = new(settings.DatabricksHost.TrimEnd('/') + "/");

    public async Task<JsonDocument> GetAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, idempotent: true, ct);
        return await ReadJsonResponseAsync(HttpMethod.Get, path, response, ct);
    }

    /// <summary>GET that returns null on 404.</summary>
    public async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, idempotent: true, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        return await ReadJsonResponseAsync(HttpMethod.Get, path, response, ct);
    }

    public async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, idempotent: false, ct);
        return await ReadJsonResponseAsync(HttpMethod.Post, path, response, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, body: null, idempotent: true, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Lakebase API DELETE {path} failed ({(int)response.StatusCode}): {LakebaseHttp.SafeErrorSummary(body)}");
        }
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool idempotent,
        CancellationToken ct)
        => LakebaseHttp.SendWithRetryAsync(async attemptCt =>
        {
            var token = await auth.GetAccessTokenAsync(attemptCt);
            var request = new HttpRequestMessage(method, new Uri(_baseAddress, path.TrimStart('/')));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return request;
        }, idempotent, ct);

    private static async Task<JsonDocument> ReadJsonResponseAsync(
        HttpMethod method,
        string path,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Lakebase API {method} {path} failed ({(int)response.StatusCode} {response.StatusCode}): {LakebaseHttp.SafeErrorSummary(body)}");

        return string.IsNullOrWhiteSpace(body)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(body);
    }
}
