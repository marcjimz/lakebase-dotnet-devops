using System.Text.RegularExpressions;
using Npgsql;

namespace Lakebase;

/// <summary>
/// Converts DATABASE_URL (Npgsql, postgres URI, or jdbc:postgresql) into Flyway JDBC env values.
/// Never logs passwords.
/// </summary>
public static class FlywayJdbc
{
    public static (string JdbcUrl, string User, string Password) FromDatabaseUrl(string databaseUrl)
    {
        var parts = Parse(databaseUrl);
        return (ToJdbc(parts.Host, parts.Port, parts.Database, parts.SslMode), parts.User, parts.Password);
    }

    /// <summary>Parses any supported DATABASE_URL form. <see cref="PostgresConnectionParts.SslMode"/> uses libpq/JDBC names.</summary>
    public static PostgresConnectionParts Parse(string databaseUrl)
    {
        var raw = databaseUrl.Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("DATABASE_URL is empty");

        if (raw.StartsWith("jdbc:postgresql:", StringComparison.OrdinalIgnoreCase))
            return FromJdbc(raw);
        if (IsPostgresUri(raw))
            return FromPostgresUri(raw);
        if (raw.Contains(';') || Regex.IsMatch(raw, @"(?i)\bhost\s*="))
            return FromNpgsql(raw);

        throw new InvalidOperationException(
            "DATABASE_URL must be Npgsql semicolon format, postgres URI, or jdbc:postgresql");
    }

    public static bool IsPostgresUri(string databaseUrl)
        => databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static PostgresConnectionParts FromNpgsql(string url)
    {
        var builder = new NpgsqlConnectionStringBuilder(url);
        var host = builder.Host ?? "";
        var port = builder.Port == 0 ? 5432 : builder.Port;
        var database = builder.Database ?? "";
        var user = builder.Username ?? "";
        var password = builder.Password ?? "";
        var sslmode = Regex.IsMatch(url, @"(?i)ssl\s*mode|sslmode")
            ? ToJdbcSslMode(builder.SslMode.ToString())
            : "require";
        return new PostgresConnectionParts(host, port, database, user, password, sslmode);
    }

    private static PostgresConnectionParts FromPostgresUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException("DATABASE_URL is not a valid postgres URI");
        return FromParsedUri(parsed);
    }

    private static PostgresConnectionParts FromJdbc(string url)
    {
        var asUri = url.Replace("jdbc:postgresql://", "postgresql://", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(asUri, UriKind.Absolute, out var parsed))
            throw new InvalidOperationException("DATABASE_URL is not a valid jdbc:postgresql URL");
        return FromParsedUri(parsed);
    }

    private static PostgresConnectionParts FromParsedUri(Uri parsed)
    {
        var query = ParseQuery(parsed.Query);
        var user = Uri.UnescapeDataString(parsed.UserInfo.Split(':')[0]);
        var password = "";
        if (parsed.UserInfo.Contains(':'))
            password = Uri.UnescapeDataString(parsed.UserInfo[(parsed.UserInfo.IndexOf(':') + 1)..]);
        if (query.TryGetValue("user", out var queryUser) && string.IsNullOrEmpty(user))
            user = queryUser;
        if (query.TryGetValue("password", out var queryPassword) && string.IsNullOrEmpty(password))
            password = queryPassword;
        var sslmode = query.GetValueOrDefault("sslmode", "require");
        var host = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 5432;
        var database = Uri.UnescapeDataString(parsed.AbsolutePath.Trim('/'));
        return new PostgresConnectionParts(host, port, database, user, password, sslmode);
    }

    public static string ToJdbc(string host, int port, string database, string sslmode)
    {
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException("DATABASE_URL is missing Host");
        if (string.IsNullOrEmpty(database))
            throw new InvalidOperationException("DATABASE_URL is missing Database");
        return $"jdbc:postgresql://{host}:{port}/{database}?sslmode={sslmode}";
    }

    public static string ToJdbcSslMode(string? npgsqlSslMode)
    {
        if (string.IsNullOrEmpty(npgsqlSslMode))
            return "require";
        var normalized = npgsqlSslMode.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "disable" => "disable",
            "allow" => "allow",
            "prefer" => "prefer",
            "require" => "require",
            "verifyca" => "verify-ca",
            "verifyfull" => "verify-full",
            _ => "require",
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return result;
        var trimmed = query.TrimStart('?');
        foreach (var chunk in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chunk.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            result[key] = value;
        }

        return result;
    }
}

/// <summary>Components of a Postgres connection; <see cref="SslMode"/> uses libpq/JDBC names (e.g. <c>verify-full</c>).</summary>
public sealed record PostgresConnectionParts(
    string Host,
    int Port,
    string Database,
    string User,
    string Password,
    string SslMode);
