using Npgsql;

namespace Lakebase;

public static class LakebaseConnectionFactory
{
    /// <summary>One-off connection for CLI operations. Apps should use a shared data source.</summary>
    public static async Task<NpgsqlConnection> OpenConnectionAsync(
        DatabaseConnectionSettings settings,
        CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(await ResolveConnectionStringAsync(settings, ct));
        await connection.OpenAsync(ct);
        return connection;
    }

    public static async Task<string> ResolveConnectionStringAsync(
        DatabaseConnectionSettings settings,
        CancellationToken ct = default)
    {
        if (!ShouldRefreshToken(settings))
            return settings.ConnectionString;

        var token = await new LakebaseTokenProvider(settings).GetTokenAsync(ct);
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            Password = token,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Builds a pooled data source with automatic credential rotation. Create exactly one per
    /// endpoint for the process lifetime (register as a singleton); each call creates a new pool.
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(DatabaseConnectionSettings settings)
    {
        if (!ShouldRefreshToken(settings))
            return NpgsqlDataSource.Create(settings.ConnectionString);

        var tokenProvider = new LakebaseTokenProvider(settings);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionStringWithoutPassword(settings.ConnectionString));
        // The provider caches until shortly before expiry, so frequent polling is cheap and
        // keeps new physical connections on a fresh credential.
        dataSourceBuilder.UsePeriodicPasswordProvider(
            async (_, tokenCt) => await tokenProvider.GetTokenAsync(tokenCt),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(10));
        return dataSourceBuilder.Build();
    }

    private static string ConnectionStringWithoutPassword(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = null,
        };
        return builder.ConnectionString;
    }

    private static bool ShouldRefreshToken(DatabaseConnectionSettings settings)
        => settings.UseOAuthTokenRefresh
            && GitHubOidc.CanMint(settings)
            && !string.IsNullOrEmpty(settings.Endpoint);
}
