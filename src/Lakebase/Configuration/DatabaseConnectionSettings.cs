namespace Lakebase;

/// <summary>
/// Branch database connection settings (SampleApp, lakebase db Flyway commands, credential minting).
/// Locally these come from the branch metadata the Lakebase SCM extension's post-checkout hook writes
/// to <c>.env</c>; CI sets <c>DATABASE_URL</c> / <c>LAKEBASE_ENDPOINT</c> directly.
/// </summary>
public sealed class DatabaseConnectionSettings : IWorkspaceCredentials
{
    public const string DefaultDatabaseName = "databricks_postgres";
    public const string DefaultEndpointId = "primary";

    public required string DatabricksHost { get; init; }
    public string? DatabricksToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ConfigProfile { get; init; }
    public required string ConnectionString { get; init; }
    public string? Endpoint { get; init; }
    public bool UseOAuthTokenRefresh { get; init; }

    public static DatabaseConnectionSettings Load(IDatabricksCli? cli = null)
    {
        EnvConfig.LoadDatabase();

        var profile = EnvConfig.Get("DATABRICKS_CONFIG_PROFILE");
        var host = EnvConfig.WorkspaceHost(profile)
            ?? throw new InvalidOperationException(
                "Set DATABRICKS_CONFIG_PROFILE (a 'databricks auth login' profile) or DATABRICKS_HOST in .env");

        var credentials = new Credentials(
            host,
            EnvConfig.Get("DATABRICKS_TOKEN"),
            EnvConfig.Get("LAKEBASE_DATABASE_CLIENT_ID") ?? EnvConfig.Get("DATABRICKS_CLIENT_ID"),
            EnvConfig.Get("LAKEBASE_DATABASE_CLIENT_SECRET") ?? EnvConfig.Get("DATABRICKS_CLIENT_SECRET"),
            profile);

        var endpoint = ResolveEndpoint();
        var connectionString = EnvConfig.Get("DATABASE_URL") is { } databaseUrl
            ? DatabaseUrl.ToNpgsql(databaseUrl)
            : FromBranchMetadata(credentials, cli);

        // Unset means "mint when we know the endpoint", so a stale DB_PASSWORD in .env is never used.
        // CI sets LAKEBASE_USE_OAUTH_TOKEN=false alongside a freshly minted password.
        var useOAuth = EnvConfig.Get("LAKEBASE_USE_OAUTH_TOKEN") is { } flag
            ? string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
            : endpoint is not null;

        return new DatabaseConnectionSettings
        {
            DatabricksHost = host,
            DatabricksToken = credentials.DatabricksToken,
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            ConfigProfile = profile,
            ConnectionString = connectionString,
            Endpoint = endpoint,
            UseOAuthTokenRefresh = useOAuth,
        };
    }

    /// <summary>
    /// <c>LAKEBASE_ENDPOINT</c> may be a full resource path (this repo and CI) or just the endpoint id
    /// (the extension's convention, default <c>primary</c>) combined with the project and branch ids.
    /// </summary>
    public static string? ResolveEndpoint()
    {
        var endpoint = EnvConfig.Get("LAKEBASE_ENDPOINT");
        if (endpoint?.Contains('/') == true)
            return endpoint;

        var project = EnvConfig.Get("LAKEBASE_PROJECT_ID");
        var branch = ResolveBranchId();
        if (project is null || branch is null)
            return null;
        return $"projects/{project}/branches/{branch}/endpoints/{endpoint ?? DefaultEndpointId}";
    }

    /// <summary>
    /// <c>LAKEBASE_BRANCH_ID</c> (written by the extension), else the Lakebase branch paired with the
    /// checked-out git branch, so a fresh .env on main works before the extension has synced it.
    /// </summary>
    public static string? ResolveBranchId()
        => EnvConfig.Get("LAKEBASE_BRANCH_ID")
            ?? (GitBranch.Current() is { } gitBranch ? GitBranch.ToLakebaseBranch(gitBranch) : null);

    public static string DatabaseName()
        => EnvConfig.Get("LAKEBASE_DATABASE_NAME")
            ?? EnvConfig.Get("PGDATABASE")
            ?? EnvConfig.Get("DB_NAME")
            ?? DefaultDatabaseName;

    private static string FromBranchMetadata(Credentials credentials, IDatabricksCli? cli)
    {
        var pgHost = EnvConfig.Get("LAKEBASE_HOST")
            ?? LookUpEndpointHost(credentials, cli)
            ?? throw new InvalidOperationException(
                "No branch connection: set LAKEBASE_PROJECT_ID in .env and check out a git branch whose Lakebase "
                + "branch exists (main pairs with production), or click Refresh Credentials in the Lakebase SCM view.");

        var username = EnvConfig.Get("DB_USERNAME");
        if (username is null && WorkspaceAuthModes.Resolve(credentials) == WorkspaceAuthMode.DatabricksCli)
        {
            username = DatabricksCli.GetCurrentUserNameAsync(
                cli ?? DatabricksCli.Default, credentials.ConfigProfile, credentials.DatabricksHost)
                .GetAwaiter().GetResult();
        }

        return DatabaseUrl.Build(
            pgHost,
            5432,
            DatabaseName(),
            username ?? throw new InvalidOperationException("Set DB_USERNAME in .env (your Postgres role)."),
            password: null);
    }

    /// <summary>Asks the Lakebase API for the resolved branch's endpoint host (one GET, at startup).</summary>
    private static string? LookUpEndpointHost(Credentials credentials, IDatabricksCli? cli)
    {
        var project = EnvConfig.Get("LAKEBASE_PROJECT_ID");
        var branch = ResolveBranchId();
        if (project is null || branch is null)
            return null;

        var api = new LakebaseApiClient(credentials, new DatabricksWorkspaceAuth(credentials, cli));
        using var endpoints = api.TryGetAsync($"/api/2.0/postgres/projects/{project}/branches/{branch}/endpoints")
            .GetAwaiter().GetResult();
        return endpoints is null ? null : LakebasePlatformService.TryGetEndpointHost(endpoints.RootElement);
    }

    private sealed record Credentials(
        string DatabricksHost,
        string? DatabricksToken,
        string? ClientId,
        string? ClientSecret,
        string? ConfigProfile) : IWorkspaceCredentials;
}
