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
        var branch = EnvConfig.Get("LAKEBASE_BRANCH_ID");
        if (project is null || branch is null)
            return null;
        return $"projects/{project}/branches/{branch}/endpoints/{endpoint ?? DefaultEndpointId}";
    }

    public static string DatabaseName()
        => EnvConfig.Get("LAKEBASE_DATABASE_NAME")
            ?? EnvConfig.Get("PGDATABASE")
            ?? EnvConfig.Get("DB_NAME")
            ?? DefaultDatabaseName;

    private static string FromBranchMetadata(Credentials credentials, IDatabricksCli? cli)
    {
        var pgHost = EnvConfig.Get("LAKEBASE_HOST")
            ?? throw new InvalidOperationException(
                "No branch connection in .env (LAKEBASE_HOST / DATABASE_URL). Check out a git branch so the "
                + "Lakebase SCM hook writes it, or run 'lakebase platform branch create'.");

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

    private sealed record Credentials(
        string DatabricksHost,
        string? DatabricksToken,
        string? ClientId,
        string? ClientSecret,
        string? ConfigProfile) : IWorkspaceCredentials;
}
