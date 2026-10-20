namespace Lakebase;

/// <summary>
/// Lakebase control-plane settings (branch create/delete, role assignment, promote).
/// Loaded from .env (legacy: .env.platform); never used for Postgres connections.
/// </summary>
public sealed class PlatformSettings : IWorkspaceCredentials
{
    public required string DatabricksHost { get; init; }
    public string? DatabricksToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ConfigProfile { get; init; }
    public required string ProjectId { get; init; }
    public required string MainBranchId { get; init; }
    public string? DevGroup { get; init; }
    public string? AppServicePrincipalId { get; init; }
    public string? ProductionDatabaseUrl { get; init; }
    public string? ProductionEndpoint { get; init; }
    /// <summary>Validation branch name for staging apply / promote cleanup (default: development).</summary>
    public string StagingBranchId { get; init; } = "development";
    public bool AllowBranchDelete { get; init; }
    /// <summary>Lakebase role id for the app SP (default: portal-app).</summary>
    public string AppRoleId { get; init; } = "portal-app";
    /// <summary>Postgres database name on each branch (default: databricks_postgres).</summary>
    public string DatabaseName { get; init; } = "databricks_postgres";

    public static PlatformSettings Load()
    {
        EnvConfig.LoadPlatform();

        var profile = EnvConfig.Get("DATABRICKS_CONFIG_PROFILE");
        var host = EnvConfig.WorkspaceHost(profile)
            ?? throw new InvalidOperationException(
                "Set DATABRICKS_CONFIG_PROFILE (a 'databricks auth login' profile) or DATABRICKS_HOST in .env");
        var projectId = EnvConfig.Get("LAKEBASE_PROJECT_ID")
            ?? throw new InvalidOperationException("LAKEBASE_PROJECT_ID not set in .env");
        var mainBranchId = EnvConfig.Get("LAKEBASE_MAIN_BRANCH_ID") ?? "production";

        var clientId = Environment.GetEnvironmentVariable("LAKEBASE_PLATFORM_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("LAKEBASE_PLATFORM_CLIENT_SECRET")
            ?? Environment.GetEnvironmentVariable("DATABRICKS_CLIENT_SECRET");

        return new PlatformSettings
        {
            DatabricksHost = host,
            DatabricksToken = EnvConfig.Get("DATABRICKS_TOKEN"),
            ClientId = clientId,
            ClientSecret = clientSecret,
            ConfigProfile = profile,
            ProjectId = projectId,
            MainBranchId = mainBranchId,
            DevGroup = Environment.GetEnvironmentVariable("LAKEBASE_DEV_GROUP"),
            AppServicePrincipalId = Environment.GetEnvironmentVariable("LAKEBASE_APP_SP_ID"),
            ProductionDatabaseUrl = Environment.GetEnvironmentVariable("PRODUCTION_DATABASE_URL"),
            ProductionEndpoint = Environment.GetEnvironmentVariable("PRODUCTION_LAKEBASE_ENDPOINT"),
            StagingBranchId = Environment.GetEnvironmentVariable("LAKEBASE_STAGING_BRANCH_ID") ?? "development",
            AppRoleId = Environment.GetEnvironmentVariable("LAKEBASE_APP_ROLE_ID") is { Length: > 0 } roleId ? roleId : "portal-app",
            DatabaseName = Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_NAME") is { Length: > 0 } db ? db : "databricks_postgres",
            AllowBranchDelete = string.Equals(
                Environment.GetEnvironmentVariable("LAKEBASE_ALLOW_BRANCH_DELETE"),
                "true",
                StringComparison.OrdinalIgnoreCase),
        };
    }
}
