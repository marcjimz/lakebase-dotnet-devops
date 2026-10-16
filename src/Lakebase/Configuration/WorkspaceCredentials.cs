namespace Lakebase;

public interface IWorkspaceCredentials
{
    string DatabricksHost { get; }
    string? DatabricksToken { get; }
    string? ClientId { get; }
    string? ClientSecret { get; }
}
