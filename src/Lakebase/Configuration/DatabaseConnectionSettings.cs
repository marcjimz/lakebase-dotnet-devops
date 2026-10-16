namespace Lakebase;

/// <summary>
/// Branch database connection settings (SampleApp, lakebase db Flyway commands, credential minting).
/// Loaded from .env.database — separate from platform provisioning credentials.
/// </summary>
public sealed class DatabaseConnectionSettings : IWorkspaceCredentials
{
    public required string DatabricksHost { get; init; }
    public string? DatabricksToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string ConnectionString { get; init; }
    public string? Endpoint { get; init; }
    public bool UseOAuthTokenRefresh { get; init; }

    public static DatabaseConnectionSettings Load()
    {
        EnvConfig.LoadDatabase();

        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? throw new InvalidOperationException("DATABASE_URL not set in .env.database");
        var host = Environment.GetEnvironmentVariable("DATABRICKS_HOST")
            ?? throw new InvalidOperationException("DATABRICKS_HOST not set in .env.database");

        var clientId = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_SECRET")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_SECRET");

        return new DatabaseConnectionSettings
        {
            DatabricksHost = host.TrimEnd('/'),
            DatabricksToken = Environment.GetEnvironmentVariable("DATABRICKS_TOKEN"),
            ClientId = clientId,
            ClientSecret = clientSecret,
            ConnectionString = connectionString,
            Endpoint = Environment.GetEnvironmentVariable("LAKEBASE_ENDPOINT"),
            UseOAuthTokenRefresh = string.Equals(
                Environment.GetEnvironmentVariable("LAKEBASE_USE_OAUTH_TOKEN"),
                "true",
                StringComparison.OrdinalIgnoreCase),
        };
    }
}
