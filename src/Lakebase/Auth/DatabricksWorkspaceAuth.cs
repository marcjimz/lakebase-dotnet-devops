using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lakebase;

/// <summary>
/// Workspace OAuth for Lakebase APIs. The mode comes from <see cref="WorkspaceAuthModes.Resolve"/>:
/// the developer's Databricks CLI login or a PAT locally; service-principal client credentials or
/// GitHub Actions OIDC federation (<c>LAKEBASE_AUTH_TYPE=github-oidc</c>, no stored secret) in CI.
/// </summary>
public sealed class DatabricksWorkspaceAuth(IWorkspaceCredentials settings, IDatabricksCli? cli = null)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WorkspaceAuthMode Mode { get; } = WorkspaceAuthModes.Resolve(settings);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (Mode == WorkspaceAuthMode.Pat)
            return settings.DatabricksToken!;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiry - RefreshSkew)
                return _cachedToken;

            if (Mode == WorkspaceAuthMode.DatabricksCli)
            {
                (_cachedToken, _tokenExpiry) = await DatabricksCli.GetAccessTokenAsync(
                    cli ?? DatabricksCli.Default, settings.ConfigProfile, settings.DatabricksHost, ct);
                return _cachedToken;
            }

            var useOidc = Mode == WorkspaceAuthMode.GitHubOidc;
            var form = useOidc
                ? await GitHubOidc.TokenExchangeFormAsync(settings, ct)
                : new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
            form["scope"] = Environment.GetEnvironmentVariable("LAKEBASE_OAUTH_SCOPE") ?? "postgres";

            using var tokenResp = await LakebaseHttp.SendWithRetryAsync(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.DatabricksHost}/oidc/v1/token")
                {
                    Content = new FormUrlEncodedContent(form),
                };
                if (!useOidc)
                {
                    // HTTP Basic keeps the client secret out of the form body.
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                }

                return Task.FromResult(request);
            }, idempotent: true, ct);

            var body = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Failed to obtain workspace OAuth token ({(int)tokenResp.StatusCode}): {LakebaseHttp.SafeErrorSummary(body)}");

            using var tokenJson = JsonDocument.Parse(body);
            var now = DateTime.UtcNow;
            _cachedToken = tokenJson.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Token response missing access_token");
            _tokenExpiry = TokenLifetime.ExpiryFromExpiresIn(tokenJson.RootElement, now);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
