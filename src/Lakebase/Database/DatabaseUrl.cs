using Npgsql;

namespace Lakebase;

/// <summary>
/// Npgsql accepts only keyword connection strings (<c>Host=...;Username=...</c>). The Lakebase SCM
/// extension writes <c>DATABASE_URL</c> as a <c>postgresql://</c> URI, so convert before use.
/// </summary>
public static class DatabaseUrl
{
    public static string ToNpgsql(string databaseUrl)
    {
        var raw = databaseUrl.Trim();
        if (!FlywayJdbc.IsPostgresUri(raw) && !raw.StartsWith("jdbc:postgresql:", StringComparison.OrdinalIgnoreCase))
            return raw;

        var parts = FlywayJdbc.Parse(raw);
        return Build(parts.Host, parts.Port, parts.Database, parts.User, parts.Password, parts.SslMode);
    }

    public static string Build(
        string host,
        int port,
        string database,
        string? username,
        string? password,
        string sslMode = "require")
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            SslMode = ToNpgsqlSslMode(sslMode),
        };
        if (!string.IsNullOrEmpty(username))
            builder.Username = username;
        if (!string.IsNullOrEmpty(password))
            builder.Password = password;
        return builder.ConnectionString;
    }

    private static SslMode ToNpgsqlSslMode(string sslMode) => sslMode.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => SslMode.Require,
    };
}
