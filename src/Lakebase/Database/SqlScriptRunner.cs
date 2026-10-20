namespace Lakebase;

/// <summary>
/// Runs a plain SQL file (e.g. lab seed data) on the current branch. Not a migration: nothing is
/// recorded in flyway_schema_history, so scripts should be idempotent.
/// </summary>
public static class SqlScriptRunner
{
    public static async Task<int> RunFileAsync(
        DatabaseConnectionSettings settings,
        string path,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"SQL script not found: {path}", path);

        var sql = await File.ReadAllTextAsync(path, ct);
        await using var connection = await LakebaseConnectionFactory.OpenConnectionAsync(settings, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(ct);
    }
}
