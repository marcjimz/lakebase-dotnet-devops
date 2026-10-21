using Lakebase;

namespace SampleApp.Tests.IntegrationTests;

/// <summary>
/// Latest successful Flyway version on the branch the tests run against, read once from
/// <c>flyway_schema_history</c>. Null when the database can't be reached, so tests run and fail
/// loudly instead of being skipped.
/// </summary>
internal static class SchemaVersion
{
    private static readonly Lazy<int?> Lazy = new(Read);

    public static int? Current => Lazy.Value;

    private static int? Read()
    {
        try
        {
            var settings = DatabaseConnectionSettings.Load();
            using var connection = LakebaseConnectionFactory.OpenConnectionAsync(settings).GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT coalesce(max(version::int), 0) FROM flyway_schema_history
                WHERE success AND version ~ '^[0-9]+$'
                """;
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            return null;
        }
    }

    public static string? SkipReason(int required)
        => Current is { } current && current < required
            ? $"Needs schema V{required}; this branch is at V{current}. Copy lab/migrations/V{required}__*.sql "
              + "into db/migrations and run ./scripts/flyway-migrate.sh."
            : null;
}

/// <summary>A fact that is skipped (with the reason) until the branch has the given migration.</summary>
public sealed class RequiresSchemaFactAttribute : FactAttribute
{
    public RequiresSchemaFactAttribute(int version) => Skip = SchemaVersion.SkipReason(version);
}

/// <summary>A theory that is skipped (with the reason) until the branch has the given migration.</summary>
public sealed class RequiresSchemaTheoryAttribute : TheoryAttribute
{
    public RequiresSchemaTheoryAttribute(int version) => Skip = SchemaVersion.SkipReason(version);
}
