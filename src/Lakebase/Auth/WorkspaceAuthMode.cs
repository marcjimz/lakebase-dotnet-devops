namespace Lakebase;

/// <summary>
/// How workspace API calls authenticate. Local development uses the developer's own identity
/// (<see cref="DatabricksCli"/>, or <see cref="Pat"/> as a fallback); service principals
/// (<see cref="ClientCredentials"/>, <see cref="GitHubOidc"/>) are for CI/CD.
/// </summary>
public enum WorkspaceAuthMode
{
    /// <summary>OAuth U2M via <c>databricks auth login</c>; tokens come from <c>databricks auth token</c>.</summary>
    DatabricksCli,
    /// <summary>Static <c>DATABRICKS_TOKEN</c>.</summary>
    Pat,
    /// <summary>Service principal client ID + secret (CI).</summary>
    ClientCredentials,
    /// <summary>Service principal client ID + GitHub Actions OIDC federation (CI).</summary>
    GitHubOidc,
}

public static class WorkspaceAuthModes
{
    /// <summary>
    /// <c>LAKEBASE_AUTH_MODE</c> wins when set. Otherwise infer: a static token, then service
    /// principal settings (the existing CI contract), then the Databricks CLI login.
    /// </summary>
    public static WorkspaceAuthMode Resolve(IWorkspaceCredentials settings)
    {
        var explicitMode = Environment.GetEnvironmentVariable("LAKEBASE_AUTH_MODE");
        if (!string.IsNullOrWhiteSpace(explicitMode))
            return Validate(Parse(explicitMode), settings);

        if (!string.IsNullOrEmpty(settings.DatabricksToken))
            return WorkspaceAuthMode.Pat;
        if (!string.IsNullOrEmpty(settings.ClientId) && !string.IsNullOrEmpty(settings.ClientSecret))
            return WorkspaceAuthMode.ClientCredentials;
        if (!string.IsNullOrEmpty(settings.ClientId) && Lakebase.GitHubOidc.IsEnabled())
            return WorkspaceAuthMode.GitHubOidc;
        return WorkspaceAuthMode.DatabricksCli;
    }

    public static WorkspaceAuthMode Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "databricks-cli" or "cli" or "oauth" or "u2m" => WorkspaceAuthMode.DatabricksCli,
        "pat" or "token" => WorkspaceAuthMode.Pat,
        "client-credentials" or "m2m" => WorkspaceAuthMode.ClientCredentials,
        Lakebase.GitHubOidc.AuthType => WorkspaceAuthMode.GitHubOidc,
        _ => throw new InvalidOperationException(
            $"Unknown LAKEBASE_AUTH_MODE '{value}'. Use databricks-cli, pat, client-credentials, or github-oidc."),
    };

    private static WorkspaceAuthMode Validate(WorkspaceAuthMode mode, IWorkspaceCredentials settings)
    {
        var missing = mode switch
        {
            WorkspaceAuthMode.Pat when string.IsNullOrEmpty(settings.DatabricksToken) => "DATABRICKS_TOKEN",
            WorkspaceAuthMode.ClientCredentials when string.IsNullOrEmpty(settings.ClientId)
                || string.IsNullOrEmpty(settings.ClientSecret) => "service principal client ID and secret",
            WorkspaceAuthMode.GitHubOidc when string.IsNullOrEmpty(settings.ClientId) => "service principal client ID",
            _ => null,
        };
        return missing is null
            ? mode
            : throw new InvalidOperationException($"LAKEBASE_AUTH_MODE={mode} requires {missing}.");
    }
}
