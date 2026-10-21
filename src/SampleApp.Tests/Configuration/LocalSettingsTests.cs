using Lakebase;
using Npgsql;

namespace SampleApp.Tests.Configuration;

/// <summary>Tests that change process environment variables run serially.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment";
}

/// <summary>
/// Sets environment variables for one test and restores them afterwards. The repo's env files
/// are loaded first so a developer's real .env cannot leak into a variable a test cleared.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    private static readonly string[] Managed =
    [
        "DATABRICKS_HOST", "DATABRICKS_CONFIG_PROFILE", "DATABRICKS_CONFIG_FILE", "DATABRICKS_TOKEN",
        "DATABRICKS_CLIENT_ID", "DATABRICKS_CLIENT_SECRET",
        "LAKEBASE_DATABASE_CLIENT_ID", "LAKEBASE_DATABASE_CLIENT_SECRET",
        "LAKEBASE_PLATFORM_CLIENT_ID", "LAKEBASE_PLATFORM_CLIENT_SECRET",
        "LAKEBASE_AUTH_MODE", "LAKEBASE_AUTH_TYPE", "LAKEBASE_USE_OAUTH_TOKEN",
        "LAKEBASE_PROJECT_ID", "LAKEBASE_BRANCH_ID", "LAKEBASE_HOST", "LAKEBASE_ENDPOINT",
        "LAKEBASE_MAIN_BRANCH_ID", "LAKEBASE_DATABASE_NAME", "LAKEBASE_APP_SP_ID",
        "DATABASE_URL", "DB_USERNAME", "DB_PASSWORD", "DB_NAME", "PGDATABASE",
    ];

    private readonly Dictionary<string, string?> _saved = new();

    public EnvScope(params (string Name, string Value)[] values)
    {
        EnvConfig.Load();
        foreach (var name in Managed)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var (name, value) in values)
        {
            if (!_saved.ContainsKey(name))
                _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Builds postgres:// URLs from parts so no credential-shaped literal sits in source.</summary>
internal static class TestUrls
{
    public static string Postgres(string user, string password, string hostAndPath)
        => $"postgresql://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{hostAndPath}";
}

internal sealed class FakeDatabricksCli(Func<IReadOnlyList<string>, string> respond) : IDatabricksCli
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<string> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        Calls.Add(args);
        return Task.FromResult(respond(args));
    }

    public static FakeDatabricksCli Standard() => new(args => args[0] == "auth"
        ? """{"access_token":"cli-token","token_type":"Bearer","expiry":"2099-01-01T00:00:00Z"}"""
        : """{"userName":"dev@example.com"}""");
}

internal sealed record TestCredentials(
    string DatabricksHost = "https://example.invalid",
    string? DatabricksToken = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? ConfigProfile = null) : IWorkspaceCredentials;

public class DatabaseUrlTests
{
    [Fact]
    public void ExtensionUri_BecomesNpgsqlKeywords()
    {
        var cs = DatabaseUrl.ToNpgsql(
            TestUrls.Postgres("dev@example.com", "p@ss", "ep-1.database.us-east-1.cloud.databricks.com:5432/databricks_postgres?sslmode=require"));

        var builder = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("ep-1.database.us-east-1.cloud.databricks.com", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("databricks_postgres", builder.Database);
        Assert.Equal("dev@example.com", builder.Username);
        Assert.Equal("p@ss", builder.Password);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void Jdbc_BecomesNpgsqlKeywords()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.ToNpgsql("jdbc:postgresql://ep-1:5432/db?sslmode=verify-full"));
        Assert.Equal("ep-1", builder.Host);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
    }

    [Fact]
    public void KeywordFormat_PassesThroughUnchanged()
    {
        const string cs = "Host=ep-1;Port=5432;Database=db;Username=app;SSLMode=Require";
        Assert.Equal(cs, DatabaseUrl.ToNpgsql(cs));
    }
}

[Collection(EnvironmentCollection.Name)]
public class WorkspaceAuthModeTests
{
    [Fact]
    public void NoCredentials_DefaultsToDatabricksCli()
    {
        using var _ = new EnvScope();
        Assert.Equal(WorkspaceAuthMode.DatabricksCli, WorkspaceAuthModes.Resolve(new TestCredentials()));
    }

    [Fact]
    public void Token_IsPat()
    {
        using var _ = new EnvScope();
        Assert.Equal(WorkspaceAuthMode.Pat, WorkspaceAuthModes.Resolve(new TestCredentials(DatabricksToken: "dapi")));
    }

    [Fact]
    public void ClientIdAndSecret_IsClientCredentials()
    {
        using var _ = new EnvScope();
        Assert.Equal(
            WorkspaceAuthMode.ClientCredentials,
            WorkspaceAuthModes.Resolve(new TestCredentials(ClientId: "sp", ClientSecret: "secret")));
    }

    [Fact]
    public void ClientIdWithGitHubOidc_IsGitHubOidc()
    {
        using var _ = new EnvScope(("LAKEBASE_AUTH_TYPE", "github-oidc"));
        Assert.Equal(WorkspaceAuthMode.GitHubOidc, WorkspaceAuthModes.Resolve(new TestCredentials(ClientId: "sp")));
    }

    [Fact]
    public void ExplicitMode_Wins()
    {
        using var _ = new EnvScope(("LAKEBASE_AUTH_MODE", "databricks-cli"));
        Assert.Equal(
            WorkspaceAuthMode.DatabricksCli,
            WorkspaceAuthModes.Resolve(new TestCredentials(DatabricksToken: "dapi", ClientId: "sp", ClientSecret: "s")));
    }

    [Fact]
    public void ExplicitPatWithoutToken_Throws()
    {
        using var _ = new EnvScope(("LAKEBASE_AUTH_MODE", "pat"));
        var ex = Assert.Throws<InvalidOperationException>(() => WorkspaceAuthModes.Resolve(new TestCredentials()));
        Assert.Contains("DATABRICKS_TOKEN", ex.Message);
    }

    [Fact]
    public void UnknownMode_Throws()
    {
        using var _ = new EnvScope(("LAKEBASE_AUTH_MODE", "kerberos"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceAuthModes.Resolve(new TestCredentials()));
    }

    [Fact]
    public async Task CliMode_UsesProfileAndCachesToken()
    {
        using var _ = new EnvScope();
        var cli = FakeDatabricksCli.Standard();
        var auth = new DatabricksWorkspaceAuth(new TestCredentials(ConfigProfile: "lab"), cli);

        Assert.Equal("cli-token", await auth.GetAccessTokenAsync());
        Assert.Equal("cli-token", await auth.GetAccessTokenAsync());

        var call = Assert.Single(cli.Calls);
        Assert.Equal(["auth", "token", "--profile", "lab"], call);
    }
}

[Collection(EnvironmentCollection.Name)]
public class DatabaseConnectionSettingsTests
{
    private static string ConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"databrickscfg-{Guid.NewGuid():N}");
        File.WriteAllText(path, """
            [DEFAULT]
            host = https://wrong.cloud.databricks.com

            [lab]
            host = https://lab.cloud.databricks.com/
            auth_type = databricks-cli
            """);
        return path;
    }

    /// <summary>
    /// Enforces the local contract: the .env the Lakebase SCM extension maintains (project id, CLI
    /// profile, hook branch metadata) is enough. No client secret, PAT, or password is required.
    /// </summary>
    [Fact]
    public void ExtensionMetadataOnly_ResolvesWithoutSecretsOrPat()
    {
        var cfg = ConfigFile();
        using var _ = new EnvScope(
            ("DATABRICKS_CONFIG_FILE", cfg),
            ("DATABRICKS_CONFIG_PROFILE", "lab"),
            ("LAKEBASE_PROJECT_ID", "lakebase-dotnet-lab"),
            ("LAKEBASE_BRANCH_ID", "feature-lab-v2"),
            ("LAKEBASE_HOST", "ep-1.database.us-east-1.cloud.databricks.com"),
            ("LAKEBASE_ENDPOINT", "primary"),
            ("DB_USERNAME", "dev@example.com"));

        var settings = DatabaseConnectionSettings.Load();

        Assert.Equal("https://lab.cloud.databricks.com", settings.DatabricksHost);
        Assert.Equal("projects/lakebase-dotnet-lab/branches/feature-lab-v2/endpoints/primary", settings.Endpoint);
        Assert.True(settings.UseOAuthTokenRefresh);
        Assert.True(LakebaseConnectionFactory.ShouldRefreshToken(settings));
        Assert.Equal(WorkspaceAuthMode.DatabricksCli, WorkspaceAuthModes.Resolve(settings));
        Assert.Null(settings.ClientSecret);
        Assert.Null(settings.DatabricksToken);

        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal("ep-1.database.us-east-1.cloud.databricks.com", builder.Host);
        Assert.Equal("databricks_postgres", builder.Database);
        Assert.Equal("dev@example.com", builder.Username);
        Assert.Null(builder.Password);
        File.Delete(cfg);
    }

    [Fact]
    public void MissingUsername_ComesFromCliLogin()
    {
        using var _ = new EnvScope(
            ("DATABRICKS_HOST", "https://lab.cloud.databricks.com"),
            ("LAKEBASE_PROJECT_ID", "p"),
            ("LAKEBASE_BRANCH_ID", "b"),
            ("LAKEBASE_HOST", "ep-1"));

        var settings = DatabaseConnectionSettings.Load(FakeDatabricksCli.Standard());

        Assert.Equal("dev@example.com", new NpgsqlConnectionStringBuilder(settings.ConnectionString).Username);
    }

    [Fact]
    public void ExtensionUriWithPassword_IsNormalizedAndStillMints()
    {
        using var _ = new EnvScope(
            ("DATABRICKS_HOST", "https://lab.cloud.databricks.com"),
            ("LAKEBASE_PROJECT_ID", "p"),
            ("LAKEBASE_BRANCH_ID", "b"),
            ("DATABASE_URL", TestUrls.Postgres("dev@example.com", "stale", "ep-1:5432/databricks_postgres?sslmode=require")));

        var settings = DatabaseConnectionSettings.Load();

        Assert.Equal("ep-1", new NpgsqlConnectionStringBuilder(settings.ConnectionString).Host);
        Assert.True(LakebaseConnectionFactory.ShouldRefreshToken(settings));
    }

    [Fact]
    public void CiShape_IsUnchanged()
    {
        using var _ = new EnvScope(
            ("DATABRICKS_HOST", "https://ci.cloud.databricks.com"),
            ("LAKEBASE_DATABASE_CLIENT_ID", "sp"),
            ("LAKEBASE_DATABASE_CLIENT_SECRET", "secret"),
            ("LAKEBASE_ENDPOINT", "projects/p/branches/pr-1/endpoints/primary"),
            ("DATABASE_URL", "Host=ep-1;Port=5432;Database=databricks_postgres;Username=sp;Password=minted;SSLMode=Require"),
            ("LAKEBASE_USE_OAUTH_TOKEN", "false"));

        var settings = DatabaseConnectionSettings.Load();

        Assert.Equal("projects/p/branches/pr-1/endpoints/primary", settings.Endpoint);
        Assert.False(LakebaseConnectionFactory.ShouldRefreshToken(settings));
        Assert.Equal(WorkspaceAuthMode.ClientCredentials, WorkspaceAuthModes.Resolve(settings));
        Assert.Equal("minted", new NpgsqlConnectionStringBuilder(settings.ConnectionString).Password);
    }

    [Fact]
    public void NoBranchConnection_ExplainsHowToGetOne()
    {
        using var _ = new EnvScope(("DATABRICKS_HOST", "https://lab.cloud.databricks.com"));
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseConnectionSettings.Load());
        Assert.Contains("No branch connection", ex.Message);
    }

    [Theory]
    [InlineData("main", "production")]
    [InlineData("master", "production")]
    [InlineData("feature/member-email", "feature-member-email")]
    public void GitBranch_MapsToLakebaseBranch(string git, string lakebase)
    {
        using var _ = new EnvScope();
        Assert.Equal(lakebase, GitBranch.ToLakebaseBranch(git));
    }

    [Fact]
    public void GitBranch_ReadsHeadAndIgnoresDetached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature/x\n");
        Assert.Equal("feature/x", GitBranch.Current(root));

        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "4e0346c1b2a3\n");
        Assert.Null(GitBranch.Current(root));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void BranchIdFromEnv_WinsOverGit()
    {
        using var _ = new EnvScope(("LAKEBASE_BRANCH_ID", "feature-pinned"), ("LAKEBASE_PROJECT_ID", "p"));
        Assert.Equal("projects/p/branches/feature-pinned/endpoints/primary", DatabaseConnectionSettings.ResolveEndpoint());
    }

    [Fact]
    public void ProfileHost_IsReadFromConfigFile()
    {
        var cfg = ConfigFile();
        Assert.Equal("https://lab.cloud.databricks.com", DatabricksConfigFile.HostForProfile("lab", cfg));
        Assert.Equal("https://wrong.cloud.databricks.com", DatabricksConfigFile.HostForProfile("DEFAULT", cfg));
        Assert.Null(DatabricksConfigFile.HostForProfile("default", cfg));
        File.Delete(cfg);
    }
}

[Collection(EnvironmentCollection.Name)]
public class DatabaseEnvWriterTests
{
    [Fact]
    public void BranchCreate_WritesHookKeysAndDropsStaleConnection()
    {
        using var _ = new EnvScope();
        var path = Path.Combine(Path.GetTempPath(), $"env-{Guid.NewGuid():N}");
        File.WriteAllText(path, $"""
            # mine
            LAKEBASE_PROJECT_ID=lakebase-dotnet-lab
            DATABASE_URL={TestUrls.Postgres("old", "pw", "ep-old:5432/databricks_postgres")}
            DB_PASSWORD=pw
            LAKEBASE_BRANCH_ID=production
            """);

        DatabaseEnvWriter.UpdateBranchConnection(
            path, "ep-new", "projects/lakebase-dotnet-lab/branches/feature-x/endpoints/primary", username: null);

        var lines = File.ReadAllLines(path);
        Assert.Contains("# mine", lines);
        Assert.Contains("LAKEBASE_PROJECT_ID=lakebase-dotnet-lab", lines);
        Assert.Contains("LAKEBASE_HOST=ep-new", lines);
        Assert.Contains("LAKEBASE_BRANCH_ID=feature-x", lines);
        Assert.Contains("LAKEBASE_ENDPOINT=primary", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("DATABASE_URL=") || l.StartsWith("DB_PASSWORD="));
        File.Delete(path);
    }
}
