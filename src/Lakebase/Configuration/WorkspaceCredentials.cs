namespace Lakebase;

public interface IWorkspaceCredentials
{
    string DatabricksHost { get; }
    string? DatabricksToken { get; }
    string? ClientId { get; }
    string? ClientSecret { get; }

    /// <summary>Databricks CLI profile (<c>DATABRICKS_CONFIG_PROFILE</c>) used by <see cref="WorkspaceAuthMode.DatabricksCli"/>.</summary>
    string? ConfigProfile => null;
}
