namespace Lakebase;

/// <summary>
/// Mints and caches the short-lived Lakebase Postgres credential for one endpoint.
/// Thread-safe; register one instance per data source (see <see cref="LakebaseConnectionFactory.CreateDataSource"/>).
/// </summary>
public class LakebaseTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(10);

    private readonly DatabaseConnectionSettings _settings;
    private readonly LakebaseApiClient _api;
    private string? _currentToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LakebaseTokenProvider(DatabaseConnectionSettings settings)
    {
        _settings = settings;
        var auth = new DatabricksWorkspaceAuth(settings);
        _api = new LakebaseApiClient(settings, auth);
    }

    public LakebaseTokenProvider(string databricksHost, string endpoint, string clientId, string clientSecret)
        : this(new DatabaseConnectionSettings
        {
            DatabricksHost = databricksHost.TrimEnd('/'),
            Endpoint = endpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            ConnectionString = "Host=localhost",
            UseOAuthTokenRefresh = true,
        })
    {
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentToken is null || DateTime.UtcNow >= _tokenExpiry - RefreshSkew)
                await RefreshAsync(ct);
            return _currentToken!;
        }
        finally { _lock.Release(); }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.Endpoint))
            throw new InvalidOperationException("LAKEBASE_ENDPOINT (or LAKEBASE_PROJECT_ID + LAKEBASE_BRANCH_ID) not set in .env");

        using var result = await _api.PostAsync(
            "/api/2.0/postgres/credentials",
            new { endpoint = _settings.Endpoint },
            ct);
        var now = DateTime.UtcNow;
        _currentToken = result.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Credential response missing token");
        _tokenExpiry = TokenLifetime.ExpiryFromCredential(result.RootElement, now);
    }
}
