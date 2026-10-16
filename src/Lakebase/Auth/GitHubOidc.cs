using System.Net.Http.Headers;
using System.Text.Json;

namespace Lakebase;

/// <summary>
/// GitHub Actions OIDC → Databricks workload identity federation (opt-in via
/// <c>LAKEBASE_AUTH_TYPE=github-oidc</c>). The job needs <c>permissions: id-token: write</c>, and the
/// service principal (<c>*_CLIENT_ID</c>) needs a federation policy trusting this repo/environment.
/// No client secret is stored in GitHub.
/// </summary>
public static class GitHubOidc
{
    public const string AuthType = "github-oidc";

    public static bool IsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("LAKEBASE_AUTH_TYPE"), AuthType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the settings can mint workspace tokens without a static token.</summary>
    public static bool CanMint(IWorkspaceCredentials settings)
        => !string.IsNullOrEmpty(settings.ClientId)
            && (!string.IsNullOrEmpty(settings.ClientSecret) || IsEnabled());

    public static async Task<Dictionary<string, string>> TokenExchangeFormAsync(
        IWorkspaceCredentials settings,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.ClientId))
            throw new InvalidOperationException("github-oidc auth requires the service principal client ID (*_CLIENT_ID).");

        var requestUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        if (string.IsNullOrEmpty(requestUrl) || string.IsNullOrEmpty(requestToken))
            throw new InvalidOperationException(
                "github-oidc auth requires a GitHub Actions job with 'permissions: id-token: write'.");

        // Must match an audience in the service principal's federation policy.
        var audience = Environment.GetEnvironmentVariable("DATABRICKS_TOKEN_AUDIENCE")
            ?? $"{settings.DatabricksHost.TrimEnd('/')}/oidc/v1/token";

        var url = $"{requestUrl}&audience={Uri.EscapeDataString(audience)}";
        using var response = await LakebaseHttp.SendWithRetryAsync(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
            return Task.FromResult(request);
        }, idempotent: true, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub OIDC token request failed ({(int)response.StatusCode}): {LakebaseHttp.SafeErrorSummary(body)}");

        using var json = JsonDocument.Parse(body);
        var idToken = json.RootElement.GetProperty("value").GetString()
            ?? throw new InvalidOperationException("GitHub OIDC response missing value");

        return new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
            ["subject_token"] = idToken,
            ["client_id"] = settings.ClientId,
        };
    }
}
