using System.Text;
using System.Text.RegularExpressions;

namespace Lakebase;

/// <summary>
/// Updates .env.database after branch create so developers do not hand-edit hosts.
/// </summary>
public static partial class DatabaseEnvWriter
{
    public static string? FindDatabaseEnvPath()
    {
        var root = EnvConfig.FindRepoRoot();
        if (root is null)
            return null;

        return Path.Combine(root, ".env.database");
    }

    public static void UpdateBranchConnection(
        string host,
        string endpointPath,
        string? username = null)
    {
        var path = FindDatabaseEnvPath()
            ?? throw new InvalidOperationException("Could not locate repo root for .env.database");

        username ??= Environment.GetEnvironmentVariable("LAKEBASE_DATABASE_CLIENT_ID")
            ?? Environment.GetEnvironmentVariable("LAKEBASE_APP_SP_ID")
            ?? ReadExistingUsername(path)
            ?? "<app-sp-application-id>";

        var connection =
            $"Host={host};Port=5432;Database=databricks_postgres;Username={username};SSLMode=Require";

        Dictionary<string, string> updates = new(StringComparer.Ordinal)
        {
            ["LAKEBASE_ENDPOINT"] = endpointPath,
            ["DATABASE_URL"] = connection,
        };

        UpsertKeys(path, updates);
        CliLog.Progress($"==> Updated {path}");
        CliLog.Detail($"    LAKEBASE_ENDPOINT={endpointPath}");
        CliLog.Detail($"    DATABASE_URL=Host={host};...;Username={username};SSLMode=Require");
    }

    private static string? ReadExistingUsername(string path)
    {
        if (!File.Exists(path))
            return null;

        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("DATABASE_URL=", StringComparison.Ordinal))
                continue;

            var m = UsernamePattern().Match(line);
            if (m.Success && m.Groups[1].Value is not ("<role>" or "<app-sp-application-id>"))
                return m.Groups[1].Value;
        }

        return null;
    }

    private static void UpsertKeys(string path, IReadOnlyDictionary<string, string> updates)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>
            {
                "# Auto-updated by lakebase platform branch create",
                "DATABRICKS_HOST=",
                "LAKEBASE_USE_OAUTH_TOKEN=true",
            };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq];
            if (!updates.TryGetValue(key, out var value))
                continue;

            lines[i] = $"{key}={value}";
            seen.Add(key);
        }

        foreach (var (key, value) in updates)
        {
            if (seen.Contains(key))
                continue;
            lines.Add($"{key}={value}");
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.AppendLine(line);
        File.WriteAllText(path, sb.ToString());
    }

    [GeneratedRegex(@"Username=([^;]+)")]
    private static partial Regex UsernamePattern();
}
