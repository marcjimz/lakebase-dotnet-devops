using Npgsql;

namespace Lakebase;

/// <summary>
/// Makes the app service principal the owner of application objects on a Dev feature branch
/// so developers can run Flyway DDL locally with the same identity as the app.
/// PostgreSQL ALTER TABLE requires ownership; Lakebase COW copies owner from the parent.
/// Covers every table and sequence in the application schema (<c>LAKEBASE_APP_SCHEMA</c>, default
/// <c>public</c>) so new migrations need no code change. Never run against a stable branch.
/// </summary>
public static class FeatureBranchDdl
{
    public static string AppSchema
        => NormalizeIdentifier(Environment.GetEnvironmentVariable("LAKEBASE_APP_SCHEMA") is { Length: > 0 } s ? s : "public", "schema");

    public static async Task<GrantDdlResult> GrantAppOwnershipAsync(
        DatabaseConnectionSettings settings,
        string appSpPostgresRole,
        CancellationToken ct = default)
    {
        var role = NormalizeRole(appSpPostgresRole);
        var schema = AppSchema;
        await using var conn = await LakebaseConnectionFactory.OpenConnectionAsync(settings, ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT current_user";
        var currentUser = (string?)await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Could not resolve current_user.");

        var alreadyOwner = string.Equals(currentUser, role, StringComparison.OrdinalIgnoreCase)
            || await RoleOwnsAppTablesAsync(cmd, role, schema, ct);

        if (alreadyOwner)
        {
            CliLog.Progress($"==> App SP '{role}' already owns tables in schema '{schema}' — DDL grant is a no-op.");
            return new GrantDdlResult(role, currentUser, Transferred: false);
        }

        cmd.CommandText = """
            SELECT 1
            FROM pg_roles
            WHERE rolname = @role
            """;
        cmd.Parameters.AddWithValue("role", role);
        var exists = await cmd.ExecuteScalarAsync(ct);
        cmd.Parameters.Clear();
        if (exists is null)
        {
            throw new InvalidOperationException(
                $"Postgres role '{role}' does not exist on this branch. Create the Lakebase feature branch so the app SP role is provisioned first.");
        }

        var statements = new List<string>();
        cmd.CommandText = """
            SELECT format('ALTER TABLE %I.%I OWNER TO %I', n.nspname, c.relname, @role)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'p')
              AND pg_get_userbyid(c.relowner) <> @role
            UNION ALL
            SELECT format('ALTER SEQUENCE %I.%I OWNER TO %I', n.nspname, c.relname, @role)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_depend d
              ON d.objid = c.oid AND d.classid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')
            WHERE n.nspname = @schema
              AND c.relkind = 'S'
              AND d.objid IS NULL
              AND pg_get_userbyid(c.relowner) <> @role
            """;
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("schema", schema);
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                statements.Add(reader.GetString(0));
        }

        cmd.Parameters.Clear();
        foreach (var sql in statements)
        {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var quotedSchema = QuoteIdent(schema);
        var quotedRole = QuoteIdent(role);
        cmd.CommandText = $"""
            GRANT USAGE, CREATE ON SCHEMA {quotedSchema} TO {quotedRole};
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA {quotedSchema} TO {quotedRole};
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {quotedSchema} TO {quotedRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA {quotedSchema} GRANT ALL ON TABLES TO {quotedRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA {quotedSchema} GRANT ALL ON SEQUENCES TO {quotedRole};
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        CliLog.Progress($"==> Transferred ownership of {statements.Count} object(s) in schema '{schema}' to app SP '{role}'.");
        return new GrantDdlResult(role, currentUser, Transferred: true);
    }

    private static async Task<bool> RoleOwnsAppTablesAsync(NpgsqlCommand cmd, string role, string schema, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT bool_and(pg_get_userbyid(c.relowner) = @role)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'p')
            """;
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("schema", schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        cmd.Parameters.Clear();
        return result is true;
    }

    public static string NormalizeRole(string appSpPostgresRole)
    {
        var role = appSpPostgresRole.Trim();
        if (role.Length == 0)
            throw new InvalidOperationException("Set LAKEBASE_APP_SP_ID to the app service principal application (client) id.");
        foreach (var ch in role)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@')
                continue;
            throw new InvalidOperationException($"Invalid Postgres role name '{role}'.");
        }

        return role;
    }

    public static string NormalizeIdentifier(string value, string kind)
    {
        var ident = value.Trim();
        if (ident.Length is 0 or > 63)
            throw new InvalidOperationException($"Invalid Postgres {kind} name '{value}' (1-63 characters).");
        foreach (var ch in ident)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_')
                continue;
            throw new InvalidOperationException($"Invalid Postgres {kind} name '{value}' (letters, digits, underscore).");
        }

        return ident;
    }

    private static string QuoteIdent(string ident)
        => "\"" + ident.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

public sealed record GrantDdlResult(string AppSpRole, string GrantedBy, bool Transferred);
